namespace TestFileGenerator.Generation;

internal sealed record GeneratorOptions(
    string OutputPath,
    long   TargetBytes,
    int    Seed,
    double DuplicateRatio)
{
    internal const double DefaultDuplicateRatio = 0.1;
}
