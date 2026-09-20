# Initial full-index performance

C# reference extraction tracks local declarations only for callable bodies whose
prepared lines may contain `=>`. Arrow positions are collected lazily once per file;
eligibility is cached per callable, and missing body bounds remain conservative.
Empty enum catalogs skip qualified-member scanning, and ordinary unqualified words
do not allocate segment lists during that scan. C#, Razor, Blazor and CSHTML share
these paths. Capture order, overload isolation, masked strings/comments, qualified
enum reads and name normalization retain their existing behavior.

Fresh reference writes encode the pending self-reference and mutual-recursion flags
as SQL zero literals. Both the native and provider writers bind 12 values per row
instead of 14; the native writer can fit 42 rows within its existing 512-parameter
budget instead of 36. Ordinary writes retain their supplied flags and 14 bindings.
Full mutual-recursion refreshes skip known-zero rows that cannot match a
reverse edge: only distinct resolved identities or fully unresolved name pairs need
evaluation. Nonzero and NULL flags are always revisited, including non-call rows,
so stale and legacy values are repaired. All language writers share these changes;
name folding, reverse-edge matching, transaction boundaries and rollback stay unchanged.

C# declaration confirmation also rejects impossible prefixes before regex evaluation.
Before the first `(`, an unmatched `)` or statement punctuation outside generic
arguments cannot match either anchored declaration pattern. Generic argument text
stays opaque, and encountering `(` returns control to the existing tuple/parameter
regex grammar. This avoids expensive confirmation of multiline parameter fragments
that acquire an opening parenthesis from later method-body text. C#, Razor, Blazor
and CSHTML share the gate in both pre-extraction and normal symbol extraction.
No extractor contract or output changes; differential tests retain full symbol
records, constructors, tuple/generic returns, explicit interfaces and function pointers.

C# enum-member extraction rejects impossible qualifiers, calls and simple assignment
targets before constructing receiver-shadow scopes. Ordinary and recursive declaration
patterns reuse each callable's body text and the offsets already available to their
scans, avoiding a new full-body string and a preceding-line scan per pattern. C#,
Razor, Blazor and CSHTML share this behavior. Reference coordinates, shadowing rules,
alias/global qualification and extraction limits are unchanged; no cache survives
the extraction. Regression coverage compares all symbol/reference fields and bounds
allocations for 128 scoped patterns instead of asserting elapsed time.

Fresh reference-source lookup now checks the materialized file set once through
partial indexes for display aliases and legacy NULL canonical keys. When neither exists, references use only
the existing canonical-name range probe, without the three-way union and final
comparison sort. Files with either form retain all three probes. The SQL and native
statement caches distinguish both shapes, including repeated-source batches, and
the proof is reset and recomputed on each materialization, including after file rollback.
This is shared by every language using the fresh writer; names, containment ranks,
tie-breaking, database layout and transaction boundaries are unchanged.

Reference position lookup can use an exact recorded column when a preceding
character proves that no earlier name match could overlap it. Other inputs keep
the forward non-overlapping search and stop once later matches cannot be nearer;
ties still choose the earlier match. All language writers share this qualifier
lookup. C# type and invocation arity lookups share the shortcut and combine their
trimmed-column and first-eligible fallback searches into one pass. Escaped/Unicode
names, identifier boundaries, constructor recognition and unusable columns keep
their existing behavior. A 2,048-reference dense-line regression examines one
occurrence per recorded hit instead of rescanning all 2,048 names. A separate
warmed 4,096-type-reference probe took about 163 ms before and 0.6–1.4 ms after;
this input-specific observation is not a general latency guarantee.

Exact-input punctuation gates reject 25 audited declaration patterns across C#,
Java, Kotlin, C/C++, JavaScript and TypeScript before regex evaluation. C# aliases
share them. Each pattern declares its required characters: explicit-interface
members accept `(` or `[`, applicable generic prefixes accept `(` or `<`, and
brace properties require `{`. The checks inspect merged and recovery inputs,
preserve existing word-gate validation, and leave regexes unchanged. Compact Java
constructors and argument-free arrow parameters retain their existing paths.
C# same-line member probes also require property body punctuation or event/delegate
words before attempting the corresponding regexes.

Reference candidate ranks 1–4 probe the existing file/name and container/name
indexes before evaluating ranking predicates. An ID set combines these three
eligible scopes without duplicating symbols that match more than one scope.
Original language restrictions, C# attribute suffixes, null/empty containers,
same-file fallback and tied candidates remain unchanged. The shared SQL serves
initial indexing, scoped refreshes and retained graph rebuilds; no schema change
is needed. On the fixed 1,577-file snapshot, the isolated candidate statement fell
from 301.27 million to 49.17 million SQLite VM instructions (about 84% less work),
with identical candidate triples. This is statement work, not whole-index speed.
The three probes add overhead when names are unique or every same-name declaration
is already in an eligible scope: isolated 1,024-reference unique-name fixtures used
171k→249k VM instructions with local targets and 83k→102k without a source container.
Their observed statement times increased by 0.5–0.9 ms. The benefit depends on how
many unrelated same-name declarations the original global probe would visit.

C# declaration confirmation rejects impossible suffixes before attempting the
member/method regexes, including expression-bodied declarations and accessor
lookahead. After optional whitespace and one opening brace, methods require `)`;
members require an identifier ending. Non-ASCII suffixes still reach the member
regex, preserving its Unicode semantics. C#, Razor, Blazor and CSHTML share the
check in both workspace pre-extraction and ordinary extraction. No extractor
contract or persisted output changes. Tests compare every symbol property with
the gate disabled and verify fewer regex attempts instead of asserting timing.

C# lambda capture extraction scans each body once for eligible local names and
retains the first occurrence's column, then emits captures in declaration order.
Parameter shadowing, escaped/Unicode names, and method/overload scope boundaries
retain their existing behavior. Function-local lookup keys retain all scope
fields without formatting a new string for every declaration. C#, Razor, Blazor
and CSHTML share both improvements. A warmed 128-local fixture with a long class
name allocated 2.51 MB before and 0.34 MB after; the net8/net9 regression ceiling
is 1 MB. This measures extraction allocations, not whole-index speed.

An ordinary CLI full scan of an empty database uses the authoritative fresh
bulk writer. Its text bindings use at most 1 KiB of stack scratch space and
pooled buffers for larger UTF-8 values, instead of allocating a byte array for
every long chunk, signature, or reference context. SQLite copies each value
before the scratch buffer is reused. Pooled arrays are cleared on return,
including when binding fails. Empty text, NULL, embedded NUL, supplementary
Unicode, and UTF-8 replacement of unpaired surrogates retain their existing
storage semantics. The optimization is shared by all indexed languages.

This does not change database layout, extraction coverage, transaction
boundaries, or cancellation and rollback behavior. Ordinary updates, rebuilds,
and MCP writes keep their existing writer selection.

When display aliases or legacy keys require all name forms, fresh reference-source
lookup probes the canonical, display, and legacy ASCII name indexes with `UNION ALL`.
Matching the same physical symbol through more
than one name does not change its containment rank or ID, and only the first
ranked ID is consumed. Avoiding duplicate elimination removes a temporary
B-tree per reference while preserving same-file scoping, nested range selection,
tie-breaking, and legacy fallback. Ordinary source repair remains unchanged.

Those temporary indexes also carry containment rank. Each probe stops at its
first containing declaration, and the final comparison sorts at most three
candidates. A non-containing early candidate is skipped before that limit.
This applies to every language using the fresh writer and retains file-savepoint
rollback. A regression with 128 overlapping same-name declarations and 64 lookups
used 520 callbacks at 1,000 SQLite VM instructions per callback before this
change; the new budget is at most 32 callbacks on both test runtimes. This measures
lookup work, not end-to-end speed or worst-case misses across disjoint ranges.

Fresh reference INSERTs also resolve repeated source identities once per statement
when at least half the batch rows repeat an earlier nonempty source identity.
The identity includes file, line, original container name and its folded override;
NULL and empty names stay distinct. A materialized, bounded source map is joined
back to every original reference in input order. Unique or sparsely repeated
sources, absent container names and single-row inserts retain direct probes.
Both SQL and native-statement caches distinguish the two shapes, and no lookup
survives the statement or file savepoint. The shared
writer applies this to all languages. A 128-reference fixture over 128 disjoint
same-name declarations stays within 140 callbacks at 1,000 SQLite VM instructions
per callback, including source materialization and insertion; the prior path
used 217 callbacks in the corresponding isolated probe. This is a work budget,
not a guarantee of faster end-to-end indexing for every reference distribution.

All language-independent folded symbol/reference keys use runtime-vectorized
ASCII validation and casing. Unicode names still normalize with NFKC and apply
the same vendored casefold deltas, but append lowered scalar values directly
instead of allocating intermediate strings per character. Already folded ASCII
names retain their original string. Persisted keys, their contract version and
runtime fingerprint are unchanged; readers and every writer use the same fold.

C# declaration lookahead retains a confirmed method header while checking for
property accessors. When the first body token rules out an accessor, it stops
there instead of joining subsequent method bodies up to a later field. This
avoids repeated large strings in files with many multiline methods, preserves
method ranges and signatures, and recovers affected constructor declarations.
The same extractor handles Razor, Blazor and CSHTML. All four language keys now
use C# extractor contract 19, so an ordinary full scan refreshes old rows even
when source files are unchanged. Comments and accessor attributes still receive
the existing lookahead.

C# static-lambda rejection examines only arrows that can still enclose the
candidate name and explicit-return prefixes within the current declaration
segment. Parenthesized tuple groups remain intact. This avoids repeated scans
and prefix allocations across earlier members on a dense line, while preserving
static constructors, generic methods, and typed/tuple/function-pointer lambdas.
Razor, Blazor and CSHTML share this behavior. A warmed extraction of 64 same-line
static methods allocated 406.5 MB before these bounds and 0.93 MB after them;
the regression fixture includes positive controls and allows 2 MiB on net8/net9.
This allocation budget does not assert a wall-clock speed for other inputs.

Confirmed method-prefix matching also requires an opening parenthesis before
running the regex. Parameter continuation fragments ending in `) {` cannot match
without it; the fast rejection preserves full symbol records and is covered by
regex-attempt counts rather than a timing threshold.

On a fixed 1,554-file snapshot of this repository at `0d39d0658`, a fresh Release
.NET 8 index on macOS ARM64 with `--parallelism 2 --memory-trace` took 76.0 seconds
before these three changes and 68.9 seconds after them. Total managed allocations
fell from 18.46 GB to 7.83 GB. Each run used a new database and the same source
snapshot. These are single-run observations, not a general speed guarantee.
All pre-existing symbol identities and extraction metadata remained identical;
seven missed constructors were recovered, removing seven declaration-site
reference rows and updating the corresponding overload-resolution candidates.
Both runs completed all 1,554 files without warnings or extraction errors.

For the suffix gates, per-statement source sharing and lambda capture changes,
the fixed snapshot is `a33c5a8eb`: 1,577 files, 59,387 symbols and 562,615 references.
Two fresh Release .NET 8 runs per version on macOS ARM64 with
`--parallelism 2 --memory-trace` took 71.4/74.4 seconds before and 64.0/60.6 seconds
after (about 15% lower mean elapsed time). Each run used a new database; heavy
tests were stopped during measurement. Total managed allocations were
7.93–7.96 GB before and 8.10 GB after, so this is not an overall allocation
reduction. All runs completed without warnings, errors or extraction cap hits.
Logical file, chunk, symbol, reference-line, issue, reference and resolution-candidate
records match after normalizing generated IDs and indexing timestamps. These
measurements describe this C#-heavy snapshot, not a general speed guarantee.

For the additional punctuation gates, scoped candidate seeks and reference-position
changes, the same `a33c5a8eb` source snapshot was measured against executable
`8533dff4f`. Two fresh Release .NET 8 runs per version on macOS ARM64, alternating
before/after with `--parallelism 2 --memory-trace`, took 59.6/60.9 seconds before
and 43.9/45.4 seconds after: mean 60.2→44.7 seconds, about 26% less elapsed time.
Total managed allocations fell from 8.09–8.10 GB to 7.48 GB, about 8%.
Heavy tests were stopped during measurement. All 1,577 files completed without
warnings, errors or extraction cap hits. The logical records in files, chunks,
symbols, reference lines, issues, references and candidate triples match after
normalizing generated IDs and indexing timestamps. These are two observations
per version on a C#-heavy snapshot; see the unique-name scope-probe tradeoff above.

For canonical-only source probes, receiver-scope reuse and declaration-prefix
pruning, the same `a33c5a8eb` snapshot was measured against executable `d374746ed`.
Two fresh Release .NET 8 runs per version on macOS ARM64, alternating before/after
with `--parallelism 2 --memory-trace`, took 44.1/43.1 seconds before and 37.4/37.9
seconds after: mean 43.6→37.7 seconds, about 13.6% less elapsed time. Total managed
allocations were 7.47 GB before and 7.51 GB after (about 0.5% higher). Each run used
a new database, with no heavy tests or builds running. All 1,577 files, 59,387 symbols
and 562,615 references completed without warnings, errors or extraction cap hits.
Both before/after pairs have identical logical file, chunk, symbol, reference-line,
issue, reference, candidate and hotspot records after generated-ID and indexing-time
normalization, including each reference's complete candidate set. Schema, user_version
and stable contract/readiness metadata also match; all four databases pass SQLite
integrity checks. FTS posting payloads were not compared. These are two observations
per version on a C#-heavy snapshot, not a general speed guarantee.

## 日本語

C#の参照抽出は、前処理済みの本体に `=>` を含む可能性がある関数だけでローカル宣言を
追跡します。矢印の位置はファイルごとに必要になってから1回収集し、関数ごとの判定を
再利用します。本体の範囲が不明な場合は保守的に追跡を続けます。列挙型の候補が空なら
修飾メンバーの走査を省き、走査中の非修飾の単語にはsegment listを確保しません。
C#・Razor・Blazor・CSHTMLで共通です。captureの順序・overloadの分離・文字列／commentの
マスク・修飾された列挙型の読取り・名前の正規化は既存の動作を維持します。

初回の参照書込みでは、グラフ確定前の自己参照・相互再帰flagをSQLの定数0として扱い、
native／providerの両writerで1行あたりのbindを14個から12個へ減らします。native writerは
既存の512パラメーター上限内で36行ではなく42行を処理できます。通常書込みは入力flagと
14個のbindを維持します。全体の相互再帰更新では、逆向きの参照が成立しない
既知の0の行を除外し、異なる解決済みidentityの対、または双方未解決の名前の対を評価します。
非0／NULLのflagは非call行も含め必ず再評価し、古い値や旧形式の値を修復します。
全言語のwriterで共通です。名前のfold・逆向き参照の照合・transaction境界・rollbackは
変更しません。

C#の宣言確認では、不可能なprefixも正規表現の評価前に除外します。最初の `(` より
前では、generic引数の外側の余分な `)` やstatement記号は、どちらの先頭固定の宣言
パターンにも一致しません。generic引数の内部は検査せず、`(` に達したら既存の
tuple／引数のregex文法へ委ねます。複数行引数の続きが後続メソッド本体から `(` を
取り込んでしまう場合の高価な宣言確認を避けます。C#・Razor・Blazor・CSHTMLの
事前抽出と通常抽出で共通です。抽出契約・出力は変えず、全シンボル項目とconstructor・
tuple／generic戻り値・明示的interface・関数pointerを最適化無効時と比較します。

C#のenum member抽出は、成立しない修飾子・呼出し・単純代入先をreceiverの隠蔽scope
作成前に除外します。通常／recursive declaration patternはcallableごとの本文と
走査で既知のoffsetを再利用し、patternごとの本文再生成と先行行の再走査を避けます。
C#・Razor・Blazor・CSHTMLで共通です。参照位置・隠蔽規則・alias／global修飾・抽出上限
は変わらず、抽出終了後に残るcacheもありません。全シンボル／参照項目の比較と、
scope付きpattern128個の割り当て上限で回帰を検証し、時間の閾値は使いません。

初回の参照元検索では、ファイル群の一時表を構築するたびに、部分indexでdisplay aliasと
旧形式のNULL canonical keyの有無を一度確認します。どちらもなければ既存のcanonical名の
範囲検索だけを使い、3経路の union と最終順位比較を省略します。いずれかがある場合は
従来の3経路を維持します。SQL／native statement の両キャッシュで、参照元共有バッチも
含めて形式を区別し、rollback後も含めて一時表の構築ごとに判定を破棄・再計算します。
初回 writer を使う全言語で共通です。名前・包含順位・同順位の選択・DB構造・transaction
境界は変わりません。

参照位置の検索では、直前の文字から先行する名前の一致が記録列をまたがないと
証明できる場合に、その列を直接使います。他の入力は従来の非重複の順方向検索を
維持し、後続の一致がより近くなり得ない位置で止めます。同距離なら先行する一致を
選びます。修飾子の検索は全言語のwriterで共通です。C#の型引数数・呼出し引数数も
この短縮処理を使い、trim済み列の検索と最初の適格な候補へのfallbackを1回の走査に
まとめます。escape／Unicode名・識別子境界・constructor判定・利用できない列の
挙動は維持します。密な1行の参照2,048件を使う回帰テストでは、毎回2,048個の名前を
走査する代わりに、記録位置の一致ごとに1候補だけを調べます。別の型参照4,096件の
ウォームアップ後の試験は約163 msから0.6～1.4 msへ短縮しました。この入力での観測値で
あり、一般的な応答時間を保証するものではありません。

変換済み入力の記号判定により、C#、Java、Kotlin、C/C++、JavaScript、TypeScriptの
監査済み25宣言パターンで不要な正規表現評価を省略します。C#の別名言語も共通です。
明示的interfaceメンバーの `(`/`[`、対象generic prefixの `(`/`<`、brace propertyの
`{` のように必要な記号はパターンごとに指定します。結合済み宣言・救済入力そのものを
検査し、正規表現・既存の単語判定・compact constructor・括弧なしarrow引数の挙動は
維持します。C#の同一行メンバー判定でも、property本体の記号やevent／delegateの
単語がある場合だけ、対応する正規表現を評価します。

参照候補の順位1～4では、既存のファイル／名前・コンテナ／名前の索引から対象を
取得して順位条件を評価します。3つの対象スコープをID集合にまとめ、複数スコープに
一致するシンボルの重複を除きます。言語制限・C# attribute の接尾辞・NULL／空の
コンテナ・同一ファイルへの fallback・同順位候補は維持します。初回インデックス・
差分更新・保持グラフの再構築で共通のSQLを使い、スキーマ変更はありません。
固定1,577ファイルの候補生成文だけを計測すると、SQLite VM命令は3億127万から
4,917万へ約84%減り、候補の全項目が一致しました。文単位の処理量であり、
インデックス全体の速度を示すものではありません。
名前が一意、または同名宣言がすべて対象スコープ内の場合には、3回の索引照会が
追加負荷になります。参照1,024件の一意名fixtureでは、同一ファイルの候補ありで
17.1万→24.9万命令、参照元コンテナなしで8.3万→10.2万命令となり、文の所要時間は
0.5～0.9 ms増えました。効果は、従来の名前検索で走査していた無関係な同名宣言の
件数によって変わります。

C# の宣言確認では、式形式の宣言と accessor の先読みも含め、成立しない末尾を
メンバー／メソッドの regex 照合前に除外します。空白と任意の開き brace 1個を
除くと、メソッドには `)`、メンバーには識別子の末尾が必須です。非 ASCII の末尾は
従来のメンバー regex へ渡し、Unicode の判定を維持します。C#・Razor・Blazor・
CSHTML の事前抽出と通常抽出で共通です。抽出契約と保存結果は変更しません。
テストでは gate 無効時と全シンボル項目を比較し、時間ではなく照合回数の減少を
検証します。

C# の lambda 捕捉抽出は、本体を1回走査して対象のローカル名と最初の出現位置を
保持し、宣言順に参照を出力します。引数による隠蔽、escape／Unicode 名、メソッド・
overload 間のスコープ分離を維持します。ローカル名の検索 key はスコープの全項目を
保持し、宣言ごとの文字列整形を省きます。C#・Razor・Blazor・CSHTML で共通です。
長いクラス名とローカル128個の fixture は、ウォームアップ後の割り当て量が変更前の
2.51 MBから0.34 MBへ減り、net8/net9の回帰上限を1 MBとしています。抽出の割り当て量の
測定であり、インデックス全体の速度を表すものではありません。

空のデータベースに対する通常の CLI フルスキャンは、初回専用の一括 writer を
使用します。文字列の bind には最大 1 KiB のスタック作業領域と、大きな UTF-8 値
向けのプールを使い、長いチャンク・シグネチャ・参照コンテキストごとの byte 配列
割り当てを避けます。SQLite が各値をコピーしてから作業領域を再利用し、プールの
配列は bind 失敗時もクリアして返却します。空文字列、NULL、埋め込み NUL、補助
平面 Unicode、不正な単独サロゲートの UTF-8 置換という既存の保存結果を維持します。
この最適化はすべての対応言語で共通です。

DB レイアウト、抽出範囲、トランザクション境界、取消・ロールバックの挙動は
変わりません。通常の更新、rebuild、MCP の writer 選択も既存のままです。

display aliasや旧形式keyのために全経路が必要な場合、初回の参照元検索は canonical 名・
display 名・旧 ASCII 名の index を `UNION ALL` で照会します。
同じ実体シンボルが複数の名前から見つかっても包含範囲の順位と ID は
同じであり、消費するのは順位先頭の ID だけです。参照ごとの重複除去用の一時 B-tree
を省きつつ、同一ファイルへの限定、入れ子の選択、同順位の決定、旧形式への fallback
を維持します。通常の参照元修復処理は変更しません。

一時 index には包含範囲の順位も持たせ、各照会では参照行を含む最上位の宣言だけを
取得します。最後に並べ替える候補は最大3件です。範囲外の候補は件数制限の前に除外
します。初回 writer を使う全言語に適用し、ファイル単位の savepoint rollback も
維持します。同名の重複範囲128件に対する64回の照会では、変更前は SQLite VM の
1,000命令ごとの callback が520回、変更後の回帰テスト上限は両 runtime で32回です。
これは照会処理量の測定であり、全体の速度や互いに離れた範囲での miss の最悪値を
保証するものではありません。

初回の参照 INSERT でも、バッチの半数以上の行が先行する空でない参照元と重複する
場合は、その解決を statement ごとに一度へまとめます。
識別にはファイル・行・元のコンテナ名・folded override を含め、NULL と空文字列も
区別します。上限付きの一時的な対応表を元の全参照へ入力順で結合します。1行だけの
INSERT、重複が少ないバッチ、コンテナ名のないバッチは従来の直接検索を使います。
SQL と native statement の両キャッシュで形式を区別し、検索結果は statement や
ファイル savepoint を越えて残りません。全言語共通の writer に適用します。
離れた同名宣言128件と同じ行の参照
128件の fixture では、参照元の一時表作成と INSERT を含め、SQLite VM 1,000命令ごとの
callback 上限を140回としています。対応する単独計測で従来経路は217回でした。
これは処理量の上限であり、あらゆる参照分布で全体の所要時間が短縮する保証ではありません。

全言語共通のシンボル・参照の folded key は、ランタイムのベクトル化された ASCII
検査と小文字化を使います。Unicode 名は既存の NFKC 正規化と casefold 差分表を
維持しつつ、文字ごとの一時文字列を作らずに小文字化した scalar を直接追加します。
変換済みの ASCII 名は元の文字列を再利用します。保存 key、契約バージョン、実行環境
fingerprint は変わらず、reader と各 writer は同じ変換を使います。

C# 宣言の先読みでは、プロパティの accessor を調べる間、確認済みのメソッド
header を保持します。body の先頭 token で accessor でないと分かれば停止し、
後続メソッドの body を末尾の field まで連結し続けません。複数行メソッドが多い
ファイルで巨大な一時文字列の反復生成を避け、メソッドの範囲・signature を保ち、
影響を受けていた constructor 宣言も回復します。同じ抽出器を使う Razor・Blazor・
CSHTML を含む4つの言語 key は C# 抽出契約19を使い、通常のフルスキャンで未変更の
既存ファイルも再抽出します。コメントや accessor 属性の先読みは維持します。

C# の静的 lambda の除外判定は、対象名をまだ含み得る arrow と、現在の宣言区間内の
明示的な戻り値型 prefix に限定します。括弧で囲まれた tuple group は維持します。
密な1行で前のメンバーまで繰り返し走査・文字列生成する処理を避け、static constructor・
generic method・型付き／tuple／関数ポインター型 lambda の判定を保ちます。
Razor・Blazor・CSHTML も共通です。同一行の static method 64件のウォームアップ後の
抽出は、この制限の前で406.5 MB、後で0.93 MBを割り当てました。回帰fixtureには
正例の対照も加え、net8/net9とも上限2 MiBを設けます。別の入力に対する所要時間の
保証ではありません。

確認済みメソッド prefix の照合でも、regex 実行の前に開き括弧を必須とします。
`(` を含まず `) {` で終わる引数の継続断片は照合できないため、先に除外します。
全シンボル項目の一致と regex 試行回数で検証し、時間の閾値には依存しません。

このリポジトリの `0d39d0658` 時点の1,554ファイルを固定し、macOS ARM64・Release
.NET 8・`--parallelism 2 --memory-trace` で空DBから計測したところ、3つの変更前は
76.0秒、変更後は68.9秒でした。managed の総割り当て量は18.46 GBから7.83 GBに
減りました。毎回新しいDBと同じソースを使った単回の観測値であり、一般的な速度を
保証するものではありません。既存シンボルの識別情報・抽出メタデータを維持し、
欠落していた constructor 7件を回復して、宣言位置の参照7行を除去し、対応する
overload の解決候補も更新しました。
両方とも全1,554ファイルを警告・抽出エラーなしで完了しています。

宣言末尾 gate・statement 内の参照元共有・lambda 捕捉の3変更は、`a33c5a8eb` の
固定ソース（1,577ファイル・59,387シンボル・562,615参照）でも測定しました。
macOS ARM64・Release .NET 8・`--parallelism 2 --memory-trace` で各版2回ずつ、
毎回新しいDBを使い、重いテストを止めて計測しています。変更前は71.4／74.4秒、
変更後は64.0／60.6秒で、平均所要時間は約15%減りました。managed の総割り当て量は
変更前7.93～7.96 GB、変更後8.10 GBであり、全体の割り当て削減ではありません。
全実行で警告・エラー・抽出上限到達はありませんでした。生成IDとインデックス時刻を
正規化すると、ファイル・チャンク・シンボル・参照行・issue・参照・解決候補の全レコードが
一致します。C#中心のこの固定ソースでの観測値であり、一般的な速度を保証しません。

追加の記号判定・スコープ別候補検索・参照位置検索も、同じ `a33c5a8eb` のソースを
固定し、実行ファイル `8533dff4f` と比較しました。macOS ARM64・Release .NET 8・
`--parallelism 2 --memory-trace` で変更前後を交互に各2回、毎回空DBから計測した結果、
変更前59.6／60.9秒、変更後43.9／45.4秒で、平均60.2→44.7秒、約26%の短縮でした。
managedの総割り当て量は8.09～8.10 GBから7.48 GBへ約8%減りました。
計測中は重いテストを停止し、全1,577ファイルが警告・エラー・抽出上限到達なしで
完了しています。生成ID・インデックス時刻を正規化すると、ファイル・チャンク・
シンボル・参照行・issue・参照・解決候補の全レコードが一致します。各版2回の
C#中心の固定ソースでの観測値です。一意名に対する索引照会の追加負荷は上記のとおりです。

canonical名だけの参照元検索・receiver scopeの再利用・宣言prefixの除外も、同じ
`a33c5a8eb` の固定ソースを使い、実行ファイル `d374746ed` と比較しました。
macOS ARM64・Release .NET 8・`--parallelism 2 --memory-trace` で変更前後を交互に
各2回測定し、変更前44.1／43.1秒、変更後37.4／37.9秒、平均43.6→37.7秒で
約13.6%短縮しました。managedの総割り当て量は変更前7.47 GB、変更後7.51 GBで
約0.5%増えています。毎回新しいDBを使い、重いテストやビルドを実行せずに計測しました。
全1,577ファイル・59,387シンボル・562,615参照が警告・エラー・抽出上限到達なしで
完了しています。両方の変更前後ペアで、生成ID・インデックス時刻を正規化すると、
ファイル・チャンク・シンボル・参照行・issue・参照・解決候補・hotspot集計の全レコードが
一致し、参照ごとの候補集合も一致しました。schema・user_version・実行時刻等に依存しない
契約／readiness metadataも一致し、4つのDBすべてがSQLiteの整合性検査を通過しています。
FTSのposting payloadは比較対象外です。C#中心の固定ソースで各版2回の観測値であり、
一般的な速度を保証するものではありません。
