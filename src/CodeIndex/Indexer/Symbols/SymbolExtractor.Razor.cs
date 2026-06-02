using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex RazorPageDirectiveRegex = new(
        @"^\s*@page\s+""(?<route>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RazorImplementsDirectiveRegex = new(
        @"^\s*@implements\s+(?<type>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RazorAttributeDirectiveRegex = new(
        @"^\s*@attribute\s+\[\s*(?<type>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RazorLayoutDirectiveRegex = new(
        @"^\s*@layout\s+(?<type>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsRazorFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".razor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".cshtml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRazorLanguage(string? lang)
        => string.Equals(lang, "razor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lang, "blazor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lang, "cshtml", StringComparison.OrdinalIgnoreCase);

    private static void ExtractRazorDirectiveSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            TryAddRazorDirectiveSymbol(fileId, symbols, line, i + 1, RazorPageDirectiveRegex, "route", "route");
            TryAddRazorDirectiveSymbol(fileId, symbols, line, i + 1, RazorImplementsDirectiveRegex, "implements", "type");
            TryAddRazorDirectiveSymbol(fileId, symbols, line, i + 1, RazorAttributeDirectiveRegex, "attribute", "type");
            TryAddRazorDirectiveSymbol(fileId, symbols, line, i + 1, RazorLayoutDirectiveRegex, "layout", "type");
        }
    }

    private static void TryAddRazorDirectiveSymbol(
        long fileId,
        List<SymbolRecord> symbols,
        string line,
        int lineNumber,
        Regex regex,
        string kind,
        string nameGroup)
    {
        var match = regex.Match(line);
        if (!match.Success)
            return;

        var group = match.Groups[nameGroup];
        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = kind,
                Name = group.Value,
                Line = lineNumber,
                StartLine = lineNumber,
                StartColumn = group.Index,
                EndLine = lineNumber,
                Signature = line.Trim(),
            },
            line);
    }
}
