# Cloud Bootstrap Prompt

> **Maintainers / authorized operators only** — see [MAINTAINERS.md](MAINTAINERS.md). End users running cdidx on their own codebase don't need this file.
> **Maintainer・認可オペレーター向け** — 全体の索引は [MAINTAINERS.md](MAINTAINERS.md#maintainer-と認可オペレーター向け) を参照。自分のコードベースに cdidx を使うだけのエンドユーザーには不要です。

Kickoff prompt for cloud AI coding sessions (for example Claude Code or OpenAI
Codex) that work on this repo without a local .NET SDK. Paste the English
block or the Japanese block — not both — into your session's first message.

Cloud 側の AI コーディングセッション（例: Claude Code / OpenAI Codex、
.NET SDK が無い前提）に最初に投げるプロンプトです。英語ブロックか日本語
ブロックの**どちらか一方**を貼り付けてください。両方は貼らないでください。

---

## English

You are running in a container without the .NET SDK. That's a feature, not a
bug — you are the first user who cannot `dotnet build` their way out of a
broken install path, so any breakage in the published install flow will hit
you first. Use that.

### Step 1 — Install cdidx with the published one-liner (no .NET required)

In Codex, run each fenced command below as a separate Bash command. The guard
allows only the exact single-command bootstrap forms shown here.

```bash
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
```

```bash
export PATH="$HOME/.local/bin:$PATH"
```

If `raw.githubusercontent.com` is blocked in your environment, run the
repo-local installer instead and pin an explicit version (for example, the
value in this repo's `version.json`) to avoid the extra latest-release API
lookup:

```bash
bash ./install.sh vX.Y.Z
```

```bash
export PATH="$HOME/.local/bin:$PATH"
```

If release downloads are blocked from `github.com`, point the installer at a
reachable GitHub mirror/proxy by setting:
`CDIDX_GITHUB_BASE_URL` and `CDIDX_GITHUB_API_BASE_URL` before running
`install.sh`.

Example:

```bash
CDIDX_GITHUB_BASE_URL="https://mirror.example.invalid" CDIDX_GITHUB_API_BASE_URL="https://mirror.example.invalid/api" bash ./install.sh vX.Y.Z
```

Public release and API URLs, including every redirect, must remain HTTPS. The
only HTTP exception is the installer's built-in loopback self-test on
`127.0.0.1` / `localhost`. Network operations use bounded connect, total, and
low-speed timeouts plus bounded retries. Constrained environments may tune
`CDIDX_NETWORK_CONNECT_TIMEOUT_SECONDS`, `CDIDX_NETWORK_MAX_TIME_SECONDS`,
`CDIDX_NETWORK_LOW_SPEED_LIMIT_BYTES`,
`CDIDX_NETWORK_LOW_SPEED_TIME_SECONDS`, `CDIDX_NETWORK_RETRY_COUNT`, and
`CDIDX_NETWORK_RETRY_DELAY_SECONDS`; the installer rejects non-integer or
out-of-range values instead of silently disabling a bound.

Release inputs are also bounded before and after extraction. The installer
caps release archives at 512 MiB and checksum/signature metadata at 1 MiB,
including unknown-length responses while they are still streaming,
rejects archives with more than 4,096 members, more than 1 GiB of declared or
extracted regular-file data, more than 1.125 GiB of expanded tar stream, or a
compression ratio above 250:1, and rejects links and other unsupported member
types. Extraction uses a private mode-0700 staging directory, restrictive
permissions, and ownership/permission-preserving behavior is disabled.

Do not assume "Claude Code cloud has no outbound restrictions" just because it
works there. A more common difference is that each environment has different
egress policy / proxy allow-lists. In Codex sessions with this repository guard,
do not hand-roll `env | grep` or `curl -I` probes; use the allowed installer
diagnostic instead:

Run the relevant diagnostic as a single command. Use the first command when
`version.json` should select the release:

```bash
bash ./install.sh --doctor
```

Use this when you need to probe a specific release:

```bash
bash ./install.sh --doctor v1.11.0
```

Use this when probing through a mirror/proxy endpoint:

```bash
CDIDX_GITHUB_BASE_URL="https://mirror.example.invalid" CDIDX_GITHUB_API_BASE_URL="https://mirror.example.invalid/api" bash ./install.sh --doctor v1.11.0
```

If one environment gets `200/302` and another gets `403` (or `CONNECT tunnel
failed`), treat it as a network-policy difference first, not an installer bug.

If **all** candidate hosts (`raw.githubusercontent.com`, `github.com`, and
your mirror/proxy host) fail at CONNECT tunnel stage with `403`, the deny is
happening in upstream proxy/egress policy before TLS. In that case:

1. Route substitution alone will not unblock the install.
2. Ask the network team to allow-list at least one artifact path.
3. Use `bash ./install.sh --self-test-local-mirror` only to validate installer
   logic offline (it does not prove external release reachability).

`--doctor` prints the active proxy environment, probes the latest-release
API plus the release tarball and `sha256sums.txt` for the requested version
(or the version in `version.json`), and on `CONNECT tunnel failed,
response 403` prints the same upstream-proxy guidance as a single actionable
next step. It installs nothing and only writes inside `/tmp`. Exit code 0 if
every probe is reachable, 1 otherwise, so it is also safe to wire into
automated environment checks.

> **Codex session note (as of commit `559f378`)**
> In this repository's OpenAI Codex cloud container, outbound HTTPS currently
> goes through a mandatory proxy (`http://proxy:8080`), and release-host
> requests to `raw.githubusercontent.com` / `github.com` fail with
> `CONNECT tunnel failed, response 403`. That means a real global install from
> GitHub artifacts is blocked by upstream egress policy in this environment
> until at least one artifact host path is allow-listed (or an actually
> reachable mirror/proxy endpoint is provided).

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

By default, this self-test installs into a temporary directory so it does not
overwrite an existing `~/.local/bin/cdidx`. If `CDIDX_INSTALL_DIR` is already
exported (for example, as part of your normal install routine) and points at a
well-known install path — `~/.local/bin`, `/usr/local/bin`, `/usr/bin`,
`/opt/homebrew/bin`, `/opt/local/bin` — **or** at any directory that already
contains a `cdidx` executable, the self-test aborts with an `ERROR:` and does
not touch that directory. The self-test installs a mock `cdidx` that only
handles `--version`, so the abort protects you from silently breaking the real
binary.

If you genuinely want to observe the mock payload land in a real install
location (rare — usually you want a pre-hosted mirror instead), pass
`--self-test-allow-overwrite`:

```bash
bash ./install.sh --self-test-local-mirror --self-test-allow-overwrite
```

It also requires `python3` and permission to bind a loopback listener on
`127.0.0.1`; some restricted sandboxes forbid local listen sockets entirely,
in which case this self-test must run in a less-restricted shell or against a
pre-hosted mirror instead.
If port `18765` is already taken, move the local mirror to a different port:

```bash
CDIDX_LOCAL_MIRROR_PORT=18766 bash ./install.sh --self-test-local-mirror
```

This mode uses a mock payload only to validate installer flow (download URL
selection, checksum verification, extraction, placement). It is not a
replacement for validating official release artifacts in a network-open
environment.

To validate a real release end-to-end (download + install + indexing + FTS5
search) without touching the user's real install, use `--reinstall-real
<version>`:

```bash
bash ./install.sh --reinstall-real v1.11.0
```

This installs the requested tag into an isolated `/tmp/cdidx-reinstall-real.XXXXXX`
dir, runs `cdidx --version` and verifies the reported version matches the
requested tag, then builds a tiny scratch Python project and runs
`cdidx . --db <scratch>/.cdidx/codeindex.db` followed by
`cdidx search greet --db <...>` against it and confirms the match payload
surfaces the scratch symbol. Human-readable output is used on purpose so this
validation covers the default user path. Current release binaries are trimmed
with source-generated CLI JSON DTOs, so `--json` is expected to work; the
`JsonOutputFailure` path is only a fallback for old or custom binaries that
miss a serializer registration. `--self-test-local-mirror` only stubs
`--version`, so regressions in indexing, native SQLite load, or FTS5 search
paths would slip past it; `--reinstall-real` closes that gap.
`CDIDX_INSTALL_DIR` is intentionally ignored by this mode so a broken build can
never clobber a working real install, and both temp dirs are cleaned up on exit
via `trap`.

The installer downloads the latest release tarball, verifies SHA256, and
copies the binary **plus the adjacent runtime assets** (`version.json` and
`libe_sqlite3.so` on Linux / `libe_sqlite3.dylib` on macOS) into
`$HOME/.local/bin/`. All three files must end up there — the native SQLite
library is loaded via P/Invoke from the binary's directory, and
`version.json` is what `cdidx --version` reads.

### Claude-specific tripwire note

The installer and smoke-test guidance above applies to Claude Code and
Codex-style shells. The next step is specifically about this repo's
tracked `.claude/settings.json` tripwire, so it only matters on harnesses
that enforce those Claude permission rules.

### Codex-specific guard note

This repo's tracked `.codex/hooks.json` loads a Bash guard that blocks generic
network download commands and generic/global `cdidx` invocations. That guard
now has only these intentionally narrow bootstrap exceptions:

- the exact official installer one-liner from
  `raw.githubusercontent.com/Widthdom/CodeIndex/.../install.sh | bash`;
- a direct repo-local `install.sh` invocation such as `bash ./install.sh
  vX.Y.Z`, `bash ./install.sh --doctor vX.Y.Z`, or `bash ./install.sh
  --self-test-local-mirror`.
- the exact absolute-path resolver print command and JSON-RPC `initialize`
  pipe, with the fully expanded installed path, shown in Step 1.5 / Step 2
  below.

Arbitrary `curl | bash`, unrelated download commands, bare `cdidx`,
`~/.local/bin/cdidx`, `$HOME/.local/bin/cdidx`, `$CDIDX`, and other
path-qualified global `cdidx` binaries remain blocked. After installing in
Codex, use the fully expanded absolute path from Step 1.5 below as a literal
command path for every `cdidx` command. The guard intentionally does not trust
`$CDIDX` / `${CDIDX}` because it cannot verify what those variables contain.
If the exact installer command is still blocked, the Codex session is running
an older checkout of this repo's guard; update to a branch that contains this
bootstrap exception before trying to install again. This guard change does not
bypass upstream egress policy: a real `CONNECT tunnel failed, response 403`
still requires an allow-listed artifact host path or a reachable mirror/proxy
endpoint.

### Step 1.5 — Invoke `cdidx` via its fully expanded absolute path

The repo-tracked `.claude/settings.json` denies `Bash(cdidx:*)`,
`Bash(~/.local/bin/cdidx:*)`, and `Bash($HOME/.local/bin/cdidx:*)` as a
**best-effort tripwire** (see `CLAUDE.md`) against local sessions that
would silently fall back to a stale global binary. Claude Code
permission matching is textual, so invoking the installed binary via
its fully expanded absolute path is not matched by any of those three
entries and is therefore allowed without any permission edit. In Codex, print
the path once and paste the printed literal path into every command below:

```bash
CDIDX_PATH="$(readlink -f "$HOME/.local/bin/cdidx" 2>/dev/null || realpath "$HOME/.local/bin/cdidx")"; printf '%s\n' "$CDIDX_PATH"
```

The examples below use `/root/.local/bin/cdidx` as a placeholder. Replace it
with the exact path printed by the resolver command before running them. Do not
use `$CDIDX` / `${CDIDX}` for normal Codex commands; those variable forms are
blocked because the guard cannot validate their runtime value.

Do **not** edit the tracked `.claude/settings.json` to bypass the
tripwire. Doing so dirties the worktree (breaking `git_is_dirty` as a
trust signal for `status`/`inspect`) and risks an accidental commit
that weakens the tripwire for every other contributor. The expanded
absolute path is the intended, non-mutating unblock.

### Step 2 — Smoke test the installed binary end-to-end

Run every command below separately. If any of them fails on a clean install,
that failure is your highest-priority improvement target — every new user is
hitting the same wall.

Version smoke:

```bash
/root/.local/bin/cdidx --version
```

SQLite write path:

```bash
/root/.local/bin/cdidx . --db /tmp/smoke.db
```

SQLite read path:

```bash
/root/.local/bin/cdidx status --db /tmp/smoke.db
```

Indexed search path:

```bash
/root/.local/bin/cdidx search "install" --path install.sh --snippet-lines 4 --db /tmp/smoke.db
```

MCP initialize smoke:

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' | /root/.local/bin/cdidx mcp
```

If `cdidx --version` reports `v0.0.0`, `version.json` is missing next to the
binary. If any command dies with `DllNotFoundException: Unable to load shared
library 'e_sqlite3'`, the native SQLite library was not installed next to the
binary. Both are install-path bugs — inspect `install.sh` and the layout
under `~/.local/bin/`, then peek inside the downloaded tarball with
`tar tzf` to confirm what the release ships vs. what the installer places.

### `--json` output

The published `install.sh` binary is a trimmed self-contained release.
CLI commands invoked with `--json` (for example `cdidx index --json`,
`cdidx status --json`) are expected to emit machine-readable JSON because
their DTOs are covered by source-generated serializers.

If you see `Error: --json is not available on this trimmed build.`, you
are running an old or custom binary that missed a CLI JSON serializer
source-generation path. Reinstall with `install.sh` or use the
NuGet/global-tool build; MCP remains available when you want structured
responses through an MCP client.

### Step 3 — Operate within the no-SDK constraint

- Use the installed `cdidx` binary for all indexing and querying of this
  repo. Do **not** try `dotnet build` / `dotnet test` / `dotnet run` — they
  will not work here.
- The installed binary is whatever the latest published release ships. It
  is **not** this branch's unreleased C# changes. Use it to explore the
  repo, never to "verify" an unreleased source edit.
- Incremental reindex:
  after editing tracked files locally (uncommitted edits), use `--files`:
  ```bash
  /root/.local/bin/cdidx . --files <changed-file> [<changed-file> ...]
  ```
  Or refresh the entire workspace:
  ```bash
  /root/.local/bin/cdidx .
  ```
  Only after a commit, refresh paths from the last commit's diff:
  ```bash
  /root/.local/bin/cdidx . --commits HEAD
  ```
  `--commits HEAD` only covers paths in the last committed diff; it will
  not pick up uncommitted edits made in the current session. Default to
  `--files` (or a full-index command using the literal expanded path) after
  local edits to avoid searching a stale index.
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
  user-visible error messages. Locate those strings with the literal expanded
  installed path, for example `/root/.local/bin/cdidx search "initialize"`,
  or with `/root/.local/bin/cdidx find "initialize" --path src`.

### If you must touch C\#

- Keep the change small and non-destructive.
- State plainly in the commit message and any PR description that this
  environment cannot validate the C# change, and defer to CI and to a
  reviewer with a local .NET SDK.
- Do **not** claim "verified locally" or "dotnet test passes" — you
  cannot prove either here.

### Everything else follows `CLAUDE.md` and `SELF_IMPROVEMENT.md`

Per-commit checklist, bilingual English / Japanese documentation,
ordinary implementation PRs add bilingual fragments under
`changelog.d/unreleased/` instead of editing `CHANGELOG.md`, no U+FFFD
characters in changed files, no PR unless the user explicitly asks. Confirm
before any destructive action.

---

## 日本語

あなたは .NET SDK のないコンテナで動いています。これは欠点ではなく強みです。
`dotnet build` に逃げられない最初のユーザーなので、公開済みのインストール
経路に壊れがあれば最初に踏むのはあなた自身です。それを活かしてください。

### Step 1 — 公開済みワンライナーで cdidx をインストール（.NET 不要）

Codex では、以下の fenced command をそれぞれ別々の Bash コマンドとして
実行してください。guard が許可するのは、ここに示した exact な
single-command bootstrap 形式だけです。

```bash
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
```

```bash
export PATH="$HOME/.local/bin:$PATH"
```

もし環境の制約で `raw.githubusercontent.com` に到達できない場合は、リポジトリ
内の `install.sh` を直接実行し、`latest` API 参照を避けるため明示バージョン
（例: このリポジトリの `version.json` の値）を指定してください:

```bash
bash ./install.sh vX.Y.Z
```

```bash
export PATH="$HOME/.local/bin:$PATH"
```

`github.com` からのリリース取得自体がブロックされる環境では、`install.sh`
実行前に `CDIDX_GITHUB_BASE_URL` と `CDIDX_GITHUB_API_BASE_URL` を設定して、
到達可能な GitHub mirror/proxy 経由に切り替えてください。

例:

```bash
CDIDX_GITHUB_BASE_URL="https://mirror.example.invalid" CDIDX_GITHUB_API_BASE_URL="https://mirror.example.invalid/api" bash ./install.sh vX.Y.Z
```

公開リリース URL と API URL は、すべてのリダイレクトを含めて HTTPS のままに
する必要があります。HTTP の例外は installer 組み込み self-test が使う
`127.0.0.1` / `localhost` の loopback だけです。ネットワーク処理には connect・
total・low-speed timeout と有界 retry が適用されます。制約のある環境では
`CDIDX_NETWORK_CONNECT_TIMEOUT_SECONDS`、`CDIDX_NETWORK_MAX_TIME_SECONDS`、
`CDIDX_NETWORK_LOW_SPEED_LIMIT_BYTES`、
`CDIDX_NETWORK_LOW_SPEED_TIME_SECONDS`、`CDIDX_NETWORK_RETRY_COUNT`、
`CDIDX_NETWORK_RETRY_DELAY_SECONDS` を調整できますが、整数でない値や範囲外の
値は上限を黙って無効化せず installer が拒否します。

release input は展開前後にも上限が適用されます。installer は release archive を
512 MiB、checksum / signature metadata を 1 MiB に制限し、長さが事前に不明な
response も転送中に上限で停止します。4,096 を超える member、
宣言または実展開された regular-file data が 1 GiB を超える archive、展開後 tar
stream が 1.125 GiB を超える archive、圧縮率が 250:1 を超える archive を拒否します。
link とその他の未対応 member type も拒否し、mode 0700 の private staging directory、
制限的な permission、owner / permission を引き継がない extraction を使用します。

なお、「Claude Code cloud では通る」ことだけで
「外向き制限が一切ない」とは断定しないでください。実際には環境ごとに
egress policy や proxy の allow-list が異なるケースが多いです。この
リポジトリ guard が有効な Codex セッションでは、`env | grep` や `curl -I` の
手打ち probe は使わず、許可済みの installer 診断を使ってください:

該当する診断を single command として実行してください。最初のコマンドは
`version.json` のバージョンで probe する場合に使います:

```bash
bash ./install.sh --doctor
```

特定リリースを probe する場合はこちらです:

```bash
bash ./install.sh --doctor v1.11.0
```

mirror/proxy 経由で probe する場合はこちらです:

```bash
CDIDX_GITHUB_BASE_URL="https://mirror.example.invalid" CDIDX_GITHUB_API_BASE_URL="https://mirror.example.invalid/api" bash ./install.sh --doctor v1.11.0
```

片方が `200/302`、もう片方が `403`（または `CONNECT tunnel failed`）なら、
まず installer 不具合よりネットワークポリシー差分を疑うべきです。

`raw.githubusercontent.com` / `github.com` / mirror・proxy 候補の**すべて**が
CONNECT トンネル段階で `403` になる場合、拒否は TLS 前の
upstream proxy / egress policy 側で発生しています。このケースでは:

1. 経路差し替えだけでは解決しません。
2. 少なくとも 1 つの artifact 配信経路を network 管理者に allow-list して
   もらってください。
3. `bash ./install.sh --self-test-local-mirror` は installer ロジックの
   オフライン検証用途に限って使ってください（外部 release 到達性の証明にはなりません）。

`--doctor` は有効な proxy 環境変数を表示したうえで、指定バージョン（または
`version.json` のバージョン）で latest-release API・リリース tarball・
`sha256sums.txt` の 3 URL を probe し、`CONNECT tunnel failed, response 403`
を検出したら、上記と同じ「upstream proxy 側の拒否 / 経路差し替えでは解消
しない / 少なくとも 1 つの artifact 経路を allow-list してもらう /
`CDIDX_GITHUB_BASE_URL` / `CDIDX_GITHUB_API_BASE_URL` を到達可能な内部 mirror
に向ける」ガイダンスを 1 つのアクションとして出します。インストールは行わず、
書き込みは `/tmp` 配下のみ。全 probe が 2xx/3xx なら exit 0、それ以外は
exit 1 なので、自動環境チェックのフックにも安全に組み込めます。

> **Codex セッション注記（commit `559f378` 時点）**
> このリポジトリの OpenAI Codex クラウドコンテナでは、外向き HTTPS が
> 必須プロキシ（`http://proxy:8080`）経由になっており、
> `raw.githubusercontent.com` / `github.com` への release 取得は
> `CONNECT tunnel failed, response 403` で失敗する。したがってこの環境では、
> artifact 配信経路の少なくとも 1 つが allow-list される（または実到達可能な
> mirror/proxy エンドポイントが提供される）まで、GitHub artifact からの
> 実バイナリのグローバルインストールは成立しない。

### 任意: 外向きネットワーク不要のインストール経路 self-test

外向き通信が塞がれていても installer の処理経路を end-to-end で検証したい場合、
`127.0.0.1` 上に**ローカル mirror**を立てて
`CDIDX_GITHUB_BASE_URL` を向ける方法が使えます。これでも checksum 検証・展開・
配置の流れ自体は本番と同じで、取得元ホストだけをローカル化できます。

組み込みの self-test モードは次のとおりです:

```bash
bash ./install.sh --self-test-local-mirror
```

この self-test は既定では一時ディレクトリへインストールするため、既存の
`~/.local/bin/cdidx` を上書きしません。もし通常インストール運用で
`CDIDX_INSTALL_DIR` を既に export している場合でも、その値が
`~/.local/bin`、`/usr/local/bin`、`/usr/bin`、`/opt/homebrew/bin`、
`/opt/local/bin` のような既知の install 先、または既に `cdidx` 実行ファイル
を持つディレクトリを指しているときは、`ERROR:` を出して中断し、そのディレク
トリには一切触りません。self-test が置く `cdidx` は `--version` しか返さない
mock なので、この中断は実バイナリが黙って壊されることを防ぐためのガードです。

どうしても mock の配置結果を実際の install 先で観察したい場合（稀です。
通常は事前ホスト済み mirror のほうが適切です）は、
`--self-test-allow-overwrite` を併用してください:

```bash
bash ./install.sh --self-test-local-mirror --self-test-allow-overwrite
```

また、この self-test には `python3` と `127.0.0.1` への loopback listen
権限が必要です。restricted sandbox によってはローカル listen socket 自体が
禁止されるため、その場合はより制約の弱い shell か、事前に用意した mirror に
対して実行してください。
既定ポート `18765` が使用中なら、local mirror を別ポートへ逃がせます:

```bash
CDIDX_LOCAL_MIRROR_PORT=18766 bash ./install.sh --self-test-local-mirror
```

このモードは installer の処理経路（取得URL選択・checksum 検証・展開・配置）
を確認するための mock payload を使います。ネットワークが開いた環境での
公式 release artifact 検証の代替ではありません。

ユーザーの実インストールを触らずに実リリースを end-to-end 検証
（ダウンロード＋インストール＋インデックス＋FTS5 検索）したい場合は
`--reinstall-real <version>` を使ってください:

```bash
bash ./install.sh --reinstall-real v1.11.0
```

このモードは指定タグを隔離された `/tmp/cdidx-reinstall-real.XXXXXX` に
インストールし、`cdidx --version` を走らせて報告されたバージョンが
要求タグと一致することを検証したうえで、極小の Python プロジェクトを
生成して `cdidx . --db <scratch>/.cdidx/codeindex.db` と
`cdidx search greet --db <...>` を通し、出力中にスクラッチシンボルが
現れることを確認します。出力は人間向けフォーマットを意図的に使い、
既定のユーザー経路を検証します。現在の公式 release binary は trimmed
ですが、source-generated CLI JSON DTO により `--json` は動作する前提です。
`JsonOutputFailure` は serializer 登録が欠けた古い/カスタムバイナリ向けの
fallback です。`--self-test-local-mirror` のモックは `--version` しか
スタブしないため、インデックス・ネイティブ SQLite ロード・FTS5 検索の
回帰は素通りしますが、`--reinstall-real` でその穴を埋められます。
このモードは `CDIDX_INSTALL_DIR` を意図的に無視するので、壊れたビルドが
実インストールを上書きすることはありません。temp ディレクトリは `trap` で必ず片付けます。

インストーラは最新リリースの tarball をダウンロードし、SHA256 を検証して、
バイナリに加え**隣接ランタイム資産**（`version.json`、Linux は
`libe_sqlite3.so`、macOS は `libe_sqlite3.dylib`）を `$HOME/.local/bin/` に
配置します。3ファイルが揃っている必要があります — ネイティブ SQLite
ライブラリはバイナリのディレクトリから P/Invoke でロードされ、
`version.json` は `cdidx --version` が読むファイルです。

### Claude 専用 tripwire メモ

ここまでの installer / smoke test 手順は Claude Code と Codex 系 shell の
どちらにも当てはまります。次の step は、このリポジトリが追跡している
`.claude/settings.json` tripwire に関する話なので、その Claude 系 permission
ルールが効くハーネスでだけ意味があります。

### Codex 専用 guard メモ

このリポジトリが追跡している `.codex/hooks.json` は、汎用ネットワーク
ダウンロードコマンドと汎用・グローバル `cdidx` 呼び出しを止める Bash guard
を読み込みます。この guard には、次のものだけを通す意図的に狭い
bootstrap 例外を入れています:

- `raw.githubusercontent.com/Widthdom/CodeIndex/.../install.sh | bash`
  という公式 installer ワンライナーそのもの。
- `bash ./install.sh vX.Y.Z`、`bash ./install.sh --doctor vX.Y.Z`、
  `bash ./install.sh --self-test-local-mirror` のような、リポジトリ直下
  `install.sh` の直接実行。
- 次の Step 1.5 / Step 2 に載せている、絶対パス表示コマンドと、
  完全展開済みインストールパスを直接使う JSON-RPC `initialize` pipe の
  正確な形。

任意の `curl | bash`、無関係な download コマンド、裸の `cdidx`、
`~/.local/bin/cdidx`、`$HOME/.local/bin/cdidx`、`$CDIDX`、その他の
パス付きグローバル `cdidx` は引き続きブロックされます。Codex で
インストールした後は、次の Step 1.5 で得る完全展開済み絶対パスを
コマンドパスとしてそのまま貼り付け、毎回 `cdidx` を呼んでください。
guard は `$CDIDX` / `${CDIDX}` を意図的に信用しません。変数の実際の中身を
検証できないためです。正確な installer コマンドまでブロックされる場合、
その Codex セッションはこの bootstrap 例外を含まない古い checkout の guard
を実行しています。インストールを再試行する前に、この例外を含むブランチへ
更新してください。この guard 変更は upstream egress policy を迂回するもの
ではありません: 本物の `CONNECT tunnel failed, response 403` は、少なくとも
1 つの artifact 配信経路の allow-list、または実際に到達可能な
mirror/proxy endpoint が必要です。

### Step 1.5 — `cdidx` は完全展開した絶対パスで呼び出す

リポジトリ追跡の `.claude/settings.json` は、ローカルセッションが
古いグローバルバイナリに黙ってフォールバックしないよう
`Bash(cdidx:*)`、`Bash(~/.local/bin/cdidx:*)`、
`Bash($HOME/.local/bin/cdidx:*)` を **best-effort tripwire** として
deny しています（詳細は `CLAUDE.md`）。Claude Code の permission
matching はテキスト一致なので、インストール済みバイナリを**完全展開した
絶対パス**で起動すればこの 3 エントリとは一致せず、パーミッション編集
なしに通ります。Codex では以下のように 1 回だけパスを表示し、出力された
literal なパスを以降の各コマンドへ貼り付けてください:

```bash
CDIDX_PATH="$(readlink -f "$HOME/.local/bin/cdidx" 2>/dev/null || realpath "$HOME/.local/bin/cdidx")"; printf '%s\n' "$CDIDX_PATH"
```

以下の例では `/root/.local/bin/cdidx` を placeholder として使います。実行前に、
resolver コマンドが表示した正確なパスへ置き換えてください。通常の Codex
コマンドでは `$CDIDX` / `${CDIDX}` を使わないでください。guard が実行時の
変数値を検証できないため、これらの変数形式はブロックされます。

追跡対象の `.claude/settings.json` を編集して tripwire を外す
運用は**取らないでください**。worktree が dirty になり
`status` / `inspect` の信頼指標である `git_is_dirty` が
意味を失い、さらに誤ってコミットすれば全貢献者向けの
tripwire を弱めてしまいます。完全展開絶対パスでの呼び出しが、
編集を伴わない正規の回避手順です。

### Step 2 — インストール済みバイナリをエンドツーエンドでスモーク

以下をそれぞれ別々に実行してください。クリーンインストール直後にどれか1つでも
失敗したら、それが最優先の改善対象です — 全新規ユーザーが同じ壁にぶつかって
いる証拠です。

バージョンスモーク:

```bash
/root/.local/bin/cdidx --version
```

SQLite 書き込み経路:

```bash
/root/.local/bin/cdidx . --db /tmp/smoke.db
```

SQLite 読み取り経路:

```bash
/root/.local/bin/cdidx status --db /tmp/smoke.db
```

インデックス検索経路:

```bash
/root/.local/bin/cdidx search "install" --path install.sh --snippet-lines 4 --db /tmp/smoke.db
```

MCP initialize smoke:

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' | /root/.local/bin/cdidx mcp
```

`cdidx --version` が `v0.0.0` を返すなら、バイナリの隣に `version.json` が
ありません。どれかが `DllNotFoundException: Unable to load shared library
'e_sqlite3'` で失敗した場合、ネイティブ SQLite ライブラリがバイナリの隣に
インストールされていません。どちらもインストール経路のバグです — `install.sh`
と `~/.local/bin/` のレイアウトを確認し、ダウンロード済み tarball を
`tar tzf` で覗いて、リリースが配っている内容とインストーラが配置している
内容の差分を突き合わせてください。

### `--json` 出力

公開 `install.sh` バイナリは trim 済み自己完結 release ですが、CLI で
`--json` を付けたコマンド（例: `cdidx index --json`、`cdidx status --json`）
は source-generated serializer でカバーされているため、機械可読 JSON を
出力する想定です。

`Error: --json is not available on this trimmed build.` が出る場合は、
現在の公開 release ではなく、CLI JSON serializer の source-generation 経路を
欠いた古いバイナリまたは custom binary を実行しています。`install.sh` で
入れ直すか NuGet グローバルツール版を使ってください。MCP クライアント経由の
構造化レスポンスが必要な場合は、引き続き MCP も利用できます。

### Step 3 — SDK なしの制約下で動く

- このリポジトリのインデックス・検索は全てインストール済み `cdidx` で
  行う。`dotnet build` / `dotnet test` / `dotnet run` は**試さない** —
  ここでは動きません。
- インストール済みバイナリは「公開済み最新リリース」であって、
  「このブランチの未リリース C# 変更」ではありません。リポジトリ探索には
  使えますが、未リリースのソース変更の「検証」には使えません。
- インクリメンタル再インデックス:
  ローカルで編集した直後（未コミット）は `--files` を使います:
  ```bash
  /root/.local/bin/cdidx . --files <変更ファイル> [<変更ファイル> ...]
  ```
  もしくはワークスペース全体をリフレッシュします:
  ```bash
  /root/.local/bin/cdidx .
  ```
  コミット後のみ、直近コミットの差分に含まれるファイルを更新します:
  ```bash
  /root/.local/bin/cdidx . --commits HEAD
  ```
  `--commits HEAD` は直近コミットに含まれるパスしか対象にしません。
  現在のセッションで未コミットの編集は拾わないので、ローカル編集後は
  `--files` または literal な完全展開済みパスを使ったフルインデックスを
  既定として使い、古いインデックスを検索してしまわないようにしてください。
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
  するエラーメッセージ。これらの文字列は literal な完全展開済み path で、
  例えば `/root/.local/bin/cdidx search "initialize"`、または
  `/root/.local/bin/cdidx find "initialize" --path src` で特定できます。

### どうしても C# を触る場合

- 変更は小さく非破壊に留める。
- コミットメッセージと PR 説明に、この環境では C# 変更をローカル検証
  できず、CI と .NET SDK を持つレビュアーに委ねると明記する。
- 「ローカルで検証済み」「dotnet test 通過」とは**書かない** — ここでは
  どちらも証明できません。

### その他は `CLAUDE.md` と `SELF_IMPROVEMENT.md` に従う

コミット毎チェックリスト、英日併記ドキュメント、通常の実装 PR では
`CHANGELOG.md` を直接編集せず `changelog.d/unreleased/` に英日併記
fragment を追加すること、変更ファイルに U+FFFD を混入させないこと、
明示依頼がなければ PR は開かないこと。破壊的変更の前には必ず確認する。
