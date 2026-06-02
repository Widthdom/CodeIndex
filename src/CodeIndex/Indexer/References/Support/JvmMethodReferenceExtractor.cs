using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class JvmMethodReferenceExtractor
{
    private const string FunctionalIdentifierPattern = @"@?[_\p{L}\$][\w$]*";
    private const string JvmMethodReferenceIdentifierPattern = @"(?:@?[_\p{L}\$][\w$]*|`[^`\r\n]+`)";
    private const string JvmMethodReferenceTypeArgumentListPattern = @"(?:\s*<[^<>\r\n]*(?:<[^<>\r\n]*>[^<>\r\n]*)*>)?";
    private const string JvmMethodReferenceOwnerSegmentPattern = JvmMethodReferenceIdentifierPattern + JvmMethodReferenceTypeArgumentListPattern + @"(?:\s*\[\s*\])*";

    private static readonly Regex MethodReferenceRegex = new(
        $@"(?<![\w$])(?:(?<owner>(?:this|super|{JvmMethodReferenceOwnerSegmentPattern}(?:\.{JvmMethodReferenceOwnerSegmentPattern})*))\s*)?::\s*(?<name>{JvmMethodReferenceIdentifierPattern}|new)(?=\s*(?:[;,)\]]|$))",
        RegexOptions.Compiled);

    public static void EmitMethodReferenceReferences(
        string language,
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in MethodReferenceRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            var container = resolveContainerForColumn(nameGroup.Index);
            var ownerGroup = match.Groups["owner"];

            if (string.Equals(nameGroup.Value, "class", StringComparison.Ordinal))
                continue;

            if (string.Equals(nameGroup.Value, "new", StringComparison.Ordinal))
            {
                if (!ownerGroup.Success || ownerGroup.Value.Length == 0)
                    continue;
                if (ownerGroup.Value is "this" or "super")
                    continue;

                var ownerName = StripJvmOwnerTypeDecorations(ownerGroup.Value);
                if (ownerName.Length == 0)
                    continue;

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    ownerName,
                    ownerGroup.Index,
                    "instantiate",
                    context,
                    lineNumber,
                    container);
                continue;
            }

            EmitOwnerTypeReference(language, ownerGroup, references, seen, fileId, context, lineNumber, container);
            AddChainReference(references, seen, fileId, language, nameGroup.Value, nameGroup.Index, context, lineNumber, container);
        }
    }

    private static void EmitOwnerTypeReference(
        string language,
        Group ownerGroup,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (language is not ("java" or "kotlin"))
            return;
        if (!ownerGroup.Success || ownerGroup.Value.Length == 0)
            return;
        if (ownerGroup.Value is "this" or "super")
            return;

        var ownerName = StripJvmOwnerTypeDecorations(ownerGroup.Value);
        var leafStartInOwner = FindLastUnquotedDot(ownerName) + 1;
        if (leafStartInOwner >= ownerName.Length)
            return;

        var leaf = StripBacktickIdentifier(ownerName[leafStartInOwner..]);
        if (!IsLikelyJvmTypeName(leaf))
            return;

        ReferenceExtractor.AddTypeReferenceSegment(
            references,
            seen,
            fileId,
            leaf,
            ownerGroup.Index + leafStartInOwner,
            context,
            lineNumber,
            container,
            language);
    }

    private static string StripJvmOwnerTypeDecorations(string name)
    {
        var withoutGenerics = StripJvmGenericArguments(name);
        return StripJvmArraySuffixes(withoutGenerics);
    }

    private static string StripJvmGenericArguments(string name)
    {
        StringBuilder? builder = null;
        var genericDepth = 0;
        var inBacktickIdentifier = false;

        foreach (var ch in name)
        {
            if (ch == '`')
            {
                if (genericDepth == 0)
                    (builder ??= new StringBuilder(name.Length)).Append(ch);
                inBacktickIdentifier = !inBacktickIdentifier;
                continue;
            }

            if (!inBacktickIdentifier)
            {
                if (ch == '<')
                {
                    genericDepth++;
                    builder ??= new StringBuilder(name.Length);
                    continue;
                }

                if (ch == '>' && genericDepth > 0)
                {
                    genericDepth--;
                    continue;
                }
            }

            if (genericDepth == 0)
                (builder ??= new StringBuilder(name.Length)).Append(ch);
        }

        return builder?.ToString() ?? name;
    }

    private static string StripJvmArraySuffixes(string name)
    {
        var end = name.Length;
        while (true)
        {
            var closeIndex = SkipWhitespaceBackward(name, end - 1);
            if (closeIndex < 0 || name[closeIndex] != ']')
                break;

            var openIndex = SkipWhitespaceBackward(name, closeIndex - 1);
            if (openIndex < 0 || name[openIndex] != '[')
                break;

            end = openIndex;
            while (end > 0 && char.IsWhiteSpace(name[end - 1]))
                end--;
        }

        return end == name.Length ? name : name[..end];
    }

    private static int SkipWhitespaceBackward(string text, int index)
    {
        while (index >= 0 && char.IsWhiteSpace(text[index]))
            index--;
        return index;
    }

    private static int FindLastUnquotedDot(string text)
    {
        var last = -1;
        var inBacktickIdentifier = false;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '`')
            {
                inBacktickIdentifier = !inBacktickIdentifier;
                continue;
            }

            if (!inBacktickIdentifier && ch == '.')
                last = index;
        }

        return last;
    }

    private static string StripBacktickIdentifier(string name)
    {
        if (name.Length >= 2 && name[0] == '`' && name[^1] == '`')
            return name[1..^1];
        return name;
    }

    private static bool IsLikelyJvmTypeName(string name)
    {
        if (name.Length == 0)
            return false;

        var index = name[0] == '@' ? 1 : 0;
        return index < name.Length && char.IsUpper(name[index]);
    }

    private static void AddChainReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string language,
        string name,
        int column,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        name = NormalizeMethodReferenceName(language, name);
        var dedupeKey = ReferenceExtractor.BuildReferenceDedupeKey(fileId, language, lineNumber, column, "call", name, container);
        if (!seen.Add(dedupeKey))
            return;

        references.Add(new ReferenceRecord
        {
            FileId = fileId,
            SymbolName = name,
            ReferenceKind = "call",
            Line = lineNumber,
            Column = column,
            Context = context,
            ContainerKind = container?.Kind,
            ContainerName = container?.Name,
        });
    }

    private static string NormalizeMethodReferenceName(string language, string name)
        => language == "kotlin" ? StripBacktickIdentifier(name) : name;
}
