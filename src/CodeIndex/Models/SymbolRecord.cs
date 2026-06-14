namespace CodeIndex.Models;

/// <summary>
/// Represents a symbol (function, class, import) extracted from source code.
/// ソースコードから抽出されたシンボル（関数、クラス、インポート）を表す。
/// </summary>
public class SymbolRecord
{
    public const string MetadataTargetSourceExtractor = "extractor";
    public const string MetadataTargetSourceResolver = "resolver";

    public long Id { get; set; }
    public long FileId { get; set; }

    /// <summary>Symbol kind: 'function', 'class', or 'import' / シンボル種別</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Language-specific sub-kind when known / 言語固有の細分類</summary>
    public string? SubKind { get; set; }

    /// <summary>Symbol name / シンボル名</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Line number (1-based) / 行番号（1始まり）</summary>
    public int Line { get; set; }

    /// <summary>Definition start line (1-based) / 定義開始行（1始まり）</summary>
    public int StartLine { get; set; }

    /// <summary>Definition start column (0-based) when known / 定義開始列（0始まり、分かる場合）</summary>
    public int? StartColumn { get; set; }

    /// <summary>Definition end line (1-based) / 定義終了行（1始まり）</summary>
    public int EndLine { get; set; }

    /// <summary>Body start line when known / 本体開始行（判定できる場合）</summary>
    public int? BodyStartLine { get; set; }

    /// <summary>Body end line when known / 本体終了行（判定できる場合）</summary>
    public int? BodyEndLine { get; set; }

    /// <summary>Original declaration or signature line / 元の宣言・シグネチャ行</summary>
    public string? Signature { get; set; }

    /// <summary>Enclosing symbol kind when known / 親シンボル種別</summary>
    public string? ContainerKind { get; set; }

    /// <summary>Enclosing symbol name when known / 親シンボル名</summary>
    public string? ContainerName { get; set; }

    /// <summary>Qualified enclosing symbol path when known / 修飾付き親シンボル経路</summary>
    public string? ContainerQualifiedName { get; set; }

    /// <summary>Authoritative cross-file family key when known / 正式な cross-file family キー</summary>
    public string? FamilyKey { get; set; }

    /// <summary>Visibility or export-like modifier when known / 可視性または公開修飾子</summary>
    public string? Visibility { get; set; }

    /// <summary>Return type when known / 戻り値型</summary>
    public string? ReturnType { get; set; }

    /// <summary>
    /// Authoritative metadata-target flag (e.g. C# attribute class derived from System.Attribute).
    /// Persisted in `symbols.is_metadata_target` after a per-language resolver pass and gated by
    /// `metadata_target_version_<lang>` in `codeindex_meta`. NULL on legacy DBs and on languages
    /// without resolver coverage; readers fall back to the legacy heuristic in that case.
    /// メタデータ対象フラグ（C# Attribute サブクラス等）。`metadata_target_version_<lang>` で
    /// gating される resolver が full populate した後にだけ trust される。
    /// </summary>
    public bool? IsMetadataTarget { get; set; }

    /// <summary>
    /// Provenance for `IsMetadataTarget` when known (`extractor` or `resolver`).
    /// `IsMetadataTarget` の由来（分かる場合は `extractor` または `resolver`）。
    /// </summary>
    public string? MetadataTargetSource { get; set; }

    /// <summary>0-based occurrence index of the same signature on the same raw line / 同一 raw 行・同一 signature 内での 0-based 出現順</summary>
    public int? SameLineSignatureOccurrenceIndex { get; set; }
}
