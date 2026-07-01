using CodeIndex.Database;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static bool IsSqlGraphContractSignal(ExactQuerySignal signal)
        => !signal.ExactIndexAvailable
           && !signal.HasMissingIndex
           && !signal.HasMissingTable
           && signal.DegradedReason?.Contains(DegradationReasonCodes.SqlGraphContractNotReady, StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsCSharpCanonicalNameSignal(ExactQuerySignal signal)
        => !signal.ExactIndexAvailable
           && !signal.HasMissingIndex
           && !signal.HasMissingTable
           && signal.DegradedReason?.Contains(DegradationReasonCodes.CSharpSymbolNameNotReady, StringComparison.OrdinalIgnoreCase) == true;

    private static void WriteExactSymbolWarningIfNeeded(bool exact, bool json, ExactQuerySignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (!exact || json || signal.ExactIndexAvailable || signal.DegradedReason == null)
            return;

        if (signal.HasMissingIndex)
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact symbol query ran without the supporting index ({signal.DegradedReason}). Results are correct but may be slow.");
            CommandErrorWriter.WriteStderr("Hint: re-index with `cdidx index <projectPath>` to upgrade the DB layout.");
            return;
        }

        if (IsCSharpCanonicalNameSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact symbol query may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildCSharpCanonicalNameRepairCommand(reader, options)}` to refresh canonical C# symbol names.");
            return;
        }

        if (IsSqlGraphContractSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact symbol query may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows.");
        }
    }

    private static void WriteExactGraphWarningIfNeeded(bool exact, bool json, ExactQuerySignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (!exact || json || signal.ExactIndexAvailable || signal.DegradedReason == null)
            return;

        if (signal.HasMissingIndex)
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact graph query ran without the supporting index ({signal.DegradedReason}). Results are correct but may be slow.");
            CommandErrorWriter.WriteStderr("Hint: re-index with `cdidx index <projectPath>` to upgrade the DB layout.");
            return;
        }

        if (IsCSharpCanonicalNameSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact graph query may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildCSharpCanonicalNameRepairCommand(reader, options)}` to refresh canonical C# symbol names.");
            return;
        }

        if (IsSqlGraphContractSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact graph query may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows.");
        }
    }

    private static void WriteExactBundleWarningIfNeeded(bool exact, bool json, ExactQuerySignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (!exact || json || signal.ExactIndexAvailable || signal.DegradedReason == null)
            return;

        if (signal.HasMissingIndex)
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact inspect bundle ran without all supporting indexes ({signal.DegradedReason}). Results are correct but may be slow.");
            CommandErrorWriter.WriteStderr("Hint: re-index with `cdidx index <projectPath>` to upgrade the DB layout.");
            return;
        }

        if (IsCSharpCanonicalNameSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact inspect bundle may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildCSharpCanonicalNameRepairCommand(reader, options)}` to refresh canonical C# symbol names.");
            return;
        }

        if (IsSqlGraphContractSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact inspect bundle may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows.");
        }
    }
}
