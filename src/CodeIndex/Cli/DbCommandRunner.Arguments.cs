using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class DbCommandRunner
{
    internal static DbCommandOptions ParseArgs(string[] args)
        => new DbArgumentParser().Parse(args);

    internal static bool IsExplicitBatchReadOnlyInvocation(string[] args)
    {
        var options = ParseArgs(args);
        if (options.ParseError is not null
            || options.ShowHelp
            || options.IntegrityAliasUsed
            || CountModes(options) != 1)
        {
            return false;
        }

        return options.ExplicitSubcommand switch
        {
            "schema" => options.Schema,
            "integrity" => options.IntegrityCheck,
            _ => false,
        };
    }

    private static int CountModes(DbCommandOptions options) =>
        (options.IntegrityCheck ? 1 : 0)
        + (options.Schema ? 1 : 0)
        + (options.Prune ? 1 : 0)
        + (options.Checkpoint ? 1 : 0)
        + (options.ListCheckpoints ? 1 : 0)
        + (options.Restore ? 1 : 0)
        + (options.RestoreBackups ? 1 : 0);
}
