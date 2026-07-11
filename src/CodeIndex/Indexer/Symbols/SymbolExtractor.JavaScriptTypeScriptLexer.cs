namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static JavaScriptLexedLine LexJavaScriptLine(string line, JavaScriptLexState state)
    {
        char[]? sanitized = null;
        var i = 0;

        char[] GetSanitizedBuffer()
        {
            return sanitized ??= line.ToCharArray();
        }

        void SetSanitized(int index, char value)
        {
            if (sanitized is null)
            {
                if (line[index] == value)
                    return;

                sanitized = line.ToCharArray();
            }

            sanitized[index] = value;
        }

        while (i < line.Length)
        {
            var ch = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (state.Mode == JavaScriptLexMode.BlockComment)
            {
                SetSanitized(i, ' ');
                if (ch == '*' && next == '/')
                {
                    SetSanitized(i + 1, ' ');
                    state = state with { Mode = JavaScriptLexMode.Code };
                    i++;
                }

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.SingleQuote)
            {
                if (ch is not '\'' and not '\\')
                    SetSanitized(i, ' ');

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '\'')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.DoubleQuote)
            {
                if (ch is not '"' and not '\\')
                    SetSanitized(i, ' ');

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '"')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.TemplateString)
            {
                if (ch is not '`' and not '\\')
                    SetSanitized(i, ' ');

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '`')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (ch == '/' && next == '/')
            {
                while (i < line.Length)
                {
                    SetSanitized(i, ' ');
                    i++;
                }

                break;
            }

            if (ch == '/' && next == '*')
            {
                SetSanitized(i, ' ');
                SetSanitized(i + 1, ' ');
                state = state with { Mode = JavaScriptLexMode.BlockComment };
                i++;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (state.ExpectingControlFlowOpenParen && ch != '(')
                state = state with { ExpectingControlFlowOpenParen = false };

            if (state.RegexAllowedAfterControlFlowParen && ch != '/')
            {
                state = state with
                {
                    RegexAllowedAfterControlFlowParen = false
                };
            }

            if (ch == '\'')
            {
                state = state with { Mode = JavaScriptLexMode.SingleQuote, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '"')
            {
                state = state with { Mode = JavaScriptLexMode.DoubleQuote, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '`')
            {
                state = state with { Mode = JavaScriptLexMode.TemplateString, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '/' && CanStartJavaScriptRegexLiteral(state))
            {
                SetSanitized(i, ' ');
                i = SkipJavaScriptRegexLiteral(line, GetSanitizedBuffer(), i);
                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Other,
                    PreviousIdentifier = null
                };
                i++;
                continue;
            }

            if (char.IsLetter(ch) || ch == '_' || ch == '$')
            {
                var tokenStart = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '$'))
                    i++;

                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Identifier,
                    PreviousIdentifier = line[tokenStart..i],
                    ExpectingControlFlowOpenParen = IsJavaScriptControlFlowKeyword(line[tokenStart..i])
                };
                continue;
            }

            if (char.IsDigit(ch))
            {
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '.'))
                    i++;

                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Number,
                    PreviousIdentifier = null,
                    ExpectingControlFlowOpenParen = false
                };
                continue;
            }

            if (!char.IsWhiteSpace(ch))
            {
                var controlFlowParenDepth = state.ControlFlowParenDepth;
                var regexAllowedAfterControlFlowParen = state.RegexAllowedAfterControlFlowParen;

                if (ch == '(')
                {
                    if (state.ExpectingControlFlowOpenParen)
                    {
                        controlFlowParenDepth = 1;
                        regexAllowedAfterControlFlowParen = false;
                    }
                    else if (controlFlowParenDepth > 0)
                    {
                        controlFlowParenDepth++;
                    }
                }
                else if (ch == ')' && controlFlowParenDepth > 0)
                {
                    controlFlowParenDepth--;
                    if (controlFlowParenDepth == 0)
                        regexAllowedAfterControlFlowParen = true;
                }

                state = state with
                {
                    PreviousTokenKind = ch switch
                    {
                        ')' => JavaScriptPrevTokenKind.CloseParen,
                        ']' => JavaScriptPrevTokenKind.CloseBracket,
                        '}' => JavaScriptPrevTokenKind.CloseBrace,
                        _ => JavaScriptPrevTokenKind.Other
                    },
                    PreviousIdentifier = null,
                    ExpectingControlFlowOpenParen = false,
                    ControlFlowParenDepth = controlFlowParenDepth,
                    RegexAllowedAfterControlFlowParen = regexAllowedAfterControlFlowParen
                };
            }

            i++;
        }

        return new JavaScriptLexedLine(sanitized is null ? line : new string(sanitized), state);
    }

    private static bool CanStartJavaScriptRegexLiteral(JavaScriptLexState state)
    {
        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.None)
            return true;

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.Other)
            return true;

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.Identifier)
        {
            return IsJavaScriptRegexPrefixKeyword(state.PreviousIdentifier);
        }

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.CloseParen)
            return state.RegexAllowedAfterControlFlowParen;

        return false;
    }

    private static bool IsJavaScriptControlFlowKeyword(string identifier)
    {
        return identifier is "if" or "for" or "while" or "switch" or "catch" or "with";
    }

    private static bool IsJavaScriptRegexPrefixKeyword(string? identifier)
    {
        return identifier is
            "return" or "throw" or "case" or "delete" or "typeof" or "void" or "new" or
            "in" or "of" or "instanceof" or "yield" or "await" or "else" or "do" or "finally";
    }

    private static int SkipJavaScriptRegexLiteral(string line, char[] sanitized, int slashIndex)
    {
        var i = slashIndex + 1;
        var inCharacterClass = false;

        while (i < line.Length)
        {
            sanitized[i] = ' ';
            var ch = line[i];
            if (ch == '\\')
            {
                if (i + 1 < line.Length)
                {
                    sanitized[i + 1] = ' ';
                    i += 2;
                    continue;
                }

                return i;
            }

            if (ch == '[')
            {
                inCharacterClass = true;
                i++;
                continue;
            }

            if (ch == ']' && inCharacterClass)
            {
                inCharacterClass = false;
                i++;
                continue;
            }

            if (ch == '/' && !inCharacterClass)
            {
                i++;
                while (i < line.Length && char.IsLetter(line[i]))
                {
                    sanitized[i] = ' ';
                    i++;
                }

                return i - 1;
            }

            i++;
        }

        return line.Length - 1;
    }
}
