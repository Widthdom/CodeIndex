using System.Text.Json.Serialization;

namespace CodeIndex.Models;

/// <summary>
/// Represents an indexed symbol reference such as a call site.
/// 呼び出し箇所などのインデックス済みシンボル参照を表す。
/// </summary>
public class ReferenceRecord
{
    public long Id { get; set; }
    public long FileId { get; set; }

    /// <summary>Referenced symbol name / 参照先シンボル名</summary>
    public string SymbolName { get; set; } = string.Empty;

    /// <summary>Language-specific persisted identity key when generic folding is insufficient / 一般的なfoldでは不十分な場合の言語固有identity key</summary>
    [JsonInclude]
    internal string? IdentitySymbolNameFolded { get; set; }

    /// <summary>Reference kind such as call or instantiate / 参照種別</summary>
    public string ReferenceKind { get; set; } = string.Empty;

    /// <summary>Line number (1-based) / 行番号（1始まり）</summary>
    public int Line { get; set; }

    /// <summary>Column number (1-based) / 列番号（1始まり）</summary>
    public int Column { get; set; }

    /// <summary>Physical source-token width in UTF-16 code units / 物理 source token の UTF-16 code unit 幅</summary>
    public int SpanLength { get; set; }

    /// <summary>Trimmed source line for quick inspection / すばやい確認用のtrim済みソース行</summary>
    public string Context { get; set; } = string.Empty;

    /// <summary>Enclosing symbol kind when known / 親シンボル種別</summary>
    public string? ContainerKind { get; set; }

    /// <summary>Enclosing symbol name when known / 親シンボル名</summary>
    public string? ContainerName { get; set; }

    /// <summary>Language-specific persisted container identity key / 言語固有の永続化container identity key</summary>
    [JsonInclude]
    internal string? IdentityContainerNameFolded { get; set; }

    /// <summary>
    /// Receiver/type qualifier immediately before the referenced name when it is a stable
    /// type-like identifier (for example <c>FileShare</c> in <c>FileShare.ReadWrite</c>).
    /// 参照名直前の安定した型相当修飾子（例: <c>FileShare.ReadWrite</c> の <c>FileShare</c>）。
    /// </summary>
    public string? TargetQualifier { get; set; }

    /// <summary>True when a language-specific receiver explicitly denotes the current container / 言語固有receiverが現在のcontainerを明示する場合はtrue</summary>
    [JsonInclude]
    internal bool SuppressInferredTargetQualifier { get; set; }

    /// <summary>True when the enclosing symbol references itself / 親シンボル自身への参照なら true</summary>
    public bool IsSelfReference { get; set; }

    /// <summary>True when this edge is part of a direct two-symbol cycle / 直接の2シンボル循環に含まれるなら true</summary>
    public bool IsMutualRecursion { get; set; }
}
