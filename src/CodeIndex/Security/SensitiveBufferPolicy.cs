using System.Buffers;
using System.Security.Cryptography;

namespace CodeIndex.Security;

internal static class SensitiveBufferPolicy
{
    internal const int GeneratedJsonCaptureInitialCapacityBytes = 16 * 1024;

    internal static int GetBoundedGeneratedJsonInitialCapacity(int maxBytes) =>
        Math.Min(Math.Max(maxBytes, 0), GeneratedJsonCaptureInitialCapacityBytes);

    internal static void ClearSensitiveBytes(Span<byte> bytes)
    {
        if (bytes.Length == 0)
            return;
        CryptographicOperations.ZeroMemory(bytes);
    }

    internal static void ClearUsedSensitiveBytes(byte[] buffer, int usedBytes)
    {
        if (usedBytes <= 0 || buffer.Length == 0)
            return;
        ClearSensitiveBytes(buffer.AsSpan(0, Math.Min(usedBytes, buffer.Length)));
    }

    internal static void ClearWholeSensitiveBuffer(byte[] buffer) =>
        ClearSensitiveBytes(buffer.AsSpan());

    internal static void ReturnSensitiveTokenBuffer(byte[] rented) =>
        ArrayPool<byte>.Shared.Return(rented, clearArray: true);

    internal static void ReturnSensitivePayloadBuffer(byte[] buffer, int usedBytes, bool rented)
    {
        ClearUsedSensitiveBytes(buffer, usedBytes);
        if (rented)
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
    }

    internal static void ReturnSensitiveCopyBuffer(byte[] rented)
    {
        ClearWholeSensitiveBuffer(rented);
        ArrayPool<byte>.Shared.Return(rented, clearArray: false);
    }

    internal static void ReturnNonSensitiveProtocolBuffer(byte[] rented) =>
        ArrayPool<byte>.Shared.Return(rented);
}
