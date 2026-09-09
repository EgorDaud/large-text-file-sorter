using FileSorter.Startup;
using FileSorter.Verification;
using Xunit;

namespace FileSorter.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void Parsing_when_only_the_positional_arguments_are_given_applies_the_documented_defaults()
    {
        Assert.True(CommandLine.TryParseOptions(["in.txt", "out.txt"], out SorterOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal("in.txt", options.InputPath);
        Assert.Equal("out.txt", options.OutputPath);
        Assert.Equal(".", options.TempDirectory); // out.txt has no directory component
        Assert.Equal(1L * 1024 * 1024 * 1024, options.MemoryBudgetBytes);
        Assert.Equal(64 * 1024, options.MaxLineLength);
        Assert.Equal(Environment.ProcessorCount, options.Parallelism);
        Assert.Equal(Pipeline.Akka, options.Pipeline);
    }

    [Fact]
    public void Parsing_when_the_output_path_has_a_directory_defaults_temp_to_that_directory()
    {
        string outputPath = Path.Combine("some", "nested", "out.txt");
        Assert.True(CommandLine.TryParseOptions(["in.txt", outputPath], out SorterOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal(Path.Combine("some", "nested"), options.TempDirectory);
    }

    [Fact]
    public void Parsing_when_every_option_is_given_carries_all_of_them_through()
    {
        Assert.True(CommandLine.TryParseOptions(
            [
                "in.txt", "out.txt",
                "--temp", "scratch",
                "--memory", "2GiB",
                "--max-line", "128KiB",
                "--parallelism", "3",
                "--pipeline", "channels",
            ],
            out SorterOptions? options,
            out _));

        Assert.Equal(
            new SorterOptions("in.txt", "out.txt", "scratch", 2L * 1024 * 1024 * 1024, 128 * 1024, 3, Pipeline.Channels),
            options);
    }

    // Pipeline is internal, and a [Theory]'s parameters must be as accessible as
    // the public test method itself -- expected outcomes are carried as bool
    // (true means Akka) rather than the enum, to keep InlineData legal here.
    [Theory]
    [InlineData("akka", true)]
    [InlineData("AKKA", true)]
    [InlineData("channels", false)]
    [InlineData("Channels", false)]
    public void Parsing_reads_pipeline_case_insensitively(string pipelineText, bool expectAkka)
    {
        Assert.True(CommandLine.TryParseOptions(
            ["in.txt", "out.txt", "--pipeline", pipelineText], out SorterOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal(expectAkka ? Pipeline.Akka : Pipeline.Channels, options.Pipeline);
    }

    [Theory]
    [InlineData("512", 512)]
    [InlineData("512B", 512)]
    [InlineData("2KiB", 2 * 1024)]
    [InlineData("3MiB", 3 * 1024 * 1024)]
    [InlineData("4GiB", 4L * 1024 * 1024 * 1024)]
    [InlineData("4gib", 4L * 1024 * 1024 * 1024)]
    public void Parsing_when_memory_carries_a_unit_suffix_reads_it_as_binary_bytes(string size, long expected)
    {
        Assert.True(CommandLine.TryParseOptions(
            ["in.txt", "out.txt", "--memory", size], out SorterOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal(expected, options.MemoryBudgetBytes);
    }

    [Theory]
    [InlineData("512", 512)]
    [InlineData("1KiB", 1024)]
    [InlineData("1MiB", 1024 * 1024)]
    public void Parsing_when_max_line_carries_a_unit_suffix_reads_it_as_binary_bytes(string size, int expected)
    {
        Assert.True(CommandLine.TryParseOptions(
            ["in.txt", "out.txt", "--max-line", size], out SorterOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal(expected, options.MaxLineLength);
    }

    [Fact]
    public void Parsing_when_the_memory_value_is_unusable_names_the_memory_option()
    {
        Assert.False(CommandLine.TryParseOptions(
            ["in.txt", "out.txt", "--memory", "1EiB"], out SorterOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("--memory", error);
    }

    [Fact]
    public void Parsing_when_the_max_line_value_is_unusable_names_the_max_line_option()
    {
        Assert.False(CommandLine.TryParseOptions(
            ["in.txt", "out.txt", "--max-line", "not-a-size"], out SorterOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("--max-line", error);
    }

    [Theory]
    [InlineData("2147483632")] // int.MaxValue - 15: one past the documented ceiling
    [InlineData("2147483647")] // int.MaxValue itself
    public void Parsing_rejects_a_max_line_value_above_the_documented_ceiling(string maxLine)
    {
        // MemoryBudget adds small, fixed offsets (at most +3) to maxLineLength while
        // computing buffer floors, and a value this close to int.MaxValue makes that
        // addition wrap negative in plain int arithmetic, surfacing to the operator as
        // a nonsensical negative minimum budget. The CLI rejects it before it ever
        // reaches that arithmetic.
        Assert.False(CommandLine.TryParseOptions(
            ["in.txt", "out.txt", "--max-line", maxLine], out SorterOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("--max-line", error);
    }

    [Fact]
    public void Parsing_accepts_a_max_line_value_exactly_at_the_documented_ceiling()
    {
        Assert.True(CommandLine.TryParseOptions(
            ["in.txt", "out.txt", "--max-line", "2147483631"], out SorterOptions? options, out _)); // int.MaxValue - 16

        Assert.NotNull(options);
        Assert.Equal(int.MaxValue - 16, options.MaxLineLength);
    }

    [Fact]
    public void Parsing_when_the_parallelism_value_is_unusable_names_the_parallelism_option()
    {
        Assert.False(CommandLine.TryParseOptions(
            ["in.txt", "out.txt", "--parallelism", "many"], out SorterOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("--parallelism", error);
    }

    [Fact]
    public void Parsing_when_the_pipeline_value_is_unusable_names_the_pipeline_option()
    {
        Assert.False(CommandLine.TryParseOptions(
            ["in.txt", "out.txt", "--pipeline", "threads"], out SorterOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("--pipeline", error);
    }

    [Theory]
    [InlineData("in.txt")]                                            // no output
    [InlineData("--memory", "1GiB")]                                  // no input, no output
    [InlineData("in.txt", "out.txt", "--memory")]                     // value missing
    [InlineData("in.txt", "out.txt", "--memory", "1EiB")]             // unknown unit
    [InlineData("in.txt", "out.txt", "--memory", "-1")]               // negative
    [InlineData("in.txt", "out.txt", "--memory", "0")]                // non-positive
    [InlineData("in.txt", "out.txt", "--max-line", "0")]              // non-positive
    [InlineData("in.txt", "out.txt", "--max-line", "9223372036854775807")] // exceeds int.MaxValue
    [InlineData("in.txt", "out.txt", "--parallelism", "0")]           // non-positive
    [InlineData("in.txt", "out.txt", "--parallelism", "-1")]          // negative
    [InlineData("in.txt", "out.txt", "--parallelism", "1.5")]         // not an integer
    [InlineData("in.txt", "out.txt", "--pipeline", "threads")]        // unknown pipeline
    [InlineData("in.txt", "out.txt", "--colour", "blue")]             // unknown option
    [InlineData("in.txt", "out.txt", "extra.txt")]                    // a third positional
    [InlineData("", "out.txt")]                                       // empty input path
    [InlineData("in.txt", "")]                                        // empty output path
    public void Parsing_when_an_argument_is_unusable_explains_which_one(params string[] args)
    {
        Assert.False(CommandLine.TryParseOptions(args, out SorterOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Parsing_verify_options_with_only_the_positional_arguments_applies_the_documented_default()
    {
        Assert.True(CommandLine.TryParseVerifyOptions(
            ["--verify", "in.txt", "out.txt"], out VerifyOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal("in.txt", options.InputPath);
        Assert.Equal("out.txt", options.OutputPath);
        Assert.Equal(64 * 1024, options.MaxLineLength); // same documented default as sort mode
    }

    [Fact]
    public void Parsing_verify_options_honours_an_explicit_max_line()
    {
        Assert.True(CommandLine.TryParseVerifyOptions(
            ["--verify", "in.txt", "out.txt", "--max-line", "128KiB"], out VerifyOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal(128 * 1024, options.MaxLineLength);
    }

    [Fact]
    public void Parsing_verify_options_rejects_a_max_line_value_that_would_overflow_the_fixed_scan_buffer()
    {
        // OutputVerifier.ScanAsync allocates one fixed buffer of
        // OutputVerifier.BaseBufferSize + maxLineLength bytes per file, so verify mode
        // has a lower --max-line ceiling than sort mode's much larger one. A value
        // between the two ceilings would otherwise reach that allocation and throw an
        // unhandled ArgumentOutOfRangeException instead of a documented exit 3.
        long ceiling = Array.MaxLength - OutputVerifier.BaseBufferSize;

        Assert.False(CommandLine.TryParseVerifyOptions(
            ["--verify", "in.txt", "out.txt", "--max-line", (ceiling + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            out VerifyOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("--max-line", error);
    }

    [Fact]
    public void Parsing_verify_options_accepts_a_max_line_value_exactly_at_the_scan_buffer_ceiling()
    {
        long ceiling = Array.MaxLength - OutputVerifier.BaseBufferSize;

        Assert.True(CommandLine.TryParseVerifyOptions(
            ["--verify", "in.txt", "out.txt", "--max-line", ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            out VerifyOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal(ceiling, (long)options.MaxLineLength);
    }

    [Theory]
    [InlineData("--verify")]                                          // no input, no output
    [InlineData("--verify", "in.txt")]                                // no output
    [InlineData("--verify", "in.txt", "out.txt", "extra.txt")]        // a third positional
    [InlineData("--verify", "in.txt", "out.txt", "--memory", "1GiB")] // --memory is not a verify-mode option
    [InlineData("--verify", "in.txt", "out.txt", "--max-line", "0")]  // non-positive
    [InlineData("--verify", "in.txt", "out.txt", "--max-line")]       // value missing
    public void Parsing_verify_options_when_an_argument_is_unusable_explains_which_one(params string[] args)
    {
        Assert.False(CommandLine.TryParseVerifyOptions(args, out VerifyOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.NotEmpty(error);
    }
}
