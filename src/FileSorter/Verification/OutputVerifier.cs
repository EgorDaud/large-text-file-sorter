using System.Diagnostics.CodeAnalysis;
using System.Text;
using FileSorter.LineFormat;

namespace FileSorter.Verification;

// Checks output order and compares line counts and order-independent hashes in one scan
// per file. It shares the production parser and comparator, so it is not an independent oracle.
// Each scan uses a fixed buffer; output comparison also retains the previous line.
// The wrapping sum of FNV-1a hashes can collide for different multisets.
// Input uses the same BOM/CRLF normalization as the sorter. Output keeps content CRs
// and must end every line with LF.
internal static class OutputVerifier
{
    private const int PreviewMaxBytes = 128;

    // Each scan reserves this much read space plus maxLineLength bytes for carry.
    // CommandLine also uses this constant to calculate the largest valid --max-line.
    internal const int BaseBufferSize = 4 * 1024 * 1024;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    [SuppressMessage(
        "Reliability", "CA2025:Ensure tasks using 'IDisposable' instances complete before the instances are disposed",
        Justification = "Task.WhenAll below completes both scans -- including the second one when the " +
        "first faults -- before the using declarations dispose the streams those scans read from. The rule " +
        "fires because the tasks are held in locals rather than awaited where they are started, which is " +
        "what lets the two files be scanned concurrently.")]
    public static async Task<VerificationResult> RunAsync(VerifyOptions options, CancellationToken ct)
    {
        using FileStream input = new(
            options.InputPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using FileStream output = new(
            options.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long inputBytes = input.Length;
        long outputBytes = output.Length;

        Task<ScanOutcome> inputScan = ScanAsync(input, options.InputPath, options.MaxLineLength, stripByteOrderMark: true, stripCarriageReturn: true, checkOrder: false, requireTerminatedTail: false, ct);
        Task<ScanOutcome> outputScan = ScanAsync(output, options.OutputPath, options.MaxLineLength, stripByteOrderMark: false, stripCarriageReturn: false, checkOrder: true, requireTerminatedTail: true, ct);
        // WhenAll first: awaiting the scans in turn would leave the second one's failure
        // unobserved when the first throws.
        await Task.WhenAll(inputScan, outputScan);

        ScanOutcome inputOutcome = await inputScan;
        ScanOutcome outputOutcome = await outputScan;
        FileScanReport inputReport = new(inputOutcome.Summary.LineCount, inputBytes, inputOutcome.Summary.Hash);
        FileScanReport outputReport = new(outputOutcome.Summary.LineCount, outputBytes, outputOutcome.Summary.Hash);

        if (outputOutcome.Violation is { } violation)
        {
            string detail =
                $"Order violation at output line {violation.LineNumber}: previous \"{violation.PreviousPreview}\" " +
                $"sorts after current \"{violation.CurrentPreview}\".";

            // The line number tells the caller that the output count and hash are partial.
            return new VerificationResult(
                VerificationOutcome.OrderViolation, inputReport, outputReport, detail, violation.LineNumber);
        }

        // The scan reached EOF, so the count and hash cover all complete lines.
        if (outputOutcome.Unterminated is { } unterminated)
        {
            string detail =
                $"Output line {unterminated.LineNumber} at byte offset {unterminated.ByteOffset} has no trailing " +
                $"line feed (\"{unterminated.Preview}\"): the sorter always terminates its last line, so this " +
                "output was never completed by a sort.";
            return new VerificationResult(VerificationOutcome.OutputNotTerminated, inputReport, outputReport, detail);
        }

        if (inputReport.LineCount != outputReport.LineCount)
        {
            string detail =
                $"Line count mismatch: input has {inputReport.LineCount} line(s), output has {outputReport.LineCount}.";
            return new VerificationResult(VerificationOutcome.CountMismatch, inputReport, outputReport, detail);
        }

        if (inputReport.Hash != outputReport.Hash)
        {
            string detail =
                $"Hash mismatch: input hash {inputReport.Hash:x16}, output hash {outputReport.Hash:x16} -- " +
                "same line count, different content.";
            return new VerificationResult(VerificationOutcome.HashMismatch, inputReport, outputReport, detail);
        }

        return new VerificationResult(VerificationOutcome.Verified, inputReport, outputReport, FailureDetail: null);
    }

    private readonly record struct ScanSummary(long LineCount, ulong Hash);

    private sealed record OrderViolation(long LineNumber, string PreviousPreview, string CurrentPreview);

    private sealed record UnterminatedOutput(long LineNumber, long ByteOffset, string Preview);

    private sealed record ScanOutcome(ScanSummary Summary, OrderViolation? Violation, UnterminatedOutput? Unterminated);

    // Attach the file path to parse failures so input and output errors are distinguishable.
    private static async Task<ScanOutcome> ScanAsync(
        Stream stream, string filePath, int maxLineLength, bool stripByteOrderMark, bool stripCarriageReturn,
        bool checkOrder, bool requireTerminatedTail, CancellationToken ct)
    {
        try
        {
            return await ScanCoreAsync(stream, maxLineLength, stripByteOrderMark, stripCarriageReturn, checkOrder, requireTerminatedTail, ct);
        }
        catch (MalformedLineException ex)
        {
            throw new MalformedLineException(ex.ByteOffset, ex.LineNumber, ex.Preview, filePath);
        }
    }

    private static async Task<ScanOutcome> ScanCoreAsync(
        Stream stream, int maxLineLength, bool stripByteOrderMark, bool stripCarriageReturn, bool checkOrder,
        bool requireTerminatedTail, CancellationToken ct)
    {
        int bufferSize = CheckedBufferSize(maxLineLength);
        byte[] buffer = new byte[bufferSize];

        // Keep the previous line across buffer refills.
        byte[]? previousLine = checkOrder ? new byte[maxLineLength + 1] : null;
        LineDescriptor previousDescriptor = default;
        bool havePrevious = false;

        long lineCount = 0;
        long lineNumber = 1;
        ulong hashSum = 0;
        OrderViolation? violation = null;
        UnterminatedOutput? unterminated = null;

        void HandleLine(byte[] source, int offset, int length, long fileOffsetOfBufferZero)
        {
            if (checkOrder)
            {
                LineDescriptor current = Describe(source, offset, length, fileOffsetOfBufferZero, lineNumber);
                if (havePrevious && LineOrder.Compare(in previousDescriptor, previousLine!, in current, source) > 0)
                {
                    violation = new OrderViolation(
                        lineNumber,
                        PreviewOf(previousLine!, previousDescriptor.Offset, previousDescriptor.Length),
                        PreviewOf(source, offset, length));
                    return;
                }

                // Rebase offsets after copying; the parsed number and prefix remain valid.
                Array.Copy(source, offset, previousLine!, 0, length);
                previousDescriptor = new LineDescriptor(
                    current.Prefix, current.Number, offset: 0, length, stringOffset: current.StringOffset - offset);
                havePrevious = true;
            }
            else
            {
                // Validate input syntax even though its order is irrelevant.
                Describe(source, offset, length, fileOffsetOfBufferZero, lineNumber);
            }

            hashSum = unchecked(hashSum + Fnv1a64(source.AsSpan(offset, length)));
            lineCount++;
            lineNumber++;
        }

        int carryLength = 0;
        long streamPosition = 0;
        bool firstBlock = true;

        while (violation is null)
        {
            int fillAmount = bufferSize - carryLength;
            int freshCount = await FillAsync(stream, buffer, carryLength, fillAmount, ct);
            int windowLength = carryLength + freshCount;
            bool exhausted = freshCount < fillAmount;
            streamPosition += freshCount;

            if (windowLength == 0)
            {
                break;
            }

            // Carry starts at buffer offset zero, so this maps offsets back to the file.
            long blockBaseOffset = streamPosition - windowLength;
            LineCursor cursor = new(
                buffer.AsSpan(0, windowLength), maxLineLength, blockBaseOffset, lineNumber,
                stripByteOrderMark: firstBlock && stripByteOrderMark, stripCarriageReturn: stripCarriageReturn);
            firstBlock = false;

            while (violation is null && cursor.TryReadLine(out int offset, out int length))
            {
                HandleLine(buffer, offset, length, blockBaseOffset);
            }

            if (violation is not null)
            {
                break;
            }

            int carryOffset = cursor.CarryOffset;
            carryLength = cursor.CarryLength;

            if (exhausted)
            {
                if (carryLength > 0)
                {
                    if (requireTerminatedTail)
                    {
                        // Output must end in LF. Accepting an unterminated tail would hide truncation.
                        unterminated = new UnterminatedOutput(
                            lineNumber, blockBaseOffset + carryOffset, PreviewOf(buffer, carryOffset, carryLength));
                    }
                    else
                    {
                        // Match the input reader: strip a final bare CR and ignore a tail containing only CR.
                        bool tailEndsInCarriageReturn = buffer[carryOffset + carryLength - 1] == (byte)'\r';
                        int finalLength = tailEndsInCarriageReturn ? carryLength - 1 : carryLength;
                        if (finalLength > 0)
                        {
                            HandleLine(buffer, carryOffset, finalLength, blockBaseOffset);
                        }
                    }
                }

                break;
            }

            Array.Copy(buffer, carryOffset, buffer, 0, carryLength);
        }

        return new ScanOutcome(new ScanSummary(lineCount, hashSum), violation, unterminated);
    }

    // Guard direct callers as well as the CLI against array-size overflow.
    private static int CheckedBufferSize(int maxLineLength)
    {
        long size = (long)BaseBufferSize + maxLineLength;
        if (size > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineLength), maxLineLength,
                $"--max-line is too large for --verify's fixed {BaseBufferSize / (1024 * 1024)} MiB buffer.");
        }

        return (int)size;
    }

    private static LineDescriptor Describe(byte[] buffer, int offset, int length, long fileOffsetOfBufferZero, long lineNumber)
    {
        ReadOnlySpan<byte> line = buffer.AsSpan(offset, length);
        if (!LineParser.TryParse(line, out long number, out int stringStart))
        {
            ReadOnlySpan<byte> preview = line.Length > PreviewMaxBytes ? line[..PreviewMaxBytes] : line;
            throw new MalformedLineException(fileOffsetOfBufferZero + offset, lineNumber, Encoding.UTF8.GetString(preview));
        }

        int stringOffset = offset + stringStart;
        int stringLength = LineDescriptor.StringLengthOf(offset, length, stringOffset);
        ulong prefix = LineDescriptor.BuildPrefix(buffer.AsSpan(stringOffset, stringLength));
        return new LineDescriptor(prefix, number, offset, length, stringOffset);
    }

    private static string PreviewOf(byte[] buffer, int offset, int length)
    {
        int previewLength = Math.Min(length, PreviewMaxBytes);
        return Encoding.UTF8.GetString(buffer, offset, previewLength);
    }

    private static async Task<int> FillAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    // FNV-1a arithmetic wraps modulo 2^64.
    private static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong hash = FnvOffsetBasis;
        foreach (byte b in data)
        {
            hash = unchecked((hash ^ b) * FnvPrime);
        }

        return hash;
    }
}
