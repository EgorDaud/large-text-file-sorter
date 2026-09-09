using System.Text;

namespace TestFileGenerator.Generation;

/// UTF-8 words used to generate varied string parts, including non-ASCII data.
internal static class Vocabulary
{
    private static readonly string[] Source =
    [
        "Apple", "Banana", "Cherry", "Date", "Elderberry", "Fig", "Grape", "Honeydew",
        "Kiwi", "Lemon", "Mango", "Nectarine", "Orange", "Papaya", "Quince", "Raspberry",
        "Strawberry", "Tangerine", "Watermelon", "Pomegranate", "Blackcurrant", "Apricot",
        "is", "was", "and", "the", "a", "of", "in", "with", "without", "under", "over",
        "yellow", "red", "green", "blue", "purple", "orange", "black", "white", "silver",
        "something", "anything", "everything", "nothing", "somewhere", "elsewhere",
        "quick", "slow", "bright", "dull", "heavy", "light", "extraordinary", "plain",
        "morning", "evening", "midnight", "afternoon", "yesterday", "tomorrow",
        "river", "mountain", "harbour", "meadow", "lighthouse", "cathedral", "warehouse",
        "sorting", "merging", "streaming", "buffering", "spilling", "comparing",
        "APPLE", "Banana Bread", "ZEBRA", "aardvark", "Zulu", "zulu",
        "Café", "naïve", "façade", "jalapeño", "Grüße", "smörgås", "Zürich", "piñata",
        "crème", "Ünicode", "日本語", "Москва", "τέλος", "ελλάδα", "Ærø", "Ångström",
    ];

    public static readonly byte[][] Words = BuildWords();

    public static readonly int LongestWordBytes = LongestOf(Words);

    private static byte[][] BuildWords()
    {
        byte[][] words = new byte[Source.Length][];
        for (int i = 0; i < Source.Length; i++)
        {
            words[i] = Encoding.UTF8.GetBytes(Source[i]);
        }

        return words;
    }

    private static int LongestOf(byte[][] words)
    {
        int longest = 0;
        foreach (byte[] word in words)
        {
            longest = Math.Max(longest, word.Length);
        }

        return longest;
    }
}
