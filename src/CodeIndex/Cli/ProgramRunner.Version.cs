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
    // `--version` is now build-aware so dev builds from main are not
    // indistinguishable from tagged releases in bug reports (#1550). Human
    // output stays on a single line — `cdidx v<ver>` optionally followed by
    // ` (commit <sha>, built <date>, <clean|dirty>)` — so the install.sh
    // reinstall validator can stay anchored against trailing diagnostic spam.
    // バグ報告で dev ビルドとリリースタグを区別できるよう `--version` を
    // ビルド情報付きにする (#1550)。人間出力は 1 行に保ち、install.sh の
    // reinstall validator が末尾診断文を誤って許容しないよう、括弧で囲った
    // メタデータ以外を許さない形に揃える。
    internal static int RunVersion(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        string? appVersion = null,
        CancellationToken cancellationToken = default)
    {
        var wantsJson = false;
        foreach (var arg in cmdArgs)
        {
            if (arg is "--json")
            {
                wantsJson = true;
                continue;
            }
            CommandErrorWriter.WriteStderr($"Error: --version does not accept '{arg}'.");
            CommandErrorWriter.WriteStderr("Hint: use `cdidx --version` or `cdidx --version --json`.");
            return CommandExitCodes.UsageError;
        }

        var baseMetadata = ConsoleUi.LoadBuildMetadata();
        // Honour the caller-provided appVersion (overrides version.json so
        // tests and embedded hosts can pin a specific semver) while keeping
        // the assembly-stamped commit/build-date/dirty fields.
        // 呼び出し元が appVersion を渡した場合はそれを優先する（テストや
        // 組み込みホストが semver を固定できるよう）一方、commit / build
        // date / dirty は刻印された値をそのまま使う。
        var metadata = string.IsNullOrWhiteSpace(appVersion)
            ? baseMetadata
            : baseMetadata with { Version = appVersion! };
        if (wantsJson)
        {
            var payload = new VersionInfoJsonResult(
                Name: "cdidx",
                Version: metadata.Version,
                Commit: metadata.Commit,
                BuildDate: metadata.BuildDate,
                Dirty: metadata.Dirty);
            var json = JsonSerializer.Serialize(payload, CliJsonSerializerContextFactory.Create(jsonOptions).VersionInfoJsonResult);
            Console.WriteLine(json);
            return CommandExitCodes.Success;
        }

        var updateHint = UpdateChecker.GetNewerReleaseHint(metadata.Version, cancellationToken);
        Console.WriteLine(FormatVersionLine(metadata, updateHint));
        return CommandExitCodes.Success;
    }

    internal static string FormatVersionLine(ConsoleUi.BuildMetadata metadata, string? updateHint = null)
    {
        var commit = string.IsNullOrWhiteSpace(metadata.Commit) ? "unknown" : metadata.Commit;
        var buildDate = string.IsNullOrWhiteSpace(metadata.BuildDate) ? "unknown" : metadata.BuildDate;
        var dirty = string.IsNullOrWhiteSpace(metadata.Dirty) ? "unknown" : metadata.Dirty;
        var suffix = string.IsNullOrWhiteSpace(updateHint) ? string.Empty : $" [{updateHint}]";

        // Suppress the metadata suffix only when every component is "unknown",
        // so legacy callers that depend on the exact `cdidx v<ver>` shape keep
        // working when no build stamp is present (e.g. mocked binaries).
        // 全項目が unknown のときだけ末尾メタデータを省略し、ビルド刻印が
        // 無い旧バイナリ／モックでも `cdidx v<ver>` 形式を保つ。
        if (commit == "unknown" && buildDate == "unknown" && dirty == "unknown")
            return $"cdidx v{metadata.Version}{suffix}";

        return $"cdidx v{metadata.Version} (commit {commit}, built {buildDate}, {dirty}){suffix}";
    }

    private static int RunCompletions(string[] cmdArgs, JsonSerializerOptions jsonOptions, string commandName = "--completions")
    {
        var usage = $"cdidx {commandName} <shell>";
        var wantsJson = ContainsJsonOutputFlag(cmdArgs);
        if (wantsJson)
            return CommandErrorWriter.WriteJsonOrHuman(
                true,
                jsonOptions,
                "--json is not supported for completions.",
                CommandExitCodes.UsageError,
                "rerun with one of `bash`, `zsh`, `fish`, or `powershell`; completions output is already a shell script.",
                usage);

        if (cmdArgs.Length == 0)
            return CommandErrorWriter.Write(
                $"{commandName} requires a shell value.",
                CommandExitCodes.UsageError,
                "rerun with one of `bash`, `zsh`, `fish`, or `powershell`.",
                usage);

        if (cmdArgs[0].StartsWith("-", StringComparison.Ordinal))
            return CommandErrorWriter.Write(
                $"{commandName} requires a shell value, got option-like token '{cmdArgs[0]}'.",
                CommandExitCodes.UsageError,
                "rerun with one of `bash`, `zsh`, `fish`, or `powershell`.",
                usage);

        if (cmdArgs.Length > 1)
            return CommandErrorWriter.Write(
                $"{commandName} accepts exactly one shell value, got extra {ConsoleUi.Counted(cmdArgs.Length - 1, "argument")}: {string.Join(", ", cmdArgs.Skip(1).Select(arg => $"`{arg}`"))}.",
                CommandExitCodes.UsageError,
                "rerun with exactly one shell name: `bash`, `zsh`, `fish`, or `powershell`.",
                usage);

        if (ConsoleUi.PrintCompletions(cmdArgs[0]))
            return CommandExitCodes.Success;

        return CommandErrorWriter.Write(
            $"unsupported completion shell `{cmdArgs[0]}`.",
            CommandExitCodes.UsageError,
            "rerun with one of `bash`, `zsh`, `fish`, or `powershell`.",
            usage);
    }

    private static string StripErrorPrefix(string message)
    {
        const string prefix = "Error: ";
        return message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message;
    }

    private static int ShowError(string[] args, string message, JsonSerializerOptions jsonOptions)
    {
        if (ContainsJsonOutputFlag(args))
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                true,
                jsonOptions,
                message,
                CommandExitCodes.UsageError,
                "run `cdidx --help` to list available commands.");
        }

        CommandErrorWriter.WriteStderr($"Error: {message}");

        var input = args[0];
        if (!input.StartsWith('-'))
        {
            var best = ConsoleUi.FindClosestCommand(input);
            if (best != null)
                CommandErrorWriter.WriteStderr($"Did you mean: cdidx {best}?");
        }

        CommandErrorWriter.WriteStderr("Run 'cdidx --help' for usage information.");
        return CommandExitCodes.UsageError;
    }
}
