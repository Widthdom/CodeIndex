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
            "status" => List(json, jsonOptions),
            "current" => Current(json, jsonOptions),
            "use" => Use(args[1..], json, jsonOptions),
            _ => CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "Unknown workspace command.", CommandExitCodes.UsageError, "use `cdidx workspace list`, `cdidx workspace use <name>`, or `cdidx workspace current`.")
        };
    }

    private static int List(bool json, JsonSerializerOptions jsonOptions)
    {
        var manifest = WorkspaceManifestLoader.Find(Environment.CurrentDirectory);
        if (manifest == null)
        {
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(new WorkspaceListJsonResult(null, Array.Empty<WorkspaceMember>()), jsonOptions));
            else
                Console.WriteLine("No cdidx.workspace.json or .cdidx-workspace.json found.");
            return CommandExitCodes.Success;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new WorkspaceListJsonResult(manifest, manifest.Members), jsonOptions));
            return CommandExitCodes.Success;
        }

        Console.WriteLine($"Manifest : {manifest.Path}");
        Console.WriteLine($"Strategy : {manifest.IndexStrategy}");
        foreach (var member in manifest.Members)
            Console.WriteLine($"  {(member.Exists ? "ok" : "missing")}  {member.Path}  ->  {member.DbPath}");
        return CommandExitCodes.Success;
    }

    private static int Current(bool json, JsonSerializerOptions jsonOptions)
    {
        var state = ActiveWorkspace.Load();
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new ActiveWorkspaceJsonResult(state, null), jsonOptions));
        else if (state == null)
            Console.WriteLine("No active workspace set.");
        else
            Console.WriteLine($"{state.Name}: {state.Root} -> {state.DbPath}");
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
            var matches = manifest.Members
                .Where(m => string.Equals(Path.GetFileName(m.Path), name, StringComparison.OrdinalIgnoreCase))
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

        var root = member?.Path ?? Environment.CurrentDirectory;
        var dbPath = member?.DbPath ?? DbPathResolver.ResolveForIndex(root, explicitDbPath: null);
        var state = new ActiveWorkspaceState(name, root, dbPath);
        ActiveWorkspace.Save(state);
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new ActiveWorkspaceJsonResult(state, ActiveWorkspace.StatePath), jsonOptions));
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
}
