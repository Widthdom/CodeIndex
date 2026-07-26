using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class PythonReferenceExtractor
{
    public static void EmitTypeAliasReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf('=') < 0)
            return;
        if (preparedLine.IndexOf("TypeAlias", StringComparison.Ordinal) < 0
            && !MayStartPythonTypeAliasStatement(preparedLine))
            return;

        foreach (Match match in Regex.EnumerateMatches(
                     TypeAliasRhsExpressionRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var typeGroup = match.Groups["type"];
            EmitPythonTypeExpressionReferences(
                typeGroup,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container,
                resolveContainerForReference: null,
                isIgnoredName);
        }
    }

    private static bool MayStartPythonTypeAliasStatement(string preparedLine)
    {
        var index = 0;
        while (index < preparedLine.Length && char.IsWhiteSpace(preparedLine[index]))
            index++;

        if (index + "type".Length >= preparedLine.Length)
            return false;

        if (!preparedLine.AsSpan(index).StartsWith("type", StringComparison.Ordinal))
            return false;

        return char.IsWhiteSpace(preparedLine[index + "type".Length]);
    }

    public static void EmitNewTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf("NewType", StringComparison.Ordinal) < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(
                     NewTypeUnderlyingTypeRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var name = match.Groups["name"].Value;
            if (isIgnoredName(name))
                continue;

            ReferenceExtractor.AddTypeReferenceSegments(
                references,
                seen,
                fileId,
                name,
                match.Groups["name"].Index,
                context,
                lineNumber,
                container,
                "python");
        }
    }

    public static void EmitTypeVarBoundReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if ((preparedLine.IndexOf("TypeVar", StringComparison.Ordinal) < 0
                && preparedLine.IndexOf("ParamSpec", StringComparison.Ordinal) < 0)
            || preparedLine.IndexOf("bound", StringComparison.Ordinal) < 0)
        {
            return;
        }

        foreach (Match match in Regex.EnumerateMatches(
                     TypeVarBoundTypeRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            EmitPythonTypeExpressionReferences(
                match.Groups["type"],
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container,
                resolveContainerForReference: null,
                isIgnoredName);
        }
    }

    public static void EmitTypeVarConstraintReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf("TypeVar", StringComparison.Ordinal) < 0
            && preparedLine.IndexOf("ParamSpec", StringComparison.Ordinal) < 0)
        {
            return;
        }
        if (preparedLine.IndexOf(',') < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(
                     TypeVarConstraintTypesRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var typesGroup = match.Groups["types"];
            EmitPythonTypeExpressionReferences(
                typesGroup,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container,
                resolveContainerForReference: null,
                isIgnoredName);
        }
    }

    public static void EmitGetTypeHintsReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf("get_type_hints", StringComparison.Ordinal) < 0)
            return;

        if (preparedLine.IndexOf("typing", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(
                         QualifiedGetTypeHintsTargetRegex,
                         preparedLine))
            {
                if (ReferenceExtractor.ReferenceLimitReached(references))
                    break;

                var name = match.Groups["name"].Value;
                if (isIgnoredName(name))
                    continue;

                ReferenceExtractor.AddTypeReferenceSegments(
                    references,
                    seen,
                    fileId,
                    name,
                    match.Groups["name"].Index,
                    context,
                    lineNumber,
                    container,
                    "python");
            }
        }

        foreach (Match match in Regex.EnumerateMatches(
                     GetTypeHintsTargetRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var name = match.Groups["name"].Value;
            if (isIgnoredName(name))
                continue;

            ReferenceExtractor.AddTypeReferenceSegments(
                references,
                seen,
                fileId,
                name,
                match.Groups["name"].Index,
                context,
                lineNumber,
                container,
                "python");
        }
    }

    public static void EmitDynamicImportReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf('(') < 0)
            return;

        if (preparedLine.IndexOf("importlib", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(
                         ImportlibDynamicImportRegex,
                         preparedLine))
            {
                if (ReferenceExtractor.ReferenceLimitReached(references))
                    break;

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    "importlib",
                    match.Index,
                    "call",
                    context,
                    lineNumber,
                    container,
                    "python");

                var literalMatch = ImportlibDynamicImportLiteralRegex.Match(originalLine, match.Index);
                if (!literalMatch.Success || literalMatch.Index != match.Index)
                    continue;

                var moduleGroup = literalMatch.Groups["module"];
                if (moduleGroup.Success && moduleGroup.Value.Length > 0)
                {
                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        moduleGroup.Value,
                        moduleGroup.Index,
                        "import",
                        context,
                        lineNumber,
                        container,
                        "python");
                }
            }
        }

        if (preparedLine.IndexOf("__import__", StringComparison.Ordinal) < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(
                     BuiltinDynamicImportRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var literalMatch = BuiltinDynamicImportLiteralRegex.Match(originalLine, match.Index);
            if (!literalMatch.Success || literalMatch.Index != match.Index)
                continue;

            var moduleGroup = literalMatch.Groups["module"];
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                moduleGroup.Value,
                moduleGroup.Index,
                "import",
                context,
                lineNumber,
                container,
                "python");
        }
    }
}
