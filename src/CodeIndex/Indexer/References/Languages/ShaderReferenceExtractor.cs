using System.Text.RegularExpressions;
using CodeIndex.Models;

using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class ShaderReferenceExtractor
{
    internal const string TrackedNameBudgetDiagnosticKind =
        "reference_shader_tracked_name_budget_exceeded";
    internal const string LineNameBudgetDiagnosticKind =
        "reference_shader_line_name_budget_exceeded";
    private const int MaxPendingLayoutLines = 16;
    private const RegexOptions SharedRegexOptions =
        RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex IdentifierRegex = new(@"[A-Za-z_]\w*", SharedRegexOptions);
    private static readonly Regex IncludeRegex = new(
        @"^\s*#\s*include\s*[<""](?<path>[^>""\r\n]+)[>""]",
        SharedRegexOptions);
    private static readonly Regex IncludeDirectiveRegex = new(
        @"^\s*#\s*include\b",
        SharedRegexOptions);
    private static readonly Regex CudaKernelLaunchRegex = new(
        @"(?<![\w:])(?<name>(?:[A-Za-z_]\w*::)*[A-Za-z_]\w*)(?:\s*<[^<>\r\n]{1,512}>)?\s*<<<",
        SharedRegexOptions);
    private static readonly Regex CudaKernelHeaderRegex = new(
        @"\b__global__\b.*?\((?<parameters>[^)]*)\)",
        SharedRegexOptions);
    private static readonly Regex CudaParameterNameRegex = new(
        @"(?<name>[A-Za-z_]\w*)\s*(?:\[[^\]\r\n]*\]\s*)?(?:=[^,\r\n]+)?$",
        SharedRegexOptions);
    private static readonly Regex CudaConstantRegex = new(
        @"\b__constant__\s+(?:[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*(?:\s*<[^>]+>)?[\s*&]+)+(?<name>[A-Za-z_]\w*)",
        SharedRegexOptions);
    private static readonly Regex GlslBindingRegex = new(
        @"\b(?:uniform|buffer)\s+(?:[A-Za-z_]\w*(?:\s*<[^>]+>)?\s+)?(?<name>[A-Za-z_]\w*)\b",
        SharedRegexOptions);
    private static readonly Regex GlslBlockStartRegex = new(
        @"\b(?:uniform|buffer)\s+(?<name>[A-Za-z_]\w*)\s*\{",
        SharedRegexOptions);
    private static readonly Regex GlslBlockEndRegex = new(
        @"}\s*(?<name>[A-Za-z_]\w*)?\s*;",
        SharedRegexOptions);
    private static readonly Regex HlslBindingRegex = new(
        @"\b(?<name>[A-Za-z_]\w*)\s*:\s*register\s*\(",
        SharedRegexOptions);
    private static readonly Regex HlslCbufferStartRegex = new(
        @"\b(?:cbuffer|tbuffer)\s+(?<name>[A-Za-z_]\w*)(?:\s*:\s*register\s*\([^)]*\))?\s*(?<open>\{)?",
        SharedRegexOptions);
    private static readonly Regex ShaderBlockMemberRegex = new(
        @"\b(?<name>[A-Za-z_]\w*)\s*(?:\[[^\]\r\n]*\]\s*)?;",
        SharedRegexOptions);
    private static readonly Regex MetalBindingRegex = new(
        @"\b(?<name>[A-Za-z_]\w*)\s*\[\[\s*(?:buffer|texture|sampler)\s*\(",
        SharedRegexOptions);
    private static readonly Regex WgslBindingRegex = new(
        @"\bvar(?:<[^>\r\n]+>)?\s+(?<name>[A-Za-z_]\w*)\s*:",
        SharedRegexOptions);

    internal sealed class State
    {
        private readonly Action<ReferenceExtractionDiagnostic>? _reportDiagnostic;
        private bool _trackedNameBudgetReported;
        private bool _lineNameBudgetReported;

        public State(
            string language,
            Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
        {
            Language = language;
            _reportDiagnostic = reportDiagnostic;
        }

        public string Language { get; }

        public HashSet<string> ResourceNames { get; } = new(StringComparer.Ordinal);

        public HashSet<string> GlobalResourceNames { get; } = new(StringComparer.Ordinal);

        public HashSet<string> TypeNames { get; } = new(StringComparer.Ordinal);

        public HashSet<(int Line, string Name)> ResourceDefinitions { get; } = [];

        public HashSet<(int Line, string Name)> TypeDefinitions { get; } = [];

        public Dictionary<int, List<BindingSite>> BindingsByLine { get; } = [];

        public Dictionary<string, List<ScopedResource>> ScopedResourcesByName { get; } =
            new(StringComparer.Ordinal);

        public bool TryTrackName(HashSet<string> names, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || names.Contains(name))
                return !string.IsNullOrWhiteSpace(name);

            var limit = ReferenceExtractor.GetSafetyLimits().MaxLookupSymbols;
            if (names.Count < limit)
            {
                names.Add(name);
                return true;
            }

            if (!_trackedNameBudgetReported)
            {
                _trackedNameBudgetReported = true;
                _reportDiagnostic?.Invoke(
                    new ReferenceExtractionDiagnostic(
                        TrackedNameBudgetDiagnosticKind,
                        $"Shader reference extraction for '{Language}' exceeded the tracked-name budget of {limit:N0}; graph results for this file are incomplete."));
            }

            return false;
        }

        public void ReportLineNameBudget(int limit)
        {
            if (_lineNameBudgetReported)
                return;

            _lineNameBudgetReported = true;
            _reportDiagnostic?.Invoke(
                new ReferenceExtractionDiagnostic(
                    LineNameBudgetDiagnosticKind,
                    $"Shader reference extraction for '{Language}' exceeded the per-line identifier budget of {limit:N0}; graph results for this file are incomplete."));
        }

    }

    internal readonly record struct BindingSite(string Name, int Column);

    internal readonly record struct ScopedResource(
        string ContainerName,
        int HeaderEndLine,
        int BodyEndLine,
        int FirstBodyColumn);

    public static State? CreateState(
        string language,
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<SymbolRecord>? workspaceSymbols,
        Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
    {
        if (language is not ("cuda" or "glsl" or "hlsl" or "metal" or "wgsl"))
            return null;

        var state = new State(language, reportDiagnostic);
        foreach (var symbol in symbols)
        {
            if (IsTypeDefinition(symbol.Kind) && state.TryTrackName(state.TypeNames, symbol.Name))
                state.TypeDefinitions.Add((symbol.StartLine, symbol.Name));

            if (symbol.Kind == "property"
                && symbol.ContainerName is null
                && state.TryTrackName(state.ResourceNames, symbol.Name))
            {
                state.GlobalResourceNames.Add(symbol.Name);
                state.ResourceDefinitions.Add((symbol.StartLine, symbol.Name));
            }
        }

        if (workspaceSymbols is not null)
        {
            foreach (var symbol in workspaceSymbols)
            {
                if (IsTypeDefinition(symbol.Kind))
                    state.TryTrackName(state.TypeNames, symbol.Name);
            }
        }

        PrecomputeBindingsAndGlobalResources(state, preparedLines, symbols);
        if (language == "cuda")
            TrackCudaKernelParameters(state, preparedLines, symbols);

        return state;
    }

    public static void EmitLineReferences(
        State state,
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainer)
    {
        EmitIncludeReference(
            state,
            preparedLine,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainer);
        EmitBindingReferences(
            state,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainer);

        if (state.Language == "cuda")
        {
            EmitCudaKernelLaunchReferences(
                preparedLine,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainer);
        }

        EmitTrackedNameReferences(
            state,
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainer);
    }

    private static bool IsTypeDefinition(string kind)
        => kind is "class"
            or "struct"
            or "interface"
            or "enum"
            or "record"
            or "type"
            or "typealias"
            or "union";

    private static void TrackCudaKernelParameters(
        State state,
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.SubKind != "cuda_kernel"
                && !(symbol.Signature?.Contains("__global__", StringComparison.Ordinal) ?? false))
            {
                continue;
            }

            var headerEndLine = Math.Clamp(
                symbol.BodyStartLine ?? symbol.StartLine,
                symbol.StartLine,
                preparedLines.Count);
            var bodyEndLine = Math.Clamp(
                symbol.BodyEndLine ?? symbol.EndLine,
                headerEndLine,
                preparedLines.Count);
            var openingBrace = preparedLines[headerEndLine - 1].IndexOf('{');
            var firstBodyColumn = openingBrace >= 0
                ? openingBrace + 1
                : int.MaxValue;
            var header = string.Join(
                " ",
                preparedLines.Skip(symbol.StartLine - 1).Take(headerEndLine - symbol.StartLine + 1));
            var match = CudaKernelHeaderRegex.Match(header);
            if (!match.Success)
                continue;

            var parameters = match.Groups["parameters"];
            var parameterEnumerator = new DelimitedSpanEnumerable(
                header.AsSpan(parameters.Index, parameters.Length),
                ',').GetEnumerator();
            while (parameterEnumerator.MoveNext())
            {
                var parameter = parameterEnumerator.Current;
                var name = CudaParameterNameRegex.Match(
                    header,
                    parameters.Index + parameterEnumerator.CurrentStart,
                    parameter.Length).Groups["name"];
                if (!name.Success || string.IsNullOrWhiteSpace(name.Value))
                    continue;

                if (!state.ScopedResourcesByName.TryGetValue(name.Value, out var scopes))
                {
                    if (!state.TryTrackName(state.ResourceNames, name.Value))
                        continue;

                    scopes = [];
                    state.ScopedResourcesByName[name.Value] = scopes;
                }

                scopes.Add(new ScopedResource(
                    symbol.Name,
                    headerEndLine,
                    bodyEndLine,
                    firstBodyColumn));
            }
        }
    }

    private static void PrecomputeBindingsAndGlobalResources(
        State state,
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<SymbolRecord> symbols)
    {
        string? glslBlockName = null;
        List<(string Name, int Line)>? glslBlockMembers = null;
        var glslLayoutCollecting = false;
        var glslBindingPending = false;
        var glslLayoutLineCount = 0;
        string? hlslCbufferName = null;
        var hlslCbufferBodyStarted = false;
        var wgslBindingPending = false;

        for (var lineIndex = 0; lineIndex < preparedLines.Count; lineIndex++)
        {
            var lineNumber = lineIndex + 1;
            var line = preparedLines[lineIndex];

            if (state.Language == "glsl")
            {
                if (glslBlockName is not null)
                {
                    var blockEnd = GlslBlockEndRegex.Match(line);
                    if (blockEnd.Success)
                    {
                        var instance = blockEnd.Groups["name"];
                        var closingBrace = line.IndexOf('}');
                        if (closingBrace > 0)
                            TrackBlockMembers(glslBlockMembers!, line[..closingBrace], lineNumber);
                        if (instance.Success && !string.IsNullOrWhiteSpace(instance.Value))
                        {
                            TrackBindingDefinition(state, instance.Value, lineNumber, instance.Index);
                        }
                        else
                        {
                            TrackBindingDefinition(state, glslBlockName, lineNumber, Math.Max(0, line.IndexOf('}')));
                            foreach (var member in glslBlockMembers!)
                                TrackResourceDefinition(state, member.Name, member.Line);
                        }

                        glslBlockName = null;
                        glslBlockMembers = null;
                        continue;
                    }

                    TrackBlockMembers(glslBlockMembers!, line, lineNumber);
                    continue;
                }

                var wasCollectingLayout = glslLayoutCollecting;
                var startsLayout = line.Contains("layout", StringComparison.Ordinal);
                if (startsLayout)
                {
                    glslBindingPending = line.Contains("binding", StringComparison.Ordinal);
                    glslLayoutCollecting = !line.Contains(')');
                    glslLayoutLineCount = 1;
                }
                else if (glslLayoutCollecting)
                {
                    glslLayoutLineCount++;
                    if (line.Contains("binding", StringComparison.Ordinal))
                        glslBindingPending = true;
                    if (line.Contains(')'))
                        glslLayoutCollecting = false;
                    if (glslLayoutLineCount > MaxPendingLayoutLines)
                    {
                        glslLayoutCollecting = false;
                        glslBindingPending = false;
                    }
                }

                if (glslBindingPending)
                {
                    var blockStart = GlslBlockStartRegex.Match(line);
                    if (blockStart.Success)
                    {
                        glslBindingPending = false;
                        glslLayoutCollecting = false;
                        glslBlockName = blockStart.Groups["name"].Value;
                        glslBlockMembers = [];
                        var blockEnd = GlslBlockEndRegex.Match(line);
                        if (blockEnd.Success)
                        {
                            var instance = blockEnd.Groups["name"];
                            var openingBrace = line.IndexOf('{');
                            var closingBrace = line.IndexOf('}', openingBrace + 1);
                            if (openingBrace >= 0 && closingBrace > openingBrace)
                            {
                                TrackBlockMembers(
                                    glslBlockMembers,
                                    line[(openingBrace + 1)..closingBrace],
                                    lineNumber);
                            }

                            TrackBindingDefinition(
                                state,
                                instance.Success && !string.IsNullOrWhiteSpace(instance.Value)
                                    ? instance.Value
                                    : glslBlockName,
                                lineNumber,
                                instance.Success ? instance.Index : blockStart.Groups["name"].Index);
                            if (!instance.Success || string.IsNullOrWhiteSpace(instance.Value))
                            {
                                foreach (var member in glslBlockMembers)
                                    TrackResourceDefinition(state, member.Name, member.Line);
                            }

                            glslBlockName = null;
                            glslBlockMembers = null;
                        }

                        continue;
                    }

                    var bindingFound = false;
                    foreach (var match in Regex.EnumerateMatches(GlslBindingRegex, line))
                    {
                        bindingFound = true;
                        var name = match.Groups["name"];
                        TrackBindingDefinition(state, name.Value, lineNumber, name.Index);
                    }

                    if (bindingFound)
                    {
                        glslBindingPending = false;
                        glslLayoutCollecting = false;
                        continue;
                    }
                }

                var isLayoutQualifierLine = startsLayout || wasCollectingLayout;
                if (glslBindingPending
                    && !glslLayoutCollecting
                    && !isLayoutQualifierLine
                    && line.Trim().Length > 0)
                {
                    glslBindingPending = false;
                }

                continue;
            }

            if (state.Language == "hlsl")
            {
                if (hlslCbufferName is not null)
                {
                    var memberText = line;
                    if (!hlslCbufferBodyStarted)
                    {
                        var openingBrace = line.IndexOf('{');
                        if (openingBrace < 0)
                            continue;
                        hlslCbufferBodyStarted = true;
                        memberText = line[(openingBrace + 1)..];
                    }

                    var closingBrace = memberText.IndexOf('}');
                    if (closingBrace >= 0)
                        memberText = memberText[..closingBrace];
                    foreach (var member in Regex.EnumerateMatches(ShaderBlockMemberRegex, memberText))
                    {
                        var name = member.Groups["name"];
                        TrackResourceDefinition(state, name.Value, lineNumber);
                    }

                    if (closingBrace >= 0)
                    {
                        hlslCbufferName = null;
                        hlslCbufferBodyStarted = false;
                    }

                    continue;
                }

                var cbufferStart = HlslCbufferStartRegex.Match(line);
                if (cbufferStart.Success)
                {
                    hlslCbufferName = cbufferStart.Groups["name"].Value;
                    hlslCbufferBodyStarted = cbufferStart.Groups["open"].Success;
                    if (line.Contains("register", StringComparison.Ordinal))
                    {
                        var name = cbufferStart.Groups["name"];
                        TrackBindingDefinition(state, name.Value, lineNumber, name.Index);
                    }

                    if (hlslCbufferBodyStarted)
                    {
                        var openingBrace = line.IndexOf('{');
                        var memberText = line[(openingBrace + 1)..];
                        var closingBrace = memberText.IndexOf('}');
                        if (closingBrace >= 0)
                            memberText = memberText[..closingBrace];
                        foreach (var member in Regex.EnumerateMatches(ShaderBlockMemberRegex, memberText))
                        {
                            var name = member.Groups["name"];
                            TrackResourceDefinition(state, name.Value, lineNumber);
                        }

                        if (closingBrace >= 0)
                        {
                            hlslCbufferName = null;
                            hlslCbufferBodyStarted = false;
                        }
                    }

                    continue;
                }

                foreach (var match in Regex.EnumerateMatches(HlslBindingRegex, line))
                {
                    var name = match.Groups["name"];
                    TrackBindingDefinition(state, name.Value, lineNumber, name.Index);
                }

                continue;
            }

            if (state.Language == "wgsl")
            {
                var hasBindingAttribute = line.Contains("@binding", StringComparison.Ordinal)
                    || line.Contains("@group", StringComparison.Ordinal);
                if (hasBindingAttribute)
                    wgslBindingPending = true;

                var binding = WgslBindingRegex.Match(line);
                if (binding.Success && (wgslBindingPending || hasBindingAttribute))
                {
                    var name = binding.Groups["name"];
                    TrackBindingDefinition(state, name.Value, lineNumber, name.Index);
                    wgslBindingPending = false;
                    continue;
                }

                var trimmed = line.Trim();
                if (wgslBindingPending
                    && trimmed.Length > 0
                    && !trimmed.StartsWith('@'))
                {
                    wgslBindingPending = false;
                }

                continue;
            }

            if (state.Language == "metal")
            {
                foreach (var match in Regex.EnumerateMatches(MetalBindingRegex, line))
                {
                    var name = match.Groups["name"];
                    TrackMetalBindingDefinition(
                        state,
                        symbols,
                        preparedLines,
                        name.Value,
                        lineNumber,
                        name.Index);
                }

                continue;
            }

            var bindingRegex = state.Language switch
            {
                "cuda" => CudaConstantRegex,
                _ => null,
            };
            if (bindingRegex is null)
                continue;

            foreach (var match in Regex.EnumerateMatches(bindingRegex, line))
            {
                var name = match.Groups["name"];
                TrackBindingDefinition(state, name.Value, lineNumber, name.Index);
            }
        }
    }

    private static void TrackMetalBindingDefinition(
        State state,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<string> preparedLines,
        string name,
        int lineNumber,
        int column)
    {
        var container = symbols
            .Where(symbol =>
                symbol.Kind == "function"
                && symbol.StartLine <= lineNumber
                && symbol.EndLine >= lineNumber)
            .OrderBy(symbol => symbol.EndLine - symbol.StartLine)
            .FirstOrDefault();
        if (container is null)
            return;

        var headerEndLine = Math.Clamp(
            container.BodyStartLine ?? container.StartLine,
            container.StartLine,
            preparedLines.Count);
        var bodyEndLine = Math.Clamp(
            container.BodyEndLine ?? container.EndLine,
            headerEndLine,
            preparedLines.Count);
        var openingBrace = preparedLines[headerEndLine - 1].IndexOf('{');
        var firstBodyColumn = openingBrace >= 0
            ? openingBrace + 1
            : int.MaxValue;

        if (!state.TryTrackName(state.ResourceNames, name))
            return;

        state.ResourceDefinitions.Add((lineNumber, name));
        AddBindingSite(state, name, lineNumber, column);
        if (!state.ScopedResourcesByName.TryGetValue(name, out var scopes))
        {
            scopes = [];
            state.ScopedResourcesByName[name] = scopes;
        }

        scopes.Add(new ScopedResource(
            container.Name,
            headerEndLine,
            bodyEndLine,
            firstBodyColumn));
    }

    private static void TrackBlockMembers(
        List<(string Name, int Line)> members,
        string line,
        int lineNumber)
    {
        foreach (var member in Regex.EnumerateMatches(ShaderBlockMemberRegex, line))
        {
            var name = member.Groups["name"].Value;
            if (!string.IsNullOrWhiteSpace(name))
                members.Add((name, lineNumber));
        }
    }

    private static void TrackBindingDefinition(
        State state,
        string name,
        int lineNumber,
        int column)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        TrackResourceDefinition(state, name, lineNumber);
        AddBindingSite(state, name, lineNumber, column);
    }

    private static void AddBindingSite(
        State state,
        string name,
        int lineNumber,
        int column)
    {
        if (!state.BindingsByLine.TryGetValue(lineNumber, out var bindings))
        {
            bindings = [];
            state.BindingsByLine[lineNumber] = bindings;
        }

        if (!ContainsBindingSite(bindings, name, column))
            bindings.Add(new BindingSite(name, column));
    }

    internal static bool ContainsBindingSite(
        IReadOnlyList<BindingSite> bindings,
        string name,
        int column)
    {
        for (var bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
        {
            var binding = bindings[bindingIndex];
            if (binding.Name == name && binding.Column == column)
                return true;
        }

        return false;
    }

    private static void TrackResourceDefinition(State state, string name, int lineNumber)
    {
        if (!state.TryTrackName(state.ResourceNames, name))
            return;

        state.GlobalResourceNames.Add(name);
        state.ResourceDefinitions.Add((lineNumber, name));
    }

    private static void EmitIncludeReference(
        State state,
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainer)
    {
        if (state.Language == "wgsl")
            return;

        if (!IncludeDirectiveRegex.Match(preparedLine).Success)
            return;

        var match = IncludeRegex.Match(originalLine);
        if (!match.Success)
            return;

        var path = match.Groups["path"];
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            path.Value,
            path.Index,
            "import",
            context,
            lineNumber,
            resolveContainer(path.Index),
            state.Language);
    }

    private static void EmitBindingReferences(
        State state,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainer)
    {
        if (!state.BindingsByLine.TryGetValue(lineNumber, out var bindings))
            return;

        foreach (var binding in bindings)
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                binding.Name,
                binding.Column,
                "binding",
                context,
                lineNumber,
                resolveContainer(binding.Column),
                state.Language);
        }
    }

    private static void EmitCudaKernelLaunchReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainer)
    {
        foreach (var match in Regex.EnumerateMatches(CudaKernelLaunchRegex, preparedLine))
        {
            var name = match.Groups["name"];
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name.Value,
                name.Index,
                "call",
                context,
                lineNumber,
                resolveContainer(name.Index),
                language: null);
        }
    }

    private static void EmitTrackedNameReferences(
        State state,
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainer)
    {
        var matchesVisited = 0;
        var maxNamesPerLine = ReferenceExtractor.GetSafetyLimits().MaxNamesPerLine;
        foreach (var match in Regex.EnumerateMatches(IdentifierRegex, preparedLine))
        {
            if (++matchesVisited > maxNamesPerLine)
            {
                state.ReportLineNameBudget(maxNamesPerLine);
                break;
            }

            var name = match.Value;
            if (state.TypeNames.Contains(name)
                && !state.TypeDefinitions.Contains((lineNumber, name)))
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    name,
                    match.Index,
                    "type_reference",
                    context,
                    lineNumber,
                    resolveContainer(match.Index),
                    state.Language);
            }

            if (!state.ResourceNames.Contains(name)
                || state.ResourceDefinitions.Contains((lineNumber, name)))
            {
                continue;
            }

            var container = resolveContainer(match.Index);
            if (container is null)
                continue;

            var isGlobalResource = state.GlobalResourceNames.Contains(name);
            var isScopedResource = state.ScopedResourcesByName.TryGetValue(name, out var scopes)
                && ContainsActiveScopedResource(
                    scopes,
                    container.Name,
                    lineNumber,
                    match.Index);
            if (!isGlobalResource && !isScopedResource)
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                match.Index,
                "resource_reference",
                context,
                lineNumber,
                container,
                state.Language);
        }
    }

    internal static bool ContainsActiveScopedResource(
        IReadOnlyList<ScopedResource> scopes,
        string containerName,
        int lineNumber,
        int column)
    {
        for (var scopeIndex = 0; scopeIndex < scopes.Count; scopeIndex++)
        {
            var scope = scopes[scopeIndex];
            if (scope.ContainerName == containerName
                && (lineNumber > scope.HeaderEndLine
                    || (lineNumber == scope.HeaderEndLine
                        && column >= scope.FirstBodyColumn))
                && lineNumber <= scope.BodyEndLine)
            {
                return true;
            }
        }

        return false;
    }
}
