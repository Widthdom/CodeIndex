using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Extracts lightweight symbol references such as call sites.
/// 軽量なシンボル参照（呼び出し箇所など）を抽出する。
/// </summary>
public static partial class ReferenceExtractor
{
    private static readonly TimeSpan ExtractionRegexTimeout = TimeSpan.FromSeconds(2);
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

    private static readonly Regex StringLiteralRegex = new(
        "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|`(?:\\\\.|[^`\\\\])*`",
        RegexOptions.Compiled);
    private static readonly Regex NonBacktickStringLiteralRegex = new(
        "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'",
        RegexOptions.Compiled);
    private static readonly Regex InlineBlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Compiled);
    internal const string CSharpIdentifierPattern = @"@?[_\p{L}]\w*";
    private const string FunctionalIdentifierPattern = @"@?[_\p{L}\$][\w$]*";
    private const string CSharpTypeExpressionPattern =
        @"(?:global::)?(?:"
        + CSharpIdentifierPattern
        + @"\s*(?:(?:\.|::)\s*"
        + CSharpIdentifierPattern
        + @")*)(?:\s*<[^)\];{}]+>)?(?:\s*\[[^\]\n]*\])*";
    private static readonly Regex CSharpLocalDeclarationRegex = new(
        $@"(?<![\w@])(?:var|{CSharpTypeExpressionPattern})\s+(?<name>{CSharpIdentifierPattern})\s*(?=[=;,\)])",
        RegexOptions.Compiled);
    private static readonly Regex CSharpLambdaRegex = new(
        $@"(?<params>\([^)]*\)|{CSharpIdentifierPattern})\s*=>\s*(?<body>.*)$",
        RegexOptions.Compiled);
    // The `(?:\?\.)?` segment captures JavaScript / TypeScript optional chaining calls such as
    // `callback?.()` and `callback?.<T>()`. Without it the `?.` stops the regex from reaching the
    // trailing `(`, and the call reference to `callback` is silently dropped. Other supported
    // languages that use `?.` (C# / Kotlin / Swift / Dart) place an identifier between `?.` and
    // `(`, so their existing call sites continue to match via the identifier itself. See issue #294.
    // `(?:\?\.)?` は JavaScript / TypeScript の optional chaining 呼び出し (`callback?.()` や
    // `callback?.<T>()`) を捕捉するための segment。これが無いと `?.` の存在で末尾 `(` に到達できず、
    // `callback` への call 参照が黙って欠落する。C# / Kotlin / Swift / Dart などの `?.` は後ろに
    // 識別子が続くため、従来通り識別子自身が CallRegex にマッチして影響を受けない。issue #294 参照。
    // Nested generic call sites such as `Foo<Bar<int>>()` / `new Dict<K, List<V>>()` are
    // recovered by a depth-aware fallback scanner because the flat `<[^>\n]+>` segment cannot
    // balance the closing `>>`. See issue #263.
    // `Foo<Bar<int>>()` や `new Dict<K, List<V>>()` のようなネスト generic 呼び出しは、
    // 平坦な `<[^>\n]+>` では末尾 `>>` を釣り合わせられないため、depth-aware な fallback scanner
    // で補完する。issue #263 参照。
    private static readonly Regex CallRegex = new($@"(?<![\w$])(?<name>{CSharpIdentifierPattern})(?:\?\.)?(?:::)?(?:<[^>\n]+>)?\s*\(", RegexOptions.Compiled);
    // Method-group / method-reference handoffs do not have a trailing `(`, so the shared
    // CallRegex cannot see them. C# / JS / TS use a context gate plus a callable-name allowlist,
    // while Java / Kotlin / Scala use the unique `::` sigil.
    // `(` を持たない method-group / method-reference handoff は共通 CallRegex では拾えないため、
    // C# / JS / TS は文脈ゲート＋ callable-name allowlist、Java / Kotlin / Scala は `::` sigil で拾う。
    private static readonly Regex MethodGroupReferenceRegex = new(
        $@"(?<![\w$])(?:(?:[=,]\s*|return\s+|=>\s+|(?<contextTarget>{FunctionalIdentifierPattern})(?:<[^>\n]+>)?\s*\(\s*))(?:(?:this|base|{FunctionalIdentifierPattern}(?:\.{FunctionalIdentifierPattern})*)\s*\.\s*)?(?<name>{FunctionalIdentifierPattern})(?!\s*\()(?!\s*`)(?=\s*(?:[;,)\]]|$))",
        RegexOptions.Compiled);
    // JSX / TSX component element open tags. Capitalized tag names are treated as component
    // call sites, while lowercase intrinsic HTML tags stay excluded by design.
    // JSX / TSX の component open tag。大文字始まりの tag 名だけを component 呼び出しとして扱い、
    // 小文字始まりの intrinsic HTML tag は意図的に除外する。
    private static readonly Regex JsxElementOpenRegex = new(
        @"<(?<name>[A-Z][\w$]*(?:\.[A-Za-z_$][\w$]*)*)",
        RegexOptions.Compiled);
    // SQL stored-procedure call without parentheses: T-SQL `EXEC` / `EXECUTE` and MySQL / MariaDB `CALL`.
    // The shared CallRegex requires a trailing `(`, which misses the dominant real-world form such as
    // `EXEC dbo.sp_Target;`, `EXEC dbo.sp_Target @x = 1, @y = 2;`, `CALL sp_Helper;`, and the bracketed
    // form `EXEC [dbo].[sp_Target]`. The regex captures only the final identifier (schema prefixes are
    // consumed as a prefix) and tolerates the optional T-SQL return-value assignment
    // `EXEC @retval = dbo.sp_Target ...`. Bracket handling is done at emission time so `[sp_Target]`
    // is normalized back to `sp_Target`. See issue #232.
    // SQL のストアドプロシージャを `(` なしで呼び出す T-SQL `EXEC` / `EXECUTE` と MySQL / MariaDB `CALL`。
    // 共通 CallRegex は末尾 `(` を要求するため、`EXEC dbo.sp_Target;` など実運用で圧倒的に多い形を取りこぼす。
    // 先頭側の schema prefix は吸収し、末端の識別子だけを `name` として捕捉する。T-SQL 固有の
    // `EXEC @retval = dbo.sp_Target ...` 形にも対応し、`[sp_Target]` のような角括弧識別子は発行時に除去する。
    // Bracketed identifiers inside the qualifier and name groups accept any character except `[`,
    // `]`, or a line terminator. T-SQL allows `#` (temp procedure), `-` (hyphenated names),
    // spaces, Unicode symbols, and punctuation inside bracket quoting, and the narrower `[\w ]+`
    // would silently drop `EXEC [#tempProc]`, `EXEC [dbo].[proc-name]`, and similar legitimate
    // forms while falsely misattributing the qualifier `[dbo]` as the proc name.
    // Qualifier segments are optional (the inner `?`) so SQL Server's linked-server form with
    // an omitted database or schema part — `EXEC AdventureWorks..sp_GetCustomer;` /
    // `EXEC [AdventureWorks]..[proc-name];` — terminates on the real procedure name instead of
    // falling back to the first segment. Identifier alternatives also accept backtick-quoted
    // C# event subscription/unsubscription: Click += OnClick — both LHS and RHS must be PascalCase identifiers
    // C# イベント購読・解除: Click += OnClick — LHS と RHS の両方が PascalCase 識別子のみ
    private static readonly Regex EventSubscriptionRegex = new(@"(?<name>[A-Z]\w*)\s*[+-]=\s*(?:new\s+)?[A-Z]\w*", RegexOptions.Compiled);
    // C# / Java parenless object / collection / dictionary / array initializer such as
    // `new Foo { X = 1 }`, `new List<int> { 1, 2, 3 }`, `new Dictionary<K, V> { [k] = v }`,
    // `new Foo[] { ... }`, `new Foo[N] { ... }`, `new Foo[,] { ... }`, `new Foo[][] { ... }`,
    // and qualified type names like `new N.Foo { X = 1 }` / `new global::N.Foo { X = 1 }`.
    // CallRegex requires a trailing `(`, so these forms are otherwise dropped from the
    // reference table even though the type is genuinely instantiated. Anonymous types
    // (`new { Name = ... }`), target-typed `new()`, and collection expressions (`new[] { ... }`)
    // intentionally do not match because they have no named target. Nested generics deeper than
    // one `<...>` level (e.g. `new Dictionary<string, List<int>> { ... }`) follow the same
    // limitation as the existing CallRegex generics handling. See issue #286.
    // C# / Java の括弧省略インスタンス化（オブジェクト / コレクション / ディクショナリ /
    // 配列イニシャライザ）。CallRegex は `(` が必須なため取りこぼすが、実体は型のインスタンス化なので
    // `instantiate` として拾う。匿名型 `new { ... }`、target-typed `new()`、
    // collection expression `new[] { ... }` は対象を持たないため意図的にマッチさせない。
    // 1 段を超えるネストした generic（`Dictionary<string, List<int>>` 等）は既存 CallRegex と同様の制限。issue #286 参照。
    private static readonly Regex CSharpJavaInitializerRegex = new(
        $@"\bnew\s+(?:global::)?(?:{CSharpIdentifierPattern}(?:\s*::\s*|\s*\.\s*))*(?<name>{CSharpIdentifierPattern})(?:\s*<[^>\n]+>)?(?:\s*\[[^\[\]\n]*\])*\s*\{{",
        RegexOptions.Compiled);
    // Allman-style C# / Java parenless initializer where `{` sits on the next non-empty
    // line. The trailing regex captures `new <Type>` ending the current line (with optional
    // generic + array shape), and the caller peeks forward to confirm the next non-blank
    // prepared line begins with `{` before emitting an `instantiate` edge. See issue #286.
    // Allman スタイルの多行 parenless initializer。`new <Type>` が行末で終わり、次の非空 prepared line が
    // `{` から始まる場合にだけ `instantiate` を発行する。issue #286 参照。
    private static readonly Regex CSharpJavaInitializerTrailingRegex = new(
        $@"\bnew\s+(?:global::)?(?:{CSharpIdentifierPattern}(?:\s*::\s*|\s*\.\s*))*(?<name>{CSharpIdentifierPattern})(?:\s*<[^>\n]+>)?(?:\s*\[[^\[\]\n]*\])*\s*$",
        RegexOptions.Compiled);
    private static readonly Regex CSharpUsingAliasRegex = new(
        @"^\s*(?:global\s+)?using\s+(?!static\b)(?<alias>@?[A-Za-z_]\w*)\s*=\s*(?<target>[^;]+)",
        RegexOptions.Compiled);
    private static readonly Regex CSharpUsingStaticRegex = new(
        @"^\s*(?:global\s+)?using\s+static\s+(?<target>[^;]+)",
        RegexOptions.Compiled);
    private static readonly Regex CSharpUsingNamespaceRegex = new(
        @"^\s*(?:global\s+)?using\s+(?!static\b)(?<target>[^;=]+)",
        RegexOptions.Compiled);
    private static readonly Regex CSharpLocalValueNameRegex = new(
        @"(?:^\s*|[;{}]\s*)(?:(?:(?:await\s+)?using\s+var)|var|(?:(?:const\s+)?[A-Za-z_]\w*(?:\s*::\s*|\s*\.\s*)*[A-Za-z_]\w*(?:\s*<[^>\n]+>)?(?:\s*\?)?(?:\s*\[\s*\])*))\s+(?<name>@?[A-Za-z_]\w*)\s*(?==|;|,)",
        RegexOptions.Compiled);
    private static readonly Regex CSharpForeachValueNameRegex = new(
        @"\bforeach\s*\(\s*(?:var|(?:[A-Za-z_]\w*(?:\s*::\s*|\s*\.\s*)*[A-Za-z_]\w*(?:\s*<[^>\n]+>)?(?:\s*\?)?(?:\s*\[\s*\])*))\s+(?<name>@?[A-Za-z_]\w*)\s+in\b",
        RegexOptions.Compiled);
    private static readonly Regex CSharpQueryRangeValueNameRegex = new(
        @"\b(?:from|join)\s+(?<name>@?[A-Za-z_]\w*)\s+in\b|\blet\s+(?<name>@?[A-Za-z_]\w*)\s*=|\binto\s+(?<name>@?[A-Za-z_]\w*)\b",
        RegexOptions.Compiled);
    private const string CSharpDeclarationPatternTypeRegex = @"(?:var|(?:[A-Za-z_]\w*(?:\s*::\s*|\s*\.\s*)*[A-Za-z_]\w*(?:\s*<[^>\n]+>)?(?:\s*\?)?(?:\s*\[\s*\])*))";
    private const string CSharpRecursivePatternClauseRegex = @"(?:\s*\{[^\n]*\})?";
    private static readonly Regex CSharpDeclarationPatternValueNameRegex = new(
        @"\bis\s+" + CSharpDeclarationPatternTypeRegex + CSharpRecursivePatternClauseRegex + @"\s+(?<name>@?[A-Za-z_]\w*)\b",
        RegexOptions.Compiled);
    private static readonly Regex CSharpSwitchExpressionDeclarationPatternValueNameRegex = new(
        @"^\s*(?<type>" + CSharpDeclarationPatternTypeRegex + @")\s+(?<name>@?[A-Za-z_]\w*)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex CSharpCaseDeclarationPatternValueNameRegex = new(
        @"\bcase\s+" + CSharpDeclarationPatternTypeRegex + CSharpRecursivePatternClauseRegex + @"\s+(?<name>@?[A-Za-z_]\w*)\b(?=\s*(?::|\bwhen\b))",
        RegexOptions.Compiled);
    private static readonly Regex CSharpOutValueNameRegex = new(
        @"\bout\s+(?:var|(?:[A-Za-z_]\w*(?:\s*::\s*|\s*\.\s*)*[A-Za-z_]\w*(?:\s*<[^>\n]+>)?(?:\s*\?)?(?:\s*\[\s*\])*))\s+(?<name>@?[A-Za-z_]\w*)(?=\s*[\),])",
        RegexOptions.Compiled);
    private static readonly Regex CSharpCatchValueNameRegex = new(
        @"\bcatch\s*\(\s*(?:[A-Za-z_]\w*(?:\s*::\s*|\s*\.\s*)*[A-Za-z_]\w*(?:\s*<[^>\n]+>)?(?:\s*\?)?(?:\s*\[\s*\])*)\s+(?<name>@?[A-Za-z_]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex CSharpUsingStatementValueNameRegex = new(
        @"\busing\s*\(\s*(?:var|(?:[A-Za-z_]\w*(?:\s*::\s*|\s*\.\s*)*[A-Za-z_]\w*(?:\s*<[^>\n]+>)?(?:\s*\?)?(?:\s*\[\s*\])*))\s+(?<name>@?[A-Za-z_]\w*)\s*=",
        RegexOptions.Compiled);
    private static readonly Regex CSharpFixedValueNameRegex = new(
        @"\bfixed\s*\(\s*(?:var|(?:[A-Za-z_]\w*(?:\s*::\s*|\s*\.\s*)*[A-Za-z_]\w*(?:\s*<[^>\n]+>)?(?:\s*\?)?(?:\s*\[\s*\])*))\s+(?<name>@?[A-Za-z_]\w*)\s*=",
        RegexOptions.Compiled);
    private static readonly Regex CSharpStaticModifierRegex = new(@"\bstatic\b", RegexOptions.Compiled);
    // Inline `where` constraint in a C# type header; used to trim base-list parsing
    // C# 型ヘッダーの where 制約句。base-list 解析の終端として使用
    private static readonly Regex CSharpWhereClauseRegex = new(@"\s+where\s+(?<name>[\w?.]+)\s*:", RegexOptions.Compiled);
    // C# record declaration with a primary-constructor parameter list.
    // Used to synthesize a function-kind container for primary-ctor base calls
    // (e.g. `record Child(int x) : Parent(x)`), so `callers` / `callees` / `impact`
    // can attribute the `Parent(x)` edge to the record's synthetic constructor.
    // C# record のプライマリーコンストラクタ宣言を検出し、base primary-ctor 呼び出しの
    // 参照を record の合成コンストラクタに紐付けるために使う。
    private static readonly Regex CSharpRecordPrimaryCtorSignatureRegex = new(
        $@"\brecord\s+(?:class\s+|struct\s+)?{CSharpIdentifierPattern}(?:<[^>]+>)?\s*\(",
        RegexOptions.Compiled);
    // Same intent as CSharpRecordPrimaryCtorSignatureRegex but applied to the joined multi-line
    // header produced by CollectCSharpRecordHeader, so split-line forms like
    // `public record Child\n(\n    int Value\n)\n    : Parent(Value);` still match.
    // Also covers C# 12 `class` / `struct` primary constructors such as
    // `public class Child(int value) : Parent(value) { }` and
    // `public struct Child(int value) : IParent { }` so their `Parent(value)` chain edges are
    // also attributed to the synthetic function-kind container named after the declaring type.
    // CollectCSharpRecordHeader で連結された複数行ヘッダーに対しても当てるため、`record` / `class` /
    // `struct` と `(` が別行に分かれる書式でも primary-ctor 宣言と判定できるようにする。
    // C# 12 以降の class / struct primary constructor にも同じ合成コンテナ経路を適用する。
    private static readonly Regex CSharpPrimaryCtorHeaderRegex = new(
        $@"\b(?:record\s+(?:class\s+|struct\s+)?|class\s+|struct\s+){CSharpIdentifierPattern}(?:\s*<[^>]+>)?\s*\(",
        RegexOptions.Compiled);
    // C# compile-time type/member references: `nameof(X.Y)`, `typeof(T)`, `sizeof(T)`, `default(T)`.
    // Keywords are in SharedIgnoredCallNames so CallRegex skips them, but their arguments have no
    // trailing `(` and therefore slip through. Captured here as a dedicated "type_reference" kind
    // so callers/callees (which exclude type_reference by default) stay unaffected while
    // references and impact see the edge. See issue #253.
    // The regex only locates the keyword and opening `(`; the argument itself is walked by
    // ExtractCSharpTypeKeywordSegments so generic `<...>`, array `[...]`, and `global::` qualifiers
    // are handled without truncating the real type path.
    // C# の nameof/typeof/sizeof/default は、キーワード自体が SharedIgnoredCallNames にあるため
    // CallRegex では読み飛ばされ、引数の識別子も末尾に `(` が無いため通常経路では捕捉できない。
    // ここで type_reference として拾い、callers/callees（既定で type_reference を除外）に影響せず
    // references と impact だけに edge を届ける。issue #253 参照。
    // 正規表現はキーワードと `(` の位置だけを捕捉し、引数本体の走査は ExtractCSharpTypeKeywordSegments
    // に任せる。これにより generic `<...>`、配列 `[...]`、`global::` 等を途中で切らない。
    private static readonly Regex CSharpTypeKeywordIntroRegex = new(
        @"(?<![\w$])(?<keyword>nameof|typeof|sizeof|default)\s*\(",
        RegexOptions.Compiled);
    // Reflection member-name lookups such as `GetMethod("Run")` carry a real member reference
    // even though the symbol name appears as string data. Emit only literal or literal-concat
    // first arguments so dynamic names stay conservative.
    private static readonly Regex CSharpReflectionNameApiIntroRegex = new(
        @"(?<![\w$])(?<name>GetMethod|GetField|GetProperty|GetEvent|GetMember|GetNestedType)\s*\(",
        RegexOptions.Compiled);
    // C# type tests (`o is Base`, `o is not Base`, `o as Base`).
    // `is` / `is not` / `as` の型位置 (`o is Base`, `o is not Base`, `o as Base`)。
    private static readonly Regex CSharpIsAsTypeTestRegex = new(
        $@"(?<![\w$])(?:is\s+(?:not\s+)?|as\s+)(?<type>{CSharpTypeExpressionPattern})",
        RegexOptions.Compiled,
        ExtractionRegexTimeout);
    internal static readonly Regex CSharpTrailingIsAsTypePatternIntroRegex = new(
        @"(?<![\w$])(?:is(?:\s+not)?|as)\s*$",
        RegexOptions.Compiled);
    internal static readonly Regex CSharpTrailingCaseTypePatternIntroRegex = new(
        @"(?<![\w$])case(?:\s+not)?\s*$",
        RegexOptions.Compiled);
    internal static readonly Regex CSharpIsAsTypePatternIntroContextRegex = new(
        @"(?<![\w$])(?:is(?:\s+not)?|as)",
        RegexOptions.Compiled);
    internal static readonly Regex CSharpCaseTypePatternIntroContextRegex = new(
        @"(?<![\w$])case(?:\s+not)?",
        RegexOptions.Compiled);
    // C# `case` labels use a small structural follow-token check so declaration / recursive /
    // positional/logical patterns stay visible while constant member labels like
    // `case Color.Red:` and `case Color.Red or Color.Blue:` do not leak
    // `type_reference` edges.
    // C# の `case` ラベルは後続 token を小さく構文判定し、declaration / recursive /
    // positional / logical pattern を残しつつ `case Color.Red:` や
    // `case Color.Red or Color.Blue:` のような定数ラベルは `type_reference` にしない。
    private static readonly Regex CSharpCaseLabelRegex = new(
        @"(?<![\w$])case\s+",
        RegexOptions.Compiled);
    private static readonly Regex CSharpTypeExpressionAtCursorRegex = new(
        $@"\G(?<type>{CSharpTypeExpressionPattern})",
        RegexOptions.Compiled,
        ExtractionRegexTimeout);
    // C# XML-doc cross-reference (`<see cref="Base.Do"/>`, `<seealso cref="ILogger.Log"/>`).
    // C# XML doc の `<see cref="Base.Do"/>` / `<seealso cref="ILogger.Log"/>`。
    private static readonly Regex CSharpDocCrefRegex = new(
        @"<(?:see|seealso)\s+cref\s*=\s*""(?<cref>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Javadoc / KDoc cross-reference links (`{@link Foo#bar}`, `@see Foo`, `[Foo.bar]`).
    // Javadoc / KDoc の cross-reference link。
    private static readonly Regex JvmDocInlineLinkRegex = new(
        @"\{@(?:link|linkplain|value)\s+(?<target>[^\s}]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex JvmDocSeeReferenceRegex = new(
        @"(?:^|\s)@(?:see|throws|exception)\s+(?<target>[^\s}]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex KDocBracketLinkRegex = new(
        @"\[(?<target>#?(?:[_\p{L}][\w$]*|`[^`\r\n]+`)(?:(?:\.|#)(?:[_\p{L}][\w$]*|`[^`\r\n]+`))*)\](?!\s*(?:\(|\[))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Java primitive type names that can precede `.class` (e.g. `int.class`, `void.class`).
    // Skipped from reference rows because they are language-level keywords, not indexed types.
    // `int.class` 等に現れる Java のプリミティブ型。インデックス対象の型ではないため除外する。
    private static readonly HashSet<string> JavaPrimitiveTypeNames = new(StringComparer.Ordinal)
    {
        "int", "long", "short", "boolean", "byte", "char", "float", "double", "void",
    };

    // C# predefined type aliases / void / dynamic / var. They resolve to BCL primitives that are
    // not indexed as user-defined symbols, so emitting them as `type_reference` just pollutes
    // references/inspect output without ever linking to a real definition.
    // C# の built-in 型 alias / void / dynamic / var。ユーザー定義シンボルに解決しないため
    // type_reference として残すとノイズにしかならない。issue #253 のレビュー指摘により除外。
    private static readonly HashSet<string> CSharpBuiltInTypeNames = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
        "nint", "nuint", "char", "float", "double", "decimal",
        "string", "object", "void", "dynamic", "var",
    };
    private static readonly HashSet<string> CSharpWhereConstraintIgnoredSegments = new(StringComparer.Ordinal)
    {
        "allows", "default", "notnull", "ref", "unmanaged",
    };
    private static readonly Dictionary<string, HashSet<string>> LanguageBuiltInTypeNames = new(StringComparer.Ordinal)
    {
        ["typescript"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "any", "bigint", "boolean", "false", "never", "null", "number", "object", "string",
            "infer", "keyof", "readonly", "symbol", "true", "undefined", "unique", "unknown", "void",
        },
        ["kotlin"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "Any", "Boolean", "Byte", "Char", "Double", "Float", "Int", "Long", "Nothing",
            "Short", "String", "Unit",
        },
        ["swift"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "Any", "Bool", "Character", "Double", "Float", "Int", "Int8", "Int16", "Int32", "Int64",
            "Never", "Self", "String", "UInt", "UInt8", "UInt16", "UInt32", "UInt64", "Void",
            "any", "async", "borrowing", "consuming", "each", "inout", "isolated", "repeat", "rethrows",
            "sending", "some", "throws",
        },
        ["rust"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "Self", "bool", "char", "const", "dyn", "f32", "f64", "for", "i8", "i16", "i32", "i64", "i128",
            "impl", "isize", "mut", "ref", "static", "str", "u8", "u16", "u32", "u64", "u128", "usize",
        },
        ["c"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "_Atomic", "bool", "char", "const", "double", "enum", "float", "int", "long",
            "restrict", "short", "signed", "size_t", "ssize_t", "struct", "uint8_t",
            "uint16_t", "uint32_t", "uint64_t", "union", "unsigned", "void", "volatile",
        },
        ["cpp"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "bool", "char", "char8_t", "char16_t", "char32_t", "double", "float", "int", "long",
            "short", "signed", "size_t", "ssize_t", "std", "string", "unsigned", "void",
            "wchar_t",
        },
        ["go"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "any", "bool", "byte", "comparable", "complex64", "complex128", "error", "float32",
            "float64", "int", "int8", "int16", "int32", "int64", "rune", "string", "uint",
            "uint8", "uint16", "uint32", "uint64", "uintptr", "chan", "func", "interface",
            "map", "struct",
        },
        ["dart"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "bool", "double", "dynamic", "Function", "int", "Never", "Null", "num", "Object",
            "String", "void",
        },
        ["vb"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Boolean", "Byte", "Char", "Date", "Decimal", "Double", "Integer", "Long", "Object",
            "SByte", "Short", "Single", "String", "UInteger", "ULong", "UShort", "Void",
        },
        ["fortran"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "character", "complex", "double", "integer", "logical", "precision", "real",
        },
        ["pascal"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AnsiString", "Boolean", "Byte", "Cardinal", "Char", "Double", "Extended", "Integer",
            "LongInt", "LongWord", "Pointer", "Real", "ShortInt", "Single", "SmallInt", "String",
            "Variant", "WideString", "Word",
        },
        ["objc"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "BOOL", "Class", "CGFloat", "NSInteger", "NSUInteger", "SEL", "bool", "char", "double",
            "float", "id", "instancetype", "int", "long", "short", "void",
        },
        ["haskell"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "Bool", "Char", "Double", "Either", "False", "Float", "IO", "Int", "Integer", "Maybe",
            "Nothing", "String", "True",
        },
    };
    // C# pattern-only keywords / literals that can appear after `is` / `case not` but are never
    // real user-defined types. Filter them before AddTypeExpressionSegments so `is not null`,
    // `is default`, and similar constant patterns do not surface phantom `type_reference` rows.
    // `is` / `case not` の後ろに現れうるが、実在型ではない C# のパターン専用キーワード / リテラル。
    // AddTypeExpressionSegments 前に落とし、`is not null` や `is default` などの定数パターンから
    // phantom な `type_reference` 行が出ないようにする。
    private static readonly HashSet<string> CSharpNonTypePatternTokens = new(StringComparer.Ordinal)
    {
        "default", "false", "not", "null", "true",
    };

    // No-arg C# attribute name (`[Serializable]`, `[assembly: CLSCompliant]`, `[System.Obsolete]`,
    // `[global::System.Obsolete]`, `[Alias::MyAttr]`, `[Required, Key]`, and their multi-line
    // variants where `[` / `]` sit on separate lines). CallRegex only matches identifiers followed
    // by `(`, so no-arg attributes would otherwise never be indexed. The pattern refuses to match
    // when the identifier is followed by `(` (handled by CallRegex + TryClassifyMetadataReference)
    // or a qualifier continuation (`.` / `::`). The match is gated downstream by
    // `IsInsideCSharpAttributeRange`, so it is safe to relax the `[` / `,` left-anchor in favor of
    // a word-boundary lookbehind — that lets a bare identifier on a line like `    Serializable`
    // inside a multi-line attribute section still be recognized.
    // 引数なしの C# attribute 名用 regex。`[Serializable]` などは CallRegex では拾えないため専用の
    // 入口で捕捉する。`global::System.Obsolete` や `Alias::MyAttr` のように `::` 修飾子の付く形も
    // 許容する。`[` / `,` / `]` が別行にある複数行形（例: `[\n Serializable\n]`）も取り込むため、
    // 左側は `[` / `,` ではなく単語境界だけでアンカーする。属性以外の位置で誤検出しないよう、
    // マッチ後は `IsInsideCSharpAttributeRange` で属性レンジ内かどうかを確認する。後続が `(`
    // （CallRegex 経路）や `.` / `::`（qualifier 継続）なら名前を確定させず、行末（`$`）・`]`・`,`
    // のいずれかで初めて採用する。
    private static readonly Regex CSharpNoArgAttributeRegex = new(
        $@"(?<!\w)(?:{CSharpIdentifierPattern}\s*:\s*)?(?:{CSharpIdentifierPattern}\s*(?:\.|::)\s*)*(?<name>{CSharpIdentifierPattern})(?:\s*<[^\n]+?>)?\s*(?=[\],]|$)",
        RegexOptions.Compiled);

    // No-arg Java-family annotation (`@Deprecated`, `@Override`, `@org.junit.Test`, `@field:Deprecated`).
    // CallRegex only catches `@Name(` forms; this pattern fills the bare `@Name` gap. The leading
    // lookbehind `(?<![\w)])` prevents Kotlin label references like `return@foo` from matching.
    // 引数なしの Java 系 annotation 名用 regex。`@Deprecated` のような形は CallRegex では拾えないため
    // 専用経路で捕捉する。先頭の lookbehind `(?<![\w)])` で Kotlin の `return@foo` のようなラベル参照を
    // 除外する。
    private static readonly Regex NoArgAnnotationRegex = new(
        @"(?<![\w)])@(?:[A-Za-z_]\w*\s*:\s*)?(?:[A-Za-z_]\w*\s*\.\s*)*(?<name>[A-Za-z_]\w*)\b(?!\s*[.(])",
        RegexOptions.Compiled);
    private static readonly Regex KotlinBacktickAnnotationRegex = new(
        @"(?<![\w)])@(?:[A-Za-z_]\w*\s*:\s*)?(?<name>`[^`\r\n]+`)(?:\s*\([^)\r\n]*\))?",
        RegexOptions.Compiled);


    // Languages whose `@Decorator(args)` / `@Annotation(args)` / `@Attribute(args)` syntax
    // should produce `annotation` reference rows rather than `call` rows (issue #293).
    // Swift uses `@available(...)`, `@objc`, `@MainActor`, etc. as compile-time metadata;
    // Gradle/Groovy uses `@CompileStatic`, `@TaskAction`, etc. the same way. Without this
    // reclassification, `callers` / `callees` / `hotspots` / `impact` on those languages
    // get polluted with metadata edges.
    // `@Decorator(args)` / `@Annotation(args)` / `@Attribute(args)` を `call` ではなく
    // `annotation` として記録すべき言語 (issue #293)。Swift の `@available(...)` / `@objc` /
    // `@MainActor` や、Gradle/Groovy の `@CompileStatic` / `@TaskAction` も compile-time
    // metadata なので同じ扱いにする。再分類しないと `callers` / `callees` / `hotspots` /
    // `impact` に metadata edge が混入する。
    private static readonly HashSet<string> AnnotationLanguages = new(StringComparer.Ordinal)
    {
        "java", "kotlin", "scala", "typescript", "javascript", "swift", "gradle", "dart",
    };

    // Kotlin use-site target prefixes for annotations (e.g. `@field:Deprecated("msg")`,
    // `@file:JvmName("Foo")`). Keep aligned with the Kotlin language spec use-site targets.
    // Kotlin の use-site target 付き注釈用の接頭辞。
    private static readonly HashSet<string> KotlinAnnotationTargets = new(StringComparer.Ordinal)
    {
        "field", "get", "set", "param", "setparam", "property", "receiver", "file", "delegate", "all",
    };

    public static IReadOnlyCollection<string> GetSupportedLanguages()
        => RegisteredLanguages
            .Concat(new[] { "vue", "svelte", "razor", "blazor", "cshtml" })
            .Concat(ExtractorPluginRegistry.ReferenceLanguages)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Registered language keys for reference extraction.
    /// 参照抽出に登録されている言語キー。
    /// </summary>
    public static IReadOnlyCollection<string> RegisteredLanguages => Extractors.Keys.ToArray();

    private static string? NormalizeLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return null;

        lang = lang.Trim().ToLowerInvariant();
        return lang is "vue" or "svelte"
            ? "typescript"
            : lang is "razor" or "blazor" or "cshtml"
                ? "csharp"
                : lang;
    }

    private static string? NormalizePluginLanguage(string? lang)
        => string.IsNullOrWhiteSpace(lang) ? null : lang.Trim().ToLowerInvariant();

    public static bool SupportsLanguage(string? lang)
    {
        var normalized = NormalizeLanguage(lang);
        if (normalized != null && Extractors.ContainsKey(normalized))
            return true;

        return NormalizePluginLanguage(lang) is string pluginLanguage
            && ExtractorPluginRegistry.TryGetReferenceExtractor(pluginLanguage, out _);
    }

    /// <summary>
    /// Returns the registered reference extractor for a supported language.
    /// 対応言語の登録済み参照抽出器を返す。
    /// </summary>
    public static bool TryGetExtractor(string? lang, out IReferenceExtractor extractor)
    {
        var normalized = NormalizeLanguage(lang);
        if (normalized != null && Extractors.TryGetValue(normalized, out extractor!))
            return true;

        extractor = null!;
        return false;
    }

    public static bool? SupportsSymbolGraph(string? lang, string? kind, string? containerKind)
    {
        if (lang == null)
            return null;

        return SupportsLanguage(lang);
    }

    public static string? GetUnsupportedSymbolKind(string? lang, string? kind, string? containerKind)
    {
        return null;
    }

    /// <summary>
    /// Build a human-readable reason explaining graph-support status for the given language.
    /// Returns null when neither language nor support status is known.
    /// 指定言語の graph 対応状況を人間向けに説明する文字列を返す。言語も対応状況も不明なら null。
    /// </summary>
    public static string? BuildGraphSupportReason(string? lang, bool? graphSupported, string? kind = null, string? containerKind = null)
    {
        if (lang == null || graphSupported == null)
            return null;

        if (graphSupported.Value)
            return $"Call-graph extraction is indexed for '{lang}'.";

        return $"Call-graph extraction is not indexed for '{lang}'. Use search, definition, excerpt, or files instead.";
    }

    public static string? BuildGraphSupportReasonWithUnsupportedEnumMemberGap(string? lang, bool? graphSupported, bool hasUnsupportedEnumMember, bool hasSupportedGraphDefinition)
    {
        var baseReason = BuildGraphSupportReason(lang, graphSupported);
        if (!hasUnsupportedEnumMember)
            return baseReason;

        var enumGapReason = hasSupportedGraphDefinition
            ? "Exact results also include C# enum members whose access edges are not indexed yet."
            : BuildGraphSupportReason("csharp", true, "enum", "enum");

        if (!hasSupportedGraphDefinition)
            return enumGapReason;

        if (string.IsNullOrWhiteSpace(baseReason))
            return enumGapReason;
        if (string.IsNullOrWhiteSpace(enumGapReason) || string.Equals(baseReason, enumGapReason, StringComparison.Ordinal))
            return baseReason;

        return $"{baseReason} {enumGapReason}";
    }

    private static bool IsUnsupportedCSharpEnumMemberSymbol(string? lang, string? kind, string? containerKind)
    {
        return false;
    }

    private static string NormalizeKotlinBacktickIdentifier(string name)
    {
        if (name.Length >= 2 && name[0] == '`' && name[^1] == '`')
            return name[1..^1];
        return name;
    }

    /// <summary>
    /// Extract indexed references for supported languages.
    /// 対応言語向けにインデックス化する参照を抽出する。
    /// </summary>
    public static List<ReferenceRecord> Extract(
        long fileId,
        string? lang,
        string content,
        IReadOnlyList<SymbolRecord> symbols,
        string? path = null,
        IReadOnlyList<SymbolRecord>? workspaceSymbols = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestedLanguage = lang;
        var pluginLanguage = NormalizePluginLanguage(lang);
        if (!TryGetExtractor(lang, out var extractor))
        {
            if (pluginLanguage == null || !ExtractorPluginRegistry.TryGetReferenceExtractor(pluginLanguage, out var pluginExtractor))
                return [];

            if (string.IsNullOrEmpty(content))
                return [];
            if (ChunkSplitter.HasOversizeLine(content))
                return [];
            if (content.Contains('\r'))
                content = content.Replace("\r\n", "\n").Replace("\r", "\n");
            content = FileIndexer.StripLineLeadingInvisibles(content);
            cancellationToken.ThrowIfCancellationRequested();

            return pluginExtractor.Extract(
                    fileId,
                    content,
                    new ExtractionContext(pluginLanguage, path, symbols, workspaceSymbols))
                .ToList();
        }

        lang = NormalizeLanguage(lang);
        var language = lang!;
        return extractor.Extract(new ReferenceExtractionContext(
            fileId,
            language,
            content,
            symbols,
            path,
            workspaceSymbols,
            requestedLanguage,
            cancellationToken));
    }
    private static Dictionary<int, HashSet<string>> BuildDefinitionNamesByLine(
        string language,
        IReadOnlyList<SymbolRecord> symbols)
    {
        var definitionNamesComparer = GetDefinitionNamesComparer(language);

        return symbols
            .GroupBy(symbol => symbol.Line)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var names = new HashSet<string>(definitionNamesComparer);
                    foreach (var symbol in group)
                    {
                        names.Add(symbol.Name);
                        if (language == "sql")
                        {
                            SqlReferenceExtractor.AddDefinitionNameAliases(names, symbol);
                        }
                    }

                    return names;
                });
    }

    private static StringComparer GetDefinitionNamesComparer(string language)
        => language == "sql"
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static List<SymbolRecord> BuildReferenceContainerCandidates(IReadOnlyList<SymbolRecord> symbols)
        => symbols
            .Where(symbol => symbol.BodyStartLine != null && symbol.BodyEndLine != null &&
                              (IsFunctionLikeSymbolKind(symbol.Kind) || symbol.Kind == "hook" || symbol.Kind == "accessor" || symbol.Kind == "class"
                               || symbol.Kind == "struct" || symbol.Kind == "namespace"
                               || symbol.Kind == "object" || symbol.Kind == "property" || symbol.Kind == "class_hook"))
            .OrderBy(symbol => (symbol.BodyEndLine ?? symbol.EndLine) - (symbol.BodyStartLine ?? symbol.StartLine))
            .ToList();

    private static List<SymbolRecord>? BuildCSharpXmlDocAttachmentScopeCandidates(
        string language,
        IReadOnlyList<SymbolRecord> symbols)
        => language == "csharp"
            ? symbols
                .Where(symbol => symbol.BodyStartLine != null && symbol.BodyEndLine != null
                                 && symbol.Kind is "class" or "struct" or "interface" or "enum" or "namespace")
                .OrderBy(symbol => (symbol.BodyEndLine ?? symbol.EndLine) - (symbol.BodyStartLine ?? symbol.StartLine))
                .ToList()
            : null;

    private static List<SymbolRecord> BuildEnclosingTypeCandidates(IReadOnlyList<SymbolRecord> symbols)
        => symbols
            .Where(symbol => symbol.BodyStartLine != null && symbol.BodyEndLine != null &&
                             (symbol.Kind == "class" || symbol.Kind == "struct" || symbol.Kind == "interface" || symbol.Kind == "enum"))
            .OrderBy(symbol => (symbol.BodyEndLine ?? symbol.EndLine) - (symbol.BodyStartLine ?? symbol.StartLine))
            .ToList();

    private static Dictionary<int, SymbolRecord[]>? BuildSwiftPropertyDefinitionsByLine(
        string language,
        IReadOnlyList<SymbolRecord> symbols)
        => language == "swift"
            ? symbols
                .Where(symbol => symbol.Kind == "property")
                .GroupBy(symbol => symbol.Line)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(symbol => symbol.StartColumn ?? 0).ToArray())
            : null;

    private static void EmitPhpLinePreambleReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        int lineNumber,
        Func<SymbolRecord?> getLineContainer,
        ref bool inDocblock,
        ref SymbolRecord? docblockContainer,
        ref HashSet<string>? docblockPropertyNames)
    {
        if (originalLine.Contains("#[", StringComparison.Ordinal))
        {
            var attributeContext = originalLine.Trim();
            if (attributeContext.Length > 0)
            {
                PhpReferenceExtractor.EmitAttributeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    attributeContext,
                    lineNumber,
                    getLineContainer());
            }
        }

        if (originalLine.IndexOf("/**", StringComparison.Ordinal) >= 0)
        {
            inDocblock = true;
            docblockContainer = getLineContainer();
            docblockPropertyNames = new HashSet<string>(StringComparer.Ordinal);
        }

        var docblockContext = originalLine.Trim();
        if (docblockContext.Length > 0)
        {
            if (originalLine.Contains("param", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockParamTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("return", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockReturnTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("var", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockVarTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("@throws", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockThrowsTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("extends", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockExtendsTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("implements", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockImplementsTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("@mixin", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockMixinTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("property", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockPropertyTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer),
                    docblockPropertyNames);
            }

            if (originalLine.Contains("@method", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockMethodReturnTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
                PhpReferenceExtractor.EmitDocblockMethodParameterTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("@template", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockTemplateBoundTypeReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }

            if (originalLine.Contains("type", StringComparison.OrdinalIgnoreCase))
            {
                PhpReferenceExtractor.EmitDocblockTypeAliasTargetReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
                PhpReferenceExtractor.EmitDocblockImportTypeSourceReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    docblockContext,
                    lineNumber,
                    ResolvePhpDocblockContainer(inDocblock, docblockContainer, getLineContainer));
            }
        }

        if (inDocblock && originalLine.IndexOf("*/", StringComparison.Ordinal) >= 0)
        {
            inDocblock = false;
            docblockContainer = null;
            docblockPropertyNames = null;
        }
    }

    private static SymbolRecord? ResolvePhpDocblockContainer(
        bool inDocblock,
        SymbolRecord? docblockContainer,
        Func<SymbolRecord?> getLineContainer)
        => inDocblock ? docblockContainer : getLineContainer();

    internal static void AddReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Match match,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string? language = null)
    {
        AddReference(
            references,
            seen,
            fileId,
            match.Groups["name"].Value,
            match.Groups["name"].Index,
            referenceKind,
            context,
            lineNumber,
            container,
            language);
    }

    internal static void AddReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string name,
        int nameIndex,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string? language = null)
    {
        var column = nameIndex + 1;
        var dedupeKey = BuildReferenceDedupeKey(fileId, language, lineNumber, column, referenceKind, name, container);
        if (!seen.Add(dedupeKey))
            return;

        references.Add(new ReferenceRecord
        {
            FileId = fileId,
            SymbolName = name,
            ReferenceKind = referenceKind,
            Line = lineNumber,
            Column = column,
            Context = context,
            ContainerKind = container?.Kind,
            ContainerName = container?.Name,
            IsSelfReference = IsSameReferenceName(container?.Name, name),
        });
    }

    internal static string BuildReferenceDedupeKey(
        long fileId,
        string? language,
        int lineNumber,
        int column,
        string referenceKind,
        string name,
        SymbolRecord? container)
    {
        var languageSegment = string.IsNullOrWhiteSpace(language) ? "-" : language;
        var containerKindSegment = string.IsNullOrWhiteSpace(container?.Kind) ? "-" : container.Kind;
        var containerNameSegment = string.IsNullOrWhiteSpace(container?.Name) ? "-" : container.Name;
        return $"{fileId}:{languageSegment}:{lineNumber}:{column}:{referenceKind}:{containerKindSegment}:{containerNameSegment}:{name}";
    }

    private static void EmitCSharpLambdaCaptureReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Dictionary<string, HashSet<string>>? localNamesByFunction)
    {
        if (container?.Kind != "function"
            || localNamesByFunction == null
            || !localNamesByFunction.TryGetValue(GetCSharpContainerLocalScopeKey(container), out var localNames)
            || localNames.Count == 0)
        {
            return;
        }

        foreach (Match lambda in CSharpLambdaRegex.Matches(preparedLine))
        {
            var body = lambda.Groups["body"].Value;
            if (string.IsNullOrWhiteSpace(body))
                continue;

            var parameterNames = CollectCSharpLambdaParameterNames(lambda.Groups["params"].Value);
            foreach (var localName in localNames)
            {
                if (parameterNames.Contains(localName))
                    continue;
                if (!ContainsCSharpIdentifier(body, localName, out var bodyRelativeIndex))
                    continue;

                AddReference(
                    references,
                    seen,
                    fileId,
                    localName,
                    lambda.Groups["body"].Index + bodyRelativeIndex,
                    "capture",
                    context,
                    lineNumber,
                    container,
                    "csharp");
            }
        }
    }

    private static HashSet<string> CollectCSharpLambdaParameterNames(string parameterText)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(parameterText, CSharpIdentifierPattern))
        {
            var name = NormalizeAtPrefixedIdentifier(match.Value);
            if (!IsIgnoredCallName("csharp", name))
                names.Add(name);
        }

        return names;
    }

    private static bool ContainsCSharpIdentifier(string text, string name, out int index)
    {
        index = -1;
        var normalizedName = NormalizeAtPrefixedIdentifier(name);
        foreach (Match match in Regex.Matches(text, CSharpIdentifierPattern))
        {
            if (string.Equals(NormalizeAtPrefixedIdentifier(match.Value), normalizedName, StringComparison.Ordinal))
            {
                index = match.Index;
                return true;
            }
        }

        return false;
    }

    private static void TrackCSharpLocalDeclarations(
        string preparedLine,
        SymbolRecord? container,
        Dictionary<string, HashSet<string>>? localNamesByFunction)
    {
        if (container?.Kind != "function" || localNamesByFunction == null)
            return;
        if (preparedLine.Contains("=>", StringComparison.Ordinal))
            return;

        foreach (Match match in CSharpLocalDeclarationRegex.Matches(preparedLine))
        {
            var name = NormalizeAtPrefixedIdentifier(match.Groups["name"].Value);
            if (IsIgnoredCallName("csharp", name))
                continue;

            var scopeKey = GetCSharpContainerLocalScopeKey(container);
            if (!localNamesByFunction.TryGetValue(scopeKey, out var localNames))
            {
                localNames = new HashSet<string>(StringComparer.Ordinal);
                localNamesByFunction[scopeKey] = localNames;
            }

            localNames.Add(name);
        }
    }

    private static string GetCSharpContainerLocalScopeKey(SymbolRecord container)
        => $"{container.Kind}:{container.ContainerQualifiedName}:{container.ContainerKind}:{container.ContainerName}:{container.Name}:{container.StartLine}:{container.EndLine}:{container.BodyStartLine}:{container.BodyEndLine}:{container.StartColumn}";

    private static void MarkMutualRecursionReferences(List<ReferenceRecord> references)
    {
        var edges = new HashSet<(string Caller, string Callee)>();
        foreach (var reference in references)
        {
            if (!IsCallGraphLikeReferenceKind(reference.ReferenceKind)
                || string.IsNullOrWhiteSpace(reference.ContainerName)
                || string.IsNullOrWhiteSpace(reference.SymbolName)
                || reference.IsSelfReference)
            {
                continue;
            }

            edges.Add((NormalizeReferenceCycleName(reference.ContainerName), NormalizeReferenceCycleName(reference.SymbolName)));
        }

        if (edges.Count == 0)
            return;

        foreach (var reference in references)
        {
            if (!IsCallGraphLikeReferenceKind(reference.ReferenceKind)
                || string.IsNullOrWhiteSpace(reference.ContainerName)
                || string.IsNullOrWhiteSpace(reference.SymbolName)
                || reference.IsSelfReference)
            {
                continue;
            }

            var caller = NormalizeReferenceCycleName(reference.ContainerName);
            var callee = NormalizeReferenceCycleName(reference.SymbolName);
            if (edges.Contains((callee, caller)))
                reference.IsMutualRecursion = true;
        }
    }

    private static bool IsCallGraphLikeReferenceKind(string referenceKind)
        => referenceKind is "call" or "instantiate" or "subscribe" or "unsubscribe" or "razor_event_binding";

    private static bool IsSameReferenceName(string? left, string right)
        => !string.IsNullOrWhiteSpace(left)
            && string.Equals(NormalizeReferenceCycleName(left), NormalizeReferenceCycleName(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeReferenceCycleName(string name)
    {
        var trimmed = name.Trim();
        var dot = trimmed.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < trimmed.Length)
            return trimmed[(dot + 1)..];
        var colon = trimmed.LastIndexOf("::", StringComparison.Ordinal);
        return colon >= 0 && colon + 2 < trimmed.Length ? trimmed[(colon + 2)..] : trimmed;
    }

    private readonly record struct PythonLogicalHeaderReferenceLine(string Text, int[] PhysicalLines, int[] PhysicalColumns);

    private static bool TryBuildPythonLogicalHeaderReferenceLine(
        string[] lines,
        int startLineIndex,
        int startColumn,
        out PythonLogicalHeaderReferenceLine header)
    {
        var builder = new StringBuilder();
        var physicalLines = new List<int>();
        var physicalColumns = new List<int>();
        var parenDepth = 0;
        var bracketDepth = 0;
        var inString = false;
        var quote = '\0';

        for (var lineIndex = startLineIndex; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var column = lineIndex == startLineIndex ? startColumn : FindFirstNonWhitespaceColumn(line);
            var fragmentEndColumn = FindPythonCommentColumn(line, column);
            if (column < fragmentEndColumn)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                    physicalLines.Add(lineIndex);
                    physicalColumns.Add(column);
                }

                for (var fragmentColumn = column; fragmentColumn < fragmentEndColumn; fragmentColumn++)
                {
                    var fragmentChar = line[fragmentColumn];
                    if (fragmentChar == '\\' && fragmentColumn == fragmentEndColumn - 1)
                        break;

                    builder.Append(fragmentChar);
                    physicalLines.Add(lineIndex);
                    physicalColumns.Add(fragmentColumn);
                }
            }

            for (var scan = column; scan < line.Length; scan++)
            {
                var ch = line[scan];
                if (inString)
                {
                    if (ch == '\\')
                    {
                        scan++;
                        continue;
                    }

                    if (ch == quote)
                        inString = false;
                    continue;
                }

                if (ch is '\'' or '"')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }

                if (ch == '#')
                    break;
                if (ch == '(')
                    parenDepth++;
                else if (ch == ')' && parenDepth > 0)
                    parenDepth--;
                else if (ch == '[')
                    bracketDepth++;
                else if (ch == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (ch == ':' && parenDepth == 0 && bracketDepth == 0)
                {
                    header = new PythonLogicalHeaderReferenceLine(builder.ToString(), physicalLines.ToArray(), physicalColumns.ToArray());
                    return header.Text.Length > 0;
                }
            }

            if (parenDepth == 0 && bracketDepth == 0 && !line.TrimEnd().EndsWith('\\'))
                break;
        }

        header = new PythonLogicalHeaderReferenceLine(builder.ToString(), physicalLines.ToArray(), physicalColumns.ToArray());
        return header.Text.Length > 0;
    }

    private static bool TryBuildPythonLogicalStatementReferenceLine(
        string[] lines,
        int startLineIndex,
        int startColumn,
        out PythonLogicalHeaderReferenceLine header)
    {
        var builder = new StringBuilder();
        var physicalLines = new List<int>();
        var physicalColumns = new List<int>();
        var parenDepth = 0;
        var bracketDepth = 0;
        var inString = false;
        var quote = '\0';

        for (var lineIndex = startLineIndex; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var column = lineIndex == startLineIndex ? startColumn : FindFirstNonWhitespaceColumn(line);
            var fragmentEndColumn = FindPythonCommentColumn(line, column);
            if (column < fragmentEndColumn)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                    physicalLines.Add(lineIndex);
                    physicalColumns.Add(column);
                }

                for (var fragmentColumn = column; fragmentColumn < fragmentEndColumn; fragmentColumn++)
                {
                    var fragmentChar = line[fragmentColumn];
                    if (fragmentChar == '\\' && fragmentColumn == fragmentEndColumn - 1)
                        break;

                    builder.Append(fragmentChar);
                    physicalLines.Add(lineIndex);
                    physicalColumns.Add(fragmentColumn);
                }
            }

            for (var scan = column; scan < line.Length; scan++)
            {
                var ch = line[scan];
                if (inString)
                {
                    if (ch == '\\')
                    {
                        scan++;
                        continue;
                    }

                    if (ch == quote)
                        inString = false;
                    continue;
                }

                if (ch is '\'' or '"')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }

                if (ch == '#')
                    break;
                if (ch == '(')
                    parenDepth++;
                else if (ch == ')' && parenDepth > 0)
                    parenDepth--;
                else if (ch == '[')
                    bracketDepth++;
                else if (ch == ']' && bracketDepth > 0)
                    bracketDepth--;
            }

            if (parenDepth == 0 && bracketDepth == 0 && !line.TrimEnd().EndsWith('\\'))
                break;
        }

        header = new PythonLogicalHeaderReferenceLine(builder.ToString(), physicalLines.ToArray(), physicalColumns.ToArray());
        return header.Text.Length > 0;
    }

    private static int FindPythonCommentColumn(string line, int startColumn)
    {
        var inString = false;
        var quote = '\0';
        for (var index = startColumn; index < line.Length; index++)
        {
            var ch = line[index];
            if (inString)
            {
                if (ch == '\\')
                {
                    index++;
                    continue;
                }

                if (ch == quote)
                    inString = false;
                continue;
            }

            if (ch is '\'' or '"')
            {
                inString = true;
                quote = ch;
                continue;
            }

            if (ch == '#')
                return index;
        }

        return line.Length;
    }

    private static int FindFirstNonWhitespaceColumn(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        return index;
    }

    private static void RemapPythonLogicalHeaderReferences(
        List<ReferenceRecord> references,
        int startIndex,
        PythonLogicalHeaderReferenceLine header,
        string[] lines)
    {
        for (var i = startIndex; i < references.Count; i++)
        {
            var logicalIndex = references[i].Column - 1;
            if (logicalIndex < 0 || logicalIndex >= header.PhysicalLines.Length)
                continue;

            var physicalLineIndex = header.PhysicalLines[logicalIndex];
            references[i].Line = physicalLineIndex + 1;
            references[i].Column = header.PhysicalColumns[logicalIndex] + 1;
            references[i].Context = lines[physicalLineIndex].Trim();
        }
    }

    private static Dictionary<(int Line, string Kind), SymbolRecord> BuildPythonDefinitionContainersByLineAndKind(IReadOnlyList<SymbolRecord> symbols)
    {
        var containers = new Dictionary<(int Line, string Kind), SymbolRecord>();
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "function"))
                continue;

            containers.TryAdd((symbol.Line, symbol.Kind), symbol);
        }

        return containers;
    }

    private static bool IsJsxFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".jsx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tsx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySkipTypeScriptJsxTypeArguments(string preparedLine, ref int scan)
    {
        if (scan >= preparedLine.Length || preparedLine[scan] != '<')
            return false;

        var depth = 0;
        while (scan < preparedLine.Length)
        {
            var ch = preparedLine[scan++];
            if (ch == '\'' || ch == '"')
            {
                while (scan < preparedLine.Length)
                {
                    var quoted = preparedLine[scan++];
                    if (quoted == '\\')
                    {
                        scan = Math.Min(scan + 1, preparedLine.Length);
                        continue;
                    }

                    if (quoted == ch)
                        break;
                }

                continue;
            }

            if (ch == '=' && scan < preparedLine.Length && preparedLine[scan] == '>')
            {
                scan++;
                continue;
            }

            if (ch == '<')
            {
                depth++;
            }
            else if (ch == '>')
            {
                depth--;
                if (depth == 0)
                    return true;
                if (depth < 0)
                    return false;
            }
        }

        return false;
    }

    private static bool IsRazorFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".razor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".cshtml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObjCSelectorLiteralCall(string line, string name, int nameIndex) =>
        string.Equals(NormalizeAtPrefixedIdentifier(name), "selector", StringComparison.Ordinal)
        && (name.StartsWith('@') || nameIndex > 0 && line[nameIndex - 1] == '@');

    /// <summary>
    /// Emit one `type_reference` row per dot-segment of a captured argument. Columns are
    /// computed relative to the original line so tooling can jump to the exact identifier.
    /// 捕捉した引数の dot-segment ごとに `type_reference` 行を発行する。列位置は元の行基準で計算する。
    /// </summary>
    internal static void AddTypeReferenceSegments(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string arg,
        int argStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language)
    {
        int offset = 0;
        foreach (var segment in arg.Split('.'))
        {
            if (segment.Length == 0)
            {
                offset += 1; // '.' separator / ドット区切り分
                continue;
            }

            var normalizedSegment = language == "csharp" ? NormalizeCSharpIdentifier(segment) : segment;
            var isEscapedCSharpIdentifier = language == "csharp" && segment[0] == '@';
            if (!IsIgnoredTypeReferenceSegment(language, normalizedSegment, isEscapedCSharpIdentifier))
            {
                int column = argStartInLine + offset + 1; // 1-based / 1始まり
                var dedupeKey = BuildReferenceDedupeKey(fileId, language, lineNumber, column, "type_reference", normalizedSegment, container);
                if (seen.Add(dedupeKey))
                {
                    references.Add(new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = normalizedSegment,
                        ReferenceKind = "type_reference",
                        Line = lineNumber,
                        Column = column,
                        Context = context,
                        ContainerKind = container?.Kind,
                        ContainerName = container?.Name,
                    });
                }
            }

            offset += segment.Length + 1; // segment + '.'
        }
    }

    private static bool IsIgnoredTypeReferenceSegment(string language, string segment, bool isEscapedCSharpIdentifier = false, IReadOnlySet<string>? ignoredSegments = null)
    {
        if (isEscapedCSharpIdentifier)
            return false;
        if (ignoredSegments != null && ignoredSegments.Contains(segment))
            return true;
        if (IsIgnoredCallName(language, segment))
            return true;
        if (language == "java" && JavaPrimitiveTypeNames.Contains(segment))
            return true;
        if (language == "csharp" && CSharpBuiltInTypeNames.Contains(segment))
            return true;
        if (LanguageBuiltInTypeNames.TryGetValue(language, out var builtInTypes)
            && builtInTypes.Contains(segment))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Walk the argument list of a C# nameof/typeof/sizeof/default starting at
    /// <paramref name="startIndex"/> (the char right after `(`). Emits one `type_reference` row
    /// per identifier segment while handling generic `&lt;...&gt;`, array `[...]`,
    /// parenthesized/tuple groups `(...)`, and `global::` / `Alias::` qualifier skipping so nested
    /// paths like `nameof(List&lt;int&gt;.Count)`, `nameof(global::System.String)`,
    /// and `typeof((Foo, Bar))` are indexed correctly.
    /// C# の nameof/typeof/sizeof/default の引数を `(` 直後から lexer で走査し、
    /// generic `&lt;...&gt;`・配列 `[...]`・タプル `(...)` 群・`global::` / `Alias::` 修飾子を
    /// 跨ぎながら識別子セグメントごとに type_reference を発行する。
    /// </summary>
    private static void ExtractCSharpTypeKeywordSegments(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string line,
        int startIndex,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        int i = startIndex;
        int parenDepth = 0;
        int angleDepth = 0;
        bool expectSegment = true;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == ')')
            {
                if (parenDepth == 0)
                    return;
                parenDepth--;
                i++;
                expectSegment = false;
                continue;
            }

            if (c == ',')
            {
                if (parenDepth == 0 && angleDepth == 0)
                    return;
                // Tuple or generic argument separator inside `typeof((Foo, Bar))` /
                // `typeof(List<Foo, Bar>)` — keep scanning.
                // `typeof((Foo, Bar))` のタプル要素区切りや `typeof(List<Foo, Bar>)`
                // の generic 引数区切りは続けて走査する。
                i++;
                expectSegment = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (expectSegment && IsCSharpIdentifierStart(c))
            {
                int segStart = i;
                if (line[i] == '@')
                    i++;
                while (i < line.Length && IsCSharpIdentifierPart(line[i]))
                    i++;
                var rawSegment = line.Substring(segStart, i - segStart);
                var segment = NormalizeCSharpIdentifier(rawSegment);
                var isEscapedCSharpIdentifier = rawSegment.Length > 0 && rawSegment[0] == '@';
                // `Alias::Member` — the left-hand side is a namespace alias, not an indexed
                // type. Drop it instead of emitting it, and treat what follows the `::` as a
                // fresh segment head.
                // `Alias::Member` の左辺はエイリアスであり型シンボルではないため発行せず、
                // `::` の右側を新しいセグメント先頭として読み直す。
                if (i + 1 < line.Length && line[i] == ':' && line[i + 1] == ':')
                {
                    i += 2;
                    expectSegment = true;
                    continue;
                }

                if (ignoredSegments?.Contains(segment) == true)
                {
                    expectSegment = false;
                    continue;
                }

                AddTypeReferenceSegment(references, seen, fileId, segment, segStart, context, lineNumber, container, language, isEscapedCSharpIdentifier);
                expectSegment = false;
                continue;
            }

            if (c == '.')
            {
                i++;
                expectSegment = true;
                continue;
            }

            if (c == '<')
            {
                angleDepth++;
                i++;
                expectSegment = true;
                continue;
            }

            if (c == '>')
            {
                if (angleDepth == 0)
                    return;
                angleDepth--;
                i++;
                expectSegment = false;
                continue;
            }

            if (c == '[')
            {
                i = SkipBalanced(line, i, '[', ']');
                continue;
            }

            if (c == '(')
            {
                // Track paren depth instead of skipping the body so tuple/parenthesized
                // type groups like `typeof((Foo, Bar))` still yield inner segments.
                // タプル型 `typeof((Foo, Bar))` の中身も拾えるよう、括弧はスキップせず
                // 深さだけ追跡する。
                parenDepth++;
                i++;
                expectSegment = true;
                continue;
            }

            // Unknown token (operator, string start, etc.) — stop scanning this argument.
            // 解釈できないトークンが来たら、このキーワード引数の走査を打ち切る。
            return;
        }
    }

    private static void ExtractCSharpReflectionNameLiteralReferences(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string preparedLine,
        string originalLine,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (!CSharpReflectionNameApiIntroRegex.IsMatch(preparedLine))
            return;

        var codeLine = SanitizeCSharpCommentsForReflectionNameScan(originalLine);
        foreach (Match match in CSharpReflectionNameApiIntroRegex.Matches(codeLine))
        {
            if (IsInsideCSharpStringLiteral(codeLine, match.Index))
                continue;
            if (!preparedLine.Contains(match.Groups["name"].Value, StringComparison.Ordinal))
                continue;

            var argStart = match.Index + match.Length;
            if (!TryReadCSharpReflectionNameLiteral(originalLine, argStart, out var symbolName, out var nameIndex))
                continue;
            if (!IsValidCSharpReflectionSymbolName(symbolName))
                continue;

            AddReference(references, seen, fileId, symbolName, nameIndex, "type_reference", context, lineNumber, container, "csharp");
        }
    }

    private static bool TryReadCSharpReflectionNameLiteral(string line, int startIndex, out string symbolName, out int nameIndex)
    {
        symbolName = string.Empty;
        nameIndex = -1;
        var builder = new StringBuilder();
        var i = startIndex;
        var sawLiteral = false;
        var firstLiteralIndex = -1;

        while (i < line.Length)
        {
            SkipWhitespace(line, ref i);
            if (!TryReadCSharpStringLiteral(line, ref i, out var value, out var literalContentIndex))
                return false;

            if (!sawLiteral)
                firstLiteralIndex = literalContentIndex;
            sawLiteral = true;
            builder.Append(value);

            SkipWhitespace(line, ref i);
            if (i >= line.Length)
                return false;
            if (line[i] == ',' || line[i] == ')')
            {
                symbolName = builder.ToString();
                nameIndex = firstLiteralIndex;
                return sawLiteral && symbolName.Length > 0;
            }
            if (line[i] != '+')
                return false;

            i++;
        }

        return false;
    }

    private static string SanitizeCSharpCommentsForReflectionNameScan(string line)
    {
        var chars = line.ToCharArray();
        var inRegularString = false;
        var inVerbatimString = false;
        var inChar = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inRegularString)
            {
                if (c == '\\' && i + 1 < line.Length)
                    i++;
                else if (c == '"')
                    inRegularString = false;
                continue;
            }
            if (inVerbatimString)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    i++;
                else if (c == '"')
                    inVerbatimString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\' && i + 1 < line.Length)
                    i++;
                else if (c == '\'')
                    inChar = false;
                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                return line[..i];
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i += 2;
                while (i < line.Length)
                {
                    chars[i] = ' ';
                    if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        chars[i + 1] = ' ';
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                inVerbatimString = true;
                i++;
                continue;
            }
            if (c == '$' && i + 1 < line.Length && line[i + 1] == '"')
            {
                inRegularString = true;
                i++;
                continue;
            }
            if (c == '"')
                inRegularString = true;
            else if (c == '\'')
                inChar = true;
        }

        return new string(chars);
    }

    private static bool IsInsideCSharpStringLiteral(string line, int targetIndex)
    {
        var inRegularString = false;
        var inVerbatimString = false;
        for (var i = 0; i < line.Length && i < targetIndex; i++)
        {
            var c = line[i];
            if (inRegularString)
            {
                if (c == '\\' && i + 1 < line.Length)
                    i++;
                else if (c == '"')
                    inRegularString = false;
                continue;
            }
            if (inVerbatimString)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    i++;
                else if (c == '"')
                    inVerbatimString = false;
                continue;
            }

            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                inVerbatimString = true;
                i++;
            }
            else if (c == '$' && i + 1 < line.Length && line[i + 1] == '"')
            {
                inRegularString = true;
                i++;
            }
            else if (c == '"')
            {
                inRegularString = true;
            }
        }

        return inRegularString || inVerbatimString;
    }

    private static bool TryReadCSharpStringLiteral(string line, ref int index, out string value, out int contentIndex)
    {
        value = string.Empty;
        contentIndex = -1;
        var verbatim = false;
        if (index + 1 < line.Length && line[index] == '@' && line[index + 1] == '"')
        {
            verbatim = true;
            index++;
        }
        else if (index < line.Length && line[index] == '$')
        {
            return false;
        }

        if (index >= line.Length || line[index] != '"')
            return false;

        contentIndex = index + 1;
        index++;
        var builder = new StringBuilder();
        while (index < line.Length)
        {
            var c = line[index];
            if (c == '"')
            {
                if (verbatim && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index += 2;
                    continue;
                }

                index++;
                value = builder.ToString();
                return true;
            }

            if (!verbatim && c == '\\' && index + 1 < line.Length)
            {
                builder.Append(line[index + 1]);
                index += 2;
                continue;
            }

            builder.Append(c);
            index++;
        }

        return false;
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }

    private static bool IsValidCSharpReflectionSymbolName(string symbolName)
    {
        if (symbolName.Length == 0 || !IsCSharpIdentifierStart(symbolName[0]))
            return false;
        for (var i = 1; i < symbolName.Length; i++)
        {
            if (!IsCSharpIdentifierPart(symbolName[i]))
                return false;
        }
        return true;
    }

    internal static void EmitCSharpTypePositionReferences(
        string preparedLine,
        string originalLine,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container,
        CSharpWhereConstraintState pendingWhereConstraint,
        ref CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern)
    {
        var csharpGenericParameterNames = CollectCSharpGenericParameterNamesForDeclaration(preparedLine);
        TryEmitCSharpBaseListReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, csharpGenericParameterNames);
        EmitCSharpWhereConstraintReferences(
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            csharpGenericParameterNames,
            pendingWhereConstraint);
        EmitDeclarationTypeReferences("csharp", preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, csharpGenericParameterNames);

        foreach (Match match in CSharpIsAsTypeTestRegex.Matches(preparedLine))
        {
            var typeGroup = match.Groups["type"];
            int continuationIndex = SkipWhitespace(preparedLine, typeGroup.Index + typeGroup.Length);
            if (TryEmitCSharpLogicalTypePatternHeads(
                    preparedLine,
                    typeGroup.Value,
                    typeGroup.Index,
                    continuationIndex,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate,
                    (logicalTypeExpression, logicalTypeIndex) => AddTypeExpressionSegments(
                        references,
                        seen,
                        fileId,
                        logicalTypeExpression,
                        logicalTypeIndex,
                        context,
                        lineNumber,
                        resolveContainerForColumn(logicalTypeIndex),
                        "csharp",
                        csharpGenericParameterNames)))
            {
                continue;
            }

            if (IsCSharpNonTypePatternExpression(typeGroup.Value)
                || IsCSharpConstantPatternMemberHead(
                    typeGroup.Value,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate)
                || IsCSharpLogicalConstantPatternAtCursor(
                    preparedLine,
                    typeGroup.Value,
                    continuationIndex,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate))
            {
                continue;
            }

            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                typeGroup.Value,
                typeGroup.Index,
                context,
                lineNumber,
                resolveContainerForColumn(typeGroup.Index),
                "csharp",
                csharpGenericParameterNames);
        }

        EmitCSharpCaseTypePatternReferences(
            preparedLine,
            originalLine,
            csharpQualifiedConstantPatternMemberLookup,
            csharpQualifiedTypePatternLookup,
            csharpUsingAliases,
            csharpUsingStatics,
            hasActiveSameFileCSharpTypeCandidate,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            ref pendingCSharpMultiLineTypePattern);
    }

    internal static void AdvanceCSharpMultiLineTypePatternState(
        string preparedLine,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        ref CSharpMultiLineTypePatternState state)
    {
        if (!state.WaitingForHead && state.PendingTypeExpression == null)
            return;

        var cursor = SkipWhitespace(preparedLine, 0);
        if (state.WaitingForHead)
        {
            if (!TryConsumeCSharpMultiLineTypePatternHead(
                    preparedLine,
                    context,
                    lineNumber,
                    resolveContainerForColumn,
                    ref cursor,
                    ref state))
            {
                if (IsStandaloneCSharpMultiLinePatternNegation(preparedLine))
                    return;

                state = default;
                return;
            }
        }
        else if (!TryConsumeCSharpLogicalPatternKeyword(preparedLine, cursor, out cursor))
        {
            FlushPendingCSharpMultiLineTypePatternReference(
                ref state,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate,
                references,
                seen,
                fileId);
            return;
        }
        else
        {
            FlushPendingCSharpMultiLineTypePatternReference(
                ref state,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate,
                references,
                seen,
                fileId);
            if (!TryConsumeCSharpMultiLineTypePatternHead(
                    preparedLine,
                    context,
                    lineNumber,
                    resolveContainerForColumn,
                    ref cursor,
                    ref state))
            {
                state = state with { WaitingForHead = true };
                return;
            }
        }

        while (TryConsumeCSharpLogicalPatternKeyword(
            preparedLine,
            SkipWhitespace(preparedLine, state.PendingTypeIndex + state.PendingTypeExpression!.Length),
            out cursor))
        {
            FlushPendingCSharpMultiLineTypePatternReference(
                ref state,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate,
                references,
                seen,
                fileId);
            if (!TryConsumeCSharpMultiLineTypePatternHead(
                    preparedLine,
                    context,
                    lineNumber,
                    resolveContainerForColumn,
                    ref cursor,
                    ref state))
            {
                state = state with { WaitingForHead = true };
                return;
            }
        }
    }

    private static bool TryConsumeCSharpMultiLineTypePatternHead(
        string preparedLine,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        ref int cursor,
        ref CSharpMultiLineTypePatternState state)
    {
        cursor = SkipWhitespace(preparedLine, cursor);
        if (TryConsumeCSharpPatternKeyword(preparedLine, ref cursor, "not"))
            cursor = SkipWhitespace(preparedLine, cursor);

        var match = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, cursor);
        if (!match.Success)
            return false;

        var typeGroup = match.Groups["type"];
        state = new CSharpMultiLineTypePatternState(
            WaitingForHead: false,
            PendingTypeExpression: typeGroup.Value,
            PendingTypeIndex: typeGroup.Index,
            PendingTypeLineNumber: lineNumber,
            PendingContext: context,
            PendingContainer: resolveContainerForColumn(typeGroup.Index));
        cursor = SkipWhitespace(preparedLine, typeGroup.Index + typeGroup.Length);
        return true;
    }

    internal static void FlushPendingCSharpMultiLineTypePatternReference(
        ref CSharpMultiLineTypePatternState state,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId)
    {
        if (state.PendingTypeExpression == null || state.PendingContext == null)
        {
            state = default;
            return;
        }

        if (!IsCSharpNonTypePatternExpression(state.PendingTypeExpression)
            && !IsCSharpConstantPatternMemberHead(
                state.PendingTypeExpression,
                state.PendingTypeLineNumber,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate))
        {
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                state.PendingTypeExpression,
                state.PendingTypeIndex,
                state.PendingContext,
                state.PendingTypeLineNumber,
                state.PendingContainer,
                "csharp");
        }

        state = default;
    }

    private static bool IsStandaloneCSharpMultiLinePatternNegation(string preparedLine)
    {
        var cursor = SkipWhitespace(preparedLine, 0);
        if (!TryConsumeCSharpPatternKeyword(preparedLine, ref cursor, "not"))
            return false;

        return SkipWhitespace(preparedLine, cursor) >= preparedLine.Length;
    }

    internal static void StartWaitingForCSharpMultiLineTypePatternHead(ref CSharpMultiLineTypePatternState state)
    {
        state = new CSharpMultiLineTypePatternState(
            WaitingForHead: true,
            PendingTypeExpression: null,
            PendingTypeIndex: 0,
            PendingTypeLineNumber: 0,
            PendingContext: null,
            PendingContainer: null);
    }

    private static void EmitCSharpCaseTypePatternReferences(
        string preparedLine,
        string originalLine,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        ref CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern)
    {
        foreach (Match caseMatch in CSharpCaseLabelRegex.Matches(preparedLine))
        {
            int cursor = SkipWhitespace(preparedLine, caseMatch.Index + caseMatch.Length);
            bool hadLeadingNot = TryConsumeCSharpPatternKeyword(preparedLine, ref cursor, "not");
            if (hadLeadingNot)
                cursor = SkipWhitespace(preparedLine, cursor);

            var typeMatch = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, cursor);
            if (!typeMatch.Success)
            {
                var rawCaseCursor = SkipCSharpTriviaForward(originalLine, caseMatch.Index + caseMatch.Length);
                if (TryConsumeLeadingCSharpPatternKeyword(originalLine, ref rawCaseCursor, "not"))
                    rawCaseCursor = SkipCSharpTriviaForward(originalLine, rawCaseCursor);

                if (HasOnlyTrailingCSharpTrivia(originalLine, rawCaseCursor))
                    StartWaitingForCSharpMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
                continue;
            }

            var typeGroup = typeMatch.Groups["type"];
            var currentTypeExpression = typeGroup.Value;
            var currentTypeIndex = typeGroup.Index;
            var currentContinuationIndex = SkipWhitespace(preparedLine, typeGroup.Index + typeGroup.Length);
            var sawLogicalKeyword = false;
            var waitingForNextHead = false;

            while (TryConsumeCSharpLogicalPatternKeyword(preparedLine, currentContinuationIndex, out var nextHeadCursor))
            {
                sawLogicalKeyword = true;
                if (!IsCSharpLogicalConstantPatternHead(
                        preparedLine,
                        currentTypeExpression,
                        nextHeadCursor,
                        lineNumber,
                        csharpQualifiedConstantPatternMemberLookup,
                        csharpQualifiedTypePatternLookup,
                        csharpUsingAliases,
                        csharpUsingStatics,
                        hasActiveSameFileCSharpTypeCandidate))
                {
                    AddTypeExpressionSegments(
                        references,
                        seen,
                        fileId,
                        currentTypeExpression,
                        currentTypeIndex,
                        context,
                        lineNumber,
                        resolveContainerForColumn(currentTypeIndex),
                        "csharp");
                }

                int nextTypeCursor = nextHeadCursor;
                if (TryConsumeCSharpPatternKeyword(preparedLine, ref nextTypeCursor, "not"))
                    nextTypeCursor = SkipWhitespace(preparedLine, nextTypeCursor);

                var nextMatch = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, nextTypeCursor);
                if (!nextMatch.Success)
                {
                    var rawNextTypeCursor = SkipCSharpTriviaForward(originalLine, nextHeadCursor);
                    if (TryConsumeLeadingCSharpPatternKeyword(originalLine, ref rawNextTypeCursor, "not"))
                        rawNextTypeCursor = SkipCSharpTriviaForward(originalLine, rawNextTypeCursor);

                    if (HasOnlyTrailingCSharpTrivia(originalLine, rawNextTypeCursor))
                    {
                        StartWaitingForCSharpMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
                        waitingForNextHead = true;
                    }
                    break;
                }

                var nextTypeGroup = nextMatch.Groups["type"];
                currentTypeExpression = nextTypeGroup.Value;
                currentTypeIndex = nextTypeGroup.Index;
                currentContinuationIndex = SkipWhitespace(preparedLine, currentTypeIndex + currentTypeExpression.Length);
            }

            if (waitingForNextHead)
                continue;

            if (sawLogicalKeyword)
            {
                if (!IsCSharpNonTypePatternExpression(currentTypeExpression)
                    && !IsCSharpConstantPatternMemberHead(
                        currentTypeExpression,
                        lineNumber,
                        csharpQualifiedConstantPatternMemberLookup,
                        csharpUsingAliases,
                        csharpUsingStatics,
                        hasActiveSameFileCSharpTypeCandidate))
                {
                    AddTypeExpressionSegments(
                        references,
                        seen,
                        fileId,
                        currentTypeExpression,
                        currentTypeIndex,
                        context,
                        lineNumber,
                        resolveContainerForColumn(currentTypeIndex),
                        "csharp");
                }

                continue;
            }

            if (!IsCSharpCaseTypePatternContinuation(
                    preparedLine,
                    currentTypeExpression,
                    currentContinuationIndex,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate,
                    lineNumber))
            {
                continue;
            }

            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                currentTypeExpression,
                currentTypeIndex,
                context,
                lineNumber,
                resolveContainerForColumn(currentTypeIndex),
                "csharp");
        }
    }

    private static bool HasOnlyTrailingCSharpTrivia(string text, int cursor)
    {
        while (cursor < text.Length)
        {
            if (char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
                continue;
            }

            if (cursor + 1 < text.Length
                && text[cursor] == '/'
                && text[cursor + 1] == '/')
            {
                return true;
            }

            if (cursor + 1 < text.Length
                && text[cursor] == '/'
                && text[cursor + 1] == '*')
            {
                var commentEnd = text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return true;

                cursor = commentEnd + 2;
                continue;
            }

            return false;
        }

        return true;
    }

    private static int SkipCSharpTriviaForward(string text, int cursor)
    {
        while (cursor < text.Length)
        {
            if (char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
                continue;
            }

            if (cursor + 1 < text.Length
                && text[cursor] == '/'
                && text[cursor + 1] == '/')
            {
                return text.Length;
            }

            if (cursor + 1 < text.Length
                && text[cursor] == '/'
                && text[cursor + 1] == '*')
            {
                var commentEnd = text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return text.Length;

                cursor = commentEnd + 2;
                continue;
            }

            break;
        }

        return cursor;
    }

    private static bool TryConsumeLeadingCSharpPatternKeyword(string text, ref int cursor, string keyword)
    {
        if (string.IsNullOrEmpty(keyword))
            return false;

        cursor = SkipCSharpTriviaForward(text, cursor);
        if (cursor + keyword.Length > text.Length
            || !text.AsSpan(cursor, keyword.Length).Equals(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        var nextIndex = cursor + keyword.Length;
        if (nextIndex < text.Length
            && (char.IsLetterOrDigit(text[nextIndex]) || text[nextIndex] == '_'))
        {
            return false;
        }

        cursor = nextIndex;
        return true;
    }

    private sealed class BuiltInReferenceExtractor(string language) : IReferenceExtractor
    {
        public string Language { get; } = language;

        public List<ReferenceRecord> Extract(ReferenceExtractionContext request)
        {
            if (!string.Equals(request.Language, Language, StringComparison.Ordinal))
                throw new ArgumentException($"Extractor for '{Language}' cannot handle '{request.Language}'.", nameof(request));

            return ExtractCore(request);
        }
    }


}
