using System.Globalization;
using System.Text;
using TestFileGenerator.Generation;

namespace TestFileGenerator.Tests.Generation;

/// <summary>
/// Generates into memory and reads the result back with a deliberately independent
/// parser, so a defect in the line grammar cannot hide behind the code that produced it.
/// </summary>
internal static class GeneratedOutput
{
    public static byte[] Write(long targetBytes, double duplicateRatio = 0.1, int seed = 1)
    {
        using MemoryStream sink = new();
        FileWriter.Write(sink, Composer(seed, duplicateRatio), targetBytes);
        return sink.ToArray();
    }

    public static LineComposer Composer(int seed = 1, double duplicateRatio = 0.1) =>
        new(new GeneratorOptions("unused-by-composition", TargetBytes: 0, seed, duplicateRatio));

    /// <summary>Splits and parses, throwing on anything the settled line grammar rejects.</summary>
    public static IReadOnlyList<GeneratedLine> Parse(byte[] output)
    {
        List<GeneratedLine> lines = [];
        int start = 0;

        while (start < output.Length)
        {
            int terminator = Array.IndexOf(output, (byte)'\n', start);
            if (terminator < 0)
            {
                throw new FormatException($"The line at byte {start} is not terminated.");
            }

            lines.Add(ParseLine(output.AsSpan(start, terminator - start), start));
            start = terminator + 1;
        }

        return lines;
    }

    private static GeneratedLine ParseLine(ReadOnlySpan<byte> line, int offset)
    {
        int separator = line.IndexOf((byte)'.');
        if (separator < 0)
        {
            throw new FormatException($"No separator in the line at byte {offset}: '{Encoding.UTF8.GetString(line)}'.");
        }

        ReadOnlySpan<byte> number = line[..separator];
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
        {
            throw new FormatException($"The number in the line at byte {offset} is not a signed 64-bit value: '{Encoding.UTF8.GetString(line)}'.");
        }

        if (number.Length > 1 && number[0] == (byte)'0')
        {
            throw new FormatException($"The number in the line at byte {offset} is not canonical: '{Encoding.UTF8.GetString(line)}'.");
        }

        if (separator + 1 >= line.Length || line[separator + 1] != (byte)' ')
        {
            throw new FormatException($"No space follows the separator in the line at byte {offset}: '{Encoding.UTF8.GetString(line)}'.");
        }

        return new GeneratedLine(parsed, Encoding.UTF8.GetString(line[(separator + 2)..]), line.Length);
    }
}

/// <param name="ByteLength">The line length in bytes, terminator excluded.</param>
internal readonly record struct GeneratedLine(long Number, string StringPart, int ByteLength);
