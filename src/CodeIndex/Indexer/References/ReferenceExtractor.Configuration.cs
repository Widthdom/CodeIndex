using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static readonly TimeSpan ExtractionRegexTimeout = TimeSpan.FromSeconds(2);
    internal const int MaxReferenceLookupSymbols = 50_000;
    internal const int MaxReferenceLookupLines = 20_000;
    internal const int MaxReferenceLookupNamesPerLine = 512;
    internal const int MaxReferenceContainerCandidates = 20_000;
    internal const int MaxSwiftPropertyDefinitionsPerLine = MaxReferenceLookupNamesPerLine;
    internal static readonly IReadOnlyList<string> ReferenceSafetyCapDiagnosticKinds =
    [
        "reference_all_definition_lookup_symbol_budget_exceeded",
        "reference_container_candidate_budget_exceeded",
        "reference_csharp_xml_doc_scope_candidate_budget_exceeded",
        "reference_definition_lookup_line_budget_exceeded",
        "reference_definition_lookup_line_name_budget_exceeded",
        "reference_definition_lookup_symbol_budget_exceeded",
        "reference_enclosing_type_candidate_budget_exceeded",
        "reference_scientific_native_dependency_name_budget_exceeded",
        ShaderReferenceExtractor.LineNameBudgetDiagnosticKind,
        ShaderReferenceExtractor.TrackedNameBudgetDiagnosticKind,
        "reference_swift_property_line_budget_exceeded",
        "reference_swift_property_line_name_budget_exceeded",
        "reference_swift_property_symbol_budget_exceeded",
    ];
    private static readonly HashSet<string> ReferenceSafetyCapDiagnosticKindSet =
        new(ReferenceSafetyCapDiagnosticKinds, StringComparer.Ordinal);
    private static readonly AsyncLocal<ReferenceExtractionSafetyLimits?> SafetyLimitsOverride = new();
    private const int ReferenceListInitialCapacityLineThreshold = 128;
    private const int ReferenceListInitialCapacityMax = 1024;
    private static readonly IReadOnlySet<string> EmptyDefinitionNameSet = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<int, HashSet<string>> EmptyDefinitionNamesByLine =
        new Dictionary<int, HashSet<string>>();
    private static readonly string[] AdditionalReferenceLanguages =
    [
        "vue",
        "svelte",
        "razor",
        "blazor",
        "cshtml",
    ];

    private static string[] SplitContentLines(string content) =>
        SourceLineSplitter.Split(content);

    internal static ReferenceExtractionSafetyLimits? SafetyLimitsForTesting
    {
        get => SafetyLimitsOverride.Value;
        set => SafetyLimitsOverride.Value = value;
    }

    public static ReferenceExtractionSafetyLimits GetSafetyLimits()
        => SafetyLimitsOverride.Value ?? new ReferenceExtractionSafetyLimits
        {
            MaxLookupSymbols = MaxReferenceLookupSymbols,
            MaxLookupLines = MaxReferenceLookupLines,
            MaxNamesPerLine = MaxReferenceLookupNamesPerLine,
            MaxContainerCandidates = MaxReferenceContainerCandidates,
        };

    internal static bool IsSafetyCapDiagnosticKind(string kind)
        => ReferenceSafetyCapDiagnosticKindSet.Contains(kind);

    // THREAD-SAFETY: Reference extraction is stateless per call. Shared Regex instances and
    // lookup tables are initialized once and then read concurrently; language-specific state
    // must be created per extraction call (for example via CreateState helpers) rather than
    // stored in mutable static fields.
    private static readonly HashSet<string> SharedIgnoredCallNames = new(StringComparer.Ordinal)
    {
        // Control flow / 制御フロー
        "if", "else", "for", "foreach", "while", "switch", "catch", "lock", "do", "try", "when",
        // Keywords that look like calls / 呼び出しに見えるキーワード
        "sizeof", "typeof", "return", "throw", "nameof", "await", "using", "new",
        // Type/member keywords / 型・メンバーキーワード
        "class", "struct", "record", "interface", "enum", "delegate", "event", "namespace",
        "def", "function", "func",
    };
    private static readonly HashSet<string> SharedIgnoredCallNamesCaseInsensitive = new(SharedIgnoredCallNames, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MethodGroupContextTargetIgnoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "for", "foreach", "while", "switch", "catch", "lock", "do", "try", "nameof",
        "typeof", "sizeof", "using", "return", "throw", "checked", "unchecked", "default", "stackalloc",
        "fixed", "await", "yield", "when",
    };

    private static readonly HashSet<string> TypeScriptTypeQueryContextTokens = new(StringComparer.Ordinal)
    {
        "extends",
        "implements",
        "satisfies",
        "as",
        "type",
    };

    private static readonly HashSet<string> TypeScriptTypeQueryDisqualifyingTokens = new(StringComparer.Ordinal)
    {
        "if",
        "else",
        "for",
        "foreach",
        "while",
        "switch",
        "case",
        "do",
        "try",
        "catch",
        "return",
        "throw",
        "new",
        "delete",
        "void",
        "await",
        "yield",
        "in",
        "instanceof",
        "=>",
        "?",
    };

    private static bool IsFunctionLikeSymbolKind(string kind)
        => kind is "function" or "operator" or "lambda" or "async_function" or "generator" or "async_generator";

    private static readonly Dictionary<string, HashSet<string>> LanguageSpecificIgnoredCallNames = new(StringComparer.Ordinal)
    {
        // C# contextual keywords and common false positives / C# 文脈キーワードとよくある偽陽性
        ["csharp"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "is", "as", "in", "var", "base", "this", "value", "get", "set", "init", "where",
            "from", "select", "orderby", "group", "into", "join", "let", "on", "equals",
            "async", "yield", "checked", "unchecked", "default", "stackalloc", "fixed",
        },
        // Java contextual keywords / Java 文脈キーワード
        // `this` is listed so generic CallRegex does not emit a phantom `call this` edge
        // after JavaReferenceExtractor rewrites the chain to the owning class.
        // `this` も含めることで、連鎖書き換え後の generic CallRegex が `call this` を二重に出すのを防ぐ。
        ["java"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "instanceof", "super", "this", "assert", "throws", "extends", "implements", "synchronized",
        },
        // Kotlin constructor delegation is rewritten by KotlinReferenceExtractor, so suppress the
        // declaration/delegation keywords that generic CallRegex would otherwise index as calls.
        // Kotlin の constructor 委譲は KotlinReferenceExtractor で書き換えるため、
        // 汎用 CallRegex が拾う宣言・委譲 keyword 自体は call として残さない。
        ["kotlin"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "constructor", "super", "this",
        },
        // Rust macro declaration keywords / Rust マクロ宣言キーワード
        // `macro_rules!` declarations will be seen by the Rust macro-call regex below, but they are
        // declaration sites rather than call sites, so suppress the keyword itself.
        // `macro_rules!` 宣言は下の Rust macro-call regex でも見えてしまうが、これは呼び出しではなく
        // 宣言なのでキーワード自体を抑止する。
        ["rust"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "macro_rules",
        },
        ["c"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "auto", "break", "case", "const", "continue", "default", "extern", "goto",
            "inline", "register", "restrict", "static", "switch", "typedef", "volatile",
        },
        ["cpp"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "alignas", "auto", "break", "case", "catch", "concept", "const", "constexpr",
            "consteval", "constinit", "continue", "co_await", "co_return", "co_yield",
            "decltype", "default", "delete", "explicit", "extern", "friend", "inline",
            "mutable", "noexcept", "operator", "override", "private", "protected", "public",
            "requires", "static", "template", "this", "typedef", "typename", "using", "virtual",
            "volatile",
        },
        // GPU-language metadata is declarative, even when its surface syntax uses parentheses.
        // GPU 言語のメタデータは括弧を使う構文でも宣言であり、呼び出しではない。
        ["cuda"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "alignas", "auto", "break", "case", "catch", "concept", "const", "constexpr",
            "consteval", "constinit", "continue", "co_await", "co_return", "co_yield",
            "decltype", "default", "delete", "explicit", "extern", "friend", "inline",
            "mutable", "noexcept", "operator", "override", "private", "protected", "public",
            "requires", "static", "template", "this", "typedef", "typename", "using", "virtual",
            "volatile",
            "__global__", "__device__", "__host__", "__shared__", "__constant__",
            "__launch_bounds__", "__align__", "__device_builtin__",
        },
        ["glsl"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "layout",
        },
        ["hlsl"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "register", "packoffset", "numthreads", "domain", "partitioning",
            "outputtopology", "outputcontrolpoints", "patchconstantfunc", "maxtessfactor",
        },
        ["metal"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "buffer", "texture", "sampler", "threadgroup", "stage_in",
            "thread_position_in_grid", "threads_per_threadgroup",
        },
        ["go"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "append", "cap", "close", "copy", "delete", "len", "make", "new", "panic", "recover",
            "chan", "defer", "fallthrough", "func", "go", "interface", "map", "package",
            "range", "select", "type", "var",
        },
        ["dart"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "assert", "async", "base", "const", "covariant", "deferred", "dynamic",
            "export", "extends", "extension", "external", "factory", "final", "hide", "implements",
            "import", "late", "library", "mixin", "on", "operator", "part", "required", "show",
            "typedef", "void", "with",
        },
        ["elixir"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "alias", "after", "behaviour", "case", "catch", "cond", "def", "defdelegate",
            "defguard", "defguardp", "defimpl", "defmacro", "defmacrop", "defmodule", "defp",
            "defprotocol", "defstruct", "do", "else", "end", "for", "fn", "if", "impl",
            "import", "quote", "receive", "require", "rescue", "try", "unless", "unquote",
            "use", "with",
        },
        ["lua"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if",
            "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until",
            "while",
        },
        // JavaScript / TypeScript contextual keywords / JavaScript / TypeScript 文脈キーワード
        ["javascript"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "import", "super", "yield",
        },
        ["typescript"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "import", "super", "yield",
        },
        // Python contextual keywords / Python の文脈キーワード
        ["python"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "raise", "yield", "from", "super",
        },
        // Ruby contextual keywords / Ruby の文脈キーワード
        ["ruby"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "raise", "yield", "super", "include", "extend", "prepend", "refine", "alias", "alias_method", "describe",
            "resource", "resources", "create_table", "attribute", "serialize",
            "private_constant", "public_constant", "module_function", "rescue_from", "gem", "composed_of",
            "accepts_nested_attributes_for",
            "unless", "case", "begin", "until", "module", "rescue", "ensure",
        },
        ["perl"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "use", "require", "package", "sub", "my", "our", "local", "state",
            "if", "elsif", "unless", "while", "until", "foreach", "for", "given", "when",
            "print", "say", "die", "warn", "open", "close", "defined", "exists", "delete",
            "bless", "ref", "scalar", "wantarray", "eval", "do",
        },
        ["ambiguous_pl"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "use", "require", "package", "sub", "my", "our", "local", "state",
            "if", "elsif", "unless", "while", "until", "foreach", "for", "given", "when",
            "print", "say", "die", "warn", "open", "close", "defined", "exists", "delete",
            "bless", "ref", "scalar", "wantarray", "eval", "do",
            "module", "use_module", "library", "initialization", "dynamic", "multifile",
            "discontiguous", "op",
        },
        ["crystal"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "alias", "annotation", "begin", "case", "class", "def", "do", "else",
            "elsif", "end", "ensure", "enum", "extend", "for", "fun", "if", "include", "lib",
            "macro", "module", "next", "of", "private", "protected", "require", "rescue",
            "return", "select", "struct", "then", "unless", "until", "when", "while", "with", "yield",
            "as", "alignof", "instance_alignof", "instance_sizeof", "is_a?", "offsetof", "pointerof",
            "responds_to?",
        },
        ["groovy"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "apply", "as", "assert", "break", "case", "catch", "class", "continue", "def", "do",
            "else", "enum", "extends", "finally", "for", "if", "implements", "import", "in",
            "instanceof", "interface", "new", "package", "return", "super", "switch", "synchronized",
            "this", "throw", "throws", "trait", "try", "while",
        },
        ["tcl"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "append", "array", "break", "catch", "concat", "continue", "dict", "error", "eval",
            "expr", "for", "foreach", "global", "if", "incr", "info", "lappend", "lindex",
            "list", "namespace", "oo::class", "package", "proc", "rename", "return", "set",
            "string", "switch", "unset", "upvar", "uplevel", "variable", "while",
        },
        ["prolog"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "module", "use_module", "library", "initialization", "dynamic", "multifile",
            "discontiguous", "op", "true", "fail", "false", "is", "not",
        },
        // F# contextual keywords / F# 文脈キーワード
        ["fsharp"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "match", "with", "member", "override", "abstract", "mutable", "rec", "fun", "open",
            "module", "type", "of", "then", "elif", "done", "begin", "end",
            "let", "use", "if", "else", "do", "try", "finally", "in", "for", "while", "return", "yield",
            "assert", "to", "downto", "lazy", "raise", "upcast", "downcast",
        },
        // PHP include/require constructs / PHP の include/require 構文
        ["php"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "require", "require_once", "include", "include_once",
            "echo", "print", "exit", "die", "eval", "unset", "isset", "empty",
        },
        // SQL keywords. Case-insensitive because SQL is written both upper- and lowercase in real code,
        // and the `EXEC|EXECUTE|CALL` extractor preserves the original casing of the captured name.
        // The entries themselves stay uppercase for readability.
        // SQL のキーワード。実コードでは大文字・小文字が混在するうえ、`EXEC|EXECUTE|CALL` 抽出が
        // 元のケースをそのまま保持するため、比較は大文字小文字非依存にする（リストは読みやすさのため大文字表記）。
        ["sql"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE", "JOIN", "INTO",
            "VALUES", "ORDER", "GROUP", "HAVING", "LIMIT", "OFFSET", "UNION",
            "EXISTS", "BETWEEN", "LIKE", "CASE", "WHEN", "THEN", "ELSE",
            "AS", "ON", "AND", "OR", "NOT", "NULL", "IN", "IS",
            "CREATE", "ALTER", "DROP", "TABLE", "INDEX", "VIEW", "IF",
            // `EXECUTE IMMEDIATE 'dynamic SQL'` (Oracle / PL/pgSQL) — `IMMEDIATE` is not a call target.
            // `EXECUTE IMMEDIATE '動的SQL'` (Oracle / PL/pgSQL) — `IMMEDIATE` は呼び出し対象ではない。
            "IMMEDIATE",
            // The keywords that introduce a stored-procedure call themselves. The no-parens form is
            // captured by SqlProcCallRegex; the rare `EXEC(@sql)` / `EXEC('...')` dynamic-SQL form has
            // no identifier argument, so the generic CallRegex would otherwise emit a phantom
            // `call EXEC` / `call EXECUTE` / `call CALL` edge pointing at the keyword itself.
            // ストアドプロシージャ呼び出しを導入するキーワード自身。括弧なし形は SqlProcCallRegex で捕捉し、
            // 動的 SQL 形の `EXEC(@sql)` / `EXEC('...')` は識別子を持たないため、汎用 CallRegex に任せると
            // キーワード自体を指す `call EXEC` / `call EXECUTE` / `call CALL` の幽霊エッジが生まれる。
            "EXEC", "EXECUTE", "CALL",
        },
        // R keywords / R キーワード
        ["r"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "library", "cat", "paste", "paste0", "sprintf", "stop", "warning", "message",
            "invisible", "tryCatch", "withCallingHandlers", "requireNamespace", "next", "break", "repeat",
            "import", "importFrom", "export", "exportClasses", "exportMethods", "S3method", "useDynLib",
        },
        // PowerShell keywords / PowerShell キーワード
        ["powershell"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "function", "filter", "configuration", "workflow", "class", "enum",
            "param", "begin", "process", "end", "dynamicparam",
            "if", "else", "elseif", "for", "foreach", "while", "do", "until", "switch",
            "try", "catch", "finally", "trap", "return", "throw", "break", "continue",
            "using", "data", "in", "Write",
        },
        // Shell keywords / Shell キーワード
        ["shell"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "then", "else", "elif", "fi", "do", "done", "while", "until", "case", "esac", "time",
        },
        // Haskell keywords / Haskell キーワード
        ["haskell"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "data", "newtype", "instance", "deriving", "infixl", "infixr", "infix",
            "qualified", "hiding", "forall", "Just", "Nothing", "Left", "Right", "True", "False",
            "case", "class", "default", "foreign", "import", "let", "module", "of", "type", "where",
            "putStrLn", "putStr", "print",
        },
        ["vb"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AddHandler", "AddressOf", "Alias", "And", "AndAlso", "As", "ByRef", "ByVal",
            "Call", "CallByName", "Case", "Catch", "CBool", "CByte", "CChar", "CDate", "CDbl", "CDec",
            "CInt", "CLng", "CObj", "CSByte", "CShort", "CSng", "CStr", "CType", "CUInt", "CULng", "CUShort",
            "DirectCast", "End", "Erase", "Exit", "Get", "GetType",
            "GetXMLNamespace", "Global", "Handles", "Inherits", "Implements", "Imports", "Me",
            "Module", "MustInherit", "MustOverride", "MyBase", "MyClass", "Namespace", "Narrowing",
            "NameOf", "New", "Next", "Not", "Nothing", "Of", "On", "Operator", "Option", "Or", "OrElse",
            "Overloads", "Overrides", "ParamArray", "Partial", "RaiseEvent", "ReadOnly",
            "RemoveHandler", "Resume", "Return", "Select", "Set", "Shadows", "Shared", "Static",
            "Step", "Stop", "SyncLock", "Then", "TryCast", "Using", "When", "Widening", "With",
            "WithEvents", "WriteOnly", "Xor",
        },
        ["fortran"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "allocatable", "allocate", "associate", "call", "case", "class", "contains", "cycle",
            "deallocate", "do", "elemental", "else", "elseif", "end", "entry", "equivalence",
            "exit", "function", "if", "implicit", "include", "intent", "interface", "intrinsic",
            "module", "namelist", "none", "only", "operator", "optional", "parameter", "pointer",
            "private", "procedure", "program", "public", "pure", "recursive", "result", "return",
            "select", "submodule", "subroutine", "then", "type", "use", "where",
        },
        ["pascal"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and", "array", "begin", "case", "class", "const", "constructor", "destructor", "div",
            "do", "downto", "else", "end", "except", "exports", "file", "finally", "for",
            "function", "goto", "if", "implementation", "in", "inherited", "interface", "is",
            "label", "mod", "nil", "not", "object", "of", "or", "packed", "private", "procedure",
            "program", "property", "protected", "public", "published", "raise", "record", "repeat",
            "set", "shl", "shr", "then", "threadvar", "to", "try", "type", "unit", "until",
            "uses", "var", "while", "with", "xor",
        },
        ["objc"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "BOOL", "Class", "YES", "NO", "Nil", "SEL", "alloc", "autorelease", "copy", "id",
            "init", "nonatomic", "nullable", "nonnull", "readwrite", "readonly", "retain",
            "self", "strong", "super", "weak",
        },
        ["smalltalk"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "false", "nil", "self", "super", "thisContext", "true",
        },
        ["ada"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "accept", "begin", "case", "declare", "delay", "else", "elsif", "end", "entry",
            "exception", "exit", "function", "generic", "if", "loop", "package", "pragma",
            "procedure", "raise", "record", "renames", "return", "select", "task", "terminate",
            "type", "use", "when", "while", "with",
        },
        ["cython"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "cdef", "cpdef", "ctypedef", "cimport", "def", "extern", "gil", "include",
            "nogil", "property",
        },
        ["d"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "__traits", "assert", "cast", "debug", "extern", "is", "mixin", "pragma", "scope",
            "static", "unittest", "version",
        },
        ["julia"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "baremodule", "begin", "do", "export", "finally", "function", "import",
            "let", "macro", "module", "mutable", "primitive", "quote", "struct", "using", "where",
        },
        ["matlab"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "arguments", "case", "catch", "classdef", "elseif", "end", "function", "import",
            "methods", "otherwise", "parfor", "properties", "spmd",
        },
        ["nim"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "block", "case", "concept", "converter", "defer", "discard", "distinct", "from",
            "func", "import", "include", "iterator", "macro", "method", "mixin", "object",
            "proc", "template", "type", "when",
        },
        // Gradle/Groovy keywords / Gradle/Groovy キーワード
        ["gradle"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "apply", "plugins", "dependencies", "repositories", "allprojects", "subprojects",
            "task", "buildscript", "ext", "group", "version", "description",
        },
        // Terraform keywords / Terraform キーワード
        ["terraform"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "resource", "data", "variable", "output", "locals", "module", "provider",
            "terraform", "required_providers", "backend",
        },
        // Makefile keywords / Makefile キーワード
        ["makefile"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "all", "clean", "install", "build", "run", "help",
        },
        // Sass/Stylus accept CSS function syntax without separators; keep common CSS built-ins
        // from flowing through the shared CallRegex after the language-specific extractors skip them.
        // Sass/Stylus は CSS 関数構文を区切りなしで受け付けるため、言語専用 extractor で除外した
        // 代表的な CSS built-in を共有 CallRegex 側でも call として残さない。
        ["sass"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "url", "var", "calc", "rgb", "rgba", "hsl", "hsla",
        },
        ["stylus"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "url", "var", "calc", "rgb", "rgba", "hsl", "hsla",
        },
    };
    private static readonly Dictionary<string, HashSet<string>> LanguageSpecificCallNameKeeps = new(StringComparer.Ordinal)
    {
        // Rust uses `new` / `default` as ordinary method names (`Type::new`, `Default::default`).
        // Rust では `new` / `default` は通常のメソッド名 (`Type::new`, `Default::default`)。
        ["rust"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "new", "default",
        },
    };

    // JavaScript / TypeScript tokens that legally sit immediately before a template literal
    // without being a tag identifier: unary / binary operators (`void \`...\``,
    // `delete \`...\``, `foo in \`...\``, `foo instanceof \`...\``), switch-case label
    // (`case \`...\`:`), and clause / statement keywords (`export default \`...\``,
    // `try {} finally \`...\``). Without this gate the tagged-template scanner (issue #268)
    // emits phantom call rows for those keywords. This set is intentionally applied ONLY at
    // the tagged-template emit site, not to the shared `CallRegex` path, so legitimate
    // member calls like `api.in()` / `api.instanceof()` / `api.delete()` / `api.case()` /
    // `api.void()` / `promise.finally()` remain captured. The denylist is also bypassed
    // when the hit's `IsMemberAccess` flag is set — `obj.default\`x\`` and
    // `obj.finally\`y\`` are legal tagged-template calls because every reserved word is a
    // legal property name in JS/TS, and the masker's member-access detection reports those
    // hits separately from bare-keyword hits. `of` is intentionally NOT listed because it
    // is an unreserved identifier — `const of = ...; of\`x\`` is a legal tagged-template
    // call. The narrower `for (...of \`...\`)` header suppression lives in
    // `StructuralLineMasker.FilterJsForOfHeaderHits`.
    // JS/TS でタグ無しテンプレート直前に現れてタグではないトークン: 単項/二項演算子
    // (`void \`...\`` / `delete \`...\`` / `foo in \`...\`` / `foo instanceof \`...\``)、
    // switch-case ラベル (`case \`...\`:`)、clause/statement キーワード
    // (`export default \`...\`` / `try {} finally \`...\``)。汎用 CallRegex には適用せず
    // タグ付きテンプレート発行時だけに限定するため、`api.in()` / `api.instanceof()` /
    // `api.delete()` / `api.case()` / `api.void()` / `promise.finally()` のような正当な
    // メンバー呼び出しは引き続き捕捉される。さらに hit の `IsMemberAccess` が立って
    // いる場合もこの denylist を迂回する — JS/TS ではすべての予約語が property 名に
    // なれるため `obj.default\`x\`` や `obj.finally\`y\`` は正当なタグ呼び出しで、
    // masker 側でメンバーアクセス判定が済んでいる。`of` は予約語ではなく
    // `const of = ...; of\`x\`` が正当なタグ呼び出しになりうるためここには含めない。
    // `for (...of \`...\`)` ヘッダの抑止は
    // `StructuralLineMasker.FilterJsForOfHeaderHits` 側で扱う。
    private static readonly HashSet<string> JsTaggedTemplateOperatorNames = new(StringComparer.Ordinal)
    {
        "void", "case", "delete", "in", "instanceof", "default", "finally",
    };

}
