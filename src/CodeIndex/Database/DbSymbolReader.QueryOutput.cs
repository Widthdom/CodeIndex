namespace CodeIndex.Database;

public partial class DbReader
{
    private const int QueryOutputSignatureMaxChars = 512;
    private const string QueryOutputSignatureTruncationSuffix = "...";

    private static void ApplyQueryOutputSignatureLimits(IEnumerable<SymbolResult> symbols)
    {
        foreach (var symbol in symbols)
            ApplyQueryOutputSignatureLimit(symbol);
    }

    private static void ApplyQueryOutputSignatureLimit(SymbolResult symbol)
    {
        if (!TryTruncateQueryOutputSignature(symbol.Signature, out var signature, out var originalLength))
            return;

        symbol.Signature = signature;
        symbol.SignatureTruncated = true;
        symbol.SignatureOriginalLength = originalLength;
    }

    private static bool TryTruncateQueryOutputSignature(string? signature, out string? truncatedSignature, out int? originalLength)
    {
        truncatedSignature = signature;
        originalLength = null;
        if (signature == null || signature.Length <= QueryOutputSignatureMaxChars)
            return false;

        originalLength = signature.Length;
        truncatedSignature = signature[..(QueryOutputSignatureMaxChars - QueryOutputSignatureTruncationSuffix.Length)]
            + QueryOutputSignatureTruncationSuffix;
        return true;
    }

    private static void ApplyQueryOutputSignatureLimits(IEnumerable<OutlineSymbol> symbols)
    {
        foreach (var symbol in symbols)
            ApplyQueryOutputSignatureLimit(symbol);
    }

    private static void ApplyQueryOutputSignatureLimit(OutlineSymbol symbol)
    {
        if (!TryTruncateQueryOutputSignature(symbol.Signature, out var signature, out var originalLength))
            return;

        symbol.Signature = signature;
        symbol.SignatureTruncated = true;
        symbol.SignatureOriginalLength = originalLength;
    }
}
