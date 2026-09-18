using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitCoreCSharpTypeReferences(
        in CoreTypeReferenceContext type,
        ref CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern)
    {
        ref readonly var line = ref type.Line;
        if (line.Language is not "csharp")
            return;

        if (line.PreparedLine.IndexOf("=>", StringComparison.Ordinal) >= 0)
        {
            EmitCSharpLambdaCaptureReferences(
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.Container,
                type.CSharpLocalNamesByFunction);
        }

        CSharpReferenceExtractor.EmitTypePositionReferences(
            line.PreparedLine,
            line.OriginalLine,
            type.CSharpQualifiedConstantPatternMemberLookup,
            type.CSharpQualifiedTypePatternLookup,
            type.CSharpUsingAliases,
            type.CSharpUsingStatics,
            type.Lookups.HasActiveSameFileCSharpTypeCandidate,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall,
            line.Container,
            type.PendingCSharpWhereConstraint!,
            ref pendingCSharpMultiLineTypePattern);

        if (CSharpReferenceExtractor.HasTrailingIsAsTypePatternIntro(line.PreparedLine, line.OriginalLine))
        {
            CSharpReferenceExtractor.StartWaitingForMultiLineTypePatternHead(
                ref pendingCSharpMultiLineTypePattern);
        }

        if (CSharpReferenceExtractor.HasTrailingCaseTypePatternIntro(line.PreparedLine, line.OriginalLine))
        {
            CSharpReferenceExtractor.StartWaitingForMultiLineTypePatternHead(
                ref pendingCSharpMultiLineTypePattern);
        }

        TrackCSharpLocalDeclarations(
            line.PreparedLine,
            line.Container,
            type.CSharpLocalNamesByFunction);
    }

    private static void EmitCSharpLambdaCaptureReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Dictionary<CSharpLocalScopeKey, HashSet<string>>? localNamesByFunction)
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
            var candidateCount = localNames.Count;
            foreach (var parameterName in parameterNames)
            {
                if (localNames.Contains(parameterName))
                    candidateCount--;
            }
            if (candidateCount == 0)
                continue;

            var bodyIdentifiers = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match match in BoundedRegex.EnumerateMatches(body, CSharpIdentifierPattern))
            {
                var name = NormalizeAtPrefixedIdentifier(match.Value);
                if (localNames.Contains(name) && !parameterNames.Contains(name))
                    bodyIdentifiers.TryAdd(name, match.Index);
                if (bodyIdentifiers.Count == candidateCount)
                    break;
            }

            // Keep declaration order and the first occurrence's original column.
            foreach (var localName in localNames)
            {
                if (!bodyIdentifiers.TryGetValue(localName, out var bodyRelativeIndex))
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

    private static void TrackCSharpLocalDeclarations(
        string preparedLine,
        SymbolRecord? container,
        Dictionary<CSharpLocalScopeKey, HashSet<string>>? localNamesByFunction)
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

    // Keep every scope discriminator without formatting a new string per declaration.
    private readonly record struct CSharpLocalScopeKey(
        string Kind,
        string ContainerQualifiedName,
        string ContainerKind,
        string ContainerName,
        string Name,
        int StartLine,
        int EndLine,
        int? BodyStartLine,
        int? BodyEndLine,
        int? StartColumn);

    private static CSharpLocalScopeKey GetCSharpContainerLocalScopeKey(SymbolRecord container)
        => new(
            container.Kind,
            container.ContainerQualifiedName ?? "",
            container.ContainerKind ?? "",
            container.ContainerName ?? "",
            container.Name,
            container.StartLine,
            container.EndLine,
            container.BodyStartLine,
            container.BodyEndLine,
            container.StartColumn);
}
