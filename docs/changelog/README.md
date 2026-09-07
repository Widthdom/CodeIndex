# Changelog archives

## English

[Current changelog and archive index](../../CHANGELOG.md#archives--アーカイブ)

The root keeps both Unreleased sections and releases from v1.41.0 (2026-08-04)
onward. Earlier history is stored in descending release order in
[v1.22.0–v1.40.3](v1.22.0-v1.40.3.md) and
[v1.0.0–v1.21.0](v1.0.0-v1.21.0.md). Each file has an English section followed
by Japanese; each release's two entries live in the same file.

The initial cutoff retains August 2026 onward at the root. Starting with
v1.40.3 and walking backward, we placed consecutive whole release pairs into
each archive until adding the next pair and its compare definition would
exceed 2 MiB minus 8 KiB reserved for navigation. This gives reproducible
release boundaries, without assuming a calendar year fits. Initial sizes are
341,001 bytes for the root, 1,940,515 and 1,957,517 bytes for the archives.
All are below 2 MiB, with more than 2 MiB remaining before the standard
4 MiB indexing limit. No size override or history exclusion is required.

`dotnet run --project tools/CodeIndex.Changelog -- check` validates fragments
and all history files. The history ceiling is 3 MiB (3,145,728 UTF-8 bytes),
reserving at least 1 MiB below the indexing limit. Validation rejects missing
or duplicate bilingual releases, differing dates/order, invalid or missing
compare definitions, overlapping archive ranges, incorrect filenames, missing
archive-index links, and missing return navigation. Discovery is limited to
256 archive files. `prepare` validates both existing history and its rendered
output before any writes. `release-notes` accepts current or archived versions
and retains the existing fixed GitHub release-note template. Archived releases
cannot be targets of `prepare`.

Before a release, run `check` and the non-mutating `render --version X.Y.Z
--date YYYY-MM-DD` preview. If root growth would exceed 3 MiB, first archive
older history in the release-preparation PR (or an explicitly authorized
history-maintenance PR):

1. Keep both Unreleased sections and the current release at the root. Move
   the oldest consecutive release pairs until the root is at most 2 MiB.
   Never split a release or rewrite its historical content.
2. Group the moved pairs newest-first using the 2 MiB minus 8 KiB budget
   above. Use new `v<oldest>-v<newest>.md` files, keeping existing archives
   stable. Copy the English and Japanese blocks verbatim; retain legacy
   indentation and category headings. Put the relevant original reference
   definitions in each file; compare bases may name a release in another file.
3. Add each archive to the root index and link back to `../../CHANGELOG.md`
   from the archive prefix. Extend the root compatibility table with the
   former GitHub heading IDs and links to the new locations. Release heading
   `[1.40.3] - 2026-07-27` uses `1403---2026-07-27` in English and
   `1403---2026-07-27-1` in Japanese. Preserve relative and local links when
   moving content; if a body link would need rewriting, use compatible
   navigation or explicitly review that exception.
4. Compare every original release block and compare definition against the
   moved copies, and confirm each language appears exactly once. Update this
   inventory, run `check`, and exercise `prepare`/`release-notes` in disposable
   fixtures. Run ordinary indexing of `.` with the project-built binary and
   explicit root `--db .cdidx/codeindex.db`, then both root and workspace
   `status --check`. Verify representative old English and Japanese searches.

The #5294 migration preserves all 117 release pairs and both Unreleased
blocks. The root compatibility table preserves former release anchors;
repository consumers use the root URL or the changelog tool, so their entry
points remain valid. Historical text is unchanged, including the indented
Japanese v1.16.0 heading and legacy level-three category headings.
The small [USER_GUIDE.md compatibility page](USER_GUIDE.md) keeps the two
original relative error-code links working without rewriting historical prose.
It and this README are navigation documents, not release archives.

## 日本語

[最新の変更履歴とアーカイブ一覧](../../CHANGELOG.md#archives--アーカイブ)

ルートには両言語の Unreleased と v1.41.0（2026-08-04）以降を残します。
それ以前は [v1.22.0–v1.40.3](v1.22.0-v1.40.3.md) と
[v1.0.0–v1.21.0](v1.0.0-v1.21.0.md) にリリースの降順で保存します。
各ファイルは英語セクション、日本語セクションの順で、同一リリースの日英を
必ず同じファイルに保管します。

初回の境界では2026年8月以降をルートに残しました。v1.40.3 から古い順に
連続する日英のリリースと比較リンク定義をまとめ、次の組を追加すると
2 MiB から案内用の 8 KiB を引いた容量を超える箇所で分割しました。
年単位で収まるとは仮定せず、再現可能なリリース境界を使います。
初回のサイズはルート 341,001 バイト、アーカイブ 1,940,515 バイトと
1,957,517 バイトです。すべて 2 MiB 未満で、標準の索引上限 4 MiB まで
2 MiB 以上の余裕があり、サイズ上書きや履歴の除外は不要です。

`dotnet run --project tools/CodeIndex.Changelog -- check` は fragment と
すべての履歴を検証します。履歴の検証上限は 3 MiB（UTF-8 で 3,145,728
バイト）で、索引上限まで最低 1 MiB の余裕を確保します。日英の欠落・重複、
日付や順序の不一致、不正・欠落した比較リンク、範囲の重複、ファイル名の
不一致、一覧からのリンクや戻るリンクの欠落を拒否します。アーカイブは
最大256ファイルです。`prepare` は書き込み前に既存履歴と生成結果を検証します。
`release-notes` は現在と保管済みのバージョンに対応し、既存の固定形式の
GitHub リリースノートを維持します。保管済みのリリースは `prepare` の対象にできません。

リリース前に `check` と非変更の `render --version X.Y.Z --date YYYY-MM-DD`
で確認します。ルートが 3 MiB を超える場合はリリース準備 PR、または明示的に
承認された履歴保守 PR で、先に古い履歴を移します。

1. 両 Unreleased と現行リリースを残し、ルートが 2 MiB 以下になるまで
   最古側の連続する日英ペアを移します。リリース途中の分割や本文改変は禁止です。
2. 移動対象を新しい順に上記の 2 MiB − 8 KiB の予算でまとめ、新しい
   `v<最古>-v<最新>.md` を作ります。既存アーカイブは維持し、日英本文、
   字下げ、旧カテゴリ見出しをそのままコピーします。元の比較リンク定義も
   対応するファイルに置きます。比較元が別ファイルのリリースでも構いません。
3. ルート一覧とアーカイブ冒頭の `../../CHANGELOG.md` へのリンクを追加し、
   互換表に旧 GitHub 見出し ID と移動先を追加します。
   `[1.40.3] - 2026-07-27` の ID は英語が `1403---2026-07-27`、
   日本語が `1403---2026-07-27-1` です。相対リンクや文書内リンクも維持し、
   本文の書き換えが必要なら互換案内を設けるか、例外として明示的にレビューします。
4. 元の全リリース本文・比較リンク定義と移動先を照合し、各言語が1回ずつ
   存在することを確認します。この一覧を更新して `check` を実行し、使い捨て
   fixture で `prepare` と `release-notes` を検証します。リポジトリでビルドした
   CLI と明示的な `--db .cdidx/codeindex.db` で `.` を通常索引化した後、
   ルートと workspace の `status --check`、古い日英本文の検索を確認します。

#5294 の移行では117リリースの日英ペアと両 Unreleased を保持しました。
ルートの互換表は旧リリースアンカーを維持し、ルート URL や Changelog ツールを
使う既存の参照元も継続利用できます。日本語 v1.16.0 の字下げ見出しや
旧レベル3カテゴリ見出しも含め、過去の本文は変更していません。
[USER_GUIDE.md の互換ページ](USER_GUIDE.md)により、本文を書き換えずに
元のエラーコード表への相対リンク2件を維持します。この README と互換ページは
リリースアーカイブではなく案内文書です。
