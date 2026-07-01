using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private const int DockerfileJsonFormIssueLimit = 32;

    private static void AddDockerfileJsonFormIssues(List<FileIssue> issues, string relativePath, string content)
    {
        var emitted = 0;
        var diagnosticsTruncated = false;
        var lineNumber = 1;
        var lineStart = 0;
        while (lineStart <= content.Length)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = content.Length;

            var line = content[lineStart..lineEnd];
            if (TryGetDockerfileJsonFormPayload(line, out var instruction, out var payload))
            {
                if (!TryAddDockerfileJsonFormIssue(issues, relativePath, instruction, payload, lineNumber, ref emitted))
                {
                    diagnosticsTruncated = true;
                    break;
                }
            }

            if (lineEnd == content.Length)
                break;

            lineNumber++;
            lineStart = lineEnd + 1;
        }

        if (diagnosticsTruncated)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "dockerfile_json_form_issue_limit_reached",
                Line = 0,
                Message = $"Dockerfile JSON-form diagnostics capped at {DockerfileJsonFormIssueLimit} issues",
                Severity = FileIssue.SeverityWarning,
            });
        }
    }

    private static bool TryAddDockerfileJsonFormIssue(
        List<FileIssue> issues,
        string relativePath,
        string instruction,
        string payload,
        int lineNumber,
        ref int emitted)
    {
        try
        {
            using var document = SymbolExtractor.ParseDockerfileJsonFormPayload(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return true;

            var count = 0;
            foreach (var _ in document.RootElement.EnumerateArray())
            {
                count++;
                if (count <= SymbolExtractor.DockerfileJsonFormMaxItems)
                    continue;

                if (!TryAddDockerfileJsonFormIssue(
                    issues,
                    relativePath,
                    "dockerfile_json_form_truncated",
                    lineNumber,
                    $"Dockerfile {instruction} JSON form has more than {SymbolExtractor.DockerfileJsonFormMaxItems} items; extraction is capped",
                    ref emitted))
                {
                    return false;
                }

                return true;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return TryAddDockerfileJsonFormIssue(
                issues,
                relativePath,
                "dockerfile_json_form_invalid",
                lineNumber,
                $"Dockerfile {instruction} JSON form is invalid: {LimitDockerfileJsonDiagnostic(CommandErrorWriter.FormatSanitizedExceptionMessage(ex))}",
                ref emitted);
        }

        return true;
    }

    private static bool TryAddDockerfileJsonFormIssue(
        List<FileIssue> issues,
        string relativePath,
        string kind,
        int lineNumber,
        string message,
        ref int emitted)
    {
        if (emitted >= DockerfileJsonFormIssueLimit)
            return false;

        issues.Add(new FileIssue
        {
            Path = relativePath,
            Kind = kind,
            Line = lineNumber,
            Message = message,
            Severity = FileIssue.SeverityWarning,
        });
        emitted++;
        return true;
    }

    private static string LimitDockerfileJsonDiagnostic(string message)
    {
        const int limit = 180;
        return message.Length <= limit ? message : message[..limit] + "...";
    }

    private static bool TryGetDockerfileJsonFormPayload(string line, out string instruction, out string payload)
    {
        instruction = string.Empty;
        payload = string.Empty;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#')
            return false;

        if (TryConsumeDockerfileInstruction(trimmed, "ONBUILD", out var onbuildBody))
            trimmed = onbuildBody.TrimStart();

        foreach (var candidate in new[] { "VOLUME", "SHELL", "COPY", "ADD" })
        {
            if (!TryConsumeDockerfileInstruction(trimmed, candidate, out var body))
                continue;

            var jsonStart = candidate is "COPY" or "ADD"
                ? SkipDockerfileInstructionOptionsForDiagnostics(body)
                : SkipWhitespace(body, 0);
            if (jsonStart >= body.Length || body[jsonStart] != '[')
                return false;

            instruction = candidate;
            payload = body[jsonStart..].Trim();
            return true;
        }

        return false;
    }

    private static bool TryConsumeDockerfileInstruction(string text, string instruction, out string body)
    {
        body = string.Empty;
        if (!text.StartsWith(instruction, StringComparison.OrdinalIgnoreCase))
            return false;

        if (text.Length > instruction.Length && !char.IsWhiteSpace(text[instruction.Length]))
            return false;

        body = text.Length == instruction.Length ? string.Empty : text[instruction.Length..];
        return true;
    }

    private static int SkipDockerfileInstructionOptionsForDiagnostics(string body)
    {
        var index = 0;
        while (index < body.Length)
        {
            index = SkipWhitespace(body, index);
            if (index + 2 > body.Length || body[index] != '-' || body[index + 1] != '-')
                return index;

            index = ScanDockerfileInstructionTokenForDiagnostics(body, index);
        }

        return index;
    }

    private static int ScanDockerfileInstructionTokenForDiagnostics(string body, int index)
    {
        var quote = '\0';
        while (index < body.Length)
        {
            var c = body[index];
            if (quote != '\0')
            {
                if (c == '\\' && index + 1 < body.Length)
                {
                    index += 2;
                    continue;
                }

                if (c == quote)
                    quote = '\0';

                index++;
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                index++;
                continue;
            }

            if (char.IsWhiteSpace(c))
                break;

            index++;
        }

        return index;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }
}
