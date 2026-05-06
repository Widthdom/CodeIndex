using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class PerlReferenceExtractor
{
    private static readonly Regex ModuleReferenceRegex = new(
        @"^\s*(?:use|require)\s+(?<name>[\p{L}_][\w:]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BaseModuleReferenceRegex = new(
        @"^\s*use\s+(?:base|parent)\s+(?<args>.+?);?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedModuleRegex = new(
        @"['""](?<name>[\p{L}_][\w:]*)['""]|qw\s*\((?<names>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ArrowCallRegex = new(
        @"(?<receiver>(?:[\p{L}_][\w:]*)|\$[\p{L}_]\w*)\s*->\s*(?<name>[\p{L}_]\w*)\s*(?:\(|\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void EmitAdditionalReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Action<string, int> addCallLikeReference)
    {
        EmitModuleReference(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForCall);
        EmitBaseModuleReferences(originalLine, references, seen, fileId, context, lineNumber, resolveContainerForCall);
        EmitArrowCallReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForCall, addCallLikeReference);
    }

    private static void EmitModuleReference(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var match = ModuleReferenceRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var nameGroup = match.Groups["name"];
        if (string.Equals(nameGroup.Value, "strict", StringComparison.Ordinal)
            || string.Equals(nameGroup.Value, "warnings", StringComparison.Ordinal)
            || string.Equals(nameGroup.Value, "constant", StringComparison.Ordinal)
            || string.Equals(nameGroup.Value, "base", StringComparison.Ordinal)
            || string.Equals(nameGroup.Value, "parent", StringComparison.Ordinal))
        {
            return;
        }

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            nameGroup.Value,
            nameGroup.Index,
            "reference",
            context,
            lineNumber,
            resolveContainerForCall(nameGroup.Index));
    }

    private static void EmitBaseModuleReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var match = BaseModuleReferenceRegex.Match(originalLine);
        if (!match.Success)
            return;

        var args = match.Groups["args"].Value;
        var argsStart = match.Groups["args"].Index;
        foreach (Match moduleMatch in QuotedModuleRegex.Matches(args))
        {
            if (moduleMatch.Groups["name"].Success)
            {
                AddBaseModuleReference(moduleMatch.Groups["name"].Value, argsStart + moduleMatch.Groups["name"].Index, references, seen, fileId, context, lineNumber, resolveContainerForCall);
                continue;
            }

            if (!moduleMatch.Groups["names"].Success)
                continue;

            var names = moduleMatch.Groups["names"].Value;
            var namesStart = argsStart + moduleMatch.Groups["names"].Index;
            foreach (Match nameMatch in Regex.Matches(names, @"[\p{L}_][\w:]*", RegexOptions.CultureInvariant))
                AddBaseModuleReference(nameMatch.Value, namesStart + nameMatch.Index, references, seen, fileId, context, lineNumber, resolveContainerForCall);
        }
    }

    private static void AddBaseModuleReference(
        string name,
        int column,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            name,
            column,
            "type_reference",
            context,
            lineNumber,
            resolveContainerForCall(column));
    }

    private static void EmitArrowCallReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Action<string, int> addCallLikeReference)
    {
        foreach (Match match in ArrowCallRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            addCallLikeReference(nameGroup.Value, nameGroup.Index);

            var receiverGroup = match.Groups["receiver"];
            if (!string.Equals(nameGroup.Value, "new", StringComparison.Ordinal) || receiverGroup.Value.StartsWith('$'))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                receiverGroup.Value,
                receiverGroup.Index,
                "instantiate",
                context,
                lineNumber,
                resolveContainerForCall(receiverGroup.Index));
        }
    }
}
