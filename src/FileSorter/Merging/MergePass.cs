namespace FileSorter.Merging;

internal sealed record MergePass(
    IReadOnlyList<int[]> Groups,          // Run indices; each group is within the fan-in.
    IReadOnlyList<int>   CarriedForward); // Runs left unchanged in this pass.
