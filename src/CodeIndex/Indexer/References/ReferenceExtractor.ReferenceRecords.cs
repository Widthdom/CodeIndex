using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static void AddReference(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Match match,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string? language = null,
        string? targetQualifier = null)
    {
        AddReference(
            references,
            seen,
            fileId,
            match.Groups["name"].Value,
            match.Groups["name"].Index,
            referenceKind,
            context,
            lineNumber,
            container,
            language,
            targetQualifier,
            match.Groups["name"].Length);
    }

    internal static void AddReference(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string name,
        int nameIndex,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string? language = null,
        string? targetQualifier = null,
        int? sourceLength = null,
        string? identitySymbolNameFolded = null)
    {
        var column = nameIndex + 1;
        var dedupeKey = CreateReferenceDedupeKey(fileId, language, lineNumber, column, referenceKind, name, container);
        if (!seen.Add(dedupeKey))
            return;
        var currentContainerReceiver = string.Equals(
            targetQualifier,
            ScientificNativeReferenceExtractor.CurrentContainerReceiverMarker,
            StringComparison.Ordinal);

        TryAddReference(references, new ReferenceRecord
        {
            FileId = fileId,
            SymbolName = name,
            IdentitySymbolNameFolded = identitySymbolNameFolded
                ?? (language == "nim" ? NimIdentifierIdentity.Fold(name) : null),
            ReferenceKind = referenceKind,
            Line = lineNumber,
            Column = column,
            SpanLength = Math.Max(1, sourceLength ?? name.Length),
            Context = context,
            ContainerKind = container?.Kind,
            ContainerName = container?.Name,
            IdentityContainerNameFolded = language == "nim"
                ? NimIdentifierIdentity.Fold(container?.Name)
                : null,
            TargetQualifier = currentContainerReceiver ? null : targetQualifier,
            SuppressInferredTargetQualifier = currentContainerReceiver,
            IsSelfReference = (targetQualifier == null || currentContainerReceiver)
                && IsSameReferenceName(container?.Name, name),
        });
    }

    internal static string BuildReferenceDedupeKey(
        long fileId,
        string? language,
        int lineNumber,
        int column,
        string referenceKind,
        string name,
        SymbolRecord? container)
        => CreateReferenceDedupeKey(fileId, language, lineNumber, column, referenceKind, name, container).ToString();

    internal static ReferenceDedupeKey CreateReferenceDedupeKey(
        long fileId,
        string? language,
        int lineNumber,
        int column,
        string referenceKind,
        string name,
        SymbolRecord? container)
        => CreateReferenceDedupeKey(
            fileId,
            language,
            lineNumber,
            column,
            referenceKind,
            name,
            container?.Kind,
            container?.Name);

    internal static ReferenceDedupeKey CreateReferenceDedupeKey(
        long fileId,
        string? language,
        int lineNumber,
        int column,
        string referenceKind,
        string name,
        string? containerKind,
        string? containerName)
        => new(
            fileId,
            string.IsNullOrWhiteSpace(language) ? "-" : language,
            lineNumber,
            column,
            referenceKind,
            string.IsNullOrWhiteSpace(containerKind) ? "-" : containerKind,
            string.IsNullOrWhiteSpace(containerName) ? "-" : containerName,
            name);

    internal static void CompactCSharpUsingAliasReferences(List<ReferenceRecord> references, string language)
    {
        var referenceCount = references.Count;
        var deduped = new HashSet<ReferenceDedupeKey>(referenceCount);
        var writeIndex = 0;
        for (var readIndex = 0; readIndex < referenceCount; readIndex++)
        {
            var reference = references[readIndex];
            var key = CreateReferenceDedupeKey(
                reference.FileId,
                language,
                reference.Line,
                reference.Column,
                reference.ReferenceKind,
                reference.SymbolName,
                reference.ContainerKind,
                reference.ContainerName);
            if (!deduped.Add(key))
                continue;

            if (writeIndex != readIndex)
                references[writeIndex] = reference;
            writeIndex++;
        }

        if (writeIndex < referenceCount)
            references.RemoveRange(writeIndex, referenceCount - writeIndex);
    }

    internal static void MarkMutualRecursionReferences(List<ReferenceRecord> references)
    {
        var edges = new HashSet<(string Caller, string Callee)>();
        Dictionary<string, string>? normalizedNames = null;
        foreach (var reference in references)
        {
            if (!IsCallGraphLikeReferenceKind(reference.ReferenceKind)
                || string.IsNullOrWhiteSpace(reference.ContainerName)
                || string.IsNullOrWhiteSpace(reference.SymbolName)
                || reference.IsSelfReference)
            {
                continue;
            }

            edges.Add((
                GetCachedNormalizedReferenceCycleName(reference.ContainerName, ref normalizedNames),
                GetCachedNormalizedReferenceCycleName(reference.SymbolName, ref normalizedNames)));
        }

        if (edges.Count == 0)
            return;

        foreach (var reference in references)
        {
            if (!IsCallGraphLikeReferenceKind(reference.ReferenceKind)
                || string.IsNullOrWhiteSpace(reference.ContainerName)
                || string.IsNullOrWhiteSpace(reference.SymbolName)
                || reference.IsSelfReference)
            {
                continue;
            }

            var caller = GetCachedNormalizedReferenceCycleName(reference.ContainerName, ref normalizedNames);
            var callee = GetCachedNormalizedReferenceCycleName(reference.SymbolName, ref normalizedNames);
            if (edges.Contains((callee, caller)))
                reference.IsMutualRecursion = true;
        }
    }

    private static string GetCachedNormalizedReferenceCycleName(
        string name,
        ref Dictionary<string, string>? normalizedNames)
    {
        if (normalizedNames != null && normalizedNames.TryGetValue(name, out var normalizedName))
            return normalizedName;

        normalizedName = NormalizeReferenceCycleName(name);
        if (ReferenceEquals(normalizedName, name))
            return normalizedName;

        normalizedNames ??= new Dictionary<string, string>(StringComparer.Ordinal);
        normalizedNames.Add(name, normalizedName);
        return normalizedName;
    }

    private static bool IsCallGraphLikeReferenceKind(string referenceKind)
        => referenceKind is "call" or "instantiate" or "subscribe" or "unsubscribe" or "razor_event_binding";

    private static bool IsSameReferenceName(string? left, string right)
        => !string.IsNullOrWhiteSpace(left)
            && string.Equals(NormalizeReferenceCycleName(left), NormalizeReferenceCycleName(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeReferenceCycleName(string name)
    {
        var trimmed = name.Trim();
        var dot = trimmed.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < trimmed.Length)
            return trimmed[(dot + 1)..];
        var colon = trimmed.LastIndexOf("::", StringComparison.Ordinal);
        return colon >= 0 && colon + 2 < trimmed.Length ? trimmed[(colon + 2)..] : trimmed;
    }

    private const int MaxPythonLogicalReferenceLineLength = 32_768;

}
