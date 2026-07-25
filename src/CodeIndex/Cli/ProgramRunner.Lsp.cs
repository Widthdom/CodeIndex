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
    private static int RunLsp(
        string[] cmdArgs,
        string appVersion,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default)
    {
        var options = QueryCommandRunner.ParseArgs(cmdArgs, jsonDefault: true);
        if (options.ParseError != null)
        {
            CommandErrorWriter.WriteStderr(options.ParseError);
            PrintLspUsage();
            return CommandExitCodes.UsageError;
        }

        for (var i = 0; i < cmdArgs.Length; i++)
        {
            if (cmdArgs[i].StartsWith("--db=", StringComparison.Ordinal))
                continue;
            if (cmdArgs[i] == "--db")
            {
                i++;
                continue;
            }

            CommandErrorWriter.WriteStderr($"Error: {cmdArgs[i]} is not supported for lsp.");
            CommandErrorWriter.WriteStderr("Hint: use `--db <path>` to point at a specific index.");
            PrintLspUsage();
            return CommandExitCodes.UsageError;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(options.DbPath))
            {
                CommandErrorWriter.WriteStderr("Error: database path could not be resolved.");
                PrintLspUsage();
                return CommandExitCodes.UsageError;
            }

            if (!options.DbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(LongPath.EnsureWindowsPrefix(options.DbPath)))
            {
                var resolvedPath = Path.GetFullPath(options.DbPath);
                CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbNotFound}]: database not found at {resolvedPath}");
                CommandErrorWriter.WriteStderr("Hint: create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun `cdidx lsp`.");
                return CommandExitCodes.DatabaseError;
            }

            using var db = new DbContext(DbOpenIntent.QueryOnly, options.DbPath);
            if (!db.TryValidateIsCodeIndexDb(out var validationReason))
            {
                CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbError}]: invalid CodeIndex database: {validationReason}");
                return CommandExitCodes.DatabaseError;
            }

            var indexedProjectRoot = db.GetMetaString(DbContext.IndexedProjectRootMetaKey);
            if (!string.IsNullOrWhiteSpace(indexedProjectRoot)
                && bool.TryParse(db.GetMetaString(DbContext.WorkspacePathCaseSensitiveMetaKey), out var pathCaseSensitive))
            {
                PathCasing.SeedFromWorkspace(indexedProjectRoot, ignoreCase: !pathCaseSensitive);
            }

            using var server = new LspServer(db, options.DbPath, appVersion, jsonOptions, indexedProjectRoot);
            return server.Run(Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.Out.Flush();
            Console.Error.Flush();
            return CommandExitCodes.CancelledBySignal;
        }
        catch (Exception ex)
        {
            GlobalToolLog.Error("lsp_server_failed " + GlobalToolLog.FormatExceptionChain(ex));
            CommandErrorWriter.WriteStderr($"Error: LSP server failed ({FormatSanitizedExceptionSummary(ex)}).");
            Console.Out.Flush();
            Console.Error.Flush();
            return CommandExitCodes.DatabaseError;
        }
    }

    private static void PrintLspUsage()
    {
        CommandErrorWriter.WriteStderr("Usage: cdidx lsp [--db <path>]");
        CommandErrorWriter.WriteStderr("Runs a read-only Language Server Protocol server over stdio using an existing CodeIndex database.");
        CommandErrorWriter.WriteStderr("Protocol: LSP stdio uses Content-Length framing; unsupported optional methods are not advertised and return JSON-RPC -32601.");
        CommandErrorWriter.WriteStderr("Completion: index-backed symbol completion only, resolveProvider=false; unmatched or no-token positions return an empty item list.");
    }
}
