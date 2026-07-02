using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Cli;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private sealed class IgnoreRuleSet
    {
        internal static readonly IgnoreRuleSet Empty = new(null, []);

        private readonly IgnoreRuleSet? _parent;
        private readonly IReadOnlyList<IgnoreRule> _rules;
        private readonly string? _sourceDirectory;
        private readonly bool _hasBasenameOnlyRule;

        private IgnoreRuleSet(IgnoreRuleSet? parent, IReadOnlyList<IgnoreRule> rules)
        {
            _parent = parent;
            _rules = rules;
            _sourceDirectory = rules.Count == 0 ? null : rules[0].SourceDirectory;
            _hasBasenameOnlyRule = HasBasenameOnlyRule(rules);
        }

        internal static IgnoreRuleSet CreateChild(IgnoreRuleSet parent, IReadOnlyList<IgnoreRule> rules)
            => rules.Count == 0 ? parent : new IgnoreRuleSet(parent, rules);

        private static bool HasBasenameOnlyRule(IReadOnlyList<IgnoreRule> rules)
        {
            foreach (var rule in rules)
            {
                if (rule.MatchesBasenameOnly)
                    return true;
            }

            return false;
        }

        internal bool IsIgnored(string absolutePath, bool isDirectory)
        {
            for (IgnoreRuleSet? ruleSet = this; ruleSet is not null; ruleSet = ruleSet._parent)
            {
                if (ruleSet._sourceDirectory is null)
                    continue;

                var relativePath = IgnoreRule.GetRelativeCandidatePath(ruleSet._sourceDirectory, absolutePath);
                if (relativePath is null)
                    continue;

                var basename = ruleSet._hasBasenameOnlyRule ? Path.GetFileName(relativePath) : null;
                for (var index = ruleSet._rules.Count - 1; index >= 0; index--)
                {
                    var rule = ruleSet._rules[index];
                    if (rule.IsMatch(relativePath, basename, isDirectory))
                        return !rule.Negated;
                }
            }

            return false;
        }
    }

    private readonly record struct IgnoreRuleLoadResult(
        IgnoreRuleSet Rules,
        bool IgnoreRulesAvailable);

    private sealed class IgnoreRule
    {
        private readonly record struct PatternToken(char Value, bool Escaped);

        private readonly string _sourceDirectory;
        private readonly IIgnoreMatcher _matcher;
        private readonly bool _asciiIgnoreCase;
        private readonly bool _directoryOnly;
        private readonly bool _matchBasenameOnly;

        private IgnoreRule(
            string sourceDirectory,
            IIgnoreMatcher matcher,
            bool asciiIgnoreCase,
            bool negated,
            bool directoryOnly,
            bool matchBasenameOnly)
        {
            _sourceDirectory = sourceDirectory;
            _matcher = matcher;
            _asciiIgnoreCase = asciiIgnoreCase;
            Negated = negated;
            _directoryOnly = directoryOnly;
            _matchBasenameOnly = matchBasenameOnly;
        }

        internal bool Negated { get; }

        internal string SourceDirectory => _sourceDirectory;

        internal bool MatchesBasenameOnly => _matchBasenameOnly;

        internal static bool TryParse(string sourceDirectory, string rawLine, bool ignoreCase, out IgnoreRule? rule, out string? errorMessage)
        {
            rule = null;
            errorMessage = null;
            if (!TryTokenize(rawLine, out var tokens))
                return false;

            if (tokens.Count > MaxIgnorePatternLength)
            {
                errorMessage = $"Invalid ignore rule skipped: pattern exceeds {MaxIgnorePatternLength} characters";
                return false;
            }

            if (tokens[0] is { Value: '#', Escaped: false })
                return false;

            var negated = false;
            if (tokens[0] is { Value: '!', Escaped: false })
            {
                negated = true;
                tokens.RemoveAt(0);
            }

            if (tokens.Count == 0)
                return false;

            var directoryOnly = tokens[^1] is { Value: '/', Escaped: false };
            if (directoryOnly)
                tokens.RemoveAt(tokens.Count - 1);

            if (tokens.Count == 0)
                return false;

            var anchoredToSourceDirectory = tokens[0] is { Value: '/', Escaped: false };
            if (anchoredToSourceDirectory)
                tokens.RemoveAt(0);

            if (tokens.Count == 0)
                return false;

            var matchBasenameOnly = !anchoredToSourceDirectory && !ContainsUnescapedSlash(tokens);
            try
            {
                if (ignoreCase)
                    tokens = FoldAsciiTokens(tokens);

                var matcher = BuildMatcher(tokens, ignoreCase);
                rule = new IgnoreRule(sourceDirectory, matcher, ignoreCase, negated, directoryOnly, matchBasenameOnly);
                return true;
            }
            catch (ArgumentException ex)
            {
                errorMessage = $"Invalid ignore rule skipped: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}";
                return false;
            }
        }

        internal static string? GetRelativeCandidatePath(string sourceDirectory, string absolutePath)
        {
            var relativePath = NormalizeIgnorePath(GetRelativePathFromDirectory(sourceDirectory, absolutePath));
            if (relativePath.Length == 0 ||
                relativePath == "." ||
                relativePath.StartsWith("../", StringComparison.Ordinal))
            {
                return null;
            }

            return relativePath;
        }

        internal bool IsMatch(string relativePath, string? basename, bool isDirectory)
        {
            if (_directoryOnly && !isDirectory)
                return false;

            var candidate = _matchBasenameOnly
                ? basename
                : relativePath;

            if (string.IsNullOrEmpty(candidate))
                return false;

            if (_asciiIgnoreCase)
                candidate = FoldAscii(candidate);

            return _matcher.IsMatch(candidate);
        }

        private static bool ContainsUnescapedSlash(IReadOnlyList<PatternToken> tokens)
        {
            foreach (var token in tokens)
            {
                if (token is { Value: '/', Escaped: false })
                    return true;
            }

            return false;
        }

        private static bool TryTokenize(string rawLine, out List<PatternToken> tokens)
        {
            tokens = [];
            if (string.IsNullOrEmpty(rawLine))
                return false;

            var escaping = false;
            foreach (var ch in rawLine)
            {
                if (escaping)
                {
                    tokens.Add(new PatternToken(ch, Escaped: true));
                    escaping = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaping = true;
                    continue;
                }

                tokens.Add(new PatternToken(ch, Escaped: false));
            }

            if (escaping)
                tokens.Add(new PatternToken('\\', Escaped: false));

            while (tokens.Count > 0 && tokens[^1] is { Value: ' ' or '\t', Escaped: false })
                tokens.RemoveAt(tokens.Count - 1);
            while (tokens.Count > 0 && tokens[0] is { Value: ' ' or '\t', Escaped: false })
                tokens.RemoveAt(0);

            return tokens.Count > 0;
        }

        private interface IIgnoreMatcher
        {
            bool IsMatch(string candidate);
        }

        private sealed class LiteralIgnoreMatcher : IIgnoreMatcher
        {
            private readonly string _literal;

            internal LiteralIgnoreMatcher(string literal)
            {
                _literal = literal;
            }

            public bool IsMatch(string candidate)
                => string.Equals(candidate, _literal, StringComparison.Ordinal);
        }

        private sealed class RegexIgnoreMatcher : IIgnoreMatcher
        {
            private readonly Regex _regex;

            internal RegexIgnoreMatcher(Regex regex)
            {
                _regex = regex;
            }

            public bool IsMatch(string candidate)
                => _regex.IsMatch(candidate);
        }

        private static IIgnoreMatcher BuildMatcher(IReadOnlyList<PatternToken> pattern, bool ignoreCase)
        {
            if (TryBuildLiteralPattern(pattern, out var literal))
                return new LiteralIgnoreMatcher(literal);

            var builder = new StringBuilder();
            builder.Append('^');

            for (var i = 0; i < pattern.Count; i++)
            {
                var token = pattern[i];
                var ch = token.Value;
                if (token.Escaped)
                {
                    AppendRegexEscapedChar(builder, ch);
                    continue;
                }

                if (ch == '*')
                {
                    var isDoubleStar = i + 1 < pattern.Count && pattern[i + 1] is { Value: '*', Escaped: false };
                    if (isDoubleStar)
                    {
                        var nextChar = i + 2 < pattern.Count ? pattern[i + 2].Value : '\0';
                        if (nextChar == '/')
                        {
                            builder.Append("(?:[^/]+/)*");
                            i += 2;
                            continue;
                        }

                        if (i > 0 &&
                            pattern[i - 1] is { Value: '/', Escaped: false } &&
                            i + 2 == pattern.Count)
                        {
                            builder.Length -= 1;
                            builder.Append("/.*");
                            i++;
                            continue;
                        }

                        builder.Append("[^/]*");
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    if (isDoubleStar)
                        i++;
                    continue;
                }

                if (ch == '?')
                {
                    builder.Append("[^/]");
                    continue;
                }

                if (ch == '[' && TryBuildCharacterClass(pattern, ref i, builder, ignoreCase))
                    continue;

                AppendRegexEscapedChar(builder, ch);
            }

            builder.Append('$');
            return new RegexIgnoreMatcher(RegexRegistry.CreateFileIgnorePatternRegex(builder.ToString()));
        }

        private static void AppendRegexEscapedChar(StringBuilder builder, char ch)
        {
            if (IsOrdinaryRegexLiteralChar(ch))
            {
                builder.Append(ch);
                return;
            }

            builder.Append(Regex.Escape(ch.ToString()));
        }

        private static bool IsOrdinaryRegexLiteralChar(char ch) =>
            ch is not ('\\' or '*' or '+' or '?' or '|' or '{' or '[' or '(' or ')' or '^' or '$' or '.' or '#' or ' ' or '\t' or '\r' or '\n' or '\f');

        private static bool TryBuildLiteralPattern(IReadOnlyList<PatternToken> pattern, out string literal)
        {
            for (var index = 0; index < pattern.Count; index++)
            {
                var token = pattern[index];
                if (!token.Escaped && token.Value is '*' or '?' or '[')
                {
                    literal = string.Empty;
                    return false;
                }
            }

            literal = string.Create(
                pattern.Count,
                pattern,
                static (chars, tokens) =>
                {
                    for (var index = 0; index < tokens.Count; index++)
                        chars[index] = tokens[index].Value;
                });
            return true;
        }

        private static bool TryBuildCharacterClass(IReadOnlyList<PatternToken> pattern, ref int index, StringBuilder builder, bool ignoreCase)
        {
            var contentStart = index + 1;
            if (contentStart >= pattern.Count)
                throw new ArgumentException("malformed character class");

            if (pattern[contentStart] is { Value: '!', Escaped: false })
            {
                contentStart++;
            }
            else if (pattern[contentStart] is { Value: '^', Escaped: false })
            {
                contentStart++;
            }

            if (contentStart >= pattern.Count)
                throw new ArgumentException("malformed character class");

            var allowLeadingRightBracket =
                contentStart < pattern.Count &&
                pattern[contentStart] is { Value: ']', Escaped: false };

            var scanStart = allowLeadingRightBracket ? contentStart + 1 : contentStart;
            var closingIndex = FindCharacterClassClosingIndex(pattern, scanStart);

            if (closingIndex < scanStart)
                throw new ArgumentException("malformed character class");

            builder.Append('[');
            if (pattern[index + 1] is { Value: '!', Escaped: false })
            {
                builder.Append('^');
            }
            else if (pattern[index + 1] is { Value: '^', Escaped: false })
            {
                builder.Append('^');
            }

            if (allowLeadingRightBracket)
            {
                builder.Append(@"\]");
                contentStart++;
            }

            for (var i = contentStart; i < closingIndex; i++)
            {
                var token = pattern[i];
                var ch = token.Value;
                if (token.Escaped)
                {
                    AppendCharacterClassLiteral(builder, ch, ignoreCase);
                    continue;
                }

                if (ch == '[' && TryAppendPosixCharacterClass(pattern, closingIndex, ref i, builder, ignoreCase))
                    continue;

                if (i + 2 < closingIndex &&
                    pattern[i + 1] is { Value: '-', Escaped: false })
                {
                    var endToken = pattern[i + 2];
                    if (!endToken.Escaped &&
                        TryAppendCharacterClassRange(builder, ch, endToken.Value, ignoreCase))
                    {
                        i += 2;
                        continue;
                    }
                }

                if (ch is '\\' or '[' or ']')
                {
                    builder.Append('\\');
                    builder.Append(ch);
                    continue;
                }

                AppendCharacterClassLiteral(builder, ch, ignoreCase);
            }

            builder.Append(']');
            index = closingIndex;
            return true;
        }

        private static int FindCharacterClassClosingIndex(IReadOnlyList<PatternToken> pattern, int scanStart)
        {
            for (var i = scanStart; i < pattern.Count; i++)
            {
                if (pattern[i].Escaped)
                    continue;

                if (pattern[i].Value == '[' && TryFindPosixCharacterClassEnd(pattern, i, out var posixEnd))
                {
                    i = posixEnd;
                    continue;
                }

                if (pattern[i].Value == ']')
                    return i;
            }

            return -1;
        }

        private static bool TryAppendPosixCharacterClass(IReadOnlyList<PatternToken> pattern, int closingIndex, ref int index, StringBuilder builder, bool ignoreCase)
        {
            if (!TryFindPosixCharacterClassEnd(pattern, index, out var posixEnd) || posixEnd >= closingIndex)
                return false;

            var nameChars = new StringBuilder();
            for (var i = index + 2; i < posixEnd - 1; i++)
                nameChars.Append(pattern[i].Value);

            builder.Append(GetPosixCharacterClassPattern(nameChars.ToString(), ignoreCase));
            index = posixEnd;
            return true;
        }

        private static bool TryFindPosixCharacterClassEnd(IReadOnlyList<PatternToken> pattern, int startIndex, out int endIndex)
        {
            endIndex = -1;
            if (startIndex + 3 >= pattern.Count ||
                pattern[startIndex] is not { Value: '[', Escaped: false } ||
                pattern[startIndex + 1] is not { Value: ':', Escaped: false })
            {
                return false;
            }

            for (var i = startIndex + 2; i + 1 < pattern.Count; i++)
            {
                if (pattern[i] is { Value: ':', Escaped: false } &&
                    pattern[i + 1] is { Value: ']', Escaped: false })
                {
                    endIndex = i + 1;
                    return true;
                }
            }

            return false;
        }

        private static string GetPosixCharacterClassPattern(string className, bool ignoreCase)
            => className switch
            {
                "alnum" => "A-Za-z0-9",
                "alpha" => "A-Za-z",
                "blank" => " \t",
                "cntrl" => @"\x00-\x1F\x7F",
                "digit" => "0-9",
                "graph" => "!-~",
                "lower" => ignoreCase ? "A-Za-z" : "a-z",
                "print" => " -~",
                "punct" => @"!-/:-@\[-`\{-~",
                "space" => " \t\r\n\v\f",
                "upper" => ignoreCase ? "A-Za-z" : "A-Z",
                "xdigit" => "0-9A-Fa-f",
                _ => throw new ArgumentException($"unsupported POSIX character class '{className}'"),
            };

        private static string EscapeCharacterClassLiteral(char ch)
            => ch switch
            {
                '\\' or '[' or ']' or '^' or '-' => $@"\{ch}",
                _ => ch.ToString(),
            };

        private static void AppendCharacterClassLiteral(StringBuilder builder, char ch, bool ignoreCase)
        {
            if (ignoreCase && IsAsciiLetter(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                builder.Append(char.ToUpperInvariant(ch));
                return;
            }

            builder.Append(EscapeCharacterClassLiteral(ch));
        }

        private static bool TryAppendCharacterClassRange(StringBuilder builder, char start, char end, bool ignoreCase)
        {
            if (start > end)
                throw new ArgumentException("reversed character class range");

            builder.Append(EscapeCharacterClassLiteral(start));
            builder.Append('-');
            builder.Append(EscapeCharacterClassLiteral(end));

            if (!ignoreCase ||
                !IsAsciiLetter(start) ||
                !IsAsciiLetter(end))
            {
                return true;
            }

            var lowerStart = char.ToLowerInvariant(start);
            var lowerEnd = char.ToLowerInvariant(end);
            var upperStart = char.ToUpperInvariant(start);
            var upperEnd = char.ToUpperInvariant(end);

            if (lowerStart == start && lowerEnd == end)
            {
                builder.Append(char.ToUpperInvariant(start));
                builder.Append('-');
                builder.Append(char.ToUpperInvariant(end));
                return true;
            }

            if (upperStart == start && upperEnd == end)
            {
                builder.Append(char.ToLowerInvariant(start));
                builder.Append('-');
                builder.Append(char.ToLowerInvariant(end));
                return true;
            }

            return true;
        }

        private static List<PatternToken> FoldAsciiTokens(IReadOnlyList<PatternToken> tokens)
        {
            var foldedTokens = new List<PatternToken>(tokens.Count);
            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                foldedTokens.Add(new PatternToken(FoldAsciiChar(token.Value), token.Escaped));
            }

            return foldedTokens;
        }

        private static string FoldAscii(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] is not (>= 'A' and <= 'Z'))
                    continue;

                var chars = value.ToCharArray();
                chars[i] = FoldAsciiChar(chars[i]);
                for (var j = i + 1; j < chars.Length; j++)
                    chars[j] = FoldAsciiChar(chars[j]);
                return new string(chars);
            }

            return value;
        }

        private static char FoldAsciiChar(char ch)
            => ch is >= 'A' and <= 'Z'
                ? char.ToLowerInvariant(ch)
                : ch;

        private static bool IsAsciiLetter(char ch)
            => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
    }
}
