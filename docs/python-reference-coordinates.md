# Python reference coordinates

## English

Python reference columns use one-based UTF-16 code units. LSP reference ranges
use the same source positions with zero-based lines and characters. String masking
preserves the original UTF-16 length, including both code units of an astral
character such as `😀`, so literals before calls cannot shift later references.
Repeated calls on one line retain their own positions; names inside ordinary
strings and comments do not become calls.

For example, `    s = '😀'; 終了()` on source line 4 records column 15. The LSP
range starts at line 3, character 14 and ends at character 16.

Existing Python rows written with extractor contract 1 or 2 need a normal
whole-workspace index refresh (`cdidx index <project> --db <db>`). Contract 3
re-extracts unchanged Python files and replaces the old reference coordinates;
no schema migration or forced rebuild is required. Until refreshed, stored rows
retain their old coordinates. Other languages' extraction contracts are unchanged.
This repairs source positions without changing the existing heuristic reference
resolution or expanding Python syntax support.

## 日本語

Python の参照列は、UTF-16 コード単位で数えた 1 始まりの位置です。LSP の参照範囲は
同じソース位置を 0 始まりの行・文字位置で表します。文字列のマスクは元の UTF-16 長を
保持し、`😀` のような補助平面文字の 2 コード単位も維持するため、呼び出し前の文字列で
後続の参照位置がずれません。同一行の複数の呼び出しはそれぞれの位置を保持し、通常の
文字列やコメント内の名前は呼び出しとして扱いません。

例えば、ソースの 4 行目にある `    s = '😀'; 終了()` の参照列は 15 です。LSP の
範囲は行 3・文字位置 14 から始まり、文字位置 16 で終わります。

抽出契約バージョン 1 または 2 で保存した Python の行は、通常のワークスペース全体の
索引更新（`cdidx index <project> --db <db>`）が必要です。バージョン 3 は変更のない
Python ファイルも再抽出して古い参照座標を置き換えます。スキーマ移行や強制再構築は
不要です。更新するまでは保存済みの古い座標が残ります。他言語の抽出契約は変わりません。
この修正はソース位置の補正のみを行い、既存のヒューリスティックな参照解決や Python
構文の対応範囲は変更しません。
