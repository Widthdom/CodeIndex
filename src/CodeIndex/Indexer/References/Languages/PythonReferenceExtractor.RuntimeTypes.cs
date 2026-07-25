using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class PythonReferenceExtractor
{
    public static void EmitRaiseReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (!StartsWithPythonKeywordStatement(preparedLine, "raise"))
            return;

        foreach (Match match in BareRaiseTypeRegex.Matches(preparedLine))
        {
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

    public static void EmitExceptReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (!StartsWithPythonKeywordStatement(preparedLine, "except"))
            return;

        if (preparedLine.IndexOf('(') >= 0)
        {
            foreach (Match match in ExceptTupleTypeRegex.Matches(preparedLine))
            {
                var typesGroup = match.Groups["types"];
                foreach (Match typeMatch in TypeNameRegex.Matches(typesGroup.Value))
                {
                    var name = typeMatch.Groups["name"].Value;
                    if (isIgnoredName(name))
                        continue;

                    ReferenceExtractor.AddTypeReferenceSegments(
                        references,
                        seen,
                        fileId,
                        name,
                        typesGroup.Index + typeMatch.Groups["name"].Index,
                        context,
                        lineNumber,
                        container,
                        "python");
                }
            }
        }

        foreach (Match match in ExceptTypeRegex.Matches(preparedLine))
        {
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

    public static void EmitIsInstanceReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf("isinstance", StringComparison.Ordinal) < 0)
            return;

        if (MayContainPythonTupleArgument(preparedLine))
        {
            foreach (Match match in IsInstanceTupleTypeRegex.Matches(preparedLine))
            {
                var typesGroup = match.Groups["types"];
                foreach (Match typeMatch in TypeNameRegex.Matches(typesGroup.Value))
                {
                    var name = typeMatch.Groups["name"].Value;
                    if (isIgnoredName(name))
                        continue;

                    ReferenceExtractor.AddTypeReferenceSegments(
                        references,
                        seen,
                        fileId,
                        name,
                        typesGroup.Index + typeMatch.Groups["name"].Index,
                        context,
                        lineNumber,
                        container,
                        "python");
                }
            }
        }

        foreach (Match match in IsInstanceTypeRegex.Matches(preparedLine))
        {
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

    public static void EmitIsSubclassReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf("issubclass", StringComparison.Ordinal) < 0)
            return;

        if (MayContainPythonTupleArgument(preparedLine))
        {
            foreach (Match match in IsSubclassTupleTypeRegex.Matches(preparedLine))
            {
                var typesGroup = match.Groups["types"];
                foreach (Match typeMatch in TypeNameRegex.Matches(typesGroup.Value))
                {
                    var name = typeMatch.Groups["name"].Value;
                    if (isIgnoredName(name))
                        continue;

                    ReferenceExtractor.AddTypeReferenceSegments(
                        references,
                        seen,
                        fileId,
                        name,
                        typesGroup.Index + typeMatch.Groups["name"].Index,
                        context,
                        lineNumber,
                        container,
                        "python");
                }
            }
        }

        foreach (Match match in IsSubclassTypeRegex.Matches(preparedLine))
        {
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

    public static void EmitCastReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf("cast", StringComparison.Ordinal) < 0)
            return;

        if (preparedLine.IndexOf("typing", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in QualifiedCastTypeRegex.Matches(preparedLine))
            {
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

        foreach (Match match in CastTypeRegex.Matches(preparedLine))
        {
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

    private static bool MayContainPythonTupleArgument(string preparedLine)
    {
        var commaIndex = preparedLine.IndexOf(',');
        while (commaIndex >= 0)
        {
            var index = commaIndex + 1;
            while (index < preparedLine.Length && char.IsWhiteSpace(preparedLine[index]))
                index++;

            if (index < preparedLine.Length && preparedLine[index] == '(')
                return true;

            commaIndex = preparedLine.IndexOf(',', commaIndex + 1);
        }

        return false;
    }

    private static bool StartsWithPythonKeywordStatement(string preparedLine, string keyword)
    {
        var index = SkipPythonWhitespace(preparedLine, 0);
        return StartsWithPythonKeywordAt(preparedLine, index, keyword);
    }

    private static bool StartsWithPythonDefStatement(string preparedLine)
    {
        var index = SkipPythonWhitespace(preparedLine, 0);
        if (StartsWithPythonKeywordAt(preparedLine, index, "async"))
            index = SkipPythonWhitespace(preparedLine, index + "async".Length);

        return StartsWithPythonKeywordAt(preparedLine, index, "def");
    }

    private static bool StartsWithPythonKeywordAt(string preparedLine, int index, string keyword)
    {
        if (!preparedLine.AsSpan(index).StartsWith(keyword, StringComparison.Ordinal))
            return false;

        var after = index + keyword.Length;
        return after >= preparedLine.Length || !IsPythonIdentifierContinue(preparedLine[after]);
    }

    private static int SkipPythonWhitespace(string preparedLine, int index)
    {
        while (index < preparedLine.Length && char.IsWhiteSpace(preparedLine[index]))
            index++;
        return index;
    }

    private static bool IsPythonIdentifierContinue(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_';

    public static void EmitAssertTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf("assert_type", StringComparison.Ordinal) < 0)
            return;

        if (preparedLine.IndexOf("typing", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in QualifiedAssertTypeRegex.Matches(preparedLine))
            {
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

        foreach (Match match in AssertTypeRegex.Matches(preparedLine))
        {
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

}
