using TestFileGenerator.Generation;

namespace FileSorter.Benchmarks;

// Benchmark input is the real generator's output, so a change to key handling is measured
// against the same string-part vocabulary, number shapes and duplicate mix the shipped file
// has. The generator's default duplicate ratio is used for the same reason.
internal static class SyntheticInput
{
    private const double DuplicateRatio = 0.1;

    // The seed gives stable input. The final line is omitted when it would exceed the target.
    public static byte[] Generate(long targetBytes, int seed)
    {
        // The generator's output size is only known once it has run, and an array cannot
        // be shrunk. A counting pass with the same seed sizes the array exactly, which
        // avoids both a growing stream and a trimming copy.
        long exactBytes = FileWriter.Write(Stream.Null, NewComposer(seed), targetBytes, CancellationToken.None);

        byte[] data = new byte[checked((int)exactBytes)];
        using MemoryStream fixedBuffer = new(data);
        FileWriter.Write(fixedBuffer, NewComposer(seed), targetBytes, CancellationToken.None);
        return data;
    }

    private static LineComposer NewComposer(int seed) =>
        new(new GeneratorOptions(OutputPath: string.Empty, TargetBytes: 0, seed, DuplicateRatio));
}
