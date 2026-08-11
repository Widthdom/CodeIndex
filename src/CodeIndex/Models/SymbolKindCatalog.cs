using System.Collections.Frozen;

namespace CodeIndex.Models;

/// <summary>
/// Public taxonomy for persisted symbol and reference kind values.
/// 永続化される symbol/reference kind 値の公開 taxonomy。
/// </summary>
public static class SymbolKindCatalog
{
    private static readonly string[] CanonicalSymbolKinds =
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

    // Preserve the public field and ordered array surface for compatibility, but do not use
    // this mutable array as an internal source of truth. Callers have historically been able
    // to replace individual elements even though the field itself is readonly.
    // 公開 field と順序付き array の互換 surface は維持するが、この mutable array を内部の
    // source of truth にはしない。field 自体は readonly でも caller は従来から要素を置換できる。
    public static readonly string[] SymbolKinds = [.. CanonicalSymbolKinds];

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

    private static readonly string[] CanonicalReferenceKinds =
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

    public static readonly string[] ReferenceKinds = [.. CanonicalReferenceKinds];

    // The private ordered snapshots are the sole source for persistence validation, schema
    // checks/migrations, and ctags filters. Public arrays remain compatibility copies, so an
    // accidental element mutation cannot split those internal contracts.
    // private な順序付き snapshot だけを persistence validation、schema check / migration、
    // ctags filter の source とする。公開 array は互換用 copy のため、誤った要素変更でも
    // これらの内部契約が分裂しない。
    internal static IReadOnlyList<string> PersistedSymbolKinds { get; } =
        Array.AsReadOnly(CanonicalSymbolKinds);

    internal static IReadOnlyList<string> PersistedReferenceKinds { get; } =
        Array.AsReadOnly(CanonicalReferenceKinds);

    internal static string PersistedSymbolKindSqlCheckInList { get; } =
        ToSqlCheckInList(CanonicalSymbolKinds);

    internal static string PersistedReferenceKindSqlCheckInList { get; } =
        ToSqlCheckInList(CanonicalReferenceKinds);

    private static readonly FrozenSet<string> ValidSymbolKinds =
        CanonicalSymbolKinds.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ValidReferenceKinds =
        CanonicalReferenceKinds.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsValidSymbolKind(string? kind)
        => kind != null && ValidSymbolKinds.Contains(kind);

    public static bool IsValidReferenceKind(string? kind)
        => kind != null && ValidReferenceKinds.Contains(kind);

    public static string ToSqlCheckInList(IEnumerable<string> values)
        => string.Join(", ", values.Select(value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'"));
}
