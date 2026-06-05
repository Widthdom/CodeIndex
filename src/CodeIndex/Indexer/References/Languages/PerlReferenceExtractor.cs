using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class PerlReferenceExtractor
{
    private static readonly Regex ModuleReferenceRegex = new(
        @"^\s*(?:use|require)\s+(?<name>[\p{L}_][\w:]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RequiredModulePathRegex = new(
        @"^\s*require\s+['""](?<path>[\p{L}_][\p{L}\p{Nd}_]*(?:/[\p{L}_][\p{L}\p{Nd}_]*)*\.pm)['""]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BaseModuleReferenceRegex = new(
        @"^\s*use\s+(?:base|parent)\s+(?<args>.+?);?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MooseInheritanceReferenceRegex = new(
        @"^\s*(?:extends|with)\s+(?<args>.+?);?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedModuleRegex = new(
        @"['""](?<name>[\p{L}_][\w:]*)['""]|qw\s*\((?<paren>[^)]*)\)|qw\s*\[(?<bracket>[^\]]*)\]|qw\s*\{(?<brace>[^}]*)\}|qw\s*<(?<angle>[^>]*)>|qw\s*/(?<slash>[^/]*)/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] QwNamesGroupNames = ["paren", "bracket", "brace", "angle", "slash"];

    private static readonly Regex ArrowCallRegex = new(
        @"(?<receiver>(?:[\p{L}_][\w:]*)|\$[\p{L}_]\w*)\s*->\s*(?<name>[\p{L}_]\w*)\s*(?:\(|\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QualifiedFunctionCallRegex = new(
        @"(?<![\w:])(?<name>[\p{L}_]\w*(?:::[\p{L}_]\w*)+)\s*\(",
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
        EmitRequiredModulePathReference(originalLine, references, seen, fileId, context, lineNumber, resolveContainerForCall);
        EmitBaseModuleReferences(originalLine, references, seen, fileId, context, lineNumber, resolveContainerForCall);
        EmitMooseInheritanceReferences(originalLine, references, seen, fileId, context, lineNumber, resolveContainerForCall);
        EmitQualifiedFunctionCallReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForCall);
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

    private static void EmitRequiredModulePathReference(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var match = RequiredModulePathRegex.Match(originalLine);
        if (!match.Success)
            return;

        var pathGroup = match.Groups["path"];
        var moduleName = pathGroup.Value[..^3].Replace("/", "::", StringComparison.Ordinal);
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            moduleName,
            pathGroup.Index,
            "reference",
            context,
            lineNumber,
            resolveContainerForCall(pathGroup.Index));
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

        AddQuotedModuleReferences(match.Groups["args"], references, seen, fileId, context, lineNumber, resolveContainerForCall);
    }

    private static bool TryGetQwNamesGroup(Match moduleMatch, out Group namesGroup)
    {
        foreach (var groupName in QwNamesGroupNames)
        {
            namesGroup = moduleMatch.Groups[groupName];
            if (namesGroup.Success)
                return true;
        }

        namesGroup = moduleMatch.Groups["paren"];
        return false;
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

    private static void EmitMooseInheritanceReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var match = MooseInheritanceReferenceRegex.Match(originalLine);
        if (!match.Success)
            return;

        AddQuotedModuleReferences(match.Groups["args"], references, seen, fileId, context, lineNumber, resolveContainerForCall);
    }

    private static void AddQuotedModuleReferences(
        Group argsGroup,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var args = argsGroup.Value;
        var argsStart = argsGroup.Index;
        foreach (Match moduleMatch in BoundedRegex.EnumerateMatches(QuotedModuleRegex, args))
        {
            if (moduleMatch.Groups["name"].Success)
            {
                AddBaseModuleReference(moduleMatch.Groups["name"].Value, argsStart + moduleMatch.Groups["name"].Index, references, seen, fileId, context, lineNumber, resolveContainerForCall);
                continue;
            }

            if (!TryGetQwNamesGroup(moduleMatch, out var namesGroup))
                continue;

            var names = namesGroup.Value;
            var namesStart = argsStart + namesGroup.Index;
            foreach (Match nameMatch in BoundedRegex.EnumerateMatches(names, @"[\p{L}_][\w:]*", RegexOptions.CultureInvariant))
                AddBaseModuleReference(nameMatch.Value, namesStart + nameMatch.Index, references, seen, fileId, context, lineNumber, resolveContainerForCall);
        }
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

    private static void EmitQualifiedFunctionCallReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        foreach (Match match in QualifiedFunctionCallRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (IsQualifiedSubroutineDefinition(preparedLine, nameGroup.Index))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                nameGroup.Value,
                nameGroup.Index,
                "call",
                context,
                lineNumber,
                resolveContainerForCall(nameGroup.Index));
        }
    }

    private static bool IsQualifiedSubroutineDefinition(string line, int nameIndex)
    {
        var prefix = line[..nameIndex].TrimEnd();
        return string.Equals(prefix, "sub", StringComparison.Ordinal)
            || prefix.EndsWith(" sub", StringComparison.Ordinal);
    }
}
