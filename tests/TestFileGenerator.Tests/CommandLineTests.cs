using TestFileGenerator.Generation;
using Xunit;

namespace TestFileGenerator.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void Parsing_when_only_the_required_arguments_are_given_applies_the_documented_defaults()
    {
        Assert.True(Program.TryParseOptions(["out.txt", "--size", "1MiB"], out GeneratorOptions? options, out _));

        Assert.Equal(new GeneratorOptions("out.txt", 1024 * 1024, Seed: 0, DuplicateRatio: 0.1), options);
    }

    [Fact]
    public void Parsing_when_every_option_is_given_carries_all_of_them_through()
    {
        Assert.True(Program.TryParseOptions(
            ["out.txt", "--size", "2GiB", "--seed", "42", "--duplicate-ratio", "0.25"],
            out GeneratorOptions? options,
            out _));

        Assert.Equal(new GeneratorOptions("out.txt", 2L * 1024 * 1024 * 1024, Seed: 42, DuplicateRatio: 0.25), options);
    }

    [Theory]
    [InlineData("512", 512)]
    [InlineData("512B", 512)]
    [InlineData("2KiB", 2 * 1024)]
    [InlineData("3MiB", 3 * 1024 * 1024)]
    [InlineData("100GiB", 100L * 1024 * 1024 * 1024)]
    [InlineData("100gib", 100L * 1024 * 1024 * 1024)]
    public void Parsing_when_a_size_carries_a_unit_suffix_reads_it_as_binary_bytes(string size, long expected)
    {
        Assert.True(Program.TryParseOptions(["out.txt", "--size", size], out GeneratorOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal(expected, options.TargetBytes);
    }

    [Theory]
    [InlineData("out.txt")]                                        // no size
    [InlineData("--size", "1MiB")]                                 // no output path
    [InlineData("out.txt", "--size")]                              // value missing
    [InlineData("out.txt", "--size", "1EiB")]                      // unknown unit
    [InlineData("out.txt", "--size", "-1")]                        // negative
    [InlineData("out.txt", "--size", "9223372036854775807KiB")]    // overflows a signed 64-bit count
    [InlineData("", "--size", "1MiB")]                             // empty output path
    [InlineData("out.txt", "--size", "1MiB", "--duplicate-ratio", "1.5")]
    [InlineData("out.txt", "--size", "1MiB", "--duplicate-ratio", "NaN")]
    [InlineData("out.txt", "--size", "1MiB", "--duplicate-ratio", "Infinity")]
    [InlineData("out.txt", "--size", "1MiB", "--seed", "many")]
    [InlineData("out.txt", "--size", "1MiB", "--max-line", "1KiB")] // a knob the generator does not have
    [InlineData("out.txt", "--size", "1MiB", "--colour", "blue")]  // unknown option
    [InlineData("out.txt", "--size", "1MiB", "other.txt")]         // a second output path
    public void Parsing_when_an_argument_is_unusable_explains_which_one(params string[] args)
    {
        Assert.False(Program.TryParseOptions(args, out GeneratorOptions? options, out string? error));

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.NotEmpty(error);
    }
}
