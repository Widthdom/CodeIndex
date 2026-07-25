using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class DynamicDeclarativeReferenceExtractor
{
    private static string PreparePrologCallScanLine(
        string language,
        string line,
        bool isClauseContinuation)
    {
        if (language is not ("prolog" or "ambiguous_pl"))
            return line;

        var masked = line.ToCharArray();
        var clauseStartColumn = isClauseContinuation
            ? FindPrologClauseTerminator(line, 0) + 1
            : 0;
        if (isClauseContinuation && clauseStartColumn == 0)
            return line;
        var changed = false;
        while (TryFindNextPrologClauseStart(line, clauseStartColumn, out var headStartColumn)
            && TryFindPrologHeadBoundary(
                line,
                headStartColumn,
                out var bodyStartColumn,
                out var clauseEndColumn))
        {
            if (bodyStartColumn >= 0)
            {
                FillWithSpaces(masked, headStartColumn, bodyStartColumn);
                changed = true;
                if (clauseEndColumn < 0)
                    break;
            }
            else
            {
                FillWithSpaces(masked, headStartColumn, clauseEndColumn + 1);
                changed = true;
            }

            clauseStartColumn = clauseEndColumn + 1;
        }

        return changed ? new string(masked) : line;
    }

    private static bool TryFindNextPrologClauseStart(
        string line,
        int searchColumn,
        out int clauseStartColumn)
    {
        for (var column = Math.Max(0, searchColumn); column < line.Length; column++)
        {
            if (char.IsWhiteSpace(line[column]))
                continue;

            clauseStartColumn = column;
            return char.IsLower(line[column]);
        }

        clauseStartColumn = -1;
        return false;
    }

    private static bool TryFindPrologHeadBoundary(
        string line,
        int headStartColumn,
        out int bodyStartColumn,
        out int clauseEndColumn)
    {
        bodyStartColumn = -1;
        clauseEndColumn = -1;
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var column = headStartColumn; column < line.Length; column++)
        {
            var ch = line[column];
            if (ch is '\'' or '"')
            {
                column = SkipQuotedToken(line, column, ch) - 1;
                continue;
            }
            switch (ch)
            {
                case '(':
                    parenthesisDepth++;
                    continue;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    continue;
            }
            if (parenthesisDepth != 0 || bracketDepth != 0 || braceDepth != 0)
                continue;

            var separatorLength = line.AsSpan(column).StartsWith("-->", StringComparison.Ordinal)
                ? 3
                : line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal)
                    ? 2
                    : 0;
            if (separatorLength > 0)
            {
                bodyStartColumn = column + separatorLength;
                clauseEndColumn = FindPrologClauseTerminator(line, bodyStartColumn);
                return true;
            }
            if (IsPrologClauseTerminator(line, column))
            {
                clauseEndColumn = column;
                return true;
            }
        }

        return false;
    }

    internal static bool IsPrologClauseTerminator(string line, int column)
    {
        if (column < 0 || column >= line.Length || line[column] != '.')
            return false;

        return PrologClauseTerminatorMaps
            .GetValue(line, static currentLine => new PrologClauseTerminatorMap(currentLine))
            .IsTerminator(column);
    }

    private sealed class PrologClauseTerminatorMap
    {
        private readonly bool[] _terminatorColumns;

        public PrologClauseTerminatorMap(string line)
        {
            _terminatorColumns = new bool[line.Length];
            var parenthesisDepth = 0;
            var bracketDepth = 0;
            var braceDepth = 0;
            for (var column = 0; column < line.Length; column++)
            {
                var ch = line[column];
                if (ch is '\'' or '"')
                {
                    column = SkipQuotedToken(line, column, ch) - 1;
                    continue;
                }
                switch (ch)
                {
                    case '(':
                        parenthesisDepth++;
                        continue;
                    case ')' when parenthesisDepth > 0:
                        parenthesisDepth--;
                        continue;
                    case '[':
                        bracketDepth++;
                        continue;
                    case ']' when bracketDepth > 0:
                        bracketDepth--;
                        continue;
                    case '{':
                        braceDepth++;
                        continue;
                    case '}' when braceDepth > 0:
                        braceDepth--;
                        continue;
                }

                if (ch != '.'
                    || parenthesisDepth != 0
                    || bracketDepth != 0
                    || braceDepth != 0)
                {
                    continue;
                }

                var previous = column > 0 ? line[column - 1] : '\0';
                var next = column + 1 < line.Length ? line[column + 1] : '\0';
                if (previous != '.'
                    && next != '.'
                    && !(char.IsDigit(previous) && char.IsDigit(next))
                    && (next == '\0' || char.IsWhiteSpace(next)))
                {
                    _terminatorColumns[column] = true;
                }
            }
        }

        public bool IsTerminator(int column) => _terminatorColumns[column];
    }

    private static int FindPrologClauseTerminator(string line, int startColumn)
    {
        for (var column = Math.Max(0, startColumn); column < line.Length; column++)
        {
            if (IsPrologClauseTerminator(line, column))
                return column;
        }

        return -1;
    }

    private static void EmitImportReference(
        string language,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var match = language switch
        {
            "crystal" => CrystalRequireRegex.Match(originalLine),
            "groovy" => GroovyImportRegex.Match(originalLine),
            "tcl" => TclPackageRegex.Match(originalLine),
            "prolog" or "ambiguous_pl" => PrologImportRegex.Match(originalLine),
            _ => Match.Empty,
        };
        if (!match.Success)
            return;

        var nameGroup = match.Groups["name"];
        var name = NormalizeImportTarget(language, nameGroup.Value);
        if (name.Length == 0)
            return;

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            name,
            nameGroup.Index,
            "type_reference",
            context,
            lineNumber,
            resolveContainerForCall(nameGroup.Index),
            language);
    }

    private static string NormalizeImportTarget(string language, string name)
    {
        var normalized = name.Replace('\\', '/').TrimEnd('/');
        if (language == "groovy")
            return normalized[(normalized.LastIndexOf('.') + 1)..];
        if (language is "crystal" or "prolog" or "ambiguous_pl")
        {
            normalized = normalized[(normalized.LastIndexOf('/') + 1)..];
            var extensionIndex = normalized.LastIndexOf('.');
            if (extensionIndex > 0)
                normalized = normalized[..extensionIndex];
        }
        return normalized;
    }

}
