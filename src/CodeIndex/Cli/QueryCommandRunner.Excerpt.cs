using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Semantics;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunExcerpt(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("excerpt", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: true);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var preparedArguments = PrepareExcerptArguments(cmdArgs);
        var options = ParseArgs(
            preparedArguments.Args,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false);
        if (TryWriteUnsupportedOptionError("excerpt", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("excerpt")))
            return CommandExitCodes.UsageError;
        if (TryWriteNonPositiveCoordinateRangeError(
                options,
                jsonOptions,
                includeHumanOutput: true,
                "--line",
                "--start",
                "--start-line",
                "--end",
                "--end-line"))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteParseError(options, "excerpt"))
            return CommandExitCodes.UsageError;
        if (options.Query == null)
        {
            WriteUsageError(
                "excerpt requires a path argument",
                GetUsageLineOrThrow("excerpt"),
                "Pass the indexed file path after `excerpt`, for example: `cdidx excerpt src/CodeIndex/Program.cs --start 20`.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("excerpt", options))
            return CommandExitCodes.UsageError;
        var focusLengthSpecified = cmdArgs.Any(arg => arg == "--focus-length" || arg.StartsWith("--focus-length=", StringComparison.Ordinal));
        if (options.FocusColumn == null && focusLengthSpecified)
        {
            WriteValidationError(
                "--focus-length requires --focus-column.",
                "Add `--focus-column <n>` so excerpt knows which token to keep visible inside the clamped line.");
            return CommandExitCodes.UsageError;
        }
        if ((options.StartColumn.HasValue || options.EndColumn.HasValue)
            && (options.MaxLineWidth != 0
                || options.ContextBefore != 0
                || options.ContextAfter != 0
                || options.FocusLine.HasValue
                || options.FocusColumn.HasValue))
        {
            WriteValidationError(
                "--start-column and --end-column require an unclamped, context-free excerpt.",
                "Add `--max-line-width 0` and omit context/focus options so source columns map exactly to the returned boundary lines.");
            return CommandExitCodes.UsageError;
        }

        var filePathArgument = options.Query;
        var startLine = options.StartLine;
        var endLine = options.EndLine;
        if (startLine == null)
        {
            var locationParsed = TryParseExcerptLocationArgument(
                options.Query,
                out var parsedPath,
                out var parsedStartLine,
                out var parsedEndLine,
                out var invalidLineValue);
            if (invalidLineValue != null)
            {
                return WriteExcerptOneBasedRangeError(
                    options,
                    jsonOptions,
                    invalidLineValue);
            }
            if (locationParsed)
            {
                filePathArgument = parsedPath;
                startLine = parsedStartLine;
                endLine ??= parsedEndLine;
            }
        }

        if (startLine == null)
        {
            WriteValidationError(
                "excerpt requires --start <line>",
                "Add a starting line number, for example: `cdidx excerpt src/CodeIndex/Program.cs --start 20` or `cdidx excerpt src/CodeIndex/Program.cs:20`.");
            return CommandExitCodes.UsageError;
        }

        var startLineValue = startLine.Value;
        var endLineValue = endLine ?? startLineValue;
        if (!preparedArguments.EndAtEof && endLineValue < startLineValue)
        {
            WriteValidationError(
                $"--start ({startLineValue}) must be less than or equal to --end ({endLineValue}).",
                "Use `--start` less than or equal to `--end`, or omit `--end` to read a single line.");
            return CommandExitCodes.UsageError;
        }

        var filePath = DbPathResolver.ResolveQueryFilePath(options.DbPath, filePathArgument, options.DbPathExplicit);
        return WithDb(options, jsonOptions, reader =>
        {
            var indexedFile = reader.GetFileByPath(filePath);
            if (indexedFile == null)
            {
                return CommandErrorWriter.WriteJsonOrHuman(
                    options.Json,
                    jsonOptions,
                    $"indexed file not found: {filePath}",
                    CommandExitCodes.NotFound,
                    "Use `cdidx files --json` to confirm the indexed path, then retry with that exact path.",
                    errorCode: CommandErrorCodes.FileNotFound,
                    category: "not_found");
            }

            var requestedEndLine = preparedArguments.EndAtEof
                ? indexedFile.Lines
                : endLineValue;
            if (indexedFile.Lines <= 0)
            {
                return WriteExcerptRangeOutsideFileError(
                    options,
                    jsonOptions,
                    startLineValue,
                    requestedEndLine,
                    indexedFile.Lines,
                    "the indexed file is empty");
            }

            var effectiveStartLine = startLineValue;
            var effectiveEndLine = requestedEndLine;
            if (preparedArguments.ClampRange)
            {
                effectiveStartLine = Math.Min(effectiveStartLine, indexedFile.Lines);
                effectiveEndLine = Math.Min(effectiveEndLine, indexedFile.Lines);
            }
            else if (startLineValue > indexedFile.Lines || requestedEndLine > indexedFile.Lines)
            {
                return WriteExcerptRangeOutsideFileError(
                    options,
                    jsonOptions,
                    startLineValue,
                    requestedEndLine,
                    indexedFile.Lines,
                    $"requested excerpt range {startLineValue}-{requestedEndLine} is outside {filePath} (1-{indexedFile.Lines})");
            }

            if (options.FocusLine.HasValue)
            {
                var requestedStart = Math.Max(1, effectiveStartLine - options.ContextBefore);
                var requestedEnd = Math.Min(indexedFile.Lines, effectiveEndLine + options.ContextAfter);
                if (options.FocusLine.Value < requestedStart || options.FocusLine.Value > requestedEnd)
                {
                    CommandErrorWriter.WriteStderr($"Error: --focus-line ({options.FocusLine.Value}) must be within the returned excerpt range ({requestedStart}-{requestedEnd}).");
                    return CommandExitCodes.UsageError;
                }
            }
            if (options.FocusColumn.HasValue)
            {
                var focusLineLength = reader.GetExcerptFocusLineLength(
                    filePath,
                    effectiveStartLine,
                    effectiveEndLine,
                    options.ContextBefore,
                    options.ContextAfter,
                    options.FocusLine ?? effectiveStartLine);
                if (focusLineLength.HasValue && options.FocusColumn.Value > focusLineLength.Value)
                {
                    CommandErrorWriter.WriteStderr($"Error: --focus-column ({options.FocusColumn.Value}) must be within the focused line length ({focusLineLength.Value}).");
                    return CommandExitCodes.UsageError;
                }
            }

            var excerpt = reader.GetExcerpt(
                filePath,
                effectiveStartLine,
                effectiveEndLine,
                options.ContextBefore,
                options.ContextAfter,
                options.MaxLineWidth,
                options.FocusLine ?? effectiveStartLine,
                options.FocusColumn,
                options.FocusLength);
            if (excerpt == null)
            {
                if (!options.Json)
                    CommandErrorWriter.WriteStderr("No excerpt found.");
                return ZeroResultExitCode(options);
            }
            if (!TryClipExcerptColumns(
                    excerpt,
                    options.StartColumn,
                    options.EndColumn,
                    out var columnRangeError))
            {
                WriteValidationError(
                    columnRangeError!,
                    "Use 1-based columns within the first and last returned source lines, with the start column not after the end column on a single-line excerpt.");
                return CommandExitCodes.UsageError;
            }
            excerpt.RequestedStartLine = startLineValue;
            excerpt.RequestedEndLine = requestedEndLine;
            if (options.Json)
            {
                ExcerptRecoveryCommandFormatter.ApplyDbPath(excerpt, options.DbPath, options.RedactPaths ?? true);
                if (!options.NoSemanticTokens)
                    excerpt.SemanticTokens = BuildExcerptSemanticTokens(excerpt, reader);
            }

            if (options.Json)
            {
                var payload = JsonSerializer.SerializeToNode(excerpt, CliJsonSerializerContextFactory.Create(jsonOptions).FileExcerptResult)!.AsObject();
                payload["requested_end_mode"] = preparedArguments.EndAtEof ? "eof" : "numeric";
                payload["range_clamped"] =
                    effectiveStartLine != startLineValue ||
                    effectiveEndLine != requestedEndLine;
                if (!options.NoSemanticTokens && excerpt.SemanticTokens is { Count: > 0 })
                    payload["semantic_tokens_hint"] = "Use --no-semantic-tokens to omit semantic_tokens for compact JSON.";
                var writeExitCode = WriteJsonPayloadWithOptionalByteLimit(
                    payload,
                    options,
                    jsonOptions,
                    "excerpt",
                    "excerpt",
                    "Use --no-semantic-tokens, reduce the excerpt range/context, clamp --max-line-width, or increase --max-json-bytes.");
                if (writeExitCode != CommandExitCodes.Success)
                    return writeExitCode;
            }
            else
            {
                Console.WriteLine($"{excerpt.Path}:{excerpt.StartLine}-{excerpt.EndLine}");
                WriteNumberedExcerpt(excerpt.StartLine, excerpt.Content);
            }
            return CommandExitCodes.Success;
        });
    }

    private static bool TryClipExcerptColumns(
        FileExcerptResult excerpt,
        int? startColumn,
        int? endColumn,
        out string? error)
    {
        error = null;
        if (!startColumn.HasValue && !endColumn.HasValue)
            return true;

        var lines = excerpt.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0)
            return true;

        var firstLineLength = lines[0].Length;
        var lastLineLength = lines[^1].Length;
        if (startColumn is int requestedStartColumn
            && requestedStartColumn > firstLineLength)
        {
            error = $"--start-column ({requestedStartColumn}) must be within the first returned line length ({firstLineLength}).";
            return false;
        }
        if (endColumn is int requestedEndColumn
            && requestedEndColumn > lastLineLength)
        {
            error = $"--end-column ({requestedEndColumn}) must be within the last returned line length ({lastLineLength}).";
            return false;
        }
        if (lines.Length == 1
            && startColumn.HasValue
            && endColumn.HasValue
            && startColumn.Value > endColumn.Value)
        {
            error = $"--start-column ({startColumn.Value}) must be less than or equal to --end-column ({endColumn.Value}) for a single-line excerpt.";
            return false;
        }

        var clippedLines = new string[lines.Length];
        var spans = new List<ExcerptContentLineSpan>(lines.Length);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var sourceStartColumn = lineIndex == 0 && startColumn.HasValue
                ? startColumn.Value
                : 1;
            var sourceEndColumn = lineIndex == lines.Length - 1 && endColumn.HasValue
                ? endColumn.Value
                : line.Length;
            var startIndex = sourceStartColumn - 1;
            var endIndexExclusive = sourceEndColumn;
            clippedLines[lineIndex] = line[startIndex..endIndexExclusive];
            spans.Add(new ExcerptContentLineSpan
            {
                ContentLine = lineIndex + 1,
                SourceLine = excerpt.StartLine + lineIndex,
                ContentStartColumn = 1,
                ContentEndColumn = clippedLines[lineIndex].Length,
                SourceStartColumn = sourceStartColumn,
                SourceEndColumn = sourceEndColumn,
            });
        }

        excerpt.Content = string.Join('\n', clippedLines);
        excerpt.ContentLineSpans = spans;
        return true;
    }

    private static bool TryParseExcerptLocationArgument(
        string value,
        out string path,
        out int startLine,
        out int? endLine,
        out string? invalidLineValue)
    {
        path = string.Empty;
        startLine = 0;
        endLine = null;
        invalidLineValue = null;

        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            return false;

        var range = value[(separator + 1)..];
        if (long.TryParse(range, NumberStyles.Integer, CultureInfo.InvariantCulture, out var singleLine)
            && singleLine <= 0)
        {
            invalidLineValue = range;
            return false;
        }
        var dash = range.StartsWith("-", StringComparison.Ordinal)
            ? range.IndexOf('-', 1)
            : range.IndexOf('-');
        if (dash < 0)
        {
            if (!TryParsePositiveLine(range, out startLine))
                return false;

            path = value[..separator];
            return true;
        }

        if (dash == range.Length - 1)
            return false;

        var startText = range[..dash];
        var endText = range[(dash + 1)..];
        if (long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var invalidStart)
            && invalidStart <= 0)
        {
            invalidLineValue = startText;
            return false;
        }
        if (long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var invalidEnd)
            && invalidEnd <= 0)
        {
            invalidLineValue = endText;
            return false;
        }
        if (!TryParsePositiveLine(startText, out startLine)
            || !TryParsePositiveLine(endText, out var parsedEndLine))
        {
            return false;
        }

        path = value[..separator];
        endLine = parsedEndLine;
        return true;
    }

    private static bool TryParsePositiveLine(string value, out int line)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out line) && line > 0;

    private static (string[] Args, bool EndAtEof, bool ClampRange) PrepareExcerptArguments(string[] args)
    {
        const string eofSentinel = "10000000";
        var prepared = new List<string>(args.Length);
        var endAtEof = false;
        var clampRange = false;
        var positionalOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (positionalOnly)
            {
                prepared.Add(argument);
                continue;
            }
            if (argument == "--")
            {
                positionalOnly = true;
                prepared.Add(argument);
                continue;
            }
            if (argument == "--clamp")
            {
                clampRange = true;
                continue;
            }
            if (argument is "--end" or "--end-line")
            {
                var acceptsEof = argument == "--end";
                prepared.Add(argument);
                endAtEof = false;
                if (i + 1 < args.Length)
                {
                    var value = args[++i];
                    endAtEof = acceptsEof
                        && string.Equals(value, "eof", StringComparison.OrdinalIgnoreCase);
                    prepared.Add(endAtEof ? eofSentinel : value);
                }
                continue;
            }

            var equalsIndex = argument.IndexOf('=');
            if (equalsIndex > 0
                && argument[..equalsIndex] is "--end" or "--end-line")
            {
                var option = argument[..equalsIndex];
                var value = argument[(equalsIndex + 1)..];
                endAtEof = option == "--end"
                    && string.Equals(value, "eof", StringComparison.OrdinalIgnoreCase);
                prepared.Add(endAtEof
                    ? argument[..(equalsIndex + 1)] + eofSentinel
                    : argument);
                continue;
            }

            prepared.Add(argument);
        }

        return (prepared.ToArray(), endAtEof, clampRange);
    }

    private static int WriteExcerptOneBasedRangeError(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string rawValue)
        => CommandErrorWriter.WriteJsonOrHuman(
            options.Json,
            jsonOptions,
            $"requested line {rawValue} is outside the valid range beginning at 1.",
            CommandExitCodes.InvalidArgument,
            "Use a line number of 1 or greater.",
            GetUsageLineOrThrow("excerpt"),
            CommandErrorCodes.LineOutOfRange,
            category: "range",
            command: "excerpt");

    private static int WriteExcerptRangeOutsideFileError(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        int requestedStartLine,
        int requestedEndLine,
        int totalLines,
        string message)
    {
        var startBeyondEof = totalLines > 0 && requestedStartLine > totalLines;
        return CommandErrorWriter.WriteJsonOrHuman(
            options.Json,
            jsonOptions,
            message,
            CommandExitCodes.InvalidArgument,
            totalLines == 0
                ? "The indexed file has no one-based line range to read."
                : startBeyondEof
                    ? $"Use `--start {totalLines}` or earlier, or add `--clamp` to explicitly clamp the range to the indexed file."
                    : $"Use `--end eof` to read through line {totalLines}, or add `--clamp` to explicitly clamp numeric overshoot.",
            GetUsageLineOrThrow("excerpt"),
            CommandErrorCodes.LineOutOfRange,
            category: "range",
            command: "excerpt",
            additionalJsonProperties: new JsonObject
            {
                ["requested_start_line"] = requestedStartLine,
                ["requested_end_line"] = requestedEndLine,
                ["total_lines"] = totalLines,
                ["range_recovery"] = new JsonObject
                {
                    ["strict_numeric_default"] = true,
                    ["end_at_eof_supported"] = totalLines > 0 && !startBeyondEof,
                    ["clamp_supported"] = true,
                    ["suggested_start_line"] = startBeyondEof ? totalLines : null,
                    ["suggested_end_line"] = totalLines > 0 && !startBeyondEof ? totalLines : null,
                },
            });
    }

    private static List<ExcerptSemanticToken> BuildExcerptSemanticTokens(
        FileExcerptResult excerpt,
        DbReader reader)
    {
        var tokens = new List<ExcerptSemanticToken>();
        var lines = excerpt.Content.Replace("\r\n", "\n").Split('\n');
        var spans = excerpt.ContentLineSpans.Count == 0
            ? BuildIdentityExcerptContentLineSpans(excerpt, lines)
            : excerpt.ContentLineSpans;
        var isCSharp = string.Equals(excerpt.Lang, "csharp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(excerpt.Path), ".cs", StringComparison.OrdinalIgnoreCase);
        var indexedSourceLines = isCSharp
            ? reader.GetIndexedSourceLinesForSemanticTokens(
                excerpt.Path,
                CSharpSemanticTokenClassifier.DefaultExcerptSourceLineLimit,
                CSharpSemanticTokenClassifier.DefaultExcerptSourceCharacterLimit)
            : [];
        var classifiesIndexedSource = indexedSourceLines.Count > 0;
        var includedSourceLines = classifiesIndexedSource
            ? spans
                .Where(span => span.SourceLine > 0)
                .Select(span => span.SourceLine - 1)
                .ToHashSet()
            : null;
        var classifiedCSharpTokens = isCSharp
            ? CSharpSemanticTokenClassifier.Classify(
                classifiesIndexedSource ? indexedSourceLines : lines,
                CSharpSemanticTokenClassifier.DefaultExcerptTokenLimit,
                includedSourceLines)
            : [];
        var visibleCSharpTokens = isCSharp && classifiesIndexedSource
            ? CSharpSemanticTokenClassifier.Classify(
                lines,
                CSharpSemanticTokenClassifier.DefaultExcerptTokenLimit)
            : [];
        var csharpTokenRanges = new HashSet<(int Line, int StartColumn, int EndColumn)>();
        foreach (var span in spans)
        {
            if (span.ContentLine <= 0 || span.ContentLine > lines.Length)
                continue;

            var line = lines[span.ContentLine - 1];
            var startColumn = Math.Clamp(span.ContentStartColumn - 1, 0, line.Length);
            var endColumn = Math.Clamp(span.ContentEndColumn - 1, startColumn, line.Length);
            if (isCSharp)
            {
                var classifiedLine = classifiesIndexedSource
                    ? span.SourceLine - 1
                    : span.ContentLine - 1;
                var classifiedStartColumn = classifiesIndexedSource
                    ? span.SourceStartColumn - 1
                    : startColumn;
                var classifiedEndColumn = classifiesIndexedSource
                    ? span.SourceEndColumn - 1
                    : endColumn;
                foreach (var token in classifiedCSharpTokens)
                {
                    if (token.Line != classifiedLine ||
                        token.StartCharacter < classifiedStartColumn ||
                        token.StartCharacter + token.Length > classifiedEndColumn)
                    {
                        continue;
                    }

                    var sourceStartColumn = span.SourceStartColumn +
                        token.StartCharacter -
                        classifiedStartColumn;
                    AddCSharpToken(token, sourceStartColumn);
                }

                if (classifiesIndexedSource)
                {
                    foreach (var token in visibleCSharpTokens)
                    {
                        if (token.Line != span.ContentLine - 1 ||
                            token.StartCharacter < startColumn ||
                            token.StartCharacter + token.Length > endColumn)
                        {
                            continue;
                        }

                        var sourceStartColumn = span.SourceStartColumn +
                            token.StartCharacter -
                            startColumn;
                        AddCSharpToken(token, sourceStartColumn);
                    }
                }
                continue;
            }

            var column = startColumn;
            while (column < endColumn)
            {
                if (!IsSemanticTokenStart(line[column]))
                {
                    column++;
                    continue;
                }

                var start = column;
                column++;
                while (column < endColumn && IsSemanticTokenPart(line[column]))
                    column++;

                var tokenText = line[start..column];
                var sourceStartColumn = span.SourceStartColumn + ((start + 1) - span.ContentStartColumn);
                var sourceEndColumn = span.SourceStartColumn + ((column + 1) - span.ContentStartColumn);
                tokens.Add(new ExcerptSemanticToken
                {
                    StartLine = span.SourceLine,
                    StartColumn = sourceStartColumn,
                    EndLine = span.SourceLine,
                    EndColumn = sourceEndColumn,
                    Type = ClassifySemanticToken(tokenText),
                });
            }

            void AddCSharpToken(
                ClassifiedCSharpSemanticToken token,
                int sourceStartColumn)
            {
                var sourceEndColumn = sourceStartColumn + token.Length;
                if (tokens.Count >= CSharpSemanticTokenClassifier.DefaultExcerptTokenLimit ||
                    !csharpTokenRanges.Add((
                        span.SourceLine,
                        sourceStartColumn,
                        sourceEndColumn)))
                {
                    return;
                }

                tokens.Add(new ExcerptSemanticToken
                {
                    StartLine = span.SourceLine,
                    StartColumn = sourceStartColumn,
                    EndLine = span.SourceLine,
                    EndColumn = sourceEndColumn,
                    Type = CSharpSemanticTokenClassifier.ToProtocolName(token.Kind),
                    Modifiers = token.IsDeclaration ? ["declaration"] : [],
                });
            }
        }

        return tokens;
    }

    private static List<ExcerptContentLineSpan> BuildIdentityExcerptContentLineSpans(FileExcerptResult excerpt, string[] lines)
    {
        var spans = new List<ExcerptContentLineSpan>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            spans.Add(new ExcerptContentLineSpan
            {
                ContentLine = i + 1,
                SourceLine = excerpt.StartLine + i,
                ContentStartColumn = 1,
                ContentEndColumn = lines[i].Length + 1,
                SourceStartColumn = 1,
                SourceEndColumn = lines[i].Length + 1,
            });
        }

        return spans;
    }

    private static bool IsSemanticTokenStart(char value) =>
        char.IsLetter(value) || value == '_' || char.IsDigit(value);

    private static bool IsSemanticTokenPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static string ClassifySemanticToken(string token)
    {
        if (token.All(char.IsDigit))
            return "number";
        if (char.IsUpper(token[0]))
            return "type";
        return "variable";
    }
}
