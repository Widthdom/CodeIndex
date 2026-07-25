using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{

    private static IndexFileFailure BuildIndexFileFailure(string projectPath, string filePath, Exception ex, string stage)
    {
        var relativePath = FileIndexer.NormalizePathSeparators(FileIndexer.GetRelativePathFromDirectory(projectPath, filePath));
        var message = BuildSanitizedIndexFileFailureMessage(stage, ex.GetType().Name, out var messageTruncated);
        return new IndexFileFailure(relativePath, stage, ex.GetType().Name, message, messageTruncated);
    }

    private static IndexFileFailure BuildScanFailure(FileIndexer.ScanError error)
    {
        var message = SanitizeAndCapMcpIndexFailureMessage(error.Message, out var messageTruncated);
        return new IndexFileFailure(
            FileIndexer.NormalizePathSeparators(error.Path),
            "scan",
            nameof(FileIndexer.ScanError),
            message,
            messageTruncated);
    }

    private static McpIndexDiagnostic BuildMcpIndexExceptionDiagnostic(
        string code,
        string category,
        string stage,
        string projectRoot,
        string filePath,
        Exception ex)
    {
        var path = SanitizeMcpIndexDiagnosticPath(projectRoot, filePath);
        var exceptionType = SanitizeMcpIndexFailureToken(ex.GetType().Name, "Exception");
        var message = SanitizeAndCapMcpIndexFailureMessage(
            DiagnosticRedactor.FormatExceptionMessage(ex, MaxMcpIndexFailureMessageLength),
            out var messageTruncated);
        return new McpIndexDiagnostic(code, category, path, stage, exceptionType, message, messageTruncated);
    }

    internal static JsonObject BuildMcpIndexExceptionDiagnosticForTesting(
        string code,
        string category,
        string stage,
        string projectRoot,
        string filePath,
        Exception ex)
        => BuildMcpIndexDiagnosticJson(BuildMcpIndexExceptionDiagnostic(
            code,
            category,
            stage,
            projectRoot,
            filePath,
            ex));

    private static string SanitizeMcpIndexDiagnosticPath(string projectRoot, string path)
    {
        try
        {
            var relative = FileIndexer.NormalizePathSeparators(FileIndexer.GetRelativePathFromDirectory(projectRoot, path));
            if (!string.IsNullOrWhiteSpace(relative)
                && relative != "."
                && !relative.StartsWith("../", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
            {
                return McpBoundedText.ForDisplay(relative, 256).Text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }

        return "<redacted>";
    }

    private static void AddMcpIndexDiagnostics(
        JsonObject structured,
        IReadOnlyList<IndexFileFailure> failures,
        IReadOnlyList<McpIndexDiagnostic> diagnostics)
    {
        var total = failures.Count + diagnostics.Count;
        if (total == 0)
            return;

        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new JsonArray();
        var emitted = 0;
        foreach (var failure in failures)
        {
            var diagnostic = new McpIndexDiagnostic(
                "recoverable_index_error",
                "recoverable_index_error",
                failure.Path,
                failure.Stage,
                failure.ExceptionType,
                failure.Message,
                failure.MessageTruncated);
            AddMcpIndexDiagnosticCategory(categories, diagnostic.Category);
            if (emitted < 50)
            {
                items.Add(BuildMcpIndexDiagnosticJson(diagnostic));
                emitted++;
            }
        }

        foreach (var diagnostic in diagnostics)
        {
            AddMcpIndexDiagnosticCategory(categories, diagnostic.Category);
            if (emitted < 50)
            {
                items.Add(BuildMcpIndexDiagnosticJson(diagnostic));
                emitted++;
            }
        }

        var categoryJson = new JsonObject();
        foreach (var entry in categories.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            categoryJson[entry.Key] = entry.Value;

        structured["diagnostics"] = new JsonObject
        {
            ["total_count"] = total,
            ["sample_count"] = emitted,
            ["truncated"] = total > emitted,
            ["categories"] = categoryJson,
            ["items"] = items,
        };
    }

    private static void AddMcpIndexDiagnosticCategory(Dictionary<string, int> categories, string category)
        => categories[category] = categories.TryGetValue(category, out var count) ? count + 1 : 1;

    private static JsonObject BuildMcpIndexDiagnosticJson(McpIndexDiagnostic diagnostic)
        => new()
        {
            ["code"] = diagnostic.Code,
            ["category"] = diagnostic.Category,
            ["path"] = diagnostic.Path,
            ["stage"] = diagnostic.Stage,
            ["exception_type"] = diagnostic.ExceptionType,
            ["message"] = diagnostic.Message,
            ["message_truncated"] = diagnostic.MessageTruncated,
        };

    internal static string BuildSanitizedIndexFileFailureMessageForTesting(string stage, string exceptionType, out bool messageTruncated) =>
        BuildSanitizedIndexFileFailureMessage(stage, exceptionType, out messageTruncated);

    internal static string SanitizeMcpIndexFailureMessageForTesting(string message, out bool messageTruncated) =>
        SanitizeAndCapMcpIndexFailureMessage(message, out messageTruncated);

}
