using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class RubyReferenceExtractor
{
    private static readonly Regex CommandCallRegex = new(
        @"(?<![\w$@])(?<name>[A-Za-z_]\w*[?!]?)\s+(?![=<>!~+\-*/%&|^]|do\b|end\b|then\b|\()(?:[:'""\w])",
        RegexOptions.Compiled);

    private static readonly Regex BlockCallRegex = new(
        @"(?<![\w$@:])(?<name>[A-Za-z_]\w*[?!]?)(?:\s*\([^\)\r\n]*\))?\s*(?:\{|do\b)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> CommandTargetReferenceNames = new(StringComparer.Ordinal)
    {
        "include", "extend", "prepend", "using", "refine", "autoload", "require", "require_relative", "load", "gem", "raise", "attr", "attr_accessor", "attr_reader", "attr_writer",
        "private_constant", "public_constant", "module_function",
        "alias", "alias_method",
        "define_method", "describe", "resource", "resources", "create_table", "attribute", "serialize", "before_action", "after_action", "around_action", "helper_method", "rescue_from",
        "has_many", "has_one", "belongs_to", "composed_of", "accepts_nested_attributes_for", "scope", "delegate", "validates", "enum",
    };

    private static readonly HashSet<string> CommandTargetSingleTokenNames = new(StringComparer.Ordinal)
    {
        "require", "require_relative", "load", "gem", "raise", "define_method", "create_table", "attribute", "serialize",
    };

    private static readonly HashSet<string> ClassNameOptionCommandNames = new(StringComparer.Ordinal)
    {
        "has_many", "has_one", "belongs_to", "composed_of",
    };

    private static readonly HashSet<string> SymbolLiteralFirstArgumentCommandNames = new(StringComparer.Ordinal)
    {
        "send", "public_send", "define_method",
    };

    private static readonly Regex CommandTargetTokenRegex = new(
        @"(?<![\w$@])(?<token>:(?:""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|[A-Za-z_]\w*[?!]?)|[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')",
        RegexOptions.Compiled);

    private static readonly Regex SymbolLiteralFirstArgumentRegex = new(
        @"(?<![\w$@])(?<name>send|public_send|define_method)\s*\(\s*(?<token>:(?:""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|[A-Za-z_]\w*[?!]?))",
        RegexOptions.Compiled);

    private static readonly Regex ClassNameOptionRegex = new(
        @"(?<![\w$@]):?class_name\s*(?::|=>)\s*(?<quote>['""])(?<name>[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)\k<quote>",
        RegexOptions.Compiled);

    private static readonly Regex ClassInheritanceRegex = new(
        @"^\s*class\s+[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*\s*<\s*(?<name>[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)",
        RegexOptions.Compiled);

    private static readonly Regex RescueClauseRegex = new(
        @"(?<![\w$@])rescue\s+(?<types>[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*(?:\s*,\s*[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)*)",
        RegexOptions.Compiled);

    private static readonly Regex QualifiedConstantRegex = new(
        @"[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*",
        RegexOptions.Compiled);

    public static void EmitAdditionalCallReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall,
        HashSet<int> matchedCallIndices,
        Action<string, int> addCallLikeReference)
    {
        EmitInheritanceReferences(
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForCall);

        EmitRescueTypeReferences(
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForCall);

        foreach (Match match in CommandCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            matchedCallIndices.Add(callIndex);
            addCallLikeReference(name, callIndex);
            EmitCommandTargetReferences(
                name,
                callIndex,
                originalLine,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForCall);
        }

        EmitSymbolLiteralFirstArgumentReferences(
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForCall);

        foreach (Match match in BlockCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }
    }

    public static void EmitCommandTargetReferences(
        string name,
        int callIndex,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var isKnownCommand = CommandTargetReferenceNames.Contains(name);
        var onlyFirstSymbolLiteral = SymbolLiteralFirstArgumentCommandNames.Contains(name) || !isKnownCommand;
        if (!isKnownCommand && !onlyFirstSymbolLiteral)
            return;

        var argsStart = callIndex + name.Length;
        while (argsStart < originalLine.Length && char.IsWhiteSpace(originalLine[argsStart]))
            argsStart++;

        if (argsStart < originalLine.Length && originalLine[argsStart] == '(')
            argsStart++;

        while (argsStart < originalLine.Length && char.IsWhiteSpace(originalLine[argsStart]))
            argsStart++;

        if (argsStart >= originalLine.Length)
            return;

        var tail = originalLine[argsStart..];
        var commentIndex = tail.IndexOf('#');
        if (commentIndex >= 0)
            tail = tail[..commentIndex];

        EmitClassNameOptionReferences(
            name,
            tail,
            argsStart,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForCall);

        var matchedAny = false;
        foreach (Match match in CommandTargetTokenRegex.Matches(tail))
        {
            var rawToken = match.Groups["token"].Value;
            if (rawToken.Length == 0)
                continue;

            if (string.Equals(rawToken, "do", StringComparison.Ordinal)
                || string.Equals(rawToken, "end", StringComparison.Ordinal)
                || string.Equals(rawToken, "then", StringComparison.Ordinal))
            {
                break;
            }

            if (IsHashOptionKey(tail, match, rawToken))
                break;

            if (onlyFirstSymbolLiteral && rawToken[0] != ':')
                break;

            if (string.Equals(name, "raise", StringComparison.Ordinal))
            {
                if (rawToken[0] == ':' || rawToken[0] == '\'' || rawToken[0] == '"')
                    return;
                if (!IsIdentifierStart(rawToken[0]))
                    return;
            }
            else if (CommandTargetSingleTokenNames.Contains(name) && matchedAny)
            {
                break;
            }
            else if (rawToken[0] == '\'' || rawToken[0] == '"')
            {
                if (!string.Equals(name, "require", StringComparison.Ordinal)
                    && !string.Equals(name, "require_relative", StringComparison.Ordinal)
                    && !string.Equals(name, "load", StringComparison.Ordinal)
                    && !string.Equals(name, "gem", StringComparison.Ordinal)
                    && !string.Equals(name, "create_table", StringComparison.Ordinal)
                    && !string.Equals(name, "define_method", StringComparison.Ordinal))
                {
                    continue;
                }
            }

            var token = NormalizeCommandTargetToken(rawToken);
            if (string.IsNullOrWhiteSpace(token))
                continue;

            var tokenIndex = argsStart + match.Groups["token"].Index;
            var targetContainer = resolveContainerForCall(tokenIndex);
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                token,
                tokenIndex,
                "reference",
                context,
                lineNumber,
                targetContainer);
            matchedAny = true;

            if (CommandTargetSingleTokenNames.Contains(name))
                break;
            if (onlyFirstSymbolLiteral)
                break;
        }
    }

    private static void EmitSymbolLiteralFirstArgumentReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        foreach (Match match in SymbolLiteralFirstArgumentRegex.Matches(preparedLine))
        {
            var rawToken = match.Groups["token"].Value;
            var token = NormalizeCommandTargetToken(rawToken);
            if (string.IsNullOrWhiteSpace(token))
                continue;

            var tokenIndex = match.Groups["token"].Index;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                token,
                tokenIndex,
                "reference",
                context,
                lineNumber,
                resolveContainerForCall(tokenIndex));
        }
    }

    private static bool IsIdentifierStart(char ch) => char.IsLetter(ch) || ch == '_';

    private static void EmitInheritanceReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var match = ClassInheritanceRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var name = match.Groups["name"].Value;
        var tokenIndex = match.Groups["name"].Index;
        var targetContainer = resolveContainerForCall(tokenIndex);
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            name,
            tokenIndex,
            "type_reference",
            context,
            lineNumber,
            targetContainer);
    }

    private static void EmitRescueTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        foreach (Match rescueMatch in RescueClauseRegex.Matches(preparedLine))
        {
            var typesGroup = rescueMatch.Groups["types"];
            foreach (Match typeMatch in QualifiedConstantRegex.Matches(typesGroup.Value))
            {
                var name = typeMatch.Value;
                var tokenIndex = typesGroup.Index + typeMatch.Index;
                var targetContainer = resolveContainerForCall(tokenIndex);
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    name,
                    tokenIndex,
                    "type_reference",
                    context,
                    lineNumber,
                    targetContainer);
            }
        }
    }

    private static void EmitClassNameOptionReferences(
        string commandName,
        string tail,
        int tailStartIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        if (!ClassNameOptionCommandNames.Contains(commandName))
            return;

        foreach (Match match in ClassNameOptionRegex.Matches(tail))
        {
            var name = match.Groups["name"].Value;
            var tokenIndex = tailStartIndex + match.Groups["name"].Index;
            var targetContainer = resolveContainerForCall(tokenIndex);
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                tokenIndex,
                "reference",
                context,
                lineNumber,
                targetContainer);
        }
    }

    private static bool IsHashOptionKey(string tail, Match match, string rawToken)
    {
        var tokenIndex = match.Groups["token"].Index;
        var nextIndex = tokenIndex + rawToken.Length;
        while (nextIndex < tail.Length && char.IsWhiteSpace(tail[nextIndex]))
            nextIndex++;

        if (rawToken[0] == ':')
            return nextIndex + 1 < tail.Length && tail[nextIndex] == '=' && tail[nextIndex + 1] == '>';

        return nextIndex < tail.Length && tail[nextIndex] == ':';
    }

    private static string NormalizeCommandTargetToken(string token)
    {
        if (token.Length == 0)
            return token;

        if (token[0] == ':')
        {
            token = token[1..];
            if (token.Length >= 2
                && ((token[0] == '\'' && token[^1] == '\'')
                    || (token[0] == '"' && token[^1] == '"')))
            {
                token = token[1..^1];
            }
        }
        else if (token.Length >= 2
            && ((token[0] == '\'' && token[^1] == '\'')
                || (token[0] == '"' && token[^1] == '"')))
        {
            token = token[1..^1];
        }

        return token;
    }
}
