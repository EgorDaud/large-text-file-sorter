namespace FileSorter.Merging;

internal static class MergePlanner
{
    public static IReadOnlyList<MergePass> Plan(int runCount, int fanIn)
    {
        if (fanIn < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(fanIn), fanIn,
                "A fan-in below two never reduces the run count, so no plan could ever terminate.");
        }

        // Zero or one run needs no merge pass.
        if (runCount <= 1)
        {
            return [];
        }

        List<MergePass> passes = [];
        int count = runCount;

        while (count > 1)
        {
            // Carry a single remainder forward rather than copying it through a one-run merge.
            int carried = count % fanIn == 1 ? 1 : 0;
            int toGroup = count - carried;

            int groupCount = (toGroup + fanIn - 1) / fanIn;
            int baseSize = toGroup / groupCount;
            int oversizedGroups = toGroup % groupCount; // These groups take one extra run.

            int[][] groups = new int[groupCount][];
            int cursor = 0;
            for (int g = 0; g < groupCount; g++)
            {
                int size = baseSize + (g < oversizedGroups ? 1 : 0);
                groups[g] = [.. Enumerable.Range(cursor, size)];
                cursor += size;
            }

            int[] carriedForward = [.. Enumerable.Range(cursor, carried)];
            passes.Add(new MergePass(groups, carriedForward));

            // The next pass sees merged groups in group order, followed by carried runs.
            count = groupCount + carried;
        }

        return passes;
    }
}
