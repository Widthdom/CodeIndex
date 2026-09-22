# Python search origins

## English

Search facets and filtered regex find share bounded Python lexical classification
from **indexed source**, without running Python or importing modules. Use
`cdidx find subprocess --path .agent_harness/ --regex --origin code --count --json`
or `cdidx search subprocess --lang python --origin code --json`. MCP `search`,
`find`, and `find_in_file` use the same classifier with their existing `origin`,
`excludeOrigin`, `resultKind`, `excludeComments`, `excludeStrings`, and
`excludeFixtures` options. Find semantic filters require `regex:true`.

The syntax envelope is Python 3.12/3.13 lexical string syntax, including PEP 701:

- Code and `#` comments, ordinary single/double and triple quotes, escaped quotes
  and physical line continuations, and case-insensitive `r`, `u`, `b`, `br`, `rb`,
  `f`, `fr`, and `rf` prefixes are recognized. Docstrings remain `string_literal`.
- F-string literal text and doubled literal braces are `string_literal`;
  replacement expressions are `code`. Nested ordinary/raw/bytes/f-strings and
  comments inside expressions keep their own origins. Same-quote expressions and
  multiline expressions are supported, including in a single-quoted f-string.
- Balanced parentheses, brackets and dictionaries, debug `=`, `!s`/`!r`/`!a`,
  literal format specifications and nested format replacement expressions are
  supported. Parenthesize lambdas and assignment expressions as Python requires.
  A format specification containing the enclosing string's quote is conservatively
  unsupported. Python 3.14 template strings (`t`/`tr`/`rt`) are unsupported.

This is lexical origin evidence, not syntax validation or runtime/dataflow proof.
Malformed lexical delimiters, unsupported prefixes/formats/conversions, missing
indexed lines, conflicting overlap and exhausted budgets produce `unknown` with
bounded `origin_unavailable` reason/start/extent evidence. An unfinished string or
replacement invalidates provisional origins from its outermost opening delimiter.
Unknowns remain non-authoritative even when negative filters reject every unknown
match. CLI filtered counts/rows return partial exit `11` (`--allow-partial` accepts
`0` without restoring authority). CLI/MCP keep classification completeness separate
from scan completion; MCP search and recipe children also preserve bounded
`classification_incomplete_reasons` and `classification_recovery_guidance`.

Each file/pass classifies at most 4,096 lines and admits 8 Mi UTF-16 source
characters (at most 24 Mi UTF-8 bytes) from at most 128 indexed chunks, charging
overlap. The shared reader bounds each SQLite substring to 8 Mi + 1 Unicode
characters; an over-budget chunk stops that window before admitting partial lines. CLI
`--origin-passes 1..16` (default 1) continues the same lexical state across bounded
windows, up to 65,536 lines / 128 Mi UTF-16 source characters per file. A context
retains at most 262,144 merged non-code spans, 64 string/replacement frames and 64
balanced delimiters per replacement. Each line retains at most one additional
comparison view (the same source length plus an optional CR terminator) so repeated
matches do not rescan its source.
Scanning checks cancellation at each lexical
step and at most every 4,096 characters within identifier/escape runs. Contexts
are shared within a returned search page or one find file scan, never globally;
new requests rebuild only their explicitly bounded prefixes. Pagination and match
counts do not increase these limits. Indexed-generation changes discard the
provisional context.

When `retry_origin_passes` is available, restart the CLI query **without a cursor**
using the suggested `--origin-passes`. A zero-progress window, malformed/missing
context, span/nesting cap or the 16-pass ceiling cannot be repaired by more passes.
MCP retains its existing one-pass budget; inspect unknowns without exclusions or
use CLI continuation. Increasing output limits cannot repair classification.
Stable reasons include `indexed_prefix_unavailable`, `indexed_text_mismatch`,
`indexed_generation_changed`, `line_budget_exhausted`, `character_budget_exhausted`,
`chunk_budget_exhausted`, `python_span_budget_exhausted`, `interpolation_nesting_limit`,
`unbalanced_interpolation`, `unterminated_ordinary_string`,
`unsupported_python_string_prefix`, `unsupported_python_escape`,
`unsupported_interpolation_conversion`, and `unsupported_interpolation_expression`.

Original line, one-based UTF-16 column and match length are unchanged, including
astral characters and repeated/zero-width matches. Python end-of-line positions
use the lexical state at that position; an empty line inside a triple-quoted
string remains a string. LF/CRLF and chunk/page boundaries preserve the same
decisions. String matches in recognized test paths retain `test_fixture:true`.
There is no schema migration, extractor stamp change or reindex requirement for
this query feature. Live edits still need normal indexing. Python reference
resolution and its [coordinate contract](python-reference-coordinates.md#english)
are unchanged; the query lexer does not share reference-masking mutations.

## 日本語

search の facet と意味フィルター付き正規表現 find は、**索引済みソース**から上限付きで
Python の字句 origin を分類します。Python の実行やモジュールの import は行いません。
`cdidx find subprocess --path .agent_harness/ --regex --origin code --count --json`
または `cdidx search subprocess --lang python --origin code --json` を使います。
MCP の `search`、`find`、`find_in_file` も同じ分類器を使い、従来の `origin`、
`excludeOrigin`、`resultKind`、`excludeComments`、`excludeStrings`、`excludeFixtures`
に対応します。find の意味フィルターには `regex:true` が必要です。

対応範囲は PEP 701 を含む Python 3.12／3.13 の文字列字句構文です。

- コード、`#` コメント、単一／二重／三重引用符、引用符のエスケープ、物理行の継続と、
  大小文字を区別しない `r`、`u`、`b`、`br`、`rb`、`f`、`fr`、`rf` 接頭辞に対応します。
  docstring は `string_literal` のままです。
- f-string の文字部分と二重のリテラル波括弧は `string_literal`、補間式は `code` です。
  式に入れ子になった通常／raw／bytes／f-string やコメントはそれぞれの origin を保ちます。
  同じ引用符の再利用や、単一引用符の f-string 内も含む複数行の補間式に対応します。
- 対応する丸括弧・角括弧・辞書、デバッグ用 `=`、`!s`／`!r`／`!a`、書式の文字部分と
  入れ子の書式補間式に対応します。lambda と代入式は Python の規則に従って括弧で囲みます。
  外側の文字列と同じ引用符を含む書式指定は保守的に未対応とします。
  Python 3.14 のテンプレート文字列（`t`／`tr`／`rt`）には対応しません。

これは字句上の位置の根拠であり、構文検証や実行時・データフローの証明ではありません。
不正な区切り、未対応の接頭辞・書式・変換指定、索引行の欠落、重複行の不整合、予算超過は
`unknown` となり、上限付きの `origin_unavailable` に理由・開始位置・範囲を保持します。
未終了の文字列や補間式は、最も外側の開始区切り以降の暫定判定を無効化します。
負のフィルターで unknown をすべて除外しても、結果を確定扱いにはしません。
CLI のフィルター付き件数・行出力は部分結果の終了コード `11` を返します。
`--allow-partial` は確定性を回復せずに `0` を許容します。CLI／MCP は分類完了と走査完了を
区別し、MCP search と recipe の子結果も上限付きの `classification_incomplete_reasons` と
`classification_recovery_guidance` を保持します。

各ファイル・各パスは最大 4,096 行を分類し、重複分を含め最大 8 Mi UTF-16 文字（UTF-8 で
最大 24 Mi バイト）を、最大 128 索引チャンクから受け入れます。共通読み取り処理は SQLite の
各部分文字列を 8 Mi + 1 Unicode 文字に制限し、予算超過のチャンクでは不完全な行を採用せず
その窓を停止します。CLI の `--origin-passes 1..16`（既定 1）は同じ
字句状態を次の窓へ引き継ぎ、ファイルごとに最大 65,536 行／128 Mi UTF-16 文字まで扱います。
保持する非コード区間は結合後で最大 262,144 個、文字列・補間フレームは 64 個、
補間式ごとの対応する区切りは 64 個です。各行は元の長さ（任意の行末 CR を含む）の比較対象を追加で最大1つ保持し、
反復する一致ごとにソース全体を再比較しません。字句処理の各ステップ、および識別子・エスケープの
連続走査で最大 4,096 文字ごとにキャンセルを確認します。文脈は返却する検索ページ内または
find の1ファイル走査内で共有し、全体共有キャッシュには保持しません。新しい要求では明示した
上限内の先頭部分だけを再構築します。ページ位置や一致件数で予算を増やさず、索引世代が
変わった場合は暫定文脈を破棄します。

`retry_origin_passes` があれば、**カーソルを外し**、案内された `--origin-passes` で CLI を
再実行します。前進できない窓、欠落・不正な文脈、区間数・入れ子数の上限、16パスの上限は
追加パスで修復できません。MCP は従来の1パス予算を維持します。除外を外して unknown を
確認するか、CLI の継続を使ってください。出力件数を増やしても分類は回復しません。
安定した理由コードは `indexed_prefix_unavailable`、`indexed_text_mismatch`、
`indexed_generation_changed`、`line_budget_exhausted`、`character_budget_exhausted`、
`chunk_budget_exhausted`、`python_span_budget_exhausted`、`interpolation_nesting_limit`、
`unbalanced_interpolation`、`unterminated_ordinary_string`、`unsupported_python_string_prefix`、
`unsupported_python_escape`、`unsupported_interpolation_conversion`、
`unsupported_interpolation_expression` です。

元の行、1始まりの UTF-16 列、一致長は、補助平面文字・同一行の反復一致・ゼロ幅一致を含めて
維持します。Python の行末位置はその位置の字句状態を使い、三重引用符の中の空行は文字列です。
LF／CRLF とチャンク・ページ境界でも同じ判定を維持します。認識済みテストパスの文字列一致は
`test_fixture:true` のままです。このクエリ機能にスキーマ移行・抽出器のバージョン変更・
再索引は不要ですが、実ソースの編集には通常の索引更新が必要です。Python の参照解決と
[座標契約](python-reference-coordinates.md#日本語) は変わらず、参照用マスク処理も変更しません。
