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
    private static int RunDoctor(string[] args, string appVersion, JsonSerializerOptions jsonOptions)
    {
        var wantsJson = args.Any(static arg => arg == "--json" || arg.StartsWith("--json=", StringComparison.Ordinal));
        var json = false;
        bool? redactPaths = null;
        var envInventory = DoctorEnvironmentInventoryMode.None;
        string? envDomain = null;
        string? envCategory = null;
        string? envSensitivity = null;
        int? maxJsonBytes = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--env-domain" || arg.StartsWith("--env-domain=", StringComparison.Ordinal))
            {
                _ = TryReadDoctorValueOption(args, ref i, arg, "--env-domain", wantsJson, jsonOptions, out envDomain, out var optionExitCode);
                if (optionExitCode.HasValue)
                    return optionExitCode.Value;
                continue;
            }
            if (arg == "--env-category" || arg.StartsWith("--env-category=", StringComparison.Ordinal))
            {
                _ = TryReadDoctorValueOption(args, ref i, arg, "--env-category", wantsJson, jsonOptions, out envCategory, out var optionExitCode);
                if (optionExitCode.HasValue)
                    return optionExitCode.Value;
                continue;
            }
            if (arg == "--env-sensitivity" || arg.StartsWith("--env-sensitivity=", StringComparison.Ordinal))
            {
                _ = TryReadDoctorValueOption(args, ref i, arg, "--env-sensitivity", wantsJson, jsonOptions, out envSensitivity, out var optionExitCode);
                if (optionExitCode.HasValue)
                    return optionExitCode.Value;
                continue;
            }
            if (arg == "--max-json-bytes" || arg.StartsWith("--max-json-bytes=", StringComparison.Ordinal))
            {
                if (!TryConsumeInlineOrNext(args, ref i, arg, "--max-json-bytes", out var value)
                    || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    || parsed <= 0)
                {
                    return CommandErrorWriter.WriteJsonOrHuman(
                        wantsJson,
                        jsonOptions,
                        "--max-json-bytes requires a positive integer.",
                        CommandExitCodes.InvalidArgument,
                        "pass a positive UTF-8 byte limit, for example `--max-json-bytes 16384`.",
                        usage: GetDoctorUsage());
                }
                maxJsonBytes = parsed;
                continue;
            }

            switch (arg)
            {
                case "--json":
                    json = true;
                    break;
                case "--redact-paths":
                    redactPaths = true;
                    break;
                case "--show-paths":
                    redactPaths = false;
                    break;
                case "--env-inventory":
                case "--env-inventory=compact":
                    envInventory = DoctorEnvironmentInventoryMode.Compact;
                    break;
                case "--env-inventory=full":
                    envInventory = DoctorEnvironmentInventoryMode.Full;
                    break;
                default:
                    return CommandErrorWriter.WriteJsonOrHuman(
                        wantsJson,
                        jsonOptions,
                        arg.StartsWith("--json=", StringComparison.Ordinal)
                            ? "doctor supports --json only; --json=<format> is not supported."
                            : $"Unknown doctor argument: {arg}",
                        CommandExitCodes.InvalidArgument,
                        $"use `{GetDoctorUsage()}`.");
            }
        }

        var filtersRequested = envDomain is not null || envCategory is not null || envSensitivity is not null;
        if (filtersRequested && envInventory != DoctorEnvironmentInventoryMode.Full)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                wantsJson,
                jsonOptions,
                "doctor environment inventory filters require --env-inventory=full.",
                CommandExitCodes.InvalidArgument,
                "add `--env-inventory=full` before filtering by domain, category, or sensitivity.",
                usage: GetDoctorUsage());
        }
        if (maxJsonBytes.HasValue && (!json || envInventory != DoctorEnvironmentInventoryMode.Full))
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                wantsJson,
                jsonOptions,
                "doctor --max-json-bytes requires --json and --env-inventory=full.",
                CommandExitCodes.InvalidArgument,
                "use `cdidx doctor --json --env-inventory=full --max-json-bytes <n>`.",
                usage: GetDoctorUsage());
        }

        if (!TryFilterDoctorEnvironmentInventory(
                envDomain,
                envCategory,
                envSensitivity,
                wantsJson,
                jsonOptions,
                out var filteredInventory,
                out var filterExitCode))
        {
            return filterExitCode;
        }

        if (json)
        {
            return WriteDoctorJson(
                appVersion,
                jsonOptions,
                redactPaths ?? true,
                envInventory == DoctorEnvironmentInventoryMode.Full,
                filteredInventory,
                maxJsonBytes);
        }

        if (envInventory == DoctorEnvironmentInventoryMode.Full)
        {
            WriteEnvironmentInventory(filteredInventory);
            return CommandExitCodes.Success;
        }

        if (envInventory == DoctorEnvironmentInventoryMode.Compact)
        {
            WriteEnvironmentInventorySummary();
            return CommandExitCodes.Success;
        }

        var dbResolution = DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null);
        Console.WriteLine("cdidx doctor");
        Console.WriteLine(ConsoleUi.FormatSummaryLine("version", appVersion));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("commit", ConsoleUi.LoadBuildMetadata().Commit));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("rid", RuntimeInformation.RuntimeIdentifier));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("os", RuntimeInformation.OSDescription));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("kernel", Environment.OSVersion.VersionString));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("dotnet", RuntimeInformation.FrameworkDescription));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("process", Environment.ProcessPath ?? "<unknown>"));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("base_dir", AppContext.BaseDirectory));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("cwd", Environment.CurrentDirectory));
        Console.WriteLine();
        Console.WriteLine("terminal:");
        Console.WriteLine(ConsoleUi.FormatSummaryLine("stdout_tty", !Console.IsOutputRedirected, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("stderr_tty", !Console.IsErrorRedirected, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("columns", FormatDoctorEnvironmentValue(CdidxEnvironment.GetProcessEnvironmentVariable("COLUMNS")), indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("no_color", FormatDoctorEnvironmentValue(CdidxEnvironment.GetProcessEnvironmentVariable("NO_COLOR")), indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("term", FormatDoctorEnvironmentValue(CdidxEnvironment.GetProcessEnvironmentVariable("TERM")), indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("locale", CultureInfo.CurrentCulture.Name, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("ui_locale", CultureInfo.CurrentUICulture.Name, indent: "  "));
        Console.WriteLine();
        var display = BuildDoctorDisplayJson();
        Console.WriteLine("display:");
        Console.WriteLine(ConsoleUi.FormatSummaryLine("color", display.Color.Enabled, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("color_source", display.Color.Source, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("terminal_hint", display.TerminalHint.HasHint, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("progress", display.Progress.Enabled, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("progress_source", display.Progress.Source, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("max_line_width", display.MaxLineWidth.Value, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("max_line_width_source", display.MaxLineWidth.Source, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("ambiguous_width", display.AmbiguousWidth.Wide, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("ambiguous_locale", display.AmbiguousWidth.Locale, indent: "  "));
        Console.WriteLine();
        Console.WriteLine("paths:");
        Console.WriteLine(ConsoleUi.FormatSummaryLine("db", dbResolution.DbPath, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("data_dir", dbResolution.DataDir ?? "<explicit-db>", indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("data_source", dbResolution.DataDirSource ?? "explicit-db", indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("log_dir", GlobalToolLog.ResolveLogDirectoryForStatus(), indent: "  "));
        Console.WriteLine();
        Console.WriteLine("config:");
        Console.WriteLine(ConsoleUi.FormatSummaryLine(CdidxConfigFile.FileName, File.Exists(Path.Combine(Environment.CurrentDirectory, CdidxConfigFile.FileName)) ? "present" : "not found", indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine(CdidxConfigFile.DisableEnvVar, FormatDoctorEnvironmentValue(CdidxEnvironment.GetProcessEnvironmentVariable(CdidxConfigFile.DisableEnvVar)), indent: "  "));
        Console.WriteLine();
        Console.WriteLine("github:");
        Console.WriteLine(ConsoleUi.FormatSummaryLine("proxy_default_credentials", GitHubHttpClientFactory.FormatProxyDefaultCredentialsStatus(), indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("max_request_timeout_s", GitHubHttpClientFactory.MaxRequestTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture), indent: "  "));
        Console.WriteLine();
        Console.WriteLine("cdidx_env:");
        foreach (var (key, value) in EnumerateCdidxEnvironment())
            Console.WriteLine(ConsoleUi.FormatSummaryLine(key, value, indent: "  "));
        return CommandExitCodes.Success;
    }

    private enum DoctorEnvironmentInventoryMode
    {
        None,
        Compact,
        Full,
    }

    private static string GetDoctorUsage()
        => "cdidx doctor [--json] [--redact-paths|--show-paths] [--env-inventory[=compact|full]] [--env-domain <domain>] [--env-category <category>] [--env-sensitivity <sensitivity>] [--max-json-bytes <n>]";

    private static bool TryReadDoctorValueOption(
        string[] args,
        ref int index,
        string arg,
        string flag,
        bool wantsJson,
        JsonSerializerOptions jsonOptions,
        out string? value,
        out int? exitCode)
    {
        value = null;
        exitCode = null;
        if (arg != flag && !arg.StartsWith(flag + "=", StringComparison.Ordinal))
            return false;

        if (!TryConsumeInlineOrNext(args, ref index, arg, flag, out var parsed)
            || string.IsNullOrWhiteSpace(parsed))
        {
            exitCode = CommandErrorWriter.WriteJsonOrHuman(
                wantsJson,
                jsonOptions,
                $"{flag} requires a non-empty value.",
                CommandExitCodes.InvalidArgument,
                $"pass one value reported by `cdidx doctor --env-inventory` for {flag}.",
                usage: GetDoctorUsage());
            return true;
        }

        value = parsed;
        return true;
    }

    private static bool TryFilterDoctorEnvironmentInventory(
        string? domain,
        string? category,
        string? sensitivity,
        bool wantsJson,
        JsonSerializerOptions jsonOptions,
        out IReadOnlyList<EnvironmentVariableInventoryItem> filtered,
        out int exitCode)
    {
        filtered = [];
        exitCode = CommandExitCodes.Success;
        foreach (var (flag, value, selector) in new (string Flag, string? Value, Func<EnvironmentVariableInventoryItem, string> Selector)[]
                 {
                     ("--env-domain", domain, static item => item.Domain),
                     ("--env-category", category, static item => item.Category),
                     ("--env-sensitivity", sensitivity, static item => item.Sensitivity),
                 })
        {
            if (value is null)
                continue;
            if (EnvironmentVariableInventory.Items.Any(item => string.Equals(selector(item), value, StringComparison.OrdinalIgnoreCase)))
                continue;

            var allowed = string.Join(
                ", ",
                EnvironmentVariableInventory.Items
                    .Select(selector)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static candidate => candidate, StringComparer.Ordinal));
            exitCode = CommandErrorWriter.WriteJsonOrHuman(
                wantsJson,
                jsonOptions,
                $"Unknown {flag} value: {value}",
                CommandExitCodes.InvalidArgument,
                $"choose one of: {allowed}.",
                usage: GetDoctorUsage());
            return false;
        }

        filtered = EnvironmentVariableInventory.Items
            .Where(item => domain is null || string.Equals(item.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Where(item => category is null || string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(item => sensitivity is null || string.Equals(item.Sensitivity, sensitivity, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static item => item.Name, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private static int WriteDoctorJson(
        string appVersion,
        JsonSerializerOptions jsonOptions,
        bool redactPaths,
        bool includeFullEnvironmentInventory,
        IReadOnlyList<EnvironmentVariableInventoryItem> environmentInventory,
        int? maxJsonBytes)
    {
        var dbResolution = DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null);
        var build = ConsoleUi.LoadBuildMetadata();
        var payload = new DoctorJsonResult(
            ApiVersion: "1",
            Version: appVersion,
            Commit: build.Commit,
            Rid: RuntimeInformation.RuntimeIdentifier,
            Os: RuntimeInformation.OSDescription,
            Kernel: Environment.OSVersion.VersionString,
            Dotnet: RuntimeInformation.FrameworkDescription,
            Process: RedactDoctorPath(Environment.ProcessPath ?? "<unknown>", redactPaths),
            BaseDir: RedactDoctorPath(AppContext.BaseDirectory, redactPaths),
            Cwd: RedactDoctorPath(Environment.CurrentDirectory, redactPaths),
            Terminal: new DoctorTerminalJsonResult(
                StdoutTty: !Console.IsOutputRedirected,
                StderrTty: !Console.IsErrorRedirected,
                Columns: FormatDoctorJsonEnvironmentValue("COLUMNS", redactPaths),
                NoColor: FormatDoctorJsonEnvironmentValue("NO_COLOR", redactPaths),
                Term: FormatDoctorJsonEnvironmentValue("TERM", redactPaths),
                Locale: CultureInfo.CurrentCulture.Name,
                UiLocale: CultureInfo.CurrentUICulture.Name),
            Display: BuildDoctorDisplayJson(),
            Paths: new DoctorPathsJsonResult(
                Db: RedactDoctorPath(dbResolution.DbPath, redactPaths),
                DataDir: RedactDoctorPath(dbResolution.DataDir ?? "<explicit-db>", redactPaths),
                DataSource: dbResolution.DataDirSource ?? "explicit-db",
                LogDir: RedactDoctorPath(GlobalToolLog.ResolveLogDirectoryForStatus(), redactPaths)),
            Config: new DoctorConfigJsonResult(
                DotCdidxrcJson: File.Exists(Path.Combine(Environment.CurrentDirectory, CdidxConfigFile.FileName)) ? "present" : "not_found",
                DisableConfigFile: FormatDoctorJsonEnvironmentValue(CdidxConfigFile.DisableEnvVar, redactPaths)),
            CdidxEnv: EnumerateCdidxEnvironmentJson(redactPaths).ToArray(),
            EnvironmentInventorySummary: includeFullEnvironmentInventory
                ? EnvironmentVariableInventory.BuildSummary(environmentInventory)
                : EnvironmentVariableInventory.BuildSummary(),
            EnvironmentInventory: includeFullEnvironmentInventory ? environmentInventory : null,
            Redaction: new DoctorRedactionJsonResult(
                PathsRedacted: redactPaths,
                SecretsRedacted: true));

        var json = JsonSerializer.Serialize(payload, CliJsonSerializerContextFactory.Create(jsonOptions).DoctorJsonResult);
        var byteCount = Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Environment.NewLine);
        if (maxJsonBytes.HasValue && byteCount > maxJsonBytes.Value)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                true,
                jsonOptions,
                $"doctor JSON output is {byteCount.ToString(CultureInfo.InvariantCulture)} bytes and exceeds --max-json-bytes {maxJsonBytes.Value.ToString(CultureInfo.InvariantCulture)}.",
                CommandExitCodes.UsageError,
                "increase --max-json-bytes or narrow the full environment inventory with --env-domain, --env-category, or --env-sensitivity.",
                usage: GetDoctorUsage());
        }

        Console.WriteLine(json);
        return CommandExitCodes.Success;
    }

    private static DoctorDisplayJsonResult BuildDoctorDisplayJson()
    {
        var maxLineWidth = EnvironmentOptionParser.ReadInt32(
            QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable,
            LineWidthFormatter.DefaultMaxLineWidth,
            minimum: 0,
            maximum: LineWidthFormatter.MaxAllowedLineWidth);

        return new DoctorDisplayJsonResult(
            Color: BuildDoctorColorDecision(),
            Progress: BuildDoctorProgressDecision(),
            TerminalHint: BuildDoctorTerminalHint(),
            MaxLineWidth: new DoctorDisplayMaxLineWidthJsonResult(
                maxLineWidth.Value,
                maxLineWidth.SourceKind,
                maxLineWidth.Source,
                maxLineWidth.Status,
                maxLineWidth.UsedFallback,
                maxLineWidth.Fallback,
                maxLineWidth.Minimum,
                maxLineWidth.Maximum,
                maxLineWidth.Name,
                maxLineWidth.RawValue is null ? "<unset>" : ConsoleUi.FormatBoundedValue(maxLineWidth.RawValue)),
            AmbiguousWidth: BuildDoctorAmbiguousWidthDecision(),
            Truncation: new DoctorDisplayTruncationJsonResult(
                LineWidthFormatter.DefaultMaxLineWidth,
                LineWidthFormatter.MaxAllowedLineWidth,
                ConsoleUi.DefaultDiagnosticValueCharLimit,
                "... <truncated; original length N chars>"));
    }

    private static DoctorDisplayDecisionJsonResult BuildDoctorColorDecision()
    {
        var enabled = ConsoleUi.ShouldUseColor();
        return ConsoleUi.GetColorModeForDiagnostics() switch
        {
            ColorMode.Always => new DoctorDisplayDecisionJsonResult(enabled, "flag", "--color=always"),
            ColorMode.Never => new DoctorDisplayDecisionJsonResult(enabled, "flag", "--color=never"),
            _ when IsDoctorForceColorRequested() => new DoctorDisplayDecisionJsonResult(enabled, "CLICOLOR_FORCE", "forced"),
            _ when !string.IsNullOrEmpty(CdidxEnvironment.GetEnvironmentVariable("NO_COLOR")) => new DoctorDisplayDecisionJsonResult(enabled, "NO_COLOR", "disabled"),
            _ when CdidxEnvironment.GetEnvironmentVariable("CLICOLOR") == "0" => new DoctorDisplayDecisionJsonResult(enabled, "CLICOLOR", "disabled"),
            _ => new DoctorDisplayDecisionJsonResult(enabled, "terminal", enabled ? "ansi_available" : "not_interactive")
        };
    }

    private static DoctorDisplayDecisionJsonResult BuildDoctorProgressDecision()
    {
        var enabled = ConsoleUi.ShouldUseProgressAnimation();
        var progressOverride = ConsoleUi.GetProgressAnimationOverrideForDiagnostics();
        if (progressOverride.HasValue)
            return new DoctorDisplayDecisionJsonResult(enabled, "flag", progressOverride.Value ? "enabled_override" : "--no-progress");
        if (IsTruthyDoctorEnvironmentValue(CdidxEnvironment.GetEnvironmentVariable(ConsoleUi.DisableProgressEnvironmentVariable)))
            return new DoctorDisplayDecisionJsonResult(enabled, ConsoleUi.DisableProgressEnvironmentVariable, "disabled");
        if (IsTruthyDoctorEnvironmentValue(CdidxEnvironment.GetEnvironmentVariable(ConsoleUi.PrefersReducedMotionEnvironmentVariable)))
            return new DoctorDisplayDecisionJsonResult(enabled, ConsoleUi.PrefersReducedMotionEnvironmentVariable, "reduced_motion");
        return new DoctorDisplayDecisionJsonResult(enabled, "default", "enabled");
    }

    private static DoctorDisplayTerminalHintJsonResult BuildDoctorTerminalHint()
    {
        var wtSession = FormatDoctorJsonEnvironmentValue("WT_SESSION", redactPaths: false);
        var wtProfile = FormatDoctorJsonEnvironmentValue("WT_PROFILE_ID", redactPaths: false);
        return new DoctorDisplayTerminalHintJsonResult(
            HasDoctorTerminalEnvironmentHint(),
            IsDoctorTerminalEnvironmentDisabled(),
            Console.IsOutputRedirected,
            Console.Out is StringWriter,
            FormatDoctorJsonEnvironmentValue("TERM", redactPaths: false),
            FormatDoctorJsonEnvironmentValue("TERM_PROGRAM", redactPaths: false),
            FormatDoctorJsonEnvironmentValue("CI", redactPaths: false),
            wtSession != "<unset>" ? wtSession : wtProfile);
    }

    private static DoctorDisplayAmbiguousWidthJsonResult BuildDoctorAmbiguousWidthDecision()
    {
        var locale = CdidxEnvironment.GetEnvironmentVariable("LC_ALL");
        var source = "LC_ALL";
        if (string.IsNullOrEmpty(locale))
        {
            locale = CdidxEnvironment.GetEnvironmentVariable("LC_CTYPE");
            source = "LC_CTYPE";
        }
        if (string.IsNullOrEmpty(locale))
        {
            locale = CdidxEnvironment.GetEnvironmentVariable("LANG");
            source = "LANG";
        }
        if (string.IsNullOrEmpty(locale))
        {
            locale = "<unset>";
            source = "default";
        }

        var wide = locale.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                   || locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                   || locale.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
        return new DoctorDisplayAmbiguousWidthJsonResult(wide, source, ConsoleUi.FormatBoundedValue(locale));
    }

    private static bool HasDoctorTerminalEnvironmentHint()
    {
        if (!string.IsNullOrEmpty(CdidxEnvironment.GetEnvironmentVariable("WT_SESSION")))
            return true;
        if (!string.IsNullOrEmpty(CdidxEnvironment.GetEnvironmentVariable("WT_PROFILE_ID")))
            return true;
        if (!string.IsNullOrEmpty(CdidxEnvironment.GetEnvironmentVariable("TERM_PROGRAM")))
            return true;

        var term = CdidxEnvironment.GetEnvironmentVariable("TERM");
        return !string.IsNullOrWhiteSpace(term)
               && !term.Equals("dumb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDoctorTerminalEnvironmentDisabled()
        => string.Equals(CdidxEnvironment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase)
           || IsDoctorCiEnvironment();

    private static bool IsDoctorCiEnvironment()
    {
        var ci = CdidxEnvironment.GetEnvironmentVariable("CI");
        return !string.IsNullOrEmpty(ci)
               && !ci.Equals("0", StringComparison.OrdinalIgnoreCase)
               && !ci.Equals("false", StringComparison.OrdinalIgnoreCase)
               && !ci.Equals("no", StringComparison.OrdinalIgnoreCase)
               && !ci.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDoctorForceColorRequested()
    {
        var force = CdidxEnvironment.GetEnvironmentVariable("CLICOLOR_FORCE");
        return !string.IsNullOrEmpty(force) && force != "0";
    }

    private static bool IsTruthyDoctorEnvironmentValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Trim() is not ("0" or "false" or "False" or "FALSE" or "no" or "No" or "NO");
    }

    private static void WriteEnvironmentInventory(IReadOnlyList<EnvironmentVariableInventoryItem> items)
    {
        Console.WriteLine("environment_inventory:");
        foreach (var item in items)
        {
            var firstLocation = item.Locations.FirstOrDefault();
            var location = firstLocation is null
                ? "<unknown>"
                : $"{firstLocation.Path}:{firstLocation.Line}";
            Console.WriteLine($"  {item.Name}");
            Console.WriteLine(ConsoleUi.FormatSummaryLine("domain", item.Domain, indent: "    "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("category", item.Category, indent: "    "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("sensitivity", item.Sensitivity, indent: "    "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("policy", item.Policy, indent: "    "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("default", item.DefaultBehavior, indent: "    "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("config", item.ConfigFileSupported, indent: "    "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("invalid", item.InvalidValueBehavior, indent: "    "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("location", location, indent: "    "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("description", item.Description, indent: "    "));
        }
    }

    private static void WriteEnvironmentInventorySummary()
    {
        var summary = EnvironmentVariableInventory.BuildSummary();
        Console.WriteLine("environment_inventory_summary:");
        Console.WriteLine(ConsoleUi.FormatSummaryLine("total", summary.Total, indent: "  "));
        WriteEnvironmentInventorySummaryBuckets("by_domain", summary.ByDomain);
        WriteEnvironmentInventorySummaryBuckets("by_sensitivity", summary.BySensitivity);
        WriteEnvironmentInventorySummaryBuckets("by_category", summary.ByCategory);
        Console.WriteLine(ConsoleUi.FormatSummaryLine("full_detail", "cdidx doctor --env-inventory=full", indent: "  "));
    }

    private static void WriteEnvironmentInventorySummaryBuckets(
        string title,
        IReadOnlyList<EnvironmentVariableInventorySummaryBucketJsonResult> buckets)
    {
        Console.WriteLine($"  {title}:");
        foreach (var bucket in buckets)
            Console.WriteLine(ConsoleUi.FormatSummaryLine(bucket.Name, bucket.Count, indent: "    "));
    }

    private static IEnumerable<DoctorEnvironmentVariableJsonResult> EnumerateCdidxEnvironmentJson(bool redactPaths)
    {
        var rows = CdidxEnvironment.EnumerateProcessEnvironmentVariables()
            .Where(e => e.Key.StartsWith("CDIDX_", StringComparison.Ordinal))
            .OrderBy(e => e.Key, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var sensitive = IsSensitiveEnvironmentName(row.Key);
            var value = sensitive
                ? "<redacted>"
                : string.IsNullOrEmpty(row.Value)
                    ? "<empty>"
                    : RedactDoctorPath(row.Value, redactPaths);
            var bounded = ConsoleUi.BoundDisplayText(value);
            yield return new DoctorEnvironmentVariableJsonResult(row.Key, bounded.Text, sensitive, bounded.Truncated, bounded.OriginalLength);
        }
    }

    private static string FormatDoctorJsonEnvironmentValue(string name, bool redactPaths)
    {
        var value = CdidxEnvironment.GetProcessEnvironmentVariable(name);
        return value == null ? "<unset>" : ConsoleUi.FormatBoundedValue(RedactDoctorPath(value, redactPaths));
    }

    private static string RedactDoctorPath(string value, bool redactPaths)
        => redactPaths ? DiagnosticRedactor.RedactSensitiveText(value, "[redacted]", redactPaths: true) : value;

    private static IEnumerable<(string Key, string Value)> EnumerateCdidxEnvironment()
    {
        var rows = CdidxEnvironment.EnumerateProcessEnvironmentVariables()
            .Where(e => e.Key.StartsWith("CDIDX_", StringComparison.Ordinal))
            .OrderBy(e => e.Key, StringComparer.Ordinal);
        var any = false;
        foreach (var row in rows)
        {
            any = true;
            yield return (row.Key, IsSensitiveEnvironmentName(row.Key) ? "<redacted>" : string.IsNullOrEmpty(row.Value) ? "<empty>" : ConsoleUi.FormatBoundedValue(row.Value));
        }

        if (!any)
            yield return ("<none>", "");
    }

    private static string FormatDoctorEnvironmentValue(string? value)
        => value == null ? "<unset>" : ConsoleUi.FormatBoundedValue(value);

    private static bool IsSensitiveEnvironmentName(string name) =>
        name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PASSWD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PWD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
        || name.Contains("AUTH", StringComparison.OrdinalIgnoreCase)
        || name.Contains("APIKEY", StringComparison.OrdinalIgnoreCase)
        || name.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PRIVATE_KEY", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase);
}
