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
        List<ReferenceRecord>? references = null;
        ReferenceDedupeSet? seen = null;

        for (var i = 0; i < matchLines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = matchLines[i];
            var context = rawLines[i].Trim();

            AddSolidityInheritanceReferences(ref references, ref seen, fileId, line, context, lineNumber, containerResolver);
            AddSolidityLibraryReferences(ref references, ref seen, fileId, line, context, lineNumber, containerResolver);
            AddSolidityModifierReferences(ref references, ref seen, fileId, line, context, lineNumber, containerResolver);
            AddSolidityEventReferences(ref references, ref seen, fileId, line, context, lineNumber, containerResolver);
            AddSolidityInterfaceCallReferences(ref references, ref seen, fileId, line, context, lineNumber, containerResolver);
        }

        return references ?? [];
    }

    private static void AddSolidityInheritanceReferences(
        ref List<ReferenceRecord>? references,
        ref ReferenceDedupeSet? seen,
        long fileId,
        string line,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        if (!MayContainSolidityInheritanceMarker(line))
            return;

        var match = SolidityInheritanceRegex.Match(line);
        if (!match.Success)
            return;

        var bases = match.Groups["bases"];
        foreach (Match baseMatch in BoundedRegex.EnumerateMatches(SolidityBaseIdentifierRegex, bases.Value))
        {
            var name = baseMatch.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            AddSolidityReference(
                ref references,
                ref seen,
                fileId,
                name,
                bases.Index + baseMatch.Groups["name"].Index,
                "extends",
                context,
                lineNumber,
                containerResolver);
        }
    }

    private static bool MayContainSolidityInheritanceMarker(string line)
    {
        var index = line.IndexOf("is", StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index - 1;
            var after = index + "is".Length;
            if (before >= 0
                && after < line.Length
                && char.IsWhiteSpace(line[before])
                && char.IsWhiteSpace(line[after]))
            {
                return true;
            }

            index = line.IndexOf("is", index + "is".Length, StringComparison.Ordinal);
        }

        return false;
    }

    private static void AddSolidityLibraryReferences(
        ref List<ReferenceRecord>? references,
        ref ReferenceDedupeSet? seen,
        long fileId,
        string line,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        if (line.IndexOf("using", StringComparison.Ordinal) < 0
            || line.IndexOf("for", StringComparison.Ordinal) < 0)
        {
            return;
        }

        var match = SolidityUsingLibraryRegex.Match(line);
        if (!match.Success)
            return;

        var name = match.Groups["name"];
        AddSolidityReference(ref references, ref seen, fileId, name.Value, name.Index, "use", context, lineNumber, containerResolver);
    }

    private static void AddSolidityModifierReferences(
        ref List<ReferenceRecord>? references,
        ref ReferenceDedupeSet? seen,
        long fileId,
        string line,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        if (line.IndexOf('(') < 0
            || !MayContainSolidityCallableHeader(line))
        {
            return;
        }

        var header = SolidityCallableHeaderRegex.Match(line);
        if (!header.Success)
            return;

        var tail = header.Groups["tail"];
        foreach (Match modifier in BoundedRegex.EnumerateMatches(SolidityTailIdentifierRegex, tail.Value))
        {
            var name = modifier.Groups["name"];
            if (SolidityModifierTailKeywords.Contains(name.Value))
                continue;

            AddSolidityReference(
                ref references,
                ref seen,
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
        ref List<ReferenceRecord>? references,
        ref ReferenceDedupeSet? seen,
        long fileId,
        string line,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        if (line.IndexOf("emit", StringComparison.Ordinal) < 0
            || line.IndexOf('(') < 0)
        {
            return;
        }

        foreach (Match match in BoundedRegex.EnumerateMatches(SolidityEmitRegex, line))
        {
            var name = match.Groups["name"];
            AddSolidityReference(ref references, ref seen, fileId, name.Value, name.Index, "call", context, lineNumber, containerResolver);
        }
    }

    private static void AddSolidityInterfaceCallReferences(
        ref List<ReferenceRecord>? references,
        ref ReferenceDedupeSet? seen,
        long fileId,
        string line,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        if (line.IndexOf('.') < 0 || line.IndexOf('(') < 0)
            return;

        foreach (Match match in BoundedRegex.EnumerateMatches(SolidityInterfaceCastCallRegex, line))
        {
            var type = match.Groups["type"];
            AddSolidityReference(ref references, ref seen, fileId, type.Value, type.Index, "type_reference", context, lineNumber, containerResolver);

            var method = match.Groups["method"];
            AddSolidityReference(ref references, ref seen, fileId, method.Value, method.Index, "call", context, lineNumber, containerResolver);
        }
    }

    private static bool MayContainSolidityCallableHeader(string line)
        => line.IndexOf("function", StringComparison.Ordinal) >= 0
           || line.IndexOf("constructor", StringComparison.Ordinal) >= 0
           || line.IndexOf("fallback", StringComparison.Ordinal) >= 0
           || line.IndexOf("receive", StringComparison.Ordinal) >= 0;

    private static void AddSolidityReference(
        ref List<ReferenceRecord>? references,
        ref ReferenceDedupeSet? seen,
        long fileId,
        string name,
        int nameIndex,
        string referenceKind,
        string context,
        int lineNumber,
        InnermostContainerResolver containerResolver)
    {
        AddReference(
            references ??= [],
            seen ??= new ReferenceDedupeSet(),
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
