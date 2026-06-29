using System.Reflection;
using System.Text.Json.Serialization;
using CodeIndex.Database;

namespace CodeIndex.Cli;

/// <summary>
/// Central inventory for stable CLI machine contracts that are otherwise easy to
/// update independently: process exit codes, structured error codes, JSON API
/// versioning, source-generated JSON roots, and golden JSON payload families.
/// CLI の機械向け契約を一箇所で棚卸しする manifest。終了コード、構造化エラーコード、
/// JSON API version、source-generated JSON root、golden JSON payload family をまとめる。
/// </summary>
internal static class CliContractManifest
{
    public static string JsonApiVersion => JsonOutputContract.ApiVersion;

    public static IReadOnlyList<CliExitCodeContract> ExitCodes { get; } =
    [
        new(nameof(CommandExitCodes.Success), CommandExitCodes.Success),
        new(nameof(CommandExitCodes.UsageError), CommandExitCodes.UsageError),
        new(nameof(CommandExitCodes.NotFound), CommandExitCodes.NotFound),
        new(nameof(CommandExitCodes.DatabaseError), CommandExitCodes.DatabaseError),
        new(nameof(CommandExitCodes.FeatureUnavailable), CommandExitCodes.FeatureUnavailable),
        new(nameof(CommandExitCodes.StaleIndex), CommandExitCodes.StaleIndex),
        new(nameof(CommandExitCodes.TransientDatabaseError), CommandExitCodes.TransientDatabaseError),
        new(nameof(CommandExitCodes.InvalidArgument), CommandExitCodes.InvalidArgument),
        new(nameof(CommandExitCodes.CancelledBySignal), CommandExitCodes.CancelledBySignal),
        new(nameof(CommandExitCodes.InstallError), CommandExitCodes.InstallError),
        new(nameof(CommandExitCodes.RuntimeError), CommandExitCodes.RuntimeError),
        new(nameof(CommandExitCodes.UnhandledException), CommandExitCodes.UnhandledException),
        new(nameof(CommandExitCodes.ExUsage), CommandExitCodes.ExUsage),
        new(nameof(CommandExitCodes.Interrupted), CommandExitCodes.Interrupted, IsAlias: true, AliasOf: nameof(CommandExitCodes.CancelledBySignal)),
        new(nameof(CommandExitCodes.LegacyInterrupted), CommandExitCodes.LegacyInterrupted),
    ];

    public static IReadOnlyList<CliErrorCodeContract> ErrorCodes { get; } =
    [
        new(nameof(CommandErrorCodes.DbNotFound), CommandErrorCodes.DbNotFound, CommandExitCodes.NotFound),
        new(nameof(CommandErrorCodes.DbLocked), CommandErrorCodes.DbLocked, CommandExitCodes.TransientDatabaseError),
        new(nameof(CommandErrorCodes.SchemaTooNew), CommandErrorCodes.SchemaTooNew, CommandExitCodes.DatabaseError),
        new(nameof(CommandErrorCodes.DbNotWritable), CommandErrorCodes.DbNotWritable, CommandExitCodes.DatabaseError),
        new(nameof(CommandErrorCodes.DbIntegrityFailed), CommandErrorCodes.DbIntegrityFailed, CommandExitCodes.DatabaseError),
        new(nameof(CommandErrorCodes.FtsQuerySyntax), CommandErrorCodes.FtsQuerySyntax, null),
        new(nameof(CommandErrorCodes.TempStoreExhausted), CommandErrorCodes.TempStoreExhausted, CommandExitCodes.DatabaseError),
        new(nameof(CommandErrorCodes.DbError), CommandErrorCodes.DbError, CommandExitCodes.DatabaseError),
        new(nameof(CommandErrorCodes.FeatureUnavailable), CommandErrorCodes.FeatureUnavailable, CommandExitCodes.FeatureUnavailable),
        new(nameof(CommandErrorCodes.UsageError), CommandErrorCodes.UsageError, CommandExitCodes.InvalidArgument),
        new(nameof(CommandErrorCodes.DirectoryNotFound), CommandErrorCodes.DirectoryNotFound, CommandExitCodes.NotFound),
        new(nameof(CommandErrorCodes.Interrupted), CommandErrorCodes.Interrupted, CommandExitCodes.CancelledBySignal),
        new(nameof(CommandErrorCodes.IndexExtractionStalled), CommandErrorCodes.IndexExtractionStalled, null),
        new(nameof(CommandErrorCodes.RegexMatchTimeout), CommandErrorCodes.RegexMatchTimeout, null),
        new(nameof(CommandErrorCodes.FileSystemCaseProbeFailed), CommandErrorCodes.FileSystemCaseProbeFailed, CommandExitCodes.DatabaseError),
    ];

    public static IReadOnlyList<Type> CliJsonRootTypes { get; } = LoadCliJsonRootTypes();

    public static IReadOnlyList<CliGoldenJsonContract> GoldenJsonPayloads { get; } =
    [
        new("status", "status.json"),
        new("search", "search.json"),
        new("references", "references.json"),
        new("impact", "impact.json"),
        new("excerpt", "excerpt.json"),
    ];

    private static IReadOnlyList<Type> LoadCliJsonRootTypes() =>
        typeof(CliJsonSerializerContext)
            .GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType == typeof(JsonSerializableAttribute))
            .Select(GetJsonSerializableRootType)
            .ToArray();

    private static Type GetJsonSerializableRootType(CustomAttributeData attribute)
    {
        if (attribute.ConstructorArguments.Count == 1
            && attribute.ConstructorArguments[0].Value is Type type)
        {
            return type;
        }

        throw new InvalidOperationException("JsonSerializableAttribute is missing its root type constructor argument.");
    }
}

internal sealed record CliExitCodeContract(
    string Name,
    int Value,
    bool IsAlias = false,
    string? AliasOf = null);

internal sealed record CliErrorCodeContract(
    string Name,
    string Code,
    int? CodeIndexExceptionExitCode);

internal sealed record CliGoldenJsonContract(
    string Command,
    string GoldenFile);
