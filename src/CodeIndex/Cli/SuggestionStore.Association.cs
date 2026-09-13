using System.Globalization;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public partial class SuggestionStore
{
    internal Action<string>? ValidateAssociationWriteForTesting { get; set; }

    /// <summary>
    /// Atomically associates an existing issue without any network access or submission attempt.
    /// Existing associations cannot be replaced; repeating the same identity is a no-op.
    /// ネットワークアクセスや投稿試行を行わず、既存 Issue を原子的に関連付ける。
    /// 既存の関連付けは置換できず、同一の識別情報による再実行は何も変更しない。
    /// </summary>
    public MutationResult TryLinkIssue(
        string id,
        string expectedRevisionHash,
        string repository,
        string issue,
        string? linkedBy,
        string? reason,
        out SuggestionRecord? updated)
    {
        updated = null;
        if (!TryParseIssueIdentity(repository, issue, out var identity))
            return MutationResult.InvalidAssociation;

        SuggestionRecord? result = null;
        var mutationResult = WithFileLock(() =>
        {
            var records = ReadUnlocked();
            var index = records.FindIndex(record => string.Equals(record.Id, id, StringComparison.Ordinal));
            if (index < 0)
                return MutationResult.NotFound;
            var record = records[index];
            if (IsSubmissionInFlight(record))
                return MutationResult.SubmissionInFlight;

            if (record.UpstreamAssociation != null || record.UpstreamIssueNumber != null || !string.IsNullOrWhiteSpace(record.UpstreamUrl))
            {
                if (!TryParseIssueIdentity(identity!.Repository, record.UpstreamUrl, out var existing)
                    || existing != identity
                    || (record.UpstreamIssueNumber != null && record.UpstreamIssueNumber != identity.Number)
                    || (record.UpstreamAssociation != null
                        && !string.Equals(record.UpstreamAssociation.Repository, identity.Repository, StringComparison.OrdinalIgnoreCase)))
                    return MutationResult.AssociationConflict;

                result = record;
                return MutationResult.Success;
            }
            if (!string.Equals(record.RevisionHash, expectedRevisionHash, StringComparison.Ordinal))
                return MutationResult.RevisionConflict;

            var actor = RedactAndBoundAuditValue(linkedBy, MaxStatusChangedByLength, out var actorTypes) ?? "cdidx-cli";
            var boundedReason = RedactAndBoundAuditValue(reason, MaxStatusChangeReasonLength, out var reasonTypes);
            var redactedTypes = actorTypes.Concat(reasonTypes).Distinct(StringComparer.Ordinal).ToArray();
            if (redactedTypes.Length > 0)
                WriteRedactionWarning(redactedTypes);

            var linkedAt = GetUtcNow();
            record.UpstreamUrl = identity!.Url;
            record.UpstreamIssueNumber = identity.Number;
            record.UpstreamAssociation = new(identity.Repository, linkedAt, actor, boundedReason);
            if (record.Status == SuggestionStatus.Draft)
            {
                record.PreviousStatus = record.Status;
                record.Status = SuggestionStatus.SubmittedPendingTriage;
                record.StatusChangedAt = linkedAt;
                record.StatusChangedBy = actor;
                record.StatusChangeReason = "Existing GitHub issue linked manually; remote state was not verified.";
            }
            // Preserve actual attempt/error/sync/resolution evidence. Only cancel future retries.
            // 実際の試行・エラー・同期・解決の証跡は維持し、今後の再試行予約だけを解除する。
            record.NextRetryAt = null;
            SaveUnlocked(records, ValidateAssociationWriteForTesting);
            result = record;
            return MutationResult.Success;
        });
        updated = result;
        return mutationResult;
    }

    internal static bool IsValidIssueAssociation(string? repository, string? issue)
        => TryParseIssueIdentity(repository, issue, out _);

    private sealed record IssueIdentity(string Repository, int Number)
    {
        public string Url => $"https://github.com/{Repository}/issues/{Number.ToString(CultureInfo.InvariantCulture)}";
    }

    private static bool TryParseIssueIdentity(string? repository, string? issue, out IssueIdentity? identity)
    {
        identity = null;
        if (repository == null || repository.Length > 140 || issue == null || issue.Length > 2048)
            return false;
        var parts = repository.Split('/');
        if (parts.Length != 2
            || parts[0].Length is < 1 or > 39
            || !char.IsAsciiLetterOrDigit(parts[0][0])
            || !char.IsAsciiLetterOrDigit(parts[0][^1])
            || parts[0].Contains("--", StringComparison.Ordinal)
            || !parts[0].All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            || parts[1].Length is < 1 or > 100
            || parts[1] is "." or ".."
            || !parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            return false;

        var normalizedRepository = repository.ToLowerInvariant();
        var numberText = issue;
        if (!issue.All(char.IsAsciiDigit))
        {
            // Validate the original spelling so URI normalization cannot hide
            // dot segments, escaped paths, credentials, ports, queries or PR URLs.
            // URI 正規化で相対要素・エスケープ・認証情報・ポート・クエリ・PR URL を受理しない。
            var prefix = $"https://github.com/{repository}/issues/";
            if (!issue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            numberText = issue[prefix.Length..];
        }
        if (numberText.Length == 0 || numberText.Length > 10 || numberText[0] == '0'
            || !numberText.All(char.IsAsciiDigit)
            || !int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number <= 0)
            return false;
        identity = new(normalizedRepository, number);
        return true;
    }
}
