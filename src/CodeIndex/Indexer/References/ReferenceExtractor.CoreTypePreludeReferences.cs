using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitCoreTypePreludeReferences(
        in CoreTypeReferenceContext type,
        ref CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern)
    {
        AdvanceCoreCSharpMultiLineTypePattern(in type, ref pendingCSharpMultiLineTypePattern);
        EmitCoreCSharpEventReferences(in type);
        EmitCoreConstructorChainReferences(in type);
        EmitCoreCompileTimeTypeReferences(in type);

        ref readonly var line = ref type.Line;
        if (line.Language is "csharp" or "java" or "kotlin")
        {
            EmitCatchTypeReferences(
                line.Language,
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);
        }
    }

    private static void AdvanceCoreCSharpMultiLineTypePattern(
        in CoreTypeReferenceContext type,
        ref CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern)
    {
        ref readonly var line = ref type.Line;
        if (line.Language is not "csharp")
            return;

        CSharpReferenceExtractor.AdvanceMultiLineTypePatternState(
            line.PreparedLine,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall,
            type.CSharpQualifiedConstantPatternMemberLookup,
            type.CSharpUsingAliases,
            type.CSharpUsingStatics,
            type.Lookups.HasActiveSameFileCSharpTypeCandidate,
            line.References,
            line.Seen,
            line.FileId,
            ref pendingCSharpMultiLineTypePattern);
    }

    private static void EmitCoreCSharpEventReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        if (line.Language is not "csharp"
            || (line.PreparedLine.IndexOf("+=", StringComparison.Ordinal) < 0
                && line.PreparedLine.IndexOf("-=", StringComparison.Ordinal) < 0))
        {
            return;
        }

        foreach (Match match in BoundedRegex.EnumerateMatches(EventSubscriptionRegex, line.PreparedLine))
        {
            if (ReferenceLimitReached(line.References))
                break;
            var eventContainer = line.ResolveContainerForCall(match.Groups["name"].Index);
            AddReference(
                line.References,
                line.Seen,
                line.FileId,
                match,
                "subscribe",
                line.Context,
                line.LineNumber,
                eventContainer);
        }
    }

    private static void EmitCoreConstructorChainReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        switch (line.Language)
        {
            case "csharp":
                CSharpReferenceExtractor.EmitCtorChainReferences(
                    line.PreparedLine, type.Lookups.GetEnclosingTypeCandidates, type.ContainerCandidates,
                    type.StructuralLines, line.References, line.Seen, line.FileId, line.Context,
                    line.LineNumber, line.Container);
                break;
            case "java":
                JavaReferenceExtractor.EmitCtorChainReferences(
                    line.PreparedLine, type.Lookups.GetEnclosingTypeCandidates, type.Symbols,
                    type.StructuralLines, line.References, line.Seen, line.FileId, line.Context,
                    line.LineNumber, line.Container);
                break;
            case "kotlin":
                KotlinReferenceExtractor.EmitCtorDelegationReferences(
                    line.PreparedLine, type.Lookups.GetEnclosingTypeCandidates, type.Symbols,
                    type.StructuralLines, line.References, line.Seen, line.FileId, line.Context,
                    line.LineNumber, line.Container);
                break;
        }
    }

    private static void EmitCoreCompileTimeTypeReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        switch (line.Language)
        {
            case "csharp":
                EmitCoreCSharpCompileTimeTypeReferences(in type);
                break;
            case "java":
                JavaReferenceExtractor.EmitDotClassTypeLiteralReferences(
                    line.PreparedLine,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.Container);
                break;
            case "kotlin":
                KotlinReferenceExtractor.EmitClassLiteralReferences(
                    line.PreparedLine,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.Container);
                KotlinReferenceExtractor.EmitBacktickConstructorReferences(
                    line.PreparedLine,
                    type.KotlinConstructorTypeNames!,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.ResolveContainerForCall);
                break;
        }
    }

    private static void EmitCoreCSharpCompileTimeTypeReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        var hasTypeKeywordIntro = line.PreparedLine.IndexOf('(') >= 0
            && (line.PreparedLine.IndexOf("nameof", StringComparison.Ordinal) >= 0
                || line.PreparedLine.IndexOf("typeof", StringComparison.Ordinal) >= 0
                || line.PreparedLine.IndexOf("sizeof", StringComparison.Ordinal) >= 0
                || line.PreparedLine.IndexOf("default", StringComparison.Ordinal) >= 0);
        if (hasTypeKeywordIntro)
        {
            var genericParameterNames = CollectCSharpGenericParameterNamesForDeclaration(line.PreparedLine);
            foreach (Match match in BoundedRegex.EnumerateMatches(CSharpTypeKeywordIntroRegex, line.PreparedLine))
            {
                if (ReferenceLimitReached(line.References))
                    break;
                int parenIndex = match.Index + match.Length - 1;
                ExtractCSharpTypeKeywordSegments(
                    line.References, line.Seen, line.FileId, line.PreparedLine, parenIndex + 1,
                    line.Context, line.LineNumber, line.Container, line.Language, genericParameterNames);
            }
        }

        if (line.OriginalLine.IndexOf('"') >= 0)
        {
            ExtractCSharpReflectionNameLiteralReferences(
                line.References, line.Seen, line.FileId, line.PreparedLine, line.OriginalLine,
                line.Context, line.LineNumber, line.Container);
        }
    }
}
