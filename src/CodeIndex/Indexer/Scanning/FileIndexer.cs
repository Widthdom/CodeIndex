using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Scans directories for source files and builds FileRecords.
/// ディレクトリを走査してソースファイルからFileRecordを構築する。
/// </summary>
public partial class FileIndexer
{
    internal const int MaxDanglingFileSystemEntryScanCandidates = 4096;
    internal static Func<string, bool>? FileSystemIgnoreCaseProbeForTesting { get; set; }
    internal static Func<string, FileSystemInfo?>? ResolveDirectoryLinkTargetForTesting { get; set; }

    private static readonly string[] HotspotFamilyMarkerLanguages = ["csharp", "vb", "fsharp", "msbuild"];
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
        [".config"] = "xml",
        [".runsettings"] = "xml",
        [".rules"] = "config",
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
    private readonly Func<string, IEnumerable<string>>? _enumerateFilesForTesting;
    private readonly Func<string, IEnumerable<string>> _enumerateFileSystemEntries;
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

    private sealed record DirectoryScanState(
        List<string> Results,
        Dictionary<string, string> FileLanguages,
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
        Func<string, IEnumerable<string>>? enumerateFileSystemEntries = null,
        SymlinkPolicy symlinkPolicy = SymlinkPolicy.None,
        int? maxDanglingFileSystemEntryScanCandidates = null,
        IReadOnlyList<string>? generatedCodePatterns = null)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _ignoreRuleRoot = NormalizeIgnoreRuleRoot(ignoreRuleRoot);
        _ancestorIgnoreDirectories = BuildAncestorIgnoreDirectories(_ignoreRuleRoot, _projectRoot);
        _ignoreCase = ignoreCase;
        _directoryIgnoreCaseProbe = directoryIgnoreCaseProbe ?? ProbeExistingDirectoryIgnoreCase;
        _enumerateFilesForTesting = enumerateFiles;
        _enumerateFileSystemEntries = enumerateFileSystemEntries ?? (dir => CodeIndex.FileSystemTraversalPolicy.EnumerateFileSystemEntries(LongPath.EnsureWindowsPrefix(dir)));
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

    internal LanguageDetectionResult TryDetectLanguageForIndexing(
        string filePath,
        string? content = null,
        FileProbeStatus? knownIndexability = null)
        => TryDetectLanguage(filePath, content, _symlinkPolicy, _projectRoot, knownIndexability);

    internal static string? GetReusableDetectedLanguage(
        string filePath,
        IReadOnlyDictionary<string, string>? detectedLanguages)
    {
        if (detectedLanguages == null || !detectedLanguages.TryGetValue(filePath, out var language))
            return null;

        return CanReuseDetectedLanguageWithoutContent(filePath, language)
            ? language
            : null;
    }

    internal static bool CanReuseDetectedLanguageWithoutContent(string filePath, string? language)
    {
        if (string.IsNullOrEmpty(language))
            return false;

        var fileName = Path.GetFileName(filePath);
        if (FileNameMap.TryGetValue(fileName, out var nameLanguage))
            return string.Equals(language, nameLanguage, StringComparison.Ordinal);

        foreach (var (prefix, prefixLanguage) in FileNamePrefixMap)
        {
            if (fileName.Length > prefix.Length &&
                fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(language, prefixLanguage, StringComparison.Ordinal);
            }
        }

        if (TryDetectLanguageOverride(filePath, fileName, out var overrideLanguage))
            return string.Equals(language, overrideLanguage, StringComparison.Ordinal);

        var extension = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(extension)
            && !string.Equals(extension, ".h", StringComparison.OrdinalIgnoreCase);
    }

    internal static LanguageDetectionResult TryDetectLanguage(string filePath, string? content = null)
        => TryDetectLanguage(filePath, content, SymlinkPolicy.None, projectRoot: null, knownIndexability: null);

    internal static LanguageDetectionResult TryDetectLanguage(
        string filePath,
        string? content,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot,
        FileProbeStatus? knownIndexability = null)
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
        if (TryDetectLanguageOverride(filePath, fileName, out var overrideLang))
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

        return TryDetectLanguageFromShebang(filePath, symlinkPolicy, projectRoot, knownIndexability);
    }

    private static bool TryDetectLanguageOverride(string filePath, string fileName, out string language)
    {
        language = string.Empty;
        var overrides = LanguageMapOverrides.LoadEffectiveMap(filePath);
        if (overrides.Count == 0)
            return false;

        for (var dotIndex = fileName.IndexOf('.'); dotIndex >= 0; dotIndex = fileName.IndexOf('.', dotIndex + 1))
        {
            if (overrides.TryGetValue(fileName[dotIndex..], out var mappedLanguage))
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

        var path = filePath.AsSpan();
        if (StartsWithWindowsDeviceNamespace(path))
        {
            return true;
        }

        for (var start = 0; start < path.Length;)
        {
            while (start < path.Length && IsWindowsPathSeparator(path[start]))
                start++;
            if (start >= path.Length)
                break;

            var end = start;
            while (end < path.Length && !IsWindowsPathSeparator(path[end]))
                end++;

            var name = path[start..end];
            var extensionIndex = name.IndexOf('.');
            if (extensionIndex >= 0)
                name = name[..extensionIndex];

            if (IsWindowsReservedDeviceName(name))
                return true;

            start = end + 1;
        }

        return false;
    }

    private static bool StartsWithWindowsDeviceNamespace(ReadOnlySpan<char> path)
    {
        if (path.Length >= 4
            && IsWindowsPathSeparator(path[0])
            && IsWindowsPathSeparator(path[1])
            && path[2] == '.'
            && IsWindowsPathSeparator(path[3]))
        {
            return true;
        }

        if (path.Length < 22
            || !IsWindowsPathSeparator(path[0])
            || !IsWindowsPathSeparator(path[1])
            || path[2] != '?'
            || !IsWindowsPathSeparator(path[3]))
        {
            return false;
        }

        var remaining = path[4..];
        if (!remaining.StartsWith("GLOBALROOT".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        remaining = remaining["GLOBALROOT".Length..];
        if (remaining.IsEmpty || !IsWindowsPathSeparator(remaining[0]))
            return false;

        remaining = remaining[1..];
        if (!remaining.StartsWith("Device".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        remaining = remaining["Device".Length..];
        return !remaining.IsEmpty && IsWindowsPathSeparator(remaining[0]);
    }

    private static bool IsWindowsPathSeparator(char value)
        => value is '\\' or '/';

    private static bool IsWindowsReservedDeviceName(ReadOnlySpan<char> name)
    {
        if (name.Equals("CON".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4
            && (name.StartsWith("COM".AsSpan(), StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("LPT".AsSpan(), StringComparison.OrdinalIgnoreCase))
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
        return FileSystemBoundary.TryGetAttributes(path, out var attributes) switch
        {
            FileSystemBoundaryProbeStatus.Found => HasSkippedAttributes(attributes),
            FileSystemBoundaryProbeStatus.Missing => true,
            _ => false,
        };
    }

    private static bool IsReparsePoint(string path)
    {
        return FileSystemBoundary.TryGetAttributes(path, out var attributes) == FileSystemBoundaryProbeStatus.Found
            && FileSystemBoundary.IsSymlinkOrReparsePoint(attributes);
    }

    private static FileProbeStatus ToFileProbeStatus(FileSystemBoundaryProbeStatus status)
        => status switch
        {
            FileSystemBoundaryProbeStatus.Missing => FileProbeStatus.Missing,
            FileSystemBoundaryProbeStatus.PermissionDenied or FileSystemBoundaryProbeStatus.IoError => OperatingSystem.IsWindows()
                ? FileProbeStatus.Supported
                : FileProbeStatus.ProbeFailed,
            _ => FileProbeStatus.ProbeFailed,
        };

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
        var probeStatus = FileSystemBoundary.TryGetAttributes(filePath, out var attributes);
        if (probeStatus != FileSystemBoundaryProbeStatus.Found)
            return ToFileProbeStatus(probeStatus);

        return GetFileIndexabilityForFoundAttributes(filePath, attributes, symlinkPolicy, projectRoot);
    }

    private static FileProbeStatus GetFileIndexabilityForFoundAttributes(
        string filePath,
        FileAttributes attributes,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot)
    {
        if (FileSystemBoundary.IsSymlinkOrReparsePoint(attributes))
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
            foreach (var file in CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(prefixedDir, pattern))
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
        pendingDirectories.Push(new ProjectMarkerFingerprintDirectory(
            dir,
            ToRelativePath(dir),
            inheritedIgnoreRules,
            IsProjectRoot: true));
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pendingDirectories.Pop();
            if (GetDirectoryFilterKind(current.Path, current.RelativePath, current.IgnoreRules, current.IsProjectRoot) != PathFilterKind.None)
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

                var passthrough = IsSubmoduleAncestorPassthrough(current.RelativePath);
                foreach (var enumeratedSubDir in EnumerateProjectMarkerDirectories(currentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var subDir = LongPath.RemoveWindowsPrefix(enumeratedSubDir);
                    if (HasSkippedAttributes(subDir))
                        continue;
                    var subRelativePath = ToRelativePath(subDir);
                    if (IsNestedGitRepository(subDir) && !IsSubmoduleOrAncestor(subRelativePath))
                        continue;
                    if (passthrough && !IsSubmoduleOrAncestor(subRelativePath))
                        continue;
                    if (GetDirectoryFilterKind(subDir, subRelativePath, activeIgnoreRules) != PathFilterKind.None)
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

                    pendingDirectories.Push(new ProjectMarkerFingerprintDirectory(
                        subDir,
                        subRelativePath,
                        activeIgnoreRules,
                        IsProjectRoot: false));
                }
            }
            catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
            {
                var exceptionType = FileSystemTraversalFailure.ExceptionTypeName(ex);
                AddProjectMarkerTraversalWarning(errors, currentDirectory, exceptionType);
                MarkProjectMarkerTraversalTruncated(
                    traversalState,
                    $"traversal failed with {exceptionType}");
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
            : CodeIndex.FileSystemTraversalPolicy.EnumerateDirectories(LongPath.EnsureWindowsPrefix(dir));

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

    private string ToRelativePath(string absolutePath)
    {
        var relativePath = NormalizePathSeparators(Path.GetRelativePath(_projectRoot, absolutePath));
        return relativePath == "." ? string.Empty : relativePath;
    }

}
