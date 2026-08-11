namespace CodeIndex.Database;

internal enum CSharpBaseListProjection
{
    // DbReader resolves the complete type expression, including generic arguments.
    TypeReference,

    // Metadata propagation needs only the qualified type name before generic/constructor syntax.
    HeadIdentifier,
}

internal static class CSharpBaseListParser
{
    internal static List<string> Parse(string? signature, CSharpBaseListProjection projection)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(signature))
            return result;

        var colonIndex = FindBaseListColon(signature);
        if (colonIndex < 0)
            return result;

        var state = new ScanState();
        var entryStart = colonIndex + 1;
        for (var i = entryStart; i < signature.Length; i++)
        {
            if (!state.IsCodeCharacter(signature, ref i))
                continue;

            var ch = signature[i];
            if (state.IsTopLevel)
            {
                if (ch is '{' or ';')
                {
                    AddProjectedEntry(result, signature[entryStart..i], projection);
                    return result;
                }

                if (ch == ',')
                {
                    AddProjectedEntry(result, signature[entryStart..i], projection);
                    entryStart = i + 1;
                    continue;
                }

                if (IsWhereKeyword(signature, i))
                {
                    AddProjectedEntry(result, signature[entryStart..i], projection);
                    return result;
                }
            }

            state.UpdateDelimiterDepth(ch);
        }

        AddProjectedEntry(result, signature[entryStart..], projection);
        return result;
    }

    private static int FindBaseListColon(string signature)
    {
        var state = new ScanState();
        for (var i = 0; i < signature.Length; i++)
        {
            if (!state.IsCodeCharacter(signature, ref i))
                continue;

            var ch = signature[i];
            if (state.IsTopLevel)
            {
                if (ch is '{' or ';' || IsWhereKeyword(signature, i))
                    return -1;

                if (ch == ':' && !IsAliasQualifierColon(signature, i))
                    return i;
            }

            state.UpdateDelimiterDepth(ch);
        }

        return -1;
    }

    private static bool IsAliasQualifierColon(string signature, int index)
    {
        return (index > 0 && signature[index - 1] == ':')
            || (index + 1 < signature.Length && signature[index + 1] == ':');
    }

    private static bool IsWhereKeyword(string signature, int index)
    {
        const string Keyword = "where";
        if (index + Keyword.Length > signature.Length
            || !signature.AsSpan(index, Keyword.Length).SequenceEqual(Keyword))
        {
            return false;
        }

        if (index > 0 && IsIdentifierPart(signature[index - 1]))
            return false;

        return index + Keyword.Length >= signature.Length
            || !IsIdentifierPart(signature[index + Keyword.Length]);
    }

    private static bool IsIdentifierPart(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch is '_' or '@';
    }

    private static void AddProjectedEntry(List<string> result, string rawEntry, CSharpBaseListProjection projection)
    {
        var entry = rawEntry.Trim();
        if (entry.Length == 0)
            return;

        if (projection == CSharpBaseListProjection.TypeReference)
        {
            result.Add(entry);
            return;
        }

        if (projection != CSharpBaseListProjection.HeadIdentifier)
            throw new ArgumentOutOfRangeException(nameof(projection), projection, null);

        var cut = entry.Length;
        for (var i = 0; i < entry.Length; i++)
        {
            var ch = entry[i];
            if (ch is '<' or '(' || char.IsWhiteSpace(ch))
            {
                cut = i;
                break;
            }
        }

        if (cut > 0)
            result.Add(entry[..cut]);
    }

    private struct ScanState
    {
        private int _angleDepth;
        private int _parenDepth;
        private int _squareDepth;
        private char _quote;
        private bool _escaped;
        private bool _verbatimString;
        private bool _lineComment;
        private bool _blockComment;

        internal readonly bool IsTopLevel => _angleDepth == 0 && _parenDepth == 0 && _squareDepth == 0;

        internal bool IsCodeCharacter(string text, ref int index)
        {
            var ch = text[index];
            if (_lineComment)
            {
                if (ch is '\r' or '\n')
                    _lineComment = false;
                return false;
            }

            if (_blockComment)
            {
                if (ch == '*' && index + 1 < text.Length && text[index + 1] == '/')
                {
                    _blockComment = false;
                    index++;
                }
                return false;
            }

            if (_quote != '\0')
            {
                if (_escaped)
                {
                    _escaped = false;
                    return false;
                }

                if (!_verbatimString && ch == '\\')
                {
                    _escaped = true;
                    return false;
                }

                if (_verbatimString && ch == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    index++;
                    return false;
                }

                if (ch == _quote)
                {
                    _quote = '\0';
                    _verbatimString = false;
                }
                return false;
            }

            if (ch == '/' && index + 1 < text.Length)
            {
                if (text[index + 1] == '/')
                {
                    _lineComment = true;
                    index++;
                    return false;
                }

                if (text[index + 1] == '*')
                {
                    _blockComment = true;
                    index++;
                    return false;
                }
            }

            if (ch is '"' or '\'')
            {
                _quote = ch;
                _verbatimString = ch == '"'
                    && ((index > 0 && text[index - 1] == '@')
                        || (index > 1 && text[index - 2] == '@' && text[index - 1] == '$'));
                return false;
            }

            return true;
        }

        internal void UpdateDelimiterDepth(char ch)
        {
            switch (ch)
            {
                case '<':
                    if (_parenDepth == 0 && _squareDepth == 0)
                        _angleDepth++;
                    break;
                case '>':
                    if (_parenDepth == 0 && _squareDepth == 0 && _angleDepth > 0)
                        _angleDepth--;
                    break;
                case '(':
                    _parenDepth++;
                    break;
                case ')':
                    if (_parenDepth > 0)
                        _parenDepth--;
                    break;
                case '[':
                    _squareDepth++;
                    break;
                case ']':
                    if (_squareDepth > 0)
                        _squareDepth--;
                    break;
            }
        }
    }
}
