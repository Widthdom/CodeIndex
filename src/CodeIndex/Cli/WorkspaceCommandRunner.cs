using System.Text.Json;

namespace CodeIndex.Cli;

internal static class WorkspaceCommandRunner
{
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

        var member = useDefault
            ? null
            : manifest?.Members.FirstOrDefault(m => string.Equals(Path.GetFileName(m.Path), name, StringComparison.OrdinalIgnoreCase));
        if (manifest != null && member == null && !useDefault)
            return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "workspace member was not found.", CommandExitCodes.UsageError, "run `cdidx workspace list` and pass one of the listed member directory names.");
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
}
