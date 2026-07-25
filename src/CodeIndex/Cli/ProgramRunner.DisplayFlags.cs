using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Lsp;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ProgramRunner
{
    internal static bool TryConsumeColorFlag(ref string[] args, out string error)
    {
        error = string.Empty;
        ConsoleUi.SetColorMode(ColorMode.Auto);
        if (args.Length == 0)
            return true;

        var kept = new List<string>(args.Length);
        ColorMode? requested = null;
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // After a `--` token, leave everything alone so subcommands keep
            // their query-escape semantics (e.g. `cdidx search -- --color=auto`).
            if (passthrough)
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                passthrough = true;
                kept.Add(arg);
                continue;
            }
            if (ShouldPreserveQueryCommandToken(args, i))
            {
                kept.Add(arg);
                continue;
            }

            string? rawValue = null;
            if (arg == "--color")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --color requires a value (one of `auto`, `always`, `never`).";
                    return false;
                }
                rawValue = args[++i];
            }
            else if (arg.StartsWith("--color=", StringComparison.Ordinal))
            {
                rawValue = arg.Substring("--color=".Length);
            }
            else
            {
                kept.Add(arg);
                continue;
            }

            if (!ConsoleUi.TryParseColorMode(rawValue, out var mode))
            {
                error = $"Error: invalid --color value `{rawValue}`.";
                return false;
            }
            requested = mode;
        }

        if (requested.HasValue)
            ConsoleUi.SetColorMode(requested.Value);
        args = kept.ToArray();
        return true;
    }

    internal static void TryConsumeAsciiFlag(ref string[] args)
    {
        ConsoleUi.SetAsciiOutput(false);
        if (args.Length == 0)
            return;

        var kept = new List<string>(args.Length);
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (passthrough)
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                passthrough = true;
                kept.Add(arg);
                continue;
            }
            if (ShouldPreserveQueryCommandToken(args, i))
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--ascii")
            {
                ConsoleUi.SetAsciiOutput(true);
                continue;
            }

            kept.Add(arg);
        }

        args = kept.ToArray();
    }

    internal static void TryConsumeNoProgressFlag(ref string[] args)
    {
        ConsoleUi.SetProgressAnimationEnabled(null);
        if (args.Length == 0)
            return;

        var kept = new List<string>(args.Length);
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (passthrough)
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                passthrough = true;
                kept.Add(arg);
                continue;
            }
            if (ShouldPreserveQueryCommandToken(args, i))
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--no-progress")
            {
                ConsoleUi.SetProgressAnimationEnabled(false);
                continue;
            }

            kept.Add(arg);
        }

        args = kept.ToArray();
    }

    // Strip `--palette <name>` / `--palette=<name>` from `args` before
    // subcommand parsing. Mirrors `TryConsumeColorFlag` so any subcommand
    // (CLI or MCP) inherits the chosen ANSI palette without re-parsing.
    // Anything after `--` is passed through verbatim so subcommand
    // query-escape semantics are preserved (#1569).
    internal static bool TryConsumePaletteFlag(ref string[] args, out string error)
    {
        error = string.Empty;
        ConsoleUi.SetColorPalette(null);
        if (args.Length == 0)
            return true;

        var kept = new List<string>(args.Length);
        ColorPalette? requested = null;
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (passthrough)
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                passthrough = true;
                kept.Add(arg);
                continue;
            }
            if (ShouldPreserveQueryCommandToken(args, i))
            {
                kept.Add(arg);
                continue;
            }

            string? rawValue = null;
            if (arg == "--palette")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --palette requires a value (one of `basic`, `256`, `truecolor`).";
                    return false;
                }
                rawValue = args[++i];
            }
            else if (arg.StartsWith("--palette=", StringComparison.Ordinal))
            {
                rawValue = arg.Substring("--palette=".Length);
            }
            else
            {
                kept.Add(arg);
                continue;
            }

            if (!ConsoleUi.TryParseColorPalette(rawValue, out var palette))
            {
                error = $"Error: invalid --palette value `{rawValue}`.";
                return false;
            }
            requested = palette;
        }

        if (requested.HasValue)
            ConsoleUi.SetColorPalette(requested.Value);
        args = kept.ToArray();
        return true;
    }

    // Strip the `--debug-unsafe` opt-in from `args` before subcommand parsing.
    // The flag must be passed every command invocation (not via env var) so a stale
    // CDIDX_DEBUG=unsafe in a shell profile or CI env cannot quietly leak indexed
    // source content (#1530). Anything after `--` is left untouched so subcommand
    // query strings keep their literal semantics.
    // サブコマンド処理前に `--debug-unsafe` を取り除く。環境変数 CDIDX_DEBUG=unsafe が
    // シェルプロファイル / CI に残った状態で索引済みソースが漏れないよう、明示的にフラグを
    // 毎回渡す運用にする（#1530）。`--` 以降はサブコマンドのクエリ文字列を保つため触らない。
    internal static bool TryConsumeDebugUnsafeFlag(ref string[] args)
    {
        if (args.Length == 0)
            return false;

        var kept = new List<string>(args.Length);
        var passthrough = false;
        var seen = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (passthrough)
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                passthrough = true;
                kept.Add(arg);
                continue;
            }
            if (ShouldPreserveQueryCommandToken(args, i))
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--debug-unsafe")
            {
                seen = true;
                continue;
            }
            kept.Add(arg);
        }

        if (seen)
        {
            DbDebug.EnableUnsafeForProcess();
            args = kept.ToArray();
        }
        return seen;
    }

    internal static bool TryConsumeStrictVersionFlag(ref string[] args, out bool strictVersion, out string error)
    {
        strictVersion = IsTruthyEnvironmentVariable("CDIDX_STRICT_VERSION");
        error = string.Empty;
        if (args.Length == 0)
            return true;

        var kept = new List<string>(args.Length);
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (passthrough)
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                passthrough = true;
                kept.Add(arg);
                continue;
            }
            if (ShouldPreserveQueryCommandToken(args, i))
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--strict-version")
            {
                strictVersion = true;
                continue;
            }
            if (arg.StartsWith("--strict-version=", StringComparison.Ordinal))
            {
                error = "Error: --strict-version does not accept a value.";
                return false;
            }
            kept.Add(arg);
        }

        args = kept.ToArray();
        return true;
    }
}
