using FileSorter.Cli;
using FileSorter.Verification;
using Xunit;

namespace FileSorter.Tests.Cli;

public sealed class VerifyOptionsTests
{
    [Fact]
    public void Parsing_with_only_the_positional_arguments_applies_the_documented_default()
    {
        Assert.True(VerifyOptions.TryParse(
            ["--verify", "in.txt", "out.txt"], out VerifyOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal("in.txt", options.InputPath);
        Assert.Equal("out.txt", options.OutputPath);
        Assert.Equal(64 * 1024, options.MaxLineLength);
    }

    [Fact]
    public void Parsing_honours_an_explicit_max_line()
    {
        Assert.True(VerifyOptions.TryParse(
            ["--verify", "in.txt", "out.txt", "--max-line", "128KiB"], out VerifyOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal(128 * 1024, options.MaxLineLength);
    }

    [Fact]
    public void Parsing_rejects_a_max_line_value_that_would_overflow_the_fixed_scan_buffer()
    {
        long ceiling = Array.MaxLength - OutputVerifier.BaseBufferSize;

        Assert.False(VerifyOptions.TryParse(
            ["--verify", "in.txt", "out.txt", "--max-line", (ceiling + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            out VerifyOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("--max-line", error);
    }

    [Fact]
    public void Parsing_accepts_a_max_line_value_exactly_at_the_scan_buffer_ceiling()
    {
        long ceiling = Array.MaxLength - OutputVerifier.BaseBufferSize;

        Assert.True(VerifyOptions.TryParse(
            ["--verify", "in.txt", "out.txt", "--max-line", ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            out VerifyOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal(ceiling, (long)options.MaxLineLength);
    }

    [Theory]
    [InlineData("--verify")]
    [InlineData("--verify", "in.txt")]
    [InlineData("--verify", "in.txt", "out.txt", "extra.txt")]
    [InlineData("--verify", "in.txt", "out.txt", "--memory", "1GiB")]
    [InlineData("--verify", "in.txt", "out.txt", "--max-line", "0")]
    [InlineData("--verify", "in.txt", "out.txt", "--max-line")]
    public void Parsing_when_an_argument_is_unusable_explains_which_one(params string[] args)
    {
        Assert.False(VerifyOptions.TryParse(args, out VerifyOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.NotEmpty(error);
    }
}
