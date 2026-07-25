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
    private static int CheckWorkspaceVersionPin(string appVersion, string startDirectory, bool strictVersion)
    {
        var pinPath = FindWorkspaceVersionPin(startDirectory);
        if (pinPath == null)
            return CommandExitCodes.Success;

        if (!TryReadWorkspaceVersionPin(pinPath, out var required, out var warning))
        {
            CommandErrorWriter.WriteStderr(warning);
            return CommandExitCodes.Success;
        }

        if (string.IsNullOrWhiteSpace(required) || VersionsMatch(required, appVersion))
            return CommandExitCodes.Success;

        var message = $"workspace requires cdidx v{NormalizeVersion(required)}, but this binary is v{NormalizeVersion(appVersion)} ({pinPath}).";
        if (!strictVersion)
        {
            CommandErrorWriter.WriteStderr($"Warning: {message}");
            return CommandExitCodes.Success;
        }

        CommandErrorWriter.WriteStderr($"Error: {message}");
        CommandErrorWriter.WriteStderr("Hint: rerun without --strict-version to warn only, or install the pinned cdidx version for this workspace.");
        return CommandExitCodes.ExUsage;
    }

    private static bool TryReadWorkspaceVersionPin(string pinPath, out string required, out string warning)
    {
        required = string.Empty;
        warning = string.Empty;

        try
        {
            var bytes = ReadWorkspaceVersionPinBytes(pinPath);
            if (bytes.Length > WorkspaceVersionPinMaxBytes)
            {
                warning = BuildWorkspaceVersionPinWarning($"file exceeds {WorkspaceVersionPinMaxBytes} bytes");
                return false;
            }

            return TryParseWorkspaceVersionPin(DecodeWorkspaceVersionPinBytes(bytes), out required, out warning);
        }
        catch (Exception ex)
        {
            warning = BuildWorkspaceVersionPinReadWarning(ex);
            return false;
        }
    }

    private static byte[] ReadWorkspaceVersionPinBytes(string pinPath)
    {
        var buffer = new byte[WorkspaceVersionPinMaxBytes + 1];
        var totalRead = 0;

        using var stream = new FileStream(
            pinPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: Math.Min(1024, buffer.Length),
            FileOptions.SequentialScan);

        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
                break;
            totalRead += read;
        }

        if (totalRead == buffer.Length)
            return buffer;

        var result = new byte[totalRead];
        Array.Copy(buffer, result, totalRead);
        return result;
    }

    private static string DecodeWorkspaceVersionPinBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: Math.Min(1024, Math.Max(1, bytes.Length)));
        return reader.ReadToEnd();
    }

    private static bool TryParseWorkspaceVersionPin(string content, out string required, out string warning)
    {
        required = string.Empty;
        warning = string.Empty;

        using var reader = new StringReader(content);
        var skippedBlankLines = 0;
        var lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (line.Length > WorkspaceVersionPinMaxLineChars)
            {
                warning = BuildWorkspaceVersionPinWarning($"line {lineNumber} exceeds {WorkspaceVersionPinMaxLineChars} characters");
                return false;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                skippedBlankLines++;
                if (skippedBlankLines > WorkspaceVersionPinMaxSkippedBlankLines)
                {
                    warning = BuildWorkspaceVersionPinWarning($"more than {WorkspaceVersionPinMaxSkippedBlankLines} leading blank lines");
                    return false;
                }

                continue;
            }

            required = line.Trim();
            return true;
        }

        return true;
    }

    internal static string BuildWorkspaceVersionPinReadWarningForTesting(Exception exception)
        => BuildWorkspaceVersionPinReadWarning(exception);

    private static string BuildWorkspaceVersionPinWarning(string reason)
        => $"Warning: ignoring .cdidx-version: {ConsoleUi.FormatBoundedValue(reason)}.";

    private static string BuildWorkspaceVersionPinReadWarning(Exception exception)
    {
        var reason = exception switch
        {
            UnauthorizedAccessException => "permission denied",
            ArgumentException or NotSupportedException or PathTooLongException => "invalid path",
            IOException => "read failed",
            _ => "read failed",
        };
        return $"Warning: could not read .cdidx-version: {reason}.";
    }

    internal static string? FindWorkspaceVersionPin(string startDirectory)
    {
        var current = Path.GetFullPath(startDirectory);
        if (File.Exists(current))
            current = Path.GetDirectoryName(current) ?? current;

        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(current, ".cdidx-version");
            if (File.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(current);
            if (parent == null)
                return null;
            current = parent.FullName;
        }

        return null;
    }

    private static bool VersionsMatch(string required, string actual)
        => string.Equals(NormalizeVersion(required), NormalizeVersion(actual), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith('v') || trimmed.StartsWith('V') ? trimmed[1..] : trimmed;
    }
}
