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
        FileProbeStatus? knownIndexability = null)
        => TryDetectLanguage(
            filePath,
            content,
            _symlinkPolicy,
            _projectRoot,
            knownIndexability,
            LoadLanguageMapOverridesForIndexing,
            fileNameIgnoreCase: DirectoryUsesIgnoreCase(Path.GetDirectoryName(filePath) ?? _projectRoot));

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

            var loaded = LanguageMapOverrides.LoadEffectiveMapFromDirectory(startDirectory);
            _languageMapOverrideCache[startDirectory] = loaded;
            return CacheLastLanguageMapOverrideLookup(startDirectory, loaded);
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
        bool fileNameIgnoreCase = true)
    {
        var fileName = Path.GetFileName(filePath);
        var ext = Path.GetExtension(fileName);

        // Trusted explicit suffix overrides are authoritative for extensions, exact special
        // filenames that contain a suffix, and suffixed prefix variants.
        // 信頼済みの明示 suffix override は拡張子、suffix を含む完全一致 special filename、
        // suffix 付き prefix variant のいずれでも最優先する。
        if (TryDetectLanguageOverride(filePath, fileName, languageMapOverrideResolver, out var overrideLang))
            return new LanguageDetectionResult(FileProbeStatus.Supported, overrideLang);

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
            var shebangLanguage = TryDetectLanguageFromShebang(filePath, symlinkPolicy, projectRoot, knownIndexability);
            if (shebangLanguage.Status == FileProbeStatus.Supported)
                return shebangLanguage;
            if (knownIndexability.HasValue
                && shebangLanguage.Status is FileProbeStatus.Missing or FileProbeStatus.ProbeFailed)
            {
                return shebangLanguage;
            }
        }

        if (string.Equals(ext, ".m", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".pl", StringComparison.OrdinalIgnoreCase))
        {
            return TryDetectAmbiguousExtensionLanguage(filePath, ext, content, projectRoot, knownIndexability);
        }

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

        if (ExtractorPluginRegistry.TryGetLanguageForExtension(ext, projectRoot, out var pluginLang))
            return new LanguageDetectionResult(FileProbeStatus.Supported, pluginLang);

        if (!string.IsNullOrEmpty(ext))
        {
            ExtractorPluginRegistry.LoadPatternConfigsForPath(filePath, projectRoot);
            if (ExtractorPluginRegistry.TryGetLanguageForExtension(ext, projectRoot, out pluginLang))
                return new LanguageDetectionResult(FileProbeStatus.Supported, pluginLang);

            return TryDetectLanguageFromShebang(filePath, symlinkPolicy, projectRoot, knownIndexability);
        }

        return TryDetectLanguageFromShebang(filePath, symlinkPolicy, projectRoot, knownIndexability);
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
}
