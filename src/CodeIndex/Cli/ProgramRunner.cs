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
    private const int RetainedQueryTraceFileCount = 30;
    internal const int QueryTraceValueMaxChars = 128;
    internal const int QueryTraceArrayMaxItems = 8;
    internal const string QuietEnvironmentVariable = "CDIDX_QUIET";
    internal const string AllowUnauthenticatedHttpFlag = "--allow-unauthenticated-http";
    private const string ReleaseAssetUrlTemplate = "https://github.com/Widthdom/CodeIndex/releases/download/{0}/{1}";
    private const string ReleasePageUrlTemplate = "https://github.com/Widthdom/CodeIndex/releases/tag/{0}";
    private const string InstallerScriptAssetName = "install.sh";
    private const string UpgradeInstallerDirectoryPrefix = "cdidx-install-";
    private const string ReleaseChecksumAssetName = "sha256sums.txt";
    private const string ReleaseAttestationSignerWorkflow = "github.com/Widthdom/CodeIndex/.github/workflows/release.yml";
    private const long MaxInstallerScriptBytes = 1024 * 1024;
    internal const long MaxReleaseChecksumBytes = 256 * 1024;
    private const int InstallerSuppressedOutputDrainBufferChars = 4096;
    internal const int InstallerSuppressedOutputTailChars = 4096;
    private const string UpgradeInstallerVerification = "github_attestation_and_sha256_manifest";
    private const string UpgradeInstallerTrustBoundary = "When installation runs, sha256sums.txt and install.sh must verify against the pinned CodeIndex release workflow and selected tag before the manifest checksum is trusted or installer code is executed; compat bypasses are reported separately.";
    internal const int WorkspaceVersionPinMaxBytes = 4096;
    internal const int WorkspaceVersionPinMaxSkippedBlankLines = 16;
    internal const int WorkspaceVersionPinMaxLineChars = 256;
    internal const long TestExtractorMaxInputBytes = 4 * 1024 * 1024;
    internal const int TestExtractorJsonComparisonMaxBytes = (int)TestExtractorMaxInputBytes * 4;
    internal const int TestExtractorJsonComparisonMaxDepth = 32;
    private const int TestExtractorReadBufferBytes = 81920;
    private static readonly TimeSpan InstallerRunTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallerKillWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan McpHttpDisposeTimeout = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> NonLogGlobalOptionNames =
        CliFlagSchema.GetTopLevelGlobalOptionNames(includeLogOptions: false);
    private static readonly HashSet<string> TopLevelValueOptionNames =
        CliFlagSchema.GetTopLevelValueOptionNames();
    private static readonly AsyncLocal<TimeProvider?> ScopedTimeProviderForTesting = new();
    private static readonly AsyncLocal<Func<HttpClient>?> ScopedUpgradeHttpClientFactoryForTesting = new();
    private static readonly AsyncLocal<Func<string, string, CancellationToken, bool>?> ScopedUpgradeAssetProvenanceVerifierForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedTestExtractorFileLengthCheckedForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedDeleteInstallDirectoryWriteProbeForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedDeleteUpgradeInstallerScriptForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedDeleteUpgradeInstallerDirectoryForTesting = new();

    internal static TimeProvider TimeProvider
    {
        get => ScopedTimeProviderForTesting.Value ?? System.TimeProvider.System;
        set => ScopedTimeProviderForTesting.Value = ReferenceEquals(value, System.TimeProvider.System) ? null : value;
    }

    internal static Func<HttpClient> UpgradeHttpClientFactory
    {
        get => ScopedUpgradeHttpClientFactoryForTesting.Value ?? CreateUpgradeHttpClient;
        set => ScopedUpgradeHttpClientFactoryForTesting.Value = value == CreateUpgradeHttpClient ? null : value;
    }

    internal static Func<string, string, CancellationToken, bool> UpgradeAssetProvenanceVerifier
    {
        get => ScopedUpgradeAssetProvenanceVerifierForTesting.Value ?? VerifyUpgradeAssetProvenance;
        set => ScopedUpgradeAssetProvenanceVerifierForTesting.Value = value == VerifyUpgradeAssetProvenance ? null : value;
    }

    internal static Action<string>? TestExtractorFileLengthCheckedForTesting
    {
        get => ScopedTestExtractorFileLengthCheckedForTesting.Value;
        set => ScopedTestExtractorFileLengthCheckedForTesting.Value = value;
    }

    internal static Action<string>? DeleteInstallDirectoryWriteProbeForTesting
    {
        get => ScopedDeleteInstallDirectoryWriteProbeForTesting.Value;
        set => ScopedDeleteInstallDirectoryWriteProbeForTesting.Value = value;
    }

    internal static Action<string>? DeleteUpgradeInstallerScriptForTesting
    {
        get => ScopedDeleteUpgradeInstallerScriptForTesting.Value;
        set => ScopedDeleteUpgradeInstallerScriptForTesting.Value = value;
    }

    internal static Action<string>? DeleteUpgradeInstallerDirectoryForTesting
    {
        get => ScopedDeleteUpgradeInstallerDirectoryForTesting.Value;
        set => ScopedDeleteUpgradeInstallerDirectoryForTesting.Value = value;
    }

    private sealed record CommandRunContext(
        JsonSerializerOptions JsonOptions,
        string AppVersion,
        DateTimeOffset StartTimestamp,
        Stopwatch Stopwatch,
        CancellationToken CancellationToken);

    internal sealed record UpgradeHandoff(
        string Command,
        string Url,
        string Asset,
        string AssetUrl);

    internal static int Run(
        string[] args,
        JsonSerializerOptions? jsonOptions = null,
        string? appVersion = null,
        string? configStartDirectory = null,
        Action? beforeDispatchForTesting = null,
        CancellationToken cancellationToken = default)
    {
        if (args.Length > 0 && StringComparer.Ordinal.Equals(args[0], SymbolExtractionWorker.CommandName))
        {
            using var symbolWorkerOutput = Console.OpenStandardOutput();
            _ = SymbolExtractionWorker.TryRunCommand(
                args,
                Console.In,
                symbolWorkerOutput,
                Console.Error,
                out var symbolWorkerExitCode,
                cancellationToken: cancellationToken);
            return symbolWorkerExitCode;
        }

        if (ExtractorPluginWorker.TryRunCommand(args, Console.In, Console.Out, Console.Error, out var pluginWorkerExitCode))
            return pluginWorkerExitCode;

        if (PostExtractionHookDiscoveryWorker.TryRunCommand(args, Console.In, Console.Out, Console.Error, out var hookDiscoveryWorkerExitCode))
            return hookDiscoveryWorkerExitCode;

        if (PostExtractionHookCallbackWorker.TryRunCommand(args, Console.In, Console.Out, Console.Error, out var hookWorkerExitCode))
            return hookWorkerExitCode;

        using var recoveryInvocationScope = ExcerptRecoveryCommandFormatter.UseCurrentProcessInvocation();
        appVersion ??= ConsoleUi.LoadVersion();

        // Load project-local `.cdidxrc.json` before anything else reads env vars so log
        // location, debug mode, and MCP gates honor the file (#1571). Hard-fail on
        // validation errors so silent typos cannot quietly change behavior.
        // 環境変数を読む処理より先に `.cdidxrc.json` を読み込み、ログ位置 / debug / MCP ゲート
        // などが config を反映できるようにする (#1571)。スキーマ違反は黙って無視せず exit する。
        var configResult = CdidxConfigFile.Load(configStartDirectory ?? Environment.CurrentDirectory);
        if (configResult.Failed && !IsConfigShowCommand(args) && !IsValidateConfigCommand(args))
        {
            return CommandErrorWriter.Write(
                StripErrorPrefix(configResult.Error ?? "configuration file validation failed."),
                CommandExitCodes.UsageError,
                $"fix or remove `{CdidxConfigFile.FileName}`, or set `{CdidxConfigFile.DisableEnvVar}=1` to bypass it.");
        }

        using var configEnvironment = CdidxEnvironment.Push(configResult.Settings, configResult.Sources);

        if (!TryConsumeGlobalLogFlags(ref args, out var globalLogEnvironment, out var globalLogError))
        {
            CommandErrorWriter.Write(StripErrorPrefix(globalLogError), "use --log-format <text|json>, --log-retain-count <N>, or --log-max-size-mb <N>.");
            return CommandExitCodes.InvalidArgument;
        }

        using var globalLogFlagEnvironment = CdidxEnvironment.Push(globalLogEnvironment);
        using var globalToolLog = GlobalToolLog.TryStart(args, appVersion);
        if (configResult.Loaded)
            GlobalToolLog.Info($"config_file_loaded path={configResult.ConfigPath}");
        jsonOptions ??= CreateDefaultJsonOptions();
        EnsureRedirectedStdoutUsesUtf8();

        var quiet = TryConsumeQuietFlag(ref args) || IsTruthyEnvironmentVariable(QuietEnvironmentVariable);
        using var quietScope = quiet ? QuietStderrScope.Start() : null;

        if (!TryConsumeColorFlag(ref args, out var colorError))
        {
            CommandErrorWriter.Write(StripErrorPrefix(colorError), "use one of `auto`, `always`, `never`.");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.InvalidArgument} color_flag_invalid=true");
            return CommandExitCodes.InvalidArgument;
        }

        if (!TryConsumePaletteFlag(ref args, out var paletteError))
        {
            CommandErrorWriter.Write(StripErrorPrefix(paletteError), "use one of `basic`, `256`, `truecolor`.");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.InvalidArgument} palette_flag_invalid=true");
            return CommandExitCodes.InvalidArgument;
        }

        TryConsumeAsciiFlag(ref args);
        TryConsumeNoProgressFlag(ref args);

        if (!TryConsumeMetricsFlag(ref args, out var metricsPath, out var metricsError))
        {
            CommandErrorWriter.Write(StripErrorPrefix(metricsError), "pass `--metrics <path>` (e.g. `--metrics out.jsonl`).");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.InvalidArgument} metrics_flag_invalid=true");
            return CommandExitCodes.InvalidArgument;
        }

        using var metricsSession = MetricsSink.TryStart(metricsPath);

        TryConsumeDebugUnsafeFlag(ref args);
        if (!TryConsumeStrictVersionFlag(ref args, out var strictVersion, out var strictVersionError))
        {
            CommandErrorWriter.Write(StripErrorPrefix(strictVersionError), "use `--strict-version` without a value.");
            return CommandExitCodes.InvalidArgument;
        }
        if (!TryValidateOutputFormatOptions(args, out var outputFormatError, out var outputFormatHint, out var outputFormatUsage))
        {
            CommandErrorWriter.Write(outputFormatError, outputFormatHint, outputFormatUsage);
            return CommandExitCodes.UsageError;
        }
        if (TryConsumePrettyJsonFlag(ref args))
            jsonOptions = new JsonSerializerOptions(jsonOptions) { WriteIndented = true };
        using var jsonAnsiScope = ConsoleUi.SuppressAnsiForJsonOutput(ContainsJsonOutputFlag(args));

        var commandStopwatch = Stopwatch.StartNew();
        var commandStartTimestamp = TimeProvider.GetUtcNow();
        var versionPinExit = CheckWorkspaceVersionPin(appVersion, configStartDirectory ?? Environment.CurrentDirectory, strictVersion);
        if (versionPinExit != CommandExitCodes.Success)
            return versionPinExit;

        var context = new CommandRunContext(jsonOptions, appVersion, commandStartTimestamp, commandStopwatch, cancellationToken);
        if (TryRunImmediateCommand(args, context, out var immediateExitCode))
            return immediateExitCode;

        try
        {
            return RunDispatchedCommand(args, context, beforeDispatchForTesting);
        }
        catch (CodeIndexException ex)
        {
            // Issue #1580: surface Code, Path, Category, and Hint uniformly so
            // users can tell which file failed and automation has a stable
            // signal to branch on instead of parsing free-form messages.
            // #1580: 失敗ファイル / 構造化フィールドを CLI で一律に表示する。
            var exitCode = MapCodeIndexExceptionExitCode(ex.Code);
            CodeIndexExceptionFormatter.Write(ex, args, context.JsonOptions);
            GlobalToolLog.Error($"command_complete exit_code={exitCode} code_index_exception code={ex.Code} category={ex.Category} path={ex.Path}", ex, includeStacks: false);
            EmitCommandMetric(args[0], args, context.StartTimestamp, context.Stopwatch, exitCode, ex.Code);
            return exitCode;
        }
        catch (OperationCanceledException ex)
        {
            GlobalToolLog.Error($"command_complete exit_code={CommandExitCodes.CancelledBySignal} operation_cancelled", ex, includeStacks: false);
            CommandErrorWriter.WriteStderr("Error: command cancelled before it could complete.");
            EmitCommandMetric(args[0], args, context.StartTimestamp, context.Stopwatch, CommandExitCodes.CancelledBySignal, ex.GetType().Name);
            return CommandExitCodes.CancelledBySignal;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
            {
                GlobalToolLog.Error($"command_complete exit_code={exitCode} handled_exception", ex, includeStacks: false);
                EmitCommandMetric(args[0], args, context.StartTimestamp, context.Stopwatch, exitCode, ex.GetType().Name);
                return exitCode;
            }

            var unhandledExitCode = MapUnhandledExceptionExitCode(ex);
            GlobalToolLog.Error($"command_complete exit_code={unhandledExitCode} unhandled_exception", ex);
            var failureCaptured = LastFailureEventStore.TryPersist(
                args,
                context.AppVersion,
                unhandledExitCode,
                ex,
                TimeProvider.GetUtcNow());
            CommandErrorWriter.WriteStderr(failureCaptured
                ? "Error: command failed before it could complete. Run `cdidx report` for details."
                : "Error: command failed before it could complete; current failure diagnostics could not be saved.");
            EmitCommandMetric(args[0], args, context.StartTimestamp, context.Stopwatch, unhandledExitCode, ex.GetType().Name);
            return unhandledExitCode;
        }
    }

    private static bool IsConfigShowCommand(IReadOnlyList<string> args)
    {
        var commandIndex = 0;
        while (commandIndex < args.Count && args[commandIndex].StartsWith("--", StringComparison.Ordinal))
        {
            var option = args[commandIndex];
            var optionName = option.Split('=', 2)[0];
            commandIndex++;
            if (!option.Contains('=', StringComparison.Ordinal)
                && TopLevelValueOptionNames.Contains(optionName)
                && commandIndex < args.Count)
            {
                commandIndex++;
            }
        }

        return commandIndex + 1 < args.Count
               && string.Equals(args[commandIndex], "config", StringComparison.Ordinal)
               && string.Equals(args[commandIndex + 1], "show", StringComparison.Ordinal);
    }

    private static bool IsValidateConfigCommand(IReadOnlyList<string> args)
    {
        var commandIndex = 0;
        while (commandIndex < args.Count && args[commandIndex].StartsWith("--", StringComparison.Ordinal))
        {
            var option = args[commandIndex];
            var optionName = option.Split('=', 2)[0];
            commandIndex++;
            if (!option.Contains('=', StringComparison.Ordinal)
                && TopLevelValueOptionNames.Contains(optionName)
                && commandIndex < args.Count)
            {
                commandIndex++;
            }
        }

        return commandIndex < args.Count
               && string.Equals(args[commandIndex], "validate-config", StringComparison.Ordinal);
    }







    // Strip `--metrics <path>` / `--metrics=<path>` from the global args before subcommand
    // parsing so any command (CLI or MCP) inherits the same JSONL metrics sink without
    // each subcommand re-declaring the flag. Falls back to the CDIDX_METRICS env var when
    // the explicit flag is absent. Anything after `--` is left untouched to preserve
    // subcommand query-escape semantics (#1549).
    // サブコマンド解析前に `--metrics <path>` / `--metrics=<path>` を取り除き、CLI/MCPいずれの
    // コマンドでも同じJSONLシンクを継承させる。明示フラグが無い場合は CDIDX_METRICS 環境変数に
    // フォールバック。`--` 以降はサブコマンドのクエリエスケープ意味論を保つため触らない (#1549)。

    private const string DefaultMcpHttpListen = "127.0.0.1:38080";
    internal const string McpHttpTokenEnvVar = "CDIDX_MCP_HTTP_TOKEN";









}
