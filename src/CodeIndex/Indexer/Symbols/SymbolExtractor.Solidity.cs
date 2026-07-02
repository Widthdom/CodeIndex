using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex SolidityTypeDeclarationRegex = new(
        @"^\s*(?:abstract\s+)?(?<keyword>contract|interface|library)\s+(?<name>" + SolidityLanguageSupport.IdentifierPattern + @")\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityFunctionDeclarationRegex = new(
        @"^\s*function\s+(?<name>" + SolidityLanguageSupport.IdentifierPattern + @")\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityConstructorDeclarationRegex = new(
        @"^\s*(?<name>constructor)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityFallbackReceiveDeclarationRegex = new(
        @"^\s*(?<name>fallback|receive)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityEventDeclarationRegex = new(
        @"^\s*event\s+(?<name>" + SolidityLanguageSupport.IdentifierPattern + @")\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityErrorDeclarationRegex = new(
        @"^\s*error\s+(?<name>" + SolidityLanguageSupport.IdentifierPattern + @")\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityStructDeclarationRegex = new(
        @"^\s*struct\s+(?<name>" + SolidityLanguageSupport.IdentifierPattern + @")\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityEnumDeclarationRegex = new(
        @"^\s*enum\s+(?<name>" + SolidityLanguageSupport.IdentifierPattern + @")\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityModifierDeclarationRegex = new(
        @"^\s*modifier\s+(?<name>" + SolidityLanguageSupport.IdentifierPattern + @")\s*(?:\(|\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static List<SymbolRecord> ExtractSoliditySymbols(long fileId, string[] lines)
    {
        var matchLines = SolidityLanguageSupport.MaskCommentsAndStrings(lines);
        var symbols = new List<SymbolRecord>();

        for (var i = 0; i < matchLines.Length; i++)
        {
            var line = matchLines[i];
            if (MayContainSolidityTypeDeclaration(line))
            {
                var typeMatch = SolidityTypeDeclarationRegex.Match(line);
                if (typeMatch.Success)
                {
                    var keyword = typeMatch.Groups["keyword"].Value;
                    var kind = keyword == "interface" ? "interface" : "class";
                    AddSoliditySymbol(symbols, fileId, kind, keyword, typeMatch, lines[i], matchLines, i, BodyStyle.Brace);
                    continue;
                }
            }

            var functionMatch = SolidityFunctionDeclarationRegex.Match(line);
            if (functionMatch.Success)
            {
                AddSoliditySymbol(symbols, fileId, "function", "function", functionMatch, lines[i], matchLines, i, BodyStyle.Brace);
                continue;
            }

            var constructorMatch = SolidityConstructorDeclarationRegex.Match(line);
            if (constructorMatch.Success)
            {
                AddSoliditySymbol(symbols, fileId, "function", "constructor", constructorMatch, lines[i], matchLines, i, BodyStyle.Brace);
                continue;
            }

            var fallbackReceiveMatch = SolidityFallbackReceiveDeclarationRegex.Match(line);
            if (fallbackReceiveMatch.Success)
            {
                AddSoliditySymbol(symbols, fileId, "function", fallbackReceiveMatch.Groups["name"].Value, fallbackReceiveMatch, lines[i], matchLines, i, BodyStyle.Brace);
                continue;
            }

            var eventMatch = SolidityEventDeclarationRegex.Match(line);
            if (eventMatch.Success)
            {
                AddSoliditySymbol(symbols, fileId, "event", "event", eventMatch, lines[i], matchLines, i, BodyStyle.None);
                continue;
            }

            var errorMatch = SolidityErrorDeclarationRegex.Match(line);
            if (errorMatch.Success)
            {
                AddSoliditySymbol(symbols, fileId, "type", "error", errorMatch, lines[i], matchLines, i, BodyStyle.None);
                continue;
            }

            if (line.Contains("struct", StringComparison.Ordinal))
            {
                var structMatch = SolidityStructDeclarationRegex.Match(line);
                if (structMatch.Success)
                {
                    AddSoliditySymbol(symbols, fileId, "struct", "struct", structMatch, lines[i], matchLines, i, BodyStyle.Brace);
                    continue;
                }
            }

            if (line.Contains("enum", StringComparison.Ordinal))
            {
                var enumMatch = SolidityEnumDeclarationRegex.Match(line);
                if (enumMatch.Success)
                {
                    AddSoliditySymbol(symbols, fileId, "enum", "enum", enumMatch, lines[i], matchLines, i, BodyStyle.Brace);
                    continue;
                }
            }

            var modifierMatch = SolidityModifierDeclarationRegex.Match(line);
            if (modifierMatch.Success)
                AddSoliditySymbol(symbols, fileId, "function", "modifier", modifierMatch, lines[i], matchLines, i, BodyStyle.Brace);
        }

        AssignContainers(symbols, lines, null);
        PopulateDeclaredContainerQualifiedNames(symbols);
        return symbols;
    }

    private static bool MayContainSolidityTypeDeclaration(string line) =>
        line.Contains("contract", StringComparison.Ordinal)
        || line.Contains("interface", StringComparison.Ordinal)
        || line.Contains("library", StringComparison.Ordinal);

    private static void AddSoliditySymbol(
        List<SymbolRecord> symbols,
        long fileId,
        string kind,
        string subKind,
        Match match,
        string rawLine,
        string[] matchLines,
        int lineIndex,
        BodyStyle bodyStyle)
    {
        var name = match.Groups["name"];
        var (endLine, bodyStartLine, bodyEndLine) = ResolveRange(matchLines, lineIndex, bodyStyle, "solidity", match.Index);
        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = kind,
            SubKind = subKind,
            Name = name.Value,
            Line = lineIndex + 1,
            StartLine = lineIndex + 1,
            StartColumn = name.Index,
            EndLine = endLine,
            BodyStartLine = bodyStartLine,
            BodyEndLine = bodyEndLine,
            Signature = rawLine.Trim(),
        });
    }
}
