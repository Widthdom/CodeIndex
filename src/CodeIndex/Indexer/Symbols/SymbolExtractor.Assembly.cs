using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex AssemblyLabelRegex = new(
        @"^\s*(?<name>[A-Za-z_.$?@][A-Za-z0-9_.$?@]*)\s*:(?!:)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AssemblyProcRegex = new(
        @"^\s*(?<name>[A-Za-z_.$?@][A-Za-z0-9_.$?@]*)\s+PROC\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AssemblySegmentRegex = new(
        @"^\s*(?<name>[A-Za-z_.$?@][A-Za-z0-9_.$?@]*)\s+SEGMENT\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AssemblyNamedSectionRegex = new(
        @"^\s*(?:section|\.section|segment)\s+(?<name>[^,\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AssemblyBareSectionRegex = new(
        @"^\s*(?<name>\.(?:text|data|bss|rodata|rdata|const|code))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AssemblyExternRegex = new(
        @"^\s*(?:extern|extrn|import)\s+(?<name>[A-Za-z_.$?@][A-Za-z0-9_.$?@]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AssemblyIncludeRegex = new(
        @"^\s*(?:%include|#include|include|\.include)\s+[""']?(?<name>[^""'\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AssemblyMacroRegex = new(
        @"^\s*(?:(?:%macro|\.macro)\s+(?<name1>[A-Za-z_.$?@][A-Za-z0-9_.$?@]*)|(?<name2>[A-Za-z_.$?@][A-Za-z0-9_.$?@]*)\s+MACRO\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AssemblyConstantRegex = new(
        @"^\s*(?:(?:%define|#define|\.equ|\.set)\s+(?<name1>[A-Za-z_.$?@][A-Za-z0-9_.$?@]*)|(?<name2>[A-Za-z_.$?@][A-Za-z0-9_.$?@]*)\s+(?:equ\b|=))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static List<SymbolRecord> ExtractAssemblySymbols(long fileId, string[] lines)
    {
        var symbols = new List<SymbolRecord>();
        var functionSymbols = new List<SymbolRecord>();
        var sectionSymbols = new List<SymbolRecord>();

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var rawLine = lines[i];
            var codeLine = StripAssemblyComment(rawLine);
            if (string.IsNullOrWhiteSpace(codeLine))
                continue;

            if (TryAddAssemblySectionSymbol(fileId, symbols, sectionSymbols, codeLine, lineNumber, rawLine))
                continue;

            if (TryAddAssemblyFunctionSymbol(fileId, symbols, functionSymbols, codeLine, lineNumber, rawLine))
                continue;

            if (TryAddAssemblyMacroSymbol(fileId, symbols, functionSymbols, codeLine, lineNumber, rawLine))
                continue;

            if (TryAddAssemblyImportSymbol(fileId, symbols, codeLine, lineNumber, rawLine))
                continue;

            TryAddAssemblyConstantSymbol(fileId, symbols, codeLine, lineNumber, rawLine);
        }

        AssignAssemblyRanges(functionSymbols, sectionSymbols, lines.Length);
        AssignContainers(symbols, lines, null);
        PopulateDeclaredContainerQualifiedNames(symbols);
        return symbols;
    }

    internal static string StripAssemblyComment(string line, bool preserveHashImmediates = false)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < line.Length && char.IsWhiteSpace(line[firstNonWhitespace]))
            firstNonWhitespace++;

        if (firstNonWhitespace < line.Length && line[firstNonWhitespace] == '@')
            return line[..firstNonWhitespace];

        var preserveLeadingHashDirective = firstNonWhitespace < line.Length
            && line[firstNonWhitespace] == '#'
            && (line[firstNonWhitespace..].StartsWith("#include", StringComparison.OrdinalIgnoreCase)
                || line[firstNonWhitespace..].StartsWith("#define", StringComparison.OrdinalIgnoreCase));

        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (inSingleQuote || inDoubleQuote)
                continue;

            if (c == '#')
            {
                if (i == firstNonWhitespace && preserveLeadingHashDirective)
                    continue;
                if (preserveHashImmediates && IsAssemblyHashImmediate(line, i))
                    continue;
                return line[..i];
            }

            if (c == ';')
                return line[..i];

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                return line[..i];
        }

        return line;
    }

    private static bool IsAssemblyHashImmediate(string line, int hashIndex)
    {
        var previous = hashIndex - 1;
        while (previous >= 0 && char.IsWhiteSpace(line[previous]))
            previous--;
        if (previous < 0 || line[previous] != ',')
            return false;

        var next = hashIndex + 1;
        return next < line.Length && !char.IsWhiteSpace(line[next]);
    }

    private static bool TryAddAssemblySectionSymbol(
        long fileId,
        List<SymbolRecord> symbols,
        List<SymbolRecord> sectionSymbols,
        string codeLine,
        int lineNumber,
        string rawLine)
    {
        var match = AssemblyNamedSectionRegex.Match(codeLine);
        if (!match.Success)
            match = AssemblyBareSectionRegex.Match(codeLine);
        if (!match.Success)
            match = AssemblySegmentRegex.Match(codeLine);
        if (!match.Success)
            return false;

        var name = match.Groups["name"].Value;
        var symbol = CreateAssemblySymbol(fileId, "namespace", name, lineNumber, match.Groups["name"].Index, codeLine);
        AddSymbolRecord(symbols, null, lineNumber, symbol, rawLine);
        sectionSymbols.Add(symbol);
        return true;
    }

    private static bool TryAddAssemblyFunctionSymbol(
        long fileId,
        List<SymbolRecord> symbols,
        List<SymbolRecord> functionSymbols,
        string codeLine,
        int lineNumber,
        string rawLine)
    {
        var match = AssemblyLabelRegex.Match(codeLine);
        if (!match.Success)
            match = AssemblyProcRegex.Match(codeLine);
        if (!match.Success)
            return false;

        var name = match.Groups["name"].Value;
        var symbol = CreateAssemblySymbol(fileId, "function", name, lineNumber, match.Groups["name"].Index, codeLine);
        AddSymbolRecord(symbols, null, lineNumber, symbol, rawLine);
        functionSymbols.Add(symbol);
        return true;
    }

    private static bool TryAddAssemblyMacroSymbol(
        long fileId,
        List<SymbolRecord> symbols,
        List<SymbolRecord> functionSymbols,
        string codeLine,
        int lineNumber,
        string rawLine)
    {
        var match = AssemblyMacroRegex.Match(codeLine);
        if (!match.Success)
            return false;

        var group = match.Groups["name1"].Success ? match.Groups["name1"] : match.Groups["name2"];
        var symbol = CreateAssemblySymbol(fileId, "function", group.Value, lineNumber, group.Index, codeLine);
        AddSymbolRecord(symbols, null, lineNumber, symbol, rawLine);
        functionSymbols.Add(symbol);
        return true;
    }

    private static bool TryAddAssemblyImportSymbol(
        long fileId,
        List<SymbolRecord> symbols,
        string codeLine,
        int lineNumber,
        string rawLine)
    {
        var match = AssemblyExternRegex.Match(codeLine);
        if (!match.Success)
            match = AssemblyIncludeRegex.Match(codeLine);
        if (!match.Success)
            return false;

        var name = match.Groups["name"].Value;
        AddSymbolRecord(symbols, null, lineNumber, CreateAssemblySymbol(fileId, "import", name, lineNumber, match.Groups["name"].Index, codeLine), rawLine);
        return true;
    }

    private static bool TryAddAssemblyConstantSymbol(
        long fileId,
        List<SymbolRecord> symbols,
        string codeLine,
        int lineNumber,
        string rawLine)
    {
        var match = AssemblyConstantRegex.Match(codeLine);
        if (!match.Success)
            return false;

        var group = match.Groups["name1"].Success ? match.Groups["name1"] : match.Groups["name2"];
        AddSymbolRecord(symbols, null, lineNumber, CreateAssemblySymbol(fileId, "property", group.Value, lineNumber, group.Index, codeLine), rawLine);
        return true;
    }

    private static SymbolRecord CreateAssemblySymbol(
        long fileId,
        string kind,
        string name,
        int lineNumber,
        int startColumn,
        string codeLine)
        => new()
        {
            FileId = fileId,
            Kind = kind,
            Name = name,
            Line = lineNumber,
            StartLine = lineNumber,
            StartColumn = startColumn,
            EndLine = lineNumber,
            Signature = codeLine.Trim(),
        };

    private static void AssignAssemblyRanges(
        List<SymbolRecord> functionSymbols,
        List<SymbolRecord> sectionSymbols,
        int lineCount)
    {
        var sections = sectionSymbols.OrderBy(symbol => symbol.StartLine).ThenBy(symbol => symbol.StartColumn ?? 0).ToList();
        var functions = functionSymbols.OrderBy(symbol => symbol.StartLine).ThenBy(symbol => symbol.StartColumn ?? 0).ToList();
        for (var i = 0; i < functions.Count; i++)
        {
            var current = functions[i];
            var nextFunctionStartLine = i + 1 < functions.Count ? functions[i + 1].StartLine : lineCount + 1;
            var nextSectionStartLine = sections.FirstOrDefault(symbol => symbol.StartLine > current.StartLine)?.StartLine ?? lineCount + 1;
            var nextStartLine = Math.Min(nextFunctionStartLine, nextSectionStartLine);
            current.BodyStartLine = current.StartLine;
            current.BodyEndLine = Math.Max(current.StartLine, nextStartLine - 1);
            current.EndLine = current.BodyEndLine.Value;
        }

        for (var i = 0; i < sections.Count; i++)
        {
            var current = sections[i];
            var nextStartLine = i + 1 < sections.Count ? sections[i + 1].StartLine : lineCount + 1;
            current.BodyStartLine = Math.Min(current.StartLine + 1, lineCount);
            current.BodyEndLine = Math.Max(current.StartLine, nextStartLine - 1);
            current.EndLine = current.BodyEndLine.Value;
        }
    }
}
