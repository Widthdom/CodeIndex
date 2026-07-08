# Platform And Terminal Boundaries

This note records the user-visible terminal, color, and notification contracts
for `cdidx`. It is intended for audits that need to reason about Windows,
macOS, Linux, CI, headless shells, and MCP/server-style hosts.

## English

### Color Output

`--color=always` and `--color=never` are explicit operator choices. In `auto`
mode, `CLICOLOR_FORCE` enables ANSI color, non-empty `NO_COLOR` disables it,
`CLICOLOR=0` disables it, and terminal detection decides the fallback.

JSON output scopes suppress ANSI decoration for structured payloads even when
color is forced. CI and `TERM=dumb` disable automatic interactive terminal
behavior, so scripts should prefer `--json`, `--quiet`, or `--color=never`
when stdout/stderr are collected as logs.

### Completion Notifications

`--notify` and `CDIDX_NOTIFY` accept `auto`, `bell`, `osc9`, `desktop`, and
`none`. `auto` emits a completion signal only when an interactive console is
detected. `none`, `--quiet`, and `--json` suppress completion notifications.

`bell` emits a single BEL byte. `osc9` emits one OSC 9 notification sequence
with CR/LF flattened before writing to stderr. `desktop` is a compatibility
alias for `osc9`; `cdidx` does not launch a native desktop notification helper.
Explicit `bell`, `osc9`, and `desktop` are bounded to one terminal control
sequence, so CI, MCP, and server hosts that capture stderr should set
`--notify none`, `CDIDX_NOTIFY=none`, `--quiet`, or `--json`.

### Platform Detection

Windows ANSI output additionally depends on virtual-terminal processing or a
known terminal environment hint. macOS and Linux use POSIX terminal hints such
as `TERM` and `TERM_PROGRAM`, while CI and `TERM=dumb` disable automatic
interactive behavior. Unsupported platform-specific behavior should be
documented as an explicit no-op or as a bounded diagnostic rather than relying
on silent terminal escape output.

## 日本語

### カラー出力

`--color=always` と `--color=never` は明示的な操作指定です。`auto` では
`CLICOLOR_FORCE` が ANSI color を有効化し、空でない `NO_COLOR` が無効化し、
`CLICOLOR=0` も無効化します。それ以外は端末検出にフォールバックします。

JSON 出力スコープでは、color が強制されていても構造化 payload に ANSI 装飾を
混ぜません。CI と `TERM=dumb` は自動的な interactive terminal 挙動を無効化するため、
stdout/stderr をログとして回収するスクリプトでは `--json`、`--quiet`、または
`--color=never` を優先してください。

### 完了通知

`--notify` と `CDIDX_NOTIFY` は `auto`、`bell`、`osc9`、`desktop`、`none` を
受け付けます。`auto` は interactive console が検出された場合だけ完了シグナルを
出します。`none`、`--quiet`、`--json` は完了通知を抑制します。

`bell` は BEL byte を 1 つだけ出します。`osc9` は CR/LF を空白へ平坦化したうえで、
OSC 9 通知 sequence を 1 つだけ stderr に書きます。`desktop` は `osc9` の互換
alias であり、`cdidx` は native desktop notification helper を起動しません。
明示的な `bell`、`osc9`、`desktop` は 1 つの terminal control sequence に制限されるため、
stderr を捕捉する CI、MCP、server host では `--notify none`、`CDIDX_NOTIFY=none`、
`--quiet`、または `--json` を指定してください。

### プラットフォーム検出

Windows の ANSI 出力は virtual-terminal processing または既知の terminal environment
hint にも依存します。macOS と Linux は `TERM` や `TERM_PROGRAM` のような POSIX 端末
hint を使い、CI と `TERM=dumb` は自動 interactive behavior を無効化します。
未対応の platform-specific 挙動は、silent な terminal escape 出力に頼らず、
明示的な no-op または上限付き diagnostic として文書化してください。
