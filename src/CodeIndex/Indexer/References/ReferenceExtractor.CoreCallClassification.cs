using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool TryAddCoreCallLikeReference(
        in CoreCallReferenceContext call,
        string name,
        int callIndex,
        bool isScientificNativeLanguage,
        string? targetQualifier = null)
    {
        ref readonly var line = ref call.Line;
        var normalizedName = NormalizeCoreCallName(
            line.Language,
            name);
        if (ShouldSuppressCoreCallCandidate(
                in call,
                name,
                normalizedName,
                callIndex))
        {
            return false;
        }

        var callContainer = line.ResolveContainerForCall(callIndex);
        if (TryHandleCSharpValueReceiverCall(
                in call,
                name,
                normalizedName,
                callIndex,
                callContainer))
        {
            return false;
        }

        if (TryAddCoreInstantiationReference(
                in call,
                name,
                normalizedName,
                callIndex,
                callContainer,
                targetQualifier))
        {
            return true;
        }

        if (IsIgnoredCallName(line.Language, name)
            && !(line.Language == "scala"
                && string.Equals(name, "foreach", StringComparison.Ordinal)))
        {
            return false;
        }

        if (TryAddCoreMetadataReference(
                in call,
                name,
                normalizedName,
                callIndex,
                callContainer))
        {
            return true;
        }

        if (TryAddSpecialCoreCallReference(
                in call,
                name,
                normalizedName,
                callIndex,
                callContainer))
        {
            return true;
        }

        AddCoreClassifiedReference(
            in call,
            normalizedName,
            callIndex,
            "call",
            callContainer,
            isScientificNativeLanguage ? line.Language : null,
            targetQualifier,
            name.Length);
        return true;
    }

    private static string NormalizeCoreCallName(
        string language,
        string name) =>
        language == "fsharp"
            && FSharpReferenceExtractor.IsOperatorCallName(name)
                ? $"operator {name}"
                : language == "rust"
                    ? RustReferenceExtractor.NormalizeIdentifier(name)
                    : NormalizeAtPrefixedIdentifier(name);

    private static bool TryGetKnownPythonTypeCall(
        in CoreCallReferenceContext call,
        string candidate,
        int callIndex,
        out string canonicalName)
    {
        ref readonly var line = ref call.Line;
        canonicalName = candidate;
        var separator = candidate.LastIndexOf('.');
        var leaf = separator >= 0 ? candidate[(separator + 1)..] : candidate;
        if (leaf.Length == 0 || !char.IsUpper(leaf, 0))
            return false;

        if (call.Lookups.HasSameFilePythonClass(candidate, leaf))
            return true;

        return PythonImportBindingResolver.TryResolveImportedTypeCall(
            candidate,
            line.PreparedLine,
            callIndex,
            call.Lookups.GetPythonImportedTypeCallLookup(),
            out canonicalName);
    }
}
