using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CodeIndex.Indexer;

internal static class CSharpWorkspaceContractFingerprint
{
    // This is an optimization proof, never a readiness stamp. Exceeding its budget
    // leaves the existing conservative update path in charge.
    internal const int MaxFiles = 50_000;

    internal static string HashSource(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    internal static string? Build(
        List<(string Path, bool Suppressed)> targets,
        List<CSharpStaticInterfacePrepass.FileTarget> candidates,
        string?[] checksums,
        CancellationToken cancellationToken)
    {
        if (targets.Count > MaxFiles)
            return null;

        var candidateChecksums = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateChecksums[candidates[i].IndexPath] = checksums[i];
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Lookup builders receive ordered symbol lists. Bind the contributor order
        // as well as the inventory rather than assuming ambiguous lookups commute.
        Append(hash, "contributor_order");
        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (checksums[i] != null)
                Append(hash, candidates[i].IndexPath);
        }
        Append(hash, "path_inventory");
        foreach (var target in targets.OrderBy(target => target.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(hash, target.Path);
            Append(hash, target.Suppressed ? "suppressed" : "source");
            candidateChecksums.TryGetValue(target.Path, out var checksum);
            // Only sources admitted by the existing static/enum/const candidate
            // gate can contribute workspace lookup symbols. Still bind every path
            // and suppression decision, so additions, renames and removals differ.
            Append(hash, checksum ?? "no_workspace_symbols");
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
