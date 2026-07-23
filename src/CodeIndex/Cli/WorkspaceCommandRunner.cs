using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static class WorkspaceCommandRunner
{
    private const int MaxAmbiguousMemberCandidates = 5;
    private const int MaxAmbiguousMemberPathChars = 160;
    internal const int MaxMemberHealthDatabaseProbes = 64;

    internal static int Run(string[] args, JsonSerializerOptions jsonOptions)
    {
        var json = args.Contains("--json", StringComparer.Ordinal);
        args = args.Where(a => a != "--json").ToArray();
        if (args.Length == 0)
            return List(json, jsonOptions);

        return args[0] switch
        {
            "list" => List(json, jsonOptions),
            "status" => List(json, jsonOptions, includeActiveWorkspaceStatus: true),
            "current" => Current(json, jsonOptions),
            "use" => Use(args[1..], json, jsonOptions),
            "clear" or "deactivate" => Clear(args[1..], json, jsonOptions),
            _ => CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "Unknown workspace command.", CommandExitCodes.UsageError, "use `cdidx workspace list`, `cdidx workspace use <name-or-relative-path>`, `cdidx workspace current`, or `cdidx workspace clear`.")
        };
    }

    private static int List(bool json, JsonSerializerOptions jsonOptions, bool includeActiveWorkspaceStatus = false)
    {
        var discovery = WorkspaceManifestLoader.Discover(Environment.CurrentDirectory);
        if (discovery.Path == null)
        {
            if (json)
            {
                var manifestStatus = new WorkspaceManifestStatusJsonResult(
                    discovery.Status,
                    discovery.Reason,
                    null,
                    discovery.SearchedPaths,
                    discovery.SupportedFiles);
                Console.WriteLine(JsonSerializer.Serialize(
                    new WorkspaceListJsonResult(
                        null,
                        Array.Empty<WorkspaceMember>(),
                        manifestStatus,
                        BuildActiveWorkspaceStatus(includeActiveWorkspaceStatus, manifest: null),
                        MemberHealthSummary: null),
                    jsonOptions));
            }
            else
                Console.WriteLine("No cdidx.workspace.json or .cdidx-workspace.json found.");
            return CommandExitCodes.Success;
        }

        WorkspaceManifest manifest;
        try
        {
            manifest = WorkspaceManifestLoader.Load(discovery.Path);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return WriteManifestValidationError(json, jsonOptions, ex);
        }

        if (json)
        {
            var memberHealth = includeActiveWorkspaceStatus
                ? BuildMemberHealth(manifest)
                : null;
            var manifestStatus = new WorkspaceManifestStatusJsonResult(
                "loaded",
                "loaded",
                manifest.Path,
                discovery.SearchedPaths,
                discovery.SupportedFiles);
            Console.WriteLine(JsonSerializer.Serialize(
                new WorkspaceListJsonResult(
                    manifest,
                    memberHealth?.Members ?? manifest.Members,
                    manifestStatus,
                    BuildActiveWorkspaceStatus(includeActiveWorkspaceStatus, manifest),
                    memberHealth?.Summary),
                jsonOptions));
            return CommandExitCodes.Success;
        }

        Console.WriteLine($"Manifest : {manifest.Path}");
        Console.WriteLine($"Strategy : {manifest.IndexStrategy}");
        var humanMembers = includeActiveWorkspaceStatus
            ? BuildMemberHealth(manifest).Members
            : manifest.Members;
        foreach (var member in humanMembers)
        {
            var label = member.IndexHealth?.Status ?? (member.Exists ? "ok" : "missing");
            var healthSuffix = member.IndexHealth is null
                ? string.Empty
                : $"  ({FormatMemberHealth(member.IndexHealth)})";
            Console.WriteLine($"  {label,-11}  {member.Path}  ->  {member.DbPath}{healthSuffix}");
        }
        return CommandExitCodes.Success;
    }

    private static ActiveWorkspaceJsonResult? BuildActiveWorkspaceStatus(bool include, WorkspaceManifest? manifest)
        => include
            ? ActiveWorkspaceJsonResult.FromWorkspaceStatus(ActiveWorkspace.Load(), path: null, manifest)
            : null;

    private static int Current(bool json, JsonSerializerOptions jsonOptions)
    {
        var state = ActiveWorkspace.Load();
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(ActiveWorkspaceJsonResult.From(state, null), jsonOptions));
        else if (state == null)
            Console.WriteLine("No active workspace set.");
        else
            Console.WriteLine($"{state.Name}: {state.Root} -> {state.DbPath}");
        return CommandExitCodes.Success;
    }

    private static int Clear(string[] args, bool json, JsonSerializerOptions jsonOptions)
    {
        if (args.Length != 0)
            return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "workspace clear does not accept arguments.", CommandExitCodes.UsageError, "run `cdidx workspace clear` without a workspace name.");

        if (!string.IsNullOrWhiteSpace(CdidxEnvironment.GetProcessEnvironmentVariable(ActiveWorkspace.EnvironmentVariable)))
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                json,
                jsonOptions,
                $"active workspace is set by {ActiveWorkspace.EnvironmentVariable}.",
                CommandExitCodes.UsageError,
                $"unset {ActiveWorkspace.EnvironmentVariable} before running `cdidx workspace clear`; environment configuration takes precedence over persisted state.");
        }

        try
        {
            ActiveWorkspace.Clear();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                json,
                jsonOptions,
                $"failed to clear active workspace: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
                CommandExitCodes.UsageError,
                "verify that the per-user cdidx configuration directory is writable and try again.");
        }

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(ActiveWorkspaceJsonResult.From(state: null, path: null), jsonOptions));
        else
            Console.WriteLine("Active workspace cleared.");
        return CommandExitCodes.Success;
    }

    private static int Use(string[] args, bool json, JsonSerializerOptions jsonOptions)
    {
        if (args.Length != 1)
            return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "workspace use requires a name or relative path.", CommandExitCodes.UsageError, "run `cdidx workspace use <name-or-relative-path>` from a manifest member or pass `default`.");

        var name = args[0];
        var manifest = WorkspaceManifestLoader.Find(Environment.CurrentDirectory);
        var useDefault = string.Equals(name, "default", StringComparison.OrdinalIgnoreCase);
        if (manifest == null && !useDefault)
            return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "workspace manifest was not found.", CommandExitCodes.UsageError, "run `cdidx workspace use <name-or-relative-path>` from a manifest member or pass `default`.");

        WorkspaceMember? member = null;
        var selectedName = name;
        if (manifest != null && !useDefault)
        {
            var memberNameComparison = PathCasing.ComparisonFor(manifest.Root);
            WorkspaceMember[] matches;
            if (WorkspaceManifestLoader.TryResolveMemberSelectorPath(
                    manifest,
                    name,
                    out var selectedPath,
                    out var selectedRelativePath))
            {
                matches = manifest.Members
                    .Where(m => string.Equals(m.Path, selectedPath, memberNameComparison))
                    .Take(1)
                    .ToArray();
                if (matches.Length == 1)
                    selectedName = selectedRelativePath!;
            }
            else
            {
                matches = manifest.Members
                    .Where(m => string.Equals(Path.GetFileName(m.Path), name, memberNameComparison))
                    .Take(MaxAmbiguousMemberCandidates + 1)
                    .ToArray();
            }

            if (matches.Length == 0)
                return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "workspace member was not found.", CommandExitCodes.UsageError, "run `cdidx workspace list` and pass a listed member directory name or manifest-relative path.");
            if (matches.Length > 1)
                return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "workspace member name is ambiguous.", CommandExitCodes.UsageError, $"matching members: {FormatAmbiguousMemberCandidates(matches)}. Pass a manifest-relative member path to select one.");

            member = matches[0];
        }

        if (member is { Exists: false })
            return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "workspace member is missing on disk.", CommandExitCodes.UsageError, "create the missing member directory or run `cdidx workspace list` and choose an existing member.");

        var root = Environment.CurrentDirectory;
        if (member != null)
        {
            var manifestRoot = manifest ?? throw new InvalidOperationException("workspace manifest was not found.");
            root = string.Equals(manifestRoot.IndexStrategy, "single", StringComparison.OrdinalIgnoreCase)
                ? manifestRoot.Root
                : member.Path;
        }

        var dbPath = member?.DbPath ?? DbPathResolver.ResolveForIndex(root, explicitDbPath: null);
        var state = new ActiveWorkspaceState(selectedName, root, dbPath);
        try
        {
            ActiveWorkspace.Save(state);
        }
        catch (InvalidOperationException ex)
        {
            return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, CommandErrorWriter.FormatSanitizedExceptionMessage(ex), CommandExitCodes.UsageError, "set XDG_CONFIG_HOME to an absolute writable directory or choose a workspace whose database is inside its root.");
        }

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(ActiveWorkspaceJsonResult.From(state, ActiveWorkspace.StatePath), jsonOptions));
        else
            Console.WriteLine($"Active workspace set to {state.Name}: {state.DbPath}");
        return CommandExitCodes.Success;
    }

    private static string FormatAmbiguousMemberCandidates(IReadOnlyList<WorkspaceMember> matches)
    {
        var candidates = matches
            .Take(MaxAmbiguousMemberCandidates)
            .Select(member => TruncateAmbiguousMemberPath(member.Path));
        var suffix = matches.Count > MaxAmbiguousMemberCandidates ? ", ..." : string.Empty;
        return string.Join(", ", candidates) + suffix;
    }

    private static string TruncateAmbiguousMemberPath(string path)
        => path.Length <= MaxAmbiguousMemberPathChars
            ? path
            : path[..(MaxAmbiguousMemberPathChars - 3)] + "...";

    private static MemberHealthBuildResult BuildMemberHealth(WorkspaceManifest manifest)
    {
        var members = new List<WorkspaceMember>(manifest.Members.Count);
        var cache = new Dictionary<string, WorkspaceMemberIndexHealth>(
            StringComparer.FromComparison(PathCasing.ComparisonFor(manifest.Root)));
        var singleStrategy = string.Equals(manifest.IndexStrategy, "single", StringComparison.OrdinalIgnoreCase);
        var databaseProbeCount = 0;
        var unprobedMemberCount = 0;

        foreach (var member in manifest.Members)
        {
            WorkspaceMemberIndexHealth health;
            var dbExists = File.Exists(LongPath.EnsureWindowsPrefix(member.DbPath));
            if (!member.Exists)
            {
                health = new WorkspaceMemberIndexHealth(
                    DbExists: dbExists,
                    Probed: false,
                    Status: "missing",
                    Reason: "member_missing");
            }
            else if (!dbExists)
            {
                health = new WorkspaceMemberIndexHealth(
                    DbExists: false,
                    Probed: false,
                    Status: "missing",
                    Reason: "database_not_found");
            }
            else if (cache.TryGetValue(member.DbPath, out var cachedHealth))
            {
                health = cachedHealth;
            }
            else if (databaseProbeCount >= MaxMemberHealthDatabaseProbes)
            {
                health = new WorkspaceMemberIndexHealth(
                    DbExists: true,
                    Probed: false,
                    Status: "not_checked",
                    Reason: "database_probe_limit_reached");
                unprobedMemberCount++;
            }
            else
            {
                databaseProbeCount++;
                var projectRoot = singleStrategy ? manifest.Root : member.Path;
                health = ProbeMemberHealth(member.DbPath, projectRoot);
                cache[member.DbPath] = health;
            }

            members.Add(member with { IndexHealth = health });
        }

        return new MemberHealthBuildResult(
            members,
            new WorkspaceMemberHealthSummary(
                manifest.Members.Count,
                databaseProbeCount,
                MaxMemberHealthDatabaseProbes,
                unprobedMemberCount,
                unprobedMemberCount > 0));
    }

    private static WorkspaceMemberIndexHealth ProbeMemberHealth(string dbPath, string projectRoot)
    {
        try
        {
            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            if (!db.TryValidateIsCodeIndexDb(out _))
            {
                return new WorkspaceMemberIndexHealth(
                    DbExists: true,
                    Probed: true,
                    Status: "invalid",
                    Reason: "invalid_codeindex_database",
                    SchemaCompatible: false);
            }

            using var reader = new DbReader(db);
            var snapshot = reader.GetWorkspaceIndexHealth();
            var schemaCompatible = !snapshot.IndexNewerThanReader;
            var graphReady = snapshot.GraphTableAvailable
                && snapshot.GraphDataCurrent
                && snapshot.ReferenceGraphComplete
                && snapshot.IndexComplete;
            if (!schemaCompatible)
            {
                return new WorkspaceMemberIndexHealth(
                    DbExists: true,
                    Probed: true,
                    Status: "incompatible",
                    Reason: "index_newer_than_reader",
                    SchemaCompatible: false,
                    FreshnessReason: "schema_incompatible",
                    IndexedAt: snapshot.IndexedAt,
                    LatestModified: snapshot.LatestModified,
                    GraphTableAvailable: snapshot.GraphTableAvailable,
                    GraphDataCurrent: snapshot.GraphDataCurrent,
                    ReferenceGraphComplete: snapshot.ReferenceGraphComplete,
                    IndexComplete: snapshot.IndexComplete,
                    GraphReady: graphReady,
                    IndexNewerThanReader: true);
            }

            var freshness = IndexFreshnessChecker.Check(
                reader,
                projectRoot,
                internalIndexDatabasePath: DbPathResolver.NormalizeDbPath(dbPath));
            var status = "ready";
            var reason = "ready";
            if (!freshness.Checked)
            {
                status = "degraded";
                reason = "freshness_check_unavailable";
            }
            else if (!freshness.MatchesWorkspace)
            {
                status = "stale";
                reason = freshness.Reason;
            }
            else if (!snapshot.IndexComplete)
            {
                status = "degraded";
                reason = "index_incomplete";
            }
            else if (!snapshot.GraphTableAvailable)
            {
                status = "degraded";
                reason = "graph_table_missing";
            }
            else if (!snapshot.ReferenceGraphComplete)
            {
                status = "degraded";
                reason = "reference_graph_incomplete";
            }
            else if (!snapshot.GraphDataCurrent)
            {
                status = "degraded";
                reason = "graph_data_not_current";
            }

            return new WorkspaceMemberIndexHealth(
                DbExists: true,
                Probed: true,
                Status: status,
                Reason: reason,
                SchemaCompatible: true,
                IndexMatchesWorkspace: freshness.Checked ? freshness.MatchesWorkspace : null,
                FreshnessReason: freshness.Reason,
                IndexedAt: snapshot.IndexedAt,
                LatestModified: snapshot.LatestModified,
                GraphTableAvailable: snapshot.GraphTableAvailable,
                GraphDataCurrent: snapshot.GraphDataCurrent,
                ReferenceGraphComplete: snapshot.ReferenceGraphComplete,
                IndexComplete: snapshot.IndexComplete,
                GraphReady: graphReady,
                IndexNewerThanReader: false);
        }
        catch (Exception ex) when (IsMemberHealthProbeFailure(ex))
        {
            return new WorkspaceMemberIndexHealth(
                DbExists: true,
                Probed: true,
                Status: "unavailable",
                Reason: "database_probe_failed");
        }
    }

    private static bool IsMemberHealthProbeFailure(Exception ex)
        => ex is SqliteException
            or CodeIndexException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException;

    private static string FormatMemberHealth(WorkspaceMemberIndexHealth health)
    {
        var schema = health.SchemaCompatible switch
        {
            true => "schema compatible",
            false => "schema incompatible",
            _ => "schema unknown",
        };
        var freshness = health.IndexMatchesWorkspace switch
        {
            true => "index fresh",
            false => $"index stale: {health.FreshnessReason}",
            _ => $"freshness {health.FreshnessReason ?? "not checked"}",
        };
        var graph = health.GraphReady switch
        {
            true => "graph ready",
            false => "graph degraded",
            _ => "graph unknown",
        };
        return $"{schema}; {freshness}; {graph}; reason={health.Reason}";
    }

    private sealed record MemberHealthBuildResult(
        IReadOnlyList<WorkspaceMember> Members,
        WorkspaceMemberHealthSummary Summary);

    private static int WriteManifestValidationError(bool json, JsonSerializerOptions jsonOptions, Exception ex)
        => CommandErrorWriter.WriteJsonOrHuman(
            json,
            jsonOptions,
            $"workspace manifest is invalid: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
            CommandExitCodes.UsageError,
            "Fix the workspace manifest; `members` must be an array of relative path strings.",
            errorCode: CommandErrorCodes.WorkspaceManifestInvalid,
            category: "workspace_manifest_invalid");
}
