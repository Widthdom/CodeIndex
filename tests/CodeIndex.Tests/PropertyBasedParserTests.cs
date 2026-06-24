using System;
using System.Linq;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeIndex.Tests;

/// <summary>
/// Property-based tests for parser-heavy paths (issue #1572). Existing example-based
/// tests already lock in hand-picked cases; these complement them by asserting
/// universal invariants — never-throws contracts, idempotence, and "the output is
/// always parseable by the downstream consumer" — across the random inputs that
/// FsCheck generates (empty strings, control chars, surrogate halves, embedded
/// special characters, long strings).
/// </summary>
public class PropertyBasedParserTests
{
    /// <summary>
    /// CLI parser contract: <see cref="ArgHelper.WantsHelp"/> must not throw on
    /// any <c>string[]</c> shape. Catches accidental indexer or null-deref regressions
    /// in the single-value-flag skip logic when callers pass unusual token sequences.
    /// </summary>
    [Property(MaxTest = 200, EndSize = 64)]
    public bool ArgHelper_WantsHelp_NeverThrows(NonNull<string>[] tokens)
    {
        var args = (tokens ?? Array.Empty<NonNull<string>>())
            .Select(t => t.Item)
            .ToArray();
        _ = ArgHelper.WantsHelp(args.AsSpan());
        return true;
    }

    /// <summary>
    /// <see cref="ProgramRunner.IsProjectPathArg"/> must not throw for any non-null
    /// string. The top-level CLI dispatcher uses it on arbitrary argv tokens, so any
    /// exception here would surface as a startup crash.
    /// </summary>
    [Property(MaxTest = 200)]
    public bool IsProjectPathArg_NeverThrows(NonNull<string> arg)
    {
        _ = ProgramRunner.IsProjectPathArg(arg.Item);
        return true;
    }

    /// <summary>
    /// PathOps contract from issue #1572: separator normalization must be idempotent
    /// — applying it twice must equal applying it once — so callers can re-normalize
    /// already-normalized DB-stored paths without drift.
    /// </summary>
    [Property(MaxTest = 200)]
    public bool NormalizePathSeparators_IsIdempotent(NonNull<string> path)
    {
        var once = FileIndexer.NormalizePathSeparators(path.Item);
        var twice = FileIndexer.NormalizePathSeparators(once);
        return once == twice;
    }

    /// <summary>
    /// FTS5 builder contract from issue #1572: the literal-safe sanitizer must always
    /// produce a string that the FTS5 MATCH grammar can parse, regardless of what
    /// special characters (double quotes, asterisks, operator keywords, embedded
    /// control chars) the user typed. We verify by feeding the sanitized output into
    /// a real in-memory FTS5 table and asserting no syntax-error SqliteException
    /// surfaces. We additionally require that the sanitizer did NOT collapse the
    /// non-whitespace input to the empty-token fallback <c>"\"\""</c> — otherwise a
    /// regression that aggressively stripped all input would pass vacuously.
    /// </summary>
    [Property(MaxTest = 200, EndSize = 64)]
    public Property SanitizeFtsQuery_EmitsParseableFts5(NonWhiteSpaceString query, bool prefix)
    {
        var sanitized = DbReader.SanitizeFtsQuery(query.Item, prefix);
        var nonTrivial = sanitized.Length > 2 && sanitized != "\"\"";
        return (nonTrivial && TryRunFts5Match(sanitized)).ToProperty();
    }

    private static bool TryRunFts5Match(string match)
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE fts USING fts5(c);";
            create.ExecuteNonQuery();
        }
        using var query = conn.CreateCommand();
        query.CommandText = "SELECT count(*) FROM fts WHERE fts MATCH @q";
        query.Parameters.AddWithValue("@q", match);
        try
        {
            query.ExecuteScalar();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            return false;
        }
    }
}
