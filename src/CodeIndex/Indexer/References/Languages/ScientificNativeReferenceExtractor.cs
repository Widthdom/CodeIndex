using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class ScientificNativeReferenceExtractor
{
    private static readonly HashSet<string> SupportedLanguages =
        new(StringComparer.Ordinal) { "ada", "cython", "d", "julia", "matlab", "nim", "objc" };

    private static readonly Regex NimImportRegex = new(
        @"^\s*(?:from\s+(?<name>[A-Za-z_][\w./]*)\s+import\b|(?:import|include)\s+(?<name>[A-Za-z_][\w./]*))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NimBaseTypeRegex = new(
        @"\bobject\s+of\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NimAnnotatedTypeRegex = new(
        @":\s*(?:(?:var|lent|sink)\s+)?(?<name>[A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MatlabImportRegex = new(
        @"^\s*import\s+(?<name>[A-Za-z]\w*(?:\.[A-Za-z*]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MatlabBaseTypeRegex = new(
        @"^\s*classdef\b[^<\r\n]*<\s*(?<name>[A-Za-z]\w*(?:\.[A-Za-z]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JuliaImportRegex = new(
        @"^\s*(?:using|import)\s+(?<name>\.*[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JuliaTypeRegex = new(
        @"(?:<:|::)\s*(?<name>[A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JuliaMacroCallRegex = new(
        @"(?<![\w@])@(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DImportRegex = new(
        @"^\s*(?:(?:public|static)\s+)*import\s+(?:(?:[A-Za-z_]\w*)\s*=\s*)?(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DBaseTypeRegex = new(
        @"^\s*(?:class|interface)\s+[A-Za-z_]\w*(?:\s*\([^)]*\))?\s*:\s*(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CythonImportRegex = new(
        @"^\s*(?:from\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s+cimport\b|(?:cimport|import)\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CythonBaseTypeRegex = new(
        @"^\s*cdef\s+class\s+[A-Za-z_]\w*\s*\(\s*(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AdaImportRegex = new(
        @"^\s*(?:(?:limited|private)\s+)*with\s+(?<name>[A-Za-z]\w*(?:\.[A-Za-z]\w*)*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AdaDerivedTypeRegex = new(
        @"^\s*type\s+[A-Za-z]\w*\s+is\s+new\s+(?<name>[A-Za-z]\w*(?:\.[A-Za-z]\w*)*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AdaBareCallRegex = new(
        @"^\s*(?!(?:end|null|return|exit|raise|goto)\b)(?<name>[A-Za-z]\w*(?:\.[A-Za-z]\w*)*)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectiveCImportRegex = new(
        """^\s*#\s*(?:import|include)\s*[<"](?<name>[^>"]+)[>"]""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool Supports(string language) => SupportedLanguages.Contains(language);

    internal static void EmitReferences(
        string language,
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        Action<string, int> addCallLikeReference)
    {
        switch (language)
        {
            case "nim":
                EmitMatch(NimImportRegex, "import");
                EmitMatches(NimBaseTypeRegex, "type_reference");
                EmitMatches(NimAnnotatedTypeRegex, "type_reference");
                break;
            case "matlab":
                EmitMatch(MatlabImportRegex, "import");
                EmitMatches(MatlabBaseTypeRegex, "type_reference");
                break;
            case "julia":
                EmitMatch(JuliaImportRegex, "import");
                EmitMatches(JuliaTypeRegex, "type_reference");
                foreach (Match match in JuliaMacroCallRegex.Matches(preparedLine))
                {
                    var group = match.Groups["name"];
                    addCallLikeReference(group.Value, group.Index);
                }
                break;
            case "d":
                EmitMatch(DImportRegex, "import");
                EmitMatches(DBaseTypeRegex, "type_reference");
                break;
            case "cython":
                EmitMatch(CythonImportRegex, "import");
                EmitMatches(CythonBaseTypeRegex, "type_reference");
                break;
            case "ada":
                EmitMatch(AdaImportRegex, "import");
                EmitMatches(AdaDerivedTypeRegex, "type_reference");
                var bareCall = AdaBareCallRegex.Match(preparedLine);
                if (bareCall.Success)
                {
                    var group = bareCall.Groups["name"];
                    addCallLikeReference(group.Value, group.Index);
                }
                break;
            case "objc":
                EmitMatch(ObjectiveCImportRegex, "import");
                break;
        }

        void EmitMatch(Regex regex, string referenceKind)
        {
            var match = regex.Match(preparedLine);
            if (match.Success)
                EmitGroup(match.Groups["name"], referenceKind);
        }

        void EmitMatches(Regex regex, string referenceKind)
        {
            foreach (Match match in regex.Matches(preparedLine))
                EmitGroup(match.Groups["name"], referenceKind);
        }

        void EmitGroup(Group group, string referenceKind)
        {
            if (!group.Success || group.Length == 0)
                return;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                group.Value,
                group.Index,
                referenceKind,
                context,
                lineNumber,
                resolveContainerForColumn(group.Index),
                language);
        }
    }
}
