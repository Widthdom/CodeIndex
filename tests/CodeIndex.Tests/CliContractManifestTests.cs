using System.Reflection;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public class CliContractManifestTests
{
    [Fact]
    public void JsonApiVersion_MatchesCanonicalJsonOutputContract_Issue4166()
    {
        Assert.Equal(JsonOutputContract.ApiVersion, CliContractManifest.JsonApiVersion);
        Assert.Equal("1", CliContractManifest.JsonApiVersion);
    }

    [Fact]
    public void ExitCodes_CoverEveryDeclaredCommandExitCode_Issue4166()
    {
        var declared = GetConstantNames(typeof(CommandExitCodes));
        var manifested = CliContractManifest.ExitCodes.Select(code => code.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(declared, manifested);
    }

    [Fact]
    public void ExitCodes_AreUniqueExceptDeclaredAliases_Issue4166()
    {
        Assert.NotEmpty(CliContractManifest.ExitCodes);

        var names = CliContractManifest.ExitCodes.Select(code => code.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        var canonicalValues = CliContractManifest.ExitCodes
            .Where(code => !code.IsAlias)
            .Select(code => code.Value)
            .ToList();
        Assert.Equal(canonicalValues.Count, canonicalValues.Distinct().Count());

        foreach (var alias in CliContractManifest.ExitCodes.Where(code => code.IsAlias))
        {
            Assert.False(string.IsNullOrWhiteSpace(alias.AliasOf));
            var target = Assert.Single(CliContractManifest.ExitCodes, code => code.Name == alias.AliasOf);
            Assert.Equal(target.Value, alias.Value);
        }
    }

    [Fact]
    public void ErrorCodes_CoverEveryDeclaredCommandErrorCode_Issue4166()
    {
        var declared = GetConstantNames(typeof(CommandErrorCodes));
        var manifested = CliContractManifest.ErrorCodes.Select(code => code.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(declared, manifested);
    }

    [Fact]
    public void ErrorCodes_AreUniqueAndSequential_Issue4166()
    {
        Assert.NotEmpty(CliContractManifest.ErrorCodes);

        var names = CliContractManifest.ErrorCodes.Select(code => code.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        var codes = CliContractManifest.ErrorCodes.Select(code => code.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());

        for (var index = 0; index < CliContractManifest.ErrorCodes.Count; index++)
        {
            var contract = CliContractManifest.ErrorCodes[index];
            var expectedPrefix = $"E{index + 1:000}_";
            Assert.StartsWith(expectedPrefix, contract.Code, StringComparison.Ordinal);
            Assert.Matches(new Regex("^E[0-9]{3}_[A-Z0-9_]+$"), contract.Code);
        }
    }

    [Fact]
    public void ErrorCodes_WithExceptionExitCodesMatchProgramRunnerMapping_Issue4166()
    {
        foreach (var contract in CliContractManifest.ErrorCodes)
        {
            if (contract.CodeIndexExceptionExitCode is null)
                continue;

            Assert.Equal(
                contract.CodeIndexExceptionExitCode.Value,
                ProgramRunner.MapCodeIndexExceptionExitCode(contract.Code));
        }
    }

    [Fact]
    public void CliJsonRootTypes_AreUniqueAndSourceGenerated_Issue4166()
    {
        var types = CliContractManifest.CliJsonRootTypes;

        Assert.NotEmpty(types);
        Assert.Equal(types.Count, types.Distinct().Count());

        foreach (var type in types)
            Assert.NotNull(CliJsonSerializerContext.Default.GetTypeInfo(type));
    }

    [Fact]
    public void GoldenJsonPayloads_AreUniqueAndCheckedIn_Issue4166()
    {
        var payloads = CliContractManifest.GoldenJsonPayloads;

        Assert.NotEmpty(payloads);
        Assert.Equal(payloads.Count, payloads.Select(payload => payload.Command).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(payloads.Count, payloads.Select(payload => payload.GoldenFile).Distinct(StringComparer.Ordinal).Count());

        var goldenDirectory = RepositoryTestPaths.Combine("tests", "CodeIndex.Tests", "golden");
        var checkedInGoldens = Directory.GetFiles(goldenDirectory, "*.json")
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var manifestedGoldens = payloads
            .Select(payload => payload.GoldenFile)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(checkedInGoldens, manifestedGoldens);

        foreach (var payload in payloads)
        {
            Assert.EndsWith(".json", payload.GoldenFile, StringComparison.Ordinal);
            Assert.True(
                File.Exists(Path.Combine(goldenDirectory, payload.GoldenFile)),
                $"Missing golden JSON payload: {payload.GoldenFile}");
        }
    }

    private static string[] GetConstantNames(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && !field.IsInitOnly)
            .Select(field => field.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
