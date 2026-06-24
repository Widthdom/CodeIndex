namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    // Directories to skip (case-insensitive for cross-platform) / スキップするディレクトリ（クロスプラットフォーム対応で大文字小文字を区別しない）
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg",
        "node_modules", "__pycache__", ".pytest_cache",
        "venv", ".venv", "env",
        "dist", "build", ".build", "out",
        "bin", "obj",                   // .NET build outputs / .NETビルド出力
        "target",                       // Rust/Java/Maven build output / Rust/Java/Mavenビルド出力
        ".gradle",                      // Gradle cache / Gradleキャッシュ
        ".next", ".nuxt",
        ".idea", ".vscode",
        "coverage", "vendor",
        ".terraform",                   // Terraform state/plugin cache / Terraformステート・プラグインキャッシュ
        ".cargo",                       // Cargo registry cache / Cargoレジストリキャッシュ
        ".pub-cache",                   // Dart pub cache / Dart pubキャッシュ
        "_build",                       // Elixir/Mix build output / Elixir/Mixビルド出力
    };

    // Files to skip (case-insensitive for cross-platform consistency with SkipDirs)
    // スキップするファイル名（SkipDirsと同様にクロスプラットフォーム対応で大文字小文字を区別しない）
    private static readonly HashSet<string> SkipFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db",
    };

    // macOS AppleDouble resource-fork prefix. Files written by HFS+/SMB-style metadata carriers
    // (e.g. archives unpacked on a non-HFS volume, or macOS-mounted SMB/NFS shares) appear as
    // `._<original>` siblings of the real file. These are binary metadata blobs that masquerade
    // as the real file's language (so the symbol extractor wastes work on noise) and they are
    // never under a project's source control. Skip them by filename pattern regardless of where
    // they appear in the tree. Recognized dotfiles (e.g. .gitignore, .editorconfig, .cdidxrc.json)
    // are not affected because they do not start with this prefix.
    // macOS の AppleDouble (`._<原ファイル>`) 接頭辞。HFS+/SMB 系のメタデータ伝搬や macOS マウント
    // SMB/NFS 共有経由で生成される resource fork で、原ファイルと同じ拡張子のメタデータバイナリが
    // index/シンボル抽出に紛れ込み雑音化する。バージョン管理対象でもないためツリーのどこにあっても
    // ファイル名パターンで除外する。`.gitignore` / `.editorconfig` / `.cdidxrc.json` のような既知
    // dotfile はこの接頭辞を持たないため影響を受けない。
    private const string AppleDoublePrefix = "._";

    // True for filenames that the scanner must skip purely by name, independent of .gitignore
    // / .cdidxignore. Bundles the exact-name SkipFiles list with the AppleDouble pattern so the
    // full-scan walker and update-mode path filter share a single rule.
    // 走査経路 (full-scan の walker と --files/--commits の path filter) が共通参照する、
    // 既定でスキップするファイル名判定。SkipFiles の完全一致と AppleDouble 接頭辞を一括判定する。
    internal static bool IsDefaultExcludedFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;
        if (SkipFiles.Contains(fileName))
            return true;
        return fileName.StartsWith(AppleDoublePrefix, StringComparison.Ordinal);
    }
}
