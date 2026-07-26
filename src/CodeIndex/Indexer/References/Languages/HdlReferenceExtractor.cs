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
        @"^\s*(?<target>" + VerilogIdentifierPattern + @")(?:\s*#\s*\([^;\r\n]*\))?\s+(?<instance>" + VerilogIdentifierPattern + @")(?:\s*\[[^\]\r\n;]+\])*\s*\(",
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
        @"^\s*library\s+(?<body>" + VhdlIdentifierPattern + @"(?:\s*,\s*" + VhdlIdentifierPattern + @")*)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlUseRegex = new(
        @"^\s*use\s+(?<body>" + VhdlIdentifierPattern + @"(?:\." + VhdlIdentifierPattern + @")*(?:\s*,\s*" + VhdlIdentifierPattern + @"(?:\." + VhdlIdentifierPattern + @")*)*)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlSelectedNameRegex = new(
        VhdlIdentifierPattern + @"(?:\." + VhdlIdentifierPattern + @")*",
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
        @"^\s*end(?:\s+(?<kind>architecture|entity|package|function|procedure|process|configuration))?(?:\s+body)?(?:\s+(?<name>" + VhdlIdentifierPattern + @"))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlSubprogramBodyMarkerRegex = new(
        @"\bis\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlParameterNamesRegex = new(
        @"(?<names>" + VhdlIdentifierPattern + @"(?:\s*,\s*" + VhdlIdentifierPattern + @")*)\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VhdlLocalDeclarationRegex = new(
        @"^\s*(?:variable|constant|signal)\s+(?<names>" + VhdlIdentifierPattern + @"(?:\s*,\s*" + VhdlIdentifierPattern + @")*)\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> VerilogInstantiationKeywords = new(StringComparer.Ordinal)
    {
        "always", "always_comb", "always_ff", "always_latch", "and", "assign", "begin", "buf", "bufif0",
        "bufif1", "case", "casex", "casez", "class", "cmos", "deassign", "disable", "else", "end", "event",
        "covergroup", "for", "force", "forever", "fork", "function", "generate", "if", "initial", "input", "interface",
        "join", "join_any", "join_none", "logic", "macromodule", "module", "nand", "nmos", "nor", "not",
        "notif0", "notif1", "or", "output", "package", "parameter", "pmos", "primitive", "program", "pullup",
        "pulldown", "rcmos", "reg", "release", "repeat", "rnmos", "rpmos", "rtran", "rtranif0", "rtranif1",
        "property", "sequence", "task", "tran", "tranif0", "tranif1", "typedef", "wait", "wand", "while", "wire", "wor", "xnor", "xor",
    };
    private static readonly HashSet<string> VhdlControlEndNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "block", "case", "component", "for", "generate", "if", "loop", "protected", "record", "units",
    };
    private static readonly HashSet<string> VhdlInstantiationKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "assert", "block", "case", "component", "entity", "for", "generate", "if", "loop", "process",
    };

    private sealed record HdlKnownSymbol(
        string Name,
        string ReferenceKind,
        HashSet<int>? LocalDesignUnitIds);

    private sealed record HdlScope(
        SymbolRecord Symbol,
        int DesignUnitId,
        HashSet<string> ShadowedNames);

    private sealed class VhdlPendingSubprogramHeader(string name)
    {
        public string Name { get; } = name;
        public HashSet<string> ShadowedNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ParenthesisDepth { get; set; }
    }

    private sealed record VhdlCompletedSubprogramHeader(
        string Name,
        HashSet<string> ShadowedNames);

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
        var lineCount = Math.Min(lines.Length, limits.MaxLookupLines);
        var vhdlDesignUnitIds = request.Language == "vhdl"
            ? BuildVhdlDesignUnitIds(lines, lineCount, request.CancellationToken)
            : null;
        var (knownSymbols, definitionsByLine) = BuildHdlKnownSymbols(
            request,
            comparer,
            limits,
            vhdlDesignUnitIds);
        var scopes = new List<HdlScope>();
        var nextDesignUnitId = 1;
        VhdlPendingSubprogramHeader? pendingVhdlSubprogram = null;
        var inVerilogBlockComment = false;
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
            var container = scopes.Count > 0 ? scopes[^1].Symbol : null;
            VhdlCompletedSubprogramHeader? completedVhdlSubprogram = null;
            var vhdlHeaderDeclaredNames = request.Language == "vhdl"
                ? AdvanceVhdlSubprogramHeader(
                    structuralLine,
                    ref pendingVhdlSubprogram,
                    out completedVhdlSubprogram)
                : null;
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
                var vhdlDeclaredNames = request.Language == "vhdl"
                    ? MergeVhdlDeclaredNames(
                        GetVhdlDeclaredNames(structuralLine),
                        vhdlHeaderDeclaredNames)
                    : null;
                if (vhdlDeclaredNames is { Count: > 0 }
                    && scopes.Count > 0
                    && scopes[^1].Symbol.Kind == "function"
                    && VhdlLocalDeclarationRegex.IsMatch(structuralLine))
                {
                    scopes[^1].ShadowedNames.UnionWith(vhdlDeclaredNames);
                }

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
                    scopes,
                    vhdlDeclaredNames,
                    vhdlDesignUnitIds?[lineIndex] ?? 0,
                    limits,
                    ref lineNameBudgetReported);
            }

            if (request.Language == "vhdl" && completedVhdlSubprogram != null)
            {
                AddHdlScope(
                    scopes,
                    "function",
                    completedVhdlSubprogram.Name,
                    ref nextDesignUnitId,
                    completedVhdlSubprogram.ShadowedNames);
            }
            else
            {
                TryPushHdlScope(
                    request.Language,
                    structuralLine,
                    scopes,
                    ref nextDesignUnitId);
            }
        }

        return references;
    }

    private static (
        Dictionary<string, HdlKnownSymbol> KnownSymbols,
        Dictionary<int, HashSet<string>> DefinitionsByLine)
        BuildHdlKnownSymbols(
            ReferenceExtractionContext request,
            StringComparer comparer,
            ReferenceExtractionSafetyLimits limits,
            IReadOnlyList<int>? vhdlDesignUnitIds)
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
            if (referenceKind != null)
            {
                var localDesignUnitId = request.Language == "vhdl"
                    && symbol.Kind == "property"
                    && vhdlDesignUnitIds != null
                    && symbol.Line > 0
                    && symbol.Line <= vhdlDesignUnitIds.Count
                        ? vhdlDesignUnitIds[symbol.Line - 1]
                        : (int?)null;
                if (!knownSymbols.TryGetValue(symbol.Name, out var existing)
                    || GetHdlReferenceKindPriority(referenceKind) > GetHdlReferenceKindPriority(existing.ReferenceKind))
                {
                    knownSymbols[symbol.Name] = new HdlKnownSymbol(
                        symbol.Name,
                        referenceKind,
                        localDesignUnitId > 0 ? [localDesignUnitId.Value] : null);
                }
                else if (referenceKind == existing.ReferenceKind
                    && localDesignUnitId > 0
                    && existing.LocalDesignUnitIds != null)
                {
                    existing.LocalDesignUnitIds.Add(localDesignUnitId.Value);
                }
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
            var bodyGroup = libraryMatch.Groups["body"];
            foreach (Match nameMatch in VhdlIdentifierRegex.Matches(bodyGroup.Value))
            {
                AddHdlReference(
                    request,
                    references,
                    seen,
                    nameMatch.Value,
                    bodyGroup.Index + nameMatch.Index,
                    "import",
                    originalLine,
                    lineNumber,
                    container,
                    specialPositions);
            }
        }

        var useMatch = VhdlUseRegex.Match(structuralLine);
        if (useMatch.Success)
        {
            var bodyGroup = useMatch.Groups["body"];
            foreach (Match pathMatch in VhdlSelectedNameRegex.Matches(bodyGroup.Value))
            {
                var (packageName, packageOffset) = SelectVhdlPackage(pathMatch.Value);
                AddHdlReference(
                    request,
                    references,
                    seen,
                    packageName,
                    bodyGroup.Index + pathMatch.Index + packageOffset,
                    "import",
                    originalLine,
                    lineNumber,
                    container,
                    specialPositions);
                foreach (Match identifierMatch in VhdlIdentifierRegex.Matches(pathMatch.Value))
                    specialPositions.Add(bodyGroup.Index + pathMatch.Index + identifierMatch.Index);
            }
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
        var componentCount = 0;
        var previousComponent = ReadOnlySpan<char>.Empty;
        var previousOffset = 0;
        var lastComponent = ReadOnlySpan<char>.Empty;
        var lastOffset = 0;
        foreach (var component in new DelimitedSpanEnumerable(path.AsSpan(), '.'))
        {
            previousComponent = lastComponent;
            previousOffset = lastOffset;
            lastComponent = component;
            lastOffset = componentCount == 0
                ? 0
                : lastOffset + previousComponent.Length + 1;
            componentCount++;
        }

        ReadOnlySpan<char> package;
        int offset;
        switch (componentCount)
        {
            case 1:
                package = lastComponent;
                offset = lastOffset;
                break;
            case 2 when lastComponent.Equals("all", StringComparison.OrdinalIgnoreCase):
                package = previousComponent;
                offset = previousOffset;
                break;
            case 2:
                package = lastComponent;
                offset = lastOffset;
                break;
            default:
                package = previousComponent;
                offset = previousOffset;
                break;
        }
        return (package.ToString(), offset);
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
        IReadOnlyList<HdlScope> scopes,
        IReadOnlySet<string>? declaredNames,
        int currentDesignUnitId,
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
                    && definitions.Contains(match.Value)
                || declaredNames?.Contains(match.Value) == true
                || IsHdlNameShadowed(scopes, match.Value)
                || knownSymbol.LocalDesignUnitIds is { Count: > 0 } localDesignUnitIds
                    && !localDesignUnitIds.Contains(currentDesignUnitId))
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

    private static bool IsHdlNameShadowed(
        IReadOnlyList<HdlScope> scopes,
        string name)
    {
        for (var scopeIndex = 0; scopeIndex < scopes.Count; scopeIndex++)
        {
            if (scopes[scopeIndex].ShadowedNames.Contains(name))
                return true;
        }

        return false;
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

}
