using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Extracts symbols (functions, classes, imports) using regex patterns.
/// 正規表現を使ってシンボル（関数、クラス、インポート）を抽出する。
/// </summary>
public static partial class SymbolExtractor
{
    public const int DefaultContractVersion = 1;
    public const int ExpandedLanguageContractVersion = 2;
    public const int PythonContractVersion = 2;
    public const int CSharpContractVersion = 5;
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

    private const int SymbolListInitialCapacityLineThreshold = 128;
    private const int SymbolListInitialCapacityMax = 1024;
    private const string JuliaIdentifierPattern = @"[\p{L}_]\w*";
    private const string JuliaQualifiedCallableIdentifierPattern =
        JuliaIdentifierPattern + @"(?:\." + JuliaIdentifierPattern + @")*!?";

    private static string[] SplitContentLines(string content) =>
        content.IndexOf('\n', StringComparison.Ordinal) < 0 ? [content] : content.Split('\n');

    private static List<SymbolRecord> CreateSymbolListForLines(int lineCount)
    {
        var initialCapacity = EstimateSymbolListInitialCapacity(lineCount);
        return initialCapacity == 0
            ? []
            : new List<SymbolRecord>(initialCapacity);
    }

    private static int EstimateSymbolListInitialCapacity(int lineCount)
    {
        if (lineCount < SymbolListInitialCapacityLineThreshold)
            return 0;

        return Math.Min(SymbolListInitialCapacityMax, Math.Max(16, lineCount / 8));
    }

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

    private static IReadOnlyList<SymbolRecord> BuildEnumDeclarationSnapshot(IReadOnlyList<SymbolRecord> symbols, long? fileId = null)
    {
        List<(SymbolRecord Symbol, int OriginalIndex)>? candidates = null;
        for (var index = 0; index < symbols.Count; index++)
        {
            var symbol = symbols[index];
            if (fileId is { } requestedFileId && symbol.FileId != requestedFileId)
                continue;

            if (symbol.Kind == "enum" && symbol.BodyStartLine != null && symbol.BodyEndLine != null)
                (candidates ??= []).Add((symbol, index));
        }

        if (candidates is null)
            return Array.Empty<SymbolRecord>();

        if (candidates.Count == 1)
            return [candidates[0].Symbol];

        candidates.Sort(static (left, right) =>
        {
            var comparison = left.Symbol.StartLine.CompareTo(right.Symbol.StartLine);
            if (comparison != 0)
                return comparison;

            comparison = right.Symbol.EndLine.CompareTo(left.Symbol.EndLine);
            if (comparison != 0)
                return comparison;

            return left.OriginalIndex.CompareTo(right.OriginalIndex);
        });

        var snapshot = new SymbolRecord[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            snapshot[i] = candidates[i].Symbol;
        return snapshot;
    }

    // THREAD-SAFETY: Symbol extraction is intentionally stateless per call. Shared Regex
    // instances and lookup tables are initialized once by the CLR and must be treated as
    // immutable after type initialization; per-file extraction state belongs in local
    // variables or per-call collections, never in static mutable caches.
    private const string SqlQualifiedIdentifierSegmentPattern = @"(?:\[(?:[^\]\r\n]|\]\])+\]|""[^""]+""|[\w$#]+)";
    private const string SqlQualifiedIdentifierPattern =
        @"(?:" + SqlQualifiedIdentifierSegmentPattern + @")(?:\s*\.\s*(?:" + SqlQualifiedIdentifierSegmentPattern + @"))*";
    // Swift declarations commonly carry attributes on the same line as the declaration keyword.
    // Allow those prefixes so annotated declarations still index by their actual names.
    // Swift の宣言では、宣言キーワードと同じ行に属性が付くことが多い。
    // その前置きを許容し、注釈付き宣言でも実際の名前でインデックスできるようにする。
    private const string SwiftAttributeNamePattern = @"\w+(?:\.\w+)*";
    private const string SwiftAttributePattern = @"(?:@" + SwiftAttributeNamePattern + @"(?:\([^)]*\))?\s+)*";
    private static readonly Regex SwiftPropertyDeclarationRegex = new(
        @"^\s*(?<attributes>(?:@" + SwiftAttributeNamePattern + @"(?:\([^)]*\))?\s+)*)?(?:(?:public|private|internal|open|fileprivate|package)(?:\s*\(\s*set\s*\))?\s+)?(?:(?:lazy|weak|unowned|final|static|class|nonisolated)\s+)*(?:let|var)\s+(?<name>`[^`]+`|\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SwiftPropertyWrapperAttributeRegex = new(
        @"@(?<name>[A-Z]\w*(?:\.[A-Z]\w*)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SwiftAccessorDeclarationRegex = new(
        @"^\s*" + SwiftAttributePattern + @"(?:(?:mutating|nonmutating)\s+)?(?:@(?=willSet\b|didSet\b))?(?<name>get|set|willSet|didSet)\b(?:\s*\([^)]*\))?(?:\s+(?:async|throws|rethrows))*\s*(?:\{|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SwiftNonWrapperPropertyAttributes = new(StringComparer.Ordinal)
    {
        "IBOutlet",
        "IBOutletCollection",
        "IBInspectable",
        "NSManaged",
        "GKInspectable",
    };
    // C++ return-type atoms need to accept both ordinary word tokens and `decltype(...)`.
    // The decltype branch allows nested parentheses so modern forms such as
    // `decltype(auto)`, `decltype((value))`, and `decltype(foo<T>(x))` stay searchable.
    // C++ の戻り値型トークンは通常の単語トークンに加え `decltype(...)` も受け入れる必要がある。
    // ここで括弧の入れ子を許容し、`decltype(auto)` / `decltype((value))` /
    // `decltype(foo<T>(x))` のような現代的な形も検索可能なままにする。
    private const string CppDecltypePattern =
        @"decltype\s*\((?:(?>[^()]+)|\((?<CppDecltypeDepth>)|\)(?<-CppDecltypeDepth>))*(?(CppDecltypeDepth)(?!))\)";
    private const string CppFunctionReturnTypeAtomPattern = @"(?:" + CppDecltypePattern + @"|[\w:<>~]+)";
    // GCC/Clang/MSVC attribute specifiers can appear before the return type or between return
    // type tokens. Keep them inside the function return-type matcher so common annotated C
    // functions still surface in `symbols` / `search`.
    // GCC/Clang/MSVC の attribute specifier は戻り値型の前や、戻り値型トークンの途中に現れる。
    // それらを戻り値型マッチャーに含めて、よくある注釈付き C 関数も `symbols` / `search` に出るようにする。
    private const string CAttributeSpecifierTokenPattern =
        @"(?:\[\[[^\r\n]*?\]\]\s*|__attribute__\s*\(\((?:(?>[^()]+)|\((?<CAttributeDepth>)|\)(?<-CAttributeDepth>))*(?(CAttributeDepth)(?!))\)\)\s*|__declspec\s*\((?:(?>[^()]+)|\((?<CAttributeDepth>)|\)(?<-CAttributeDepth>))*(?(CAttributeDepth)(?!))\)\s*|_Noreturn\s+)";
    private const string CFunctionReturnTypePattern =
        @"(?<returnType>(?:(?:\w+[\s*]+)|" + CAttributeSpecifierTokenPattern + @")+)";
    private const string JavaUnicodeEscapePattern = @"\\u+[0-9A-Fa-f]{4}";
    private const string JavaIdentifierPattern =
        @"(?:[\p{L}_$]|" + JavaUnicodeEscapePattern + @")(?:[\p{L}\p{Nd}_$]|" + JavaUnicodeEscapePattern + @")*";
    private const string JavaQualifiedIdentifierPattern = JavaIdentifierPattern + @"(?:\s*\.\s*" + JavaIdentifierPattern + @")*";
    private const string JavaMethodTypeParameterPattern =
        @"(?:<(?:(?>[^<>]+)|<(?<JavaMethodTypeParameterDepth>)|>(?<-JavaMethodTypeParameterDepth>))*(?(JavaMethodTypeParameterDepth)(?!))>\s+)?";
    private const string JavaReturnTypePattern =
        @"(?:" + JavaQualifiedIdentifierPattern + @"(?:\s*<[^;=(){}]+>)?(?:\s*\[\s*\])*)";
    private const string KotlinIdentifierPattern = @"(?:\w+|`[^`\r\n]+`)";
    private const string CythonIdentifierPattern = @"[A-Za-z_]\w*";
    private const string CythonDottedIdentifierPattern = CythonIdentifierPattern + @"(?:\." + CythonIdentifierPattern + @")*";
    private const string CythonDeclarationPrefixPattern = @"(?:(?:public|readonly|api|inline|extern|nogil|const|volatile)\s+)*";
    private const string CythonNativeReturnTypePattern =
        @"(?<returnType>(?:(?:const|volatile|unsigned|signed|long|short|int|double|float|char|void|bint|object|Py_ssize_t|size_t|" + CythonDottedIdentifierPattern + @")(?:\s*[*&])?\s+)+)";
    private const string HdlIdentifierPattern = @"[A-Za-z_$][A-Za-z0-9_$]*";
    private const string HdlDeclaratorPrefixPattern =
        @"(?:(?:signed|unsigned|automatic|static|wire|reg|logic|bit|byte|shortint|int|longint|integer|time|real|realtime|string|chandle|event)\s+|\[[^\]\r\n]+\]\s+)*";
    private const string VhdlIdentifierPattern = @"[A-Za-z][A-Za-z0-9_]*";
    private static readonly Regex HdlInlineParameterRegex = new(
        @"\b(?:parameter|localparam)\s+(?:type\s+)?" + HdlDeclaratorPrefixPattern + @"(?<name>" + HdlIdentifierPattern + @")\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string ShaderIdentifierPattern = @"[A-Za-z_]\w*";
    private const string ShaderTypePattern = @"[\w:<>,]+(?:\s*[*&])?(?:\s*\[[^\]\r\n]+\])?";
    private const string ShaderAttributePrefixPattern = @"(?:(?:layout\s*\([^)]*\)|\[[^\]\r\n]+\]|@\w+(?:\([^)]*\))?)\s*)*";
    private const string ShaderFunctionStartBlacklistPattern = @"^(?!\s*(?:if|for|while|switch|return|discard)\b)";
    private static readonly Regex RPacmanPackageLoaderStartRegex = new(
        @"^\s*(?:(?:[\w.]+)::)?p_load\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex RPacmanPackageLoaderArgumentRegex = new(
        @"(?:^|,)\s*(?!(?:[A-Za-z.][\w.]*\s*=))(?:['""](?<quotedName>[^'""]+)['""]|(?<name>[A-Za-z.][\w.]*))",
        RegexOptions.Compiled);
    private static readonly Regex CobolProgramIdLineRegex = new(
        @"^\s*(?:IDENTIFICATION\s+DIVISION\.\s*)?(?:PROGRAM|CLASS)-ID\.\s*(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolProcedureDivisionRegex = new(
        @"^\s*PROCEDURE\s+DIVISION\.\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolEntryRegex = new(
        @"^\s*ENTRY\s+(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolSectionHeaderRegex = new(
        @"^\s{0,6}(?<name>[A-Z0-9][A-Z0-9-]*)\s+SECTION\.\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolParagraphHeaderRegex = new(
        @"^\s{0,6}(?<name>[A-Z0-9][A-Z0-9-]*)\.\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolEndProgramRegex = new(
        @"^\s*END\s+(?:PROGRAM|CLASS)(?:\s+(?<name>[A-Z0-9][A-Z0-9-]*))?\.\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PhpGroupUseRegex = new(
        @"^\s*use\s+(?:(?<type>function|const)\s+)?(?<prefix>[\w\\]+\\)\{\s*(?<items>[^{}]+?)\s*\}\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PhpUseRegex = new(
        @"^\s*use\s+(?:(?<type>function|const)\s+)?(?<name>[\w\\]+)(?:\s+as\s+(?<alias>\w+))?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PhpRequireIncludeRegex = new(
        @"^\s*(?:require|include)(?:_once)?\s*\(?\s*(?:'(?<singleName>[^']+)'|""(?<doubleName>[^""]+)"")\s*\)?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PhpPrefixedRequireIncludeRegex = new(
        @"^\s*(?:require|include)(?:_once)?\s*\(?\s*(?<prefix>(?:(?:__DIR__|__FILE__|dirname\s*\(\s*__FILE__\s*\))\s*\.\s*)+)\s*(?:'(?<singleName>[^']+)'|""(?<doubleName>[^""]+)"")\s*\)?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const string CFunctionStartBlacklistPattern = @"^(?!\s*typedef\b)(?!\s*(?:if|else|for|while|switch|return|sizeof)\s*[\(\{;])";
    private const string CFunctionNameBlacklistPattern = @"(?!(?:int|void|char|short|long|float|double|signed|unsigned|bool|_Bool|size_t|ssize_t|intptr_t|uintptr_t|int8_t|int16_t|int32_t|int64_t|uint8_t|uint16_t|uint32_t|uint64_t)\b)";
    private const string CppFunctionStartBlacklistPattern = @"^(?!\s*typedef\b)(?!\s*(?:if|else|for|while|switch|return|sizeof|using|namespace)\s*[\(\{;<])";
    private const string CppTemplatePrefixPattern = @"(?:template\s*<[^>]*>\s*)*";
    private const string CppAttributePrefixPattern = @"(?:\[\[[^\r\n]*?\]\]\s*)*";
    private static readonly Regex CppFriendTypeDeclarationRegex = new(
        @"\bfriend\s+(?<kind>class|struct|union|enum(?:\s+class)?)\s+(?<name>(?:[A-Za-z_]\w*::)*[A-Za-z_]\w*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppFriendFunctionDeclarationRegex = new(
        @"\bfriend\s+(?!(?:class|struct|union|typename|enum)\b)(?<returnType>[^;()]*?)\b(?<name>(?:[A-Za-z_]\w*::)*(?:[A-Za-z_]\w*|operator\s*(?:new\[\]|delete\[\]|new|delete|\[\]|[^\s(]+)))(?:\s*<[^>]+>)?\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PartialModifierRegex = new(@"\bpartial\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GoImportSpecRegex = new(
        @"^(?<name>(?:(?:[._]|[\p{L}_][\p{L}\p{Nd}_]*)\s+)?""(?:\\.|[^""\\])*"")(?:\s*;)?(?:\s*(?://.*|/\*.*\*/))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoTypeBlockSpecRegex = new(
        @"^(?<name>\w+)(?:\[[^\]]+\])?\s+(?:(?<kind>struct|interface)\b|.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoInterfaceHeaderRegex = new(
        @"^\s*(?:type\s+)?\w+(?:\[[^\]]+\])?\s+interface\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoInterfaceMethodRegex = new(
        @"^\s*(?<name>[A-Za-z_]\w*)\s*(?:\[[^\]\r\n]+\])?\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoInterfaceEmbeddedTypeRegex = new(
        @"^\s*(?:~\s*)?(?<name>[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)(?:\[[^\]\r\n]+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoStructEmbeddedTypeRegex = new(
        @"^\s*\*?\s*(?<name>[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)(?:\[[^\]\r\n]+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> GoInterfaceEmbeddedTypeBlacklist = new(StringComparer.Ordinal)
    {
        "bool",
        "byte",
        "complex64",
        "complex128",
        "float32",
        "float64",
        "int",
        "int8",
        "int16",
        "int32",
        "int64",
        "rune",
        "string",
        "uint",
        "uint8",
        "uint16",
        "uint32",
        "uint64",
        "uintptr",
    };
    private static readonly Regex GoValueBlockSpecRegex = new(
        @"^(?<names>[A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoLabelRegex = new(
        @"^(?<name>[A-Za-z_]\w*)\s*:\s*(?!=)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RustUseStartRegex = new(
        @"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?use\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly record struct RustUseSymbolOccurrence(string Name, int Line, int Column);
    private const string RustIdentifierPattern = @"(?:r#)?\w+";
    private static readonly Regex RustMultilineImplForRegex = new(
        @"^\s*(?:unsafe\s+)?impl(?:<[^>]+>)?\s+.+?\s+for\s+(?<name>" + RustIdentifierPattern + @")\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex RustMultilineImplTypeRegex = new(
        @"^\s*(?:unsafe\s+)?impl(?:<[^>]+>)?\s+(?<name>" + RustIdentifierPattern + @")(?!\s+for\b)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex DockerfileNamedFromImageRegex = new(
        @"^\s*FROM\s+(?:--platform=\S+\s+)?(?<name>\S+)\s+(?:AS|as)\s+[A-Za-z0-9_.-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex XamlClassRegex = new(
        @"\bx:Class\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XamlDataTypeRegex = new(
        @"\bx:DataType\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XamlTypeArgumentsRegex = new(
        @"\bx:TypeArguments\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XamlTargetTypeRegex = new(
        @"\bTargetType\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XamlTypeObjectElementRegex = new(
        @"<\s*x:Type(?:Extension)?\b[^>]*\bTypeName\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex XamlTypePropertyElementRegex = new(
        @"<\s*(?<owner>x:Type(?:Extension)?)\.TypeName\b[^>]*>(?<value>.*?)</\s*\k<owner>\.TypeName\s*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex XamlNameRegex = new(
        @"\bx:Name\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XamlKeyRegex = new(
        @"\bx:Key\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] XamlEventAttributeNames =
    [
        "Clicked",
        "Tapped",
        "Loaded",
        "Unloaded",
        "SelectionChanged",
        "TextChanged",
        "CheckedChanged",
        "Unchecked",
        "SelectedIndexChanged",
        "PointerPressed",
        "PointerReleased",
        "PointerEntered",
        "PointerExited",
        "Drop",
        "DragOver",
        "Completed",
        "Appearing",
        "Disappearing",
        "NavigatedTo",
        "NavigatedFrom",
        "SizeChanged",
    ];
    private static readonly Regex XamlEventHandlerRegex = new(
        @"\b(?:" + string.Join("|", XamlEventAttributeNames) + @")\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XamlBindingRegex = new(
        @"\{(?<kind>Binding|x:Bind|TemplateBinding|CompiledBinding|ReflectionBinding)\b(?<content>(?:[^{}]|{[^{}]*})*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XamlBindingPathPropertyElementRegex = new(
        @"<\s*Binding\.Path\b[^>]*>(?<value>.*?)</\s*Binding\.Path\s*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex XamlBindingElementNamePropertyElementRegex = new(
        @"<\s*Binding\.ElementName\b[^>]*>(?<value>.*?)</\s*Binding\.ElementName\s*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex XamlReferenceNamePropertyElementRegex = new(
        @"<\s*(?<owner>x:Reference(?:Extension)?)\.Name\b[^>]*>(?<value>.*?)</\s*\k<owner>\.Name\s*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly string[] XamlResourceReferenceMarkupPrefixes =
    [
        "{StaticResource",
        "{StaticResourceExtension",
        "{DynamicResource",
        "{DynamicResourceExtension",
    ];
    private static readonly string[] XamlReferenceMarkupPrefixes =
    [
        "{x:ReferenceExtension",
        "{x:Reference",
    ];
    private static readonly string[] XamlReferenceObjectElementPrefixes =
    [
        "<x:ReferenceExtension",
        "<x:Reference",
    ];
    private static readonly Regex ObjCCategoryDeclarationRegex = new(
        @"^\s*@(?:interface|implementation)\s+(?<class>\w+)\s*\(\s*(?<category>[^)]+?)\s*\)(?:\s*<[^>]+>)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SqlDefinerRegex = new(
        @"\bDEFINER\s*=\s*(?:'(?<user1>[^'\r\n]+)'|`(?<user2>[^`\r\n]+)`|(?<user3>[^\s@'`]+))\s*@\s*(?:'(?<host1>[^'\r\n]+)'|`(?<host2>[^`\r\n]+)`|(?<host3>[^\s'`]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SqlDefinerMarkerRegex = new(
        @"\bDEFINER\s*=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SqlCteDefinitionRegex = new(
        $@"(?<![\w$])(?:WITH\s+(?:RECURSIVE\s+)?|,\s*)(?<name>{SqlQualifiedIdentifierSegmentPattern})(?:\s*\([^)]*\))?\s+AS\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SqlAlterTableAddGeneratedColumnRegex = new(
        $@"(?<![\w$])ALTER\s+TABLE\s+(?<table>{SqlQualifiedIdentifierPattern})\s+ADD(?:\s+COLUMN)?\s+(?!CONSTRAINT\b)(?<name>{SqlQualifiedIdentifierSegmentPattern})\b(?=[^;]*?\b(?:GENERATED\s+(?:ALWAYS\s+)?AS|AS\s*\(|DEFAULT\s+NEXT\s+VALUE\s+FOR)\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SqlCreateTableBodyRegex = new(
        $@"(?<![\w$])CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:(?:(?:GLOBAL|LOCAL)\s+)?(?:TEMP|TEMPORARY)\s+|UNLOGGED\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<table>{SqlQualifiedIdentifierPattern})\s*\((?<body>[\s\S]*?)\)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SqlGeneratedColumnDefinitionMarkerRegex = new(
        @"\b(?:GENERATED\s+(?:ALWAYS\s+)?AS|AS\s*\(|DEFAULT\s+NEXT\s+VALUE\s+FOR)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SqlColumnDefinitionNameRegex = new(
        $@"^\s*(?<name>{SqlQualifiedIdentifierSegmentPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SqlReturnsTableMarkerRegex = new(
        @"\bRETURNS\s+TABLE\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SqlOutParameterRegex = new(
        @"(?:^|,)\s*(?:OUT|INOUT)\s+(?<name>(?:\[(?:[^\]\r\n]|\]\])+\]|`[^`\r\n]+`|""(?:""""|[^""\r\n])+""|[_\p{L}][\p{L}\p{Mn}\p{Mc}\p{Nd}\p{Pc}$]*))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SqlCreateRoutineHeaderRegex = new(
        @"^\s*CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:DEFINER\s*=\s*(?:'[^'\r\n]+'|`[^`\r\n]+`|[^\s@'`]+)\s*@\s*(?:'[^'\r\n]+'|`[^`\r\n]+`|[^\s'`]+)\s+)?(?:PROCEDURE|PROC|FUNCTION)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Optional TypeScript generic type-argument token that may sit between an HOC call
    // name and its `(`. Consumed only by the TypeScript HOC-binding row — the JavaScript
    // row intentionally does NOT accept this token, because JavaScript has no generic
    // syntax and a bare `memo < Props > (Component)` is a chained comparison / call
    // expression that must NOT produce a phantom HOC binding. The expression balances up
    // to three levels of nested angle brackets (`<Record<string, Map<string, Props>>>`)
    // and allows parenthesised segments (`<(props: Props) => JSX.Element>`) inside a
    // generic argument, which covers the function-type / conditional-type shapes real TS
    // HOC call sites use. Each parenthesised segment itself balances one level of nested
    // parens — `\((?:[^()]|\([^()]*\))*\)` — so callback-prop shapes such as
    // `<(props: { onClick: (x: number) => void }) => JSX.Element>` still match; the
    // inner `\([^()]*\)` branch is disjoint from `[^()]` (first char `(` vs not `(`), so
    // the paren balancer stays ReDoS-safe. The outer alternation treats `=>` as a single
    // two-character token via `=>?` (greedy `?` so the `>` is consumed when present)
    // instead of letting the `>` leak out and close the outer `<...>` early, which would
    // otherwise drop function-type generic arguments. Each alternation branch starts
    // with a distinct character class — `[^<>()=]` (plain), `=>?` (=-rooted), `\(`
    // (paren), `<` (nested angle) — so the engine never has overlapping choices at a
    // single input position, which rules out catastrophic backtracking on long or
    // malformed inputs. Four or more levels of angle-bracket nesting, or two or more
    // levels of paren nesting inside a single generic argument, are vanishingly rare in
    // real HOC signatures and would require a full bracket walker to stay ReDoS-safe.
    // Closes #240.
    // HOC 呼び出し名と `(` の間に入りうる、TypeScript の generic 型引数トークン（オプション）。
    // TypeScript 行の HOC 束縛だけがこのトークンを受け付け、JavaScript 行は意図的に
    // 受け付けない。JavaScript には generic 構文が無く、`memo < Props > (Component)` は
    // 比較・呼び出しの連鎖式であって、ここから phantom な HOC 束縛を生やしてはいけないため。
    // 式は 3 段までのネストした山括弧（`<Record<string, Map<string, Props>>>`）と、
    // generic 引数内の丸括弧付きセグメント（`<(props: Props) => JSX.Element>`）を許容する
    // ので、実在する TS HOC 呼び出しで使われる関数型・条件型形状までカバーできる。各
    // 丸括弧セグメント自身も 1 段のネスト丸括弧を許容する（`\((?:[^()]|\([^()]*\))*\)`）
    // ため、callback-prop 形
    // （`<(props: { onClick: (x: number) => void }) => JSX.Element>`）もマッチする。
    // 内側の `\([^()]*\)` 分岐は `[^()]` と先頭文字が互いに素（`(` vs それ以外）なので、
    // 丸括弧バランサーも ReDoS 安全に保たれる。外側 alternation は `=>` を `=>?` の 2
    // 文字トークンとして 1 度に消費する（greedy の `?` によって後続の `>` があれば必ず
    // 消費）。こうしないと `=>` の `>` が外側の山括弧閉じとして早期マッチしてしまい、
    // 関数型 generic 引数全体が落ちる。各 alternation 分岐は先頭文字クラスが互いに素
    // （`[^<>()=]`（平文字）、`=>?`（=-root）、`\(`（丸括弧）、`<`（ネスト山括弧））で、
    // 同一入力位置で選択が重ならないため、長い入力や不正な入力に対しても catastrophic
    // backtracking が発生しない。4 段以上の山括弧ネストや、単一 generic 引数内での 2 段
    // 以上の丸括弧ネストは実 HOC シグネチャでは極めて稀で、ReDoS 安全に受理するには完全
    // な bracket walker が必要になるため、それぞれ 3 段・1 段で打ち切る。#240 解消。
    private const string TypeScriptOptionalHocTypeArgsPattern = @"(?:<(?:[^<>()=]|=>?|\((?:[^()]|\([^()]*\))*\)|<(?:[^<>()=]|=>?|\((?:[^()]|\([^()]*\))*\)|<(?:[^<>()=]|=>?|\((?:[^()]|\([^()]*\))*\))*>)*>)*>\s*)?";
    // Optional TypeScript generic parameter list that may follow a `type` alias name.
    // Allow defaulted parameters (`T = string`) in addition to constraints and nested
    // type expressions so generic aliases stay searchable.
    // `type` エイリアス名の後に続く TypeScript の generic parameter list（オプション）。
    // `T = string` のような default 付き parameter に加え、constraint や入れ子の
    // type expression も許容して generic alias を検索対象に残す。
    private const string TypeScriptOptionalTypeParameterListPattern = @"(?:<(?:[^<>()=]|=(?!>)|=>?|\((?:[^()]|\([^()]*\))*\)|<(?:[^<>()=]|=(?!>)|=>?|\((?:[^()]|\([^()]*\))*\)|<(?:[^<>()=]|=(?!>)|=>?|\((?:[^()]|\([^()]*\))*\))*>)*>)*>\s*)?";

    private enum BodyStyle
    {
        None,
        Brace,
        Indent,
        RubyEnd,
        FortranEnd,
        ElixirEnd,
        ScientificEnd,
        JuliaShortFunction,
        VisualBasicEnd,
        PascalEnd,
        AdaEnd,
        SmalltalkMethod,
        SqlProcBody,
    }

    private sealed record SymbolPattern(
        string Kind,
        Regex Regex,
        BodyStyle BodyStyle,
        string? VisibilityGroup = null,
        string? ReturnTypeGroup = null);

    private enum CssContextKind
    {
        GroupingAtRule,
        QualifiedRule,
    }

    private enum JavaScriptLexMode
    {
        Code,
        SingleQuote,
        DoubleQuote,
        TemplateString,
        BlockComment,
    }

    private enum JavaScriptPrevTokenKind
    {
        None,
        Identifier,
        Number,
        CloseParen,
        CloseBracket,
        CloseBrace,
        Other,
    }

    private enum CSharpLexMode
    {
        Code,
        String,
        Char,
        VerbatimString,
        RawString,
        BlockComment,
    }

    private enum JavaScriptScopeKind
    {
        Other,
        Block,
        Function,
        StaticBlock,
        Class,
        Namespace,
        Object,
    }

    [Flags]
    private enum JavaScriptScopePrivacyFlags
    {
        None = 0,
        FunctionLike = 1,
        Block = 2,
        Namespace = 4,
    }

    private readonly record struct JavaScriptLexState(
        JavaScriptLexMode Mode = JavaScriptLexMode.Code,
        bool EscapeNext = false,
        JavaScriptPrevTokenKind PreviousTokenKind = JavaScriptPrevTokenKind.None,
        bool PreviousIdentifierAllowsRegex = false,
        bool ExpectingControlFlowOpenParen = false,
        int ControlFlowParenDepth = 0,
        bool RegexAllowedAfterControlFlowParen = false);

    private readonly record struct JavaScriptLexedLine(
        string SanitizedLine,
        JavaScriptLexState EndState);

    private readonly record struct CSharpLexState(
        CSharpLexMode Mode = CSharpLexMode.Code,
        bool EscapeNext = false,
        int RawDelimiterLength = 0,
        // Interpolation tracking for $@"..." / @$"..." / $"""..."""  / $$"""..."""  etc.
        // IsInterpolated / InterpolationDollarCount describe the CURRENT string mode
        // (only meaningful while Mode is a string mode). Return* fields preserve the
        // outer interpolated string's info while we are inside an interpolation hole
        // (Mode = Code with InterpolationBraceDepth > 0). InterpolationParent keeps
        // an immutable stack when another interpolated string starts inside that hole.
        // 補間 verbatim / raw 文字列のホール追跡。IsInterpolated / InterpolationDollarCount は
        // 現在のモード（string 系モードのときだけ意味を持つ）を表し、Return* は
        // ホール内（Mode = Code かつ InterpolationBraceDepth > 0）の間、外側の
        // 補間文字列情報を退避する。ホール内で別の補間文字列が始まった場合は
        // InterpolationParent の immutable stack に外側の状態を退避する。
        bool IsInterpolated = false,
        int InterpolationDollarCount = 0,
        int InterpolationBraceDepth = 0,
        CSharpLexMode InterpolationReturnMode = CSharpLexMode.Code,
        int InterpolationReturnRawDelimiterLength = 0,
        int InterpolationReturnDollarCount = 0,
        CSharpInterpolationFrame? InterpolationParent = null);

    private sealed record CSharpInterpolationFrame(CSharpLexState State);

    private readonly record struct CSharpLexedLine(
        string SanitizedLine,
        CSharpLexState EndState);

    private readonly record struct CSharpPropertyMatchCandidate(
        string MatchLine,
        int LastConsumedLineIndex,
        int SignatureLastLineIndex,
        int? SignatureLastLineExclusiveEndColumn = null,
        int? ExpressionBodyEndLineIndex = null,
        int? ExpressionBodyEndLineExclusiveEndColumn = null);

    private readonly record struct FortranContinuationMatchCandidate(
        string MatchLine,
        int LastConsumedLineIndex);

    private enum CSharpAccessorProbeStatus
    {
        Pending,
        Found,
        Rejected
    }


    private readonly record struct JavaScriptClassScanTarget(
        int StartIndex,
        int StartColumn,
        int ScanStartIndex,
        int ScanEndExclusive,
        int FirstLineScanOffset,
        string ContainerKind,
        string ContainerName,
        bool IsExported = false);

    private static readonly HashSet<string> TypeScriptBareMethodModifiers =
    [
        "public", "private", "protected", "static", "readonly", "abstract", "override", "async", "get", "set"
    ];

    // Enum declaration — visibility optional; modifier order is free. Accepts `file` (file-scoped
    // enum) and `new` (member-hiding nested enum in a derived type) as non-visibility modifiers.
    // Closes #353.
    // enum 宣言 — visibility は任意で、修飾子の順序は自由。非 visibility 修飾子として `file`
    // （ファイルスコープ enum）と `new`（派生型でのネスト enum 隠蔽）を受け付ける。Closes #353.
    private static readonly Regex CSharpEnumDeclarationRegex = new($@"^\s*(?:(?<visibility>public|private|protected\s+internal|private\s+protected|protected|internal)\s+|(?:file|new)\s+)*enum\s+(?<name>{CSharpIdentifierPattern})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpEnumMemberRegex = new($@"^\s*(?<name>{CSharpIdentifierPattern})\s*(?:=\s*(?:-?\d|0x|{CSharpIdentifierPattern}(?:\s*\|\s*{CSharpIdentifierPattern})*)[^""']*)?,?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpEnumMemberNameRegex = new($@"^\s*(?<name>{CSharpIdentifierPattern})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JavaCompactConstructorRegex = new(
        @"^\s*(?:(?<visibility>public|private|protected)\s+)?(?<name>\w+)\s*(?=\{|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PhpPropertyHookAccessorRegex = new(
        @"^\s*(?<name>get|set)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DartClassDeclarationRegex = new(
        @"^\s*(?:(?:abstract|base|final|interface|sealed)\s+)*(?:mixin\s+)?class\s+\w+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DartBareConstConstructorRegex = new(
        @"^\s*const\s+(?<name>[A-Z_]\w*(?:\.\w+)?)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpSameLinePropertyStatementStartRegex = new(
        $@"^\s*(?:(?:{CSharpVisibilityPattern})\s+|(?:static|virtual|override|abstract|sealed|new|required|partial|readonly|unsafe|extern|ref(?:\s+readonly)?)\s+)*(?!(?:class|struct|interface|enum|record|namespace|delegate|event|const|using|return|throw|yield|var|typeof|sizeof|nameof|default|if|for|foreach|while|switch|catch|lock|case|else|when|break|continue|goto|await)\b)(?:(?:ref(?:\s+readonly)?)\s+)?(?:{CSharpTypePattern})\s+(?:{CSharpExplicitInterfaceQualifierPattern}\.)?{CSharpIdentifierPattern}\s*(?:\{{|=>\s*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpSameLineEventStatementStartRegex = new(
        $@"^\s*(?:(?:{CSharpVisibilityPattern})\s+|(?:static|unsafe|extern|virtual|override|abstract|sealed|new|partial|file)\s+)*event\s+(?:{CSharpTypePattern})\s+{CSharpIdentifierPattern}\s*(?:[;=]|\{{)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpSameLineDelegateStatementStartRegex = new(
        $@"^\s*(?:(?:{CSharpVisibilityPattern})\s+|(?:static|unsafe|file|new)\s+)*delegate\s+(?:{CSharpTypePattern})\s+{CSharpIdentifierPattern}\s*[\(<]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpSameLineEventOrDelegateStatementStartRegex = new(
        $@"^\s*(?:(?:{CSharpVisibilityPattern})\s+|(?:static|unsafe|extern|virtual|override|abstract|sealed|new|partial|file)\s+)*(?:event\s+(?:{CSharpTypePattern})\s+{CSharpIdentifierPattern}\s*(?:[;=]|\{{)|delegate\s+(?:{CSharpTypePattern})\s+{CSharpIdentifierPattern}\s*[\(<])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> JavaScriptTypeScriptControlFlowHeaderKeywords =
    [
        "if", "for", "while", "switch", "catch", "with"
    ];

    private readonly record struct JavaScriptTypeScriptMethodHeaderInfo(
        string Name,
        int BodyStartColumn,
        string? Visibility = null,
        int? GenericStartColumn = null,
        int? GenericEndColumn = null,
        int? ReturnTypeStartColumn = null,
        int? ReturnTypeEndColumn = null,
        int? HeaderEndColumn = null,
        bool HasBody = true,
        bool IsAsync = false,
        bool IsGenerator = false,
        // For class-field arrow properties with an expression body (`handleClick = () => 42;`),
        // this marks the inclusive column of the last expression char (before `;`) in the
        // accumulated sanitized header. Null means brace body or no expression body was detected.
        // クラスフィールド矢印プロパティが式本体を持つ場合 (`handleClick = () => 42;`)、
        // 終端記号 `;` の直前にある式末尾の inclusive 列位置。null は block body か式本体非検出。
        int? ExpressionBodyEndColumn = null);

    private readonly record struct JavaScriptTypeScriptMethodHeaderCapture(
        string SourceHeader,
        JavaScriptTypeScriptMethodHeaderInfo HeaderInfo,
        int HeaderEndLineIndex,
        int HeaderEndColumn,
        int BodyStartLineIndex,
        int BodyStartColumn,
        // For expression-body arrow fields, these are the source line/col of the last
        // expression char (`;` の直前). Null for brace-body arrow fields.
        // 式本体矢印 field の場合の式末尾 source 位置 (終端 `;` の直前)。block body は null。
        int? BodyEndLineIndex = null,
        int? BodyEndColumn = null);

    private struct JavaScriptTypeScriptFunctionHeaderState
    {
        public bool Active;
        public bool SawParameterList;
        public bool InReturnType;
        public int ParenDepth;
        public int BracketDepth;
        public int BraceDepth;
        public int ReturnParenDepth;
        public int ReturnBracketDepth;
        public int ReturnAngleDepth;
        public int ReturnBraceDepth;
        public bool ReturnSawToken;
        public string? PreviousReturnToken;
    }

    private enum JavaScriptTypeScriptMethodHeaderParseStatus
    {
        IncompleteOrInvalid = 0,
        Parsed = 1,
        DeclarationOnly = 2,
    }

    private enum JavaScriptTypeScriptFunctionHeaderConsumeResult
    {
        NotActive = 0,
        Consumed = 1,
        BodyStart = 2,
    }

    private const string JavaScriptTypeScriptIdentifierPattern = @"[$\p{L}_][$\p{L}\p{Nd}_]*";

    private static readonly Regex JavaScriptTypeScriptAnonymousDefaultExportRegex = new(
        @"^\s*(?<visibility>export)\s+default\b",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptClassExpressionBindingRegex = new(
        $@"^\s*(?:(?<visibility>export)\s+)?(?:(?<bindingKind>const|let|var)\s+(?<alias>{JavaScriptTypeScriptIdentifierPattern})|exports\.(?<exportsAlias>{JavaScriptTypeScriptIdentifierPattern})|module\.exports\.(?<moduleExportsAlias>{JavaScriptTypeScriptIdentifierPattern})|(?<moduleExports>module\.exports))\s*=",
        RegexOptions.Compiled);

    private static readonly Regex TypeScriptExportEqualsRegex = new(
        @"^\s*export\s*=",
        RegexOptions.Compiled);

    // Matches the binding portion of object-literal declarations: LHS identifier plus the `=`
    // assignment. The opening `{` is intentionally NOT required on the same line so multi-line
    // forms like `const obj =\n{\n ... }` are still detected. Callers locate the `{` via
    // TryFindJavaScriptTypeScriptObjectLiteralOpenBrace (lex-state aware), then hand the resulting
    // (lineOfBrace, columnOfBrace) to ResolveRange(BodyStyle.Brace). Recognizes
    // const/let/var/export plus CommonJS module.exports / exports.NAME assignments.
    // オブジェクトリテラル宣言の binding 部分（LHS 識別子と `=`）に一致させる。右辺の `{` を同一行に
    // 要求しないのは、`const obj =\n{\n ... }` のような複数行スタイルも拾うため。`{` の位置は
    // TryFindJavaScriptTypeScriptObjectLiteralOpenBrace が lex 状態を引き継ぎつつ別途走査し、
    // 見つけた (lineOfBrace, columnOfBrace) を ResolveRange(BodyStyle.Brace) に渡す。const/let/var/export
    // に加え、CommonJS の module.exports / exports.NAME 代入経路にも対応する。
    private static readonly Regex JavaScriptTypeScriptObjectLiteralBindingRegex = new(
        $@"^\s*(?:(?<visibility>export)\s+)?(?:(?<bindingKind>const|let|var)\s+(?<alias>{JavaScriptTypeScriptIdentifierPattern})|exports\.(?<exportsAlias>{JavaScriptTypeScriptIdentifierPattern})|module\.exports\.(?<moduleExportsAlias>{JavaScriptTypeScriptIdentifierPattern})|(?<moduleExports>module\.exports))(?:\s*:\s*[^=]+?)?\s*=\s*",
        RegexOptions.Compiled);

    // Matches `export default` at start of line. `export default { ... }` is an anonymous object
    // that becomes the module's default export; its method-shorthand members are attached to a
    // virtual "default" container. Uses the same lex-aware `{` scan as the binding regex.
    // 行頭の `export default` に一致。`export default { ... }` は無名オブジェクトでモジュールの
    // 既定エクスポートになり、そのメソッド省略記法のメンバは仮想コンテナ "default" に紐付ける。
    // 後続の `{` の位置は binding 用と同じ lex-aware 走査で特定する。
    private static readonly Regex JavaScriptTypeScriptExportDefaultObjectLiteralRegex = new(
        @"^\s*export\s+default\s*",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptStarReExportRegex = new(
        $@"^\s*export\s*(?:type\s+)?\*(?:\s*as\s+(?<namespace>{JavaScriptTypeScriptIdentifierPattern}))?\s*from\s*(?<module>['""][^'""]+['""])(?:\s+(?:with|assert)\s+\{{[^}}]*\}})?\s*;?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptNamedReExportRegex = new(
        @"^\s*export\s*(?:type\s+)?\{\s*(?<specifiers>[^}]+)\s*\}\s*from\s*(?<module>['""][^'""]+['""])(?:\s+(?:with|assert)\s+\{[^}]*\})?\s*;?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptDestructuredNamedExportRegex = new(
        @"^\s*export\s+(?:const|let|var)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptExportedVariableDeclarationRegex = new(
        @"^\s*export\s+(?:declare\s+)?(?:const|let|var)\b",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptCommonJsNamedExportAssignmentRegex = new(
        $@"^\s*(?:module\.exports|exports)(?:\.(?<name>{JavaScriptTypeScriptIdentifierPattern})|\[\s*(?:['""](?<bracketName>[^'""]*)['""]|(?<numericBracketName>\d+(?:\.\d+)?))\s*\])(?:\s*:\s*[^=]+?)?\s*(?<![=!<>])=(?![=>])\s*(?<rhs>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptCommonJsDefaultExportAssignmentRegex = new(
        @"^\s*module\.exports(?:\s*:\s*[^=]+?)?\s*(?<![=!<>])=(?![=>])\s*(?<rhs>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptQualifiedAssignmentRegex = new(
        $@"^\s*(?<name>[A-Z][\w$]*(?:\.[\w$]+)+)\s*(?<![=!<>])=(?![=>])\s*(?<rhs>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptArrowAssignmentValueRegex = new(
        $@"^(?:async\s+)?(?:\([^)]*\)|{JavaScriptTypeScriptIdentifierPattern})\s*=>",
        RegexOptions.Compiled);

    private static readonly Regex SvelteReactivePropertyRegex = new(
        @"^\s*\$:\s*(?<name>\w+)\s*=",
        RegexOptions.Compiled);

    private const string VbVisibilityPattern = @"(?:Public|Private|Protected|Friend)(?:\s+(?:Protected|Friend))?";
    private const string VbTypeModifierPattern = @"(?:Partial|MustInherit|NotInheritable)";
    private const string VbMemberModifierPattern = @"(?:Shared|Overrides|Overridable|NotOverridable|MustOverride|Overloads|Shadows|Async|Iterator|Partial|Declare|PtrSafe|Auto|Ansi|Unicode)";
    private const string VbOperatorModifierPattern = @"(?:Shared|Overrides|Overridable|MustOverride|Overloads|Shadows|Async|Partial|Widening|Narrowing)";
    private const string VbPropertyModifierPattern = @"(?:Shared|Overrides|Overridable|NotOverridable|MustOverride|Overloads|Shadows|Default|ReadOnly|WriteOnly)";
    private const string VbEventModifierPattern = @"(?:Shared|Overloads|Shadows|Custom)";
    private const string VbIdentifierPattern = @"(?:\[[^\]\r\n]+\]|\w+)";

    private static readonly Dictionary<string, List<SymbolPattern>> PatternCache = new()
    {
        ["python"] =
        [
            new("function", new Regex(@"^\s*(?:async\s+)?def\s+(?<name>\w+)\s*(?:\[[^\]]*\])?\s*(?:\(|\[)", RegexOptions.Compiled), BodyStyle.Indent),
            new("lambda",   new Regex(@"^\s*(?<name>\w+)\s*=\s*lambda\b", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*class\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Indent),
            new("class",    new Regex(@"^\s*(?<name>\w+)\s*=\s*(?:(?:typing|collections)\.)?(?:NamedTuple|namedtuple)\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?<name>\w+)\s*=\s*(?:dataclasses\.)?make_dataclass\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?<name>\w+)\s*=\s*(?:(?:typing|typing_extensions)\.)?TypedDict\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?<name>\w+)\s*=\s*(?:enum\.)?(?:Enum|IntEnum|Flag|IntFlag|StrEnum)\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?<name>\w+)\s*=\s*(?:pydantic\.)?create_model\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("typealias", new Regex(@"^\s*type\s+(?<name>\w+)\s*(?:\[[^\]]*\])?\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("typealias", new Regex(@"^\s*(?<name>\w+)\s*:\s*(?:(?:typing|typing_extensions)\.)?TypeAlias\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("typealias", new Regex(@"^\s*(?<name>\w+)\s*=\s*(?:(?:typing|typing_extensions)\.)?NewType\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("type_parameter", new Regex(@"^\s*(?<name>\w+)\s*=\s*(?:(?:typing|typing_extensions)\.)?(?:TypeVar|ParamSpec|TypeVarTuple)\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("property", new Regex(@"^\s*(?<name>\w+)\s*:\s*(?:(?:typing|typing_extensions)\.)?Final(?:\[[^\]]+\])?\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*(?:from\s+(?<name>(?:\.+[\w.]*|[\w.]+))\s+import\b|import\s+(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*(?:[_\p{L}]\w*\s*=\s*)?(?:importlib\.import_module|importlib\.util\.find_spec|__import__)\s*\(\s*['""](?<name>[^'""]+)['""]", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["cython"] =
        [
            new("import", new Regex(@"^\s*from\s+(?<name>" + CythonDottedIdentifierPattern + @")\s+cimport\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*cimport\s+(?<name>" + CythonDottedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*import\s+(?<name>" + CythonDottedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*include\s+(?:'(?<name>[^']+)'|""(?<name>[^""]+)"")", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*cdef\s+extern\s+from\s+(?:'(?<name>[^']+)'|""(?<name>[^""]+)"")", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class", new Regex(@"^\s*cdef\s+class\s+(?<name>" + CythonIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Indent),
            new("class", new Regex(@"^\s*class\s+(?<name>" + CythonIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Indent),
            new("struct", new Regex(@"^\s*(?:ctypedef\s+)?cdef\s+struct\s+(?<name>" + CythonIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Indent),
            new("enum", new Regex(@"^\s*(?:ctypedef\s+)?cdef\s+enum\s+(?<name>" + CythonIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Indent),
            new("typealias", new Regex(@"^\s*ctypedef\s+(?!(?:struct|enum|union)\b)(?<returnType>.+?)\s+(?<name>" + CythonIdentifierPattern + @")\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None, ReturnTypeGroup: "returnType"),
            new("function", new Regex(@"^\s*(?:cdef|cpdef)\s+(?!(?:class|struct|enum|extern)\b)" + CythonDeclarationPrefixPattern + @"(?:(?<returnType>[\w.<>*,\[\]\s]+?)\s+)?(?<name>" + CythonIdentifierPattern + @")\s*(?:\[[^\]]+\]\s*)?\(", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Indent, ReturnTypeGroup: "returnType"),
            new("function", new Regex(@"^\s*(?:async\s+)?def\s+(?<name>" + CythonIdentifierPattern + @")\s*(?:\[[^\]]*\])?\s*(?:\(|\[)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Indent),
            new("function", new Regex(@"^\s*" + CythonNativeReturnTypePattern + @"(?<name>" + CythonIdentifierPattern + @")\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None, ReturnTypeGroup: "returnType"),
            new("property", new Regex(@"^\s*cdef\s+(?!(?:class|struct|enum|extern)\b)" + CythonDeclarationPrefixPattern + @"(?<returnType>.+?)\s+(?<name>" + CythonIdentifierPattern + @")\s*(?::|=|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None, ReturnTypeGroup: "returnType"),
        ],
        ["cobol"] =
        [
            // COBOL is organized around program IDs rather than brace-scoped members.
            // Keep the extraction deliberately small and conservative: one symbol per program.
            // COBOL は brace ではなく program ID 単位で構成されるため、抽出は保守的に
            // program ひとつにつき 1 symbol に絞る。
            new("class", new Regex(@"^\s*(?:IDENTIFICATION\s+DIVISION\.\s*)?(?:PROGRAM|CLASS)-ID\.\s*(?<name>[A-Z0-9][A-Z0-9-]*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*METHOD-ID\.\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["javascript"] =
        [
            // Include optional `*` between `function` and name for generator functions (e.g. `function* gen()`, `async function* asyncGen()`)
            // `function` と名前の間に任意の `*` を許容し、ジェネレータ関数 (`function* gen()`, `async function* asyncGen()`) にも対応
            new("function", new Regex(@"^\s*(?<visibility>export)\s+(?<name>default)\s+(?<async>async\s+)?function(?:\s+|\s*(?<generator>\*)\s*)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>export)\s+(?:default\s+)?)?(?<async>async\s+)?function(?:\s+|\s*(?<generator>\*)\s*)(?<name>\w+)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("lambda", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:const|let|var)\s+(?<name>\w+)\s*=\s*(?:async\s+)?(?:\([^)]*\)|[^=])\s*=>", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("lambda", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:const|let|var)\s+(?<name>\w+)\s*(?::\s*.+?)?\s*=\s*(?<async>async\s+)?function\s*(?<generator>\*)?\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:const|let|var)\s+(?<name>\w+)\s*(?::\s*.+?)?\s*=\s*(?<async>async\s+)?function(?:\s+|\s*(?<generator>\*)\s*)\w+\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // HOC-wrapped / call-result component bindings such as
            // `const Wrapped = React.memo(...)`, `const Box = React.forwardRef(...)`,
            // `const Connected = connect(...)(Component)`, `const Styled = styled.div`...``,
            // or `const WithAuth = withAuthentication(Home)`. The arrow pattern above does
            // not fire for these because the RHS is a call expression, tagged template,
            // or plain identifier — there is no `=>` right after the `=`. The RHS is
            // restricted to a known set of HOC call shapes — `React.memo(` /
            // `React.forwardRef(` / `React.lazy(`, `styled.`/`styled(`/`styled``,
            // bare `connect(`/`memo(`/`forwardRef(`/`lazy(`/`observer(`, and
            // `with<PascalCase>(`. Styled factory captures (`const F = styled.div;`) and
            // plain styled calls (`const F = styled(Component);`) are NOT real component
            // bindings — they produce a factory / a styled-component-of-component but do
            // not declare a rendered component here — so an additional post-match gate
            // rejects them unless the source line carries a tagged-template backtick.
            // The gate checks the raw (unmasked) line because
            // StructuralLineMasker.MaskJsTsTemplateLiteralContents masks template
            // delimiters to space, which would otherwise make the same regex accept the
            // non-template forms too. Unlike the TypeScript row below, the JavaScript
            // row deliberately does NOT accept an optional `<TypeArgs>` token
            // between the HOC call name and its `(` — JavaScript has no generic
            // syntax and `const Result = memo < Props > (Component);` is a chained
            // comparison / call expression that must not produce a phantom HOC
            // binding. The asymmetry with the TypeScript row is documented on
            // TypeScriptOptionalHocTypeArgsPattern. Ordinary PascalCase constants like
            // `const Config = loadConfig();` and `const Theme = React.createContext(null);`
            // (non-HOC React API calls — `createContext`, hooks, etc.) and class
            // expressions like `const Widget = class extends ...` do NOT produce phantom
            // `function` symbols. The class-expression synthetic pass owns the `= class`
            // shape on its own. BodyStyle.None because the RHS body span is not
            // line-trackable from the declaration line alone; declaration-only visibility
            // into the symbol is still strictly better than dropping the binding. Place
            // AFTER the arrow-function pattern so a capitalized arrow binding wins that
            // row via stopAfterFirstPatternMatch and is not shadowed here. Closes #240.
            // React.memo / React.forwardRef / connect(...)(Component) / styled.div`...` /
            // withAuthentication(Home) のような HOC ラップや呼び出し結果代入の
            // コンポーネント束縛を取り込む。上の arrow パターンは `=` 直後に `=>` を
            // 要求するため、RHS が呼び出し式・タグ付きテンプレート・プレーン識別子では
            // 発火しない。RHS を既知の HOC 呼び出し形 — `React.memo(` / `React.forwardRef(`
            // / `React.lazy(`、`styled.` / `styled(` / `styled``、素の `connect(` /
            // `memo(` / `forwardRef(` / `lazy(` / `observer(`、`with<PascalCase>(` — に
            // 限定する。styled の factory 捕捉（`const F = styled.div;`）や素の呼び出し
            // （`const F = styled(Component);`）は実体のあるコンポーネント束縛ではないため、
            // マッチ後のゲートでタグ付きテンプレートのバッククォートを原文行に要求し、
            // これらが phantom な function シンボルを生やさないようにする。ゲートは raw
            // 行を参照する — `StructuralLineMasker.MaskJsTsTemplateLiteralContents` が
            // テンプレート区切りを空白にマスクするため、同じ regex を使っても masked
            // 経由では区別できないのがゲートを raw 行で行う理由。JavaScript 行は TypeScript
            // 行と異なり、HOC 呼び出し名と `(` の
            // 間に generic 型引数トークン `<...>` を意図的に受け付けない。JavaScript に
            // generic 構文は無く、`const Result = memo < Props > (Component);` は単なる
            // 比較・呼び出し連鎖式であって phantom な HOC 束縛を生やしてはならない。
            // 非対称な扱いは TypeScriptOptionalHocTypeArgsPattern のコメントで詳述する。
            // `const Config = loadConfig();` のような通常 PascalCase 定数や、
            // `const Theme = React.createContext(null);` のような非 HOC の React API 呼び出し
            // （`createContext` や hooks 等）、`const Widget = class extends ...` の
            // クラス式束縛で架空の `function` シンボルが生えないようにする。`= class` 形は
            // class expression の合成パスが単独で処理する。RHS 本体は宣言行だけでは
            // 行単位に追えないため BodyStyle.None。宣言のみでも束縛が消失するよりは実用的。
            // arrow パターンより後に置き、大文字始まりの arrow 束縛は先に一致した段階で
            // stopAfterFirstPatternMatch が立ち、こちらで上書きされないようにする。
            // Closes #240.
            new("function", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:const|let|var)\s+(?<name>[A-Z]\w*)\s*=\s*(?:React\.(?:memo|forwardRef|lazy)\s*\(|styled[.(`]|connect\s*\(|memo\s*\(|forwardRef\s*\(|lazy\s*\(|observer\s*\(|with[A-Z]\w*\s*\()", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("class",    new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:default\s+)?class\s+(?<name>(?!extends\b)\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("import",   new Regex(@"^\s*import\s+(?<name>.+?)\s+from\s+", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["typescript"] =
        [
            // Include optional `*` between `function` and name for generator functions (e.g. `function* gen()`, `async function* asyncGen()`)
            // `function` と名前の間に任意の `*` を許容し、ジェネレータ関数 (`function* gen()`, `async function* asyncGen()`) にも対応
            new("function", new Regex(@"^\s*(?<visibility>export)\s+(?<name>default)\s+(?<async>async\s+)?function(?:\s+|\s*(?<generator>\*)\s*)" + TypeScriptOptionalTypeParameterListPattern + @"\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>export)\s+(?:default\s+)?)?(?:declare\s+)?(?<async>async\s+)?function(?:\s+|\s*(?<generator>\*)\s*)(?<name>\w+)\s*[\(<]", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("import", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:declare\s+)?type\s+(?<name>\w+)" + TypeScriptOptionalTypeParameterListPattern + @"\s*=", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("property", new Regex(@"^\s*(?:(?<visibility>export)\s+)?declare\s+(?:const|let|var)\s+(?<name>\w+)(?::\s*[^;=]+)?\s*;", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("lambda", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:const|let|var)\s+(?<name>\w+)\s*(?::\s*.+?)?\s*=\s*(?:async\s+)?(?:\([^)]*\)|[^=])\s*=>", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("lambda", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:const|let|var)\s+(?<name>\w+)\s*(?::\s*.+?)?\s*=\s*(?<async>async\s+)?function\s*(?<generator>\*)?\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:const|let|var)\s+(?<name>\w+)\s*(?::\s*.+?)?\s*=\s*(?<async>async\s+)?function(?:\s+|\s*(?<generator>\*)\s*)\w+\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // HOC-wrapped / call-result component bindings — same narrow HOC-prefix set
            // as the JavaScript row above, extended with an optional TypeScript generic
            // type-argument token between the HOC call name and its `(` via the shared
            // TypeScriptOptionalHocTypeArgsPattern constant. The generic token balances
            // up to three levels of nested angle brackets
            // (`React.memo<Record<string, Map<string, Props>>>(Box)`) and allows
            // parenthesised segments inside a generic argument
            // (`React.memo<(props: Props) => JSX.Element>(Box)`) so function-type and
            // conditional-type TS HOC call sites still match. The `React.` branch is
            // pinned to `React.memo(` / `React.forwardRef(` / `React.lazy(` so non-HOC
            // React API calls (`const Theme = React.createContext(null);`,
            // `const Stable = React.useCallback(() => 1, []);`) do NOT produce phantom
            // `function` rows on the TypeScript side either. The JavaScript row above
            // intentionally does NOT carry the generic token because JS has no generic
            // syntax and `memo < Props > (Component)` is a chained comparison / call
            // expression; see the TypeScriptOptionalHocTypeArgsPattern comment for the
            // ReDoS-safety reasoning behind the 3-level-plus-parens shape. TypeScript
            // sources often carry a type annotation between the binding name and `=`
            // (e.g. `const Connected: React.ComponentType<Props> = connect(...)(MyComponent);`).
            // The optional `:` branch consumes the annotation lazily up to the first `=`;
            // even when a type contains `=>` (as in `const F: () => void = fn;`), the
            // lazy match back-tracks so the name group is still captured correctly. The
            // arrow-function row above also accepts the same optional annotation so a
            // typed arrow binding (`const Callback: (x: number) => number = (x) =>
            // x + 1;`) still wins with BodyStyle.Brace and is not shadowed here.
            // Closes #240.
            // HOC ラップや呼び出し結果代入のコンポーネント束縛 — JavaScript 行と同じ
            // 狭い HOC プレフィックス集合を使い、共有定数
            // TypeScriptOptionalHocTypeArgsPattern で HOC 呼び出し名と `(` の間に
            // TypeScript の generic 型引数トークンをオプションで受け入れる。この
            // トークンは 3 段までのネストした山括弧
            // （`React.memo<Record<string, Map<string, Props>>>(Box)`）と、
            // generic 引数内の丸括弧付きセグメント
            // （`React.memo<(props: Props) => JSX.Element>(Box)`）を許容するため、
            // 関数型・条件型を使う TS HOC 呼び出しもマッチする。`React.` 分岐は
            // `React.memo(` / `React.forwardRef(` / `React.lazy(` に固定し、
            // `const Theme = React.createContext(null);` や
            // `const Stable = React.useCallback(() => 1, []);` のような非 HOC の
            // React API 呼び出しが TypeScript 側でも phantom `function` シンボルを
            // 生やさないようにする。JavaScript 行は generic トークンを意図的に持たない。
            // JS に generic 構文は無く、`memo < Props > (Component)` は比較・呼び出しの
            // 連鎖式だからである。3 段 + 括弧許容にした ReDoS 安全性の根拠は
            // TypeScriptOptionalHocTypeArgsPattern のコメントを参照。TypeScript では
            // 束縛名と `=` の間に型注釈（例:
            // `const Connected: React.ComponentType<Props> = connect(...)(MyComponent);`）
            // が入ることが多いため、オプションの `:` 分岐で最初の `=` まで遅延一致する。
            // 型に `=>` が含まれる場合（例: `const F: () => void = fn;`）もバックトラックで
            // 名前グループは正しく取得できる。上の arrow 行も同じ型注釈を受け付けるため、
            // 型注釈付き arrow 束縛（`const Callback: (x: number) => number = (x) =>
            // x + 1;`）は BodyStyle.Brace 側で先勝ちし、こちらで上書きされない。
            // Closes #240.
            new("function", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:const|let|var)\s+(?<name>[A-Z]\w*)\s*(?::\s*.+?)?\s*=\s*(?:React\.(?:memo|forwardRef|lazy)\s*" + TypeScriptOptionalHocTypeArgsPattern + @"\(|styled[.(`]|connect\s*" + TypeScriptOptionalHocTypeArgsPattern + @"\(|memo\s*" + TypeScriptOptionalHocTypeArgsPattern + @"\(|forwardRef\s*" + TypeScriptOptionalHocTypeArgsPattern + @"\(|lazy\s*" + TypeScriptOptionalHocTypeArgsPattern + @"\(|observer\s*" + TypeScriptOptionalHocTypeArgsPattern + @"\(|with[A-Z]\w*\s*" + TypeScriptOptionalHocTypeArgsPattern + @"\()", RegexOptions.Compiled), BodyStyle.None, "visibility"),
              // Abstract class, declare class / 抽象クラス、declare クラス
              new("class",    new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:default\s+)?(?:(?:abstract|declare)\s+)*class\s+(?<name>(?!(?:extends|implements)\b)\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
              // UMD namespace export / UMD 名前空間エクスポート
              new("namespace", new Regex($@"^\s*export\s+as\s+namespace\s+(?<name>{JavaScriptTypeScriptIdentifierPattern})", RegexOptions.Compiled), BodyStyle.None),
              // namespace/module — supports both identifier (namespace Foo) and quoted ambient (declare module 'express')
              // 名前空間・モジュール — 識別子形式と引用符付きアンビエント形式の両方に対応
              new("namespace", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:declare\s+)?(?:namespace|module)\s+['""](?<name>[^'""]+)['""]", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
              new("namespace", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:declare\s+)?(?:namespace|module)\s+(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
              new("interface", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:declare\s+)?interface\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
              new("import", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:declare\s+)?type\s+(?<name>\w+)(?:\s*<[^=]+>)?", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("enum",     new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:declare\s+)?(?:const\s+)?enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("import",   new Regex(@"^\s*import\s+(?<name>.+?)\s+from\s+", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["csharp"] =
        [
            // Verbatim and Unicode-escaped identifier segments (`@Foo.@Bar`, `\u0046oo`) are
            // accepted via `CSharpNamespacePattern` / `CSharpIdentifierPattern` and later
            // canonicalized by `CSharpSymbolNameNormalizer`.
            // verbatim / Unicode escape 識別子の各セグメントを `CSharpNamespacePattern` /
            // `CSharpIdentifierPattern` 経由で受け入れ、`CSharpSymbolNameNormalizer` で
            // canonical 化する。
            new("namespace", new Regex($@"^\s*namespace\s+(?<name>{CSharpNamespacePattern})\s*;", RegexOptions.Compiled), BodyStyle.None),  // file-scoped namespace (C# 10+)
            new("namespace", new Regex($@"^\s*namespace\s+(?<name>{CSharpNamespacePattern})", RegexOptions.Compiled), BodyStyle.Brace),  // block-scoped namespace
            // extern alias (must precede using directives per C# spec) — captures assembly-alias reconciliation
            // extern alias — C# 仕様上 using より前に置かれるファイル先頭宣言。アセンブリエイリアス用
            new("import",    new Regex($@"^\s*extern\s+alias\s+(?<name>{CSharpIdentifierPattern})\s*;", RegexOptions.Compiled), BodyStyle.None),
            // using alias (using X = Y;) — must come before general using to capture alias name.
            // Verbatim alias identifiers like `using @AliasAttr = A.BaseAttr;` still surface as an
            // `import` row via `CSharpIdentifierPattern`; the DbWriter-side normalizer strips the
            // leading `@`.
            // using エイリアス — 一般 using より前に配置しエイリアス名を取得。verbatim 識別子
            // (`using @AliasAttr = A.BaseAttr;`) も `CSharpIdentifierPattern` 経由で import 行として
            // 拾える。
            new("import",    new Regex($@"^\s*(?:global\s+)?using\s+(?<name>{CSharpIdentifierPattern})\s*=\s*[^;]+;", RegexOptions.Compiled), BodyStyle.None),
            new("import",    new Regex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?<name>[^;=]+);", RegexOptions.Compiled), BodyStyle.None),
            // Const field — must come before class/method patterns to avoid misclassification.
            // Modifier order is free: visibility may appear anywhere in the modifier sequence,
            // so `new public const` and `public new const` are both captured. Closes #355.
            // returnType uses the shared CSharpTypePattern (same token the method / property /
            // indexer / delegate / event rows already use) so tuple / named-tuple /
            // nullable-tuple / generic-over-tuple / global::-qualified / tuple-array const field
            // types are captured instead of silently dropped. The legacy hand-rolled char class
            // had no `(`, `)`, or `\s`, so `public const (int, int) Pair = (1, 2);` failed the
            // returnType group and fell through every subsequent row. Closes #346.
            // const フィールド — クラス/メソッドパターンより前に配置し誤分類を防ぐ。
            // 修飾子順序は自由で、visibility は修飾子列の任意位置に現れてよい（例: `new public const` /
            // `public new const`）。Closes #355.
            // returnType は method / property / indexer / delegate / event 行で既に使っている共有
            // トークン CSharpTypePattern を使う。これにより tuple / 名前付き tuple / nullable tuple /
            // generic-over-tuple / `global::` 修飾 / tuple-array を戻り値型とする const フィールドを
            // 取りこぼさない。従来の手書き文字クラスには `(` / `)` / `\s` が無く、
            // `public const (int, int) Pair = (1, 2);` は returnType 群で失敗し、以降のどの行にも
            // マッチしなかった。Closes #346.
            new("function",  new Regex($@"^\s*(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:new|static)\s+)*const\s+(?<returnType>{CSharpTypePattern})\s+(?<name>{CSharpIdentifierPattern})\s*=", RegexOptions.Compiled), BodyStyle.None, "visibility", "returnType"),
            // Static readonly field / static readonly フィールド
            // Modifier order is free: `static` and `readonly` may appear in any order, and `new`
            // (member hiding) may appear anywhere in the modifier sequence. Visibility is also
            // accepted anywhere, not just at the front, so legacy orderings like
            // `readonly public static` / `static public readonly` still classify as fields
            // instead of falling through to the plain-field (kind `property`) row. Closes #355.
            // static/readonly の順序は自由で、`new`（メンバー隠蔽）も任意位置に置ける。visibility も
            // 先頭以外の位置に現れることを許容し、`readonly public static` や `static public readonly`
            // のような旧来の並びでも kind `field` で取り扱う。通常フィールド（kind `property`）の
            // 正規表現に流れ落ちないようにする。Closes #355.
            // Share CSharpTypePattern with const and plain fields so tuple, nullable-tuple, and
            // generic-over-tuple types retain stable field kind and complete return-type metadata.
            // const / 通常フィールドと CSharpTypePattern を共有し、tuple / nullable tuple /
            // generic-over-tuple 型でも安定した field kind と完全な return-type metadata を保持する。
            // Closes #4616.
            new("function",  new Regex(
                $@"^\s*"
              + $@"(?=(?:(?:{CSharpVisibilityPattern}|new|static|readonly)\s+)*static\s+)"
              + $@"(?=(?:(?:{CSharpVisibilityPattern}|new|static|readonly)\s+)*readonly\s+)"
              + $@"(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:new|static|readonly)\s+)+"
              + $@"(?<returnType>{CSharpTypePattern})\s+(?<name>{CSharpIdentifierPattern})\s*[=;]",
                RegexOptions.Compiled), BodyStyle.None, "visibility", "returnType"),
            // Plain field (instance, readonly, volatile, plain static, etc.) — kind `property`.
            // Must come AFTER the `const` and `static readonly` patterns (which take priority
            // with kind `function`), and BEFORE the structural declaration patterns.
            // The terminator `=(?![=>])` or `;` distinguishes fields from methods (which end
            // with `(`), property accessors (which end with `{`), expression-bodied members
            // (which use `=>`), and comparison-operator overloads (which contain `==`).
            // The negative lookahead repeats every visibility and modifier keyword so the
            // regex engine cannot backtrack past an unconsumed `public static event …`
            // declaration and match it as a field whose returnType is `public static event …`.
            // Closes #298.
            // 通常フィールド（instance / readonly / volatile / 通常 static など） — kind は `property`。
            // `const` / `static readonly` パターン（kind `function`）より後、型宣言パターンより前に置く。
            // 終端を `=(?![=>])` または `;` にすることで、メソッド（`(`）、プロパティアクセサ（`{`）、
            // 式本体メンバー（`=>`）、比較演算子オーバーロード（`==`）を除外する。
            // visibility / modifier キーワードを negative lookahead にも並べて、regex engine が
            // それらを returnType として飲み込む方向に backtrack して `public static event …`
            // のような宣言を field としてマッチすることを防ぐ。Closes #298.
            // Modifier order is free, so visibility may appear anywhere in the modifier
            // sequence (e.g. `static public int X;`). Closes #355.
            // 修飾子順序は自由で、visibility を修飾子列の任意位置に置ける
            // （例: `static public int X;`）。Closes #355.
            new("property",  new Regex(
                $@"^\s*(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|readonly|volatile|new|unsafe|extern|required)\s+)*"
              + @"(?!(?:public|private|protected|internal|static|readonly|volatile|new|unsafe|extern|required|abstract|virtual|override|sealed|async|partial|file|ref|var|class|struct|interface|enum|record|namespace|delegate\b(?!\*)|event|const|using|return|throw|yield|if|for|foreach|while|switch|catch|lock|case|else|when|break|continue|goto|await|try|do|typeof|sizeof|nameof|default|operator|this|base)\b)"
              + $@"(?<returnType>{CSharpTypePattern})\s+"
              + @"(?<name>" + CSharpIdentifierPattern + @")\s*(?:=(?![=>])|;)",
                RegexOptions.Compiled),
                BodyStyle.None, "visibility", "returnType"),
            // Interface — visibility optional; modifier order is free, so visibility may appear
            // anywhere in the modifier sequence (e.g. `partial public interface`, `file interface`,
            // `new public interface` for nested types). Closes #355.
            // インターフェース — visibility 省略可。修飾子順序は自由
            // （例: `partial public interface`、`file interface`、ネスト型向けの `new public interface`）。Closes #355.
            new("interface", new Regex($@"^\s*(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:partial|unsafe|file|new)\s+)*interface\s+(?<name>{CSharpIdentifierPattern})", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Enum — visibility optional / enum — visibility 省略可
            new("enum",      CSharpEnumDeclarationRegex, BodyStyle.Brace, "visibility"),
            // Struct (including record struct, ref struct, readonly struct) — visibility optional;
            // modifier order is free, so visibility may appear anywhere in the modifier sequence
            // (e.g. `readonly public struct`, `ref public struct`). Closes #355.
            // 構造体（record struct, ref struct, readonly struct を含む）— visibility 省略可。
            // 修飾子順序は自由で、visibility は任意位置に置いてよい（例: `readonly public struct`、
            // `ref public struct`）。Closes #355.
            new("struct",    new Regex($@"^\s*(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|partial|readonly|file|new|ref|unsafe)\s+)*(?:record\s+)?struct\s+(?<name>{CSharpIdentifierPattern})", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Class (including record, record class) — visibility optional (defaults to internal
            // for top-level); modifier order is free, so visibility may appear anywhere in the
            // modifier sequence (e.g. `abstract public class`, `sealed public class`). Closes #355.
            // クラス（record, record class を含む）— visibility は省略可能（トップレベルでは internal がデフォルト）。
            // 修飾子順序は自由で、visibility は任意位置に置いてよい（例: `abstract public class`、
            // `sealed public class`）。Closes #355.
            new("class",     new Regex($@"^\s*(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|partial|abstract|sealed|readonly|file|new|unsafe)\s+)*(?:record\s+class\s+|record\s+|class\s+)(?<name>{CSharpIdentifierPattern})", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Implicit/explicit conversion operator — must come before general operator pattern.
            // Visibility may appear before or after `static` / `unsafe` / `extern`. Closes #355.
            // Modifier slot also accepts `abstract|virtual|sealed|override|new` so C# 11
            // `static abstract` / `abstract static` interface conversion operators (generic
            // math: `System.Numerics.INumber<TSelf>` etc.) and default-implementation /
            // member-hiding forms on interfaces are not silently dropped. Closes #244.
            // 暗黙的/明示的変換演算子 — 一般のoperatorパターンより先に配置。
            // visibility は `static` / `unsafe` / `extern` のどちら側にも置ける。Closes #355.
            // 修飾子スロットは `abstract|virtual|sealed|override|new` も受け付ける。
            // これにより C# 11 の `static abstract` / `abstract static` interface 変換演算子
            // （generic math: `System.Numerics.INumber<TSelf>` など）と、interface 上の
            // default implementation / member hiding 形態を黙って取りこぼさない。Closes #244.
            new("operator",  new Regex(
                $@"^\s*"
              + $@"(?=(?:(?:{CSharpVisibilityPattern}|static|abstract|virtual|sealed|override|new|unsafe|extern)\s+)*static\s+)"
              + $@"(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|abstract|virtual|sealed|override|new|unsafe|extern)\s+)+"
              + @"(?<name>(?:implicit|explicit)\s+operator\s+.+?)\s*\(",
                RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Operator overload (+ - * / == != < > etc.) — must come before method pattern.
            // Visibility may appear before or after `static`. Closes #355.
            // Modifier slot also accepts `abstract|virtual|sealed|override|new` so C# 11
            // `static abstract` / `abstract static` interface operators (generic math:
            // `IAdditionOperators<T>`, `IComparisonOperators<T>`, etc.) are not silently
            // dropped. Closes #244.
            // 演算子オーバーロード — メソッドパターンより前に配置。
            // visibility は `static` のどちら側にも置ける。Closes #355.
            // 修飾子スロットは `abstract|virtual|sealed|override|new` も受け付ける。
            // これにより C# 11 の `static abstract` / `abstract static` interface 演算子
            // （generic math: `IAdditionOperators<T>`、`IComparisonOperators<T>` など）を
            // 黙って取りこぼさない。Closes #244.
            new("operator",  new Regex(
                $@"^\s*"
              + $@"(?=(?:(?:{CSharpVisibilityPattern}|static|abstract|virtual|sealed|override|new|unsafe|extern)\s+)*static\s+)"
              + $@"(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|abstract|virtual|sealed|override|new|unsafe|extern)\s+)+"
              + @".+?\s+(?<name>operator\s+(?:checked\s+)?\S+)\s*\(",
                RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Method with return type — visibility optional for explicit interface impl and nested members.
            // Negative lookahead excludes call-site lines (await/return/throw/yield/var/typeof/sizeof/nameof/default/if/for/while/switch/catch/lock/using)
            // and ternary continuation branches (`? Foo(...)` / `: Foo(...)`) that would otherwise resemble returnType + name.
            // LINQ query-expression keywords (from/where/select/orderby/group/join/let/into/on/equals/ascending/descending/by)
            // are also excluded so continuation lines like `select Mapper.Convert(x)` or `where Validator.Check(x)` do not
            // fire returnType+qualifier+name phantoms. The lookahead is anchored to the line-leading token, so it only
            // blocks continuation forms; ordinary method declarations whose NAME happens to be a LINQ keyword still match
            // via their return type (e.g. `public void where() { }`). Closes #377.
            // The `(?!(?:base|this)\b)` guard on the name capture belt-and-suspenders against constructor-chain
            // initializers (`: base(...)` / `: this(...)`) leaking phantom `function base` / `function this`
            // symbols if any upstream guard becomes permissive. Closes #331.
            // Note: `new` is NOT excluded because `new void Hidden()` is a valid C# member-hiding declaration.
            // 戻り値型付きメソッド — 明示的インターフェース実装やネストメンバー向けに visibility 省略可。
            // negative lookahead で呼び出し行（await/return/throw/yield/var/typeof 等）と ternary continuation を除外する。
            // LINQ 式キーワード (from/where/select/orderby/group/join/let/into/on/equals/ascending/descending/by) も除外し、
            // `select Mapper.Convert(x)` や `where Validator.Check(x)` のような continuation 行が returnType+qualifier+name
            // phantom を生まないようにする。lookahead は行頭トークンに固定しているため、continuation 形のみを弾き、
            // LINQ キーワードと同名のメソッド（例: `public void where() { }`）は戻り値型を介して通常どおり一致する。Closes #377.
            // `(?!(?:base|this)\b)` を name キャプチャに付け、上流ガードが緩んだ場合でも
            // コンストラクタ初期化子 (`: base(...)` / `: this(...)`) が phantom `function base` / `function this`
            // として漏れないよう二重化する。Closes #331.
            // 注意: `new` は除外しない。`new void Hidden()` は C# のメンバー隠蔽宣言として有効。
            new("function",  new Regex($@"^\s*(?!\[\s*(?:assembly|module|type|return|param|field|property|event|method)\s*:)(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|sealed|partial|readonly|unsafe|extern|virtual|override|abstract|new|file|ref(?:\s+readonly)?)\s+)*async\s+(?<returnType>(?=[\w@?.<>\[\],:\s]*IAsync(?:Enumerable|Enumerator)\b){CSharpTypePattern})\s+(?!(?:base|this)\b)(?<name>{CSharpIdentifierPattern})\s*{CSharpMethodTypeParameterListPattern}\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility", "returnType"),
            new("function",  new Regex($@"^\s*(?!\[\s*(?:assembly|module|type|return|param|field|property|event|method)\s*:)(?![?:])(?!(?:await|return|throw|yield|var|typeof|sizeof|nameof|default|if|for|foreach|while|switch|catch|lock|using|case|else|when|break|continue|goto|from|where|select|orderby|group|join|let|into|on|equals|ascending|descending|by)\b)(?!\s*(?:(?:{CSharpVisibilityPattern}|static|sealed|partial|readonly|unsafe|extern|virtual|override|abstract|async|new|file|ref(?:\s+readonly)?)\s+)*delegate\b(?!\s*\*))(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|sealed|partial|readonly|unsafe|extern|virtual|override|abstract|async|new|file|ref(?:\s+readonly)?)\s+)*(?!{CSharpNonTypeKeywordPattern})(?<returnType>{CSharpTypePattern})\s+(?!(?:base|this)\b)(?<name>{CSharpIdentifierPattern})\s*{CSharpMethodTypeParameterListPattern}\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility", "returnType"),
            new("lambda",    new Regex($@"^\s*(?:var|{CSharpTypePattern})\s+(?<name>{CSharpIdentifierPattern})\s*=\s*(?:async\s+)?(?:\([^)]*\)|{CSharpIdentifierPattern})\s*=>", RegexOptions.Compiled), BodyStyle.None),
            // Constructor (no return type, name followed by parenthesis) — needs visibility.
            // `unsafe` / `extern` can appear before or after visibility, and C# 14 partial
            // constructors place `partial` after visibility, so declarations like
            // `unsafe public S(int* p) {}`, `extern public S(int x);`, and `public partial S();`
            // are still captured with visibility populated. Closes #355.
            // The negative lookahead after the opening paren rejects lines where the matching
            // `)` is followed by an identifier + `{` / `(` / `;` / `=>` / `=` (with optional
            // tuple-type suffixes `?` / `[]` / `[,]` / `[,,]` and whitespaced variants like
            // `) []` / `) ?` in between via CSharpTupleSuffixPattern), which is the shape of a
            // property with a modifier + tuple return type (`public required (int, int) R1
            // { get; init; }`, `public required (int, int) [] R4 { get; init; }`), an
            // expression-bodied method with a modifier (`public readonly (int, int)? M() =>
            // null;`, `public readonly (int, int) ? M3() => default;`), or a plain field with a
            // modifier + tuple type — both the uninitialized form (`public readonly (int, int) ?
            // F5;`, terminated by `;`) and the initialized form (`public readonly (int, int) ?
            // F4 = null;`, terminated by `=` excluding `==` / `=>`). A plain ctor signature
            // cannot match because there is no identifier between the closing `)` and the body
            // opener. The plain-field shapes are covered because #400's same-line plain-field
            // advance no longer sets stopAfterFirstPatternMatch, so the ctor regex now runs on
            // lines the plain-field pattern already claimed and would otherwise re-emit a phantom
            // `function readonly` ctor row. Using a positional check (not a keyword deny-list)
            // preserves support for legal (though unusual) type names that collide with
            // contextual keywords. Multi-line ctor signatures where the closing `)` is on a
            // later line are unaffected because the lookahead only triggers when a `)` is
            // visible on the current line. Sharing CSharpTupleSuffixPattern with CSharpTypePattern
            // keeps the ctor lookahead and the upstream property / method / plain-field rows in
            // sync on which formatting variants count as a tuple-suffix return type. Closes #349.
            // コンストラクタ（戻り値なし、名前の後に括弧）— visibility 必須。
            // `unsafe` / `extern` は visibility の前後どちらにも置け、C# 14 の partial
            // constructor は visibility の後ろに `partial` を置くため、
            // `unsafe public S(int* p) {}`、`extern public S(int x);`、`public partial S();`
            // でも visibility を拾える。Closes #355.
            // 開き括弧の直後に置いた否定先読みは、「対応する `)` のあとに識別子 + `{` / `(` / `;` /
            // `=>` / `=`（間に `?` / `[]` / `[,]` / `[,,]` の tuple サフィックス、および
            // CSharpTupleSuffixPattern によって `) []` / `) ?` のような空白を挟んだ整形バリエーションも
            // 許す）」形の行を弾く。これは `public required (int, int) R1 { get; init; }` や
            // `public required (int, int) [] R4 { get; init; }` のような modifier 付き property、
            // `public readonly (int, int)? M() => null;` や `public readonly (int, int) ? M3() => default;`
            // のような modifier 付き式形式メソッド、および modifier 付き tuple 型の plain field —
            // `public readonly (int, int) ? F5;` のような未初期化（`;` 終端）形、
            // `public readonly (int, int) ? F4 = null;` のような初期化（`=` 終端、`==` / `=>` は除外）形 —
            // であり、従来はいずれも `required` / `readonly` を ctor 名として greedy に喰っていた。
            // 通常の ctor シグネチャでは閉じ括弧と本体開始の間に識別子が入らないためマッチし続ける。
            // plain field 形が対象に入ったのは、#400 の同一行 plain-field 前進が
            // stopAfterFirstPatternMatch をセットしなくなったため、ctor 正規表現が plain-field
            // パターン既取得の行にも再走して phantom `function readonly` を再発する経路ができたため。
            // キーワード deny-list ではなく位置検査なので、contextual keyword と綴りが衝突する合法な
            // 型名のコンストラクタも弾かない。複数行にまたがる ctor シグネチャ（閉じ括弧が次行以降にある場合）は、
            // 現在行に `)` が出ないため lookahead が発動せずそのままマッチする。
            // CSharpTupleSuffixPattern を CSharpTypePattern と共有することで、ctor 否定先読みと上流の
            // property / method / plain-field 行が tuple サフィックス戻り値の受理形について常に一致する。Closes #349.
            new("function",  new Regex($@"^\s*(?:(?:unsafe|extern)\s+)*(?<visibility>{CSharpVisibilityPattern})\s+(?:(?:unsafe|extern|partial)\s+)*(?<name>{CSharpIdentifierPattern})\s*\((?!.*\){CSharpTupleSuffixPattern}\s*{CSharpIdentifierPattern}\s*(?:[{{(;]|=>|=(?![=>])))", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Partial method declaration with an omitted return type. Older partial-method
            // syntax can omit the accessibility modifier and still mean `void`; keep this
            // after the constructor row so `public partial Widget();` remains a constructor.
            // 戻り値型を省略した partial method 宣言。旧来の partial method 構文では
            // accessibility を省略し、戻り値型は `void` とみなされる。`public partial Widget();`
            // は constructor のまま扱うため、この行は constructor 行の後ろに置く。
            new("function",  new Regex($@"^\s*(?:(?:static|sealed|readonly|unsafe|extern|virtual|override|abstract|async|new|file|ref(?:\s+readonly)?)\s+)*(?<returnType>partial)\s+(?<name>{CSharpIdentifierPattern})\s*{CSharpMethodTypeParameterListPattern}\(", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            // Static constructor / 静的コンストラクタ
            // Keep this ahead of the property rows so same-line compact bodies such as
            // `class C { static C() { } public int P { get; set; } }` emit the static ctor
            // before the later property match short-circuits the pattern scan. The shape is
            // specific enough that it does not overlap with normal methods (no return type,
            // empty parameter list, optional `unsafe` around `static`). Closes #478.
            // 同一行のコンパクトな型本体
            // (`class C { static C() { } public int P { get; set; } }`) では、後続 property が
            // pattern scan を打ち切る前に static ctor を先に拾う必要があるため、property 行より前に置く。
            // この形は「戻り値型なし・引数なし・`static` 前後の任意 `unsafe`」に限定されるため、
            // 通常メソッドとは重ならない。Closes #478.
            new("function",  new Regex($@"^\s*(?:unsafe\s+)?static\s+(?:unsafe\s+)?(?<name>{CSharpIdentifierPattern})\s*\(\s*\)\s*\{{?", RegexOptions.Compiled), BodyStyle.Brace),
            // Property with get/set/init — visibility optional
            // Reject statement keywords (return/throw/switch/...) as the return type so that
            // multi-line statement fragments merged by BuildCSharpPropertyMatchLine — e.g.
            // `return o switch` combined with an opening `{` on the next line — are not
            // misclassified as a property. Closes #233.
            // プロパティ（get/set/init）— visibility 省略可
            // `return o switch` のような複数行にまたがる文断片が `BuildCSharpPropertyMatchLine`
            // で結合された結果、property として誤判定されるのを防ぐため、戻り値型として
            // ステートメントキーワードを拒否する。Closes #233.
            new("property",  new Regex($@"^\s*(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|virtual|override|abstract|sealed|new|required|partial|readonly|unsafe|extern|ref(?:\s+readonly)?)\s+)*(?!(?:class|struct|interface|enum|record|namespace|delegate|event|const|using|return|throw|yield|var|typeof|sizeof|nameof|default|if|for|foreach|while|switch|catch|lock|case|else|when|break|continue|goto|await)\b)(?<returnType>{CSharpTypePattern})\s+(?<name>{CSharpIdentifierPattern})\s*\{{", RegexOptions.Compiled), BodyStyle.Brace, "visibility", "returnType"),
            // Expression-bodied property (public int X => ...) — must come before delegate.
            // Uses BodyStyle.Brace so FindCSharpBraceRange detects '=>' and assigns a body
            // range covering the declaration line through the terminating ';', which
            // ReferenceExtractor.FindInnermostContainer needs to attribute accessor-internal
            // calls to the property rather than the enclosing class.
            // Closes #233.
            // 式本体プロパティ (public int X => ...) — delegate の前に配置。
            // `BodyStyle.Brace` にして `FindCSharpBraceRange` の '=>' 検出で宣言行から
            // 終端 ';' までを本体範囲として扱えるようにする。
            // ReferenceExtractor.FindInnermostContainer が accessor 内呼び出しを外側
            // クラスではなく property に帰属させるために必要。
            // Closes #233.
            new("property",  new Regex($@"^\s*(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|virtual|override|abstract|sealed|new|required|partial|readonly|unsafe|extern|ref(?:\s+readonly)?)\s+)*(?!(?:class|struct|interface|enum|record|namespace|delegate|event|const|using|return|throw|yield|var|typeof|sizeof|nameof|default|if|for|foreach|while|switch|catch|lock|case|else|when|break|continue|goto|await)\b)(?<returnType>{CSharpTypePattern})\s+(?<name>{CSharpIdentifierPattern})\s*=>\s*", RegexOptions.Compiled), BodyStyle.Brace, "visibility", "returnType"),
            // Delegate — visibility optional; modifier order is free. Accepts `static` / `unsafe` /
            // `file` (file-scoped delegate) / `new` (nested delegate hiding). Closes #355.
            // デリゲート — visibility 省略可。修飾子順序は自由。`static` / `unsafe` /
            // `file`（file スコープ delegate）/ `new`（ネスト delegate の隠蔽）を受け付ける。Closes #355.
            new("delegate",  new Regex($@"^\s*(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|unsafe|file|new)\s+)*delegate\s+(?<returnType>{CSharpTypePattern})\s+(?<name>{CSharpIdentifierPattern})\s*[\(<]", RegexOptions.Compiled), BodyStyle.None, "visibility", "returnType"),
            // Event — visibility optional; modifier order is free. Accepts `static` / `unsafe` /
            // `extern` plus inheritance modifiers (`virtual` / `override` / `abstract` / `sealed` / `new`)
            // which are all legal on event declarations per the C# spec. `partial` is also legal on
            // events (C# 14 field-like partial events, and extended partial member support on accessor
            // events), so accept it as well — otherwise every `partial event` declaration would be
            // silently dropped from symbols / definition / outline. Closes #350.
            // イベント — visibility 省略可。修飾子順序は自由。`static` / `unsafe` / `extern` に加え、
            // C# 仕様で event 宣言に有効な継承修飾子 (`virtual` / `override` / `abstract` / `sealed` / `new`)
            // も受け付ける。event には `partial` も合法 (C# 14 field-like partial event、およびアクセサ
            // ベースの partial member 拡張) なので、ここでも受け付けないと `partial event` 宣言が
            // symbols / definition / outline から無言で欠落する。Closes #350.
            new("event",     new Regex($@"^\s*(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|unsafe|extern|virtual|override|abstract|sealed|new|partial)\s+)*event\s+(?<returnType>{CSharpTypePattern})\s+(?<name>{CSharpIdentifierPattern})\s*(?:[;=]|\{{)", RegexOptions.Compiled), BodyStyle.None, "visibility", "returnType"),
            // Explicit interface event implementation (e.g. event EventHandler IFoo.Changed)
            // must capture the trailing member name rather than dropping the declaration or
            // inventing the qualifier as the event name. BodyStyle.Brace lets accessor blocks
            // on the same line or following lines share the normal brace-range path.
            // 明示的インターフェース event 実装 (例: event EventHandler IFoo.Changed) は、
            // qualifier 側ではなく末尾のメンバー名を event 名として捕捉しなければならない。
            // BodyStyle.Brace を使い、同一行/次行どちらの accessor block も通常の brace-range
            // 経路で扱う。
            new("event",     new Regex($@"^\s*(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|unsafe|extern|virtual|override|abstract|sealed|new|partial)\s+)*event\s+(?<returnType>{CSharpTypePattern})\s+{CSharpExplicitInterfaceQualifierPattern}\s*\.\s*(?<name>{CSharpIdentifierPattern})\b", RegexOptions.Compiled), BodyStyle.Brace, "visibility", "returnType"),
            // Explicit interface implementation (e.g. void IDisposable.Dispose())
            // Requires a valid return type (not a statement keyword) and interface name before the dot.
            // Reject named-argument labels only when they are followed by a qualified call site,
            // so alias-qualified types like `global::System.String` and `Alias::Type` still match.
            // LINQ query-expression keywords are also excluded from the negative lookahead so that
            // continuation lines like `where Validator.Check(x)` / `select Mapper.Convert(x)` /
            // `orderby Math.Abs(x)` do not match as `returnType + interface.member`. `new` is also
            // excluded so expression statements like `new System.Text.StringBuilder().Append(...)`
            // or `new Outer.Inner().Consume()` do not masquerade as an explicit interface method
            // (returnType=`new`, interface=the dot-chain qualifier preceding the constructed type
            // — which may be a namespace prefix like `System.Text`, an enclosing-type chain like
            // `Outer` in `new Outer.Inner()` where `Outer` is an outer class, or a mix of both
            // like `MyApp.Outer` in `new MyApp.Outer.Inner()` where `MyApp` is a namespace and
            // `Outer` is an enclosing type; the regex does not distinguish which segments are
            // namespaces and which are enclosing types at this position — and name=the
            // identifier right before the first `(`, i.e. the type being constructed:
            // `StringBuilder` / `Inner`; the trailing `.Append(...)` / `.Consume()` chain is
            // never part of the capture because the regex stops at the first `(`).
            // Closes #362, #377.
            // 明示的インターフェース実装 (例: void IDisposable.Dispose())
            // 有効な戻り値型（ステートメントキーワードではない）とドット前のインターフェース名を要求。
            // qualified call site を伴う named-argument label のみ除外し、
            // `global::System.String` や `Alias::Type` のような alias-qualified 型は許可する。
            // `new` も除外して、`new System.Text.StringBuilder().Append(...)` や
            // `new Outer.Inner().Consume()` のような式文が、returnType=`new` /
            // interface=構築型の手前のドット連鎖修飾子（namespace `System.Text` / 外側クラス
            // `Outer` のみ / namespace と外側型の混在 `MyApp.Outer`（`MyApp` が namespace、
            // `Outer` が外側型）のいずれでもよく、正規表現はこの位置で namespace と外側型を
            // 区別しない）/ name=構築される型（最初の `(` の直前の識別子、例: `StringBuilder`
            // / `Inner`。正規表現は最初の `(` で止まるので、末尾の `.Append(...)` /
            // `.Consume()` チェーンはキャプチャされない）として
            // 明示的インターフェースメソッドに化けないようにする。
            new("function",  new Regex($@"^\s*(?![?:])(?!(?:await|return|throw|yield|var|typeof|sizeof|nameof|default|if|for|foreach|while|switch|catch|lock|using|case|else|when|break|continue|goto|new|from|where|select|orderby|group|join|let|into|on|equals|ascending|descending|by)\b)(?!\w+\s*:\s*(?:global::)?[\w@.<>:]+\.\w+\s*{CSharpMethodTypeParameterListPattern}[\(\[])(?:(?<refModifier>ref(?:\s+readonly)?)\s+)?(?<returnType>{CSharpTypePattern})\s+{CSharpExplicitInterfaceQualifierPattern}\.(?<name>{CSharpIdentifierPattern})\s*{CSharpMethodTypeParameterListPattern}[\(\[]", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            // Explicit interface property implementation (brace body), e.g. int IThing.Value { get; set; }
            // Mirrors the explicit-interface method row above: the qualifier is non-capturing so the
            // short property name (Value) is recorded as name, consistent with how the method row
            // exposes Dispose/CompareTo instead of the qualified form. Closes #333.
            // 明示的インターフェースプロパティ実装（ブレース本体）。例: int IThing.Value { get; set; }
            // 上の明示的インターフェースメソッド行と同じ構造で、修飾子は非キャプチャにしてショート名
            // (Value) のみを name として記録する。メソッド側が Dispose / CompareTo を返すのと揃える。
            // Closes #333.
            new("property",  new Regex($@"^\s*(?![?:])(?!(?:class|struct|interface|enum|record|namespace|delegate|event|const|using|return|throw|yield|var|typeof|sizeof|nameof|default|if|for|foreach|while|switch|catch|lock|case|else|when|break|continue|goto|await)\b)(?:(?<refModifier>ref(?:\s+readonly)?)\s+)?(?<returnType>{CSharpTypePattern})\s+{CSharpExplicitInterfaceQualifierPattern}\.(?<name>{CSharpIdentifierPattern})\s*\{{", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            // Explicit interface property implementation (expression body), e.g. string IThing.Name => "x";
            // 明示的インターフェースプロパティ実装（式本体）。例: string IThing.Name => "x";
            new("property",  new Regex($@"^\s*(?![?:])(?!(?:class|struct|interface|enum|record|namespace|delegate|event|const|using|return|throw|yield|var|typeof|sizeof|nameof|default|if|for|foreach|while|switch|catch|lock|case|else|when|break|continue|goto|await)\b)(?:(?<refModifier>ref(?:\s+readonly)?)\s+)?(?<returnType>{CSharpTypePattern})\s+{CSharpExplicitInterfaceQualifierPattern}\.(?<name>{CSharpIdentifierPattern})\s*=>\s*", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            // Indexer (this[...]) — `partial` is legal on indexers since C# 13 (extended partial
            // member support), so accept it alongside the other modifiers. Otherwise every
            // `partial` indexer declaration would be silently dropped from symbols / definition /
            // outline. Closes #350.
            // インデクサ (this[...]) — C# 13 で indexer に対しても `partial` が使える (partial
            // member 拡張) ため、他の修飾子と並べて受け付ける。そうしないと `partial` indexer 宣言
            // が symbols / definition / outline から無言で欠落する。Closes #350.
            new("function",  new Regex($@"^\s*(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|virtual|override|abstract|sealed|new|readonly|unsafe|extern|partial|ref(?:\s+readonly)?)\s+)*(?<returnType>{CSharpTypePattern})\s+(?<name>this)\s*\[", RegexOptions.Compiled), BodyStyle.Brace, "visibility", "returnType"),
            // Finalizer (destructor) / ファイナライザ（デストラクタ）
            new("function",  new Regex($@"^\s*~(?<name>{CSharpIdentifierPattern})\s*\(\s*\)", RegexOptions.Compiled), BodyStyle.Brace),
            // Enum member (e.g. Red, Green = 1,) — requires 4+ spaces indent, name only,
            // and optional = with numeric/hex/identifier value. Does NOT match string/object assignments.
            // enum メンバー（例: Red, Green = 1,）— 4+スペースインデント必須、名前のみ、
            // 数値/16進/識別子の値指定はオプション。文字列/オブジェクト代入にはマッチしない。
            new("enum",      CSharpEnumMemberRegex, BodyStyle.None),
            // #region for navigation / ナビゲーション用 #region
            new("namespace", new Regex(@"^\s*#region\s+(?<name>.+)$", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["go"] =
        [
            new("namespace", new Regex(@"^\s*package\s+(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^func\s+(?:\([^)]+\)\s+)?(?<name>\w+)(?:\[[^\]\r\n]+\])?\s*[\(\[]", RegexOptions.Compiled), BodyStyle.Brace),
            new("lambda",   new Regex(@"^\s*(?<name>\w+)\s*(?::=|=)\s*func\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            new("struct",   new Regex(@"^type\s+(?<name>\w+)(?:\[[^\]]+\])?\s+struct\b", RegexOptions.Compiled), BodyStyle.Brace),
            new("protocol", new Regex(@"^type\s+(?<name>\w+)(?:\[[^\]]+\])?\s+interface\b", RegexOptions.Compiled), BodyStyle.Brace),
            // Type alias (type Name = OtherType or type Name OtherType) / 型エイリアス
            new("import",   new Regex(@"^type\s+(?<name>\w+)(?:\[[^\]]+\])?\s+[=\w]", RegexOptions.Compiled), BodyStyle.None),
            // Top-level const declarations / トップレベル const 宣言
            new("property", new Regex(@"^const\s+(?<name>\w+)(?:\s+\w[\w.*\[\]]*)?\s*=", RegexOptions.Compiled), BodyStyle.None),
            // Const declaration inside const block / const ブロック内の定数宣言
            new("property", new Regex(@"^\s+(?<name>[A-Z]\w*)\s*=\s*", RegexOptions.Compiled), BodyStyle.None),
            // Package-level var / パッケージレベル変数
            new("property", new Regex(@"^var\s+(?<name>\w+)\s", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["fortran"] =
        [
            // Named interfaces / 名前付き interface
            new("namespace", new Regex(@"^\s*interface\s+(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.FortranEnd),
            // Fortran modules / モジュール
            new("namespace", new Regex(@"^\s*module\s+(?!(?:procedure|subroutine|function)\b)(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.FortranEnd),
            // Fortran submodules / サブモジュール
            new("namespace", new Regex(@"^\s*submodule\s*\(\s*[^)]*\)\s*(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.FortranEnd),
            // Program units / プログラム本体
            new("class", new Regex(@"^\s*program\s+(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.FortranEnd),
            // Block data program units / block data プログラム単位
            new("class", new Regex(@"^\s*block\s+data\s+(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.FortranEnd),
            // Derived types / 派生型
            new("class", new Regex(@"^\s*type(?!\s*\()\b(?:\s*,\s*(?:abstract|public|private|sequence|bind\s*\([^)]+\)|extends\s*\([^)]+\)))*\s*(?:::)?\s*(?!(?:is|default)\b)(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.FortranEnd),
            // Enumerators / enumerator 定数
            new("property", new Regex(@"^\s*enumerator(?:\s*::)?\s*(?<name>[A-Za-z_]\w*)(?<enumTail>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Parameter constants / parameter 定数
            new("property", new Regex(@"^\s*(?:(?:integer|real|logical|complex)(?:\s*\([^)]+\))?|character(?:\s*\([^)]+\))?|double\s+precision|type\s*\([^)]+\)|class\s*\([^)]+\))\s*,[^:\r\n]*\bparameter\b[^:\r\n]*::\s*(?<name>[A-Za-z_]\w*)(?<paramTail>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Old-style parameter constants / 旧形式 parameter 定数
            new("property", new Regex(@"^\s*parameter\s*\(\s*(?<name>[A-Za-z_]\w*)(?<paramTail>.*)\)\s*(?:!.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Typed variables and components / 型付き変数・component
            new("property", new Regex(@"^\s*(?<returnType>(?:(?:integer|real|logical|complex)(?:\s*\([^)]+\))?|character(?:\s*\([^)]+\))?|double\s+precision|type\s*\([^)]+\)|class\s*\([^)]+\)))\s*(?:,\s*(?![^:\r\n]*\bparameter\b)[^:\r\n]*)?::\s*(?<name>[A-Za-z_]\w*)(?<paramTail>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None, ReturnTypeGroup: "returnType"),
            // Old-style typed variables without :: / :: なしの旧形式型付き変数
            new("property", new Regex(@"^\s*(?<returnType>(?:(?:integer|real|logical|complex)(?:\s*\([^)]+\))?|character(?:\s*\([^)]+\))?|double\s+precision|type\s*\([^)]+\)|class\s*\([^)]+\)))\s+(?!(?:function|subroutine)\b)(?<name>[A-Za-z_]\w*)(?<paramTail>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None, ReturnTypeGroup: "returnType"),
            // Attribute-only variables / 属性のみの変数宣言
            new("property", new Regex(@"^\s*(?:(?:allocatable|pointer|target|optional|save|dimension\s*\([^)]+\)|intent\s*\([^)]+\))\s*,\s*)*(?:allocatable|pointer|target|optional|save|dimension\s*\([^)]+\)|intent\s*\([^)]+\))\s*(?:::)?\s*(?<name>[A-Za-z_]\w*)(?<paramTail>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Common block members / common block メンバー
            new("property", new Regex(@"^\s*common\s+(?:/\s*[A-Za-z_]\w*\s*/\s*)?(?<name>[A-Za-z_]\w*)(?<paramTail>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Namelist members / namelist メンバー
            new("property", new Regex(@"^\s*namelist\s+/\s*[A-Za-z_]\w*\s*/\s*(?<name>[A-Za-z_]\w*)(?<paramTail>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Subroutines / サブルーチン
            new("function", new Regex(@"^\s*(?:(?:pure|elemental|recursive|module|impure)\s+)*subroutine\s+(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.FortranEnd),
            // Entry points / entry 手続き
            new("function", new Regex(@"^\s*entry\s+(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Module procedure implementations / module procedure 実装
            new("function", new Regex(@"^\s*module\s+procedure\s+(?<name>[A-Za-z_]\w*)\s*(?:!.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.FortranEnd),
            // Procedure declarations in interfaces / interface 内の手続き宣言
            new("function", new Regex(@"^\s*(?:(?:pure|elemental|recursive|impure)\s+)*(?:(?:module\s+)?procedure)(?:\s*\([^)]+\))?(?:\s*,\s*[A-Za-z_]\w*)*\s*(?:::\s*)?(?<name>[A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Typed or untyped functions / 型付き・型なし関数
            new("function", new Regex(@"^\s*(?:(?:pure|elemental|recursive|module|impure)\s+)*(?:(?:(?:integer|real|logical|complex)(?:\s*\([^)]+\))?|character(?:\s*\([^)]+\))?|double\s+precision|type\s*\([^)]+\)|class\s*\([^)]+\)|procedure\s*\([^)]+\))\s+)?function\s+(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.FortranEnd),
        ],
        ["rust"] =
        [
            // macro_rules! / マクロ定義
            new("function", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?macro_rules!\s+(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // const/static items / 定数・静的変数
            new("function", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?(?:const|static)\s+(?<name>(?:r#)?\w+)\s*:", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            // fn with expanded modifiers: async, const, unsafe, default, extern (ABI optional) /
            // 拡張修飾子: async, const, unsafe, default, extern（ABI は省略可）
            new("function", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?(?:(?:async|const|unsafe|default|extern(?:\s+""[^""]+"")?)\s+)*fn\s+(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("class",    new Regex(@"\b(?<name>unsafe)\s*\{", RegexOptions.Compiled), BodyStyle.Brace),
            new("struct",   new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?(?:struct|union)\s+(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("enum",     new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?enum\s+(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Enum variants / `Red`, `Ok(T)`, `Circle { radius: f64 }`, `Point`
            new("property", new Regex(@"^\s{4,}(?<name>[A-Z][A-Za-z0-9_]*)\s*(?:\([^()\r\n]*\)|\{[^{}\r\n]*\})?\s*,?\s*$", RegexOptions.Compiled), BodyStyle.None),
            new("protocol", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?trait\s+(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // impl Trait for Type / `unsafe impl Trait for Type` should attach to the type being extended.
            // `impl Trait for Type` / `unsafe impl Trait for Type` は、拡張先の型に紐づける。
            new("class",    new Regex(@"^\s*(?:unsafe\s+)?impl(?:<[^>]+>)?\s+.+?\s+for\s+(?:(?:r#)?\w+::)*(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*(?:unsafe\s+)?impl(?:<[^>]+>)?\s+(?:(?:r#)?\w+::)*(?<name>(?:r#)?\w+)(?!\s+for\b)", RegexOptions.Compiled), BodyStyle.Brace),
            // file module declarations and inline modules / ファイルモジュール宣言とインラインモジュール
            new("file_module", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?mod\s+(?<name>(?:r#)?\w+)\s*;", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("namespace", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?mod\s+(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Trait associated type defaults / trait 関連型のデフォルト
            new("property", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?type\s+(?<name>(?:r#)?\w+)(?:\s*<[^=>]+>)?(?:\s*:\s*[^=;]+)?\s*=\s*(?<returnType>[^;]+)", RegexOptions.Compiled), BodyStyle.None, "visibility", "returnType"),
            // type alias / 型エイリアス
            new("import",   new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?type\s+(?<name>(?:r#)?\w+)(?:\s*<[^=]+>)?", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("import",   new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?use\s+(?<name>.+);", RegexOptions.Compiled), BodyStyle.None, "visibility"),
        ],
        ["java"] =
        [
            // Package declaration / package 宣言
            new("namespace", new Regex($@"^\s*package\s+(?<name>{JavaQualifiedIdentifierPattern})\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            // Module declaration (Java 9+ module-info.java) / モジュール宣言（Java 9+ の module-info.java）
            new("namespace", new Regex($@"^\s*(?:open\s+)?module\s+(?<name>{JavaQualifiedIdentifierPattern})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // Annotation type (@interface) / アノテーション型
            new("class",    new Regex($@"^\s*(?<visibility>public|private|protected)?\s*@interface\s+(?<name>{JavaIdentifierPattern})", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, "visibility"),
            // record (Java 16+) — must come before general class pattern / record は一般クラスパターンの前に配置
            new("class",    new Regex($@"^\s*(?<visibility>public|private|protected)?\s*(?:(?:static|final|abstract|sealed|non-sealed|strictfp)\s+)*record\s+(?<name>{JavaIdentifierPattern})", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, "visibility"),
            // Interface / インターフェース
            new("interface", new Regex($@"^\s*(?<visibility>public|private|protected)?\s*(?:(?:static|abstract|sealed|non-sealed|strictfp)\s+)*interface\s+(?<name>{JavaIdentifierPattern})", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, "visibility"),
            // Enum / enum
            new("enum",     new Regex($@"^\s*(?<visibility>public|private|protected)?\s*(?:(?:static|strictfp)\s+)*enum\s+(?<name>{JavaIdentifierPattern})", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, "visibility"),
            // Class — with extended modifiers (final, sealed, static, abstract, strictfp)
            // クラス — 拡張修飾子対応（final, sealed, static, abstract, strictfp）
            new("class",    new Regex($@"^\s*(?<visibility>public|private|protected)?\s*(?:(?:static|final|abstract|sealed|non-sealed|strictfp)\s+)*class\s+(?<name>{JavaIdentifierPattern})", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, "visibility"),
            // Static final field (Java equivalent of C# const) — order-flexible and annotation-friendly.
            // static final フィールド — 語順柔軟かつアノテーション併用にも対応。
            new("function", new Regex($@"^\s*(?:@\w+(?:\([^)]*\))?\s+)*(?<visibility>public|private|protected)?\s*(?=(?:(?:static|final|transient|volatile)\s+)*static\b)(?=(?:(?:static|final|transient|volatile)\s+)*final\b)(?:(?:static|final|transient|volatile)\s+)*(?<returnType>{JavaReturnTypePattern})\s+(?<name>[A-Z_]\w*)\s*=", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None, "visibility", "returnType"),
            // Method with return type — expanded modifiers (default, native, synchronized, final)
            // 戻り値型付きメソッド — 拡張修飾子対応（default, native, synchronized, final）
            new("function", new Regex($@"^\s*(?!(?:return|throw|new|if|for|while|switch|do|case|else|try|catch|finally|synchronized|break|continue|yield|assert)\b)(?:@\w+(?:\([^)]*\))?\s+)*(?<visibility>public|private|protected)?\s*(?:(?:static|abstract|synchronized|final|default|native|strictfp)\s+)*(?!(?:record)\b){JavaMethodTypeParameterPattern}(?<returnType>{JavaReturnTypePattern})\s+(?<name>{JavaIdentifierPattern})\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, "visibility", "returnType"),
            // Enum members are extracted by ExtractJavaEnumMembers using a body-scoped scanner,
            // which handles any indent style (tab, 2-space, 4-space) and skips member-like lines
            // outside the enum body (e.g. `\tRED();` method calls inside a class body).
            // enum メンバーは ExtractJavaEnumMembers の body-scoped scanner で抽出する。
            // 任意のインデントスタイル（タブ、2スペース、4スペース）に対応しつつ、enum 本体外の
            // メンバー風の行（例: クラス本体内の `\tRED();` メソッド呼び出し）を誤検出しない。
            new("import",   new Regex(@"^\s*import\s+(?<name>.+);", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["kotlin"] =
        [
            // Companion object / コンパニオンオブジェクト
            new("class",    new Regex($@"^\s*companion\s+object(?:\s+(?<name>{KotlinIdentifierPattern}))?", RegexOptions.Compiled), BodyStyle.Brace),
            // Interface / インターフェース
            // Kotlin fun interface / Kotlin の fun interface も interface として扱う。
            new("interface", new Regex($@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:sealed|expect|actual)\s+)*(?:fun\s+)?interface\s+(?<name>{KotlinIdentifierPattern})", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Enum class / enum クラス
            new("enum",     new Regex($@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:expect|actual)\s+)*enum\s+class\s+(?<name>{KotlinIdentifierPattern})", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Class/object with expanded modifiers: data, sealed, value, inline, inner, annotation, expect, actual
            // クラス/オブジェクト — 拡張修飾子対応: data, sealed, value, inline, inner, annotation, expect, actual
            new("class",    new Regex($@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:abstract|data|sealed|open|inner|value|inline|annotation|expect|actual)\s+)*(?:class|object)\s+(?<name>{KotlinIdentifierPattern})", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Function / 関数 (including extension, secondary constructor, override, and abstract forms)
            // 関数 — 拡張・セカンダリコンストラクタ・override・abstract 形を含む
            new("function", new Regex($@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:suspend|inline|infix|operator|tailrec|external|expect|actual|abstract|override|open|final)\s+)*fun\s+(?:<[^>]+>\s+)?(?:{KotlinIdentifierPattern}(?:<[^>]+>)?\.)?(?<name>{KotlinIdentifierPattern})\s*[\(<](?:.*?\))?(?::\s*(?<returnType>[^ {{=]+))?", RegexOptions.Compiled), BodyStyle.Brace, "visibility", "returnType"),
            // Secondary constructor / セカンダリコンストラクタ
            new("function", new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*constructor\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Enum entry / enum エントリ
            new("property", new Regex($@"^\s{{2,}}(?<name>(?:[A-Z][A-Z0-9_]*|`[^`\r\n]+`))\s*(?:\((?<returnType>[^)]*)\))?\s*(?:,|\{{|;)?\s*$", RegexOptions.Compiled), BodyStyle.Brace, "returnType"),
            // Top-level val/var property / トップレベルプロパティ
            new("property", new Regex($@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:const|lateinit|override)\s+)?(?:val|var)\s+(?<name>{KotlinIdentifierPattern})\s*[=:]", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            // Type alias / 型エイリアス
            new("import",   new Regex($@"^\s*(?<visibility>public|private|protected|internal)?\s*typealias\s+(?<name>{KotlinIdentifierPattern})(?:\s*<[^=]+>)?\s*=", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("import",   new Regex(@"^\s*import\s+(?<name>.+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["ruby"] =
        [
            // attr_accessor/attr_reader/attr_writer as property declarations / プロパティ宣言
            new("property", new Regex(@"^\s*attr_(?:accessor|reader|writer)\s+:(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None),
            // alias_method / alias — capture the introduced method name for navigation
            new("function", new Regex(@"^\s*alias_method\b\s+:?(?<name>\w+[?!=]?)\s*,\s*:?\w+[?!=]?", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*alias\b\s+:?(?<name>\w+[?!=]?)\s+:?\w+[?!=]?", RegexOptions.Compiled), BodyStyle.None),
            // scope/has_many/belongs_to (Rails DSL) — extracted as function for navigation
            new("function", new Regex(@"^\s*(?:scope|has_many|has_one|belongs_to)\s+:(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None),
            new("property", new Regex(@"^\s*enum\s+:(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None),
            new("property", new Regex(@"^\s*attribute\s+:(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None),
            new("property", new Regex(@"^\s*store_accessor\s+:\w+\s*,\s*:(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None),
            new("namespace", new Regex(@"^\s*namespace\s+:(?<name>\w+)\b.*\bdo\b", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("function", new Regex(@"^\s*factory\s+:(?<name>\w+)\b.*\bdo\b", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("function", new Regex(@"^\s*shared_examples(?:_for)?\s+(?<quote>['""])(?<name>[^'""]+)\k<quote>\s*do\b", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("property", new Regex(@"^\s*subject\s*\(\s*:(?<name>\w+)\s*\)\s*do\b", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("property", new Regex(@"^\s*let!?\s*\(\s*:(?<name>\w+)\s*\)\s*do\b", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("function", new Regex(@"^\s*task\s+(?::(?<name>\w+)|(?<name>\w+)\s*:)", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?<name>[A-Z][A-Za-z0-9_]*)\s*=\s*Class\.new\b.*\bdo\b", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("class",    new Regex(@"^\s*(?<name>[A-Z][A-Za-z0-9_]*)\s*=\s*Struct\.new\b.*\bdo\b", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("property", new Regex(@"^\s*(?<name>[A-Z][A-Za-z0-9_]*)\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?:private|protected|public)\s+)?def\s+(?:(?:self|[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)\.)?(?<name>\[\]=?|\*\*|<<|>>|<=>|===|==|!=|!~|=~|<=|>=|[+\-*/%&|^~<>]=?|[+\-]@|!)", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("function", new Regex(@"^\s*(?:(?:private|protected|public)\s+)?def\s+(?:(?:self|[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)\.)?(?<name>\w+[?!=]?)", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("class",    new Regex(@"^\s*class\s+(?<name>[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("class",    new Regex(@"^\s*module\s+(?<name>[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("import",   new Regex(@"^\s*require(?:_relative)?\s+(?<name>.+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["crystal"] =
        [
            new("namespace", new Regex(@"^\s*module\s+(?<name>[A-Z]\w*(?:::[A-Z]\w*)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.RubyEnd),
            new("class",    new Regex(@"^\s*(?:abstract\s+)?class\s+(?<name>[A-Z]\w*(?:::[A-Z]\w*)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.RubyEnd),
            new("struct",   new Regex(@"^\s*struct\s+(?<name>[A-Z]\w*(?:::[A-Z]\w*)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.RubyEnd),
            new("enum",     new Regex(@"^\s*enum\s+(?<name>[A-Z]\w*(?:::[A-Z]\w*)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.RubyEnd),
            new("function", new Regex(@"^\s*(?:(?:private|protected)\s+)*abstract\s+def\s+(?:self\.)?(?<name>[A-Za-z_]\w*[?!=]?)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:private\s+|protected\s+)?def\s+(?:self\.)?(?<name>[A-Za-z_]\w*[?!=]?)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.RubyEnd),
            new("function", new Regex(@"^\s*macro\s+(?<name>[A-Za-z_]\w*[?!=]?)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.RubyEnd),
            new("typealias", new Regex(@"^\s*alias\s+(?<name>[A-Z]\w*)\s*=", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",   new Regex(@"^\s*require\s+(?<name>.+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["groovy"] =
        [
            new("namespace", new Regex(@"^\s*package\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("interface", new Regex(@"^\s*(?:(?:public|private|protected|static|abstract)\s+)*(?:interface|trait)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*(?:(?:public|private|protected|static)\s+)*enum\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*class\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?:@[A-Za-z_$][\w.$]*(?:\s*\([^)\r\n]*\))?\s+)*(?!(?:if|for|while|switch|catch|return|throw|new)\b)(?:(?:public|private|protected|static|final|abstract|synchronized|native|strictfp)\s+)*(?:<[^(){}\r\n]+>\s+)?(?<returnType>def|void|boolean|byte|char|short|int|long|float|double|BigDecimal|BigInteger|String|[A-Za-z_$][\w.$]*(?:\s*<[^(){}\r\n]+>)?(?:\s*\[\])*)\s+(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            new("lambda",   new Regex(@"^\s*(?:def\s+)?(?<name>[A-Za-z_]\w*)\s*=\s*\{", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*import\s+(?:static\s+)?(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*(?:\.\*)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["julia"] =
        [
            new("namespace", new Regex(@"^\s*(?:baremodule|module)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.ScientificEnd),
            new("struct",   new Regex(@"^\s*(?:mutable\s+)?struct\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.ScientificEnd),
            new("type",     new Regex(@"^\s*(?:abstract|primitive)\s+type\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.ScientificEnd),
            new("function", new Regex(@"^\s*function\s+(?<name>" + JuliaQualifiedCallableIdentifierPattern + @")\s*(?:\(|\{)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.ScientificEnd),
            new("function", new Regex(@"^\s*macro\s+(?<name>[A-Za-z_]\w*)\s*(?:\(|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.ScientificEnd),
            new("function", new Regex(@"^\s*(?<name>" + JuliaQualifiedCallableIdentifierPattern + @")\s*\([^)\r\n]*\)\s*(?:where\s*(?:\{[^}\r\n]*\}|[A-Za-z_]\w*)\s*)?=", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.JuliaShortFunction),
            new("property", new Regex(@"^\s*const\s+(?<name>[A-Z_]\w*)\s*=", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",   new Regex(@"^\s*(?:using|import)\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["tcl"] =
        [
            new("namespace", new Regex(@"^\s*namespace\s+eval\s+(?<name>[A-Za-z_:][\w:.-]*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex(@"^\s*oo::class\s+create\s+(?<name>[A-Za-z_:][\w:.-]*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*proc\s+(?<name>[A-Za-z_:][\w:.-]*)\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*(?:variable|set)\s+(?<name>[A-Za-z_:][\w:.-]*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",   new Regex(@"^\s*package\s+(?:require|provide)\s+(?<name>[A-Za-z_:][\w:.-]*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["ada"] =
        [
            new("namespace", new Regex(@"^\s*package\s+(?:body\s+)?(?<name>[A-Za-z]\w*(?:\.[A-Za-z]\w*)*)\s+is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.AdaEnd),
            new("type",      new Regex(@"^\s*(?:subtype|type)\s+(?<name>[A-Za-z]\w*)\s+is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("type",      new Regex(@"^\s*(?:task|protected)\s+(?:type\s+)?(?<name>[A-Za-z]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.AdaEnd),
            new("function",  new Regex(@"^\s*(?:(?:overriding|not\s+overriding)\s+)?(?:function|procedure)\s+(?:(?:[A-Za-z]\w*)\.)*(?<name>[A-Za-z]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.AdaEnd),
            new("import",    new Regex(@"^\s*with\s+(?<name>[A-Za-z]\w*(?:\.[A-Za-z]\w*)*)\s*;", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["d"] =
        [
            new("namespace", new Regex(@"^\s*module\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("interface", new Regex(@"^\s*(?:(?:public|private|protected|package|static|abstract|extern)\s+)*interface\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("class",     new Regex(@"^\s*(?:(?:public|private|protected|package|static|abstract|final|extern)\s+)*class\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("struct",    new Regex(@"^\s*(?:(?:public|private|protected|package|static|extern)\s+)*struct\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("union",     new Regex(@"^\s*(?:(?:public|private|protected|package|static|extern)\s+)*union\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("enum",      new Regex(@"^\s*(?:(?:public|private|protected|package|static)\s+)*enum\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("typealias", new Regex(@"^\s*(?:alias|typedef)\s+(?<name>[A-Za-z_]\w*)\s*=", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function",  new Regex(@"^\s*(?!(?:if|for|while|switch|catch|return|throw|new|assert|version|debug)\b)(?:(?:public|private|protected|package|static|extern|export|final|abstract|override|synchronized|pure|nothrow|@safe|@trusted|@system)\s+)*(?<returnType>(?:auto|void|bool|byte|ubyte|short|ushort|int|uint|long|ulong|cent|ucent|float|double|real|char|wchar|dchar|string|[A-Za-z_][\w.]*)(?:\s*[*\[\]])*)\s+(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            new("import",    new Regex(@"^\s*import\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["nim"] =
        [
            new("type",      new Regex(@"^\s*type\s+(?<name>[A-Za-z_]\w*)\*?\s*=\s*(?:ref\s+)?(?:object|enum|tuple|distinct)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Indent),
            new("type",      new Regex(@"^\s+(?<name>[A-Za-z_]\w*)\*?\s*=\s*(?:ref\s+)?(?:object|enum|tuple|distinct)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Indent),
            new("function",  new Regex(@"^\s*(?:proc|func|method|iterator|template|macro|converter)\s+(?<name>`[^`\r\n]+`|[A-Za-z_]\w*)\*?", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Indent),
            new("property",  new Regex(@"^\s*(?:const|let|var)\s+(?<name>[A-Za-z_]\w*)\*?\s*(?::|=)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",    new Regex(@"^\s*(?:import|include)\s+(?<name>[A-Za-z_][\w./]*(?:\s*,\s*[A-Za-z_][\w./]*)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",    new Regex(@"^\s*from\s+(?<name>[A-Za-z_][\w./]*)\s+import\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["perl"] =
        [
            // Perl package declarations / Perl の package 宣言
            new("namespace", new Regex(@"^\s*package\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b(?:\s+v?[\d._]+)?\s*\{", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("namespace", new Regex(@"^\s*package\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b(?:\s+v?[\d._]+)?\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            // Perl class feature declarations / Perl class feature の宣言
            new("class", new Regex(@"^\s*class\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("interface", new Regex(@"^\s*role\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // Perl constants are compile-time subroutines, so expose them as functions for navigation.
            // Perl constant はコンパイル時 subroutine なので、ナビゲーション用に function として出す。
            new("function", new Regex(@"^\s*use\s+constant\s+(?<name>" + PerlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            // Perl module imports / Perl の module import
            new("import", new Regex(@"^\s*use\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*require\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            // Moose/Moo attributes / Moose/Moo の属性
            new("property", new Regex(@"^\s*has\s+(?<quote>['""]?)\+?(?<name>" + PerlIdentifierPattern + @")\k<quote>\s*=>", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            // Package variables / package 変数
            new("property", new Regex(@"^\s*our\s+[$@%](?<name>" + PerlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            // Perl class feature fields / Perl class feature の field
            new("property", new Regex(@"^\s*field\s+[$@%](?<name>" + PerlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            // Perl subroutines / Perl の subroutine
            new("function", new Regex(@"^\s*(?:(?:my|state)\s+)?sub\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b(?:\s*:[^{;]+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?:method|fun)\s+(?<name>" + PerlIdentifierPattern + @")\b(?:\s*:[^{;]+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
        ],
        ["matlab"] =
        [
            new("class", new Regex(@"^\s*classdef\s*(?:\([^)]*\)\s*)?(?<name>[A-Za-z]\w*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.ScientificEnd),
            new("function", new Regex(@"^\s*function\s+(?:(?:\[[^\]]+\]|[A-Za-z]\w*)\s*=\s*)?(?<name>[A-Za-z]\w*)\s*(?:\(|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.ScientificEnd),
            new("import", new Regex(@"^\s*import\s+(?<name>[A-Za-z]\w*(?:\.[A-Za-z*]\w*)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["prolog"] =
        [
            new("namespace", new Regex(@"^\s*:-\s*module\s*\(\s*(?<name>[a-z][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*:-\s*use_module\s*\(\s*(?<name>[a-z][A-Za-z0-9_]*(?:\([^)]*\))?)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?<name>[a-z][A-Za-z0-9_]*)\s*(?:\([^\r\n]*\))?\s*(?::-|-->|\.(?=\s*(?:$|:-|[a-z][A-Za-z0-9_]*\s*\(\s*$|[a-z][A-Za-z0-9_]*(?:\s*\([^)]*\))?\s*(?::-|-->|\.))))", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["ambiguous_pl"] =
        [
            // Keep ambiguous .pl files structured without choosing Perl or Prolog prematurely.
            // .pl の判定が曖昧でも Perl / Prolog のどちらかへ早計に固定せず、構造を保持する。
            new("namespace", new Regex(@"^\s*package\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b(?:\s+v?[\d._]+)?\s*\{", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("namespace", new Regex(@"^\s*package\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b(?:\s+v?[\d._]+)?\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class", new Regex(@"^\s*class\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("interface", new Regex(@"^\s*role\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("function", new Regex(@"^\s*use\s+constant\s+(?<name>" + PerlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*use\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*require\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*our\s+[$@%](?<name>" + PerlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?:my|state)\s+)?sub\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b(?:\s*:[^{;]+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?:method|fun)\s+(?<name>" + PerlIdentifierPattern + @")\b(?:\s*:[^{;]+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("namespace", new Regex(@"^\s*:-\s*module\s*\(\s*(?<name>[a-z][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*:-\s*use_module\s*\(\s*(?<name>[a-z][A-Za-z0-9_]*(?:\([^)]*\))?)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?<name>[a-z][A-Za-z0-9_]*)\s*(?:\([^\r\n]*\))?\s*(?::-|-->|\.(?=\s*(?:$|:-|[a-z][A-Za-z0-9_]*\s*\(\s*$|[a-z][A-Za-z0-9_]*(?:\s*\([^)]*\))?\s*(?::-|-->|\.))))", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["c"] =
        [
            new("function", new Regex(CFunctionStartBlacklistPattern + CFunctionReturnTypePattern + CFunctionNameBlacklistPattern + @"(?<name>\w+)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            // #define macros / #define マクロ
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)\(", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)(?=\s|$)", RegexOptions.Compiled), BodyStyle.None),
            new("struct",   new Regex(@"^\s*typedef\s+struct\s+(?:\w+\s*)?(?:\*+\s*)?(?<name>\w+)\s*;", RegexOptions.Compiled), BodyStyle.None),
            new("struct",   new Regex(@"^\s*(?:typedef\s+)?struct\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("union",    new Regex(@"^\s*typedef\s+union\s+(?:\w+\s*)?(?:\*+\s*)?(?<name>\w+)\s*;", RegexOptions.Compiled), BodyStyle.None),
            new("union",    new Regex(@"^\s*(?:typedef\s+)?union\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*typedef\s+enum\s+(?:\w+\s+)?(?<name>\w+)\s*;", RegexOptions.Compiled), BodyStyle.None),
            new("enum",     new Regex(@"^\s*(?:typedef\s+)?enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*#\s*(?:include(?:_next)?|import)\s+(?:<(?<name>[^>]+)>|""(?<name>[^""]+)""|(?<name>[^\s]+))", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["cpp"] =
        [
            new("namespace", new Regex(CppFunctionStartBlacklistPattern + @"(?:export\s+)?module\s+(?<name>[\w.]+(?::[\w.]+)?)\b", RegexOptions.Compiled), BodyStyle.None),
            new("import", new Regex(CppFunctionStartBlacklistPattern + @"(?:export\s+)?import\s+(?:<(?<name>[^>\r\n]+)>|""(?<name>[^""\r\n]+)""|(?<name>:?[A-Za-z_]\w*(?:[.:][A-Za-z_]\w*)*))\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("namespace", new Regex(CppFunctionStartBlacklistPattern + @"inline\s+namespace\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("interface", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?:export\s+)?concept\s+(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.None),
            new("specialization", new Regex(CppFunctionStartBlacklistPattern + @"\s*template\s*<[^>]*>\s*(?:class|struct|union)\s+(?<name>(?:[A-Za-z_]\w*::)*[A-Za-z_]\w*)\s*<[^;{}]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("specialization", new Regex(CppFunctionStartBlacklistPattern + @"\s*template\s*<>\s*" + CppAttributePrefixPattern + @"(?:extern\s+""(?:C|C\+\+)""\s*)?" + CppAttributePrefixPattern + @"(?:(?<returnType>(?:(?:" + CppFunctionReturnTypeAtomPattern + @")[\s*&]+)+))?(?:(?:[\w:<>]+\s*::\s*)+)?" + CFunctionNameBlacklistPattern + @"(?<name>~?\w+|operator(?:\s*\(\)|\s*\[\]|\s*[^\s(]+(?:\s+[^\s(]+)?))\s*<[^>\r\n]+>\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            new("function", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + CppAttributePrefixPattern + @"(?:extern\s+""(?:C|C\+\+)""\s*)?" + CppAttributePrefixPattern + @"(?:(?<returnType>(?:(?:" + CppFunctionReturnTypeAtomPattern + @")[\s*&]+)+))?(?:(?:[\w:<>]+\s*::\s*)+)?" + CFunctionNameBlacklistPattern + @"(?<name>~?\w+|operator(?:\s*\(\)|\s*\[\]|\s*[^\s(]+(?:\s+[^\s(]+)?))(?:\s*<[^>]+>)?\s*\(", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            // Type alias / 型エイリアス
            new("import", new Regex(CppFunctionStartBlacklistPattern + @"using\s+enum\s+(?<name>(?:[A-Za-z_]\w*::)*[A-Za-z_]\w*)\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(CppFunctionStartBlacklistPattern + @"template\s*<[^>]+>\s*(?:export\s+)?using\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("import", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?:export\s+)?using\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("import", new Regex(CppFunctionStartBlacklistPattern + @"template\s*<[^>]+>\s*(?:export\s+)?using\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.Brace),
            new("import", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?:export\s+)?using\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.Brace),
            new("import", new Regex(@"^\s*typedef\s+(?![^;]*\().*\b(?<name>\w+)\s*;", RegexOptions.Compiled), BodyStyle.None),
            new("import", new Regex(@"^\s*typedef\s+(?![^;]*\().*\b(?<name>\w+)\s*;", RegexOptions.Compiled), BodyStyle.Brace),
            // #define macros / #define マクロ
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)\(", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)(?=\s|$)", RegexOptions.Compiled), BodyStyle.None),
            new("property", new Regex(@"^(?:export\s+)?(?:(?:inline|static)\s+)*constexpr\s+(?<returnType>(?:[\w:<>~]+(?:\s*[*&])?\s+)+)(?<name>(?:[A-Z_]\w*|k[A-Z]\w*))\s*=", RegexOptions.Compiled), BodyStyle.None, ReturnTypeGroup: "returnType"),
            new("property", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?<returnType>(?:[\w:<>~]+[\s*&]+)+)(?:(?:[\w:<>]+\s*::\s*)+)(?<name>\w+)\s*=\s*[^;]+;", RegexOptions.Compiled), BodyStyle.None, ReturnTypeGroup: "returnType"),
            new("class",    new Regex(CppFunctionStartBlacklistPattern + @"\s*(?:export\s+)?" + CppTemplatePrefixPattern + @"class\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("struct",   new Regex(CppFunctionStartBlacklistPattern + @"\s*(?:export\s+)?" + CppTemplatePrefixPattern + @"struct\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("union",    new Regex(CppFunctionStartBlacklistPattern + @"\s*(?:export\s+)?" + CppTemplatePrefixPattern + @"union\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("namespace", new Regex(@"^\s*(?:export\s+)?namespace\s+(?!\w+\s*=)(?<name>\w+(?:::\w+)*)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*(?:export\s+)?(?:typedef\s+)?enum\s+(?:class\s+)?(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*#\s*(?:include|import)\s+(?:<(?<name>[^>]+)>|""(?<name>[^""]+)""|(?<name>[^\s]+))", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["php"] =
        [
            // Variable-bound closures / 変数に束縛されたクロージャ
            new("function", new Regex(@"^\s*\$(?<name>\w+)\s*=\s*(?:static\s+)?function\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*\$(?<name>\w+)\s*=\s*(?:static\s+)?fn\s*\(", RegexOptions.Compiled), BodyStyle.None),
            // Const declaration / 定数宣言
            new("function", new Regex(@"^\s*define\s*\(\s*['""](?<name>[A-Za-z_]\w*)['""]\s*,", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?<visibility>public|private|protected)\s+)?const\s+(?<returnType>\??[A-Za-z_\\][\w\\]*(?:\s*[|&]\s*\??[A-Za-z_\\][\w\\]*)*)\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.None, "visibility", ReturnTypeGroup: "returnType"),
            new("function", new Regex(@"^\s*(?:(?<visibility>public|private|protected)\s+)?const\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            // Class property declarations / クラスプロパティ宣言
            new("property", new Regex(@"^\s*(?:(?<visibility>public|private|protected|var)\s+)(?:(?:static|readonly)\s+)*(?:(?<returnType>\??[A-Za-z_\\][\w\\]*(?:\s*[|&]\s*\??[A-Za-z_\\][\w\\]*)*)\s+)?\$(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.None, "visibility", ReturnTypeGroup: "returnType"),
            new("function", new Regex(@"^\s*(?:(?:(?<visibility>public|private|protected)|static|abstract|final)\s+)*function\s+(?<name>\w+)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Class with expanded modifiers: abstract, final, readonly (PHP 8.2+)
            // 拡張修飾子対応: abstract, final, readonly (PHP 8.2+)
            new("class",    new Regex(@"^\s*(?:(?:abstract|final|readonly)\s+)*class\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("interface", new Regex(@"^\s*interface\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("trait", new Regex(@"^\s*trait\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*enum\s+(?<name>\w+)(?:\s*:\s*(?<returnType>[A-Za-z_\\][\w\\]*))?", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            new("property", new Regex(@"^\s*case\s+(?<name>\w+)(?:\s*=\s*(?<returnType>[^;]+?))?\s*;", RegexOptions.Compiled), BodyStyle.None, ReturnTypeGroup: "returnType"),
            // Namespace / 名前空間
            new("namespace", new Regex(@"^\s*namespace\s+(?<name>[\w\\]+)", RegexOptions.Compiled), BodyStyle.Brace),
        ],
        ["swift"] =
        [
            // Swift function names may be ordinary identifiers or escaped identifiers
            // wrapped in backticks (e.g. `func `repeat`() {}`).
            // Swift の関数名は通常識別子に加えて、バッククォートでエスケープした識別子
            // （例: `func `repeat`() {}`）も取りうる。
            new("function", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:static|class|nonisolated|mutating|nonmutating|prefix|infix|postfix)\s+)*(?:override\s+)?func\s+(?<name>`[^`]+`|\w+|[~!%^&*+\-=|/?<>.]+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:required|convenience|nonisolated|mutating|nonmutating|override)\s+)*(?<name>init)(?:\?)?\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:nonisolated)\s+)*(?<name>deinit)\s*(?:\{|$)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:static|class|nonisolated|mutating|nonmutating|override)\s+)*(?<name>subscript)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("struct",    new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:final)\s+)*struct\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("enum",      new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:indirect\s+)?enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("property",  new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:indirect\s+)?case\s+(?<name>\w+)(?<caseTail>(?:\s*(?:\([^:\r\n]*\))?(?:\s*=\s*(?<returnType>(?:""(?:\\.|[^""\\])*""|[^,\r\n])+))?\s*(?:,\s*\w+(?:\s*\([^:\r\n]*\))?(?:\s*=\s*(?:""(?:\\.|[^""\\])*""|[^,\r\n])+)?)*)\s*)$", RegexOptions.Compiled), BodyStyle.None, "visibility", "returnType"),
            new("property",  new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:indirect\s+)?case\s+(?<name>\w+)(?:\s*\([^)]*\))?(?:\s*=\s*(?<returnType>.+?))?\s*$", RegexOptions.Compiled), BodyStyle.None, "visibility", ReturnTypeGroup: "returnType"),
            new("protocol", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"protocol\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("associatedtype", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"associatedtype\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("typealias", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"typealias\s+(?<name>\w+)(?:\s*<[^=]+>)?\s*=", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("property", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>(?:public|private|internal|open|fileprivate|package)(?:\s*\(\s*set\s*\))?)?\s*" + SwiftAttributePattern + @"(?:(?:lazy|weak|unowned|final|static|class|nonisolated)\s+)*(?:let|var)\s+(?<name>`[^`]+`|\w+)(?=\s*(?:[:=]|$))", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            // Extension declarations are important search anchors in Swift-heavy codebases.
            // A dedicated parser keeps nested generic targets searchable even when the
            // extension also carries protocol conformances or `where` clauses.
            // extension 宣言は Swift コード検索における重要なアンカー。
            // 専用パーサにより、protocol conformance や `where` 句が付く場合でも
            // ネストした generic target を検索対象として維持する。
            new("class",    new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:final)\s+)?extension\s+(?<name>[^\r\n{]+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // actor (Swift 5.5+) / アクター
            new("class",    new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:final|distributed)\s+)*(?:class|actor)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Type alias / 型エイリアス: backtick-escaped names and generic/where clauses.
            new("typealias", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"typealias\s+(?<name>`[^`]+`|\w+)(?=\s*(?:<|=|where\b|$))", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("function", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"macro\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("interface", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"precedencegroup\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:prefix|infix|postfix)\s+operator\s+(?<name>\S+)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("import",   new Regex(@"^\s*" + SwiftAttributePattern + @"(?:(?:public|private|internal|open|fileprivate|package)\s+)?import\s+(?<name>.+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["objc"] =
        [
            new("class",    new Regex(@"^\s*@interface\s+(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*@implementation\s+(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*@(?:interface|implementation)\s+(?<name>\w+\s*\(\s*[^)]+?\s*\))\b", RegexOptions.Compiled), BodyStyle.Brace),
            new("interface", new Regex(@"^\s*@protocol\s+(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.Brace),
            // Apple enum macros / Apple の enum マクロ
            new("enum",     new Regex(@"^\s*typedef\s+(?:NS_(?:CLOSED_)?ENUM|NS_EXTENSIBLE_ENUM)\s*\([^,]+,\s*(?<name>\w+)\s*\)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*typedef\s+NS_OPTIONS\s*\([^,]+,\s*(?<name>\w+)\s*\)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*typedef\s+NS_ERROR_ENUM\s*\([^,]+,\s*(?<name>\w+)\s*\)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*typedef\s+(?:CF_ENUM|CF_OPTIONS)\s*\([^,]+,\s*(?<name>\w+)\s*\)", RegexOptions.Compiled), BodyStyle.Brace),
            new("property", new Regex(@"^\s*@property\b(?:\s*\([^)]*\))?.*?(?<name>\w+)\s*;", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*[+-]\s*\([^)]*\)\s*(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*#(?:import|include)\s+[<""](?<name>[^"">]+)[>""]", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["fsharp"] =
        [
            new("function", new Regex(@"^\s*let!?\s+(?:(?:rec|mutable|inline|private|internal|public)\s+)*(?<name>(?:``[^`]+``|\w+))(?:\s+(?:\w+|\())?", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*use!?\s+(?<name>(?:``[^`]+``|\w+))\s*(?:=|:)", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*and\s+(?<name>(?:``[^`]+``|\w+))\s+(?:``[^`]+``|\w+|\()", RegexOptions.Compiled), BodyStyle.None),
            new("struct", new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))(?:\s*<[^>]+>)?\s*=\s*\{", RegexOptions.Compiled), BodyStyle.Brace),
            new("interface", new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))(?:\s*<[^>]+>)?\s*=\s*interface\b", RegexOptions.Compiled), BodyStyle.None),
            new("struct", new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))(?:\s*<[^>]+>)?\s*=\s*struct\b", RegexOptions.Compiled), BodyStyle.None),
            new("delegate", new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))(?:\s*<[^>]+>)?\s*=\s*delegate\b", RegexOptions.Compiled), BodyStyle.None),
            new("class", new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))(?:\s*<[^=]+?>)?(?:\s+when\b[^=]+)?\s*=\s*class\b", RegexOptions.Compiled), BodyStyle.None),
            // Generic abbreviations such as `type Result<'T> = Choice<'T, string>` should not be
            // mistaken for union cases just because the right-hand side starts with a capitalized
            // type name.
            // `type Result<'T> = Choice<'T, string>` のような generic abbreviation は、
            // 右辺が大文字始まりの型名でも union case と誤認しない。
            new("typealias", new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))\s*<[^=]+?>\s*(?:when\b[^=]+)?\s*=\s*(?![^\r\n]*\|)(?!(?:class|delegate|interface|struct|enum|exception)\b)(?!\{)(?!\|)(?!\()", RegexOptions.Compiled), BodyStyle.None),
            new("enum", new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))(?:\s*<[^>]+>)?\s*=\s*(?:\|?\s*[A-Z][\w']*\b(?:\s*\|[^=].*)?)", RegexOptions.Compiled), BodyStyle.Brace),
            // Simple aliases without generic parameters stay searchable as `typealias`.
            // generic 引数なしの単純な alias も `typealias` として検索可能にする。
            new("typealias", new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))\s*=\s*(?![^\r\n]*\|)(?!(?:class|delegate|interface|struct|enum|exception)\b)(?!\{)(?!\|)(?!\()", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))(?:\s*<[^>]+>)?(?:\s+when\b[^=]+)?\s*(?:\([^)]*\))\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("struct",   new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))\s*=\s*\{", RegexOptions.Compiled), BodyStyle.None),
            new("enum",     new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))\s*=\s*(?:\|\s*)?\w+(?:\s*\|\s*\w+)+", RegexOptions.Compiled), BodyStyle.None),
            new("exception", new Regex(@"^\s*exception\s+(?:(?:private|internal)\s+)?(?<name>(?:``[^`]+``|\w+))", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)?(?<name>(?:``[^`]+``|\w+))\s*=\s*(?!\{)(?!\|)(?!class\b)(?!delegate\b)(?!struct\b)(?!interface\b)(?!enum\b).+", RegexOptions.Compiled), BodyStyle.None),
            new("namespace", new Regex(@"^\s*namespace\s+(?:(?:rec|global)\s+)*(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*module\s+(?:(?:(?:private|internal)\s+|rec\s+))*(?:``[^`]+``|[\w.]+)\s*=\s*(?<name>(?:``[^`]+``|[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
            new("namespace", new Regex(@"^\s*module\s+(?:(?:(?:private|internal)\s+|rec\s+))*(?<name>(?:``[^`]+``|[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?<visibility>private|internal|public)\s+)?override\s+(?:(?:this|_|\w+)\.)?(?<name>(?:``[^`]+``|\w+))\s*(?:\(|=|:)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>private|internal|public)\s+)?abstract\s+(?!member\b)(?<name>(?:``[^`]+``|\w+))\s*:", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("property", new Regex(@"^\s*(?:(?<visibility>private|internal|public)\s+)?(?:static\s+)?val\s+(?:mutable\s+)?(?<name>(?:``[^`]+``|\w+))\s*:", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("property", new Regex(@"^\s*(?:(?<visibility>private|internal|public)\s+)?(?:(?:static|abstract|override|default)\s+)*member\s+(?:(?:private|internal)\s+)?val\s+(?<name>(?:``[^`]+``|\w+))(?=\s|\(|=|:|$)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>private|internal|public)\s+)?(?:(?:static|abstract|override|default)\s+)*member\s+(?:(?:private|internal)\s+)?(?:(?:inline)\s+)?(?:(?:this|_|\w+)\.)?(?!val\b)(?<name>(?:``[^`]+``|\w+))(?=\s|\(|=|:|$)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("import",   new Regex(@"^\s*open\s+(?:type\s+)?(?<name>(?:``[^`]+``|[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["vb"] =
        [
            new("namespace", new Regex(@"^\s*Namespace\s+(?<name>(?:Global\.)?" + VbIdentifierPattern + @"(?:\." + VbIdentifierPattern + @")*)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.VisualBasicEnd),
            new("delegate", new Regex(@$"^\s*(?:(?:{VbMemberModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?Delegate\s+(?:Sub|Function)\s+(?<name>{VbIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None, "visibility"),
            new("function", new Regex(@$"^\s*(?:(?:{VbMemberModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbMemberModifierPattern})\s+)*(?:Sub|Function)\s+(?<name>{VbIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None, "visibility"),
            new("operator", new Regex(@$"^\s*(?:(?:{VbOperatorModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbOperatorModifierPattern})\s+)*(?<name>Operator\s+[^\s(]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.VisualBasicEnd, "visibility"),
            new("property", new Regex(@$"^\s*(?:(?:Shared|Shadows)\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:Shared|Shadows)\s+)*Const\s+(?<name>{VbIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None, "visibility"),
            new("property", new Regex(@$"^\s*(?:(?:Shared|Shadows|ReadOnly|WithEvents)\s+)*(?<visibility>{VbVisibilityPattern})\s+(?:(?:Shared|Shadows|ReadOnly|WithEvents)\s+)*(?<name>{VbIdentifierPattern})\s+As\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None, "visibility"),
            new("property", new Regex(@$"^\s*(?:(?:{VbPropertyModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbPropertyModifierPattern})\s+)*Property\s+(?<name>{VbIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None, "visibility"),
            new("event",    new Regex(@$"^\s*(?:(?:{VbEventModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbEventModifierPattern})\s+)*Event\s+(?<name>{VbIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None, "visibility"),
            new("interface", new Regex(@$"^\s*(?:(?:{VbTypeModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbTypeModifierPattern})\s+)*Interface\s+(?<name>{VbIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.VisualBasicEnd, "visibility"),
            new("enum",     new Regex(@$"^\s*(?:(?<visibility>{VbVisibilityPattern})\s+)?Enum\s+(?<name>{VbIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.VisualBasicEnd, "visibility"),
            new("struct",   new Regex(@$"^\s*(?:(?:Partial)\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:Partial)\s+)*Structure\s+(?<name>{VbIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.VisualBasicEnd, "visibility"),
            new("class",    new Regex(@$"^\s*(?:(?:{VbTypeModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbTypeModifierPattern})\s+)*(?:Class|Module)\s+(?<name>{VbIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.VisualBasicEnd, "visibility"),
            new("import",   new Regex(@"^\s*Imports\s+<\s*xmlns:(?<name>[A-Za-z_][\w.-]*)\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",   new Regex(@$"^\s*Imports\s+(?<name>{VbIdentifierPattern})\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",   new Regex(@"^\s*Imports\s+(?<name>.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["scala"] =
        [
            new("implicit", new Regex(@"^\s*(?<visibility>private|protected)?\s*implicit\s+(?:override\s+)?(?:def|val|var|class)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("given", new Regex(@"^\s*(?<visibility>private|protected)?\s*given\s+(?:(?<name>\w+)\s*(?::|as)|(?<name>[A-Z]\w*)\b)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*(?<visibility>private|protected)?\s*(?:override\s+)?def\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("interface", new Regex(@"^\s*(?<visibility>private|protected)?\s*(?:sealed\s+)?trait\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("enum",     new Regex(@"^\s*(?<visibility>private|protected)?\s*enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("class",    new Regex(@"^\s*(?<visibility>private|protected)?\s*(?:abstract\s+|sealed\s+|final\s+)?(?:case\s+)?class\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("object",   new Regex(@"^\s*(?<visibility>private|protected)?\s*(?:sealed\s+|final\s+)?(?:case\s+)?object\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("import",   new Regex(@"^\s*type\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*import\s+(?<name>.+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["haskell"] =
        [
            new("function", new Regex(@"^(?:>\s+|\s*)(?<name>[a-z_]\w*)\s+::", RegexOptions.Compiled), BodyStyle.None),
            new("interface", new Regex(@"^\s*class\s+(?<name>[A-Z]\w*)", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?:data|newtype|type)\s+(?<name>[A-Z]\w*)", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*import\s+(?:qualified\s+)?(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["r"] =
        [
            new("function", new Regex(@"^\s*`(?<name>[^`]+)`\s*<<?-\s*(?:function\s*\(|\\\s*\()", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?<name>[\w.]+)\s*<<?-\s*(?:function\s*\(|\\\s*\()", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*`(?<name>[^`]+)`\s*=\s*(?:function\s*\(|\\\s*\()", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?<name>[\w.]+)\s*=\s*(?:function\s*\(|\\\s*\()", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*assign\s*\(\s*(?:x\s*=\s*)?['""](?<name>[^'""]+)['""]\s*,\s*(?:value\s*=\s*)?(?:function\s*\(|\\\s*\()", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?:function\s*\(|\\\s*\()[^\r\n#]*(?:->>|->)\s*`(?<name>[^`]+)`", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?:function\s*\(|\\\s*\()[^\r\n#]*(?:->>|->)\s*(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?:(?:[\w.]+)::)?test_that\s*\(\s*['""](?<name>[^'""]+)['""]", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?:(?:[\w.]+)::)?(?:describe|it)\s*\(\s*['""](?<name>[^'""]+)['""]", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*output\$(?<name>[\w.]+)\s*<<?-\s*render\w+\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*output\s*\[\s*\[\s*['""](?<name>[^'""]+)['""]\s*\]\s*\]\s*<<?-\s*render\w+\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*`(?<name>[^`]+)`\s*(?:<<?-|=)\s*(?:reactive|eventReactive|observe|observeEvent)\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?<name>[\w.]+)\s*(?:<<?-|=)\s*(?:reactive|eventReactive|observe|observeEvent)\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*(?:(?:[\w.]+)::)?(?:setClass|setRefClass|setClassUnion|setOldClass|R6Class)\s*\(\s*(?:(?:Class|classes|className|classname|name)\s*=\s*)?(?:['""](?<name>[^'""]+)['""]|(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?:(?:[\w.]+)::)?setIs\s*\(.*?\b(?:class2|to)\s*=\s*(?:['""](?<name>[^'""]+)['""]|(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*inherit\s*=\s*(?:c\(\s*)?(?:['""](?<name>[^'""]+)['""]|(?<name>[A-Z][\w.]*))", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?:[\w.]+)::)?setValidity\s*\(\s*(?:(?:Class|class|classes|name)\s*=\s*)?(?:['""](?<name>[^'""]+)['""]|(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?:[\w.]+)::)?(?:setGeneric|setGroupGeneric)\s*\(\s*(?:(?:f|generic|name)\s*=\s*)?['""](?<name>[^'""]+)['""]", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?:[\w.]+)::)?setMethod\s*\(\s*(?:(?:f|generic|name)\s*=\s*)?(?:['""](?<name>[^'""]+)['""]|(?<name>[\w.]+))\s*,", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?<visibility>public|private|active)\s*=\s*list\(\s*(?<name>[\w.]+)\s*=\s*function\s*\(", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("import",   new Regex(@"^\s*(?:(?:[\w.]+)::)?(?:library|require)\s*\(\s*help\s*=\s*(?:['""](?<name>[^'""]+)['""]|(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*(?:(?:[\w.]+)::)?(?:library|require|requireNamespace)\s*\(\s*(?:(?:package|pkg)\s*=\s*)?(?:['""](?<name>[^'""]+)['""]|(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*(?:(?:[\w.]+)::)?(?:source|sys\.source)\s*\(\s*(?:file\s*=\s*)?['""](?<name>[^'""]+)['""]", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["lua"] =
        [
            new("function", new Regex(@"^\s*(?:local\s+)?function\s+(?<name>[\w.:]+)\s*\(", RegexOptions.Compiled), BodyStyle.ElixirEnd),
            new("function", new Regex(@"^\s*local\s+(?<name>[\w]+)\s*=\s*function\s*\(", RegexOptions.Compiled), BodyStyle.ElixirEnd),
            new("function", new Regex(@"^\s*(?<name>[\w]+(?:[.:][\w]+)+)\s*=\s*function\s*\(", RegexOptions.Compiled), BodyStyle.ElixirEnd),
            new("import",   new Regex(@"^\s*(?:local\s+\w+\s*=\s*)?require\s*\(?['""](?<name>[^'""]+)['""]", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["elixir"] =
        [
            new("function", new Regex(@"^\s*(?:def|defp|defmacro|defguardp?)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.ElixirEnd),
            new("class",    new Regex(@"^\s*defmodule\s+(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.ElixirEnd),
            new("interface", new Regex(@"^\s*defprotocol\s+(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.ElixirEnd),
            new("protocol_impl", new Regex(@"^\s*defimpl\s+(?<name>[\w.]+(?:\s*,\s*for:\s*(?:\[[^\]]+\]|[\w.{}]+))?)", RegexOptions.Compiled), BodyStyle.ElixirEnd),
            new("import",   new Regex(@"^\s*(?:import|alias|use|require)\s+(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["clojure"] =
        [
            // Clojure forms are parenthesized, so use conservative line anchors.
            // Clojure の form は括弧ベースなので、保守的な行アンカーだけを拾う。
            new("namespace", new Regex(@"^\s*\(\s*ns\s+(?<name>[^\s\)\[\{]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex(@"^\s*\(\s*(?:defrecord|deftype)\s+(?<name>[^\s\)\[\{]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("protocol", new Regex(@"^\s*\(\s*defprotocol\s+(?<name>[^\s\)\[\{]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*\(\s*(?:defn-?|defmacro|defmulti|defmethod)\s+(?<name>[^\s\)\[\{]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*\(\s*(?:def|defonce)\s+(?<name>[^\s\)\[\{]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["erlang"] =
        [
            new("namespace", new Regex(@"^\s*-module\s*\(\s*(?<name>[a-z][\w@]*|'[^'\r\n]+')\s*\)\s*\.", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("struct",   new Regex(@"^\s*-record\s*\(\s*(?<name>[a-z][\w@]*|'[^'\r\n]+')\s*,", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("type",     new Regex(@"^\s*-(?:type|opaque)\s+(?<name>[a-z][\w@]*|'[^'\r\n]+')\s*(?:\(|::)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?<name>[a-z][\w@]*|'[^'\r\n]+')\s*\([^)\r\n]*\)\s*(?:when\b[^-\r\n]*)?->", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",   new Regex(@"^\s*-(?:import|include(?:_lib)?)\s*\(\s*(?<name>[^)\r\n]+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["ocaml"] =
        [
            new("namespace", new Regex(@"^\s*module\s+(?:type\s+)?(?<name>[A-Z][A-Za-z0-9_']*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex(@"^\s*class(?:\s+type)?\s+(?<name>[A-Za-z_][A-Za-z0-9_']*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("type",     new Regex(@"^\s*type\s+(?:nonrec\s+)?(?:'[\w]+\s+)*(?<name>[A-Za-z_][A-Za-z0-9_']*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*let\s+(?:rec\s+)?(?<name>[A-Za-z_][A-Za-z0-9_']*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*val\s+(?<name>[A-Za-z_][A-Za-z0-9_']*)\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",   new Regex(@"^\s*open\s+(?<name>[A-Z][\w.']*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["raku"] =
        [
            new("namespace", new Regex(@"^\s*(?:unit\s+)?(?:module|package)\s+(?<name>[\w:.-]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("interface", new Regex(@"^\s*(?:unit\s+)?role\s+(?<name>[\w:.-]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.RubyEnd),
            new("class",    new Regex(@"^\s*(?:unit\s+)?(?:class|grammar)\s+(?<name>[\w:.-]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.RubyEnd),
            new("enum",     new Regex(@"^\s*enum\s+(?<name>[\w:.-]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?:my|our|multi|proto|only)\s+)*(?:sub|method|submethod|macro)\s+(?<name>[\w:!?.-]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.RubyEnd),
            new("property", new Regex(@"^\s*(?:(?:my|our|state|constant)\s+)*(?<name>[$@%&]\w[\w-]*)\s*=", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["dart"] =
        [
            new("function", new Regex(@"^\s*(?!return\b|await\b|const\b|new\b|throw\b|yield\b|if\b|else\b|for\b|while\b|switch\b|case\b|catch\b|do\b|try\b|finally\b|class\b|enum\b|mixin\b|extension\b|typedef\b|library\b|part\b|import\b|export\b)(?:(?:static|abstract|override|external)\s+)*(?<rt>\w[\w<>,\s\?]*?)\s+(?<name>(?!if\b|else\b|for\b|while\b|switch\b|case\b|class\b|enum\b|mixin\b|extension\b|typedef\b|library\b|part\b|import\b|export\b|abstract\b|void\b|var\b|final\b|late\b|const\b|new\b|return\b|throw\b|yield\b|await\b|extends\b|implements\b|with\b|on\b|is\b|as\b|in\b|of\b|super\b|this\b)\w+)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "rt"),
            new("function", new Regex(@"^\s*factory\s+(?<name>[A-Z_]\w*(?:\.\w+)?)\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*const\s+(?<name>[A-Z_]\w*(?:\.\w+)?)\s*\((?=[^)]*(?:\bthis\b|\bsuper\b))", RegexOptions.Compiled), BodyStyle.None),
            new("function", DartBareConstConstructorRegex, BodyStyle.None),
            new("function", new Regex(@"^\s*(?<name>[A-Z_]\w*(?:\.\w+)?)\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*typedef\s+(?<name>\w+)(?:<[^>]*>)?\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*typedef\s+(?:[\w<>,\[\]\?\.\s]+\s+)+(?<name>\w+)\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("enum",     new Regex(@"^\s*enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*(?:abstract\s+)?(?:class|mixin)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*extension\s+(?<name>\w+)\s+on\s+", RegexOptions.Compiled), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*import\s+'(?<name>[^']+)'", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["pascal"] =
        [
            new("namespace", new Regex(@"^\s*(?:unit|program|library|package)\s+(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*(?:class|object)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("struct",   new Regex(@"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*record\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("interface", new Regex(@"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*interface\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("enum",     new Regex(@"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?:class|static)\s+)?(?:procedure|function|constructor|destructor)\s+(?:(?:[A-Za-z_]\w*)\.)?(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.PascalEnd),
            new("property", new Regex(@"^\s*property\s+(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",   new Regex(@"^\s*uses\s+(?<name>.+?)(?:;|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["smalltalk"] =
        [
            new("class",    new Regex(@"^\s*(?:[A-Za-z_]\w*)\s+subclass:\s*#(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?:Class\s+named:|Object\s+subclass:)\s*#(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:[A-Za-z_]\w*)(?:\s+class)?\s*>>\s*(?<name>[A-Za-z_]\w*:?(?:\s+[A-Za-z_]\w+\s+[A-Za-z_]\w*:)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.SmalltalkMethod),
        ],
        ["graphql"] =
        [
            new("interface", new Regex(@"^\s*interface\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*(?:type|union|scalar|input)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?:query|mutation|subscription)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*fragment\s+(?<name>\w+)\s+on\s+\w+", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*directive\s+@(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*extend\s+(?:type|interface|input|enum)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*extend\s+(?:union|scalar)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*schema\s*\{", RegexOptions.Compiled), BodyStyle.Brace),
        ],
        ["gradle"] =
        [
            new("function", new Regex(@"^\s*(?:task|def)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*(?:apply\s+plugin\s*:\s*|id\s*[\s(]\s*)['""](?<name>[^'""]+)['""]", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["makefile"] =
        [
            new("property", new Regex(@"^(?<name>[\w.-]+)\s*(?::=|::=|=|\?=|\+=)", RegexOptions.Compiled), BodyStyle.None),  // Makefile variable assignments / Makefile変数代入
            new("rule", new Regex(@"^(?<name>\.PHONY)\s*:(?!=|:=)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),  // Makefile special-target metadata / Makefile特殊ターゲットメタデータ
            new("function", new Regex(@"^(?!\.PHONY\s*:)(?<name>[\w.%-]+)\s*:(?!=|:=)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),  // Makefile targets / Makefileターゲット
        ],
        ["cmake"] =
        [
            new("function", new Regex(@"^\s*(?:function|macro)\s*\(\s*(?<name>[A-Za-z_][\w.-]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:add_executable|add_library|add_custom_target)\s*\(\s*(?<name>[A-Za-z_][\w.-]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*(?:set|option)\s*\(\s*(?<name>[A-Za-z_][\w.-]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*(?:include|find_package)\s*\(\s*(?<name>[A-Za-z_][\w.:+-]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["justfile"] =
        [
            new("import", new Regex(@"^\s*(?:import|mod)\s+[""'](?<name>[^""']+)[""']", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^(?<name>[A-Za-z_][\w.-]*)\s*(?::=|=|\+=)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^(?<name>[A-Za-z_][\w.-]*)(?:\s+[^:#\r\n]+)?\s*:(?![:=])", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["msbuild"] = [],
        ["dockerfile"] =
        [
            new("build_arg", new Regex(@"^\s*ARG\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("environment", new Regex(@"^\s*ENV\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("label", new Regex(@"^\s*LABEL\s+(?<name>[A-Za-z0-9_.-]+)\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("label", new Regex(@"^\s*LABEL\s+(?<name>[A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("expose", new Regex(@"^\s*EXPOSE\s+(?<name>\d+(?:/(?:tcp|udp))?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("user", new Regex(@"^\s*USER\s+(?<name>[A-Za-z0-9_][A-Za-z0-9_.-]*(?::[A-Za-z0-9_][A-Za-z0-9_.-]*)?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("workdir", new Regex(@"^\s*WORKDIR\s+(?<name>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("volume", new Regex(@"^\s*VOLUME\s+(?<name>(?!\[)\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("stopsignal", new Regex(@"^\s*STOPSIGNAL\s+(?<name>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("stage", new Regex(@"^\s*FROM\s+(?:--platform=\S+\s+)?\S+\s+(?:AS|as)\s+(?<name>[A-Za-z0-9_.-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),  // Named stage / 名前付きステージ
            new("base_image", new Regex(@"^\s*FROM\s+(?:--platform=\S+\s+)?(?<name>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),  // Base image / ベースイメージ
        ],
        ["protobuf"] =
        [
            new("class",    new Regex(@"^\s*message\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("namespace", new Regex(@"^\s*package\s+(?<name>[\w.]+)\s*;", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*oneof\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*extend\s+(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*service\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*rpc\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*import\s+""(?<name>[^""]+)"";", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["verilog"] =
        [
            new("module", new Regex(@"^\s*(?:module|macromodule|primitive)\s+(?<name>" + HdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*function\s+(?:automatic\s+|static\s+)?(?:(?<returnType>[\w$:\[\]\s]+?)\s+)?(?<name>" + HdlIdentifierPattern + @")\s*(?:\(|;)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None, ReturnTypeGroup: "returnType"),
            new("function", new Regex(@"^\s*task\s+(?:automatic\s+|static\s+)?(?<name>" + HdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*(?:parameter|localparam)\s+(?:type\s+)?" + HdlDeclaratorPrefixPattern + @"(?<name>" + HdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*(?:input|output|inout|wire|reg|logic)\s+" + HdlDeclaratorPrefixPattern + @"(?<name>" + HdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*`include\s+""(?<name>[^""]+)""", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["systemverilog"] =
        [
            new("module", new Regex(@"^\s*(?:module|macromodule|primitive|program)\s+(?<name>" + HdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("interface", new Regex(@"^\s*interface\s+(?<name>" + HdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("package", new Regex(@"^\s*package\s+(?<name>" + HdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class", new Regex(@"^\s*(?:virtual\s+)?class\s+(?<name>" + HdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("enum", new Regex(@"^\s*typedef\s+enum\b[^\r\n]*\s+(?<name>" + HdlIdentifierPattern + @")\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("struct", new Regex(@"^\s*typedef\s+struct\b[^\r\n]*\s+(?<name>" + HdlIdentifierPattern + @")\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("typealias", new Regex(@"^\s*typedef\s+(?!(?:enum|struct|union)\b)[^\r\n]*\s+(?<name>" + HdlIdentifierPattern + @")\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*function\s+(?:automatic\s+|static\s+|virtual\s+)?(?:(?<returnType>[\w$:\[\]\s]+?)\s+)?(?<name>" + HdlIdentifierPattern + @")\s*(?:\(|;)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None, ReturnTypeGroup: "returnType"),
            new("function", new Regex(@"^\s*task\s+(?:automatic\s+|static\s+|virtual\s+)?(?<name>" + HdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*(?:parameter|localparam)\s+(?:type\s+)?" + HdlDeclaratorPrefixPattern + @"(?<name>" + HdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*(?:input|output|inout|wire|reg|logic|rand|randc)\s+" + HdlDeclaratorPrefixPattern + @"(?<name>" + HdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*import\s+(?<name>" + HdlIdentifierPattern + @"(?:::(?:" + HdlIdentifierPattern + @"|\*))?)\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*`include\s+""(?<name>[^""]+)""", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["vhdl"] =
        [
            new("import", new Regex(@"^\s*library\s+(?<name>" + VhdlIdentifierPattern + @")\s*;", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*use\s+(?<name>" + VhdlIdentifierPattern + @"(?:\." + VhdlIdentifierPattern + @")*)\s*;", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("module", new Regex(@"^\s*entity\s+(?<name>" + VhdlIdentifierPattern + @")\s+is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("module", new Regex(@"^\s*architecture\s+(?<name>" + VhdlIdentifierPattern + @")\s+of\s+" + VhdlIdentifierPattern + @"\s+is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("package", new Regex(@"^\s*package\s+body\s+(?<name>" + VhdlIdentifierPattern + @")\s+is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("package", new Regex(@"^\s*package\s+(?<name>" + VhdlIdentifierPattern + @")\s+is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("module", new Regex(@"^\s*component\s+(?<name>" + VhdlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("module", new Regex(@"^\s*configuration\s+(?<name>" + VhdlIdentifierPattern + @")\s+of\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*function\s+(?<name>" + VhdlIdentifierPattern + @")\s*(?:\(|return\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*procedure\s+(?<name>" + VhdlIdentifierPattern + @")\s*(?:\(|is\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?<name>" + VhdlIdentifierPattern + @")\s*:\s*process\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("typealias", new Regex(@"^\s*(?:type|subtype)\s+(?<name>" + VhdlIdentifierPattern + @")\s+is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*(?:signal|constant|variable|generic|port)\s+(?<name>" + VhdlIdentifierPattern + @")\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["glsl"] =
        [
            new("struct", new Regex(@"^\s*struct\s+(?<name>" + ShaderIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("property", new Regex(@"^\s*" + ShaderAttributePrefixPattern + @"(?:uniform|buffer)\s+(?:(?<returnType>" + ShaderTypePattern + @")\s+)?(?<name>" + ShaderIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            new("property", new Regex(@"^\s*" + ShaderAttributePrefixPattern + @"(?:in|out|attribute|varying)\s+(?<returnType>" + ShaderTypePattern + @")\s+(?<name>" + ShaderIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None, ReturnTypeGroup: "returnType"),
            new("function", new Regex(ShaderFunctionStartBlacklistPattern + @"\s*" + ShaderAttributePrefixPattern + @"(?<returnType>" + ShaderTypePattern + @")\s+(?<name>" + ShaderIdentifierPattern + @")\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
        ],
        ["hlsl"] =
        [
            new("struct", new Regex(@"^\s*struct\s+(?<name>" + ShaderIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("property", new Regex(@"^\s*(?:cbuffer|tbuffer)\s+(?<name>" + ShaderIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("property", new Regex(@"^\s*(?:globallycoherent\s+)?(?:RW)?(?:Texture\w*|Buffer|StructuredBuffer|RWStructuredBuffer|ByteAddressBuffer|RWByteAddressBuffer|Sampler\w*)\s*(?:<[^>\r\n]+>)?\s+(?<name>" + ShaderIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*(?:groupshared|static|uniform)\s+(?<returnType>" + ShaderTypePattern + @")\s+(?<name>" + ShaderIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None, ReturnTypeGroup: "returnType"),
            new("function", new Regex(ShaderFunctionStartBlacklistPattern + @"\s*" + ShaderAttributePrefixPattern + @"(?<returnType>" + ShaderTypePattern + @")\s+(?<name>" + ShaderIdentifierPattern + @")\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
        ],
        ["metal"] =
        [
            new("struct", new Regex(@"^\s*struct\s+(?<name>" + ShaderIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("property", new Regex(@"^\s*(?:constant|device|threadgroup)\s+(?<returnType>" + ShaderTypePattern + @")\s+(?<name>" + ShaderIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None, ReturnTypeGroup: "returnType"),
            new("function", new Regex(ShaderFunctionStartBlacklistPattern + @"\s*(?:kernel|vertex|fragment)\s+(?<returnType>" + ShaderTypePattern + @")\s+(?<name>" + ShaderIdentifierPattern + @")\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            new("function", new Regex(ShaderFunctionStartBlacklistPattern + @"\s*(?<returnType>" + ShaderTypePattern + @")\s+(?<name>" + ShaderIdentifierPattern + @")\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
        ],
        ["wgsl"] =
        [
            new("struct", new Regex(@"^\s*struct\s+(?<name>" + ShaderIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("typealias", new Regex(@"^\s*alias\s+(?<name>" + ShaderIdentifierPattern + @")\s*=", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*(?:@\w+(?:\([^)]*\))?\s*)*(?:var(?:<[^>\r\n]+>)?|let|const|override)\s+(?<name>" + ShaderIdentifierPattern + @")\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:@\w+(?:\([^)]*\))?\s*)*fn\s+(?<name>" + ShaderIdentifierPattern + @")\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
        ],
        ["shell"] =
        [
            // Bash/Zsh function declarations / Bash/Zsh 関数宣言
            new("function", new Regex(@"^\s*(?:function\s+)?(?<name>\w+)\s*\(\s*\)\s*\{?", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*function\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            // Alias definitions / エイリアス定義
            new("alias", new Regex(@"^\s*alias(?:\s+-[^\s=]+)*\s+(?<name>[A-Za-z_][A-Za-z0-9_-]*)\s*=", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["sql"] =
        [
            // Identifier shape accepts PG double-quoted ("name"), T-SQL bracketed ([name]), or bare
            // ([\w$#]+) to cover Oracle identifiers such as SYS$LINK / USER#1, optionally qualified
            // with dots (schema.name, [dbo].[sp_X], "s"."n").
            // 識別子形式は PG の "name"、T-SQL の [name]、裸 ([\w$#]+) を受け入れる。裸 ID は
            // SYS$LINK / USER#1 のような Oracle 識別子も拾える。ドットで修飾可能
            //（schema.name、[dbo].[sp_X]、"s"."n"）。
            // CREATE TABLE / VIEW — Postgres TEMP/UNLOGGED + MATERIALIZED VIEW, T-SQL `CREATE OR ALTER` (2016+)
            // CREATE TABLE / VIEW — Postgres の TEMP/UNLOGGED や MATERIALIZED VIEW、T-SQL の `CREATE OR ALTER`（2016+）に対応
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:(?:(?:GLOBAL|LOCAL)\s+)?(?:TEMP|TEMPORARY)\s+|UNLOGGED\s+)?(?:TABLE|(?:MATERIALIZED\s+)?VIEW)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // CREATE PROCEDURE / PROC / FUNCTION / TRIGGER — Postgres `OR REPLACE` and T-SQL `OR ALTER` / `PROC` short form
            // Uses BodyStyle.SqlProcBody so the body range covers the BEGIN...END / dollar-quoted body,
            // letting ReferenceExtractor.ResolveContainerForCall attribute calls inside the body to the
            // enclosing procedure (see issue #429).
            // CREATE PROCEDURE / PROC / FUNCTION / TRIGGER — Postgres の `OR REPLACE` と T-SQL の `OR ALTER` / 短縮形 `PROC` に対応
            // BodyStyle.SqlProcBody により BEGIN...END / dollar-quoted の本体範囲を求め、ReferenceExtractor の
            // ResolveContainerForCall が本体内の呼び出しを外側のプロシージャに帰属させられるようにする（issue #429）。
            new("function", new Regex($@"^\s*CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:PROCEDURE|PROC|FUNCTION|TRIGGER)\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.SqlProcBody),
            // SQL Server aggregate definitions are callable search anchors too, but they do not have
            // a statement body to scan, so they stay on the BodyStyle.None path.
            // SQL Server の aggregate 定義も検索アンカーとして有用だが、走査すべき statement body は
            // 持たないため BodyStyle.None のまま扱う。
            new("function", new Regex($@"^\s*CREATE\s+AGGREGATE\b\s+(?<name>{SqlQualifiedIdentifierPattern})\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("enum",     new Regex($@"^\s*CREATE\s+TYPE\s+(?<name>{SqlQualifiedIdentifierPattern})\s+AS\s+ENUM\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Oracle: CREATE [OR REPLACE] TYPE BODY <name> and CREATE [OR REPLACE] PACKAGE [BODY] <name>.
            // These must precede the bare CREATE TYPE / CREATE PACKAGE rows so the `BODY` keyword is
            // not absorbed as the object name.
            // Oracle: CREATE [OR REPLACE] TYPE BODY <name> と CREATE [OR REPLACE] PACKAGE [BODY] <name>。
            // 裸の CREATE TYPE / CREATE PACKAGE 行より前に置き、`BODY` キーワードを name として
            // 飲み込まないようにする。
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?TYPE\s+BODY\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:EDITIONABLE\s+|NONEDITIONABLE\s+)?PACKAGE\s+BODY\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:EDITIONABLE\s+|NONEDITIONABLE\s+)?PACKAGE\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?TYPE\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // SQL Server legacy scalar-object definitions still appear in older T-SQL codebases.
            // The `AS <expression>` tail is part of the definition, not a body to track.
            // SQL Server の legacy な scalar-object 定義は古い T-SQL コードベースに残っている。
            // 末尾の `AS <expression>` は定義の一部であり、追跡すべき body ではない。
            new("class",    new Regex($@"^\s*CREATE\s+(?:RULE|DEFAULT)\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("namespace", new Regex($@"^\s*CREATE\s+SCHEMA\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?<name>(?!AUTHORIZATION\b){SqlQualifiedIdentifierPattern})|AUTHORIZATION\s+(?<name>{SqlQualifiedIdentifierPattern}))", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+(?:SEQUENCE|DOMAIN)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",   new Regex($@"^\s*CREATE\s+EXTENSION\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // T-SQL SYNONYM (also Oracle / DB2)
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:PUBLIC\s+)?SYNONYM\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Oracle: CREATE [SHARED] [PUBLIC] DATABASE LINK <name> — must precede the bare CREATE DATABASE row
            // so the `LINK` token is not taken as a name. SHARED and PUBLIC may appear together in that order.
            // Oracle: CREATE [SHARED] [PUBLIC] DATABASE LINK <name> — 裸の CREATE DATABASE 行より前に置き、
            // `LINK` を name として飲み込まないようにする。SHARED と PUBLIC はこの順で 2 語並ぶことがある。
            new("class",    new Regex($@"^\s*CREATE\s+(?:SHARED\s+)?(?:PUBLIC\s+)?DATABASE\s+LINK\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // T-SQL server-level / database-level principals and objects, plus Oracle-only DIRECTORY / CONTEXT / PROFILE.
            // Include T-SQL SECURITY POLICY so row-level-security policy definitions are discoverable.
            // T-SQL のサーバ/データベースレベルのプリンシパル・オブジェクトと、Oracle 固有の DIRECTORY / CONTEXT / PROFILE。
            // T-SQL の SECURITY POLICY も含め、行レベルセキュリティポリシー定義を検索可能にする。
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:DATABASE|LOGIN|USER|ROLE|CERTIFICATE|DIRECTORY|CONTEXT|PROFILE|ASSEMBLY|XML\s+SCHEMA\s+COLLECTION|SECURITY\s+POLICY)\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // T-SQL partitioning and full-text catalogs
            // T-SQL のパーティション関連と全文検索カタログ
            new("function", new Regex($@"^\s*CREATE\s+PARTITION\s+FUNCTION\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+PARTITION\s+SCHEME\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+FULLTEXT\s+CATALOG\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?!ON\b)(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // ALTER covers the same object kinds we create above, so migration scripts remain visible.
            // Kinds are split to match the CREATE side (procedure-like → function, schema → namespace,
            // extension → import, everything else → class) so `symbols --kind` / `definition` / `inspect`
            // stay consistent across a CREATE + ALTER pair on the same object.
            // ALTER も上記の CREATE と同じ種類をカバーし、マイグレーションスクリプトが可視になるようにする。
            // CREATE 側に合わせて kind を分割し（プロシージャ類 → function、SCHEMA → namespace、
            // EXTENSION → import、その他 → class）、同じオブジェクトに対する CREATE と ALTER で
            // `symbols --kind` / `definition` / `inspect` の種別が揃うようにする。
            // ALTER PROCEDURE / PROC / FUNCTION / TRIGGER share the body shape with CREATE so they
            // also get BodyStyle.SqlProcBody. ALTER PARTITION FUNCTION is body-less (it modifies the
            // partition boundary, not code), so it keeps BodyStyle.None via a separate pattern below.
            // ALTER PROCEDURE / PROC / FUNCTION / TRIGGER は CREATE と同じ本体形状を持つため
            // BodyStyle.SqlProcBody を使う。ALTER PARTITION FUNCTION は本体を持たない
            // （パーティション境界の変更のみ）ため、下の別パターンで BodyStyle.None のままにする。
            new("function", new Regex($@"^\s*ALTER\s+(?:PROCEDURE|PROC|FUNCTION|TRIGGER)\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.SqlProcBody),
            new("function", new Regex($@"^\s*ALTER\s+AGGREGATE\b\s+(?<name>{SqlQualifiedIdentifierPattern})\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("function", new Regex($@"^\s*ALTER\s+PARTITION\s+FUNCTION\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("namespace", new Regex($@"^\s*ALTER\s+SCHEMA\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import",   new Regex($@"^\s*ALTER\s+EXTENSION\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Oracle: ALTER DATABASE LINK <name> — must precede the bare ALTER DATABASE row so `LINK`
            // is not absorbed as the object name. Real Oracle body compilation is expressed as
            // `ALTER PACKAGE <name> COMPILE BODY` / `ALTER TYPE <name> COMPILE BODY` and falls through
            // to the generic ALTER row below; there is no `ALTER PACKAGE BODY <name>` syntax in Oracle.
            // Oracle: ALTER DATABASE LINK <name> — 裸の ALTER DATABASE 行より前に置き `LINK` を name
            // として飲み込まないようにする。Oracle の body コンパイルは実際には
            // `ALTER PACKAGE <name> COMPILE BODY` / `ALTER TYPE <name> COMPILE BODY` の形で、下の
            // generic ALTER 行で拾う。`ALTER PACKAGE BODY <name>` のような構文は Oracle に存在しない。
            new("class",    new Regex($@"^\s*ALTER\s+DATABASE\s+LINK\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("class",    new Regex($@"^\s*ALTER\s+(?:TABLE|(?:MATERIALIZED\s+)?VIEW|SEQUENCE|SYNONYM|LOGIN|USER|ROLE|DATABASE|CERTIFICATE|INDEX|PACKAGE|TYPE|DOMAIN|DIRECTORY|PROFILE|ASSEMBLY|XML\s+SCHEMA\s+COLLECTION|PARTITION\s+SCHEME|FULLTEXT\s+CATALOG|SECURITY\s+POLICY)\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["terraform"] =
        [
            // Terraform resource/data: capture the logical name (second quoted token), not the type
            // Terraform resource/data: 型ではなく論理名（第2引用トークン）をキャプチャ
            new("class",    new Regex(@"^\s*resource\s+""[^""]+""\s+""(?<name>[^""]+)""", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*data\s+""[^""]+""\s+""(?<name>[^""]+)""", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*module\s+""(?<name>[^""]+)""", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*provider\s+""(?<name>[^""]+)""", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*(?<name>terraform)\s*\{", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*(?<name>import|moved|removed)\s*\{", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*check\s+""(?<name>[^""]+)""", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*variable\s+""(?<name>[^""]+)""", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*output\s+""(?<name>[^""]+)""", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?<name>locals)\s*\{", RegexOptions.Compiled), BodyStyle.Brace),
        ],
        ["css"] =
        [
            // @import / @use (SCSS) / インポート
            new("import",   new Regex(@"^\s*@(?:import|use|forward)\s+(?<name>.+?)\s*;", RegexOptions.Compiled), BodyStyle.None),
            // @counter-style / カウンタースタイル
            new("function", new Regex(@"^\s*@counter-style\s+(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // @function (SCSS) / 関数
            new("function", new Regex(@"^\s*@function\s+(?<name>[\w-]+)", RegexOptions.Compiled), BodyStyle.Brace),
            // @mixin (SCSS) / ミックスイン
            new("function", new Regex(@"^\s*@mixin\s+(?<name>[\w-]+)", RegexOptions.Compiled), BodyStyle.Brace),
            // @keyframes / キーフレーム
            new("function", new Regex(@"^\s*@keyframes\s+(?<name>[\w-]+)", RegexOptions.Compiled), BodyStyle.Brace),
            // @font-face / フォントフェイス
            new("function", new Regex(@"^\s*@font-face\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // @property / カスタムプロパティ登録
            new("property", new Regex(@"^\s*@property\s+(?<name>--[\w-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // @page / ページ規則
            new("namespace", new Regex(@"^\s*@page(?:\s+(?<name>:[\w-]+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // @namespace / 名前空間
            new("namespace", new Regex(@"^\s*@namespace(?:\s+(?<name>[\w-]+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // @layer reset, base, theme; / レイヤー順序宣言
            new("namespace", new Regex(@"^\s*@layer\s+(?<name>[\w-]+)(?:\s*,\s*[\w-]+)*\s*;", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Grouping at-rules / grouping at-rule
            new("namespace", new Regex(@"^\s*@(?<name>layer|container|supports|media)\b[^{]*\{", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // :root selector / :root セレクタ
            new("class",    new Regex(@"^\s*(?<name>:root)\s*[,{]", RegexOptions.Compiled), BodyStyle.Brace),
            // Standalone attribute selector / 単独属性セレクタ
            new("class",    new Regex(@"^\s*(?<name>\[[^\]]+\](?:(?:::?[\w-]+)|(?:\[[^\]]+\]))*)\s*[,{]", RegexOptions.Compiled), BodyStyle.Brace),
            // Pseudo-class / pseudo-element / attribute selectors / 疑似クラス・疑似要素・属性セレクタ
            new("class",    new Regex(@"^\s*(?<name>(?:[#.]?[\w-]+|\*)(?:(?:::?[\w-]+)|(?:\[[^\]]+\]))+)\s*[,{]", RegexOptions.Compiled), BodyStyle.Brace),
            // CSS class selector at top level (not nested) / トップレベルのCSSクラスセレクタ
            new("class",    new Regex(@"^\s*(?<name>\.[\w-]+)(?=[\s\.,:>+~\[\{])", RegexOptions.Compiled), BodyStyle.Brace),
            // CSS ID selector at top level / トップレベルのIDセレクタ
            new("class",    new Regex(@"^\s*(?<name>#[\w-]+)\s*[,{]", RegexOptions.Compiled), BodyStyle.Brace),
            // Native CSS nesting selectors / ネイティブ CSS nesting セレクタ
            new("property", new Regex(@"^\s*&(?:(?::(?<name>[\w-]+))|(?:\s*(?:[>+~]\s*)?(?:\.|#)?(?<name>[\w-]+)))(?:(?:::?[\w-]+)|(?:\[[^\]]+\]))*\s*[,{]", RegexOptions.Compiled), BodyStyle.Brace),
            // CSS custom property declaration / CSS カスタムプロパティ宣言
            new("property", new Regex(@"^\s*(?<name>--[\w-]+)\s*:", RegexOptions.Compiled), BodyStyle.None),
            // SCSS $variable declaration / SCSS 変数宣言
            new("property", new Regex(@"^\$(?<name>[\w-]+)\s*:", RegexOptions.Compiled), BodyStyle.None),
            // SCSS placeholder selector / SCSS プレースホルダーセレクタ
            new("class",    new Regex(@"^\s*(?<name>%[\w-]+)\s*[,{]", RegexOptions.Compiled), BodyStyle.Brace),
        ],
        ["sass"] =
        [
            // Sass indented syntax has no braces, so keep these as line-level anchors.
            // Sass インデント構文は波括弧を持たないため、行単位のアンカーとして扱う。
            new("import",   new Regex(@"^\s*@(?:import|use|forward)\s+(?<name>.+?)(?:\s*!default)?\s*$", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:@mixin\s+|=)(?<name>[\w-]+)", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*@function\s+(?<name>[\w-]+)", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*@keyframes\s+(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*\$(?<name>[\w-]+)\s*:", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?<name>[.#%][\w-]+)(?=[\s\.,:>+~\[]|$)", RegexOptions.Compiled), BodyStyle.None),
            new("property", new Regex(@"^\s*&(?:(?::(?<name>[\w-]+))|(?:\s*(?:[>+~]\s*)?(?:\.|#)?(?<name>[\w-]+)))", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["stylus"] =
        [
            // Stylus supports optional punctuation, so only capture conservative declaration shapes.
            // Stylus は句読点を省略できるため、保守的な宣言形だけを捕捉する。
            new("import",   new Regex(@"^\s*@(?:import|require|use)\s+(?<name>.+?)\s*$", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^(?<name>[A-Za-z_][\w-]*)\s*\([^)\r\n]*\)\s*$", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*@keyframes\s+(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            new("property", new Regex(@"^\s*\$?(?<name>[A-Za-z_][\w-]*)\s*(?:=|:=)\s*", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?<name>[.#%][\w-]+)(?=[\s\.,:>+~\[]|$)", RegexOptions.Compiled), BodyStyle.None),
            new("property", new Regex(@"^\s*&(?:(?::(?<name>[\w-]+))|(?:\s*(?:[>+~]\s*)?(?:\.|#)?(?<name>[\w-]+)))", RegexOptions.Compiled), BodyStyle.None),
        ],
        // HTML does not use the regex pattern loop — it needs true tag-structure
        // awareness (attribute enumeration, quoted-value handling, custom-element
        // detection) that regex alone can't express without losing outer-tag
        // context. `Extract` dispatches to `ExtractHtmlSymbols`, which drives a
        // character state machine. The empty list here keeps "html" listed as a
        // supported language via `GetSupportedLanguages()` without pretending to
        // offer regex-based extraction.
        // HTML は汎用の regex パターンループではなく、タグ構造を理解した走査（属性列挙、
        // 引用符付き値の処理、カスタム要素検出）を必要とするため、`Extract` は
        // `ExtractHtmlSymbols` に分岐して文字単位の state machine で抽出する。空リストは
        // `GetSupportedLanguages()` で "html" を対応言語として残すための置き場であり、
        // regex 抽出を模したものではない。
        ["html"] = [],
        ["powershell"] =
        [
            // DSC configuration / workflow declarations / DSC 構成・workflow 宣言
            new("function", new Regex(@"^\s*configuration\s+(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.Brace),
            new("function", new Regex(@"^\s*workflow\s+(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // Function/filter declarations with optional scope prefixes / scope プレフィックス付き関数・フィルタ宣言
            new("function", new Regex(@"^\s*(?:function|filter)\s+(?:(?:script|global|local|private):)?(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // PowerShell class members / PowerShell クラスメンバー
            // Return-typed methods and modifiers such as `static` / `hidden` / `static hidden`
            // stay on the function path.
            // 戻り値付き method と `static` / `hidden` / `static hidden` のような修飾子は
            // function パスで扱う。
            new("function", new Regex(@"^\s*(?:(?:static|hidden)\s+)*(?:\[[^\]]+\]\s+)+(?<name>[\w-]+)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // Constructors are bare class-name declarations inside a class body, so the
            // PascalCase gate keeps most cmdlet-style calls out while still catching the
            // canonical PS5+ shape.
            // コンストラクタは class 本体内に置かれる bare な class-name 宣言なので、
            // PascalCase の条件で cmdlet 風の呼び出しを大半弾きつつ、PS5+ の標準形を拾う。
            new("function", new Regex(@"^\s*(?<name>[A-Z]\w*)\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            // Alias definitions / エイリアス定義
            new("alias", new Regex(@"^\s*(?:Set-Alias|New-Alias)\s+(?:-Name\s+)?(?<name>[\w-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Attributes and typed properties / 属性付きプロパティと型付きプロパティ
            new("property", new Regex(@"^\s*(?:(?:static|hidden)\s+)*(?:\[[^\]]+\]\s*)+\$(?<name>\w+)\s*(?:=|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Class (PowerShell 5+) / クラス (PowerShell 5+)
            new("class",    new Regex(@"^\s*class\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // Enum (PowerShell 5+) / enum (PowerShell 5+)
            new("enum",     new Regex(@"^\s*enum\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // Enum values / enum 値
            new("enum",     new Regex(@"^\s{2,}(?<name>[\w-]+)\s*(?:=\s*[^#\r\n]+)?\s*$", RegexOptions.Compiled), BodyStyle.None),
            // Import-Module / using module / using namespace / using assembly / モジュールインポート
            new("import",   new Regex(@"^\s*(?:Import-Module|using\s+(?:module|namespace|assembly))\s+(?<name>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        ["batch"] =
        [
            // Labels — goto :X / call :X targets, the only navigation anchors in a batch script.
            // `::` comment form has no label name, so the name character class naturally rejects it.
            // Dotted labels like `:build.release` are real batch label names, so accept `.` too.
            // `:EOF` is a reserved batch target used by `goto :EOF` / `call :EOF`, not a user-defined
            // label, so exclude it — but only the literal full-name `eof`. Labels that merely begin
            // with `eof` such as `:eof2` / `:eofish` / `:end-of-file` / `:eof.x` must still surface,
            // which is why the negative lookahead checks for name-terminating characters instead of `\b`.
            // ラベル — goto :X / call :X の着地点であり、batch スクリプト内で唯一のナビゲーションアンカー。
            // `::` コメント形式はラベル名を持たないため名前文字クラスが自然に弾く。
            // `:build.release` のようなドット付きラベルも正規のラベル名として受け入れる。
            // `:EOF` は `goto :EOF` / `call :EOF` 用の予約ターゲットであってユーザー定義ラベルではないため除外するが、
            // 除外するのは名前全体が `eof` のときだけ。`:eof2` / `:eofish` / `:end-of-file` / `:eof.x` のように
            // 単に `eof` で始まるだけのラベルは通す必要があるため、`\b` ではなく名前終端文字を見る negative lookahead を使う。
            new("function", new Regex(@"^\s*:(?!eof(?![\w.-]))(?<name>[\w.\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
            // Variable assignment — set VAR=value, set /a VAR=expr, set /p VAR=prompt, set "VAR=value".
            // Also handles `@set VAR=...` (echo suppression prefix), `set /a VAR+=1` (compound
            // assignment operators), `if ... set VAR=...` (inline assignment inside a one-line
            // control statement), and same-line multi-statement forms `set A=1 & set B=2`,
            // `( set X=1 )`, `if ... ( set P=1 ) else set Q=2`, `for ... do set LOOPVAR=...`.
            // Boundary alternation: line-leading `^`, or after `&` / `(` / `\belse` / `\bdo` so
            // the regex (paired with the batch multi-match advance in the extractor loop) can
            // emit one symbol per `set` occurrence on the same line instead of dropping every
            // assignment after the first match. `rem` / `@rem` / `::` comment lines can also
            // contain those boundary tokens (e.g. `REM & set FAKE=1`), so they are short-
            // circuited by `IsBatchCommentLine` before this pattern ever runs — the boundary
            // alternation alone is not enough to keep comment bodies out of the capture.
            // 変数代入 — set VAR=value、set /a VAR=expr、set /p VAR=prompt、set "VAR=value" に対応。
            // 併せて `@set VAR=...` (echo 抑止プレフィクス) 、`set /a VAR+=1` (複合代入演算子) 、
            // `if ... set VAR=...` (1 行制御文内の代入) 、および `set A=1 & set B=2` / `( set X=1 )` /
            // `if ... ( set P=1 ) else set Q=2` / `for ... do set LOOPVAR=...` のような同一行複数ステートメント形も拾う。
            // 境界は `^` / `&` / `(` / `\belse` / `\bdo` のいずれかで、extractor 側の batch 専用
            // multi-match advance と組み合わせて 1 行中の `set` ごとに 1 シンボルを出す。
            // `rem` / `@rem` / `::` コメント行にもこれらの境界トークンが入りうる
            // (`REM & set FAKE=1` 等) ため、この正規表現が走る前に `IsBatchCommentLine` で
            // 行ごと早期スキップしている — 境界 alternation だけではコメント本文を弾ききれない。
            new("property", new Regex(@"(?:(?:^|&|\()\s*|(?:\belse|\bdo)\s+)(?:@\s*)?(?:if\s+.+?\s+)?set\s+(?:/[aApP]\s+)?""?(?<name>[A-Za-z_][\w]*)\s*(?:[+\-*/%&^|]|<<|>>)?=", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.None),
        ],
        // Assembly uses a dedicated line scanner because label body ranges extend until
        // the next label/section rather than a brace or indentation boundary.
        // assembly は label の body range が次の label / section まで続くため、
        // brace / indent 境界ではなく専用の行走査で抽出する。
        ["assembly"] = [],
        ["zig"] =
        [
            // Public and private function declarations / 公開・非公開の関数宣言
            new("function", new Regex(@"^\s*(?:(?<visibility>pub)\s+)?(?:inline\s+)?fn\s+(?<name>\w+)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Struct/union/enum defined via const / const による struct/union/enum 定義
            new("struct",   new Regex(@"^\s*(?:(?<visibility>pub)\s+)?const\s+(?<name>\w+)\s*=\s*(?:extern\s+|packed\s+)?struct\b", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("enum",     new Regex(@"^\s*(?:(?<visibility>pub)\s+)?const\s+(?<name>\w+)\s*=\s*(?:extern\s+)?enum\b", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("class",    new Regex(@"^\s*(?:(?<visibility>pub)\s+)?const\s+(?<name>\w+)\s*=\s*(?:extern\s+|packed\s+)?union\b", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Error set / エラーセット
            new("class",    new Regex(@"^\s*(?:(?<visibility>pub)\s+)?const\s+(?<name>\w+)\s*=\s*error\b", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Test declarations / テスト宣言
            new("function", new Regex(@"^\s*test\s+""(?<name>[^""]+)""", RegexOptions.Compiled), BodyStyle.Brace),
            // @import / インポート
            new("import",   new Regex(@"^\s*(?:(?:pub)\s+)?const\s+\w+\s*=\s*@import\s*\(\s*""(?<name>[^""]+)""", RegexOptions.Compiled), BodyStyle.None),
        ],
    };

    private static readonly string[] BuiltInSymbolLanguages = PatternCache.Keys.ToArray();

    /// <summary>
    /// Return the set of languages that have symbol-extraction patterns.
    /// シンボル抽出パターンを持つ言語のセットを返す。
    /// </summary>
    public static IReadOnlyCollection<string> GetSupportedLanguages()
        => GetSupportedLanguages(workspaceRoot: null);

    internal static IReadOnlyCollection<string> GetSupportedLanguages(string? workspaceRoot)
    {
        var pluginLanguages = ExtractorPluginRegistry.GetSymbolLanguages(workspaceRoot);
        var capacity = BuiltInSymbolLanguages.Length + AdditionalSymbolLanguages.Length + pluginLanguages.Count;
        var languages = new List<string>(capacity);
        var seen = new HashSet<string>(capacity, StringComparer.Ordinal);

        AddSupportedLanguages(BuiltInSymbolLanguages, languages, seen);
        AddSupportedLanguages(AdditionalSymbolLanguages, languages, seen);
        AddSupportedLanguages(pluginLanguages, languages, seen);
        return languages.ToArray();
    }

    private static void AddSupportedLanguages(
        IEnumerable<string> candidates,
        List<string> languages,
        HashSet<string> seen)
    {
        foreach (var language in candidates)
        {
            if (seen.Add(language))
                languages.Add(language);
        }
    }

    private static string? NormalizeLanguage(string? lang)
    {
        if (lang is null)
            return null;

        var trimmed = lang.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return null;

        if (trimmed.Equals("vue", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("svelte", StringComparison.OrdinalIgnoreCase))
        {
            return "typescript";
        }

        if (trimmed.Equals("razor", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("blazor", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("cshtml", StringComparison.OrdinalIgnoreCase))
        {
            return "csharp";
        }

        return trimmed.Equals("cuda", StringComparison.OrdinalIgnoreCase)
            ? "cpp"
            : NormalizeLanguageKey(lang, trimmed);
    }

    private static string? NormalizePluginLanguage(string? lang)
    {
        if (lang is null)
            return null;

        var trimmed = lang.AsSpan().Trim();
        return trimmed.IsEmpty ? null : NormalizeLanguageKey(lang, trimmed);
    }

    private static string NormalizeLanguageKey(string original, ReadOnlySpan<char> trimmed)
    {
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (char.ToLowerInvariant(trimmed[i]) != trimmed[i])
                return trimmed.ToString().ToLowerInvariant();
        }

        return trimmed.Length == original.Length && trimmed.SequenceEqual(original.AsSpan())
            ? original
            : trimmed.ToString();
    }


    private static readonly HashSet<string> ContainerKinds =
    [
        "class", "struct", "interface", "protocol", "protocol_impl", "namespace", "enum", "object", "heading", "specialization", "class_hook"
    ];

    private static bool[] FindPowerShellEnumBodyLines(string[] structuralLines)
    {
        var result = new bool[structuralLines.Length];
        var waitingForOpeningBrace = false;
        var enumBraceDepth = 0;

        for (var i = 0; i < structuralLines.Length; i++)
        {
            var line = structuralLines[i];
            if (enumBraceDepth > 0)
                result[i] = true;

            if (enumBraceDepth == 0 && !waitingForOpeningBrace)
            {
                var trimmed = line.AsSpan().TrimStart();
                if (trimmed.StartsWith("enum", StringComparison.OrdinalIgnoreCase)
                    && trimmed.Length > 4
                    && char.IsWhiteSpace(trimmed[4]))
                {
                    waitingForOpeningBrace = true;
                }
            }

            if (!waitingForOpeningBrace && enumBraceDepth == 0)
                continue;

            foreach (var character in line)
            {
                if (character == '{')
                    enumBraceDepth++;
                else if (character == '}')
                    enumBraceDepth--;
            }

            if (enumBraceDepth > 0)
            {
                waitingForOpeningBrace = false;
            }
            else if (!waitingForOpeningBrace || line.Contains('}'))
            {
                enumBraceDepth = 0;
                waitingForOpeningBrace = false;
            }
        }

        return result;
    }

    private static bool IsRustDirectTraitBodyMember(List<SymbolRecord> symbols, int candidateLine)
    {
        SymbolRecord? innermostContainer = null;
        foreach (var symbol in symbols)
        {
            if (!symbol.BodyStartLine.HasValue || !symbol.BodyEndLine.HasValue)
                continue;
            if (candidateLine < symbol.BodyStartLine.Value || candidateLine > symbol.BodyEndLine.Value)
                continue;
            if (innermostContainer == null || symbol.StartLine >= innermostContainer.StartLine)
                innermostContainer = symbol;
        }

        return innermostContainer?.Kind == "protocol";
    }

    private static bool TryPrepareSymbolExtraction(
        long fileId,
        string? originalLang,
        string content,
        bool contentIsNormalized,
        bool? hasOversizeLine,
        int? conflictMarkerLine,
        string? filePath,
        string? projectRoot,
        bool patternConfigsAlreadyLoaded,
        CancellationToken cancellationToken,
        out string? lang,
        out string preparedContent,
        out List<SymbolRecord>? symbols)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lang = NormalizeLanguage(originalLang);
        var pluginLanguage = NormalizePluginLanguage(originalLang);
        preparedContent = content;
        symbols = null;

        if (lang == null && pluginLanguage == null)
        {
            symbols = [];
            return true;
        }

        // Null / empty fast path — keep the direct-call null-safe contract that
        // FileIndexer.StripLineLeadingInvisibles' IsNullOrEmpty check used to provide
        // before the CRLF normalization step was added in front of it. Closes #183.
        // null / 空入力は早期 return。CRLF 正規化を StripLineLeadingInvisibles の前に
        // 入れたことで helper 側の IsNullOrEmpty による null 許容が効かなくなる
        // ため、direct call の null セーフ契約をここで復元する。Closes #183.
        if (string.IsNullOrEmpty(content))
        {
            symbols = [];
            return true;
        }

        // Oversize-line skip: bail out for files that pack a multi-MB payload
        // into a single physical line (minified bundles, base64 blobs). The
        // matching guard in ChunkSplitter / ReferenceExtractor / ValidateContent
        // keeps the indexer from stalling on regex backtracking and surfaces
        // the skip as a `line_too_long` FileIssue. Closes #1542.
        // 1 行に複数 MB のペイロードを詰めたファイル (minified bundle や base64
        // ペイロード等) は早期に抜ける。ChunkSplitter / ReferenceExtractor /
        // ValidateContent の同等ガードと合わせて、正規表現のバックトラックで
        // インデクサが止まることを防ぎ、スキップは `line_too_long` FileIssue
        // として表面化させる。Closes #1542.
        if (hasOversizeLine ?? ChunkSplitter.HasOversizeLine(content))
        {
            symbols = [];
            return true;
        }

        if ((conflictMarkerLine ?? FileIndexer.GetConflictMarkerLine(content)) > 0)
        {
            symbols = [];
            return true;
        }

        if (!contentIsNormalized)
        {
            content = FileIndexer.NormalizeContentForPrepass(content);
        }
        preparedContent = content;
        cancellationToken.ThrowIfCancellationRequested();
        if (!patternConfigsAlreadyLoaded)
            ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);

        if (pluginLanguage != null
            && !PatternCache.ContainsKey(pluginLanguage)
            && ExtractorPluginRegistry.TryGetSymbolExtractor(pluginLanguage, projectRoot, out var pluginExtractor))
        {
            var pluginSymbols = pluginExtractor.Extract(
                    fileId,
                    preparedContent,
                    new ExtractionContext(pluginLanguage, filePath));
            symbols = CopyPluginSymbols(pluginSymbols);
            return true;
        }

        return false;
    }

    private static List<SymbolRecord> CopyPluginSymbols(IReadOnlyList<SymbolRecord> symbols)
    {
        var copiedSymbols = new List<SymbolRecord>(symbols.Count);
        for (var i = 0; i < symbols.Count; i++)
            copiedSymbols.Add(symbols[i]);
        return copiedSymbols;
    }

    /// <summary>
    /// Extract symbols from the given source content.
    /// 指定されたソース内容からシンボルを抽出する。
    /// </summary>
    /// <param name="fileId">The file ID in the database / データベース上のファイルID</param>
    /// <param name="lang">Detected language / 検出された言語</param>
    /// <param name="content">Full file content / ファイル全体の内容</param>
    /// <param name="filePath">Relative file path when available / 利用可能なら相対ファイルパス</param>
    /// <returns>List of extracted symbols / 抽出されたシンボルのリスト</returns>
    public static List<SymbolRecord> Extract(long fileId, string? lang, string content, string? filePath = null, string? projectRoot = null, CancellationToken cancellationToken = default)
        => ExtractCore(
            fileId,
            lang,
            content,
            contentIsNormalized: false,
            hasOversizeLine: null,
            conflictMarkerLine: null,
            filePath,
            projectRoot,
            patternConfigsAlreadyLoaded: false,
            cancellationToken: cancellationToken);

    internal static List<SymbolRecord> ExtractWithPatternConfigsLoaded(
        long fileId,
        string? lang,
        string content,
        string? filePath = null,
        string? projectRoot = null,
        CancellationToken cancellationToken = default)
        => ExtractCore(
            fileId,
            lang,
            content,
            contentIsNormalized: false,
            hasOversizeLine: null,
            conflictMarkerLine: null,
            filePath,
            projectRoot,
            patternConfigsAlreadyLoaded: true,
            cancellationToken: cancellationToken);

    internal static List<SymbolRecord> ExtractNormalized(
        long fileId,
        string? lang,
        string content,
        bool hasOversizeLine,
        string? filePath = null,
        string? projectRoot = null,
        CancellationToken cancellationToken = default,
        int? conflictMarkerLine = null,
        bool patternConfigsAlreadyLoaded = false)
        => ExtractCore(
            fileId,
            lang,
            content,
            contentIsNormalized: true,
            hasOversizeLine,
            conflictMarkerLine,
            filePath,
            projectRoot,
            patternConfigsAlreadyLoaded,
            cancellationToken);

    private static void ExtractHdlInlineParameterSymbols(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!line.Contains("parameter", StringComparison.Ordinal))
                continue;

            foreach (Match match in HdlInlineParameterRegex.Matches(line))
            {
                var name = match.Groups["name"].ValueSpan.Trim().ToString();
                if (name.Length == 0)
                    continue;

                var lineNumber = index + 1;
                if (HasSymbolLineIdentity(extractionState, symbols, fileId, lineNumber, "property", name))
                    continue;

                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = name,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = match.Groups["name"].Index,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                });
            }
        }
    }

    private static bool? TryClassifyCSharpExtractorMetadataTarget(string? lang, string kind, string? signature)
    {
        if (!string.Equals(lang, "csharp", StringComparison.Ordinal) || kind != "class")
            return null;

        foreach (var baseIdentifier in ParseCSharpExtractorBaseIdentifiers(signature))
        {
            var normalized = StripCSharpVerbatimIdentifierPrefixes(baseIdentifier);
            if (string.Equals(normalized, "Attribute", StringComparison.Ordinal)
                || string.Equals(normalized, "System.Attribute", StringComparison.Ordinal)
                || string.Equals(normalized, "global::System.Attribute", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return null;
    }

    private static List<string> ParseCSharpExtractorBaseIdentifiers(string? signature)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(signature))
            return result;

        int colonIdx = FindCSharpExtractorBaseListColon(signature);
        if (colonIdx < 0)
            return result;

        int genericDepth = 0;
        var current = new StringBuilder();
        for (int i = colonIdx + 1; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c == '<')
            {
                genericDepth++;
                current.Append(c);
                continue;
            }
            if (c == '>')
            {
                if (genericDepth > 0)
                    genericDepth--;
                current.Append(c);
                continue;
            }
            if (c == '{')
                break;
            if (genericDepth == 0 && c == ',')
            {
                AddCSharpExtractorBaseIdentifier(result, current.ToString());
                current.Clear();
                continue;
            }
            if (genericDepth == 0 && (c == 'w' || c == 'W') && LooksLikeCSharpWhereKeyword(signature, i))
            {
                AddCSharpExtractorBaseIdentifier(result, current.ToString());
                return result;
            }
            current.Append(c);
        }

        AddCSharpExtractorBaseIdentifier(result, current.ToString());
        return result;
    }

    private static int FindCSharpExtractorBaseListColon(string signature)
    {
        int genericDepth = 0;
        int parenDepth = 0;
        for (int i = 0; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c == '<') { genericDepth++; continue; }
            if (c == '>') { if (genericDepth > 0) genericDepth--; continue; }
            if (c == '(') { parenDepth++; continue; }
            if (c == ')') { if (parenDepth > 0) parenDepth--; continue; }
            if (c == '{')
                return -1;
            if (genericDepth == 0 && parenDepth == 0 && (c == 'w' || c == 'W')
                && LooksLikeCSharpWhereKeyword(signature, i))
            {
                return -1;
            }
            if (c == ':' && genericDepth == 0 && parenDepth == 0)
            {
                if (i + 1 < signature.Length && signature[i + 1] == ':')
                {
                    i++;
                    continue;
                }
                if (i > 0 && signature[i - 1] == ':')
                    continue;
                return i;
            }
        }
        return -1;
    }

    private static bool LooksLikeCSharpWhereKeyword(string signature, int i)
    {
        if (i + 5 > signature.Length)
            return false;
        if (string.Compare(signature, i, "where", 0, 5, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        if (i > 0)
        {
            char prev = signature[i - 1];
            if (char.IsLetterOrDigit(prev) || prev == '_')
                return false;
        }
        if (i + 5 < signature.Length)
        {
            char next = signature[i + 5];
            if (char.IsLetterOrDigit(next) || next == '_')
                return false;
        }
        return true;
    }

    private static void AddCSharpExtractorBaseIdentifier(List<string> result, string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return;

        int cut = trimmed.Length;
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '<' || char.IsWhiteSpace(c))
            {
                cut = i;
                break;
            }
        }
        var head = trimmed[..cut];
        if (head.Length > 0)
            result.Add(head);
    }

    private static string StripCSharpVerbatimIdentifierPrefixes(string value)
    {
        if (value.IndexOf('@', StringComparison.Ordinal) < 0)
            return value;
        return value.Replace(".@", ".", StringComparison.Ordinal)
            .Replace("::@", "::", StringComparison.Ordinal)
            .TrimStart('@');
    }

    private static void ClassifyScalaCompanions(List<SymbolRecord> symbols)
    {
        Dictionary<string, List<SymbolRecord>>? topLevelClasses = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "class"
                || !string.IsNullOrWhiteSpace(symbol.ContainerKind)
                || string.IsNullOrWhiteSpace(symbol.Name))
            {
                continue;
            }

            topLevelClasses ??= new Dictionary<string, List<SymbolRecord>>(StringComparer.Ordinal);
            if (!topLevelClasses.TryGetValue(symbol.Name, out var companionClasses))
            {
                companionClasses = new List<SymbolRecord>();
                topLevelClasses.Add(symbol.Name, companionClasses);
            }

            companionClasses.Add(symbol);
        }

        if (topLevelClasses is not { Count: > 0 })
            return;

        foreach (var scalaObject in symbols)
        {
            if (scalaObject.Kind != "object"
                || !string.IsNullOrWhiteSpace(scalaObject.ContainerKind)
                || string.IsNullOrWhiteSpace(scalaObject.Name))
            {
                continue;
            }

            if (!topLevelClasses.TryGetValue(scalaObject.Name, out var companionClasses))
                continue;

            scalaObject.SubKind ??= "companion_object";
            foreach (var companionClass in companionClasses)
                companionClass.SubKind ??= "has_companion_object";
        }
    }

    private static void ClassifyJavaScriptTypeScriptReactHooks(List<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is "function" or "lambda" && IsJavaScriptTypeScriptReactHookName(symbol.Name))
                symbol.Kind = "hook";
        }
    }


    internal static bool IsJavaScriptTypeScriptReactHookName(string name)
        => name.Length >= 4
           && name.StartsWith("use", StringComparison.Ordinal)
           && IsJavaScriptTypeScriptIdentifierStart(name[3])
           && char.IsUpper(name[3]);




    private static int FindFirstNonWhitespaceColumn(string text)
    {
        var column = 0;
        while (column < text.Length && char.IsWhiteSpace(text[column]))
            column++;

        return column;
    }


    // Java identifier start: Unicode letter / letter-number / underscore / dollar. Continue chars also
    // allow digits, connector punctuation, and combining marks so enum members like `RÉSUMÉ` survive intact.
    // Java 識別子の先頭: Unicode の letter / letter-number / underscore / dollar。
    // 継続文字は数字・connector punctuation・結合文字も許可し、`RÉSUMÉ` のような enum member を切らない。
    public static void ApplyFamilyScope(IEnumerable<SymbolRecord> symbols, string scopeKey)
    {
        foreach (var symbol in symbols)
        {
            if (string.IsNullOrWhiteSpace(symbol.FamilyKey))
                continue;

            symbol.FamilyKey = $"{scopeKey}|{symbol.FamilyKey}";
        }
    }

    private sealed class SymbolExtractionState
    {
        private readonly int _initialCapacity;
        private SymbolAddState? _symbolAddState;
        private SymbolLineIdentityState? _symbolLineIdentityState;

        public SymbolExtractionState(int initialCapacity = 0)
        {
            _initialCapacity = initialCapacity;
        }

        public static SymbolExtractionState FromSymbols(List<SymbolRecord> symbols)
        {
            var state = new SymbolExtractionState(symbols.Count);
            foreach (var symbol in symbols)
                state.Record(symbol);
            return state;
        }

        public int GetExactDuplicateCount(SymbolRecord symbol) =>
            _symbolAddState?.GetExactDuplicateCount(symbol) ?? 0;

        public int? GetSameLineSignatureOccurrenceIndex(SymbolRecord symbol) =>
            _symbolAddState?.GetSameLineSignatureOccurrenceIndex(symbol)
            ?? (TryGetSameLineSignatureKey(symbol, out _) ? 0 : null);

        public bool HasSymbolLineIdentity(List<SymbolRecord> symbols, SymbolLineIdentity identity)
        {
            if (symbols.Count == 0)
                return false;

            return (_symbolLineIdentityState ??= new(_initialCapacity)).Contains(symbols, identity);
        }

        public void Record(SymbolRecord symbol) =>
            (_symbolAddState ??= new(_initialCapacity)).Record(symbol);

        public void Remove(SymbolRecord symbol) =>
            _symbolAddState?.Remove(symbol);
    }

    private sealed class SymbolExtractionList : List<SymbolRecord>
    {
        public SymbolExtractionList(int initialCapacity)
            : base(initialCapacity)
        {
            ExtractionState = new SymbolExtractionState(initialCapacity);
        }

        public SymbolExtractionState ExtractionState { get; }
    }

    private sealed class SymbolAddState
    {
        private readonly int _initialCapacity;
        private Dictionary<SymbolRecordIdentity, int>? _exactCounts;
        private Dictionary<SameLineSignatureKey, int>? _sameLineSignatureCounts;

        public SymbolAddState(int initialCapacity)
        {
            _initialCapacity = initialCapacity;
        }

        public int GetExactDuplicateCount(SymbolRecord symbol)
        {
            if (_exactCounts is null)
                return 0;

            var key = new SymbolRecordIdentity(symbol);
            return _exactCounts.TryGetValue(key, out var count) ? count : 0;
        }

        public int? GetSameLineSignatureOccurrenceIndex(SymbolRecord symbol)
        {
            if (!TryGetSameLineSignatureKey(symbol, out var key))
                return null;

            if (_sameLineSignatureCounts is null)
                return 0;

            return _sameLineSignatureCounts.TryGetValue(key, out var count) ? count : 0;
        }

        public void Record(SymbolRecord symbol)
        {
            var exactKey = new SymbolRecordIdentity(symbol);
            var exactCounts = _exactCounts ??= CreateSymbolRecordIdentityDictionary(_initialCapacity);
            exactCounts[exactKey] = exactCounts.TryGetValue(exactKey, out var exactCount)
                ? exactCount + 1
                : 1;

            if (TryGetSameLineSignatureKey(symbol, out var sameLineKey))
            {
                var sameLineSignatureCounts = _sameLineSignatureCounts ??= CreateSameLineSignatureDictionary(_initialCapacity);
                sameLineSignatureCounts[sameLineKey] = sameLineSignatureCounts.TryGetValue(sameLineKey, out var sameLineCount)
                    ? sameLineCount + 1
                    : 1;
            }
        }

        public void Remove(SymbolRecord symbol)
        {
            if (_exactCounts is not null)
            {
                var exactKey = new SymbolRecordIdentity(symbol);
                if (_exactCounts.TryGetValue(exactKey, out var exactCount))
                {
                    if (exactCount <= 1)
                        _exactCounts.Remove(exactKey);
                    else
                        _exactCounts[exactKey] = exactCount - 1;
                }
            }

            if (!TryGetSameLineSignatureKey(symbol, out var sameLineKey))
                return;

            if (_sameLineSignatureCounts is null)
                return;

            if (!_sameLineSignatureCounts.TryGetValue(sameLineKey, out var sameLineCount))
                return;

            if (sameLineCount <= 1)
                _sameLineSignatureCounts.Remove(sameLineKey);
            else
                _sameLineSignatureCounts[sameLineKey] = sameLineCount - 1;
        }
    }

    private static Dictionary<SymbolRecordIdentity, int> CreateSymbolRecordIdentityDictionary(int initialCapacity) =>
        initialCapacity == 0
            ? new Dictionary<SymbolRecordIdentity, int>()
            : new Dictionary<SymbolRecordIdentity, int>(initialCapacity);

    private static Dictionary<SameLineSignatureKey, int> CreateSameLineSignatureDictionary(int initialCapacity) =>
        initialCapacity == 0
            ? new Dictionary<SameLineSignatureKey, int>()
            : new Dictionary<SameLineSignatureKey, int>(initialCapacity);

    private readonly record struct SymbolRecordIdentity(
        string Kind,
        string Name,
        int Line,
        int StartLine,
        int? StartColumn,
        int EndLine,
        int? BodyStartLine,
        int? BodyEndLine,
        string? Signature,
        string? Visibility,
        string? ReturnType)
    {
        public SymbolRecordIdentity(SymbolRecord symbol)
            : this(
                symbol.Kind,
                symbol.Name,
                symbol.Line,
                symbol.StartLine,
                symbol.StartColumn,
                symbol.EndLine,
                symbol.BodyStartLine,
                symbol.BodyEndLine,
                symbol.Signature,
                symbol.Visibility,
                symbol.ReturnType)
        {
        }
    }

    private readonly record struct SymbolLineIdentity(long FileId, int Line, string Kind, string Name);
    private readonly record struct SymbolKindNameIdentity(string Kind, string Name);

    private sealed class SymbolLineIdentityState
    {
        private readonly HashSet<SymbolLineIdentity> _identities;
        private int _knownCount;

        public SymbolLineIdentityState(int initialCapacity)
        {
            _identities = initialCapacity == 0 ? [] : new HashSet<SymbolLineIdentity>(initialCapacity);
        }

        public bool Contains(List<SymbolRecord> symbols, SymbolLineIdentity identity)
        {
            Sync(symbols);
            return _identities.Contains(identity);
        }

        private void Sync(List<SymbolRecord> symbols)
        {
            if (_knownCount > symbols.Count)
            {
                _identities.Clear();
                _knownCount = 0;
            }

            for (; _knownCount < symbols.Count; _knownCount++)
                _identities.Add(GetSymbolLineIdentity(symbols[_knownCount]));
        }
    }

    private static HashSet<SymbolLineIdentity> BuildSymbolLineIdentities(IEnumerable<SymbolRecord> symbols, int expectedAdditionalLines = 0)
    {
        var identities = symbols is ICollection<SymbolRecord> collection
            ? new HashSet<SymbolLineIdentity>(collection.Count + EstimateSymbolListInitialCapacity(expectedAdditionalLines))
            : new HashSet<SymbolLineIdentity>();
        foreach (var symbol in symbols)
            identities.Add(GetSymbolLineIdentity(symbol));
        return identities;
    }

    private static SymbolLineIdentity GetSymbolLineIdentity(SymbolRecord symbol)
        => new(symbol.FileId, symbol.Line, symbol.Kind, symbol.Name);

    private static bool HasSymbolLineIdentity(
        HashSet<SymbolLineIdentity> identities,
        long fileId,
        int lineNumber,
        string kind,
        string name)
        => identities.Contains(new SymbolLineIdentity(fileId, lineNumber, kind, name));

    private static bool HasSymbolLineIdentity(
        SymbolExtractionState extractionState,
        List<SymbolRecord> symbols,
        long fileId,
        int lineNumber,
        string kind,
        string name)
        => extractionState.HasSymbolLineIdentity(symbols, new SymbolLineIdentity(fileId, lineNumber, kind, name));

    private static void RecordSymbolLineIdentity(HashSet<SymbolLineIdentity> identities, SymbolRecord symbol)
        => identities.Add(GetSymbolLineIdentity(symbol));

    private readonly record struct SameLineSignatureKey(int Line, int StartLine, string Signature);
    private static bool TryAddRPacmanPackageLoaderSymbols(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        var codeLine = StripRCommentForPackageLoader(line);
        var startMatch = RPacmanPackageLoaderStartRegex.Match(codeLine);
        if (!startMatch.Success)
            return false;

        var argsStart = startMatch.Index + startMatch.Length;
        var args = codeLine[argsStart..];
        var added = false;
        foreach (Match match in RPacmanPackageLoaderArgumentRegex.Matches(args))
        {
            var quotedNameGroup = match.Groups["quotedName"];
            var nameGroup = quotedNameGroup.Success ? quotedNameGroup : match.Groups["name"];
            if (!nameGroup.Success)
                continue;

            AddSymbolRecord(
                symbols,
                extractionState,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "import",
                    Name = nameGroup.Value,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = argsStart + nameGroup.Index,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                },
                line);
            added = true;
        }

        return added;
    }

    private static string StripRCommentForPackageLoader(string line)
    {
        var inBacktickIdentifier = false;
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0')
            {
                if (ch == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (inBacktickIdentifier)
            {
                if (ch == '`')
                    inBacktickIdentifier = false;
                continue;
            }

            if (ch == '`')
            {
                inBacktickIdentifier = true;
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '#')
                return line[..i];
        }

        return line;
    }

    private static bool TryGetSameLineSignatureKey(SymbolRecord symbol, out SameLineSignatureKey key)
    {
        if (symbol.Signature != null
            && symbol.StartLine == symbol.EndLine
            && symbol.Line == symbol.StartLine)
        {
            key = new SameLineSignatureKey(symbol.Line, symbol.StartLine, symbol.Signature);
            return true;
        }

        key = default;
        return false;
    }

    private static SymbolExtractionState ResolveExtractionState(List<SymbolRecord> symbols) =>
        symbols is SymbolExtractionList extractionSymbols
            ? extractionSymbols.ExtractionState
            : SymbolExtractionState.FromSymbols(symbols);

    private static void AddSymbolRecord(
        List<SymbolRecord> symbols,
        HashSet<SymbolLineIdentity>? cssSeenSymbols,
        int lineNumber,
        SymbolRecord symbol,
        string? rawLine = null) =>
        AddSymbolRecord(symbols, ResolveExtractionState(symbols), cssSeenSymbols, lineNumber, symbol, rawLine);

    private static void AddSymbolRecord(
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState,
        HashSet<SymbolLineIdentity>? cssSeenSymbols,
        int lineNumber,
        SymbolRecord symbol,
        string? rawLine = null)
    {
        if (string.IsNullOrWhiteSpace(symbol.Name))
            return;

        if (cssSeenSymbols != null)
        {
            var identity = new SymbolLineIdentity(symbol.FileId, lineNumber, symbol.Kind, symbol.Name);
            if (!cssSeenSymbols.Add(identity))
                return;
        }

        if (symbol.Kind == "function"
            && (symbol.BodyStartLine != null || symbol.BodyEndLine != null))
        {
            RemoveTrailingSameNameDeclarationOnlyFunctions(symbols, extractionState, symbol);
        }

        symbol.SameLineSignatureOccurrenceIndex = extractionState.GetSameLineSignatureOccurrenceIndex(symbol);

        // Same-line restart paths can legitimately revisit the same declaration from a
        // different regex row or restart offset. Suppress only exact duplicate symbol
        // records so mixed-kind recovery does not emit the same declaration twice while
        // still allowing legitimate overloads / siblings with the same short name but
        // different ranges or signatures. Closes #472 / #473 follow-up.
        // same-line の restart 経路では、別 regex 行や別 restart offset から同じ宣言を
        // 再訪しうる。ここでは exact duplicate の `SymbolRecord` だけを抑止し、
        // mixed-kind 回復で同じ宣言が二重出力されるのを防ぎつつ、範囲や signature が
        // 異なる正当な overload / sibling はそのまま残す。Closes #472 / #473 follow-up.
        var duplicateCount = extractionState.GetExactDuplicateCount(symbol);
        if (duplicateCount > 0
            && !HasRemainingSameLineSignatureOccurrence(symbol, rawLine, duplicateCount))
        {
            return;
        }

        extractionState.Record(symbol);
        symbols.Add(symbol);
    }


    private static void RemoveTrailingSameNameDeclarationOnlyFunctions(
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState,
        SymbolRecord symbol)
    {
        for (var index = symbols.Count - 1; index >= 0; index--)
        {
            var prior = symbols[index];
            if (prior.FileId != symbol.FileId
                || prior.Kind != symbol.Kind
                || !string.Equals(prior.Name, symbol.Name, StringComparison.Ordinal)
                || !string.Equals(prior.ContainerKind, symbol.ContainerKind, StringComparison.Ordinal)
                || !string.Equals(prior.ContainerName, symbol.ContainerName, StringComparison.Ordinal)
                || !string.Equals(prior.ContainerQualifiedName, symbol.ContainerQualifiedName, StringComparison.Ordinal))
            {
                break;
            }

            if (prior.BodyStartLine != null || prior.BodyEndLine != null)
                break;

            var signature = prior.Signature?.TrimStart();
            if (signature != null
                && (signature.StartsWith("declare ", StringComparison.Ordinal)
                    || CSharpPartialFunctionDeclarationSignatureRegex.IsMatch(signature)
                    || IsAdaForwardDeclarationPair(signature, symbol.Signature)))
            {
                break;
            }

            extractionState.Remove(prior);
            symbols.RemoveAt(index);
        }
    }

    private static bool IsAdaForwardDeclarationPair(
        string declarationSignature,
        string? implementationSignature)
    {
        if (implementationSignature == null
            || !AdaRoutineBodySignatureRegex.IsMatch(implementationSignature))
        {
            return false;
        }

        var trimmed = declarationSignature.Trim();
        if (!trimmed.EndsWith(';'))
            return false;

        return trimmed.StartsWith("procedure ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("function ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("overriding procedure ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("overriding function ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("not overriding procedure ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("not overriding function ", StringComparison.OrdinalIgnoreCase);
    }

    // Some compact same-line C# fixtures can legitimately contain two distinct siblings with
    // the same short signature on the same physical line
    // (`Child { } } public partial class Child { }`). Allow as many identical rows as the raw
    // line actually contains, and suppress only the true restart duplicates beyond that. Closes #552.
    // compact な同一行 C# fixture では、同じ短い signature を持つ別 sibling が同じ物理行に
    // 実在しうる (`Child { } } public partial class Child { }`)。raw 行に実在する出現回数までは
    // 許容し、それを超える restart 由来の真の duplicate だけを抑止する。Closes #552.
    private static bool HasRemainingSameLineSignatureOccurrence(SymbolRecord symbol, string? rawLine, int duplicateCount)
    {
        if (rawLine == null
            || symbol.Signature == null
            || symbol.StartLine != symbol.EndLine
            || symbol.Line != symbol.StartLine)
        {
            return false;
        }

        return CountNonOverlappingOccurrences(rawLine, symbol.Signature) > duplicateCount;
    }


    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) ResolveRange(string[] lines, int startIndex, BodyStyle bodyStyle) =>
        ResolveRange(lines, startIndex, bodyStyle, null, 0, null, null);

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) ResolveRange(
        string[] lines,
        int startIndex,
        BodyStyle bodyStyle,
        string? lang = null,
        int startColumn = 0,
        string[]? scientificBodyScannerLines = null,
        bool[]? matlabExplicitOuterClosureByLine = null)
    {
        return bodyStyle switch
        {
            BodyStyle.Brace when lang is "javascript" or "typescript" => FindJavaScriptBraceRange(lines, startIndex, lang, startColumn),
            BodyStyle.Brace when lang == "csharp" => FindCSharpBraceRange(lines, startIndex, startColumn),
            BodyStyle.Brace when lang == "java" => FindJavaBraceRange(lines, startIndex, startColumn),
            BodyStyle.Brace when lang == "shell" => FindShellFunctionRange(lines, startIndex, startColumn),
            BodyStyle.Brace => FindBraceRange(lines, startIndex, startColumn, lang),
            BodyStyle.Indent => FindIndentRange(lines, startIndex),
            BodyStyle.RubyEnd => FindRubyRange(lines, startIndex),
            BodyStyle.FortranEnd => FindFortranRange(lines, startIndex),
            BodyStyle.ElixirEnd => FindElixirRange(lines, startIndex),
            BodyStyle.ScientificEnd when lang is "julia" or "matlab" => FindScientificEndRange(
                  scientificBodyScannerLines ?? PrepareScientificBodyScannerLines(lines, lang),
                  startIndex,
                  lang,
                  matlabExplicitOuterClosureByLine: matlabExplicitOuterClosureByLine),
            BodyStyle.JuliaShortFunction when lang == "julia" => FindJuliaShortFunctionRange(
                scientificBodyScannerLines ?? PrepareScientificBodyScannerLines(lines, lang),
                startIndex),
            BodyStyle.VisualBasicEnd => FindVisualBasicRange(lines, startIndex),
            BodyStyle.PascalEnd => FindPascalRange(lines, startIndex),
            BodyStyle.AdaEnd => FindAdaRange(lines, startIndex),
            BodyStyle.SmalltalkMethod => FindSmalltalkMethodRange(lines, startIndex),
            BodyStyle.SqlProcBody => FindSqlProcBodyRange(lines, startIndex),
            _ => (startIndex + 1, null, null),
        };
    }
    private static bool[] FindCSharpSwitchExpressionLines(string[] structuralLines)
    {
        var switchExpressionLines = new bool[structuralLines.Length];
        Stack<bool>? braceKinds = null;
        var activeSwitchExpressionDepth = 0;
        var pendingSwitchExpression = 0;
        var pendingSwitchKeyword = false;
        var insideBlockComment = false;

        for (int lineIndex = 0; lineIndex < structuralLines.Length; lineIndex++)
        {
            if (activeSwitchExpressionDepth > 0)
                switchExpressionLines[lineIndex] = true;

            var line = structuralLines[lineIndex];
            for (int cursor = 0; cursor < line.Length; cursor++)
            {
                if (insideBlockComment)
                {
                    if (cursor + 1 < line.Length && line[cursor] == '*' && line[cursor + 1] == '/')
                    {
                        insideBlockComment = false;
                        cursor++;
                    }

                    continue;
                }

                if (cursor + 1 < line.Length && line[cursor] == '/' && line[cursor + 1] == '/')
                    break;

                if (cursor + 1 < line.Length && line[cursor] == '/' && line[cursor + 1] == '*')
                {
                    insideBlockComment = true;
                    cursor++;
                    continue;
                }

                if (char.IsWhiteSpace(line[cursor]))
                    continue;

                if (pendingSwitchKeyword)
                {
                    if (line[cursor] == '(')
                    {
                        pendingSwitchKeyword = false;
                    }
                    else if (line[cursor] == '{')
                    {
                        pendingSwitchExpression++;
                        pendingSwitchKeyword = false;
                    }
                    else
                    {
                        pendingSwitchKeyword = false;
                    }
                }

                if (IsCSharpKeywordAt(line, cursor, "switch"))
                {
                    pendingSwitchKeyword = true;
                    cursor += "switch".Length - 1;
                    continue;
                }

                if (line[cursor] == '{')
                {
                    var startsSwitchExpression = pendingSwitchExpression > 0;
                    (braceKinds ??= new Stack<bool>()).Push(startsSwitchExpression);
                    if (startsSwitchExpression)
                    {
                        pendingSwitchExpression--;
                        activeSwitchExpressionDepth++;
                    }

                    continue;
                }

                if (line[cursor] == '}' && braceKinds != null && braceKinds.Count > 0)
                {
                    if (braceKinds.Pop())
                        activeSwitchExpressionDepth--;
                }
            }
        }

        return switchExpressionLines;
    }

    private static bool IsCSharpKeywordAt(string line, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > line.Length)
            return false;

        if (!line.AsSpan(index, keyword.Length).SequenceEqual(keyword))
            return false;

        var previous = index > 0 ? line[index - 1] : '\0';
        if (previous == '@' || previous == '_' || char.IsLetterOrDigit(previous))
            return false;

        var nextIndex = index + keyword.Length;
        if (nextIndex >= line.Length)
            return true;

        var next = line[nextIndex];
        return next != '_' && !char.IsLetterOrDigit(next);
    }


    private readonly record struct DeclaredContainerIdentity(long FileId, string Kind, string Name);

    private static void PopulateDeclaredContainerQualifiedNames(List<SymbolRecord> symbols)
    {
        var requestedContainers = new HashSet<DeclaredContainerIdentity>();
        foreach (var symbol in symbols)
        {
            if (symbol.ContainerKind != null && symbol.ContainerName != null)
                requestedContainers.Add(new DeclaredContainerIdentity(symbol.FileId, symbol.ContainerKind, symbol.ContainerName));
        }

        if (requestedContainers.Count == 0)
            return;

        var declaredContainers = new Dictionary<DeclaredContainerIdentity, List<SymbolRecord>>(requestedContainers.Count);
        foreach (var candidate in symbols)
        {
            var identity = new DeclaredContainerIdentity(candidate.FileId, candidate.Kind, candidate.Name);
            if (!requestedContainers.Contains(identity))
                continue;

            if (!declaredContainers.TryGetValue(identity, out var candidates))
            {
                candidates = [];
                declaredContainers.Add(identity, candidates);
            }
            candidates.Add(candidate);
        }

        foreach (var symbol in symbols)
        {
            if (symbol.ContainerKind == null || symbol.ContainerName == null)
                continue;

            var identity = new DeclaredContainerIdentity(symbol.FileId, symbol.ContainerKind, symbol.ContainerName);
            if (!declaredContainers.TryGetValue(identity, out var candidates))
                continue;

            var container = FindDeclaredContainerSymbol(candidates, symbol);
            if (container == null)
                continue;

            symbol.ContainerQualifiedName = container.ContainerQualifiedName != null
                ? $"{container.ContainerQualifiedName}.{container.Name}"
                : container.Name;
        }
    }

    private static SymbolRecord? FindDeclaredContainerSymbol(IReadOnlyList<SymbolRecord> candidates, SymbolRecord symbol)
    {
        SymbolRecord? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.StartLine > symbol.StartLine
                || candidate.EndLine < symbol.EndLine)
            {
                continue;
            }

            if (best == null
                || candidate.StartLine > best.StartLine
                || (candidate.StartLine == best.StartLine && candidate.EndLine < best.EndLine))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static void AssignContainers(
        List<SymbolRecord> symbols,
        string[]? rawLines = null,
        Func<CSharpLexState[]>? getCSharpLineStartStates = null)
    {
        if (symbols.Count == 0)
            return;

        if (symbols.Count == 1)
        {
            AssignTopLevelFamilyKey(symbols[0]);
            return;
        }

        if (!ContainsContainerCandidates(symbols))
        {
            foreach (var symbol in symbols)
                AssignTopLevelFamilyKey(symbol);
            return;
        }

        var ordered = BuildContainerAssignmentOrder(symbols);

        var stack = new Stack<SymbolRecord>();
        foreach (var orderedSymbol in ordered)
        {
            var symbol = orderedSymbol.Symbol;
            while (stack.Count > 0 && !IsFileScopedNamespace(stack.Peek()) && symbol.StartLine > stack.Peek().EndLine)
                stack.Pop();

            var containerPath = GetEffectiveContainerPath(stack, symbol, rawLines, getCSharpLineStartStates);

            if (containerPath.Count > 0)
            {
                var effectiveContainer = containerPath[^1];
                if (symbol.ContainerKind != null && symbol.ContainerName != null)
                {
                    var explicitContainerIndex = -1;
                    for (var i = containerPath.Count - 1; i >= 0; i--)
                    {
                        var container = containerPath[i];
                        if (container.Kind == symbol.ContainerKind
                            && container.Name == symbol.ContainerName)
                        {
                            explicitContainerIndex = i;
                            break;
                        }
                    }

                    var shouldPromoteToMoreSpecificContainer =
                        symbol.ContainerKind == "enum"
                        && explicitContainerIndex >= 0
                        && explicitContainerIndex < containerPath.Count - 1
                        && effectiveContainer.Kind == "function"
                        && effectiveContainer.ContainerKind == "enum";

                    if (shouldPromoteToMoreSpecificContainer)
                    {
                        effectiveContainer = containerPath[^1];
                        symbol.ContainerKind = effectiveContainer.Kind;
                        symbol.ContainerName = effectiveContainer.Name;
                        symbol.ContainerQualifiedName = BuildQualifiedContainerName(containerPath, containerPath.Count - 1);
                    }
                    else
                    {
                        var explicitContainerAlreadyPresent = explicitContainerIndex == containerPath.Count - 1;
                        var parentQualifiedName = BuildQualifiedContainerName(containerPath);
                        symbol.ContainerQualifiedName ??= explicitContainerAlreadyPresent
                            ? parentQualifiedName
                            : string.IsNullOrWhiteSpace(parentQualifiedName)
                                ? symbol.ContainerName
                                : $"{parentQualifiedName}.{symbol.ContainerName}";
                    }
                }
                else
                {
                    symbol.ContainerKind ??= effectiveContainer.Kind;
                    symbol.ContainerName ??= effectiveContainer.Name;
                    var qualifiedContainerName = BuildQualifiedContainerName(containerPath);
                    symbol.ContainerQualifiedName = qualifiedContainerName;
                    symbol.FamilyKey = BuildInheritedFamilyKey(effectiveContainer, qualifiedContainerName);
                }
            }

            symbol.FamilyKey ??= BuildSelfFamilyKey(symbol, containerPath);

            if (CanContainSymbols(symbol))
                stack.Push(symbol);
        }
    }

    private static void AssignTopLevelFamilyKey(SymbolRecord symbol)
        => symbol.FamilyKey ??= BuildSelfFamilyKey(symbol, Array.Empty<SymbolRecord>());

    private static bool ContainsContainerCandidates(IReadOnlyList<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (CanContainSymbols(symbol))
                return true;
        }

        return false;
    }

    private readonly record struct ContainerAssignmentSortEntry(SymbolRecord Symbol, int OriginalIndex);

    private static List<ContainerAssignmentSortEntry> BuildContainerAssignmentOrder(IReadOnlyList<SymbolRecord> symbols)
    {
        if (symbols.Count == 0)
            return [];

        if (symbols.Count == 1)
            return [new ContainerAssignmentSortEntry(symbols[0], 0)];

        var ordered = new List<ContainerAssignmentSortEntry>(symbols.Count);
        for (var i = 0; i < symbols.Count; i++)
            ordered.Add(new ContainerAssignmentSortEntry(symbols[i], i));

        ordered.Sort(CompareContainerAssignmentSortEntries);
        return ordered;
    }

    private static int CompareContainerAssignmentSortEntries(ContainerAssignmentSortEntry left, ContainerAssignmentSortEntry right)
    {
        var compare = left.Symbol.StartLine.CompareTo(right.Symbol.StartLine);
        if (compare != 0)
            return compare;

        var leftStartColumnRank = left.Symbol.StartColumn.HasValue ? 0 : 1;
        var rightStartColumnRank = right.Symbol.StartColumn.HasValue ? 0 : 1;
        compare = leftStartColumnRank.CompareTo(rightStartColumnRank);
        if (compare != 0)
            return compare;

        compare = (left.Symbol.StartColumn ?? int.MaxValue).CompareTo(right.Symbol.StartColumn ?? int.MaxValue);
        if (compare != 0)
            return compare;

        compare = right.Symbol.EndLine.CompareTo(left.Symbol.EndLine);
        if (compare != 0)
            return compare;

        compare = (right.Symbol.Signature?.Length ?? 0).CompareTo(left.Symbol.Signature?.Length ?? 0);
        if (compare != 0)
            return compare;

        return left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static IReadOnlyList<SymbolRecord> GetEffectiveContainerPath(
        Stack<SymbolRecord> containers,
        SymbolRecord symbol,
        string[]? rawLines = null,
        Func<CSharpLexState[]>? getCSharpLineStartStates = null)
    {
        if (containers.Count == 0)
            return [];

        if (containers.Count == 1)
        {
            var container = containers.Peek();
            return ContainsSymbol(container, symbol, rawLines, getCSharpLineStartStates)
                ? [container]
                : [];
        }

        var orderedContainers = containers.ToArray();
        var containingContainers = new List<SymbolRecord>(orderedContainers.Length);
        for (var i = orderedContainers.Length - 1; i >= 0; i--)
        {
            var container = orderedContainers[i];
            if (ContainsSymbol(container, symbol, rawLines, getCSharpLineStartStates))
                containingContainers.Add(container);
        }

        if (containingContainers.Count == 0)
            return [];

        if (symbol.Kind == "enum" && symbol.BodyStartLine == null)
        {
            var enumIndex = containingContainers.FindLastIndex(container => container.Kind == "enum");
            if (enumIndex >= 0)
                containingContainers.RemoveRange(enumIndex + 1, containingContainers.Count - enumIndex - 1);
        }

        return containingContainers;
    }

    private static string? BuildQualifiedContainerName(IReadOnlyList<SymbolRecord> containers) =>
        BuildQualifiedContainerName(containers, containers.Count);

    private static string? BuildQualifiedContainerName(IReadOnlyList<SymbolRecord> containers, int count)
    {
        if (count <= 0)
            return null;

        StringBuilder? builder = null;
        for (var i = 0; i < count; i++)
        {
            var name = containers[i].Name;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            builder ??= new StringBuilder(name.Length);
            if (builder.Length > 0)
                builder.Append('.');

            builder.Append(name);
        }

        return builder?.ToString();
    }

    private static string? BuildInheritedFamilyKey(SymbolRecord container, string? qualifiedContainerName) =>
        SupportsCrossFileFamily(container)
            ? qualifiedContainerName
            : null;

    private static string? BuildSelfFamilyKey(SymbolRecord symbol, IReadOnlyList<SymbolRecord> containers)
    {
        if (!SupportsCrossFileFamily(symbol))
            return null;

        var symbolName = symbol.Name;
        if (containers.Count == 0)
            return symbolName;

        StringBuilder? builder = null;
        for (var i = 0; i < containers.Count; i++)
        {
            var name = containers[i].Name;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            builder ??= new StringBuilder(name.Length + symbolName.Length + 1);
            if (builder.Length > 0)
                builder.Append('.');

            builder.Append(name);
        }

        builder ??= new StringBuilder(symbolName.Length);
        if (builder.Length > 0)
            builder.Append('.');

        builder.Append(symbolName);

        return builder?.ToString();
    }

    private static bool SupportsCrossFileFamily(SymbolRecord symbol) =>
        symbol.Kind is "class" or "interface" or "struct"
        && !string.IsNullOrWhiteSpace(symbol.Signature)
        && PartialModifierRegex.IsMatch(symbol.Signature);

    private static bool TryGetObjCCategoryDisplayName(string objcDeclaration, string baseName, out string displayName)
    {
        var match = ObjCCategoryDeclarationRegex.Match(objcDeclaration);
        if (!match.Success || !string.Equals(match.Groups["class"].Value, baseName, StringComparison.Ordinal))
        {
            displayName = string.Empty;
            return false;
        }

        var categoryName = match.Groups["category"].ValueSpan.Trim().ToString();
        if (categoryName.Length == 0)
        {
            displayName = string.Empty;
            return false;
        }

        displayName = $"{baseName}({categoryName})";
        return true;
    }

    private static bool CanContainSymbols(SymbolRecord symbol)
    {
        if (symbol.Kind == "function"
            && symbol.ContainerKind == "enum"
            && symbol.BodyStartLine != null
            && symbol.BodyEndLine != null)
        {
            return true;
        }

        if (!ContainerKinds.Contains(symbol.Kind))
            return false;

        if (IsFileScopedNamespace(symbol))
            return true;

        return symbol.BodyStartLine != null && symbol.BodyEndLine != null;
    }

    private static bool ContainsSymbol(
        SymbolRecord container,
        SymbolRecord candidate,
        string[]? rawLines = null,
        Func<CSharpLexState[]>? getCSharpLineStartStates = null)
    {
        if (IsFileScopedNamespace(container))
            return candidate.StartLine > container.StartLine;

        if (container.BodyStartLine == null || container.BodyEndLine == null)
            return false;

        if (candidate.StartLine == container.StartLine)
        {
            if (TryContainsCSharpSameLineSymbolByRawLine(container, candidate, rawLines, getCSharpLineStartStates, out var containsSameLineSymbol))
                return containsSameLineSymbol;

            return CanContainSameLineSymbol(container, candidate)
                && container.Signature != null
                && candidate.Signature != null
                && container.Signature.Contains(candidate.Signature, StringComparison.Ordinal);
        }

        if (candidate.StartLine >= container.BodyStartLine
            && candidate.StartLine <= container.BodyEndLine
            && candidate.StartLine > container.StartLine)
        {
            return true;
        }

        return IsInsideCSharpClosingBraceLineContainer(container, candidate, rawLines, getCSharpLineStartStates);
    }

    private static bool TryContainsCSharpSameLineSymbolByRawLine(
        SymbolRecord container,
        SymbolRecord candidate,
        string[]? rawLines,
        Func<CSharpLexState[]>? getCSharpLineStartStates,
        out bool contains)
    {
        contains = false;
        if (rawLines == null
            || container.Signature == null
            || candidate.Signature == null
            || container.StartLine != candidate.StartLine
            || container.StartLine <= 0
            || container.StartLine > rawLines.Length
            || !CanContainSameLineSymbol(container, candidate))
        {
            return false;
        }

        var lineIndex = container.StartLine - 1;
        var csharpLineStartStates = getCSharpLineStartStates?.Invoke();
        if (csharpLineStartStates == null || container.StartLine > csharpLineStartStates.Length)
            return false;

        var rawLine = rawLines[lineIndex];
        var lineStartState = csharpLineStartStates[lineIndex];
        var containerStartColumn = FindSignatureOccurrenceStartColumn(
            rawLine,
            container.Signature,
            container.SameLineSignatureOccurrenceIndex ?? 0,
            lineStartState);
        var candidateStartColumn = FindSignatureOccurrenceStartColumn(
            rawLine,
            candidate.Signature,
            candidate.SameLineSignatureOccurrenceIndex ?? 0,
            lineStartState);
        if (containerStartColumn < 0 || candidateStartColumn < 0)
            return false;

        if (container.BodyStartLine == container.StartLine
            && container.EndLine == container.StartLine)
        {
            var closingBraceColumn = FindCSharpSameLineContainerClosingBraceColumn(rawLine, containerStartColumn, lineStartState);
            if (closingBraceColumn < 0)
                return false;

            contains = candidateStartColumn > containerStartColumn
                && candidateStartColumn < closingBraceColumn;
            return true;
        }

        return false;
    }

    // A wrapped C# type can deliberately end its body one line earlier when the closing
    // brace line also starts an outer sibling (`} public int Q { get; }`). That keeps the
    // later outer sibling out of the inner container, but the last inner member may still
    // live earlier on that same closing-brace line (`public int P { get; } } public int Q`).
    // Reconstruct the matching closing-brace column on the raw end line and treat only the
    // declarations that start before that brace as inner members. Closes #549.
    // wrapped な C# type は、閉じ brace 行に outer sibling (`} public int Q { get; }`)
    // が続くとき、本体終端を 1 行手前へ倒して後続 sibling を inner container から外す。
    // ただし最後の inner member 自体が同じ閉じ brace 行の前半に載ることがあり
    // (`public int P { get; } } public int Q`)、そのままだと inner member まで外へ漏れる。
    // そこで raw end line 上で対応する closing brace 列を再構築し、その brace より前に
    // 始まる宣言だけを inner member として扱う。Closes #549.
    private static bool IsInsideCSharpClosingBraceLineContainer(
        SymbolRecord container,
        SymbolRecord candidate,
        string[]? rawLines,
        Func<CSharpLexState[]>? getCSharpLineStartStates)
    {
        if (rawLines == null
            || container.BodyStartLine == null
            || container.BodyEndLine == null
            || container.BodyEndLine.Value >= container.EndLine
            || candidate.Signature == null
            || candidate.StartLine != container.EndLine
            || candidate.StartLine <= container.StartLine)
        {
            return false;
        }

        var lineIndex = container.EndLine - 1;
        if (lineIndex < 0 || lineIndex >= rawLines.Length)
            return false;

        var closingBraceColumn = FindCSharpClosingBraceColumnOnContainerEndLine(container, rawLines);
        if (closingBraceColumn < 0)
            return false;

        var candidateColumn = FindSignatureOccurrenceStartColumn(
            rawLines[lineIndex],
            candidate.Signature,
            candidate.SameLineSignatureOccurrenceIndex ?? 0,
            getCSharpLineStartStates?.Invoke() is { } csharpLineStartStates
            && lineIndex < csharpLineStartStates.Length
                ? csharpLineStartStates[lineIndex]
                : new CSharpLexState());
        return candidateColumn >= 0 && candidateColumn < closingBraceColumn;
    }

    private static int FindCSharpClosingBraceColumnOnContainerEndLine(SymbolRecord container, string[] rawLines)
    {
        if (container.BodyStartLine == null
            || container.EndLine <= 0
            || container.EndLine > rawLines.Length
            || container.BodyStartLine.Value <= 0
            || container.BodyStartLine.Value > container.EndLine)
        {
            return -1;
        }

        var lexState = new CSharpLexState();
        var depth = 0;
        var endLineIndex = container.EndLine - 1;
        for (var lineIndex = container.BodyStartLine.Value - 1; lineIndex < endLineIndex; lineIndex++)
        {
            var lineResult = LexCSharpLine(rawLines[lineIndex], lexState);
            lexState = lineResult.EndState;

            foreach (var ch in lineResult.SanitizedLine)
            {
                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                }
            }
        }

        var sanitizedLine = LexCSharpLine(rawLines[endLineIndex], lexState).SanitizedLine;
        if (depth <= 0)
            return -1;

        for (var i = 0; i < sanitizedLine.Length; i++)
        {
            var ch = sanitizedLine[i];
            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int FindSignatureOccurrenceStartColumn(
        string rawLine,
        string signature,
        int occurrenceIndex,
        CSharpLexState lineStartState)
    {
        if (occurrenceIndex < 0 || string.IsNullOrEmpty(rawLine) || string.IsNullOrEmpty(signature))
            return -1;

        // Same-line C# occurrence tracking must ignore declaration lookalikes inside string
        // literals and comments, or the nth "real" declaration is mapped onto an earlier
        // quoted/commented copy of the same signature. LexCSharpLine preserves original
        // columns while blanking those regions, so the resulting indices still line up with
        // the raw line. Closes #558.
        // same-line C# の occurrence tracking は、文字列リテラルやコメント中の見かけ上の
        // 宣言を数えてはいけない。そうしないと n 個目の「本物の」宣言が、より前にある
        // quoted/commented な同一 signature へ誤対応付けされる。LexCSharpLine は元の列を
        // 保ったまま当該領域だけ空白化するので、得られる index は raw line と整合したまま使える。
        var searchLine = LexCSharpLine(rawLine, lineStartState).SanitizedLine;
        var currentOccurrence = 0;
        var searchStart = 0;
        while (searchStart < searchLine.Length)
        {
            var matchIndex = searchLine.IndexOf(signature, searchStart, StringComparison.Ordinal);
            if (matchIndex < 0)
                return -1;

            if (currentOccurrence == occurrenceIndex)
                return matchIndex;

            currentOccurrence++;
            searchStart = matchIndex + signature.Length;
        }

        return -1;
    }

    private static int FindCSharpSameLineContainerClosingBraceColumn(
        string rawLine,
        int containerStartColumn,
        CSharpLexState lineStartState)
    {
        if (containerStartColumn < 0 || containerStartColumn >= rawLine.Length)
            return -1;

        var sanitizedLine = LexCSharpLine(rawLine, lineStartState).SanitizedLine;
        var openBraceColumn = sanitizedLine.IndexOf('{', containerStartColumn);
        if (openBraceColumn < 0)
            return -1;

        var depth = 0;
        for (var i = openBraceColumn; i < sanitizedLine.Length; i++)
        {
            var ch = sanitizedLine[i];
            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static bool CanContainSameLineSymbol(SymbolRecord container, SymbolRecord candidate)
    {
        return (container.Kind, candidate.Kind) switch
        {
            ("function", _) when container.ContainerKind == "enum" && container.BodyStartLine != null && container.BodyEndLine != null => true,
            ("enum", "enum") => true,
            ("namespace", _) => true,
            ("class", _) => true,
            ("struct", _) => true,
            ("interface", _) => true,
            ("protocol", _) => true,
            _ => false,
        };
    }

    // C# file-scoped namespace: `namespace X;` with no braces. Matches only declarations whose
    // signature starts with the `namespace` keyword, so body-less namespace rows from other
    // languages (e.g. SQL `CREATE SCHEMA ...;` / `ALTER SCHEMA ...;`) are not treated as
    // file-scoped and therefore do not wrap every subsequent top-level symbol as their container.
    // C# の file-scoped namespace（`namespace X;` 形）だけを対象とする。`namespace` キーワードで
    // 始まるシグネチャに限定することで、SQL の `CREATE SCHEMA ...;` / `ALTER SCHEMA ...;` のような
    // 他言語の body 無し namespace 行が file-scoped namespace 扱いになり、以降のトップレベル
    // シンボル全てを自分の配下にぶら下げてしまう事故を防ぐ。
    private static bool IsFileScopedNamespace(SymbolRecord symbol)
    {
        if (symbol.Kind != "namespace")
            return false;
        if (symbol.BodyStartLine != null || symbol.BodyEndLine != null)
            return false;
        if (symbol.Signature == null)
            return false;
        var trimmed = symbol.Signature.AsSpan().TrimStart();
        return trimmed.StartsWith("namespace ", StringComparison.Ordinal)
            || trimmed.StartsWith("namespace\t", StringComparison.Ordinal);
    }

    private static int CountIndent(string line)
    {
        int indent = 0;
        foreach (var c in line)
        {
            if (c == ' ')
                indent++;
            else if (c == '\t')
                indent += 4;
            else
                break;
        }

        return indent;
    }

    private static bool StartsWithKeyword(string line, int startIndex, string keyword)
    {
        if (startIndex < 0 || startIndex + keyword.Length > line.Length)
            return false;

        if (string.CompareOrdinal(line, startIndex, keyword, 0, keyword.Length) != 0)
            return false;

        var nextIndex = startIndex + keyword.Length;
        return nextIndex >= line.Length || char.IsWhiteSpace(line[nextIndex]);
    }

    private static string? TryGetGroup(Match match, string? groupName)
    {
        if (groupName == null || !match.Groups[groupName].Success)
            return null;

        return NormalizeMetadata(match.Groups[groupName].Value);
    }

    private static string? NormalizeMetadata(string? value)
    {
        if (value is null)
            return null;

        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return null;

        return trimmed.Length == value.Length ? value : trimmed.ToString();
    }

    private static string NormalizeExtractedSymbolName(string? lang, string name, Match match, string matchLine)
    {
        return lang switch
        {
            "csharp" => CSharpSymbolNameNormalizer.Normalize(name, match, matchLine),
            "cobol" => CobolSymbolNameNormalizer.Normalize(name),
            "fsharp" => FSharpSymbolNameNormalizer.Normalize(name),
            "java" => JavaSymbolNameNormalizer.Normalize(name),
            "kotlin" => KotlinSymbolNameNormalizer.Normalize(name, matchLine),
            "ruby" => NormalizeRubySymbolName(name, matchLine),
            "rust" => RustSymbolNameNormalizer.Normalize(name),
            "smalltalk" => NormalizeSmalltalkSelectorName(name),
            "swift" => SwiftSymbolNameNormalizer.Normalize(name),
            "vb" => NormalizeVisualBasicSymbolName(name),
            "sql" => SqlSymbolNameNormalizer.Normalize(name),
            _ => name,
        };
    }

    private static string NormalizeVisualBasicSymbolName(string name)
    {
        var trimmed = name.Trim();
        if (TryValidateVisualBasicIdentifierSegments(trimmed, out var hasEscapedSegment))
            return hasEscapedSegment
                ? StripVisualBasicIdentifierEscapes(trimmed)
                : trimmed;

        return trimmed;
    }

    private static bool TryValidateVisualBasicIdentifierSegments(string name, out bool hasEscapedSegment)
    {
        hasEscapedSegment = false;
        var segmentStart = 0;
        for (var index = 0; index <= name.Length; index++)
        {
            if (index < name.Length && name[index] != '.')
                continue;

            var segment = name.AsSpan(segmentStart, index - segmentStart);
            if (!IsVisualBasicIdentifierSegment(segment))
                return false;

            hasEscapedSegment |= IsVisualBasicEscapedIdentifier(segment);
            segmentStart = index + 1;
        }

        return true;
    }

    private static bool IsVisualBasicIdentifierSegment(ReadOnlySpan<char> segment)
    {
        if (segment.Length == 0)
            return false;
        if (IsVisualBasicEscapedIdentifier(segment))
            return true;

        foreach (var ch in segment)
        {
            if (ch != '_' && !char.IsLetterOrDigit(ch))
                return false;
        }

        return true;
    }

    private static bool IsCppTemplateSpecializationSymbol(
        string kind,
        string name,
        string signature,
        IReadOnlyList<string> lines,
        int lineIndex)
    {
        if (kind is not ("class" or "struct" or "union" or "function"))
            return false;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(signature))
            return false;
        if (!signature.Contains(name + "<", StringComparison.Ordinal))
            return false;

        var trimmedSignature = signature.AsSpan().TrimStart();
        if (trimmedSignature.StartsWith("template", StringComparison.Ordinal)
            || trimmedSignature.StartsWith("export template", StringComparison.Ordinal))
        {
            return true;
        }

        for (var previousLineIndex = lineIndex - 1; previousLineIndex >= 0; previousLineIndex--)
        {
            var previous = lines[previousLineIndex].AsSpan().Trim();
            if (previous.IsEmpty)
                continue;
            return previous.StartsWith("template", StringComparison.Ordinal)
                || previous.StartsWith("export template", StringComparison.Ordinal);
        }

        return false;
    }

    private static readonly Regex RustAssociatedTypeDefaultRegex = new(
        @"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?type\s+(?<name>(?:r#)?\w+)(?:\s*<[^=;]+>)?(?:\s*:[^=;]+)?\s*=\s*(?<returnType>[^;]+)\s*;",
        RegexOptions.Compiled);

    private static void ExtractRustAssociatedTypeDefaultSymbols(long fileId, string[] lines, string[] structuralLines, List<SymbolRecord> symbols)
    {
        if (!LinesContain(lines, "type", StringComparison.Ordinal))
            return;

        var traits = BuildRustAssociatedTypeContainerSnapshot(symbols);
        if (traits.Count == 0)
            return;

        foreach (var trait in traits)
        {
            if (!TryFindRustBraceBodyBounds(structuralLines, trait.StartLine - 1, out var startLineIndex, out var endLineIndex))
                continue;

            var depth = 1;
            for (var lineIndex = startLineIndex + 1; lineIndex < endLineIndex; lineIndex++)
            {
                if (depth == 1)
                {
                    var match = RustAssociatedTypeDefaultRegex.Match(lines[lineIndex]);
                    if (match.Success)
                    {
                        var nameGroup = match.Groups["name"];
                        var name = RustSymbolNameNormalizer.Normalize(nameGroup.Value);
                        var lineNumber = lineIndex + 1;
                        symbols.Add(new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "property",
                            Name = name,
                            Line = lineNumber,
                            StartLine = lineNumber,
                            StartColumn = nameGroup.Index,
                            EndLine = lineNumber,
                            Signature = lines[lineIndex].Trim(),
                            ContainerKind = trait.Kind,
                            ContainerName = trait.Name,
                            ContainerQualifiedName = trait.ContainerQualifiedName,
                            Visibility = match.Groups["visibility"].Success ? match.Groups["visibility"].Value : null,
                            ReturnType = match.Groups["returnType"].ValueSpan.Trim().ToString(),
                        });
                    }
                }

                depth = Math.Max(1, depth + CountBraceDelta(structuralLines[lineIndex]));
            }
        }
    }

    private static IReadOnlyList<SymbolRecord> BuildRustAssociatedTypeContainerSnapshot(IReadOnlyList<SymbolRecord> symbols)
    {
        List<(SymbolRecord Symbol, int OriginalIndex)>? candidates = null;
        for (var index = 0; index < symbols.Count; index++)
        {
            var symbol = symbols[index];
            if (symbol.Kind is ("interface" or "protocol")
                && symbol.BodyStartLine is > 0
                && symbol.BodyEndLine is > 0)
            {
                (candidates ??= []).Add((symbol, index));
            }
        }

        if (candidates is null)
            return Array.Empty<SymbolRecord>();

        if (candidates.Count == 1)
            return [candidates[0].Symbol];

        candidates.Sort(static (left, right) =>
        {
            var comparison = left.Symbol.StartLine.CompareTo(right.Symbol.StartLine);
            return comparison != 0
                ? comparison
                : left.OriginalIndex.CompareTo(right.OriginalIndex);
        });

        var snapshot = new SymbolRecord[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            snapshot[i] = candidates[i].Symbol;
        return snapshot;
    }

    private static bool TryFindRustBraceBodyBounds(string[] structuralLines, int startLineIndex, out int bodyStartLineIndex, out int bodyEndLineIndex)
    {
        bodyStartLineIndex = 0;
        bodyEndLineIndex = 0;
        if (startLineIndex < 0 || startLineIndex >= structuralLines.Length)
            return false;

        var depth = 0;
        var opened = false;
        for (var lineIndex = startLineIndex; lineIndex < structuralLines.Length; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            if (!opened)
            {
                var openColumn = line.IndexOf('{');
                if (openColumn < 0)
                    continue;

                opened = true;
                bodyStartLineIndex = lineIndex;
                depth = 1 + CountBraceDelta(line[(openColumn + 1)..]);
            }
            else
            {
                depth += CountBraceDelta(line);
            }

            if (opened && depth == 0)
            {
                bodyEndLineIndex = lineIndex;
                return true;
            }
        }

        return false;
    }

    private static int CountBraceDelta(string line)
    {
        var delta = 0;
        var inDoubleQuote = false;
        var escapeNext = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inDoubleQuote && line[index] == '\\')
            {
                escapeNext = true;
                continue;
            }

            if (line[index] == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (inDoubleQuote)
                continue;

            if (index + 1 < line.Length && line[index] == '/' && line[index + 1] == '/')
                break;

            if (line[index] == '{')
                delta++;
            else if (line[index] == '}')
                delta--;
        }

        return delta;
    }

    private static bool IsVisualBasicEscapedIdentifier(ReadOnlySpan<char> segment)
        => segment.Length >= 2 && segment[0] == '[' && segment[^1] == ']';

    private static string StripVisualBasicIdentifierEscapes(string name)
    {
        var builder = new StringBuilder(name.Length);
        var segmentStart = 0;
        for (var index = 0; index <= name.Length; index++)
        {
            if (index < name.Length && name[index] != '.')
                continue;

            if (segmentStart > 0)
                builder.Append('.');

            var segment = name.AsSpan(segmentStart, index - segmentStart);
            builder.Append(IsVisualBasicEscapedIdentifier(segment)
                ? segment[1..^1]
                : segment);
            segmentStart = index + 1;
        }

        return builder.ToString();
    }

    private static string NormalizeRubySymbolName(string name, string matchLine)
    {
        if (!matchLine.AsSpan().TrimStart().StartsWith("require", StringComparison.Ordinal))
            return name;

        var trimmed = name.AsSpan().Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '\'' && trimmed[^1] == '\'')
                || (trimmed[0] == '"' && trimmed[^1] == '"')))
        {
            return trimmed[1..^1].ToString();
        }

        return trimmed.Length == name.Length ? name : trimmed.ToString();
    }

    private static string NormalizeSmalltalkSelectorName(string name)
    {
        var trimmed = name.Trim();
        if (!trimmed.Contains(':'))
            return trimmed;

        var builder = new StringBuilder(trimmed.Length);
        var tokenStart = -1;
        for (var index = 0; index <= trimmed.Length; index++)
        {
            var atEnd = index == trimmed.Length;
            if (!atEnd && !char.IsWhiteSpace(trimmed[index]))
            {
                if (tokenStart < 0)
                    tokenStart = index;
                continue;
            }

            if (tokenStart < 0)
                continue;

            if (trimmed[index - 1] == ':')
                builder.Append(trimmed, tokenStart, index - tokenStart);

            tokenStart = -1;
        }

        return builder.Length == 0 ? trimmed : builder.ToString();
    }

    private static readonly Regex ComplexityRegex = new(
        @"\b(?:if|else\s+if|elif|elsif|elseif|case|catch|except|when|while|for|foreach|guard)\b|(?:\?\?|&&|\|\||[?:](?!=))",
        RegexOptions.Compiled);
    /// <summary>
    /// Estimate cyclomatic complexity of a code body using keyword counting.
    /// This is a heuristic — not a true control-flow-graph analysis.
    /// Baseline is 1 (a straight-line function has complexity 1).
    /// コードボディのサイクロマティック複雑度をキーワードカウントで推定する。
    /// 真の制御フローグラフ解析ではなくヒューリスティック。基準値は1（直線的関数の複雑度）。
    /// </summary>
    public static int EstimateComplexity(string bodyContent)
    {
        if (string.IsNullOrWhiteSpace(bodyContent))
            return 1;
        return 1 + ComplexityRegex.Matches(bodyContent).Count;
    }
}
