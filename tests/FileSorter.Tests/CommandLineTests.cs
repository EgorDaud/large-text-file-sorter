using FileSorter.Cli;
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
        Assert.Equal(".", options.TempDirectory);
        Assert.Equal(1L * 1024 * 1024 * 1024, options.MemoryBudgetBytes);
        Assert.Equal(64 * 1024, options.MaxLineLength);
        Assert.Equal(Environment.ProcessorCount, options.Parallelism);
        Assert.Equal(Pipeline.Channels, options.Pipeline);
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
                "--pipeline", "akka",
            ],
            out SorterOptions? options,
            out _));

        Assert.Equal(
            new SorterOptions("in.txt", "out.txt", "scratch", 2L * 1024 * 1024 * 1024, 128 * 1024, 3, Pipeline.Akka),
            options);
    }

    // bool, not Pipeline: an internal enum cannot be a parameter of a public test method.
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
    [InlineData("2147483589")] // Array.MaxLength - 2
    [InlineData("2147483631")] // int.MaxValue - 16
    [InlineData("2147483647")] // int.MaxValue
    public void Parsing_rejects_a_max_line_value_above_the_documented_ceiling(string maxLine)
    {
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
            ["in.txt", "out.txt", "--max-line", "2147483588"], out SorterOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal(Array.MaxLength - 3, options.MaxLineLength);
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
    [InlineData("in.txt")]
    [InlineData("--memory", "1GiB")]
    [InlineData("in.txt", "out.txt", "--memory")]
    [InlineData("in.txt", "out.txt", "--memory", "1EiB")]
    [InlineData("in.txt", "out.txt", "--memory", "-1")]
    [InlineData("in.txt", "out.txt", "--memory", "0")]
    [InlineData("in.txt", "out.txt", "--max-line", "0")]
    [InlineData("in.txt", "out.txt", "--max-line", "9223372036854775807")]
    [InlineData("in.txt", "out.txt", "--parallelism", "0")]
    [InlineData("in.txt", "out.txt", "--parallelism", "-1")]
    [InlineData("in.txt", "out.txt", "--parallelism", "1.5")]
    [InlineData("in.txt", "out.txt", "--pipeline", "threads")]
    [InlineData("in.txt", "out.txt", "--colour", "blue")]
    [InlineData("in.txt", "out.txt", "extra.txt")]
    [InlineData("", "out.txt")]
    [InlineData("in.txt", "")]
    public void Parsing_when_an_argument_is_unusable_explains_which_one(params string[] args)
    {
        Assert.False(CommandLine.TryParseOptions(args, out SorterOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.NotEmpty(error);
    }

    [Fact]
    [Trait("Case", "CL-01")]
    public void Parsing_rejects_an_output_path_identical_to_the_input_path()
    {
        Assert.False(CommandLine.TryParseOptions(["data.txt", "data.txt"], out SorterOptions? options, out string? error));

        Assert.Null(options);
        Assert.Contains("different files", error);
    }

    [Fact]
    [Trait("Case", "CL-02")]
    public void Parsing_rejects_a_relative_and_an_absolute_form_of_the_same_file()
    {
        string absolute = Path.GetFullPath("data.txt");

        Assert.False(CommandLine.TryParseOptions(["data.txt", absolute], out _, out string? error));
        Assert.Contains("different files", error);

        Assert.False(CommandLine.TryParseOptions([absolute, "data.txt"], out _, out error));
        Assert.Contains("different files", error);
    }

    [Fact]
    [Trait("Case", "CL-03")]
    public void Parsing_rejects_a_path_that_reaches_the_input_through_a_parent_segment()
    {
        string detour = Path.Combine("a", "..", "data.txt");

        Assert.False(CommandLine.TryParseOptions(["data.txt", detour], out _, out string? error));
        Assert.Contains("different files", error);
    }

    [Fact]
    [Trait("Case", "CL-04")]
    public void Parsing_rejects_paths_that_differ_only_in_case_where_the_file_system_ignores_case()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Case-insensitive path comparison is applied on Windows and macOS only.");

        Assert.False(CommandLine.TryParseOptions(["Data.TXT", "data.txt"], out _, out string? error));
        Assert.Contains("different files", error);
    }

    [Theory]
    [Trait("Case", "CL-05")]
    [InlineData("in.txt", "out.txt")]
    [InlineData("in.txt", "in.txt.sorted")]
    [InlineData("data.txt", "sorted/data.txt")]
    public void Parsing_accepts_two_different_files(string input, string output)
    {
        Assert.True(CommandLine.TryParseOptions([input, output], out SorterOptions? options, out _));

        Assert.NotNull(options);
    }
}
