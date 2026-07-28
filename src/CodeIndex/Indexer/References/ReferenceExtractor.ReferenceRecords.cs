using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitPhpLinePreambleReferences(
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        int lineNumber,
        Func<SymbolRecord?> getLineContainer,
        ref bool inDocblock,
        ref SymbolRecord? docblockContainer,
        ref HashSet<string>? docblockPropertyNames)
    {
        if (originalLine.Contains("#[", StringComparison.Ordinal))
        {
            var attributeContext = originalLine.Trim();
            if (attributeContext.Length > 0)
            {
                PhpReferenceExtractor.EmitAttributeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    attributeContext,
                    lineNumber,
                    getLineContainer());
            }
        }

        if (originalLine.IndexOf("/**", StringComparison.Ordinal) >= 0)
        {
            inDocblock = true;
            docblockContainer = getLineContainer();
            docblockPropertyNames = null;
        }

        var docblockContext = originalLine.Trim();
        if (docblockContext.Length > 0)
        {
            if (originalLine.Contains("param", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockParamTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("return", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockReturnTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("var", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockVarTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("@throws", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockThrowsTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("extends", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockExtendsTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("implements", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockImplementsTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("@mixin", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockMixinTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("property", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockPropertyTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer),
                    inDocblock,
                    ref docblockPropertyNames);
            }

            if (originalLine.Contains("@method", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockMethodReturnTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
                PhpReferenceExtractor.EmitDocblockMethodParameterTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("@template", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockTemplateBoundTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("type", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockTypeAliasTargetReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
                PhpReferenceExtractor.EmitDocblockImportTypeSourceReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }
        }

        if (inDocblock && originalLine.IndexOf("*/", StringComparison.Ordinal) >= 0)
        {
            inDocblock = false;
            docblockContainer = null;
            docblockPropertyNames = null;
        }
    }

    private static SymbolRecord? ResolvePhpDocblockContainer(
        bool inDocblock,
        SymbolRecord? docblockContainer,
        Func<SymbolRecord?> getLineContainer)
        => inDocblock ? docblockContainer : getLineContainer();

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

    private static void EmitCSharpLambdaCaptureReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Dictionary<string, HashSet<string>>? localNamesByFunction)
    {
        if (container?.Kind != "function"
            || localNamesByFunction == null
            || !localNamesByFunction.TryGetValue(GetCSharpContainerLocalScopeKey(container), out var localNames)
            || localNames.Count == 0)
        {
            return;
        }

        foreach (Match lambda in BoundedRegex.EnumerateMatches(CSharpLambdaRegex, preparedLine))
        {
            var body = lambda.Groups["body"].Value;
            if (string.IsNullOrWhiteSpace(body))
                continue;

            var parameterNames = CollectCSharpLambdaParameterNames(lambda.Groups["params"].Value);
            foreach (var localName in localNames)
            {
                if (parameterNames.Contains(localName))
                    continue;
                if (!ContainsCSharpIdentifier(body, localName, out var bodyRelativeIndex))
                    continue;

                AddReference(
                    references,
                    seen,
                    fileId,
                    localName,
                    lambda.Groups["body"].Index + bodyRelativeIndex,
                    "capture",
                    context,
                    lineNumber,
                    container,
                    "csharp");
            }
        }
    }

    private static HashSet<string> CollectCSharpLambdaParameterNames(string parameterText)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in BoundedRegex.EnumerateMatches(parameterText, CSharpIdentifierPattern))
        {
            var name = NormalizeAtPrefixedIdentifier(match.Value);
            if (!IsIgnoredCallName("csharp", name))
                names.Add(name);
        }

        return names;
    }

    private static bool ContainsCSharpIdentifier(string text, string name, out int index)
    {
        index = -1;
        var normalizedName = NormalizeAtPrefixedIdentifier(name);
        foreach (Match match in BoundedRegex.EnumerateMatches(text, CSharpIdentifierPattern))
        {
            if (string.Equals(NormalizeAtPrefixedIdentifier(match.Value), normalizedName, StringComparison.Ordinal))
            {
                index = match.Index;
                return true;
            }
        }

        return false;
    }

    private static void TrackCSharpLocalDeclarations(
        string preparedLine,
        SymbolRecord? container,
        Dictionary<string, HashSet<string>>? localNamesByFunction)
    {
        if (container?.Kind != "function" || localNamesByFunction == null)
            return;
        if (preparedLine.Contains("=>", StringComparison.Ordinal))
            return;

        foreach (Match match in BoundedRegex.EnumerateMatches(CSharpLocalDeclarationRegex, preparedLine))
        {
            var name = NormalizeAtPrefixedIdentifier(match.Groups["name"].Value);
            if (IsIgnoredCallName("csharp", name))
                continue;

            var scopeKey = GetCSharpContainerLocalScopeKey(container);
            if (!localNamesByFunction.TryGetValue(scopeKey, out var localNames))
            {
                localNames = new HashSet<string>(StringComparer.Ordinal);
                localNamesByFunction[scopeKey] = localNames;
            }

            localNames.Add(name);
        }
    }

    private static string GetCSharpContainerLocalScopeKey(SymbolRecord container)
        => $"{container.Kind}:{container.ContainerQualifiedName}:{container.ContainerKind}:{container.ContainerName}:{container.Name}:{container.StartLine}:{container.EndLine}:{container.BodyStartLine}:{container.BodyEndLine}:{container.StartColumn}";

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
