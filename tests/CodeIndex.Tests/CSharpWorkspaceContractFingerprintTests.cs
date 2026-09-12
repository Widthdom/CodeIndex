using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public class CSharpWorkspaceContractFingerprintTests
{
    [Fact]
    public void Fingerprint_BindsContributionOrderInventoryAndBudget_Issue5347()
    {
        var targets = new List<(string Path, bool Suppressed)>
        {
            ("A.cs", false), ("B.cs", false), ("Consumer.cs", false),
        };
        CSharpStaticInterfacePrepass.FileTarget Target(string path)
            => new(path, path, path, path, "csharp");
        var candidates = new List<CSharpStaticInterfacePrepass.FileTarget>
            { Target("A.cs"), Target("B.cs"), Target("Consumer.cs") };
        string?[] checksums = ["source-a", "source-b", null];
        var baseline = CSharpWorkspaceContractFingerprint.Build(targets, candidates, checksums, default);
        Assert.NotNull(baseline);
        Assert.Equal(baseline, CSharpWorkspaceContractFingerprint.Build(
            targets, [candidates[2], candidates[0], candidates[1]], [null, "source-a", "source-b"], default));
        Assert.NotEqual(baseline, CSharpWorkspaceContractFingerprint.Build(
            targets, [candidates[1], candidates[0], candidates[2]], ["source-b", "source-a", null], default));
        Assert.NotEqual(baseline, CSharpWorkspaceContractFingerprint.Build(
            targets, candidates, ["source-changed", "source-b", null], default));
        targets[2] = ("Renamed.cs", false);
        Assert.NotEqual(baseline, CSharpWorkspaceContractFingerprint.Build(targets, candidates, checksums, default));
        targets[2] = ("Consumer.cs", true);
        Assert.NotEqual(baseline, CSharpWorkspaceContractFingerprint.Build(targets, candidates, checksums, default));
        Assert.Throws<OperationCanceledException>(() => CSharpWorkspaceContractFingerprint.Build(
            targets, candidates, checksums, new CancellationToken(canceled: true)));

        var bounded = Enumerable.Range(0, CSharpWorkspaceContractFingerprint.MaxFiles)
            .Select(i => ($"File{i}.cs", false)).ToList();
        Assert.NotNull(CSharpWorkspaceContractFingerprint.Build(bounded, [], [], default));
        bounded.Add(("OverBudget.cs", false));
        Assert.Null(CSharpWorkspaceContractFingerprint.Build(bounded, [], [], default));
    }
}
