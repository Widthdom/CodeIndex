using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class FSharpReferenceExtractor
{
    private const string IdentifierPattern = @"(?:``[^`]+``|[_\p{L}][\w']*)";

    private static readonly Regex PipelineCallRegex = new(
        $@"(?<![\w$])(?:\|{{1,3}}>)\s*(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\b",
        RegexOptions.Compiled);

    private static readonly Regex BackwardPipelineCallRegex = new(
        $@"(?<![\w$])(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\s*<\|{{1,3}}",
        RegexOptions.Compiled);

    private static readonly Regex BackwardPipelineArgumentCallRegex = new(
        $@"<\|{{1,3}}\s*(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\b
            (?=\s+(?:{IdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex TryFinallyApplicationCallRegex = new(
        $@"^\s*(?:try|finally)\s+(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\b
            (?=\s+(?:{IdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex ConditionApplicationCallRegex = new(
        $@"^\s*(?:if|elif|while)\s+(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\b
            (?=\s+(?!(?:then|do)\b)(?:{IdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex MatchApplicationCallRegex = new(
        $@"^\s*match!?\s+(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\b
            (?=\s+(?!with\b)(?:{IdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex WhenGuardApplicationCallRegex = new(
        $@"\bwhen\s+(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\b
            (?=\s+(?!->\b)(?:{IdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex AssertApplicationCallRegex = new(
        $@"^\s*assert\s+(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\b
            (?=\s+(?:{IdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex LazyApplicationCallRegex = new(
        $@"\blazy\s+(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\b
            (?=\s+(?:{IdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex RaiseApplicationCallRegex = new(
        $@"\braise\s+(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\b
            (?=\s+(?:{IdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex CastApplicationCallRegex = new(
        $@"\b(?:upcast|downcast)\s+(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\b
            (?=\s+(?:{IdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex NewApplicationCallRegex = new(
        $@"\bnew\s+(?:(?:{IdentifierPattern})\s*\.\s*)*(?<name>{IdentifierPattern})\b
            (?=\s+(?:{IdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex CompositionOperandCallRegex = new(
        $@"(?=(?<![\w$])(?:(?:{IdentifierPattern})\s*\.\s*)*(?<left>{IdentifierPattern})\b\s*(?:>>|<<)\s*(?:(?:{IdentifierPattern})\s*\.\s*)*(?<right>{IdentifierPattern})\b)",
        RegexOptions.Compiled);

    private static readonly Regex SpaceApplicationCallRegex = new(
        $@"(?:\b(?:then|do!?|else|in|to|downto|return!?|yield!?)\s+|->\s+|[=(,\[\{{;]\s*|^\s*)
            (?:(?:{IdentifierPattern})\s*\.\s*)*
            (?<name>{IdentifierPattern})\b
            (?=\s+(?!(?:do|else)\b)(?:{IdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex OperatorCallRegex = new(
        @"(?<![\w$])(?<name>[!%&*+\-./:<=>?@^|~]{2,})(?![\w$])",
        RegexOptions.Compiled);

    private static readonly Regex OperatorDefinitionCallRegex = new(
        @"^\s*let\s+(?:(?:rec|mutable|inline|private|internal|public)\s+)*\((?<name>[!%&*+\-./:<=>?@^|~]{2,})\)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> IgnoredOperatorCallNames = new(StringComparer.Ordinal)
    {
        "->", "<-", "..", "<|", "<||", "<|||", "|>", "||>", "|||>", "|>>", "<<", ">>", "<<<", ">>>",
        "&&", "&&&", "||", "|||", "::", "<>", "<=", ">=", "**", "@@", ":>", ":?", ":=",
    };

    public static void EmitAdditionalCallReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference)
    {
        if (preparedLine.IndexOf("|>", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(PipelineCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf("<|", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(BackwardPipelineCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }

            foreach (Match match in Regex.EnumerateMatches(BackwardPipelineArgumentCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf("try", StringComparison.Ordinal) >= 0
            || preparedLine.IndexOf("finally", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(TryFinallyApplicationCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf("if", StringComparison.Ordinal) >= 0
            || preparedLine.IndexOf("elif", StringComparison.Ordinal) >= 0
            || preparedLine.IndexOf("while", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(ConditionApplicationCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf("match", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(MatchApplicationCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf("when", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(WhenGuardApplicationCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf("assert", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(AssertApplicationCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf("lazy", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(LazyApplicationCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf("raise", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(RaiseApplicationCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf("upcast", StringComparison.Ordinal) >= 0
            || preparedLine.IndexOf("downcast", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(CastApplicationCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf("new", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(NewApplicationCallRegex, preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf(">>", StringComparison.Ordinal) >= 0
            || preparedLine.IndexOf("<<", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(CompositionOperandCallRegex, preparedLine))
            {
                addCallLikeReference(match.Groups["left"].Value, match.Groups["left"].Index);
                addCallLikeReference(match.Groups["right"].Value, match.Groups["right"].Index);
            }
        }

        foreach (Match match in Regex.EnumerateMatches(SpaceApplicationCallRegex, preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        if (!HasOperatorCallCandidate(preparedLine))
        {
            return;
        }

        var definitionMatch = OperatorDefinitionCallRegex.Match(preparedLine);
        foreach (Match match in Regex.EnumerateMatches(OperatorCallRegex, preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (IgnoredOperatorCallNames.Contains(name))
                continue;

            if (definitionMatch.Success
                && string.Equals(definitionMatch.Groups["name"].Value, name, StringComparison.Ordinal)
                && match.Groups["name"].Index == definitionMatch.Groups["name"].Index)
            {
                continue;
            }

            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }
    }

    public static bool IsOperatorCallName(string name)
    {
        if (name.Length < 2)
            return false;

        foreach (var ch in name)
        {
            if (!IsOperatorCallChar(ch))
                return false;
        }

        return true;
    }

    private static bool HasOperatorCallCandidate(string line)
    {
        var previousWasOperator = false;
        foreach (var ch in line)
        {
            var isOperator = IsOperatorCallChar(ch);
            if (isOperator && previousWasOperator)
            {
                return true;
            }

            previousWasOperator = isOperator;
        }

        return false;
    }

    private static bool IsOperatorCallChar(char ch) => "!%&*+-./:<=>?@^|~".IndexOf(ch) >= 0;
}
