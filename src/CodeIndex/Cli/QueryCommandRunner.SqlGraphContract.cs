using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal static SqlGraphContractSignal NarrowSqlGraphContractSignal(SqlGraphContractSignal signal, bool relevant)
    {
        if (!signal.Relevant || relevant)
            return signal;

        return new SqlGraphContractSignal(Ready: true, Relevant: false, DegradedReason: null);
    }

    internal static SqlGraphContractSignal NarrowSqlGraphContractSignalByLanguages(
        SqlGraphContractSignal signal,
        IEnumerable<string?> langs,
        params string?[] additionalLangs)
        => NarrowSqlGraphContractSignal(
            signal,
            additionalLangs.Any(DbReader.IsSqlLanguage) || DbReader.ContainsSqlLanguage(langs));

    internal static SqlGraphContractSignal NarrowSqlGraphContractSignalByPaths(
        DbReader reader,
        SqlGraphContractSignal signal,
        IEnumerable<string> paths,
        params string?[] additionalLangs)
        => NarrowSqlGraphContractSignal(
            signal,
            additionalLangs.Any(DbReader.IsSqlLanguage) || reader.AnyFilePathHasLanguage(paths, "sql"));

    private static void AddSqlGraphContractJsonFields(JsonObject payload, SqlGraphContractSignal signal)
    {
        if (!signal.Relevant)
            return;

        payload["sql_graph_contract_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
                payload["sql_graph_contract_degraded_reason"] = signal.DegradedReason;
        }
    }

    private static void WriteSqlGraphContractWarningIfNeeded(bool json, SqlGraphContractSignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (json || !signal.Relevant || signal.Ready || signal.DegradedReason == null)
            return;

        CommandErrorWriter.WriteStderr($"WARN: {signal.DegradedReason}");
        CommandErrorWriter.WriteStderr($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows before trusting SQL graph/dependency results.");
    }
}
