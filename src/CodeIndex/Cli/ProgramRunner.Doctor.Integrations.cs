using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Mcp;

namespace CodeIndex.Cli;

internal static partial class ProgramRunner
{
    private const int DoctorIntegrationMemberLimit = 20;
    private const int DoctorIntegrationDiagnosticLimit = 20;
    private const int DoctorIntegrationConfigurationCandidateLimit = 128;

    private static int RunDoctorIntegrations(
        string appVersion,
        JsonSerializerOptions jsonOptions,
        bool json,
        bool redactPaths,
        bool check,
        int? maxJsonBytes)
    {
        var cwd = Path.GetFullPath(Environment.CurrentDirectory);
        var dbResolution = DbPathResolver.ResolveForQuery(cwd, explicitDbPath: null, explicitDataDir: null);
        var workspace = BuildDoctorIntegrationWorkspace(cwd, redactPaths, out var workspaceRoot, out var workspaceScopeWarning);
        var project = BuildDoctorIntegrationProject(
            workspaceRoot,
            workspace,
            workspaceScopeWarning,
            dbResolution,
            redactPaths,
            out var resolvedProjectRoot);
        var hook = BuildDoctorIntegrationHook(cwd, appVersion, redactPaths);
        var mcp = BuildDoctorIntegrationMcp();
        var lsp = BuildDoctorIntegrationLsp(project, dbResolution, redactPaths);
        var watch = BuildDoctorIntegrationWatch();
        var extensions = BuildDoctorIntegrationExtensions(resolvedProjectRoot);
        var overall = ResolveDoctorIntegrationOverallStatus(
            project.Status,
            hook.Status,
            mcp.Status,
            lsp.Status,
            watch.Status,
            extensions.Status);
        var payload = new DoctorIntegrationsJsonResult(
            ApiVersion: JsonOutputContract.ApiVersion,
            SchemaVersion: "1",
            Status: overall,
            Check: check,
            Project: project,
            Hook: hook,
            Mcp: mcp,
            Lsp: lsp,
            Watch: watch,
            Extensions: extensions,
            Redaction: new DoctorRedactionJsonResult(redactPaths, SecretsRedacted: true));

        if (json)
        {
            var serialized = JsonSerializer.Serialize(
                payload,
                CliJsonSerializerContextFactory.Create(jsonOptions).DoctorIntegrationsJsonResult);
            var byteCount = Encoding.UTF8.GetByteCount(serialized) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (maxJsonBytes.HasValue && byteCount > maxJsonBytes.Value)
            {
                return CommandErrorWriter.WriteJsonOrHuman(
                    true,
                    jsonOptions,
                    $"doctor integration JSON output is {byteCount.ToString(CultureInfo.InvariantCulture)} bytes and exceeds --max-json-bytes {maxJsonBytes.Value.ToString(CultureInfo.InvariantCulture)}.",
                    CommandExitCodes.UsageError,
                    "increase --max-json-bytes; integration arrays and diagnostics are already bounded.",
                    usage: GetDoctorUsage(),
                    errorCode: CommandErrorCodes.UsageError,
                    command: "doctor");
            }

            Console.WriteLine(serialized);
        }
        else
        {
            WriteDoctorIntegrationText(payload);
        }

        return check && overall is "warning" or "error"
            ? CommandExitCodes.StaleIndex
            : CommandExitCodes.Success;
    }

    private static DoctorIntegrationWorkspaceJsonResult BuildDoctorIntegrationWorkspace(
        string cwd,
        bool redactPaths,
        out string workspaceRoot,
        out bool scopeWarning)
    {
        scopeWarning = false;
        try
        {
            var manifest = WorkspaceManifestLoader.Find(cwd);
            if (manifest == null)
            {
                workspaceRoot = cwd;
                return new DoctorIntegrationWorkspaceJsonResult(
                    "not_applicable",
                    null,
                    null,
                    null,
                    0,
                    [],
                    0,
                    false);
            }

            workspaceRoot = manifest.Root;
            var members = manifest.Members
                .Take(DoctorIntegrationMemberLimit)
                .Select(member => RedactDoctorPath(member.Path, redactPaths))
                .ToArray();
            var isMember = string.Equals(manifest.IndexStrategy, "single", StringComparison.OrdinalIgnoreCase)
                ? PathCasing.IsPathEqualOrParent(manifest.Root, cwd)
                : manifest.Members.Any(member => PathCasing.IsPathEqualOrParent(member.Path, cwd));
            scopeWarning = manifest.Members.Count > 0 && !isMember;
            var omitted = Math.Max(0, manifest.Members.Count - members.Length);
            return new DoctorIntegrationWorkspaceJsonResult(
                scopeWarning ? "warning" : "ready",
                RedactDoctorPath(manifest.Path, redactPaths),
                RedactDoctorPath(manifest.Root, redactPaths),
                manifest.IndexStrategy,
                manifest.Members.Count,
                members,
                omitted,
                omitted > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or NotSupportedException)
        {
            workspaceRoot = cwd;
            scopeWarning = true;
            return new DoctorIntegrationWorkspaceJsonResult(
                "warning",
                null,
                null,
                null,
                0,
                [RedactDoctorPath(CommandErrorWriter.FormatSanitizedException(ex), redactPaths)],
                0,
                false);
        }
    }

    private static DoctorIntegrationProjectJsonResult BuildDoctorIntegrationProject(
        string workspaceRoot,
        DoctorIntegrationWorkspaceJsonResult workspace,
        bool workspaceScopeWarning,
        DbPathResolution dbResolution,
        bool redactPaths,
        out string resolvedProjectRoot)
    {
        resolvedProjectRoot = workspaceRoot;
        var displayRoot = RedactDoctorPath(workspaceRoot, redactPaths);
        var displayDb = RedactDoctorPath(dbResolution.DbPath, redactPaths);
        var dbSource = dbResolution.DataDirSource ?? "explicit_db";
        if (!File.Exists(LongPath.EnsureWindowsPrefix(dbResolution.DbPath)))
        {
            return new DoctorIntegrationProjectJsonResult(
                "error",
                "database_missing",
                displayRoot,
                displayDb,
                dbSource,
                false,
                null,
                null,
                [],
                0,
                false,
                null,
                null,
                null,
                null,
                "database_missing",
                workspace,
                ["cdidx index ."]);
        }

        try
        {
            using var configurationScope = ExtractorPluginRegistry.BeginAuthorizedConfigurationScope();
            using var db = new DbContext(DbOpenIntent.QueryOnly, dbResolution.DbPath);
            if (!db.TryValidateIsCodeIndexDb(out _))
            {
                return new DoctorIntegrationProjectJsonResult(
                    "error",
                    "schema_incompatible",
                    displayRoot,
                    displayDb,
                    dbSource,
                    true,
                    false,
                    null,
                    [],
                    0,
                    false,
                    null,
                    null,
                    null,
                    null,
                    "schema_incompatible",
                    workspace,
                    ["cdidx index . --rebuild --yes"]);
            }

            var reader = new DbReader(db);
            var status = reader.GetStatus(includeDatabaseSizeAttribution: false);
            var projectRoot = string.IsNullOrWhiteSpace(status.ProjectRoot) ? workspaceRoot : status.ProjectRoot;
            resolvedProjectRoot = projectRoot;
            var repositoryRoot = GitHelper.TryFindWorktreeRootWithoutProcess(projectRoot);
            var workspaceHeadCommit = repositoryRoot == null
                ? null
                : GitHelper.TryReadHeadCommitWithoutProcess(projectRoot);
            var skipWorktreePaths = GitHelper.TryReadSkipWorktreePathsWithoutProcess(projectRoot);
            var freshness = IndexFreshnessChecker.Check(
                reader,
                projectRoot,
                internalIndexDatabasePath: DbPathResolver.NormalizeDbPath(dbResolution.DbPath),
                allowGitCommands: false,
                knownSkipWorktreePaths: skipWorktreePaths,
                knownSkipWorktreePathsComplete: repositoryRoot == null || skipWorktreePaths != null,
                knownWorkspaceHeadCommit: workspaceHeadCommit,
                knownRepositoryRoot: repositoryRoot);
            var matches = freshness.Checked && freshness.MatchesWorkspace;
            var persistedReadinessWarning = !status.IndexComplete
                                            || status.MigrationInProgress
                                            || status.IndexNewerThanReader;
            var resultStatus = !freshness.Checked || workspaceScopeWarning || persistedReadinessWarning
                ? "warning"
                : matches
                    ? "ready"
                    : "warning";
            var reason = workspaceScopeWarning
                ? "workspace_scope_mismatch"
                : status.MigrationInProgress
                    ? "migration_in_progress"
                    : status.IndexNewerThanReader
                        ? "index_newer_than_reader"
                        : !status.IndexComplete
                            ? "index_incomplete"
                            : !freshness.Checked
                                ? freshness.Reason ?? "freshness_unavailable"
                                : matches
                                    ? "index_fresh"
                                    : freshness.Reason ?? "index_stale";
            var incompleteReasons = (status.IndexIncompleteReasons ?? [])
                .Take(DoctorIntegrationDiagnosticLimit)
                .ToArray();
            var incompleteReasonsOmitted = Math.Max(
                0,
                (status.IndexIncompleteReasons?.Count ?? 0) - incompleteReasons.Length);
            return new DoctorIntegrationProjectJsonResult(
                resultStatus,
                reason,
                RedactDoctorPath(projectRoot, redactPaths),
                displayDb,
                dbSource,
                true,
                true,
                status.IndexComplete,
                incompleteReasons,
                incompleteReasonsOmitted,
                incompleteReasonsOmitted > 0,
                status.MigrationInProgress,
                status.IndexNewerThanReader,
                status.IndexedAt,
                freshness.Checked ? freshness.MatchesWorkspace : null,
                freshness.Reason,
                workspace,
                matches && !workspaceScopeWarning && !persistedReadinessWarning
                    ? []
                    : ["cdidx status --check", "cdidx status --json", "cdidx index ."]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            return new DoctorIntegrationProjectJsonResult(
                "error",
                "database_unreadable",
                displayRoot,
                displayDb,
                dbSource,
                true,
                false,
                null,
                [],
                0,
                false,
                null,
                null,
                null,
                null,
                RedactDoctorPath(CommandErrorWriter.FormatSanitizedException(ex), redactPaths),
                workspace,
                ["cdidx status --check", "cdidx index . --rebuild --yes"]);
        }
    }

    private static DoctorIntegrationHookJsonResult BuildDoctorIntegrationHook(
        string projectRoot,
        string appVersion,
        bool redactPaths)
    {
        try
        {
            var snapshot = HookCommandRunner.CaptureDoctorSnapshot(projectRoot, appVersion);
            if (snapshot == null)
            {
                return new DoctorIntegrationHookJsonResult(
                    "not_applicable",
                    "not_a_git_repository",
                    "none",
                    "none",
                    "absent",
                    null,
                    null,
                    RedactDoctorPath(projectRoot, redactPaths),
                    null,
                    []);
            }

            var status = snapshot.HookStatus switch
            {
                "installed" when snapshot.ManagedState == "managed" && snapshot.ExecutableStatus is null or "available" => "ready",
                "absent" => "not_configured",
                _ => "warning",
            };
            var reason = ResolveDoctorIntegrationHookReason(status, snapshot);
            return new DoctorIntegrationHookJsonResult(
                status,
                reason,
                snapshot.RepositoryType,
                snapshot.TargetScope,
                snapshot.HookStatus,
                snapshot.ManagedState,
                snapshot.ExecutableStatus,
                RedactDoctorPath(snapshot.WorktreeRoot, redactPaths),
                RedactDoctorPath(snapshot.HookPath, redactPaths),
                status == "ready" ? [] : ["cdidx hooks status", "cdidx hooks install"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new DoctorIntegrationHookJsonResult(
                "warning",
                "hook_status_unavailable",
                "unknown",
                "unknown",
                "unknown",
                null,
                null,
                RedactDoctorPath(projectRoot, redactPaths),
                null,
                ["cdidx hooks status", RedactDoctorPath(CommandErrorWriter.FormatSanitizedException(ex), redactPaths)]);
        }
    }

    internal static string ResolveDoctorIntegrationHookReason(
        string status,
        HookDoctorSnapshot snapshot)
        => status switch
        {
            "ready" => "managed_hook_current",
            "not_configured" => "hook_absent",
            _ when snapshot.HookStatus == "custom" => "custom_hook_present",
            _ when snapshot.ExecutableStatus is not null and not "available" => snapshot.ExecutableStatus,
            _ => snapshot.ManagedState ?? "hook_requires_attention",
        };

    private static DoctorIntegrationMcpJsonResult BuildDoctorIntegrationMcp()
    {
        var stdioToken = ReadDoctorMcpToken(McpAuthenticatorFactory.AuthTokenEnvVar);
        var httpToken = ReadDoctorMcpToken("CDIDX_MCP_HTTP_TOKEN", McpAuthenticatorFactory.AuthTokenEnvVar);
        var invalid = !stdioToken.Valid || !httpToken.Valid;
        return new DoctorIntegrationMcpJsonResult(
            invalid ? "error" : "ready",
            invalid ? "invalid_auth_configuration" : "stdio_ready_http_optional",
            [
                new DoctorIntegrationMcpTransportJsonResult(
                    stdioToken.Valid ? "ready" : "error",
                    !stdioToken.Valid ? "invalid_auth_token" : stdioToken.Configured ? "auth_configured" : "local_stdio",
                    "stdio",
                    stdioToken.Configured,
                    stdioToken.Source,
                    "not_configured",
                    "cli_option",
                    ["cdidx mcp"]),
                new DoctorIntegrationMcpTransportJsonResult(
                    !httpToken.Valid ? "error" : httpToken.Configured ? "ready" : "not_configured",
                    !httpToken.Valid ? "invalid_auth_token" : httpToken.Configured ? "auth_configured" : "auth_not_configured",
                    "http",
                    httpToken.Configured,
                    httpToken.Source,
                    "not_configured",
                    "cli_option",
                    ["set CDIDX_MCP_HTTP_TOKEN", "cdidx mcp --transport http"]),
            ]);
    }

    private static DoctorMcpTokenReadiness ReadDoctorMcpToken(params string[] environmentVariables)
    {
        foreach (var environmentVariable in environmentVariables)
        {
            var raw = CdidxEnvironment.GetProcessEnvironmentVariable(environmentVariable);
            if (string.IsNullOrEmpty(raw))
                continue;
            try
            {
                _ = McpEnvironment.GetOptionalToken(environmentVariable);
                return new DoctorMcpTokenReadiness(true, true, environmentVariable);
            }
            catch (FormatException)
            {
                return new DoctorMcpTokenReadiness(true, false, environmentVariable);
            }
        }

        return new DoctorMcpTokenReadiness(false, true, null);
    }

    private readonly record struct DoctorMcpTokenReadiness(bool Configured, bool Valid, string? Source);

    private static DoctorIntegrationLspJsonResult BuildDoctorIntegrationLsp(
        DoctorIntegrationProjectJsonResult project,
        DbPathResolution dbResolution,
        bool redactPaths)
    {
        var ready = project.DatabaseExists && project.SchemaCompatible == true;
        return new DoctorIntegrationLspJsonResult(
            ready ? "ready" : "error",
            ready ? "database_resolved" : project.Reason,
            ready,
            RedactDoctorPath(dbResolution.DbPath, redactPaths),
            "cdidx lsp",
            ready ? [] : ["cdidx index .", "cdidx lsp"]);
    }

    private static DoctorIntegrationWatchJsonResult BuildDoctorIntegrationWatch()
    {
        var preferred = IndexWatchRunner.ResolveWatchBackendName();
        var platformSupported = OperatingSystem.IsMacOS()
                                || OperatingSystem.IsLinux()
                                || OperatingSystem.IsWindows();
        return new DoctorIntegrationWatchJsonResult(
            platformSupported ? "ready" : "warning",
            platformSupported ? "platform_backend_available" : "generic_filesystem_watcher",
            preferred,
            true,
            OperatingSystem.IsMacOS() ? "polling" : null,
            null,
            ["cdidx index . --watch"]);
    }

    private static DoctorIntegrationExtensionsJsonResult BuildDoctorIntegrationExtensions(string projectRoot)
    {
        var diagnostics = new List<string>();
        var pluginCandidates = CountDoctorIntegrationCandidates(
            Path.Combine(projectRoot, ".cdidx", "plugins"),
            ["*.dll"],
            diagnostics);
        var patternCandidates = CountDoctorIntegrationCandidates(
            Path.Combine(projectRoot, ".cdidx", "patterns"),
            ["*.yaml", "*.yml"],
            diagnostics);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            pluginCandidates += CountDoctorIntegrationCandidates(
                Path.Combine(home, ".cdidx", "plugins"),
                ["*.dll"],
                diagnostics);
            patternCandidates += CountDoctorIntegrationCandidates(
                Path.Combine(home, ".config", "cdidx", "patterns"),
                ["*.yaml", "*.yml"],
                diagnostics);
        }

        var runtime = ExtractorPluginRegistry.CaptureDoctorRuntimeSnapshot(projectRoot);
        var trusted = ExtractorPluginRegistry.WorkspacePluginsTrustedForDoctor();
        var configured = pluginCandidates + patternCandidates > 0;
        var loaded = runtime.LoadedPluginAssemblies + runtime.LoadedPatternConfigs > 0;
        var diagnosticCount = runtime.DiagnosticCount + diagnostics.Count;
        var status = diagnosticCount > 0
            ? "warning"
            : configured && !loaded
                ? "warning"
                : loaded
                    ? "ready"
                    : "not_configured";
        var reason = diagnosticCount > 0
            ? "configuration_diagnostics"
            : configured && !loaded
                ? "configured_not_loaded"
                : loaded
                    ? "extensions_loaded"
                    : "no_extension_configuration";
        var boundedDiagnostics = runtime.Diagnostics
            .Concat(diagnostics)
            .Select(static diagnostic => DiagnosticRedactor.RedactSensitiveText(
                diagnostic,
                "[redacted]",
                redactPaths: true))
            .Take(DoctorIntegrationDiagnosticLimit)
            .ToArray();
        var omitted = Math.Max(0, diagnosticCount - boundedDiagnostics.Length);
        return new DoctorIntegrationExtensionsJsonResult(
            status,
            reason,
            trusted,
            pluginCandidates,
            patternCandidates,
            runtime.LoadedPluginAssemblies,
            runtime.LoadedPatternConfigs,
            diagnosticCount,
            boundedDiagnostics,
            omitted,
            omitted > 0,
            status is "warning" ? ["cdidx status --json"] : []);
    }

    private static int CountDoctorIntegrationCandidates(
        string directory,
        IReadOnlyList<string> patterns,
        List<string> diagnostics)
    {
        if (!Directory.Exists(LongPath.EnsureWindowsPrefix(directory)))
            return 0;

        try
        {
            var count = 0;
            foreach (var pattern in patterns)
            {
                foreach (var _ in Directory.EnumerateFiles(
                             LongPath.EnsureWindowsPrefix(directory),
                             pattern,
                             SearchOption.TopDirectoryOnly))
                {
                    if (count >= DoctorIntegrationConfigurationCandidateLimit)
                    {
                        diagnostics.Add("configuration_candidate_limit_exceeded");
                        return count;
                    }
                    count++;
                }
            }
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add($"configuration_scan_failed:{ex.GetType().Name}");
            return 0;
        }
    }

    private static string ResolveDoctorIntegrationOverallStatus(params string[] statuses)
    {
        if (statuses.Contains("error", StringComparer.Ordinal))
            return "error";
        if (statuses.Contains("warning", StringComparer.Ordinal))
            return "warning";
        return "ready";
    }

    private static void WriteDoctorIntegrationText(DoctorIntegrationsJsonResult payload)
    {
        Console.WriteLine("cdidx integration readiness");
        Console.WriteLine(ConsoleUi.FormatSummaryLine("status", payload.Status));
        WriteSection("project", payload.Project.Status, payload.Project.Reason);
        WriteDetail("project_root", payload.Project.ProjectRoot);
        WriteDetail("database_path", payload.Project.DatabasePath);
        WriteDetail("database_source", payload.Project.DatabaseSource);
        WriteDetail("database_exists", FormatRequiredBool(payload.Project.DatabaseExists));
        WriteDetail("schema_compatible", FormatOptionalBool(payload.Project.SchemaCompatible));
        WriteDetail("index_complete", FormatOptionalBool(payload.Project.IndexComplete));
        WriteDetail("index_matches_workspace", FormatOptionalBool(payload.Project.IndexMatchesWorkspace));
        WriteDetail("freshness_reason", payload.Project.FreshnessReason);
        WriteDetail("workspace_status", payload.Project.Workspace.Status);
        WriteDetail("workspace_index_strategy", payload.Project.Workspace.IndexStrategy);
        WriteDetail("workspace_member_count", payload.Project.Workspace.MemberCount.ToString(CultureInfo.InvariantCulture));
        foreach (var reason in payload.Project.IndexIncompleteReasons)
            WriteDetail("index_incomplete_reason", reason);
        WriteRemediation(payload.Project.Remediation);

        WriteSection("hook", payload.Hook.Status, payload.Hook.Reason);
        WriteDetail("repository_type", payload.Hook.RepositoryType);
        WriteDetail("target_scope", payload.Hook.TargetScope);
        WriteDetail("hook_status", payload.Hook.HookStatus);
        WriteDetail("managed_state", payload.Hook.ManagedState);
        WriteDetail("executable_status", payload.Hook.ExecutableStatus);
        WriteDetail("current_worktree", payload.Hook.CurrentWorktree);
        WriteDetail("hook_path", payload.Hook.HookPath);
        WriteRemediation(payload.Hook.Remediation);

        WriteSection("mcp", payload.Mcp.Status, payload.Mcp.Reason);
        foreach (var transport in payload.Mcp.Transports)
        {
            var prefix = $"{transport.Transport}_";
            WriteDetail(prefix + "status", transport.Status);
            WriteDetail(prefix + "reason", transport.Reason);
            WriteDetail(prefix + "auth_configured", FormatRequiredBool(transport.AuthConfigured));
            WriteDetail(prefix + "auth_source", transport.AuthSource);
            WriteDetail(prefix + "audit_status", transport.AuditStatus);
            WriteDetail(prefix + "audit_source", transport.AuditSource);
            foreach (var command in transport.Remediation)
                WriteDetail(prefix + "remediation", command);
        }

        WriteSection("lsp", payload.Lsp.Status, payload.Lsp.Reason);
        WriteDetail("configured", FormatRequiredBool(payload.Lsp.Configured));
        WriteDetail("database_path", payload.Lsp.DatabasePath);
        WriteDetail("launch_command", payload.Lsp.LaunchCommand);
        WriteRemediation(payload.Lsp.Remediation);

        WriteSection("watch", payload.Watch.Status, payload.Watch.Reason);
        WriteDetail("preferred_backend", payload.Watch.PreferredBackend);
        WriteDetail("available", FormatRequiredBool(payload.Watch.Available));
        WriteDetail("fallback_backend", payload.Watch.FallbackBackend);
        WriteDetail("fallback_reason", payload.Watch.FallbackReason);
        WriteRemediation(payload.Watch.Remediation);

        WriteSection("extensions", payload.Extensions.Status, payload.Extensions.Reason);
        WriteDetail("workspace_plugins_trusted", FormatRequiredBool(payload.Extensions.WorkspacePluginsTrusted));
        WriteDetail("plugin_candidates", payload.Extensions.PluginCandidates.ToString(CultureInfo.InvariantCulture));
        WriteDetail("pattern_config_candidates", payload.Extensions.PatternConfigCandidates.ToString(CultureInfo.InvariantCulture));
        WriteDetail("loaded_plugin_assemblies", payload.Extensions.LoadedPluginAssemblies.ToString(CultureInfo.InvariantCulture));
        WriteDetail("loaded_pattern_configs", payload.Extensions.LoadedPatternConfigs.ToString(CultureInfo.InvariantCulture));
        WriteDetail("diagnostic_count", payload.Extensions.DiagnosticCount.ToString(CultureInfo.InvariantCulture));
        foreach (var diagnostic in payload.Extensions.Diagnostics)
            WriteDetail("diagnostic", diagnostic);
        WriteRemediation(payload.Extensions.Remediation);

        static void WriteSection(string name, string status, string reason)
        {
            Console.WriteLine();
            Console.WriteLine($"{name}:");
            Console.WriteLine(ConsoleUi.FormatSummaryLine("status", status, indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("reason", reason, indent: "  "));
        }

        static void WriteDetail(string name, string? value)
        {
            if (value != null)
                Console.WriteLine(ConsoleUi.FormatSummaryLine(name, value, indent: "  "));
        }

        static void WriteRemediation(IReadOnlyList<string> remediation)
        {
            foreach (var command in remediation)
                Console.WriteLine(ConsoleUi.FormatSummaryLine("remediation", command, indent: "  "));
        }

        static string FormatRequiredBool(bool value) => value ? "true" : "false";
        static string? FormatOptionalBool(bool? value)
            => value.HasValue ? FormatRequiredBool(value.Value) : null;
    }
}
