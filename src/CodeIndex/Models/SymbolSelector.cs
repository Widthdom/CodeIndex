using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeIndex.Models;

/// <summary>
/// Stable selector for one indexed symbol identity.
/// インデックス済みシンボル identity を 1 件選択する安定 selector。
/// </summary>
public readonly record struct SymbolSelector(long SymbolId, string? GenerationFingerprint = null)
{
    private const string GenerationSeparator = "@g:";

    public static bool TryParse(string? value, out SymbolSelector selector)
    {
        selector = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var generationSeparatorIndex = value.IndexOf(GenerationSeparator, StringComparison.Ordinal);
        var idValue = generationSeparatorIndex >= 0
            ? value.AsSpan(0, generationSeparatorIndex)
            : value.AsSpan();
        string? generationFingerprint = null;
        if (generationSeparatorIndex >= 0)
        {
            var generationValue = value.AsSpan(generationSeparatorIndex + GenerationSeparator.Length);
            if (generationValue.Length != 16 || !generationValue.ToString().All(Uri.IsHexDigit))
                return false;
            generationFingerprint = generationValue.ToString().ToLowerInvariant();
        }

        if (!idValue.StartsWith("id:", StringComparison.Ordinal)
            || !long.TryParse(
                idValue["id:".Length..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var symbolId)
            || symbolId <= 0)
        {
            return false;
        }

        selector = new SymbolSelector(symbolId, generationFingerprint);
        return true;
    }

    public static string BuildGenerationFingerprint(string generationIdentity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(generationIdentity));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public override string ToString()
        => GenerationFingerprint == null
            ? $"id:{SymbolId.ToString(CultureInfo.InvariantCulture)}"
            : $"id:{SymbolId.ToString(CultureInfo.InvariantCulture)}{GenerationSeparator}{GenerationFingerprint}";
}
