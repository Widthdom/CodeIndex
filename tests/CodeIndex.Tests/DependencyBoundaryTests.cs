namespace CodeIndex.Tests;

public class DependencyBoundaryTests
{
    [Fact]
    public void CliCommandMetadataDependencies_RemainAcyclic_Issue4741()
    {
        AssertDoesNotReference(
            "CliCommandMetadata.cs",
            "CliCommandCatalog",
            "CliFlagSchema",
            "ConsoleCompletionRenderer");
        AssertDoesNotReference(
            "CliCommandCatalog.cs",
            "CliFlagSchema",
            "ConsoleCompletionRenderer");
        AssertDoesNotReference(
            "CliFlagSchema.cs",
            "CliCommandCatalog",
            "ConsoleCompletionRenderer");
        AssertDoesNotReference(
            "ConsoleCompletionRenderer.cs",
            "CliCommandCatalog");
    }

    [Fact]
    public void ConfigEnvironmentDependencies_RemainAcyclic_Issue4741()
    {
        AssertDoesNotReference(
            "CdidxEnvironment.cs",
            "ActiveWorkspace",
            "CdidxConfigFile",
            "CdidxConfigSourceResolver");
        AssertDoesNotReference(
            "CdidxConfigSourceResolver.cs",
            "ActiveWorkspace",
            "CdidxConfigFile",
            "CdidxEnvironment");
        AssertDoesNotReference(
            "ActiveWorkspace.cs",
            "CdidxConfigFile",
            "CdidxConfigSourceResolver");
    }

    private static void AssertDoesNotReference(string fileName, params string[] forbiddenTypeNames)
    {
        var source = RepositoryTestPaths.ReadText("src", "CodeIndex", "Cli", fileName);
        foreach (var forbiddenTypeName in forbiddenTypeNames)
        {
            Assert.DoesNotContain(
                forbiddenTypeName,
                source,
                StringComparison.Ordinal);
        }
    }
}
