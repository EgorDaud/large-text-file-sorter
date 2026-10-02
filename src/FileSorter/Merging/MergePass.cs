namespace FileSorter.Merging;

internal sealed record MergePass(
    IReadOnlyList<int[]> Groups,
    IReadOnlyList<int>   CarriedForward);
