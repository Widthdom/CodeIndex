using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Scans directories for source files and builds FileRecords.
/// ディレクトリを走査してソースファイルからFileRecordを構築する。
/// </summary>
public class FileIndexer
{
    internal enum FileProbeStatus
    {
        Supported,
        Unsupported,
        ProbeFailed,
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

    public readonly record struct ScanFilesResult(
        IReadOnlyList<string> Files,
        IReadOnlyList<ScanError> Errors,
        IReadOnlyList<string> NonIndexablePaths,
        IReadOnlyList<string> ProbeFailedFilePaths,
        IReadOnlyList<string> ListedDirectories,
        IReadOnlyList<string> FullyScannedDirectories,
        IReadOnlyList<string> AttributePrunedDirectories)
    {
        public bool HadErrors => Errors.Any(error => error.IsFatal);
    }

    internal enum PathFilterKind
    {
        None,
        IgnoredByRules,
        ExcludedByDefaultDirectory,
        ExcludedByDefaultFile,
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
            PathFilterKind.ExcludedByDefaultFile;
    }

    private static readonly string[] HotspotFamilyMarkerLanguages = ["csharp", "vb", "fsharp", "msbuild"];
    private static readonly string[] IgnoreFileNames = [".gitignore", ".cdidxignore"];
    // Extension-to-language mapping / 拡張子→言語名マッピング
    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"]     = "python",
        [".pyi"]    = "python",  // Python type stub (PEP 561) / Python 型スタブ
        [".pyw"]    = "python",  // Windowed Python script / Windows 用 Python スクリプト
        // Cython's `.pyx` / `.pxd` live in their own search-only bucket: they extend Python syntax
        // with `cdef class` / `cpdef` / `cdef` forms that the Python regex extractor cannot parse,
        // so mapping them to `python` would advertise `symbol_extraction=true` while emitting zero
        // symbols — the same "advertised contract vs. actual behavior" gap that sunk the earlier
        // `.sass` / `.styl` → `css` mapping.
        // Cython の `.pyx` / `.pxd` は `cdef class` / `cpdef` / `cdef` を含み Python 用正規表現では
        // 拾えない。python にマップすると `symbol_extraction=true` と広告しつつ 0 件しか出ない
        // 齟齬になるため、`.sass` / `.styl` と同じく独立の search-only バケットに分ける。
        [".pyx"]    = "cython",  // Cython source / Cython ソース
        [".pxd"]    = "cython",  // Cython declaration / Cython 宣言
        [".js"]     = "javascript",
        [".cjs"]    = "javascript",
        [".mjs"]    = "javascript",
        [".ts"]     = "typescript",
        [".cts"]    = "typescript",
        [".mts"]    = "typescript",
        [".jsx"]    = "javascript",
        [".tsx"]    = "typescript",
        [".rb"]     = "ruby",
        [".rake"]   = "ruby",    // Rake tasks / Rake タスク
        [".gemspec"]= "ruby",    // RubyGems spec / RubyGems スペック
        [".podspec"]= "ruby",    // CocoaPods spec (Ruby DSL) / CocoaPods スペック
        [".groovy"] = "groovy",
        [".gvy"]    = "groovy",
        [".gy"]     = "groovy",
        [".gsh"]    = "groovy",
        [".go"]     = "go",
        [".rs"]     = "rust",
        [".java"]   = "java",
        [".kt"]     = "kotlin",
        [".kts"]    = "kotlin",  // Kotlin Script / Kotlin スクリプト (Gradle Kotlin DSL など)
        [".swift"]  = "swift",
        [".cu"]     = "cuda",
        [".cuh"]    = "cuda",
        [".glsl"]   = "glsl",
        [".vert"]   = "glsl",
        [".frag"]   = "glsl",
        [".hlsl"]   = "hlsl",
        [".wgsl"]   = "wgsl",
        [".metal"]  = "metal",
        [".c"]      = "c",
        [".cpp"]    = "cpp",
        [".cc"]     = "cpp",
        [".cxx"]    = "cpp",
        [".h"]      = "c",       // Could be C or C++; defaults to C for symbol extraction
        [".hh"]     = "cpp",
        [".hpp"]    = "cpp",
        [".hxx"]    = "cpp",
        [".cs"]     = "csharp",
        [".cshtml"] = "csharp",  // Razor (ASP.NET MVC/Pages) / Razor テンプレート
        [".razor"]  = "csharp",  // Blazor component / Blazor コンポーネント
        [".m"]      = "objc",
        [".mm"]     = "objc",
        [".php"]    = "php",
        [".s"]      = "assembly", // Also used by Scheme; assembly is the more common default.
        [".S"]      = "assembly",
        [".asm"]    = "assembly",
        [".nasm"]   = "assembly",
        [".sh"]     = "shell",
        [".sql"]    = "sql",
        [".pgsql"]  = "sql",     // PostgreSQL dialect / PostgreSQL 方言
        [".tsql"]   = "sql",     // T-SQL (SQL Server) / T-SQL (SQL Server)
        [".plsql"]  = "sql",     // PL/SQL (Oracle) / PL/SQL (Oracle)
        [".pls"]    = "sql",     // PL/SQL script (Oracle) / PL/SQL スクリプト (Oracle)
        [".pks"]    = "sql",     // PL/SQL package spec (Oracle) / PL/SQL パッケージ仕様 (Oracle)
        [".pkb"]    = "sql",     // PL/SQL package body (Oracle) / PL/SQL パッケージ本体 (Oracle)
        [".plb"]    = "sql",     // PL/SQL wrapped source (Oracle) / PL/SQL ラップ済みソース (Oracle)
        [".psql"]   = "sql",     // psql scripts / psql スクリプト
        [".md"]     = "markdown",
        [".yaml"]   = "yaml",
        [".yml"]    = "yaml",
        [".json"]   = "json",
        [".toml"]   = "toml",
        [".xaml"]   = "xml",    // WPF/MAUI/Avalonia XAML / XAML テンプレート
        [".axaml"]  = "xml",    // Avalonia XAML / Avalonia XAML
        [".csproj"] = "msbuild",// C# project file / C# プロジェクトファイル
        [".fsproj"] = "msbuild",// F# project file / F# プロジェクトファイル
        [".vbproj"] = "msbuild",// VB.NET project file / VB.NET プロジェクトファイル
        [".props"]  = "msbuild",// MSBuild props / MSBuild プロパティ
        [".targets"]= "msbuild",// MSBuild targets / MSBuild ターゲット
        [".html"]   = "html",
        [".htm"]    = "html",    // Legacy / Windows / IIS default / 旧来の Windows / IIS 既定拡張子
        [".xhtml"]  = "html",    // XHTML / XHTML
        [".shtml"]  = "html",    // Server-side includes / サーバサイドインクルード
        [".css"]    = "css",
        [".scss"]   = "css",
        [".less"]   = "css",    // Less preprocessor / Less プリプロセッサ
        [".pcss"]   = "css",    // PostCSS / PostCSS
        // Sass indented syntax / Stylus use indentation instead of braces, so they live in
        // separate search-only buckets — the CSS symbol extractor's brace-based patterns do
        // not apply, but exact-name search still works.
        // Sass インデント構文と Stylus は波括弧ではなくインデントで構造化するため、
        // CSS のシンボル抽出（波括弧ベース）は使わず、検索用の別バケットに分ける。
        [".sass"]   = "sass",
        [".styl"]   = "stylus",
        [".vue"]    = "vue",
        [".svelte"] = "svelte",
        [".tf"]     = "terraform",
        [".v"]      = "verilog",  // Verilog defaults here; SystemVerilog has its own extensions.
        [".sv"]     = "systemverilog",
        [".svh"]    = "systemverilog",
        [".vhd"]    = "vhdl",
        [".vhdl"]   = "vhdl",
        [".lisp"]   = "commonlisp",
        [".lsp"]    = "commonlisp",
        [".cl"]     = "commonlisp", // Common Lisp wins the default over OpenCL here.
        [".rkt"]    = "racket",
        [".pas"]    = "pascal",
        [".pp"]     = "pascal",
        [".dpr"]    = "pascal",
        [".st"]     = "smalltalk",
        [".smalltalk"] = "smalltalk",
        [".ada"]    = "ada",
        [".adb"]    = "ada",
        [".ads"]    = "ada",
        [".f"]      = "fortran",
        [".f77"]    = "fortran",
        [".f90"]    = "fortran",
        [".f95"]    = "fortran",
        [".f03"]    = "fortran",
        [".f08"]    = "fortran",
        [".for"]    = "fortran",
        [".ftn"]    = "fortran",
        [".cbl"]    = "cobol",
        [".cob"]    = "cobol",
        [".cobol"]  = "cobol",
        [".cpy"]    = "cobol",   // COBOL copybook / COBOL コピー句
        [".raku"]   = "raku",
        [".rakumod"]= "raku",
        [".rakutest"]= "raku",
        [".t"]      = "perl",    // Common Perl test scripts / Perl の test スクリプト
        [".dart"]   = "dart",
        [".scala"]  = "scala",
        [".sc"]     = "scala",
        [".r"]      = "r",
        [".R"]      = "r",
        [".ex"]     = "elixir",
        [".exs"]    = "elixir",
        [".lua"]    = "lua",
        [".ml"]     = "ocaml",
        [".mli"]    = "ocaml",
        [".cr"]     = "crystal",
        [".clj"]    = "clojure",
        [".cljs"]   = "clojure",
        [".cljc"]   = "clojure",
        [".edn"]    = "clojure",
        [".d"]      = "d",
        [".erl"]    = "erlang",
        [".hrl"]    = "erlang",
        [".jl"]     = "julia",
        [".nim"]    = "nim",
        [".nims"]   = "nim",
        [".pl"]     = "perl",
        [".pm"]     = "perl",
        [".pod"]    = "perl",
        [".psgi"]   = "perl",
        [".cgi"]    = "perl",
        [".fcgi"]   = "perl",
        [".t"]      = "perl",
        [".sol"]    = "solidity",
        [".tcl"]    = "tcl",
        [".tk"]     = "tcl",
        [".fs"]     = "fsharp",
        [".fsx"]    = "fsharp",
        [".fsi"]    = "fsharp",
        [".bas"]    = "vb",
        [".cls"]    = "vb",
        [".ctl"]    = "vb",
        [".dob"]    = "vb",
        [".dsr"]    = "vb",
        [".frm"]    = "vb",
        [".pag"]    = "vb",
        [".vba"]    = "vb",
        [".vb"]     = "vb",
        [".vbhtml"] = "vb",
        [".vbs"]    = "vb",
        [".hs"]     = "haskell",
        [".lhs"]    = "haskell",
        [".zig"]    = "zig",
        [".proto"]  = "protobuf",  // Protocol Buffers / Protocol Buffers 定義
        [".graphql"]= "graphql",   // GraphQL schema/queries / GraphQL スキーマ・クエリ
        [".gql"]    = "graphql",
        [".gradle"] = "gradle",    // Gradle build scripts / Gradle ビルドスクリプト
        [".cmake"]  = "cmake",     // CMake scripts / CMake スクリプト
        [".mk"]     = "makefile",  // Makefile fragment / Makefile フラグメント
        [".ps1"]    = "powershell",// PowerShell scripts / PowerShell スクリプト
        [".psm1"]   = "powershell",// PowerShell modules / PowerShell モジュール
        [".psd1"]   = "powershell",// PowerShell data files / PowerShell データファイル
        [".bat"]    = "batch",     // Windows batch files / Windows バッチファイル
        [".cmd"]    = "batch",
        [".bash"]   = "shell",
        [".zsh"]    = "shell",
        [".fish"]   = "shell",
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
        ["Dockerfile"]    = "dockerfile",
        [".dockerfile"]   = "dockerfile",
        ["Containerfile"] = "dockerfile",   // Podman's Dockerfile alternative / Podman の Dockerfile 代替
        [".containerfile"]= "dockerfile",
        ["Makefile"]      = "makefile",
        ["GNUmakefile"]   = "makefile",     // GNU Make explicit filename / GNU Make 明示ファイル名
        ["Justfile"]      = "justfile",     // Just command runner / Just コマンドランナー
        ["CMakeLists.txt"]= "cmake",
        ["Vagrantfile"]   = "ruby",         // Vagrant uses Ruby DSL / Vagrant は Ruby DSL
        ["Gemfile"]       = "ruby",         // Bundler dependency manifest / Bundler 依存マニフェスト
        ["Rakefile"]      = "ruby",         // Rake task runner / Rake タスクランナー
        ["Podfile"]       = "ruby",         // CocoaPods dependency manifest / CocoaPods 依存マニフェスト
        ["Guardfile"]     = "ruby",         // Guard file-watcher / Guard ファイルウォッチャー
        ["Capfile"]       = "ruby",         // Capistrano deployment / Capistrano デプロイ
        ["NAMESPACE"]     = "r",            // R package namespace directives / R パッケージ namespace ディレクティブ
        [".Rprofile"]     = "r",            // R startup profile / R 起動プロファイル
        ["Rprofile.site"] = "r",            // Site-wide R startup profile / サイト共通 R 起動プロファイル
        ["BUILD"]         = "python",       // Bazel Starlark build file / Bazel Starlark ビルドファイル
        ["BUILD.bazel"]   = "python",
        ["WORKSPACE"]     = "python",       // Bazel workspace / Bazel ワークスペース
        ["WORKSPACE.bazel"]= "python",
        ["pyproject.toml"] = "python",      // Python project manifest / Python プロジェクトマニフェスト
        ["requirements.txt"] = "python",    // Python dependencies manifest / Python 依存関係マニフェスト
        ["go.mod"]        = "go",           // Go module manifest / Go モジュールマニフェスト
        ["go.work"]       = "go",           // Go workspace manifest / Go ワークスペースマニフェスト
        [".editorconfig"] = "editorconfig",
        [".gitignore"]    = "gitignore",
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
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml",
        "Gemfile.lock", "Cargo.lock", "composer.lock", "poetry.lock", "bun.lockb",
    };

    // Maximum file size to index (10 MB) / インデックス対象の最大ファイルサイズ (10 MB)
    private const long MaxFileSize = 10 * 1024 * 1024;

    private readonly string _projectRoot;
    private readonly string _ignoreRuleRoot;
    private readonly IReadOnlyList<string> _ancestorIgnoreDirectories;
    private readonly bool _ignoreCase;
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

            while (tokens.Count > 0 && tokens[^1] is { Value: ' ', Escaped: false })
                tokens.RemoveAt(tokens.Count - 1);

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
            return new Regex(builder.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled);
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
                builder.Append(@"\^");
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

    public FileIndexer(string projectRoot, bool ignoreCase, string? ignoreRuleRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _ignoreRuleRoot = NormalizeIgnoreRuleRoot(ignoreRuleRoot);
        _ancestorIgnoreDirectories = BuildAncestorIgnoreDirectories(_ignoreRuleRoot, _projectRoot);
        _ignoreCase = ignoreCase;
        var pathComparer = _ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        (_submodulePaths, _submoduleAncestorPaths) = LoadGitSubmodulePaths(_ignoreRuleRoot, _projectRoot, pathComparer);
    }

    private static bool ProbeFileSystemIgnoreCase(string projectRoot)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(projectRoot);
            if (TryCreateCaseVariant(normalizedRoot, out var rootVariant))
                return Directory.Exists(LongPath.EnsureWindowsPrefix(rootVariant));

            var probePath = Path.Combine(normalizedRoot, $".cdidx_case_probe_{Guid.NewGuid():N}");
            var prefixedProbePath = LongPath.EnsureWindowsPrefix(probePath);
            File.WriteAllText(prefixedProbePath, string.Empty);
            try
            {
                return TryCreateCaseVariant(probePath, out var probeVariant) && File.Exists(LongPath.EnsureWindowsPrefix(probeVariant));
            }
            finally
            {
                if (File.Exists(prefixedProbePath))
                    File.Delete(prefixedProbePath);
            }
        }
        catch
        {
            return OperatingSystem.IsWindows();
        }
    }

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
        return merged;
    }

    public static string? DetectLanguage(string filePath)
        => TryDetectLanguage(filePath).Language;

    internal static bool IsIgnoreFilePath(string path)
        => IgnoreFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    internal static LanguageDetectionResult TryDetectLanguage(string filePath, string? content = null)
    {
        // Exact filename matching beats extension lookup so manifest-style filenames like
        // `pyproject.toml` can map to a project language instead of the generic file type.
        // `pyproject.toml` のようなマニフェスト系ファイル名が、汎用拡張子ではなく
        // プロジェクト言語に紐づくよう、完全一致ファイル名を拡張子より先に判定する。
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

        if (!string.IsNullOrEmpty(ext))
            return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

        return TryDetectLanguageFromShebang(filePath);
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

    internal static FileProbeStatus GetFileIndexability(string filePath)
    {
        // Reject symlinks/reparse points here so every caller (full scan, --files / --commits update mode,
        // dry-run) gets the same skip behavior. On Windows, Hidden/System paths are also rejected to avoid
        // indexing OS-owned caches such as System Volume Information and $Recycle.Bin during broad scans.
        // Using File.GetAttributes uses lstat-like semantics on .NET (does not follow the symlink target),
        // which is what we need on both Windows and Unix.
        // The Unix stat() path below follows symlinks, so without this guard a symlink-to-regular-file
        // would otherwise slip through as Supported.
        // ここで symlink / reparse point を弾くことで、フルスキャン・--files/--commits の update モード・
        // dry-run など全呼び出し元に同じ skip 挙動を効かせる。Windows では Hidden/System 属性も弾き、
        // broad scan で System Volume Information や $Recycle.Bin などの OS 管理 cache を索引しない。
        // File.GetAttributes は .NET 上で lstat 相当（symlink target を辿らない）なので、Windows でも Unix でも必要な判定になる。
        // Unix 側の stat() は symlink を辿るため、このガードが無いと symlink→通常ファイルが
        // Supported として通過してしまう。
        if (HasSkippedAttributes(filePath))
            return FileProbeStatus.Unsupported;

        if (OperatingSystem.IsWindows())
            return FileProbeStatus.Supported;

        if (!UnixFileStatus.TryGetFileMode(filePath, out var mode))
            return FileProbeStatus.ProbeFailed;

        return (mode & UnixFileStatus.FileTypeMask) == UnixFileStatus.RegularFile
            ? FileProbeStatus.Supported
            : FileProbeStatus.Unsupported;
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

    public string? GetProjectMarkerFingerprint(string? lang)
    {
        var projectMarkerPatterns = GetProjectMarkerPatterns(lang);
        if (projectMarkerPatterns == null)
            return null;

        var projectMarkers = new List<string>();
        CollectProjectMarkerFiles(_projectRoot, projectMarkerPatterns, projectMarkers);
        projectMarkers.Sort(StringComparer.Ordinal);

        var payload = string.Join('\n', projectMarkers);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
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

    private static int CountProjectMarkerFiles(string dir, IReadOnlyList<string> patterns)
    {
        var prefixedDir = LongPath.EnsureWindowsPrefix(dir);
        var count = 0;
        foreach (var pattern in patterns)
        {
            foreach (var file in Directory.EnumerateFiles(prefixedDir, pattern, SearchOption.TopDirectoryOnly))
            {
                if (HasSkippedAttributes(LongPath.RemoveWindowsPrefix(file)))
                    continue;

                count++;
                if (count > 1)
                    return count;
            }
        }

        return count;
    }

    private void CollectProjectMarkerFiles(string dir, IReadOnlyList<string> patterns, List<string> projectMarkers)
    {
        try
        {
            var prefixedDir = LongPath.EnsureWindowsPrefix(dir);
            foreach (var pattern in patterns)
            {
                foreach (var file in Directory.EnumerateFiles(prefixedDir, pattern, SearchOption.TopDirectoryOnly))
                {
                    var markerFile = LongPath.RemoveWindowsPrefix(file);
                    if (!HasSkippedAttributes(markerFile))
                        projectMarkers.Add(NormalizeScopeKey(Path.GetRelativePath(_projectRoot, markerFile)));
                }
            }

            foreach (var enumeratedSubDir in Directory.EnumerateDirectories(prefixedDir))
            {
                var subDir = LongPath.RemoveWindowsPrefix(enumeratedSubDir);
                if (SkipDirs.Contains(Path.GetFileName(subDir)) || HasSkippedAttributes(subDir))
                    continue;

                CollectProjectMarkerFiles(subDir, patterns, projectMarkers);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort like ScanFiles(): unreadable directories do not abort the whole index.
            // ScanFiles() と同じく best-effort。読めないディレクトリでは index 全体を落とさない。
        }
        catch (IOException)
        {
            // Best-effort like ScanFiles(): transient IO failures should only skip that subtree.
            // ScanFiles() と同じく best-effort。IO 失敗はその subtree だけスキップする。
        }
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
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static bool IsPathEqualOrParent(string candidateParent, string candidateChild)
    {
        var normalizedParent = Path.GetFullPath(candidateParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedChild = Path.GetFullPath(candidateChild)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return CodeIndex.Cli.PathCasing.IsPathEqualOrParent(normalizedParent, normalizedChild);
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
        var fullPath = Path.GetFullPath(absolutePath);
        var relativePath = NormalizeIgnorePath(Path.GetRelativePath(_projectRoot, fullPath));
        if (relativePath.StartsWith("../", StringComparison.Ordinal))
            return new PathFilterResult(PathFilterKind.None, errors);

        var fullyScanned = true;
        var preloadResult = LoadAncestorIgnoreRules(errors, ref fullyScanned);
        var activeIgnoreRules = preloadResult.Rules;
        if (!preloadResult.IgnoreRulesAvailable)
            return new PathFilterResult(PathFilterKind.IgnoreRulesUnavailable, errors);

        var projectRootFilterKind = GetDirectoryFilterKind(_projectRoot, activeIgnoreRules, isProjectRoot: true);
        if (projectRootFilterKind != PathFilterKind.None)
            return new PathFilterResult(projectRootFilterKind, errors);

        if (relativePath.Length == 0 || relativePath == ".")
            return new PathFilterResult(PathFilterKind.None, errors);

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentDirectory = _projectRoot;
        var loadResult = LoadIgnoreRulesForDirectory(currentDirectory, activeIgnoreRules, errors, ref fullyScanned);
        activeIgnoreRules = loadResult.Rules;
        if (!loadResult.IgnoreRulesAvailable)
            return new PathFilterResult(PathFilterKind.IgnoreRulesUnavailable, errors);

        var directorySegmentCount = isDirectory ? segments.Length : Math.Max(segments.Length - 1, 0);
        // Mirror EnumerateDirectory's passthrough behavior so update-mode filters (--files /
        // --commits) match a fresh full scan: when SkipDirs is overridden because we're
        // routing toward a declared submodule, files/subdirs that do not themselves lead
        // to a submodule must still be excluded.
        // EnumerateDirectory の passthrough と挙動を一致させ、--files / --commits などの
        // 更新モードのフィルタがフルスキャンと食い違わないようにする。submodule への通過のため
        // SkipDirs を上書きした場合でも、submodule に到達しないファイル・サブディレクトリは
        // 引き続き除外する。
        var inSubmodulePassthrough = false;
        for (var i = 0; i < directorySegmentCount; i++)
        {
            var directoryName = segments[i];
            var childDirectory = Path.Combine(currentDirectory, directoryName);
            var cumulativeRelPath = NormalizeIgnorePath(Path.GetRelativePath(_projectRoot, childDirectory));
            var isSubmodule = _submodulePaths.Contains(cumulativeRelPath);
            var isSubmoduleAncestor = _submoduleAncestorPaths.Contains(cumulativeRelPath);

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

            if (activeIgnoreRules.IsIgnored(childDirectory, isDirectory: true))
                return new PathFilterResult(PathFilterKind.IgnoredByRules, errors);

            currentDirectory = childDirectory;
            fullyScanned = true;
            loadResult = LoadIgnoreRulesForDirectory(currentDirectory, activeIgnoreRules, errors, ref fullyScanned);
            activeIgnoreRules = loadResult.Rules;
            if (!loadResult.IgnoreRulesAvailable)
                return new PathFilterResult(PathFilterKind.IgnoreRulesUnavailable, errors);
        }

        if (isDirectory)
            return new PathFilterResult(PathFilterKind.None, errors);

        // File directly inside a submodule-ancestor passthrough directory: walker would not
        // index it, so neither should this filter.
        // submodule 祖先（passthrough）に直接置かれているファイルは walker も索引しないため
        // ここでも除外する。
        if (inSubmodulePassthrough)
            return new PathFilterResult(PathFilterKind.ExcludedByDefaultDirectory, errors);

        var fileName = Path.GetFileName(fullPath);
        if (SkipFiles.Contains(fileName))
            return new PathFilterResult(PathFilterKind.ExcludedByDefaultFile, errors);

        return activeIgnoreRules.IsIgnored(fullPath, isDirectory: false)
            ? new PathFilterResult(PathFilterKind.IgnoredByRules, errors)
            : new PathFilterResult(PathFilterKind.None, errors);
    }

    internal ScanFilesResult ScanFilesDetailed()
    {
        var files = new List<string>();
        var errors = new List<ScanError>();
        var nonIndexablePaths = new HashSet<string>(StringComparer.Ordinal);
        var probeFailedFilePaths = new HashSet<string>(StringComparer.Ordinal);
        var listedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var fullyScannedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var attributePrunedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var fullyScanned = true;
        var preloadResult = LoadAncestorIgnoreRules(errors, ref fullyScanned);
        if (preloadResult.IgnoreRulesAvailable)
        {
            ScanDirectory(_projectRoot, files, errors, nonIndexablePaths, probeFailedFilePaths, listedDirectories, fullyScannedDirectories, attributePrunedDirectories, preloadResult.Rules, isProjectRoot: true);
        }
        return new ScanFilesResult(
            files,
            errors,
            nonIndexablePaths.ToList(),
            probeFailedFilePaths.ToList(),
            listedDirectories.ToList(),
            fullyScannedDirectories.ToList(),
            attributePrunedDirectories.ToList());
    }

    private bool ScanDirectory(
        string dir,
        List<string> results,
        List<ScanError> errors,
        HashSet<string> nonIndexablePaths,
        HashSet<string> probeFailedFilePaths,
        HashSet<string> listedDirectories,
        HashSet<string> fullyScannedDirectories,
        HashSet<string> attributePrunedDirectories,
        IgnoreRuleSet activeIgnoreRules,
        bool isProjectRoot = false)
    {
        var relativeDir = ToRelativePath(dir);

        var filterKind = GetDirectoryFilterKind(dir, activeIgnoreRules, isProjectRoot);
        if (filterKind != PathFilterKind.None)
        {
            listedDirectories.Add(relativeDir);
            fullyScannedDirectories.Add(relativeDir);
            return true;
        }

        return EnumerateDirectory(dir, results, errors, nonIndexablePaths, probeFailedFilePaths, listedDirectories, fullyScannedDirectories, attributePrunedDirectories, activeIgnoreRules);
    }

    private bool EnumerateDirectory(
        string dir,
        List<string> results,
        List<ScanError> errors,
        HashSet<string> nonIndexablePaths,
        HashSet<string> probeFailedFilePaths,
        HashSet<string> listedDirectories,
        HashSet<string> fullyScannedDirectories,
        HashSet<string> attributePrunedDirectories,
        IgnoreRuleSet inheritedIgnoreRules)
    {
        var fullyScanned = true;
        try
        {
            var loadResult = LoadIgnoreRulesForDirectory(dir, inheritedIgnoreRules, errors, ref fullyScanned);
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

            if (!passthrough)
            {
                foreach (var enumeratedFile in Directory.EnumerateFiles(LongPath.EnsureWindowsPrefix(dir)))
                {
                    // Strip any \\?\ prefix returned by EnumerateFiles when we passed a long-path
                    // directory, so downstream relative-path math (which compares against the
                    // un-prefixed _projectRoot) still produces the canonical project-relative key.
                    // \\?\ 接頭辞付きの long-path ディレクトリを渡したとき EnumerateFiles も接頭辞付きで
                    // 返すため、_projectRoot（接頭辞なし）と突き合わせる相対パス計算が崩れないよう剥がす。
                    var file = LongPath.RemoveWindowsPrefix(enumeratedFile);
                    var fileName = Path.GetFileName(file);

                    // Skip excluded file names / 除外ファイル名をスキップ
                    if (SkipFiles.Contains(fileName))
                        continue;

                    if (activeIgnoreRules.IsIgnored(file, isDirectory: false))
                        continue;

                    // GetFileIndexability also rejects file symlinks/reparse points so the update-mode
                    // (--files / --commits) path gets the same skip behavior without a second probe here.
                    // GetFileIndexability もファイル symlink / reparse point を拒否するため、
                    // update モード (--files / --commits) でも同じ skip 挙動が二重プローブ無しで効く。
                    var indexability = GetFileIndexability(file);
                    if (indexability == FileProbeStatus.ProbeFailed)
                    {
                        var relativePath = ToRelativePath(file);
                        errors.Add(new ScanError(relativePath, "Could not probe file for indexability/language."));
                        probeFailedFilePaths.Add(relativePath);
                        continue;
                    }

                    if (indexability != FileProbeStatus.Supported)
                    {
                        nonIndexablePaths.Add(ToRelativePath(file));
                        continue;
                    }

                    // Include files with a known extension/filename or an extensionless recognized shebang
                    // 既知の拡張子・既知ファイル名、または拡張子なしで shebang を認識できるファイルを含める
                    var language = TryDetectLanguage(file);
                    if (language.Status == FileProbeStatus.ProbeFailed)
                    {
                        var relativePath = ToRelativePath(file);
                        errors.Add(new ScanError(relativePath, "Could not probe file for indexability/language."));
                        probeFailedFilePaths.Add(relativePath);
                        continue;
                    }

                    if (language.Status == FileProbeStatus.Supported)
                        results.Add(file);
                    else
                        nonIndexablePaths.Add(ToRelativePath(file));
                }
            }

            // A successful file listing proves the direct children of this directory.
            // Child subtree failures must not revoke that authority for sibling-file purge.
            // ファイル列挙が成功した時点で、このディレクトリ直下の子要素については authoritative とみなせる。
            // 子サブツリー失敗が sibling file purge の authority を奪ってはいけない。
            listedDirectories.Add(ToRelativePath(dir));

            foreach (var enumeratedSubDir in Directory.EnumerateDirectories(LongPath.EnsureWindowsPrefix(dir)))
            {
                var subDir = LongPath.RemoveWindowsPrefix(enumeratedSubDir);
                // In passthrough mode, only descend into subdirectories that are themselves
                // submodules or submodule ancestors. Treat siblings the same way SkipDirs
                // would have treated them at this point.
                // passthrough 中は、submodule 自体または submodule の祖先に該当する
                // サブディレクトリのみ降りる。その他は本来 SkipDirs で止まっていた扱いに戻す。
                if (passthrough && !IsSubmoduleOrAncestor(subDir))
                {
                    var subRelative = ToRelativePath(subDir);
                    listedDirectories.Add(subRelative);
                    fullyScannedDirectories.Add(subRelative);
                    continue;
                }

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
                if (HasSkippedAttributes(subDir))
                {
                    var subRelative = ToRelativePath(subDir);
                    listedDirectories.Add(subRelative);
                    fullyScannedDirectories.Add(subRelative);
                    attributePrunedDirectories.Add(subRelative);
                    continue;
                }

                fullyScanned &= ScanDirectory(subDir, results, errors, nonIndexablePaths, probeFailedFilePaths, listedDirectories, fullyScannedDirectories, attributePrunedDirectories, activeIgnoreRules);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip inaccessible directories / アクセス不可ディレクトリはスキップ
            errors.Add(new ScanError(ToRelativePath(dir), "Could not scan directory due to permissions."));
            fullyScanned = false;
        }
        catch (IOException)
        {
            // Skip on I/O errors / I/Oエラー時はスキップ
            errors.Add(new ScanError(ToRelativePath(dir), "Could not scan directory due to an I/O error."));
            fullyScanned = false;
        }

        if (fullyScanned)
            fullyScannedDirectories.Add(ToRelativePath(dir));

        return fullyScanned;
    }

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
            var prefixedIgnorePath = LongPath.EnsureWindowsPrefix(ignorePath);
            if (!File.Exists(prefixedIgnorePath))
                continue;

            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(prefixedIgnorePath))
                {
                    lineNumber++;
                    if (IgnoreRule.TryParse(dir, line, _ignoreCase, out var rule, out var errorMessage) && rule != null)
                        rules.Add(rule);
                    else if (errorMessage != null)
                        errors.Add(new ScanError($"{ToRelativePath(ignorePath)}:{lineNumber}", errorMessage, ScanIssueSeverity.Warning));
                }
            }
            catch (UnauthorizedAccessException)
            {
                errors.Add(new ScanError(ToRelativePath(ignorePath), $"Could not read {ignoreFileName}."));
                fullyScanned = false;
                ignoreRulesAvailable = false;
            }
            catch (IOException)
            {
                errors.Add(new ScanError(ToRelativePath(ignorePath), $"Could not read {ignoreFileName}."));
                fullyScanned = false;
                ignoreRulesAvailable = false;
            }
        }

        return ignoreRulesAvailable
            ? new IgnoreRuleLoadResult(IgnoreRuleSet.CreateChild(inheritedIgnoreRules, rules), IgnoreRulesAvailable: true)
            : new IgnoreRuleLoadResult(inheritedIgnoreRules, IgnoreRulesAvailable: false);
    }

    private IgnoreRuleLoadResult LoadAncestorIgnoreRules(List<ScanError> errors, ref bool fullyScanned)
    {
        var activeIgnoreRules = IgnoreRuleSet.Empty;
        foreach (var dir in _ancestorIgnoreDirectories)
        {
            var loadResult = LoadIgnoreRulesForDirectory(dir, activeIgnoreRules, errors, ref fullyScanned);
            activeIgnoreRules = loadResult.Rules;
            if (!loadResult.IgnoreRulesAvailable)
                return new IgnoreRuleLoadResult(activeIgnoreRules, IgnoreRulesAvailable: false);
        }

        return new IgnoreRuleLoadResult(activeIgnoreRules, IgnoreRulesAvailable: true);
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

        var relativePath = NormalizeIgnorePath(Path.GetRelativePath(ignoreRuleRoot, projectRoot));
        if (relativePath.Length == 0 || relativePath == "." || relativePath.StartsWith("../", StringComparison.Ordinal))
            return [];

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return [];

        var directories = new List<string> { ignoreRuleRoot };
        var currentDirectory = ignoreRuleRoot;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            currentDirectory = Path.Combine(currentDirectory, segments[i]);
            directories.Add(currentDirectory);
        }

        return directories;
    }

    // Parse <ignoreRuleRoot>/.gitmodules and return submodule working-tree paths (and
    // their ancestor directories) relative to projectRoot. Submodules outside projectRoot
    // are dropped silently. Absent or unreadable .gitmodules yields empty sets so callers
    // see the same shape as a non-submodule repository.
    // <ignoreRuleRoot>/.gitmodules を解析し、projectRoot 相対の submodule ワークツリーパスと
    // その祖先ディレクトリを返す。projectRoot 外の submodule は無視。.gitmodules が無い・
    // 読めない場合は空集合を返し、submodule の無いリポジトリと同じ形を保つ。
    private static (HashSet<string> Paths, HashSet<string> AncestorPaths) LoadGitSubmodulePaths(
        string ignoreRuleRoot, string projectRoot, StringComparer pathComparer)
    {
        var submodulePaths = new HashSet<string>(pathComparer);
        var ancestorPaths = new HashSet<string>(pathComparer);

        var gitmodulesPath = Path.Combine(ignoreRuleRoot, ".gitmodules");
        var prefixedGitmodulesPath = LongPath.EnsureWindowsPrefix(gitmodulesPath);
        if (!File.Exists(prefixedGitmodulesPath))
            return (submodulePaths, ancestorPaths);

        string[] lines;
        try
        {
            lines = File.ReadAllLines(prefixedGitmodulesPath);
        }
        catch (IOException)
        {
            return (submodulePaths, ancestorPaths);
        }
        catch (UnauthorizedAccessException)
        {
            return (submodulePaths, ancestorPaths);
        }

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

            submodulePaths.Add(relativeToProject);
            var segments = relativeToProject.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < segments.Length; i++)
                ancestorPaths.Add(string.Join('/', segments, 0, i));
        }

        return (submodulePaths, ancestorPaths);
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

            var value = line[(equalsIndex + 1)..].Trim();
            var commentIndex = value.IndexOfAny(['#', ';']);
            if (commentIndex >= 0)
                value = value[..commentIndex].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            if (value.Length == 0)
                continue;
            if (Path.IsPathRooted(value))
                continue;

            yield return value;
        }
    }

    private static string NormalizeIgnorePath(string path)
        => path.Replace('\\', '/').TrimEnd('/');

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
        var indexability = GetFileIndexability(absolutePath);
        if (indexability != FileProbeStatus.Supported)
            throw new InvalidOperationException("Only regular files can be indexed");

        var relativePath = Path.GetRelativePath(_projectRoot, absolutePath);

        // Read raw bytes through a single FileStream and cap the accumulated payload at
        // MaxFileSize so a file that grew between the size probe and the read can no
        // longer bypass the cap. Splitting `FileInfo.Length` from `File.ReadAllBytes`
        // left a TOCTOU window where an attacker (or any build/log emitter rapidly
        // appending to a generated file) could grow a 1 MB file to multi-GB between
        // stat and read and force the indexer into an OOM-sized allocation; reading
        // through one open handle removes the second stat call, and the read loop's
        // running total guarantees we never accumulate more than MaxFileSize bytes
        // regardless of how aggressively a concurrent writer extends the file.
        // ファイルを 1 本の FileStream で開き、MaxFileSize を上限として累積バッファを
        // 制限することで、サイズ確認と読み込みの間にファイルが肥大化しても上限を
        // 回避できないようにする。`FileInfo.Length` と `File.ReadAllBytes` を分離して
        // いた従来実装では、stat と read の間に攻撃者 (もしくは自動生成ファイルを高速
        // に追記し続けるビルド/ログ系) が 1 MB のファイルを数 GB まで肥大化させて
        // インデクサに巨大確保を強制できる TOCTOU 経路があったが、1 本のオープン
        // ハンドルで読むことで 2 回目の stat を排除し、ループ側の総バイト数チェック
        // により並行追記がどれほど激しくても MaxFileSize 以上の確保は発生しない。
        byte[] bytes;
        long sizeBytes;
        DateTime modifiedUtc;
        var ioPath = LongPath.EnsureWindowsPrefix(absolutePath);
        using (var stream = new FileStream(
            ioPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: false))
        {
            var initialLength = stream.Length;
            if (initialLength > MaxFileSize)
                throw new InvalidOperationException($"File too large ({initialLength / 1024 / 1024} MB > {MaxFileSize / 1024 / 1024} MB limit)");

            // Pre-size the accumulator to the observed length but cap initial capacity
            // at MaxFileSize so a tampered Length cannot force a huge up-front allocation.
            // 初期容量は観測した長さに合わせるが MaxFileSize で打ち切り、Length を偽装
            // されても巨大な事前確保を起こさないようにする。
            var initialCapacity = (int)Math.Min(initialLength, MaxFileSize);
            using var accumulator = new MemoryStream(initialCapacity);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxFileSize)
                    throw new InvalidOperationException($"File too large (> {MaxFileSize / 1024 / 1024} MB limit; grew during read)");
                accumulator.Write(buffer, 0, read);
            }
            bytes = accumulator.ToArray();
            sizeBytes = total;
            modifiedUtc = File.GetLastWriteTimeUtc(ioPath);
        }

        // Compute the checksum on the byte stream after collapsing CRLF / CR to LF so
        // a Windows clone (core.autocrlf=true) and a Linux/macOS clone of the same logical
        // file produce identical checksums. Hashing the unnormalized raw bytes used to
        // mark every file as "changed" the first time a developer indexed a cross-OS clone
        // or a shared NAS workspace. BOM bytes are preserved verbatim so BOM addition /
        // removal still flips the checksum and triggers incremental re-index. The streaming
        // helper avoids re-encoding the UTF-8 string (~10 MB saved for large files).
        // Closes #1544.
        // CRLF / CR を LF に潰した上で SHA256 を取り、Windows (core.autocrlf=true) clone と
        // Linux/macOS clone の同じ論理内容で checksum を一致させる。生バイトをそのまま
        // ハッシュしていた以前は、cross-OS の clone や共有 NAS で全ファイルが「変更あり」
        // 扱いとなり毎回フル再索引が走っていた。BOM はそのままハッシュ対象に残すので
        // BOM の追加 / 削除はインクリメンタル再索引で引き続き検知できる。streaming
        // ヘルパは UTF-8 文字列を再エンコードせずに済み、大ファイルで約 10 MB 節約する。
        // Closes #1544.
        var checksum = ComputeChecksum(bytes);

        string content;
        string? warning = null;
        // BOM-based encoding detection: UTF-16 LE/BE source files are otherwise mangled
        // by the UTF-8 decoder (every other byte is U+FFFD or NUL), silently dropping
        // every symbol they declare. UTF-32 LE shares the first two bytes with UTF-16 LE
        // (FF FE [00 00]), so we exclude that prefix from the UTF-16 LE path rather than
        // misclassify a UTF-32 LE file as UTF-16 LE with embedded NULs. Closes #1540.
        // BOM ベースのエンコーディング検出: UTF-16 LE/BE のソースファイルは UTF-8
        // デコーダで毎バイト U+FFFD / NUL に変換され、ファイル内のシンボルが丸ごと
        // 消える。UTF-32 LE は UTF-16 LE と先頭 2 バイトを共有する (FF FE [00 00])
        // ため、UTF-16 LE 経路から除外し UTF-8 fallback に流す。Closes #1540.
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            content = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false)
                .GetString(bytes);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
                 && !(bytes.Length >= 4 && bytes[2] == 0x00 && bytes[3] == 0x00))
        {
            content = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false)
                .GetString(bytes);
        }
        else
        {
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
        }
        // Normalize line endings to LF / 改行をLFに正規化
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");
        // Strip every line-leading UTF-8 BOM (U+FEFF): the leading BOM at offset 0
        // and any BOM that immediately follows a `\n` (e.g. from accidental file
        // concatenation or tool insertion). Leading BOM alone would make `^\s*`-
        // anchored regexes silently miss line-1 declarations in BOM-prefixed
        // Windows-authored sources; a BOM at the start of a mid-file line would
        // additionally leak a phantom glyph through `search` / `excerpt` chunk
        // output. Non-line-leading U+FEFF (Unicode 3.2+ ZWNBSP inside a string
        // literal, identifier, or comment — e.g. `const s = "A\uFEFFB"`) is kept
        // verbatim: stripping it would corrupt content fidelity for intentional
        // mid-line ZWNBSP use. The checksum computed above keeps BOM bytes in the
        // hash input (only CRLF / CR are collapsed to LF) so incremental re-index
        // still detects BOM add / removal. Closes #183. Cross-OS CRLF / LF parity
        // is handled by ComputeChecksum itself; see #1544.
        // 行頭の UTF-8 BOM (U+FEFF) だけを剥がす — オフセット 0 の先頭 BOM と、
        // `\n` の直後にある BOM (ファイル連結やツール挿入で発生) が対象。先頭 BOM
        // だけでも行指向の `^\s*` 固定正規表現で BOM 付き Windows 作成ソースの
        // 1 行目宣言を黙って取りこぼし、mid-file 行頭 BOM は `search` / `excerpt`
        // のチャンク出力にそのまま幽霊グリフを漏らす。行頭以外の U+FEFF
        // (Unicode 3.2+ の ZWNBSP を文字列リテラル・識別子・コメントで意図的に
        // 使用しているケース、例: `const s = "A\uFEFFB"`) はそのまま保持する:
        // これを剥がすと mid-line ZWNBSP の意図的利用に対して内容が壊れる。
        // checksum は上で算出済みで、ハッシュ入力に BOM をそのまま含めたまま
        // CRLF / CR のみを LF に潰すため、BOM の追加 / 削除はインクリメンタル
        // 再索引で引き続き検知される。Closes #183。OS をまたいだ CRLF / LF の
        // 同一性は ComputeChecksum 自体で担保する。#1544 参照。
        content = StripLineLeadingBom(content);
        // Accurate line count: ignore trailing newline, and treat content that became
        // empty after CRLF / BOM stripping as zero lines (not `"".Split('\n') == [""]`'s
        // off-by-one of 1) so `files.lines` stays consistent with the 0-chunk contract
        // ChunkSplitter.Split applies to the same content. Closes #183.
        // 正確な行数: 末尾改行を無視し、CRLF / BOM 剥がしの結果として空になった
        // コンテンツは 0 行として扱う (`"".Split('\n') == [""]` の 1 件ずれを避ける)。
        // これにより `files.lines` が ChunkSplitter.Split が同じ内容に対して適用する
        // 0 チャンク契約と整合する。Closes #183.
        var lineCount = content.Length == 0
            ? 0
            : (content.EndsWith('\n')
                ? content[..^1].Split('\n').Length
                : content.Split('\n').Length);

        var record = new FileRecord
        {
            Path = NormalizePathSeparators(relativePath),
            Lang = TryDetectLanguage(absolutePath, content).Language,
            Size = sizeBytes,
            Lines = lineCount,
            Checksum = checksum,
            Modified = modifiedUtc,
        };

        return (record, content, bytes, warning);
    }

    /// <summary>
    /// Strip every line-leading UTF-8 BOM (U+FEFF). Assumes CRLF has already been
    /// normalized to LF so `\n` is the sole line separator. Preserves non-line-
    /// leading U+FEFF verbatim (intentional ZWNBSP inside string literals etc.).
    /// 行頭の UTF-8 BOM (U+FEFF) のみ剥がす。呼び出し前に CRLF が LF へ
    /// 正規化済みであることを前提とする。行頭以外の U+FEFF (文字列リテラル等で
    /// 意図的に使われる ZWNBSP) はそのまま保持する。
    /// </summary>
    internal static string StripLineLeadingBom(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;
        // Fast path: BOM-free files (the dominant case) skip the line-tracking
        // scan and return the input unchanged so no StringBuilder is allocated.
        // 高速パス: BOM が一切無いファイル (支配的ケース) は行頭追跡走査を回避し、
        // 入力をそのまま返して StringBuilder を確保しない。
        if (!content.Contains('\uFEFF'))
            return content;

        // U+FEFF is present somewhere. Locate the first *line-leading* BOM in a
        // single pass; if every occurrence is mid-line (intentional ZWNBSP
        // inside string literals etc.), return the input unchanged so the
        // no-strip path also avoids any allocation.
        // U+FEFF が含まれている。最初の「行頭」 BOM を 1 パスで探し、行頭以外
        // のみ (文字列リテラル内の意図的な ZWNBSP 等) であれば入力をそのまま
        // 返し、「剥がし不要」のケースでも割り当てを発生させない。
        int firstStripIndex = -1;
        bool atLineStart = true;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '\uFEFF' && atLineStart)
            {
                firstStripIndex = i;
                break;
            }
            atLineStart = c == '\n';
        }
        if (firstStripIndex < 0)
            return content;

        // Allocate only after at least one line-leading BOM is confirmed. The
        // capacity accounts for stripping at least the BOM at firstStripIndex.
        // 少なくとも 1 つの行頭 BOM が確定した時点で初めて確保する。容量は
        // firstStripIndex の BOM を必ず剥がす分を差し引いた値にしておく。
        var sb = new StringBuilder(content.Length - 1);
        if (firstStripIndex > 0)
            sb.Append(content, 0, firstStripIndex);
        // The char at firstStripIndex is itself a line-leading BOM; resume
        // after it with atLineStart = true so consecutive BOMs at the same
        // logical position (e.g. multiple BOMs right after `\n`) are stripped.
        // firstStripIndex の文字自体が行頭 BOM。直後から再開し atLineStart を
        // true に戻すことで `\n` 直後に連続する BOM も同じ論理位置として剥がす。
        atLineStart = true;
        for (int i = firstStripIndex + 1; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '\uFEFF' && atLineStart)
                continue;
            sb.Append(c);
            atLineStart = c == '\n';
        }
        return sb.ToString();
    }

    /// <summary>
    /// Validate file content for encoding issues.
    /// ファイル内容のエンコーディング問題を検証する。
    /// </summary>
    public static List<FileIssue> ValidateContent(string relativePath, byte[] rawBytes, string content)
    {
        var issues = new List<FileIssue>();

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
        var hasUtf16BeBom = rawBytes.Length >= 2 && rawBytes[0] == 0xFE && rawBytes[1] == 0xFF;
        var hasUtf16LeBom = rawBytes.Length >= 2 && rawBytes[0] == 0xFF && rawBytes[1] == 0xFE
            && !(rawBytes.Length >= 4 && rawBytes[2] == 0x00 && rawBytes[3] == 0x00);
        var isUtf16 = hasUtf16BeBom || hasUtf16LeBom;

        if (isUtf16)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "utf16_bom",
                Line = 1,
                Message = hasUtf16BeBom
                    ? "UTF-16 BE BOM detected (decoded as UTF-16)"
                    : "UTF-16 LE BOM detected (decoded as UTF-16)",
            });
        }

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
        var nonUtf8Likely = fffdCount >= NonUtf8LikelyMinCount
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
            });
        }

        // U+FFFD replacement characters baked into the file / ファイルに焼き付いたU+FFFD置換文字
        // Skip the per-line emission when `non_utf8_likely` already fired so a mojibake file
        // does not produce hundreds of near-duplicate `replacement_char` issues.
        // `non_utf8_likely` が出た場合は重複を抑え 1 件のアグリゲートに集約する。
        if (!nonUtf8Likely)
        {
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
        }

        // Raw-byte heuristics: skip for UTF-16-decoded files because every UTF-16 LE ASCII
        // codepoint looks like a NUL byte and CRLF appears as 0D 00 0A 00, so `bom` /
        // `null_byte` / `mixed_line_endings` / `cr_only_line_endings` would all misfire.
        // UTF-16 デコード経路では生バイト列が NUL バイトと 0D 00 0A 00 で埋まり、`bom` /
        // `null_byte` / `mixed_line_endings` / `cr_only_line_endings` がすべて誤検出する
        // ためスキップする。
        if (!isUtf16)
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

        return issues;
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
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[4096];
        int n = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b == 0x0D)
            {
                buffer[n++] = 0x0A;
                if (i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
                    i++;
            }
            else
            {
                buffer[n++] = b;
            }
            if (n == buffer.Length)
            {
                hasher.AppendData(buffer);
                n = 0;
            }
        }
        if (n > 0)
            hasher.AppendData(buffer[..n]);
        Span<byte> hash = stackalloc byte[32];
        if (!hasher.TryGetHashAndReset(hash, out var written) || written != hash.Length)
            throw new InvalidOperationException("SHA256 produced an unexpected hash length");
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Try to infer a language from an extensionless script shebang.
    /// This is a cheap fallback used only after extension and exact-filename checks fail.
    /// 拡張子・完全一致ファイル名で判定できない場合だけ、拡張子なしスクリプトの shebang から言語を推定する。
    /// </summary>
    private static LanguageDetectionResult TryDetectLanguageFromShebang(string filePath)
    {
        if (GetFileIndexability(filePath) != FileProbeStatus.Supported)
            return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

        try
        {
            using var stream = File.OpenRead(LongPath.EnsureWindowsPrefix(filePath));
            if (!stream.CanRead)
                return new LanguageDetectionResult(FileProbeStatus.ProbeFailed, null);

            Span<byte> buffer = stackalloc byte[256];
            var bytesRead = stream.Read(buffer);
            if (bytesRead <= 0)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var firstLine = Encoding.UTF8.GetString(buffer[..bytesRead])
                .Split(['\r', '\n'], 2)[0];

            if (firstLine.StartsWith('\uFEFF'))
                firstLine = firstLine[1..];

            if (!firstLine.StartsWith("#!", StringComparison.Ordinal))
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var commandLine = firstLine[2..].Trim();
            if (string.IsNullOrWhiteSpace(commandLine))
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var tokens = commandLine
                .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var interpreter = ResolveShebangInterpreter(tokens);
            if (interpreter == null)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var language = MapShebangInterpreterToLanguage(interpreter);
            return language != null
                ? new LanguageDetectionResult(FileProbeStatus.Supported, language)
                : new LanguageDetectionResult(FileProbeStatus.Unsupported, null);
        }
        catch (IOException)
        {
            return new LanguageDetectionResult(FileProbeStatus.ProbeFailed, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new LanguageDetectionResult(FileProbeStatus.ProbeFailed, null);
        }
    }

    private static string? ResolveShebangInterpreter(IReadOnlyList<string> tokens)
    {
        var interpreter = Path.GetFileName(tokens[0]).ToLowerInvariant();
        if (interpreter is not "env")
            return interpreter;

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.StartsWith("-", StringComparison.Ordinal))
                continue;

            // `env FOO=bar python` style assignments before the real interpreter.
            // `env FOO=bar python` のような代入はスキップして本体の interpreter を探す。
            if (token.Contains('='))
                continue;

            return Path.GetFileName(token).ToLowerInvariant();
        }

        return null;
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
