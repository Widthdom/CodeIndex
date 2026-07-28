using System.Globalization;

namespace CodeIndex.Semantics;

internal enum CSharpSemanticTokenKind
{
    Namespace,
    Type,
    Class,
    Enum,
    Interface,
    Struct,
    TypeParameter,
    Parameter,
    Variable,
    Property,
    EnumMember,
    Event,
    Function,
    Method,
    Keyword,
    Modifier,
    Number,
    Field,
}

internal readonly record struct ClassifiedCSharpSemanticToken(
    int Line,
    int StartCharacter,
    int Length,
    CSharpSemanticTokenKind Kind,
    bool IsDeclaration);

internal static class CSharpSemanticTokenClassifier
{
    internal const int DefaultExcerptTokenLimit = 10_000;

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "abstract", "async", "const", "extern", "file", "internal", "override", "partial",
        "private", "protected", "public", "readonly", "required", "sealed", "static",
        "unsafe", "virtual", "volatile",
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "add", "alias", "and", "as", "ascending", "async", "await", "base", "bool", "break",
        "by", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal",
        "default", "delegate", "descending", "do", "double", "dynamic", "else", "enum", "equals",
        "event", "explicit", "false", "finally", "fixed", "float", "for", "foreach", "from", "get",
        "global", "goto", "group", "if", "implicit", "in", "init", "int", "interface", "internal",
        "into", "is", "join", "let", "lock", "long", "managed", "nameof", "namespace", "new", "not",
        "notnull", "null", "object", "on", "operator", "or", "orderby", "out", "override", "params",
        "partial", "private", "protected", "public", "readonly", "record", "ref", "remove", "required",
        "return", "sbyte", "scoped", "sealed", "select", "set", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint",
        "ulong", "unchecked", "unmanaged", "unsafe", "using", "ushort", "value", "var", "virtual",
        "void", "volatile", "when", "where", "while", "with", "yield",
    };

    private static readonly HashSet<string> BuiltInTypeKeywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "char", "decimal", "double", "dynamic", "float", "int", "long",
        "nint", "nuint", "object", "sbyte", "short", "string", "uint", "ulong", "ushort", "void",
    };

    private static readonly HashSet<string> TypeDeclarationKeywords = new(StringComparer.Ordinal)
    {
        "class", "enum", "interface", "record", "struct",
    };

    internal static IReadOnlyList<ClassifiedCSharpSemanticToken> Classify(
        IReadOnlyList<string?> sourceLines,
        int maxTokens)
    {
        if (maxTokens <= 0 || sourceLines.Count == 0)
            return [];

        var lexemes = Tokenize(sourceLines, maxTokens);
        if (lexemes.Count == 0)
            return [];

        var kinds = new CSharpSemanticTokenKind?[lexemes.Count];
        var declarations = new bool[lexemes.Count];
        var declaredTypeNames = new HashSet<string>(StringComparer.Ordinal);
        var declaredTypeParameterNames = new HashSet<string>(StringComparer.Ordinal);
        var declaredParameterNames = new HashSet<string>(StringComparer.Ordinal);
        var declaredPropertyNames = new HashSet<string>(StringComparer.Ordinal);
        var declaredFieldNames = new HashSet<string>(StringComparer.Ordinal);
        var methodBodies = new List<(int Start, int End)>();

        ClassifyKeywordsAndNumbers(lexemes, kinds);
        ClassifyDirectives(lexemes, kinds);
        ClassifyTypeDeclarations(
            lexemes,
            kinds,
            declarations,
            declaredTypeNames,
            declaredTypeParameterNames,
            declaredParameterNames);
        ClassifyProperties(
            lexemes,
            kinds,
            declarations,
            declaredPropertyNames);
        ClassifyMethods(
            lexemes,
            kinds,
            declarations,
            declaredTypeNames,
            declaredTypeParameterNames,
            declaredParameterNames,
            methodBodies);
        ClassifyVariablesAndFields(
            lexemes,
            kinds,
            declarations,
            methodBodies,
            declaredFieldNames);
        ClassifyRemainingIdentifiers(
            lexemes,
            kinds,
            declaredTypeNames,
            declaredTypeParameterNames,
            declaredParameterNames,
            declaredPropertyNames,
            declaredFieldNames);

        var result = new List<ClassifiedCSharpSemanticToken>(Math.Min(maxTokens, lexemes.Count));
        for (var index = 0; index < lexemes.Count && result.Count < maxTokens; index++)
        {
            if (kinds[index] is not { } kind)
                continue;

            var lexeme = lexemes[index];
            result.Add(new ClassifiedCSharpSemanticToken(
                lexeme.Line,
                lexeme.Start,
                lexeme.Length,
                kind,
                declarations[index]));
        }

        return result;
    }

    internal static string ToProtocolName(CSharpSemanticTokenKind kind) => kind switch
    {
        CSharpSemanticTokenKind.Namespace => "namespace",
        CSharpSemanticTokenKind.Type => "type",
        CSharpSemanticTokenKind.Class => "class",
        CSharpSemanticTokenKind.Enum => "enum",
        CSharpSemanticTokenKind.Interface => "interface",
        CSharpSemanticTokenKind.Struct => "struct",
        CSharpSemanticTokenKind.TypeParameter => "typeParameter",
        CSharpSemanticTokenKind.Parameter => "parameter",
        CSharpSemanticTokenKind.Variable => "variable",
        CSharpSemanticTokenKind.Property => "property",
        CSharpSemanticTokenKind.EnumMember => "enumMember",
        CSharpSemanticTokenKind.Event => "event",
        CSharpSemanticTokenKind.Function => "function",
        CSharpSemanticTokenKind.Method => "method",
        CSharpSemanticTokenKind.Keyword => "keyword",
        CSharpSemanticTokenKind.Modifier => "modifier",
        CSharpSemanticTokenKind.Number => "number",
        CSharpSemanticTokenKind.Field => "field",
        _ => "variable",
    };

    internal static int ToLspTokenType(CSharpSemanticTokenKind kind) => kind switch
    {
        CSharpSemanticTokenKind.Namespace => 0,
        CSharpSemanticTokenKind.Type => 1,
        CSharpSemanticTokenKind.Class => 2,
        CSharpSemanticTokenKind.Enum => 3,
        CSharpSemanticTokenKind.Interface => 4,
        CSharpSemanticTokenKind.Struct => 5,
        CSharpSemanticTokenKind.TypeParameter => 6,
        CSharpSemanticTokenKind.Parameter => 7,
        CSharpSemanticTokenKind.Variable => 8,
        CSharpSemanticTokenKind.Property => 9,
        CSharpSemanticTokenKind.EnumMember => 10,
        CSharpSemanticTokenKind.Event => 11,
        CSharpSemanticTokenKind.Function => 12,
        CSharpSemanticTokenKind.Method => 13,
        CSharpSemanticTokenKind.Keyword => 15,
        CSharpSemanticTokenKind.Modifier => 16,
        CSharpSemanticTokenKind.Number => 19,
        CSharpSemanticTokenKind.Field => 23,
        _ => 8,
    };

    private static void ClassifyKeywordsAndNumbers(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds)
    {
        for (var index = 0; index < lexemes.Count; index++)
        {
            var lexeme = lexemes[index];
            if (lexeme.Kind == LexemeKind.Number)
            {
                kinds[index] = CSharpSemanticTokenKind.Number;
                continue;
            }

            if (lexeme.Kind != LexemeKind.Identifier)
                continue;

            var word = NormalizeIdentifier(lexeme.Text);
            if (Modifiers.Contains(word))
                kinds[index] = CSharpSemanticTokenKind.Modifier;
            else if (Keywords.Contains(word))
                kinds[index] = CSharpSemanticTokenKind.Keyword;
        }
    }

    private static void ClassifyDirectives(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds)
    {
        for (var index = 0; index < lexemes.Count; index++)
        {
            var word = NormalizeIdentifier(lexemes[index].Text);
            if (word == "namespace")
            {
                MarkQualifiedName(lexemes, kinds, index + 1, stopAtEquals: false);
            }
            else if (word == "using" && IsDirectiveStart(lexemes, index))
            {
                MarkQualifiedName(lexemes, kinds, index + 1, stopAtEquals: true);
            }
        }
    }

    private static void ClassifyTypeDeclarations(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds,
        bool[] declarations,
        HashSet<string> declaredTypeNames,
        HashSet<string> declaredTypeParameterNames,
        HashSet<string> declaredParameterNames)
    {
        for (var index = 0; index < lexemes.Count; index++)
        {
            var keyword = NormalizeIdentifier(lexemes[index].Text);
            if (!TypeDeclarationKeywords.Contains(keyword))
                continue;

            var nameIndex = FindNextUnclassifiedIdentifier(lexemes, kinds, index + 1);
            if (nameIndex < 0)
                continue;

            var kind = keyword switch
            {
                "enum" => CSharpSemanticTokenKind.Enum,
                "interface" => CSharpSemanticTokenKind.Interface,
                "struct" => CSharpSemanticTokenKind.Struct,
                _ => CSharpSemanticTokenKind.Class,
            };
            kinds[nameIndex] = kind;
            declarations[nameIndex] = true;
            declaredTypeNames.Add(NormalizeIdentifier(lexemes[nameIndex].Text));

            MarkTypeParametersAfter(
                lexemes,
                kinds,
                declarations,
                nameIndex,
                declaredTypeParameterNames);

            if (TryFindInvocationOpenParen(lexemes, nameIndex, out var openParen))
            {
                MarkParameters(
                    lexemes,
                    kinds,
                    declarations,
                    openParen,
                    declaredParameterNames);
            }
        }
    }

    private static void ClassifyProperties(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds,
        bool[] declarations,
        HashSet<string> declaredPropertyNames)
    {
        for (var index = 0; index < lexemes.Count; index++)
        {
            if (lexemes[index].Kind != LexemeKind.Identifier ||
                kinds[index].HasValue ||
                !IsDeclarationTypeBefore(lexemes, kinds, index))
            {
                continue;
            }

            var next = index + 1;
            if (next >= lexemes.Count)
                continue;

            var isProperty = lexemes[next].Text == "{" && ContainsPropertyAccessor(lexemes, next);
            isProperty |= lexemes[next].Text == "=>";
            if (!isProperty)
                continue;

            kinds[index] = CSharpSemanticTokenKind.Property;
            declarations[index] = true;
            declaredPropertyNames.Add(NormalizeIdentifier(lexemes[index].Text));
        }
    }

    private static void ClassifyMethods(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds,
        bool[] declarations,
        HashSet<string> declaredTypeNames,
        HashSet<string> declaredTypeParameterNames,
        HashSet<string> declaredParameterNames,
        List<(int Start, int End)> methodBodies)
    {
        for (var index = 0; index < lexemes.Count; index++)
        {
            if (lexemes[index].Kind != LexemeKind.Identifier || kinds[index].HasValue)
                continue;
            if (!TryFindInvocationOpenParen(lexemes, index, out var openParen))
                continue;

            var previous = index - 1;
            if (previous >= 0 && NormalizeIdentifier(lexemes[previous].Text) == "new")
            {
                kinds[index] = CSharpSemanticTokenKind.Type;
                continue;
            }

            var name = NormalizeIdentifier(lexemes[index].Text);
            var isDeclaration = declaredTypeNames.Contains(name) ||
                IsDeclarationTypeBefore(lexemes, kinds, index);
            kinds[index] = CSharpSemanticTokenKind.Method;
            declarations[index] = isDeclaration;

            if (!isDeclaration)
                continue;

            MarkTypeParametersAfter(
                lexemes,
                kinds,
                declarations,
                index,
                declaredTypeParameterNames);
            var closeParen = MarkParameters(
                lexemes,
                kinds,
                declarations,
                openParen,
                declaredParameterNames);
            if (closeParen >= 0 && TryFindBodyRange(lexemes, closeParen, out var body))
                methodBodies.Add(body);
        }
    }

    private static void ClassifyVariablesAndFields(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds,
        bool[] declarations,
        IReadOnlyList<(int Start, int End)> methodBodies,
        HashSet<string> declaredFieldNames)
    {
        for (var index = 0; index < lexemes.Count; index++)
        {
            if (lexemes[index].Kind != LexemeKind.Identifier ||
                kinds[index].HasValue ||
                !IsDeclarationTypeBefore(lexemes, kinds, index) ||
                !HasDeclarationTerminatorAfter(lexemes, index))
            {
                continue;
            }

            var insideMethod = methodBodies.Any(body => index > body.Start && index < body.End);
            var kind = insideMethod
                ? CSharpSemanticTokenKind.Variable
                : CSharpSemanticTokenKind.Field;
            kinds[index] = kind;
            declarations[index] = true;
            if (kind == CSharpSemanticTokenKind.Field)
                declaredFieldNames.Add(NormalizeIdentifier(lexemes[index].Text));
        }
    }

    private static void ClassifyRemainingIdentifiers(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds,
        IReadOnlySet<string> declaredTypeNames,
        IReadOnlySet<string> declaredTypeParameterNames,
        IReadOnlySet<string> declaredParameterNames,
        IReadOnlySet<string> declaredPropertyNames,
        IReadOnlySet<string> declaredFieldNames)
    {
        for (var index = 0; index < lexemes.Count; index++)
        {
            if (lexemes[index].Kind != LexemeKind.Identifier || kinds[index].HasValue)
                continue;

            var word = NormalizeIdentifier(lexemes[index].Text);
            if (declaredTypeParameterNames.Contains(word))
            {
                kinds[index] = CSharpSemanticTokenKind.TypeParameter;
            }
            else if (declaredParameterNames.Contains(word))
            {
                kinds[index] = CSharpSemanticTokenKind.Parameter;
            }
            else if (declaredPropertyNames.Contains(word))
            {
                kinds[index] = CSharpSemanticTokenKind.Property;
            }
            else if (declaredFieldNames.Contains(word))
            {
                kinds[index] = CSharpSemanticTokenKind.Field;
            }
            else if (declaredTypeNames.Contains(word) ||
                IsTypeContext(lexemes, index) ||
                StartsWithUppercase(word))
            {
                kinds[index] = CSharpSemanticTokenKind.Type;
            }
            else if (index > 0 && lexemes[index - 1].Text is "." or "?.")
            {
                kinds[index] = CSharpSemanticTokenKind.Property;
            }
            else
            {
                kinds[index] = CSharpSemanticTokenKind.Variable;
            }
        }
    }

    private static List<Lexeme> Tokenize(IReadOnlyList<string?> sourceLines, int maxTokens)
    {
        var maxLexemes = Math.Max(4_096, checked(Math.Min(maxTokens, 100_000) * 32));
        var result = new List<Lexeme>(Math.Min(maxLexemes, 16_384));
        var inBlockComment = false;
        var stringMode = StringMode.None;
        var rawQuoteCount = 0;
        var ordinaryQuote = '\0';

        for (var lineIndex = 0; lineIndex < sourceLines.Count && result.Count < maxLexemes; lineIndex++)
        {
            var line = sourceLines[lineIndex] ?? string.Empty;
            for (var index = 0; index < line.Length && result.Count < maxLexemes;)
            {
                if (inBlockComment)
                {
                    var end = line.IndexOf("*/", index, StringComparison.Ordinal);
                    if (end < 0)
                        break;
                    inBlockComment = false;
                    index = end + 2;
                    continue;
                }

                if (stringMode == StringMode.Raw)
                {
                    var end = FindRawStringEnd(line, index, rawQuoteCount);
                    if (end < 0)
                        break;
                    stringMode = StringMode.None;
                    index = end;
                    continue;
                }

                if (stringMode == StringMode.Verbatim)
                {
                    var end = line.IndexOf('"', index);
                    if (end < 0)
                        break;
                    if (end + 1 < line.Length && line[end + 1] == '"')
                    {
                        index = end + 2;
                        continue;
                    }
                    stringMode = StringMode.None;
                    index = end + 1;
                    continue;
                }

                if (stringMode == StringMode.Ordinary)
                {
                    if (line[index] == '\\')
                    {
                        index = Math.Min(index + 2, line.Length);
                        continue;
                    }
                    if (line[index++] == ordinaryQuote)
                        stringMode = StringMode.None;
                    continue;
                }

                if (index + 1 < line.Length && line[index] == '/' && line[index + 1] == '/')
                    break;
                if (index + 1 < line.Length && line[index] == '/' && line[index + 1] == '*')
                {
                    inBlockComment = true;
                    index += 2;
                    continue;
                }

                var quoteCount = CountConsecutive(line, index, '"');
                if (quoteCount >= 3)
                {
                    stringMode = StringMode.Raw;
                    rawQuoteCount = quoteCount;
                    index += quoteCount;
                    continue;
                }

                if (StartsVerbatimString(line, index, out var prefixLength))
                {
                    stringMode = StringMode.Verbatim;
                    index += prefixLength;
                    continue;
                }

                if (line[index] is '\'' or '"')
                {
                    stringMode = StringMode.Ordinary;
                    ordinaryQuote = line[index++];
                    continue;
                }

                if (IsIdentifierStart(line[index]))
                {
                    var start = index++;
                    while (index < line.Length && IsIdentifierPart(line[index]))
                        index++;
                    result.Add(new Lexeme(
                        lineIndex,
                        start,
                        index - start,
                        line[start..index],
                        LexemeKind.Identifier));
                    continue;
                }

                if (char.IsDigit(line[index]))
                {
                    var start = index++;
                    while (index < line.Length &&
                        (char.IsLetterOrDigit(line[index]) || line[index] is '_' or '.'))
                    {
                        index++;
                    }
                    result.Add(new Lexeme(
                        lineIndex,
                        start,
                        index - start,
                        line[start..index],
                        LexemeKind.Number));
                    continue;
                }

                if (char.IsWhiteSpace(line[index]))
                {
                    index++;
                    continue;
                }

                var punctuationLength = index + 1 < line.Length &&
                    line.AsSpan(index, 2) is "=>" or "::" or "?." or "??"
                    ? 2
                    : 1;
                result.Add(new Lexeme(
                    lineIndex,
                    index,
                    punctuationLength,
                    line.Substring(index, punctuationLength),
                    LexemeKind.Punctuation));
                index += punctuationLength;
            }
        }

        return result;
    }

    private static bool IsDirectiveStart(IReadOnlyList<Lexeme> lexemes, int index)
    {
        if (index + 1 < lexemes.Count && lexemes[index + 1].Text == "(")
            return false;

        for (var previous = index - 1;
             previous >= 0 && lexemes[previous].Line == lexemes[index].Line;
             previous--)
        {
            if (NormalizeIdentifier(lexemes[previous].Text) != "global")
                return false;
        }

        return true;
    }

    private static void MarkQualifiedName(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds,
        int start,
        bool stopAtEquals)
    {
        var equals = -1;
        var end = start;
        while (end < lexemes.Count && lexemes[end].Text is not ";" and not "{")
        {
            if (lexemes[end].Text == "=")
                equals = end;
            end++;
        }

        var nameStart = stopAtEquals && equals >= 0 ? equals + 1 : start;
        for (var index = nameStart; index < end; index++)
        {
            if (lexemes[index].Kind == LexemeKind.Identifier &&
                NormalizeIdentifier(lexemes[index].Text) != "static")
            {
                kinds[index] = CSharpSemanticTokenKind.Namespace;
            }
        }
    }

    private static int FindNextUnclassifiedIdentifier(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds,
        int start)
    {
        for (var index = start; index < lexemes.Count; index++)
        {
            if (lexemes[index].Text is ";" or "{" or "}")
                return -1;
            if (lexemes[index].Kind == LexemeKind.Identifier &&
                !Keywords.Contains(NormalizeIdentifier(lexemes[index].Text)) &&
                !kinds[index].HasValue)
            {
                return index;
            }
        }
        return -1;
    }

    private static void MarkTypeParametersAfter(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds,
        bool[] declarations,
        int nameIndex,
        HashSet<string> declaredTypeParameterNames)
    {
        var open = nameIndex + 1;
        if (open >= lexemes.Count || lexemes[open].Text != "<")
            return;

        var close = FindMatching(lexemes, open, "<", ">");
        if (close < 0)
            return;

        for (var index = open + 1; index < close; index++)
        {
            if (lexemes[index].Kind != LexemeKind.Identifier ||
                Keywords.Contains(NormalizeIdentifier(lexemes[index].Text)))
            {
                continue;
            }
            kinds[index] = CSharpSemanticTokenKind.TypeParameter;
            declarations[index] = true;
            declaredTypeParameterNames.Add(NormalizeIdentifier(lexemes[index].Text));
        }
    }

    private static int MarkParameters(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds,
        bool[] declarations,
        int openParen,
        HashSet<string> declaredParameterNames)
    {
        var closeParen = FindMatching(lexemes, openParen, "(", ")");
        if (closeParen < 0)
            return -1;

        var segmentStart = openParen + 1;
        var nestedDepth = 0;
        for (var index = openParen + 1; index <= closeParen; index++)
        {
            var text = lexemes[index].Text;
            if (text is "(" or "[" or "<" or "{")
                nestedDepth++;
            else if (text is ")" or "]" or ">" or "}")
                nestedDepth--;

            if ((text == "," && nestedDepth == 0) || index == closeParen)
            {
                var parameterIndex = FindParameterName(lexemes, kinds, segmentStart, index);
                if (parameterIndex >= 0)
                {
                    kinds[parameterIndex] = CSharpSemanticTokenKind.Parameter;
                    declarations[parameterIndex] = true;
                    declaredParameterNames.Add(NormalizeIdentifier(lexemes[parameterIndex].Text));
                }
                segmentStart = index + 1;
            }
        }

        return closeParen;
    }

    private static int FindParameterName(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds,
        int start,
        int end)
    {
        var equals = end;
        for (var index = start; index < end; index++)
        {
            if (lexemes[index].Text == "=")
            {
                equals = index;
                break;
            }
        }

        for (var index = equals - 1; index >= start; index--)
        {
            if (lexemes[index].Kind != LexemeKind.Identifier)
                continue;
            var word = NormalizeIdentifier(lexemes[index].Text);
            if (!Keywords.Contains(word) && kinds[index] is not CSharpSemanticTokenKind.Modifier)
                return index;
        }

        return -1;
    }

    private static bool TryFindInvocationOpenParen(
        IReadOnlyList<Lexeme> lexemes,
        int nameIndex,
        out int openParen)
    {
        openParen = -1;
        var next = nameIndex + 1;
        if (next >= lexemes.Count)
            return false;
        if (lexemes[next].Text == "(")
        {
            openParen = next;
            return true;
        }
        if (lexemes[next].Text != "<")
            return false;

        var close = FindMatching(lexemes, next, "<", ">");
        if (close < 0 || close + 1 >= lexemes.Count || lexemes[close + 1].Text != "(")
            return false;
        openParen = close + 1;
        return true;
    }

    private static bool TryFindBodyRange(
        IReadOnlyList<Lexeme> lexemes,
        int closeParen,
        out (int Start, int End) body)
    {
        body = default;
        for (var index = closeParen + 1; index < lexemes.Count; index++)
        {
            if (lexemes[index].Text is ";" or "=>")
                return false;
            if (lexemes[index].Text != "{")
                continue;
            var close = FindMatching(lexemes, index, "{", "}");
            if (close < 0)
                return false;
            body = (index, close);
            return true;
        }
        return false;
    }

    private static bool ContainsPropertyAccessor(IReadOnlyList<Lexeme> lexemes, int openBrace)
    {
        var closeBrace = FindMatching(lexemes, openBrace, "{", "}");
        if (closeBrace < 0)
            return false;
        for (var index = openBrace + 1; index < closeBrace; index++)
        {
            if (NormalizeIdentifier(lexemes[index].Text) is "get" or "set" or "init" or "add" or "remove")
                return true;
        }
        return false;
    }

    private static bool IsDeclarationTypeBefore(
        IReadOnlyList<Lexeme> lexemes,
        CSharpSemanticTokenKind?[] kinds,
        int index)
    {
        if (index <= 0 || lexemes[index - 1].Text is "." or "?.")
            return false;

        var previous = index - 1;
        if (lexemes[previous].Text is ">" or "]" or "?")
            return true;
        if (lexemes[previous].Kind != LexemeKind.Identifier)
            return false;

        var word = NormalizeIdentifier(lexemes[previous].Text);
        return BuiltInTypeKeywords.Contains(word) ||
            word == "var" ||
            kinds[previous] is CSharpSemanticTokenKind.Type or
                CSharpSemanticTokenKind.Class or
                CSharpSemanticTokenKind.Enum or
                CSharpSemanticTokenKind.Interface or
                CSharpSemanticTokenKind.Struct or
                CSharpSemanticTokenKind.TypeParameter ||
            StartsWithUppercase(word);
    }

    private static bool HasDeclarationTerminatorAfter(IReadOnlyList<Lexeme> lexemes, int index)
    {
        if (index + 1 >= lexemes.Count)
            return false;
        return lexemes[index + 1].Text is "=" or ";" or "," or ")" or "[";
    }

    private static bool IsTypeContext(IReadOnlyList<Lexeme> lexemes, int index)
    {
        if (index <= 0)
            return false;
        var previous = NormalizeIdentifier(lexemes[index - 1].Text);
        return previous is "as" or "default" or "is" or "new" or "sizeof" or "typeof";
    }

    private static int FindMatching(
        IReadOnlyList<Lexeme> lexemes,
        int openIndex,
        string open,
        string close)
    {
        var depth = 0;
        for (var index = openIndex; index < lexemes.Count; index++)
        {
            if (lexemes[index].Text == open)
                depth++;
            else if (lexemes[index].Text == close && --depth == 0)
                return index;
        }
        return -1;
    }

    private static bool StartsVerbatimString(string line, int index, out int prefixLength)
    {
        prefixLength = 0;
        if (line[index] == '@' && index + 1 < line.Length && line[index + 1] == '"')
            prefixLength = 2;
        else if (index + 2 < line.Length &&
            ((line[index] == '$' && line[index + 1] == '@') ||
             (line[index] == '@' && line[index + 1] == '$')) &&
            line[index + 2] == '"')
        {
            prefixLength = 3;
        }
        return prefixLength > 0;
    }

    private static int CountConsecutive(string text, int start, char value)
    {
        var index = start;
        while (index < text.Length && text[index] == value)
            index++;
        return index - start;
    }

    private static int FindRawStringEnd(string line, int start, int quoteCount)
    {
        for (var index = start; index < line.Length; index++)
        {
            if (line[index] == '"' && CountConsecutive(line, index, '"') >= quoteCount)
                return index + quoteCount;
        }
        return -1;
    }

    private static bool IsIdentifierStart(char value) =>
        char.IsLetter(value) || value is '_' or '@';

    private static bool IsIdentifierPart(char value)
    {
        var category = char.GetUnicodeCategory(value);
        return char.IsLetterOrDigit(value) ||
            value == '_' ||
            category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.ConnectorPunctuation or
                UnicodeCategory.Format;
    }

    private static bool StartsWithUppercase(string value) =>
        value.Length > 0 && char.IsUpper(value[0]);

    private static string NormalizeIdentifier(string value) => value.TrimStart('@');

    private enum LexemeKind
    {
        Identifier,
        Number,
        Punctuation,
    }

    private enum StringMode
    {
        None,
        Ordinary,
        Verbatim,
        Raw,
    }

    private readonly record struct Lexeme(
        int Line,
        int Start,
        int Length,
        string Text,
        LexemeKind Kind);
}
