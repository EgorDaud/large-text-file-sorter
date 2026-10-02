using TestFileGenerator.Generation;

namespace FileSorter.Benchmarks;

internal static class SyntheticInput
{
    public static byte[] Generate(long targetBytes, int seed)
    {
        // A counting pass with the same seed sizes the array exactly.
        long exactBytes = FileWriter.Write(Stream.Null, NewComposer(seed), targetBytes, CancellationToken.None);

        byte[] data = new byte[checked((int)exactBytes)];
        using MemoryStream fixedBuffer = new(data);
        FileWriter.Write(fixedBuffer, NewComposer(seed), targetBytes, CancellationToken.None);
        return data;
    }

    private static LineComposer NewComposer(int seed) =>
        new(new GeneratorOptions(OutputPath: string.Empty, TargetBytes: 0, seed, GeneratorOptions.DefaultDuplicateRatio));
}
