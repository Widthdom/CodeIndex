using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;
using Microsoft.Win32.SafeHandles;

namespace CodeIndex.Indexer;

/// <summary>
/// Scans directories for source files and builds FileRecords.
/// ディレクトリを走査してソースファイルからFileRecordを構築する。
/// </summary>
public class FileIndexer
{
    internal const int MaxDanglingFileSystemEntryScanCandidates = 4096;
    internal static Func<string, bool>? FileSystemIgnoreCaseProbeForTesting { get; set; }
    internal static Func<string, FileSystemInfo?>? ResolveDirectoryLinkTargetForTesting { get; set; }

    public enum SymlinkPolicy
    {
        None,
        Internal,
        All,
    }

    internal enum FileProbeStatus
    {
        Supported,
        Unsupported,
        ProbeFailed,
        Missing,
    }

    internal readonly record struct LanguageDetectionResult(FileProbeStatus Status, string? Language);

    public enum ScanIssueSeverity
    {
        Warning,
        Error,
    }

    public readonly record struct ScanError(string Path, string Message, ScanIssueSeverity Severity = ScanIssueSeverity.Error)
    {
        public bool IsFatal => Severity == ScanIssueSeverity.Error;
    }

    internal readonly record struct FileIdentity(ulong DeviceId, ulong Inode);

    public readonly record struct ScanFilesResult(
        IReadOnlyList<string> Files,
        IReadOnlyList<ScanError> Errors,
        IReadOnlyList<string> NonIndexablePaths,
        IReadOnlyList<string> UnknownExtensionFiles,
        IReadOnlyList<string> ProbeFailedFilePaths,
        IReadOnlyList<string> ListedDirectories,
        IReadOnlyList<string> FullyScannedDirectories,
        IReadOnlySet<string> CheckpointedDirectories,
        IReadOnlyList<string> AncestorIgnoreDirectories,
        IReadOnlyList<string> AttributePrunedDirectories,
        IReadOnlyList<string> NestedRepositories,
        IReadOnlyList<string> DanglingSymlinks)
    {
        public bool HadErrors => Errors.Any(error => error.IsFatal);
    }

    internal enum PathFilterKind
    {
        None,
        IgnoredByRules,
        ExcludedByDefaultDirectory,
        ExcludedByDefaultFile,
        OutsideProjectRoot,
        IgnoreRulesUnavailable,
    }

    internal readonly record struct PathFilterResult(
        PathFilterKind FilterKind,
        IReadOnlyList<ScanError> Errors)
    {
        public bool ShouldSkip => FilterKind != PathFilterKind.None;
        public bool ShouldDeleteExisting => FilterKind is
            PathFilterKind.IgnoredByRules or
            PathFilterKind.ExcludedByDefaultDirectory or
            PathFilterKind.ExcludedByDefaultFile or
            PathFilterKind.OutsideProjectRoot;
    }

    private sealed class ProjectMarkerFingerprintTraversalState
    {
        public int DirectoriesVisited { get; set; }
        public int MarkerFilesCollected { get; set; }
        public bool Truncated { get; set; }
        public string TruncationReason { get; set; } = "unknown";
    }

    private readonly record struct ProjectMarkerFingerprintDirectory(string Path, IgnoreRuleSet IgnoreRules, bool IsProjectRoot);

    internal readonly record struct ProjectMarkerFingerprintResult(string? Fingerprint, bool IsComplete)
    {
        public IReadOnlyList<ScanError> Warnings { get; init; } = [];
    }

    private static readonly string[] HotspotFamilyMarkerLanguages = ["csharp", "vb", "fsharp", "msbuild"];
    private const int ConflictMarkerScanLimitBytes = 50 * 1024;
    private const int DockerfileJsonFormIssueLimit = 32;
    private const int MaxDirectoryTraversalDepth = 128;
    private const int GitLfsPointerMaxBytes = 1024;
    private const int MaxGitmodulesBytes = 256 * 1024;
    private const int MaxGitmodulesLines = 4096;
    private const int MaxGitmodulesLineChars = 16 * 1024;
    internal const int MaxGitmodulesSubmodulePaths = 1024;
    private const int MaxProjectMarkerTraversalWarnings = 32;
    private static readonly string[] IgnoreFileNames = [".gitignore", ".cdidxignore"];
    private const int MaxIgnoreFileBytes = 256 * 1024;
    private const int MaxIgnoreFileLines = 8192;
    private const int MaxIgnoreRulesPerFile = 4096;
    private const int MaxProjectMarkerFingerprintDirectories = 8192;
    private const int MaxProjectMarkerFingerprintFiles = 4096;
    private const int MaxIgnorePatternLength = 512;
    private static readonly TimeSpan IgnoreRegexMatchTimeout = TimeSpan.FromMilliseconds(100);
    // Extension-to-language mapping / 拡張子→言語名マッピング
    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "python",
        [".pyi"] = "python",  // Python type stub (PEP 561) / Python 型スタブ
        [".pyw"] = "python",  // Windowed Python script / Windows 用 Python スクリプト
        // Cython keeps its own bucket because it extends Python with native declarations
        // (`cdef`, `cpdef`, `cimport`) that need Cython-specific symbol patterns.
        // Cython は `cdef` / `cpdef` / `cimport` など Python を拡張した native 宣言を持つため、
        // Python ではなく Cython 専用の symbol pattern を使う独立 bucket にする。
        [".pyx"] = "cython",  // Cython source / Cython ソース
        [".pxd"] = "cython",  // Cython declaration / Cython 宣言
        [".js"] = "javascript",
        [".cjs"] = "javascript",
        [".mjs"] = "javascript",
        [".ts"] = "typescript",
        [".cts"] = "typescript",
        [".mts"] = "typescript",
        [".jsx"] = "javascript",
        [".tsx"] = "typescript",
        [".rb"] = "ruby",
        [".rake"] = "ruby",    // Rake tasks / Rake タスク
        [".gemspec"] = "ruby",    // RubyGems spec / RubyGems スペック
        [".podspec"] = "ruby",    // CocoaPods spec (Ruby DSL) / CocoaPods スペック
        [".groovy"] = "groovy",
        [".gvy"] = "groovy",
        [".gy"] = "groovy",
        [".gsh"] = "groovy",
        [".go"] = "go",
        [".rs"] = "rust",
        [".java"] = "java",
        [".kt"] = "kotlin",
        [".kts"] = "kotlin",  // Kotlin Script / Kotlin スクリプト (Gradle Kotlin DSL など)
        [".swift"] = "swift",
        [".cu"] = "cuda",
        [".cuh"] = "cuda",
        [".glsl"] = "glsl",
        [".vert"] = "glsl",
        [".frag"] = "glsl",
        [".hlsl"] = "hlsl",
        [".wgsl"] = "wgsl",
        [".metal"] = "metal",
        [".c"] = "c",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".cxx"] = "cpp",
        [".h"] = "c",       // Could be C or C++; defaults to C for symbol extraction
        [".hh"] = "cpp",
        [".hpp"] = "cpp",
        [".hxx"] = "cpp",
        [".cs"] = "csharp",
        [".cshtml"] = "csharp",  // Razor (ASP.NET MVC/Pages) / Razor テンプレート
        [".razor"] = "csharp",  // Blazor component / Blazor コンポーネント
        [".m"] = "objc",
        [".mm"] = "objc",
        [".php"] = "php",
        [".s"] = "assembly", // Also used by Scheme; assembly is the more common default.
        [".S"] = "assembly",
        [".asm"] = "assembly",
        [".nasm"] = "assembly",
        [".sh"] = "shell",
        [".sql"] = "sql",
        [".pgsql"] = "sql",     // PostgreSQL dialect / PostgreSQL 方言
        [".tsql"] = "sql",     // T-SQL (SQL Server) / T-SQL (SQL Server)
        [".plsql"] = "sql",     // PL/SQL (Oracle) / PL/SQL (Oracle)
        [".pls"] = "sql",     // PL/SQL script (Oracle) / PL/SQL スクリプト (Oracle)
        [".pks"] = "sql",     // PL/SQL package spec (Oracle) / PL/SQL パッケージ仕様 (Oracle)
        [".pkb"] = "sql",     // PL/SQL package body (Oracle) / PL/SQL パッケージ本体 (Oracle)
        [".plb"] = "sql",     // PL/SQL wrapped source (Oracle) / PL/SQL ラップ済みソース (Oracle)
        [".psql"] = "sql",     // psql scripts / psql スクリプト
        [".md"] = "markdown",
        [".yaml"] = "yaml",
        [".yml"] = "yaml",
        [".json"] = "json",
        [".toml"] = "toml",
        [".xaml"] = "xml",    // WPF/MAUI/Avalonia XAML / XAML テンプレート
        [".axaml"] = "xml",    // Avalonia XAML / Avalonia XAML
        [".sln"] = "solution", // Visual Studio solution / Visual Studio ソリューション
        [".manifest"] = "app_manifest", // Windows application manifest / Windows アプリケーションマニフェスト
        [".csproj"] = "msbuild",// C# project file / C# プロジェクトファイル
        [".fsproj"] = "msbuild",// F# project file / F# プロジェクトファイル
        [".vbproj"] = "msbuild",// VB.NET project file / VB.NET プロジェクトファイル
        [".props"] = "msbuild",// MSBuild props / MSBuild プロパティ
        [".targets"] = "msbuild",// MSBuild targets / MSBuild ターゲット
        [".html"] = "html",
        [".htm"] = "html",    // Legacy / Windows / IIS default / 旧来の Windows / IIS 既定拡張子
        [".xhtml"] = "html",    // XHTML / XHTML
        [".shtml"] = "html",    // Server-side includes / サーバサイドインクルード
        [".css"] = "css",
        [".scss"] = "css",
        [".less"] = "css",    // Less preprocessor / Less プリプロセッサ
        [".pcss"] = "css",    // PostCSS / PostCSS
        // Sass indented syntax / Stylus use indentation instead of braces, so they live in
        // separate buckets with conservative line-level extractors rather than the CSS
        // brace-scoped extractor.
        // Sass インデント構文と Stylus は波括弧ではなくインデントで構造化するため、
        // CSS の波括弧スコープ抽出ではなく、保守的な行単位抽出を持つ別バケットに分ける。
        [".sass"] = "sass",
        [".styl"] = "stylus",
        [".vue"] = "vue",
        [".svelte"] = "svelte",
        [".tf"] = "terraform",
        [".v"] = "verilog",  // Verilog defaults here; SystemVerilog has its own extensions.
        [".sv"] = "systemverilog",
        [".svh"] = "systemverilog",
        [".vhd"] = "vhdl",
        [".vhdl"] = "vhdl",
        [".lisp"] = "commonlisp",
        [".lsp"] = "commonlisp",
        [".cl"] = "commonlisp", // Common Lisp wins the default over OpenCL here.
        [".rkt"] = "racket",
        [".pas"] = "pascal",
        [".pp"] = "pascal",
        [".dpr"] = "pascal",
        [".st"] = "smalltalk",
        [".smalltalk"] = "smalltalk",
        [".ada"] = "ada",
        [".adb"] = "ada",
        [".ads"] = "ada",
        [".f"] = "fortran",
        [".f77"] = "fortran",
        [".f90"] = "fortran",
        [".f95"] = "fortran",
        [".f03"] = "fortran",
        [".f08"] = "fortran",
        [".for"] = "fortran",
        [".ftn"] = "fortran",
        [".cbl"] = "cobol",
        [".cob"] = "cobol",
        [".cobol"] = "cobol",
        [".cpy"] = "cobol",   // COBOL copybook / COBOL コピー句
        [".raku"] = "raku",
        [".rakumod"] = "raku",
        [".rakutest"] = "raku",
        [".t"] = "perl",    // Common Perl test scripts / Perl の test スクリプト
        [".dart"] = "dart",
        [".scala"] = "scala",
        [".sc"] = "scala",
        [".r"] = "r",
        [".R"] = "r",
        [".ex"] = "elixir",
        [".exs"] = "elixir",
        [".lua"] = "lua",
        [".ml"] = "ocaml",
        [".mli"] = "ocaml",
        [".cr"] = "crystal",
        [".clj"] = "clojure",
        [".cljs"] = "clojure",
        [".cljc"] = "clojure",
        [".edn"] = "clojure",
        [".d"] = "d",
        [".erl"] = "erlang",
        [".hrl"] = "erlang",
        [".jl"] = "julia",
        [".nim"] = "nim",
        [".nims"] = "nim",
        [".pl"] = "perl",
        [".pm"] = "perl",
        [".pod"] = "perl",
        [".psgi"] = "perl",
        [".cgi"] = "perl",
        [".fcgi"] = "perl",
        [".t"] = "perl",
        [".sol"] = "solidity",
        [".tcl"] = "tcl",
        [".tk"] = "tcl",
        [".fs"] = "fsharp",
        [".fsx"] = "fsharp",
        [".fsi"] = "fsharp",
        [".bas"] = "vb",
        [".cls"] = "vb",
        [".ctl"] = "vb",
        [".dob"] = "vb",
        [".dsr"] = "vb",
        [".frm"] = "vb",
        [".pag"] = "vb",
        [".vba"] = "vb",
        [".vb"] = "vb",
        [".vbhtml"] = "vb",
        [".vbs"] = "vb",
        [".hs"] = "haskell",
        [".lhs"] = "haskell",
        [".zig"] = "zig",
        [".proto"] = "protobuf",  // Protocol Buffers / Protocol Buffers 定義
        [".graphql"] = "graphql",   // GraphQL schema/queries / GraphQL スキーマ・クエリ
        [".gql"] = "graphql",
        [".gradle"] = "gradle",    // Gradle build scripts / Gradle ビルドスクリプト
        [".cmake"] = "cmake",     // CMake scripts / CMake スクリプト
        [".mk"] = "makefile",  // Makefile fragment / Makefile フラグメント
        [".ps1"] = "powershell",// PowerShell scripts / PowerShell スクリプト
        [".psm1"] = "powershell",// PowerShell modules / PowerShell モジュール
        [".psd1"] = "powershell",// PowerShell data files / PowerShell データファイル
        [".bat"] = "batch",     // Windows batch files / Windows バッチファイル
        [".cmd"] = "batch",
        [".bash"] = "shell",
        [".zsh"] = "shell",
        [".fish"] = "shell",
        [".dockerfile"] = "dockerfile", // Suffix-style Dockerfile names such as app.Dockerfile / app.Dockerfile 形式
        [".containerfile"] = "dockerfile", // Suffix-style Containerfile names such as app.Containerfile / app.Containerfile 形式
    };

    private static readonly (string Pattern, string Language)[] DisplayOnlyLanguageExtensions =
    [
        (".S", "assembly"),
    ];

    // Exact file names (case-insensitive) mapped to language / 完全一致ファイル名→言語マッピング
    private static readonly Dictionary<string, string> FileNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile",
        [".dockerfile"] = "dockerfile",
        ["Containerfile"] = "dockerfile",   // Podman's Dockerfile alternative / Podman の Dockerfile 代替
        [".containerfile"] = "dockerfile",
        ["Makefile"] = "makefile",
        ["GNUmakefile"] = "makefile",     // GNU Make explicit filename / GNU Make 明示ファイル名
        ["Justfile"] = "justfile",     // Just command runner / Just コマンドランナー
        ["CMakeLists.txt"] = "cmake",
        ["Vagrantfile"] = "ruby",         // Vagrant uses Ruby DSL / Vagrant は Ruby DSL
        ["Gemfile"] = "dependency_manifest", // Bundler dependency manifest / Bundler 依存マニフェスト
        ["Rakefile"] = "ruby",         // Rake task runner / Rake タスクランナー
        ["Podfile"] = "dependency_manifest", // CocoaPods dependency manifest / CocoaPods 依存マニフェスト
        ["Guardfile"] = "ruby",         // Guard file-watcher / Guard ファイルウォッチャー
        ["Capfile"] = "ruby",         // Capistrano deployment / Capistrano デプロイ
        ["NAMESPACE"] = "r",            // R package namespace directives / R パッケージ namespace ディレクティブ
        [".Rprofile"] = "r",            // R startup profile / R 起動プロファイル
        ["Rprofile.site"] = "r",            // Site-wide R startup profile / サイト共通 R 起動プロファイル
        ["BUILD"] = "python",       // Bazel Starlark build file / Bazel Starlark ビルドファイル
        ["BUILD.bazel"] = "python",
        ["WORKSPACE"] = "python",       // Bazel workspace / Bazel ワークスペース
        ["WORKSPACE.bazel"] = "python",
        ["package.json"] = "dependency_manifest", // npm package manifest / npm パッケージマニフェスト
        ["pyproject.toml"] = "dependency_manifest", // Python project manifest / Python プロジェクトマニフェスト
        ["requirements.txt"] = "dependency_manifest", // Python dependencies manifest / Python 依存関係マニフェスト
        ["Pipfile"] = "dependency_manifest", // Pipenv manifest / Pipenv マニフェスト
        ["poetry.toml"] = "dependency_manifest", // Poetry configuration manifest / Poetry 設定マニフェスト
        ["Cargo.toml"] = "dependency_manifest", // Cargo package manifest / Cargo パッケージマニフェスト
        ["composer.json"] = "dependency_manifest", // Composer package manifest / Composer パッケージマニフェスト
        ["go.mod"] = "dependency_manifest", // Go module manifest / Go モジュールマニフェスト
        ["go.work"] = "dependency_manifest", // Go workspace manifest / Go ワークスペースマニフェスト
        ["packages.config"] = "dependency_manifest", // NuGet packages.config manifest / NuGet packages.config マニフェスト
        ["Directory.Packages.props"] = "dependency_manifest", // NuGet central package manifest / NuGet central package マニフェスト
        ["package-lock.json"] = "dependency_lock", // npm lockfile / npm lockfile
        ["npm-shrinkwrap.json"] = "dependency_lock", // npm shrinkwrap lockfile / npm shrinkwrap lockfile
        ["yarn.lock"] = "dependency_lock", // Yarn lockfile / Yarn lockfile
        ["pnpm-lock.yaml"] = "dependency_lock", // pnpm lockfile / pnpm lockfile
        ["bun.lock"] = "dependency_lock", // Bun text lockfile / Bun text lockfile
        ["bun.lockb"] = "dependency_lock", // Bun binary lockfile / Bun binary lockfile
        ["Gemfile.lock"] = "dependency_lock", // Bundler lockfile / Bundler lockfile
        ["Cargo.lock"] = "dependency_lock", // Cargo lockfile / Cargo lockfile
        ["composer.lock"] = "dependency_lock", // Composer lockfile / Composer lockfile
        ["poetry.lock"] = "dependency_lock", // Poetry lockfile / Poetry lockfile
        ["Pipfile.lock"] = "dependency_lock", // Pipenv lockfile / Pipenv lockfile
        ["go.sum"] = "dependency_lock", // Go module checksum lockfile / Go module checksum lockfile
        ["uv.lock"] = "dependency_lock", // uv lockfile / uv lockfile
        ["packages.lock.json"] = "dependency_lock", // NuGet lockfile / NuGet lockfile
        [".editorconfig"] = "editorconfig",
        [".gitignore"] = "gitignore",
        [".dockerignore"] = "dockerignore",
    };

    // Filename prefixes (with trailing dot) mapped to language for suffixed variants like
    // Dockerfile.dev / Makefile.common / GNUmakefile.am. The suffix must be non-empty.
    // Dockerfile.dev / Makefile.common / GNUmakefile.am のようにサフィックス付きで使われる
    // ファイル名のプレフィックス→言語マッピング。サフィックスは1文字以上必須。
    private static readonly (string Prefix, string Language)[] FileNamePrefixMap =
    [
        ("Dockerfile.",  "dockerfile"),
        ("Dockerfile-",  "dockerfile"),
        ("Dockerfile_",  "dockerfile"),
        ("Containerfile.", "dockerfile"),
        ("Containerfile-", "dockerfile"),
        ("Containerfile_", "dockerfile"),
        ("Makefile.",    "makefile"),
        ("GNUmakefile.", "makefile"),
    ];

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

    public const string MaxFileSizeEnvironmentVariable = "CDIDX_MAX_FILE_BYTES";
    // Default maximum file size to index (4 MiB). Larger generated/vendor payloads
    // can still be opted in with --max-file-bytes, but the default path should not
    // allocate a single multi-megabyte byte[] for common source scans.
    // インデックス対象の既定最大ファイルサイズ (4 MiB)。生成物や vendor の大容量 payload は
    // --max-file-bytes で明示的に opt-in できるが、既定経路では一般的な source scan で
    // multi-MB の単一 byte[] を確保しない。
    public const long DefaultMaxFileSizeBytes = 4 * 1024 * 1024;
    // Extensionless shebang detection reads at most the first physical line within this
    // byte cap. NUL bytes or a line that reaches the cap without LF/CR are treated as
    // unsupported so binary executables and minified data are not parsed as scripts.
    private const int ShebangProbeByteLimit = 256;

    private readonly string _projectRoot;
    private readonly string _ignoreRuleRoot;
    private readonly IReadOnlyList<string> _ancestorIgnoreDirectories;
    private readonly bool _ignoreCase;
    private readonly Func<string, bool?> _directoryIgnoreCaseProbe;
    private readonly Func<string, IEnumerable<string>> _enumerateFiles;
    private readonly Dictionary<string, bool> _directoryIgnoreCaseCache;
    private readonly long _maxFileSizeBytes;
    private readonly FileContentLoader _contentLoader;
    private readonly SymlinkPolicy _symlinkPolicy;
    private readonly int _maxDanglingFileSystemEntryScanCandidates;
    private readonly GeneratedCodePatternMatcher _generatedCodePatterns;
    // Submodule working-tree paths declared in <ignoreRuleRoot>/.gitmodules, relative to
    // _projectRoot and slash-normalized. Used to override SkipDirs so that submodules
    // hosted under SkipDirs-named directories (e.g. vendor/foo) remain visible to the
    // indexer. Empty when .gitmodules is missing or unreadable.
    // <ignoreRuleRoot>/.gitmodules で宣言された submodule のワークツリーパス（_projectRoot 相対、
    // スラッシュ正規化済み）。vendor/foo のように SkipDirs 名のディレクトリ配下にある submodule を
    // 可視化するため SkipDirs を上書きする。.gitmodules が無い・読めない場合は空。
    private readonly HashSet<string> _submodulePaths;
    // Ancestor path prefixes of every entry in _submodulePaths (exclusive of the submodule
    // itself). When such an ancestor matches SkipDirs we pass through it without indexing
    // its direct files, descending only into the submodule branch.
    // _submodulePaths 各要素の祖先パス（submodule 自身は含まない）。SkipDirs 名と一致した場合は
    // 通過モードとしてその直下ファイルを索引せず、submodule 方向のみ降りる。
    private readonly HashSet<string> _submoduleAncestorPaths;
    private readonly IReadOnlyList<ScanError> _submoduleLoadWarnings;

    internal static Func<string, IEnumerable<string>>? EnumerateProjectMarkerDirectoriesForTesting { get; set; }
    internal static Func<string, IReadOnlyList<string>>? ReadGitmodulesLinesForTesting { get; set; }

    private sealed class IgnoreRuleSet
    {
        internal static readonly IgnoreRuleSet Empty = new(null, []);

        private readonly IgnoreRuleSet? _parent;
        private readonly IReadOnlyList<IgnoreRule> _rules;

        private IgnoreRuleSet(IgnoreRuleSet? parent, IReadOnlyList<IgnoreRule> rules)
        {
            _parent = parent;
            _rules = rules;
        }

        internal static IgnoreRuleSet CreateChild(IgnoreRuleSet parent, IReadOnlyList<IgnoreRule> rules)
            => rules.Count == 0 ? parent : new IgnoreRuleSet(parent, rules);

        internal bool IsIgnored(string absolutePath, bool isDirectory)
        {
            var ignored = _parent?.IsIgnored(absolutePath, isDirectory) ?? false;
            foreach (var rule in _rules)
            {
                if (rule.IsMatch(absolutePath, isDirectory))
                    ignored = !rule.Negated;
            }

            return ignored;
        }
    }

    private readonly record struct IgnoreRuleLoadResult(
        IgnoreRuleSet Rules,
        bool IgnoreRulesAvailable);

    private sealed record DirectoryScanState(
        List<string> Results,
        List<ScanError> Errors,
        HashSet<string> NonIndexablePaths,
        HashSet<string> UnknownExtensionFiles,
        HashSet<string> ProbeFailedFilePaths,
        HashSet<string> ListedDirectories,
        HashSet<string> FullyScannedDirectories,
        HashSet<string> CheckpointedDirectories,
        HashSet<string> AttributePrunedDirectories,
        HashSet<string> NestedRepositories,
        HashSet<string> DanglingSymlinks,
        HashSet<FileIdentity> VisitedFileIdentities,
        HashSet<string> VisitedDirectories);

    private sealed class IgnoreRule
    {
        private readonly record struct PatternToken(char Value, bool Escaped);

        private readonly string _sourceDirectory;
        private readonly Regex _matcher;
        private readonly bool _asciiIgnoreCase;
        private readonly bool _directoryOnly;
        private readonly bool _matchBasenameOnly;

        private IgnoreRule(
            string sourceDirectory,
            Regex matcher,
            bool asciiIgnoreCase,
            bool negated,
            bool directoryOnly,
            bool matchBasenameOnly)
        {
            _sourceDirectory = sourceDirectory;
            _matcher = matcher;
            _asciiIgnoreCase = asciiIgnoreCase;
            Negated = negated;
            _directoryOnly = directoryOnly;
            _matchBasenameOnly = matchBasenameOnly;
        }

        internal bool Negated { get; }

        internal static bool TryParse(string sourceDirectory, string rawLine, bool ignoreCase, out IgnoreRule? rule, out string? errorMessage)
        {
            rule = null;
            errorMessage = null;
            if (!TryTokenize(rawLine, out var tokens))
                return false;

            if (tokens.Count > MaxIgnorePatternLength)
            {
                errorMessage = $"Invalid ignore rule skipped: pattern exceeds {MaxIgnorePatternLength} characters";
                return false;
            }

            if (tokens[0] is { Value: '#', Escaped: false })
                return false;

            var negated = false;
            if (tokens[0] is { Value: '!', Escaped: false })
            {
                negated = true;
                tokens.RemoveAt(0);
            }

            if (tokens.Count == 0)
                return false;

            var directoryOnly = tokens[^1] is { Value: '/', Escaped: false };
            if (directoryOnly)
                tokens.RemoveAt(tokens.Count - 1);

            if (tokens.Count == 0)
                return false;

            var anchoredToSourceDirectory = tokens[0] is { Value: '/', Escaped: false };
            if (anchoredToSourceDirectory)
                tokens.RemoveAt(0);

            if (tokens.Count == 0)
                return false;

            var matchBasenameOnly = !anchoredToSourceDirectory && !tokens.Any(token => token is { Value: '/', Escaped: false });
            try
            {
                if (ignoreCase)
                    tokens = FoldAsciiTokens(tokens);

                var matcher = BuildMatcher(tokens, ignoreCase);
                rule = new IgnoreRule(sourceDirectory, matcher, ignoreCase, negated, directoryOnly, matchBasenameOnly);
                return true;
            }
            catch (ArgumentException ex)
            {
                errorMessage = $"Invalid ignore rule skipped: {ex.Message}";
                return false;
            }
        }

        internal bool IsMatch(string absolutePath, bool isDirectory)
        {
            if (_directoryOnly && !isDirectory)
                return false;

            var relativePath = NormalizeIgnorePath(Path.GetRelativePath(_sourceDirectory, absolutePath));
            if (relativePath.Length == 0 ||
                relativePath == "." ||
                relativePath.StartsWith("../", StringComparison.Ordinal))
            {
                return false;
            }

            var candidate = _matchBasenameOnly
                ? Path.GetFileName(relativePath)
                : relativePath;

            if (string.IsNullOrEmpty(candidate))
                return false;

            if (_asciiIgnoreCase)
                candidate = FoldAscii(candidate);

            return _matcher.IsMatch(candidate);
        }

        private static bool TryTokenize(string rawLine, out List<PatternToken> tokens)
        {
            tokens = [];
            if (string.IsNullOrEmpty(rawLine))
                return false;

            var escaping = false;
            foreach (var ch in rawLine)
            {
                if (escaping)
                {
                    tokens.Add(new PatternToken(ch, Escaped: true));
                    escaping = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaping = true;
                    continue;
                }

                tokens.Add(new PatternToken(ch, Escaped: false));
            }

            if (escaping)
                tokens.Add(new PatternToken('\\', Escaped: false));

            while (tokens.Count > 0 && tokens[^1] is { Value: ' ' or '\t', Escaped: false })
                tokens.RemoveAt(tokens.Count - 1);
            while (tokens.Count > 0 && tokens[0] is { Value: ' ' or '\t', Escaped: false })
                tokens.RemoveAt(0);

            return tokens.Count > 0;
        }

        private static Regex BuildMatcher(IReadOnlyList<PatternToken> pattern, bool ignoreCase)
        {
            var builder = new StringBuilder();
            builder.Append('^');

            for (var i = 0; i < pattern.Count; i++)
            {
                var token = pattern[i];
                var ch = token.Value;
                if (token.Escaped)
                {
                    builder.Append(Regex.Escape(ch.ToString()));
                    continue;
                }

                if (ch == '*')
                {
                    var isDoubleStar = i + 1 < pattern.Count && pattern[i + 1] is { Value: '*', Escaped: false };
                    if (isDoubleStar)
                    {
                        var nextChar = i + 2 < pattern.Count ? pattern[i + 2].Value : '\0';
                        if (nextChar == '/')
                        {
                            builder.Append("(?:[^/]+/)*");
                            i += 2;
                            continue;
                        }

                        if (i > 0 &&
                            pattern[i - 1] is { Value: '/', Escaped: false } &&
                            i + 2 == pattern.Count)
                        {
                            builder.Length -= 1;
                            builder.Append("/.*");
                            i++;
                            continue;
                        }

                        builder.Append("[^/]*");
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    if (isDoubleStar)
                        i++;
                    continue;
                }

                if (ch == '?')
                {
                    builder.Append("[^/]");
                    continue;
                }

                if (ch == '[' && TryBuildCharacterClass(pattern, ref i, builder, ignoreCase))
                    continue;

                builder.Append(Regex.Escape(ch.ToString()));
            }

            builder.Append('$');
            return new Regex(
                builder.ToString(),
                RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.NonBacktracking,
                IgnoreRegexMatchTimeout);
        }

        private static bool TryBuildCharacterClass(IReadOnlyList<PatternToken> pattern, ref int index, StringBuilder builder, bool ignoreCase)
        {
            var contentStart = index + 1;
            if (contentStart >= pattern.Count)
                throw new ArgumentException("malformed character class");

            if (pattern[contentStart] is { Value: '!', Escaped: false })
            {
                contentStart++;
            }
            else if (pattern[contentStart] is { Value: '^', Escaped: false })
            {
                contentStart++;
            }

            if (contentStart >= pattern.Count)
                throw new ArgumentException("malformed character class");

            var allowLeadingRightBracket =
                contentStart < pattern.Count &&
                pattern[contentStart] is { Value: ']', Escaped: false };

            var scanStart = allowLeadingRightBracket ? contentStart + 1 : contentStart;
            var closingIndex = FindCharacterClassClosingIndex(pattern, scanStart);

            if (closingIndex < scanStart)
                throw new ArgumentException("malformed character class");

            builder.Append('[');
            if (pattern[index + 1] is { Value: '!', Escaped: false })
            {
                builder.Append('^');
            }
            else if (pattern[index + 1] is { Value: '^', Escaped: false })
            {
                builder.Append('^');
            }

            if (allowLeadingRightBracket)
            {
                builder.Append(@"\]");
                contentStart++;
            }

            for (var i = contentStart; i < closingIndex; i++)
            {
                var token = pattern[i];
                var ch = token.Value;
                if (token.Escaped)
                {
                    AppendCharacterClassLiteral(builder, ch, ignoreCase);
                    continue;
                }

                if (ch == '[' && TryAppendPosixCharacterClass(pattern, closingIndex, ref i, builder, ignoreCase))
                    continue;

                if (i + 2 < closingIndex &&
                    pattern[i + 1] is { Value: '-', Escaped: false })
                {
                    var endToken = pattern[i + 2];
                    if (!endToken.Escaped &&
                        TryAppendCharacterClassRange(builder, ch, endToken.Value, ignoreCase))
                    {
                        i += 2;
                        continue;
                    }
                }

                if (ch is '\\' or '[' or ']')
                {
                    builder.Append('\\');
                    builder.Append(ch);
                    continue;
                }

                AppendCharacterClassLiteral(builder, ch, ignoreCase);
            }

            builder.Append(']');
            index = closingIndex;
            return true;
        }

        private static int FindCharacterClassClosingIndex(IReadOnlyList<PatternToken> pattern, int scanStart)
        {
            for (var i = scanStart; i < pattern.Count; i++)
            {
                if (pattern[i].Escaped)
                    continue;

                if (pattern[i].Value == '[' && TryFindPosixCharacterClassEnd(pattern, i, out var posixEnd))
                {
                    i = posixEnd;
                    continue;
                }

                if (pattern[i].Value == ']')
                    return i;
            }

            return -1;
        }

        private static bool TryAppendPosixCharacterClass(IReadOnlyList<PatternToken> pattern, int closingIndex, ref int index, StringBuilder builder, bool ignoreCase)
        {
            if (!TryFindPosixCharacterClassEnd(pattern, index, out var posixEnd) || posixEnd >= closingIndex)
                return false;

            var nameChars = new StringBuilder();
            for (var i = index + 2; i < posixEnd - 1; i++)
                nameChars.Append(pattern[i].Value);

            builder.Append(GetPosixCharacterClassPattern(nameChars.ToString(), ignoreCase));
            index = posixEnd;
            return true;
        }

        private static bool TryFindPosixCharacterClassEnd(IReadOnlyList<PatternToken> pattern, int startIndex, out int endIndex)
        {
            endIndex = -1;
            if (startIndex + 3 >= pattern.Count ||
                pattern[startIndex] is not { Value: '[', Escaped: false } ||
                pattern[startIndex + 1] is not { Value: ':', Escaped: false })
            {
                return false;
            }

            for (var i = startIndex + 2; i + 1 < pattern.Count; i++)
            {
                if (pattern[i] is { Value: ':', Escaped: false } &&
                    pattern[i + 1] is { Value: ']', Escaped: false })
                {
                    endIndex = i + 1;
                    return true;
                }
            }

            return false;
        }

        private static string GetPosixCharacterClassPattern(string className, bool ignoreCase)
            => className switch
            {
                "alnum" => "A-Za-z0-9",
                "alpha" => "A-Za-z",
                "blank" => " \t",
                "cntrl" => @"\x00-\x1F\x7F",
                "digit" => "0-9",
                "graph" => "!-~",
                "lower" => ignoreCase ? "A-Za-z" : "a-z",
                "print" => " -~",
                "punct" => @"!-/:-@\[-`\{-~",
                "space" => " \t\r\n\v\f",
                "upper" => ignoreCase ? "A-Za-z" : "A-Z",
                "xdigit" => "0-9A-Fa-f",
                _ => throw new ArgumentException($"unsupported POSIX character class '{className}'"),
            };

        private static string EscapeCharacterClassLiteral(char ch)
            => ch switch
            {
                '\\' or '[' or ']' or '^' or '-' => $@"\{ch}",
                _ => ch.ToString(),
            };

        private static void AppendCharacterClassLiteral(StringBuilder builder, char ch, bool ignoreCase)
        {
            if (ignoreCase && IsAsciiLetter(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                builder.Append(char.ToUpperInvariant(ch));
                return;
            }

            builder.Append(EscapeCharacterClassLiteral(ch));
        }

        private static bool TryAppendCharacterClassRange(StringBuilder builder, char start, char end, bool ignoreCase)
        {
            if (start > end)
                throw new ArgumentException("reversed character class range");

            builder.Append(EscapeCharacterClassLiteral(start));
            builder.Append('-');
            builder.Append(EscapeCharacterClassLiteral(end));

            if (!ignoreCase ||
                !IsAsciiLetter(start) ||
                !IsAsciiLetter(end))
            {
                return true;
            }

            var lowerStart = char.ToLowerInvariant(start);
            var lowerEnd = char.ToLowerInvariant(end);
            var upperStart = char.ToUpperInvariant(start);
            var upperEnd = char.ToUpperInvariant(end);

            if (lowerStart == start && lowerEnd == end)
            {
                builder.Append(char.ToUpperInvariant(start));
                builder.Append('-');
                builder.Append(char.ToUpperInvariant(end));
                return true;
            }

            if (upperStart == start && upperEnd == end)
            {
                builder.Append(char.ToLowerInvariant(start));
                builder.Append('-');
                builder.Append(char.ToLowerInvariant(end));
                return true;
            }

            return true;
        }

        private static List<PatternToken> FoldAsciiTokens(IReadOnlyList<PatternToken> tokens)
            => tokens
                .Select(token => new PatternToken(FoldAsciiChar(token.Value), token.Escaped))
                .ToList();

        private static string FoldAscii(string value)
        {
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                chars[i] = FoldAsciiChar(chars[i]);
            return new string(chars);
        }

        private static char FoldAsciiChar(char ch)
            => ch is >= 'A' and <= 'Z'
                ? char.ToLowerInvariant(ch)
                : ch;

        private static bool IsAsciiLetter(char ch)
            => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
    }

    public FileIndexer(string projectRoot)
        : this(projectRoot, ignoreCase: ProbeFileSystemIgnoreCase(projectRoot), ignoreRuleRoot: null)
    {
    }

    public FileIndexer(string projectRoot, bool ignoreCase)
        : this(projectRoot, ignoreCase, ignoreRuleRoot: null)
    {
    }

    public FileIndexer(
        string projectRoot,
        bool ignoreCase,
        string? ignoreRuleRoot,
        long? maxFileSizeBytes = null,
        IReadOnlyList<string>? generatedCodePatterns = null)
        : this(projectRoot, ignoreCase, ignoreRuleRoot, maxFileSizeBytes, directoryIgnoreCaseProbe: null, generatedCodePatterns: generatedCodePatterns)
    {
    }

    internal FileIndexer(
        string projectRoot,
        bool ignoreCase,
        string? ignoreRuleRoot,
        long? maxFileSizeBytes,
        Func<string, bool?>? directoryIgnoreCaseProbe,
        Func<string, IEnumerable<string>>? enumerateFiles = null,
        SymlinkPolicy symlinkPolicy = SymlinkPolicy.None,
        int? maxDanglingFileSystemEntryScanCandidates = null,
        IReadOnlyList<string>? generatedCodePatterns = null)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _ignoreRuleRoot = NormalizeIgnoreRuleRoot(ignoreRuleRoot);
        _ancestorIgnoreDirectories = BuildAncestorIgnoreDirectories(_ignoreRuleRoot, _projectRoot);
        _ignoreCase = ignoreCase;
        _directoryIgnoreCaseProbe = directoryIgnoreCaseProbe ?? ProbeExistingDirectoryIgnoreCase;
        _enumerateFiles = enumerateFiles ?? (dir => Directory.EnumerateFiles(LongPath.EnsureWindowsPrefix(dir)));
        _directoryIgnoreCaseCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        _maxFileSizeBytes = ResolveMaxFileSizeBytes(maxFileSizeBytes);
        _contentLoader = new FileContentLoader(_maxFileSizeBytes);
        _symlinkPolicy = symlinkPolicy;
        _maxDanglingFileSystemEntryScanCandidates = Math.Max(
            1,
            maxDanglingFileSystemEntryScanCandidates ?? MaxDanglingFileSystemEntryScanCandidates);
        _generatedCodePatterns = GeneratedCodePatternMatcher.FromPatterns(generatedCodePatterns, ignoreCase);
        ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(_projectRoot);
        var pathComparer = _ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        (_submodulePaths, _submoduleAncestorPaths, _submoduleLoadWarnings) = LoadGitSubmodulePaths(_ignoreRuleRoot, _projectRoot, pathComparer);
    }

    internal static bool TryParseMaxFileSizeBytes(string? value, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var splitAt = trimmed.Length;
        while (splitAt > 0 && char.IsLetter(trimmed[splitAt - 1]))
            splitAt--;

        var numberPart = trimmed[..splitAt].Trim();
        var suffix = trimmed[splitAt..].Trim().ToLowerInvariant();
        if (!long.TryParse(numberPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0)
            return false;

        long multiplier = suffix switch
        {
            "" or "b" or "byte" or "bytes" => 1,
            "k" or "kb" or "kib" => 1024L,
            "m" or "mb" or "mib" => 1024L * 1024L,
            "g" or "gb" or "gib" => 1024L * 1024L * 1024L,
            _ => 0,
        };
        if (multiplier == 0)
            return false;

        if (number > int.MaxValue / multiplier)
            return false;

        bytes = number * multiplier;
        return true;
    }

    private static long ResolveMaxFileSizeBytes(long? explicitMaxFileSizeBytes)
    {
        if (explicitMaxFileSizeBytes is > 0 and <= int.MaxValue)
            return explicitMaxFileSizeBytes.Value;

        var envValue = CdidxEnvironment.GetEnvironmentVariable(MaxFileSizeEnvironmentVariable);
        return TryParseMaxFileSizeBytes(envValue, out var envBytes)
            ? envBytes
            : DefaultMaxFileSizeBytes;
    }

    private static bool ProbeFileSystemIgnoreCase(string projectRoot)
    {
        var normalizedRoot = projectRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(projectRoot);
            if (FileSystemIgnoreCaseProbeForTesting is { } probeOverride)
                return probeOverride(normalizedRoot);

            if (TryProbeExistingDirectoryPath(normalizedRoot, out var ignoreCase))
                return ignoreCase;

            using var probe = CaseSensitivityProbeDirectory.CreateProbePathScope(normalizedRoot, "case-probe-");
            var probePath = probe.Path;
            FileWriteProbe.WriteEmptyFile(probePath);
            try
            {
                if (TryCreateCaseVariant(probePath, out var probeVariant))
                    return File.Exists(LongPath.EnsureWindowsPrefix(probeVariant));
            }
            finally
            {
                FileWriteProbe.DeleteFileIfExists(probePath);
            }

            throw new CaseSensitivityProbeException(
                "Failed to create a case-variant path for filesystem case-sensitivity probing.",
                normalizedRoot,
                probePath: probePath);
        }
        catch (CaseSensitivityProbeException)
        {
            throw;
        }
        catch (Exception ex) when (IsCaseSensitivityProbeFailure(ex))
        {
            throw new CaseSensitivityProbeException(
                "Failed to determine filesystem case sensitivity.",
                normalizedRoot,
                ex);
        }
    }

    private static bool TryProbeExistingDirectoryPath(string path, out bool ignoreCase)
    {
        ignoreCase = false;
        if (!TryCreateCaseVariant(path, out var variant))
            return false;

        ignoreCase = Directory.Exists(LongPath.EnsureWindowsPrefix(variant));
        return true;
    }

    private static bool IsCaseSensitivityProbeFailure(Exception ex)
        => ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;

    private static bool TryCreateCaseVariant(string path, out string variant)
    {
        var chars = path.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            var ch = chars[i];
            if (!char.IsLetter(ch))
                continue;

            chars[i] = char.IsUpper(ch)
                ? char.ToLowerInvariant(ch)
                : char.ToUpperInvariant(ch);
            variant = new string(chars);
            return true;
        }

        variant = path;
        return false;
    }

    private static bool? ProbeExistingDirectoryIgnoreCase(string directory)
    {
        try
        {
            var normalizedDirectory = Path.GetFullPath(directory);
            return TryCreateCaseVariant(normalizedDirectory, out var variant)
                ? Directory.Exists(LongPath.EnsureWindowsPrefix(variant))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool DirectoryUsesIgnoreCase(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (_directoryIgnoreCaseCache.TryGetValue(fullPath, out var ignoreCase))
            return ignoreCase;

        ignoreCase = _directoryIgnoreCaseProbe(fullPath) ?? _ignoreCase;
        _directoryIgnoreCaseCache[fullPath] = ignoreCase;
        return ignoreCase;
    }

    /// <summary>
    /// Return all file patterns (extensions and filenames) mapped to their language names.
    /// 全ファイルパターン（拡張子とファイル名）と対応する言語名のマッピングを返す。
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetLanguageExtensions()
    {
        // Merge extension map and filename map for a complete view
        // 完全な一覧のため拡張子マップとファイル名マップを統合
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (pattern, lang) in LangMap)
            merged.TryAdd(pattern, lang);
        // Keep display-only case variants that collapse in the case-insensitive detection map.
        // case-insensitive な検出マップでは潰れる表示用 case variant を保持する。
        foreach (var (pattern, lang) in DisplayOnlyLanguageExtensions)
            merged.TryAdd(pattern, lang);
        foreach (var (name, lang) in FileNameMap)
            merged.TryAdd(name, lang);
        // Surface suffixed variants like Dockerfile.dev / Makefile.am as `<Prefix><suffix>` entries
        // so `cdidx languages` and the MCP listing reflect what TryDetectLanguage actually handles.
        // Dockerfile.dev / Makefile.am のようなサフィックス付き変種も `<Prefix><suffix>` 形で
        // 露出させ、`cdidx languages` や MCP の一覧が TryDetectLanguage の実挙動と一致するようにする。
        foreach (var (prefix, lang) in FileNamePrefixMap)
            merged.TryAdd($"{prefix}<suffix>", lang);
        foreach (var (extension, lang) in ExtractorPluginRegistry.LanguageExtensions)
            merged.TryAdd(extension, lang);
        foreach (var (extension, lang) in LanguageMapOverrides.LoadEffectiveMap())
            merged[extension] = lang;
        return merged;
    }

    public static string? DetectLanguage(string filePath)
        => TryDetectLanguage(filePath).Language;

    internal static bool IsIgnoreFilePath(string path)
        => IgnoreFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    internal LanguageDetectionResult TryDetectLanguageForIndexing(string filePath, string? content = null)
        => TryDetectLanguage(filePath, content, _symlinkPolicy, _projectRoot);

    internal static LanguageDetectionResult TryDetectLanguage(string filePath, string? content = null)
        => TryDetectLanguage(filePath, content, SymlinkPolicy.None, projectRoot: null);

    internal static LanguageDetectionResult TryDetectLanguage(
        string filePath,
        string? content,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot)
    {
        // Exact filename matching beats extension lookup so manifest-style filenames like
        // `pyproject.toml` can map to a dependency category instead of the generic file type.
        // `pyproject.toml` のようなマニフェスト系ファイル名が、汎用拡張子ではなく
        // dependency category に紐づくよう、完全一致ファイル名を拡張子より先に判定する。
        var fileName = Path.GetFileName(filePath);
        if (FileNameMap.TryGetValue(fileName, out var nameLang))
            return new LanguageDetectionResult(FileProbeStatus.Supported, nameLang);

        // Then try known filename prefixes for suffixed variants like Dockerfile.dev / Makefile.am.
        // The suffix must be non-empty so a bare `Dockerfile.` with trailing dot does not match.
        // Dockerfile.dev や Makefile.am のようなサフィックス付き変種を検出する。
        // `Dockerfile.` のような末尾ドットだけの形には一致させないため、サフィックスは1文字以上必須。
        foreach (var (prefix, prefixLang) in FileNamePrefixMap)
        {
            if (fileName.Length > prefix.Length &&
                fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return new LanguageDetectionResult(FileProbeStatus.Supported, prefixLang);
            }
        }

        var ext = Path.GetExtension(filePath);
        if (TryDetectLanguageOverride(filePath, out var overrideLang))
            return new LanguageDetectionResult(FileProbeStatus.Supported, overrideLang);

        if (LangMap.TryGetValue(ext, out var lang))
        {
            if (lang == "c" && string.Equals(ext, ".h", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(content))
            {
                var cppHeaderLanguage = TryDetectCppHeaderLanguage(content);
                if (cppHeaderLanguage != null)
                    return new LanguageDetectionResult(FileProbeStatus.Supported, cppHeaderLanguage);
            }

            return new LanguageDetectionResult(FileProbeStatus.Supported, lang);
        }

        if (ExtractorPluginRegistry.LanguageExtensions.TryGetValue(ext, out var pluginLang))
            return new LanguageDetectionResult(FileProbeStatus.Supported, pluginLang);

        if (!string.IsNullOrEmpty(ext))
        {
            ExtractorPluginRegistry.LoadPatternConfigsForPath(filePath);
            if (ExtractorPluginRegistry.LanguageExtensions.TryGetValue(ext, out pluginLang))
                return new LanguageDetectionResult(FileProbeStatus.Supported, pluginLang);

            return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);
        }

        return TryDetectLanguageFromShebang(filePath, symlinkPolicy, projectRoot);
    }

    private static bool TryDetectLanguageOverride(string filePath, out string language)
    {
        language = string.Empty;
        var fileName = Path.GetFileName(filePath);
        foreach (var (extension, mappedLanguage) in LanguageMapOverrides.LoadEffectiveMap(filePath))
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                language = mappedLanguage;
                return true;
            }
        }

        return false;
    }

    private static string? TryDetectCppHeaderLanguage(string content)
    {
        const int maxLines = 200;
        var remaining = content.AsSpan();
        var inspectedLines = 0;

        while (remaining.Length > 0 && inspectedLines < maxLines)
        {
            var newlineIndex = remaining.IndexOf('\n');
            var line = newlineIndex >= 0 ? remaining[..newlineIndex] : remaining;
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];

            if (LooksLikeCppHeaderLine(line))
                return "cpp";

            if (newlineIndex < 0)
                break;

            remaining = remaining[(newlineIndex + 1)..];
            inspectedLines++;
        }

        return null;
    }

    private static bool LooksLikeCppHeaderLine(ReadOnlySpan<char> line)
    {
        line = line.Trim();
        if (line.IsEmpty)
            return false;

        if (line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith("*"))
            return false;

        if (line.StartsWith("namespace ", StringComparison.Ordinal)
            || line.StartsWith("template ", StringComparison.Ordinal)
            || line.StartsWith("template<", StringComparison.Ordinal)
            || line.StartsWith("using ", StringComparison.Ordinal)
            || line.StartsWith("class ", StringComparison.Ordinal)
            || line.StartsWith("enum class ", StringComparison.Ordinal)
            || line.StartsWith("enum struct ", StringComparison.Ordinal)
            || line.StartsWith("public:", StringComparison.Ordinal)
            || line.StartsWith("private:", StringComparison.Ordinal)
            || line.StartsWith("protected:", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.Contains("constexpr ", StringComparison.Ordinal)
            || line.Contains("consteval ", StringComparison.Ordinal)
            || line.Contains("constinit ", StringComparison.Ordinal)
            || line.Contains("decltype(", StringComparison.Ordinal)
            || line.Contains("friend ", StringComparison.Ordinal)
            || line.Contains("std::", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    internal static bool CanIndexFile(string filePath)
        => GetFileIndexability(filePath) == FileProbeStatus.Supported;

    internal static bool IsWindowsDevicePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var normalized = filePath.Replace('\\', '/');
        if (normalized.StartsWith("//./", StringComparison.Ordinal)
            || normalized.StartsWith("//?/GLOBALROOT/Device/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment;
            var extensionIndex = name.IndexOf('.');
            if (extensionIndex >= 0)
                name = name[..extensionIndex];

            if (IsWindowsReservedDeviceName(name))
                return true;
        }

        return false;
    }

    private static bool IsWindowsReservedDeviceName(string name)
    {
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && name[3] >= '1'
            && name[3] <= '9';
    }

    internal static bool HasSkippedAttributes(FileAttributes attributes, bool isWindows)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return true;

        return isWindows && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
    }

    private static bool HasSkippedAttributes(FileAttributes attributes)
        => HasSkippedAttributes(attributes, OperatingSystem.IsWindows());

    // Detect symbolic links / reparse points and Windows Hidden/System paths so the scanner can skip them.
    // Treats probe failures (e.g. dangling symlinks whose target is gone) as skipped attributes
    // so the scanner skips them instead of trying to read the missing target.
    // symlink / reparse point と Windows の Hidden/System 属性を検出し、スキャナでスキップできるようにする。
    // プローブ失敗（例: target が消えた dangling symlink）は missing target を読もうとせずスキップするため、
    // skip 対象属性扱いにする。
    private static bool HasSkippedAttributes(string path)
    {
        try
        {
            var attributes = File.GetAttributes(LongPath.EnsureWindowsPrefix(path));
            return HasSkippedAttributes(attributes);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(LongPath.EnsureWindowsPrefix(path)) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private bool ShouldSkipDirectoryLink(string subDir, List<ScanError> errors, HashSet<string> danglingSymlinks)
    {
        if (!IsReparsePoint(subDir))
            return HasSkippedAttributes(subDir);

        var relative = ToRelativePath(subDir);
        DirectoryInfo info = new(LongPath.EnsureWindowsPrefix(subDir));
        FileSystemInfo? target;
        try
        {
            target = ResolveDirectoryLinkTargetForTesting != null
                ? ResolveDirectoryLinkTargetForTesting(subDir)
                : info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (FileNotFoundException)
        {
            target = null;
        }
        catch (DirectoryNotFoundException)
        {
            target = null;
        }
        catch (IOException)
        {
            target = null;
        }
        catch (UnauthorizedAccessException)
        {
            errors.Add(new ScanError(
                relative,
                "Skipped symlinked directory because its target could not be resolved due to permissions.",
                ScanIssueSeverity.Warning));
            return true;
        }

        if (target?.FullName is not { Length: > 0 } targetPath || !Directory.Exists(LongPath.EnsureWindowsPrefix(targetPath)))
        {
            danglingSymlinks.Add(relative);
            errors.Add(new ScanError(relative, "Skipped dangling symlink because its target could not be resolved.", ScanIssueSeverity.Warning));
            return true;
        }

        if (_symlinkPolicy == SymlinkPolicy.All)
            return false;

        if (_symlinkPolicy == SymlinkPolicy.Internal && IsPathEqualOrParent(_projectRoot, targetPath))
            return false;

        errors.Add(new ScanError(
            relative,
            $"Skipped symlinked directory outside the active symlink policy: target {FormatSymlinkPolicyTargetForDiagnostic(targetPath)}",
            ScanIssueSeverity.Warning));
        return true;
    }

    private string FormatSymlinkPolicyTargetForDiagnostic(string targetPath)
    {
        if (!IsPathEqualOrParent(_projectRoot, targetPath))
            return "<outside project root>";

        var relative = NormalizePathSeparators(Path.GetRelativePath(_projectRoot, targetPath));
        return relative == "." ? "<project root>" : relative;
    }

    internal bool ShouldSkipDirectoryTraversal(string directory)
        => ShouldSkipDirectoryLink(
            directory,
            errors: new List<ScanError>(),
            danglingSymlinks: new HashSet<string>(StringComparer.Ordinal));

    private static string GetDirectoryTraversalIdentity(string directory)
    {
        if (!IsReparsePoint(directory))
            return directory;

        try
        {
            DirectoryInfo info = new(LongPath.EnsureWindowsPrefix(directory));
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target?.FullName is { Length: > 0 } targetPath)
                return targetPath;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return $"unresolved-reparse:{Path.GetFullPath(directory)}";
    }

    internal static FileProbeStatus GetFileIndexability(string filePath)
        => GetFileIndexability(filePath, SymlinkPolicy.None, projectRoot: null);

    internal FileProbeStatus GetFileIndexabilityForIndexing(string filePath)
        => GetFileIndexability(filePath, _symlinkPolicy, _projectRoot);

    internal static FileProbeStatus GetFileIndexability(
        string filePath,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot)
    {
        if (OperatingSystem.IsWindows() && IsWindowsDevicePath(filePath))
            return FileProbeStatus.Unsupported;

        // File.GetAttributes uses lstat-like semantics on .NET (does not follow the symlink target),
        // which lets us apply the active symlink policy before the Unix stat() path follows the target.
        // Windows Hidden/System paths remain rejected to avoid indexing OS-owned caches during broad scans.
        // File.GetAttributes は .NET 上で lstat 相当（symlink target を辿らない）なので、
        // Unix の stat() が target を辿る前に symlink policy を適用できる。Windows では
        // broad scan で OS 管理 cache を索引しないよう Hidden/System も引き続き弾く。
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(LongPath.EnsureWindowsPrefix(filePath));
        }
        catch (FileNotFoundException)
        {
            return FileProbeStatus.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FileProbeStatus.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return OperatingSystem.IsWindows()
                ? FileProbeStatus.Supported
                : FileProbeStatus.ProbeFailed;
        }
        catch (IOException)
        {
            return OperatingSystem.IsWindows()
                ? FileProbeStatus.Supported
                : FileProbeStatus.ProbeFailed;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return GetFileSymlinkIndexability(filePath, symlinkPolicy, projectRoot);

        if (HasSkippedAttributes(attributes))
            return FileProbeStatus.Unsupported;

        if (OperatingSystem.IsWindows())
            return FileProbeStatus.Supported;

        if (!UnixFileStatus.TryGetFileMode(filePath, out var mode))
            return FileProbeStatus.ProbeFailed;

        return (mode & UnixFileStatus.FileTypeMask) == UnixFileStatus.RegularFile
            ? FileProbeStatus.Supported
            : FileProbeStatus.Unsupported;
    }

    private static FileProbeStatus GetFileSymlinkIndexability(
        string filePath,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot)
    {
        if (symlinkPolicy == SymlinkPolicy.None)
            return FileProbeStatus.Unsupported;

        FileSystemInfo? target;
        try
        {
            FileInfo info = new(LongPath.EnsureWindowsPrefix(filePath));
            target = info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (FileNotFoundException)
        {
            return FileProbeStatus.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FileProbeStatus.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return FileProbeStatus.ProbeFailed;
        }
        catch (IOException)
        {
            return FileProbeStatus.ProbeFailed;
        }

        if (target?.FullName is not { Length: > 0 } targetPath)
            return FileProbeStatus.Unsupported;

        if (symlinkPolicy == SymlinkPolicy.Internal)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !IsPathEqualOrParent(projectRoot, targetPath))
                return FileProbeStatus.Unsupported;
        }

        return GetFileIndexability(targetPath, SymlinkPolicy.None, projectRoot: null);
    }

    public string GetFamilyScopeKey(string absolutePath, string? lang)
    {
        var projectMarkerPatterns = GetProjectMarkerPatterns(lang);
        if (projectMarkerPatterns != null)
        {
            var primaryProjectMarkerPatterns = GetPrimaryProjectMarkerPatterns(lang) ?? projectMarkerPatterns;
            var currentDir = Path.GetDirectoryName(Path.GetFullPath(absolutePath));
            while (!string.IsNullOrEmpty(currentDir))
            {
                var markerCount = CountProjectMarkerFiles(currentDir, primaryProjectMarkerPatterns);
                if (markerCount == 1)
                    return NormalizeScopeKey(Path.GetRelativePath(_projectRoot, currentDir));
                if (markerCount > 1)
                    return DeriveAmbiguousProjectScopeKey(Path.GetFullPath(absolutePath), currentDir);
                if (CountProjectMarkerFiles(currentDir, projectMarkerPatterns) > 0)
                    return NormalizeScopeKey(Path.GetRelativePath(_projectRoot, currentDir));

                if (PathsEqual(currentDir, _projectRoot))
                    break;

                currentDir = Path.GetDirectoryName(currentDir);
            }
        }

        var relativePath = Path.GetRelativePath(_projectRoot, absolutePath);
        return DeriveFallbackFamilyScopeKey(relativePath);
    }

    public static IReadOnlyList<string> GetHotspotFamilyMarkerLanguages() => HotspotFamilyMarkerLanguages;

    public static bool SupportsHotspotFamilyMarkerLanguage(string? lang) =>
        GetProjectMarkerPatterns(lang) != null;

    public string? GetProjectMarkerFingerprint(string? lang, CancellationToken cancellationToken = default) =>
        GetProjectMarkerFingerprintResult(lang, cancellationToken).Fingerprint;

    internal ProjectMarkerFingerprintResult GetProjectMarkerFingerprintResult(
        string? lang,
        CancellationToken cancellationToken = default) =>
        GetProjectMarkerFingerprintResult(lang, MaxProjectMarkerFingerprintDirectories, MaxProjectMarkerFingerprintFiles, cancellationToken);

    internal string? GetProjectMarkerFingerprintForTesting(
        string? lang,
        int maxDirectories,
        int maxMarkerFiles,
        CancellationToken cancellationToken = default) =>
        GetProjectMarkerFingerprintResult(lang, maxDirectories, maxMarkerFiles, cancellationToken).Fingerprint;

    internal ProjectMarkerFingerprintResult GetProjectMarkerFingerprintResultForTesting(
        string? lang,
        int maxDirectories,
        int maxMarkerFiles,
        CancellationToken cancellationToken = default) =>
        GetProjectMarkerFingerprintResult(lang, maxDirectories, maxMarkerFiles, cancellationToken);

    private ProjectMarkerFingerprintResult GetProjectMarkerFingerprintResult(
        string? lang,
        int maxDirectories,
        int maxMarkerFiles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projectMarkerPatterns = GetProjectMarkerPatterns(lang);
        if (projectMarkerPatterns == null)
            return new ProjectMarkerFingerprintResult(null, IsComplete: true);

        var projectMarkers = new List<string>();
        var traversalState = new ProjectMarkerFingerprintTraversalState();
        var errors = new List<ScanError>();
        var fullyScanned = true;
        var preloadResult = LoadAncestorIgnoreRules(errors, ref fullyScanned);
        if (preloadResult.IgnoreRulesAvailable)
        {
            CollectProjectMarkerFiles(
                _projectRoot,
                preloadResult.Rules,
                projectMarkerPatterns,
                projectMarkers,
                Math.Max(1, maxDirectories),
                Math.Max(1, maxMarkerFiles),
                traversalState,
                errors,
                cancellationToken);
        }
        else
        {
            traversalState.Truncated = true;
        }

        if (traversalState.Truncated)
        {
            projectMarkers.Add(
                $"__cdidx_project_marker_fingerprint_truncated__:reason={traversalState.TruncationReason};directories={traversalState.DirectoriesVisited};markers={traversalState.MarkerFilesCollected}");
        }

        projectMarkers.Sort(StringComparer.Ordinal);

        var payload = string.Join('\n', projectMarkers);
        return new ProjectMarkerFingerprintResult(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
            !traversalState.Truncated)
        {
            Warnings = errors
                .Where(static error => !error.IsFatal)
                .ToArray(),
        };
    }

    public static string DeriveFallbackFamilyScopeKey(string relativePath)
    {
        var normalized = NormalizeScopeKey(relativePath);
        if (normalized == ".")
            return ".";

        var firstSeparator = normalized.IndexOf('/');
        if (firstSeparator < 0)
            return ".";

        return normalized[..firstSeparator];
    }

    private static string NormalizeScopeKey(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(normalized) || normalized == "."
            ? "."
            : normalized;
    }

    private string DeriveAmbiguousProjectScopeKey(string absolutePath, string anchorDir)
    {
        var anchorScope = NormalizeScopeKey(Path.GetRelativePath(_projectRoot, anchorDir));
        var relativeFromAnchor = NormalizeScopeKey(Path.GetRelativePath(anchorDir, absolutePath));
        if (relativeFromAnchor == ".")
            return anchorScope;

        var firstSeparator = relativeFromAnchor.IndexOf('/');
        if (firstSeparator < 0)
            return JoinScope(anchorScope, $"__file__/{relativeFromAnchor}");

        return JoinScope(anchorScope, relativeFromAnchor[..firstSeparator]);
    }

    private static string JoinScope(string left, string right)
    {
        if (left == ".")
            return right;

        return $"{left}/{right}";
    }

    private int CountProjectMarkerFiles(string dir, IReadOnlyList<string> patterns)
    {
        var count = 0;
        foreach (var markerFile in EnumerateProjectMarkerFiles(dir, patterns))
        {
            if (!IsProjectMarkerVisible(markerFile, activeIgnoreRules: null))
                continue;

            count++;
            if (count > 1)
                return count;
        }

        return count;
    }

    private IEnumerable<string> EnumerateProjectMarkerFiles(
        string dir,
        IReadOnlyList<string> patterns,
        CancellationToken cancellationToken = default)
    {
        var prefixedDir = LongPath.EnsureWindowsPrefix(dir);
        foreach (var pattern in patterns)
        {
            foreach (var file in Directory.EnumerateFiles(prefixedDir, pattern, SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return LongPath.RemoveWindowsPrefix(file);
            }
        }
    }

    private bool IsProjectMarkerVisible(string markerFile, IgnoreRuleSet? activeIgnoreRules)
    {
        if (HasSkippedAttributes(markerFile))
            return false;

        return activeIgnoreRules is null
            ? !EvaluatePathFilter(markerFile).ShouldSkip
            : !activeIgnoreRules.IsIgnored(markerFile, isDirectory: false);
    }

    private void CollectProjectMarkerFiles(
        string dir,
        IgnoreRuleSet inheritedIgnoreRules,
        IReadOnlyList<string> patterns,
        List<string> projectMarkers,
        int maxDirectories,
        int maxMarkerFiles,
        ProjectMarkerFingerprintTraversalState traversalState,
        List<ScanError> errors,
        CancellationToken cancellationToken)
    {
        var pendingDirectories = new Stack<ProjectMarkerFingerprintDirectory>();
        pendingDirectories.Push(new ProjectMarkerFingerprintDirectory(dir, inheritedIgnoreRules, IsProjectRoot: true));
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pendingDirectories.Pop();
            if (GetDirectoryFilterKind(current.Path, current.IgnoreRules, current.IsProjectRoot) != PathFilterKind.None)
                continue;

            if (traversalState.DirectoriesVisited >= maxDirectories)
            {
                TruncateProjectMarkerTraversal(
                    traversalState,
                    errors,
                    current.Path,
                    $"directory budget {maxDirectories:N0} exhausted after visiting {traversalState.DirectoriesVisited:N0} directories");
                return;
            }

            var currentDirectory = current.Path;
            traversalState.DirectoriesVisited++;
            try
            {
                var fullyScanned = true;
                var loadResult = LoadIgnoreRulesForDirectory(currentDirectory, current.IgnoreRules, errors, ref fullyScanned);
                if (!loadResult.IgnoreRulesAvailable)
                {
                    TruncateProjectMarkerTraversal(
                        traversalState,
                        errors,
                        currentDirectory,
                        "ignore-rule loading failed");
                    return;
                }

                var activeIgnoreRules = loadResult.Rules;
                foreach (var markerFile in EnumerateProjectMarkerFiles(currentDirectory, patterns, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsProjectMarkerVisible(markerFile, activeIgnoreRules))
                        continue;

                    if (traversalState.MarkerFilesCollected >= maxMarkerFiles)
                    {
                        TruncateProjectMarkerTraversal(
                            traversalState,
                            errors,
                            currentDirectory,
                            $"marker file budget {maxMarkerFiles:N0} exhausted after collecting {traversalState.MarkerFilesCollected:N0} marker files");
                        return;
                    }

                    projectMarkers.Add(NormalizeScopeKey(Path.GetRelativePath(_projectRoot, markerFile)));
                    traversalState.MarkerFilesCollected++;
                }

                var passthrough = IsSubmoduleAncestorPassthrough(currentDirectory);
                foreach (var enumeratedSubDir in EnumerateProjectMarkerDirectories(currentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var subDir = LongPath.RemoveWindowsPrefix(enumeratedSubDir);
                    if (HasSkippedAttributes(subDir))
                        continue;
                    if (IsNestedGitRepository(subDir) && !IsSubmoduleOrAncestor(subDir))
                        continue;
                    if (passthrough && !IsSubmoduleOrAncestor(subDir))
                        continue;
                    if (GetDirectoryFilterKind(subDir, activeIgnoreRules) != PathFilterKind.None)
                        continue;

                    if (traversalState.DirectoriesVisited + pendingDirectories.Count >= maxDirectories)
                    {
                        TruncateProjectMarkerTraversal(
                            traversalState,
                            errors,
                            currentDirectory,
                            $"directory budget {maxDirectories:N0} would be exceeded while queuing subdirectories after visiting {traversalState.DirectoriesVisited:N0} directories");
                        return;
                    }

                    pendingDirectories.Push(new ProjectMarkerFingerprintDirectory(subDir, activeIgnoreRules, IsProjectRoot: false));
                }
            }
            catch (UnauthorizedAccessException)
            {
                AddProjectMarkerTraversalWarning(errors, currentDirectory, nameof(UnauthorizedAccessException));
                MarkProjectMarkerTraversalTruncated(
                    traversalState,
                    $"traversal failed with {nameof(UnauthorizedAccessException)}");
            }
            catch (IOException)
            {
                AddProjectMarkerTraversalWarning(errors, currentDirectory, nameof(IOException));
                MarkProjectMarkerTraversalTruncated(
                    traversalState,
                    $"traversal failed with {nameof(IOException)}");
            }
        }
    }

    private void TruncateProjectMarkerTraversal(
        ProjectMarkerFingerprintTraversalState traversalState,
        List<ScanError> errors,
        string directory,
        string reason)
    {
        MarkProjectMarkerTraversalTruncated(traversalState, reason);
        AddProjectMarkerBudgetWarning(errors, directory, reason);
    }

    private static void MarkProjectMarkerTraversalTruncated(
        ProjectMarkerFingerprintTraversalState traversalState,
        string reason)
    {
        if (!traversalState.Truncated)
            traversalState.TruncationReason = reason;
        traversalState.Truncated = true;
    }

    private static IEnumerable<string> EnumerateProjectMarkerDirectories(string dir)
        => EnumerateProjectMarkerDirectoriesForTesting is { } enumerate
            ? enumerate(dir)
            : Directory.EnumerateDirectories(LongPath.EnsureWindowsPrefix(dir));

    private void AddProjectMarkerTraversalWarning(List<ScanError> errors, string directory, string exceptionType)
    {
        if (errors.Count(static error => error.Message.StartsWith("Project marker discovery skipped", StringComparison.Ordinal))
            >= MaxProjectMarkerTraversalWarnings)
        {
            return;
        }

        var relativePath = NormalizeIgnorePath(Path.GetRelativePath(_projectRoot, directory));
        if (string.IsNullOrEmpty(relativePath))
            relativePath = ".";

        errors.Add(new ScanError(
            relativePath,
            $"Project marker discovery skipped this subtree because it could not be traversed ({exceptionType}).",
            ScanIssueSeverity.Warning));
    }

    private void AddProjectMarkerBudgetWarning(List<ScanError> errors, string directory, string reason)
    {
        if (errors.Count(static error => error.Message.StartsWith("Project marker discovery truncated", StringComparison.Ordinal))
            >= MaxProjectMarkerTraversalWarnings)
        {
            return;
        }

        var relativePath = NormalizeIgnorePath(Path.GetRelativePath(_projectRoot, directory));
        if (string.IsNullOrEmpty(relativePath))
            relativePath = ".";

        errors.Add(new ScanError(
            relativePath,
            $"Project marker discovery truncated because {reason}.",
            ScanIssueSeverity.Warning));
    }

    private static IReadOnlyList<string>? GetProjectMarkerPatterns(string? lang) => lang switch
    {
        "csharp" => ["*.csproj"],
        "vb" => ["*.vbproj"],
        "fsharp" => ["*.fsproj"],
        "msbuild" => ["*.csproj", "*.fsproj", "*.vbproj", "*.props", "*.targets"],
        _ => null,
    };

    private static IReadOnlyList<string>? GetPrimaryProjectMarkerPatterns(string? lang) => lang switch
    {
        "csharp" => ["*.csproj"],
        "vb" => ["*.vbproj"],
        "fsharp" => ["*.fsproj"],
        "msbuild" => ["*.csproj", "*.fsproj", "*.vbproj"],
        _ => null,
    };

    private static bool PathsEqual(string left, string right)
        => CodeIndex.Cli.PathCasing.PathsEqual(
            NormalizePathForComparison(left),
            NormalizePathForComparison(right));

    private static bool IsPathEqualOrParent(string candidateParent, string candidateChild)
    {
        var normalizedParent = NormalizePathForComparison(candidateParent);
        var normalizedChild = NormalizePathForComparison(candidateChild);
        return CodeIndex.Cli.PathCasing.IsPathEqualOrParent(normalizedParent, normalizedChild);
    }

    private static string NormalizePathForComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return Path.TrimEndingDirectorySeparator(fullPath);

        var remaining = Path.GetRelativePath(root, fullPath);
        if (remaining == "." || remaining.Length == 0)
            return Path.TrimEndingDirectorySeparator(fullPath);

        var current = root;
        foreach (var segment in remaining.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0 || segment == ".")
                continue;

            current = Path.Combine(current, segment);
            current = ResolvePathComparisonSegment(current);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static string ResolvePathComparisonSegment(string fullPath)
    {
        try
        {
            var attributes = File.GetAttributes(fullPath);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath);
            var target = info?.ResolveLinkTarget(returnFinalTarget: true);
            if (target?.FullName is { Length: > 0 } resolvedPath)
                return resolvedPath;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return fullPath;
    }

    /// <summary>
    /// Enumerate all indexable files under the project root.
    /// プロジェクトルート以下のインデックス対象ファイルを列挙する。
    /// </summary>
    public IReadOnlyList<string> ScanFiles()
        => ScanFilesDetailed().Files;

    internal PathFilterResult EvaluatePathFilter(string absolutePath, bool isDirectory = false)
    {
        var errors = new List<ScanError>();
        if (TryEvaluatePathFilterPrefix(absolutePath, errors, out var fullPath, out var relativePath) is { } prefixResult)
            return prefixResult;
        if (TryLoadRootPathFilterRules(errors, isDirectory, out var activeIgnoreRules) is { } rootResult)
            return rootResult;

        if (relativePath.Length == 0 || relativePath == ".")
            return new PathFilterResult(PathFilterKind.None, errors);

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var directorySegmentCount = isDirectory ? segments.Length : Math.Max(segments.Length - 1, 0);
        var directoryResult = EvaluatePathFilterDirectorySegments(
            segments,
            directorySegmentCount,
            errors,
            activeIgnoreRules,
            out var leafIgnoreRules,
            out var inSubmodulePassthrough);
        if (directoryResult != null)
            return directoryResult.Value;

        if (isDirectory)
            return new PathFilterResult(PathFilterKind.None, errors);

        return EvaluatePathFilterLeafFile(fullPath, errors, leafIgnoreRules, inSubmodulePassthrough);
    }

    private PathFilterResult? TryEvaluatePathFilterPrefix(
        string absolutePath,
        List<ScanError> errors,
        out string fullPath,
        out string relativePath)
    {
        fullPath = string.Empty;
        relativePath = string.Empty;
        if (!IsFilePathSyntaxIndexable(absolutePath))
        {
            errors.Add(new ScanError(
                FormatPathForScanIssue(absolutePath),
                "Skipped file because its path contains NUL or control characters.",
                ScanIssueSeverity.Warning));
            return new PathFilterResult(PathFilterKind.ExcludedByDefaultFile, errors);
        }

        fullPath = Path.GetFullPath(absolutePath);
        if (!IsPathEqualOrParent(_projectRoot, fullPath))
            return new PathFilterResult(PathFilterKind.OutsideProjectRoot, errors);

        relativePath = NormalizeIgnorePath(Path.GetRelativePath(_projectRoot, fullPath));
        if (relativePath.StartsWith("../", StringComparison.Ordinal))
            return new PathFilterResult(PathFilterKind.None, errors);

        return null;
    }

    private PathFilterResult? TryLoadRootPathFilterRules(
        List<ScanError> errors,
        bool isDirectory,
        out IgnoreRuleSet activeIgnoreRules)
    {
        var fullyScanned = true;
        var preloadResult = LoadAncestorIgnoreRules(errors, ref fullyScanned);
        activeIgnoreRules = preloadResult.Rules;
        if (!preloadResult.IgnoreRulesAvailable)
            return new PathFilterResult(PathFilterKind.IgnoreRulesUnavailable, errors);

        var projectRootFilterKind = GetDirectoryFilterKind(_projectRoot, activeIgnoreRules, isProjectRoot: true);
        return projectRootFilterKind != PathFilterKind.None
            ? new PathFilterResult(projectRootFilterKind, errors)
            : null;
    }

    private PathFilterResult? EvaluatePathFilterDirectorySegments(
        string[] segments,
        int directorySegmentCount,
        List<ScanError> errors,
        IgnoreRuleSet activeIgnoreRules,
        out IgnoreRuleSet leafIgnoreRules,
        out bool inSubmodulePassthrough)
    {
        var currentDirectory = _projectRoot;
        var fullyScanned = true;
        var loadResult = LoadIgnoreRulesForDirectory(currentDirectory, activeIgnoreRules, errors, ref fullyScanned);
        leafIgnoreRules = loadResult.Rules;
        inSubmodulePassthrough = false;
        if (!loadResult.IgnoreRulesAvailable)
            return new PathFilterResult(PathFilterKind.IgnoreRulesUnavailable, errors);

        // Mirror EnumerateDirectory's passthrough behavior so update-mode filters (--files /
        // --commits) match a fresh full scan: when SkipDirs is overridden because we're
        // routing toward a declared submodule, files/subdirs that do not themselves lead
        // to a submodule must still be excluded.
        // EnumerateDirectory の passthrough と挙動を一致させ、--files / --commits などの
        // 更新モードのフィルタがフルスキャンと食い違わないようにする。submodule への通過のため
        // SkipDirs を上書きした場合でも、submodule に到達しないファイル・サブディレクトリは
        // 引き続き除外する。
        for (var i = 0; i < directorySegmentCount; i++)
        {
            var directoryName = segments[i];
            var childDirectory = Path.Combine(currentDirectory, directoryName);
            var cumulativeRelPath = NormalizeIgnorePath(Path.GetRelativePath(_projectRoot, childDirectory));
            var isSubmodule = _submodulePaths.Contains(cumulativeRelPath);
            var isSubmoduleAncestor = _submoduleAncestorPaths.Contains(cumulativeRelPath);

            if (IsNestedGitRepository(childDirectory) && !isSubmodule && !isSubmoduleAncestor)
                return new PathFilterResult(PathFilterKind.ExcludedByDefaultDirectory, errors);

            if (SkipDirs.Contains(directoryName))
            {
                if (!isSubmodule && !isSubmoduleAncestor)
                    return new PathFilterResult(PathFilterKind.ExcludedByDefaultDirectory, errors);
            }
            else if (inSubmodulePassthrough && !isSubmodule && !isSubmoduleAncestor)
            {
                return new PathFilterResult(PathFilterKind.ExcludedByDefaultDirectory, errors);
            }

            if (isSubmodule)
                inSubmodulePassthrough = false;
            else if (isSubmoduleAncestor)
                inSubmodulePassthrough = true;

            if (leafIgnoreRules.IsIgnored(childDirectory, isDirectory: true))
                return new PathFilterResult(PathFilterKind.IgnoredByRules, errors);

            currentDirectory = childDirectory;
            fullyScanned = true;
            loadResult = LoadIgnoreRulesForDirectory(currentDirectory, leafIgnoreRules, errors, ref fullyScanned);
            leafIgnoreRules = loadResult.Rules;
            if (!loadResult.IgnoreRulesAvailable)
                return new PathFilterResult(PathFilterKind.IgnoreRulesUnavailable, errors);
        }

        return null;
    }

    private PathFilterResult EvaluatePathFilterLeafFile(
        string fullPath,
        List<ScanError> errors,
        IgnoreRuleSet activeIgnoreRules,
        bool inSubmodulePassthrough)
    {
        // File directly inside a submodule-ancestor passthrough directory: walker would not
        // index it, so neither should this filter.
        // submodule 祖先（passthrough）に直接置かれているファイルは walker も索引しないため
        // ここでも除外する。
        if (inSubmodulePassthrough)
            return new PathFilterResult(PathFilterKind.ExcludedByDefaultDirectory, errors);

        var fileName = Path.GetFileName(fullPath);
        if (IsDefaultExcludedFileName(fileName))
            return new PathFilterResult(PathFilterKind.ExcludedByDefaultFile, errors);

        return activeIgnoreRules.IsIgnored(fullPath, isDirectory: false)
            ? new PathFilterResult(PathFilterKind.IgnoredByRules, errors)
            : new PathFilterResult(PathFilterKind.None, errors);
    }

    internal ScanFilesResult ScanFilesDetailed(
        IReadOnlySet<string>? checkpointedDirectories = null,
        bool continueOnError = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = new List<string>();
        var errors = new List<ScanError>();
        var nonIndexablePaths = new HashSet<string>(StringComparer.Ordinal);
        var unknownExtensionFiles = new HashSet<string>(StringComparer.Ordinal);
        var probeFailedFilePaths = new HashSet<string>(StringComparer.Ordinal);
        var listedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var fullyScannedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var activeCheckpointedDirectories = checkpointedDirectories is { Count: > 0 }
            ? new HashSet<string>(checkpointedDirectories, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var attributePrunedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var nestedRepositories = new HashSet<string>(StringComparer.Ordinal);
        var danglingSymlinks = new HashSet<string>(StringComparer.Ordinal);
        var visitedFileIdentities = new HashSet<FileIdentity>();
        var visitedDirectories = new HashSet<string>(StringComparer.Ordinal) { NormalizePathForComparison(_projectRoot) };
        var scanState = new DirectoryScanState(
            files,
            errors,
            nonIndexablePaths,
            unknownExtensionFiles,
            probeFailedFilePaths,
            listedDirectories,
            fullyScannedDirectories,
            activeCheckpointedDirectories,
            attributePrunedDirectories,
            nestedRepositories,
            danglingSymlinks,
            visitedFileIdentities,
            visitedDirectories);
        errors.AddRange(_submoduleLoadWarnings);
        var fullyScanned = true;
        var preloadResult = LoadAncestorIgnoreRules(errors, ref fullyScanned);
        if (preloadResult.IgnoreRulesAvailable)
        {
            ScanDirectory(_projectRoot, scanState, preloadResult.Rules, isProjectRoot: true, continueOnError, cancellationToken, depth: 0);
        }
        return new ScanFilesResult(
            scanState.Results,
            scanState.Errors,
            scanState.NonIndexablePaths.ToList(),
            scanState.UnknownExtensionFiles.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            scanState.ProbeFailedFilePaths.ToList(),
            scanState.ListedDirectories.ToList(),
            scanState.FullyScannedDirectories.ToList(),
            scanState.CheckpointedDirectories.Concat(scanState.FullyScannedDirectories).ToHashSet(StringComparer.Ordinal),
            _ancestorIgnoreDirectories.ToList(),
            scanState.AttributePrunedDirectories.ToList(),
            scanState.NestedRepositories.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            scanState.DanglingSymlinks.OrderBy(path => path, StringComparer.Ordinal).ToList());
    }

    private bool ScanDirectory(
        string dir,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool isProjectRoot = false,
        bool continueOnError = true,
        CancellationToken cancellationToken = default,
        int depth = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relativeDir = ToRelativePath(dir);

        if (depth > MaxDirectoryTraversalDepth)
        {
            scanState.Errors.Add(new ScanError(
                relativeDir,
                $"Skipped directory because traversal depth exceeded {MaxDirectoryTraversalDepth}. Check for symlink loops or unexpectedly deep generated trees.",
                ScanIssueSeverity.Warning));
            return true;
        }

        if (scanState.CheckpointedDirectories.Contains(relativeDir))
            return true;

        var filterKind = GetDirectoryFilterKind(dir, activeIgnoreRules, isProjectRoot);
        if (filterKind != PathFilterKind.None)
        {
            scanState.ListedDirectories.Add(relativeDir);
            scanState.FullyScannedDirectories.Add(relativeDir);
            return true;
        }

        return EnumerateDirectory(dir, scanState, activeIgnoreRules, continueOnError, cancellationToken, depth);
    }

    private bool IsNestedGitRepository(string dir)
    {
        if (PathsEqual(dir, _projectRoot))
            return false;

        return Directory.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(dir, ".git"))) ||
            File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(dir, ".git")));
    }

    private bool EnumerateDirectory(
        string dir,
        DirectoryScanState scanState,
        IgnoreRuleSet inheritedIgnoreRules,
        bool continueOnError,
        CancellationToken cancellationToken = default,
        int depth = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullyScanned = true;
        try
        {
            var loadResult = LoadIgnoreRulesForDirectory(dir, inheritedIgnoreRules, scanState.Errors, ref fullyScanned);
            var activeIgnoreRules = loadResult.Rules;
            if (!loadResult.IgnoreRulesAvailable)
                return false;

            // Submodule passthrough: we are inside a SkipDirs-named ancestor of a submodule
            // (e.g. vendor/ on the way to vendor/foo). Honor SkipDirs for this directory's
            // own files and unrelated subdirs while still descending toward the submodule.
            // submodule の祖先で SkipDirs 名のディレクトリ（例: vendor/foo の vendor/）の場合は、
            // 当該ディレクトリの直下ファイルおよび submodule と無関係なサブディレクトリには
            // SkipDirs を適用しつつ、submodule 方向にだけ降りる。
            var passthrough = IsSubmoduleAncestorPassthrough(dir);
            var directoryIgnoreCase = DirectoryUsesIgnoreCase(dir);
            if (directoryIgnoreCase != _ignoreCase)
            {
                scanState.Errors.Add(new ScanError(
                    ToRelativePath(dir),
                    "Filesystem case-sensitivity differs from the project root; deduplicating file paths for this directory.",
                    ScanIssueSeverity.Warning));
            }

            if (!passthrough)
                EnumerateIndexableFilesInDirectory(dir, scanState, activeIgnoreRules, directoryIgnoreCase, cancellationToken);

            // A successful file listing proves the direct children of this directory.
            // Child subtree failures must not revoke that authority for sibling-file purge.
            // ファイル列挙が成功した時点で、このディレクトリ直下の子要素については authoritative とみなせる。
            // 子サブツリー失敗が sibling file purge の authority を奪ってはいけない。
            scanState.ListedDirectories.Add(ToRelativePath(dir));
            RecordDanglingFileSystemEntries(dir, scanState, cancellationToken);
            fullyScanned &= EnumerateSubdirectories(dir, scanState, activeIgnoreRules, passthrough, continueOnError, cancellationToken, depth);
        }
        catch (UnauthorizedAccessException)
        {
            // Skip inaccessible directories / アクセス不可ディレクトリはスキップ
            scanState.Errors.Add(new ScanError(ToRelativePath(dir), "Could not scan directory due to permissions."));
            fullyScanned = false;
        }
        catch (IOException)
        {
            // Skip on I/O errors / I/Oエラー時はスキップ
            scanState.Errors.Add(new ScanError(ToRelativePath(dir), "Could not scan directory due to an I/O error."));
            fullyScanned = false;
        }

        if (fullyScanned)
            scanState.FullyScannedDirectories.Add(ToRelativePath(dir));

        return fullyScanned;
    }

    private void EnumerateIndexableFilesInDirectory(
        string dir,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool directoryIgnoreCase,
        CancellationToken cancellationToken)
    {
        var seenFilePaths = directoryIgnoreCase
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var enumeratedFile in _enumerateFiles(dir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Strip any \\?\ prefix returned by EnumerateFiles when we passed a long-path
            // directory, so downstream relative-path math (which compares against the
            // un-prefixed _projectRoot) still produces the canonical project-relative key.
            // \\?\ 接頭辞付きの long-path ディレクトリを渡したとき EnumerateFiles も接頭辞付きで
            // 返すため、_projectRoot（接頭辞なし）と突き合わせる相対パス計算が崩れないよう剥がす。
            var file = LongPath.RemoveWindowsPrefix(enumeratedFile);
            if (!TryAcceptScannedFile(file, scanState, activeIgnoreRules, seenFilePaths))
                continue;

            scanState.Results.Add(file);
        }
    }

    private bool TryAcceptScannedFile(
        string file,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        HashSet<string>? seenFilePaths)
    {
        if (!IsFilePathSyntaxIndexable(file))
        {
            var issuePath = FormatPathForScanIssue(file);
            scanState.Errors.Add(new ScanError(
                issuePath,
                "Skipped file because its path contains NUL or control characters.",
                ScanIssueSeverity.Warning));
            scanState.NonIndexablePaths.Add(issuePath);
            return false;
        }

        if (seenFilePaths is not null && !seenFilePaths.Add(Path.GetFullPath(file)))
        {
            var relativePath = ToRelativePath(file);
            scanState.Errors.Add(new ScanError(
                relativePath,
                "Skipped duplicate file path that differs only by case on a case-insensitive directory.",
                ScanIssueSeverity.Warning));
            scanState.NonIndexablePaths.Add(relativePath);
            return false;
        }

        var fileName = Path.GetFileName(file);

        // Skip excluded file names / 除外ファイル名をスキップ
        if (IsDefaultExcludedFileName(fileName))
            return false;

        if (activeIgnoreRules.IsIgnored(file, isDirectory: false))
            return false;

        return TryAcceptSupportedScannedFile(file, scanState);
    }

    private bool TryAcceptSupportedScannedFile(string file, DirectoryScanState scanState)
    {
        // Use the instance symlink policy here so full scans and update paths apply the same
        // file-link behavior.
        // full scan と update 経路で同じ file-link 挙動になるよう instance の symlink policy を使う。
        var indexability = GetFileIndexabilityForIndexing(file);
        if (indexability == FileProbeStatus.Missing)
        {
            var relativePath = ToRelativePath(file);
            scanState.Errors.Add(new ScanError(
                relativePath,
                "Skipped file because it was deleted during scanning.",
                ScanIssueSeverity.Warning));
            scanState.NonIndexablePaths.Add(relativePath);
            return false;
        }

        if (indexability == FileProbeStatus.ProbeFailed)
        {
            var relativePath = ToRelativePath(file);
            scanState.Errors.Add(new ScanError(relativePath, "Could not probe file for indexability/language."));
            scanState.ProbeFailedFilePaths.Add(relativePath);
            return false;
        }

        if (indexability != FileProbeStatus.Supported)
        {
            scanState.NonIndexablePaths.Add(ToRelativePath(file));
            return false;
        }

        var relativeFile = ToRelativePath(file);
        // Include files with a known extension/filename or an extensionless recognized shebang
        // 既知の拡張子・既知ファイル名、または拡張子なしで shebang を認識できるファイルを含める
        var language = TryDetectLanguageForIndexing(file);
        if (language.Status == FileProbeStatus.Missing)
        {
            scanState.Errors.Add(new ScanError(
                relativeFile,
                "Skipped file because it was deleted during scanning.",
                ScanIssueSeverity.Warning));
            scanState.NonIndexablePaths.Add(relativeFile);
            return false;
        }

        if (language.Status == FileProbeStatus.ProbeFailed)
        {
            scanState.Errors.Add(new ScanError(relativeFile, "Could not probe file for indexability/language."));
            scanState.ProbeFailedFilePaths.Add(relativeFile);
            return false;
        }

        if (language.Status != FileProbeStatus.Supported)
        {
            scanState.NonIndexablePaths.Add(relativeFile);
            if (HasUnknownExtension(file) && !IsInternalIndexArtifactPath(relativeFile))
                scanState.UnknownExtensionFiles.Add(relativeFile);
            return false;
        }

        if (TryGetFileIdentity(file, out var identity) && !scanState.VisitedFileIdentities.Add(identity))
        {
            scanState.Errors.Add(new ScanError(
                relativeFile,
                "Skipped hardlinked file because the same file content was already indexed from another path.",
                ScanIssueSeverity.Warning));
            scanState.NonIndexablePaths.Add(relativeFile);
            return false;
        }

        return true;
    }

    private void RecordDanglingFileSystemEntries(
        string dir,
        DirectoryScanState scanState,
        CancellationToken cancellationToken)
    {
        var candidateLimit = _maxDanglingFileSystemEntryScanCandidates;
        var candidateCount = 0;
        foreach (var enumeratedEntry in Directory.EnumerateFileSystemEntries(LongPath.EnsureWindowsPrefix(dir)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateCount++;
            if (candidateCount > candidateLimit)
            {
                var relativeDir = ToRelativePath(dir);
                scanState.Errors.Add(new ScanError(
                    relativeDir,
                    $"Dangling filesystem entry scan truncated after {candidateLimit:N0} candidate(s); additional dangling symlink diagnostics in this directory may be omitted.",
                    ScanIssueSeverity.Warning));
                return;
            }

            var entry = LongPath.RemoveWindowsPrefix(enumeratedEntry);
            if (!IsReparsePoint(entry) || ReparsePointTargetExists(entry))
                continue;

            var relativeEntry = ToRelativePath(entry);
            scanState.DanglingSymlinks.Add(relativeEntry);
            scanState.Errors.Add(new ScanError(relativeEntry, "Skipped dangling symlink because its target could not be resolved.", ScanIssueSeverity.Warning));
            scanState.ListedDirectories.Add(relativeEntry);
            scanState.FullyScannedDirectories.Add(relativeEntry);
            scanState.AttributePrunedDirectories.Add(relativeEntry);
        }
    }

    private static bool ReparsePointTargetExists(string path)
    {
        var entryPath = LongPath.EnsureWindowsPrefix(path);
        if (Directory.Exists(entryPath))
            return true;

        try
        {
            FileInfo info = new(entryPath);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target?.FullName is not { Length: > 0 } targetPath)
                return false;

            var targetEntryPath = LongPath.EnsureWindowsPrefix(targetPath);
            return File.Exists(targetEntryPath) || Directory.Exists(targetEntryPath);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private bool EnumerateSubdirectories(
        string dir,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool passthrough,
        bool continueOnError,
        CancellationToken cancellationToken,
        int depth)
    {
        var fullyScanned = true;
        foreach (var enumeratedSubDir in Directory.EnumerateDirectories(LongPath.EnsureWindowsPrefix(dir)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subDir = LongPath.RemoveWindowsPrefix(enumeratedSubDir);
            if (TryRecordNonRecursiveSubdirectory(subDir, scanState, passthrough))
                continue;

            // Skip directory symlinks/reparse points to prevent infinite recursion on ancestor loops
            // and duplicate indexing when a symlink points inside the same tree. On Windows, also
            // skip Hidden/System directories so drive-root scans do not descend into OS-owned caches.
            // Record the skipped directory itself as listed (for the immediate-parent purge path) AND
            // as a prune prefix so the purge walker can authoritatively drop deep descendants that
            // earlier runs left behind.
            // ディレクトリ symlink / reparse point は親方向ループでの無限再帰や、
            // ツリー内を指す symlink での二重 index を防ぐためスキップする。Windows では
            // drive root 走査で OS 管理 cache に降りないよう Hidden/System ディレクトリもスキップする。
            // skip したディレクトリ自身を listed 扱い（immediate parent purge 用）かつ prune prefix として
            // 記録することで、以前の実行でできた深い子孫エントリも purge walker が確実に削除できる。
            if (ShouldSkipDirectoryLink(subDir, scanState.Errors, scanState.DanglingSymlinks))
            {
                RecordPrunedDirectory(subDir, scanState);
                continue;
            }

            var resolvedSubDir = NormalizePathForComparison(GetDirectoryTraversalIdentity(subDir));
            if (!scanState.VisitedDirectories.Add(resolvedSubDir))
            {
                var subRelative = ToRelativePath(subDir);
                scanState.Errors.Add(new ScanError(subRelative, "Skipped symlinked directory because its resolved target was already scanned.", ScanIssueSeverity.Warning));
                RecordPrunedDirectory(subDir, scanState);
                continue;
            }

            var childFullyScanned = ScanDirectory(subDir, scanState, activeIgnoreRules, continueOnError: continueOnError, cancellationToken: cancellationToken, depth: depth + 1);
            fullyScanned &= childFullyScanned;
            if (!continueOnError && !childFullyScanned)
                break;
        }

        return fullyScanned;
    }

    private bool TryRecordNonRecursiveSubdirectory(string subDir, DirectoryScanState scanState, bool passthrough)
    {
        if (IsNestedGitRepository(subDir) && !IsSubmoduleOrAncestor(subDir))
        {
            var subRelative = ToRelativePath(subDir);
            scanState.ListedDirectories.Add(subRelative);
            scanState.FullyScannedDirectories.Add(subRelative);
            scanState.NestedRepositories.Add(subRelative);
            return true;
        }

        // In passthrough mode, only descend into subdirectories that are themselves
        // submodules or submodule ancestors. Treat siblings the same way SkipDirs
        // would have treated them at this point.
        // passthrough 中は、submodule 自体または submodule の祖先に該当する
        // サブディレクトリのみ降りる。その他は本来 SkipDirs で止まっていた扱いに戻す。
        if (passthrough && !IsSubmoduleOrAncestor(subDir))
        {
            var subRelative = ToRelativePath(subDir);
            scanState.ListedDirectories.Add(subRelative);
            scanState.FullyScannedDirectories.Add(subRelative);
            return true;
        }

        return false;
    }

    private void RecordPrunedDirectory(string dir, DirectoryScanState scanState)
    {
        var relativeDir = ToRelativePath(dir);
        scanState.ListedDirectories.Add(relativeDir);
        scanState.FullyScannedDirectories.Add(relativeDir);
        scanState.AttributePrunedDirectories.Add(relativeDir);
    }

    internal static bool TryGetFileIdentity(string path, out FileIdentity identity)
    {
        identity = default;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            return false;

        try
        {
            if (OperatingSystem.IsWindows())
                return TryGetWindowsFileIdentity(path, out identity);

            if (OperatingSystem.IsMacOS())
            {
                if (StatMac(path, out var stat) != 0)
                    return false;

                identity = new FileIdentity((uint)stat.DeviceId, stat.Inode);
                return true;
            }

            if (StatLinux(path, out var linuxStat) != 0)
                return false;

            identity = new FileIdentity(linuxStat.DeviceId, linuxStat.Inode);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGetWindowsFileIdentity(string path, out FileIdentity identity)
    {
        identity = default;
        using var handle = CreateFile(
            path,
            desiredAccess: 0,
            shareMode: FileShare.ReadWrite | FileShare.Delete,
            securityAttributes: IntPtr.Zero,
            creationDisposition: FileMode.Open,
            flagsAndAttributes: FileAttributes.Normal,
            templateFile: IntPtr.Zero);
        if (handle.IsInvalid)
            return false;

        if (!GetFileInformationByHandle(handle, out var info))
            return false;

        var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        identity = new FileIdentity(info.VolumeSerialNumber, fileIndex);
        return true;
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatLinux(string path, out LinuxStat stat);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatMac(string path, out MacStat stat);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        [MarshalAs(UnmanagedType.U4)] FileAccess desiredAccess,
        [MarshalAs(UnmanagedType.U4)] FileShare shareMode,
        IntPtr securityAttributes,
        [MarshalAs(UnmanagedType.U4)] FileMode creationDisposition,
        [MarshalAs(UnmanagedType.U4)] FileAttributes flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle fileHandle, out WindowsFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong DeviceId;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint Uid;
        public uint Gid;
        public int Pad0;
        public ulong Rdev;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public long AccessTimeSeconds;
        public long AccessTimeNanoseconds;
        public long ModificationTimeSeconds;
        public long ModificationTimeNanoseconds;
        public long ChangeTimeSeconds;
        public long ChangeTimeNanoseconds;
        public long Unused0;
        public long Unused1;
        public long Unused2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacStat
    {
        public int DeviceId;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint Uid;
        public uint Gid;
        public int Rdev;
        public MacTimespec AccessTime;
        public MacTimespec ModificationTime;
        public MacTimespec ChangeTime;
        public MacTimespec BirthTime;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        public int Spare;
        public long Qspare0;
        public long Qspare1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    private static bool HasUnknownExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(extension)
            && !LangMap.ContainsKey(extension)
            && !ExtractorPluginRegistry.LanguageExtensions.ContainsKey(extension);
    }

    private static bool IsInternalIndexArtifactPath(string relativePath)
        => relativePath.Equals(".cdidx", StringComparison.Ordinal)
            || relativePath.StartsWith(".cdidx/", StringComparison.Ordinal);

    private PathFilterKind GetDirectoryFilterKind(string dir, IgnoreRuleSet activeIgnoreRules, bool isProjectRoot = false)
    {
        if (!isProjectRoot)
        {
            var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
            if (SkipDirs.Contains(dirName) && !IsSubmoduleOrAncestor(dir))
                return PathFilterKind.ExcludedByDefaultDirectory;
        }

        return activeIgnoreRules.IsIgnored(dir, isDirectory: true)
            ? PathFilterKind.IgnoredByRules
            : PathFilterKind.None;
    }

    // True when relpath under _projectRoot matches a .gitmodules-declared submodule
    // working-tree path or one of its ancestor directories. Allows the walker to
    // descend through SkipDirs-named ancestors (e.g. vendor/) to reach declared
    // submodules without dropping the broader SkipDirs policy elsewhere.
    // _projectRoot 配下の相対パスが .gitmodules で宣言された submodule のワークツリーまたは
    // その祖先ディレクトリに一致するときに true。vendor/ のような SkipDirs 名の祖先を
    // 通過して submodule に到達できるよう、限定的に SkipDirs を上書きする。
    private bool IsSubmoduleOrAncestor(string dir)
    {
        if (_submodulePaths.Count == 0)
            return false;
        var relPath = ToRelativePath(dir);
        if (relPath.Length == 0)
            return false;
        return _submodulePaths.Contains(relPath) || _submoduleAncestorPaths.Contains(relPath);
    }

    private bool IsSubmoduleAncestorPassthrough(string dir)
    {
        if (_submoduleAncestorPaths.Count == 0)
            return false;
        var relPath = ToRelativePath(dir);
        if (relPath.Length == 0)
            return false;
        if (_submodulePaths.Contains(relPath))
            return false;
        if (!_submoduleAncestorPaths.Contains(relPath))
            return false;
        // Passthrough propagates from any SkipDirs-named ancestor along the path. If no
        // segment of relPath matches SkipDirs, this directory would have been walked
        // normally without our override, so the override is not in effect here.
        // SkipDirs 名の祖先からは下方向に passthrough を伝播する。relPath のどの segment も
        // SkipDirs に該当しない場合、我々の上書き無しでも walker は通っていたはずなので
        // ここでの上書きは効いていない。
        var segments = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (SkipDirs.Contains(segment))
                return true;
        }
        return false;
    }

    private IgnoreRuleLoadResult LoadIgnoreRulesForDirectory(
        string dir,
        IgnoreRuleSet inheritedIgnoreRules,
        List<ScanError> errors,
        ref bool fullyScanned)
    {
        var rules = new List<IgnoreRule>();
        var ignoreRulesAvailable = true;

        foreach (var ignoreFileName in IgnoreFileNames)
        {
            var ignorePath = Path.Combine(dir, ignoreFileName);
            if (!TryAppendIgnoreRulesFromFile(
                    dir,
                    ignorePath,
                    ignoreFileName,
                    rules,
                    errors,
                    ref fullyScanned))
            {
                fullyScanned = false;
                ignoreRulesAvailable = false;
            }
        }

        return ignoreRulesAvailable
            ? new IgnoreRuleLoadResult(IgnoreRuleSet.CreateChild(inheritedIgnoreRules, rules), IgnoreRulesAvailable: true)
            : new IgnoreRuleLoadResult(inheritedIgnoreRules, IgnoreRulesAvailable: false);
    }

    private IgnoreRuleLoadResult LoadWorkspaceConfigIgnoreRules(
        IgnoreRuleSet inheritedIgnoreRules,
        List<ScanError> errors,
        ref bool fullyScanned)
    {
        var configIgnorePath = Path.Combine(_projectRoot, ".codeindex", ".cdidxignore");
        return LoadIgnoreRulesFile(
            sourceDirectory: _projectRoot,
            ignorePath: configIgnorePath,
            ignoreFileName: ".codeindex/.cdidxignore",
            inheritedIgnoreRules,
            errors,
            ref fullyScanned);
    }

    private IgnoreRuleLoadResult LoadAncestorIgnoreRules(List<ScanError> errors, ref bool fullyScanned)
    {
        var activeIgnoreRules = IgnoreRuleSet.Empty;
        foreach (var dir in _ancestorIgnoreDirectories)
        {
            if (!CanReadDirectory(dir, out var reason))
            {
                errors.Add(new ScanError(ToRelativePath(dir), $"Could not read ancestor ignore directory: {reason}."));
                fullyScanned = false;
                return new IgnoreRuleLoadResult(activeIgnoreRules, IgnoreRulesAvailable: false);
            }

            var loadResult = LoadIgnoreRulesForDirectory(dir, activeIgnoreRules, errors, ref fullyScanned);
            activeIgnoreRules = loadResult.Rules;
            if (!loadResult.IgnoreRulesAvailable)
                return new IgnoreRuleLoadResult(activeIgnoreRules, IgnoreRulesAvailable: false);
        }

        return LoadWorkspaceConfigIgnoreRules(activeIgnoreRules, errors, ref fullyScanned);
    }

    private IgnoreRuleLoadResult LoadIgnoreRulesFile(
        string sourceDirectory,
        string ignorePath,
        string ignoreFileName,
        IgnoreRuleSet inheritedIgnoreRules,
        List<ScanError> errors,
        ref bool fullyScanned)
    {
        var rules = new List<IgnoreRule>();
        if (!TryAppendIgnoreRulesFromFile(
                sourceDirectory,
                ignorePath,
                ignoreFileName,
                rules,
                errors,
                ref fullyScanned))
        {
            fullyScanned = false;
            return new IgnoreRuleLoadResult(inheritedIgnoreRules, IgnoreRulesAvailable: false);
        }

        return new IgnoreRuleLoadResult(IgnoreRuleSet.CreateChild(inheritedIgnoreRules, rules), IgnoreRulesAvailable: true);
    }

    private bool TryAppendIgnoreRulesFromFile(
        string sourceDirectory,
        string ignorePath,
        string ignoreFileName,
        List<IgnoreRule> rules,
        List<ScanError> errors,
        ref bool fullyScanned)
    {
        var prefixedIgnorePath = LongPath.EnsureWindowsPrefix(ignorePath);

        try
        {
            if (!TryReadBoundedUtf8SidecarLines(
                    prefixedIgnorePath,
                    MaxIgnoreFileBytes,
                    MaxIgnoreFileLines,
                    out var lines,
                    out var skippedReason,
                    out var readFailure))
            {
                if (readFailure.ExceptionType is nameof(FileNotFoundException) or nameof(DirectoryNotFoundException))
                    return true;

                errors.Add(new ScanError(
                    ToRelativePath(ignorePath),
                    $"Could not safely read {ignoreFileName} because {skippedReason}."));
                fullyScanned = false;
                return false;
            }

            var lineNumber = 0;
            var rulesInFile = 0;
            foreach (var line in lines)
            {
                lineNumber++;
                if (IgnoreRule.TryParse(sourceDirectory, line, _ignoreCase, out var rule, out var errorMessage) && rule != null)
                {
                    if (rulesInFile >= MaxIgnoreRulesPerFile)
                    {
                        errors.Add(new ScanError(
                            $"{ToRelativePath(ignorePath)}:{lineNumber}",
                            $"Stopped scanning because {ignoreFileName} exceeds {MaxIgnoreRulesPerFile} rules."));
                        fullyScanned = false;
                        return false;
                    }

                    rules.Add(rule);
                    rulesInFile++;
                }
                else if (errorMessage != null)
                {
                    errors.Add(new ScanError($"{ToRelativePath(ignorePath)}:{lineNumber}", errorMessage, ScanIssueSeverity.Warning));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (!File.Exists(prefixedIgnorePath))
                throw;

            errors.Add(new ScanError(ToRelativePath(ignorePath), $"Could not read {ignoreFileName} due to permissions.", ScanIssueSeverity.Warning));
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            errors.Add(new ScanError(ToRelativePath(ignorePath), $"Could not read {ignoreFileName}."));
            fullyScanned = false;
            return false;
        }

        return true;
    }

    private string NormalizeIgnoreRuleRoot(string? ignoreRuleRoot)
    {
        if (string.IsNullOrWhiteSpace(ignoreRuleRoot))
            return _projectRoot;

        var candidate = Path.GetFullPath(ignoreRuleRoot);
        return IsPathEqualOrParent(candidate, _projectRoot)
            ? candidate
            : _projectRoot;
    }

    private static IReadOnlyList<string> BuildAncestorIgnoreDirectories(string ignoreRuleRoot, string projectRoot)
    {
        if (PathsEqual(ignoreRuleRoot, projectRoot))
            return [];

        if (!IsPathEqualOrParent(ignoreRuleRoot, projectRoot))
            return [];

        var directories = new Stack<string>();
        var root = Path.GetFullPath(ignoreRuleRoot);
        var current = Directory.GetParent(Path.GetFullPath(projectRoot));
        while (current != null)
        {
            directories.Push(current.FullName);
            if (PathsEqual(current.FullName, root))
                return directories.ToList();

            current = current.Parent;
        }

        return [];
    }

    private static bool CanReadDirectory(string dir, out string reason)
    {
        if (!Directory.Exists(LongPath.EnsureWindowsPrefix(dir)))
        {
            reason = "directory-missing";
            return false;
        }

        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(LongPath.EnsureWindowsPrefix(dir)).GetEnumerator();
            _ = enumerator.MoveNext();
            reason = string.Empty;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "access-denied";
            return false;
        }
        catch (IOException)
        {
            reason = "io-error";
            return false;
        }
    }

    // Parse <ignoreRuleRoot>/.gitmodules and return submodule working-tree paths (and
    // their ancestor directories) relative to projectRoot. Submodules outside projectRoot
    // are dropped silently. Absent or unreadable .gitmodules yields empty sets so callers
    // see the same shape as a non-submodule repository.
    // <ignoreRuleRoot>/.gitmodules を解析し、projectRoot 相対の submodule ワークツリーパスと
    // その祖先ディレクトリを返す。projectRoot 外の submodule は無視。.gitmodules が無い・
    // 読めない場合は空集合を返し、submodule の無いリポジトリと同じ形を保つ。
    private static (HashSet<string> Paths, HashSet<string> AncestorPaths, IReadOnlyList<ScanError> Warnings) LoadGitSubmodulePaths(
        string ignoreRuleRoot, string projectRoot, StringComparer pathComparer)
    {
        var submodulePaths = new HashSet<string>(pathComparer);
        var ancestorPaths = new HashSet<string>(pathComparer);
        var warnings = new List<ScanError>();

        var gitmodulesPath = Path.Combine(ignoreRuleRoot, ".gitmodules");
        var prefixedGitmodulesPath = LongPath.EnsureWindowsPrefix(gitmodulesPath);
        if (!File.Exists(prefixedGitmodulesPath))
            return (submodulePaths, ancestorPaths, warnings);

        try
        {
            IReadOnlyList<string> lines;
            if (ReadGitmodulesLinesForTesting is { } readGitmodulesLines)
            {
                lines = readGitmodulesLines(prefixedGitmodulesPath);
            }
            else
            {
                if (!TryReadBoundedUtf8SidecarLines(
                        prefixedGitmodulesPath,
                        MaxGitmodulesBytes,
                        MaxGitmodulesLines,
                        out lines,
                        out var skippedReason,
                        out _))
                {
                    warnings.Add(new ScanError(
                        NormalizeIgnorePath(Path.GetRelativePath(projectRoot, gitmodulesPath)),
                        $"Skipped .gitmodules because {skippedReason}.",
                        ScanIssueSeverity.Warning));
                    return (submodulePaths, ancestorPaths, warnings);
                }
            }

            var submodulePathCount = 0;
            foreach (var rawSubmodulePath in ParseSubmodulePathsFromGitmodules(lines))
            {
                string absolute;
                try
                {
                    absolute = Path.GetFullPath(Path.Combine(ignoreRuleRoot, rawSubmodulePath));
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var relativeToProject = NormalizeIgnorePath(Path.GetRelativePath(projectRoot, absolute));
                if (relativeToProject.Length == 0
                    || relativeToProject == "."
                    || relativeToProject.StartsWith("../", StringComparison.Ordinal))
                {
                    continue;
                }

                if (submodulePathCount >= MaxGitmodulesSubmodulePaths)
                {
                    warnings.Add(new ScanError(
                        NormalizeIgnorePath(Path.GetRelativePath(projectRoot, gitmodulesPath)),
                        $"Stopped parsing .gitmodules submodule paths after {MaxGitmodulesSubmodulePaths} entries.",
                        ScanIssueSeverity.Warning));
                    break;
                }

                submodulePathCount++;
                if (submodulePaths.Add(relativeToProject))
                {
                    var segments = relativeToProject.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 1; i < segments.Length; i++)
                        ancestorPaths.Add(string.Join('/', segments, 0, i));
                }
            }
        }
        catch (IOException ex)
        {
            AddGitmodulesDiscoveryWarning(warnings, projectRoot, gitmodulesPath, ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            AddGitmodulesDiscoveryWarning(warnings, projectRoot, gitmodulesPath, ex.GetType().Name);
        }

        return (submodulePaths, ancestorPaths, warnings);
    }

    private static void AddGitmodulesDiscoveryWarning(
        List<ScanError> warnings,
        string projectRoot,
        string gitmodulesPath,
        string exceptionType)
    {
        warnings.Add(new ScanError(
            NormalizeIgnorePath(Path.GetRelativePath(projectRoot, gitmodulesPath)),
            $"Skipped .gitmodules because it could not be read ({exceptionType}).",
            ScanIssueSeverity.Warning));
    }

    private static bool TryReadBoundedUtf8SidecarLines(
        string path,
        int maxBytes,
        int maxLines,
        out IReadOnlyList<string> lines,
        out string skippedReason,
        out BoundedTextFileReadFailure failure)
    {
        var success = BoundedLineReader.TryReadUtf8File(
            path,
            maxBytes,
            maxLines,
            MaxGitmodulesLineChars,
            out lines,
            out failure);
        skippedReason = success ? string.Empty : failure.Reason;
        return success;
    }

    // Tolerant .gitmodules reader: yields each declared submodule's "path = ..." value.
    // Supports comments (# / ;), inline comments, surrounding double quotes, and
    // ignores absolute or empty values. Quoted-string escapes are not expanded since
    // submodule paths in practice are plain relative filesystem paths.
    // .gitmodules を寛容に読み、各 submodule の "path = ..." 値を返す。コメント(# / ;)、
    // インラインコメント、両端のダブルクオート、絶対パス・空値の除外をサポート。実用上の
    // submodule パスは通常のファイル名なのでクォート内のエスケープは展開しない。
    private static IEnumerable<string> ParseSubmodulePathsFromGitmodules(IEnumerable<string> lines)
    {
        var inSubmoduleSection = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (line[0] == '#' || line[0] == ';')
                continue;

            if (line[0] == '[')
            {
                var endBracket = line.IndexOf(']');
                if (endBracket < 0)
                {
                    inSubmoduleSection = false;
                    continue;
                }

                var sectionHeader = line.Substring(1, endBracket - 1).Trim();
                inSubmoduleSection = sectionHeader.StartsWith("submodule", StringComparison.OrdinalIgnoreCase)
                    && sectionHeader.Length > "submodule".Length
                    && char.IsWhiteSpace(sectionHeader["submodule".Length]);
                continue;
            }

            if (!inSubmoduleSection)
                continue;

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex < 0)
                continue;
            var key = line.Substring(0, equalsIndex).Trim();
            if (!string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = StripGitmodulesInlineComment(line[(equalsIndex + 1)..]);
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            if (value.Length == 0)
                continue;
            if (Path.IsPathRooted(value))
                continue;

            yield return value;
        }
    }

    private static string StripGitmodulesInlineComment(string value)
    {
        var inQuotes = false;
        var escaping = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (escaping)
            {
                escaping = false;
                continue;
            }

            if (inQuotes && ch == '\\')
            {
                escaping = true;
                continue;
            }

            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && ch is '#' or ';')
                return value[..i].Trim();
        }

        return value.Trim();
    }

    internal static string NormalizeIgnorePath(string path)
        => NormalizePathSeparators(path).TrimEnd('/');

    /// <summary>
    /// Normalize OS path separators to '/' for DB storage and lookup.
    /// On Windows this converts '\' to '/'. On POSIX it returns the path
    /// unchanged so filenames that legitimately contain '\' (e.g. "back\slash.py")
    /// survive round-trip through the index.
    /// DB は '/' 固定で保存するため OS に応じて区切り文字だけを正規化する。
    /// Windows は '\' を '/' に変換し、POSIX ではファイル名内の '\' を壊さないよう何もしない。
    /// </summary>
    public static string NormalizePathSeparators(string path)
        => Path.DirectorySeparatorChar == '\\' ? path.Replace('\\', '/') : path;

    /// <summary>
    /// Normalize index paths to the DB invariant: platform separators plus Unicode NFC.
    /// DB 保存・lookup 用 path は区切り文字正規化に加えて Unicode NFC に正規化する。
    /// </summary>
    public static string NormalizeIndexPath(string path)
        => NormalizePathSeparators(path).Normalize(NormalizationForm.FormC);

    /// <summary>
    /// Build a FileRecord and return file content (avoids reading the file twice).
    /// FileRecordを構築しファイル内容も返す（二重読み込み防止）。
    /// </summary>
    public (FileRecord record, string content, string? warning) BuildRecord(string absolutePath, CancellationToken cancellationToken = default)
    {
        var (record, content, _, warning) = BuildRecordWithRawBytes(absolutePath, cancellationToken);
        return (record, content, warning);
    }

    /// <summary>
    /// Build a FileRecord and return both decoded content and raw bytes.
    /// Callers can run encoding validation without a second file read.
    /// FileRecordを構築し、デコード済み内容とraw bytesを返す。
    /// 呼び出し側は再読込なしでエンコーディング検証できる。
    /// </summary>
    public (FileRecord record, string content, byte[] rawBytes, string? warning) BuildRecordWithRawBytes(string absolutePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsFilePathSyntaxIndexable(absolutePath))
            throw new InvalidOperationException("Cannot index a file path that contains NUL or control characters.");

        var indexability = GetFileIndexabilityForIndexing(absolutePath);
        if (indexability != FileProbeStatus.Supported)
            throw new InvalidOperationException("Only regular files can be indexed");

        var relativePath = Path.GetRelativePath(_projectRoot, absolutePath);
        var normalizedRelativePath = NormalizeIndexPath(relativePath);

        var loaded = _contentLoader.Load(
            absolutePath,
            normalizedRelativePath,
            relativePath,
            cancellationToken);
        var record = new FileRecord
        {
            Path = normalizedRelativePath,
            Lang = TryDetectLanguageForIndexing(absolutePath, loaded.Content).Language,
            Size = loaded.SizeBytes,
            Lines = loaded.LineCount,
            Checksum = loaded.Checksum,
            Modified = loaded.ModifiedUtc,
            Generated = IsGeneratedCodeFile(normalizedRelativePath, loaded.Content),
        };

        return (record, loaded.Content, loaded.RawBytes, loaded.Warning);
    }

    public FileRecord BuildSkippedFileRecord(string absolutePath)
    {
        if (!IsFilePathSyntaxIndexable(absolutePath))
            throw new InvalidOperationException("Cannot index a file path that contains NUL or control characters.");

        var relativePath = Path.GetRelativePath(_projectRoot, absolutePath);
        var normalizedRelativePath = NormalizeIndexPath(relativePath);
        var ioPath = LongPath.EnsureWindowsPrefix(absolutePath);
        var info = new FileInfo(ioPath);
        return new FileRecord
        {
            Path = normalizedRelativePath,
            Lang = TryDetectLanguageForIndexing(absolutePath).Language,
            Size = info.Exists ? info.Length : 0,
            Lines = 0,
            Checksum = null,
            Modified = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
            Generated = HasGeneratedCodeFileName(normalizedRelativePath),
        };
    }

    internal static bool IsFilePathSyntaxIndexable(string path)
    {
        foreach (var c in path)
        {
            if (c < ' ')
                return false;
        }

        return true;
    }

    private string FormatPathForScanIssue(string absolutePath)
    {
        var displayPath = absolutePath;
        try
        {
            displayPath = Path.GetRelativePath(_projectRoot, absolutePath);
        }
        catch (ArgumentException)
        {
        }

        return EscapeControlCharacters(NormalizePathSeparators(displayPath));
    }

    private static string EscapeControlCharacters(string value)
    {
        var firstControl = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] < ' ')
            {
                firstControl = i;
                break;
            }
        }

        if (firstControl < 0)
            return value;

        var builder = new StringBuilder(value.Length + 8);
        if (firstControl > 0)
            builder.Append(value, 0, firstControl);
        for (var i = firstControl; i < value.Length; i++)
        {
            var c = value[i];
            if (c < ' ')
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            else
                builder.Append(c);
        }

        return builder.ToString();
    }

    internal static bool IsGeneratedCodeFile(string relativePath, string content)
        => HasGeneratedCodeFileName(relativePath) || HasGeneratedCodeHeader(content);

    internal const string GeneratedCodeExtractionSkippedIssueKind = "generated_code_extraction_skipped";

    internal FileIssue? BuildGeneratedCodeExtractionSkippedIssue(string relativePath)
        => _generatedCodePatterns.TryMatch(relativePath, out _)
            ? new FileIssue
            {
                Path = relativePath,
                Kind = GeneratedCodeExtractionSkippedIssueKind,
                Line = 0,
                Message = "Generated-code extraction suppressed by project configuration; file content and chunks were indexed, but symbols and references were skipped.",
                Origin = "generated_code_pattern",
                Severity = FileIssue.SeverityInfo,
            }
            : null;

    internal static int CountPhysicalLines(string content)
    {
        if (content.Length == 0)
            return 0;

        var lines = 1;
        var lastWasLineBreak = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c != '\r' && c != '\n')
            {
                lastWasLineBreak = false;
                continue;
            }

            lastWasLineBreak = true;
            if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                i++;

            if (i + 1 < content.Length)
                lines++;
        }

        return lastWasLineBreak ? Math.Max(lines, 1) : lines;
    }

    public static void ValidateSymbolLineRanges(FileRecord record, IReadOnlyList<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            ValidateSymbolLine(record, symbol, symbol.Line, nameof(symbol.Line));
            ValidateSymbolLine(record, symbol, symbol.StartLine, nameof(symbol.StartLine), allowZero: true);
            ValidateSymbolLine(record, symbol, symbol.EndLine, nameof(symbol.EndLine), allowZero: true, allowOnePastEnd: true);
            ValidateSymbolLine(record, symbol, symbol.BodyStartLine, nameof(symbol.BodyStartLine), allowOnePastEnd: true);
            ValidateSymbolLine(record, symbol, symbol.BodyEndLine, nameof(symbol.BodyEndLine), allowOnePastEnd: true);
        }
    }

    private static void ValidateSymbolLine(FileRecord record, SymbolRecord symbol, int? line, string fieldName, bool allowZero = false, bool allowOnePastEnd = false)
    {
        if (line is null)
            return;

        if (allowZero && line == 0)
            return;

        var maxLine = record.Lines + (allowOnePastEnd ? 1 : 0);
        if (line < 1 || line > maxLine)
        {
            throw new InvalidOperationException(
                $"{record.Path}: extracted symbol '{symbol.Name}' has {fieldName}={line}, outside file line range 1..{maxLine}");
        }
    }

    private static bool HasGeneratedCodeFileName(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.dart", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".gen.go", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".generated.ts", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("_pb.go", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("_pb2.py", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasGeneratedCodeHeader(string content)
    {
        var lineStart = 0;
        for (var lineNumber = 0; lineNumber < 20 && lineStart <= content.Length; lineNumber++)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = content.Length;

            var line = content[lineStart..lineEnd].Trim();
            if (line.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)
                || line.Contains("@generated", StringComparison.OrdinalIgnoreCase)
                || (line.Contains("generated by", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("DO NOT EDIT", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (lineEnd == content.Length)
                break;
            lineStart = lineEnd + 1;
        }

        return false;
    }

    internal static string NormalizeLineEndings(string content)
        => FileContentLoader.NormalizeLineEndings(content);

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

    /// <summary>
    /// Validate file content for encoding issues.
    /// ファイル内容のエンコーディング問題を検証する。
    /// </summary>
    public static List<FileIssue> ValidateContent(string relativePath, byte[] rawBytes, string content, string? language = null)
    {
        var issues = new List<FileIssue>();

        if (IsGitLfsPointer(rawBytes))
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "lfs_pointer_skipped",
                Line = 1,
                Message = "Git LFS pointer file skipped; fetch LFS objects to index real file content",
            });
        }

        // UTF-16 BOM-detected files are decoded as UTF-16 in BuildRecordWithRawBytes, so the
        // raw-byte heuristics for `bom` / `null_byte` / `mixed_line_endings` would all misfire
        // (every UTF-16 LE character ASCII point looks like a NUL byte; CRLF appears as 0D 00
        // 0A 00). Emit a single `utf16_bom` issue instead so `validate` clearly explains the
        // file was decoded via UTF-16. The content-side U+FFFD check still runs so genuine
        // invalid surrogate pairs are reported. Closes #1540.
        // UTF-16 BOM 検出ファイルは BuildRecordWithRawBytes で UTF-16 デコード済みのため、
        // 生バイト系の `bom` / `null_byte` / `mixed_line_endings` 判定はすべて誤検出する
        // (UTF-16 LE では ASCII 部の片バイトが NUL、CRLF は 0D 00 0A 00)。代わりに
        // `utf16_bom` 1 件を出して `validate` が「UTF-16 として解釈した」ことを示し、
        // 不正サロゲートペアに備え content 側 U+FFFD 走査は継続する。Closes #1540.
        var isUtf16 = TryDetectUtf16Encoding(rawBytes, allowHeuristic: true, out var utf16BigEndian, out var hasUtf16Bom);

        if (isUtf16)
        {
            if (hasUtf16Bom)
                AddUtf16BomIssue(issues, relativePath, utf16BigEndian);
            else
                AddUtf16HeuristicIssue(issues, relativePath, utf16BigEndian);
        }

        if (TryGetConflictMarkerLine(content, out var conflictMarkerLine))
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "conflict_markers",
                Line = conflictMarkerLine,
                Message = "Git conflict markers detected; resolve the conflict before indexing symbols or references",
            });
        }

        AddReplacementCharacterIssues(issues, relativePath, rawBytes, content, isUtf16, utf16BigEndian, hasUtf16Bom);

        // Raw-byte heuristics: skip for UTF-16-decoded files because every UTF-16 LE ASCII
        // codepoint looks like a NUL byte and CRLF appears as 0D 00 0A 00, so `bom` /
        // `null_byte` / `mixed_line_endings` / `cr_only_line_endings` would all misfire.
        // UTF-16 デコード経路では生バイト列が NUL バイトと 0D 00 0A 00 で埋まり、`bom` /
        // `null_byte` / `mixed_line_endings` / `cr_only_line_endings` がすべて誤検出する
        // ためスキップする。
        if (!isUtf16)
            AddRawByteContentIssues(issues, relativePath, rawBytes);

        AddOversizeContentIssues(issues, relativePath, content);
        var effectiveLanguage = language ?? TryDetectLanguage(relativePath, content).Language;
        if (effectiveLanguage is "xml" or "msbuild")
            AddXmlStructureIssues(issues, relativePath, content);
        if (effectiveLanguage == "dockerfile")
        {
            AddDockerfileJsonFormIssues(issues, relativePath, content);
        }

        return issues;
    }

    private static void AddXmlStructureIssues(List<FileIssue> issues, string relativePath, string content)
    {
        if (!SymbolExtractor.TryGetXmlStructureIssue(content, out var issue))
            return;

        issues.Add(new FileIssue
        {
            Path = relativePath,
            Kind = issue.Kind,
            Line = issue.Line,
            Message = issue.Message,
            Severity = FileIssue.SeverityWarning,
        });
    }

    private static void AddDockerfileJsonFormIssues(List<FileIssue> issues, string relativePath, string content)
    {
        var emitted = 0;
        var diagnosticsTruncated = false;
        var lineNumber = 1;
        var lineStart = 0;
        while (lineStart <= content.Length)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = content.Length;

            var line = content[lineStart..lineEnd];
            if (TryGetDockerfileJsonFormPayload(line, out var instruction, out var payload))
            {
                if (!TryAddDockerfileJsonFormIssue(issues, relativePath, instruction, payload, lineNumber, ref emitted))
                {
                    diagnosticsTruncated = true;
                    break;
                }
            }

            if (lineEnd == content.Length)
                break;

            lineNumber++;
            lineStart = lineEnd + 1;
        }

        if (diagnosticsTruncated)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "dockerfile_json_form_issue_limit_reached",
                Line = 0,
                Message = $"Dockerfile JSON-form diagnostics capped at {DockerfileJsonFormIssueLimit} issues",
                Severity = FileIssue.SeverityWarning,
            });
        }
    }

    private static bool TryAddDockerfileJsonFormIssue(
        List<FileIssue> issues,
        string relativePath,
        string instruction,
        string payload,
        int lineNumber,
        ref int emitted)
    {
        try
        {
            using var document = SymbolExtractor.ParseDockerfileJsonFormPayload(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return true;

            var count = 0;
            foreach (var _ in document.RootElement.EnumerateArray())
            {
                count++;
                if (count <= SymbolExtractor.DockerfileJsonFormMaxItems)
                    continue;

                if (!TryAddDockerfileJsonFormIssue(
                    issues,
                    relativePath,
                    "dockerfile_json_form_truncated",
                    lineNumber,
                    $"Dockerfile {instruction} JSON form has more than {SymbolExtractor.DockerfileJsonFormMaxItems} items; extraction is capped",
                    ref emitted))
                {
                    return false;
                }

                return true;
            }
        }
        catch (JsonException ex)
        {
            return TryAddDockerfileJsonFormIssue(
                issues,
                relativePath,
                "dockerfile_json_form_invalid",
                lineNumber,
                $"Dockerfile {instruction} JSON form is invalid: {LimitDockerfileJsonDiagnostic(ex.Message)}",
                ref emitted);
        }

        return true;
    }

    private static bool TryAddDockerfileJsonFormIssue(
        List<FileIssue> issues,
        string relativePath,
        string kind,
        int lineNumber,
        string message,
        ref int emitted)
    {
        if (emitted >= DockerfileJsonFormIssueLimit)
            return false;

        issues.Add(new FileIssue
        {
            Path = relativePath,
            Kind = kind,
            Line = lineNumber,
            Message = message,
            Severity = FileIssue.SeverityWarning,
        });
        emitted++;
        return true;
    }

    private static string LimitDockerfileJsonDiagnostic(string message)
    {
        const int limit = 180;
        return message.Length <= limit ? message : message[..limit] + "...";
    }

    private static bool TryGetDockerfileJsonFormPayload(string line, out string instruction, out string payload)
    {
        instruction = string.Empty;
        payload = string.Empty;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#')
            return false;

        if (TryConsumeDockerfileInstruction(trimmed, "ONBUILD", out var onbuildBody))
            trimmed = onbuildBody.TrimStart();

        foreach (var candidate in new[] { "VOLUME", "SHELL", "COPY", "ADD" })
        {
            if (!TryConsumeDockerfileInstruction(trimmed, candidate, out var body))
                continue;

            var jsonStart = candidate is "COPY" or "ADD"
                ? SkipDockerfileInstructionOptionsForDiagnostics(body)
                : SkipWhitespace(body, 0);
            if (jsonStart >= body.Length || body[jsonStart] != '[')
                return false;

            instruction = candidate;
            payload = body[jsonStart..].Trim();
            return true;
        }

        return false;
    }

    private static bool TryConsumeDockerfileInstruction(string text, string instruction, out string body)
    {
        body = string.Empty;
        if (!text.StartsWith(instruction, StringComparison.OrdinalIgnoreCase))
            return false;

        if (text.Length > instruction.Length && !char.IsWhiteSpace(text[instruction.Length]))
            return false;

        body = text.Length == instruction.Length ? string.Empty : text[instruction.Length..];
        return true;
    }

    private static int SkipDockerfileInstructionOptionsForDiagnostics(string body)
    {
        var index = 0;
        while (index < body.Length)
        {
            index = SkipWhitespace(body, index);
            if (index + 2 > body.Length || body[index] != '-' || body[index + 1] != '-')
                return index;

            index = ScanDockerfileInstructionTokenForDiagnostics(body, index);
        }

        return index;
    }

    private static int ScanDockerfileInstructionTokenForDiagnostics(string body, int index)
    {
        var quote = '\0';
        while (index < body.Length)
        {
            var c = body[index];
            if (quote != '\0')
            {
                if (c == '\\' && index + 1 < body.Length)
                {
                    index += 2;
                    continue;
                }

                if (c == quote)
                    quote = '\0';

                index++;
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                index++;
                continue;
            }

            if (char.IsWhiteSpace(c))
                break;

            index++;
        }

        return index;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

    private static void AddUtf16BomIssue(List<FileIssue> issues, string relativePath, bool utf16BigEndian)
    {
        issues.Add(new FileIssue
        {
            Path = relativePath,
            Kind = "utf16_bom",
            Line = 1,
            Message = utf16BigEndian
                ? "UTF-16 BE BOM detected (decoded as UTF-16)"
                : "UTF-16 LE BOM detected (decoded as UTF-16)",
        });
    }

    private static void AddUtf16HeuristicIssue(List<FileIssue> issues, string relativePath, bool utf16BigEndian)
    {
        issues.Add(new FileIssue
        {
            Path = relativePath,
            Kind = "utf16_heuristic",
            Line = 1,
            Message = utf16BigEndian
                ? "BOM-less UTF-16 BE detected by NUL-byte heuristic (decoded as UTF-16)"
                : "BOM-less UTF-16 LE detected by NUL-byte heuristic (decoded as UTF-16)",
        });
    }

    private static void AddReplacementCharacterIssues(
        List<FileIssue> issues,
        string relativePath,
        byte[] rawBytes,
        string content,
        bool isUtf16,
        bool utf16BigEndian,
        bool hasUtf16Bom)
    {
        // Aggregate signal: when a large fraction of the decoded content is U+FFFD, the file
        // most likely uses a non-UTF8 encoding without a BOM (SHIFT_JIS / GBK / ISO-8859-1).
        // Emit one `non_utf8_likely` issue and suppress the per-line `replacement_char`
        // emission below so a mangled mojibake file does not produce hundreds of near-duplicate
        // issues that drown the actual diagnostic. The minimum count of 5 avoids tripping on
        // tiny stub files that happen to contain a single bad byte. Closes #1540.
        // 集約シグナル: デコード後の content に U+FFFD が大量にあるファイルは BOM 無し
        // 非 UTF-8 (SHIFT_JIS / GBK / ISO-8859-1) の可能性が高い。`non_utf8_likely` 1 件
        // を出し下の `replacement_char` 行単位出力は抑止する。1% 閾値だけだと大ファイル
        // で数百件の重複が出てしまい本来の診断を埋もれさせるためアグリゲートで代替。
        // 最低 5 件しきい値で、たまたま 1 byte 壊れた小さなスタブを誤検出しないように。
        // Closes #1540.
        const double NonUtf8LikelyRatioThreshold = 0.01;
        const int NonUtf8LikelyMinCount = 5;
        var fffdCount = CountReplacementChars(content);
        var replacementCharOrigin = fffdCount > 0
            ? DetermineReplacementCharOrigin(rawBytes, isUtf16, utf16BigEndian, hasUtf16Bom)
            : null;
        var nonUtf8Likely = replacementCharOrigin == FileIssue.OriginDecodeReplacement
            && fffdCount >= NonUtf8LikelyMinCount
            && content.Length > 0
            && (double)fffdCount / content.Length >= NonUtf8LikelyRatioThreshold;
        if (nonUtf8Likely)
        {
            var ratioPercent = 100.0 * fffdCount / content.Length;
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "non_utf8_likely",
                Line = 0,
                Message = $"Likely non-UTF8 encoding ({fffdCount} U+FFFD over {content.Length} chars, {ratioPercent:F1}%); source may be SHIFT_JIS, GBK, ISO-8859-1, or UTF-16 without BOM",
                Origin = FileIssue.OriginDecodeReplacement,
                Severity = FileIssue.SeverityWarning,
            });
        }

        // U+FFFD replacement characters baked into the file / ファイルに焼き付いたU+FFFD置換文字
        // Skip the per-line emission when `non_utf8_likely` already fired so a mojibake file
        // does not produce hundreds of near-duplicate `replacement_char` issues.
        // `non_utf8_likely` が出た場合は重複を抑え 1 件のアグリゲートに集約する。
        if (nonUtf8Likely)
            return;

        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] != '\uFFFD')
                continue;

            // Find line number / 行番号を特定
            var lineNum = content[..i].Count(c => c == '\n') + 1;
            var isSourceLiteral = replacementCharOrigin == FileIssue.OriginSourceLiteral;
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "replacement_char",
                Line = lineNum,
                Message = isSourceLiteral
                    ? $"U+FFFD source literal at line {lineNum}"
                    : $"U+FFFD decoder replacement character at line {lineNum}",
                Origin = replacementCharOrigin,
                Severity = isSourceLiteral ? FileIssue.SeverityInfo : FileIssue.SeverityWarning,
            });
            // Skip to next line to avoid reporting every char on the same line
            // 同じ行の連続報告を避けるため次の行までスキップ
            var nextNewline = content.IndexOf('\n', i);
            if (nextNewline >= 0) i = nextNewline;
        }
    }

    private static void AddRawByteContentIssues(List<FileIssue> issues, string relativePath, byte[] rawBytes)
    {
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

        AddLineEndingIssues(issues, relativePath, rawBytes);
    }

    private static void AddLineEndingIssues(List<FileIssue> issues, string relativePath, byte[] rawBytes)
    {
        // Line-ending classification — check raw bytes before LF normalization so
        // bare CR (legacy Mac) and three-way mixes are not silently flattened by
        // the `\r\n` → `\n` then `\r` → `\n` pass in BuildRecordWithRawBytes.
        // 改行コードの判定 — LF 正規化前の rawBytes で確認。BuildRecordWithRawBytes が
        // `\r\n`→`\n`、`\r`→`\n` の順で潰してしまうため、生バイトで CR-only (旧 Mac)
        // と 3 種混在を見分ける。
        var hasCrlf = false;
        var hasLfOnly = false;
        var hasCrOnly = false;
        for (int i = 0; i < rawBytes.Length; i++)
        {
            if (rawBytes[i] == 0x0D)
            {
                if (i + 1 < rawBytes.Length && rawBytes[i + 1] == 0x0A)
                {
                    hasCrlf = true;
                    i++; // skip the LF after CR
                }
                else
                {
                    hasCrOnly = true;
                }
            }
            else if (rawBytes[i] == 0x0A)
            {
                hasLfOnly = true;
            }
        }
        var distinctEndingTypes = (hasCrlf ? 1 : 0) + (hasLfOnly ? 1 : 0) + (hasCrOnly ? 1 : 0);
        if (distinctEndingTypes >= 3)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "mixed_line_endings_three_way",
                Line = 0,
                Message = "Mixed line endings (CRLF, LF, and CR)",
            });
        }
        else if (distinctEndingTypes == 2)
        {
            string description;
            if (hasCrlf && hasLfOnly)
                description = "CRLF and LF";
            else if (hasCrlf && hasCrOnly)
                description = "CRLF and CR";
            else
                description = "LF and CR";
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "mixed_line_endings",
                Line = 0,
                Message = $"Mixed line endings ({description})",
            });
        }
        else if (hasCrOnly)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "cr_only_line_endings",
                Line = 0,
                Message = "CR-only line endings (legacy Mac)",
            });
        }
    }

    private static void AddOversizeContentIssues(List<FileIssue> issues, string relativePath, string content)
    {
        // line_too_long — surface the chunk/symbol/reference skip path that
        // triggers when a single physical line exceeds ChunkSplitter.MaxLineLength
        // (e.g. 1 MB minified `.min.js`, base64-encoded asset). The matching
        // guards in ChunkSplitter, SymbolExtractor, and ReferenceExtractor
        // already return empty for such files; this FileIssue lets callers
        // diagnose the silent stall the issue was filed for. Closes #1542.
        // line_too_long — 単一物理行が ChunkSplitter.MaxLineLength を超える
        // ファイル (例: 1 MB minified .min.js、base64 ペイロード) で発生する
        // chunk/symbol/reference スキップ経路を可視化する。ChunkSplitter /
        // SymbolExtractor / ReferenceExtractor 側の同等ガードはすでに空を返す
        // ため、本 FileIssue は issue 起票時の「無音停止」を切り分けやすくする
        // 観測点を提供する。Closes #1542.
        var longLine = FindOversizeLine(content, ChunkSplitter.MaxLineLength);
        if (longLine > 0)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "line_too_long",
                Line = longLine,
                Message = $"Line {longLine} exceeds {ChunkSplitter.MaxLineLength}-char cap; chunks/symbols/references skipped",
            });
        }

        var longFtsTokenLine = FindOversizeFtsTokenLine(content, CodeIndex.Database.DbReader.FtsUnicode61MaxTokenLength);
        if (longFtsTokenLine > 0)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "fts_token_too_long",
                Line = longFtsTokenLine,
                Message = $"Line {longFtsTokenLine} contains an FTS5 unicode61 token longer than {CodeIndex.Database.DbReader.FtsUnicode61MaxTokenLength} characters; that token is not searchable through FTS",
            });
        }
    }

    internal static bool ContainsIndexBlockingNullByte(byte[] rawBytes)
        => FileContentLoader.ContainsIndexBlockingNullByte(rawBytes);

    internal static bool TryDetectUtf16Encoding(
        byte[] rawBytes,
        bool allowHeuristic,
        out bool bigEndian,
        out bool hasBom)
        => FileContentLoader.TryDetectUtf16Encoding(rawBytes, allowHeuristic, out bigEndian, out hasBom);

    internal sealed class BinaryFileSkippedException(
        string relativePath,
        long nullByteOffset,
        string message) : InvalidOperationException(message)
    {
        public string RelativePath { get; } = relativePath;
        public long NullByteOffset { get; } = nullByteOffset;
    }

    internal sealed class FileTooLargeSkippedException(
        string relativePath,
        long actualBytes,
        long limitBytes,
        string message) : InvalidOperationException(message)
    {
        public string RelativePath { get; } = relativePath;
        public long ActualBytes { get; } = actualBytes;
        public long LimitBytes { get; } = limitBytes;
    }

    public static bool HasConflictMarkers(string content) =>
        TryGetConflictMarkerLine(content, out _);

    private static bool TryGetConflictMarkerLine(string content, out int line)
    {
        line = 0;
        if (string.IsNullOrEmpty(content))
            return false;

        var byteCount = 0;
        var lineStart = 0;
        var lineNumber = 1;
        for (int i = 0; i <= content.Length; i++)
        {
            if (i < content.Length)
            {
                byteCount += content[i] <= '\u007f' ? 1 : System.Text.Encoding.UTF8.GetByteCount(content.AsSpan(i, 1));
                if (byteCount > ConflictMarkerScanLimitBytes)
                    return false;
            }

            if (i < content.Length && content[i] != '\n')
                continue;

            var lineLength = i - lineStart;
            if (lineLength > 0 && content[lineStart + lineLength - 1] == '\r')
                lineLength--;
            var currentLine = content.AsSpan(lineStart, lineLength);
            if (currentLine.StartsWith("<<<<<<<", StringComparison.Ordinal)
                || currentLine.StartsWith(">>>>>>>", StringComparison.Ordinal))
            {
                line = lineNumber;
                return true;
            }

            lineStart = i + 1;
            lineNumber++;
        }

        return false;
    }

    /// <summary>
    /// Return the 1-based number of the first line whose length exceeds
    /// <paramref name="maxLineLength"/>, or 0 when none. Assumes `\n` is the
    /// only line separator (callers normalize CRLF). Used by ValidateContent
    /// to attach a precise line number to the `line_too_long` FileIssue.
    /// </summary>
    private static int FindOversizeLine(string content, int maxLineLength)
    {
        if (string.IsNullOrEmpty(content))
            return 0;
        int lineNumber = 1;
        int lineLen = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                lineNumber++;
                lineLen = 0;
                continue;
            }
            lineLen++;
            if (lineLen > maxLineLength)
                return lineNumber;
        }
        return 0;
    }

    private static int FindOversizeFtsTokenLine(string content, int maxTokenLength)
    {
        if (string.IsNullOrEmpty(content))
            return 0;

        var lineNumber = 1;
        var tokenLength = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                lineNumber++;
                tokenLength = 0;
                continue;
            }

            if (IsLikelyUnicode61TokenRune(rune))
            {
                tokenLength++;
                if (tokenLength > maxTokenLength)
                    return lineNumber;
            }
            else
            {
                tokenLength = 0;
            }
        }

        return 0;
    }

    private static bool IsLikelyUnicode61TokenRune(Rune rune)
        => rune.Value == '_'
            || Rune.IsLetter(rune)
            || Rune.IsDigit(rune)
            || Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark;

    /// <summary>
    /// Count U+FFFD replacement characters in decoded content.
    /// デコード済みcontent内のU+FFFD置換文字数を計上する。
    /// </summary>
    private static int CountReplacementChars(string content)
    {
        var count = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '�') count++;
        }
        return count;
    }

    private static string DetermineReplacementCharOrigin(byte[] rawBytes, bool isUtf16, bool utf16BigEndian, bool hasUtf16Bom)
    {
        try
        {
            if (isUtf16)
            {
                _ = new UnicodeEncoding(utf16BigEndian, byteOrderMark: hasUtf16Bom, throwOnInvalidBytes: true)
                    .GetString(rawBytes);
            }
            else
            {
                _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(rawBytes);
            }

            return FileIssue.OriginSourceLiteral;
        }
        catch (DecoderFallbackException)
        {
            return FileIssue.OriginDecodeReplacement;
        }
    }

    /// <summary>
    /// Compute SHA256 checksum from file bytes after collapsing CRLF / CR to LF.
    /// Matches the line-ending normalization that BuildRecord applies to the decoded
    /// content so cross-OS clones (Windows with core.autocrlf=true vs Linux/macOS) of the
    /// same logical file produce the same checksum, while BOM bytes pass through unchanged
    /// so BOM add / remove still triggers incremental re-index. Streams through
    /// IncrementalHash with a fixed buffer so large files do not require an extra full
    /// normalized-byte copy. Closes #1544.
    /// CRLF / CR を LF に潰してから SHA256 を算出する。BuildRecord がデコード後 content に
    /// 適用するのと同じ改行正規化を生バイト側でも行うので、Windows (core.autocrlf=true) と
    /// Linux/macOS で同じ論理内容を clone した場合に checksum が一致する。BOM はそのまま
    /// ハッシュ対象に残るので、BOM 追加 / 削除はインクリメンタル再索引で引き続き検知される。
    /// IncrementalHash に固定バッファで投入する streaming 実装なので、大ファイルでも
    /// 正規化後バイトのフルコピーを追加で確保しない。Closes #1544.
    /// </summary>
    internal static string ComputeChecksum(byte[] bytes)
        => FileContentLoader.ComputeChecksum(bytes);

    internal static bool TryComputeChecksum(string filePath, long maxBytes, out string checksum)
        => FileContentLoader.TryComputeChecksum(filePath, maxBytes, out checksum);

    /// <summary>
    /// Try to infer a language from an extensionless script shebang.
    /// This is a cheap fallback used only after extension and exact-filename checks fail.
    /// It reads at most <see cref="ShebangProbeByteLimit"/> bytes from the first line;
    /// NUL bytes and over-cap first lines are treated as non-scripts.
    /// 拡張子・完全一致ファイル名で判定できない場合だけ、拡張子なしスクリプトの shebang から言語を推定する。
    /// </summary>
    private static LanguageDetectionResult TryDetectLanguageFromShebang(
        string filePath,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot)
    {
        var indexability = GetFileIndexability(filePath, symlinkPolicy, projectRoot);
        if (indexability == FileProbeStatus.Missing)
            return new LanguageDetectionResult(FileProbeStatus.Missing, null);

        if (indexability != FileProbeStatus.Supported)
            return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

        try
        {
            using var stream = File.OpenRead(LongPath.EnsureWindowsPrefix(filePath));
            if (!stream.CanRead)
                return new LanguageDetectionResult(FileProbeStatus.ProbeFailed, null);

            Span<byte> buffer = stackalloc byte[ShebangProbeByteLimit];
            var bytesRead = stream.Read(buffer);
            if (bytesRead <= 0)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var bytes = buffer[..bytesRead];
            var shebangEncoding = DetectShebangEncoding(bytes);
            if (shebangEncoding == ShebangEncoding.Unsupported)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            if ((shebangEncoding == ShebangEncoding.Utf8 || shebangEncoding == ShebangEncoding.Utf8Bom)
                && bytes.Contains((byte)0))
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var preambleLength = GetShebangPreambleLength(shebangEncoding);
            var lineEnd = FindShebangLineEnd(bytes, shebangEncoding, preambleLength);
            if (lineEnd < 0)
            {
                if (bytesRead == ShebangProbeByteLimit)
                    return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);
                lineEnd = bytesRead;
            }

            var firstLineBytes = bytes[preambleLength..lineEnd];
            var firstLine = DecodeShebangLine(firstLineBytes, shebangEncoding);

            if (firstLine.StartsWith('\uFEFF'))
                firstLine = firstLine[1..];

            if (!firstLine.StartsWith("#!", StringComparison.Ordinal))
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var commandLine = firstLine[2..].Trim();
            if (string.IsNullOrWhiteSpace(commandLine))
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var tokens = TokenizeShebangCommandLine(commandLine);
            if (tokens.Count == 0)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var interpreter = ResolveShebangInterpreter(tokens);
            if (interpreter == null)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var language = MapShebangInterpreterToLanguage(interpreter);
            return language != null
                ? new LanguageDetectionResult(FileProbeStatus.Supported, language)
                : new LanguageDetectionResult(FileProbeStatus.Unsupported, null);
        }
        catch (FileNotFoundException)
        {
            return new LanguageDetectionResult(FileProbeStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new LanguageDetectionResult(FileProbeStatus.Missing, null);
        }
        catch (IOException)
        {
            return new LanguageDetectionResult(FileProbeStatus.ProbeFailed, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new LanguageDetectionResult(FileProbeStatus.ProbeFailed, null);
        }
        catch (DecoderFallbackException)
        {
            return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);
        }
    }

    private enum ShebangEncoding
    {
        Utf8,
        Utf8Bom,
        Utf16LittleEndian,
        Utf16BigEndian,
        Unsupported,
    }

    private static ShebangEncoding DetectShebangEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                return ShebangEncoding.Unsupported;
            if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                return ShebangEncoding.Unsupported;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return ShebangEncoding.Utf8Bom;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return ShebangEncoding.Utf16LittleEndian;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return ShebangEncoding.Utf16BigEndian;

        return ShebangEncoding.Utf8;
    }

    private static int GetShebangPreambleLength(ShebangEncoding encoding) => encoding switch
    {
        ShebangEncoding.Utf8Bom => 3,
        ShebangEncoding.Utf16LittleEndian or ShebangEncoding.Utf16BigEndian => 2,
        _ => 0,
    };

    private static int FindShebangLineEnd(ReadOnlySpan<byte> bytes, ShebangEncoding encoding, int start)
    {
        if (encoding is ShebangEncoding.Utf8 or ShebangEncoding.Utf8Bom)
            return bytes[start..].IndexOfAny((byte)'\r', (byte)'\n') is var lineEnd && lineEnd >= 0
                ? start + lineEnd
                : -1;

        for (var i = start; i + 1 < bytes.Length; i += 2)
        {
            var ch = encoding == ShebangEncoding.Utf16LittleEndian
                ? (bytes[i] | (bytes[i + 1] << 8))
                : ((bytes[i] << 8) | bytes[i + 1]);
            if (ch is '\r' or '\n')
                return i;
        }

        return -1;
    }

    private static string DecodeShebangLine(ReadOnlySpan<byte> bytes, ShebangEncoding encoding) => encoding switch
    {
        ShebangEncoding.Utf16LittleEndian => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true)
            .GetString(bytes),
        ShebangEncoding.Utf16BigEndian => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true)
            .GetString(bytes),
        _ => new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes),
    };

    private static IReadOnlyList<string> TokenizeShebangCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        var token = new StringBuilder(commandLine.Length);
        char? quote = null;
        var escaped = false;

        foreach (var ch in commandLine)
        {
            if (escaped)
            {
                token.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote is { } activeQuote)
            {
                if (ch == activeQuote)
                    quote = null;
                else
                    token.Append(ch);
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch is ' ' or '\t')
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }
                continue;
            }

            token.Append(ch);
        }

        if (escaped)
            token.Append('\\');
        if (token.Length > 0)
            tokens.Add(token.ToString());

        return tokens;
    }

    private static string? ResolveShebangInterpreter(IReadOnlyList<string> tokens)
    {
        var interpreter = NormalizeShebangInterpreterToken(tokens[0]);
        if (interpreter == null)
            return null;
        if (interpreter is not "env")
            return interpreter;

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == "--")
                continue;
            if (token.StartsWith("-", StringComparison.Ordinal))
                continue;

            // `env FOO=bar python` style assignments before the real interpreter.
            // `env FOO=bar python` のような代入はスキップして本体の interpreter を探す。
            if (token.Contains('='))
                continue;

            return NormalizeShebangInterpreterToken(token);
        }

        return null;
    }

    private static string? NormalizeShebangInterpreterToken(string token)
    {
        var candidate = token;
        if (token.IndexOfAny([' ', '\t']) >= 0)
        {
            var nestedTokens = TokenizeShebangCommandLine(token);
            if (nestedTokens.Count == 0)
                return null;
            candidate = nestedTokens[0];
        }

        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        return Path.GetFileName(candidate).ToLowerInvariant();
    }

    private static string? MapShebangInterpreterToLanguage(string interpreter) => interpreter switch
    {
        "bash" or "sh" or "zsh" or "fish" or "dash" or "ksh" or "ash" => "shell",
        "node" or "nodejs" => "javascript",
        "ruby" => "ruby",
        "php" => "php",
        "lua" => "lua",
        "pwsh" or "powershell" => "powershell",
        _ when interpreter.StartsWith("python", StringComparison.Ordinal) => "python",
        _ => null,
    };

    private string ToRelativePath(string absolutePath)
    {
        var relativePath = NormalizePathSeparators(Path.GetRelativePath(_projectRoot, absolutePath));
        return relativePath == "." ? string.Empty : relativePath;
    }

    private static class UnixFileStatus
    {
        internal const int FileTypeMask = 0xF000;
        internal const int RegularFile = 0x8000;

        internal static bool TryGetFileMode(string filePath, out int mode)
        {
            mode = 0;
            if (NativeMethods.Stat(filePath, out var status) != 0)
                return false;

            mode = status.Mode;
            return true;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FileStatus
        {
            internal FileStatusFlags Flags;
            internal int Mode;
            internal uint Uid;
            internal uint Gid;
            internal long Size;
            internal long ATime;
            internal long ATimeNsec;
            internal long MTime;
            internal long MTimeNsec;
            internal long CTime;
            internal long CTimeNsec;
            internal long BirthTime;
            internal long BirthTimeNsec;
            internal long Dev;
            internal long RDev;
            internal long Ino;
            internal uint UserFlags;
        }

        [System.Flags]
        private enum FileStatusFlags : uint
        {
            None = 0,
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
            internal static extern int Stat(string path, out FileStatus output);
        }
    }
}
