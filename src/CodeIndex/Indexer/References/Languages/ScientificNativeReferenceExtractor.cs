using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class ScientificNativeReferenceExtractor
{
    private const int MaxDependenciesPerDeclaration = 64;

    private static readonly HashSet<string> SupportedLanguages =
        new(StringComparer.Ordinal) { "ada", "cython", "d", "julia", "matlab", "nim", "objc" };

    private static readonly Regex NimFromImportRegex = new(
        @"^\s*from\s+(?<name>[A-Za-z_][\w./]*)\s+import\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NimImportListRegex = new(
        @"^\s*(?:import|include)\s+(?<names>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NimBaseTypeRegex = new(
        @"\bobject\s+of\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NimAnnotatedTypeRegex = new(
        @":\s*(?:(?:var|lent|sink)\s+)?(?<name>[A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MatlabImportListRegex = new(
        @"^\s*import\s+(?<names>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MatlabBaseTypeListRegex = new(
        @"^\s*classdef\b[^<\r\n]*<\s*(?<names>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JuliaImportListRegex = new(
        @"^\s*(?:using|import)\s+(?<names>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JuliaTypeRegex = new(
        @"(?:<:|::)\s*(?<name>[A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JuliaMacroCallRegex = new(
        @"(?<![\w@])@(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DImportListRegex = new(
        @"^\s*(?:(?:public|static)\s+)*import\s+(?<names>[^;\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DBaseTypeListRegex = new(
        @"^\s*(?:class|interface)\s+[A-Za-z_]\w*(?:\s*\([^)]*\))?\s*:\s*(?<names>[^{\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CythonFromImportRegex = new(
        @"^\s*from\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s+cimport\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CythonImportListRegex = new(
        @"^\s*(?:cimport|import)\s+(?<names>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CythonBaseTypeListRegex = new(
        @"^\s*cdef\s+class\s+[A-Za-z_]\w*\s*\(\s*(?<names>[^)\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AdaImportListRegex = new(
        @"^\s*(?:(?:limited|private)\s+)*with\s+(?<names>[^;\r\n]+)",
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
                EmitMatch(NimFromImportRegex, "import");
                EmitNameList(NimImportListRegex, "import", ',');
                EmitMatches(NimBaseTypeRegex, "type_reference");
                EmitMatches(NimAnnotatedTypeRegex, "type_reference");
                break;
            case "matlab":
                EmitNameList(MatlabImportListRegex, "import", ',', splitOnWhitespace: true);
                EmitNameList(MatlabBaseTypeListRegex, "type_reference", '&');
                break;
            case "julia":
                EmitNameList(JuliaImportListRegex, "import", ',', stopAtColon: true);
                EmitMatches(JuliaTypeRegex, "type_reference");
                foreach (Match match in JuliaMacroCallRegex.Matches(preparedLine))
                {
                    var group = match.Groups["name"];
                    addCallLikeReference(group.Value, group.Index);
                }
                break;
            case "d":
                EmitNameList(DImportListRegex, "import", ',', stopAtColon: true, stripLeadingAlias: true);
                EmitNameList(DBaseTypeListRegex, "type_reference", ',');
                break;
            case "cython":
                EmitMatch(CythonFromImportRegex, "import");
                EmitNameList(CythonImportListRegex, "import", ',');
                EmitNameList(CythonBaseTypeListRegex, "type_reference", ',');
                break;
            case "ada":
                EmitNameList(AdaImportListRegex, "import", ',');
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

        void EmitNameList(
            Regex regex,
            string referenceKind,
            char separator,
            bool splitOnWhitespace = false,
            bool stopAtColon = false,
            bool stripLeadingAlias = false)
        {
            var match = regex.Match(preparedLine);
            if (!match.Success)
                return;

            var group = match.Groups["names"];
            if (!group.Success || group.Length == 0)
                return;

            var names = group.Value;
            var namesEnd = names.Length;
            if (stopAtColon)
            {
                var colonIndex = names.IndexOf(':');
                if (colonIndex >= 0)
                    namesEnd = colonIndex;
            }

            var dependencyCount = 0;
            var segmentStart = 0;
            for (var index = 0; index <= namesEnd && dependencyCount < MaxDependenciesPerDeclaration; index++)
            {
                var atEnd = index == namesEnd;
                var isSeparator = !atEnd
                    && (names[index] == separator || (splitOnWhitespace && char.IsWhiteSpace(names[index])));
                if (!atEnd && !isSeparator)
                    continue;

                if (TryEmitDependencySegment(
                    names,
                    segmentStart,
                    index,
                    group.Index,
                    referenceKind,
                    stripLeadingAlias))
                {
                    dependencyCount++;
                }

                segmentStart = index + 1;
                while (segmentStart < namesEnd
                    && (names[segmentStart] == separator
                        || (splitOnWhitespace && char.IsWhiteSpace(names[segmentStart]))))
                {
                    segmentStart++;
                    index++;
                }
            }
        }

        bool TryEmitDependencySegment(
            string names,
            int segmentStart,
            int segmentEnd,
            int absoluteOffset,
            string referenceKind,
            bool stripLeadingAlias)
        {
            while (segmentStart < segmentEnd && char.IsWhiteSpace(names[segmentStart]))
                segmentStart++;
            while (segmentEnd > segmentStart && char.IsWhiteSpace(names[segmentEnd - 1]))
                segmentEnd--;
            if (segmentStart >= segmentEnd)
                return false;

            if (stripLeadingAlias)
            {
                var equalsIndex = names.LastIndexOf('=', segmentEnd - 1, segmentEnd - segmentStart);
                if (equalsIndex >= segmentStart)
                {
                    segmentStart = equalsIndex + 1;
                    while (segmentStart < segmentEnd && char.IsWhiteSpace(names[segmentStart]))
                        segmentStart++;
                }
            }

            for (var index = segmentStart; index + 3 < segmentEnd; index++)
            {
                if (!char.IsWhiteSpace(names[index])
                    || !names.AsSpan(index + 1, 2).Equals("as", StringComparison.OrdinalIgnoreCase)
                    || !char.IsWhiteSpace(names[index + 3]))
                {
                    continue;
                }

                segmentEnd = index;
                break;
            }

            while (segmentEnd > segmentStart && char.IsWhiteSpace(names[segmentEnd - 1]))
                segmentEnd--;

            var nameEnd = segmentStart;
            while (nameEnd < segmentEnd && IsDependencyNameChar(names[nameEnd]))
                nameEnd++;
            while (nameEnd > segmentStart && names[nameEnd - 1] is '.' or '/')
                nameEnd--;

            var firstIdentifierIndex = segmentStart;
            while (firstIdentifierIndex < nameEnd && names[firstIdentifierIndex] == '.')
                firstIdentifierIndex++;
            if (firstIdentifierIndex >= nameEnd
                || !(char.IsLetter(names[firstIdentifierIndex]) || names[firstIdentifierIndex] == '_'))
            {
                return false;
            }

            EmitName(
                names[segmentStart..nameEnd],
                absoluteOffset + segmentStart,
                referenceKind);
            return true;
        }

        void EmitGroup(Group group, string referenceKind)
        {
            if (!group.Success || group.Length == 0)
                return;

            EmitName(group.Value, group.Index, referenceKind);
        }

        void EmitName(string name, int index, string referenceKind)
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                index,
                referenceKind,
                context,
                lineNumber,
                resolveContainerForColumn(index),
                language);
        }
    }

    private static bool IsDependencyNameChar(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '.' or '/' or '*';
}
