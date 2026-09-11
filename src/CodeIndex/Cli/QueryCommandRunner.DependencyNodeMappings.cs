using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static int RunDependencyNodeMappings(QueryCommandOptions options, string[] args, string format,
        JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        const string hint = "Use `deps --cycles --group-partial-types --node-mappings --json --db <path>`; add --cycle-node <id> --node-generation <token> to resolve one node.";
        if (!options.DependencyNodeMappings || !options.DependencyCycles || !options.GroupDependencyPartialTypes
            || !options.Json || options.JsonOutputFormatExplicit || format != OutputFormatEdgeList || options.WorkspaceDbPaths.Count != 0)
            return CommandErrorWriter.WriteJsonOrHuman(options.Json, jsonOptions,
                "Declaration navigation requires --node-mappings --cycles --group-partial-types --json and a single database.",
                CommandExitCodes.UsageError, hint: hint, command: "deps");
        // A catalogue lookup has no edge filters or SCC pagination. Fail explicitly
        // instead of suggesting that a graph filter narrows declaration membership.
        if (TryWriteUnsupportedOptionError("deps", args, new HashSet<string>(StringComparer.Ordinal)
            { "--db", "--data-dir", "--json", "--limit", "--top", "--cycles", "--group-partial-types",
              "--node-mappings", "--cycle-node", "--node-generation", "--mapping-cursor", "--max-json-bytes" }))
            return CommandExitCodes.UsageError;
        return WithDb(options, jsonOptions, reader =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var payload = DependencyCycleNavigation.BuildPage(reader, options.DependencyCycleNode,
                    options.DependencyNodeGeneration, options.DependencyMappingCursor,
                    options.LimitExplicit ? options.Limit : DependencyCycleNavigation.NodeLimit, options.MaxJsonBytes, jsonOptions);
                CommandErrorWriter.WriteStdout(payload.ToJsonString(EnsureJsonNodeSerializerOptions(jsonOptions)));
                return payload["status"]?.GetValue<string>() == "error" ? CommandExitCodes.UsageError : CommandExitCodes.Success;
            }
            catch (ArgumentException ex)
            {
                return CommandErrorWriter.WriteJsonOrHuman(true, jsonOptions, ex.Message,
                    CommandExitCodes.UsageError, hint: hint, command: "deps");
            }
        }, cancellationToken: cancellationToken);
    }
}
