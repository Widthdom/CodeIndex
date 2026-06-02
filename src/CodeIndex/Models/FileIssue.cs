namespace CodeIndex.Models;

/// <summary>
/// Represents an encoding or content issue found in a file.
/// ファイルで見つかったエンコーディングまたは内容の問題を表す。
/// </summary>
public class FileIssue
{
    public const string OriginSourceLiteral = "source_literal";
    public const string OriginDecodeReplacement = "decode_replacement";
    public const string SeverityInfo = "info";
    public const string SeverityWarning = "warning";

    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Line { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Origin { get; set; }
    public string? Severity { get; set; }
}
