using System.Globalization;
using System.Text.Json;
using CodeIndex.Database;

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
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false);
        if (TryWriteUnsupportedOptionError("excerpt", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("excerpt")))
            return CommandExitCodes.UsageError;
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
        if (options.FocusColumn == null && (options.FocusLine.HasValue || focusLengthSpecified))
        {
            var focusError = options.FocusLine.HasValue && focusLengthSpecified
                ? "--focus-line and --focus-length require --focus-column."
                : options.FocusLine.HasValue
                    ? "--focus-line requires --focus-column."
                    : "--focus-length requires --focus-column.";
            WriteValidationError(
                focusError,
                "Add `--focus-column <n>` so excerpt knows which token to keep visible inside the clamped line.");
            return CommandExitCodes.UsageError;
        }

        var filePathArgument = options.Query;
        var startLine = options.StartLine;
        var endLine = options.EndLine;
        if (startLine == null
            && TryParseExcerptLocationArgument(options.Query, out var parsedPath, out var parsedStartLine, out var parsedEndLine))
        {
            filePathArgument = parsedPath;
            startLine = parsedStartLine;
            endLine ??= parsedEndLine;
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
        if (endLineValue < startLineValue)
        {
            WriteValidationError(
                $"--start ({startLineValue}) must be less than or equal to --end ({endLineValue}).",
                "Use `--start` less than or equal to `--end`, or omit `--end` to read a single line.");
            return CommandExitCodes.UsageError;
        }

        var filePath = DbPathResolver.ResolveQueryFilePath(options.DbPath, filePathArgument, options.DbPathExplicit);
        return WithDb(options, jsonOptions, reader =>
        {
            if (options.FocusLine.HasValue)
            {
                var file = reader.GetFileByPath(filePath);
                if (file != null)
                {
                    var requestedStart = Math.Max(1, startLineValue - options.ContextBefore);
                    var requestedEnd = Math.Min(file.Lines, endLineValue + options.ContextAfter);
                    if (options.FocusLine.Value < requestedStart || options.FocusLine.Value > requestedEnd)
                    {
                        CommandErrorWriter.WriteStderr($"Error: --focus-line ({options.FocusLine.Value}) must be within the returned excerpt range ({requestedStart}-{requestedEnd}).");
                        return CommandExitCodes.UsageError;
                    }
                }
            }
            if (options.FocusColumn.HasValue)
            {
                var focusLineLength = reader.GetExcerptFocusLineLength(
                    filePath,
                    startLineValue,
                    endLineValue,
                    options.ContextBefore,
                    options.ContextAfter,
                    options.FocusLine ?? startLineValue);
                if (focusLineLength.HasValue && options.FocusColumn.Value > focusLineLength.Value)
                {
                    CommandErrorWriter.WriteStderr($"Error: --focus-column ({options.FocusColumn.Value}) must be within the focused line length ({focusLineLength.Value}).");
                    return CommandExitCodes.UsageError;
                }
            }

            var excerpt = reader.GetExcerpt(
                filePath,
                startLineValue,
                endLineValue,
                options.ContextBefore,
                options.ContextAfter,
                options.MaxLineWidth,
                options.FocusLine ?? startLineValue,
                options.FocusColumn,
                options.FocusLength);
            if (excerpt == null)
            {
                if (!options.Json)
                    CommandErrorWriter.WriteStderr("No excerpt found.");
                return ZeroResultExitCode(options);
            }
            if (options.Json)
            {
                ExcerptRecoveryCommandFormatter.ApplyDbPath(excerpt, options.DbPath);
                if (!options.NoSemanticTokens)
                    excerpt.SemanticTokens = BuildExcerptSemanticTokens(excerpt);
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(excerpt, CliJsonSerializerContextFactory.Create(jsonOptions).FileExcerptResult));
            }
            else
            {
                Console.WriteLine($"{excerpt.Path}:{excerpt.StartLine}-{excerpt.EndLine}");
                WriteNumberedExcerpt(excerpt.StartLine, excerpt.Content);
            }
            return CommandExitCodes.Success;
        });
    }

    private static bool TryParseExcerptLocationArgument(
        string value,
        out string path,
        out int startLine,
        out int? endLine)
    {
        path = string.Empty;
        startLine = 0;
        endLine = null;

        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            return false;

        var range = value[(separator + 1)..];
        var dash = range.IndexOf('-');
        if (dash < 0)
        {
            if (!TryParsePositiveLine(range, out startLine))
                return false;

            path = value[..separator];
            return true;
        }

        if (dash == 0 || dash == range.Length - 1 || range.IndexOf('-', dash + 1) >= 0)
            return false;

        if (!TryParsePositiveLine(range[..dash], out startLine)
            || !TryParsePositiveLine(range[(dash + 1)..], out var parsedEndLine))
        {
            return false;
        }

        path = value[..separator];
        endLine = parsedEndLine;
        return true;
    }

    private static bool TryParsePositiveLine(string value, out int line)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out line) && line > 0;

    private static List<ExcerptSemanticToken> BuildExcerptSemanticTokens(FileExcerptResult excerpt)
    {
        var tokens = new List<ExcerptSemanticToken>();
        var lines = excerpt.Content.Replace("\r\n", "\n").Split('\n');
        var spans = excerpt.ContentLineSpans.Count == 0
            ? BuildIdentityExcerptContentLineSpans(excerpt, lines)
            : excerpt.ContentLineSpans;
        foreach (var span in spans)
        {
            if (span.ContentLine <= 0 || span.ContentLine > lines.Length)
                continue;

            var line = lines[span.ContentLine - 1];
            var startColumn = Math.Clamp(span.ContentStartColumn - 1, 0, line.Length);
            var endColumn = Math.Clamp(span.ContentEndColumn - 1, startColumn, line.Length);
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
