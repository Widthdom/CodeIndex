using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    public const int ReferenceExtractionCapHitFileLimit = 50;
    public const string ReferenceExtractionCapStateUnavailableReason = "reference_extraction_cap_state_unavailable";

    private ReferenceExtractionCapHitSummary? _referenceExtractionCapHits;

    public ReferenceExtractionCapHitSummary GetReferenceExtractionCapHits()
        => _referenceExtractionCapHits ??= ReadReferenceExtractionCapHits(
            _conn,
            _hasIssuesTable,
            transaction: null);

    internal static ReferenceExtractionCapHitSummary ReadReferenceExtractionCapHits(
        SqliteConnection connection,
        bool hasIssuesTable,
        SqliteTransaction? transaction)
    {
        if (!hasIssuesTable)
        {
            return new ReferenceExtractionCapHitSummary
            {
                StateAvailable = false,
                Reasons = [ReferenceExtractionCapStateUnavailableReason],
                FileLimit = ReferenceExtractionCapHitFileLimit,
            };
        }

        using var totalsCommand = CreateReferenceExtractionCapHitCommand(connection, transaction, """
            SELECT COUNT(*), COUNT(DISTINCT fi.file_id)
            FROM file_issues fi
            WHERE fi.kind IN ({0})
            """);
        long hitCount;
        long affectedFileCount;
        using (var totalsReader = totalsCommand.ExecuteTrackedReader())
        {
            if (!totalsReader.TrackedRead())
                return EmptyReferenceExtractionCapHitSummary();
            hitCount = totalsReader.GetInt64(0);
            affectedFileCount = totalsReader.GetInt64(1);
        }

        if (hitCount == 0)
            return EmptyReferenceExtractionCapHitSummary();

        var reasons = new List<string>();
        using (var reasonCommand = CreateReferenceExtractionCapHitCommand(connection, transaction, """
            SELECT fi.kind
            FROM file_issues fi
            WHERE fi.kind IN ({0})
            GROUP BY fi.kind
            ORDER BY fi.kind
            """))
        using (var reasonReader = reasonCommand.ExecuteTrackedReader())
        {
            while (reasonReader.TrackedRead())
                reasons.Add(reasonReader.GetString(0));
        }

        var files = new List<ReferenceExtractionFileCapHits>();
        using (var fileCommand = CreateReferenceExtractionCapHitCommand(connection, transaction, """
            WITH affected_files AS (
                SELECT fi.file_id, MIN(fi.id) AS first_issue_id
                FROM file_issues fi
                WHERE fi.kind IN ({0})
                GROUP BY fi.file_id
                ORDER BY first_issue_id, fi.file_id
                LIMIT @fileLimit
            )
            SELECT f.path, fi.kind, COUNT(*), af.first_issue_id
            FROM affected_files af
            JOIN files f ON f.id = af.file_id
            JOIN file_issues fi ON fi.file_id = af.file_id
            WHERE fi.kind IN ({0})
            GROUP BY f.path, fi.kind, af.first_issue_id
            ORDER BY af.first_issue_id, f.path, fi.kind
            """))
        {
            SqliteCommandPolicy.Add(fileCommand, "@fileLimit", ReferenceExtractionCapHitFileLimit);
            using var fileReader = fileCommand.ExecuteTrackedReader();
            string? currentPath = null;
            long currentCount = 0;
            List<string>? currentReasons = null;
            while (fileReader.TrackedRead())
            {
                var path = fileReader.GetString(0);
                if (!string.Equals(currentPath, path, StringComparison.Ordinal))
                {
                    if (currentPath != null)
                    {
                        files.Add(new ReferenceExtractionFileCapHits
                        {
                            File = currentPath,
                            HitCount = currentCount,
                            Reasons = currentReasons!,
                        });
                    }
                    currentPath = path;
                    currentCount = 0;
                    currentReasons = [];
                }
                currentReasons!.Add(fileReader.GetString(1));
                currentCount += fileReader.GetInt64(2);
            }
            if (currentPath != null)
            {
                files.Add(new ReferenceExtractionFileCapHits
                {
                    File = currentPath,
                    HitCount = currentCount,
                    Reasons = currentReasons!,
                });
            }
        }

        return new ReferenceExtractionCapHitSummary
        {
            HitCount = hitCount,
            AffectedFileCount = affectedFileCount,
            Reasons = reasons,
            Files = files,
            FilesTruncated = affectedFileCount > files.Count,
            FileLimit = ReferenceExtractionCapHitFileLimit,
        };
    }

    private static ReferenceExtractionCapHitSummary EmptyReferenceExtractionCapHitSummary() => new()
    {
        FileLimit = ReferenceExtractionCapHitFileLimit,
    };

    private static SqliteCommand CreateReferenceExtractionCapHitCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sqlTemplate)
    {
        var command = connection.CreateCommand();
        if (transaction != null)
            command.Transaction = transaction;
        var parameterNames = new string[ReferenceExtractor.ReferenceSafetyCapDiagnosticKinds.Count];
        for (var index = 0; index < parameterNames.Length; index++)
        {
            var parameterName = $"@capKind{index}";
            parameterNames[index] = parameterName;
            SqliteCommandPolicy.Add(command, parameterName, ReferenceExtractor.ReferenceSafetyCapDiagnosticKinds[index]);
        }
        command.CommandText = string.Format(System.Globalization.CultureInfo.InvariantCulture, sqlTemplate, string.Join(", ", parameterNames));
        return command;
    }
}
