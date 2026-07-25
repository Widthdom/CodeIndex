using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Lsp;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ProgramRunner
{
    private static int RunTestExtractor(string[] args, JsonSerializerOptions jsonOptions)
    {
        string? language = null;
        string? file = null;
        string? expect = null;
        var json = args.Contains("--json", StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (TryConsumeInlineOrNext(args, ref i, arg, "--language", out var value))
                language = value;
            else if (TryConsumeInlineOrNext(args, ref i, arg, "--file", out value))
                file = value;
            else if (TryConsumeInlineOrNext(args, ref i, arg, "--expect-symbols", out value) || TryConsumeInlineOrNext(args, ref i, arg, "--expect", out value))
                expect = value;
            else if (arg == "--json")
                continue;
            else
                return WriteTestExtractorError(json, jsonOptions, $"Unknown test-extractor argument: {arg}", CommandExitCodes.InvalidArgument, "use --language <lang> --file <path> [--expect-symbols <json>] [--json].");
        }

        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(file))
            return WriteTestExtractorError(json, jsonOptions, "test-extractor requires --language and --file.", CommandExitCodes.InvalidArgument, "use --language <lang> --file <path> [--expect-symbols <json>] [--json].");
        if (!TryReadTestExtractorFile(file, "source", json, jsonOptions, out var source, out var readExitCode))
            return readExitCode;

        var symbols = Indexer.SymbolExtractor.Extract(1, language, source, file);
        if (expect != null)
        {
            if (!TryReadTestExtractorFile(expect, "expected symbols", json, jsonOptions, out var expected, out readExitCode))
                return readExitCode;
            var actual = JsonSerializer.Serialize(symbols);
            if (!TryJsonEquivalent(expected, actual, out var jsonError))
            {
                if (jsonError is not null)
                {
                    return WriteTestExtractorError(
                        json,
                        jsonOptions,
                        $"test-extractor expected or actual symbols JSON could not be parsed within the {TestExtractorJsonComparisonMaxBytes} byte and {TestExtractorJsonComparisonMaxDepth} depth limits: {jsonError.Message}",
                        CommandExitCodes.InvalidArgument,
                        "Use a smaller or shallower expected-symbols JSON fixture.");
                }

                if (json)
                {
                    return WriteTestExtractorError(
                        true,
                        jsonOptions,
                        "Expected symbols did not match extracted symbols.",
                        CommandExitCodes.InvalidArgument,
                        "Update the expected-symbols fixture or inspect the extracted symbols without --expect-symbols.");
                }
                CommandErrorWriter.WriteStderr("Expected symbols did not match extracted symbols.");
                CommandErrorWriter.WriteStderr(actual);
                return CommandExitCodes.InvalidArgument;
            }
        }

        if (json || expect == null)
        {
            var result = new TestExtractorJsonResult(JsonSerializer.SerializeToElement(symbols));
            CommandOutputWriter.WriteJson(
                result,
                CliJsonSerializerContextFactory.Create(jsonOptions).TestExtractorJsonResult);
        }
        return CommandExitCodes.Success;
    }

    private static bool TryReadTestExtractorFile(
        string path,
        string role,
        bool json,
        JsonSerializerOptions jsonOptions,
        out string content,
        out int exitCode)
    {
        content = string.Empty;
        exitCode = CommandExitCodes.Success;
        var displayRole = $"test-extractor {role} file";
        if (!File.Exists(LongPath.EnsureWindowsPrefix(path)))
        {
            exitCode = WriteTestExtractorError(json, jsonOptions, $"{displayRole} not found: {path}", CommandExitCodes.NotFound);
            return false;
        }

        try
        {
            using var stream = BoundedFile.OpenReadForLengthCheckedText(path);
            if (stream.Length > TestExtractorMaxInputBytes)
            {
                exitCode = WriteTestExtractorTooLargeError(json, jsonOptions, displayRole, stream.Length);
                return false;
            }

            TestExtractorFileLengthCheckedForTesting?.Invoke(path);
            if (!TryReadTestExtractorStream(stream, displayRole, json, jsonOptions, out content, out exitCode))
                return false;

            return true;
        }
        catch (IOException ex)
        {
            exitCode = WriteTestExtractorError(json, jsonOptions, $"{displayRole} could not be read: {FormatSanitizedExceptionSummary(ex)}", CommandExitCodes.InvalidArgument);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            exitCode = WriteTestExtractorError(json, jsonOptions, $"{displayRole} could not be read: {FormatSanitizedExceptionSummary(ex)}", CommandExitCodes.InvalidArgument);
            return false;
        }
    }

    private static bool TryReadTestExtractorStream(
        Stream stream,
        string displayRole,
        bool json,
        JsonSerializerOptions jsonOptions,
        out string content,
        out int exitCode)
    {
        content = string.Empty;
        exitCode = CommandExitCodes.Success;
        using var buffer = new MemoryStream(capacity: (int)Math.Min(TestExtractorMaxInputBytes, Math.Max(0, stream.Length)));
        var scratch = new byte[TestExtractorReadBufferBytes];
        long bytesRead = 0;
        while (true)
        {
            var remainingBudget = TestExtractorMaxInputBytes + 1 - bytesRead;
            if (remainingBudget <= 0)
            {
                exitCode = WriteTestExtractorTooLargeError(json, jsonOptions, displayRole, bytesRead);
                return false;
            }

            var read = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, remainingBudget));
            if (read == 0)
                break;

            bytesRead += read;
            if (bytesRead > TestExtractorMaxInputBytes)
            {
                exitCode = WriteTestExtractorTooLargeError(json, jsonOptions, displayRole, bytesRead);
                return false;
            }

            buffer.Write(scratch, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        // The loop above rejects streams beyond TestExtractorMaxInputBytes before this materializes text.
        content = reader.ReadToEnd();
        return true;
    }

    private static int WriteTestExtractorTooLargeError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string displayRole,
        long bytes)
        => WriteTestExtractorError(
            json,
            jsonOptions,
            $"{displayRole} is too large: {bytes} bytes exceeds the {TestExtractorMaxInputBytes} byte limit.",
            CommandExitCodes.InvalidArgument,
            "Use a smaller extractor fixture or expectation file.");

    private static int WriteTestExtractorError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string message,
        int exitCode,
        string? hint = null)
        => CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, message, exitCode, hint);

    private static bool TryConsumeInlineOrNext(string[] args, ref int index, string arg, string flag, out string value)
    {
        value = string.Empty;
        if (arg.StartsWith(flag + "=", StringComparison.Ordinal))
        {
            value = arg[(flag.Length + 1)..];
            return true;
        }

        if (arg != flag || index + 1 >= args.Length)
            return false;

        value = args[++index];
        return true;
    }

    private static bool TryJsonEquivalent(string expected, string actual, out Exception? error)
    {
        error = null;
        try
        {
            using var expectedDoc = BoundedJson.ParseDocument(
                expected,
                TestExtractorJsonComparisonMaxBytes,
                TestExtractorJsonComparisonMaxDepth);
            using var actualDoc = BoundedJson.ParseDocument(
                actual,
                TestExtractorJsonComparisonMaxBytes,
                TestExtractorJsonComparisonMaxDepth);
            return JsonSerializer.Serialize(expectedDoc.RootElement) == JsonSerializer.Serialize(actualDoc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            error = ex;
            return false;
        }
    }
}
