using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool TryAddCoreInstantiationReference(
        in CoreCallReferenceContext call,
        string name,
        string normalizedName,
        int callIndex,
        SymbolRecord? callContainer,
        string? targetQualifier)
    {
        ref readonly var line = ref call.Line;
        if (IsConstructorCallName(
                line.Language,
                line.PreparedLine,
                callIndex))
        {
            AddCoreClassifiedReference(
                in call,
                normalizedName,
                callIndex,
                "instantiate",
                callContainer,
                line.Language,
                targetQualifier,
                name.Length);
            return true;
        }

        if (line.Language == "rust"
            && RustReferenceExtractor.IsLikelyInstantiationCallName(
                name,
                normalizedName,
                line.PreparedLine,
                callIndex))
        {
            AddCoreClassifiedReference(
                in call,
                normalizedName,
                callIndex,
                "instantiate",
                callContainer,
                line.Language,
                targetQualifier: null,
                name.Length);
            return true;
        }

        if (line.Language == "python"
            && TryGetKnownPythonTypeCall(
                in call,
                normalizedName,
                callIndex,
                out var pythonTypeName))
        {
            AddCoreClassifiedReference(
                in call,
                pythonTypeName,
                callIndex,
                "instantiate",
                callContainer,
                line.Language,
                targetQualifier: null,
                name.Length);
            return true;
        }

        return false;
    }

    private static bool TryAddCoreMetadataReference(
        in CoreCallReferenceContext call,
        string name,
        string normalizedName,
        int callIndex,
        SymbolRecord? callContainer)
    {
        ref readonly var line = ref call.Line;
        var insideCSharpAttributeRange = call.CSharpAttributeRanges != null
            && IsInsideCSharpAttributeRange(
                call.CSharpAttributeRanges,
                callIndex);
        var metadataKind = TryClassifyMetadataReference(
            line.Language,
            line.PreparedLine,
            callIndex,
            insideCSharpAttributeRange);
        if (metadataKind == null)
            return false;

        AddCoreClassifiedReference(
            in call,
            normalizedName,
            callIndex,
            metadataKind,
            callContainer,
            line.Language,
            targetQualifier: null,
            name.Length);
        if (line.Language == "csharp"
            && metadataKind == "attribute"
            && CSharpReferenceExtractor.TryGetCallerInfoAttributeTypeName(
                name,
                line.PreparedLine,
                callIndex) is { } callerInfoAttributeTypeName)
        {
            AddCoreClassifiedReference(
                in call,
                callerInfoAttributeTypeName,
                callIndex,
                "type_reference",
                callContainer,
                line.Language,
                targetQualifier: null,
                name.Length);
        }

        return true;
    }

    private static bool TryAddSpecialCoreCallReference(
        in CoreCallReferenceContext call,
        string name,
        string normalizedName,
        int callIndex,
        SymbolRecord? callContainer)
    {
        ref readonly var line = ref call.Line;
        if (line.Language == "kotlin"
            && KotlinReferenceExtractor.IsConstructorCallName(
                normalizedName,
                call.KotlinConstructorTypeNames!))
        {
            AddCoreClassifiedReference(
                in call,
                normalizedName,
                callIndex,
                "instantiate",
                callContainer,
                referenceLanguage: null,
                targetQualifier: null,
                name.Length);
            return true;
        }

        if (line.Language is "javascript" or "typescript"
            && SymbolExtractor.IsJavaScriptTypeScriptReactHookName(
                normalizedName))
        {
            AddCoreClassifiedReference(
                in call,
                normalizedName,
                callIndex,
                "consumes_hook",
                callContainer,
                referenceLanguage: null,
                targetQualifier: null,
                name.Length);
            return true;
        }

        return false;
    }

    private static void AddCoreClassifiedReference(
        in CoreCallReferenceContext call,
        string symbolName,
        int callIndex,
        string referenceKind,
        SymbolRecord? callContainer,
        string? referenceLanguage,
        string? targetQualifier,
        int sourceLength)
    {
        ref readonly var line = ref call.Line;
        AddReference(
            line.References,
            line.Seen,
            line.FileId,
            symbolName,
            callIndex,
            referenceKind,
            line.Context,
            line.LineNumber,
            callContainer,
            referenceLanguage,
            targetQualifier,
            sourceLength);
    }
}
