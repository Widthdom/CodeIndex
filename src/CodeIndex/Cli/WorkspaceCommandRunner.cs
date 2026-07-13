using System.Text.Json;

namespace CodeIndex.Cli;

internal static class WorkspaceCommandRunner
{
    private const int MaxAmbiguousMemberCandidates = 5;
    private const int MaxAmbiguousMemberPathChars = 160;

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
            _ => CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "Unknown workspace command.", CommandExitCodes.UsageError, "use `cdidx workspace list`, `cdidx workspace use <name>`, `cdidx workspace current`, or `cdidx workspace clear`.")
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
                        BuildActiveWorkspaceStatus(includeActiveWorkspaceStatus, manifest: null)),
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
            var manifestStatus = new WorkspaceManifestStatusJsonResult(
                "loaded",
                "loaded",
                manifest.Path,
                discovery.SearchedPaths,
                discovery.SupportedFiles);
            Console.WriteLine(JsonSerializer.Serialize(
                new WorkspaceListJsonResult(
                    manifest,
                    manifest.Members,
                    manifestStatus,
                    BuildActiveWorkspaceStatus(includeActiveWorkspaceStatus, manifest)),
                jsonOptions));
            return CommandExitCodes.Success;
        }

        Console.WriteLine($"Manifest : {manifest.Path}");
        Console.WriteLine($"Strategy : {manifest.IndexStrategy}");
        foreach (var member in manifest.Members)
            Console.WriteLine($"  {(member.Exists ? "ok" : "missing")}  {member.Path}  ->  {member.DbPath}");
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
            return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "workspace use requires a name.", CommandExitCodes.UsageError, "run `cdidx workspace use <name>` from a manifest member or pass `default`.");

        var name = args[0];
        var manifest = WorkspaceManifestLoader.Find(Environment.CurrentDirectory);
        var useDefault = string.Equals(name, "default", StringComparison.OrdinalIgnoreCase);
        if (manifest == null && !useDefault)
            return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "workspace manifest was not found.", CommandExitCodes.UsageError, "run `cdidx workspace use <name>` from a manifest member or pass `default`.");

        WorkspaceMember? member = null;
        if (manifest != null && !useDefault)
        {
            var memberNameComparison = PathCasing.ComparisonFor(manifest.Root);
            var matches = manifest.Members
                .Where(m => string.Equals(Path.GetFileName(m.Path), name, memberNameComparison))
                .Take(MaxAmbiguousMemberCandidates + 1)
                .ToArray();

            if (matches.Length == 0)
                return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "workspace member was not found.", CommandExitCodes.UsageError, "run `cdidx workspace list` and pass one of the listed member directory names.");
            if (matches.Length > 1)
                return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "workspace member name is ambiguous.", CommandExitCodes.UsageError, $"matching members: {FormatAmbiguousMemberCandidates(matches)}. Use unique member directory names in the workspace manifest.");

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
        var state = new ActiveWorkspaceState(name, root, dbPath);
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
