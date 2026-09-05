using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using Xunit;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class AuditBaselineIssue5261Tests
{
    [Fact]
    public void Compare_ReconcilesMovementChangesReviewsAndIncompleteCoverage()
    {
        var baseline = Snapshot(Entry("src/One.cs", "first"), Entry("src/Two.cs", "second"));
        var id = AuditBaselineStore.Text(baseline["entries"]![0]!.AsObject(), "id");
        AuditBaselineStore.Review(baseline, id, "reviewer", "Validated size guard.");
        var current = baseline.DeepClone().AsObject();
        current["entries"]![0]!["line"] = 500;
        var same = AuditBaselineStore.Compare(baseline, current);
        AssertCounts(same, 0, 2, 0, 0);
        Assert.Contains(same["results"]!.AsArray(), row => row!["review_applies"]!.GetValue<bool>());

        current["entries"]!.AsArray().RemoveAt(1);
        current["entries"]!.AsArray().Add(Entry("src/Three.cs", "third"));
        AssertCounts(AuditBaselineStore.Compare(baseline, current), 1, 1, 1, 0);
        current["entries"]![0]!["context"] = AuditBaselineStore.Hash("changed guard");
        current["entries"]![0]!.AsObject().Remove("review");
        var changed = AuditBaselineStore.Compare(baseline, current);
        AssertCounts(changed, 1, 0, 1, 1);
        Assert.All(changed["results"]!.AsArray(), row => Assert.False(row!["review_applies"]!.GetValue<bool>()));

        foreach (var key in new[] { "scope_fingerprint", "recipe_fingerprint", "identity_version", "workspace_fingerprint", "index_generation" })
        {
            var incompatible = current.DeepClone().AsObject();
            incompatible.Remove(key);
            AssertCounts(AuditBaselineStore.Compare(baseline, incompatible), 0, 0, 0, 3);
        }
        foreach (var reason in new[] { "stale", "partial", "cancelled", "failed", "capped" })
        {
            var incomplete = current.DeepClone().AsObject();
            incomplete["complete"] = false;
            incomplete["coverage_reasons"] = new JsonArray(reason);
            AssertCounts(AuditBaselineStore.Compare(baseline, incomplete), 0, 0, 0, 3);
            AssertCounts(AuditBaselineStore.Compare(incomplete, baseline), 0, 0, 0, 3);
        }
    }

    [Fact]
    public void Compare_DuplicatesRenamesAndOutputBoundsRemainExplicit()
    {
        var duplicate = Entry("src/One.cs", "same");
        var baseline = Snapshot(duplicate.DeepClone().AsObject(), duplicate.DeepClone().AsObject());
        AssertCounts(AuditBaselineStore.Compare(baseline, Snapshot(duplicate.DeepClone().AsObject())), 0, 0, 0, 1);
        Assert.Throws<InvalidDataException>(() => AuditBaselineStore.Review(baseline, AuditBaselineStore.Text(duplicate, "id"), "actor", "reason"));
        AssertCounts(AuditBaselineStore.Compare(Snapshot(Entry("old/One.cs", "same")), Snapshot(Entry("new/One.cs", "same"))), 0, 0, 0, 2);
        var many = Snapshot(Enumerable.Range(0, AuditBaselineStore.ResultLimit + 1).Select(i => Entry($"src/File{i}.cs", $"match{i}")).ToArray());
        var delta = AuditBaselineStore.Compare(Snapshot(), many);
        AssertCounts(delta, AuditBaselineStore.ResultLimit + 1, 0, 0, 0);
        Assert.Equal(AuditBaselineStore.ResultLimit, delta["returned"]!.GetValue<int>());
        Assert.Equal(1, delta["omitted_count"]!.GetValue<int>());
        Assert.True(delta["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Store_BoundsInputValidatesPathsAndPublishesPrivatelyWithoutImplicitOverwrite()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_baseline_store_5261");
        var path = Path.Combine(project.Root, "baseline.json");
        var baseline = Snapshot(Entry("src/日本語.cs", "secret-source-not-stored"));
        AuditBaselineStore.Write(path, baseline, false);
        Assert.DoesNotContain("secret-source-not-stored", File.ReadAllText(path), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.ThrowsAny<IOException>(() => AuditBaselineStore.Write(path, Snapshot(), false));
        Assert.Single(AuditBaselineStore.Read(path)["entries"]!.AsArray());
        AuditBaselineStore.Write(path, Snapshot(), true);
        Assert.Empty(AuditBaselineStore.Read(path)["entries"]!.AsArray());
        foreach (var badPath in new[] { "../file.cs", "/root/file.cs", "C:/file.cs", "src\\file.cs", "src//file.cs", "src/./file.cs", "src/\u001bfile.cs" })
            Assert.Throws<InvalidDataException>(() => AuditBaselineStore.NormalizePath(badPath));
        foreach (var malformed in new[] { "{", "[]", "{\"schema_version\":99}", new string('[', 17) + new string(']', 17) })
        {
            File.WriteAllText(path, malformed);
            Assert.ThrowsAny<Exception>(() => AuditBaselineStore.Read(path));
        }
        using (var stream = File.Create(path)) stream.SetLength(AuditBaselineStore.MaxBytes + 1);
        Assert.Throws<InvalidDataException>(() => AuditBaselineStore.Read(path));
        var tooMany = Snapshot(Enumerable.Range(0, AuditBaselineStore.MaxEntries + 1).Select(i => Entry("src/A.cs", i.ToString())).ToArray());
        Assert.Throws<InvalidDataException>(() => AuditBaselineStore.Write(path, tooMany, true));
    }

    [Fact]
    public void Cli_ExportsAndComparesExistingRecipeExecutionAndRejectsUnsafeOptions()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_baseline_cli_5261");
        var source = Path.Combine(project.Root, "One.cs");
        File.WriteAllText(source, "class One { string Value = \"Issue5261Needle\"; }\n");
        var db = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var baseline = Path.Combine(project.Root, ".cdidx", "baseline.json");
        var registry = new SearchAuditRecipeRegistry([new SearchAuditRecipe("fixture", "fixture",
            [new SearchAuditRecipeQuery("needle", "Issue5261Needle", "fixture", [], "Review.")])], []);
        var (indexExit, _, _) = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", db, "--json"], JsonOptions));
        Assert.Equal(0, indexExit);
        var (exportExit, exportOut, exportError) = CaptureConsole(() => QueryCommandRunner.RunAuditBaseline(
            ["export", baseline, "--db", db, "--json"], JsonOptions, registryForTesting: registry));
        Assert.Equal(string.Empty, exportError);
        Assert.Equal(0, exportExit);
        Assert.True(JsonNode.Parse(exportOut)!["complete"]!.GetValue<bool>());
        JsonOutputSnapshotHelper.AssertMatches("audit-baseline-export.json", exportOut);
        var stored = AuditBaselineStore.Read(baseline);
        Assert.Single(stored["entries"]!.AsArray());
        var (compareExit, compareOut, _) = CaptureConsole(() => QueryCommandRunner.RunAuditBaseline(
            ["compare", baseline, "--db", db, "--json"], JsonOptions, registryForTesting: registry));
        Assert.Equal(0, compareExit);
        AssertCounts(JsonNode.Parse(compareOut)!.AsObject(), 0, 1, 0, 0);
        JsonOutputSnapshotHelper.AssertMatches("audit-baseline-compare.json", compareOut);
        var id = stored["entries"]![0]!["id"]!.GetValue<string>();
        var (reviewExit, _, _) = CaptureConsole(() => QueryCommandRunner.RunAudit(
            ["baseline-review", baseline, id, "--actor", "reviewer", "--reason", "Checked the guard.", "--overwrite", "--json"], JsonOptions));
        Assert.Equal(0, reviewExit);
        File.WriteAllText(source, new string('\n', 50) + "class One { string Value = \"Issue5261Needle\"; }\n");
        CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", db, "--json"], JsonOptions));
        var (movedExit, movedOut, _) = CaptureConsole(() => QueryCommandRunner.RunAuditBaseline(
            ["compare", baseline, "--db", db, "--json"], JsonOptions, registryForTesting: registry));
        Assert.Equal(0, movedExit);
        AssertCounts(JsonNode.Parse(movedOut)!.AsObject(), 0, 1, 0, 0);
        Assert.True(JsonNode.Parse(movedOut)!["results"]![0]!["review_applies"]!.GetValue<bool>());
        File.WriteAllText(source, "class One { }\n");
        File.WriteAllText(Path.Combine(project.Root, "Two.cs"), "class Two { string Value = \"Issue5261Needle\"; }\n");
        var (staleExit, staleOut, _) = CaptureConsole(() => QueryCommandRunner.RunAuditBaseline(
            ["compare", baseline, "--db", db, "--json"], JsonOptions, registryForTesting: registry));
        Assert.Equal(CommandExitCodes.PartialResult, staleExit);
        AssertCounts(JsonNode.Parse(staleOut)!.AsObject(), 0, 0, 0, 1);
        CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", db, "--json"], JsonOptions));
        var (deltaExit, deltaOut, _) = CaptureConsole(() => QueryCommandRunner.RunAuditBaseline(
            ["compare", baseline, "--db", db, "--json"], JsonOptions, registryForTesting: registry));
        Assert.Equal(0, deltaExit);
        AssertCounts(JsonNode.Parse(deltaOut)!.AsObject(), 1, 0, 1, 0);
        foreach (var extra in new[] { new[] { "--format", "compact" }, new[] { "--summary-only" }, new[] { "--overwrite" } })
        {
            var (exit, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunAudit(
                ["baseline-compare", baseline, "--json", .. extra], JsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, exit);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("audit", JsonNode.Parse(stdout)!["command"]!.GetValue<string>());
        }
    }

    [Fact]
    public void HelpAndCompletionsExposeOnlyAcceptedBaselineFlags()
    {
        foreach (var verb in new[] { "baseline-export", "baseline-compare", "baseline-review" })
        {
            var flags = CliFlagSchema.GetCompletionFlagsForCommand("audit", verb).Select(flag => flag.Name).Order(StringComparer.Ordinal).ToArray();
            var expected = verb == "baseline-review" ? new[] { "--actor", "--reason", "--overwrite", "--json" }
                : new[] { "--recipe", "--db", "--lang", "--path", "--exclude-path", "--exclude-tests", "--audit-scope", "--since", "--limit", "--total-limit", "--json" }
                    .Concat(verb == "baseline-export" ? ["--overwrite"] : Array.Empty<string>()).ToArray();
            Assert.Equal(expected.Order(StringComparer.Ordinal), flags);
            var (_, help, _) = CaptureConsole(() => ConsoleUi.PrintCommandUsage("audit-" + verb) ? 0 : 1);
            Assert.Contains("cdidx audit " + verb, help, StringComparison.Ordinal);
            foreach (var shell in new[] { "bash", "zsh", "fish", "powershell" })
                Assert.Contains(verb, ConsoleCompletionRenderer.GetCompletionScript(shell), StringComparison.Ordinal);
        }
    }

    private static JsonObject Snapshot(params JsonObject[] entries) => new()
    {
        ["format"] = "cdidx-audit-baseline",
        ["schema_version"] = 1,
        ["identity_version"] = "1",
        ["recipe_schema_version"] = "1",
        ["scope_fingerprint"] = "scope",
        ["recipe_fingerprint"] = "recipe",
        ["workspace_fingerprint"] = "workspace",
        ["index_generation"] = "generation",
        ["complete"] = true,
        ["count_authoritative"] = true,
        ["coverage_reasons"] = new JsonArray(),
        ["entries"] = new JsonArray(entries.Cast<JsonNode>().ToArray()),
    };

    private static JsonObject Entry(string path, string evidence)
    {
        var match = AuditBaselineStore.Hash(evidence);
        return new JsonObject
        {
            ["recipe"] = "recipe",
            ["query"] = "query",
            ["path"] = path,
            ["line"] = 1,
            ["match"] = match,
            ["context"] = AuditBaselineStore.Hash("context", evidence),
            ["id"] = AuditBaselineStore.Hash("recipe", "query", path, match),
            ["identity_complete"] = true,
        };
    }

    private static void AssertCounts(JsonObject result, int added, int unchanged, int resolved, int unknown)
    {
        Assert.Equal(added, result["totals"]!["new"]!.GetValue<int>());
        Assert.Equal(unchanged, result["totals"]!["unchanged"]!.GetValue<int>());
        Assert.Equal(resolved, result["totals"]!["resolved"]!.GetValue<int>());
        Assert.Equal(unknown, result["totals"]!["unknown"]!.GetValue<int>());
        Assert.Equal(added + unchanged + resolved + unknown, result["total"]!.GetValue<int>());
    }
}
