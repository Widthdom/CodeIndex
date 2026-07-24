using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static void AddReferenceGraphContractJsonFields(
        JsonObject payload,
        SqlGraphContractSignal sqlSignal,
        HdlGraphContractSignal hdlSignal)
    {
        AddSqlGraphContractJsonFields(payload, sqlSignal);
        AddHdlGraphContractJsonFields(payload, hdlSignal);
    }

    private static void AddHdlGraphContractJsonFields(
        JsonObject payload,
        HdlGraphContractSignal signal)
    {
        if (!signal.Relevant)
            return;

        payload["hdl_graph_contract_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["hdl_graph_contract_degraded_reason"] = signal.DegradedReason;
            payload["degraded"] = true;
        }
    }

    private static void WriteHdlGraphContractWarningIfNeeded(
        bool json,
        HdlGraphContractSignal signal)
    {
        if (json || !signal.Relevant || signal.Ready)
            return;

        CommandErrorWriter.WriteStderr(
            $"WARN: {signal.DegradedReason} Graph results are degraded until a full scan refreshes HDL references.");
    }
}
