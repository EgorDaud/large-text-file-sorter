using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace FileSorter.LineFormat;

internal readonly struct LineDescriptor
{
    // First eight string bytes, big-endian and right-padded with zeros.
    // StringOffset needs a full int because leading zeros can make the number field long.
    public readonly ulong Prefix;
    public readonly long  Number;        // parsed once at read time, never re-parsed
    public readonly int   StringOffset;  // string part start, absolute into the chunk buffer

    // Packing offset and length keeps the descriptor at four fields for JIT struct
    // promotion. It is still 32 bytes, but measured faster than the five-field layout.
    private readonly long _offsetAndLength;

    public LineDescriptor(ulong prefix, long number, int offset, int length, int stringOffset)
    {
        Prefix = prefix;
        Number = number;
        StringOffset = stringOffset;
        _offsetAndLength = ((long)offset << 32) | (uint)length;
    }

    public int Offset => (int)(_offsetAndLength >> 32);   // raw line start, absolute into the chunk buffer
    public int Length => (int)_offsetAndLength;           // raw line length, terminators excluded

    public int StringLength => StringLengthOf(Offset, Length, StringOffset);

    /// <summary>
    /// Parses one line (terminators already stripped) and builds its descriptor. Offsets are
    /// absolute into <paramref name="buffer"/>. Returns false for a malformed line, leaving the
    /// caller to choose the diagnostic.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryCreate(byte[] buffer, int offset, int length, out LineDescriptor descriptor)
    {
        if (!LineParser.TryParse(buffer.AsSpan(offset, length), out long number, out int stringStart))
        {
            descriptor = default;
            return false;
        }

        int stringOffset = offset + stringStart;
        int stringLength = StringLengthOf(offset, length, stringOffset);
        ulong prefix = BuildPrefix(buffer.AsSpan(stringOffset, stringLength));
        descriptor = new LineDescriptor(prefix, number, offset, length, stringOffset);
        return true;
    }

    public static int StringLengthOf(int offset, int length, int stringOffset) => offset + length - stringOffset;

    /// Returns the first eight string bytes, big-endian and right-padded with zeros.
    public static ulong BuildPrefix(ReadOnlySpan<byte> stringPart)
    {
        if (stringPart.Length >= sizeof(ulong))
        {
            return BinaryPrimitives.ReadUInt64BigEndian(stringPart);
        }

        Span<byte> padded = stackalloc byte[sizeof(ulong)];
        padded.Clear();
        stringPart.CopyTo(padded);
        return BinaryPrimitives.ReadUInt64BigEndian(padded);
    }
}
