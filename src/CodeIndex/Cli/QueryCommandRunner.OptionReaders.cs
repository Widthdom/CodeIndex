using System.Text;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    // Per-flag hints appended to "Error: <flag> requires a value." so users learn the expected
    // value type or range without consulting `--help`. Routed through BuildMissingOptionValueError
    // so every missing-value site reuses the same table and the messages stay consistent.
    // 「<flag> requires a value.」 missing-value error に追記するフラグ別ヒント。
    // すべての missing-value 経路を BuildMissingOptionValueError 経由にして、コマンド間で
    // メッセージを揃え、ヒントの単一情報源を維持する。
    private static readonly Dictionary<string, string> MissingOptionValueHints = new(StringComparer.Ordinal)
    {
        ["--db"] = "pass a path to a CodeIndex SQLite database, e.g. `--db .cdidx/codeindex.db` or `--db file:///absolute/path/to/codeindex.db?immutable=1`, or omit `--db` to use `.cdidx/codeindex.db`.",
        ["--workspace-db"] = "pass a path to another workspace member CodeIndex SQLite database. Repeat the flag up to 7 distinct additional DBs to aggregate multiple member DBs.",
        ["--data-dir"] = "pass a directory where cdidx should store `codeindex.db`, e.g. `--data-dir /var/cache/cdidx`.",
        ["--limit"] = "pass a positive integer, e.g. `--limit 20` (default 20).",
        ["--top"] = "pass a positive integer, e.g. `--top 20` (alias for `--limit`, default 20).",
        ["--body-start"] = "pass a 1-based source line inside the symbol body, e.g. `--body-start 120`.",
        ["--body-lines"] = "pass a positive line count for the body slice, e.g. `--body-lines 40`.",
        ["--body-line-count"] = "pass a positive line count for the body slice, e.g. `--body-line-count 40` (alias for `--body-lines`).",
        ["--lang"] = "pass a language identifier, e.g. `--lang csharp`. Run `cdidx languages` for the supported set.",
        ["--query"] = "pass a search literal, e.g. `--query \"authenticate\"`. Use the `--query` form when the literal starts with `-`.",
        ["--recipe"] = "pass a built-in audit recipe name, e.g. `--recipe risky-code`, or a child query selector such as `--recipe risky-code/raw-diagnostic-echo`; run `cdidx search --list-recipes` to list available recipes.",
        ["--include-query"] = "pass a child query name from the selected recipe, e.g. `--include-query raw-diagnostic-echo`; repeat or comma-separate values.",
        ["--exclude-query"] = "pass a child query name to omit from the selected recipe, e.g. `--exclude-query cancellation-gap`; repeat or comma-separate values.",
        ["--open-issues"] = "pass an open-issues JSON file or GitHub source, e.g. `--open-issues open-issues.json` or `--open-issues github --repo owner/name`; only valid with `search --format issue-drafts`.",
        ["--repo"] = "pass a GitHub repository in owner/name form for `--open-issues github`, e.g. `--repo Widthdom/CodeIndex`.",
        ["--issue-title"] = "pass an issue title hint for ad hoc search issue-drafts, e.g. `--issue-title \"Thread.Yield audit\"`.",
        ["--issue-label"] = "pass an issue label hint for search issue-drafts, e.g. `--issue-label audit`; repeat or comma-separate values.",
        ["--cursor"] = "pass the `next_cursor` returned by a prior paged response, such as a recipe search cursor, `outline:<offset>`, or `unused:<offset>`.",
        ["--kind"] = "pass a kind identifier, e.g. `--kind function`. definition/symbols/outline/hotspots/unused take a symbol kind; references/callers/callees take a reference kind such as `call`, `instantiate`, or `subscribe`. Run the command's `--help` for the kind list.",
        ["--outline-fields"] = "pass outline symbol field names such as `name,line,signature`, or `all` for the full symbol payload.",
        ["--outline-only"] = "for inspect, return file, definitions, and nearby_symbols JSON only; add `--body` when definition body snippets are needed.",
        ["--bucket"] = "pass one unused-symbol bucket: likely_unused_private, maybe_unused_nonpublic, public_or_exported_no_refs, or reflection_or_config_suspect.",
        ["--confidence"] = "pass one unused-symbol confidence threshold: medium or low.",
        ["--min-confidence"] = "pass one unused-symbol confidence threshold: medium or low.",
        ["--visibility"] = "pass one or more of public, protected, internal, private, e.g. `--visibility public,internal`.",
        ["--exclude-visibility"] = "pass one or more of public, protected, internal, private to exclude, e.g. `--exclude-visibility private`.",
        ["--rank-by"] = "pass `weighted`, `count`, or `kind` (callers/callees only).",
        ["--max-hops"] = "pass a non-negative integer, e.g. `--max-hops 5` (default 5).",
        ["--depth"] = "deprecated alias for `--max-hops`; pass a non-negative integer, e.g. `--max-hops 5` (default 5).",
        ["--path"] = "pass a glob-style path pattern, e.g. `--path src/**`. Repeat `--path` to add more patterns.",
        ["--exclude-path"] = "pass a glob-style path pattern to exclude, e.g. `--exclude-path tests/**`. Repeat `--exclude-path` to add more.",
        ["--since"] = "pass an ISO 8601 datetime, e.g. `--since 2024-01-01` or `--since 2024-01-01T00:00:00Z`.",
        ["--start"] = "pass a 1-based line number, e.g. `--start 10`.",
        ["--end"] = "pass a 1-based line number greater than or equal to `--start`, e.g. `--end 20`.",
        ["--before"] = "pass a non-negative integer of context lines before each match, e.g. `--before 2`.",
        ["--after"] = "pass a non-negative integer of context lines after each match, e.g. `--after 2`.",
        ["--focus-line"] = "pass a 1-based line number to focus on, e.g. `--focus-line 12`.",
        ["--focus-column"] = "pass a 1-based column number to keep visible, e.g. `--focus-column 80`.",
        ["--focus-length"] = "pass a positive integer for the focused span width, e.g. `--focus-length 1` (default 1).",
        ["--name"] = "pass a literal symbol name, e.g. `--name UserService`. Repeat `--name` to add more names.",
        ["--snippet-lines"] = "pass an integer between 1 and 20, e.g. `--snippet-lines 8` (default 8); issue-draft output also accepts 0 for path/line-only evidence.",
        ["--snippet-focus"] = "pass one of `leftmost`, `quality`, or `proximity`, e.g. `--snippet-focus quality` (default quality).",
        ["--max-line-width"] = "pass a non-negative integer (`0` disables clamping), e.g. `--max-line-width 512` (default 512).",
        ["--stale-after"] = "pass a compact positive duration, e.g. `--stale-after 30m`, `--stale-after 2h`, or `--stale-after 7d`.",
        ["--slow-query-ms"] = "pass a non-negative millisecond threshold, e.g. `--slow-query-ms 500`; use 0 to log every profiled SQL statement.",
        ["--min-entrypoint-confidence"] = "pass a decimal from 0.0 through 1.0, e.g. `--min-entrypoint-confidence 0.6`.",
        ["--sections"] = "pass a comma-separated map section list, e.g. `--sections tree,languages`. Supported sections: tree, languages, hotspots, metrics.",
    };

    // Build a missing-value error string with optional caller-supplied hint lines first, then the
    // per-flag hint from MissingOptionValueHints. Newline-separated so each Hint stays on its own
    // line when written via CommandErrorWriter.WriteStderr. Returns just the base error if no hint exists.
    // 呼び出し元固有のヒント (例: inline-form) を先に、テーブル由来のフラグ別ヒントを後ろに追記する。
    // CommandErrorWriter.WriteStderr 経由で出力されたとき各 Hint が別行になるよう改行で連結する。
    private static string BuildMissingOptionValueError(string optionName, params string?[] extraHintLines)
    {
        var sb = new StringBuilder();
        sb.Append("Error: ").Append(optionName).Append(" requires a value.");
        foreach (var hint in extraHintLines)
        {
            if (string.IsNullOrEmpty(hint))
                continue;
            sb.Append('\n').Append(hint);
        }
        if (MissingOptionValueHints.TryGetValue(optionName, out var perFlagHint))
            sb.Append('\n').Append("Hint: ").Append(perFlagHint);
        return sb.ToString();
    }

    private static bool TryReadRawOptionValue(string[] args, ref int index, string optionName, string? inlineValue, out string? value, out string? error)
    {
        if (inlineValue != null)
        {
            value = inlineValue;
            error = null;
            return true;
        }

        if (index + 1 >= args.Length)
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        var candidate = args[index + 1];
        // If the next token is itself a recognized CLI option, treat this as a missing-value
        // case rather than consuming the option as if it were a value. Without this guard
        // `--limit --lang rust` was parsed as `--limit=--lang` (numeric-parse failure) and then
        // the trailing `rust` was silently dropped, leaving the user with a confusing message
        // about `--lang` being an invalid integer.
        // 次トークンが別の既知オプションなら「値欠如」として扱い、index を進めない。これを
        // 入れないと `--limit --lang rust` が `--limit=--lang` と解釈され、後続の `rust` が
        // 黙って捨てられ、`--lang` が integer じゃないという混乱したメッセージが出てしまう。
        if (IsRecognizedOptionToken(candidate))
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        index++;
        value = candidate;
        error = null;
        return true;
    }

    private static bool TryReadStringOptionValue(string[] args, ref int index, string optionName, string? inlineValue, bool allowSeparatedDashPrefixedLiteralValue, out string? value, out string? error)
    {
        if (inlineValue != null)
        {
            if (string.IsNullOrWhiteSpace(inlineValue))
            {
                value = null;
                error = BuildMissingOptionValueError(optionName);
                return false;
            }

            value = inlineValue;
            error = null;
            return true;
        }

        if (index + 1 >= args.Length)
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        var candidate = args[index + 1];
        // Apply the recognized-option guard only when the option does NOT legitimately accept
        // separated dash-prefixed literal values. For flags like `--lang` / `--kind` / `--since`
        // / `--name` (allowSeparatedDashPrefixedLiteralValue=false), `--lang --limit 5` must stop
        // at `--limit` instead of consuming a known CLI flag as the `--lang` value. For flags like
        // `--db` / `--path` / `--exclude-path` / `--query` (allowSeparatedDashPrefixedLiteralValue=true),
        // skip this guard so the downstream `IsRejectedSeparatedStringValue` can emit the
        // inline-form hint for double-dash literals, preserving the pre-existing contract.
        // dash-prefix ヒューリスティックより前に既知オプション判定を置くが、この guard は
        // `allowSeparatedDashPrefixedLiteralValue=false` の時だけ適用する。`--lang` / `--kind` /
        // `--since` / `--name` は `--lang --limit 5` のとき `--limit` を値として飲み込まず値欠如
        // として扱う。`--db` / `--path` / `--exclude-path` / `--query` は dashed literal を受け入れる
        // 設計なので対象外とし、後段の `IsRejectedSeparatedStringValue` 側で double-dash に対する
        // inline-form ヒントを返して既存契約を維持する。
        if (!allowSeparatedDashPrefixedLiteralValue && IsRecognizedOptionToken(candidate))
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }
        if (optionName != "--query" && IsRejectedSeparatedStringValue(candidate, allowSeparatedDashPrefixedLiteralValue))
        {
            value = null;
            var inlineFormHint = allowSeparatedDashPrefixedLiteralValue && candidate.StartsWith("--", StringComparison.Ordinal)
                ? $"Hint: if the literal value starts with `--`, pass it as `{optionName}=<value>`."
                : null;
            error = BuildMissingOptionValueError(optionName, inlineFormHint);
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        index++;
        value = candidate;
        error = null;
        return true;
    }

    private static bool IsRejectedSeparatedStringValue(string candidate, bool allowSeparatedDashPrefixedLiteralValue)
    {
        if (!candidate.StartsWith("-", StringComparison.Ordinal))
            return false;

        if (!allowSeparatedDashPrefixedLiteralValue)
            return true;

        return candidate.StartsWith("--", StringComparison.Ordinal);
    }

    private static bool IsRecognizedOptionToken(string value) =>
        ValueTakingOptions.Contains(value) || FlagOnlyOptions.Contains(value);

    private static bool IsBareVerbatimQueryToken(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && trimmed.All(ch => ch == '@');
    }

    private static bool TrySplitInlineOptionValue(string token, out string? optionName)
    {
        optionName = null;
        var separator = token.IndexOf('=');
        if (separator <= 0)
            return false;

        var candidate = token[..separator];
        if (!InlineValueOptions.Contains(candidate))
            return false;

        optionName = candidate;
        return true;
    }
}
