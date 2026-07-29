using CodeIndex.Database;

namespace CodeIndex.Lsp;

internal sealed partial class LspServer
{
    private readonly record struct LspProtocolKinds(int SymbolKind, int CompletionItemKind);

    private static readonly LspProtocolKinds FallbackLspProtocolKinds = new(
        SymbolKind: 13,         // Variable
        CompletionItemKind: 6); // Variable

    // Keep every persisted symbol kind in one ordinal table so document/workspace symbols and
    // completion cannot drift. Kinds without an exact LSP peer use the closest documented shape.
    private static readonly IReadOnlyDictionary<string, LspProtocolKinds> LspProtocolKindsByInternalKind =
        new Dictionary<string, LspProtocolKinds>(StringComparer.Ordinal)
        {
            ["accessor"] = new(6, 2),          // Method
            ["add"] = new(12, 3),              // Function
            ["anchor"] = new(20, 18),          // Key / Reference
            ["annotation"] = new(11, 8),       // Interface
            ["assembly"] = new(2, 9),          // Module
            ["array"] = new(18, 12),           // Array / Value
            ["async_function"] = new(12, 3),   // Function
            ["async_generator"] = new(12, 3),  // Function
            ["attribute"] = new(7, 10),        // Property
            ["associatedtype"] = new(26, 25),  // TypeParameter
            ["base_image"] = new(5, 7),        // Class
            ["build_arg"] = new(13, 6),        // Variable
            ["class"] = new(5, 7),             // Class
            ["class_hook"] = new(6, 2),        // Method
            ["code"] = new(15, 1),             // String / Text
            ["constant"] = new(14, 21),        // Constant
            ["copy"] = new(12, 3),             // Function
            ["delegate"] = new(12, 3),         // Function
            ["enum"] = new(10, 13),            // Enum
            ["environment"] = new(13, 6),      // Variable
            ["event"] = new(24, 23),           // Event
            ["expose"] = new(7, 10),           // Property
            ["field"] = new(8, 5),             // Field
            ["file_module"] = new(2, 9),       // Module
            ["function"] = new(12, 3),         // Function
            ["generator"] = new(12, 3),        // Function
            ["heading"] = new(20, 1),          // Key / Text
            ["hook"] = new(12, 3),             // Function
            ["implements"] = new(11, 8),       // Interface
            ["import"] = new(2, 9),            // Module
            ["interface"] = new(11, 8),        // Interface
            ["lambda"] = new(12, 3),           // Function
            ["label"] = new(20, 1),            // Key / Text
            ["layout"] = new(19, 7),           // Object / Class
            ["method"] = new(6, 2),            // Method
            ["module"] = new(2, 9),            // Module
            ["namespace"] = new(3, 9),         // Namespace / Module
            ["operator"] = new(25, 24),        // Operator
            ["object"] = new(19, 7),           // Object / Class
            ["package"] = new(4, 9),           // Package / Module
            ["property"] = new(7, 10),         // Property
            ["procedure"] = new(12, 3),        // Function
            ["program"] = new(2, 9),           // Module
            ["project"] = new(2, 9),           // Module
            ["protocol"] = new(11, 8),         // Interface
            ["protocol_impl"] = new(19, 7),     // Object / Class
            ["reference"] = new(13, 18),       // Variable / Reference
            ["record"] = new(23, 22),          // Struct
            ["rule"] = new(19, 7),             // Object / Class
            ["route"] = new(12, 3),            // Function
            ["run"] = new(12, 3),              // Function
            ["service"] = new(5, 7),           // Class
            ["shell"] = new(12, 3),            // Function
            ["specialization"] = new(5, 7),    // Class
            ["stage"] = new(2, 9),             // Module
            ["stopsignal"] = new(7, 10),       // Property
            ["struct"] = new(23, 22),          // Struct
            ["submodule"] = new(2, 9),         // Module
            ["subroutine"] = new(12, 3),       // Function
            ["test.method"] = new(6, 2),       // Method
            ["trait"] = new(11, 8),            // Interface
            ["type"] = new(5, 7),              // Class
            ["type_parameter"] = new(26, 25),  // TypeParameter
            ["typealias"] = new(26, 25),       // TypeParameter
            ["union"] = new(23, 22),           // Struct
            ["user"] = new(13, 6),             // Variable
            ["value"] = new(13, 12),           // Variable / Value
            ["block data"] = new(19, 7),       // Object / Class
            ["variable"] = new(13, 6),         // Variable
            ["volume"] = new(8, 5),            // Field
            ["workdir"] = new(2, 19),          // Module / Folder
        };

    internal static IEnumerable<string> MappedInternalKindsForTesting
        => LspProtocolKindsByInternalKind.Keys;

    internal static (int SymbolKind, int CompletionItemKind) MapLspKindsForTesting(string kind)
    {
        var kinds = LspKinds(kind);
        return (kinds.SymbolKind, kinds.CompletionItemKind);
    }

    internal static (int SymbolKind, int CompletionItemKind) MapLspKindsForTesting(SymbolResult symbol)
    {
        var kinds = LspKinds(symbol);
        return (kinds.SymbolKind, kinds.CompletionItemKind);
    }

    private static LspProtocolKinds LspKinds(SymbolResult symbol)
    {
        if (IsConstructorSymbol(symbol))
            return new LspProtocolKinds(SymbolKind: 9, CompletionItemKind: 4);
        if (IsEnumMemberSymbol(symbol))
            return new LspProtocolKinds(SymbolKind: 22, CompletionItemKind: 20);
        return LspKinds(symbol.Kind);
    }

    private static LspProtocolKinds LspKinds(string kind)
        => LspProtocolKindsByInternalKind.TryGetValue(kind, out var kinds)
            ? kinds
            : FallbackLspProtocolKinds;

    private static bool IsConstructorSymbol(SymbolResult symbol)
    {
        if (symbol.Kind is not ("function" or "method") ||
            string.IsNullOrEmpty(symbol.Name) ||
            string.IsNullOrWhiteSpace(symbol.Signature))
        {
            return false;
        }

        if (string.Equals(symbol.SubKind, "constructor", StringComparison.Ordinal))
            return true;

        var startsWithConstructorKeyword = SignatureStartsWithKeywordAfterModifiers(
            symbol.Signature,
            "constructor",
            ignoreCase: symbol.Lang == "pascal");
        if (startsWithConstructorKeyword && symbol.Lang == "pascal")
            return true;
        if (startsWithConstructorKeyword &&
            symbol.Lang is "javascript" or "typescript" or "kotlin" &&
            !string.IsNullOrWhiteSpace(symbol.ContainerName) &&
            string.Equals(symbol.Name, symbol.ContainerName, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(symbol.ContainerName))
            return false;

        var usesContainerName = symbol.Lang is "csharp" or "cpp" or "dart" or "groovy" or "java";
        var matchesContainerName = string.Equals(symbol.Name, symbol.ContainerName, StringComparison.Ordinal) ||
            (symbol.Lang == "dart" &&
             symbol.Name.StartsWith(symbol.ContainerName + ".", StringComparison.Ordinal));
        if (usesContainerName &&
            matchesContainerName &&
            string.IsNullOrWhiteSpace(symbol.ReturnType))
        {
            return SignatureContainsNamedDeclaration(
                symbol.Signature,
                symbol.Name,
                allowBodyBrace: symbol.Lang == "java");
        }

        var usesDedicatedName = symbol.Lang switch
        {
            "php" => symbol.Name == "__construct",
            "python" => symbol.Name == "__init__",
            "ruby" => symbol.Name == "initialize",
            "scala" => symbol.Name == "this",
            "swift" => symbol.Name == "init",
            "vb" => symbol.Name.Equals("New", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        return usesDedicatedName &&
            SignatureContainsNamedDeclaration(
                symbol.Signature,
                symbol.Name,
                allowBodyBrace: false,
                ignoreCase: symbol.Lang == "vb");
    }

    private static bool IsEnumMemberSymbol(SymbolResult symbol)
    {
        if (symbol.ContainerKind != "enum" ||
            string.IsNullOrEmpty(symbol.Name) ||
            string.IsNullOrWhiteSpace(symbol.Signature))
        {
            return false;
        }

        if (symbol.Kind == "enum" &&
            SignatureStartsWithKeywordAfterModifiers(symbol.Signature, "enum"))
        {
            return false;
        }
        if (symbol.Kind is not ("function" or "property"))
            return symbol.Kind == "enum" &&
                SignatureContainsEnumMemberDeclarator(symbol.Signature, symbol.Name, symbol.Lang);

        return SignatureContainsEnumMemberDeclarator(symbol.Signature, symbol.Name, symbol.Lang);
    }

    private static bool SignatureStartsWithKeywordAfterModifiers(
        string signature,
        string keyword,
        bool ignoreCase = false)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var remaining = signature.AsSpan().TrimStart();
        while (!remaining.IsEmpty)
        {
            if (TryConsumeLeadingDecoration(ref remaining))
                continue;
            if (TryConsumeLeadingKeyword(ref remaining, keyword, comparison))
                return true;

            var wordEnd = 0;
            while (wordEnd < remaining.Length && IsIdentifierCharacter(remaining[wordEnd]))
                wordEnd++;
            if (wordEnd == 0 || !IsDeclarationModifier(remaining[..wordEnd], comparison))
                return false;
            remaining = remaining[wordEnd..].TrimStart();
        }

        return false;
    }

    private static bool TryConsumeLeadingKeyword(
        ref ReadOnlySpan<char> text,
        string keyword,
        StringComparison comparison = StringComparison.Ordinal)
    {
        if (!text.StartsWith(keyword, comparison) ||
            (text.Length > keyword.Length && IsIdentifierCharacter(text[keyword.Length])))
        {
            return false;
        }

        text = text[keyword.Length..].TrimStart();
        return true;
    }

    private static bool IsDeclarationModifier(
        ReadOnlySpan<char> word,
        StringComparison comparison)
        => word.Equals("abstract", comparison) ||
           word.Equals("actual", comparison) ||
           word.Equals("base", comparison) ||
           word.Equals("class", comparison) ||
           word.Equals("declare", comparison) ||
           word.Equals("default", comparison) ||
           word.Equals("expect", comparison) ||
           word.Equals("export", comparison) ||
           word.Equals("external", comparison) ||
           word.Equals("fileprivate", comparison) ||
           word.Equals("final", comparison) ||
           word.Equals("indirect", comparison) ||
           word.Equals("interface", comparison) ||
           word.Equals("internal", comparison) ||
           word.Equals("open", comparison) ||
           word.Equals("package", comparison) ||
           word.Equals("partial", comparison) ||
           word.Equals("private", comparison) ||
           word.Equals("protected", comparison) ||
           word.Equals("public", comparison) ||
           word.Equals("readonly", comparison) ||
           word.Equals("sealed", comparison) ||
           word.Equals("static", comparison);

    private static bool SignatureContainsEnumMemberDeclarator(
        string signature,
        string name,
        string? lang)
    {
        var verbatimIdentifierName = lang == "csharp" ? name : null;
        var remaining = signature.AsSpan().TrimStart();
        while (TryConsumeLeadingDecoration(ref remaining, verbatimIdentifierName))
        {
        }
        TryConsumeLeadingKeyword(ref remaining, "indirect");
        TryConsumeLeadingKeyword(ref remaining, "case");

        var segmentStart = 0;
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index <= remaining.Length; index++)
        {
            if (index == remaining.Length)
                return SegmentStartsWithEnumMemberName(
                    remaining[segmentStart..],
                    name,
                    verbatimIdentifierName);

            var character = remaining[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == quote)
                    quote = '\0';
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            switch (character)
            {
                case '(':
                    parenthesisDepth++;
                    break;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    break;
                case ',' when parenthesisDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    if (SegmentStartsWithEnumMemberName(
                        remaining[segmentStart..index],
                        name,
                        verbatimIdentifierName))
                    {
                        return true;
                    }
                    segmentStart = index + 1;
                    break;
                case ';' when parenthesisDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return SegmentStartsWithEnumMemberName(
                        remaining[segmentStart..index],
                        name,
                        verbatimIdentifierName);
            }
        }

        return false;
    }

    private static bool SegmentStartsWithEnumMemberName(
        ReadOnlySpan<char> segment,
        string name,
        string? verbatimIdentifierName)
    {
        segment = segment.TrimStart();
        while (TryConsumeLeadingDecoration(ref segment, verbatimIdentifierName))
        {
        }

        ReadOnlySpan<char> candidate;
        if (!segment.IsEmpty && segment[0] == '`')
        {
            var closingBacktick = segment[1..].IndexOf('`');
            if (closingBacktick < 0)
                return false;
            candidate = segment.Slice(1, closingBacktick);
            segment = segment[(closingBacktick + 2)..];
        }
        else
        {
            if (!segment.IsEmpty && segment[0] == '@')
                segment = segment[1..];
            var nameEnd = 0;
            while (nameEnd < segment.Length && IsIdentifierCharacter(segment[nameEnd]))
                nameEnd++;
            if (nameEnd == 0)
                return false;
            candidate = segment[..nameEnd];
            segment = segment[nameEnd..];
        }

        if (!candidate.Equals(name, StringComparison.Ordinal))
            return false;
        segment = segment.TrimStart();
        return segment.IsEmpty || segment[0] is '(' or '{' or '=';
    }

    private static bool TryConsumeLeadingDecoration(
        ref ReadOnlySpan<char> text,
        string? verbatimIdentifierName = null)
    {
        var original = text;
        if (!text.IsEmpty && text[0] == '[')
        {
            if (!TryConsumeBalanced(ref text, '[', ']'))
                return false;
            text = text.TrimStart();
            return !text.IsEmpty;
        }

        if (text.IsEmpty || text[0] != '@')
            return false;

        if (!string.IsNullOrEmpty(verbatimIdentifierName) &&
            text[1..].StartsWith(verbatimIdentifierName, StringComparison.Ordinal))
        {
            var identifierEnd = verbatimIdentifierName.Length + 1;
            var hasBoundaryAfter = identifierEnd == text.Length ||
                !IsIdentifierCharacter(text[identifierEnd]);
            var suffix = text[identifierEnd..].TrimStart();
            if (hasBoundaryAfter &&
                (suffix.IsEmpty || suffix[0] is '(' or '{' or '=' or ',' or ';'))
            {
                return false;
            }
        }

        var index = 1;
        while (index < text.Length &&
               (IsIdentifierCharacter(text[index]) || text[index] is '.' or ':'))
        {
            index++;
        }
        if (index == 1)
            return false;

        var hadWhitespace = index < text.Length && char.IsWhiteSpace(text[index]);
        text = text[index..].TrimStart();
        if (!text.IsEmpty && text[0] == '(')
        {
            if (!TryConsumeBalanced(ref text, '(', ')'))
            {
                text = original;
                return false;
            }
            text = text.TrimStart();
            return !text.IsEmpty;
        }

        if (hadWhitespace && !text.IsEmpty)
            return true;

        text = original;
        return false;
    }

    private static bool TryConsumeBalanced(
        ref ReadOnlySpan<char> text,
        char opening,
        char closing)
    {
        var depth = 0;
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == quote)
                    quote = '\0';
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }
            if (character == opening)
                depth++;
            else if (character == closing && --depth == 0)
            {
                text = text[(index + 1)..];
                return true;
            }
        }

        return false;
    }

    private static bool SignatureContainsNamedDeclaration(
        string signature,
        string name,
        bool allowBodyBrace,
        bool ignoreCase = false)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var searchStart = 0;
        while (searchStart < signature.Length)
        {
            var nameStart = signature.IndexOf(name, searchStart, comparison);
            if (nameStart < 0)
                return false;

            var nameEnd = nameStart + name.Length;
            var hasVerbatimPrefix = nameStart > 0 &&
                signature[nameStart - 1] == '@' &&
                (nameStart == 1 || !IsIdentifierCharacter(signature[nameStart - 2]));
            var hasIdentifierBoundaryBefore = nameStart == 0 ||
                hasVerbatimPrefix ||
                !IsIdentifierCharacter(signature[nameStart - 1]);
            var hasIdentifierBoundaryAfter = nameEnd == signature.Length || !IsIdentifierCharacter(signature[nameEnd]);
            if (hasIdentifierBoundaryBefore && hasIdentifierBoundaryAfter)
            {
                var before = nameStart - (hasVerbatimPrefix ? 2 : 1);
                while (before >= 0 && char.IsWhiteSpace(signature[before]))
                    before--;
                if (before < 0 || signature[before] != '~')
                {
                    var after = nameEnd;
                    while (after < signature.Length && char.IsWhiteSpace(signature[after]))
                        after++;
                    if (after < signature.Length &&
                        (signature[after] == '(' || (allowBodyBrace && signature[after] == '{')))
                    {
                        return true;
                    }
                }
            }

            searchStart = nameEnd;
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char character)
        => char.IsLetterOrDigit(character) || character is '_' or '@';

    private static int SymbolKind(SymbolResult symbol) => LspKinds(symbol).SymbolKind;

    private static int CompletionItemKind(SymbolResult symbol) => LspKinds(symbol).CompletionItemKind;
}
