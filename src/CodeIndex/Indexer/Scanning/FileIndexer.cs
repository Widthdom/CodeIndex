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

        var directoryResult = EvaluatePathFilterDirectorySegments(
            relativePath,
            isDirectory,
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

        var projectRootFilterKind = GetDirectoryFilterKind(
            _projectRoot,
            string.Empty,
            activeIgnoreRules,
            isProjectRoot: true);
        return projectRootFilterKind != PathFilterKind.None
            ? new PathFilterResult(projectRootFilterKind, errors)
            : null;
    }

    private PathFilterResult? EvaluatePathFilterDirectorySegments(
        string relativePath,
        bool isDirectory,
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
        var directoryPathLength = isDirectory ? relativePath.Length : relativePath.LastIndexOf('/');
        if (directoryPathLength < 0)
            directoryPathLength = 0;

        var cumulativeRelPath = string.Empty;
        for (var segmentStart = 0; segmentStart < directoryPathLength;)
        {
            var slashIndex = relativePath.IndexOf('/', segmentStart, directoryPathLength - segmentStart);
            var segmentEnd = slashIndex >= 0 ? slashIndex : directoryPathLength;
            if (segmentEnd == segmentStart)
            {
                segmentStart++;
                continue;
            }

            var directoryName = relativePath.Substring(segmentStart, segmentEnd - segmentStart);
            var childDirectory = Path.Combine(currentDirectory, directoryName);
            cumulativeRelPath = cumulativeRelPath.Length == 0
                ? directoryName
                : string.Concat(cumulativeRelPath, "/", directoryName);
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

            segmentStart = segmentEnd + 1;
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
        var fileLanguages = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<ScanError>(_submoduleLoadWarnings.Count);
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
            fileLanguages,
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
            scanState.FileLanguages,
            scanState.Errors,
            MaterializePathSet(scanState.NonIndexablePaths),
            MaterializeSortedPathSet(scanState.UnknownExtensionFiles),
            MaterializePathSet(scanState.ProbeFailedFilePaths),
            MaterializePathSet(scanState.ListedDirectories),
            MaterializePathSet(scanState.FullyScannedDirectories),
            MaterializeCheckpointedDirectorySet(scanState.CheckpointedDirectories, scanState.FullyScannedDirectories),
            new List<string>(_ancestorIgnoreDirectories),
            MaterializePathSet(scanState.AttributePrunedDirectories),
            MaterializeSortedPathSet(scanState.NestedRepositories),
            MaterializeSortedPathSet(scanState.DanglingSymlinks));
    }

    private static List<string> MaterializePathSet(HashSet<string> paths) => paths.Count == 0 ? [] : new List<string>(paths);

    private static List<string> MaterializeSortedPathSet(HashSet<string> paths)
    {
        if (paths.Count == 0)
            return [];

        var sorted = new List<string>(paths);
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    private static HashSet<string> MaterializeCheckpointedDirectorySet(
        HashSet<string> checkpointedDirectories,
        HashSet<string> fullyScannedDirectories)
    {
        var result = new HashSet<string>(
            checkpointedDirectories.Count + fullyScannedDirectories.Count,
            StringComparer.Ordinal);
        result.UnionWith(checkpointedDirectories);
        result.UnionWith(fullyScannedDirectories);
        return result;
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

        var filterKind = GetDirectoryFilterKind(dir, relativeDir, activeIgnoreRules, isProjectRoot);
        if (filterKind != PathFilterKind.None)
        {
            scanState.ListedDirectories.Add(relativeDir);
            scanState.FullyScannedDirectories.Add(relativeDir);
            return true;
        }

        return EnumerateDirectory(dir, relativeDir, scanState, activeIgnoreRules, continueOnError, cancellationToken, depth);
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
        string relativeDir,
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
            var passthrough = IsSubmoduleAncestorPassthrough(relativeDir);
            var directoryIgnoreCase = DirectoryUsesIgnoreCase(dir);
            if (directoryIgnoreCase != _ignoreCase)
            {
                scanState.Errors.Add(new ScanError(
                    relativeDir,
                    "Filesystem case-sensitivity differs from the project root; deduplicating file paths for this directory.",
                    ScanIssueSeverity.Warning));
            }

            if (_enumerateFilesForTesting is null)
            {
                fullyScanned &= EnumerateDirectoryEntries(
                    dir,
                    relativeDir,
                    scanState,
                    activeIgnoreRules,
                    passthrough,
                    directoryIgnoreCase,
                    continueOnError,
                    cancellationToken,
                    depth);
            }
            else
            {
                if (!passthrough)
                    EnumerateIndexableFilesInDirectory(dir, scanState, activeIgnoreRules, directoryIgnoreCase, cancellationToken);

                // A successful file listing proves the direct children of this directory.
                // Child subtree failures must not revoke that authority for sibling-file purge.
                // ファイル列挙が成功した時点で、このディレクトリ直下の子要素については authoritative とみなせる。
                // 子サブツリー失敗が sibling file purge の authority を奪ってはいけない。
                scanState.ListedDirectories.Add(relativeDir);
                RecordDanglingFileSystemEntries(dir, scanState, cancellationToken);
                fullyScanned &= EnumerateSubdirectories(dir, scanState, activeIgnoreRules, passthrough, continueOnError, cancellationToken, depth);
            }
        }
        catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
        {
            scanState.Errors.Add(new ScanError(
                relativeDir,
                $"Could not scan directory due to {FileSystemTraversalFailure.DescribeReason(ex)}."));
            fullyScanned = false;
        }

        if (fullyScanned)
            scanState.FullyScannedDirectories.Add(relativeDir);

        return fullyScanned;
    }

    private bool EnumerateDirectoryEntries(
        string dir,
        string relativeDir,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool passthrough,
        bool directoryIgnoreCase,
        bool continueOnError,
        CancellationToken cancellationToken,
        int depth)
    {
        var seenFilePaths = !passthrough && directoryIgnoreCase
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;
        var subdirectories = new List<string>();
        var danglingCandidateLimit = _maxDanglingFileSystemEntryScanCandidates;
        var danglingCandidateCount = 0;
        var danglingScanTruncated = false;

        foreach (var enumeratedEntry in _enumerateFileSystemEntries(dir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = LongPath.RemoveWindowsPrefix(enumeratedEntry);
            CountDanglingCandidate(relativeDir, scanState, danglingCandidateLimit, ref danglingCandidateCount, ref danglingScanTruncated);

            var probeStatus = FileSystemBoundary.TryGetAttributes(entry, out var attributes);
            if (probeStatus != FileSystemBoundaryProbeStatus.Found)
                continue;

            if (FileSystemBoundary.IsSymlinkOrReparsePoint(attributes) && !ReparsePointTargetExists(entry))
            {
                RecordDanglingFileSystemEntry(entry, scanState);
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0 || Directory.Exists(LongPath.EnsureWindowsPrefix(entry)))
            {
                subdirectories.Add(entry);
                continue;
            }

            if (passthrough)
                continue;

            if (TryAcceptScannedFile(entry, scanState, activeIgnoreRules, seenFilePaths, attributes))
                scanState.Results.Add(entry);
        }

        // A successful immediate-child listing proves this directory for sibling-file purge.
        // Child recursion happens after that authority has been captured.
        scanState.ListedDirectories.Add(relativeDir);
        return ProcessSubdirectories(
            subdirectories,
            scanState,
            activeIgnoreRules,
            passthrough,
            continueOnError,
            cancellationToken,
            depth);
    }

    private static void CountDanglingCandidate(
        string relativeDir,
        DirectoryScanState scanState,
        int candidateLimit,
        ref int candidateCount,
        ref bool scanTruncated)
    {
        if (scanTruncated)
            return;

        candidateCount++;
        if (candidateCount <= candidateLimit)
            return;

        scanState.Errors.Add(new ScanError(
            relativeDir,
            $"Dangling filesystem entry scan truncated after {candidateLimit:N0} candidate(s); additional dangling symlink diagnostics in this directory may be omitted.",
            ScanIssueSeverity.Warning));
        scanTruncated = true;
    }

    private void EnumerateIndexableFilesInDirectory(
        string dir,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool directoryIgnoreCase,
        CancellationToken cancellationToken)
    {
        var enumerateFiles = _enumerateFilesForTesting ?? throw new InvalidOperationException("Test file enumeration is not configured.");
        var seenFilePaths = directoryIgnoreCase
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var enumeratedFile in enumerateFiles(dir))
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
        HashSet<string>? seenFilePaths,
        FileAttributes? knownAttributes = null)
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

        var knownIndexability = knownAttributes.HasValue
            ? GetFileIndexabilityForFoundAttributes(file, knownAttributes.Value, _symlinkPolicy, _projectRoot)
            : (FileProbeStatus?)null;
        return TryAcceptSupportedScannedFile(file, scanState, knownIndexability);
    }

    private bool TryAcceptSupportedScannedFile(
        string file,
        DirectoryScanState scanState,
        FileProbeStatus? knownIndexability = null)
    {
        // Use the instance symlink policy here so full scans and update paths apply the same
        // file-link behavior.
        // full scan と update 経路で同じ file-link 挙動になるよう instance の symlink policy を使う。
        var indexability = knownIndexability ?? GetFileIndexabilityForIndexing(file);
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
        var language = TryDetectLanguageForIndexing(file, knownIndexability: indexability);
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

        if (language.Language is { Length: > 0 } acceptedLanguage)
            scanState.FileLanguages[file] = acceptedLanguage;
        return true;
    }

    private void RecordDanglingFileSystemEntries(
        string dir,
        DirectoryScanState scanState,
        CancellationToken cancellationToken)
    {
        var candidateLimit = _maxDanglingFileSystemEntryScanCandidates;
        var candidateCount = 0;
        foreach (var enumeratedEntry in CodeIndex.FileSystemTraversalPolicy.EnumerateFileSystemEntries(LongPath.EnsureWindowsPrefix(dir)))
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

    private void RecordDanglingFileSystemEntry(string entry, DirectoryScanState scanState)
    {
        var relativeEntry = ToRelativePath(entry);
        scanState.DanglingSymlinks.Add(relativeEntry);
        scanState.Errors.Add(new ScanError(relativeEntry, "Skipped dangling symlink because its target could not be resolved.", ScanIssueSeverity.Warning));
        scanState.ListedDirectories.Add(relativeEntry);
        scanState.FullyScannedDirectories.Add(relativeEntry);
        scanState.AttributePrunedDirectories.Add(relativeEntry);
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
        var subdirectories = RemoveWindowsPrefixes(
            CodeIndex.FileSystemTraversalPolicy.EnumerateDirectories(LongPath.EnsureWindowsPrefix(dir)));
        return ProcessSubdirectories(
            subdirectories,
            scanState,
            activeIgnoreRules,
            passthrough,
            continueOnError,
            cancellationToken,
            depth);
    }

    private static IEnumerable<string> RemoveWindowsPrefixes(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            yield return LongPath.RemoveWindowsPrefix(path);
    }

    private bool ProcessSubdirectories(
        IEnumerable<string> subdirectories,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool passthrough,
        bool continueOnError,
        CancellationToken cancellationToken,
        int depth)
    {
        var fullyScanned = true;
        foreach (var subDir in subdirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        string? subRelative = null;
        if (IsNestedGitRepository(subDir))
        {
            subRelative = ToRelativePath(subDir);
            if (!IsSubmoduleOrAncestor(subRelative))
            {
                scanState.ListedDirectories.Add(subRelative);
                scanState.FullyScannedDirectories.Add(subRelative);
                scanState.NestedRepositories.Add(subRelative);
                return true;
            }
        }

        // In passthrough mode, only descend into subdirectories that are themselves
        // submodules or submodule ancestors. Treat siblings the same way SkipDirs
        // would have treated them at this point.
        // passthrough 中は、submodule 自体または submodule の祖先に該当する
        // サブディレクトリのみ降りる。その他は本来 SkipDirs で止まっていた扱いに戻す。
        if (passthrough)
        {
            subRelative ??= ToRelativePath(subDir);
            if (!IsSubmoduleOrAncestor(subRelative))
            {
                scanState.ListedDirectories.Add(subRelative);
                scanState.FullyScannedDirectories.Add(subRelative);
                return true;
            }
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

    private PathFilterKind GetDirectoryFilterKind(
        string dir,
        string relativeDir,
        IgnoreRuleSet activeIgnoreRules,
        bool isProjectRoot = false)
    {
        if (!isProjectRoot)
        {
            var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
            if (SkipDirs.Contains(dirName) && !IsSubmoduleOrAncestor(relativeDir))
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
    private bool IsSubmoduleOrAncestor(string relativePath)
    {
        if (_submodulePaths.Count == 0)
            return false;
        if (relativePath.Length == 0)
            return false;
        return _submodulePaths.Contains(relativePath) || _submoduleAncestorPaths.Contains(relativePath);
    }

    private bool IsSubmoduleAncestorPassthrough(string relativePath)
    {
        if (_submoduleAncestorPaths.Count == 0)
            return false;
        if (relativePath.Length == 0)
            return false;
        if (_submodulePaths.Contains(relativePath))
            return false;
        if (!_submoduleAncestorPaths.Contains(relativePath))
            return false;
        // Passthrough propagates from any SkipDirs-named ancestor along the path. If no
        // segment of relativePath matches SkipDirs, this directory would have been walked
        // normally without our override, so the override is not in effect here.
        // SkipDirs 名の祖先からは下方向に passthrough を伝播する。relativePath のどの segment も
        // SkipDirs に該当しない場合、我々の上書き無しでも walker は通っていたはずなので
        // ここでの上書きは効いていない。
        var remaining = relativePath.AsSpan();
        while (!remaining.IsEmpty)
        {
            var separatorIndex = remaining.IndexOf('/');
            var segment = separatorIndex >= 0 ? remaining[..separatorIndex] : remaining;
            if (!segment.IsEmpty && IsDefaultExcludedDirectoryName(segment))
                return true;
            if (separatorIndex < 0)
                break;
            remaining = remaining[(separatorIndex + 1)..];
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

                if (readFailure.ExceptionType == nameof(UnauthorizedAccessException))
                {
                    if (!File.Exists(prefixedIgnorePath))
                        throw new UnauthorizedAccessException(readFailure.Reason);

                    errors.Add(new ScanError(ToRelativePath(ignorePath), $"Could not read {ignoreFileName} due to permissions.", ScanIssueSeverity.Warning));
                    return true;
                }

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

        var directories = new List<string>();
        var root = Path.GetFullPath(ignoreRuleRoot);
        var current = Directory.GetParent(Path.GetFullPath(projectRoot));
        while (current != null)
        {
            directories.Add(current.FullName);
            if (PathsEqual(current.FullName, root))
            {
                directories.Reverse();
                return directories;
            }

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
            _ = CodeIndex.FileSystemTraversalPolicy.HasAnyFileSystemEntry(LongPath.EnsureWindowsPrefix(dir));
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

    private string ToRelativePath(string absolutePath)
    {
        var relativePath = NormalizePathSeparators(Path.GetRelativePath(_projectRoot, absolutePath));
        return relativePath == "." ? string.Empty : relativePath;
    }

}
