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
