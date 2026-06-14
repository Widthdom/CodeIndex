using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ClassifyCudaFunctionSubKinds(IEnumerable<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function")
                continue;

            var subKind = ResolveCudaFunctionSubKind(symbol.Signature);
            if (subKind != null)
                symbol.SubKind = subKind;
        }
    }

    private static string? ResolveCudaFunctionSubKind(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var hasGlobal = signature.Contains("__global__", StringComparison.Ordinal);
        if (hasGlobal)
            return "cuda_kernel";

        var hasDevice = signature.Contains("__device__", StringComparison.Ordinal);
        var hasHost = signature.Contains("__host__", StringComparison.Ordinal);
        if (hasDevice && hasHost)
            return "cuda_host_device";
        if (hasDevice)
            return "cuda_device";
        if (hasHost)
            return "cuda_host";

        return null;
    }
}
