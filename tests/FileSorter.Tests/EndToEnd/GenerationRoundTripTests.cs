using FileSorter.LineFormat;
using TestFileGenerator.Generation;
using Xunit;

namespace FileSorter.Tests.EndToEnd;

public sealed class GenerationRoundTripTests
{
    // The sorter's own CLI default, not the generator's much smaller
    // MaxComposedLineLength: this test stands in for the sorter reading a real file,
    // and the sorter's default is what each line has to fit inside.
    private const int SorterMaxLineLength = 64 * 1024;

    [Fact]
    [Trait("Case", "GN-09")]
    public void Parsing_real_generator_output_with_the_sorters_own_parser_accepts_every_line()
    {
        // Deliberately does not call TestFileGenerator.Tests' GeneratedOutput.Parse.
        // That helper is the generator side's own independent oracle; routing this
        // test through it would let the two programs drift and still pass.
        GeneratorOptions options = new("unused-by-composition", TargetBytes: 0, Seed: 11, DuplicateRatio: 0.2);
        LineComposer composer = new(options);

        using MemoryStream output = new();
        FileWriter.Write(output, composer, targetBytes: 512 * 1024);
        byte[] bytes = output.ToArray();

        Assert.NotEmpty(bytes);

        int linesParsed = 0;
        LineCursor cursor = new(bytes, SorterMaxLineLength);

        while (cursor.TryReadLine(out int offset, out int length))
        {
            Assert.True(length <= SorterMaxLineLength, $"Line at offset {offset} is {length} bytes long.");

            ReadOnlySpan<byte> line = bytes.AsSpan(offset, length);
            bool parsed = LineParser.TryParse(line, out long number, out int stringStart);

            Assert.True(parsed, $"Generator output at offset {offset} does not match the sorter's grammar.");
            Assert.True(number >= 0, "The generator draws only non-negative numbers.");
            Assert.True(stringStart <= length, "The string part cannot start beyond the line's own end.");

            linesParsed++;
        }

        Assert.Equal(0, cursor.CarryLength); // FileWriter never emits a partial final line
        Assert.True(linesParsed > 0);
    }
}
