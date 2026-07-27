namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    public const int DefaultContractVersion = 1;
    public const int ExpandedLanguageContractVersion = 2;
    public const int PythonContractVersion = 2;
    public const int CSharpContractVersion = 6;
    public const int DockerfileContractVersion = 2;
    public const int MakefileContractVersion = 2;
    public const int StyleAndXamlContractVersion = 2;
    public const int XmlContractVersion = 3;
    public const int FunctionalLanguageContractVersion = 3;
    public const int DynamicLanguageContractVersion = 2;
    public const int DynamicReferenceGraphContractVersion = 8;
    public const int PrologReferenceGraphContractVersion = 7;
    public const int SystemsLanguageContractVersion = 2;
    public const int ScientificNativeGraphContractVersion = 4;
    public const int RepositoryMetadataContractVersion = 2;
    public const int ApplicationManifestContractVersion = 3;

    private static readonly string[] ExplicitReferenceGraphContractLanguages =
        ["crystal", "groovy", "tcl", "prolog", "ambiguous_pl"];

    private static readonly string[] AdditionalSymbolLanguages =
    [
        "app_manifest",
        "commonlisp",
        "racket",
        "vue",
        "svelte",
        "markdown",
        "json",
        "yaml",
        "xml",
        "razor",
        "blazor",
        "cshtml",
        "solidity",
        "solution",
        "cuda",
        "ambiguous_m",
        "dependency_manifest",
        "dependency_lock",
        "jsonl",
        "toml",
        "gitignore",
        "gitattributes",
        "editorconfig",
        "dockerignore",
        "config",
    ];

    public static int GetContractVersion(string? lang)
    {
        return lang switch
        {
            null or "" => DefaultContractVersion,
            "python" => PythonContractVersion,
            "csharp" => CSharpContractVersion,
            "dockerfile" => DockerfileContractVersion,
            "makefile" => MakefileContractVersion,
            "sass" or "stylus" => StyleAndXamlContractVersion,
            "xml" => XmlContractVersion,
            "clojure" or "erlang" or "ocaml" or "raku" => FunctionalLanguageContractVersion,
            "crystal" or "groovy" or "tcl" => DynamicReferenceGraphContractVersion,
            "prolog" or "ambiguous_pl" => PrologReferenceGraphContractVersion,
            "ada" or "ambiguous_m" or "cython" or "d" or "julia" or "matlab" or "nim" or "objc" => ScientificNativeGraphContractVersion,
            "config" or "dockerignore" or "editorconfig" or "gitattributes" or "gitignore" or "jsonl" or "toml" => RepositoryMetadataContractVersion,
            "app_manifest" => ApplicationManifestContractVersion,
            "cmake" or "dependency_lock" or "dependency_manifest" or "graphql" or "html" or "json" or "justfile" or "markdown" or "msbuild" or "solution" or "yaml" => ExpandedLanguageContractVersion,
            _ => DefaultContractVersion,
        };
    }

    internal static IReadOnlyList<string> GetExplicitReferenceGraphContractLanguages() =>
        ExplicitReferenceGraphContractLanguages;

    internal static bool RequiresExplicitReferenceGraphContractStamp(string? lang) =>
        lang != null
        && ExplicitReferenceGraphContractLanguages.Contains(lang, StringComparer.Ordinal);

    internal static int GetReferenceGraphContractVersion(string lang) =>
        GetContractVersion(lang);
}
