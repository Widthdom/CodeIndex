using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class CobolReferenceExtractor
{
    private readonly record struct StatementPattern(Regex Regex, string ReferenceKind);

    private static readonly Regex CobolCallRegex = new(
        @"^\s*CALL\s+(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolCancelRegex = new(
        @"^\s*CANCEL\s+(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolCopyRegex = new(
        @"^\s*COPY\s+(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecSqlIncludeRegex = new(
        @"^\s*EXEC\s+SQL\s+INCLUDE\s+(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecSqlCallRegex = new(
        @"^\s*EXEC\s+SQL\s+CALL\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsProgramCallRegex = new(
        @"^\s*EXEC\s+CICS\s+(?:LINK|XCTL)\b.*?\bPROGRAM\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsHandleConditionCallRegex = new(
        @"^\s*EXEC\s+CICS\s+HANDLE\s+CONDITION\b.*?\b[A-Z0-9-]+\s*\(\s*(?<name>[A-Z0-9][A-Z0-9-]*)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsProgramReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+(?:LOAD)\b.*?\bPROGRAM\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsMapReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+(?:SEND|RECEIVE)\b.*?\bMAP\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsMapsetReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+(?:SEND|RECEIVE)\b.*?\bMAPSET\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsFileReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+(?:READ|WRITE|REWRITE|DELETE|STARTBR|READNEXT|READPREV|RESETBR|ENDBR|UNLOCK)\b.*?\bFILE\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsQueueReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+(?:READQ\s+(?:TS|TD)|WRITEQ\s+(?:TS|TD)|DELETEQ\s+TS)\b.*?\bQUEUE\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsResourceReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+(?:ENQ|DEQ)\b.*?\bRESOURCE\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsTransidReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+(?:START|RETURN)\b.*?\bTRANSID\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsAssignReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+ASSIGN\b.*?\bAPPLID\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsAddressReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+ADDRESS\b.*?\bCOMMAREA\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsStorageReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+(?:GETMAIN\b.*?\bSET|FREEMAIN\b.*?\bDATA)\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsReceiveReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+RECEIVE\b.*?\bINTO\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecCicsSendReferenceRegex = new(
        @"^\s*EXEC\s+CICS\s+SEND\b.*?\bFROM\s*\(\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolExecSqlSimpleReferenceRegex = new(
        @"^\s*EXEC\s+SQL\s+(?:FETCH|OPEN|CLOSE|PREPARE|EXECUTE)\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolGotoRegex = new(
        @"^\s*(?:GO\s+TO|GOTO)\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolPerformRegex = new(
        @"^\s*PERFORM\s+(?!(?:VARYING|UNTIL|WITH|TIMES|TEST|THRU|THROUGH)\b)(?<name>[A-Z0-9][A-Z0-9-]*)(?:\s+(?:THRU|THROUGH)\s+(?<end>[A-Z0-9][A-Z0-9-]*))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolUseAfterProcedureRegex = new(
        @"^\s*USE\s+AFTER\s+(?:STANDARD\s+)?(?:ERROR|EXCEPTION)\s+PROCEDURE\s+ON\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolSetRegex = new(
        @"^\s*SET\s+(?<name>[A-Z0-9][A-Z0-9-]*)\s+\b(?:TO\s+TRUE|TO\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolOpenRegex = new(
        @"^\s*OPEN\s+(?:INPUT|OUTPUT|I-O|EXTEND)\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolSearchRegex = new(
        @"^\s*SEARCH\s+(?:(?:ALL|FIRST)\s+)?(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolSimpleReferenceRegex = new(
        @"^\s*(?:READ|WRITE|REWRITE|DELETE|CLOSE|SORT|MERGE|INSPECT|DISPLAY|ACCEPT|START|RETURN|RELEASE|GENERATE|INITIATE|TERMINATE)\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolMoveRegex = new(
        @"^\s*MOVE\b.*?\bTO\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolAddRegex = new(
        @"^\s*ADD\b.*?\bTO\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolSubtractRegex = new(
        @"^\s*SUBTRACT\b.*?\bFROM\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolMultiplyRegex = new(
        @"^\s*MULTIPLY\b.*?\bBY\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolDivideRegex = new(
        @"^\s*DIVIDE\b.*?\bINTO\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolComputeRegex = new(
        @"^\s*COMPUTE\s+(?<name>[A-Z0-9][A-Z0-9-]*)\s*=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolStringRegex = new(
        @"^\s*STRING\b.*?\bINTO\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolUnstringRegex = new(
        @"^\s*UNSTRING\b.*?\bINTO\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly StatementPattern[] StatementPatterns =
    [
        new(CobolCallRegex, "call"),
        new(CobolCancelRegex, "reference"),
        new(CobolCopyRegex, "reference"),
        new(CobolExecSqlIncludeRegex, "reference"),
        new(CobolExecSqlCallRegex, "call"),
        new(CobolExecCicsProgramCallRegex, "call"),
        new(CobolExecCicsHandleConditionCallRegex, "call"),
        new(CobolExecCicsProgramReferenceRegex, "reference"),
        new(CobolExecCicsMapReferenceRegex, "reference"),
        new(CobolExecCicsMapsetReferenceRegex, "reference"),
        new(CobolExecCicsFileReferenceRegex, "reference"),
        new(CobolExecCicsQueueReferenceRegex, "reference"),
        new(CobolExecCicsResourceReferenceRegex, "reference"),
        new(CobolExecCicsTransidReferenceRegex, "reference"),
        new(CobolExecCicsAssignReferenceRegex, "reference"),
        new(CobolExecCicsAddressReferenceRegex, "reference"),
        new(CobolExecCicsStorageReferenceRegex, "reference"),
        new(CobolExecCicsReceiveReferenceRegex, "reference"),
        new(CobolExecCicsSendReferenceRegex, "reference"),
        new(CobolExecSqlSimpleReferenceRegex, "reference"),
        new(CobolGotoRegex, "call"),
        new(CobolUseAfterProcedureRegex, "reference"),
        new(CobolSetRegex, "reference"),
        new(CobolOpenRegex, "reference"),
        new(CobolSearchRegex, "reference"),
        new(CobolSimpleReferenceRegex, "reference"),
        new(CobolMoveRegex, "reference"),
        new(CobolAddRegex, "reference"),
        new(CobolSubtractRegex, "reference"),
        new(CobolMultiplyRegex, "reference"),
        new(CobolDivideRegex, "reference"),
        new(CobolComputeRegex, "reference"),
        new(CobolStringRegex, "reference"),
        new(CobolUnstringRegex, "reference"),
    ];

    public static void Emit(
        string rawLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container,
        IReadOnlyList<SymbolRecord>? cobolCallableSymbols)
    {
        foreach (var pattern in StatementPatterns)
            EmitMatches(pattern, rawLine, references, seen, fileId, context, lineNumber, container);

        foreach (Match match in CobolPerformRegex.Matches(rawLine))
        {
            var endName = match.Groups["end"].Value;
            if (!string.IsNullOrWhiteSpace(endName)
                && TryAddCobolPerformRangeReferences(
                    cobolCallableSymbols,
                    match.Groups["name"].Value,
                    endName,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    container))
            {
                continue;
            }

            EmitNamedReference(references, seen, fileId, match, "call", context, lineNumber, container);
        }
    }

    private static void EmitMatches(
        StatementPattern pattern,
        string rawLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in pattern.Regex.Matches(rawLine))
            EmitNamedReference(references, seen, fileId, match, pattern.ReferenceKind, context, lineNumber, container);
    }

    private static void EmitNamedReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Match match,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var name = match.Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(name))
            return;

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            name.ToUpperInvariant(),
            match.Groups["name"].Index,
            referenceKind,
            context,
            lineNumber,
            container);
    }

    private static bool TryAddCobolPerformRangeReferences(
        IReadOnlyList<SymbolRecord>? cobolCallableSymbols,
        string startName,
        string endName,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container)
    {
        if (cobolCallableSymbols == null || cobolCallableSymbols.Count == 0)
            return false;

        var normalizedStartName = startName.Trim();
        var normalizedEndName = endName.Trim();
        if (normalizedStartName.Length == 0 || normalizedEndName.Length == 0)
            return false;

        var startSymbol = FindCobolCallableSymbol(cobolCallableSymbols, normalizedStartName);
        if (startSymbol == null)
            return false;

        var startLine = startSymbol.Line;
        var endSymbol = FindCobolCallableSymbol(cobolCallableSymbols, normalizedEndName, startLine);
        if (endSymbol == null)
            return false;

        var lowerLine = Math.Min(startSymbol.Line, endSymbol.Line);
        var upperLine = Math.Max(startSymbol.Line, endSymbol.Line);
        var emittedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emittedAny = false;

        foreach (var symbol in cobolCallableSymbols)
        {
            if (symbol.Line < lowerLine || symbol.Line > upperLine)
                continue;

            if (!emittedNames.Add(symbol.Name))
                continue;

            var nameIndex = Math.Max(0, symbol.StartColumn.GetValueOrDefault() - 1);
            ReferenceExtractor.AddReference(references, seen, fileId, symbol.Name, nameIndex, "call", context, lineNumber, container);
            emittedAny = true;
        }

        return emittedAny;
    }

    private static SymbolRecord? FindCobolCallableSymbol(
        IReadOnlyList<SymbolRecord> cobolCallableSymbols,
        string targetName,
        int? minimumLine = null)
    {
        SymbolRecord? fallback = null;

        foreach (var symbol in cobolCallableSymbols)
        {
            if (!string.Equals(symbol.Name, targetName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (minimumLine == null || symbol.Line >= minimumLine.Value)
                return symbol;

            fallback ??= symbol;
        }

        return fallback;
    }
}
