using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private const string VerilogIdentifierPattern = @"[A-Za-z_$][A-Za-z0-9_$]*";
    private const string VhdlIdentifierPattern = @"[A-Za-z][A-Za-z0-9_]*";

    private static readonly Regex VerilogIdentifierRegex = new(
        VerilogIdentifierPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlIdentifierRegex = new(
        VhdlIdentifierPattern,
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VerilogIncludeRegex = new(
        @"^\s*`include\s+""(?<name>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SystemVerilogImportRegex = new(
        @"^\s*import\s+(?<body>[^;]+)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SystemVerilogQualifiedReferenceRegex = new(
        @"(?<![A-Za-z0-9_$])(?<package>" + VerilogIdentifierPattern + @")::(?<member>" + VerilogIdentifierPattern + @"|\*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VerilogInstantiationRegex = new(
        @"^\s*(?<target>" + VerilogIdentifierPattern + @")(?:\s*#\s*\([^;\r\n]*\))?\s+(?<instance>" + VerilogIdentifierPattern + @")\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SystemVerilogInterfacePortRegex = new(
        @"(?<![A-Za-z0-9_$])(?:(?:input|output|inout)\s+)?(?<target>" + VerilogIdentifierPattern + @")\.(?<modport>" + VerilogIdentifierPattern + @")\s+" + VerilogIdentifierPattern + @"\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VerilogScopeStartRegex = new(
        @"^\s*(?<kind>module|macromodule|primitive|program|interface|package)\s+(?<name>" + VerilogIdentifierPattern + @")\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SystemVerilogClassStartRegex = new(
        @"^\s*(?:virtual\s+)?class\s+(?<name>" + VerilogIdentifierPattern + @")\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VerilogFunctionStartRegex = new(
        @"^\s*function\b[^\r\n;]*(?<name>" + VerilogIdentifierPattern + @")\s*(?:\(|;)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VerilogTaskStartRegex = new(
        @"^\s*task\s+(?:automatic\s+|static\s+|virtual\s+)?(?<name>" + VerilogIdentifierPattern + @")\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VerilogScopeEndRegex = new(
        @"^\s*end(?<kind>module|primitive|program|interface|package|class|function|task)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VhdlLibraryRegex = new(
        @"^\s*library\s+(?<name>" + VhdlIdentifierPattern + @")\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlUseRegex = new(
        @"^\s*use\s+(?<path>" + VhdlIdentifierPattern + @"(?:\." + VhdlIdentifierPattern + @")*)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlArchitectureRegex = new(
        @"^\s*architecture\s+(?<architecture>" + VhdlIdentifierPattern + @")\s+of\s+(?<entity>" + VhdlIdentifierPattern + @")\s+is\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlEntityInstantiationRegex = new(
        @"(?:^\s*" + VhdlIdentifierPattern + @"\s*:\s*entity\s+|\buse\s+entity\s+)(?:(?<library>" + VhdlIdentifierPattern + @")\.)?(?<entity>" + VhdlIdentifierPattern + @")(?:\s*\(\s*(?<architecture>" + VhdlIdentifierPattern + @")\s*\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlComponentInstantiationRegex = new(
        @"^\s*" + VhdlIdentifierPattern + @"\s*:\s*(?:component\s+)?(?<target>" + VhdlIdentifierPattern + @")\s*(?:(?:generic|port)\s+map\b|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlArchitectureStartRegex = new(
        @"^\s*architecture\s+(?<name>" + VhdlIdentifierPattern + @")\s+of\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlEntityStartRegex = new(
        @"^\s*entity\s+(?<name>" + VhdlIdentifierPattern + @")\s+is\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlPackageStartRegex = new(
        @"^\s*package\s+(?:body\s+)?(?<name>" + VhdlIdentifierPattern + @")\s+is\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlConfigurationStartRegex = new(
        @"^\s*configuration\s+(?<name>" + VhdlIdentifierPattern + @")\s+of\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlFunctionStartRegex = new(
        @"^\s*(?:pure\s+|impure\s+)?function\s+(?<name>" + VhdlIdentifierPattern + @")\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlProcedureStartRegex = new(
        @"^\s*procedure\s+(?<name>" + VhdlIdentifierPattern + @")\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlProcessStartRegex = new(
        @"^\s*(?<name>" + VhdlIdentifierPattern + @")\s*:\s*process\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlScopeEndRegex = new(
        @"^\s*end(?:\s+(?<kind>architecture|entity|package|function|procedure|process|configuration))?(?:\s+(?<name>" + VhdlIdentifierPattern + @"))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> VerilogInstantiationKeywords = new(StringComparer.Ordinal)
    {
        "always", "always_comb", "always_ff", "always_latch", "and", "assign", "begin", "buf", "bufif0",
        "bufif1", "case", "casex", "casez", "class", "cmos", "deassign", "disable", "else", "end", "event",
        "for", "force", "forever", "fork", "function", "generate", "if", "initial", "input", "interface",
        "join", "join_any", "join_none", "logic", "macromodule", "module", "nand", "nmos", "nor", "not",
        "notif0", "notif1", "or", "output", "package", "parameter", "pmos", "primitive", "program", "pullup",
        "pulldown", "rcmos", "reg", "release", "repeat", "rnmos", "rpmos", "rtran", "rtranif0", "rtranif1",
        "task", "tran", "tranif0", "tranif1", "typedef", "wait", "wand", "while", "wire", "wor", "xnor", "xor",
    };
    private static readonly HashSet<string> VhdlControlEndNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "block", "case", "component", "for", "generate", "if", "loop", "protected", "record", "units",
    };
    private static readonly HashSet<string> VhdlInstantiationKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "assert", "block", "case", "component", "entity", "for", "generate", "if", "loop", "process",
    };

    private readonly record struct HdlKnownSymbol(string Name, string ReferenceKind);

    private static bool IsHdlReferenceLanguage(string language)
        => language is "verilog" or "systemverilog" or "vhdl";

    private static List<ReferenceRecord> ExtractHdlReferences(ReferenceExtractionContext request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        if (!TryPrepareReferenceLines(
            request.Language,
            request.Content,
            isRazorFile: false,
            request.ContentIsNormalized,
            request.HasOversizeLine,
            request.ConflictMarkerLine,
            out var preparedInput))
        {
            return [];
        }

        var lines = preparedInput.Lines;
        var limits = GetSafetyLimits();
        var references = CreateReferenceList(
            request.MaxReferenceCount,
            EstimateReferenceListInitialCapacity(lines.Length));
        var seen = CreateReferenceSeenSet(lines.Length);
        var comparer = request.Language == "vhdl"
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var (knownSymbols, definitionsByLine) = BuildHdlKnownSymbols(request, comparer, limits);
        var scopes = new List<SymbolRecord>();
        var inVerilogBlockComment = false;
        var lineCount = Math.Min(lines.Length, limits.MaxLookupLines);
        if (lines.Length > lineCount)
        {
            request.ReportDiagnostic?.Invoke(new ReferenceExtractionDiagnostic(
                "reference_definition_lookup_line_budget_exceeded",
                $"HDL reference extraction scanned the first {limits.MaxLookupLines:N0} lines and skipped additional lines."));
        }

        var lineNameBudgetReported = false;
        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            if (ReferenceLimitReached(references))
                break;

            var originalLine = lines[lineIndex];
            var structuralLine = MaskHdlCommentsAndStrings(
                originalLine,
                request.Language,
                ref inVerilogBlockComment);
            if (string.IsNullOrWhiteSpace(structuralLine))
                continue;

            var lineNumber = lineIndex + 1;
            var endedScope = TryPopHdlScope(request.Language, structuralLine, scopes);
            var container = scopes.Count > 0 ? scopes[^1] : null;
            var specialPositions = new HashSet<int>();
            if (request.Language == "vhdl")
            {
                EmitVhdlStructuralReferences(
                    request,
                    structuralLine,
                    originalLine,
                    lineNumber,
                    container,
                    references,
                    seen,
                    specialPositions);
            }
            else
            {
                EmitVerilogStructuralReferences(
                    request,
                    structuralLine,
                    originalLine,
                    lineNumber,
                    container,
                    references,
                    seen,
                    specialPositions);
            }

            if (!endedScope && !ReferenceLimitReached(references))
            {
                EmitKnownHdlReferences(
                    request,
                    structuralLine,
                    originalLine,
                    lineNumber,
                    container,
                    references,
                    seen,
                    specialPositions,
                    knownSymbols,
                    definitionsByLine,
                    limits,
                    ref lineNameBudgetReported);
            }

            TryPushHdlScope(request.Language, structuralLine, scopes);
        }

        return references;
    }

    private static (
        Dictionary<string, HdlKnownSymbol> KnownSymbols,
        Dictionary<int, HashSet<string>> DefinitionsByLine)
        BuildHdlKnownSymbols(
            ReferenceExtractionContext request,
            StringComparer comparer,
            ReferenceExtractionSafetyLimits limits)
    {
        var knownSymbols = new Dictionary<string, HdlKnownSymbol>(comparer);
        var definitionsByLine = new Dictionary<int, HashSet<string>>();
        var retainedCount = Math.Min(request.Symbols.Count, limits.MaxLookupSymbols);
        for (var index = 0; index < retainedCount; index++)
        {
            var symbol = request.Symbols[index];
            if (string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            var referenceKind = GetHdlKnownReferenceKind(symbol.Kind);
            if (referenceKind != null
                && (!knownSymbols.TryGetValue(symbol.Name, out var existing)
                    || GetHdlReferenceKindPriority(referenceKind) > GetHdlReferenceKindPriority(existing.ReferenceKind)))
            {
                knownSymbols[symbol.Name] = new HdlKnownSymbol(symbol.Name, referenceKind);
            }

            if (symbol.Line <= 0)
                continue;
            if (!definitionsByLine.TryGetValue(symbol.Line, out var names))
            {
                names = new HashSet<string>(comparer);
                definitionsByLine[symbol.Line] = names;
            }

            names.Add(symbol.Name);
        }

        if (request.Symbols.Count > retainedCount)
        {
            request.ReportDiagnostic?.Invoke(new ReferenceExtractionDiagnostic(
                "reference_definition_lookup_symbol_budget_exceeded",
                $"HDL reference extraction used the first {limits.MaxLookupSymbols:N0} symbols and skipped additional symbols."));
        }

        return (knownSymbols, definitionsByLine);
    }

    private static string? GetHdlKnownReferenceKind(string symbolKind)
        => symbolKind switch
        {
            "property" => "reference",
            "function" => "call",
            "class" or "enum" or "interface" or "module" or "package" or "struct" or "typealias" => "type_reference",
            _ => null,
        };

    private static int GetHdlReferenceKindPriority(string referenceKind)
        => referenceKind switch
        {
            "type_reference" => 3,
            "call" => 2,
            _ => 1,
        };

    private static void EmitVerilogStructuralReferences(
        ReferenceExtractionContext request,
        string structuralLine,
        string originalLine,
        int lineNumber,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        HashSet<int> specialPositions)
    {
        var includeMatch = structuralLine.TrimStart().StartsWith("`include", StringComparison.Ordinal)
            ? VerilogIncludeRegex.Match(originalLine)
            : Match.Empty;
        if (includeMatch.Success)
        {
            AddHdlReference(
                request,
                references,
                seen,
                includeMatch.Groups["name"].Value,
                includeMatch.Groups["name"].Index,
                "import",
                originalLine,
                lineNumber,
                container,
                specialPositions);
        }

        Match? importMatch = null;
        if (request.Language == "systemverilog")
        {
            importMatch = SystemVerilogImportRegex.Match(structuralLine);
            if (importMatch.Success)
            {
                var bodyGroup = importMatch.Groups["body"];
                foreach (Match itemMatch in SystemVerilogQualifiedReferenceRegex.Matches(bodyGroup.Value))
                {
                    var packageGroup = itemMatch.Groups["package"];
                    AddHdlReference(
                        request,
                        references,
                        seen,
                        packageGroup.Value,
                        bodyGroup.Index + packageGroup.Index,
                        "import",
                        originalLine,
                        lineNumber,
                        container,
                        specialPositions);
                }
            }

            var interfaceMatch = SystemVerilogInterfacePortRegex.Match(structuralLine);
            if (interfaceMatch.Success)
            {
                AddHdlReference(
                    request,
                    references,
                    seen,
                    interfaceMatch.Groups["target"].Value,
                    interfaceMatch.Groups["target"].Index,
                    "type_reference",
                    originalLine,
                    lineNumber,
                    container,
                    specialPositions);
            }

            if (importMatch is not { Success: true })
            {
                foreach (Match qualifiedMatch in SystemVerilogQualifiedReferenceRegex.Matches(structuralLine))
                {
                    AddHdlReference(
                        request,
                        references,
                        seen,
                        qualifiedMatch.Groups["package"].Value,
                        qualifiedMatch.Groups["package"].Index,
                        "reference",
                        originalLine,
                        lineNumber,
                        container,
                        specialPositions);
                }
            }
        }

        var instantiationMatch = VerilogInstantiationRegex.Match(structuralLine);
        if (instantiationMatch.Success)
        {
            var target = instantiationMatch.Groups["target"];
            if (!VerilogInstantiationKeywords.Contains(target.Value))
            {
                AddHdlReference(
                    request,
                    references,
                    seen,
                    target.Value,
                    target.Index,
                    "instantiate",
                    originalLine,
                    lineNumber,
                    container,
                    specialPositions);
            }
        }
    }

    private static void EmitVhdlStructuralReferences(
        ReferenceExtractionContext request,
        string structuralLine,
        string originalLine,
        int lineNumber,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        HashSet<int> specialPositions)
    {
        var libraryMatch = VhdlLibraryRegex.Match(structuralLine);
        if (libraryMatch.Success)
        {
            AddHdlReference(
                request,
                references,
                seen,
                libraryMatch.Groups["name"].Value,
                libraryMatch.Groups["name"].Index,
                "import",
                originalLine,
                lineNumber,
                container,
                specialPositions);
        }

        var useMatch = VhdlUseRegex.Match(structuralLine);
        if (useMatch.Success)
        {
            var pathGroup = useMatch.Groups["path"];
            var (packageName, packageOffset) = SelectVhdlPackage(pathGroup.Value);
            AddHdlReference(
                request,
                references,
                seen,
                packageName,
                pathGroup.Index + packageOffset,
                "import",
                originalLine,
                lineNumber,
                container,
                specialPositions);
        }

        var architectureMatch = VhdlArchitectureRegex.Match(structuralLine);
        if (architectureMatch.Success)
        {
            AddHdlReference(
                request,
                references,
                seen,
                architectureMatch.Groups["entity"].Value,
                architectureMatch.Groups["entity"].Index,
                "type_reference",
                originalLine,
                lineNumber,
                container,
                specialPositions);
        }

        var emittedEntityInstantiation = false;
        foreach (Match entityMatch in VhdlEntityInstantiationRegex.Matches(structuralLine))
        {
            emittedEntityInstantiation = true;
            AddHdlReference(
                request,
                references,
                seen,
                entityMatch.Groups["entity"].Value,
                entityMatch.Groups["entity"].Index,
                "instantiate",
                originalLine,
                lineNumber,
                container,
                specialPositions);

            if (entityMatch.Groups["architecture"] is { Success: true } architecture)
            {
                AddHdlReference(
                    request,
                    references,
                    seen,
                    architecture.Value,
                    architecture.Index,
                    "type_reference",
                    originalLine,
                    lineNumber,
                    container,
                    specialPositions);
            }
        }

        if (!emittedEntityInstantiation)
        {
            var componentMatch = VhdlComponentInstantiationRegex.Match(structuralLine);
            if (componentMatch.Success
                && !VhdlInstantiationKeywords.Contains(componentMatch.Groups["target"].Value))
            {
                AddHdlReference(
                    request,
                    references,
                    seen,
                    componentMatch.Groups["target"].Value,
                    componentMatch.Groups["target"].Index,
                    "instantiate",
                    originalLine,
                    lineNumber,
                    container,
                    specialPositions);
            }
        }
    }

    private static (string PackageName, int Offset) SelectVhdlPackage(string path)
    {
        var components = path.Split('.');
        var packageIndex = components.Length switch
        {
            1 => 0,
            2 when string.Equals(components[1], "all", StringComparison.OrdinalIgnoreCase) => 0,
            2 => 1,
            _ => components.Length - 2,
        };
        var offset = 0;
        for (var index = 0; index < packageIndex; index++)
            offset += components[index].Length + 1;
        return (components[packageIndex], offset);
    }

    private static void EmitKnownHdlReferences(
        ReferenceExtractionContext request,
        string structuralLine,
        string originalLine,
        int lineNumber,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        HashSet<int> specialPositions,
        IReadOnlyDictionary<string, HdlKnownSymbol> knownSymbols,
        IReadOnlyDictionary<int, HashSet<string>> definitionsByLine,
        ReferenceExtractionSafetyLimits limits,
        ref bool lineNameBudgetReported)
    {
        var identifierRegex = request.Language == "vhdl"
            ? VhdlIdentifierRegex
            : VerilogIdentifierRegex;
        var matchedNameCount = 0;
        foreach (Match match in identifierRegex.Matches(structuralLine))
        {
            if (matchedNameCount >= limits.MaxNamesPerLine)
            {
                if (!lineNameBudgetReported)
                {
                    request.ReportDiagnostic?.Invoke(new ReferenceExtractionDiagnostic(
                        "reference_definition_lookup_line_name_budget_exceeded",
                        $"HDL reference extraction retained at most {limits.MaxNamesPerLine:N0} identifier candidates per line and skipped additional names."));
                    lineNameBudgetReported = true;
                }

                break;
            }

            matchedNameCount++;
            if (specialPositions.Contains(match.Index)
                || !knownSymbols.TryGetValue(match.Value, out var knownSymbol)
                || definitionsByLine.TryGetValue(lineNumber, out var definitions)
                    && definitions.Contains(match.Value))
            {
                continue;
            }

            if (knownSymbol.ReferenceKind == "call"
                && !IsFollowedByOpenParenthesis(structuralLine, match.Index + match.Length))
            {
                continue;
            }

            AddHdlReference(
                request,
                references,
                seen,
                knownSymbol.Name,
                match.Index,
                knownSymbol.ReferenceKind,
                originalLine,
                lineNumber,
                container,
                specialPositions: null);
            if (ReferenceLimitReached(references))
                break;
        }
    }

    private static bool IsFollowedByOpenParenthesis(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        return index < line.Length && line[index] == '(';
    }

    private static void AddHdlReference(
        ReferenceExtractionContext request,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        string name,
        int nameIndex,
        string referenceKind,
        string originalLine,
        int lineNumber,
        SymbolRecord? container,
        HashSet<int>? specialPositions)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        AddReference(
            references,
            seen,
            request.FileId,
            name,
            nameIndex,
            referenceKind,
            originalLine.Trim(),
            lineNumber,
            container,
            request.Language);
        specialPositions?.Add(nameIndex);
    }

    private static bool TryPopHdlScope(
        string language,
        string structuralLine,
        List<SymbolRecord> scopes)
    {
        if (scopes.Count == 0)
            return false;

        if (language != "vhdl")
        {
            var match = VerilogScopeEndRegex.Match(structuralLine);
            if (!match.Success)
                return false;

            PopHdlScope(scopes, NormalizeVerilogScopeKind(match.Groups["kind"].Value), name: null, ignoreCase: false);
            return true;
        }

        var vhdlMatch = VhdlScopeEndRegex.Match(structuralLine);
        if (!vhdlMatch.Success)
            return false;

        var kind = vhdlMatch.Groups["kind"].Success
            ? NormalizeVhdlScopeKind(vhdlMatch.Groups["kind"].Value)
            : null;
        var name = vhdlMatch.Groups["name"].Success
            ? vhdlMatch.Groups["name"].Value
            : null;
        if (name != null && VhdlControlEndNames.Contains(name))
            return true;

        if (kind == null && name == null)
            scopes.RemoveAt(scopes.Count - 1);
        else
            PopHdlScope(scopes, kind, name, ignoreCase: true);
        return true;
    }

    private static void PopHdlScope(
        List<SymbolRecord> scopes,
        string? kind,
        string? name,
        bool ignoreCase)
    {
        var comparison = ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (var index = scopes.Count - 1; index >= 0; index--)
        {
            var scope = scopes[index];
            if ((kind == null || string.Equals(scope.Kind, kind, comparison))
                && (name == null || string.Equals(scope.Name, name, comparison)))
            {
                scopes.RemoveRange(index, scopes.Count - index);
                return;
            }
        }
    }

    private static void TryPushHdlScope(
        string language,
        string structuralLine,
        List<SymbolRecord> scopes)
    {
        if (language != "vhdl")
        {
            var match = VerilogScopeStartRegex.Match(structuralLine);
            if (match.Success)
            {
                AddHdlScope(
                    scopes,
                    NormalizeVerilogScopeKind(match.Groups["kind"].Value),
                    match.Groups["name"].Value);
                return;
            }

            match = SystemVerilogClassStartRegex.Match(structuralLine);
            if (match.Success)
            {
                AddHdlScope(scopes, "class", match.Groups["name"].Value);
                return;
            }

            match = VerilogFunctionStartRegex.Match(structuralLine);
            if (match.Success)
            {
                AddHdlScope(scopes, "function", match.Groups["name"].Value);
                return;
            }

            match = VerilogTaskStartRegex.Match(structuralLine);
            if (match.Success)
                AddHdlScope(scopes, "function", match.Groups["name"].Value);
            return;
        }

        if (TryMatchHdlScope(VhdlArchitectureStartRegex, structuralLine, "module", scopes)
            || TryMatchHdlScope(VhdlEntityStartRegex, structuralLine, "module", scopes)
            || TryMatchHdlScope(VhdlPackageStartRegex, structuralLine, "package", scopes)
            || TryMatchHdlScope(VhdlConfigurationStartRegex, structuralLine, "module", scopes)
            || TryMatchHdlScope(VhdlFunctionStartRegex, structuralLine, "function", scopes)
            || TryMatchHdlScope(VhdlProcedureStartRegex, structuralLine, "function", scopes))
        {
            return;
        }

        TryMatchHdlScope(VhdlProcessStartRegex, structuralLine, "function", scopes);
    }

    private static bool TryMatchHdlScope(
        Regex regex,
        string line,
        string kind,
        List<SymbolRecord> scopes)
    {
        var match = regex.Match(line);
        if (!match.Success)
            return false;

        AddHdlScope(scopes, kind, match.Groups["name"].Value);
        return true;
    }

    private static void AddHdlScope(List<SymbolRecord> scopes, string kind, string name)
    {
        scopes.Add(new SymbolRecord
        {
            Kind = kind,
            Name = name,
        });
    }

    private static string NormalizeVerilogScopeKind(string kind)
        => kind switch
        {
            "macromodule" or "primitive" or "program" => "module",
            "task" => "function",
            _ => kind,
        };

    private static string NormalizeVhdlScopeKind(string kind)
        => kind switch
        {
            "architecture" or "entity" or "configuration" => "module",
            "procedure" or "process" => "function",
            _ => kind,
        };

    private static string MaskHdlCommentsAndStrings(
        string line,
        string language,
        ref bool inVerilogBlockComment)
    {
        char[]? masked = null;
        var inString = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (language != "vhdl" && inVerilogBlockComment)
            {
                MaskCharacter(ref masked, line, index);
                if (line[index] == '*' && index + 1 < line.Length && line[index + 1] == '/')
                {
                    MaskCharacter(ref masked, line, ++index);
                    inVerilogBlockComment = false;
                }

                continue;
            }

            if (inString)
            {
                MaskCharacter(ref masked, line, index);
                if (language == "vhdl"
                    && line[index] == '"'
                    && index + 1 < line.Length
                    && line[index + 1] == '"')
                {
                    MaskCharacter(ref masked, line, ++index);
                    continue;
                }

                if (line[index] == '\\' && language != "vhdl" && index + 1 < line.Length)
                {
                    MaskCharacter(ref masked, line, ++index);
                    continue;
                }

                if (line[index] == '"')
                    inString = false;
                continue;
            }

            if (language == "vhdl"
                && line[index] == '\''
                && index + 2 < line.Length
                && line[index + 2] == '\'')
            {
                MaskRange(ref masked, line, index, index + 3);
                index += 2;
                continue;
            }

            if (language != "vhdl" && line[index] == '\'')
            {
                var literalEnd = FindVerilogNumericLiteralEnd(line, index);
                if (literalEnd > index)
                {
                    MaskRange(ref masked, line, index, literalEnd);
                    index = literalEnd - 1;
                    continue;
                }
            }

            if (line[index] == '"')
            {
                inString = true;
                MaskCharacter(ref masked, line, index);
                continue;
            }

            if (language == "vhdl"
                && line[index] == '-'
                && index + 1 < line.Length
                && line[index + 1] == '-')
            {
                MaskRange(ref masked, line, index, line.Length);
                break;
            }

            if (language != "vhdl"
                && line[index] == '/'
                && index + 1 < line.Length)
            {
                if (line[index + 1] == '/')
                {
                    MaskRange(ref masked, line, index, line.Length);
                    break;
                }

                if (line[index + 1] == '*')
                {
                    MaskCharacter(ref masked, line, index);
                    MaskCharacter(ref masked, line, ++index);
                    inVerilogBlockComment = true;
                }
            }
        }

        return masked == null ? line : new string(masked);
    }

    private static int FindVerilogNumericLiteralEnd(string line, int apostropheIndex)
    {
        var index = apostropheIndex + 1;
        if (index >= line.Length)
            return apostropheIndex;

        if (line[index] is '0' or '1' or 'x' or 'X' or 'z' or 'Z' or '?')
            return index + 1;

        if (line[index] is 's' or 'S')
            index++;
        if (index >= line.Length || line[index] is not ('b' or 'B' or 'o' or 'O' or 'd' or 'D' or 'h' or 'H'))
            return apostropheIndex;

        index++;
        var digitStart = index;
        while (index < line.Length
            && (char.IsLetterOrDigit(line[index]) || line[index] is '_' or '?'))
        {
            index++;
        }

        return index > digitStart ? index : apostropheIndex;
    }

    private static void MaskRange(ref char[]? masked, string source, int start, int end)
    {
        for (var index = start; index < end; index++)
            MaskCharacter(ref masked, source, index);
    }

    private static void MaskCharacter(ref char[]? masked, string source, int index)
    {
        masked ??= source.ToCharArray();
        masked[index] = ' ';
    }
}
