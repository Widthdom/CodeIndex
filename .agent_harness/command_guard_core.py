"""Shared command guard policy for Claude Code and Codex.

This module keeps the command classification and denial policy in one place
so the Claude and Codex hook adapters stay thin.
"""

from __future__ import annotations

from dataclasses import dataclass
import os
import re
import shlex
import shutil
import subprocess
from pathlib import Path


MAX_SCRIPT_SCAN_BYTES = 512 * 1024
LOCAL_CDIDX_REL = Path("src/CodeIndex/bin/Debug/net8.0/cdidx.dll")
REPO_INSTALLER_REL = Path("install.sh")
ALLOW_REASON_OFFICIAL_INSTALLER = "official cdidx installer bootstrap"
ALLOW_REASON_REPO_LOCAL_INSTALLER = "repo-local cdidx installer bootstrap"
ALLOW_REASON_EXPANDED_INSTALLED_CDIDX = "expanded installed cdidx bootstrap"
ALLOWED_INSTALLER_ENV_NAMES = {
    "CDIDX_GITHUB_BASE_URL",
    "CDIDX_GITHUB_API_BASE_URL",
    "CDIDX_LOCAL_MIRROR_PORT",
    "CDIDX_INSTALL_DIR",
}

_SHELL_CONTROL_TOKENS = {"|", "||", "&", "&&", ";", "|&", "(", ")", "<", ">", "<<", ">>"}
_INLINE_INTERPRETER_FLAGS = {"-c", "-e", "--eval"}
_INLINE_INTERPRETERS = {"python", "python3", "ruby", "perl", "node", "deno", "php"}
_INLINE_SHELLS = {"bash", "sh", "zsh", "fish"}
_INLINE_SHELL_VARIABLES = {"$SHELL", "${SHELL}"}
_UNKNOWN_GLOBAL_OPTION_SUBCOMMAND = "__unknown_global_option__"
_HIGH_RISK_UNKNOWN_GLOBAL_OPTION_REASON = "unrecognized global option before high-risk CLI subcommand is blocked"
_TRANSPARENT_SCRIPT_WRAPPERS = {"time", "timeout", "gtimeout", "command", "exec", "nice", "nohup"}
_SEARCH_OR_DISCOVERY_COMMANDS = {
    "grep",
    "egrep",
    "fgrep",
    "zgrep",
    "rgrep",
    "rg",
    "ripgrep",
    "ag",
    "ack",
    "ack-grep",
    "find",
    "fd",
    "fdfind",
    "locate",
    "mlocate",
    "mdfind",
}
_NETWORK_COMMANDS = {"curl", "wget", "http", "https", "xh", "aria2c"}
_DESTRUCTIVE_COMMANDS = {"rmdir", "unlink", "shred", "srm", "truncate"}
_PRIVILEGE_COMMANDS = {"sudo", "su", "doas"}
_PROCESS_KILL_COMMANDS = {"killall", "pkill"}
_REMOTE_TRANSFER_COMMANDS = {"ssh", "scp", "sftp", "rsync", "rclone", "nc", "ncat", "netcat", "socat", "telnet", "ftp"}
_CLIPBOARD_COMMANDS = {"pbcopy", "pbpaste"}
_MACOS_AUTOMATION_COMMANDS = {"open", "osascript", "automator"}
_MACOS_SYSTEM_COMMANDS = {"launchctl", "security", "tccutil", "spctl", "csrutil", "tmutil"}
_CLOUD_COMMANDS = {"aws", "gcloud", "az"}
_SECRET_FILE_READ_COMMANDS = {"cat", "less", "more", "head", "tail", "sed", "awk", "python", "python3", "node", "ruby", "perl", "sqlite3"}
_CWD_CHANGING_COMMANDS = {"cd", "pushd", "popd"}
_SHELL_COMMAND_PREFIX_KEYWORDS = {"if", "then", "elif", "else", "while", "until", "do"}
_SECRET_PATH_RE = re.compile(r"(?i)(?:\.env\b|\.env\.|\.pem\b|\.key\b|id_rsa|id_ed25519|credentials?|secrets?)")
ANSI_C_QUOTE_RE = re.compile(r"\$'")

SEARCH_OR_DISCOVERY_RE = re.compile(
    r"""(?ix)
    (^|[^\w./-])
    (
      grep|egrep|fgrep|zgrep|rgrep|
      rg|ripgrep|ag|ack|ack-grep|
      find|fd|fdfind|locate|mlocate|mdfind
    )
    (?=$|[^\w./-])
    """
)

GLOBAL_CDIDX_RE = re.compile(
    r"""(?ix)
    (^|[^\w./-])
    (
      cdidx
      |~/?\.local/bin/cdidx
      |\$HOME/\.local/bin/cdidx
      |\$\{HOME\}/\.local/bin/cdidx
      |\$CDIDX
      |\$\{CDIDX\}
      |(?:\.{1,2}/|/|[A-Za-z0-9_.-]+/)[^\s'"]*cdidx
    )
    (?=$|[^\w./-])
    """
)

OFFICIAL_INSTALLER_ONE_LINER_RE = re.compile(
    r"""(?ix)
    ^\s*
    curl\s+-fsSL\s+
    https://raw\.githubusercontent\.com/Widthdom/CodeIndex/
        (?:main|v[0-9][A-Za-z0-9._+-]*)/install\.sh
    \s*\|\s*
    bash
    (?:\s+-s\s+--\s+v?[0-9][A-Za-z0-9._+-]*)?
    \s*$
    """
)

CDIDX_RESOLVER_PRINT_COMMAND = (
    "CDIDX_PATH=\"$(readlink -f \"$HOME/.local/bin/cdidx\" 2>/dev/null || "
    "realpath \"$HOME/.local/bin/cdidx\")\"; printf '%s\\n' \"$CDIDX_PATH\""
)

CDIDX_MCP_INIT_SMOKE_RE = re.compile(
    r"""(?x)
    ^\s*
    echo\s+'\{"jsonrpc":"2\.0","id":1,"method":"initialize","params":\{\}\}'\s*
    \|\s*
    (?P<cdidx>/[^\s'"]+)
    \s+mcp
    \s*$
    """
)

GIT_GREP_RE = re.compile(r"(?i)(^|[^\w./-])git\s+grep\b")
EVAL_RE = re.compile(r"(?i)(^|[^\w./-])eval(?=$|[^\w./-])")
LOCAL_CDIDX_DLL_RE = re.compile(r"(?i)cdidx\.dll")
INLINE_SECRET_RE = re.compile(r"(?i)(api[_-]?key|secret|token|password)\s*[:=]\s*['\"]?[A-Za-z0-9_./+=:-]{20,}")
COMMAND_SUBSTITUTION_RE = re.compile(r"`|\$\(")
CONTROL_OP_RE = re.compile(r"(?s)(?:&&|\|\||;|\|&|\||&|`|\$\(|<|>|\n)")
INLINE_INTERPRETER_RE = re.compile(r"(?i)^\s*(?:python|python3|ruby|perl|node)\b.*(?:\s(?:-c|-e|--eval)\b)")
VARIABLE_COMMAND_RE = re.compile(r"^\$(?:[A-Za-z_][A-Za-z0-9_]*|\{[A-Za-z_][A-Za-z0-9_]*\})$")

_FORBIDDEN_COMMAND_PATTERNS: list[tuple[re.Pattern[str], str]] = [
    (re.compile(r"(?i)\brm\s+-[^\n;|&]*r[^\n;|&]*f\b|\brm\s+-[^\n;|&]*f[^\n;|&]*r\b"), "recursive forced rm is blocked"),
    (re.compile(r"(?i)\brm\s+-r\b"), "recursive rm is blocked"),
    (re.compile(r"(?i)\b(?:rmdir|unlink|shred|srm|truncate)\b"), "destructive filesystem command is blocked"),
    (re.compile(r"(?i)\bdd\s+(?:if|of)="), "dd raw device/file copy is blocked"),
    (re.compile(r"(?i)\b(?:mkfs|newfs)\b"), "filesystem formatting is blocked"),
    (re.compile(r"(?i)\bdiskutil\s+(?:erase|partition|apfs\s+delete)\b"), "disk erase/partition operation is blocked"),
    (re.compile(r"(?i)\bchmod\s+(?:777|-R)\b|\bchown\s+-R\b|\bchgrp\s+-R\b"), "dangerous recursive permission/owner change is blocked"),
    (re.compile(r"(?i)\b(?:sudo|su|doas)\b"), "privilege escalation is blocked"),
    (re.compile(r"(?i)\b(?:killall|pkill)\b|\bkill\s+-9\b"), "broad process killing is blocked"),
    (re.compile(r"(?i)\b(?:curl|wget|http|https|xh|aria2c)\b"), "network download/exfil command is blocked"),
    (re.compile(r"(?i)\b(?:curl|wget)\b.*\|\s*(?:sh|bash|zsh|python|ruby|perl)\b"), "download-and-execute is blocked"),
    (re.compile(r"(?i)\b(?:ssh|scp|sftp|rsync|rclone|nc|ncat|netcat|socat|telnet|ftp)\b"), "remote shell/file transfer is blocked"),
    (re.compile(r"(?i)\b(?:pbcopy|pbpaste)\b"), "clipboard access is blocked"),
    (re.compile(r"(?i)\b(?:launchctl|security|tccutil|spctl|csrutil|tmutil)\b"), "macOS security/system command is blocked"),
    (re.compile(r"(?i)\bdefaults\s+write\b|\bplutil\s+-replace\b"), "macOS preference modification is blocked"),
    (re.compile(r"(?i)\bgit\s+tag\b"), "git tag is blocked unless explicitly performed by the user"),
    (re.compile(r"(?i)\bgit\s+reset\s+--hard\b"), "git reset --hard is blocked"),
    (re.compile(r"(?i)\bgit\s+(?:checkout|restore)\s+\.\b"), "checkout/restore of entire worktree is blocked"),
    (re.compile(r"(?i)\bgit\s+clean\s+-[^\n;|&]*f\b"), "git clean -f is blocked"),
    (re.compile(r"(?i)\bgit\s+add\s+(?:\.|-A|--all)\b"), "bulk git add is blocked; add explicit safe files only"),
    (re.compile(r"(?i)\bgit\s+commit\s+--amend\b|\bgit\s+rebase\b|\bgit\s+filter-branch\b|\bgit\s+update-ref\b"), "history rewriting is blocked"),
    (re.compile(r"(?i)\b(?:npm|yarn|pnpm)\s+publish\b|\bdotnet\s+nuget\s+push\b|\bnuget\s+push\b"), "package publishing is blocked"),
    (re.compile(r"(?i)\b(?:npm\s+login|npm\s+adduser)\b"), "package registry login is blocked"),
    (re.compile(r"(?i)\b(?:npx|npm\s+exec|yarn\s+dlx|pnpm\s+dlx)\b"), "ephemeral package execution is blocked"),
    (re.compile(r"(?i)\b(?:terraform\s+(?:apply|destroy)|kubectl\s+(?:apply|delete)|helm\s+(?:install|upgrade|uninstall))\b"), "infra mutation is blocked"),
    (re.compile(r"(?i)\bdocker\s+(?:push|login|system\s+prune|volume\s+rm|rm|rmi)\b|\bdocker\s+buildx\s+build\b.*--push\b"), "dangerous docker operation is blocked"),
    (re.compile(r"(?i)\b(?:aws|gcloud|az)\b"), "cloud CLI is blocked"),
    (re.compile(r"(?i)\bgh\s+(?:auth|api|secret|release|repo\s+create|repo\s+fork|pr\s+merge)\b"), "GitHub CLI high-risk operation is blocked"),
    (re.compile(r"(?i)\b(?:cat|less|more|head|tail|sed|awk|python|python3|node|ruby|perl|sqlite3)\b.*(?:\.env\b|\.env\.|\.pem\b|\.key\b|id_rsa|id_ed25519|credentials?|secrets?)"), "reading secret-looking files is blocked"),
    (INLINE_SECRET_RE, "inline secret-looking value in command is blocked"),
]

_SCRIPT_FORBIDDEN_PATTERNS: list[tuple[re.Pattern[str], str]] = [
    (SEARCH_OR_DISCOVERY_RE, "script contains shell search/file-discovery command"),
    (GIT_GREP_RE, "script contains git grep"),
    *_FORBIDDEN_COMMAND_PATTERNS,
]

_SECRET_PATTERNS: list[tuple[re.Pattern[str], str]] = [
    (re.compile(r"AKIA[0-9A-Z]{16}"), "AWS access key"),
    (re.compile(r"ASIA[0-9A-Z]{16}"), "AWS temporary access key"),
    (re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----"), "private key"),
    (re.compile(r"ghp_[A-Za-z0-9_]{30,}"), "GitHub classic token"),
    (re.compile(r"github_pat_[A-Za-z0-9_]{60,}"), "GitHub fine-grained token"),
    (re.compile(r"sk-[A-Za-z0-9_-]{20,}"), "OpenAI-style API key"),
    (re.compile(r"xox[baprs]-[A-Za-z0-9-]{20,}"), "Slack token"),
    (re.compile(r"AIza[0-9A-Za-z_-]{35}"), "Google API key"),
    (INLINE_SECRET_RE, "generic secret assignment"),
]


@dataclass(frozen=True)
class GuardDecision:
    allowed: bool
    reason: str


def _allow(reason: str) -> GuardDecision:
    return GuardDecision(True, reason)


def _deny(reason: str) -> GuardDecision:
    return GuardDecision(False, reason)


def _split_command(command: str) -> list[str]:
    try:
        lexer = shlex.shlex(command, posix=True, punctuation_chars=True)
        lexer.whitespace_split = True
        lexer.commenters = ""
        return list(lexer)
    except ValueError:
        return []


def _token_segments(tokens: list[str]) -> list[list[str]]:
    segments: list[list[str]] = []
    current: list[str] = []
    for token in tokens:
        if token in _SHELL_CONTROL_TOKENS:
            if current:
                segments.append(current)
                current = []
            continue
        current.append(token)
    if current:
        segments.append(current)
    return segments


def _token_path(token: str, cwd: Path) -> Path | None:
    if not token or token.startswith("-"):
        return None
    if token in {"-c", "-e", "--eval", "-", "source", "."}:
        return None
    expanded = Path(os.path.expandvars(os.path.expanduser(token)))
    if not expanded.is_absolute():
        expanded = cwd / expanded
    try:
        return expanded.resolve()
    except Exception:
        return None


def _command_mentions_local_cdidx(command: str, project_root: Path) -> bool:
    tokens = _split_command(command)
    if len(tokens) < 2:
        return False
    if tokens[0] != "dotnet":
        return False
    dll = _token_path(tokens[1], project_root)
    expected = (project_root / LOCAL_CDIDX_REL).resolve()
    return dll == expected


def _command_is_safe_local_cdidx(command: str, cwd: Path, project_root: Path) -> bool:
    if CONTROL_OP_RE.search(command):
        return False
    if COMMAND_SUBSTITUTION_RE.search(command):
        return False
    tokens = _split_command(command)
    if len(tokens) < 2:
        return False
    if tokens[0] != "dotnet":
        return False
    dll = _token_path(tokens[1], cwd)
    expected = (project_root / LOCAL_CDIDX_REL).resolve()
    return dll == expected


def _token_is_expanded_installed_cdidx(token: str, cwd: Path) -> bool:
    if not token.startswith("/"):
        return False
    try:
        path = Path(token)
        expected = Path.home() / ".local" / "bin" / "cdidx"
    except Exception:
        return False
    return path == expected


def _path_is_expanded_installed_cdidx(path: Path) -> bool:
    try:
        return path.resolve() == (Path.home() / ".local" / "bin" / "cdidx").resolve()
    except Exception:
        return False


def _command_mentions_expanded_installed_cdidx(command: str, cwd: Path) -> bool:
    tokens = _split_command(command)
    return any(_token_is_expanded_installed_cdidx(token, cwd) for token in tokens)


def _token_is_forbidden_cdidx_executable(token: str, cwd: Path) -> bool:
    if not token or token.startswith("-") or _is_env_assignment(token):
        return False
    if token in {"cdidx", "$CDIDX", "${CDIDX}"}:
        return True
    try:
        path = Path(os.path.expandvars(os.path.expanduser(token)))
    except Exception:
        return False
    return path.name == "cdidx" and not _token_is_expanded_installed_cdidx(token, cwd)


def _command_mentions_forbidden_cdidx_executable(command: str, cwd: Path) -> bool:
    tokens = _split_command(command)
    for segment in _token_segments(tokens):
        segment = _strip_transparent_script_wrappers(_strip_leading_env_assignments(segment))
        if segment and _token_is_forbidden_cdidx_executable(segment[0], cwd):
            return True
    return False


def _command_is_safe_expanded_installed_cdidx(command: str, cwd: Path) -> bool:
    if CONTROL_OP_RE.search(command):
        return False
    if COMMAND_SUBSTITUTION_RE.search(command):
        return False
    tokens = _split_command(command)
    if not tokens:
        return False
    return _token_is_expanded_installed_cdidx(tokens[0], cwd)


def _is_inline_interpreter_flag(token: str) -> bool:
    if token in _INLINE_INTERPRETER_FLAGS or token.startswith("--eval="):
        return True
    return token.startswith("-") and not token.startswith("--") and any(flag in token[1:] for flag in {"c", "e"})


def _token_command_name(token: str) -> str:
    return Path(token).name


def _is_variable_command(token: str) -> bool:
    return bool(VARIABLE_COMMAND_RE.match(token))


def _short_flag_contains(token: str, flag: str) -> bool:
    return token.startswith("-") and not token.startswith("--") and flag.lower() in token[1:].lower()


def _has_short_flag(args: list[str], flag: str) -> bool:
    return any(_short_flag_contains(arg, flag) for arg in args)


def _has_recursive_flag(args: list[str]) -> bool:
    return _has_short_flag(args, "r") or any(arg in {"--recursive", "--no-preserve-root"} for arg in args)


def _first_arg(args: list[str]) -> str:
    for arg in args:
        if not arg.startswith("-"):
            return arg
    return ""


def _subcommand_args(
    args: list[str],
    options_with_values: set[str],
    *,
    valueless_options: set[str] | None = None,
    fail_on_unknown_option: bool = False,
) -> tuple[str, list[str]]:
    valueless_options = valueless_options or set()
    index = 0
    while index < len(args):
        arg = args[index]
        if _is_env_assignment(arg):
            index += 1
            continue
        if arg == "--":
            index += 1
            break
        if arg in options_with_values:
            index += 2
            continue
        if any(arg.startswith(option + "=") for option in options_with_values):
            index += 1
            continue
        if arg in valueless_options:
            index += 1
            continue
        if arg.startswith("-"):
            if fail_on_unknown_option:
                return _UNKNOWN_GLOBAL_OPTION_SUBCOMMAND, args[index + 1 :]
            index += 1
            continue
        return arg, args[index + 1 :]
    if index < len(args):
        return args[index], args[index + 1 :]
    return "", []


def _tokenized_git_reason(args: list[str]) -> str | None:
    subcommand = _git_subcommand(args)
    if subcommand == "grep":
        return "git grep is blocked; use dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll instead."
    if subcommand == "tag":
        return "git tag is blocked unless explicitly performed by the user"
    if subcommand == "reset" and "--hard" in args:
        return "git reset --hard is blocked"
    if subcommand in {"checkout", "restore"} and "." in args:
        return "checkout/restore of entire worktree is blocked"
    if subcommand == "clean" and _has_short_flag(args, "f"):
        return "git clean -f is blocked"
    if subcommand == "add" and any(arg in {".", "-A", "--all"} for arg in args):
        return "bulk git add is blocked; add explicit safe files only"
    if subcommand == "commit" and "--amend" in args:
        return "history rewriting is blocked"
    if subcommand in {"rebase", "filter-branch", "update-ref"}:
        return "history rewriting is blocked"
    return None


def _tokenized_package_reason(name: str, args: list[str]) -> str | None:
    if name == "npx":
        return "ephemeral package execution is blocked"
    subcommand, rest = _subcommand_args(
        args,
        {"--registry", "--cache", "--prefix", "--userconfig", "--cwd", "--dir", "-C"},
        valueless_options={"--help", "-h", "--version", "-v", "--yes", "-y", "--silent", "--verbose"},
        fail_on_unknown_option=name in {"npm", "yarn", "pnpm", "nuget"},
    )
    if subcommand == _UNKNOWN_GLOBAL_OPTION_SUBCOMMAND:
        return _HIGH_RISK_UNKNOWN_GLOBAL_OPTION_REASON
    if name in {"npm", "yarn", "pnpm"} and subcommand == "publish":
        return "package publishing is blocked"
    if name == "dotnet" and len(args) >= 2 and args[0] == "nuget" and args[1] == "push":
        return "package publishing is blocked"
    if name == "nuget" and subcommand == "push":
        return "package publishing is blocked"
    if name == "npm" and subcommand in {"login", "adduser"}:
        return "package registry login is blocked"
    if (name == "npm" and subcommand == "exec") or (name in {"yarn", "pnpm"} and subcommand == "dlx"):
        return "ephemeral package execution is blocked"
    if name == "dotnet" and subcommand == "nuget" and rest and rest[0] == "push":
        return "package publishing is blocked"
    return None


def _tokenized_infra_reason(name: str, args: list[str]) -> str | None:
    subcommand, _ = _subcommand_args(
        args,
        {"-chdir", "-n", "--namespace", "--context", "--kubeconfig", "--server", "--user", "--cluster", "--kube-context"},
        valueless_options={"--help", "-h", "--version", "-v"},
        fail_on_unknown_option=name in {"terraform", "kubectl", "helm"},
    )
    if subcommand == _UNKNOWN_GLOBAL_OPTION_SUBCOMMAND:
        return _HIGH_RISK_UNKNOWN_GLOBAL_OPTION_REASON
    if name == "terraform" and subcommand in {"apply", "destroy"}:
        return "infra mutation is blocked"
    if name == "kubectl" and subcommand in {"apply", "delete"}:
        return "infra mutation is blocked"
    if name == "helm" and subcommand in {"install", "upgrade", "uninstall"}:
        return "infra mutation is blocked"
    return None


def _tokenized_docker_reason(args: list[str]) -> str | None:
    subcommand, rest = _subcommand_args(
        args,
        {"--context", "--host", "-H", "--config"},
        valueless_options={"--help", "-h", "--version", "-v"},
        fail_on_unknown_option=True,
    )
    if subcommand == _UNKNOWN_GLOBAL_OPTION_SUBCOMMAND:
        return _HIGH_RISK_UNKNOWN_GLOBAL_OPTION_REASON
    if subcommand in {"push", "login", "rm", "rmi"}:
        return "dangerous docker operation is blocked"
    if subcommand == "image" and rest and rest[0] == "push":
        return "dangerous docker operation is blocked"
    if subcommand == "system" and rest and rest[0] == "prune":
        return "dangerous docker operation is blocked"
    if subcommand == "volume" and rest and rest[0] == "rm":
        return "dangerous docker operation is blocked"
    if subcommand == "buildx":
        buildx_subcommand, buildx_rest = _subcommand_args(
            rest,
            {"--builder", "--config"},
            valueless_options={"--help", "-h", "--version", "-v"},
            fail_on_unknown_option=True,
        )
        if buildx_subcommand == _UNKNOWN_GLOBAL_OPTION_SUBCOMMAND:
            return _HIGH_RISK_UNKNOWN_GLOBAL_OPTION_REASON
        if buildx_subcommand == "build" and _docker_buildx_build_pushes(buildx_rest):
            return "dangerous docker operation is blocked"
    return None


def _docker_buildx_build_pushes(args: list[str]) -> bool:
    for index, arg in enumerate(args):
        if arg == "--push" or arg.startswith("--push="):
            return True
        if arg in {"--output", "-o"}:
            if index + 1 < len(args) and _docker_output_spec_pushes(args[index + 1]):
                return True
            continue
        if arg.startswith("--output=") and _docker_output_spec_pushes(arg.split("=", 1)[1]):
            return True
        if arg.startswith("-o") and len(arg) > 2 and _docker_output_spec_pushes(arg[2:]):
            return True
    return False


def _docker_output_spec_pushes(value: str) -> bool:
    parts = {part.strip().lower() for part in value.split(",")}
    return "type=registry" in parts or "push=true" in parts


def _tokenized_gh_reason(args: list[str]) -> str | None:
    subcommand, rest = _subcommand_args(
        args,
        {"-R", "--repo", "--hostname", "--config"},
        valueless_options={"--help", "-h", "--version", "-v"},
        fail_on_unknown_option=True,
    )
    if subcommand == _UNKNOWN_GLOBAL_OPTION_SUBCOMMAND:
        return _HIGH_RISK_UNKNOWN_GLOBAL_OPTION_REASON
    if subcommand in {"auth", "api", "secret", "release"}:
        return "GitHub CLI high-risk operation is blocked"
    if subcommand == "repo" and rest and rest[0] in {"create", "fork"}:
        return "GitHub CLI high-risk operation is blocked"
    if subcommand == "pr":
        pr_subcommand, pr_rest = _subcommand_args(
            rest,
            {"-R", "--repo", "--hostname", "--config"},
            valueless_options={"--help", "-h", "--web"},
            fail_on_unknown_option=True,
        )
        if pr_subcommand == _UNKNOWN_GLOBAL_OPTION_SUBCOMMAND:
            return _HIGH_RISK_UNKNOWN_GLOBAL_OPTION_REASON
        if pr_subcommand == "merge":
            return "GitHub CLI high-risk operation is blocked"
    return None


def _tokenized_forbidden_tokens_reason(tokens: list[str]) -> str | None:
    tokens = _expand_env_split_strings(tokens)
    normalized = " ".join(tokens)
    if _contains_secret_like_assignment(normalized):
        return "inline secret-looking value in command is blocked"
    if _env_chdir_wrapper_present(tokens):
        return "env chdir wrappers are blocked; run from the target cwd directly"

    for segment in _token_segments(tokens):
        segment = _strip_transparent_script_wrappers(_strip_leading_env_assignments(segment))
        while segment and segment[0] in _SHELL_COMMAND_PREFIX_KEYWORDS:
            segment = _strip_transparent_script_wrappers(_strip_leading_env_assignments(segment[1:]))
        if not segment:
            continue
        token = segment[0]
        if not token or token.startswith("-") or _is_env_assignment(token):
            continue
        name = _token_command_name(token)
        args = segment[1:]
        if name in _SEARCH_OR_DISCOVERY_COMMANDS:
            return "shell search/file-discovery command is blocked; use dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll instead."
        if name in _NETWORK_COMMANDS:
            return "network download/exfil command is blocked"
        if name == "eval":
            return "eval is blocked; use a direct reviewed command or script file instead."
        if name in _CWD_CHANGING_COMMANDS:
            return "cwd-changing shell builtins are blocked; run from the target cwd directly"
        if name == "rm" and _has_recursive_flag(args):
            return "recursive rm is blocked"
        if name in _DESTRUCTIVE_COMMANDS:
            return "destructive filesystem command is blocked"
        if name == "dd" and any(arg.startswith(("if=", "of=")) for arg in args):
            return "dd raw device/file copy is blocked"
        if name == "mkfs" or name.startswith("mkfs.") or name == "newfs":
            return "filesystem formatting is blocked"
        if name == "diskutil" and (
            any(arg in {"erase", "partition"} for arg in args) or (len(args) >= 2 and args[0] == "apfs" and args[1] == "delete")
        ):
            return "disk erase/partition operation is blocked"
        if name == "chmod" and ("777" in args or _has_recursive_flag(args)):
            return "dangerous recursive permission/owner change is blocked"
        if name in {"chown", "chgrp"} and _has_recursive_flag(args):
            return "dangerous recursive permission/owner change is blocked"
        if name in _PRIVILEGE_COMMANDS:
            return "privilege escalation is blocked"
        if name in _PROCESS_KILL_COMMANDS or (name == "kill" and "-9" in args):
            return "broad process killing is blocked"
        if name in _REMOTE_TRANSFER_COMMANDS:
            return "remote shell/file transfer is blocked"
        if name in _CLIPBOARD_COMMANDS:
            return "clipboard access is blocked"
        if name in _MACOS_AUTOMATION_COMMANDS or (name == "shortcuts" and _first_arg(args) == "run"):
            return "macOS automation/app launching is blocked"
        if name in _MACOS_SYSTEM_COMMANDS:
            return "macOS security/system command is blocked"
        if name == "defaults" and _first_arg(args) == "write":
            return "macOS preference modification is blocked"
        if name == "plutil" and "-replace" in args:
            return "macOS preference modification is blocked"
        if name == "git":
            reason = _tokenized_git_reason(args)
            if reason:
                return reason
        reason = _tokenized_package_reason(name, args)
        if reason:
            return reason
        reason = _tokenized_infra_reason(name, args)
        if reason:
            return reason
        if name == "docker":
            reason = _tokenized_docker_reason(args)
            if reason:
                return reason
        if name in _CLOUD_COMMANDS:
            return "cloud CLI is blocked"
        if name == "gh":
            reason = _tokenized_gh_reason(args)
            if reason:
                return reason
        if name in _SECRET_FILE_READ_COMMANDS and any(_SECRET_PATH_RE.search(arg) for arg in args):
            return "reading secret-looking files is blocked"
    return None


def _tokenized_forbidden_command_reason(command: str) -> str | None:
    return _tokenized_forbidden_tokens_reason(_split_command(command))


def _tokenized_forbidden_script_reason(text: str) -> str | None:
    for line in text.splitlines():
        reason = _tokenized_forbidden_command_reason(line)
        if reason:
            return reason
    return _tokenized_forbidden_command_reason(text)


def _git_subcommand(args: list[str]) -> str:
    index = 0
    options_with_values = {"-c", "-C", "--config-env", "--exec-path", "--git-dir", "--work-tree", "--namespace"}
    while index < len(args):
        arg = args[index]
        if arg in options_with_values:
            index += 2
            continue
        if any(arg.startswith(option + "=") for option in options_with_values if option.startswith("--")):
            index += 1
            continue
        if arg.startswith("-"):
            index += 1
            continue
        return arg
    return ""


def _contains_inline_interpreter(command: str) -> bool:
    tokens = _expand_env_split_strings(_split_command(command))
    for index, token in enumerate(tokens):
        if _token_command_name(token) in _INLINE_INTERPRETERS and any(
            _is_inline_interpreter_flag(arg) for arg in tokens[index + 1 :]
        ):
            return True
    return bool(INLINE_INTERPRETER_RE.search(command))


def _is_inline_shell_flag(token: str) -> bool:
    if token in {"-c", "--command"}:
        return True
    return token.startswith("-") and not token.startswith("--") and "c" in token[1:]


def _contains_inline_shell_execution(command: str) -> bool:
    tokens = _expand_env_split_strings(_split_command(command))
    for index, token in enumerate(tokens):
        if (
            _token_command_name(token) in _INLINE_SHELLS
            or token in _INLINE_SHELL_VARIABLES
            or _is_variable_command(token)
        ) and any(_is_inline_shell_flag(arg) for arg in tokens[index + 1 :]):
            return True
    return False


def _contains_secret_like_assignment(text: str) -> bool:
    return bool(INLINE_SECRET_RE.search(text))


def _is_env_assignment(token: str) -> bool:
    return bool(re.match(r"^[A-Za-z_][A-Za-z0-9_]*=.*", token))


def _env_short_option_suffix(token: str, option: str) -> str | None:
    if token.startswith("--") or not token.startswith("-"):
        return None
    offset = token.find(option, 1)
    if offset < 0:
        return None
    return token[offset + 1 :]


def _expand_env_split_strings(tokens: list[str], depth: int = 0) -> list[str]:
    if depth >= 8:
        return tokens
    result: list[str] = []
    segment: list[str] = []
    for token in tokens:
        if token in _SHELL_CONTROL_TOKENS:
            if segment:
                result.extend(_expand_env_split_strings_in_segment(segment, depth))
                segment = []
            result.append(token)
            continue
        segment.append(token)
    if segment:
        result.extend(_expand_env_split_strings_in_segment(segment, depth))
    return result


def _expand_env_split_strings_in_segment(segment: list[str], depth: int) -> list[str]:
    for index, token in enumerate(segment):
        if _token_command_name(token) != "env":
            continue
        expanded = _expand_env_split_strings_from_env(segment[index:], depth)
        if expanded != segment[index:]:
            return segment[:index] + expanded
    return segment


def _expand_env_split_strings_from_env(tokens: list[str], depth: int) -> list[str]:
    tokens = _strip_leading_env_assignments(tokens)
    if not tokens or _token_command_name(tokens[0]) != "env":
        return tokens

    index = 1
    while index < len(tokens):
        token = tokens[index]
        if _is_env_assignment(token):
            index += 1
            continue
        if token in {"-i", "--ignore-environment", "-0", "--null"}:
            index += 1
            continue
        if token in {"-u", "--unset", "-C", "--chdir"}:
            index += 2
            continue
        if token in {"-a", "--argv0"}:
            index += 2
            continue
        if token.startswith(("--unset=", "-C", "--chdir=")):
            index += 1
            continue
        if token.startswith("--argv0="):
            index += 1
            continue
        c_suffix = _env_short_option_suffix(token, "C")
        if c_suffix is not None:
            index += 1 if c_suffix else 2
            continue
        if token in {"-S", "--split-string"}:
            if index + 1 >= len(tokens):
                return tokens
            split_tokens = _split_command(tokens[index + 1])
            return _expand_env_split_strings(["env"] + tokens[1:index] + split_tokens + tokens[index + 2 :], depth + 1)
        s_suffix = _env_short_option_suffix(token, "S")
        if s_suffix is not None:
            if s_suffix:
                split_tokens = _split_command(s_suffix)
                return _expand_env_split_strings(["env"] + tokens[1:index] + split_tokens + tokens[index + 1 :], depth + 1)
            if index + 1 >= len(tokens):
                return tokens
            split_tokens = _split_command(tokens[index + 1])
            return _expand_env_split_strings(["env"] + tokens[1:index] + split_tokens + tokens[index + 2 :], depth + 1)
        if token.startswith("--split-string="):
            split_tokens = _split_command(token.split("=", 1)[1])
            return _expand_env_split_strings(["env"] + tokens[1:index] + split_tokens + tokens[index + 1 :], depth + 1)
        if token.startswith("-"):
            index += 1
            continue
        return tokens
    return tokens


def _strip_leading_env_assignments(tokens: list[str]) -> list[str]:
    index = 0
    while index < len(tokens) and _is_env_assignment(tokens[index]):
        index += 1
    return tokens[index:]


def _env_segment_uses_chdir(tokens: list[str]) -> bool:
    index = 1
    while index < len(tokens):
        token = tokens[index]
        if _is_env_assignment(token):
            index += 1
            continue
        if token in {"-i", "--ignore-environment", "-0", "--null"}:
            index += 1
            continue
        if token in {"-u", "--unset"}:
            index += 2
            continue
        if token in {"-a", "--argv0"}:
            index += 2
            continue
        if token in {"-C", "--chdir"} or token.startswith(("-C", "--chdir=")):
            return True
        if token.startswith("--argv0="):
            index += 1
            continue
        if _env_short_option_suffix(token, "C") is not None:
            return True
        if token in {"-S", "--split-string"}:
            if index + 1 >= len(tokens):
                return False
            return _env_segment_uses_chdir(["env"] + _split_command(tokens[index + 1]) + tokens[index + 2 :])
        s_suffix = _env_short_option_suffix(token, "S")
        if s_suffix is not None:
            if s_suffix:
                return _env_segment_uses_chdir(["env"] + _split_command(s_suffix) + tokens[index + 1 :])
            if index + 1 >= len(tokens):
                return False
            return _env_segment_uses_chdir(["env"] + _split_command(tokens[index + 1]) + tokens[index + 2 :])
        if token.startswith("--split-string="):
            return _env_segment_uses_chdir(["env"] + _split_command(token.split("=", 1)[1]) + tokens[index + 1 :])
        if token.startswith("--unset="):
            index += 1
            continue
        if token.startswith("-"):
            index += 1
            continue
        return False
    return False


def _env_chdir_wrapper_present(tokens: list[str]) -> bool:
    for segment in _token_segments(tokens):
        segment = _strip_leading_env_assignments(segment)
        while segment:
            if _token_command_name(segment[0]) == "env" and _env_segment_uses_chdir(segment):
                return True
            stripped_env = _strip_env_command_wrapper(segment)
            if stripped_env != segment:
                segment = _strip_leading_env_assignments(stripped_env)
                continue
            stripped = _strip_one_transparent_script_wrapper(segment)
            if stripped is None:
                break
            segment = _strip_leading_env_assignments(stripped)
    return False


def _strip_env_command_wrapper(tokens: list[str]) -> list[str]:
    tokens = _strip_leading_env_assignments(tokens)
    if not tokens or _token_command_name(tokens[0]) != "env":
        return tokens

    index = 1
    while index < len(tokens):
        token = tokens[index]
        if _is_env_assignment(token):
            index += 1
            continue
        if token in {"-i", "--ignore-environment", "-0", "--null"}:
            index += 1
            continue
        if token in {"-u", "--unset", "-C", "--chdir", "-a", "--argv0"}:
            index += 2
            continue
        if token in {"-S", "--split-string"}:
            if index + 1 >= len(tokens):
                return []
            return _strip_env_command_wrapper(["env"] + _split_command(tokens[index + 1]) + tokens[index + 2 :])
        s_suffix = _env_short_option_suffix(token, "S")
        if s_suffix is not None:
            if s_suffix:
                return _strip_env_command_wrapper(["env"] + _split_command(s_suffix) + tokens[index + 1 :])
            if index + 1 >= len(tokens):
                return []
            return _strip_env_command_wrapper(["env"] + _split_command(tokens[index + 1]) + tokens[index + 2 :])
        if token.startswith("--split-string="):
            return _strip_env_command_wrapper(["env"] + _split_command(token.split("=", 1)[1]) + tokens[index + 1 :])
        if any(token.startswith(option + "=") for option in {"--unset", "--chdir", "--argv0"}):
            index += 1
            continue
        c_suffix = _env_short_option_suffix(token, "C")
        if c_suffix is not None:
            index += 1 if c_suffix else 2
            continue
        if token.startswith("-"):
            index += 1
            continue
        return tokens[index:]
    return []


def _strip_wrapper_options(
    args: list[str],
    options_with_values: set[str],
    *,
    valueless_options: set[str] | None = None,
    attached_short_options_with_values: set[str] | None = None,
) -> list[str]:
    valueless_options = valueless_options or set()
    attached_short_options_with_values = attached_short_options_with_values or set()
    index = 0
    while index < len(args):
        arg = args[index]
        if arg == "--":
            return args[index + 1 :]
        if arg in options_with_values:
            index += 2
            continue
        if any(arg.startswith(option + "=") for option in options_with_values):
            index += 1
            continue
        if any(arg.startswith(option) and len(arg) > len(option) for option in attached_short_options_with_values):
            index += 1
            continue
        if arg in valueless_options:
            index += 1
            continue
        return args[index:]
    return []


def _strip_one_transparent_script_wrapper(tokens: list[str]) -> list[str] | None:
    if not tokens:
        return None
    first = _token_command_name(tokens[0])
    if first not in _TRANSPARENT_SCRIPT_WRAPPERS:
        return None

    args = tokens[1:]
    if first in {"time"}:
        return _strip_wrapper_options(
            args,
            {"-f", "--format", "-o", "--output"},
            valueless_options={"-p", "-a", "-v", "--verbose", "-q", "--quiet"},
        )
    if first in {"timeout", "gtimeout"}:
        rest = _strip_wrapper_options(
            args,
            {"-s", "--signal", "-k", "--kill-after"},
            valueless_options={"--foreground", "--preserve-status", "-v", "--verbose"},
            attached_short_options_with_values={"-s", "-k"},
        )
        return rest[1:] if rest else []
    if first == "command":
        if args and args[0] in {"-v", "-V"}:
            return []
        return _strip_wrapper_options(args, set(), valueless_options={"-p"})
    if first == "exec":
        return _strip_wrapper_options(args, {"-a"}, valueless_options={"-c", "-l"})
    if first == "nice":
        if args and args[0].startswith("-n") and len(args[0]) > 2:
            args = args[1:]
        return _strip_wrapper_options(args, {"-n", "--adjustment"})
    if first == "nohup":
        return args
    return None


def _strip_transparent_script_wrappers(tokens: list[str]) -> list[str]:
    while True:
        tokens = _strip_env_command_wrapper(tokens)
        stripped = _strip_one_transparent_script_wrapper(tokens)
        if stripped is None:
            return tokens
        tokens = stripped


def _leading_installer_env_assignments_are_safe(tokens: list[str]) -> bool:
    for token in tokens:
        if not _is_env_assignment(token):
            return True
        name = token.split("=", 1)[0]
        if name not in ALLOWED_INSTALLER_ENV_NAMES:
            return False
        if _contains_secret_like_assignment(token):
            return False
    return True


def is_repo_local_install_script(path: Path, project_root: Path) -> bool:
    try:
        return path.resolve() == (project_root.resolve() / REPO_INSTALLER_REL).resolve()
    except Exception:
        return False


def _token_is_repo_local_installer(token: str, cwd: Path, project_root: Path) -> bool:
    path = _token_path(token, cwd)
    return path is not None and is_repo_local_install_script(path, project_root)


def _installer_args_are_safe(args: list[str]) -> bool:
    if not args:
        return True

    first = args[0]
    if first == "--doctor":
        return len(args) <= 2
    if first == "--reinstall-real":
        return len(args) == 2
    if first == "--self-test-local-mirror":
        remaining = args[1:]
        if remaining and remaining[0] == "--self-test-allow-overwrite":
            remaining = remaining[1:]
        return len(remaining) <= 1
    if first.startswith("--"):
        return False
    return len(args) == 1


def _command_mentions_repo_local_installer(command: str, cwd: Path, project_root: Path) -> bool:
    tokens = _strip_leading_env_assignments(_split_command(command))
    return any(_token_is_repo_local_installer(token, cwd, project_root) for token in tokens)


def _command_is_safe_repo_local_installer(command: str, cwd: Path, project_root: Path) -> bool:
    if CONTROL_OP_RE.search(command):
        return False
    if COMMAND_SUBSTITUTION_RE.search(command):
        return False

    raw_tokens = _split_command(command)
    if not _leading_installer_env_assignments_are_safe(raw_tokens):
        return False

    tokens = _strip_leading_env_assignments(raw_tokens)
    if not tokens:
        return False

    first = Path(tokens[0]).name
    if first == "bash":
        script_index: int | None = None
        for index, token in enumerate(tokens[1:], start=1):
            if token.startswith("-"):
                if _is_inline_shell_flag(token):
                    return False
                continue
            if _token_is_repo_local_installer(token, cwd, project_root):
                script_index = index
                break
            return False
        if script_index is None:
            return False
        return _installer_args_are_safe(tokens[script_index + 1 :])

    if _token_is_repo_local_installer(tokens[0], cwd, project_root):
        return _installer_args_are_safe(tokens[1:])

    return False


def _command_is_safe_official_installer_one_liner(command: str) -> bool:
    return bool(OFFICIAL_INSTALLER_ONE_LINER_RE.match(command))


def _command_is_safe_cdidx_resolver(command: str) -> bool:
    return command.strip() == CDIDX_RESOLVER_PRINT_COMMAND


def _command_is_safe_cdidx_mcp_init_smoke(command: str, cwd: Path) -> bool:
    match = CDIDX_MCP_INIT_SMOKE_RE.match(command)
    return bool(match and _token_is_expanded_installed_cdidx(match.group("cdidx"), cwd))


def should_skip_script_scan(decision: GuardDecision, path: Path, project_root: Path) -> bool:
    return (
        decision.allowed
        and decision.reason == ALLOW_REASON_REPO_LOCAL_INSTALLER
        and is_repo_local_install_script(path, project_root)
    )


def evaluate_bash_command(command: str, cwd: Path, project_root: Path) -> GuardDecision:
    if not isinstance(command, str) or not command.strip():
        return _deny("Bash command missing from hook input; failing closed")

    if ANSI_C_QUOTE_RE.search(command):
        return _deny("ANSI-C shell quoting is blocked; use plain reviewed command tokens instead")

    if _command_is_safe_local_cdidx(command, cwd, project_root):
        return _allow("local cdidx command")

    if _command_mentions_local_cdidx(command, project_root):
        return _deny("local cdidx commands must not use shell control operators or command substitutions")

    if LOCAL_CDIDX_DLL_RE.search(command):
        return _deny("use dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll instead")

    if _command_is_safe_cdidx_resolver(command):
        return _allow("cdidx absolute-path resolver bootstrap")

    if _command_is_safe_cdidx_mcp_init_smoke(command, cwd):
        return _allow("cdidx mcp initialize smoke test")

    if _command_is_safe_expanded_installed_cdidx(command, cwd):
        return _allow(ALLOW_REASON_EXPANDED_INSTALLED_CDIDX)

    if _command_mentions_expanded_installed_cdidx(command, cwd):
        return _deny("expanded installed cdidx commands must not use shell control operators or command substitutions")

    if _command_mentions_forbidden_cdidx_executable(command, cwd):
        return _deny(
            "global cdidx is blocked; use dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll instead, "
            "or the fully expanded installed path documented in CLOUD_BOOTSTRAP_PROMPT.md for no-SDK cloud bootstrap."
        )

    if _command_is_safe_official_installer_one_liner(command):
        return _allow(ALLOW_REASON_OFFICIAL_INSTALLER)

    if _command_mentions_repo_local_installer(command, cwd, project_root):
        if _command_is_safe_repo_local_installer(command, cwd, project_root):
            return _allow(ALLOW_REASON_REPO_LOCAL_INSTALLER)
        return _deny("repo-local install.sh bootstrap must use a direct install.sh invocation with supported installer flags only")

    tokenized_reason = _tokenized_forbidden_command_reason(command)
    if tokenized_reason is not None:
        return _deny(tokenized_reason)

    for pattern, reason in _FORBIDDEN_COMMAND_PATTERNS:
        if pattern.search(command):
            return _deny(reason)

    if _contains_inline_interpreter(command):
        return _deny("inline interpreter execution is blocked; use a reviewed script file instead")

    if _contains_inline_shell_execution(command):
        return _deny("inline shell execution is blocked; use a reviewed script file instead")

    return _allow("bash-guard allow")


def _candidate_script_paths_from_tokens(tokens: list[str], cwd: Path) -> list[Path]:
    tokens = _strip_transparent_script_wrappers(tokens)
    if not tokens:
        return []

    if any(token in _SHELL_CONTROL_TOKENS for token in tokens):
        result: list[Path] = []
        for segment in _token_segments(tokens):
            result.extend(_candidate_script_paths_from_tokens(segment, cwd))
        return result

    result: list[Path] = []
    first = Path(tokens[0]).name

    if first in {"source", "."} and len(tokens) > 1:
        path = _token_path(tokens[1], cwd)
        if path is not None:
            result.append(path)
        return result

    if first in {"bash", "sh", "zsh", "fish", "python", "python3", "ruby", "perl", "node", "deno", "php"}:
        if any(token in _INLINE_INTERPRETER_FLAGS for token in tokens[1:]):
            return result
        for token in tokens[1:]:
            if token.startswith("-"):
                continue
            path = _token_path(token, cwd)
            if path is not None:
                result.append(path)
            break
        return result

    path = _token_path(tokens[0], cwd)
    if path is None:
        return result
    if _path_is_expanded_installed_cdidx(path):
        return result

    if tokens[0].startswith(("./", "../", "/", "~")) or path.suffix.lower() in {
        ".sh", ".bash", ".zsh", ".fish", ".py", ".rb", ".pl", ".js", ".mjs", ".cjs", ".ts", ".php"
    }:
        result.append(path)

    return result


def candidate_script_paths(command: str, cwd: Path) -> list[Path]:
    return _candidate_script_paths_from_tokens(_split_command(command), cwd)


def _is_relative_to(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def check_script_file(path: Path, project_root: Path) -> GuardDecision:
    if not path.exists():
        return _deny(f"candidate script not found; failing closed: {path}")
    if not path.is_file():
        return _deny(f"candidate script is not a file: {path}")

    project_root = project_root.resolve()
    try:
        resolved = path.resolve()
    except Exception as exc:
        return _deny(f"could not resolve script path; failing closed: {path}: {exc}")

    if not _is_relative_to(resolved, project_root):
        return _deny(f"script outside project is blocked: {path}")

    try:
        with resolved.open("rb") as handle:
            data = handle.read(MAX_SCRIPT_SCAN_BYTES + 1)
    except Exception as exc:
        return _deny(f"could not inspect script before execution; failing closed: {path}: {exc}")
    if len(data) > MAX_SCRIPT_SCAN_BYTES:
        return _deny(
            f"candidate script exceeds {MAX_SCRIPT_SCAN_BYTES} byte scan limit; failing closed: {path}"
        )

    text = data.decode("utf-8", errors="ignore")
    if ANSI_C_QUOTE_RE.search(text):
        return _deny(f"script contains ANSI-C shell quoting: {path}")
    tokenized_reason = _tokenized_forbidden_script_reason(text)
    if tokenized_reason is not None:
        return _deny(f"script contains blocked command: {tokenized_reason}: {path}")
    for pattern, reason in _SCRIPT_FORBIDDEN_PATTERNS:
        if pattern.search(text):
            return _deny(f"{reason}: {path}")
    return _allow(f"script allowed: {path}")


def staged_secret_check(cwd: Path) -> GuardDecision:
    gitleaks = shutil.which("gitleaks")
    if gitleaks:
        try:
            proc = subprocess.run(
                [gitleaks, "protect", "--staged", "--redact", "--verbose"],
                cwd=str(cwd),
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=60,
                check=False,
            )
        except Exception as exc:
            return _deny(f"could not inspect staged diff for secrets with gitleaks; failing closed: {exc}")
        if proc.returncode != 0:
            output = (proc.stdout or "").strip()
            if len(output) > 2000:
                output = output[:2000] + "\n..."
            return _deny("gitleaks blocked this commit:\n" + output)
        return _allow("gitleaks passed")

    try:
        proc = subprocess.run(
            ["git", "diff", "--cached", "--unified=0", "--no-ext-diff"],
            cwd=str(cwd),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=30,
            check=False,
        )
    except Exception as exc:
        return _deny(f"could not inspect staged diff for secrets; failing closed: {exc}")
    if proc.returncode != 0:
        return _deny("could not inspect staged diff for secrets; install gitleaks or fix git diff")

    added_lines = "\n".join(
        line[1:]
        for line in (proc.stdout or "").splitlines()
        if line.startswith("+") and not line.startswith("+++")
    )
    for pattern, name in _SECRET_PATTERNS:
        if pattern.search(added_lines):
            return _deny(
                f"secret-looking staged content detected before commit: {name}; install gitleaks for better scanning"
            )
    return _allow("staged secret scan passed")
