using System.Text;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private const string CSharpFileLocalFamilyPrefix = "file-local:";
    private readonly record struct DeclaredContainerIdentity(long FileId, string Kind, string Name);

    private static void PopulateDeclaredContainerQualifiedNames(List<SymbolRecord> symbols)
    {
        var requestedContainers = new HashSet<DeclaredContainerIdentity>();
        foreach (var symbol in symbols)
        {
            if (symbol.ContainerKind != null && symbol.ContainerName != null)
                requestedContainers.Add(new DeclaredContainerIdentity(symbol.FileId, symbol.ContainerKind, symbol.ContainerName));
        }

        if (requestedContainers.Count == 0)
            return;

        var declaredContainers = new Dictionary<DeclaredContainerIdentity, List<SymbolRecord>>(requestedContainers.Count);
        foreach (var candidate in symbols)
        {
            var identity = new DeclaredContainerIdentity(candidate.FileId, candidate.Kind, candidate.Name);
            if (!requestedContainers.Contains(identity))
                continue;

            if (!declaredContainers.TryGetValue(identity, out var candidates))
            {
                candidates = [];
                declaredContainers.Add(identity, candidates);
            }
            candidates.Add(candidate);
        }

        foreach (var symbol in symbols)
        {
            if (symbol.ContainerKind == null || symbol.ContainerName == null)
                continue;

            var identity = new DeclaredContainerIdentity(symbol.FileId, symbol.ContainerKind, symbol.ContainerName);
            if (!declaredContainers.TryGetValue(identity, out var candidates))
                continue;

            var container = FindDeclaredContainerSymbol(candidates, symbol);
            if (container == null)
                continue;

            symbol.ContainerQualifiedName = container.ContainerQualifiedName != null
                ? $"{container.ContainerQualifiedName}.{container.Name}"
                : container.Name;
        }
    }

    private static SymbolRecord? FindDeclaredContainerSymbol(IReadOnlyList<SymbolRecord> candidates, SymbolRecord symbol)
    {
        SymbolRecord? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.StartLine > symbol.StartLine
                || candidate.EndLine < symbol.EndLine)
            {
                continue;
            }

            if (best == null
                || candidate.StartLine > best.StartLine
                || (candidate.StartLine == best.StartLine && candidate.EndLine < best.EndLine))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static void AssignContainers(
        List<SymbolRecord> symbols,
        string[]? rawLines = null,
        Func<CSharpLexState[]>? getCSharpLineStartStates = null,
        string? filePath = null,
        string? projectRoot = null)
    {
        if (symbols.Count == 0)
            return;

        if (symbols.Count == 1)
        {
            AssignTopLevelFamilyKey(symbols[0]);
            FinalizeCSharpFileLocalFamilyKeys(symbols, filePath, projectRoot);
            return;
        }

        var includeCallableContainers = getCSharpLineStartStates != null;
        if (!ContainsContainerCandidates(symbols, includeCallableContainers))
        {
            foreach (var symbol in symbols)
                AssignTopLevelFamilyKey(symbol);
            FinalizeCSharpFileLocalFamilyKeys(symbols, filePath, projectRoot);
            return;
        }

        var ordered = BuildContainerAssignmentOrder(symbols);

        var stack = new Stack<SymbolRecord>();
        var containerPathBuffer = new List<SymbolRecord>();
        foreach (var orderedSymbol in ordered)
        {
            var symbol = orderedSymbol.Symbol;
            while (stack.Count > 0 && !IsFileScopedNamespace(stack.Peek()) && symbol.StartLine > stack.Peek().EndLine)
                stack.Pop();

            var containerPath = GetEffectiveContainerPath(
                stack,
                symbol,
                containerPathBuffer,
                rawLines,
                getCSharpLineStartStates);

            if (containerPath.Count > 0)
            {
                var effectiveContainer = containerPath[^1];
                if (symbol.ContainerKind != null && symbol.ContainerName != null)
                {
                    var explicitContainerIndex = -1;
                    for (var i = containerPath.Count - 1; i >= 0; i--)
                    {
                        var container = containerPath[i];
                        if (container.Kind == symbol.ContainerKind
                            && container.Name == symbol.ContainerName)
                        {
                            explicitContainerIndex = i;
                            break;
                        }
                    }

                    var shouldPromoteToMoreSpecificContainer =
                        symbol.ContainerKind == "enum"
                        && explicitContainerIndex >= 0
                        && explicitContainerIndex < containerPath.Count - 1
                        && effectiveContainer.Kind == "function"
                        && effectiveContainer.ContainerKind == "enum";

                    if (shouldPromoteToMoreSpecificContainer)
                    {
                        effectiveContainer = containerPath[^1];
                        symbol.ContainerKind = effectiveContainer.Kind;
                        symbol.ContainerName = effectiveContainer.Name;
                        symbol.ContainerQualifiedName = BuildQualifiedContainerName(containerPath, containerPath.Count - 1);
                    }
                    else
                    {
                        var explicitContainerAlreadyPresent = explicitContainerIndex == containerPath.Count - 1;
                        var parentQualifiedName = BuildQualifiedContainerName(containerPath);
                        symbol.ContainerQualifiedName ??= explicitContainerAlreadyPresent
                            ? parentQualifiedName
                            : string.IsNullOrWhiteSpace(parentQualifiedName)
                                ? symbol.ContainerName
                                : $"{parentQualifiedName}.{symbol.ContainerName}";
                    }
                }
                else
                {
                    symbol.ContainerKind ??= effectiveContainer.Kind;
                    symbol.ContainerName ??= effectiveContainer.Name;
                    var qualifiedContainerName = BuildQualifiedContainerName(containerPath);
                    symbol.ContainerQualifiedName = qualifiedContainerName;
                    symbol.FamilyKey = BuildInheritedFamilyKey(effectiveContainer, containerPath);
                }
            }

            // Type declarations own their family identity. A nested partial type must
            // not retain the inherited key of its nearest partial container, because
            // sibling names and generic arities would then collapse into that parent.
            // type declaration は自身の family identity を持つ。nested partial type が
            // 親 partial container の key を保持すると sibling / arity を誤集約する。
            symbol.FamilyKey = BuildSelfFamilyKey(symbol, containerPath) ?? symbol.FamilyKey;
            if (symbol.FamilyKey == null
                && symbol.Kind is "function" or "test.method"
                && ContainsFileLocalType(containerPath))
            {
                symbol.FamilyKey = BuildFileLocalContainerFamilyKey(containerPath);
            }

            if (CanContainSymbols(symbol, includeCallableContainers))
                stack.Push(symbol);
        }

        FinalizeCSharpFileLocalFamilyKeys(symbols, filePath, projectRoot);
    }

    private static void FinalizeCSharpFileLocalFamilyKeys(
        IReadOnlyList<SymbolRecord> symbols,
        string? filePath,
        string? projectRoot)
    {
        var fileLocalFamilyBodies = symbols
            .Select(symbol => symbol.FamilyKey)
            .Where(familyKey => familyKey?.StartsWith(CSharpFileLocalFamilyPrefix, StringComparison.Ordinal) == true)
            .Select(familyKey => familyKey![CSharpFileLocalFamilyPrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
        if (fileLocalFamilyBodies.Count == 0)
            return;

        // C# permits the `file` modifier on only one part of a same-file partial type.
        // Propagate that scope to every matching declaration and inherited member before
        // persistence, then make the persisted key file-specific so all consumers,
        // including hotspots, observe the same boundary without reconstructing it.
        // C# では同一ファイル内の partial type の一部だけに `file` を付けられる。
        // 永続化前に同じ family の全宣言と配下 member へ scope を伝播し、さらに
        // 永続 key をファイル固有にして hotspots を含む全 consumer の境界を揃える。
        var fileIdentity = BuildCSharpFileLocalIdentity(filePath, projectRoot, symbols);
        foreach (var symbol in symbols)
        {
            if (string.IsNullOrWhiteSpace(symbol.FamilyKey))
                continue;

            var alreadyFileLocal = symbol.FamilyKey.StartsWith(
                CSharpFileLocalFamilyPrefix,
                StringComparison.Ordinal);
            var familyBody = alreadyFileLocal
                ? symbol.FamilyKey[CSharpFileLocalFamilyPrefix.Length..]
                : symbol.FamilyKey;
            if (!IsWithinCSharpFileLocalFamily(fileLocalFamilyBodies, familyBody))
                continue;

            symbol.FamilyKey = $"{CSharpFileLocalFamilyPrefix}{fileIdentity}\u001f{familyBody}";
            if (IsCSharpTypeFamilyKind(symbol.Kind) && symbol.IsPartialDeclaration == true)
                symbol.IsFileLocalDeclaration = true;
        }
    }

    private static bool IsWithinCSharpFileLocalFamily(
        IReadOnlySet<string> fileLocalFamilyBodies,
        string familyBody)
    {
        foreach (var fileLocalFamilyBody in fileLocalFamilyBodies)
        {
            if (string.Equals(familyBody, fileLocalFamilyBody, StringComparison.Ordinal)
                || familyBody.StartsWith(fileLocalFamilyBody + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildCSharpFileLocalIdentity(
        string? filePath,
        string? projectRoot,
        IReadOnlyList<SymbolRecord> symbols)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var identity = filePath;
            if (Path.IsPathRooted(identity) && !string.IsNullOrWhiteSpace(projectRoot))
                identity = Path.GetRelativePath(projectRoot, identity);
            return identity.Replace('\\', '/');
        }

        var fileId = symbols.Count > 0 ? symbols[0].FileId : 0;
        return $"file-id:{fileId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static bool IsCSharpTypeFamilyKind(string kind) =>
        kind is "class" or "struct" or "interface" or "record";

    private static void AssignTopLevelFamilyKey(SymbolRecord symbol)
        => symbol.FamilyKey ??= BuildSelfFamilyKey(symbol, Array.Empty<SymbolRecord>());

    private static bool ContainsContainerCandidates(
        IReadOnlyList<SymbolRecord> symbols,
        bool includeCallableContainers)
    {
        foreach (var symbol in symbols)
        {
            if (CanContainSymbols(symbol, includeCallableContainers))
                return true;
        }

        return false;
    }

    private readonly record struct ContainerAssignmentSortEntry(SymbolRecord Symbol, int OriginalIndex);

    private static List<ContainerAssignmentSortEntry> BuildContainerAssignmentOrder(IReadOnlyList<SymbolRecord> symbols)
    {
        if (symbols.Count == 0)
            return [];

        if (symbols.Count == 1)
            return [new ContainerAssignmentSortEntry(symbols[0], 0)];

        var ordered = new List<ContainerAssignmentSortEntry>(symbols.Count);
        for (var i = 0; i < symbols.Count; i++)
            ordered.Add(new ContainerAssignmentSortEntry(symbols[i], i));

        ordered.Sort(CompareContainerAssignmentSortEntries);
        return ordered;
    }

    private static int CompareContainerAssignmentSortEntries(ContainerAssignmentSortEntry left, ContainerAssignmentSortEntry right)
    {
        var compare = left.Symbol.StartLine.CompareTo(right.Symbol.StartLine);
        if (compare != 0)
            return compare;

        var leftStartColumnRank = left.Symbol.StartColumn.HasValue ? 0 : 1;
        var rightStartColumnRank = right.Symbol.StartColumn.HasValue ? 0 : 1;
        compare = leftStartColumnRank.CompareTo(rightStartColumnRank);
        if (compare != 0)
            return compare;

        compare = (left.Symbol.StartColumn ?? int.MaxValue).CompareTo(right.Symbol.StartColumn ?? int.MaxValue);
        if (compare != 0)
            return compare;

        compare = right.Symbol.EndLine.CompareTo(left.Symbol.EndLine);
        if (compare != 0)
            return compare;

        compare = (right.Symbol.Signature?.Length ?? 0).CompareTo(left.Symbol.Signature?.Length ?? 0);
        if (compare != 0)
            return compare;

        return left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static List<SymbolRecord> GetEffectiveContainerPath(
        Stack<SymbolRecord> containers,
        SymbolRecord symbol,
        List<SymbolRecord> containingContainers,
        string[]? rawLines = null,
        Func<CSharpLexState[]>? getCSharpLineStartStates = null)
    {
        containingContainers.Clear();
        if (containers.Count == 0)
            return containingContainers;

        if (containers.Count == 1)
        {
            var container = containers.Peek();
            if (ContainsSymbol(container, symbol, rawLines, getCSharpLineStartStates))
                containingContainers.Add(container);
            return containingContainers;
        }

        foreach (var container in containers)
        {
            if (ContainsSymbol(container, symbol, rawLines, getCSharpLineStartStates))
                containingContainers.Add(container);
        }
        containingContainers.Reverse();

        if (containingContainers.Count == 0)
            return containingContainers;

        if (symbol.Kind == "enum" && symbol.BodyStartLine == null)
        {
            var enumIndex = containingContainers.FindLastIndex(container => container.Kind == "enum");
            if (enumIndex >= 0)
                containingContainers.RemoveRange(enumIndex + 1, containingContainers.Count - enumIndex - 1);
        }

        return containingContainers;
    }

    private static string? BuildQualifiedContainerName(IReadOnlyList<SymbolRecord> containers) =>
        BuildQualifiedContainerName(containers, containers.Count);

    private static string? BuildQualifiedContainerName(IReadOnlyList<SymbolRecord> containers, int count)
    {
        if (count <= 0)
            return null;

        StringBuilder? builder = null;
        for (var i = 0; i < count; i++)
        {
            var name = containers[i].Name;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            builder ??= new StringBuilder(name.Length);
            if (builder.Length > 0)
                builder.Append('.');

            builder.Append(name);
        }

        return builder?.ToString();
    }

    private static string? BuildInheritedFamilyKey(
        SymbolRecord container,
        IReadOnlyList<SymbolRecord> containers)
    {
        if (!SupportsCrossFileFamily(container))
            return null;

        var familyName = BuildQualifiedFamilyName(containers);
        return familyName == null || !ContainsFileLocalType(containers)
            ? familyName
            : CSharpFileLocalFamilyPrefix + familyName;
    }

    private static string? BuildSelfFamilyKey(SymbolRecord symbol, IReadOnlyList<SymbolRecord> containers)
    {
        if (!SupportsCrossFileFamily(symbol))
            return null;

        var builder = new StringBuilder();
        AppendQualifiedFamilySegments(builder, containers);
        AppendFamilySegment(builder, symbol);
        return symbol.IsFileLocalDeclaration || ContainsFileLocalType(containers)
            ? CSharpFileLocalFamilyPrefix + builder.ToString()
            : builder.ToString();
    }

    private static string? BuildFileLocalContainerFamilyKey(IReadOnlyList<SymbolRecord> containers)
    {
        var familyName = BuildQualifiedFamilyName(containers);
        return familyName == null ? null : CSharpFileLocalFamilyPrefix + familyName;
    }

    private static bool ContainsFileLocalType(IReadOnlyList<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.IsFileLocalDeclaration)
                return true;
        }

        return false;
    }

    private static string? BuildQualifiedFamilyName(IReadOnlyList<SymbolRecord> symbols)
    {
        var builder = new StringBuilder();
        AppendQualifiedFamilySegments(builder, symbols);
        return builder.Length == 0 ? null : builder.ToString();
    }

    private static void AppendQualifiedFamilySegments(
        StringBuilder builder,
        IReadOnlyList<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
            AppendFamilySegment(builder, symbol);
    }

    private static void AppendFamilySegment(StringBuilder builder, SymbolRecord symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol.Name))
            return;
        if (builder.Length > 0)
            builder.Append('.');
        builder.Append(symbol.Name);
        var genericArity = CSharpTypeReferenceArity.GetDefinitionArity(
            symbol.Signature,
            symbol.Name,
            symbol.Kind);
        if (genericArity > 0)
        {
            builder.Append('`');
            builder.Append(genericArity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static bool SupportsCrossFileFamily(SymbolRecord symbol) =>
        symbol.Kind is "class" or "interface" or "struct" or "record"
        && (symbol.IsPartialDeclaration == true
            || (symbol.IsPartialDeclaration == null
                && !string.IsNullOrWhiteSpace(symbol.Signature)
                && PartialModifierRegex.IsMatch(symbol.Signature)));

    private static bool TryGetObjCCategoryDisplayName(string objcDeclaration, string baseName, out string displayName)
    {
        var match = ObjCCategoryDeclarationRegex.Match(objcDeclaration);
        if (!match.Success || !string.Equals(match.Groups["class"].Value, baseName, StringComparison.Ordinal))
        {
            displayName = string.Empty;
            return false;
        }

        var categoryName = match.Groups["category"].ValueSpan.Trim().ToString();
        if (categoryName.Length == 0)
        {
            displayName = string.Empty;
            return false;
        }

        displayName = $"{baseName}({categoryName})";
        return true;
    }

    private static bool CanContainSymbols(SymbolRecord symbol, bool includeCallableContainers)
    {
        if (includeCallableContainers
            && CallableContainerSelection.IsCallableKind(symbol.Kind)
            && symbol.BodyStartLine != null
            && symbol.BodyEndLine != null)
        {
            return true;
        }

        if (symbol.Kind == "function"
            && symbol.ContainerKind == "enum"
            && symbol.BodyStartLine != null
            && symbol.BodyEndLine != null)
        {
            return true;
        }

        if (!ContainerKinds.Contains(symbol.Kind))
            return false;

        if (IsFileScopedNamespace(symbol))
            return true;

        return symbol.BodyStartLine != null && symbol.BodyEndLine != null;
    }

    private static bool ContainsSymbol(
        SymbolRecord container,
        SymbolRecord candidate,
        string[]? rawLines = null,
        Func<CSharpLexState[]>? getCSharpLineStartStates = null)
    {
        if (IsFileScopedNamespace(container))
            return candidate.StartLine > container.StartLine;

        if (container.BodyStartLine == null || container.BodyEndLine == null)
            return false;

        if (candidate.StartLine == container.StartLine)
        {
            if (TryContainsCSharpSameLineSymbolByRawLine(container, candidate, rawLines, getCSharpLineStartStates, out var containsSameLineSymbol))
                return containsSameLineSymbol;

            return CanContainSameLineSymbol(container, candidate)
                && container.Signature != null
                && candidate.Signature != null
                && container.Signature.Contains(candidate.Signature, StringComparison.Ordinal);
        }

        if (TryContainsCSharpCallableEndLineSymbol(
                container,
                candidate,
                rawLines,
                getCSharpLineStartStates,
                out var containsCallableEndLineSymbol))
        {
            return containsCallableEndLineSymbol;
        }

        if (candidate.StartLine >= container.BodyStartLine
            && candidate.StartLine <= container.BodyEndLine
            && candidate.StartLine > container.StartLine)
        {
            return true;
        }

        return IsInsideCSharpClosingBraceLineContainer(container, candidate, rawLines, getCSharpLineStartStates);
    }

    private static bool TryContainsCSharpCallableEndLineSymbol(
        SymbolRecord container,
        SymbolRecord candidate,
        string[]? rawLines,
        Func<CSharpLexState[]>? getCSharpLineStartStates,
        out bool contains)
    {
        contains = false;
        if (rawLines == null
            || getCSharpLineStartStates == null
            || !CallableContainerSelection.IsCallableKind(container.Kind)
            || container.BodyEndLine == null
            || candidate.Signature == null
            || candidate.StartLine != container.BodyEndLine.Value
            || candidate.StartLine <= container.StartLine)
        {
            return false;
        }

        if (candidate.StartLine < container.EndLine)
        {
            contains = true;
            return true;
        }

        var lineIndex = candidate.StartLine - 1;
        if (lineIndex < 0 || lineIndex >= rawLines.Length)
            return false;

        var lineStartStates = getCSharpLineStartStates();
        if (lineIndex >= lineStartStates.Length)
            return false;

        var candidateColumn = FindSignatureOccurrenceStartColumn(
            rawLines[lineIndex],
            candidate.Signature,
            candidate.SameLineSignatureOccurrenceIndex ?? 0,
            lineStartStates[lineIndex]);
        if (candidateColumn < 0)
            return false;

        if (container.Signature?.Contains("=>", StringComparison.Ordinal) == true)
        {
            var sanitizedLine = LexCSharpLine(rawLines[lineIndex], lineStartStates[lineIndex]).SanitizedLine;
            var terminatorColumn = sanitizedLine.IndexOf(';');
            if (terminatorColumn < 0)
                return false;

            contains = candidateColumn < terminatorColumn;
            return true;
        }

        var closingBraceColumn = FindCSharpClosingBraceColumnOnContainerEndLine(container, rawLines);
        if (closingBraceColumn < 0)
            return false;

        contains = candidateColumn < closingBraceColumn;
        return true;
    }

    private static bool TryContainsCSharpSameLineSymbolByRawLine(
        SymbolRecord container,
        SymbolRecord candidate,
        string[]? rawLines,
        Func<CSharpLexState[]>? getCSharpLineStartStates,
        out bool contains)
    {
        contains = false;
        if (rawLines == null
            || container.Signature == null
            || candidate.Signature == null
            || container.StartLine != candidate.StartLine
            || container.StartLine <= 0
            || container.StartLine > rawLines.Length
            || !CanContainSameLineSymbol(container, candidate))
        {
            return false;
        }

        var lineIndex = container.StartLine - 1;
        var csharpLineStartStates = getCSharpLineStartStates?.Invoke();
        if (csharpLineStartStates == null || container.StartLine > csharpLineStartStates.Length)
            return false;

        var rawLine = rawLines[lineIndex];
        var lineStartState = csharpLineStartStates[lineIndex];
        var containerStartColumn = FindSignatureOccurrenceStartColumn(
            rawLine,
            container.Signature,
            container.SameLineSignatureOccurrenceIndex ?? 0,
            lineStartState);
        var candidateStartColumn = FindSignatureOccurrenceStartColumn(
            rawLine,
            candidate.Signature,
            candidate.SameLineSignatureOccurrenceIndex ?? 0,
            lineStartState);
        if (containerStartColumn < 0 || candidateStartColumn < 0)
            return false;

        if (container.BodyStartLine == container.StartLine
            && container.EndLine == container.StartLine)
        {
            var closingBraceColumn = FindCSharpSameLineContainerClosingBraceColumn(rawLine, containerStartColumn, lineStartState);
            if (closingBraceColumn < 0)
                return false;

            contains = candidateStartColumn > containerStartColumn
                && candidateStartColumn < closingBraceColumn;
            return true;
        }

        return false;
    }

    // A wrapped C# type can deliberately end its body one line earlier when the closing
    // brace line also starts an outer sibling (`} public int Q { get; }`). That keeps the
    // later outer sibling out of the inner container, but the last inner member may still
    // live earlier on that same closing-brace line (`public int P { get; } } public int Q`).
    // Reconstruct the matching closing-brace column on the raw end line and treat only the
    // declarations that start before that brace as inner members. Closes #549.
    // wrapped な C# type は、閉じ brace 行に outer sibling (`} public int Q { get; }`)
    // が続くとき、本体終端を 1 行手前へ倒して後続 sibling を inner container から外す。
    // ただし最後の inner member 自体が同じ閉じ brace 行の前半に載ることがあり
    // (`public int P { get; } } public int Q`)、そのままだと inner member まで外へ漏れる。
    // そこで raw end line 上で対応する closing brace 列を再構築し、その brace より前に
    // 始まる宣言だけを inner member として扱う。Closes #549.
    private static bool IsInsideCSharpClosingBraceLineContainer(
        SymbolRecord container,
        SymbolRecord candidate,
        string[]? rawLines,
        Func<CSharpLexState[]>? getCSharpLineStartStates)
    {
        if (rawLines == null
            || container.BodyStartLine == null
            || container.BodyEndLine == null
            || container.BodyEndLine.Value >= container.EndLine
            || candidate.Signature == null
            || candidate.StartLine != container.EndLine
            || candidate.StartLine <= container.StartLine)
        {
            return false;
        }

        var lineIndex = container.EndLine - 1;
        if (lineIndex < 0 || lineIndex >= rawLines.Length)
            return false;

        var closingBraceColumn = FindCSharpClosingBraceColumnOnContainerEndLine(container, rawLines);
        if (closingBraceColumn < 0)
            return false;

        var candidateColumn = FindSignatureOccurrenceStartColumn(
            rawLines[lineIndex],
            candidate.Signature,
            candidate.SameLineSignatureOccurrenceIndex ?? 0,
            getCSharpLineStartStates?.Invoke() is { } csharpLineStartStates
            && lineIndex < csharpLineStartStates.Length
                ? csharpLineStartStates[lineIndex]
                : new CSharpLexState());
        return candidateColumn >= 0 && candidateColumn < closingBraceColumn;
    }

    private static int FindCSharpClosingBraceColumnOnContainerEndLine(SymbolRecord container, string[] rawLines)
    {
        if (container.BodyStartLine == null
            || container.EndLine <= 0
            || container.EndLine > rawLines.Length
            || container.BodyStartLine.Value <= 0
            || container.BodyStartLine.Value > container.EndLine)
        {
            return -1;
        }

        var lexState = new CSharpLexState();
        var depth = 0;
        var endLineIndex = container.EndLine - 1;
        for (var lineIndex = container.BodyStartLine.Value - 1; lineIndex < endLineIndex; lineIndex++)
        {
            var lineResult = LexCSharpLine(rawLines[lineIndex], lexState);
            lexState = lineResult.EndState;

            foreach (var ch in lineResult.SanitizedLine)
            {
                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                }
            }
        }

        var sanitizedLine = LexCSharpLine(rawLines[endLineIndex], lexState).SanitizedLine;
        if (depth <= 0)
            return -1;

        for (var i = 0; i < sanitizedLine.Length; i++)
        {
            var ch = sanitizedLine[i];
            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int FindSignatureOccurrenceStartColumn(
        string rawLine,
        string signature,
        int occurrenceIndex,
        CSharpLexState lineStartState)
    {
        if (occurrenceIndex < 0 || string.IsNullOrEmpty(rawLine) || string.IsNullOrEmpty(signature))
            return -1;

        // Same-line C# occurrence tracking must ignore declaration lookalikes inside string
        // literals and comments, or the nth "real" declaration is mapped onto an earlier
        // quoted/commented copy of the same signature. LexCSharpLine preserves original
        // columns while blanking those regions, so the resulting indices still line up with
        // the raw line. Closes #558.
        // same-line C# の occurrence tracking は、文字列リテラルやコメント中の見かけ上の
        // 宣言を数えてはいけない。そうしないと n 個目の「本物の」宣言が、より前にある
        // quoted/commented な同一 signature へ誤対応付けされる。LexCSharpLine は元の列を
        // 保ったまま当該領域だけ空白化するので、得られる index は raw line と整合したまま使える。
        var searchLine = LexCSharpLine(rawLine, lineStartState).SanitizedLine;
        var currentOccurrence = 0;
        var searchStart = 0;
        while (searchStart < searchLine.Length)
        {
            var matchIndex = searchLine.IndexOf(signature, searchStart, StringComparison.Ordinal);
            if (matchIndex < 0)
                return -1;

            if (currentOccurrence == occurrenceIndex)
                return matchIndex;

            currentOccurrence++;
            searchStart = matchIndex + signature.Length;
        }

        return -1;
    }

    private static int FindCSharpSameLineContainerClosingBraceColumn(
        string rawLine,
        int containerStartColumn,
        CSharpLexState lineStartState)
    {
        if (containerStartColumn < 0 || containerStartColumn >= rawLine.Length)
            return -1;

        var sanitizedLine = LexCSharpLine(rawLine, lineStartState).SanitizedLine;
        var openBraceColumn = sanitizedLine.IndexOf('{', containerStartColumn);
        if (openBraceColumn < 0)
            return -1;

        var depth = 0;
        for (var i = openBraceColumn; i < sanitizedLine.Length; i++)
        {
            var ch = sanitizedLine[i];
            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static bool CanContainSameLineSymbol(SymbolRecord container, SymbolRecord candidate)
    {
        return (container.Kind, candidate.Kind) switch
        {
            ("function", _) when container.ContainerKind == "enum" && container.BodyStartLine != null && container.BodyEndLine != null => true,
            ("enum", "enum") => true,
            ("namespace", _) => true,
            ("class", _) => true,
            ("struct", _) => true,
            ("interface", _) => true,
            ("protocol", _) => true,
            _ => false,
        };
    }

    // C# file-scoped namespace: `namespace X;` with no braces. Matches only declarations whose
    // signature starts with the `namespace` keyword, so body-less namespace rows from other
    // languages (e.g. SQL `CREATE SCHEMA ...;` / `ALTER SCHEMA ...;`) are not treated as
    // file-scoped and therefore do not wrap every subsequent top-level symbol as their container.
    // C# の file-scoped namespace（`namespace X;` 形）だけを対象とする。`namespace` キーワードで
    // 始まるシグネチャに限定することで、SQL の `CREATE SCHEMA ...;` / `ALTER SCHEMA ...;` のような
    // 他言語の body 無し namespace 行が file-scoped namespace 扱いになり、以降のトップレベル
    // シンボル全てを自分の配下にぶら下げてしまう事故を防ぐ。
    private static bool IsFileScopedNamespace(SymbolRecord symbol)
    {
        if (symbol.Kind != "namespace")
            return false;
        if (symbol.BodyStartLine != null || symbol.BodyEndLine != null)
            return false;
        if (symbol.Signature == null)
            return false;
        var trimmed = symbol.Signature.AsSpan().TrimStart();
        return trimmed.StartsWith("namespace ", StringComparison.Ordinal)
            || trimmed.StartsWith("namespace\t", StringComparison.Ordinal);
    }
}
