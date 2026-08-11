using System.Collections.Frozen;

namespace CodeIndex.Models;

/// <summary>
/// Public taxonomy for persisted symbol and reference kind values.
/// 永続化される symbol/reference kind 値の公開 taxonomy。
/// </summary>
public static class SymbolKindCatalog
{
    public static readonly string[] SymbolKinds =
    [
        "accessor",
        "add",
        "anchor",
        "annotation",
        "assembly",
        "array",
        "async_function",
        "async_generator",
        "attribute",
        "associatedtype",
        "base_image",
        "build_arg",
        "class",
        "class_hook",
        "code",
        "constant",
        "copy",
        "delegate",
        "enum",
        "environment",
        "event",
        "expose",
        "field",
        "file_module",
        "function",
        "generator",
        "heading",
        "hook",
        "implements",
        "import",
        "interface",
        "lambda",
        "label",
        "layout",
        "method",
        "module",
        "namespace",
        "operator",
        "object",
        "package",
        "property",
        "procedure",
        "program",
        "project",
        "protocol",
        "protocol_impl",
        "reference",
        "record",
        "rule",
        "route",
        "run",
        "service",
        "shell",
        "specialization",
        "stage",
        "stopsignal",
        "struct",
        "submodule",
        "subroutine",
        "test.method",
        "trait",
        "type",
        "type_parameter",
        "typealias",
        "union",
        "user",
        "value",
        "block data",
        "variable",
        "volume",
        "workdir",
    ];

    /// <summary>
    /// Broad compatibility families for consumers that do not recognize newer semantic kinds.
    /// 新しい semantic kind を認識しない consumer 向けの広い互換 family。
    /// </summary>
    public static IReadOnlyDictionary<string, string> CompatibilityKindFamilies { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type_parameter"] = "type",
                ["typealias"] = "type",
            });

    public static readonly string[] ReferenceKinds =
    [
        "annotation",
        "attribute",
        "augmentation",
        "bcl_regex_without_timeout",
        "binding",
        "call",
        "capture",
        "column_reference",
        "consumes_hook",
        "const_assertion",
        "const_generic_reference",
        "copy_from",
        "cte_body_reference",
        "decorator",
        "dependency",
        "extends",
        "from",
        "friend",
        "generated_column_dependency",
        "generic_type_argument",
        "implement",
        "implicit_implementation",
        "import",
        "instantiate",
        "join_condition_reference",
        "lifetime_reference",
        "member_read",
        "metadata",
        "project_reference",
        "reference",
        "resource_reference",
        "stage",
        "razor_event_binding",
        "subscribe",
        "type_reference",
        "type_tag",
        "unsubscribe",
        "use",
    ];

    // The schema, extractors, and writer share one process-static taxonomy. Keep the public
    // arrays for ordered enumeration and schema generation, but validate hot persistence rows
    // through immutable ordinal lookups instead of scanning the arrays for every symbol and
    // reference. Taxonomy tables are immutable after type initialization by contract.
    // schema・extractor・writer は process-static な taxonomy を共有する。順序付き列挙と
    // schema 生成には公開 array を維持し、hot な永続化行の検証は行ごとの array 走査ではなく
    // immutable な ordinal lookup を使う。taxonomy table は型初期化後 immutable という契約である。
    private static readonly FrozenSet<string> ValidSymbolKinds =
        SymbolKinds.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ValidReferenceKinds =
        ReferenceKinds.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsValidSymbolKind(string? kind)
        => kind != null && ValidSymbolKinds.Contains(kind);

    public static bool IsValidReferenceKind(string? kind)
        => kind != null && ValidReferenceKinds.Contains(kind);

    public static string ToSqlCheckInList(IEnumerable<string> values)
        => string.Join(", ", values.Select(value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'"));
}
