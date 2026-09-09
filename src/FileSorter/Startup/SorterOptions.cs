namespace FileSorter.Startup;

internal sealed record SorterOptions(
    string InputPath,
    string OutputPath,
    string TempDirectory,
    long   MemoryBudgetBytes,
    int    MaxLineLength,
    int    Parallelism,
    Pipeline Pipeline);

internal enum Pipeline { Akka, Channels }
