namespace CodeIndex.Models;

/// <summary>
/// Represents a file record in the database.
/// データベース内のファイルレコードを表す。
/// </summary>
public class FileRecord
{
    public long Id { get; set; }

    /// <summary>Relative path from project root / プロジェクトルートからの相対パス</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Detected language / 検出された言語</summary>
    public string? Lang { get; set; }

    /// <summary>File size in bytes / ファイルサイズ（バイト）</summary>
    public long Size { get; set; }

    /// <summary>Total line count / 総行数</summary>
    public int Lines { get; set; }

    /// <summary>SHA256 checksum / SHA256チェックサム</summary>
    public string? Checksum { get; set; }

    /// <summary>Last modified time / 最終更新日時</summary>
    public DateTime Modified { get; set; }

    /// <summary>Generated source marker / 生成コードマーカー</summary>
    public bool Generated { get; set; }
}
