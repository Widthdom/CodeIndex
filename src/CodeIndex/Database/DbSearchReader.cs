using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

/// <summary>
/// Full-text search operations (partial class split from DbReader.cs).
/// 全文検索操作（DbReader.csからのpartial class分割）。
/// </summary>
public partial class DbReader
{
    /// <summary>
    /// Sanitize user input for FTS5 MATCH by quoting each token as a phrase.
    /// FTS5 MATCH用にユーザー入力をサニタイズ（各トークンをフレーズとして引用）。
    /// Prevents FTS5 syntax errors from special characters (*, ", AND, OR, NOT, NEAR, etc.).
    /// 特殊文字（*, ", AND, OR, NOT, NEAR等）によるFTS5構文エラーを防止する。
    /// </summary>
    private static string SanitizeFtsQuery(string query)
    {
        // Escape double quotes inside the query, then wrap each whitespace-separated
        // token in double quotes so FTS5 treats them as literal phrases. A trailing
        // `*` is preserved as a prefix-search shorthand so `auth*` can match
        // `authenticate` without requiring raw FTS5 syntax.
        // クエリ内のダブルクォートをエスケープし、各トークンをダブルクォートで囲む。
        // 末尾の `*` は prefix 検索の shorthand として保持し、`auth*` で
        // `authenticate` を raw FTS5 構文なしに検索できるようにする。
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return "\"\"";
        return string.Join(" ", tokens.Select(FormatFtsToken));
    }

    /// <summary>
    /// Build a single FTS5 phrase token. Tokens that contain CJK script (Han, Hiragana,
    /// Katakana, Hangul, and their fullwidth/halfwidth variants) get an appended '*'
    /// prefix-match operator because FTS5's default unicode61 tokenizer treats a run
    /// of adjacent CJK codepoints as a single token, so a bare '計算' query would never
    /// match content containing '計算する'. The CJK prefix promotion is unconditional,
    /// so a full-token CJK query also widens to longer tokens (e.g. '計算する' also
    /// matches '計算する追加'); callers who need strict equality on CJK substrings must
    /// route through the `exact` / `instr` path.
    /// FTS5フレーズトークンを構築。FTS5既定のunicode61トークナイザはCJK連続を単一トークンとして扱うため、
    /// CJK文字を含むトークンには prefix match の '*' を付け、'計算' で '計算する' にマッチさせる。
    /// prefix 昇格は CJK トークンに対して無条件なので、完全トークンのクエリでも長いトークンへ広がる
    /// （'計算する' は '計算する追加' もマッチ）。CJK 部分文字列の厳密一致が必要な呼び出し側は
    /// `exact` / `instr` 経路を使う。
    ///
    /// Non-CJK tokens (Latin-diacritic like 'café', Greek, Cyrillic, emoji-mixed, etc.)
    /// skip the CJK prefix path. This prevents prefix over-widening (e.g. 'foo🎉' →
    /// 'foo*' matching 'foobar'), but it does NOT make those tokens "exact-phrase" at
    /// the FTS layer: unicode61 tokenizes Latin-diacritic normally and drops symbol
    /// codepoints entirely, so 'foo🎉' is indexed and queried as the token 'foo' and
    /// therefore cannot be distinguished from a bare `def foo():` in FTS. Callers who
    /// need strict equality with emoji must route through the `exact` / `instr` path.
    /// CJK 以外のトークン（Latin-diacritic、Greek、Cyrillic、絵文字混在等）は CJK prefix 経路を
    /// 通さない。これは prefix の過度な拡張（'foo🎉' → 'foo*' で 'foobar' まで拾う）を防ぐが、
    /// FTS 層で「完全一致」を保証するものではない。unicode61 は Latin-diacritic を通常どおり
    /// トークン化し、symbol コードポイントは完全に drop するため、'foo🎉' は両側で 'foo' と
    /// トークン化され素の `def foo():` と区別できない。emoji の厳密一致が必要な場合は
    /// `exact` / `instr` 経路を使う。
    /// </summary>
    private static string FormatFtsToken(string token)
    {
        var hasExplicitPrefix = token.Length > 1 && token.EndsWith('*');
        var literalToken = hasExplicitPrefix ? token[..^1] : token;
        var quoted = "\"" + literalToken.Replace("\"", "\"\"") + "\"";
        // '"phrase"*' (no space) is FTS5 prefix-phrase syntax. '"phrase" *' (with space)
        // means "phrase followed by any token", which is not what we want.
        // '"phrase"*'（スペースなし）がFTS5のprefix phrase構文。スペース有りは別意味なので付けない。
        return (hasExplicitPrefix || ContainsCjk(literalToken)) ? quoted + "*" : quoted;
    }

    private static bool ContainsCjk(string token)
    {
        foreach (var rune in token.EnumerateRunes())
        {
            if (IsCjkScript(rune))
                return true;
        }
        return false;
    }

    private static bool IsCjkScript(Rune rune)
    {
        // Exclude symbol categories up front so emoji / pictographs / currency marks
        // never trigger CJK prefix fallback even if they live inside CJK-adjacent blocks.
        // `OtherNotAssigned` (Cn) is deliberately NOT excluded. The simple reason is that
        // .NET's Unicode tables lag the Unicode Consortium's releases — codepoints newly
        // assigned by Unicode (e.g. Extension I at U+2EBF0..U+2EE5F, assigned in Unicode
        // 15.1) still report as Cn on .NET 8 — so excluding Cn here would silently regress
        // real CJK codepoints simply because the runtime has not caught up. The subtler
        // reason is that SQLite's bundled `unicode61` tokenizer carries its own (different
        // and older) Unicode tables: empirical verification through the locally built
        // binary on .NET 8 shows that codepoints which .NET reports as Cn inside the
        // block-envelope ranges below (e.g. Tangut's reserved U+187F8..U+187FF, Khitan's
        // reserved U+18CD6..U+18CFF, Tangut Supplement's reserved U+18D09..U+18D8F) are
        // still kept as word characters by unicode61 during indexing. Allowing them to
        // prefix-promote is therefore not only safe but necessary: it preserves the same
        // "`search <codepoint>` must match a chunk containing `<codepoint>abc`" invariant
        // that the whole #198 fix is built on. Truly unreachable codepoints (ones that
        // neither .NET nor unicode61 treat as word characters) cannot produce a match even
        // if the predicate returns true, because FTS5's query tokenizer drops them on its
        // own side.
        // シンボル系カテゴリは先に除外。emoji等がCJK隣接ブロックにあってもprefix fallbackを起こさせない。
        // `OtherNotAssigned` (Cn) は意図的に除外しない。単純な理由: .NET の Unicode テーブルは
        // Unicode Consortium のリリースに遅れるため、Unicode で新たに割り当てられた
        // コードポイント（例: Unicode 15.1 で追加された Extension I の U+2EBF0..U+2EE5F）も
        // .NET 8 では依然として Cn と報告される。ここで Cn を除外すると、ランタイム側のテーブル
        // 更新遅延だけを理由に実在の CJK コードポイントが静かに regressions する。踏み込んだ理由:
        // SQLite 同梱の `unicode61` トークナイザは .NET とは別の（そしてより古い）Unicode
        // テーブルを持っており、ローカルビルドしたバイナリを .NET 8 で動かして検証した結果、
        // .NET が Cn と報告する「ブロック末尾の予約領域」のコードポイント (例: Tangut の
        // U+187F8..U+187FF、Khitan の U+18CD6..U+18CFF、Tangut Supplement の
        // U+18D09..U+18D8F) も unicode61 は indexing 側で単語文字として保持する。よってこれらを
        // prefix 昇格の対象に含めるのは安全かつ必要で、#198 の根幹不変条件「`search <codepoint>`
        // は `<codepoint>abc` を含むチャンクに必ずヒットする」を壊さないための前提になっている。
        // 本当に到達不能なコードポイント（.NET も unicode61 も単語文字として扱わないもの）は、
        // 述語が true を返しても FTS5 側のクエリトークナイザが落とすので一致は発生しない。
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.OtherSymbol
                     or UnicodeCategory.MathSymbol
                     or UnicodeCategory.CurrencySymbol
                     or UnicodeCategory.ModifierSymbol)
            return false;

        var value = rune.Value;
        // Hiragana / Katakana (including Phonetic Extensions and Katakana Phonetic Extensions),
        // Kana Supplement + Kana Extended-A + Small Kana Extension (U+1B000..U+1B16F), and
        // Kana Extended-B (U+1AFF0..U+1AFFF, Unicode 15.0).
        // ひらがな・カタカナ（音声拡張を含む）、Kana Supplement / Extended-A / Small Kana Extension、
        // および Kana Extended-B。
        if (value >= 0x3040 && value <= 0x30FF) return true;
        if (value >= 0x31F0 && value <= 0x31FF) return true;
        if (value >= 0x1AFF0 && value <= 0x1AFFF) return true;
        if (value >= 0x1B000 && value <= 0x1B16F) return true;

        // Bopomofo (U+3100..U+312F) and Bopomofo Extended (U+31A0..U+31BF). These are the
        // Mandarin Chinese phonetic system ("zhuyin"). Bopomofo letters are Unicode category
        // Lo (Other Letter), so unicode61 keeps them as regular word characters, which means
        // the same "short query vs longer token" failure mode as the original #198 repro
        // applies to Chinese phonetic text like 'ㄅabc' without explicit CJK prefix promotion.
        // 注音符号（ボポモフォ）と拡張注音符号。中国語の発音記号で Unicode カテゴリは Lo。
        // unicode61 は通常の単語文字として扱うため、'ㄅabc' のような中国語発音テキストでも
        // #198 の 0 件症状を起こさないよう CJK prefix 昇格の対象に含める。
        if (value >= 0x3100 && value <= 0x312F) return true;
        if (value >= 0x31A0 && value <= 0x31BF) return true;

        // Han-script codepoints that live outside the CJK Unified Ideographs / Extensions
        // blocks: ideographic iteration/closing marks (々 U+3005, 〆 U+3006), the ideographic
        // number zero (〇 U+3007), Hangzhou numerals (U+3021..U+3029), vertical kana repeat
        // marks (U+3031..U+3035), and the Hangzhou 10/20/30 + vertical iteration mark block
        // (U+3038..U+303B). U+3031..U+3035 are Unicode category Lm (Letter Modifier), while
        // the others are Lo / Nl — all are kept as word characters by unicode61, so they need
        // CJK prefix promotion to match against longer indexed tokens like '々abc', '〱abc', or
        // '〇abc'. The narrow explicit list avoids the whole CJK Symbols and Punctuation block
        // (U+3000..U+303F), which includes ideographic space / brackets / dots that unicode61
        // either drops or already tokenizes on.
        // CJK Unified Ideographs 範囲外の Han script コードポイント: 々(U+3005)、〆(U+3006)、
        // 〇(U+3007)、Hangzhou 数字(U+3021..U+3029)、縦書き仮名反復記号(U+3031..U+3035)、
        // Hangzhou 10/20/30 と縦書き反復記号(U+3038..U+303B)。U+3031..U+3035 は Unicode カテゴリ Lm、
        // その他は Lo / Nl。いずれも unicode61 で通常の単語文字として扱われるため、'々abc'、
        // '〱abc'、'〇abc' のような長いトークンへ CJK prefix 昇格を届かせる必要がある。
        // CJK Symbols and Punctuation ブロック全体（U+3000..U+303F）を拾わないよう、個別列挙にしている。
        if (value == 0x3005) return true;
        if (value == 0x3006) return true;
        if (value == 0x3007) return true;
        if (value >= 0x3021 && value <= 0x3029) return true;
        if (value >= 0x3031 && value <= 0x3035) return true;
        if (value >= 0x3038 && value <= 0x303B) return true;

        // CJK Unified Ideographs + Extensions A-I, Compatibility Ideographs.
        // CJK Radicals Supplement (U+2E80..U+2EFF) and Kangxi Radicals (U+2F00..U+2FDF)
        // are intentionally excluded: every codepoint in those blocks is Unicode
        // category OtherSymbol (So) and gets dropped by unicode61 during tokenization,
        // so prefix fallback cannot help anyway.
        // CJK統合漢字および拡張 A-I、互換漢字。CJK Radicals Supplement と Kangxi Radicals は
        // Unicodeカテゴリが OtherSymbol (So) で unicode61 が drop するため、prefix fallback を
        // かけても意味がなく、意図的に除外する。
        if (value >= 0x4E00 && value <= 0x9FFF) return true;
        if (value >= 0x3400 && value <= 0x4DBF) return true;
        if (value >= 0x20000 && value <= 0x2A6DF) return true;
        if (value >= 0x2A700 && value <= 0x2EBEF) return true;
        if (value >= 0x2EBF0 && value <= 0x2EE5F) return true; // Extension I (Unicode 15.1)
        if (value >= 0x30000 && value <= 0x3134F) return true; // Extension G
        if (value >= 0x31350 && value <= 0x323AF) return true; // Extension H (Unicode 15.0)
        if (value >= 0xF900 && value <= 0xFAFF) return true;
        if (value >= 0x2F800 && value <= 0x2FA1F) return true;

        // Historical East Asian scripts that unicode61 keeps as word characters and which
        // Unicode groups with / adjacent to CJK. Each Unicode block is listed as its own
        // branch so (a) regression tests can exercise one branch at a time and (b) future
        // narrowing or widening of any single block does not drag the others. Block-envelope
        // ranges cover each block's full Unicode-block bounds including the small reserved
        // holes at each block's tail (e.g. Tangut's U+187F8..U+187FF, Khitan Small Script's
        // U+18CD6..U+18CFF, Tangut Supplement's U+18D09..U+18D8F, which .NET 8 reports as
        // Cn on today's runtime tables). Empirically — verified through the locally built
        // binary on .NET 8 — unicode61 still keeps those codepoints as word characters
        // during indexing, so content containing `<hole-codepoint>abc` is indexed as a
        // single token and needs prefix promotion to satisfy the same #198 invariant.
        // Prefix-promoting these holes therefore produces correct behavior: if the
        // codepoint appears in indexed content, the search finds it; if it does not, FTS5
        // simply returns no rows.
        //   * Yi Syllables (U+A000..U+A48F, Lo — Nuosu syllabary)
        //   * Tangut (U+17000..U+187FF, Lo — Western Xia logographs, non-BMP)
        //   * Tangut Components (U+18800..U+18AFF, Lo — non-BMP)
        //   * Khitan Small Script (U+18B00..U+18CFF, Lo — non-BMP)
        //   * Tangut Supplement (U+18D00..U+18D8F, Lo — non-BMP)
        //   * Nüshu (U+1B170..U+1B2FF, Lo — Jiangyong women's script, non-BMP)
        // Also add the individual iteration / annotation codepoints in the Ideographic
        // Symbols and Punctuation block (U+16FE0..U+16FF1) that annotate Han-based
        // historical text. On the current runtime these are category Lm (U+16FE0 Tangut
        // iteration mark, U+16FE1 Nüshu iteration mark, U+16FE3 Old Chinese iteration
        // mark), Mn (U+16FE4 Khitan Small Script filler), and Mc (U+16FF0 / U+16FF1
        // Vietnamese alternate reading marks CA / NHAY). All are kept as word characters
        // by unicode61 and reproduce the #198 zero-hit shape without explicit CJK prefix
        // promotion. U+16FE2 (Old Chinese hook mark) is intentionally excluded because
        // it is Unicode category Po and unicode61 drops it during tokenization. Yi
        // Radicals (U+A490..U+A4CF) are intentionally excluded because they are Unicode
        // category So and are also dropped by unicode61 during tokenization.
        // 東アジアの歴史的文字で unicode61 が単語文字として扱い、Unicode 上も CJK 隣接に
        // 配置されているブロック。将来いずれかを絞ったり広げたりしても他ブロックが巻き添えに
        // ならないよう、Unicode ブロック単位で個別分岐にする。ブロックベース範囲は各ブロックの
        // Unicode ブロック境界全域を覆うので、末尾の小さな予約領域（例: Tangut の
        // U+187F8..U+187FF、Khitan の U+18CD6..U+18CFF、Tangut Supplement の U+18D09..U+18D8F、
        // いずれも現行の .NET 8 ランタイムでは Cn）も含まれる。ローカルビルドしたバイナリを
        // .NET 8 で動かして実証した結果、これらの予約領域コードポイントも unicode61 は
        // indexing 側で単語文字として保持する。したがって `<予約領域>abc` を含むコンテンツは
        // 単一トークンとしてインデックスされ、#198 と同じ不変条件を満たすには prefix 昇格が必要。
        // 予約領域を prefix 昇格してもコンテンツ中にそのコードポイントが現れれば一致するだけで、
        // 存在しなければ FTS5 は単に 0 行を返す — どちらも期待される挙動で誤マッチにはならない。
        //   * Yi Syllables (U+A000..U+A48F, Lo — 中国南西部のノス族音節文字)
        //   * Tangut (U+17000..U+187FF, Lo — 西夏の表意文字、非 BMP)
        //   * Tangut Components (U+18800..U+18AFF, Lo — 非 BMP)
        //   * Khitan Small Script (U+18B00..U+18CFF, Lo — 非 BMP)
        //   * Tangut Supplement (U+18D00..U+18D8F, Lo — 非 BMP)
        //   * Nüshu (U+1B170..U+1B2FF, Lo — 湖南省江永県の女性専用音節文字、非 BMP)
        // また Ideographic Symbols and Punctuation ブロック (U+16FE0..U+16FF1) の反復 /
        // 注釈記号も個別に含める。現行ランタイムではカテゴリ Lm (U+16FE0 Tangut 反復記号、
        // U+16FE1 Nüshu 反復記号、U+16FE3 Old Chinese 反復記号)、Mn (U+16FE4 Khitan Small
        // Script filler)、Mc (U+16FF0 / U+16FF1 ベトナム語 Chu Nom 読み記号 CA / NHAY)。
        // いずれも unicode61 は単語文字として保持するため、CJK prefix 昇格を入れないと
        // #198 と同じ 0 件症状を再現する。U+16FE2 (Old Chinese hook mark) は Unicode カテゴリ
        // Po で unicode61 が drop するため意図的に除外する。Yi Radicals (U+A490..U+A4CF) は
        // Unicode カテゴリ So でこれも unicode61 が drop するため意図的に除外する。
        if (value == 0x16FE0) return true;
        if (value == 0x16FE1) return true;
        if (value == 0x16FE3) return true;
        if (value == 0x16FE4) return true;
        if (value >= 0x16FF0 && value <= 0x16FF1) return true;
        if (value >= 0xA000 && value <= 0xA48F) return true;
        if (value >= 0x17000 && value <= 0x187FF) return true;
        if (value >= 0x18800 && value <= 0x18AFF) return true;
        if (value >= 0x18B00 && value <= 0x18CFF) return true;
        if (value >= 0x18D00 && value <= 0x18D8F) return true;
        if (value >= 0x1B170 && value <= 0x1B2FF) return true;

        // Hangul Syllables, Jamo, Jamo Extended-A/B, Compatibility Jamo
        // ハングル音節およびJamo
        if (value >= 0xAC00 && value <= 0xD7AF) return true;
        if (value >= 0x1100 && value <= 0x11FF) return true;
        if (value >= 0x3130 && value <= 0x318F) return true;
        if (value >= 0xA960 && value <= 0xA97F) return true;
        if (value >= 0xD7B0 && value <= 0xD7FF) return true;

        // Halfwidth and Fullwidth Forms: halfwidth Katakana (U+FF65..U+FF9F) plus
        // halfwidth Hangul Letters (U+FFA0..U+FFDC). The range intentionally stops
        // at U+FFDC: U+FFE0..U+FFEE are fullwidth currency / arrow symbols that
        // unicode61 drops anyway, and U+FFDD..U+FFDF are unassigned.
        // Halfwidth/Fullwidth ブロックの半角カナ (U+FF65..U+FF9F) と半角ハングル
        // (U+FFA0..U+FFDC)。U+FFDD 以降は未割当や unicode61 が drop する記号なので含めない。
        if (value >= 0xFF65 && value <= 0xFFDC) return true;

        return false;
    }

    /// <summary>
    /// Full-text search across indexed chunks using FTS5.
    /// FTS5を使ったチャンク全文検索。
    /// </summary>
    public List<SearchResult> Search(string query, int limit = 20, string? lang = null, bool rawQuery = false, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool deduplicate = true, DateTime? since = null, bool exact = false)
    {
        // Guard against empty/whitespace queries that would match everything
        // 空白のみのクエリが全件マッチするのを防止
        if (string.IsNullOrWhiteSpace(query))
            return [];

        lang = NormalizeQueryLanguage(lang);
        var normalizedQuery = rawQuery ? query : NormalizeLiteralSearchQuery(query, lang);
        using var cmd = _conn.CreateCommand();
        string sql;

        if (exact)
        {
            // Exact substring match using instr() — case-sensitive, no FTS5 tokenization
            // instr() による完全部分一致検索 — 大文字小文字区別、FTS5トークナイズなし
            sql = $@"
                SELECT f.path, f.lang, c.start_line, c.end_line, c.content,
                       0.0 AS rank
                FROM chunks c
                JOIN files f ON c.file_id = f.id{SearchSymbolMatchJoinsSql}
                WHERE instr(
                    {GetExactSearchTextSql("c.content", "f.lang")},
                    {GetExactSearchTextSql("@exactQuery", "f.lang")}
                ) > 0";
        }
        else
        {
            var sanitizedQuery = rawQuery ? query : SanitizeFtsQuery(normalizedQuery);
            sql = $@"
                SELECT f.path, f.lang, c.start_line, c.end_line, c.content,
                       rank
                FROM fts_chunks
                JOIN chunks c ON fts_chunks.rowid = c.id
                JOIN files f ON c.file_id = f.id{SearchSymbolMatchJoinsSql}";
            sql += " WHERE fts_chunks MATCH @query";
            cmd.Parameters.AddWithValue("@query", sanitizedQuery);
        }
        if (lang != null)
            sql += " AND f.lang = @lang";
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @since";

        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += $" ORDER BY {GetSearchOrderSql()} LIMIT @limit";

        cmd.CommandText = sql;
        if (exact)
            cmd.Parameters.AddWithValue("@exactQuery", query);
        cmd.Parameters.AddWithValue("@rankingQuery", normalizedQuery.Trim());
        cmd.Parameters.AddWithValue("@rankingQueryPrefix", $"{EscapeLikeQuery(normalizedQuery.Trim())}%");
        cmd.Parameters.AddWithValue("@limit", limit);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            cmd.Parameters.AddWithValue("@since", since.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var raw = new List<SearchResult>();
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                raw.Add(new SearchResult
                {
                    Path = reader.GetString(0),
                    Lang = GetNullableString(reader, 1),
                    StartLine = reader.GetInt32(2),
                    EndLine = reader.GetInt32(3),
                    Content = reader.GetString(4),
                    Score = reader.GetDouble(5),
                });
            }
        }
        catch (SqliteException ex) when (rawQuery && IsFtsQuerySyntaxError(ex))
        {
            throw new FtsQuerySyntaxException(ex.Message, ex);
        }
        return deduplicate ? DeduplicateOverlappingResults(raw) : raw;
    }

    public QueryCountResult CountSearchResults(string query, string? lang = null, bool rawQuery = false, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool deduplicate = true, DateTime? since = null, bool exact = false)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new QueryCountResult(0, 0);

        lang = NormalizeQueryLanguage(lang);
        var normalizedQuery = rawQuery ? query : NormalizeLiteralSearchQuery(query, lang);
        using var cmd = _conn.CreateCommand();
        string sql;

        if (exact)
        {
            sql = $@"
                SELECT f.path, c.start_line, c.end_line,
                       0.0 AS rank
                FROM chunks c
                JOIN files f ON c.file_id = f.id{SearchSymbolMatchJoinsSql}
                WHERE instr(
                    {GetExactSearchTextSql("c.content", "f.lang")},
                    {GetExactSearchTextSql("@exactQuery", "f.lang")}
                ) > 0";
        }
        else
        {
            var sanitizedQuery = rawQuery ? query : SanitizeFtsQuery(normalizedQuery);
            sql = $@"
                SELECT f.path, c.start_line, c.end_line,
                       rank
                FROM fts_chunks
                JOIN chunks c ON fts_chunks.rowid = c.id
                JOIN files f ON c.file_id = f.id{SearchSymbolMatchJoinsSql}";
            sql += " WHERE fts_chunks MATCH @query";
            cmd.Parameters.AddWithValue("@query", sanitizedQuery);
        }
        if (lang != null)
            sql += " AND f.lang = @lang";
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @since";

        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += $" ORDER BY {GetSearchOrderSql()}";

        cmd.CommandText = sql;
        if (exact)
            cmd.Parameters.AddWithValue("@exactQuery", query);
        cmd.Parameters.AddWithValue("@rankingQuery", normalizedQuery.Trim());
        cmd.Parameters.AddWithValue("@rankingQueryPrefix", $"{EscapeLikeQuery(normalizedQuery.Trim())}%");
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            cmd.Parameters.AddWithValue("@since", since.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var keptIntervals = new Dictionary<string, IntervalSet>(StringComparer.Ordinal);
        var count = 0;
        var fileCount = 0;
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var path = reader.GetString(0);
                var startLine = reader.GetInt32(1);
                var endLine = reader.GetInt32(2);
                if (!deduplicate)
                {
                    count++;
                    if (!keptIntervals.ContainsKey(path))
                    {
                        keptIntervals[path] = new IntervalSet();
                        fileCount++;
                    }
                    continue;
                }

                if (!keptIntervals.TryGetValue(path, out var intervals))
                {
                    intervals = new IntervalSet();
                    keptIntervals[path] = intervals;
                }

                if (!intervals.AddIfNoOverlap(startLine, endLine))
                    continue;

                count++;
                if (intervals.Count == 1)
                    fileCount++;
            }
        }
        catch (SqliteException ex) when (rawQuery && IsFtsQuerySyntaxError(ex))
        {
            throw new FtsQuerySyntaxException(ex.Message, ex);
        }

        return new QueryCountResult(count, fileCount);
    }

    private static string NormalizeLiteralSearchQuery(string query, string? lang) =>
        string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase)
            ? CSharpVerbatimNameNormalizer.Normalize(query)
            : query;

    private static bool IsFtsQuerySyntaxError(SqliteException ex)
    {
        var message = ex.Message;
        return message.Contains("fts5: syntax error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unterminated string", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no such column", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExactSearchTextSql(string valueSql, string langSql)
        => $"CASE WHEN {langSql} IN ('csharp', 'java', 'kotlin') THEN sql_normalize_exact_source_name({valueSql}, {langSql}) " +
           $"WHEN {langSql} = 'sql' THEN sql_normalize_name({valueSql}) " +
           $"ELSE {valueSql} END";

    /// <summary>
    /// Remove search results that overlap with a higher-ranked result in the same file.
    /// Chunks use 10-line overlap, so adjacent chunks can produce duplicate matches.
    /// 同じファイル内で上位の結果と行範囲が重なる結果を除去する。
    /// チャンクは10行重複するため、隣接チャンクが重複マッチを生じうる。
    /// </summary>
    private static List<SearchResult> DeduplicateOverlappingResults(List<SearchResult> results)
    {
        if (results.Count <= 1)
            return results;

        var keptIntervals = new Dictionary<string, IntervalSet>(StringComparer.Ordinal);
        var deduped = new List<SearchResult>();
        foreach (var r in results)
        {
            if (!keptIntervals.TryGetValue(r.Path, out var intervals))
            {
                intervals = new IntervalSet();
                keptIntervals[r.Path] = intervals;
            }

            if (!intervals.AddIfNoOverlap(r.StartLine, r.EndLine))
                continue;

            deduped.Add(r);
        }
        return deduped;
    }

    private sealed class IntervalSet
    {
        private readonly List<(int Start, int End)> _intervals = [];

        public int Count => _intervals.Count;

        public bool AddIfNoOverlap(int start, int end)
        {
            if (end < start)
                (start, end) = (end, start);

            var insertIndex = FindInsertIndex(start);
            if (insertIndex > 0 && Overlaps(_intervals[insertIndex - 1], start, end))
                return false;
            if (insertIndex < _intervals.Count && Overlaps(_intervals[insertIndex], start, end))
                return false;

            _intervals.Insert(insertIndex, (start, end));
            return true;
        }

        private int FindInsertIndex(int start)
        {
            var low = 0;
            var high = _intervals.Count;
            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (_intervals[mid].Start < start)
                    low = mid + 1;
                else
                    high = mid;
            }

            return low;
        }

        private static bool Overlaps((int Start, int End) interval, int start, int end)
        {
            return interval.Start <= end && interval.End >= start;
        }
    }

    private static string GetSearchOrderSql()
    {
        return $"{PathBucketOrder}, {ExactSymbolMatchOrder}, {PrefixSymbolMatchOrder}, {PathTextMatchOrder}, {ChunkTextMatchOrder}, rank, f.modified DESC, f.path";
    }

}
