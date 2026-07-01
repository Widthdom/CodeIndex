using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex CSharpRegionRegex = new(
        @"^\s*#region(?:\s+(?<name>.*\S))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JavaScriptTypeScriptModuleDocRegex = new(
        @"@module(?:\s+(?<name>[^\s*]+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void ExtractSectionHeadingSymbols(long fileId, string lang, string[] lines, List<SymbolRecord> symbols)
    {
        if (lang == "csharp")
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("#region", StringComparison.Ordinal) < 0)
                    continue;

                var match = CSharpRegionRegex.Match(lines[i]);
                if (!match.Success)
                    continue;

                AddHeadingSymbol(fileId, lines, symbols, i, match.Groups["name"].Value.Trim(), "#region");
            }
        }
        else if (lang == "python")
        {
            TryAddPythonModuleDocstringHeading(fileId, lines, symbols);
        }
        else
        {
            if (!LinesContain(lines, "@module", StringComparison.Ordinal))
                return;

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("@module", StringComparison.Ordinal) < 0)
                    continue;

                var match = JavaScriptTypeScriptModuleDocRegex.Match(lines[i]);
                if (!match.Success)
                    continue;

                AddHeadingSymbol(fileId, lines, symbols, i, match.Groups["name"].Value.Trim(), "@module");
            }
        }
    }

    private static void TryAddPythonModuleDocstringHeading(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                continue;

            var quote = trimmed.StartsWith("\"\"\"", StringComparison.Ordinal) ? "\"\"\"" :
                trimmed.StartsWith("'''", StringComparison.Ordinal) ? "'''" : null;
            if (quote == null)
                return;

            var name = trimmed[quote.Length..].Trim();
            if (name.EndsWith(quote, StringComparison.Ordinal))
                name = name[..^quote.Length].Trim();
            AddHeadingSymbol(fileId, lines, symbols, i, name, "module docstring");
            return;
        }
    }

    private static void AddHeadingSymbol(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        int lineIndex,
        string name,
        string fallbackName)
    {
        var lineNumber = lineIndex + 1;
        AddSymbolRecord(
            symbols,
            null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "heading",
                Name = string.IsNullOrWhiteSpace(name) ? fallbackName : name,
                Line = lineNumber,
                StartLine = lineNumber,
                EndLine = lineNumber,
                Signature = lines[lineIndex].Trim(),
            },
            lines[lineIndex]);
    }
}
