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
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_traversal_policy_{Guid.NewGuid():N}");
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
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
