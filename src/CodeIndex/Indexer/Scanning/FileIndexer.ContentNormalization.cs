namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal static string NormalizeLineEndings(string content)
        => FileContentLoader.NormalizeLineEndings(content);

    internal static string NormalizeContentForPrepass(string content)
        => FileContentLoader.NormalizeContentForPrepass(content);

    /// <summary>
    /// Strip every line-leading UTF-8 BOM (U+FEFF) and zero-width space (U+200B).
    /// Assumes CRLF has already been normalized to LF so `\n` is the sole line
    /// separator. Preserves non-line-leading invisibles verbatim.
    /// 行頭の UTF-8 BOM (U+FEFF) と zero-width space (U+200B) のみ剥がす。
    /// 呼び出し前に CRLF が LF へ正規化済みであることを前提とする。
    /// 行頭以外の不可視文字はそのまま保持する。
    /// </summary>
    internal static string StripLineLeadingInvisibles(string content)
        => FileContentLoader.StripLineLeadingInvisibles(content);

    internal static bool IsGitLfsPointer(byte[] rawBytes)
        => FileContentLoader.IsGitLfsPointer(rawBytes);
}
