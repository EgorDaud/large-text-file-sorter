using System.Diagnostics;
using System.Globalization;

namespace TestFileGenerator.Generation;

// Unshared parts end in a unique token so accidental duplicates never inflate the configured ratio.
internal sealed class LineComposer
{
    private const byte Separator = (byte)'.';
    private const byte Space = (byte)' ';
    private const byte Terminator = (byte)'\n';

    private const int MaxNumberDigits = 19;
    private const int MaxTokenBytes = 13;
    private const int MaxWordsPerStringPart = 4;
    private const int SharedStringPartCount = 64;

    private static ReadOnlySpan<byte> Base36Digits => "0123456789abcdefghijklmnopqrstuvwxyz"u8;

    private static readonly long[] PowersOfTen = BuildPowersOfTen();

    private readonly Random _random;
    private readonly double _duplicateRatio;
    private readonly byte[][] _sharedStringParts;
    private readonly byte[] _shortestSharedStringPart;

    private long _nextToken;
    private bool _forcedPairAttempted;

    private long? _pendingPairNumber;
    private byte[]? _pendingPairStringPart;
    private int _pendingPairLength;

    public LineComposer(GeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.DuplicateRatio);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.DuplicateRatio, 1.0);

        _duplicateRatio = options.DuplicateRatio;
        _random = new Random(options.Seed);
        _sharedStringParts = BuildSharedStringParts();
        _shortestSharedStringPart = ShortestOf(_sharedStringParts);
    }

    public static int MaxComposedLineLength =>
        MaxNumberDigits + 2 + MaxStringPartBytes + 1;

    private static int MaxStringPartBytes =>
        (MaxWordsPerStringPart * (Vocabulary.LongestWordBytes + 1)) + MaxTokenBytes;

    public bool TryComposeNext(Span<byte> destination, long remainingBytes, out int written)
    {
        if (destination.Length < MaxComposedLineLength)
        {
            throw new ArgumentException(
                $"A destination of at least {MaxComposedLineLength} bytes is required.",
                nameof(destination));
        }

        if (_pendingPairStringPart is { } pendingStringPart)
        {
            if (_pendingPairLength > remainingBytes)
            {
                written = 0;
                return false;
            }

            long pendingNumber = _pendingPairNumber!.Value;
            _pendingPairNumber = null;
            _pendingPairStringPart = null;
            written = WriteLine(destination, pendingNumber, pendingStringPart);
            return true;
        }

        if (!_forcedPairAttempted)
        {
            _forcedPairAttempted = true;
            if (_duplicateRatio > 0 && TryReserveForcedPair(destination, remainingBytes, out written))
            {
                return true;
            }
        }

        // Draw before checking the budget, so the target changes only where output stops.
        bool shareStringPart = _random.NextDouble() < _duplicateRatio;
        long number = NextNumber();

        int position = WriteNumber(destination, number);
        destination[position++] = Separator;
        destination[position++] = Space;
        position += shareStringPart
            ? CopySharedStringPart(destination[position..])
            : ComposeStringPart(destination[position..]);
        destination[position++] = Terminator;

        if (position > remainingBytes)
        {
            written = 0;
            return false;
        }

        written = position;
        return true;
    }

    // Both candidate pairs are always drawn, so later seeded output does not depend on the target.
    private bool TryReserveForcedPair(Span<byte> destination, long remainingBytes, out int written)
    {
        byte[] ordinaryStringPart = _sharedStringParts[_random.Next(_sharedStringParts.Length)];
        long ordinaryFirstNumber = NextNumber();
        long ordinarySecondNumber = NextNumber();
        long fallbackFirstNumber = _random.Next(10);
        long fallbackSecondNumber = _random.Next(10);

        long ordinaryFirstLength = ComposedLineLength(ordinaryFirstNumber, ordinaryStringPart);
        long ordinarySecondLength = ComposedLineLength(ordinarySecondNumber, ordinaryStringPart);
        if (ordinaryFirstLength + ordinarySecondLength <= remainingBytes)
        {
            written = WriteLine(destination, ordinaryFirstNumber, ordinaryStringPart);
            _pendingPairNumber = ordinarySecondNumber;
            _pendingPairStringPart = ordinaryStringPart;
            _pendingPairLength = (int)ordinarySecondLength;
            return true;
        }

        long fallbackFirstLength = ComposedLineLength(fallbackFirstNumber, _shortestSharedStringPart);
        long fallbackSecondLength = ComposedLineLength(fallbackSecondNumber, _shortestSharedStringPart);
        if (fallbackFirstLength + fallbackSecondLength <= remainingBytes)
        {
            written = WriteLine(destination, fallbackFirstNumber, _shortestSharedStringPart);
            _pendingPairNumber = fallbackSecondNumber;
            _pendingPairStringPart = _shortestSharedStringPart;
            _pendingPairLength = (int)fallbackSecondLength;
            return true;
        }

        written = 0;
        return false;
    }

    private static int WriteLine(Span<byte> destination, long number, byte[] stringPart)
    {
        int position = WriteNumber(destination, number);
        destination[position++] = Separator;
        destination[position++] = Space;
        stringPart.CopyTo(destination[position..]);
        position += stringPart.Length;
        destination[position++] = Terminator;
        return position;
    }

    private static int ComposedLineLength(long number, byte[] stringPart) => NumberLength(number) + 2 + stringPart.Length + 1;

    private static int NumberLength(long number)
    {
        Span<byte> scratch = stackalloc byte[MaxNumberDigits];
        bool formatted = number.TryFormat(scratch, out int written, provider: CultureInfo.InvariantCulture);
        Debug.Assert(formatted, "The scratch buffer is sized for the widest number the generator draws.");
        return written;
    }

    private static int WriteNumber(Span<byte> destination, long number)
    {
        bool formatted = number.TryFormat(destination, out int written, provider: CultureInfo.InvariantCulture);
        Debug.Assert(formatted, "The destination is sized for the widest number the generator draws.");
        return written;
    }

    private long NextNumber()
    {
        // Width first: a uniform long would almost always have 18 or 19 digits.
        int digits = _random.Next(1, MaxNumberDigits + 1);
        long lowest = digits == 1 ? 0 : PowersOfTen[digits - 1];
        long exclusiveUpperBound = digits == MaxNumberDigits ? long.MaxValue : PowersOfTen[digits];
        return _random.NextInt64(lowest, exclusiveUpperBound);
    }

    private int ComposeStringPart(Span<byte> destination)
    {
        int words = _random.Next(1, MaxWordsPerStringPart + 1);
        int position = 0;

        for (int word = 0; word < words; word++)
        {
            if (word > 0)
            {
                destination[position++] = Space;
            }

            ReadOnlySpan<byte> chosen = Vocabulary.Words[_random.Next(Vocabulary.Words.Length)];
            chosen.CopyTo(destination[position..]);
            position += chosen.Length;
        }

        destination[position++] = Space;
        return position + WriteToken(destination[position..], _nextToken++);
    }

    private int CopySharedStringPart(Span<byte> destination)
    {
        ReadOnlySpan<byte> chosen = _sharedStringParts[_random.Next(_sharedStringParts.Length)];
        chosen.CopyTo(destination);
        return chosen.Length;
    }

    private static int WriteToken(Span<byte> destination, long value)
    {
        Span<byte> token = stackalloc byte[MaxTokenBytes];
        int start = token.Length;

        do
        {
            token[--start] = Base36Digits[(int)(value % 36)];
            value /= 36;
        }
        while (value > 0);

        token[start..].CopyTo(destination);
        return token.Length - start;
    }

    private byte[][] BuildSharedStringParts()
    {
        byte[][] parts = new byte[SharedStringPartCount][];
        Span<byte> scratch = new byte[MaxStringPartBytes];

        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = scratch[..ComposeStringPart(scratch)].ToArray();
        }

        return parts;
    }

    private static byte[] ShortestOf(byte[][] parts)
    {
        byte[] shortest = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length < shortest.Length)
            {
                shortest = parts[i];
            }
        }

        return shortest;
    }

    public long MinimalForcedPairByteLength => 2 * ComposedLineLength(0, _shortestSharedStringPart);

    private static long[] BuildPowersOfTen()
    {
        long[] powers = new long[MaxNumberDigits];
        powers[0] = 1;
        for (int i = 1; i < powers.Length; i++)
        {
            powers[i] = powers[i - 1] * 10;
        }

        return powers;
    }
}
