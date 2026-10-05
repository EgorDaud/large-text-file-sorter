using TestFileGenerator.Generation;

namespace FileSorter.Benchmarks;

internal static class SyntheticInput
{
    public static byte[] Generate(long targetBytes, int seed)
    {
        // A counting pass with the same seed sizes the array exactly.
        long exactBytes = NewWriter(seed, targetBytes).Write(Stream.Null, CancellationToken.None);

        byte[] data = new byte[checked((int)exactBytes)];
        using MemoryStream fixedBuffer = new(data);
        NewWriter(seed, targetBytes).Write(fixedBuffer, CancellationToken.None);
        return data;
    }

    private static FileWriter NewWriter(int seed, long targetBytes) =>
        new(new LineComposer(new GeneratorOptions(
            OutputPath: string.Empty, TargetBytes: 0, seed, GeneratorOptions.DefaultDuplicateRatio)), targetBytes);
}
