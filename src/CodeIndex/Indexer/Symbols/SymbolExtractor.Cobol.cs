using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractCobolParagraphSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        string? programName = null;
        var inProcedureDivision = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (MayContainCobolProgramIdLine(line))
            {
                var programIdMatch = CobolProgramIdLineRegex.Match(line);
                if (programIdMatch.Success)
                {
                    programName = CobolSymbolNameNormalizer.Normalize(programIdMatch.Groups["name"].Value);
                    inProcedureDivision = false;
                    continue;
                }
            }

            if (IsCobolProcedureDivisionLine(line))
            {
                inProcedureDivision = true;
                continue;
            }

            if (IsCobolEndProgramLine(line))
            {
                programName = null;
                inProcedureDivision = false;
                continue;
            }

            if (!inProcedureDivision)
                continue;

            if (line.Contains("ENTRY", StringComparison.OrdinalIgnoreCase))
            {
                var entryMatch = CobolEntryRegex.Match(line);
                if (entryMatch.Success)
                {
                    var entryName = CobolSymbolNameNormalizer.Normalize(entryMatch.Groups["name"].Value);
                    if (string.IsNullOrWhiteSpace(entryName))
                        continue;

                    AddSymbolRecord(
                        symbols,
                        cssSeenSymbols: null,
                        i + 1,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "function",
                            Name = entryName,
                            Line = i + 1,
                            StartLine = i + 1,
                            StartColumn = entryMatch.Groups["name"].Index,
                            EndLine = i + 1,
                            Signature = line.Trim(),
                            ContainerKind = programName != null ? "class" : null,
                            ContainerName = programName,
                            ContainerQualifiedName = programName,
                        },
                        line);
                    continue;
                }
            }

            if (line.Contains("SECTION", StringComparison.OrdinalIgnoreCase))
            {
                var sectionMatch = CobolSectionHeaderRegex.Match(line);
                if (sectionMatch.Success)
                {
                    var sectionName = CobolSymbolNameNormalizer.Normalize(sectionMatch.Groups["name"].Value);
                    if (string.IsNullOrWhiteSpace(sectionName))
                        continue;

                    var (sectionEndLine, sectionBodyStartLine, sectionBodyEndLine) = FindCobolSectionRange(lines, i);
                    AddSymbolRecord(
                        symbols,
                        cssSeenSymbols: null,
                        i + 1,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "function",
                            Name = sectionName,
                            Line = i + 1,
                            StartLine = i + 1,
                            StartColumn = sectionMatch.Groups["name"].Index,
                            EndLine = sectionEndLine,
                            BodyStartLine = sectionBodyStartLine,
                            BodyEndLine = sectionBodyEndLine,
                            Signature = line.Trim(),
                            ContainerKind = programName != null ? "class" : null,
                            ContainerName = programName,
                            ContainerQualifiedName = programName,
                        },
                        line);
                    continue;
                }
            }

            if (line.IndexOf('.') < 0)
                continue;

            var paragraphMatch = CobolParagraphHeaderRegex.Match(line);
            if (!paragraphMatch.Success)
                continue;

            var name = CobolSymbolNameNormalizer.Normalize(paragraphMatch.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var (endLine, bodyStartLine, bodyEndLine) = FindCobolParagraphRange(lines, i);
            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                i + 1,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = name,
                    Line = i + 1,
                    StartLine = i + 1,
                    StartColumn = paragraphMatch.Groups["name"].Index,
                    EndLine = endLine,
                    BodyStartLine = bodyStartLine,
                    BodyEndLine = bodyEndLine,
                    Signature = line.Trim(),
                    ContainerKind = programName != null ? "class" : null,
                    ContainerName = programName,
                    ContainerQualifiedName = programName,
                },
                line);
        }
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindCobolParagraphRange(string[] lines, int startIndex)
    {
        int? bodyStartLine = null;

        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("*", StringComparison.Ordinal))
                continue;

            if (IsCobolRangeBoundaryLine(line))
            {
                if (bodyStartLine == null)
                    return (startIndex + 1, null, null);

                return (i, bodyStartLine, i);
            }

            bodyStartLine ??= i + 1;
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (lines.Length, bodyStartLine, lines.Length);
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindCobolSectionRange(string[] lines, int startIndex)
    {
        int? bodyStartLine = null;

        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("*", StringComparison.Ordinal))
                continue;

            if (IsCobolProgramIdLine(line)
                || IsCobolProcedureDivisionLine(line)
                || IsCobolEndProgramLine(line)
                || IsCobolSectionHeaderLine(line))
            {
                if (bodyStartLine == null)
                    return (startIndex + 1, null, null);

                return (i, bodyStartLine, i);
            }

            bodyStartLine ??= i + 1;
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (lines.Length, bodyStartLine, lines.Length);
    }

    private static bool IsCobolRangeBoundaryLine(string line) =>
        IsCobolProgramIdLine(line)
        || IsCobolProcedureDivisionLine(line)
        || IsCobolEndProgramLine(line)
        || IsCobolSectionHeaderLine(line)
        || IsCobolParagraphHeaderLine(line);

    private static bool IsCobolProgramIdLine(string line) =>
        MayContainCobolProgramIdLine(line)
        && CobolProgramIdLineRegex.IsMatch(line);

    private static bool MayContainCobolProgramIdLine(string line) =>
        line.Contains("PROGRAM-ID", StringComparison.OrdinalIgnoreCase)
        || line.Contains("CLASS-ID", StringComparison.OrdinalIgnoreCase)
        || line.Contains("IDENTIFICATION", StringComparison.OrdinalIgnoreCase);

    private static bool IsCobolProcedureDivisionLine(string line) =>
        line.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase)
        && CobolProcedureDivisionRegex.IsMatch(line);

    private static bool IsCobolEndProgramLine(string line) =>
        line.Contains("END", StringComparison.OrdinalIgnoreCase)
        && CobolEndProgramRegex.IsMatch(line);

    private static bool IsCobolSectionHeaderLine(string line) =>
        line.Contains("SECTION", StringComparison.OrdinalIgnoreCase)
        && CobolSectionHeaderRegex.IsMatch(line);

    private static bool IsCobolParagraphHeaderLine(string line) =>
        line.IndexOf('.') >= 0
        && CobolParagraphHeaderRegex.IsMatch(line);

}
