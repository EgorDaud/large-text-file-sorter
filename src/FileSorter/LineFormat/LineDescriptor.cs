using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace FileSorter.LineFormat;

internal readonly struct LineDescriptor
{
    public readonly ulong Prefix;
    public readonly long  Number;
    public readonly int   StringOffset;

    // Packed to keep four fields for JIT struct promotion; measured faster than five fields.
    private readonly long _offsetAndLength;

    public LineDescriptor(ulong prefix, long number, int offset, int length, int stringOffset)
    {
        Prefix = prefix;
        Number = number;
        StringOffset = stringOffset;
        _offsetAndLength = ((long)offset << 32) | (uint)length;
    }

    public int Offset => (int)(_offsetAndLength >> 32);
    public int Length => (int)_offsetAndLength;

    public int StringLength => StringLengthOf(Offset, Length, StringOffset);

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
