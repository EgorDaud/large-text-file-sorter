using CsCheck;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Properties;

public sealed class KWayMergePropertyTests
{
    // The smallest window RunCursor accepts, so every run is refilled many times.
    private const int MaxLineLength = 32;
    private const int WindowSize = MaxLineLength + 2;
    private const int DescriptorCapacity = 3;
    private const int OutputStagingBufferSize = 64;

    private static readonly Gen<char> PrintableAsciiChar = Gen.Char[' ', '~'];
    private static readonly Gen<string> PrintableStringPart = Gen.String[PrintableAsciiChar, 0, 10];

    // A trailing '\r' here is content: LineEntryGen.BuildInput writes the second '\r' that keeps it so.
    private static readonly Gen<string> StringPart =
        Gen.Select(PrintableStringPart, Gen.Int[0, 19], (s, roll) => roll == 0 ? s + "\r" : s);

    // Narrow so the zero-padded twin LineEntryGen adds still fits MaxLineLength.
    private static readonly Gen<long> Number = Gen.Long[-999_999, 999_999];

    private static readonly Gen<(long Number, string StringPart)> Entry = Gen.Select(Number, StringPart);

    private static readonly Gen<List<(long Number, string StringPart)>> BaseEntries = Entry.List[0, 120];

    private static readonly Gen<int> RunCount = Gen.Int[1, 70];

    private static readonly Gen<(List<(long Number, string StringPart)> Entries, int RunCount, int[] Bucket)> Scenario =
        Gen.Select(BaseEntries, RunCount).SelectMany(t =>
        {
            List<(long Number, string StringPart)> entries = WithVerbatimDuplicates(t.Item1);
            return Gen.Int[0, t.Item2 - 1].Array[entries.Count]
                .Select(bucket => (entries, t.Item2, bucket));
        });

    [Fact]
    public async Task Merging_one_to_seventy_random_sorted_runs_matches_the_independent_oracle()
    {
        await Scenario.SampleAsync(async scenario =>
        {
            (List<(long Number, string StringPart)> entries, int runCount, int[] bucket) = scenario;

            // Bucketing, not slicing a sorted sequence, is what splits identical lines across runs.
            List<(long Number, string StringPart)>[] perRun = [.. Enumerable.Range(0, runCount).Select(_ => new List<(long, string)>())];
            for (int i = 0; i < entries.Count; i++)
            {
                perRun[bucket[i]].Add(entries[i]);
            }

            byte[][] rawRunBytes = [.. perRun.Select(entries => LineEntryGen.BuildInput(entries))];
            byte[] expected = NaiveReferenceSort.Sort([.. rawRunBytes.SelectMany(b => b)]);
            byte[][] sortedRunBytes = [.. rawRunBytes.Select(NaiveReferenceSort.Sort)];

            MemoryStream[] runs = [.. sortedRunBytes.Select(b => new MemoryStream(b))];
            RunCursorBuffers[] cursorBuffers = [.. Enumerable.Range(0, runCount)
                .Select(_ => new RunCursorBuffers(new byte[WindowSize], new byte[WindowSize], new LineDescriptor[DescriptorCapacity]))];
            using MemoryStream output = new();
            byte[] outputStagingBuffer = new byte[OutputStagingBufferSize];

            await KWayMerge.MergeAsync(
                runs, cursorBuffers, output, outputStagingBuffer, MaxLineLength,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal(expected, output.ToArray());
        }, iter: 1_000);
    }

    private static List<(long Number, string StringPart)> WithVerbatimDuplicates(
        List<(long Number, string StringPart)> entries)
    {
        List<(long Number, string StringPart)> result = new(entries.Count + entries.Count / 4 + 1);
        for (int i = 0; i < entries.Count; i++)
        {
            result.Add(entries[i]);
            if (i % 4 == 0)
            {
                result.Add(entries[i]);
            }
        }

        return result;
    }
}
