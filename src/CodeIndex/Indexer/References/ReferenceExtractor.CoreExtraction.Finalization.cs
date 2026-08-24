using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static List<ReferenceRecord> FinalizeCoreExtraction(
        CoreReferenceLoopContext loop,
        CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern)
    {
        var request = loop.Request;
        var references = loop.References;
        if (!ReferenceLimitReached(references)
            && request.Language == "csharp")
        {
            CSharpReferenceExtractor.EmitSwitchExpressionTypePatternReferences(
                loop.Preparation.Lines,
                loop.Preparation.PreparedLines,
                loop.ContainerCandidates,
                loop.CSharpQualifiedConstantPatternMemberLookup,
                loop.CSharpQualifiedTypePatternLookup,
                loop.CSharpUsingAliases,
                loop.CSharpUsingStatics,
                loop.Lookups.HasActiveSameFileCSharpTypeCandidate,
                references,
                loop.Seen,
                request.FileId);

            var pendingReferenceStartIndex = references.Count;
            CSharpReferenceExtractor
                .FlushPendingMultiLineTypePatternReference(
                    ref pendingCSharpMultiLineTypePattern,
                    loop.CSharpQualifiedConstantPatternMemberLookup,
                    loop.CSharpUsingAliases,
                    loop.CSharpUsingStatics,
                    loop.Lookups.HasActiveSameFileCSharpTypeCandidate,
                    references,
                    loop.Seen,
                    request.FileId);
            NormalizeRawSourceLineContexts(
                loop.Preparation.Lines,
                references,
                pendingReferenceStartIndex);
        }

        if (request.Language == "csharp")
        {
            RewriteCSharpPropertyReceiverReferences(
                loop.Preparation.PreparedLines,
                references,
                loop.Lookups);
            RemoveCSharpCallsDuplicatedByMemberReads(references);
        }

        loop.Lookups.ApplyCSharpUsingAliasReferenceNames(references);
        if (!ReferenceLimitReached(references))
        {
            loop.Lookups.EmitCSharpBclRegexWithoutTimeoutReferences(
                references,
                loop.Seen);
        }
        MarkMutualRecursionReferences(references);
        return references;
    }

    private static void RemoveCSharpCallsDuplicatedByMemberReads(
        List<ReferenceRecord> references)
    {
        var memberReadSites = references
            .Where(reference => reference.ReferenceKind == "member_read")
            .Select(reference => (
                reference.FileId,
                reference.Line,
                reference.Column,
                reference.SymbolName,
                reference.ContainerKind,
                reference.ContainerName))
            .ToHashSet();
        if (memberReadSites.Count == 0)
            return;

        references.RemoveAll(reference =>
            reference.ReferenceKind == "call"
            && memberReadSites.Contains((
                reference.FileId,
                reference.Line,
                reference.Column,
                reference.SymbolName,
                reference.ContainerKind,
                reference.ContainerName)));
    }

    private static void RewriteCSharpPropertyReceiverReferences(
        IReadOnlyList<string> preparedLines,
        List<ReferenceRecord> references,
        CoreExtractionLookups lookups)
    {
        foreach (var reference in references)
        {
            if (reference.ReferenceKind != "type_reference"
                || reference.Line <= 0
                || reference.Line > preparedLines.Count
                || reference.Column <= 0)
            {
                continue;
            }

            var line = preparedLines[reference.Line - 1];
            var tokenEnd =
                reference.Column - 1 + reference.SymbolName.Length;
            if (tokenEnd >= line.Length
                || !line.AsSpan(tokenEnd)
                    .TrimStart()
                    .StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            var owner = lookups.FindCSharpContainerCandidate(
                reference.ContainerName,
                reference.Line);
            var containingType = GetContainingTypeQualifiedName(owner);
            if (containingType == null
                || !lookups.HasCSharpFieldOrPropertyMember(
                    containingType,
                    reference.SymbolName))
            {
                continue;
            }

            reference.SymbolName =
                $"{containingType}.{reference.SymbolName}";
            reference.ReferenceKind = "reference";
        }
    }
}
