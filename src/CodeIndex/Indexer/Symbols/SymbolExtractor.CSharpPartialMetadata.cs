using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private const int CSharpLeadingDeclarationLookbackLines = 64;
    private static readonly HashSet<string> CSharpStandaloneDeclarationModifiers = new(StringComparer.Ordinal)
    {
        "public", "protected", "internal", "private", "file", "new", "static", "abstract",
        "sealed", "virtual", "override", "readonly", "unsafe", "extern", "partial", "async",
        "ref", "required",
    };

    private static void PopulateCSharpPartialDeclarationMetadata(
        IReadOnlyList<string> lines,
        IReadOnlyList<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("function" or "class" or "struct" or "interface" or "record"))
                continue;

            var signature = symbol.Signature ?? string.Empty;
            var leading = ReadCSharpLeadingDeclarationEvidence(lines, symbol.StartLine);
            symbol.IsPartialDeclaration = PartialModifierRegex.IsMatch(signature) || leading.HasPartialModifier;

            var semanticScore = 0;
            if (signature.Contains('[') || leading.HasAttribute)
                semanticScore += 2;
            if (signature.Contains("///", StringComparison.Ordinal) || leading.HasDocumentation)
                semanticScore += 1;
            if (symbol.Kind is "class" or "struct" or "interface" or "record"
                && signature.Contains(':', StringComparison.Ordinal))
            {
                semanticScore += 4;
            }
            if (signature.Contains(" where ", StringComparison.Ordinal))
                semanticScore += 1;
            symbol.DeclarationSemanticScore = semanticScore;
        }
    }

    private static CSharpLeadingDeclarationEvidence ReadCSharpLeadingDeclarationEvidence(
        IReadOnlyList<string> lines,
        int declarationStartLine)
    {
        var lineIndex = Math.Min(lines.Count, Math.Max(1, declarationStartLine)) - 2;
        var minimumLineIndex = Math.Max(0, lineIndex - CSharpLeadingDeclarationLookbackLines + 1);
        var hasPartialModifier = false;
        var hasAttribute = false;
        var hasDocumentation = false;
        var attributeDepth = 0;

        for (; lineIndex >= minimumLineIndex; lineIndex--)
        {
            var trimmed = lines[lineIndex].AsSpan().Trim();
            if (trimmed.IsEmpty)
                break;

            if (trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                hasDocumentation = true;
                continue;
            }

            if (attributeDepth > 0 || trimmed[0] == '[' || trimmed[^1] == ']')
            {
                hasAttribute = true;
                attributeDepth += CountCharacter(trimmed, ']') - CountCharacter(trimmed, '[');
                attributeDepth = Math.Max(0, attributeDepth);
                continue;
            }

            if (!TryReadStandaloneCSharpModifiers(trimmed, out var hasPartial))
                break;

            hasPartialModifier |= hasPartial;
        }

        return new CSharpLeadingDeclarationEvidence(
            hasPartialModifier,
            hasAttribute,
            hasDocumentation);
    }

    private static bool TryReadStandaloneCSharpModifiers(ReadOnlySpan<char> line, out bool hasPartial)
    {
        hasPartial = false;
        var remaining = line;
        var found = false;
        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOfAny(' ', '\t');
            var token = separator < 0 ? remaining : remaining[..separator];
            if (!CSharpStandaloneDeclarationModifiers.Contains(token.ToString()))
                return false;

            found = true;
            hasPartial |= token.SequenceEqual("partial");
            if (separator < 0)
                break;
            remaining = remaining[(separator + 1)..].TrimStart();
        }
        return found;
    }

    private static int CountCharacter(ReadOnlySpan<char> text, char value)
    {
        var count = 0;
        foreach (var character in text)
        {
            if (character == value)
                count++;
        }
        return count;
    }

    private readonly record struct CSharpLeadingDeclarationEvidence(
        bool HasPartialModifier,
        bool HasAttribute,
        bool HasDocumentation);
}
