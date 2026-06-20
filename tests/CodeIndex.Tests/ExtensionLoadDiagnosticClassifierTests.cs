using System.Reflection;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Tests;

public class ExtensionLoadDiagnosticClassifierTests
{
    [Fact]
    public void ClassifyTypeLoad_BoundsLoaderExceptionDetails()
    {
        var pathLikeDependency = "/tmp/" + new string('x', 512) + "/Missing.Dependency.dll";
        var exception = new ReflectionTypeLoadException(
            [],
            [
                new FileNotFoundException("missing dependency", pathLikeDependency),
                new InvalidOperationException(new string('y', 512)),
            ]);

        var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyTypeLoad(
            "Plugin assembly type inspection",
            exception);

        Assert.Equal("type_load_failed", diagnostic.Category);
        Assert.Contains("Plugin assembly type inspection failed", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ReflectionTypeLoadException), diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(FileNotFoundException), diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(pathLikeDependency, diagnostic.Message, StringComparison.Ordinal);
        Assert.True(diagnostic.Message.Length < 320, diagnostic.Message);
    }

    [Fact]
    public void ClassifyConstructorFailure_UnwrapsTargetInvocationWithoutLeakingMessage()
    {
        var exception = new TargetInvocationException(
            new InvalidOperationException("sensitive constructor message"));

        var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyConstructorFailure(
            "Plugin type constructor",
            exception);

        Assert.Equal("constructor_failed", diagnostic.Category);
        Assert.Contains(nameof(InvalidOperationException), diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive constructor message", diagnostic.Message, StringComparison.Ordinal);
    }
}
