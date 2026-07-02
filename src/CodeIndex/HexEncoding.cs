namespace CodeIndex;

internal static class HexEncoding
{
    internal static string ToLowerHexString(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return CreateLowerHexString(bytes, 0, bytes.Length);
    }

    internal static string ToLowerHexString(byte[] bytes, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if ((uint)offset > (uint)bytes.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be within the byte array.");
        if ((uint)count > (uint)(bytes.Length - offset))
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must fit within the byte array from the offset.");

        return CreateLowerHexString(bytes, offset, count);
    }

    private static string CreateLowerHexString(byte[] bytes, int offset, int count) =>
        string.Create(
            checked(count * 2),
            (Bytes: bytes, Offset: offset),
            static (chars, state) =>
            {
                const string Digits = "0123456789abcdef";
                var charIndex = 0;
                var sourceEnd = state.Offset + (chars.Length / 2);
                for (var sourceIndex = state.Offset; sourceIndex < sourceEnd; sourceIndex++)
                {
                    var value = state.Bytes[sourceIndex];
                    chars[charIndex++] = Digits[value >> 4];
                    chars[charIndex++] = Digits[value & 0x0F];
                }
            });
}
