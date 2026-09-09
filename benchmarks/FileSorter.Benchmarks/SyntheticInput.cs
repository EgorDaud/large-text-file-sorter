using System.Text;

namespace FileSorter.Benchmarks;

// Generates deterministic "<Number>. <String>" input for benchmarks. It is separate from
// TestFileGenerator because the projects do not share internals.
internal static class SyntheticInput
{
    // Vary line lengths to exercise chunk boundaries and carry-over.
    private static readonly string[] Vocabulary =
    [
        "Apple",
        "Banana Republic",
        "Cherry Blossom Lane",
        "Date",
        "Elderberry Preserve",
        "Fig Newton Factory",
        "Grape Vineyard Road",
        "Honeydew",
        "Indigo Iris Garden",
        "Jackfruit Junction Market Street",
    ];

    // The seed gives stable input. The final line is omitted when it would exceed the target.
    public static byte[] Generate(long targetBytes, int seed)
    {
        Random random = new(seed);
        using MemoryStream buffer = new(checked((int)targetBytes) + 256);

        while (buffer.Length < targetBytes)
        {
            long number = random.NextInt64(0, 1_000_000_000);
            string word = Vocabulary[random.Next(Vocabulary.Length)];
            string line = $"{number}. {word}\n";
            byte[] encoded = Encoding.ASCII.GetBytes(line);

            if (buffer.Length + encoded.Length > targetBytes)
            {
                break;
            }

            buffer.Write(encoded, 0, encoded.Length);
        }

        return buffer.ToArray();
    }
}
