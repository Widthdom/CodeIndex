using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static readonly Regex SolidityInheritanceRegex = new(
        @"^\s*(?:abstract\s+)?(?:contract|interface)\s+" + SolidityLanguageSupport.IdentifierPattern + @"\s+is\s+(?<bases>[^{;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityBaseIdentifierRegex = new(
        @"(?<name>" + SolidityLanguageSupport.IdentifierPattern + @")(?:\s*\([^)]*\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityUsingLibraryRegex = new(
        @"^\s*using\s+(?<name>" + SolidityLanguageSupport.IdentifierPattern + @")\s+for\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityEmitRegex = new(
        @"\bemit\s+(?<name>" + SolidityLanguageSupport.IdentifierPattern + @")\s*(?=\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityInterfaceCastCallRegex = new(
        @"(?<![A-Za-z0-9_$])(?<type>[A-Z][A-Za-z0-9_$]*)\s*\([^;\r\n]*?\)\s*\.\s*(?<method>" + SolidityLanguageSupport.IdentifierPattern + @")\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityCallableHeaderRegex = new(
        @"^\s*(?:function\s+(?:" + SolidityLanguageSupport.IdentifierPattern + @")|constructor|fallback|receive)\s*\([^)]*\)(?<tail>[^{;]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SolidityTailIdentifierRegex = new(
        @"(?<name>" + SolidityLanguageSupport.IdentifierPattern + @")(?:\s*\([^)]*\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SolidityModifierTailKeywords = new(StringComparer.Ordinal)
    {
        "public", "private", "internal", "external", "view", "pure", "payable", "virtual", "override",
        "returns", "memory", "calldata", "storage", "immutable", "constant",
    };

    private static List<ReferenceRecord> ExtractSolidityReferences(
        long fileId,
        string[] rawLines,
        string[] preparedLines,
        InnermostContainerResolver containerResolver)
    {
        var matchLines = SolidityLanguageSupport.MaskCommentsAndStrings(preparedLines);
        var references = new List<ReferenceRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < matchLines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = matchLines[i];
            var context = rawLines[i].Trim();

            AddSolidityInheritanceReferences(references, seen, fileId, line, context, lineNumber, containerResolver);
            AddSolidityLibraryReferences(references, seen, fileId, line, context, lineNumber, containerResolver);
            AddSolidityModifierReferences(references, seen, fileId, line, context, lineNumber, containerResolver);
            AddSolidityEventReferences(references, seen, fileId, line, context, lineNumber, containerResolver);
            AddSolidityInterfaceCallReferences(references, seen, fileId, line, context, lineNumber, containerResolver);
        }

        return references;
    }

    private static void AddSolidityInheritanceReferences(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string line,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        var match = SolidityInheritanceRegex.Match(line);
        if (!match.Success)
            return;

        var bases = match.Groups["bases"];
        foreach (Match baseMatch in SolidityBaseIdentifierRegex.Matches(bases.Value))
        {
            var name = baseMatch.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            AddSolidityReference(
                references,
                seen,
                fileId,
                name,
                bases.Index + baseMatch.Groups["name"].Index,
                "extends",
                context,
                lineNumber,
                containerResolver);
        }
    }

    private static void AddSolidityLibraryReferences(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string line,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        var match = SolidityUsingLibraryRegex.Match(line);
        if (!match.Success)
            return;

        var name = match.Groups["name"];
        AddSolidityReference(references, seen, fileId, name.Value, name.Index, "use", context, lineNumber, containerResolver);
    }

    private static void AddSolidityModifierReferences(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string line,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        var header = SolidityCallableHeaderRegex.Match(line);
        if (!header.Success)
            return;

        var tail = header.Groups["tail"];
        foreach (Match modifier in SolidityTailIdentifierRegex.Matches(tail.Value))
        {
            var name = modifier.Groups["name"];
            if (SolidityModifierTailKeywords.Contains(name.Value))
                continue;

            AddSolidityReference(
                references,
                seen,
                fileId,
                name.Value,
                tail.Index + name.Index,
                "call",
                context,
                lineNumber,
                containerResolver);
        }
    }

    private static void AddSolidityEventReferences(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string line,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        foreach (Match match in SolidityEmitRegex.Matches(line))
        {
            var name = match.Groups["name"];
            AddSolidityReference(references, seen, fileId, name.Value, name.Index, "call", context, lineNumber, containerResolver);
        }
    }

    private static void AddSolidityInterfaceCallReferences(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string line,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        foreach (Match match in SolidityInterfaceCastCallRegex.Matches(line))
        {
            var type = match.Groups["type"];
            AddSolidityReference(references, seen, fileId, type.Value, type.Index, "type_reference", context, lineNumber, containerResolver);

            var method = match.Groups["method"];
            AddSolidityReference(references, seen, fileId, method.Value, method.Index, "call", context, lineNumber, containerResolver);
        }
    }

    private static void AddSolidityReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string name,
        int nameIndex,
        string referenceKind,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        AddReference(
            references,
            seen,
            fileId,
            name,
            nameIndex,
            referenceKind,
            context,
            lineNumber,
            containerResolver.Find(lineNumber),
            "solidity");
    }
}
