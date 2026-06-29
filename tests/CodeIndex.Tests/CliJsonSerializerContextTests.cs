using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class CliJsonSerializerContextTests
{
    public static IEnumerable<object[]> CliJsonRootTypes()
    {
        foreach (var type in CliContractManifest.CliJsonRootTypes)
            yield return [type];
    }

    [Theory]
    [MemberData(nameof(CliJsonRootTypes))]
    public void CliJsonSerializerContext_CoversEveryCliJsonRootType(Type type)
    {
        Assert.NotNull(CliJsonSerializerContext.Default.GetTypeInfo(type));
    }
}
