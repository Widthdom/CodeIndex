namespace CodeIndex.Database;

public partial class DbReader
{
    private const string GenericSymbolRankNamePenaltySqlLiteral = "0.01";
    private const string GenericSymbolRankNamesSql = "('add','all','any','append','appendline','average','build','call','combine','contains','convert','count','create','distinct','equal','equals','execute','exists','file','files','first','firstordefault','get','getboolean','getbyte','getbytes','getchar','getchars','getdatetime','getdecimal','getdouble','getfieldvalue','getfloat','getguid','getint16','getint32','getint64','getordinal','getstring','gettemppath','getvalue','getvalues','groupby','handle','id','invoke','isdbnull','key','kind','last','lastordefault','length','line','list','load','name','orderby','orderbydescending','parse','path','process','read','resolve','run','set','single','singleordefault','skip','start','stop','sum','take','text','thenby','thenbydescending','tolist','tostring','tryparse','type','update','value','values','write')";

    private static IReadOnlyList<string>? NormalizeSymbolSearchQueries(
        IReadOnlyList<string>? queries,
        string? lang,
        bool exact)
        => SymbolSearchQueryNormalizer.NormalizeQueries(queries, lang, exact);

    /// <summary>
    /// Escape LIKE wildcards (%, _) in user input to prevent unintended pattern matching.
    /// ユーザー入力のLIKEワイルドカード（%, _）をエスケープして意図しないパターンマッチを防止。
    /// </summary>
    /// <summary>
    /// Return all distinct symbol kinds present in the index.
    /// インデックス内の全シンボル種別を返す。
    /// </summary>
    /// <summary>
    /// Return symbol kind counts for status display.
    /// ステータス表示用のシンボル種別カウントを返す。
    /// </summary>
    public Dictionary<string, long> GetSymbolKindCounts()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT kind, COUNT(*) FROM symbols GROUP BY kind ORDER BY COUNT(*) DESC";
        var counts = new Dictionary<string, long>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            counts[reader.GetString(0)] = reader.GetInt64(1);
        return counts;
    }

    public List<string> GetDistinctKinds()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT kind FROM symbols ORDER BY kind";
        var kinds = new List<string>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            kinds.Add(reader.GetString(0));
        return kinds;
    }

    /// <summary>
    /// Search symbols by name pattern, optionally filtered by kind and language.
    /// シンボルを名前パターンで検索（種別・言語でフィルタ可能）。
    /// </summary>
    public List<SymbolResult> SearchSymbols(string? query = null, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, SymbolSortMode sortMode = SymbolSortMode.Name, int? startLine = null, int? endLine = null, bool groupPartials = false, int offset = 0)
    {
        var normalizedQuery = NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact);
        return SearchSymbols(
            normalizedQuery == null
                ? null
                : SymbolSearchQueryNormalizer.MarkNormalized([normalizedQuery]),
            limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact,
            visibilityFilters, excludeVisibilityFilters, sortMode, startLine, endLine,
            groupPartials, offset);
    }

    public int CountSearchSymbols(string? query = null, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var normalizedQuery = NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact);
        return CountSearchSymbols(
            normalizedQuery == null
                ? null
                : SymbolSearchQueryNormalizer.MarkNormalized([normalizedQuery]),
            limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact,
            visibilityFilters, excludeVisibilityFilters);
    }

    public bool AnySearchSymbols(IReadOnlyList<string>? queries, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var validQueries = NormalizeSymbolSearchQueries(queries, lang, exact);
        if (validQueries == null || validQueries.Count == 0)
            return CountSearchSymbols(validQueries, 1, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact) > 0;

        foreach (var query in validQueries)
        {
            if (CountSearchSymbols(
                    SymbolSearchQueryNormalizer.MarkNormalized([query]),
                    1,
                    kind,
                    lang,
                    pathPatterns,
                    excludePathPatterns,
                    excludeTests,
                    since,
                    exact) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeSymbolSearchQuery(string? query, string? lang, bool exact = false)
        => SymbolSearchQueryNormalizer.Normalize(query, lang, exact);

    private static string? ComputeSwiftBacktickAlias(string? query, string? lang)
        => SymbolSearchQueryNormalizer.ComputeSwiftBacktickAlias(query, lang);

    private static bool ShouldPreserveRustQualifiedExactQuery(string? query, string? lang, bool exact)
        => SymbolSearchQueryNormalizer.ShouldPreserveRustQualifiedExactQuery(query, lang, exact);

    private static string? NormalizeSymbolSearchQueryForSymbolSearch(string? query, string? lang, bool exact)
        => SymbolSearchQueryNormalizer.NormalizeForSymbolSearch(query, lang, exact);

    private static (string? QualifiedPath, string? ContainerPath, string? LeafName) NormalizeRustQualifiedExactQueryParts(string query)
        => SymbolSearchQueryNormalizer.NormalizeRustQualifiedExactQueryParts(query);
}
