using FileSorter.Startup;
using FileSorter.Tests.Properties;
using Xunit;

namespace FileSorter.Tests.Integration;

/// <summary>
/// The line-format cases the unit tests cover in memory, driven here through
/// <see cref="Program.RunAsync"/> against real files on disk, the way the binary's Main
/// runs. Verified against <see cref="NaiveReferenceSort"/>, the independent oracle the
/// property tests use, so a terminator-normalisation defect cannot hide behind a weaker
/// check here.
/// </summary>
[Collection("Program")]
public sealed class LineFormatIntegrationTests : IDisposable
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));

    public LineFormatIntegrationTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Case", "IT-01")]
    public async Task Sorting_a_single_line_file_reproduces_that_line()
    {
        await AssertSortedMatchesOracleAsync("42. Only line\n"u8.ToArray());
    }

    [Fact]
    [Trait("Case", "IT-02")]
    public async Task Sorting_a_file_with_no_trailing_terminator_still_emits_the_final_line()
    {
        // At the process boundary: no phantom empty line, and the unterminated last
        // line is not silently dropped.
        await AssertSortedMatchesOracleAsync("30. Cherry\n2. Apple\n100. Banana"u8.ToArray());
    }

    [Fact]
    [Trait("Case", "IT-03")]
    public async Task Sorting_a_file_using_only_the_single_character_terminator_sorts_correctly()
    {
        await AssertSortedMatchesOracleAsync("30. Cherry\n2. Apple\n100. Banana\n2. Aardvark\n"u8.ToArray());
    }

    [Fact]
    [Trait("Case", "IT-04")]
    public async Task Sorting_a_file_using_only_the_two_character_terminator_sorts_correctly()
    {
        // If a stray carriage return survived into the sort key, this file would still
        // produce output that looks sorted, just sorted on the wrong bytes, which is
        // why the oracle comparison rather than an eyeballed expectation is what
        // catches it.
        await AssertSortedMatchesOracleAsync("30. Cherry\r\n2. Apple\r\n100. Banana\r\n2. Aardvark\r\n"u8.ToArray());
    }

    [Fact]
    [Trait("Case", "IT-05")]
    public async Task Sorting_a_file_mixing_both_terminator_conventions_sorts_correctly()
    {
        await AssertSortedMatchesOracleAsync("30. Cherry\r\n2. Apple\n100. Banana\r\n2. Aardvark\n"u8.ToArray());
    }

    [Fact]
    [Trait("Case", "IT-06")]
    public async Task Sorting_a_file_beginning_with_a_byte_order_mark_sorts_correctly()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] rest = "30. Cherry\n2. Apple\n100. Banana\n"u8.ToArray();
        await AssertSortedMatchesOracleAsync([.. bom, .. rest]);
    }

    private async Task AssertSortedMatchesOracleAsync(byte[] input)
    {
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        await File.WriteAllBytesAsync(inputPath, input, TestContext.Current.CancellationToken);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        byte[] expected = NaiveReferenceSort.Sort(input);
        byte[] actual = await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken);
        Assert.Equal(expected, actual);
    }
}
