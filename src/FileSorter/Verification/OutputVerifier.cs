using System.Diagnostics.CodeAnalysis;
using FileSorter.Infrastructure;
using FileSorter.LineFormat;

namespace FileSorter.Verification;

// Not an independent oracle: it shares the production parser and comparator.
// The wrapping sum of FNV-1a hashes is order-independent but can collide for different multisets.
internal static class OutputVerifier
{
    public const int BaseBufferSize = 4 * 1024 * 1024;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    [SuppressMessage(
        "Reliability", "CA2025:Ensure tasks using 'IDisposable' instances complete before the instances are disposed",
        Justification = "Task.WhenAll below completes both scans -- including the second one when the " +
        "first faults -- before the using declarations dispose the streams those scans read from. The rule " +
        "fires because the tasks are held in locals rather than awaited where they are started, which is " +
        "what lets the two files be scanned concurrently.")]
    public static async Task<VerificationResult> RunAsync(
        string inputPath, string outputPath, int maxLineLength, CancellationToken ct)
    {
        using FileStream input = new(
            inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: FileStreams.Unbuffered,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using FileStream output = new(
            outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: FileStreams.Unbuffered,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long inputBytes = input.Length;
        long outputBytes = output.Length;

        Task<ScanOutcome> inputScan = ScanAsync(input, inputPath, FileRole.Input, maxLineLength, ct);
        Task<ScanOutcome> outputScan = ScanAsync(output, outputPath, FileRole.Output, maxLineLength, ct);
        // WhenAll first, so the second scan's fault is observed even when the first throws.
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

            return new VerificationResult(
                VerificationOutcome.OrderViolation, inputReport, outputReport, detail, violation.LineNumber);
        }

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

    private enum FileRole
    {
        Input,
        Output,
    }

    private readonly record struct ScanSummary(long LineCount, ulong Hash);

    private sealed record OrderViolation(long LineNumber, string PreviousPreview, string CurrentPreview);

    private sealed record UnterminatedOutput(long LineNumber, long ByteOffset, string Preview);

    private sealed record ScanOutcome(ScanSummary Summary, OrderViolation? Violation, UnterminatedOutput? Unterminated);

    private static async Task<ScanOutcome> ScanAsync(
        Stream stream, string filePath, FileRole role, int maxLineLength, CancellationToken ct)
    {
        try
        {
            return await ScanCoreAsync(stream, role, maxLineLength, ct);
        }
        catch (MalformedLineException ex)
        {
            throw new MalformedLineException(ex.ByteOffset, ex.LineNumber, ex.Preview, filePath);
        }
    }

    private static async Task<ScanOutcome> ScanCoreAsync(
        Stream stream, FileRole role, int maxLineLength, CancellationToken ct)
    {
        bool userInputRules = role is FileRole.Input;
        bool checkOrder = !userInputRules;
        bool requireTerminatedTail = !userInputRules;

        int bufferSize = CheckedBufferSize(maxLineLength);
        byte[] buffer = new byte[bufferSize];

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
                        MalformedLineException.PreviewOf(previousLine.AsSpan(previousDescriptor.Offset, previousDescriptor.Length)),
                        MalformedLineException.PreviewOf(source.AsSpan(offset, length)));
                    return;
                }

                Array.Copy(source, offset, previousLine!, 0, length);
                previousDescriptor = new LineDescriptor(
                    current.Prefix, current.Number, offset: 0, length, stringOffset: current.StringOffset - offset);
                havePrevious = true;
            }
            else
            {
                // Called only to validate syntax.
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
            int freshCount = await stream.ReadAtLeastAsync(
                buffer.AsMemory(carryLength, fillAmount), fillAmount, throwOnEndOfStream: false, ct);
            int windowLength = carryLength + freshCount;
            bool exhausted = freshCount < fillAmount;
            streamPosition += freshCount;

            if (windowLength == 0)
            {
                break;
            }

            long blockBaseOffset = streamPosition - windowLength;
            LineCursor cursor = new(
                buffer.AsSpan(0, windowLength), maxLineLength, blockBaseOffset, lineNumber,
                stripByteOrderMark: firstBlock && userInputRules, stripCarriageReturn: userInputRules);
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
                        unterminated = new UnterminatedOutput(
                            lineNumber, blockBaseOffset + carryOffset, MalformedLineException.PreviewOf(buffer.AsSpan(carryOffset, carryLength)));
                    }
                    else
                    {
                        int finalLength = LineCursor.UnterminatedTailLength(buffer.AsSpan(carryOffset, carryLength));
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
        if (!LineDescriptor.TryCreate(buffer, offset, length, out LineDescriptor descriptor))
        {
            throw new MalformedLineException(
                fileOffsetOfBufferZero + offset, lineNumber, MalformedLineException.PreviewOf(buffer.AsSpan(offset, length)));
        }

        return descriptor;
    }

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
