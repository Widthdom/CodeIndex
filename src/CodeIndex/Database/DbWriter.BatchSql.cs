using CodeIndex.Indexer;
using System.Text;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private const int BatchSize = 500;

    private static object FoldedNameDbValue(string? name, Dictionary<string, string?> cache)
    {
        if (name == null)
            return DBNull.Value;

        if (!cache.TryGetValue(name, out var folded))
        {
            folded = NameFold.Fold(name);
            cache[name] = folded;
        }

        return (object?)folded ?? DBNull.Value;
    }

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
}
