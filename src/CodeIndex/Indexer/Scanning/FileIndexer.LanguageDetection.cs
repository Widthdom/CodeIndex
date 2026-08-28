using CodeIndex.Cli;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
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
        [".m"] = "ambiguous_m",
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
        [".jsonl"] = "jsonl",
        [".ndjson"] = "jsonl",
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
        [".pl"] = "ambiguous_pl",
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
    private static readonly HashSet<string> ShebangAmbiguousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m",
        ".pl",
        ".t",
    };
    private static readonly string[] ContentDetectedLanguageBuckets =
    [
        "matlab",
        "prolog",
    ];
    private static readonly IReadOnlyDictionary<string, string> EmptyLanguageMapOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal enum LanguagePatternKind
    {
        Extension,
        ExactFilename,
        FilenamePrefixPattern,
    }

    internal sealed record LanguagePattern(
        string Pattern,
        string Language,
        LanguagePatternKind Kind,
        string Source);

    /// <summary>
    /// Return all file patterns (extensions and filenames) mapped to their language names.
    /// 全ファイルパターン（拡張子とファイル名）と対応する言語名のマッピングを返す。
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetLanguageExtensions()
        => GetLanguageExtensions(workspaceRoot: null, out _);

    public static IReadOnlyCollection<string> GetDetectedLanguageNames()
        => GetDetectedLanguageNames(workspaceRoot: null);

    internal static IReadOnlyCollection<string> GetDetectedLanguageNames(string? workspaceRoot)
        => GetLanguageExtensions(workspaceRoot).Values
            .Concat(ContentDetectedLanguageBuckets)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(language => language, StringComparer.Ordinal)
            .ToArray();

    internal static IReadOnlyList<string> GetContentDetectedLanguageBuckets()
        => ContentDetectedLanguageBuckets;

    internal static IReadOnlyDictionary<string, string> GetLanguageExtensions(string? workspaceRoot)
        => GetLanguageExtensions(workspaceRoot, out _);

    internal static IReadOnlyDictionary<string, string> GetLanguageExtensions(
        string? workspaceRoot,
        out IReadOnlyList<LanguageMapOverrides.Diagnostic> languageMapDiagnostics)
    {
        var patterns = GetLanguagePatterns(workspaceRoot, out languageMapDiagnostics);
        var merged = new Dictionary<string, string>(patterns.Count, StringComparer.Ordinal);
        foreach (var pattern in patterns)
            merged.TryAdd(pattern.Pattern, pattern.Language);
        return merged;
    }

    internal static IReadOnlyList<LanguagePattern> GetLanguagePatterns(
        string? workspaceRoot,
        out IReadOnlyList<LanguageMapOverrides.Diagnostic> languageMapDiagnostics)
    {
        var pluginExtensions = ExtractorPluginRegistry.GetLanguageExtensions(workspaceRoot);
        var languageMapOverrideResult = LanguageMapOverrides.LoadEffectiveMapWithDiagnostics(workspaceRoot);
        var languageMapOverrides = languageMapOverrideResult.Map;
        languageMapDiagnostics = languageMapOverrideResult.Diagnostics;
        var capacity = LangMap.Count
            + DisplayOnlyLanguageExtensions.Length
            + FileNameMap.Count
            + RepositoryRelativePathMap.Length
            + FileNamePrefixMap.Length
            + pluginExtensions.Count
            + languageMapOverrides.Count;

        var patterns = new List<LanguagePattern>(capacity);
        var patternKeys = new HashSet<(LanguagePatternKind Kind, string Pattern)>();
        var extensionIndexes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        void TryAddPattern(string pattern, string language, LanguagePatternKind kind, string source)
        {
            if (!patternKeys.Add((kind, pattern)))
                return;

            var index = patterns.Count;
            patterns.Add(new LanguagePattern(pattern, language, kind, source));
            if (kind != LanguagePatternKind.Extension)
                return;
            if (!extensionIndexes.TryGetValue(pattern, out var indexes))
            {
                indexes = [];
                extensionIndexes[pattern] = indexes;
            }
            indexes.Add(index);
        }

        foreach (var (pattern, lang) in LangMap)
            TryAddPattern(pattern, lang, LanguagePatternKind.Extension, "built_in");
        // Keep display-only case variants that collapse in the case-insensitive detection map.
        // case-insensitive な検出マップでは潰れる表示用 case variant を保持する。
        foreach (var (pattern, lang) in DisplayOnlyLanguageExtensions)
            TryAddPattern(pattern, lang, LanguagePatternKind.Extension, "built_in");
        foreach (var (name, lang) in FileNameMap)
            TryAddPattern(name, lang, LanguagePatternKind.ExactFilename, "built_in");
        foreach (var (path, lang) in RepositoryRelativePathMap)
            TryAddPattern(path, lang, LanguagePatternKind.ExactFilename, "built_in");
        // Surface suffixed variants like Dockerfile.dev / Makefile.am as `<Prefix><suffix>` entries
        // so `cdidx languages` and the MCP listing reflect what TryDetectLanguage actually handles.
        // Dockerfile.dev / Makefile.am のようなサフィックス付き変種も `<Prefix><suffix>` 形で
        // 露出させ、`cdidx languages` や MCP の一覧が TryDetectLanguage の実挙動と一致するようにする。
        foreach (var (prefix, lang) in FileNamePrefixMap)
            TryAddPattern($"{prefix}<suffix>", lang, LanguagePatternKind.FilenamePrefixPattern, "built_in");
        foreach (var (extension, lang) in pluginExtensions)
            TryAddPattern(extension, lang, LanguagePatternKind.Extension, "plugin_or_pattern");
        foreach (var (extension, lang) in languageMapOverrides)
        {
            if (!extensionIndexes.TryGetValue(extension, out var indexes))
            {
                TryAddPattern(extension, lang, LanguagePatternKind.Extension, "language_map_override");
                continue;
            }

            foreach (var index in indexes)
                patterns[index] = patterns[index] with { Language = lang, Source = "language_map_override" };
        }

        // Runtime detection applies suffix overrides before exact filename lookup. Reassign
        // exact filenames here as well so advertised capabilities match that precedence.
        // runtime detection は exact filename lookup より先に suffix override を適用するため、
        // 公開 capability でも完全一致 filename を同じ優先順位で再割り当てする。
        for (var index = 0; index < patterns.Count; index++)
        {
            var pattern = patterns[index];
            if (pattern.Kind == LanguagePatternKind.ExactFilename
                && TryGetLanguageMapOverrideForFileName(pattern.Pattern, languageMapOverrides, out var mappedLanguage))
            {
                patterns[index] = pattern with
                {
                    Language = mappedLanguage,
                    Source = "language_map_override",
                };
            }
        }
        return patterns;
    }

    public static string? DetectLanguage(string filePath)
        => TryDetectLanguage(filePath).Language;

    internal static bool IsIgnoreFilePath(string path)
    {
        var fileName = Path.GetFileName(path.AsSpan());
        return fileName.Equals(".gitignore".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".cdidxignore".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    internal LanguageDetectionResult TryDetectLanguageForIndexing(
        string filePath,
        string? content = null,
        FileProbeStatus? knownIndexability = null,
        bool deferUnknownScriptHeader = false)
        => TryDetectLanguage(
            filePath,
            content,
            _symlinkPolicy,
            _projectRoot,
            knownIndexability,
            LoadLanguageMapOverridesForIndexing,
            openReadForIndexContent: _openReadForIndexContent,
            allowPatternConfigDiscovery: !_bindConfigurationReadsToFileSystemIdentity,
            fileNameIgnoreCase: DirectoryUsesIgnoreCase(Path.GetDirectoryName(filePath) ?? _projectRoot),
            enumerateFileSystemEntries: _enumerateFileSystemEntries,
            openPatternConfig: _bindConfigurationReadsToFileSystemIdentity
                ? OpenObservedPatternConfigurationFileForRead
                : null,
            patternConfigDirectoryExists: _suppressConfigurationInputObservation
                ? null
                : ObservePatternConfigurationDirectoryExists,
            patternConfigInputObserver: ObservePatternConfigurationInput,
            deferUnknownScriptHeader: deferUnknownScriptHeader,
            repositoryRoot: _ignoreRuleRoot);

    private IReadOnlyDictionary<string, string> LoadLanguageMapOverridesForIndexing(string? startDirectory)
    {
        startDirectory = LanguageMapOverrides.NormalizeStartDirectory(startDirectory);
        var lastLookup = _lastLanguageMapOverrideLookup;
        if (lastLookup != null && string.Equals(lastLookup.StartDirectory, startDirectory, StringComparison.Ordinal))
            return lastLookup.Overrides;

        lock (_languageMapOverrideCache)
        {
            if (_languageMapOverrideCache.TryGetValue(startDirectory, out var cached))
                return CacheLastLanguageMapOverrideLookup(startDirectory, cached);

            if (TryReuseParentLanguageMapOverrides(startDirectory, out cached))
                return CacheLastLanguageMapOverrideLookup(startDirectory, cached);

            var loaded = _bindConfigurationReadsToFileSystemIdentity
                ? LanguageMapOverrides.LoadEffectiveMapFromDirectoryWithinScope(
                    startDirectory,
                    _projectRoot,
                    OpenObservedConfigurationFileForRead,
                    RecordLanguageMapConfigurationProbe)
                : LanguageMapOverrides.LoadEffectiveMapFromDirectoryForIndexing(
                    startDirectory,
                    OpenObservedConfigurationFileForRead,
                    RecordLanguageMapConfigurationProbe);
            _languageMapOverrideCache[startDirectory] = loaded;
            return CacheLastLanguageMapOverrideLookup(startDirectory, loaded);
        }
    }

    private void RecordLanguageMapConfigurationProbe(
        string path,
        LanguageMapOverrides.ConfigProbeStatus status,
        bool isUserConfiguration)
    {
        if (status == LanguageMapOverrides.ConfigProbeStatus.ProbeFailed)
        {
            MarkConfigurationInputSnapshotsIncomplete(path);
            return;
        }

        if (status == LanguageMapOverrides.ConfigProbeStatus.Missing
            && (isUserConfiguration
                || !PathCasing.IsFullPathEqualOrParent(_projectRoot, Path.GetFullPath(path))))
        {
            RecordConfigurationFileProbe(path);
        }
    }

    private IReadOnlyDictionary<string, string> CacheLastLanguageMapOverrideLookup(
        string startDirectory,
        IReadOnlyDictionary<string, string> overrides)
    {
        _lastLanguageMapOverrideLookup = new LanguageMapOverrideLookupCache(startDirectory, overrides);
        return overrides;
    }

    private bool TryReuseParentLanguageMapOverrides(
        string startDirectory,
        out IReadOnlyDictionary<string, string> overrides)
    {
        overrides = EmptyLanguageMapOverrides;
        var parent = Directory.GetParent(startDirectory)?.FullName;
        if (string.IsNullOrEmpty(parent)
            || !_languageMapOverrideCache.TryGetValue(parent, out var parentOverrides)
            || LanguageMapOverrides.ProbeWorkspaceConfigFile(startDirectory)
                != LanguageMapOverrides.ConfigProbeStatus.Missing)
        {
            return false;
        }

        overrides = parentOverrides;
        _languageMapOverrideCache[startDirectory] = overrides;
        return true;
    }

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

        var extension = Path.GetExtension(filePath);
        if (!string.IsNullOrEmpty(extension))
            return !string.Equals(extension, ".h", StringComparison.OrdinalIgnoreCase);

        var fileName = Path.GetFileName(filePath);
        if (string.Equals(language, "codeowners", StringComparison.Ordinal)
            && string.Equals(fileName, "CODEOWNERS", StringComparison.OrdinalIgnoreCase))
        {
            // The scan result already proved that this exact path is one of the supported
            // repository-relative locations, so content loading may reuse that decision.
            // scan result が supported repository-relative path であることを証明済みなので、
            // content loading はその判定を再利用できる。
            return true;
        }
        if (TryGetExactFileNameLanguage(fileName, ignoreCase: true, out var nameLanguage))
            return string.Equals(language, nameLanguage, StringComparison.Ordinal);

        if (TryGetFileNamePrefixLanguage(fileName, ignoreCase: true, out var prefixLanguage))
            return string.Equals(language, prefixLanguage, StringComparison.Ordinal);

        return false;
    }

    internal static LanguageDetectionResult TryDetectLanguage(string filePath, string? content = null)
        => TryDetectLanguage(filePath, content, SymlinkPolicy.None, projectRoot: null, knownIndexability: null);

    internal static LanguageDetectionResult TryDetectLanguage(
        string filePath,
        string? content,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot,
        FileProbeStatus? knownIndexability = null,
        Func<string?, IReadOnlyDictionary<string, string>>? languageMapOverrideResolver = null,
        Func<string, FileStream>? openReadForIndexContent = null,
        bool allowPatternConfigDiscovery = true,
        bool fileNameIgnoreCase = true,
        Func<string, IEnumerable<string>>? enumerateFileSystemEntries = null,
        Func<string, Stream>? openPatternConfig = null,
        Func<string, bool, bool>? patternConfigDirectoryExists = null,
        Action<string, ReadOnlyMemory<byte>?, long?>? patternConfigInputObserver = null,
        bool deferUnknownScriptHeader = false,
        string? repositoryRoot = null)
    {
        var fileName = Path.GetFileName(filePath);
        var ext = Path.GetExtension(fileName);

        // Trusted explicit suffix overrides are authoritative for extensions, exact special
        // filenames that contain a suffix, and suffixed prefix variants.
        // 信頼済みの明示 suffix override は拡張子、suffix を含む完全一致 special filename、
        // suffix 付き prefix variant のいずれでも最優先する。
        if (TryDetectLanguageOverride(filePath, fileName, languageMapOverrideResolver, out var overrideLang))
        {
            return TryGetAmbiguousLanguageDescriptor(ext, out _)
                ? new LanguageDetectionResult(
                    FileProbeStatus.Supported,
                    overrideLang,
                    LanguageMapOverrideDetectionSource,
                    LanguageDetectionConfidence.High)
                : new LanguageDetectionResult(FileProbeStatus.Supported, overrideLang);
        }

        // Location-scoped special files are checked before filename-only rules. Indexing passes
        // the enclosing Git worktree root (with the scan root as the non-Git fallback), so a scan
        // rooted at a subdirectory cannot promote an arbitrary nested CODEOWNERS file.
        // location scoped special file は filename-only rule より先に判定する。indexing path は
        // enclosing Git worktree root（非 Git では scan root）を渡すため、subdirectory scan が
        // 任意の nested CODEOWNERS に GitHub semantics を付与することはない。
        if (TryGetRepositoryRelativePathLanguage(
                filePath,
                repositoryRoot ?? projectRoot,
                out var repositoryPathLanguage))
        {
            return new LanguageDetectionResult(FileProbeStatus.Supported, repositoryPathLanguage);
        }

        // Exact filename matching beats built-in extension lookup so manifest-style filenames
        // like `pyproject.toml` map to a dependency category instead of the generic file type.
        // `pyproject.toml` のような manifest 系 filename が汎用 extension ではなく dependency
        // category に紐づくよう、built-in の完全一致 filename を extension より先に判定する。
        if (TryGetExactFileNameLanguage(fileName, fileNameIgnoreCase, out var nameLang))
            return new LanguageDetectionResult(FileProbeStatus.Supported, nameLang);

        // Then try known filename prefixes for suffixed variants like Dockerfile.dev / Makefile.am.
        // The suffix must be non-empty so a bare `Dockerfile.` with trailing dot does not match.
        // Dockerfile.dev や Makefile.am のようなサフィックス付き変種を検出する。
        // `Dockerfile.` のような末尾ドットだけの形には一致させないため、サフィックスは1文字以上必須。
        if (TryGetFileNamePrefixLanguage(fileName, fileNameIgnoreCase, out var prefixLang))
            return new LanguageDetectionResult(FileProbeStatus.Supported, prefixLang);

        if (ShebangAmbiguousExtensions.Contains(ext))
        {
            var shebangLanguage = TryDetectLanguageFromScriptHeader(
                filePath,
                symlinkPolicy,
                projectRoot,
                knownIndexability,
                openReadForIndexContent,
                allowZshCompdef: false);
            if (shebangLanguage.Status == FileProbeStatus.Supported)
            {
                return TryGetAmbiguousLanguageDescriptor(ext, out _)
                    ? shebangLanguage with
                    {
                        Confidence = LanguageDetectionConfidence.High,
                    }
                    : shebangLanguage;
            }
            if (knownIndexability.HasValue
                && shebangLanguage.Status is FileProbeStatus.Missing or FileProbeStatus.ProbeFailed)
            {
                return shebangLanguage;
            }
        }

        if (string.Equals(ext, ".m", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".pl", StringComparison.OrdinalIgnoreCase))
        {
            return TryDetectAmbiguousExtensionLanguage(
                filePath,
                ext,
                content,
                projectRoot,
                knownIndexability,
                openReadForIndexContent,
                enumerateFileSystemEntries);
        }

        if (LangMap.TryGetValue(ext, out var lang))
        {
            if (lang == "c" && string.Equals(ext, ".h", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(content))
                {
                    return new LanguageDetectionResult(
                        FileProbeStatus.Supported,
                        lang,
                        HeaderExtensionFallbackDetectionSource,
                        LanguageDetectionConfidence.Low);
                }

                var cppHeaderDetection = DetectCppHeaderLanguage(content);
                if (cppHeaderDetection.IsCpp)
                {
                    return new LanguageDetectionResult(
                        FileProbeStatus.Supported,
                        "cpp",
                        cppHeaderDetection.UsedStrategicSampling
                            ? HeaderSampledLexicalMarkerDetectionSource
                            : HeaderLexicalMarkerDetectionSource,
                        cppHeaderDetection.UsedStrategicSampling
                            ? LanguageDetectionConfidence.Medium
                            : LanguageDetectionConfidence.High);
                }

                return new LanguageDetectionResult(
                    FileProbeStatus.Supported,
                    lang,
                    cppHeaderDetection.UsedStrategicSampling
                        ? HeaderSampledLexicalFallbackDetectionSource
                        : HeaderLexicalFallbackDetectionSource,
                    LanguageDetectionConfidence.Low);
            }

            return new LanguageDetectionResult(FileProbeStatus.Supported, lang);
        }

        if (ExtractorPluginRegistry.TryGetLanguageForExtension(ext, projectRoot, out var pluginLang))
            return new LanguageDetectionResult(FileProbeStatus.Supported, pluginLang);

        if (!string.IsNullOrEmpty(ext))
        {
            if (allowPatternConfigDiscovery)
                ExtractorPluginRegistry.LoadPatternConfigsForPath(
                    filePath,
                    projectRoot,
                    openFile: openPatternConfig,
                    directoryExists: patternConfigDirectoryExists,
                    observeInput: patternConfigInputObserver);
            if (ExtractorPluginRegistry.TryGetLanguageForExtension(ext, projectRoot, out pluginLang))
                return new LanguageDetectionResult(FileProbeStatus.Supported, pluginLang);

            if (deferUnknownScriptHeader)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            return TryDetectLanguageFromScriptHeader(
                filePath,
                symlinkPolicy,
                projectRoot,
                knownIndexability,
                openReadForIndexContent,
                allowZshCompdef: true);
        }

        return deferUnknownScriptHeader
            ? new LanguageDetectionResult(FileProbeStatus.Unsupported, null)
            : TryDetectLanguageFromScriptHeader(
            filePath,
            symlinkPolicy,
            projectRoot,
            knownIndexability,
            openReadForIndexContent,
            allowZshCompdef: true);
    }

    private static bool TryGetExactFileNameLanguage(string fileName, bool ignoreCase, out string language)
    {
        var map = ignoreCase ? FileNameMapIgnoreCase : FileNameMap;
        if (map.TryGetValue(fileName, out var detectedLanguage))
        {
            language = detectedLanguage;
            return true;
        }

        language = string.Empty;
        return false;
    }

    private static bool TryGetRepositoryRelativePathLanguage(
        string filePath,
        string? projectRoot,
        out string language)
    {
        language = string.Empty;
        string relativePath;
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            // The public compatibility helper has no repository-root context. Preserve useful
            // exact-filename detection there, while the indexing path below remains location-aware.
            // public compatibility helper には repository root context がないため filename を
            // 認識する。indexing path は下記の projectRoot 分岐で location-aware のままにする。
            if (string.Equals(Path.GetFileName(filePath), "CODEOWNERS", StringComparison.Ordinal))
            {
                language = "codeowners";
                return true;
            }
            return false;
        }

        try
        {
            relativePath = Path.GetRelativePath(projectRoot, filePath);
            if (Path.DirectorySeparatorChar == '\\')
                relativePath = relativePath.Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (relativePath == ".." || relativePath.StartsWith("../", StringComparison.Ordinal))
            return false;

        foreach (var (path, pathLanguage) in RepositoryRelativePathMap)
        {
            // GitHub's reserved CODEOWNERS paths are case-sensitive even when the local
            // filesystem is not. Do not classify a path that GitHub would ignore.
            // GitHub の予約 CODEOWNERS path は local filesystem が case-insensitive でも
            // case-sensitive のため、GitHub が無視する path を分類しない。
            if (!string.Equals(relativePath, path, StringComparison.Ordinal))
                continue;
            language = pathLanguage;
            return true;
        }
        return false;
    }

    private static bool TryGetFileNamePrefixLanguage(string fileName, bool ignoreCase, out string language)
    {
        language = string.Empty;
        if (!CouldMatchFileNamePrefix(fileName, ignoreCase))
            return false;

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var (prefix, prefixLanguage) in FileNamePrefixMap)
        {
            if (fileName.Length > prefix.Length &&
                fileName.StartsWith(prefix, comparison))
            {
                language = prefixLanguage;
                return true;
            }
        }

        return false;
    }

    private static bool CouldMatchFileNamePrefix(string fileName, bool ignoreCase)
    {
        if (fileName.Length <= "Makefile.".Length)
            return false;

        return ignoreCase
            ? fileName[0] is 'D' or 'd' or 'C' or 'c' or 'M' or 'm' or 'G' or 'g'
            : fileName[0] is 'D' or 'C' or 'M' or 'G';
    }

    private static bool TryDetectLanguageOverride(
        string filePath,
        string fileName,
        Func<string?, IReadOnlyDictionary<string, string>>? languageMapOverrideResolver,
        out string language)
    {
        language = string.Empty;
        if (!fileName.Contains('.', StringComparison.Ordinal))
            return false;

        var startDirectory = Path.GetDirectoryName(filePath);
        var overrides = languageMapOverrideResolver == null
            ? LanguageMapOverrides.LoadEffectiveMapFromDirectory(startDirectory)
            : languageMapOverrideResolver(startDirectory);

        return TryGetLanguageMapOverrideForFileName(fileName, overrides, out language);
    }

    private static bool TryGetLanguageMapOverrideForFileName(
        string fileName,
        IReadOnlyDictionary<string, string> overrides,
        out string language)
    {
        language = string.Empty;
        if (overrides.Count == 0 || !fileName.Contains('.', StringComparison.Ordinal))
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

    private const int CppHeaderDetectionByteBudget = 48 * 1024;
    private const int CppHeaderDetectionSampleByteBudget = CppHeaderDetectionByteBudget / 3;

    private readonly record struct CppHeaderDetectionResult(bool IsCpp, bool UsedStrategicSampling);

    private static CppHeaderDetectionResult DetectCppHeaderLanguage(string content)
    {
        var usedStrategicSampling = !FitsUtf8ByteBudget(content, CppHeaderDetectionByteBudget);
        IReadOnlyList<ReferenceExtractor.CppLexicalRange> ranges = usedStrategicSampling
            ? BuildCppHeaderDetectionRanges(content)
            : [new ReferenceExtractor.CppLexicalRange(0, content.Length)];
        var maskedSamples = ReferenceExtractor.MaskCppLexicalRanges(
            content,
            ranges,
            maskPreprocessorPayloads: true,
            collapseLineSplices: true);

        for (var sampleIndex = 0; sampleIndex < maskedSamples.Length; sampleIndex++)
        {
            if (ContainsCppHeaderMarker(
                maskedSamples[sampleIndex],
                firstLineIsComplete: IsCppLogicalLineBoundary(content, ranges[sampleIndex].Start)))
            {
                return new CppHeaderDetectionResult(true, usedStrategicSampling);
            }
        }

        return new CppHeaderDetectionResult(false, usedStrategicSampling);
    }

    private static IReadOnlyList<ReferenceExtractor.CppLexicalRange> BuildCppHeaderDetectionRanges(string content)
    {
        var midpoint = content.Length / 2;
        var candidateStarts = new[]
        {
            0,
            RetreatByUtf8ByteBudget(content, midpoint, CppHeaderDetectionSampleByteBudget / 2),
            RetreatByUtf8ByteBudget(content, content.Length, CppHeaderDetectionSampleByteBudget),
        };
        Array.Sort(candidateStarts);

        var mergedRanges = new List<ReferenceExtractor.CppLexicalRange>(candidateStarts.Length);
        foreach (var start in candidateStarts)
        {
            var end = AdvanceByUtf8ByteBudget(content, start, CppHeaderDetectionSampleByteBudget);
            if (end <= start)
                continue;

            if (mergedRanges.Count > 0 && start <= mergedRanges[^1].End)
            {
                var previous = mergedRanges[^1];
                mergedRanges[^1] = new ReferenceExtractor.CppLexicalRange(previous.Start, Math.Max(previous.End, end));
            }
            else
            {
                mergedRanges.Add(new ReferenceExtractor.CppLexicalRange(start, end));
            }
        }

        return mergedRanges;
    }

    private static bool ContainsCppHeaderMarker(string content, bool firstLineIsComplete)
    {
        var lineIndex = 0;
        var lineStart = 0;
        while (lineStart <= content.Length)
        {
            var lineBreak = content.IndexOf('\n', lineStart);
            var lineEnd = lineBreak >= 0 ? lineBreak : content.Length;
            var line = content.AsSpan(lineStart, lineEnd - lineStart);
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];
            if (LooksLikeCppHeaderLine(line, allowLineStartMarkers: lineIndex > 0 || firstLineIsComplete))
                return true;

            if (lineBreak < 0)
                break;

            lineStart = lineBreak + 1;
            lineIndex++;
        }

        return false;
    }

    internal static bool ContainsCppHeaderMarkerForTesting(
        string content,
        bool firstLineIsComplete = true)
        => ContainsCppHeaderMarker(content, firstLineIsComplete);

    private static bool IsCppLogicalLineBoundary(string content, int index)
    {
        if (index == 0)
            return true;
        if (index < 0 || index > content.Length)
            return false;

        var lineBreakIndex = index - 1;
        if (content[lineBreakIndex] == '\n')
            return !IsCppHeaderSplicedLineBreak(content, lineBreakIndex);
        if (content[lineBreakIndex] == '\r')
        {
            return (index >= content.Length || content[index] != '\n')
                && !IsCppHeaderSplicedLineBreak(content, lineBreakIndex);
        }

        return false;
    }

    private static bool IsCppHeaderSplicedLineBreak(string content, int lineBreakIndex)
    {
        var precedingIndex = lineBreakIndex - 1;
        if (content[lineBreakIndex] == '\n' && precedingIndex >= 0 && content[precedingIndex] == '\r')
            precedingIndex--;
        return precedingIndex >= 0 && content[precedingIndex] == '\\';
    }

    private static bool FitsUtf8ByteBudget(string content, int byteBudget)
        => AdvanceByUtf8ByteBudget(content, 0, byteBudget) == content.Length;

    private static int AdvanceByUtf8ByteBudget(string content, int start, int byteBudget)
    {
        var byteCount = 0;
        var index = start;
        while (index < content.Length)
        {
            var charCount = 1;
            int currentByteCount;
            var ch = content[index];
            if (ch <= 0x7f)
            {
                currentByteCount = 1;
            }
            else if (ch <= 0x7ff)
            {
                currentByteCount = 2;
            }
            else if (char.IsHighSurrogate(ch) && index + 1 < content.Length && char.IsLowSurrogate(content[index + 1]))
            {
                currentByteCount = 4;
                charCount = 2;
            }
            else
            {
                currentByteCount = 3;
            }

            if (byteCount + currentByteCount > byteBudget)
                break;

            byteCount += currentByteCount;
            index += charCount;
        }

        return index;
    }

    private static int RetreatByUtf8ByteBudget(string content, int end, int byteBudget)
    {
        var byteCount = 0;
        var index = end;
        while (index > 0)
        {
            var charCount = 1;
            var candidateIndex = index - 1;
            int currentByteCount;
            var ch = content[candidateIndex];
            if (char.IsLowSurrogate(ch) && candidateIndex > 0 && char.IsHighSurrogate(content[candidateIndex - 1]))
            {
                currentByteCount = 4;
                charCount = 2;
                candidateIndex--;
            }
            else if (ch <= 0x7f)
            {
                currentByteCount = 1;
            }
            else if (ch <= 0x7ff)
            {
                currentByteCount = 2;
            }
            else
            {
                currentByteCount = 3;
            }

            if (byteCount + currentByteCount > byteBudget)
                break;

            byteCount += currentByteCount;
            index -= charCount;
        }

        return index;
    }

    private static bool LooksLikeCppHeaderLine(ReadOnlySpan<char> line, bool allowLineStartMarkers)
    {
        line = line.Trim();
        if (line.IsEmpty)
            return false;

        if (allowLineStartMarkers
            && (line.StartsWith("namespace ", StringComparison.Ordinal)
                || line.StartsWith("template ", StringComparison.Ordinal)
                || line.StartsWith("template<", StringComparison.Ordinal)
                || line.StartsWith("using ", StringComparison.Ordinal)
                || line.StartsWith("class ", StringComparison.Ordinal)
                || line.StartsWith("enum class ", StringComparison.Ordinal)
                || line.StartsWith("enum struct ", StringComparison.Ordinal)
                || line.StartsWith("public:", StringComparison.Ordinal)
                || line.StartsWith("private:", StringComparison.Ordinal)
                || line.StartsWith("protected:", StringComparison.Ordinal)))
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
}
