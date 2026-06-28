using CodeIndex;

namespace CodeIndex.Tests;

public class FileSystemTraversalPolicyTests
{
    [Fact]
    public void CreateTopDirectoryOnlyOptions_ReturnsExplicitTraversalPolicy_Issue3951()
    {
        var first = FileSystemTraversalPolicy.CreateTopDirectoryOnlyOptions();
        var second = FileSystemTraversalPolicy.CreateTopDirectoryOnlyOptions();

        Assert.NotSame(first, second);
        Assert.False(first.RecurseSubdirectories);
        Assert.False(first.IgnoreInaccessible);
        Assert.False(first.ReturnSpecialDirectories);
        Assert.Equal((FileAttributes)0, first.AttributesToSkip);
    }

    [Fact]
    public void EnumerateHelpers_DoNotRecurseIntoChildDirectories_Issue3951()
    {
        var root = TestProjectHelper.CreateTempProject("traversal_policy");
        var child = Path.Combine(root, "child");
        try
        {
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(root, "root.txt"), "root");
            File.WriteAllText(Path.Combine(child, "child.txt"), "child");

            var files = FileSystemTraversalPolicy.EnumerateFiles(root)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var directories = FileSystemTraversalPolicy.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var entries = FileSystemTraversalPolicy.EnumerateFileSystemEntries(root)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(["root.txt"], files);
            Assert.Equal(["child"], directories);
            Assert.Equal(["child", "root.txt"], entries);
            Assert.True(FileSystemTraversalPolicy.HasAnyFileSystemEntry(root));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void EnumerateHelpers_ObserveCancellationBeforeTraversal_Issue4131()
    {
        var root = TestProjectHelper.CreateTempProject("traversal_policy_cancel");
        using var cancellation = new CancellationTokenSource();
        try
        {
            File.WriteAllText(Path.Combine(root, "root.txt"), "root");
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                FileSystemTraversalPolicy.EnumerateFiles(
                    root,
                    new FileSystemTraversalOptions(cancellationToken: cancellation.Token)).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void EnumerateHelpers_EnforceEntryBudget_Issue4131()
    {
        var root = TestProjectHelper.CreateTempProject("traversal_policy_budget");
        try
        {
            File.WriteAllText(Path.Combine(root, "one.txt"), "one");
            File.WriteAllText(Path.Combine(root, "two.txt"), "two");

            var ex = Assert.Throws<FileSystemTraversalBudgetExceededException>(() =>
                FileSystemTraversalPolicy.EnumerateFiles(
                    root,
                    new FileSystemTraversalOptions(maxEntries: 1)).ToArray());

            Assert.Equal(1, ex.MaxEntries);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void FailureTaxonomy_ClassifiesExpectedTraversalFailures_Issue4131()
    {
        var budgetExceeded = new FileSystemTraversalBudgetExceededException(1);

        Assert.True(FileSystemTraversalPolicy.IsExpectedTraversalException(new UnauthorizedAccessException()));
        Assert.True(FileSystemTraversalPolicy.IsExpectedTraversalException(budgetExceeded));
        Assert.False(FileSystemTraversalPolicy.IsExpectedTraversalException(new OperationCanceledException()));
        Assert.Equal(
            FileSystemTraversalFailureKind.Permissions,
            FileSystemTraversalPolicy.ClassifyFailure(new UnauthorizedAccessException()));
        Assert.Equal(FileSystemTraversalFailureKind.BudgetExceeded, FileSystemTraversalPolicy.ClassifyFailure(budgetExceeded));
        Assert.Equal("permissions", FileSystemTraversalPolicy.DescribeFailureReason(new UnauthorizedAccessException()));
        Assert.Equal("entry budget exceeded", FileSystemTraversalPolicy.DescribeFailureReason(budgetExceeded));
    }
}
