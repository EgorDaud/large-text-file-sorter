namespace TestFileGenerator.Generation;

internal sealed record GeneratorOptions(
    string OutputPath,
    long   TargetBytes,
    int    Seed,
    double DuplicateRatio);
