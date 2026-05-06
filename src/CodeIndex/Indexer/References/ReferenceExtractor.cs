using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Extracts lightweight symbol references such as call sites.
/// 軽量なシンボル参照（呼び出し箇所など）を抽出する。
/// </summary>
public static partial class ReferenceExtractor
{
    internal readonly record struct CSharpMultiLineTypePatternState(
        bool WaitingForHead,
        string? PendingTypeExpression,
        int PendingTypeIndex,
        int PendingTypeLineNumber,
        string? PendingContext,
        SymbolRecord? PendingContainer);
    private static readonly HashSet<string> SupportedLanguages =
    [
        "python", "javascript", "typescript", "csharp", "go", "rust",
        "java", "kotlin", "ruby", "perl", "c", "cpp", "php", "swift",
        "dart", "scala", "elixir", "lua", "commonlisp", "racket", "vb", "fsharp", "sql", "cobol", "batch",
        "assembly",
        "r", "powershell", "shell", "haskell",
        "gradle", "terraform", "protobuf", "dockerfile", "makefile",
        "zig", "css", "fortran", "pascal", "objc", "smalltalk"
    ];


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
            "raise", "yield", "from",
        },
        // Ruby contextual keywords / Ruby の文脈キーワード
        ["ruby"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "raise", "yield", "super", "include", "extend",
            "unless", "case", "begin", "until", "module", "rescue", "ensure",
        },
        // F# contextual keywords / F# 文脈キーワード
        ["fsharp"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "match", "with", "member", "override", "abstract", "mutable", "rec", "fun", "open",
            "module", "type", "of", "then", "elif", "done", "begin", "end",
            "let", "use", "if", "else", "do", "try", "finally", "in", "for", "while", "return", "yield",
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
        },
        // PowerShell keywords / PowerShell キーワード
        ["powershell"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "param", "begin", "process", "Write", "trap", "finally", "elseif",
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
            "Call", "Case", "Catch", "DirectCast", "End", "Erase", "Exit", "Get", "GetType",
            "GetXMLNamespace", "Global", "Handles", "Inherits", "Implements", "Imports", "Me",
            "Module", "MustInherit", "MustOverride", "MyBase", "MyClass", "Namespace", "Narrowing",
            "New", "Next", "Not", "Nothing", "Of", "On", "Operator", "Option", "Or", "OrElse",
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
    private static readonly Regex InlineBlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Compiled);
    internal const string CSharpIdentifierPattern = @"@?[_\p{L}]\w*";
    private const string FunctionalIdentifierPattern = @"@?[_\p{L}\$][\w$]*";
    private const string CSharpTypeExpressionPattern =
        @"(?:global::)?(?:"
        + CSharpIdentifierPattern
        + @"\s*(?:(?:\.|::)\s*"
        + CSharpIdentifierPattern
        + @")*)(?:\s*<[^)\];{}]+>)?(?:\s*\[[^\]\n]*\])*";
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
    // C# type tests (`o is Base`, `o is not Base`, `o as Base`).
    // `is` / `is not` / `as` の型位置 (`o is Base`, `o is not Base`, `o as Base`)。
    private static readonly Regex CSharpIsAsTypeTestRegex = new(
        $@"(?<![\w$])(?:is\s+(?:not\s+)?|as\s+)(?<type>{CSharpTypeExpressionPattern})",
        RegexOptions.Compiled);
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
        RegexOptions.Compiled);
    // C# XML-doc cross-reference (`<see cref="Base.Do"/>`, `<seealso cref="ILogger.Log"/>`).
    // C# XML doc の `<see cref="Base.Do"/>` / `<seealso cref="ILogger.Log"/>`。
    private static readonly Regex CSharpDocCrefRegex = new(
        @"<(?:see|seealso)\s+cref\s*=\s*""(?<cref>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
    private static readonly Dictionary<string, HashSet<string>> LanguageBuiltInTypeNames = new(StringComparer.Ordinal)
    {
        ["typescript"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "any", "bigint", "boolean", "false", "never", "null", "number", "object", "string",
            "symbol", "true", "undefined", "unknown", "void",
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
        },
        ["rust"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "Self", "bool", "char", "f32", "f64", "i8", "i16", "i32", "i64", "i128", "isize",
            "str", "u8", "u16", "u32", "u64", "u128", "usize",
        },
        ["c"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "bool", "char", "double", "float", "int", "long", "short", "signed", "size_t",
            "ssize_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t", "unsigned", "void",
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
        => SupportedLanguages.Concat(new[] { "vue", "svelte", "razor", "blazor", "cshtml" }).ToArray();

    private static string? NormalizeLanguage(string? lang)
        => lang is "vue" or "svelte"
            ? "typescript"
            : lang is "razor" or "blazor" or "cshtml"
                ? "csharp"
                : lang;

    public static bool SupportsLanguage(string? lang) =>
        NormalizeLanguage(lang) is string normalized && SupportedLanguages.Contains(normalized);

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

    /// <summary>
    /// Extract indexed references for supported languages.
    /// 対応言語向けにインデックス化する参照を抽出する。
    /// </summary>
    public static List<ReferenceRecord> Extract(
        long fileId,
        string? lang,
        string content,
        IReadOnlyList<SymbolRecord> symbols,
        string? path = null)
    {
        var requestedLanguage = lang;
        if (!SupportsLanguage(lang))
            return [];

        lang = NormalizeLanguage(lang);
        var language = lang!;
        var isJsxFile = IsJsxFilePath(path);
        var isRazorFile = IsRazorFilePath(path) || requestedLanguage is "razor" or "blazor" or "cshtml";

        // Null / empty fast path — keep the direct-call null-safe contract that
        // FileIndexer.StripLineLeadingBom's IsNullOrEmpty check used to provide
        // before the CRLF normalization step was added in front of it. Closes #183.
        // null / 空入力は早期 return。CRLF 正規化を StripLineLeadingBom の前に
        // 入れたことで helper 側の IsNullOrEmpty による null 許容が効かなくなる
        // ため、direct call の null セーフ契約をここで復元する。Closes #183.
        if (string.IsNullOrEmpty(content))
            return [];

        // Normalize CRLF / CR to LF first so direct callers that bypass FileIndexer
        // still present a `\n`-only content stream, and then strip line-leading
        // UTF-8 BOM (U+FEFF) defensively so `^\s*`-anchored patterns match on
        // line 1 and on any mid-file line that begins with a BOM (e.g. from file
        // concatenation or tool insertion). StripLineLeadingBom assumes `\n` is
        // the sole line separator, so the CRLF pass must come first. Non-line-
        // leading U+FEFF is preserved so content with intentional ZWNBSP inside
        // a string literal stays verbatim. Closes #183.
        // まず CRLF / CR を LF に正規化する。StripLineLeadingBom は `\n` を唯一の
        // 行区切りとして行頭判定するので、FileIndexer を経由しない direct call
        // でも CRLF 正規化を済ませてから呼ばないと mid-file の行頭 BOM を剥がし
        // 損なう。続いて行頭 U+FEFF のみ剥がし、1 行目と mid-file の行頭 BOM 両方
        // で `^\s*` 固定パターンを成立させる。行頭以外の U+FEFF (文字列リテラル中
        // の意図的な ZWNBSP 等) はそのまま保持する。Closes #183.
        if (content.Contains('\r'))
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");
        content = FileIndexer.StripLineLeadingBom(content);
        var maskedContent = string.Equals(language, "java", StringComparison.OrdinalIgnoreCase)
            ? MaskJavaTextBlocks(content)
            : content;
        var lines = maskedContent.Split('\n');
        var structuralLines = StructuralLineMasker.MaskLines(language, lines, out var jsTaggedTemplateHits);
        var csharpLinesInsideMultilineStringContent = language == "csharp"
            ? BuildCSharpMultilineStringContentLines(lines)
            : null;
        var csharpLinesInsideBlockComment = language == "csharp"
            ? BuildCSharpBlockCommentLines(lines)
            : null;
        var referenceStructuralLines = language == "pascal"
            ? MaskPascalBlockCommentLines(structuralLines)
            : language == "haskell"
                ? MaskHaskellBlockCommentLines(structuralLines)
                : UsesCStyleBlockComments(language)
                    ? MaskCStyleBlockCommentLines(language, structuralLines)
                    : structuralLines;
        var preparedLines = new string[lines.Length];
        for (var pi = 0; pi < lines.Length; pi++)
            preparedLines[pi] = PrepareLine(language, referenceStructuralLines[pi]);
        var goImportBlockLines = language == "go"
            ? GoReferenceExtractor.BuildImportBlockLineMap(lines)
            : null;
        var luaReferenceLines = language == "lua"
            ? LuaReferenceExtractor.MaskLongCommentAndStringLines(lines)
            : null;
        var lispReferenceLines = language is "commonlisp" or "racket"
            ? SymbolExtractor.MaskLispCodeLines(lines)
            : null;
        string[]? luaPreparedLines = null;
        if (luaReferenceLines != null)
        {
            luaPreparedLines = new string[luaReferenceLines.Length];
            for (var pi = 0; pi < luaReferenceLines.Length; pi++)
                luaPreparedLines[pi] = PrepareLine(language, luaReferenceLines[pi]);
        }
        var razorReferenceLines = isRazorFile
            ? RazorReferenceExtractor.MaskCommentLines(lines)
            : null;
        // Group JS/TS tagged template call sites by line for O(1) lookup in the per-line loop.
        // Tagged templates like `gql\`...\`` / `styled.div\`...\`` / `sql\`...${x}...\`` have no
        // trailing `(`, so CallRegex cannot see them. The structural masker already identifies
        // template openers while walking JS/TS token state, and emits one hit per opener with
        // the preceding tag identifier.
        // JS/TS のタグ付きテンプレート呼び出し位置を行番号でグループ化し、ループ中の参照追加で即座に拾えるようにする。
        // `gql\`...\`` / `styled.div\`...\`` / `sql\`...${x}...\`` は末尾 `(` がなく CallRegex で取れないが、
        // 構造マスカーがテンプレート opener 検出時に先行する tag 識別子を併せて記録する。
        Dictionary<int, List<JsTaggedTemplateHit>>? jsTaggedTemplatesByLine = null;
        if (jsTaggedTemplateHits != null && jsTaggedTemplateHits.Count > 0)
        {
            jsTaggedTemplatesByLine = new Dictionary<int, List<JsTaggedTemplateHit>>();
            foreach (var hit in jsTaggedTemplateHits)
            {
                if (!jsTaggedTemplatesByLine.TryGetValue(hit.Line, out var bucket))
                {
                    bucket = new List<JsTaggedTemplateHit>();
                    jsTaggedTemplatesByLine[hit.Line] = bucket;
                }
                bucket.Add(hit);
            }
        }
        // Pre-pass C# attribute analysis so cross-line `[\n Foo("x")\n]` and parameter
        // attributes `void M([Attr] T x)` are classified consistently with same-line `[Foo]`.
        // 行を跨いだ `[\n Foo("x")\n]` やパラメータ属性 `void M([Attr] T x)` も、同一行の `[Foo]` と
        // 同じ判定で属性として扱えるように、事前パスで C# 属性セクションの範囲を構築する。
        var csharpAttrTables = language == "csharp"
            ? BuildCSharpAttributeRanges(preparedLines)
            : (null, null);
        var csharpAttrRanges = csharpAttrTables.Item1;
        // Top-level (paren-depth 0) zones inside attribute sections. Used by the no-arg
        // attribute regex so that enum / qualified-constant identifiers appearing inside
        // attribute argument lists (e.g. `AllowNumbers` in `[JsonConverter(ConverterStrategy.AllowNumbers)]`)
        // are not misclassified as no-arg attribute references.
        // 属性セクション内で paren 深さ 0 の top-level ゾーンだけを別テーブルで持つ。複数行
        // `[...]` の引数中に現れる enum / 修飾定数（`ConverterStrategy.AllowNumbers` など）が
        // no-arg attribute として誤分類されないよう、no-arg 属性用ゲートに使う。
        var csharpAttrTopLevelRanges = csharpAttrTables.Item2;
        var definitionNamesComparer = language == "sql"
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var definitionNamesByLine = symbols
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
        var sqlDefinitionLeafSpansByLine = language == "sql"
            ? SqlReferenceExtractor.BuildDefinitionLeafSpansByLine(lines, symbols)
            : null;
        var cobolCallableSymbols = language == "cobol"
            ? symbols
                .Where(symbol => symbol.Kind == "function")
                .OrderBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.StartLine)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : null;
        // Include 'property' so expression-bodied and block-bodied property accessors
        // attribute their calls to the property rather than falling through to the
        // enclosing class (see issue #233).
        // 式本体・ブロック本体のプロパティアクセサ内の呼び出しを、外側のクラスではなく
        // プロパティ自身に帰属させる (issue #233 参照)。
        var containerCandidates = symbols
            .Where(symbol => symbol.BodyStartLine != null && symbol.BodyEndLine != null &&
                              (symbol.Kind == "function" || symbol.Kind == "class"
                               || symbol.Kind == "struct" || symbol.Kind == "namespace"
                               || symbol.Kind == "property"))
            .OrderBy(symbol => (symbol.BodyEndLine ?? symbol.EndLine) - (symbol.BodyStartLine ?? symbol.StartLine))
            .ToList();
        var csharpXmlDocAttachmentScopeCandidates = language == "csharp"
            ? symbols
                .Where(symbol => symbol.BodyStartLine != null && symbol.BodyEndLine != null
                                 && symbol.Kind is "class" or "struct" or "interface" or "enum" or "namespace")
                .OrderBy(symbol => (symbol.BodyEndLine ?? symbol.EndLine) - (symbol.BodyStartLine ?? symbol.StartLine))
                .ToList()
            : null;
        // Enclosing-type candidates for constructor-chain rewrites (class/struct/record; namespace excluded).
        // Ordered innermost-first via ascending body range. Java enums can declare constructors and
        // chain via `this(...)` so `enum` is included; C# enums cannot declare constructors, and
        // `CSharpCtorChainRegex` will not match inside them, so including `enum` is a no-op there.
        // コンストラクタ連鎖の呼び先解決で使う外側の型候補（class/struct/record/enum。namespace は含めない）。
        // 内側優先で昇順にソート。Java の enum は `this(...)` 連鎖を持てるため `enum` も含める。
        // C# の enum はコンストラクタ自体を持てず `CSharpCtorChainRegex` が一致しないので副作用は無い。
        var enclosingTypeCandidates = symbols
            .Where(symbol => symbol.BodyStartLine != null && symbol.BodyEndLine != null &&
                             (symbol.Kind == "class" || symbol.Kind == "struct" || symbol.Kind == "interface" || symbol.Kind == "enum"))
            .OrderBy(symbol => (symbol.BodyEndLine ?? symbol.EndLine) - (symbol.BodyStartLine ?? symbol.StartLine))
            .ToList();

        // Synthetic function-kind container for C# primary-ctor declarations with a base
        // primary-ctor call such as `record Child(int x) : Parent(x)` or C# 12 `class Child(int x) : Parent(x)`.
        // The range spans the entire declaration header so multi-line forms where `: Parent(x)` sits on a
        // later line are covered. Later lines inside the body keep their real innermost containers.
        // C# のプライマリコンストラクタ宣言（record / class / struct）で base primary-ctor を呼んでいる場合、
        // 宣言ヘッダー全体を合成コンテナで上書きする。`{` / `;` 以降の本体行は通常の container に戻す。
        var recordPrimaryCtorRanges = BuildCSharpPrimaryCtorContainers(language, symbols, structuralLines);
        var csharpQualifiedEnumMemberLookup = BuildCSharpQualifiedEnumMemberLookup(language, symbols);
        var csharpQualifiedConstantPatternMemberLookup = BuildCSharpQualifiedConstantPatternMemberLookup(language, symbols);
        var csharpQualifiedTypePatternLookup = BuildCSharpQualifiedTypePatternLookup(language, symbols);
        var csharpKnownTypeNames = BuildCSharpKnownTypeNames(language, symbols);
        var callableDefinitionNames = BuildCallableDefinitionNames(language, symbols);
        var dockerfileStageNames = DockerfileReferenceExtractor.BuildStageNames(language, symbols);
        var shellCallableNames = ShellReferenceExtractor.BuildCallableNames(language, symbols);
        var shellGlobalAliasNames = ShellReferenceExtractor.BuildGlobalAliasNames(language, symbols);
        var csharpUsingAliases = BuildCSharpUsingAliases(language, symbols, csharpKnownTypeNames);
        var csharpUsingStatics = BuildCSharpUsingStatics(language, symbols);
        var csharpValueReceiverNames = BuildCSharpValueReceiverNamesByContainingType(language, symbols);
        var csharpFunctionValueReceiverNames = BuildCSharpValueReceiverNamesByFunctionStartLine(
            language,
            symbols,
            structuralLines,
            csharpKnownTypeNames,
            csharpUsingAliases);
        // Workspace-wide same-name type rescue needs cross-file visibility, so the
        // extractor leaves ambiguous unqualified using-static pattern heads for the
        // read path to disambiguate.
        // ワークスペース全体の同名型 rescue には cross-file 可視性が必要なため、
        // extractor は曖昧な unqualified using-static pattern head を残し、
        // read path 側で判定させる。
        bool HasActiveSameFileCSharpTypeCandidate(string typeExpression, int lineNumber)
        {
            _ = lineNumber;
            var normalized = NormalizeCSharpAliasTargetForTypeLookup(typeExpression);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            normalized = TrimLeadingCSharpGlobalQualifier(normalized);
            if (csharpKnownTypeNames.Contains(normalized))
                return true;

            var shortName = GetLastQualifiedSegment(normalized);
            return csharpUsingAliases.Any(alias =>
                alias.TargetsType
                && alias.Line <= lineNumber
                && lineNumber >= alias.ScopeStartLine
                && lineNumber <= alias.ScopeEndLine
                && string.Equals(alias.AliasName, shortName, StringComparison.Ordinal));
        }

        var references = new List<ReferenceRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pendingCSharpMultiLineTypePattern = default(CSharpMultiLineTypePatternState);
        var sqlState = language == "sql" ? SqlReferenceExtractor.CreateState() : null;
        var csharpInDelimitedDocComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var originalLine = lines[i];
            var preparedLine = luaPreparedLines?[i] ?? lispReferenceLines?[i] ?? preparedLines[i];
            var csharpAttrRangesOnLine = csharpAttrRanges?[i];
            var csharpAttrTopLevelOnLine = csharpAttrTopLevelRanges?[i];
            if (language == "csharp"
                && !(csharpLinesInsideMultilineStringContent?[i] ?? false)
                && TryGetCSharpXmlDocCommentSpan(
                    originalLine,
                    csharpInDelimitedDocComment,
                    csharpLinesInsideBlockComment?[i] ?? false,
                    out var csharpDocCommentStartIndex,
                    out var csharpDocCommentEndExclusive,
                    out var nextCsharpDelimitedDocComment))
            {
                var csharpDocCommentText = originalLine[csharpDocCommentStartIndex..csharpDocCommentEndExclusive];
                if (csharpDocCommentText.IndexOf("cref=\"", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var innermostContainer = FindInnermostContainer(containerCandidates, lineNumber);
                    var sameLineDeclarationStartColumn = GetCSharpSameLineDocumentedDeclarationStartColumn(
                        originalLine,
                        csharpDocCommentEndExclusive,
                        nextCsharpDelimitedDocComment);
                    var docContainer = FindDocumentedContainer(
                        containerCandidates,
                        structuralLines[i],
                        preparedLine,
                        csharpAttrRangesOnLine,
                        lineNumber,
                        sameLineDeclarationStartColumn);
                    if (docContainer != null
                        && (docContainer.StartLine == lineNumber
                            || CanAttachCSharpXmlDocCommentToNextDeclaration(
                                innermostContainer,
                                csharpXmlDocAttachmentScopeCandidates,
                                csharpAttrRanges,
                                preparedLines,
                                lineNumber,
                                docContainer)))
                    {
                        CSharpReferenceExtractor.EmitDocCrefReferences(
                            csharpDocCommentText,
                            references,
                            seen,
                            fileId,
                            csharpDocCommentStartIndex,
                            csharpDocCommentText.Trim(),
                            lineNumber,
                            docContainer);
                    }
                }
                csharpInDelimitedDocComment = nextCsharpDelimitedDocComment;
            }

            if (string.IsNullOrWhiteSpace(preparedLine))
            {
                if (language == "csharp"
                    && (pendingCSharpMultiLineTypePattern.WaitingForHead
                        || pendingCSharpMultiLineTypePattern.PendingTypeExpression != null))
                {
                    continue;
                }

                if (language == "csharp")
                    CSharpReferenceExtractor.FlushPendingMultiLineTypePatternReference(
                        ref pendingCSharpMultiLineTypePattern,
                        csharpQualifiedConstantPatternMemberLookup,
                        csharpUsingAliases,
                        csharpUsingStatics,
                        HasActiveSameFileCSharpTypeCandidate,
                        references,
                        seen,
                        fileId);
                continue;
            }

            var context = originalLine.Trim();
            if (context.Length == 0)
                continue;

            var definitionNames = definitionNamesByLine.TryGetValue(lineNumber, out var namesOnLine)
                ? namesOnLine
                : null;
            Dictionary<string, int>? definitionNameIndices = null;
            if (definitionNames != null && language != "sql")
            {
                definitionNameIndices = new Dictionary<string, int>(definitionNamesComparer);
                foreach (var definitionName in definitionNames)
                {
                    var definitionIndex = preparedLine.IndexOf(definitionName, StringComparison.Ordinal);
                    if (definitionIndex >= 0)
                        definitionNameIndices[definitionName] = definitionIndex;
                }
            }
            List<SqlReferenceExtractor.DefinitionLeafSpan>? sqlDefinitionLeafSpans = null;
            if (language == "sql")
                sqlDefinitionLeafSpansByLine?.TryGetValue(lineNumber, out sqlDefinitionLeafSpans);
            var container = FindInnermostContainer(containerCandidates, lineNumber);

            // Per-line Java same-line ctor synthesis. When `public Leaf(){super(0); doWork();}`
            // is entirely on one line, SymbolExtractor does not emit a function symbol for the
            // ctor (its method regex requires the line to end with `{`), so `FindInnermostContainer`
            // returns the enclosing `class:Leaf`. Body-level calls such as `doWork()` would then
            // attach to the class rather than the ctor. We pre-compute a synthetic function-kind
            // container covering the body `{ ... }` region on the current line, so those calls
            // land on `function:Leaf` and `callers Leaf` reflects what the ctor actually does.
            // 同一行 ctor は function symbol が作られないため、body `{ ... }` 内の通常 call が
            // 外側クラスに吸われてしまう。合成 function コンテナを per-line で構築して差し替える。
            (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex, int CloseBraceIndex)? javaSameLineCtor = null;
            if (language == "java")
            {
                javaSameLineCtor = JavaReferenceExtractor.TryBuildSameLineCtorSpan(
                    preparedLine,
                    lineNumber,
                    enclosingTypeCandidates);
            }

            // Per-call-site record primary-ctor override: only calls whose column sits inside the
            // record header (not in a braced body on the same line) should land on the synthetic
            // ctor. Overriding `container` for the whole line would steal body-level calls such as
            // `record Child(int V) : Parent(V) { public int Sum() => Add(V, 1); }` where `Add(...)`
            // lives past the header-terminating `{` and must stay with its real innermost container.
            // 同一行 record で `{` より後ろの本体呼び出しまで合成 ctor に奪われないよう、コール単位で
            // ヘッダ範囲（end line の end column より前）に入っているかを判定して差し替える。
            SymbolRecord? ResolveContainerForCall(int column)
            {
                foreach (var (rangeStart, rangeStartColumn, rangeEnd, rangeEndColumn, syntheticRecordCtor) in recordPrimaryCtorRanges)
                {
                    if (lineNumber < rangeStart || lineNumber > rangeEnd)
                        continue;
                    if (lineNumber == rangeStart && column < rangeStartColumn)
                        continue;
                    if (lineNumber == rangeEnd && column >= rangeEndColumn)
                        continue;
                    return syntheticRecordCtor;
                }

                // Java same-line ctor body override: calls whose column sits strictly inside the
                // `{ ... }` block on the ctor declaration line attach to the synthetic function-kind
                // ctor instead of the enclosing class container. When no matching `}` is found on
                // the same line (CloseBraceIndex < 0), the body extends beyond the current line —
                // in that case SymbolExtractor emits a real ctor function symbol (its regex matches
                // because the line ends with `{`), so this override is only needed for the fully
                // same-line shape where the matching `}` exists on the same line.
                // Java の same-line ctor では `{ ... }` 内の call を合成 function コンテナに振り向ける。
                if (javaSameLineCtor != null)
                {
                    var info = javaSameLineCtor.Value;
                    if (info.CloseBraceIndex >= 0
                        && column > info.OpenBraceIndex
                        && column < info.CloseBraceIndex)
                    {
                        return info.Synthetic;
                    }
                }

                if (language == "csharp")
                {
                    var sameLineContainer = FindInnermostSameLineCSharpContainer(
                        containerCandidates,
                        structuralLines[i],
                        lineNumber,
                        column);
                    if (sameLineContainer != null)
                        return sameLineContainer;
                }

                return container;
            }

            if (isJsxFile && (language is "javascript" or "typescript"))
            {
                foreach (Match match in JsxElementOpenRegex.Matches(preparedLine))
                {
                    var fullName = match.Groups["name"].Value;
                    var nameIndex = match.Groups["name"].Index;
                    var jsxContainer = ResolveContainerForCall(nameIndex);
                    var firstDotIndex = fullName.IndexOf('.');

                    AddReference(
                        references,
                        seen,
                        fileId,
                        firstDotIndex < 0 ? fullName : fullName[..firstDotIndex],
                        nameIndex,
                        "call",
                        context,
                        lineNumber,
                        jsxContainer);

                    var dotIndex = fullName.LastIndexOf('.');
                    if (dotIndex > 0 && dotIndex + 1 < fullName.Length)
                    {
                        AddReference(
                            references,
                            seen,
                            fileId,
                            fullName[(dotIndex + 1)..],
                            nameIndex + dotIndex + 1,
                            "call",
                            context,
                            lineNumber,
                            jsxContainer);
                    }
                }
            }

            if (language == "csharp")
            {
                CSharpReferenceExtractor.AdvanceMultiLineTypePatternState(
                    preparedLine,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    HasActiveSameFileCSharpTypeCandidate,
                    references,
                    seen,
                    fileId,
                    ref pendingCSharpMultiLineTypePattern);
            }

              bool ShouldSuppressDefinitionCall(string resolvedName, int callIndex)
              {
                  if (definitionNames == null)
                      return false;

                  if (language == "csharp")
                  {
                      if (context.Contains("when", StringComparison.Ordinal))
                          return false;
                  }

                  if (language != "sql")
                      return definitionNameIndices != null
                          && definitionNameIndices.TryGetValue(resolvedName, out var definitionIndex)
                          && callIndex == definitionIndex;

                return SqlReferenceExtractor.ShouldSuppressDefinitionCall(sqlDefinitionLeafSpans, resolvedName, callIndex);
            }

            // Event subscription/unsubscription (C#) / イベント購読・解除 (C#)
            if (language is "csharp")
            {
                foreach (Match match in EventSubscriptionRegex.Matches(preparedLine))
                {
                    var eventContainer = ResolveContainerForCall(match.Groups["name"].Index);
                    AddReference(references, seen, fileId, match, "subscribe", context, lineNumber, eventContainer);
                }
            }

            // Constructor chain-call rewrites: C# `: this(...)` / `: base(...)` and Java `this(...)` / `super(...)`
            // コンストラクタ連鎖呼び出しの書き換え
            if (language is "csharp")
            {
                CSharpReferenceExtractor.EmitCtorChainReferences(
                    preparedLine, enclosingTypeCandidates, containerCandidates,
                    structuralLines, references, seen, fileId, context, lineNumber, container);
            }
            else if (language is "java")
            {
                JavaReferenceExtractor.EmitCtorChainReferences(
                    preparedLine, enclosingTypeCandidates, symbols, structuralLines,
                    references, seen, fileId, context, lineNumber, container);
            }

            // Compile-time type/member references that CallRegex cannot see because the
            // argument has no trailing `(` of its own. See issue #253.
            // 末尾の `(` を持たず CallRegex では取れないコンパイル時の型/メンバ参照。issue #253 参照。
            if (language is "csharp")
            {
                foreach (Match match in CSharpTypeKeywordIntroRegex.Matches(preparedLine))
                {
                    int parenIndex = match.Index + match.Length - 1; // position of '(' / '(' の位置
                    ExtractCSharpTypeKeywordSegments(
                        references, seen, fileId, preparedLine, parenIndex + 1,
                        context, lineNumber, container, language);
                }
            }
            else if (language is "java")
            {
                JavaReferenceExtractor.EmitDotClassTypeLiteralReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }

            // Type-position references without an introducing keyword-call: base lists,
            // declaration types, generic constraints, throws clauses, type tests, and
            // XML-doc crefs. These are dependency edges for `references` / `impact`, but
            // not invocation edges for default `callers` / `callees`. See issue #256.
            // キーワード呼び出しの外にある型位置参照（継承リスト、宣言型、generic 制約、
            // throws、型テスト、XML doc cref）。`references` / `impact` では依存として扱うが、
            // 既定の `callers` / `callees` では呼び出しエッジではない。issue #256 参照。
            if (language == "csharp")
            {
                CSharpReferenceExtractor.EmitTypePositionReferences(
                    preparedLine,
                    originalLine,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    HasActiveSameFileCSharpTypeCandidate,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    container,
                    ref pendingCSharpMultiLineTypePattern);

                if (CSharpReferenceExtractor.HasTrailingIsAsTypePatternIntro(preparedLine, originalLine))
                {
                    CSharpReferenceExtractor.StartWaitingForMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
                }

                if (CSharpReferenceExtractor.HasTrailingCaseTypePatternIntro(preparedLine, originalLine))
                {
                    CSharpReferenceExtractor.StartWaitingForMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
                }
            }
            else if (language == "java")
            {
                JavaReferenceExtractor.EmitModuleDirectiveReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);

                JavaReferenceExtractor.EmitTypePositionReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    container);
            }
            else if (language == "typescript")
            {
                TypeScriptReferenceExtractor.EmitTypePositionReferences(
                    preparedLines,
                    i,
                    preparedLine,
                    lines[i],
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);

                TypeScriptReferenceExtractor.EmitDeclarationTypeReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language == "kotlin")
            {
                KotlinReferenceExtractor.EmitTypePositionReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language == "swift")
            {
                SwiftReferenceExtractor.EmitTypePositionReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language == "rust")
            {
                RustReferenceExtractor.EmitTypePositionReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    container);
            }
            else if (language == "c")
                CReferenceExtractor.EmitTypePositionReferences(preparedLine, originalLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall);
            else if (language == "cpp")
                CppReferenceExtractor.EmitTypePositionReferences(preparedLine, originalLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall);
            else if (language == "go")
                GoReferenceExtractor.EmitTypePositionReferences(preparedLine, originalLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall, goImportBlockLines?[i] == true);
            else if (language == "dart")
                DartReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall);
            else if (language == "vb")
                VisualBasicReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall);
            else if (language == "fortran")
                FortranReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall, container);
            else if (language == "pascal")
                PascalReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall, container);
            else if (language == "objc")
                ObjectiveCReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall, container);
            else if (language == "haskell")
                HaskellReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
            else if (language == "elixir")
                ElixirReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
            else if (language == "lua")
                LuaReferenceExtractor.EmitTypePositionReferences(luaReferenceLines?[i] ?? originalLine, references, seen, fileId, context, lineNumber, container);
            else if (language == "css")
            {
                CssReferenceExtractor.EmitCss(
                    preparedLine,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    definitionNames,
                    container);
            }

            if (language == "terraform")
            {
                TerraformReferenceExtractor.Emit(
                    preparedLine,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    definitionNames,
                    container);
            }

            if (language == "dockerfile")
            {
                DockerfileReferenceExtractor.EmitStageReferences(
                    preparedLine,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    dockerfileStageNames,
                    container);
            }

            if (language == "cobol")
            {
                CobolReferenceExtractor.Emit(
                    lines[i],
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    container,
                    cobolCallableSymbols);
            }

            var sqlSuppressedCallIndices = language is "sql"
                ? SqlReferenceExtractor.Emit(
                    structuralLines[i],
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    sqlState!,
                    ResolveContainerForCall,
                    name => IsIgnoredCallName(language, name),
                    ShouldSuppressDefinitionCall)
                : null;

            if (language == "css")
            {
                CssReferenceExtractor.EmitScss(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }

            // C# / Java parenless initializers: `new T { ... }` / `new T<U> { ... }` /
            // `new T[] { ... }` etc. CallRegex requires a trailing `(`, so these forms slip
            // through and the type is otherwise never recorded as instantiated. Emit an
            // `instantiate` row here so `references` / `callers` / `impact` see the edge.
            // See issue #286.
            // 括弧省略の C# / Java インスタンス化 (`new T { ... }` 等) は CallRegex で拾えないため、
            // 専用パスで `instantiate` を発行する。issue #286 参照。
            if (language is "csharp" or "java")
            {
                var matchedInitializerIndices = new HashSet<int>();
                foreach (Match match in CSharpJavaInitializerRegex.Matches(preparedLine))
                {
                    var rawName = match.Groups["name"].Value;
                    var nameIndex = match.Groups["name"].Index;
                    matchedInitializerIndices.Add(nameIndex);
                    if (ShouldSkipInitializerName(language, rawName))
                        continue;
                    // Do NOT skip when the type is defined in the same file — the CallRegex
                    // `IsConstructorCallName` path emits `instantiate` without a definitionNames
                    // filter, so `new Foo { ... }` and `new Foo()` should behave the same way.
                    // 同一ファイル内定義でもスキップしない。`IsConstructorCallName` 経路の
                    // `instantiate` が同様の扱いをしているため、括弧あり/なしで挙動を揃える。
                    var initContainer = ResolveContainerForCall(nameIndex);
                    var name = language == "csharp" ? NormalizeCSharpIdentifier(rawName) : rawName;
                    AddReference(references, seen, fileId, name, nameIndex, "instantiate", context, lineNumber, initContainer);
                }

                // The initializer regex has the same one-level generic ceiling as CallRegex,
                // so nested generic targets like `new Dictionary<string, List<int>> { ... }`
                // need a depth-aware fallback to keep the outer `instantiate` edge.
                // initializer regex も CallRegex と同じく generic を 1 段までしか見ないため、
                // `new Dictionary<string, List<int>> { ... }` の外側型は depth-aware fallback
                // で補って `instantiate` を落とさないようにする。
                if (language == "csharp")
                {
                    foreach (var candidate in EnumerateNestedGenericInitializerCandidates(
                                 preparedLine,
                                 matchedInitializerIndices,
                                 requireOpeningBrace: true))
                    {
                        if (ShouldSkipInitializerName(language, candidate.Name))
                            continue;

                        var initContainer = ResolveContainerForCall(candidate.NameIndex);
                        AddReference(
                            references,
                            seen,
                            fileId,
                            candidate.Name,
                            candidate.NameIndex,
                            "instantiate",
                            context,
                            lineNumber,
                            initContainer);
                    }
                }

                // Allman-style multi-line form: `new T` at end of current line with the
                // opening `{` on the next non-blank prepared line. Peek forward to confirm
                // before emitting, so trailing `new T` patterns that are not followed by `{`
                // (e.g. `var a = new Foo\n;` or `var a = new Foo\n(1, 2);`) do not produce
                // phantom `instantiate` rows.
                // Allman スタイルの多行形式: 現在行末の `new T` と次の非空 prepared line 冒頭の
                // `{` を合わせて 1 つの instantiate として扱う。`{` が続かない場合（`;` や `(` が
                // 後続する等）には幻行を出さないため、peek で確認してから発行する。
                var trailingMatch = CSharpJavaInitializerTrailingRegex.Match(preparedLine);
                var peek = i + 1;
                while (peek < preparedLines.Length && string.IsNullOrWhiteSpace(preparedLines[peek]))
                    peek++;
                if (peek < preparedLines.Length)
                {
                    var nextContent = preparedLines[peek].TrimStart();
                    if (nextContent.Length > 0 && nextContent[0] == '{')
                    {
                        if (trailingMatch.Success)
                        {
                            var rawName = trailingMatch.Groups["name"].Value;
                            var nameIndex = trailingMatch.Groups["name"].Index;
                            matchedInitializerIndices.Add(nameIndex);
                            if (!ShouldSkipInitializerName(language, rawName))
                            {
                                var initContainer = ResolveContainerForCall(nameIndex);
                                var name = language == "csharp" ? NormalizeCSharpIdentifier(rawName) : rawName;
                                AddReference(references, seen, fileId, name, nameIndex, "instantiate", context, lineNumber, initContainer);
                            }

                        }

                        if (language == "csharp")
                        {
                            foreach (var candidate in EnumerateNestedGenericInitializerCandidates(
                                         preparedLine,
                                         matchedInitializerIndices,
                                         requireOpeningBrace: false))
                            {
                                if (ShouldSkipInitializerName(language, candidate.Name))
                                    continue;

                                var initContainer = ResolveContainerForCall(candidate.NameIndex);
                                var name = language == "csharp" ? NormalizeCSharpIdentifier(candidate.Name) : candidate.Name;
                                AddReference(
                                    references,
                                    seen,
                                    fileId,
                                    name,
                                    candidate.NameIndex,
                                    "instantiate",
                                    context,
                                    lineNumber,
                                    initContainer);
                            }
                        }
                    }
                }
            }

            if (language == "css")
            {
                CssReferenceExtractor.EmitScss(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }

            if (language == "php")
            {
                PhpReferenceExtractor.EmitStaticAccessReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                PhpReferenceExtractor.EmitObjectMemberAccessReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }

            if (language is "javascript" or "typescript")
            {
                JavaScriptReferenceExtractor.EmitParenlessConstructorReferences(
                    preparedLine,
                    preparedLines,
                    i,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }

            void AddCallLikeReference(string name, int callIndex)
            {
                var normalizedName = language == "fsharp" && FSharpReferenceExtractor.IsOperatorCallName(name)
                    ? $"operator {name}"
                    : language == "rust"
                        ? RustReferenceExtractor.NormalizeIdentifier(name)
                        : NormalizeAtPrefixedIdentifier(name);

                if (language == "rust" && RustReferenceExtractor.IsFunctionDeclarationCallSite(preparedLine, callIndex))
                    return;

                // Suppress the same-line Java ctor declarator's self-call. CallRegex matches
                // `CtorName(` at the declarator once per same-line ctor, but it is a declaration
                // site — not a call — so attributing it to `class:CtorName` produces a phantom
                // `CtorName|call|class|CtorName` edge. `definitionNames` does not cover this
                // because same-line ctors do not appear in the symbol table.
                // 同一行 ctor の宣言子 `CtorName(` は呼び出しではないため CallRegex の対象から除外する。
                if (javaSameLineCtor != null
                    && callIndex == javaSameLineCtor.Value.NameIndex
                    && string.Equals(normalizedName, javaSameLineCtor.Value.Synthetic.Name, StringComparison.Ordinal))
                {
                    return;
                }

                  // C# positional patterns such as `case Point(var x, var y):` are type-pattern
                  // heads, not calls. `CallRegex` still sees `Point(` and would otherwise emit a
                  // phantom `call` edge alongside the real `type_reference`.
                  // C# の positional pattern (`case Point(var x, var y):`) は型パターンの先頭であり、
                  // 呼び出しではない。`CallRegex` が `Point(` を拾ってしまうため、そのままだと
                  // 本物の `type_reference` に加えて phantom な `call` エッジが出る。
                  var isCSharpPatternHeadCallSite = language == "csharp"
                      && CSharpReferenceExtractor.IsPatternHeadCallSite(preparedLines, i, preparedLine, callIndex);
                  if (isCSharpPatternHeadCallSite)
                      return;

                var callContainer = ResolveContainerForCall(callIndex);
                if (IsConstructorCallName(language, preparedLine, callIndex))
                {
                    AddReference(references, seen, fileId, normalizedName, callIndex, "instantiate", context, lineNumber, callContainer);
                    return;
                }
                if (IsIgnoredCallName(language, name))
                {
                    if (!(language == "scala" && string.Equals(name, "foreach", StringComparison.Ordinal)))
                        return;
                }
                if (ShouldSuppressDefinitionCall(normalizedName, callIndex))
                    return;

                // issue #293: reclassify C# attribute / Java/Kotlin/Scala/TypeScript annotation
                // usages with arguments so they do not pollute the call-graph as phantom `call` rows.
                // issue #293: 引数付きの C# attribute と Java/Kotlin/Scala/TypeScript annotation 使用を
                // `call` ではなく専用の種別に分類し、call-graph の phantom エッジを防ぐ。
                var insideCSharpAttributeRange = csharpAttrRangesOnLine != null
                    && IsInsideCSharpAttributeRange(csharpAttrRangesOnLine, callIndex);
                var metadataKind = TryClassifyMetadataReference(language, preparedLine, callIndex, insideCSharpAttributeRange);
                AddReference(references, seen, fileId, normalizedName, callIndex, metadataKind ?? "call", context, lineNumber, callContainer);
            }

            if (language is "batch")
                BatchReferenceExtractor.EmitJumpTargetReferences(
                    originalLine,
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);

            if (language is "assembly")
                AssemblyReferenceExtractor.EmitInstructionTargetReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);

            var matchedCallIndices = new HashSet<int>();
            if (language is "commonlisp" or "racket")
            {
                LispReferenceExtractor.EmitReferences(
                    language,
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    definitionNames);
            }
            else if (language is "powershell")
            {
                PowerShellReferenceExtractor.EmitCallReferences(preparedLine, AddCallLikeReference);
            }
            else if (language is "shell")
            {
                ShellReferenceExtractor.EmitReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    shellCallableNames,
                    shellGlobalAliasNames,
                    ResolveContainerForCall,
                    AddCallLikeReference);
            }
            else if (language is "assembly")
            {
                // Assembly references are operand-driven, not `name(...)` call syntax. Running the
                // shared CallRegex would misread addressing forms such as `foo(%rip)` as calls.
            }
            else
            {
                foreach (Match match in CallRegex.Matches(preparedLine))
                {
                    var name = match.Groups["name"].Value;
                    var callIndex = match.Groups["name"].Index;
                    if (language == "rust" && RustReferenceExtractor.IsRawIdentifierPrefix(preparedLine, callIndex))
                        continue;
                    if (language == "objc" && IsObjCSelectorLiteralCall(preparedLine, name, callIndex))
                        continue;
                    if (sqlSuppressedCallIndices != null && sqlSuppressedCallIndices.Contains(callIndex))
                        continue;
                    matchedCallIndices.Add(callIndex);
                    AddCallLikeReference(name, callIndex);
                    if (language == "ruby")
                        RubyReferenceExtractor.EmitCommandTargetReferences(
                            name,
                            callIndex,
                            originalLine,
                            references,
                            seen,
                            fileId,
                            context,
                            lineNumber,
                            ResolveContainerForCall);
                }

                if (language == "ruby")
                {
                    RubyReferenceExtractor.EmitAdditionalCallReferences(
                        preparedLine,
                        originalLine,
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        ResolveContainerForCall,
                        matchedCallIndices,
                        AddCallLikeReference);
                }

                if (language == "swift")
                    SwiftReferenceExtractor.EmitTrailingClosureReferences(preparedLine, AddCallLikeReference);
                else if (language == "kotlin")
                    KotlinReferenceExtractor.EmitTrailingLambdaReferences(preparedLine, AddCallLikeReference);

                if (language == "fsharp")
                {
                    FSharpReferenceExtractor.EmitAdditionalCallReferences(
                        preparedLine,
                        AddCallLikeReference);
                }

                if (language == "scala")
                {
                    ScalaReferenceExtractor.EmitTrailingBlockCallReferences(
                        preparedLine,
                        AddCallLikeReference);
                }
                else if (language == "gradle")
                {
                    void AddGradleDslReference(string name, int callIndex)
                    {
                        var normalizedName = NormalizeAtPrefixedIdentifier(name);
                        var callContainer = ResolveContainerForCall(callIndex);
                        AddReference(references, seen, fileId, normalizedName, callIndex, "call", context, lineNumber, callContainer);
                    }

                    GradleReferenceExtractor.EmitDslCallReferences(
                        preparedLine,
                        AddGradleDslReference);
                }

                if (language == "fortran")
                    FortranReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference);
                else if (language == "pascal")
                    PascalReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, definitionNames);
                else if (language == "objc")
                    ObjectiveCReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, references, seen, fileId, context, lineNumber, ResolveContainerForCall);
                else if (language == "haskell")
                    HaskellReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, definitionNames);
                else if (language == "elixir")
                    ElixirReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, definitionNames);
                else if (language == "lua")
                    LuaReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, definitionNames);
                else if (language == "smalltalk")
                    SmalltalkReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, definitionNames);

                // The flat CallRegex misses nested generic tails like `>>(` because `<[^>\n]+>`
                // stops at the first `>`. Add a depth-aware fallback so `Foo<Bar<int>>()` and
                // `new Dict<K, List<V>>()` still emit call/instantiate rows. See issue #263.
                // 平坦な CallRegex は `<[^>\n]+>` が最初の `>` で止まるため `>>(` 形を取りこぼす。
                // depth-aware な fallback を足し、`Foo<Bar<int>>()` や `new Dict<K, List<V>>()` でも
                // `call` / `instantiate` を発行する。issue #263 参照。
                foreach (var candidate in EnumerateNestedGenericCallCandidates(preparedLine, matchedCallIndices))
                    AddCallLikeReference(candidate.Name, candidate.NameIndex);
            }

            if (language == "rust")
            {
                RustReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference);
            }

            if (language == "csharp")
            {
                EmitMethodGroupReferences(
                    language,
                    preparedLine,
                    callableDefinitionNames,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language is "java")
            {
                JavaReferenceExtractor.EmitMethodReferenceReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language is "kotlin")
            {
                KotlinReferenceExtractor.EmitMethodReferenceReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language is "scala")
            {
                ScalaReferenceExtractor.EmitMethodReferenceReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }

            // Qualified C# enum-member access such as `Nested.A` or `Outer.First.None` is not
            // a method call, but downstream symbol workflows (`references`, `callers`,
            // `callees`, `inspect`, `impact`) still need an edge anchored to the narrowest
            // real owner symbol. Ordinary code paths stay `call` so existing graph readers
            // keep working, while C# attribute metadata sites are downgraded to `attribute`
            // to stay out of runtime call-graph traversals (issue #293 / #492).
            // `Nested.A` や `Outer.First.None` のような C# enum member の修飾アクセスは
            // メソッド呼び出しではないが、下流の symbol workflow では実 owner に紐づく edge が必要。
            // 通常コードでは既存 reader / SQL 契約を守るため kind は `call` を維持し、C# 属性メタデータ内だけ
            // `attribute` に落として runtime call-graph への混入を防ぐ (issue #293 / #492)。
            if (language == "csharp" && csharpQualifiedEnumMemberLookup.Count > 0)
            {
                CSharpReferenceExtractor.EmitQualifiedEnumMemberReferences(
                    preparedLine,
                    csharpQualifiedEnumMemberLookup,
                    csharpAttrRangesOnLine,
                    csharpUsingAliases,
                    csharpValueReceiverNames,
                    csharpFunctionValueReceiverNames,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }

            // issue #268: JS/TS tagged template literal call sites. The structural masker
            // already located each template opener and captured its preceding tag identifier;
            // emit one `call` row per hit so `gql\`...\`` / `styled.div\`...\`` / `sql\`...${x}...\``
            // surface in references / callers / callees / impact just like `fn()` call sites.
            // issue #268: JS/TS タグ付きテンプレートリテラルの呼び出し位置。構造マスカーが
            // テンプレート opener を検出済みで先行する tag 識別子を記録しているため、そのまま
            // `call` として発行し、`gql\`...\``・`styled.div\`...\``・`sql\`...${x}...\`` を
            // references / callers / callees / impact に反映する。
            if (jsTaggedTemplatesByLine != null
                && jsTaggedTemplatesByLine.TryGetValue(lineNumber, out var tagHitsOnLine))
            {
                foreach (var hit in tagHitsOnLine)
                {
                    var name = hit.Name;
                    // Bare-name suppression (shared ignore list + tagged-template
                    // operator denylist) is bypassed for member-access tags because
                    // any reserved / keyword-ish identifier is a legal property name
                    // in JS/TS — `obj.return\`x\``, `obj.await\`y\``, `obj.yield\`z\``,
                    // `obj.default\`w\``, `obj.finally\`v\`` all evaluate to real
                    // tagged-template calls. Only bare-keyword forms such as
                    // `yield \`x\``, `await \`x\``, `export default \`x\``,
                    // `try {} finally \`x\`` should remain suppressed.
                    // bare-name による抑止（共有 ignore list と tagged-template 演算子
                    // denylist）は member-access のタグでは迂回する。JS/TS ではすべての
                    // 予約語相当 identifier が property 名になれるため
                    // `obj.return\`x\``・`obj.await\`y\``・`obj.yield\`z\``・
                    // `obj.default\`w\``・`obj.finally\`v\`` はすべて正当なタグ呼び出し。
                    // `yield \`x\``・`await \`x\``・`export default \`x\``・
                    // `try {} finally \`x\`` のような bare-keyword 形のみ抑止する。
                    if (!hit.IsMemberAccess)
                    {
                        if (IsIgnoredCallName(language, name))
                            continue;
                        if (JsTaggedTemplateOperatorNames.Contains(name))
                            continue;
                    }
                    if (definitionNames != null && definitionNames.Contains(name))
                        continue;
                    var tagContainer = ResolveContainerForCall(hit.Column - 1);
                    AddChainReference(references, seen, fileId, name, hit.Column, "call", context, lineNumber, tagContainer);
                }
            }

            // issue #293: bare no-arg attributes / annotations are invisible to CallRegex because
            // it requires `(`. Emit them from dedicated regexes so `[Serializable]` / `@Deprecated`
            // and their siblings still populate the reference table.
            // issue #293: 引数なしの属性・アノテーションは `(` が必須な CallRegex では拾えないため、
            // 専用 regex から `[Serializable]` / `@Deprecated` などの素形を reference テーブルへ反映する。
            if (language == "csharp" && csharpAttrTopLevelOnLine != null && csharpAttrTopLevelOnLine.Count > 0)
            {
                foreach (Match match in CSharpNoArgAttributeRegex.Matches(preparedLine))
                {
                    var rawName = match.Groups["name"].Value;
                    var name = NormalizeCSharpIdentifier(rawName);
                    var nameIndex = match.Groups["name"].Index;
                    // Gate on the attribute-section top-level (paren-depth 0) zones only, so
                    // identifiers that sit inside an attribute's argument list (e.g.
                    // `ConverterStrategy.AllowNumbers` in `[JsonConverter(...)]`) are not
                    // misclassified as no-arg attributes.
                    // 属性セクションの top-level（paren 深さ 0）ゾーンでのみ採用する。属性の
                    // 引数リスト内にある識別子（`[JsonConverter(ConverterStrategy.AllowNumbers)]`
                    // の `AllowNumbers` など）を no-arg 属性として誤分類しないため。
                    if (!IsInsideCSharpAttributeRange(csharpAttrTopLevelOnLine, nameIndex))
                        continue;
                    if (IsIgnoredCallName(language, rawName))
                        continue;
                    if (definitionNames != null && definitionNames.Contains(name))
                        continue;
                    AddReference(references, seen, fileId, name, nameIndex, "attribute", context, lineNumber, container);
                }
            }
            else if (AnnotationLanguages.Contains(language))
            {
                foreach (Match match in NoArgAnnotationRegex.Matches(preparedLine))
                {
                    var name = match.Groups["name"].Value;
                    if (IsIgnoredCallName(language, name))
                        continue;
                    if (definitionNames != null && definitionNames.Contains(name))
                        continue;
                    AddReference(references, seen, fileId, match, "annotation", context, lineNumber, container);
                }
            }

            if (isRazorFile && language == "csharp")
            {
                RazorReferenceExtractor.EmitReferences(
                    razorReferenceLines?[i] ?? originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    definitionNames);
            }

            if (language == "python")
            {
                PythonReferenceExtractor.EmitDecoratorReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    definitionNames,
                    name => IsIgnoredCallName(language, name));
            }

            if (language == "r")
            {
                RReferenceExtractor.EmitNamespaceReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    definitionNames);
            }
        }

        if (language == "csharp")
        {
            CSharpReferenceExtractor.EmitSwitchExpressionTypePatternReferences(
                lines,
                preparedLines,
                containerCandidates,
                csharpQualifiedConstantPatternMemberLookup,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                HasActiveSameFileCSharpTypeCandidate,
                references,
                seen,
                fileId);

            CSharpReferenceExtractor.FlushPendingMultiLineTypePatternReference(
                ref pendingCSharpMultiLineTypePattern,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                HasActiveSameFileCSharpTypeCandidate,
                references,
                seen,
                fileId);
        }

        return references;
    }

    internal static void AddReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Match match,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container)
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
            container);
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
        SymbolRecord? container)
    {
        var column = nameIndex + 1;
        var dedupeKey = $"{lineNumber}:{column}:{referenceKind}:{name}";
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
        });
    }

    private static bool IsJsxFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".jsx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tsx", StringComparison.OrdinalIgnoreCase);
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
                var dedupeKey = $"{lineNumber}:{column}:type_reference:{normalizedSegment}";
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
        string language)
    {
        int i = startIndex;
        int parenDepth = 0;
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
                if (parenDepth == 0)
                    return;
                // Tuple element separator inside `typeof((Foo, Bar))` — keep scanning.
                // `typeof((Foo, Bar))` のタプル要素区切りは続けて走査する。
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
                i = SkipBalanced(line, i, '<', '>');
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
        ref CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern)
    {
        TryEmitCSharpBaseListReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitCSharpWhereConstraintReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitDeclarationTypeReferences("csharp", preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);

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
                        "csharp")))
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
                "csharp");
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



}
