using System.Globalization;
using TestFileGenerator.Generation;
using Xunit;

namespace FileSorter.Tests.EndToEnd;

// Helpers shared by the whole-sort test classes: running Program.RunAsync with stderr
// captured, generating input, and the independent sortedness and leftover-file checks.
internal static class SortHarness
{
    // Parses Program's "phase one produced N run(s) (...)" line. It is printed once
    // phase one finishes and is not gated by the periodic progress timer that the
    // merge-pass line is, so it is present however fast a small fixture sorts.
    internal static int ParseRunCount(string stderr)
    {
        const string marker = "phase one produced ";
        int start = stderr.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected a \"{marker}\" line in stderr, got:\n{stderr}");
        start += marker.Length;
        int end = stderr.IndexOf(' ', start);
        return int.Parse(stderr[start..end], CultureInfo.InvariantCulture);
    }

    internal static void WriteGeneratedInput(string path, long targetBytes, int seed)
    {
        GeneratorOptions options = new(path, targetBytes, seed, DuplicateRatio: 0.2);
        LineComposer composer = new(options);
        using FileStream output = new(path, FileMode.Create, FileAccess.Write);
        FileWriter.Write(output, composer, targetBytes, TestContext.Current.CancellationToken);
    }

    // Checks both halves of the property independently of FileSorter.LineFormat: the
    // permutation half proves the same multiset, and the ordering half re-derives the
    // sort key from the raw line text rather than calling LineOrder.Compare, so a
    // comparator defect cannot hide behind the comparator being tested.
    internal static void AssertIsSortedPermutationOfInput(string inputPath, string outputPath)
    {
        string[] inputLines = File.ReadAllLines(inputPath);
        string[] outputLines = File.ReadAllLines(outputPath);

        Assert.Equal(inputLines.Length, outputLines.Length);

        string[] expectedMultiset = [.. inputLines.OrderBy(line => line, StringComparer.Ordinal)];
        string[] actualMultiset = [.. outputLines.OrderBy(line => line, StringComparer.Ordinal)];
        Assert.Equal(expectedMultiset, actualMultiset);

        for (int i = 1; i < outputLines.Length; i++)
        {
            (long previousNumber, string previousString) = ParseIndependently(outputLines[i - 1]);
            (long currentNumber, string currentString) = ParseIndependently(outputLines[i]);

            int stringComparison = StringComparer.Ordinal.Compare(previousString, currentString);
            int comparison = stringComparison != 0 ? stringComparison : previousNumber.CompareTo(currentNumber);
            Assert.True(comparison <= 0, $"Output not sorted at line {i}: '{outputLines[i - 1]}' then '{outputLines[i]}'.");
        }
    }

    // Independently written oracle for a line's sort key: it deliberately calls nothing
    // from FileSorter.LineFormat, so it cannot share a defect with the production
    // parser or comparator.
    internal static (long Number, string StringPart) ParseIndependently(string line)
    {
        int separator = line.IndexOf('.');
        string numberPart = line[..separator];
        int stringStart = separator + 1;
        if (stringStart < line.Length && line[stringStart] == ' ')
        {
            stringStart++;
        }

        return (long.Parse(numberPart, System.Globalization.CultureInfo.InvariantCulture), line[stringStart..]);
    }

    internal static void AssertNoLeftoverRunFiles(string tempDirectory)
    {
        if (!Directory.Exists(tempDirectory))
        {
            return;
        }

        // Recursive: run files live inside this invocation's own private directory
        // beneath tempDirectory, not directly in it, so a non-recursive search would
        // miss a leak there entirely.
        string[] leftovers = Directory.GetFiles(tempDirectory, "run-*.tmp", SearchOption.AllDirectories);
        Assert.Empty(leftovers);

        // A leaked private directory is itself a leftover, even an empty one: nothing
        // beneath tempDirectory other than this run's own namespace should exist once
        // that run has finished.
        Assert.Empty(Directory.GetDirectories(tempDirectory));
    }
}
