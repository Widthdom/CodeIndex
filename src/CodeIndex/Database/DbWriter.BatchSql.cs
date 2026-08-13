using CodeIndex.Indexer;
using System.Text;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private const int BatchSize = 500;
    // Microsoft.Data.Sqlite resolves every named parameter through SQLite again on
    // each execution. Caller-owned transactions let us split dense writes without
    // adding transaction scopes, so keep those statements below this binding budget.
    private const int MaxCallerTransactionBatchParameters = 32;
    private const int MaxFoldedNameCacheEntries = 4096;

    private static object FoldedNameDbValue(string? name, Dictionary<string, string?> cache)
    {
        if (name == null)
            return DBNull.Value;

        if (!cache.TryGetValue(name, out var folded))
        {
            folded = NameFold.Fold(name);
            // A single generated file can contain tens of thousands of unique names.
            // Keep cross-batch reuse bounded instead of retaining every unique key until
            // the file insert completes; the first entries still cover recurring symbols
            // and containers without turning the cache into a second symbol table.
            // 生成ファイルでは一意名が数万件に達し得るため、file insert 終了まで
            // 全件を保持しない。先頭の recurring name/container を再利用しつつ、
            // cache 自体が第2の symbol table になることを防ぐ。
            if (cache.Count < MaxFoldedNameCacheEntries)
                cache[name] = folded;
        }

        return (object?)folded ?? DBNull.Value;
    }

    private static object FoldedNameDbValue(
        string? name,
        string? identityNameFolded,
        Dictionary<string, string?> cache) =>
        identityNameFolded != null
            ? identityNameFolded
            : FoldedNameDbValue(name, cache);

    private static object DisplayFoldedNameDbValue(
        string? displayNameFolded) =>
        (object?)displayNameFolded ?? DBNull.Value;

    private static Dictionary<string, string?> CreateFoldedNameCache(int rowCount, int namesPerRow)
    {
        if (rowCount <= 0 || namesPerRow <= 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        var capacity = rowCount > int.MaxValue / namesPerRow
            ? int.MaxValue
            : rowCount * namesPerRow;
        return new Dictionary<string, string?>(capacity, StringComparer.Ordinal);
    }

    private static StringBuilder CreateBatchSqlBuilder(int rowCount, int estimatedCharsPerRow)
    {
        const int BaseCapacity = 256;
        if (rowCount <= 0 || estimatedCharsPerRow <= 0)
            return new StringBuilder(BaseCapacity);

        var rowCapacity = rowCount > (int.MaxValue - BaseCapacity) / estimatedCharsPerRow
            ? int.MaxValue - BaseCapacity
            : rowCount * estimatedCharsPerRow;
        return new StringBuilder(BaseCapacity + rowCapacity);
    }

    private static int GetRowsPerInsertStatement(int columnCount)
    {
        if (columnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(columnCount));

        return Math.Max(1, Math.Min(BatchSize, SqliteDynamicSql.MaxSqlVariables / columnCount));
    }

    private static int GetRowsPerCallerTransactionInsertStatement(int columnCount)
    {
        if (columnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(columnCount));

        return Math.Max(1, Math.Min(
            GetRowsPerInsertStatement(columnCount),
            MaxCallerTransactionBatchParameters / columnCount));
    }
}
