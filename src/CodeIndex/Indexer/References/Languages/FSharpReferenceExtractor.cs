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
        foreach (Match match in PipelineCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in BackwardPipelineCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in BackwardPipelineArgumentCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in TryFinallyApplicationCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in ConditionApplicationCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in MatchApplicationCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in WhenGuardApplicationCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in AssertApplicationCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in LazyApplicationCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in RaiseApplicationCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in CastApplicationCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in NewApplicationCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in CompositionOperandCallRegex.Matches(preparedLine))
        {
            addCallLikeReference(match.Groups["left"].Value, match.Groups["left"].Index);
            addCallLikeReference(match.Groups["right"].Value, match.Groups["right"].Index);
        }

        foreach (Match match in SpaceApplicationCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in OperatorCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (IgnoredOperatorCallNames.Contains(name))
                continue;

            var definitionMatch = OperatorDefinitionCallRegex.Match(preparedLine);
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
            if ("!%&*+-./:<=>?@^|~".IndexOf(ch) < 0)
                return false;
        }

        return true;
    }
}
