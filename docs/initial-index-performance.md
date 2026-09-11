# Initial full-index performance

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

Fresh reference-source lookup probes the canonical, display, and legacy ASCII
name indexes with `UNION ALL`. Matching the same physical symbol through more
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

## 日本語

空のデータベースに対する通常の CLI フルスキャンは、初回専用の一括 writer を
使用します。文字列の bind には最大 1 KiB のスタック作業領域と、大きな UTF-8 値
向けのプールを使い、長いチャンク・シグネチャ・参照コンテキストごとの byte 配列
割り当てを避けます。SQLite が各値をコピーしてから作業領域を再利用し、プールの
配列は bind 失敗時もクリアして返却します。空文字列、NULL、埋め込み NUL、補助
平面 Unicode、不正な単独サロゲートの UTF-8 置換という既存の保存結果を維持します。
この最適化はすべての対応言語で共通です。

DB レイアウト、抽出範囲、トランザクション境界、取消・ロールバックの挙動は
変わりません。通常の更新、rebuild、MCP の writer 選択も既存のままです。

初回の参照元検索は canonical 名・display 名・旧 ASCII 名の index を `UNION ALL`
で照会します。同じ実体シンボルが複数の名前から見つかっても包含範囲の順位と ID は
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
