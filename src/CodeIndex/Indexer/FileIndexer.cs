using System.Security.Cryptography;
using System.Text;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Scans directories for source files and builds FileRecords.
/// ディレクトリを走査してソースファイルからFileRecordを構築する。
/// </summary>
public class FileIndexer
{
    // Extension-to-language mapping / 拡張子→言語名マッピング
    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"]     = "python",
        [".js"]     = "javascript",
        [".ts"]     = "typescript",
        [".jsx"]    = "javascript",
        [".tsx"]    = "typescript",
        [".rb"]     = "ruby",
        [".go"]     = "go",
        [".rs"]     = "rust",
        [".java"]   = "java",
        [".kt"]     = "kotlin",
        [".swift"]  = "swift",
        [".c"]      = "c",
        [".cpp"]    = "cpp",
        [".cc"]     = "cpp",
        [".cxx"]    = "cpp",
        [".h"]      = "c",       // Could be C or C++; defaults to C for symbol extraction
        [".hpp"]    = "cpp",
        [".hxx"]    = "cpp",
        [".cs"]     = "csharp",
        [".cshtml"] = "csharp",  // Razor (ASP.NET MVC/Pages) / Razor テンプレート
        [".razor"]  = "csharp",  // Blazor component / Blazor コンポーネント
        [".php"]    = "php",
        [".sh"]     = "shell",
        [".sql"]    = "sql",
        [".md"]     = "markdown",
        [".yaml"]   = "yaml",
        [".yml"]    = "yaml",
        [".json"]   = "json",
        [".toml"]   = "toml",
        [".xaml"]   = "xml",    // WPF/MAUI/Avalonia XAML / XAML テンプレート
        [".axaml"]  = "xml",    // Avalonia XAML / Avalonia XAML
        [".csproj"] = "xml",    // C# project file / C# プロジェクトファイル
        [".fsproj"] = "xml",    // F# project file / F# プロジェクトファイル
        [".vbproj"] = "xml",    // VB.NET project file / VB.NET プロジェクトファイル
        [".props"]  = "xml",    // MSBuild props / MSBuild プロパティ
        [".targets"]= "xml",    // MSBuild targets / MSBuild ターゲット
        [".html"]   = "html",
        [".css"]    = "css",
        [".scss"]   = "css",
        [".vue"]    = "vue",
        [".svelte"] = "svelte",
        [".tf"]     = "terraform",
        [".dart"]   = "dart",
        [".scala"]  = "scala",
        [".sc"]     = "scala",
        [".r"]      = "r",
        [".R"]      = "r",
        [".ex"]     = "elixir",
        [".exs"]    = "elixir",
        [".lua"]    = "lua",
        [".fs"]     = "fsharp",
        [".fsx"]    = "fsharp",
        [".fsi"]    = "fsharp",
        [".vb"]     = "vb",
        [".vbs"]    = "vb",
        [".hs"]     = "haskell",
        [".lhs"]    = "haskell",
        [".zig"]    = "zig",
        [".proto"]  = "protobuf",  // Protocol Buffers / Protocol Buffers 定義
        [".graphql"]= "graphql",   // GraphQL schema/queries / GraphQL スキーマ・クエリ
        [".gql"]    = "graphql",
        [".gradle"] = "gradle",    // Gradle build scripts / Gradle ビルドスクリプト
        [".cmake"]  = "cmake",     // CMake scripts / CMake スクリプト
        [".ps1"]    = "powershell",// PowerShell scripts / PowerShell スクリプト
        [".psm1"]   = "powershell",// PowerShell modules / PowerShell モジュール
        [".psd1"]   = "powershell",// PowerShell data files / PowerShell データファイル
        [".bat"]    = "batch",     // Windows batch files / Windows バッチファイル
        [".cmd"]    = "batch",
        [".bash"]   = "shell",
        [".zsh"]    = "shell",
        [".fish"]   = "shell",
    };

    // Exact file names (case-insensitive) mapped to language / 完全一致ファイル名→言語マッピング
    private static readonly Dictionary<string, string> FileNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"]    = "dockerfile",
        ["Makefile"]      = "makefile",
        ["Justfile"]      = "justfile",     // Just command runner / Just コマンドランナー
        ["CMakeLists.txt"]= "cmake",
        ["Vagrantfile"]   = "ruby",         // Vagrant uses Ruby DSL / Vagrant は Ruby DSL
        [".editorconfig"] = "editorconfig",
        [".gitignore"]    = "gitignore",
        [".dockerignore"] = "dockerignore",
    };

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
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml",
        "Gemfile.lock", "Cargo.lock", "composer.lock", "poetry.lock", "bun.lockb",
    };

    // Maximum file size to index (10 MB) / インデックス対象の最大ファイルサイズ (10 MB)
    private const long MaxFileSize = 10 * 1024 * 1024;

    private readonly string _projectRoot;

    public FileIndexer(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
    }

    /// <summary>
    /// Return all file patterns (extensions and filenames) mapped to their language names.
    /// 全ファイルパターン（拡張子とファイル名）と対応する言語名のマッピングを返す。
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetLanguageExtensions()
    {
        // Merge extension map and filename map for a complete view
        // 完全な一覧のため拡張子マップとファイル名マップを統合
        var merged = new Dictionary<string, string>(LangMap, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lang) in FileNameMap)
            merged.TryAdd(name, lang);
        return merged;
    }

    public static string? DetectLanguage(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (LangMap.TryGetValue(ext, out var lang))
            return lang;

        // Fall back to exact file name matching / ファイル名の完全一致で言語を検出
        var fileName = Path.GetFileName(filePath);
        return FileNameMap.TryGetValue(fileName, out var nameLang) ? nameLang : null;
    }

    /// <summary>
    /// Enumerate all indexable files under the project root.
    /// プロジェクトルート以下のインデックス対象ファイルを列挙する。
    /// </summary>
    public IReadOnlyList<string> ScanFiles()
    {
        var files = new List<string>();
        ScanDirectory(_projectRoot, files);
        return files;
    }

    private void ScanDirectory(string dir, List<string> results)
    {
        // Check for skip directories / スキップ対象ディレクトリかチェック
        var dirName = Path.GetFileName(dir);
        if (SkipDirs.Contains(dirName))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var fileName = Path.GetFileName(file);

                // Skip excluded file names / 除外ファイル名をスキップ
                if (SkipFiles.Contains(fileName))
                    continue;

                // Include files with a known extension or known filename
                // 既知の拡張子または既知のファイル名のファイルを含める
                var ext = Path.GetExtension(file);
                if (LangMap.ContainsKey(ext) || FileNameMap.ContainsKey(fileName))
                    results.Add(file);
            }

            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                ScanDirectory(subDir, results);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip inaccessible directories / アクセス不可ディレクトリはスキップ
        }
        catch (IOException)
        {
            // Skip on I/O errors / I/Oエラー時はスキップ
        }
    }

    /// <summary>
    /// Build a FileRecord and return file content (avoids reading the file twice).
    /// FileRecordを構築しファイル内容も返す（二重読み込み防止）。
    /// </summary>
    public (FileRecord record, string content, string? warning) BuildRecord(string absolutePath)
    {
        var (record, content, _, warning) = BuildRecordWithRawBytes(absolutePath);
        return (record, content, warning);
    }

    /// <summary>
    /// Build a FileRecord and return both decoded content and raw bytes.
    /// Callers can run encoding validation without a second file read.
    /// FileRecordを構築し、デコード済み内容とraw bytesを返す。
    /// 呼び出し側は再読込なしでエンコーディング検証できる。
    /// </summary>
    public (FileRecord record, string content, byte[] rawBytes, string? warning) BuildRecordWithRawBytes(string absolutePath)
    {
        var relativePath = Path.GetRelativePath(_projectRoot, absolutePath);
        var info = new FileInfo(absolutePath);

        // Skip files exceeding size limit to avoid OutOfMemoryException
        // OOM防止のためサイズ上限を超えるファイルをスキップ
        if (info.Length > MaxFileSize)
            throw new InvalidOperationException($"File too large ({info.Length / 1024 / 1024} MB > {MaxFileSize / 1024 / 1024} MB limit)");

        // Read raw bytes and decode UTF-8; detect invalid sequences
        // 生バイト読み込み後UTF-8デコード、不正シーケンスを検出
        var bytes = File.ReadAllBytes(absolutePath);

        // Compute checksum from raw bytes to avoid re-encoding the string (~10MB saved for large files)
        // 文字列の再エンコードを回避するためraw bytesからチェックサムを算出（大ファイルで約10MB節約）
        var checksum = ComputeChecksum(bytes);

        string content;
        string? warning = null;
        try
        {
            content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Fall back to replacement mode but warn / 置換モードにフォールバックし警告
            content = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(bytes);
            warning = $"{relativePath}: contains invalid UTF-8 bytes (replaced with U+FFFD)";
        }
        // Normalize line endings to LF / 改行をLFに正規化
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");
        // Accurate line count: ignore trailing newline / 正確な行数: 末尾改行を無視
        var lines = content.EndsWith('\n')
            ? content[..^1].Split('\n')
            : content.Split('\n');

        var record = new FileRecord
        {
            Path = relativePath.Replace('\\', '/'),
            Lang = DetectLanguage(absolutePath),
            Size = info.Length,
            Lines = lines.Length,
            Checksum = checksum,
            Modified = info.LastWriteTimeUtc,
        };

        return (record, content, bytes, warning);
    }

    /// <summary>
    /// Validate file content for encoding issues.
    /// ファイル内容のエンコーディング問題を検証する。
    /// </summary>
    public static List<FileIssue> ValidateContent(string relativePath, byte[] rawBytes, string content)
    {
        var issues = new List<FileIssue>();

        // U+FFFD replacement characters baked into the file / ファイルに焼き付いたU+FFFD置換文字
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\uFFFD')
            {
                // Find line number / 行番号を特定
                var lineNum = content[..i].Count(c => c == '\n') + 1;
                issues.Add(new FileIssue
                {
                    Path = relativePath,
                    Kind = "replacement_char",
                    Line = lineNum,
                    Message = $"U+FFFD replacement character at line {lineNum}",
                });
                // Skip to next line to avoid reporting every char on the same line
                // 同じ行の連続報告を避けるため次の行までスキップ
                var nextNewline = content.IndexOf('\n', i);
                if (nextNewline >= 0) i = nextNewline;
            }
        }

        // BOM marker / BOMマーカー
        if (rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "bom",
                Line = 1,
                Message = "UTF-8 BOM marker detected",
            });
        }

        // NULL bytes (likely binary content) / NULLバイト（バイナリ混入の可能性）
        if (rawBytes.Any(b => b == 0))
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "null_byte",
                Line = 0,
                Message = "File contains NULL bytes (possible binary content)",
            });
        }

        // Mixed line endings — check raw bytes before LF normalization
        // 混在改行コード — LF正規化前のrawBytesで確認
        var hasCrlf = false;
        var hasLfOnly = false;
        for (int i = 0; i < rawBytes.Length; i++)
        {
            if (rawBytes[i] == 0x0D && i + 1 < rawBytes.Length && rawBytes[i + 1] == 0x0A)
            {
                hasCrlf = true;
                i++; // skip the LF after CR
            }
            else if (rawBytes[i] == 0x0A)
            {
                hasLfOnly = true;
            }
        }
        if (hasCrlf && hasLfOnly)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "mixed_line_endings",
                Line = 0,
                Message = "Mixed line endings (CRLF and LF)",
            });
        }

        return issues;
    }

    /// <summary>
    /// Compute SHA256 checksum from raw file bytes.
    /// ファイルのraw bytesからSHA256チェックサムを算出する。
    /// </summary>
    private static string ComputeChecksum(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
