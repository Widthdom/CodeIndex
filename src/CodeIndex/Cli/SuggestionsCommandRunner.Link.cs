using System.Text.Json;

namespace CodeIndex.Cli;

internal static partial class SuggestionsCommandRunner
{
    private static int RunLink(SuggestionStore store, Options options, JsonSerializerOptions jsonOptions)
    {
        if (string.IsNullOrWhiteSpace(options.Id))
            return WriteUsageError("suggestions link requires an id.", options.Json, jsonOptions);
        if (options.HasContentEditableFields || options.HasQueryOnlyOptionsExceptStatusAndRepository || options.StatusSpecified)
            return WriteUsageError("suggestions link accepts only an id, --repo, --issue, --actor, --reason, --db, and --json.", options.Json, jsonOptions);
        if (!SuggestionStore.IsValidIssueAssociation(options.OpenIssuesRepository, options.Issue))
            return WriteUsageError(
                "suggestions link requires --repo <owner/name> and --issue <positive-number|https://github.com/owner/name/issues/number>. The URL must match --repo; PR URLs, other hosts, query strings and fragments are not accepted.",
                options.Json, jsonOptions);
        if (options.ActorSpecified && string.IsNullOrWhiteSpace(options.Actor))
            return WriteUsageError("--actor must not be empty.", options.Json, jsonOptions);
        if (options.ReasonSpecified && string.IsNullOrWhiteSpace(options.Reason))
            return WriteUsageError("--reason must not be empty.", options.Json, jsonOptions);

        var record = ResolveById(store.LoadAll(), options.Id);
        if (record == null)
            return WriteMutationNotFound(options, jsonOptions);
        var result = store.TryLinkIssue(record.Id, record.RevisionHash, options.OpenIssuesRepository!, options.Issue!,
            options.Actor, options.Reason, out var updated);
        return result switch
        {
            SuggestionStore.MutationResult.Success => WriteMutationSuccess("linked", updated!, options, jsonOptions),
            SuggestionStore.MutationResult.SubmissionInFlight => WriteMutationSubmissionInFlight(options, jsonOptions),
            SuggestionStore.MutationResult.RevisionConflict => WriteMutationRevisionConflict(options, jsonOptions),
            SuggestionStore.MutationResult.AssociationConflict => CommandErrorWriter.WriteJsonOrHuman(
                options.Json, jsonOptions, "Suggestion already has a different or incomplete upstream identity; the existing association was preserved.",
                CommandExitCodes.UsageError, "Inspect the existing identity with `cdidx suggestions show <id> --json`. Reassociation is not supported.",
                category: "upstream_association_conflict"),
            _ => WriteMutationNotFound(options, jsonOptions),
        };
    }
}
