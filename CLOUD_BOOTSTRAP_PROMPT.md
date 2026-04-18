# Cloud Bootstrap Prompt

> **Maintainers / forkers only** — see [MAINTAINERS.md](MAINTAINERS.md). End users running cdidx on their own codebase don't need this file.
> **Maintainer・forker 向け** — 全体の索引は [MAINTAINERS.md](MAINTAINERS.md) を参照。自分のコードベースに cdidx を使うだけのエンドユーザーには不要です。

Kickoff prompt for Claude Code (cloud / claude.ai/code) sessions that work on
this repo without a local .NET SDK. Paste the English block or the Japanese
block — not both — into your session's first message.

Cloud 側の Claude Code セッション（.NET SDK が無い前提）に最初に投げる
プロンプトです。英語ブロックか日本語ブロックの**どちらか一方**を貼り付けて
ください。両方は貼らないでください。

---

## English

You are running in a container without the .NET SDK. That's a feature, not a
bug — you are the first user who cannot `dotnet build` their way out of a
broken install path, so any breakage in the published install flow will hit
you first. Use that.

### Step 1 — Install cdidx with the published one-liner (no .NET required)

```bash
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
export PATH="$HOME/.local/bin:$PATH"
```

If `raw.githubusercontent.com` is blocked in your environment, run the
repo-local installer instead and pin an explicit version (for example, the
value in this repo's `version.json`) to avoid the extra latest-release API
lookup:

```bash
bash ./install.sh vX.Y.Z
export PATH="$HOME/.local/bin:$PATH"
```

If release downloads are blocked from `github.com`, point the installer at a
reachable GitHub mirror/proxy by setting:
`CDIDX_GITHUB_BASE_URL` and `CDIDX_GITHUB_API_BASE_URL` before running
`install.sh`.

Example:

```bash
export CDIDX_GITHUB_BASE_URL="https://<your-mirror-host>"
export CDIDX_GITHUB_API_BASE_URL="https://<your-mirror-host>/api"
bash ./install.sh vX.Y.Z
```

Do not assume "Claude Code cloud has no outbound restrictions" just because it
works there. A more common difference is that each environment has different
egress policy / proxy allow-lists. Before concluding root cause, compare:

```bash
env | grep -Ei '(^|_)(http|https)_proxy|no_proxy' || true
curl -I https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh
curl -I https://github.com/Widthdom/CodeIndex/releases/download/vX.Y.Z/CodeIndex-linux-x64.tar.gz
```

If one environment gets `200/302` and another gets `403` (or `CONNECT tunnel
failed`), treat it as a network-policy difference first, not an installer bug.

### Optional: zero-external-network install-path self-test

If outbound traffic is blocked but you still want to verify installer logic
end-to-end, host a **local mirror** on `127.0.0.1` and point
`CDIDX_GITHUB_BASE_URL` to it. The installer still performs checksum
verification and extraction exactly as in production; only the source host is
local.

You can run the built-in self-test mode:

```bash
bash ./install.sh --self-test-local-mirror
```

This mode uses a mock payload only to validate installer flow (download URL
selection, checksum verification, extraction, placement). It is not a
replacement for validating official release artifacts in a network-open
environment.

The installer downloads the latest release tarball, verifies SHA256, and
copies the binary **plus the adjacent runtime assets** (`version.json` and
`libe_sqlite3.so` on Linux / `libe_sqlite3.dylib` on macOS) into
`$HOME/.local/bin/`. All three files must end up there — the native SQLite
library is loaded via P/Invoke from the binary's directory, and
`version.json` is what `cdidx --version` reads.

### Step 1.5 — Invoke `cdidx` via its fully expanded absolute path

The repo-tracked `.claude/settings.json` denies `Bash(cdidx:*)`,
`Bash(~/.local/bin/cdidx:*)`, and `Bash($HOME/.local/bin/cdidx:*)` as a
**best-effort tripwire** (see `CLAUDE.md`) against local sessions that
would silently fall back to a stale global binary. Claude Code
permission matching is textual, so invoking the installed binary via
its fully expanded absolute path is not matched by any of those three
entries and is therefore allowed without any permission edit. Resolve
the path once and reuse it for every step below:

```bash
CDIDX="$(readlink -f "$HOME/.local/bin/cdidx" 2>/dev/null || realpath "$HOME/.local/bin/cdidx")"
# Sanity check: must print an expanded absolute path (e.g. /root/.local/bin/cdidx).
echo "$CDIDX"
```

Do **not** edit the tracked `.claude/settings.json` to bypass the
tripwire. Doing so dirties the worktree (breaking `git_is_dirty` as a
trust signal for `status`/`inspect`) and risks an accidental commit
that weakens the tripwire for every other contributor. The expanded
absolute path is the intended, non-mutating unblock.

### Step 2 — Smoke test the installed binary end-to-end

Run every command below. If any of them fails on a clean install, that
failure is your highest-priority improvement target — every new user is
hitting the same wall.

```bash
"$CDIDX" --version                                     # must print a real version, v0.0.0 is NG
"$CDIDX" . --db /tmp/smoke.db                          # SQLite write path (human output)
"$CDIDX" status --db /tmp/smoke.db                     # SQLite read path
"$CDIDX" search "install" --path install.sh --snippet-lines 4 --db /tmp/smoke.db
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' | "$CDIDX" mcp
```

If `cdidx --version` reports `v0.0.0`, `version.json` is missing next to the
binary. If any command dies with `DllNotFoundException: Unable to load shared
library 'e_sqlite3'`, the native SQLite library was not installed next to the
binary. Both are install-path bugs — inspect `install.sh` and the layout
under `~/.local/bin/`, then peek inside the downloaded tarball with
`tar tzf` to confirm what the release ships vs. what the installer places.

### Known caveat: `--json` output

The published binary is built with `PublishTrimmed=true`, which disables
reflection-based `System.Text.Json` serialization. Any CLI command invoked
with `--json` (for example `cdidx index --json`, `cdidx status --json`)
currently fails fast with:

> `Error: --json is not available on this trimmed build.`
>
> `Hint: use `cdidx mcp` for structured output, omit `--json` for`
> `human-readable output, or use the NuGet/global-tool build if you need`
> `CLI JSON.`

MCP output is unaffected because the MCP path hand-rolls JSON. Until the
trimming / source-gen JSON issue is fixed in a future release, prefer the
default human-readable output, and use the MCP server when you need
structured responses.

### Step 3 — Operate within the no-SDK constraint

- Use the installed `cdidx` binary for all indexing and querying of this
  repo. Do **not** try `dotnet build` / `dotnet test` / `dotnet run` — they
  will not work here.
- The installed binary is whatever the latest published release ships. It
  is **not** this branch's unreleased C# changes. Use it to explore the
  repo, never to "verify" an unreleased source edit.
- Incremental reindex:
  ```bash
  # After editing tracked files locally (uncommitted edits) — use --files
  "$CDIDX" . --files <changed-file> [<changed-file> ...]
  # Or refresh the entire workspace
  "$CDIDX" .
  # ONLY after a commit — refresh paths from the last commit's diff
  "$CDIDX" . --commits HEAD
  ```
  `--commits HEAD` only covers paths in the last committed diff; it will
  not pick up uncommitted edits made in the current session. Default to
  `--files` (or a full `"$CDIDX" .`) after local edits to avoid searching
  a stale index.
- Read `SELF_IMPROVEMENT.md` and follow its loop, but substitute the
  rebuild / reindex steps above for anything that assumes a local SDK.

### Safe improvement targets from this environment

You can fully validate changes in these areas without a .NET SDK:

- `install.sh`, `.github/workflows/*.yml`, the contents and layout of the
  release tarball (inspect with `tar tzf`).
- `README.md`, `CHANGELOG.md`, `CLAUDE.md`, `SELF_IMPROVEMENT.md`,
  `TESTING_GUIDE.md`, `DEVELOPER_GUIDE.md` — both the English and the
  Japanese sections of each.
- The `# Code Search Rules` / `# コードベース検索ルール` template in
  `README.md`.
- MCP `instructions`, tool descriptions, help / usage strings, and
  user-visible error messages. Those strings can be located via the
  installed binary + `Grep` against the repo, even though you cannot
  rebuild the binary locally.

### If you must touch C\#

- Keep the change small and non-destructive.
- State plainly in the commit message and any PR description that this
  environment cannot validate the C# change, and defer to CI and to a
  reviewer with a local .NET SDK.
- Do **not** claim "verified locally" or "dotnet test passes" — you
  cannot prove either here.

### Everything else follows `CLAUDE.md` and `SELF_IMPROVEMENT.md`

Per-commit checklist, bilingual English / Japanese documentation,
`CHANGELOG.md` entries go under `[Unreleased]` only, no U+FFFD characters
in changed files, no PR unless the user explicitly asks. Confirm before
any destructive action.

---

## 日本語

あなたは .NET SDK のないコンテナで動いています。これは欠点ではなく強みです。
`dotnet build` に逃げられない最初のユーザーなので、公開済みのインストール
経路に壊れがあれば最初に踏むのはあなた自身です。それを活かしてください。

### Step 1 — 公開済みワンライナーで cdidx をインストール（.NET 不要）

```bash
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
export PATH="$HOME/.local/bin:$PATH"
```

もし環境の制約で `raw.githubusercontent.com` に到達できない場合は、リポジトリ
内の `install.sh` を直接実行し、`latest` API 参照を避けるため明示バージョン
（例: このリポジトリの `version.json` の値）を指定してください:

```bash
bash ./install.sh vX.Y.Z
export PATH="$HOME/.local/bin:$PATH"
```

`github.com` からのリリース取得自体がブロックされる環境では、`install.sh`
実行前に `CDIDX_GITHUB_BASE_URL` と `CDIDX_GITHUB_API_BASE_URL` を設定して、
到達可能な GitHub mirror/proxy 経由に切り替えてください。

例:

```bash
export CDIDX_GITHUB_BASE_URL="https://<your-mirror-host>"
export CDIDX_GITHUB_API_BASE_URL="https://<your-mirror-host>/api"
bash ./install.sh vX.Y.Z
```

なお、「Claude Code cloud では通る」ことだけで
「外向き制限が一切ない」とは断定しないでください。実際には環境ごとに
egress policy や proxy の allow-list が異なるケースが多いです。結論前に、
次を比較してください:

```bash
env | grep -Ei '(^|_)(http|https)_proxy|no_proxy' || true
curl -I https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh
curl -I https://github.com/Widthdom/CodeIndex/releases/download/vX.Y.Z/CodeIndex-linux-x64.tar.gz
```

片方が `200/302`、もう片方が `403`（または `CONNECT tunnel failed`）なら、
まず installer 不具合よりネットワークポリシー差分を疑うべきです。

### 任意: 外向きネットワーク不要のインストール経路 self-test

外向き通信が塞がれていても installer の処理経路を end-to-end で検証したい場合、
`127.0.0.1` 上に**ローカル mirror**を立てて
`CDIDX_GITHUB_BASE_URL` を向ける方法が使えます。これでも checksum 検証・展開・
配置の流れ自体は本番と同じで、取得元ホストだけをローカル化できます。

組み込みの self-test モードは次のとおりです:

```bash
bash ./install.sh --self-test-local-mirror
```

このモードは installer の処理経路（取得URL選択・checksum 検証・展開・配置）
を確認するための mock payload を使います。ネットワークが開いた環境での
公式 release artifact 検証の代替ではありません。

インストーラは最新リリースの tarball をダウンロードし、SHA256 を検証して、
バイナリに加え**隣接ランタイム資産**（`version.json`、Linux は
`libe_sqlite3.so`、macOS は `libe_sqlite3.dylib`）を `$HOME/.local/bin/` に
配置します。3ファイルが揃っている必要があります — ネイティブ SQLite
ライブラリはバイナリのディレクトリから P/Invoke でロードされ、
`version.json` は `cdidx --version` が読むファイルです。

### Step 1.5 — `cdidx` は完全展開した絶対パスで呼び出す

リポジトリ追跡の `.claude/settings.json` は、ローカルセッションが
古いグローバルバイナリに黙ってフォールバックしないよう
`Bash(cdidx:*)`、`Bash(~/.local/bin/cdidx:*)`、
`Bash($HOME/.local/bin/cdidx:*)` を **best-effort tripwire** として
deny しています（詳細は `CLAUDE.md`）。Claude Code の permission
matching はテキスト一致なので、インストール済みバイナリを**完全展開した
絶対パス**で起動すればこの 3 エントリとは一致せず、パーミッション編集
なしに通ります。以下のように 1 回だけパスを解決し、以降の手順で
使い回してください:

```bash
CDIDX="$(readlink -f "$HOME/.local/bin/cdidx" 2>/dev/null || realpath "$HOME/.local/bin/cdidx")"
# 念のため確認: 展開された絶対パス（例: /root/.local/bin/cdidx）が出るはず
echo "$CDIDX"
```

追跡対象の `.claude/settings.json` を編集して tripwire を外す
運用は**取らないでください**。worktree が dirty になり
`status` / `inspect` の信頼指標である `git_is_dirty` が
意味を失い、さらに誤ってコミットすれば全貢献者向けの
tripwire を弱めてしまいます。完全展開絶対パスでの呼び出しが、
編集を伴わない正規の回避手順です。

### Step 2 — インストール済みバイナリをエンドツーエンドでスモーク

以下を全部実行してください。クリーンインストール直後にどれか1つでも
失敗したら、それが最優先の改善対象です — 全新規ユーザーが同じ壁にぶつかって
いる証拠です。

```bash
"$CDIDX" --version                                     # 実バージョンが出ること。v0.0.0 は NG
"$CDIDX" . --db /tmp/smoke.db                          # SQLite 書き込み経路（人間向け出力）
"$CDIDX" status --db /tmp/smoke.db                     # SQLite 読み取り経路
"$CDIDX" search "install" --path install.sh --snippet-lines 4 --db /tmp/smoke.db
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' | "$CDIDX" mcp
```

`cdidx --version` が `v0.0.0` を返すなら、バイナリの隣に `version.json` が
ありません。どれかが `DllNotFoundException: Unable to load shared library
'e_sqlite3'` で失敗した場合、ネイティブ SQLite ライブラリがバイナリの隣に
インストールされていません。どちらもインストール経路のバグです — `install.sh`
と `~/.local/bin/` のレイアウトを確認し、ダウンロード済み tarball を
`tar tzf` で覗いて、リリースが配っている内容とインストーラが配置している
内容の差分を突き合わせてください。

### 既知の注意点: `--json` 出力

公開バイナリは `PublishTrimmed=true` でビルドされているため、リフレクション
ベースの `System.Text.Json` 直列化が無効化されています。CLI で `--json` を
付けたコマンド（例: `cdidx index --json`、`cdidx status --json`）は現状
以下の専用エラーで即時失敗します:

> `Error: --json is not available on this trimmed build.`
>
> `Hint: use `cdidx mcp` for structured output, omit `--json` for`
> `human-readable output, or use the NuGet/global-tool build if you need`
> `CLI JSON.`

MCP 出力は手書き JSON のため影響を受けません。将来のリリースで trimming /
source-gen JSON 問題が解決するまでは、デフォルトの人間向け出力を使い、
構造化レスポンスが必要な場面は MCP サーバー経由にしてください。

### Step 3 — SDK なしの制約下で動く

- このリポジトリのインデックス・検索は全てインストール済み `cdidx` で
  行う。`dotnet build` / `dotnet test` / `dotnet run` は**試さない** —
  ここでは動きません。
- インストール済みバイナリは「公開済み最新リリース」であって、
  「このブランチの未リリース C# 変更」ではありません。リポジトリ探索には
  使えますが、未リリースのソース変更の「検証」には使えません。
- インクリメンタル再インデックス:
  ```bash
  # ローカルで編集した直後（未コミット）は --files を使う
  "$CDIDX" . --files <変更ファイル> [<変更ファイル> ...]
  # もしくはワークスペース全体をリフレッシュ
  "$CDIDX" .
  # コミット**後**のみ: 直近コミットの差分に含まれるファイルを更新
  "$CDIDX" . --commits HEAD
  ```
  `--commits HEAD` は直近コミットに含まれるパスしか対象にしません。
  現在のセッションで未コミットの編集は拾わないので、ローカル編集後は
  `--files` またはフル `"$CDIDX" .` を既定として使い、古いインデックスを
  検索してしまわないようにしてください。
- `SELF_IMPROVEMENT.md` を読み、そのループに従う。ただし、ローカル SDK を
  前提としている再ビルド / 再インデックス手順は上記で読み替える。

### この環境で安全に改善できる領域

以下は .NET SDK なしで完全検証できます:

- `install.sh`、`.github/workflows/*.yml`、リリース tarball の内容と
  レイアウト（`tar tzf` で検査）。
- `README.md`、`CHANGELOG.md`、`CLAUDE.md`、`SELF_IMPROVEMENT.md`、
  `TESTING_GUIDE.md`、`DEVELOPER_GUIDE.md` — それぞれの英語セクションと
  日本語セクションの両方。
- `README.md` の `# Code Search Rules` / `# コードベース検索ルール`
  テンプレート。
- MCP の `instructions`、ツール説明、help / usage 文字列、ユーザーが目に
  するエラーメッセージ。これらの文字列はインストール済みバイナリ + `Grep`
  で特定できます（ローカルでのリビルドは不要）。

### どうしても C# を触る場合

- 変更は小さく非破壊に留める。
- コミットメッセージと PR 説明に、この環境では C# 変更をローカル検証
  できず、CI と .NET SDK を持つレビュアーに委ねると明記する。
- 「ローカルで検証済み」「dotnet test 通過」とは**書かない** — ここでは
  どちらも証明できません。

### その他は `CLAUDE.md` と `SELF_IMPROVEMENT.md` に従う

コミット毎チェックリスト、英日併記ドキュメント、`CHANGELOG.md` の
エントリは必ず `[Unreleased]` 配下のみ、変更ファイルに U+FFFD を混入
させない、明示依頼がなければ PR は開かない。破壊的変更の前には必ず
確認する。
