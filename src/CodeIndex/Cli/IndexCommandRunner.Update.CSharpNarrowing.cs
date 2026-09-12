using System.Globalization;
using System.Security.Cryptography;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static void EvaluateUpdateCSharpContractNarrowing(
        UpdateCSharpPreflightContext context,
        UpdateCSharpPreflightState state)
    {
        var telemetry = context.Options.CSharpWorkspaceExpansion!;
        if (!state.CapturedContractFingerprint
            || !context.ContractNarrowingAllowed()
            || context.ProjectMarkerFingerprint is not { } projectMarkerFingerprint
            || state.CSharpWorkspace.ContractSourceFingerprint == null
            || state.CSharpWorkspaceInputSnapshot is not { IsComplete: true } inputs
            || state.CSharpWorkspaceSnapshots == null
            || !state.CSharpWorkspace.SourceContractEvidenceComplete)
            return;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => CSharpWorkspaceContractFingerprint.Append(hash, value);
        Add("csharp_workspace_inputs_v2");
        // A new binary must earn its own proof, even if its public version is unchanged.
        Add(typeof(IndexCommandRunner).Module.ModuleVersionId.ToString("D"));
        Add(context.ProjectRoot);
        Add(state.CSharpWorkspace.ContractSourceFingerprint);
        // Project markers change family scopes even when every C# path and
        // contributing source is unchanged. This evidence has its own scan budget.
        Add(projectMarkerFingerprint);
        Add(context.Options.MaxFileSizeBytes!.Value.ToString(CultureInfo.InvariantCulture));
        Add(context.Options.MaxSymbolsPerFile.ToString(CultureInfo.InvariantCulture));
        Add(context.Options.MaxReferencesPerFile.ToString(CultureInfo.InvariantCulture));
        Add(context.Options.SymlinkPolicy.ToString());
        foreach (var pattern in context.Options.GeneratedCodePatterns)
            Add(pattern);
        Add("configuration_inputs");
        foreach (var input in inputs.ConfigurationInputs.OrderBy(input => input.Path, StringComparer.Ordinal))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            Add(input.Path);
            Add(input.Kind.ToString());
            Add(input.Length.ToString(CultureInfo.InvariantCulture));
            if (input.Kind != FileIndexer.ConfigurationInputKind.MarkerDirectory)
                Add(input.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture));
            Add(input.ContentHash == null ? "" : Convert.ToHexString(input.ContentHash));
        }
        state.ContractBaselineFingerprint = Convert.ToHexString(hash.GetHashAndReset());

        using var command = context.Writer.Connection.CreateCommand();
        command.CommandText = """
            SELECT value FROM codeindex_meta
            WHERE key = $key AND length(CAST(value AS BLOB)) <= 256
            """;
        command.Parameters.AddWithValue("$key", DbContext.CSharpWorkspaceContractBaselineMetaKey);
        var priorBaseline = command.ExecuteScalar() as string;
        command.Parameters["$key"].Value = DbContext.LastIndexRunStartedAtMetaKey;
        var priorRun = command.ExecuteScalar() as string;
        if (priorBaseline == null || priorRun == null)
        {
            telemetry.Reason = "baseline_unavailable";
            return;
        }

        state.CanNarrowTargets = string.Equals(
            priorBaseline,
            BuildCSharpContractBaseline(priorRun, state.ContractBaselineFingerprint),
            StringComparison.Ordinal);
        telemetry.Reason = state.CanNarrowTargets ? "contract_inputs_unchanged" : "contract_inputs_changed";
        // Keep the expanded set until the existing pre-write guard validates every
        // source and configuration input. The caller narrows only after that barrier.
    }

    private static string BuildCSharpContractBaseline(string runStartedAt, string fingerprint)
        => $"{runStartedAt}|{fingerprint}";

    private static void WriteCSharpWorkspaceExpansionSummary(CSharpWorkspaceExpansion? expansion)
    {
        if (expansion == null)
            return;
        CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine(
            "C# update scope",
            $"{expansion.Decision}: {expansion.OriginalTargetCount:N0} -> {expansion.ExpandedTargetCount:N0} -> {expansion.FinalTargetCount:N0} targets ({expansion.Trigger}; {expansion.Reason})",
            indent: "  "));
    }
}
