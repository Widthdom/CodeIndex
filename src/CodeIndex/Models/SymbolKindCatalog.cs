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
        "annotation",
        "async_function",
        "async_generator",
        "attribute",
        "associatedtype",
        "class",
        "class_hook",
        "code",
        "constant",
        "delegate",
        "enum",
        "event",
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
        "protocol",
        "protocol_impl",
        "reference",
        "rule",
        "route",
        "service",
        "specialization",
        "struct",
        "submodule",
        "subroutine",
        "test.method",
        "trait",
        "type",
        "typealias",
        "union",
        "block data",
        "variable",
    ];

    public static readonly string[] ReferenceKinds =
    [
        "annotation",
        "attribute",
        "augmentation",
        "call",
        "capture",
        "column_reference",
        "consumes_hook",
        "const_assertion",
        "const_generic_reference",
        "copy_from",
        "cte_body_reference",
        "decorator",
        "extends",
        "from",
        "friend",
        "generic_type_argument",
        "implement",
        "implicit_implementation",
        "import",
        "instantiate",
        "join_condition_reference",
        "lifetime_reference",
        "metadata",
        "reference",
        "stage",
        "razor_event_binding",
        "subscribe",
        "type_alias_target",
        "type_reference",
        "unsubscribe",
        "use",
    ];

    public static bool IsValidSymbolKind(string? kind)
        => Contains(SymbolKinds, kind);

    public static bool IsValidReferenceKind(string? kind)
        => Contains(ReferenceKinds, kind);

    public static string ToSqlCheckInList(IEnumerable<string> values)
        => string.Join(", ", values.Select(value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'"));

    private static bool Contains(IEnumerable<string> values, string? value)
        => !string.IsNullOrWhiteSpace(value)
        && values.Contains(value, StringComparer.Ordinal);
}
