using CsCheck;
using FileSorter.LineFormat;
using FileSorter.Merging;
using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>
/// Property coverage for KWayMerge's loser tree. The curated cases pin the shapes worth
/// naming -- empty runs, ties, buffer stability, staging -- while this fuzzes the one
/// dimension a curated set cannot: many runs of many random lengths, including empty
/// ones, so the tree's build-time replay order and its per-line replay-after-advance
/// path are both exercised at every fan-in from 1 to 70.
/// </summary>
public sealed class KWayMergePropertyTests
{
    // The window is the smallest RunCursor accepts (maxLineLength + 2), so every run
    // takes many fills rather than one: the loser tree has to survive a run's buffer
    // being refilled and re-scanned repeatedly, not merely built once and drained.
    private const int MaxLineLength = 32;
    private const int WindowSize = MaxLineLength + 2;
    private const int DescriptorCapacity = 3;
    private const int OutputStagingBufferSize = 64;

    private static readonly Gen<char> PrintableAsciiChar = Gen.Char[' ', '~'];
    private static readonly Gen<string> PrintableStringPart = Gen.String[PrintableAsciiChar, 0, 10];

    // One roll in twenty ends the string part in a '\r' that is content, not the first
    // half of a terminator; LineEntryGen.BuildInput writes the second '\r' that keeps it
    // so. This class draws its own string parts, so it needs its own such chance.
    private static readonly Gen<string> StringPart =
        Gen.Select(PrintableStringPart, Gen.Int[0, 19], (s, roll) => roll == 0 ? s + "\r" : s);

    // Narrow enough that even the zero-padded twin LineEntryGen.BuildInput adds stays
    // inside MaxLineLength: "-999999" is the longest rendered number at 7 characters,
    // its twin "-00999999" is 9, plus ". " plus up to 10 string-part bytes is 21,
    // comfortably under the 32-byte cap above.
    private static readonly Gen<long> Number = Gen.Long[-999_999, 999_999];

    private static readonly Gen<(long Number, string StringPart)> Entry = Gen.Select(Number, StringPart);

    // Up to 120 entries before duplication, which after the doubling below and
    // LineEntryGen's twin injection covers the whole 1..70 run-count range: at 70 runs
    // many are forced empty, at 1 every line lands in the same one.
    private static readonly Gen<List<(long Number, string StringPart)>> BaseEntries = Entry.List[0, 120];

    private static readonly Gen<int> RunCount = Gen.Int[1, 70];

    // Bucket assignment is generated rather than picked with a plain Random so that it
    // shrinks and replays with the rest of the scenario. It depends on the entry count,
    // known only once BaseEntries is sampled, and on the run count, which is what
    // SelectMany is for here.
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

            // Splitting the multiset by bucket index, rather than by slicing a
            // pre-sorted sequence, is what lands byte-identical lines in different
            // runs: adjacent copies in a sorted sequence almost always slice into the
            // same run.
            List<(long Number, string StringPart)>[] perRun = [.. Enumerable.Range(0, runCount).Select(_ => new List<(long, string)>())];
            for (int i = 0; i < entries.Count; i++)
            {
                perRun[bucket[i]].Add(entries[i]);
            }

            // Each run's bytes are sorted by the independent oracle, so the fixture
            // feeding the merge is never produced by the code being checked. The
            // expected output is that same sort over every run's bytes concatenated: a
            // merge of sorted partitions of a multiset must equal a sort of the whole
            // multiset, however the partition was drawn.
            byte[][] rawRunBytes = [.. perRun.Select(entries => LineEntryGen.BuildInput(entries))];
            byte[] expected = NaiveReferenceSort.Sort([.. rawRunBytes.SelectMany(b => b)]);
            byte[][] sortedRunBytes = [.. rawRunBytes.Select(NaiveReferenceSort.Sort)];

            MemoryStream[] runs = [.. sortedRunBytes.Select(b => new MemoryStream(b))];
            RunCursorBuffers[] cursorBuffers = [.. Enumerable.Range(0, runCount)
                .Select(_ => new RunCursorBuffers(new byte[WindowSize], new byte[WindowSize], new LineDescriptor[DescriptorCapacity]))];
            using MemoryStream output = new();
            byte[] outputStagingBuffer = new byte[OutputStagingBufferSize];

            // MergeAsync disposes every run stream it is handed, so the streams created
            // above are not this test's to dispose.
            await KWayMerge.MergeAsync(
                runs, cursorBuffers, output, outputStagingBuffer, MaxLineLength,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal(expected, output.ToArray());
        }, iter: 1_000);
    }

    // Roughly every fourth entry gets a byte-for-byte copy appended after it -- not the
    // zero-padded twin BuildInput adds, which changes the raw bytes on purpose, but the
    // same tuple, rendering an identical line. Two independently drawn pairs colliding
    // is rare enough over 120 entries that without this the property could run its whole
    // budget without ever splitting a byte-identical pair across two runs.
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
