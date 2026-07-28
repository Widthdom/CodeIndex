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

    private static LspProtocolKinds LspKinds(SymbolResult symbol)
    {
        if (IsConstructorSymbol(symbol))
            return new LspProtocolKinds(SymbolKind: 9, CompletionItemKind: 4);
        if (symbol.Kind == "enum" && symbol.ContainerKind == "enum")
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
            string.IsNullOrWhiteSpace(symbol.Signature) ||
            !string.Equals(symbol.Name, symbol.ContainerName, StringComparison.Ordinal))
        {
            return false;
        }

        var signature = symbol.Signature;
        var searchStart = 0;
        while (searchStart < signature.Length)
        {
            var nameStart = signature.IndexOf(symbol.Name, searchStart, StringComparison.Ordinal);
            if (nameStart < 0)
                return false;

            var nameEnd = nameStart + symbol.Name.Length;
            var hasIdentifierBoundaryBefore = nameStart == 0 || !IsIdentifierCharacter(signature[nameStart - 1]);
            var hasIdentifierBoundaryAfter = nameEnd == signature.Length || !IsIdentifierCharacter(signature[nameEnd]);
            if (hasIdentifierBoundaryBefore && hasIdentifierBoundaryAfter)
            {
                var before = nameStart - 1;
                while (before >= 0 && char.IsWhiteSpace(signature[before]))
                    before--;
                if (before < 0 || signature[before] != '~')
                {
                    var after = nameEnd;
                    while (after < signature.Length && char.IsWhiteSpace(signature[after]))
                        after++;
                    if (after < signature.Length && signature[after] == '(')
                        return true;
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
