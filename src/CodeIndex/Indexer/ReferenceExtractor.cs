using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Extracts lightweight symbol references such as call sites.
/// 軽量なシンボル参照（呼び出し箇所など）を抽出する。
/// </summary>
public static class ReferenceExtractor
{
    private readonly record struct SqlDefinitionLeafSpan(string LeafName, int StartIndex, int EndIndexExclusive);
    private readonly record struct CSharpMultiLineTypePatternState(
        bool WaitingForHead,
        string? PendingTypeExpression,
        int PendingTypeIndex,
        int PendingTypeLineNumber,
        string? PendingContext,
        SymbolRecord? PendingContainer);
    private const string SqlProcCallIdentifierPattern = @"(?:\[(?:[^\]\r\n]|\]\])+\]|`[^`\r\n]+`|""(?:""""|[^""\r\n])+""|##?\w+|[_\p{L}][\p{L}\p{Mn}\p{Mc}\p{Nd}\p{Pc}$]*(?:;\d+)?)";
    private const string SqlProcCallQualifierPattern = @"(?:(?:" + SqlProcCallIdentifierPattern + @")?\s*\.\s*)*";

    private static readonly HashSet<string> SupportedLanguages =
    [
        "python", "javascript", "typescript", "csharp", "go", "rust",
        "java", "kotlin", "ruby", "perl", "c", "cpp", "php", "swift",
        "dart", "scala", "elixir", "lua", "vb", "fsharp", "sql", "cobol", "batch",
        "r", "powershell", "shell", "haskell",
        "gradle", "terraform", "protobuf", "dockerfile", "makefile",
        "zig", "css"
    ];

    private static readonly Regex ScssVariableReferenceRegex = new(
        @"(?<![\w$])\$(?<name>[A-Za-z_][\w-]*)",
        RegexOptions.Compiled);

    private static readonly Regex ScssExtendReferenceRegex = new(
        @"@extend\s+(?<name>[%.][A-Za-z_][\w-]*)",
        RegexOptions.Compiled);

    private static readonly Regex DockerfileStageReferenceRegex = new(
        @"^\s*FROM\s+(?:--platform=\S+\s+)?(?<name>\w+)\s+AS\s+\w+\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DockerfileCopyFromReferenceRegex = new(
        @"^\s*(?:COPY|ADD)\b.*?--from=(?<name>\w+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CobolCallRegex = new(
        @"^\s*CALL\s+(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolCopyRegex = new(
        @"^\s*COPY\s+(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[A-Z0-9][A-Z0-9-]*))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolGotoRegex = new(
        @"^\s*(?:GO\s+TO|GOTO)\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolPerformRegex = new(
        @"^\s*PERFORM\s+(?!(?:VARYING|UNTIL|WITH|TIMES|TEST|THRU|THROUGH)\b)(?<name>[A-Z0-9][A-Z0-9-]*)(?:\s+(?:THRU|THROUGH)\s+(?<end>[A-Z0-9][A-Z0-9-]*))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolSetRegex = new(
        @"^\s*SET\s+(?<name>[A-Z0-9][A-Z0-9-]*)\s+\b(?:TO\s+TRUE|TO\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolOpenRegex = new(
        @"^\s*OPEN\s+(?:INPUT|OUTPUT|I-O|EXTEND)\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolSearchRegex = new(
        @"^\s*SEARCH\s+(?:(?:ALL|FIRST)\s+)?(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolSimpleReferenceRegex = new(
        @"^\s*(?:READ|WRITE|REWRITE|DELETE|CLOSE|SORT|MERGE|INSPECT|DISPLAY|ACCEPT)\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolMoveRegex = new(
        @"^\s*MOVE\b.*?\bTO\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolAddRegex = new(
        @"^\s*ADD\b.*?\bTO\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolSubtractRegex = new(
        @"^\s*SUBTRACT\b.*?\bFROM\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolMultiplyRegex = new(
        @"^\s*MULTIPLY\b.*?\bBY\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolDivideRegex = new(
        @"^\s*DIVIDE\b.*?\bINTO\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolComputeRegex = new(
        @"^\s*COMPUTE\s+(?<name>[A-Z0-9][A-Z0-9-]*)\s*=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolStringRegex = new(
        @"^\s*STRING\b.*?\bINTO\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolUnstringRegex = new(
        @"^\s*UNSTRING\b.*?\bINTO\s+(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Batch jump targets can appear as direct commands, chained commands, or inline `if`
    // forms, including comparison-based conditions such as `if /i "%a%"=="b" goto :X`.
    // batch のジャンプ先は、直書き・連結コマンド・`if` 併用の inline 形として現れうる。
    // 比較式ベースの `if /i "%a%"=="b" goto :X` のような形も含めて扱う。
    private static readonly Regex BatchJumpTargetRegex = new(
        @"^\s*@?\s*(?:(?:if\s+(?:/i\s+)?(?:not\s+)?(?:(?:errorlevel\s+\d+|defined\s+\S+|exist\s+\S+|cmdextversion\s+\d+)|(?:[^()\r\n]+?\s*(?:==|equ|neq|lss|leq|gtr|geq)\s*[^()\r\n]+?))\s+(?:\(\s*)?)?)?(?<command>goto|call)\s+:(?<name>[\w.\-]+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Terraform dotted references are paren-less and therefore invisible to the shared CallRegex.
    // Terraform の dotted reference は括弧を伴わないため、共有 CallRegex では見えない。
    private static readonly Regex TerraformVarReferenceRegex = new(
        @"(?<![\w.])var\.(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex TerraformLocalReferenceRegex = new(
        @"(?<![\w.])local\.(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex TerraformModuleReferenceRegex = new(
        @"(?<![\w.])module\.(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex TerraformDataReferenceRegex = new(
        @"(?<![\w.])data\.[A-Za-z_]\w*\.(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex TerraformResourceReferenceRegex = new(
        @"(?<![\w.])(?<type>[A-Za-z_]\w*_[A-Za-z_]\w*)\.(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

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
        // after EmitJavaCtorChainReferences rewrites the chain to the owning class.
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
            "putStrLn", "putStr", "print",
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
    private static readonly Regex PhpStaticAccessRegex = new(
        @"(?<![\w$\\])(?<name>(?:\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*))::(?<member>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PhpObjectMemberAccessRegex = new(
        @"(?:\?->|->)\s*(?<name>[A-Za-z_]\w*)(?!\s*\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CssCustomPropertyReferenceRegex = new(@"\bvar\(\s*--(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CssAnimationNameReferenceRegex = new(@"\banimation-name\s*:\s*(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CssAnimationShorthandValueRegex = new(@"\banimation\s*:\s*(?<value>[^;{}]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CssClassSelectorReferenceRegex = new(@"(?<![A-Za-z0-9_-])\.(?<name>[\w-]+)", RegexOptions.Compiled);
    private static readonly HashSet<string> CssAnimationShorthandIgnoredTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ease", "ease-in", "ease-out", "ease-in-out", "linear",
        "step-start", "step-end", "cubic-bezier", "steps",
        "infinite", "normal", "reverse", "alternate", "alternate-reverse",
        "none", "forwards", "backwards", "both", "running", "paused",
        "initial", "inherit", "unset", "revert", "revert-layer",
    };
    // SQL-specific single-quoted string stripper: preserve identifier quoting (`[...]`, `` `...` ``,
    // and ANSI `"..."`) so the SQL graph path can still see real object names while literal payloads
    // stay masked.
    // SQL 専用の単引用符文字列リテラル除去。識別子引用（`[...]` / `` `...` `` / ANSI `"..."`）は
    // 残しつつ、文字列リテラルだけを隠して SQL graph 抽出が実オブジェクト名を見失わないようにする。
    private static readonly Regex SqlSingleQuotedStringLiteralRegex = new(
        "'(?:''|\\\\.|[^'\\\\])*'",
        RegexOptions.Compiled);
    private static readonly Regex InlineBlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Compiled);
    private const string CSharpIdentifierPattern = @"@?[_\p{L}]\w*";
    private const string FunctionalIdentifierPattern = @"@?[_\p{L}\$][\w$]*";
    private const string RustIdentifierPattern = @"(?:r#)?[_\p{L}][\w$]*";
    private const string FSharpIdentifierPattern = @"(?:``[^`]+``|[_\p{L}][\w']*)";
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
    // Rust macro calls use `!` plus one of `()`, `[]`, or `{}` instead of the shared trailing `(`.
    // Capture the full path-qualified macro name so `std::println!`, `log::info!`, and
    // `my_macro!` all surface as references. The `macro_rules` declaration keyword is filtered
    // by the Rust ignore list above.
    // Rust の macro 呼び出しは共通の末尾 `(` ではなく `!` の後に `()` / `[]` / `{}` を取る。
    // `std::println!` / `log::info!` / `my_macro!` のような path-qualified 名も含めて拾い、
    // `macro_rules` 宣言キーワードは上の Rust ignore list で除外する。
    private static readonly Regex RustMacroCallRegex = new($@"(?<![\w$])(?<name>{RustIdentifierPattern}(?:::{RustIdentifierPattern})*)(?:<[^>\n]+>)?!\s*[\(\[\{{]", RegexOptions.Compiled);
    // Rust raw identifiers such as `r#type()` are stored without the `r#` prefix, but the shared
    // call regex cannot see them because `#` is not an identifier character. Use a Rust-only
    // matcher for names that contain at least one raw segment and normalize the stored form below.
    // Rust の raw identifier (`r#type()`) は保存時に `r#` を外すが、共通 CallRegex は `#` を
    // 識別子文字として扱わないため見逃す。raw segment を含む Rust 専用 matcher を足し、
    // 下で保存形式へ正規化する。
    private static readonly Regex RustRawIdentifierCallRegex = new($@"(?<![\w$])(?<name>(?:(?:r#)?\w+::)*r#\w+(?:::(?:r#)?\w+)*)(?:<[^>\n]+>)?\s*\(", RegexOptions.Compiled);
    // Ruby command-syntax calls such as `puts "hi"`, `greet bob`, and `before_action :auth`
    // omit the trailing `(` that the shared CallRegex requires.
    // Ruby の command syntax 呼び出し (`puts "hi"` / `greet bob` / `before_action :auth`)
    // は末尾 `(` を省略できるため、共通 CallRegex では拾えない。
    private static readonly Regex RubyCommandCallRegex = new(
        @"(?<![\w$@])(?<name>[A-Za-z_]\w*[?!]?)\s+(?![=<>!~+\-*/%&|^]|do\b|end\b|then\b|\()(?:[:'""\w])",
        RegexOptions.Compiled);
    // Ruby block-call forms such as `xs.each { |x| ... }`, `xs.each do |x| ... end`, and
    // `with_transaction do ... end` do not have a trailing `(`, so the shared CallRegex misses
    // them. The optional paren segment keeps `foo(bar) do`-style DSLs visible too.
    // Ruby の block-call 形 (`xs.each { |x| ... }` / `xs.each do |x| ... end` /
    // `with_transaction do ... end`) は末尾 `(` がないため、共通 CallRegex では拾えない。
    // 任意の括弧 segment を許すことで `foo(bar) do` のような DSL 形も拾う。
    private static readonly Regex RubyBlockCallRegex = new(
        @"(?<![\w$@])(?<name>[A-Za-z_]\w*[?!]?)(?:\s*\([^\)\r\n]*\))?\s*(?:\{|do\b)",
        RegexOptions.Compiled);
    // Ruby DSL target arguments such as `include Shared`, `raise ArgumentError, ""bad""`,
    // `attr_accessor :name`, and `before_action :authenticate` should become references even when
    // the call name itself is ignored or the command form omits `(`.
    // Ruby の DSL ターゲット引数 (`include Shared` / `raise ArgumentError, ""bad""` /
    // `attr_accessor :name` / `before_action :authenticate`) は、呼び出し名が ignored でも
    // `(` 省略でも reference として残す。
    private static readonly HashSet<string> RubyCommandTargetReferenceNames = new(StringComparer.Ordinal)
    {
        "include", "extend", "require", "raise", "attr", "attr_accessor", "attr_reader", "attr_writer",
        "define_method", "before_action", "after_action", "around_action", "helper_method",
        "has_many", "has_one", "belongs_to", "scope", "delegate", "validates",
    };
    private static readonly HashSet<string> RubyCommandTargetSingleTokenNames = new(StringComparer.Ordinal)
    {
        "require", "raise", "define_method",
    };
    private static readonly Regex RubyCommandTargetTokenRegex = new(
        @"(?<![\w$@])(?<token>:(?:""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|[A-Za-z_]\w*[?!]?)|[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')",
        RegexOptions.Compiled);
    // Method-group / method-reference handoffs do not have a trailing `(`, so the shared
    // CallRegex cannot see them. C# / JS / TS use a context gate plus a callable-name allowlist,
    // while Java / Kotlin / Scala use the unique `::` sigil.
    // `(` を持たない method-group / method-reference handoff は共通 CallRegex では拾えないため、
    // C# / JS / TS は文脈ゲート＋ callable-name allowlist、Java / Kotlin / Scala は `::` sigil で拾う。
    private static readonly Regex MethodGroupReferenceRegex = new(
        $@"(?<![\w$])(?:(?:[=,]\s*|return\s+|=>\s+|(?<contextTarget>{FunctionalIdentifierPattern})(?:<[^>\n]+>)?\s*\(\s*))(?:(?:this|base|{FunctionalIdentifierPattern}(?:\.{FunctionalIdentifierPattern})*)\s*\.\s*)?(?<name>{FunctionalIdentifierPattern})(?!\s*\()(?!\s*`)(?=\s*(?:[;,)\]]|$))",
        RegexOptions.Compiled);
    private static readonly Regex JavaMethodReferenceRegex = new(
        $@"(?<![\w$])(?:(?<owner>(?:this|super|{FunctionalIdentifierPattern}(?:\.{FunctionalIdentifierPattern})*))\s*)?::\s*(?<name>{FunctionalIdentifierPattern}|new)\b(?=\s*(?:[;,)\]]|$))",
        RegexOptions.Compiled);
    // JSX / TSX component element open tags. Capitalized tag names are treated as component
    // call sites, while lowercase intrinsic HTML tags stay excluded by design.
    // JSX / TSX の component open tag。大文字始まりの tag 名だけを component 呼び出しとして扱い、
    // 小文字始まりの intrinsic HTML tag は意図的に除外する。
    private static readonly Regex JsxElementOpenRegex = new(
        @"<(?<name>[A-Z][\w$]*(?:\.[A-Za-z_$][\w$]*)*)",
        RegexOptions.Compiled);
    // Swift / Kotlin trailing-lambda calls such as `items.forEach { ... }`, `list.filter { ... }`,
    // and `animate { ... } completion: { ... }` do not have a trailing `(`, so the shared CallRegex
    // cannot see them. Emit the same `call` edge for these trailing-block forms so the call graph
    // stays aligned with the idiomatic source form. See issue #265.
    // Swift / Kotlin の trailing-lambda 呼び出し (`items.forEach { ... }`, `list.filter { ... }`,
    // `animate { ... } completion: { ... }`) は末尾 `(` を持たないため、共通 CallRegex では拾えない。
    // これらも同じ `call` edge として発行し、慣用的な記法でも call graph が欠けないようにする。
    // issue #265 参照。
    private static readonly Regex TrailingLambdaCallRegex = new($@"(?<![\w$])(?<name>{CSharpIdentifierPattern})(?:<[^>\n]+>)?\s*\{{", RegexOptions.Compiled);
    // F# pipeline forms such as `xs |> List.map f` / `xs ||> map2 f g` do not use the shared
    // trailing `(` shape, so the generic CallRegex misses them. Emit the leaf function name
    // after the pipeline operator so idiomatic `|>` chains stay searchable. Space-separated
    // application remains intentionally out of scope for now.
    // F# の pipeline 形 (`xs |> List.map f` / `xs ||> map2 f g`) は共通の末尾 `(` 形を使わないため、
    // generic CallRegex では拾えない。pipeline 演算子の後ろにある leaf function 名を出すことで、
    // 慣用的な `|>` 連鎖も検索可能にする。space-separated application は当面対象外。
    private static readonly Regex FSharpPipelineCallRegex = new(
        $@"(?<![\w$])(?:\|{{1,3}}>)\s*(?:(?:{FSharpIdentifierPattern})\s*\.\s*)*(?<name>{FSharpIdentifierPattern})\b",
        RegexOptions.Compiled);
    // F# also allows ordinary space-separated application such as `printfn "x"` or
    // `List.map increment numbers`. Capture the callable leaf when the expression starts a
    // statement or follows a small set of expression separators so common non-paren calls stay
    // visible without trying to parse the full language. Match arms (`| Some x -> printfn ...`)
    // are included because they are a very common F# search target.
    // F# では `printfn "x"` や `List.map increment numbers` のような空白区切り application も普通に使う。
    // 全体構文の解析はせず、文頭または少数の式区切りの後ろにある callable leaf を拾うことで、
    // 慣用的な non-paren 呼び出しも可視化する。`| Some x -> printfn ...` のような match arm も
    // F# の検索対象として重要なので含める。
    private static readonly Regex FSharpSpaceApplicationCallRegex = new(
        $@"(?:\b(?:then|do|else|in)\s+|->\s+|[=(,\[\{{;]\s*|^\s*)
            (?:(?:{FSharpIdentifierPattern})\s*\.\s*)*
            (?<name>{FSharpIdentifierPattern})\b
            (?=\s+(?:{FSharpIdentifierPattern}|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\(|\[|\{{|\d))",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);
    // F# symbolic operator usages such as `x ++ y`, `x >>= f`, and `(++)` should also
    // surface as searchable references. The matcher intentionally stays conservative by
    // excluding standard F# operators that already have dedicated syntax or would create
    // excessive noise in call graphs.
    // F# の symbolic operator 使用 (`x ++ y` / `x >>= f` / `(++)`) も検索可能な reference として
    // 出す。標準 F# operator は専用構文やノイズの多さを考慮して除外し、保守的に拾う。
    private static readonly Regex FSharpOperatorCallRegex = new(
        @"(?<![\w$])(?<name>[!%&*+\-./:<=>?@^|~]{2,})(?![\w$])",
        RegexOptions.Compiled);
    private static readonly Regex FSharpOperatorDefinitionCallRegex = new(
        @"^\s*let\s+(?:(?:rec|mutable|inline|private|internal|public)\s+)*\((?<name>[!%&*+\-./:<=>?@^|~]{2,})\)",
        RegexOptions.Compiled);
    private static readonly HashSet<string> FSharpIgnoredOperatorCallNames = new(StringComparer.Ordinal)
    {
        "->", "<-", "..", "<|", "<||", "<|||", "|>", "||>", "|||>", "|>>", "<<", ">>", "<<<", ">>>",
        "&&", "&&&", "||", "|||", "::", "<>", "<=", ">=", "**", "@@", ":>", ":?", ":=",
    };
    // Scala's `name { ... }` / `name { x => ... }` block-call form does not use trailing `(`,
    // so the shared CallRegex cannot see it. Use a Scala-specific pass so idiomatic block calls
    // such as `foreach {}`, `Try {}`, and `synchronized {}` still contribute `call` edges.
    // Scala の `name { ... }` / `name { x => ... }` 形式は末尾 `(` を持たないため共通 CallRegex では拾えない。
    // `foreach {}` / `Try {}` / `synchronized {}` のような慣用的なブロック呼び出しも `call` edge として出すため専用パスを使う。
    private static readonly Regex ScalaTrailingBlockCallRegex = new($@"(?<![\w$])(?<name>{FunctionalIdentifierPattern})(?:\[[^\]\n]+\])?\s*\{{", RegexOptions.Compiled);
    // Scala block-call syntax must not treat control-flow keywords such as `match {` or
    // `catch {` as invocations.
    // Scala の block-call 構文では `match {` / `catch {` のような制御フローキーワードを呼び出し扱いしない。
    private static readonly HashSet<string> ScalaIgnoredBlockCallNames = new(StringComparer.Ordinal)
    {
        "match", "catch", "else", "finally",
    };
    // Gradle/Groovy block and command-style DSL calls such as `plugins { ... }`,
    // `task buildJar(type: Jar) { ... }`, `apply plugin: 'java'`, and `println 'x'`
    // do not use the shared `foo(...)` shape. Keep the matcher narrow to known DSL
    // call forms so ordinary assignment lines stay out of the graph.
    // Gradle/Groovy の block / command 型 DSL 呼び出し (`plugins { ... }`、
    // `task buildJar(type: Jar) { ... }`、`apply plugin: 'java'`、`println 'x'`) は
    // 共通の `foo(...)` 形では拾えない。代わりに、既知の DSL 呼び出し形に絞った
    // 専用 matcher で取り込む。
    private static readonly Regex GradleBlockCallRegex = new(
        @"(?<![\w$@])(?<name>[A-Za-z_]\w*)\b(?:\s+[^\r\n{]+?)?\s*\{",
        RegexOptions.Compiled);
    private static readonly Regex GradleCommandCallRegex = new(
        @"(?<![\w$@])(?<name>[A-Za-z_]\w*)\s+(?=(?:['""]|[_\p{L}]|\d|\.|:))",
        RegexOptions.Compiled);
    // PowerShell cmdlet / function calls are statement-start or pipeline-stage forms such as
    // `Get-ChildItem -Path .`, `Write-Host "x"`, and `$items | ForEach-Object { ... }`.
    // The shared CallRegex only sees parenthesized calls and would split hyphenated cmdlets
    // after the hyphen, so PowerShell uses a dedicated pass. See issue #281.
    // PowerShell の cmdlet / function 呼び出しは `Get-ChildItem -Path .` や
    // `Write-Host "x"`、`$items | ForEach-Object { ... }` のような statement-start / pipeline 形。
    // 共有 CallRegex は `(` 付きしか見えず、ハイフン入り cmdlet も hyphen の後ろで分断するため、
    // PowerShell は専用パスを使う。issue #281 参照。
    private static readonly Regex PowerShellCallRegex = new(
        @"(?:^|[|;&{=]\s*)\s*(?<name>[A-Za-z][A-Za-z0-9]*(?:-[A-Za-z][A-Za-z0-9]*)+)\b",
        RegexOptions.Compiled | RegexOptions.Multiline);
    // Shell command-style function calls such as `setup`, `setup && cleanup`, and
    // `if setup; then ...` do not use trailing `(`, so the shared CallRegex misses them.
    // Restrict the matcher to bare command heads and only emit references for function names
    // that are actually defined in the current file.
    // Shell のコマンド構文による関数呼び出し (`setup` / `setup && cleanup` / `if setup; then ...`)
    // は末尾 `(` を持たないため、共通 CallRegex では拾えない。bare command head に限定し、
    // さらに現在ファイルで定義された function 名だけを参照として出す。
    private static readonly Regex ShellCommandCallRegex = new(
        @"(?:^\s*(?!(?:if|then|do|else|elif|while|until|time|fi)\b)|[|;&{]\s*|&&\s*|\|\|\s*|!\s+|\b(?:if|then|do|else|elif|while|until|time)\s+)(?<name>[A-Za-z_][A-Za-z0-9_-]*)(?=\s|$|[;|&}])",
        RegexOptions.Compiled);
    // Shell `source` / `.` invocations load other scripts, so surface the referenced path
    // as a `reference` edge for dependency-style search.
    // Shell の `source` / `.` 呼び出しは他スクリプトを読み込むため、依存関係検索用に
    // 参照エッジとして対象パスを出す。
    private static readonly Regex ShellSourceReferenceRegex = new(
        @"(?:^\s*(?!(?:if|then|do|else|elif|while|until|time|fi)\b)|[|;&{]\s*|&&\s*|\|\|\s*|!\s+|\b(?:if|then|do|else|elif|while|until|time)\s+)(?:source|\.)\s+(?<name>(?:'[^']*'|""[^""]*""|[^;\s&#|}]+))",
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
    // names (`` `proc-name` ``) to cover MySQL / MariaDB identifier quoting.
    // 角括弧識別子は `[` / `]` / 改行以外の任意文字を許可する。T-SQL では `#`（一時プロシージャ）、
    // `-`（ハイフン名）、空白、Unicode 記号などが正当に現れるため、`[\w ]+` ではそれらの合法な
    // 形を取りこぼし、修飾子 `[dbo]` を proc 名として誤発行してしまう。
    // 修飾子の各セグメントを optional にすることで、`EXEC AdventureWorks..sp_GetCustomer;` のように
    // SQL Server が許す省略形（`..`）でも末尾の proc 名まで到達できる。識別子候補にはバッククォート引用も含め、
    // MySQL / MariaDB の `` `proc-name` `` 形にも対応する。
    private const string SqlDoubleQuotedIdentifierPattern = "\"(?:\"\"|[^\"\\r\\n])+\"";
    private const string SqlQuotedIdentifierPattern =
        @"(?:\[[^\[\]\r\n]+\]|`[^`\r\n]+`|" + SqlDoubleQuotedIdentifierPattern + @")";
    private const string SqlBareIdentifierPattern = @"(?:##?\w+|[_\p{L}][\p{L}\p{Mn}\p{Mc}\p{Nd}\p{Pc}$]*)";
    private const string SqlTempIdentifierPattern =
        @"(?:\[(?:##?\w+)\]|`(?:##?\w+)`|" + "\"(?:##?\\w+)\"" + @"|##?\w+)";
    private const string SqlQualifiedIdentifierNoCapturePattern =
        @"(?:(?:" + SqlQuotedIdentifierPattern + "|" + SqlBareIdentifierPattern + @")\s*\.\s*)*(?:" + SqlQuotedIdentifierPattern + "|" + SqlBareIdentifierPattern + @")";
    private const string SqlQualifiedIdentifierPattern =
        @"(?:(?:" + SqlQuotedIdentifierPattern + "|" + SqlBareIdentifierPattern + @")\s*\.\s*)*(?<name>" + SqlQuotedIdentifierPattern + "|" + SqlBareIdentifierPattern + @")";
    private const string SqlSourceAliasTailPattern =
        @"(?:\s+(?:AS\s+)?(?!JOIN\b|ON\b|USING\b|WHERE\b|GROUP\b|HAVING\b|ORDER\b|LIMIT\b|OFFSET\b|FETCH\b|UNION\b|EXCEPT\b|INTERSECT\b|RETURNING\b|FOR\b|WINDOW\b)(?:" + SqlQuotedIdentifierPattern + "|" + SqlBareIdentifierPattern + "))?";
    private const string SqlSourceTableHintTailPattern =
        @"(?:\s+WITH\s*\((?:[^()]|\([^()]*\))*\))?";
    // Derived tables need to be skipped so later comma-separated sources still surface.
    // derived table 本体は飛ばして、後続の comma-separated source を落とさないようにする。
    private const string SqlParenthesizedSourcePattern =
        @"\((?:[^()]|\((?<paren>)|\)(?<-paren>))*(?(paren)(?!))\)";
    private const string SqlDerivedTableColumnAliasListPattern =
        @"(?:\s*\(\s*(?:" + SqlQuotedIdentifierPattern + "|" + SqlBareIdentifierPattern + @")(?:\s*,\s*(?:" + SqlQuotedIdentifierPattern + "|" + SqlBareIdentifierPattern + @"))*\s*\))?";
    private const string SqlSourceListItemPattern =
        @"(?:(?:ONLY|LATERAL)\b\s+)*(?:" +
        SqlQualifiedIdentifierPattern + SqlSourceTableHintTailPattern + SqlSourceAliasTailPattern +
        @"|" + SqlParenthesizedSourcePattern + SqlSourceAliasTailPattern + SqlDerivedTableColumnAliasListPattern +
        @")";
    private const string SqlTopTargetModifierPattern =
        @"TOP\s*\([^)\r\n]*\)(?:\s+PERCENT)?(?:\s+WITH\s+TIES)?";
    private const string SqlMergeTargetHintPattern =
        @"WITH\s*\((?:[^()]|\([^()]*\))*\)";
    private static readonly Regex SqlProcCallRegex = new(
        @"(?<![\w$])(?:EXEC|EXECUTE|CALL)\b\s+(?:@\w+\s*=\s*)?" + SqlProcCallQualifierPattern + @"(?<name>" + SqlProcCallIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // SQL named source references that should become `reference` edges rather than `call` edges.
    // `FROM` now captures comma-separated source lists in a single pass so later tables such as
    // `accounts` in `FROM users, accounts` are not dropped. `JOIN` / `APPLY` keep the single-source
    // path. Bare `USING` stays excluded because SQL dialects also reuse it for non-source syntax
    // such as `CREATE INDEX ... USING btree (...)` and `ALTER TABLE ... USING expr`.
    // `FROM dbo.fn_TableValued(...)` intentionally stays out because the trailing `(` means the
    // shared CallRegex already captures it as a function call. issues #284 / #695 / #802.
    // SQL のソース参照で、`call` ではなく `reference` として扱うべき形。
    // `FROM` は comma-separated な source list を 1 回で拾い、`FROM users, accounts` の
    // `accounts` のような後続 table を落とさない。`JOIN` / `APPLY` は単一 source のまま。
    // bare な `USING` は `CREATE INDEX ... USING btree (...)` や `ALTER TABLE ... USING expr`
    // のような非 source 構文にも使われるため意図的に除外する。
    // `FROM dbo.fn_TableValued(...)` は末尾 `(` により既存 CallRegex が `call` を出すため除外する。
    // issues #284 / #695 / #802.
    private static readonly Regex SqlFromSourceListRegex = new(
        $@"(?<![\w$])FROM\b\s+{SqlSourceListItemPattern}(?:\s*,\s*{SqlSourceListItemPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlSourceReferenceRegex = new(
        $@"(?<![\w$])(?:JOIN|(?:CROSS|OUTER)\s+APPLY)\b\s+{SqlSourceListItemPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlMergeUsingSourceRegex = new(
        $@"(?<![\w$])MERGE\b(?:\s+{SqlTopTargetModifierPattern})?(?:\s+INTO)?\s+{SqlQualifiedIdentifierNoCapturePattern}(?:\s+{SqlMergeTargetHintPattern})?(?:\s+(?:AS\s+)?(?!USING\b|WITH\b)(?:{SqlQuotedIdentifierPattern}|{SqlBareIdentifierPattern}))?\s+USING\b\s+(?:(?:ONLY|LATERAL)\b\s+)*{SqlQualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlMergeUsingPrefixRegex = new(
        $@"(?<![\w$])MERGE\b(?:\s+{SqlTopTargetModifierPattern})?(?:\s+INTO)?\s+{SqlQualifiedIdentifierNoCapturePattern}(?:\s+{SqlMergeTargetHintPattern})?(?:\s+(?:AS\s+)?(?!USING\b|WITH\b)(?:{SqlQuotedIdentifierPattern}|{SqlBareIdentifierPattern}))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlDeleteUsingSourceRegex = new(
        $@"(?<![\w$])DELETE\b(?:\s+{SqlTopTargetModifierPattern})?\s+FROM(?:\s+ONLY\b)?\s+{SqlQualifiedIdentifierNoCapturePattern}(?:\s+(?:AS\s+)?(?!USING\b|WHERE\b|RETURNING\b)(?:{SqlQuotedIdentifierPattern}|{SqlBareIdentifierPattern}))?\s+USING\b\s+(?:(?:ONLY|LATERAL)\b\s+)?{SqlQualifiedIdentifierPattern}(?:\s+(?:AS\s+)?(?!WHERE\b|RETURNING\b|ON\b|USING\b)(?:{SqlQuotedIdentifierPattern}|{SqlBareIdentifierPattern}))?(?:\s*,\s*(?:(?:ONLY|LATERAL)\b\s+)?{SqlQualifiedIdentifierPattern}(?:\s+(?:AS\s+)?(?!WHERE\b|RETURNING\b|ON\b|USING\b)(?:{SqlQuotedIdentifierPattern}|{SqlBareIdentifierPattern}))?)*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlDeleteUsingPrefixRegex = new(
        $@"(?<![\w$])DELETE\b(?:\s+{SqlTopTargetModifierPattern})?\s+FROM(?:\s+ONLY\b)?\s+{SqlQualifiedIdentifierNoCapturePattern}(?:\s+(?:AS\s+)?(?!USING\b|WHERE\b|RETURNING\b)(?:{SqlQuotedIdentifierPattern}|{SqlBareIdentifierPattern}))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlDeleteUsingListContinuationPrefixRegex = new(
        @"(?<![\w$])DELETE\b[\s\S]*\bUSING\b[\s\S]*,\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlFromListContinuationPrefixRegex = new(
        @"(?<![\w$])FROM\b[\s\S]*,\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlTargetReferencePrefixRegex = new(
        $@"(?<![\w$])(?:INSERT(?:\s+{SqlTopTargetModifierPattern})?\s+INTO|UPDATE\b(?:\s+(?:{SqlTopTargetModifierPattern}|ONLY\b))*|DELETE\b(?:\s+{SqlTopTargetModifierPattern})?\s+FROM(?:\s+ONLY\b)?|TRUNCATE\s+TABLE(?:\s+ONLY\b)?|CREATE(?:\s+(?:TEMP|TEMPORARY))?\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // SQL mutation targets such as `INSERT INTO tbl (...)` / `UPDATE tbl`.
    // `INSERT INTO tbl (` is a table reference, not a function call; we later suppress the generic
    // CallRegex match at the same identifier when an opening `(` immediately follows the target.
    // `INSERT INTO tbl (...)` / `UPDATE tbl` などの更新対象。
    // `INSERT INTO tbl (` は関数呼び出しではなくテーブル参照なので、直後に `(` がある場合は後段で
    // 同じ識別子の generic CallRegex を抑止する。
    private static readonly Regex SqlTargetReferenceRegex = new(
        $@"(?<![\w$])(?:INSERT(?:\s+{SqlTopTargetModifierPattern})?\s+INTO\s+{SqlQualifiedIdentifierPattern}|UPDATE\b(?:\s+{SqlTopTargetModifierPattern})\s+{SqlQualifiedIdentifierPattern}|UPDATE\b(?:\s+ONLY\b)*\s+{SqlQualifiedIdentifierPattern}|MERGE\b(?:\s+{SqlTopTargetModifierPattern})?(?:\s+INTO)?\s+{SqlQualifiedIdentifierPattern}|DELETE\b(?:\s+{SqlTopTargetModifierPattern})?\s+FROM(?:\s+ONLY\b)?\s+{SqlQualifiedIdentifierPattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex SqlTruncateTargetRegex = new(
        $@"(?<![\w$])TRUNCATE\s+TABLE\s+(?:(?:ONLY)\b\s+)?{SqlQualifiedIdentifierPattern}(?:\s*,\s*(?:(?:ONLY)\b\s+)?{SqlQualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlTopCallSuppressionRegex = new(
        @"(?<![\w$])(?:SELECT|INSERT|UPDATE|MERGE|DELETE)\b\s+(?<name>TOP)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlAccessMethodCallSuppressionRegex = new(
        $@"(?<![\w$])CREATE\b(?:\s+UNIQUE\b)?\s+INDEX\b[\s\S]*?\bUSING\b\s+(?<name>{SqlQuotedIdentifierPattern}|{SqlBareIdentifierPattern})(?=\s*\()",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // SQL Server temp-table materialization: `SELECT ... INTO #tmp` / `SELECT ... INTO ##tmp`.
    // Procedural `SELECT ... INTO variable` remains intentionally excluded. issue #649.
    // SQL Server の temp table 作成: `SELECT ... INTO #tmp` / `SELECT ... INTO ##tmp`。
    // 手続き系の `SELECT ... INTO variable` は意図的に除外したままにする。issue #649。
    private static readonly Regex SqlSelectIntoTempTargetRegex = new(
        $@"(?<![\w$])SELECT\b.*?\bINTO\s+(?<name>{SqlTempIdentifierPattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex SqlSelectIntoTempTargetStatementRegex = new(
        $@"(?<![\w$])SELECT\b.*?\bINTO\s+(?<name>{SqlTempIdentifierPattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex SqlSelectIntoTempPrefixRegex = new(
        @"(?<![\w$])SELECT\b.*?\bINTO\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlCreateTempTableRegex = new(
        $@"(?<![\w$])CREATE(?:\s+(?:TEMP|TEMPORARY))?\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?\s+(?<name>{SqlTempIdentifierPattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlUsingKeywordRegex = new(@"(?<![\w$])USING\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // T-SQL temp stored routines: `CREATE PROCEDURE #sp` / `CREATE PROC ##sp` / `CREATE FUNCTION #f`
    // and `CREATE OR ALTER|REPLACE` / `CREATE TEMPORARY` variants. Tracks the temp name as
    // established evidence so later `EXEC #sp` / `CALL #sp` / `EXECUTE #sp` calls keep their edge
    // after the proc-call `#`-gate that was added for issue #656 to suppress MySQL `# comment`
    // false positives when no T-SQL temp object exists.
    // T-SQL の一時ストアド: `CREATE PROCEDURE #sp` / `CREATE PROC ##sp` / `CREATE FUNCTION #f` と、
    // `CREATE OR ALTER|REPLACE` / `CREATE TEMPORARY` 変種。issue #656 で proc 呼び出しに追加した
    // `#` ゲート（MySQL `# comment` 誤検出を抑止するため）が有効でも、ファイル内で当該 temp 名を
    // 確立したと見なすことで `EXEC #sp` / `CALL #sp` / `EXECUTE #sp` の edge を保持する。
    private static readonly Regex SqlCreateTempRoutineRegex = new(
        $@"(?<![\w$])CREATE(?:\s+OR\s+(?:REPLACE|ALTER))?(?:\s+(?:TEMP|TEMPORARY))?\s+(?:PROC(?:EDURE)?|FUNCTION)\b(?:\s+IF\s+NOT\s+EXISTS)?\s+(?<name>{SqlTempIdentifierPattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlTrailingTempIdentifierRegex = new(
        $@"^(?:(?:ONLY)\b\s+)?(?<item>(?:{SqlTempIdentifierPattern}|{SqlQualifiedIdentifierNoCapturePattern}))(?:\s+(?:AS\s+)?(?:{SqlQuotedIdentifierPattern}|{SqlBareIdentifierPattern}))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlMergeTargetHintContinuationPrefixRegex = new(
        $@"(?<![\w$])MERGE\b(?:\s+{SqlTopTargetModifierPattern})?(?:\s+INTO)?\s+{SqlQualifiedIdentifierNoCapturePattern}\s+WITH\s*\((?:[^()]|\([^()]*\))*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly record struct SqlIdentifierScanState(
        bool InBlockComment,
        string? DollarQuoteDelimiter,
        bool InSingleQuotedString);
    // C# event subscription/unsubscription: Click += OnClick — both LHS and RHS must be PascalCase identifiers
    // C# イベント購読・解除: Click += OnClick — LHS と RHS の両方が PascalCase 識別子のみ
    private static readonly Regex EventSubscriptionRegex = new(@"(?<name>[A-Z]\w*)\s*[+-]=\s*(?:new\s+)?[A-Z]\w*", RegexOptions.Compiled);
    // C# constructor chain initializer: `public A() : this(0)` / `public B() : base(42)`
    // C# コンストラクタ連鎖イニシャライザ
    private static readonly Regex CSharpCtorChainRegex = new(@":\s*(?<kind>this|base)\s*\(", RegexOptions.Compiled);
    // Java constructor chain statement (first statement of a constructor body): `this(0);` / `super(42);`.
    // Also matches single-line ctor bodies like `Leaf(int x){super(x);}` where `{` precedes the chain call.
    // Java コンストラクタ連鎖文。`Leaf(int x){super(x);}` のように `{` 直後に連鎖文が続く
    // single-line body 形式にも対応する。
    private static readonly Regex JavaCtorChainRegex = new(@"(?:^\s*|\{\s*)(?<kind>this|super)\s*\(", RegexOptions.Compiled);
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
    // JavaScript / TypeScript allow zero-argument constructor calls without parentheses:
    // `new Foo;`, `new Date;`, `new Demo.Provider;`, `new Box<number>;`.
    // Keep this language-gated so Java / C# / other `new` forms still require either `(`
    // or their own dedicated initializer path. The caller inspects the next significant
    // token after the match: `(` / `.` / `?.` / `[` still mean "the expression continues",
    // while ordinary delimiters/operators such as `;`, `:`, `??`, `||`, `&&`, `,`, or `)`
    // are accepted as real zero-arg constructor sites. A line-end match still peeks at the
    // next non-blank prepared line so `new Foo` followed by `.bar()` / `[0]` on the next
    // line does not emit a phantom `instantiate Foo` edge. See issue #295.
    // JavaScript / TypeScript では引数なしコンストラクタ呼び出しで括弧を省略できる
    // (`new Foo;`, `new Date;`, `new Demo.Provider;`, `new Box<number>;`)。他言語の
    // `new` 形と混線しないよう JS/TS 限定にし、同一行では文終端系トークンのみ許可する。
    // 行末一致時は次の非空 prepared line を覗き、`.bar()` / `[0]` 継続行による
    // phantom `instantiate Foo` を防ぐ。issue #295 参照。
    private static readonly Regex JsTsParenlessConstructorRegex = new(
        $@"\bnew\s+(?:{CSharpIdentifierPattern}\s*\.\s*)*(?<name>{CSharpIdentifierPattern})(?:\s*<[^>\n]+>)?",
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
    // Java access/method modifier set used by the same-line ctor scanner.
    // same-line ctor 本体のスキャナで使うアクセス / メソッド修飾子一覧。
    private static readonly HashSet<string> JavaCtorModifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "static", "final", "synchronized",
        "strictfp", "abstract", "native", "default"
    };
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
    // Java compile-time type literal: `T.class`, `T[].class`, `outer.Inner.class` etc.
    // `.class` itself is a language keyword, but the type chain in front of it is a genuine
    // reference. Emit each dot-segment as `type_reference`. See issue #253.
    // Java の `T.class` は型リテラル。`.class` 自体はキーワードだが、前置の型チェーンは
    // 正当な参照。各 dot-segment を type_reference として拾う。issue #253 参照。
    private static readonly Regex JavaDotClassArgRegex = new(
        @"(?<![\w$.])(?<arg>[A-Za-z_][\w.]*)\s*(?:\[\s*\])*\s*\.class\b",
        RegexOptions.Compiled);
    // C# type tests (`o is Base`, `o is not Base`, `o as Base`).
    // `is` / `is not` / `as` の型位置 (`o is Base`, `o is not Base`, `o as Base`)。
    private static readonly Regex CSharpIsAsTypeTestRegex = new(
        $@"(?<![\w$])(?:is\s+(?:not\s+)?|as\s+)(?<type>{CSharpTypeExpressionPattern})",
        RegexOptions.Compiled);
    private static readonly Regex CSharpTrailingIsAsTypePatternIntroRegex = new(
        @"(?<![\w$])(?:is(?:\s+not)?|as)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex CSharpTrailingCaseTypePatternIntroRegex = new(
        @"(?<![\w$])case(?:\s+not)?\s*$",
        RegexOptions.Compiled);
    private static readonly Regex CSharpIsAsTypePatternIntroContextRegex = new(
        @"(?<![\w$])(?:is(?:\s+not)?|as)",
        RegexOptions.Compiled);
    private static readonly Regex CSharpCaseTypePatternIntroContextRegex = new(
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
    // Java type test (`instanceof Foo`).
    // Java の型テスト (`instanceof Foo`)。
    private static readonly Regex JavaInstanceofRegex = new(
        @"(?<![\w$])instanceof\s+(?<type>[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*(?:\s*<[^)\];{}]+>)?(?:\s*\[\s*\])*)",
        RegexOptions.Compiled);
    // JPMS module directives (`requires`, `uses`, and `provides`) are dependency edges, not calls.
    // Emit them as `type_reference` rows so `references` / `impact` can see module-level edges.
    // JPMS の module directive (`requires` / `uses` / `provides`) は呼び出しではなく依存エッジ。
    // `references` / `impact` で module-level edge が見えるよう `type_reference` として発行する。
    private static readonly Regex JavaModuleRequiresDirectiveReferenceRegex = new(
        @"^\s*requires\s+(?:transitive\s+|static\s+)*(?<name>[\w.]+)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JavaModuleUsesDirectiveReferenceRegex = new(
        @"^\s*uses\s+(?<name>[\w.]+)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JavaModuleProvidesDirectiveReferenceRegex = new(
        @"^\s*provides\s+(?<service>[\w.]+)\s+with\s+(?<implementations>[\w.,\s]+)\s*;",
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

    // Bare Python decorators like `@staticmethod` or `@pytest.fixture` are reference sites even
    // without trailing parentheses. Keep them distinct from `call` rows so the graph can tell
    // decoration apart from invocation.
    // `@staticmethod` や `@pytest.fixture` のような Python の bare decorator は、括弧がなくても
    // reference site として記録する。`call` とは別 kind にして、装飾と呼び出しを区別できるようにする。
    private static readonly Regex PythonDecoratorRegex = new(
        @"^\s*@(?<name>[_\p{L}]\w*(?:\.[_\p{L}]\w*)*)\s*(?:#.*)?$",
        RegexOptions.Compiled);

    // R namespace references like `pkg::fun` and `pkg:::fun` should be searchable as
    // references even when they are not invoked as calls.
    // R の namespace 参照 `pkg::fun` / `pkg:::fun` は、呼び出しでなくても reference として
    // 検索できるようにする。
    private static readonly Regex RNamespaceReferenceRegex = new(
        @"(?<![\w.])(?<package>[\w.]+)(?<sep>:::?)(?<name>[\w.]+)",
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
        "java", "kotlin", "scala", "typescript", "javascript", "swift", "gradle",
    };

    // Kotlin use-site target prefixes for annotations (e.g. `@field:Deprecated("msg")`,
    // `@file:JvmName("Foo")`). Keep aligned with the Kotlin language spec use-site targets.
    // Kotlin の use-site target 付き注釈用の接頭辞。
    private static readonly HashSet<string> KotlinAnnotationTargets = new(StringComparer.Ordinal)
    {
        "field", "get", "set", "param", "setparam", "property", "receiver", "file", "delegate", "all",
    };

    public static IReadOnlyCollection<string> GetSupportedLanguages()
        => SupportedLanguages.Concat(new[] { "vue", "svelte" }).ToArray();

    private static string? NormalizeLanguage(string? lang)
        => lang is "vue" or "svelte" ? "typescript" : lang;

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
        if (!SupportsLanguage(lang))
            return [];

        lang = NormalizeLanguage(lang);
        var language = lang!;
        var isJsxFile = IsJsxFilePath(path);

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
        var preparedLines = new string[lines.Length];
        for (var pi = 0; pi < lines.Length; pi++)
            preparedLines[pi] = PrepareLine(language, structuralLines[pi]);
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
                            var leafName = SqlNameResolver.GetLeafName(symbol.Name);
                            if (!string.IsNullOrWhiteSpace(leafName))
                                names.Add(leafName);
                        }
                    }

                    return names;
                });
        var sqlDefinitionLeafSpansByLine = language == "sql"
            ? BuildSqlDefinitionLeafSpansByLine(lines, symbols)
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
                              || symbol.Kind == "namespace" || symbol.Kind == "property"))
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
        var dockerfileStageNames = BuildDockerfileStageNames(language, symbols);
        var shellCallableNames = BuildShellCallableNames(language, symbols);
        var shellGlobalAliasNames = BuildShellGlobalAliasNames(language, symbols);
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
        HashSet<string>? sqlEstablishedTempObjectNames = null;
        string? sqlStatementPrefix = null;
        var sqlIdentifierScanState = default(SqlIdentifierScanState);
        var csharpInDelimitedDocComment = false;
        if (language == "sql")
        {
            sqlEstablishedTempObjectNames = new HashSet<string>(StringComparer.Ordinal);
            sqlStatementPrefix = string.Empty;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var originalLine = lines[i];
            var preparedLine = preparedLines[i];
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
                        EmitCSharpDocCrefReferences(
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
                    FlushPendingCSharpMultiLineTypePatternReference(
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
            List<SqlDefinitionLeafSpan>? sqlDefinitionLeafSpans = null;
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
                javaSameLineCtor = TryBuildJavaSameLineCtorSpan(preparedLine, lineNumber, enclosingTypeCandidates);
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
                AdvanceCSharpMultiLineTypePatternState(
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

                if (sqlDefinitionLeafSpans == null)
                    return false;

                foreach (var span in sqlDefinitionLeafSpans)
                {
                    if (callIndex >= span.StartIndex
                        && callIndex < span.EndIndexExclusive
                        && string.Equals(span.LeafName, resolvedName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
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
                EmitCSharpCtorChainReferences(
                    preparedLine, enclosingTypeCandidates, containerCandidates,
                    structuralLines, references, seen, fileId, context, lineNumber, container);
            }
            else if (language is "java")
            {
                EmitJavaCtorChainReferences(
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
                foreach (Match match in JavaDotClassArgRegex.Matches(preparedLine))
                {
                    var argGroup = match.Groups["arg"];
                    AddTypeReferenceSegments(references, seen, fileId, argGroup.Value, argGroup.Index, context, lineNumber, container, language);
                }
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
                EmitCSharpTypePositionReferences(
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

                if (CSharpTrailingIsAsTypePatternIntroRegex.IsMatch(preparedLine)
                    && HasTrailingCSharpTypePatternIntro(originalLine, CSharpIsAsTypePatternIntroContextRegex))
                {
                    StartWaitingForCSharpMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
                }

                if (CSharpTrailingCaseTypePatternIntroRegex.IsMatch(preparedLine)
                    && HasTrailingCSharpTypePatternIntro(originalLine, CSharpCaseTypePatternIntroContextRegex))
                {
                    StartWaitingForCSharpMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
                }
            }
            else if (language == "java")
            {
                EmitJavaModuleDirectiveReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);

                EmitJavaTypePositionReferences(
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
                EmitTypeScriptTypePositionReferences(
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

                EmitTypeScriptDeclarationTypeReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language == "css")
            {
                EmitCssReferences(
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
                EmitTerraformReferences(
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
                EmitDockerfileStageReferences(
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
                EmitCobolReferences(
                    lines[i],
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    container,
                    cobolCallableSymbols);
            }

            var sqlSuppressedCallIndices = language is "sql" ? new HashSet<int>() : null;

            // SQL stored-procedure calls without parentheses: `EXEC`, `EXECUTE`, and `CALL` forms that
            // the shared CallRegex cannot see because there is no trailing `(`. Emits the same
            // reference kind ("call") so the edge merges with the rare parenthesized form via dedupe.
            // See issue #232.
            // 末尾 `(` が無いため汎用 CallRegex で取れない SQL の `EXEC` / `EXECUTE` / `CALL` 形。
            // 参照 kind は "call" で揃え、稀な `(...)` 付き形式との重複は dedupe で吸収する。issue #232 参照。
            if (language is "sql")
            {
                // The shared PrepareLine strips backtick-quoted content because several other
                // languages use backticks for string literals (shell command substitution,
                // JavaScript template literals, Go raw strings). In SQL, backticks and double
                // quotes can both quote identifiers, so we re-prepare the raw line with a
                // SQL-aware stripper that only drops single-quoted literals and comments.
                // 共有 PrepareLine はバッククォート内容を文字列として除去する（他言語のテンプレート
                // リテラル等に対応するため）。SQL ではバッククォートと二重引用符が識別子引用になり得る
                // ため、単引用符リテラル、複数行 block comment、PostgreSQL dollar quote、行コメント
                // だけを除去する stateful sanitization を別途適用する。
                var sqlLineFragment = PrepareSqlLineForIdentifierScan(
                    structuralLines[i],
                    sqlIdentifierScanState,
                    sqlStatementPrefix,
                    out var sqlLineEndedByLineComment,
                    out sqlIdentifierScanState);
                if (!string.IsNullOrWhiteSpace(sqlLineFragment))
                {
                    if (ShouldFlushSqlTempObjectPrefixAtLineBoundary(sqlStatementPrefix!, sqlLineFragment))
                    {
                        CollectSqlTempObjectNamesFromStatement(sqlStatementPrefix!, sqlEstablishedTempObjectNames!);
                        sqlStatementPrefix = string.Empty;
                    }

                    var sqlCombinedLine = CombineSqlStatementPrefix(sqlStatementPrefix!, sqlLineFragment, out var sqlLineOffset);
                    int sqlStatementStart = 0;

                    while (true)
                    {
                        int terminatorIndex = FindSqlStatementTerminator(sqlCombinedLine, sqlStatementStart);
                        int statementEnd = terminatorIndex >= 0 ? terminatorIndex + 1 : sqlCombinedLine.Length;
                        var sqlStatement = sqlCombinedLine[sqlStatementStart..statementEnd];
                        int sqlStatementLineOffset = Math.Max(0, sqlLineOffset - sqlStatementStart);

                        if (!string.IsNullOrWhiteSpace(sqlStatement))
                        {
                            HashSet<int>? sqlUsingSourceIndices = null;
                            foreach (Match match in SqlMergeUsingSourceRegex.Matches(sqlStatement))
                            {
                                if (IsInsideSqlDoubleQuotedRegion(sqlStatement, match.Index))
                                    continue;
                                var nameGroup = match.Groups["name"];
                                if (nameGroup.Index < sqlStatementLineOffset)
                                    continue;

                                (sqlUsingSourceIndices ??= []).Add(nameGroup.Index);
                            }

                            foreach (Match match in SqlDeleteUsingSourceRegex.Matches(sqlStatement))
                            {
                                if (IsInsideSqlDoubleQuotedRegion(sqlStatement, match.Index))
                                    continue;

                                foreach (Capture capture in match.Groups["name"].Captures)
                                {
                                    if (capture.Index < sqlStatementLineOffset)
                                        continue;

                                    (sqlUsingSourceIndices ??= []).Add(capture.Index);
                                }
                            }

                            foreach (Match match in SqlTopCallSuppressionRegex.Matches(sqlStatement))
                            {
                                var nameGroup = match.Groups["name"];
                                if (nameGroup.Index < sqlStatementLineOffset)
                                    continue;

                                sqlSuppressedCallIndices?.Add(nameGroup.Index + sqlStatementStart - sqlLineOffset);
                            }

                            foreach (Match match in SqlAccessMethodCallSuppressionRegex.Matches(sqlStatement))
                            {
                                if (IsInsideSqlDoubleQuotedRegion(sqlStatement, match.Index))
                                    continue;
                                var nameGroup = match.Groups["name"];
                                if (nameGroup.Index < sqlStatementLineOffset)
                                    continue;
                                if (sqlUsingSourceIndices != null && sqlUsingSourceIndices.Contains(nameGroup.Index))
                                    continue;

                                sqlSuppressedCallIndices?.Add(nameGroup.Index + sqlStatementStart - sqlLineOffset);
                            }

                            foreach (Match match in SqlProcCallRegex.Matches(sqlStatement))
                            {
                                if (IsInsideSqlDoubleQuotedRegion(sqlStatement, match.Index))
                                    continue;
                                var nameGroup = match.Groups["name"];
                                if (nameGroup.Index < sqlStatementLineOffset)
                                    continue;
                                NormalizeSqlIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                                int nameColumn = nameIndex + sqlStatementStart - sqlLineOffset;

                                // Bracketed / backtick-quoted identifiers are explicitly quoted to allow reserved
                                // words (`[ORDER]`, `[USER]`, `[AS]`, `[IMMEDIATE]`, `` `order` ``) as real object
                                // names. Skip the keyword ignore list so a legitimate `EXEC [ORDER]` or
                                // `` CALL `order` `` is not silently dropped.
                                // 角括弧 / バッククォート付き識別子は予約語を識別子として使うための引用形。
                                // `[ORDER]` / `` `order` `` のような正当な名前を落とさないため keyword ignore list をスキップする。
                                if (!wasQuoted && IsIgnoredCallName(language, resolvedName))
                                    continue;
                                if (ShouldSuppressDefinitionCall(resolvedName, nameIndex))
                                    continue;
                                // issue #656: bare `#tempProc` / `##tempProc` after `EXEC` / `EXECUTE` / `CALL`
                                // is ambiguous between a T-SQL temp stored procedure call and a MySQL /
                                // MariaDB `#` line comment. Require same-file T-SQL evidence (prior
                                // `CREATE TABLE #x`, `SELECT ... INTO #x`, mutation target of `#x`, or
                                // `CREATE PROCEDURE|FUNCTION #x`) before emitting a call edge so MySQL
                                // comments stop being misindexed as temp stored procedures.
                                // Quoted forms (`EXEC [#tempProc]` / `` EXEC `#tempProc` ``) bypass the
                                // gate because bracket/backtick quoting is unambiguously an identifier.
                                // issue #656: `EXEC` / `EXECUTE` / `CALL` の直後に並ぶ bare な
                                // `#tempProc` / `##tempProc` は T-SQL の一時ストアド呼び出しと MySQL /
                                // MariaDB の `#` 行コメントのどちらにも見える。同一ファイル内で T-SQL
                                // 側の確立（先行する `CREATE TABLE #x`、`SELECT ... INTO #x`、`#x` の
                                // 変更対象、あるいは `CREATE PROCEDURE|FUNCTION #x`）がある場合だけ
                                // call edge を出すことで、MySQL の `#` コメントを一時ストアドとして
                                // 誤索引しないようにする。引用形 (`EXEC [#tempProc]` /
                                // `` EXEC `#tempProc` ``) はブラケット / バッククォート引用が明確に
                                // 識別子であるため、このゲートを通さず従来どおり edge を出す。
                                if (!wasQuoted
                                    && resolvedName.StartsWith("#", StringComparison.Ordinal)
                                    && (sqlEstablishedTempObjectNames == null || !sqlEstablishedTempObjectNames.Contains(resolvedName)))
                                    continue;

                                var sqlCallContainer = ResolveContainerForCall(nameGroup.Index);
                                AddChainReference(
                                    references, seen, fileId, resolvedName, nameColumn + 1,
                                    "call", context, lineNumber, sqlCallContainer);
                            }

                            foreach (Match match in SqlFromSourceListRegex.Matches(sqlStatement))
                            {
                                if (IsInsideSqlDoubleQuotedRegion(sqlStatement, match.Index))
                                    continue;
                                foreach (Capture capture in match.Groups["name"].Captures)
                                {
                                    if (capture.Index < sqlStatementLineOffset)
                                        continue;
                                    var followedByOpenParen = IsFollowedByOpenParen(sqlStatement, capture.Index + capture.Length);
                                    NormalizeSqlIdentifier(capture.Value, capture.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                                    int nameColumn = nameIndex + sqlStatementStart - sqlLineOffset;
                                    if (!wasQuoted && IsIgnoredCallName(language, resolvedName))
                                        continue;
                                    if (followedByOpenParen)
                                    {
                                        var sqlCallContainer = ResolveContainerForCall(capture.Index);
                                        AddChainReference(
                                            references, seen, fileId, resolvedName, nameColumn + 1,
                                            "call", context, lineNumber, sqlCallContainer);
                                        if (!wasQuoted)
                                        {
                                            sqlSuppressedCallIndices?.Add(
                                                GetSqlCallLikeSuppressionIndex(sqlStatement, capture.Index) + sqlStatementStart - sqlLineOffset);
                                        }
                                        continue;
                                    }
                                    if (resolvedName.StartsWith("#", StringComparison.Ordinal)
                                        && (sqlEstablishedTempObjectNames == null || !sqlEstablishedTempObjectNames.Contains(resolvedName)))
                                        continue;

                                    var sqlReferenceContainer = ResolveContainerForCall(capture.Index);
                                    AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, sqlReferenceContainer);
                                }
                            }

                            foreach (Match match in SqlSourceReferenceRegex.Matches(sqlStatement))
                            {
                                if (IsInsideSqlDoubleQuotedRegion(sqlStatement, match.Index))
                                    continue;
                                foreach (Capture capture in match.Groups["name"].Captures)
                                {
                                    if (capture.Index < sqlStatementLineOffset)
                                        continue;
                                    var followedByOpenParen = IsFollowedByOpenParen(sqlStatement, capture.Index + capture.Length);
                                    NormalizeSqlIdentifier(capture.Value, capture.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                                    int nameColumn = nameIndex + sqlStatementStart - sqlLineOffset;
                                    if (!wasQuoted && IsIgnoredCallName(language, resolvedName))
                                        continue;
                                    if (followedByOpenParen)
                                    {
                                        var sqlCallContainer = ResolveContainerForCall(capture.Index);
                                        AddChainReference(
                                            references, seen, fileId, resolvedName, nameColumn + 1,
                                            "call", context, lineNumber, sqlCallContainer);
                                        if (!wasQuoted)
                                        {
                                            sqlSuppressedCallIndices?.Add(
                                                GetSqlCallLikeSuppressionIndex(sqlStatement, capture.Index) + sqlStatementStart - sqlLineOffset);
                                        }
                                        continue;
                                    }
                                    if (resolvedName.StartsWith("#", StringComparison.Ordinal)
                                        && (sqlEstablishedTempObjectNames == null || !sqlEstablishedTempObjectNames.Contains(resolvedName)))
                                        continue;

                                    var sqlReferenceContainer = ResolveContainerForCall(capture.Index);
                                    AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, sqlReferenceContainer);
                                }
                            }

                            foreach (Match match in SqlMergeUsingSourceRegex.Matches(sqlStatement))
                            {
                                if (IsInsideSqlDoubleQuotedRegion(sqlStatement, match.Index))
                                    continue;
                                var nameGroup = match.Groups["name"];
                                if (nameGroup.Index < sqlStatementLineOffset)
                                    continue;
                                var followedByOpenParen = IsFollowedByOpenParen(sqlStatement, nameGroup.Index + nameGroup.Length);
                                NormalizeSqlIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                                int nameColumn = nameIndex + sqlStatementStart - sqlLineOffset;
                                if (!wasQuoted && IsIgnoredCallName(language, resolvedName))
                                    continue;
                                if (followedByOpenParen)
                                {
                                    var sqlCallContainer = ResolveContainerForCall(nameGroup.Index);
                                    AddChainReference(
                                        references, seen, fileId, resolvedName, nameColumn + 1,
                                        "call", context, lineNumber, sqlCallContainer);
                                    if (!wasQuoted)
                                    {
                                        sqlSuppressedCallIndices?.Add(
                                            GetSqlCallLikeSuppressionIndex(sqlStatement, nameGroup.Index) + sqlStatementStart - sqlLineOffset);
                                    }
                                    continue;
                                }
                                if (resolvedName.StartsWith("#", StringComparison.Ordinal)
                                    && (sqlEstablishedTempObjectNames == null || !sqlEstablishedTempObjectNames.Contains(resolvedName)))
                                    continue;

                                var sqlReferenceContainer = ResolveContainerForCall(nameGroup.Index);
                                AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, sqlReferenceContainer);
                            }

                            foreach (Match match in SqlDeleteUsingSourceRegex.Matches(sqlStatement))
                            {
                                if (IsInsideSqlDoubleQuotedRegion(sqlStatement, match.Index))
                                    continue;

                                foreach (Capture capture in match.Groups["name"].Captures)
                                {
                                    if (capture.Index < sqlStatementLineOffset)
                                        continue;
                                    var followedByOpenParen = IsFollowedByOpenParen(sqlStatement, capture.Index + capture.Length);
                                    NormalizeSqlIdentifier(capture.Value, capture.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                                    int nameColumn = nameIndex + sqlStatementStart - sqlLineOffset;
                                    if (!wasQuoted && IsIgnoredCallName(language, resolvedName))
                                        continue;
                                    if (followedByOpenParen)
                                    {
                                        var sqlCallContainer = ResolveContainerForCall(capture.Index);
                                        AddChainReference(
                                            references, seen, fileId, resolvedName, nameColumn + 1,
                                            "call", context, lineNumber, sqlCallContainer);
                                        if (!wasQuoted)
                                        {
                                            sqlSuppressedCallIndices?.Add(
                                                GetSqlCallLikeSuppressionIndex(sqlStatement, capture.Index) + sqlStatementStart - sqlLineOffset);
                                        }
                                        continue;
                                    }
                                    if (resolvedName.StartsWith("#", StringComparison.Ordinal)
                                        && (sqlEstablishedTempObjectNames == null || !sqlEstablishedTempObjectNames.Contains(resolvedName)))
                                        continue;

                                    var sqlReferenceContainer = ResolveContainerForCall(capture.Index);
                                    AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, sqlReferenceContainer);
                                }
                            }

                            foreach (Match match in SqlSelectIntoTempTargetStatementRegex.Matches(sqlStatement))
                            {
                                if (IsInsideSqlDoubleQuotedRegion(sqlStatement, match.Index))
                                    continue;
                                var nameGroup = match.Groups["name"];
                                if (nameGroup.Index < sqlStatementLineOffset)
                                    continue;
                                NormalizeSqlIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                                int nameColumn = nameIndex + sqlStatementStart - sqlLineOffset;
                                if (!wasQuoted && IsIgnoredCallName(language, resolvedName))
                                    continue;

                                var sqlReferenceContainer = ResolveContainerForCall(nameGroup.Index);
                                AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, sqlReferenceContainer);
                            }

                            foreach (Match match in SqlTargetReferenceRegex.Matches(sqlStatement))
                            {
                                if (IsInsideSqlDoubleQuotedRegion(sqlStatement, match.Index))
                                    continue;
                                var nameGroup = match.Groups["name"];
                                if (nameGroup.Index < sqlStatementLineOffset)
                                    continue;
                                NormalizeSqlIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                                int nameColumn = nameIndex + sqlStatementStart - sqlLineOffset;
                                if (!wasQuoted && IsIgnoredCallName(language, resolvedName))
                                    continue;
                                if (!wasQuoted
                                    && string.Equals(resolvedName, "SET", StringComparison.OrdinalIgnoreCase)
                                    && match.Value.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var sqlReferenceContainer = ResolveContainerForCall(nameGroup.Index);
                                AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, sqlReferenceContainer);
                                if (IsFollowedByOpenParen(sqlStatement, nameGroup.Index + nameGroup.Length))
                                {
                                    sqlSuppressedCallIndices?.Add(
                                        GetSqlCallLikeSuppressionIndex(sqlStatement, nameGroup.Index) + sqlStatementStart - sqlLineOffset);
                                }
                            }

                            foreach (Match match in SqlTruncateTargetRegex.Matches(sqlStatement))
                            {
                                if (IsInsideSqlDoubleQuotedRegion(sqlStatement, match.Index))
                                    continue;

                                foreach (Capture capture in match.Groups["name"].Captures)
                                {
                                    if (capture.Index < sqlStatementLineOffset)
                                        continue;
                                    NormalizeSqlIdentifier(capture.Value, capture.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                                    int nameColumn = nameIndex + sqlStatementStart - sqlLineOffset;
                                    if (!wasQuoted && IsIgnoredCallName(language, resolvedName))
                                        continue;

                                    var sqlReferenceContainer = ResolveContainerForCall(capture.Index);
                                    AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, sqlReferenceContainer);
                                }
                            }
                        }

                        if (terminatorIndex < 0)
                            break;

                        CollectSqlTempObjectNamesFromStatement(sqlStatement, sqlEstablishedTempObjectNames!);
                        sqlStatementStart = terminatorIndex + 1;
                        while (sqlStatementStart < sqlCombinedLine.Length && char.IsWhiteSpace(sqlCombinedLine[sqlStatementStart]))
                            sqlStatementStart++;
                    }

                    sqlStatementPrefix = AdvanceSqlStatementPrefix(sqlCombinedLine, sqlStatementStart, sqlLineEndedByLineComment);
                }
            }

            if (language == "css")
            {
                EmitCssScssReferences(
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
                EmitCssScssReferences(
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
                EmitPhpStaticAccessReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                EmitPhpObjectMemberAccessReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }

            // JavaScript / TypeScript zero-arg constructor calls may omit `()`: `new Foo;`.
            // Emit `instantiate` directly so graph queries do not treat them as unused code.
            // JavaScript / TypeScript では `new Foo;` のように `()` を省略できるため、
            // 専用パスで `instantiate` を発行して graph query から取りこぼさないようにする。
            if (language is "javascript" or "typescript")
            {
                foreach (Match match in JsTsParenlessConstructorRegex.Matches(preparedLine))
                {
                    var rawName = match.Groups["name"].Value;
                    var nameIndex = match.Groups["name"].Index;
                    var trailingProbe = match.Index + match.Length;
                    while (trailingProbe < preparedLine.Length && char.IsWhiteSpace(preparedLine[trailingProbe]))
                        trailingProbe++;

                    if (trailingProbe >= preparedLine.Length)
                    {
                        if (NextNonEmptyPreparedLineStartsWithJsContinuation(preparedLines, i))
                            continue;
                    }
                    else
                    {
                        if (preparedLine[trailingProbe] is '(' or '.' or '[')
                            continue;

                        if (preparedLine[trailingProbe] == '?'
                            && trailingProbe + 1 < preparedLine.Length
                            && preparedLine[trailingProbe + 1] == '.')
                        {
                            continue;
                        }
                    }

                    var initContainer = ResolveContainerForCall(nameIndex);
                    AddReference(references, seen, fileId, rawName, nameIndex, "instantiate", context, lineNumber, initContainer);
                }
            }

            void AddCallLikeReference(string name, int callIndex)
            {
                var normalizedName = language == "fsharp" && IsFSharpOperatorCallName(name)
                    ? $"operator {name}"
                    : language == "rust"
                        ? NormalizeRustIdentifier(name)
                        : NormalizeAtPrefixedIdentifier(name);

                if (language == "rust" && IsRustFunctionDeclarationCallSite(preparedLine, callIndex))
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
                      && IsCSharpPatternHeadCallSite(preparedLines, i, preparedLine, callIndex);
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

            static bool IsFSharpOperatorCallName(string name)
            {
                if (name.Length < 2)
                    return false;

                foreach (var ch in name)
                {
                    if ("!%&*+-./:<=>?@^|~".IndexOf(ch) < 0)
                        return false;
                }

                return true;
            }

            void EmitRubyCommandTargetReferences(string name, int callIndex)
            {
                if (language != "ruby" || !RubyCommandTargetReferenceNames.Contains(name))
                    return;

                var argsStart = callIndex + name.Length;
                while (argsStart < originalLine.Length && char.IsWhiteSpace(originalLine[argsStart]))
                    argsStart++;

                if (argsStart < originalLine.Length && originalLine[argsStart] == '(')
                    argsStart++;

                while (argsStart < originalLine.Length && char.IsWhiteSpace(originalLine[argsStart]))
                    argsStart++;

                if (argsStart >= originalLine.Length)
                    return;

                var tail = originalLine[argsStart..];
                var commentIndex = tail.IndexOf('#');
                if (commentIndex >= 0)
                    tail = tail[..commentIndex];

                var matchedAny = false;
                foreach (Match match in RubyCommandTargetTokenRegex.Matches(tail))
                {
                    var rawToken = match.Groups["token"].Value;
                    if (rawToken.Length == 0)
                        continue;

                    if (string.Equals(rawToken, "do", StringComparison.Ordinal)
                        || string.Equals(rawToken, "end", StringComparison.Ordinal)
                        || string.Equals(rawToken, "then", StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (string.Equals(name, "raise", StringComparison.Ordinal))
                    {
                        if (rawToken[0] == ':' || rawToken[0] == '\'' || rawToken[0] == '"')
                            return;
                        if (!IsRubyIdentifierStart(rawToken[0]))
                            return;
                    }
                    else if (RubyCommandTargetSingleTokenNames.Contains(name) && matchedAny)
                    {
                        break;
                    }
                    else if (rawToken[0] == '\'' || rawToken[0] == '"')
                    {
                        if (!string.Equals(name, "require", StringComparison.Ordinal)
                            && !string.Equals(name, "define_method", StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }

                    var token = NormalizeRubyCommandTargetToken(rawToken);
                    if (string.IsNullOrWhiteSpace(token))
                        continue;

                    var targetContainer = ResolveContainerForCall(argsStart + match.Groups["token"].Index);
                    AddReference(references, seen, fileId, token, argsStart + match.Groups["token"].Index, "reference", context, lineNumber, targetContainer);
                    matchedAny = true;

                    if (RubyCommandTargetSingleTokenNames.Contains(name))
                        break;
                }
            }

            if (language is "batch" && !IsBatchCommentLine(originalLine))
            {
                for (var segmentStart = 0; segmentStart < preparedLine.Length;)
                {
                    var segmentEnd = segmentStart;
                    while (segmentEnd < preparedLine.Length && !IsBatchCommandSeparator(preparedLine, segmentEnd))
                        segmentEnd++;

                    ProcessBatchJumpSegment(
                        preparedLine[segmentStart..segmentEnd],
                        segmentStart,
                        references,
                        seen,
                        context,
                        fileId,
                        lineNumber,
                        ResolveContainerForCall);

                    var segment = preparedLine[segmentStart..segmentEnd].TrimStart();
                    if (segment.Length > 0)
                    {
                        if (segment[0] == '@')
                            segment = segment[1..].TrimStart();
                        if (segment.Length > 0 && (segment.StartsWith("::", StringComparison.Ordinal) || IsBatchRemKeyword(segment, 0)))
                            break;
                    }

                    segmentStart = segmentEnd;
                    while (segmentStart < preparedLine.Length && (char.IsWhiteSpace(preparedLine[segmentStart]) || IsBatchCommandSeparator(preparedLine, segmentStart)))
                        segmentStart++;
                }
            }

            var matchedCallIndices = new HashSet<int>();
            if (language is "powershell")
            {
                foreach (Match match in PowerShellCallRegex.Matches(preparedLine))
                {
                    var name = match.Groups["name"].Value;
                    var callIndex = match.Groups["name"].Index;
                    AddCallLikeReference(name, callIndex);
                }
            }
            else if (language is "shell")
            {
                foreach (Match match in ShellCommandCallRegex.Matches(preparedLine))
                {
                    var name = match.Groups["name"].Value;
                    if (shellCallableNames == null || !shellCallableNames.Contains(name))
                        continue;

                    var callIndex = match.Groups["name"].Index;
                    AddCallLikeReference(name, callIndex);
                }

                var shellSourceLine = StripShellComment(originalLine);
                foreach (Match match in ShellSourceReferenceRegex.Matches(shellSourceLine))
                {
                    var name = NormalizeShellSourceTargetToken(match.Groups["name"].Value);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var sourceIndex = match.Groups["name"].Index;
                    AddReference(references, seen, fileId, name, sourceIndex, "reference", context, lineNumber, ResolveContainerForCall(sourceIndex));
                }

                if (shellGlobalAliasNames != null && shellGlobalAliasNames.Count > 0)
                {
                    var trimmedPreparedLine = preparedLine.TrimStart();
                    if (!trimmedPreparedLine.StartsWith("alias", StringComparison.Ordinal))
                    {
                        foreach (var aliasName in shellGlobalAliasNames)
                        {
                            var searchIndex = 0;
                            while (searchIndex < preparedLine.Length)
                            {
                                var aliasIndex = preparedLine.IndexOf(aliasName, searchIndex, StringComparison.Ordinal);
                                if (aliasIndex < 0)
                                    break;

                                if (!IsShellGlobalAliasReferenceBoundary(preparedLine, aliasIndex, aliasName.Length))
                                {
                                    searchIndex = aliasIndex + aliasName.Length;
                                    continue;
                                }

                                AddCallLikeReference(aliasName, aliasIndex);
                                searchIndex = aliasIndex + aliasName.Length;
                            }
                        }
                    }
                }
            }
            else
            {
                foreach (Match match in CallRegex.Matches(preparedLine))
                {
                    var name = match.Groups["name"].Value;
                    var callIndex = match.Groups["name"].Index;
                    if (language == "rust" && IsRustRawIdentifierPrefix(preparedLine, callIndex))
                        continue;
                    if (sqlSuppressedCallIndices != null && sqlSuppressedCallIndices.Contains(callIndex))
                        continue;
                    matchedCallIndices.Add(callIndex);
                    AddCallLikeReference(name, callIndex);
                    if (language == "ruby")
                        EmitRubyCommandTargetReferences(name, callIndex);
                }

                if (language == "ruby")
                {
                    foreach (Match match in RubyCommandCallRegex.Matches(preparedLine))
                    {
                        var name = match.Groups["name"].Value;
                        var callIndex = match.Groups["name"].Index;
                        matchedCallIndices.Add(callIndex);
                        AddCallLikeReference(name, callIndex);
                        EmitRubyCommandTargetReferences(name, callIndex);
                    }

                    foreach (Match match in RubyBlockCallRegex.Matches(preparedLine))
                    {
                        var name = match.Groups["name"].Value;
                        var callIndex = match.Groups["name"].Index;
                        AddCallLikeReference(name, callIndex);
                    }
                }

                if (language is "swift" or "kotlin")
                {
                    foreach (Match match in TrailingLambdaCallRegex.Matches(preparedLine))
                    {
                        var name = match.Groups["name"].Value;
                        var callIndex = match.Groups["name"].Index;
                        if (IsTrailingLambdaInheritanceClause(preparedLine, callIndex))
                            continue;
                        AddCallLikeReference(name, callIndex);
                    }
                }

                if (language == "fsharp")
                {
                    foreach (Match match in FSharpPipelineCallRegex.Matches(preparedLine))
                    {
                        var name = match.Groups["name"].Value;
                        var callIndex = match.Groups["name"].Index;
                        AddCallLikeReference(name, callIndex);
                    }

                    foreach (Match match in FSharpSpaceApplicationCallRegex.Matches(preparedLine))
                    {
                        var name = match.Groups["name"].Value;
                        var callIndex = match.Groups["name"].Index;
                        AddCallLikeReference(name, callIndex);
                    }

                    foreach (Match match in FSharpOperatorCallRegex.Matches(preparedLine))
                    {
                        var name = match.Groups["name"].Value;
                        if (FSharpIgnoredOperatorCallNames.Contains(name))
                            continue;

                        var definitionMatch = FSharpOperatorDefinitionCallRegex.Match(preparedLine);
                        if (definitionMatch.Success
                            && string.Equals(definitionMatch.Groups["name"].Value, name, StringComparison.Ordinal)
                            && match.Groups["name"].Index == definitionMatch.Groups["name"].Index)
                        {
                            continue;
                        }

                        var callIndex = match.Groups["name"].Index;
                        AddCallLikeReference(name, callIndex);
                    }
                }

                if (language == "scala")
                {
                    foreach (Match match in ScalaTrailingBlockCallRegex.Matches(preparedLine))
                    {
                        var name = match.Groups["name"].Value;
                        var callIndex = match.Groups["name"].Index;
                        if (ScalaIgnoredBlockCallNames.Contains(name))
                            continue;
                        AddCallLikeReference(name, callIndex);
                    }
                }
                else if (language == "gradle")
                {
                    void AddGradleDslReference(string name, int callIndex)
                    {
                        var normalizedName = NormalizeAtPrefixedIdentifier(name);
                        var callContainer = ResolveContainerForCall(callIndex);
                        AddReference(references, seen, fileId, normalizedName, callIndex, "call", context, lineNumber, callContainer);
                    }

                    foreach (Match match in GradleBlockCallRegex.Matches(preparedLine))
                    {
                        var name = match.Groups["name"].Value;
                        var callIndex = match.Groups["name"].Index;
                        AddGradleDslReference(name, callIndex);
                    }

                    foreach (Match match in GradleCommandCallRegex.Matches(preparedLine))
                    {
                        var name = match.Groups["name"].Value;
                        var callIndex = match.Groups["name"].Index;
                        AddGradleDslReference(name, callIndex);
                    }
                }

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
                foreach (Match match in RustRawIdentifierCallRegex.Matches(preparedLine))
                {
                    var name = match.Groups["name"].Value;
                    var callIndex = match.Groups["name"].Index;
                    AddCallLikeReference(name, callIndex);
                }

                foreach (Match match in RustMacroCallRegex.Matches(preparedLine))
                {
                    var name = match.Groups["name"].Value;
                    var callIndex = match.Groups["name"].Index;
                    AddCallLikeReference(name, callIndex);
                }
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
            else if (language is "java" or "kotlin" or "scala")
            {
                EmitJavaMethodReferenceReferences(
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
                EmitCSharpQualifiedEnumMemberReferences(
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

            if (language == "python")
            {
                foreach (Match match in PythonDecoratorRegex.Matches(preparedLine))
                {
                    var name = match.Groups["name"].Value;
                    if (IsIgnoredCallName(language, name))
                        continue;
                    if (definitionNames != null && definitionNames.Contains(name))
                        continue;
                    AddReference(references, seen, fileId, match, "decorator", context, lineNumber, container);
                }
            }

            if (language == "r")
            {
                foreach (Match match in RNamespaceReferenceRegex.Matches(preparedLine))
                {
                    var package = match.Groups["package"].Value;
                    var separator = match.Groups["sep"].Value;
                    var name = match.Groups["name"].Value;
                    AddReference(references, seen, fileId, $"{package}{separator}{name}", match.Groups["package"].Index, "reference", context, lineNumber, container);
                    if (definitionNames != null && definitionNames.Contains(name))
                        continue;
                    AddReference(references, seen, fileId, name, match.Groups["name"].Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (language == "csharp")
        {
            EmitCSharpSwitchExpressionTypePatternReferences(
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

            FlushPendingCSharpMultiLineTypePatternReference(
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

    private static void AddReference(
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

    private static void AddReference(
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

    private static void EmitCssReferences(
        string preparedLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        foreach (Match match in CssCustomPropertyReferenceRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (definitionNames != null && definitionNames.Contains(nameGroup.Value))
                continue;

            AddReference(references, seen, fileId, nameGroup.Value, nameGroup.Index, "reference", context, lineNumber, container);
        }

        foreach (Match match in CssAnimationNameReferenceRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (definitionNames != null && definitionNames.Contains(nameGroup.Value))
                continue;

            AddReference(references, seen, fileId, nameGroup.Value, nameGroup.Index, "reference", context, lineNumber, container);
        }

        foreach (Match match in CssAnimationShorthandValueRegex.Matches(preparedLine))
        {
            EmitCssAnimationShorthandReferences(
                match.Groups["value"].Value,
                match.Groups["value"].Index,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                definitionNames,
                container);
        }

        EmitCssClassSelectorReferences(
            preparedLine,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            definitionNames,
            container);
    }

    private static void EmitDockerfileStageReferences(
        string preparedLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? stageNames,
        SymbolRecord? container)
    {
        if (stageNames == null || stageNames.Count == 0)
            return;

        var fromMatch = DockerfileStageReferenceRegex.Match(preparedLine);
        if (fromMatch.Success)
        {
            var name = fromMatch.Groups["name"].Value;
            if (stageNames.Contains(name))
                AddReference(references, seen, fileId, name, fromMatch.Groups["name"].Index, "call", context, lineNumber, container);
        }

        foreach (Match match in DockerfileCopyFromReferenceRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (!stageNames.Contains(name))
                continue;

            AddReference(references, seen, fileId, name, match.Groups["name"].Index, "call", context, lineNumber, container);
        }
    }

    private static void EmitCobolReferences(
        string rawLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container,
        IReadOnlyList<SymbolRecord>? cobolCallableSymbols)
    {
        foreach (Match match in CobolCallRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "call", context, lineNumber, container);
        }

        foreach (Match match in CobolCopyRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolGotoRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "call", context, lineNumber, container);
        }

        foreach (Match match in CobolSetRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolOpenRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolSearchRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolSimpleReferenceRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolMoveRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolAddRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolSubtractRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolMultiplyRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolDivideRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolComputeRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolStringRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolUnstringRegex.Matches(rawLine))
        {
            EmitCobolNamedReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        foreach (Match match in CobolPerformRegex.Matches(rawLine))
        {
            var endName = match.Groups["end"].Value;
            if (!string.IsNullOrWhiteSpace(endName)
                && TryAddCobolPerformRangeReferences(
                    cobolCallableSymbols,
                    match.Groups["name"].Value,
                    endName,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    container))
            {
                continue;
            }

            EmitCobolNamedReference(references, seen, fileId, match, "call", context, lineNumber, container);
        }
    }

    private static void EmitCobolNamedReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Match match,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var name = match.Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(name))
            return;

        AddReference(references, seen, fileId, name.ToUpperInvariant(), match.Groups["name"].Index, referenceKind, context, lineNumber, container);
    }

    private static bool TryAddCobolPerformRangeReferences(
        IReadOnlyList<SymbolRecord>? cobolCallableSymbols,
        string startName,
        string endName,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container)
    {
        if (cobolCallableSymbols == null || cobolCallableSymbols.Count == 0)
            return false;

        var normalizedStartName = startName.Trim();
        var normalizedEndName = endName.Trim();
        if (normalizedStartName.Length == 0 || normalizedEndName.Length == 0)
            return false;

        var startSymbol = FindCobolCallableSymbol(cobolCallableSymbols, normalizedStartName);
        if (startSymbol == null)
            return false;

        var startLine = startSymbol.Line;
        var endSymbol = FindCobolCallableSymbol(cobolCallableSymbols, normalizedEndName, startLine);
        if (endSymbol == null)
            return false;

        var lowerLine = Math.Min(startSymbol.Line, endSymbol.Line);
        var upperLine = Math.Max(startSymbol.Line, endSymbol.Line);
        var emittedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emittedAny = false;

        foreach (var symbol in cobolCallableSymbols)
        {
            if (symbol.Line < lowerLine || symbol.Line > upperLine)
                continue;

            if (!emittedNames.Add(symbol.Name))
                continue;

            var nameIndex = Math.Max(0, symbol.StartColumn.GetValueOrDefault() - 1);
            AddReference(references, seen, fileId, symbol.Name, nameIndex, "call", context, lineNumber, container);
            emittedAny = true;
        }

        return emittedAny;
    }

    private static SymbolRecord? FindCobolCallableSymbol(
        IReadOnlyList<SymbolRecord> cobolCallableSymbols,
        string targetName,
        int? minimumLine = null)
    {
        SymbolRecord? fallback = null;

        foreach (var symbol in cobolCallableSymbols)
        {
            if (!string.Equals(symbol.Name, targetName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (minimumLine == null || symbol.Line >= minimumLine.Value)
                return symbol;

            fallback ??= symbol;
        }

        return fallback;
    }

    private static bool IsJsxFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".jsx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tsx", StringComparison.OrdinalIgnoreCase);
    }

    private static void EmitCssAnimationShorthandReferences(
        string value,
        int valueIndex,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        var segmentStart = 0;
        var parenDepth = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length)
            {
                var ch = value[i];
                if (ch == '(')
                {
                    parenDepth++;
                    continue;
                }

                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    continue;
                }

                if (ch != ',' || parenDepth > 0)
                    continue;
            }

            EmitCssAnimationShorthandSegmentReference(
                value,
                valueIndex,
                segmentStart,
                i,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                definitionNames,
                container);
            segmentStart = i + 1;
        }
    }

    private static void EmitCssAnimationShorthandSegmentReference(
        string value,
        int valueIndex,
        int segmentStart,
        int segmentEnd,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        var cursor = segmentStart;
        while (cursor < segmentEnd && char.IsWhiteSpace(value[cursor]))
            cursor++;

        while (cursor < segmentEnd)
        {
            var tokenStart = cursor;
            while (cursor < segmentEnd && !char.IsWhiteSpace(value[cursor]))
                cursor++;

            var token = value[tokenStart..cursor];
            if (!IsCssAnimationNameToken(token))
                continue;
            if (definitionNames != null && definitionNames.Contains(token))
                return;

            AddReference(references, seen, fileId, token, valueIndex + tokenStart, "reference", context, lineNumber, container);
            return;
        }
    }

    private static void EmitCssClassSelectorReferences(
        string preparedLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        var segmentStart = 0;
        while (segmentStart < preparedLine.Length)
        {
            var braceIndex = preparedLine.IndexOf('{', segmentStart);
            var segmentEnd = braceIndex >= 0 ? braceIndex : preparedLine.Length;
            var trimmedStart = segmentStart;
            while (trimmedStart < segmentEnd && char.IsWhiteSpace(preparedLine[trimmedStart]))
                trimmedStart++;

            if (trimmedStart < segmentEnd && preparedLine[trimmedStart] != '@')
            {
                var selectorSegment = preparedLine[trimmedStart..segmentEnd];
                foreach (var (partStart, partEnd) in EnumerateCssSelectorListSegments(selectorSegment))
                {
                    var selectorPart = selectorSegment[partStart..partEnd];
                    if (!ContainsCssClassSelectorReferenceCandidate(selectorPart))
                        continue;

                    var selectorPartTrimStart = 0;
                    while (selectorPartTrimStart < selectorPart.Length && char.IsWhiteSpace(selectorPart[selectorPartTrimStart]))
                        selectorPartTrimStart++;

                    var selectorPartBody = selectorPart[selectorPartTrimStart..];
                    foreach (Match match in CssClassSelectorReferenceRegex.Matches(selectorPartBody))
                    {
                        var nameGroup = match.Groups["name"];
                        var name = "." + nameGroup.Value;
                        if (definitionNames != null && definitionNames.Contains(name))
                            continue;

                        AddReference(
                            references,
                            seen,
                            fileId,
                            name,
                            trimmedStart + partStart + selectorPartTrimStart + match.Groups["name"].Index - 1,
                            "reference",
                            context,
                            lineNumber,
                            container);
                    }
                }
            }

            if (braceIndex < 0)
                break;

            segmentStart = braceIndex + 1;
        }
    }

    private static IEnumerable<(int Start, int End)> EnumerateCssSelectorListSegments(string selectorSegment)
    {
        var segmentStart = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var index = 0; index < selectorSegment.Length; index++)
        {
            var ch = selectorSegment[index];
            if (ch == '(')
            {
                parenDepth++;
                continue;
            }

            if (ch == ')' && parenDepth > 0)
            {
                parenDepth--;
                continue;
            }

            if (ch == '[')
            {
                bracketDepth++;
                continue;
            }

            if (ch == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                continue;
            }

            if (ch == ',' && parenDepth == 0 && bracketDepth == 0)
            {
                yield return (segmentStart, index);
                segmentStart = index + 1;
            }
        }

        yield return (segmentStart, selectorSegment.Length);
    }

    private static bool ContainsCssClassSelectorReferenceCandidate(string selectorPart)
    {
        var bracketDepth = 0;
        char quote = '\0';
        for (var index = 0; index < selectorPart.Length; index++)
        {
            var ch = selectorPart[index];
            if (quote != '\0')
            {
                if (ch == quote && (index == 0 || selectorPart[index - 1] != '\\'))
                    quote = '\0';
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '[')
            {
                bracketDepth++;
                continue;
            }

            if (ch == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                continue;
            }

            if (bracketDepth == 0 && ch == '.')
                return true;
        }

        return false;
    }

    private static bool IsCssAnimationNameToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (CssAnimationShorthandIgnoredTokens.Contains(token))
            return false;
        if (token.IndexOf('(') >= 0 || token.IndexOf(')') >= 0 || token.IndexOf(',') >= 0
            || token.IndexOf('/') >= 0 || token.IndexOf(':') >= 0 || token.IndexOf(';') >= 0)
            return false;
        if (IsCssAnimationTimeToken(token) || IsCssAnimationNumberToken(token))
            return false;
        if (token.StartsWith("--", StringComparison.Ordinal))
            return false;
        if (!(char.IsLetter(token[0]) || token[0] == '_' || token[0] == '-'))
            return false;
        if (token[0] == '-' && token.Length > 1 && (token[1] == '-' || char.IsDigit(token[1])))
            return false;

        for (var i = 1; i < token.Length; i++)
        {
            if (char.IsLetterOrDigit(token[i]) || token[i] == '_' || token[i] == '-')
                continue;
            return false;
        }

        return true;
    }

    private static bool IsCssAnimationTimeToken(string token)
    {
        if (token.Length < 2)
            return false;

        var unitLength = token.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
            ? 2
            : token.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
        if (unitLength == 0 || token.Length == unitLength)
            return false;

        var numberPart = token[..^unitLength];
        var sawDigit = false;
        var sawDot = false;
        foreach (var ch in numberPart)
        {
            if (char.IsDigit(ch))
            {
                sawDigit = true;
                continue;
            }

            if (ch == '.' && !sawDot)
            {
                sawDot = true;
                continue;
            }

            return false;
        }

        return sawDigit;
    }

    private static bool IsCssAnimationNumberToken(string token)
    {
        if (token.Length == 0 || token.IndexOfAny(['(', ')', ',', '/', ':', ';']) >= 0)
            return false;
        if (!(char.IsDigit(token[0]) || token[0] == '.'))
            return false;

        var sawDigit = false;
        var sawDot = false;
        foreach (var ch in token)
        {
            if (char.IsDigit(ch))
            {
                sawDigit = true;
                continue;
            }

            if (ch == '.' && !sawDot)
            {
                sawDot = true;
                continue;
            }

            return false;
        }

        return sawDigit;
    }

    private static void NormalizeSqlIdentifier(
        string rawName,
        int rawIndex,
        out string resolvedName,
        out int resolvedIndex,
        out bool wasQuoted)
    {
        if (rawName.Length >= 2
            && ((rawName[0] == '[' && rawName[^1] == ']')
                || (rawName[0] == '`' && rawName[^1] == '`')
                || (rawName[0] == '"' && rawName[^1] == '"')))
        {
            // Normalize `[name]` / `` `name` `` / `"name"` to `name` so SQL-specific regexes and the shared
            // CallRegex converge on the same symbol spelling and dedupe key.
            // `[name]` / `` `name` `` / `"name"` を `name` に正規化し、SQL 専用 regex と共有 CallRegex の
            // symbol 名と dedupe key を一致させる。
            resolvedName = rawName.Substring(1, rawName.Length - 2);
            if (rawName[0] == '"')
                resolvedName = resolvedName.Replace("\"\"", "\"", StringComparison.Ordinal);
            else if (rawName[0] == '[')
                resolvedName = resolvedName.Replace("]]", "]", StringComparison.Ordinal);
            resolvedIndex = rawIndex + 1;
            wasQuoted = true;
            return;
        }

        resolvedName = rawName;
        resolvedIndex = rawIndex;
        wasQuoted = false;
    }

    private static bool IsFollowedByOpenParen(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        return index < line.Length && line[index] == '(';
    }

    private static int GetSqlCallLikeSuppressionIndex(string line, int index)
    {
        while (index < line.Length && line[index] == '#')
            index++;

        return index;
    }

    private static string CombineSqlStatementPrefix(string prefix, string line, out int lineOffset)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            lineOffset = 0;
            return line;
        }

        lineOffset = prefix.Length + 1;
        return prefix + "\n" + line;
    }

    private static string AdvanceSqlStatementPrefix(
        string combined,
        int statementStart,
        bool lineEndedByLineComment)
    {
        var remaining = statementStart == 0 ? combined : combined[statementStart..];
        if (!lineEndedByLineComment)
            return remaining;

        return CanSqlStatementRequireLineCommentCarry(remaining) ? remaining : string.Empty;
    }

    private static bool ShouldFlushSqlTempObjectPrefixAtLineBoundary(
        string prefix,
        string nextLine)
    {
        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(nextLine))
            return false;
        if (!CanSqlStatementEstablishTempObject(prefix))
            return false;

        return StartsSqlTopLevelStatement(nextLine);
    }

    private static bool CanSqlStatementEstablishTempObject(string statement)
    {
        if (statement.IndexOf('#') < 0)
            return false;

        return SqlTargetReferenceRegex.IsMatch(statement)
            || SqlTruncateTargetRegex.IsMatch(statement)
            || SqlSelectIntoTempTargetStatementRegex.IsMatch(statement)
            || SqlCreateTempTableRegex.IsMatch(statement)
            || SqlCreateTempRoutineRegex.IsMatch(statement);
    }

    private static bool CanSqlStatementRequireLineCommentCarry(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
            return false;

        return CanSqlStatementEstablishTempObject(statement)
            || SqlTargetReferencePrefixRegex.IsMatch(statement)
            || SqlFromListContinuationPrefixRegex.IsMatch(statement)
            || SqlSelectIntoTempPrefixRegex.IsMatch(statement)
            || SqlDeleteUsingPrefixRegex.IsMatch(statement)
            || SqlDeleteUsingListContinuationPrefixRegex.IsMatch(statement)
            || SqlMergeUsingPrefixRegex.IsMatch(statement)
            || SqlMergeTargetHintContinuationPrefixRegex.IsMatch(statement);
    }

    private static bool StartsSqlTopLevelStatement(string line)
    {
        int index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length || !char.IsLetter(line[index]))
            return false;

        int start = index;
        while (index < line.Length && char.IsLetter(line[index]))
            index++;

        var keyword = line[start..index].ToUpperInvariant();
        if (keyword == "WITH")
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            return index >= line.Length || line[index] != '(';
        }

        return keyword switch
        {
            "SELECT" => true,
            "INSERT" => true,
            "UPDATE" => true,
            "DELETE" => true,
            "MERGE" => true,
            "CREATE" => true,
            "ALTER" => true,
            "DROP" => true,
            "TRUNCATE" => true,
            "SET" => true,
            "DECLARE" => true,
            "IF" => true,
            "WHILE" => true,
            "DO" => true,
            "BEGIN" => true,
            "EXEC" => true,
            "EXECUTE" => true,
            "CALL" => true,
            _ => false,
        };
    }

    private static int FindSqlStatementTerminator(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ';')
                return i;
            if (c == '`')
            {
                int closing = text.IndexOf('`', i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
                continue;
            }
            if (c == '[')
            {
                int closing = text.IndexOf(']', i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
                continue;
            }
            if (c == '"')
            {
                int closing = FindClosingSqlDoubleQuote(text, i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
            }
        }

        return -1;
    }

    private static int FindClosingSqlDoubleQuote(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;
            if (i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static int FindClosingSqlSingleQuote(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                continue;
            }
            if (text[i] != '\'')
                continue;
            if (i + 1 < text.Length && text[i + 1] == '\'')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static bool IsInsideSqlDoubleQuotedRegion(string text, int index)
    {
        if (index <= 0)
            return false;

        bool inside = false;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;
            if (inside && i + 1 < index && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            inside = !inside;
        }

        return inside;
    }

    private static bool TryReadSqlDollarQuoteDelimiter(
        string line,
        int index,
        out string delimiter)
    {
        delimiter = string.Empty;
        if (index < 0 || index >= line.Length || line[index] != '$')
            return false;
        if (index > 0 && (char.IsLetterOrDigit(line[index - 1]) || line[index - 1] == '_'))
            return false;
        if (index + 1 >= line.Length)
            return false;
        if (line[index + 1] == '$')
        {
            delimiter = "$$";
            return true;
        }
        if (!(char.IsLetter(line[index + 1]) || line[index + 1] == '_'))
            return false;

        int probe = index + 2;
        while (probe < line.Length && (char.IsLetterOrDigit(line[probe]) || line[probe] == '_'))
            probe++;
        if (probe >= line.Length || line[probe] != '$')
            return false;

        delimiter = line[index..(probe + 1)];
        return true;
    }

    private static int SkipWhitespaceAhead(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static void CollectSqlTempObjectNamesFromStatement(
        string statement,
        HashSet<string> names)
    {
        CollectSqlTempObjectNamesFromMatches(SqlTargetReferenceRegex.Matches(statement), statement, names);
        CollectSqlTempObjectNamesFromMatches(SqlTruncateTargetRegex.Matches(statement), statement, names);
        CollectSqlTempObjectNamesFromMatches(SqlSelectIntoTempTargetStatementRegex.Matches(statement), statement, names);
        CollectSqlTempObjectNamesFromMatches(SqlCreateTempTableRegex.Matches(statement), statement, names);
        CollectSqlTempObjectNamesFromMatches(SqlCreateTempRoutineRegex.Matches(statement), statement, names);
    }

    private static void CollectSqlTempObjectNamesFromMatches(MatchCollection matches, string statement, HashSet<string> names)
    {
        foreach (Match match in matches)
        {
            if (IsInsideSqlDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Captures.Count == 0)
                continue;

            foreach (Capture capture in nameGroup.Captures)
            {
                NormalizeSqlIdentifier(capture.Value, capture.Index, out var resolvedName, out _, out _);
                if (resolvedName.StartsWith("#", StringComparison.Ordinal))
                    names.Add(resolvedName);
            }
        }
    }

    /// <summary>
    /// Emit one `type_reference` row per dot-segment of a captured argument. Columns are
    /// computed relative to the original line so tooling can jump to the exact identifier.
    /// 捕捉した引数の dot-segment ごとに `type_reference` 行を発行する。列位置は元の行基準で計算する。
    /// </summary>
    private static void AddTypeReferenceSegments(
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

    private static void EmitCSharpTypePositionReferences(
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

    private static void AdvanceCSharpMultiLineTypePatternState(
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

    private static void FlushPendingCSharpMultiLineTypePatternReference(
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

    private static void StartWaitingForCSharpMultiLineTypePatternHead(ref CSharpMultiLineTypePatternState state)
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

    private static bool HasTrailingCSharpTypePatternIntro(string text, Regex introRegex)
    {
        foreach (Match match in introRegex.Matches(text))
        {
            if (HasOnlyTrailingCSharpTrivia(text, match.Index + match.Length))
                return true;
        }

        return false;
    }

    private static void EmitCSharpSwitchExpressionTypePatternReferences(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<SymbolRecord> containerCandidates,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId)
    {
        if (preparedLines.Count == 0)
            return;

        var preparedContent = string.Join("\n", preparedLines);
        for (var searchIndex = 0; searchIndex < preparedContent.Length;)
        {
            var arrowIndex = preparedContent.IndexOf("=>", searchIndex, StringComparison.Ordinal);
            if (arrowIndex < 0)
                break;

            searchIndex = arrowIndex + 2;
            var looksLikeLambda = IsPotentialCSharpLambdaArrow(preparedContent, arrowIndex);

            if (!TryGetCSharpSwitchExpressionArmTypePatternRange(
                    preparedContent,
                    arrowIndex,
                    out var bodyStartOffset,
                    out var armStartOffset,
                    out var armPatternEndOffset)
                || armStartOffset >= armPatternEndOffset)
            {
                continue;
            }

            var armText = preparedContent[armStartOffset..armPatternEndOffset];
            var cursor = SkipWhitespaceForward(armText, 0);
            if (TryConsumeCSharpPatternKeyword(armText, ref cursor, "not"))
                cursor = SkipWhitespace(armText, cursor);

            string currentTypeExpression;
            int currentTypeIndex;
            int currentContinuationIndex;
            var declarationPatternMatch = CSharpSwitchExpressionDeclarationPatternValueNameRegex.Match(armText);
            if (declarationPatternMatch.Success)
            {
                var declarationTypeGroup = declarationPatternMatch.Groups["type"];
                currentTypeExpression = declarationTypeGroup.Value;
                currentTypeIndex = declarationTypeGroup.Index;
                currentContinuationIndex = SkipWhitespace(armText, declarationTypeGroup.Index + declarationTypeGroup.Length);
            }
            else
            {
                var typeMatch = CSharpTypeExpressionAtCursorRegex.Match(armText, cursor);
                if (!typeMatch.Success)
                    continue;

                var typeGroup = typeMatch.Groups["type"];
                currentTypeExpression = typeGroup.Value;
                currentTypeIndex = typeGroup.Index;
                currentContinuationIndex = SkipWhitespace(armText, typeGroup.Index + typeGroup.Length);
            }

            var currentTypeLineNumber = GetLineNumberFromOffset(preparedContent, armStartOffset + currentTypeIndex, 1);
            if (looksLikeLambda
                && !HasStrongCSharpSwitchExpressionTypeSignal(
                    currentTypeExpression,
                    currentTypeLineNumber,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    hasActiveSameFileCSharpTypeCandidate))
            {
                continue;
            }

            while (TryConsumeCSharpLogicalPatternKeyword(armText, currentContinuationIndex, out var nextHeadCursor))
            {
                if (!IsCSharpLogicalConstantPatternHead(
                        armText,
                        currentTypeExpression,
                        nextHeadCursor,
                        currentTypeLineNumber,
                        csharpQualifiedConstantPatternMemberLookup,
                        csharpQualifiedTypePatternLookup,
                        csharpUsingAliases,
                        csharpUsingStatics,
                        hasActiveSameFileCSharpTypeCandidate))
                {
                    EmitCSharpSwitchExpressionArmTypePatternReference(
                        lines,
                        preparedLines,
                        preparedContent,
                        containerCandidates,
                        references,
                        seen,
                        fileId,
                        currentTypeExpression,
                        bodyStartOffset,
                        armStartOffset + currentTypeIndex);
                }

                var nextTypeCursor = nextHeadCursor;
                if (TryConsumeCSharpPatternKeyword(armText, ref nextTypeCursor, "not"))
                    nextTypeCursor = SkipWhitespace(armText, nextTypeCursor);

                var nextMatch = CSharpTypeExpressionAtCursorRegex.Match(armText, nextTypeCursor);
                if (!nextMatch.Success)
                {
                    currentTypeExpression = string.Empty;
                    break;
                }

                var nextTypeGroup = nextMatch.Groups["type"];
                currentTypeExpression = nextTypeGroup.Value;
                currentTypeIndex = nextTypeGroup.Index;
                currentContinuationIndex = SkipWhitespace(armText, nextTypeGroup.Index + nextTypeGroup.Length);
                currentTypeLineNumber = GetLineNumberFromOffset(preparedContent, armStartOffset + currentTypeIndex, 1);
            }

            if (currentTypeExpression.Length == 0)
                continue;

            if (IsCSharpNonTypePatternExpression(currentTypeExpression)
                || IsCSharpConstantPatternMemberHead(
                    currentTypeExpression,
                    currentTypeLineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate))
            {
                continue;
            }

            EmitCSharpSwitchExpressionArmTypePatternReference(
                lines,
                preparedLines,
                preparedContent,
                containerCandidates,
                references,
                seen,
                fileId,
                currentTypeExpression,
                bodyStartOffset,
                armStartOffset + currentTypeIndex);
        }
    }

    private static bool HasStrongCSharpSwitchExpressionTypeSignal(
        string typeExpression,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate)
    {
        return IsCSharpQualifiedTypePatternHead(
                   typeExpression,
                   lineNumber,
                   csharpQualifiedTypePatternLookup,
                   csharpUsingAliases)
               || hasActiveSameFileCSharpTypeCandidate(typeExpression, lineNumber);
    }

    private static void EmitCSharpSwitchExpressionArmTypePatternReference(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> preparedLines,
        string preparedContent,
        IReadOnlyList<SymbolRecord> containerCandidates,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string typeExpression,
        int containerAnchorOffset,
        int absoluteTypeOffset)
    {
        var position = GetLineColumnFromOffset(preparedContent, absoluteTypeOffset, 1);
        var lineIndex = position.Line - 1;
        if (lineIndex < 0 || lineIndex >= lines.Count)
            return;

        var context = lines[lineIndex];
        if (context.Length == 0)
            return;

        var containerAnchorPosition = GetLineColumnFromOffset(preparedContent, containerAnchorOffset, 1);
        var containerAnchorLineIndex = containerAnchorPosition.Line - 1;
        var container = FindInnermostSameLineCSharpContainer(
                            containerCandidates,
                            containerAnchorLineIndex >= 0 && containerAnchorLineIndex < preparedLines.Count
                                ? preparedLines[containerAnchorLineIndex]
                                : preparedLines[lineIndex],
                            containerAnchorPosition.Line,
                            containerAnchorPosition.Column)
                        ?? FindInnermostContainer(containerCandidates, containerAnchorPosition.Line);

        AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            typeExpression,
            position.Column,
            context,
            position.Line,
            container,
            "csharp");
    }

    private static void EmitJavaTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        TryEmitJavaKeywordTypeListReferences(preparedLine, "extends", references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        TryEmitJavaKeywordTypeListReferences(preparedLine, "implements", references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitJavaGenericBoundReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitJavaThrowsReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitDeclarationTypeReferences("java", preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);

        foreach (Match match in JavaInstanceofRegex.Matches(preparedLine))
        {
            var typeGroup = match.Groups["type"];
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                typeGroup.Value,
                typeGroup.Index,
                context,
                lineNumber,
                resolveContainerForColumn(typeGroup.Index),
                "java");
        }
    }

    private static void EmitTypeScriptTypePositionReferences(
        IReadOnlyList<string> preparedLines,
        int lineIndex,
        string preparedLine,
        string rawLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var tokens = GetTopLevelTokenSpans(preparedLine);
        if (tokens.Count == 0)
            return;

        for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            var token = preparedLine.Substring(tokens[tokenIndex].Start, tokens[tokenIndex].Length);
            if (token is not "typeof" and not "keyof")
                continue;

            if (!IsTypeScriptTypeQueryContext(preparedLines, lineIndex, preparedLine, tokens, tokenIndex))
                continue;

            if (!TryExtractTypeScriptTypeQueryTarget(
                    rawLine,
                    tokens[tokenIndex].Start + tokens[tokenIndex].Length,
                    out var expressionStart,
                    out var expressionLength,
                    out var literalTarget))
                continue;

            if (literalTarget != null)
            {
                AddTypeReferenceSegment(
                    references,
                    seen,
                    fileId,
                    literalTarget,
                    expressionStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(expressionStart),
                    "typescript");
                continue;
            }

            if (expressionStart < 0 || expressionStart >= rawLine.Length)
                continue;

            var expressionLengthSafe = Math.Min(expressionLength, rawLine.Length - expressionStart);
            AddTypeReferenceSegments(
                references,
                seen,
                fileId,
                rawLine.Substring(expressionStart, expressionLengthSafe),
                expressionStart,
                context,
                lineNumber,
                resolveContainerForColumn(expressionStart),
                "typescript");
        }
    }

    private static void EmitJavaModuleDirectiveReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        EmitJavaModuleDirectiveReference(
            preparedLine,
            JavaModuleRequiresDirectiveReferenceRegex,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        EmitJavaModuleDirectiveReference(
            preparedLine,
            JavaModuleUsesDirectiveReferenceRegex,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        foreach (Match match in JavaModuleProvidesDirectiveReferenceRegex.Matches(preparedLine))
        {
            var serviceGroup = match.Groups["service"];
            AddTypeReferenceSegment(
                references,
                seen,
                fileId,
                serviceGroup.Value,
                serviceGroup.Index,
                context,
                lineNumber,
                resolveContainerForColumn(serviceGroup.Index),
                "java");

            var implementationsGroup = match.Groups["implementations"];
            foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(implementationsGroup.Value))
            {
                var rawSegment = implementationsGroup.Value.Substring(segmentStart, segmentLength).Trim();
                if (rawSegment.Length == 0)
                    continue;

                var absoluteStart = implementationsGroup.Index + segmentStart + CountLeadingWhitespace(implementationsGroup.Value, segmentStart, segmentLength);
                AddTypeReferenceSegment(
                    references,
                    seen,
                    fileId,
                    rawSegment,
                    absoluteStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(absoluteStart),
                    "java");
            }
        }
    }

    private static void EmitJavaModuleDirectiveReference(
        string preparedLine,
        Regex regex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in regex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            AddTypeReferenceSegment(
                references,
                seen,
                fileId,
                nameGroup.Value,
                nameGroup.Index,
                context,
                lineNumber,
                resolveContainerForColumn(nameGroup.Index),
                "java");
        }
    }

    private static void EmitCSharpDocCrefReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        int columnOffset,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in CSharpDocCrefRegex.Matches(originalLine))
        {
            var crefGroup = match.Groups["cref"];
            var normalized = NormalizeCSharpDocCref(crefGroup.Value);
            if (normalized.Length == 0)
                continue;
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                normalized,
                columnOffset + crefGroup.Index,
                context,
                lineNumber,
                container,
                "csharp");
        }
    }

    private static void TryEmitCSharpBaseListReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var trimmed = line.TrimStart();
        if (!(trimmed.Contains(" class ", StringComparison.Ordinal)
              || trimmed.Contains(" struct ", StringComparison.Ordinal)
              || trimmed.Contains(" interface ", StringComparison.Ordinal)
              || trimmed.StartsWith("class ", StringComparison.Ordinal)
              || trimmed.StartsWith("struct ", StringComparison.Ordinal)
              || trimmed.StartsWith("interface ", StringComparison.Ordinal)
              || trimmed.Contains(" record ", StringComparison.Ordinal)
              || trimmed.StartsWith("record ", StringComparison.Ordinal)))
        {
            return;
        }

        var colonIndex = FindSignatureColonIndex(line);
        if (colonIndex < 0)
            return;

        var baseList = line.Substring(colonIndex + 1);
        var whereMatch = CSharpWhereClauseRegex.Match(baseList);
        if (whereMatch.Success)
            baseList = baseList.Substring(0, whereMatch.Index);
        baseList = TrimTrailingTypeListTerminator(baseList);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(baseList))
        {
            var rawSegment = baseList.Substring(segmentStart, segmentLength).Trim();
            if (rawSegment.Length == 0 || rawSegment.Contains('('))
                continue;
            var absoluteStart = colonIndex + 1 + segmentStart + CountLeadingWhitespace(baseList, segmentStart, segmentLength);
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                rawSegment,
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                "csharp");
        }
    }

    private static void EmitCSharpWhereConstraintReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var genericParameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in CSharpWhereClauseRegex.Matches(line))
        {
            var nameGroup = match.Groups["name"];
            if (nameGroup.Success && nameGroup.Value.Length > 0)
                genericParameterNames.Add(nameGroup.Value);
        }

        foreach (Match match in CSharpWhereClauseRegex.Matches(line))
        {
            int listStart = match.Index + match.Length;
            var remaining = line.Substring(listStart);
            var nextWhereMatch = CSharpWhereClauseRegex.Match(remaining);
            int nextWhere = nextWhereMatch.Success ? nextWhereMatch.Index : -1;
            int end = FindTypeListTerminator(remaining, allowArrow: true);
            if (nextWhere >= 0 && (end < 0 || nextWhere < end))
                end = nextWhere;
            if (end < 0)
                end = remaining.Length;
            var constraintList = remaining.Substring(0, end);
            foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(constraintList))
            {
                var rawSegment = constraintList.Substring(segmentStart, segmentLength).Trim();
                if (rawSegment.Length == 0 || rawSegment.Contains('('))
                    continue;
                var absoluteStart = listStart + segmentStart + CountLeadingWhitespace(constraintList, segmentStart, segmentLength);
                AddTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    rawSegment,
                    absoluteStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(absoluteStart),
                    "csharp",
                    genericParameterNames);
            }
        }
    }

    private static void TryEmitJavaKeywordTypeListReferences(
        string line,
        string keyword,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        int keywordIndex = FindTopLevelKeyword(line, keyword);
        if (keywordIndex < 0)
            return;

        int listStart = keywordIndex + keyword.Length;
        while (listStart < line.Length && char.IsWhiteSpace(line[listStart]))
            listStart++;

        int listEnd = FindJavaTypeListTerminator(line, listStart);
        if (listEnd < 0)
            listEnd = line.Length;
        var typeList = line.Substring(listStart, listEnd - listStart);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(typeList))
        {
            var rawSegment = typeList.Substring(segmentStart, segmentLength).Trim();
            if (rawSegment.Length == 0)
                continue;
            var absoluteStart = listStart + segmentStart + CountLeadingWhitespace(typeList, segmentStart, segmentLength);
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                rawSegment,
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                "java");
        }
    }

    private static void EmitJavaGenericBoundReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        EmitJavaCallableGenericBoundReferences(line, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitJavaNamedTypeGenericBoundReferences(line, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitJavaCallableGenericBoundReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (!TryFindCallableParameterList(line, "java", out var callableNameStart, out _, out _))
            return;

        var headerEnd = callableNameStart;
        if (TryGetCallableReturnTypeSpan(line, callableNameStart, "java", out var typeStart, out _))
            headerEnd = typeStart;

        if (headerEnd <= 0)
            return;

        EmitJavaGenericBoundReferencesFromHeader(
            line.Substring(0, headerEnd),
            0,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static void EmitJavaNamedTypeGenericBoundReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var tokens = GetTopLevelTokenSpans(line);
        if (tokens.Count < 2)
            return;

        int keywordIndex = -1;
        int nameIndex = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = line.Substring(tokens[i].Start, tokens[i].Length);
            if (token is "class" or "interface" or "enum" or "record")
            {
                keywordIndex = i;
                nameIndex = i + 1;
                break;
            }
        }

        if (keywordIndex < 0 || nameIndex < 0 || nameIndex >= tokens.Count)
            return;

        var nameToken = line.Substring(tokens[nameIndex].Start, tokens[nameIndex].Length);
        EmitJavaGenericBoundReferencesFromHeader(
            nameToken,
            tokens[nameIndex].Start,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static void EmitJavaGenericBoundReferencesFromHeader(
        string header,
        int headerStartInLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        int openAngle = header.IndexOf('<');
        if (openAngle < 0)
            return;

        int closeAngle = FindMatchingChar(header, openAngle, '<', '>');
        if (closeAngle < 0)
            return;

        var parameterClauseText = header.Substring(openAngle + 1, closeAngle - openAngle - 1);
        var genericParameterNames = CollectJavaGenericParameterNames(parameterClauseText);

        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(parameterClauseText))
        {
            var rawParameter = parameterClauseText.Substring(segmentStart, segmentLength).Trim();
            if (rawParameter.Length == 0)
                continue;

            int extendsIndex = FindTopLevelKeyword(rawParameter, "extends");
            if (extendsIndex < 0)
                continue;

            var boundsText = rawParameter.Substring(extendsIndex + "extends".Length).Trim();
            if (boundsText.Length == 0)
                continue;

            foreach (var (boundStart, boundLength) in SplitTopLevelAmpersandSpans(boundsText))
            {
                var rawBound = boundsText.Substring(boundStart, boundLength).Trim();
                if (rawBound.Length == 0)
                    continue;

                var absoluteStart = headerStartInLine + openAngle + 1 + segmentStart + extendsIndex + "extends".Length + boundStart + CountLeadingWhitespace(boundsText, boundStart, boundLength);
                AddTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    rawBound,
                    absoluteStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(absoluteStart),
                    "java",
                    genericParameterNames);
            }
        }
    }

    private static HashSet<string> CollectJavaGenericParameterNames(string parameterClauseText)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(parameterClauseText))
        {
            var rawParameter = parameterClauseText.Substring(segmentStart, segmentLength).Trim();
            if (rawParameter.Length == 0)
                continue;

            int extendsIndex = FindTopLevelKeyword(rawParameter, "extends");
            var nameFragment = extendsIndex >= 0 ? rawParameter.Substring(0, extendsIndex) : rawParameter;
            if (TryReadJavaGenericParameterName(nameFragment, out var name))
                names.Add(name);
        }

        return names;
    }

    private static bool TryReadJavaGenericParameterName(string text, out string name)
    {
        name = string.Empty;
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= text.Length)
                return false;
            if (text[i] == '@')
            {
                i = SkipJavaAnnotation(text, i);
                continue;
            }
            break;
        }

        int start = i;
        if (start >= text.Length || !IsJavaIdentifierPart(text[start]))
            return false;

        i++;
        while (i < text.Length && IsJavaIdentifierPart(text[i]))
            i++;

        name = text.Substring(start, i - start);
        return name.Length > 0;
    }

    private static void EmitJavaThrowsReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        int keywordIndex = FindTopLevelKeyword(line, "throws");
        if (keywordIndex < 0)
            return;

        int listStart = keywordIndex + "throws".Length;
        while (listStart < line.Length && char.IsWhiteSpace(line[listStart]))
            listStart++;
        int listEnd = FindTypeListTerminator(line.Substring(listStart), allowArrow: false);
        if (listEnd < 0)
            listEnd = line.Length - listStart;
        var typeList = line.Substring(listStart, listEnd);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(typeList))
        {
            var rawSegment = typeList.Substring(segmentStart, segmentLength).Trim();
            if (rawSegment.Length == 0)
                continue;
            var absoluteStart = listStart + segmentStart + CountLeadingWhitespace(typeList, segmentStart, segmentLength);
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                rawSegment,
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                "java");
        }
    }

    private static void EmitDeclarationTypeReferences(
        string language,
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (TryFindCallableParameterList(line, language, out var callableNameStart, out var paramStart, out var paramEnd))
        {
            if (TryGetCallableReturnTypeSpan(line, callableNameStart, language, out var typeStart, out var typeLength))
            {
                AddTypeExpressionSegmentsForLanguage(
                    language,
                    references,
                    seen,
                    fileId,
                    line.Substring(typeStart, typeLength),
                    typeStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(typeStart));
            }

            EmitParameterTypeReferences(
                language,
                line,
                paramStart,
                paramEnd,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }

        if (TryGetSimpleDeclarationTypeSpan(line, language, out var declarationTypeStart, out var declarationTypeLength))
        {
            AddTypeExpressionSegmentsForLanguage(
                language,
                references,
                seen,
                fileId,
                line.Substring(declarationTypeStart, declarationTypeLength),
                declarationTypeStart,
                context,
                lineNumber,
                resolveContainerForColumn(declarationTypeStart));
        }
    }

    private static void EmitTypeScriptDeclarationTypeReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        int equalsIndex = FindTopLevelAssignmentIndex(line);
        if (equalsIndex < 0)
            return;

        var head = line.Substring(0, equalsIndex);
        var tokens = GetTopLevelTokenSpans(head);
        if (tokens.Count < 2)
            return;

        int first = 0;
        while (first < tokens.Count)
        {
            var token = head.Substring(tokens[first].Start, tokens[first].Length);
            if (token is "export" or "declare")
            {
                first++;
                continue;
            }

            break;
        }

        if (first >= tokens.Count - 1)
            return;

        var keyword = head.Substring(tokens[first].Start, tokens[first].Length);
        if (!string.Equals(keyword, "type", StringComparison.Ordinal))
            return;

        int typeStart = SkipWhitespace(line, equalsIndex + 1);
        if (typeStart >= line.Length)
            return;

        int typeEnd = FindTypeScriptTypeExpressionTerminator(line, typeStart);
        if (typeEnd < 0)
            typeEnd = line.Length;

        AddTypeScriptTypeExpressionSegments(
            references,
            seen,
            fileId,
            line.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static int FindTypeScriptTypeExpressionTerminator(string line, int startIndex)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;

        for (int i = startIndex; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(line, i) - 1;
                continue;
            }

            if (c == '`')
            {
                i = ScanTypeScriptTemplateLiteralForTypeExpression(
                    line,
                    i,
                    0,
                    string.Empty,
                    0,
                    [],
                    [],
                    0,
                    null,
                    null) - 1;
                continue;
            }

            if (c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                    return i;
                if (line[i + 1] == '*')
                {
                    i = SkipTypeScriptBlockCommentForTypeExpression(line, i + 2);
                    continue;
                }
            }

            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case ';' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    return i;
            }
        }

        return line.Length;
    }

    private static bool TryFindCallableParameterList(
        string line,
        string language,
        out int callableNameStart,
        out int paramStart,
        out int paramEnd)
    {
        callableNameStart = -1;
        paramStart = -1;
        paramEnd = -1;

        if (IsDefinitelyNotTypeDeclarationLine(line, language))
            return false;

        int openParen = FindFirstTopLevelChar(line, '(');
        if (openParen <= 0)
            return false;
        if (!TryFindCallableName(line, openParen, language, out callableNameStart))
            return false;

        int closeParen = FindMatchingChar(line, openParen, '(', ')');
        if (closeParen < 0)
            return false;

        paramStart = openParen + 1;
        paramEnd = closeParen;
        return true;
    }

    private static bool TryFindCallableName(string line, int openParen, string language, out int nameStart)
    {
        nameStart = -1;
        int i = openParen - 1;
        while (i >= 0 && char.IsWhiteSpace(line[i]))
            i--;
        if (i < 0)
            return false;

        if (line[i] == '>')
        {
            int depth = 1;
            i--;
            while (i >= 0 && depth > 0)
            {
                if (line[i] == '>')
                    depth++;
                else if (line[i] == '<')
                    depth--;
                i--;
            }
            while (i >= 0 && char.IsWhiteSpace(line[i]))
                i--;
        }

        if (i < 0 || !IsTypeExpressionIdentifierPart(language, line[i]))
            return false;
        int end = i + 1;
        while (i >= 0 && IsTypeExpressionIdentifierPart(language, line[i]))
            i--;
        nameStart = i + 1;

        var name = line.Substring(nameStart, end - nameStart);
        if (IsIgnoredCallName(language, name))
            return false;
        return true;
    }

    private static bool TryGetCallableReturnTypeSpan(string line, int callableNameStart, string language, out int typeStart, out int typeLength)
    {
        typeStart = -1;
        typeLength = 0;
        var prefix = line.Substring(0, callableNameStart);
        if (prefix.IndexOf('=') >= 0 || prefix.Contains("=>", StringComparison.Ordinal))
            return false;

        var tokens = GetTopLevelTokenSpans(prefix);
        if (tokens.Count == 0)
            return false;

        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var token = prefix.Substring(tokens[i].Start, tokens[i].Length);
            if (IsCallablePrefixModifier(language, token) || token.StartsWith("[", StringComparison.Ordinal) || token.StartsWith("@", StringComparison.Ordinal))
                continue;
            if (!HasWhitespaceGap(prefix, tokens[i].Start + tokens[i].Length))
                return false;
            typeStart = tokens[i].Start;
            typeLength = tokens[i].Length;
            return true;
        }

        return false;
    }

    private static void EmitParameterTypeReferences(
        string language,
        string line,
        int paramStart,
        int paramEnd,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (paramEnd <= paramStart)
            return;

        var parameterList = line.Substring(paramStart, paramEnd - paramStart);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(parameterList))
        {
            var fragment = parameterList.Substring(segmentStart, segmentLength);
            if (!TryGetParameterTypeRelativeSpan(fragment, language, out var typeRelativeStart, out var typeRelativeLength))
                continue;

            int absoluteStart = paramStart + segmentStart + typeRelativeStart;
            AddTypeExpressionSegmentsForLanguage(
                language,
                references,
                seen,
                fileId,
                fragment.Substring(typeRelativeStart, typeRelativeLength),
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart));
        }
    }

    private static void AddTypeExpressionSegmentsForLanguage(
        string language,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (language == "typescript")
        {
            AddTypeScriptTypeExpressionSegments(
                references,
                seen,
                fileId,
                expression,
                expressionStartInLine,
                context,
                lineNumber,
                container);
            return;
        }

        AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            expression,
            expressionStartInLine,
            context,
            lineNumber,
            container,
            language);
    }

    private static void AddTypeScriptTypeExpressionSegments(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];

            if (c is '\'' or '"')
            {
                i = SkipTypeScriptQuotedString(expression, i);
                continue;
            }

            if (c == '`')
            {
                i = SkipTypeScriptTemplateLiteral(expression, i, references, seen, fileId, expressionStartInLine, context, lineNumber, container);
                continue;
            }

            if (c == '/' && i + 1 < expression.Length)
            {
                if (expression[i + 1] == '/')
                {
                    i = SkipTypeScriptLineComment(expression, i + 2);
                    continue;
                }

                if (expression[i + 1] == '*')
                {
                    i = SkipTypeScriptBlockCommentForTypeExpression(expression, i + 2);
                    continue;
                }
            }

            if (!IsTypeExpressionIdentifierStart("typescript", c))
                continue;

            int segmentStart = i;
            while (i < expression.Length && IsTypeExpressionIdentifierPart("typescript", expression[i]))
                i++;

            var segment = expression.Substring(segmentStart, i - segmentStart);
            AddTypeReferenceSegment(references, seen, fileId, segment, expressionStartInLine + segmentStart, context, lineNumber, container, "typescript");
            i--;
        }
    }

    private static int SkipTypeScriptQuotedString(string text, int start)
    {
        char quote = text[start];
        int i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
                return i;

            i++;
        }

        return text.Length - 1;
    }

    private static int SkipTypeScriptLineComment(string text, int start)
    {
        int i = start;
        while (i < text.Length && text[i] != '\n' && text[i] != '\r')
            i++;
        return Math.Max(start - 1, i - 1);
    }

    private static int SkipTypeScriptBlockComment(string text, int start)
    {
        for (int i = start; i + 1 < text.Length; i++)
        {
            if (text[i] == '*' && text[i + 1] == '/')
                return i + 1;
        }

        return text.Length - 1;
    }

    private static int SkipTypeScriptTemplateLiteral(
        string text,
        int start,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (c == '`')
                return i;

            if (c == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                int holeStart = i + 2;
                int holeEnd = FindMatchingTypeScriptTemplateHoleEnd(text, holeStart);
                if (holeEnd < 0)
                    return text.Length - 1;

                var hole = text.Substring(holeStart, holeEnd - holeStart);
                AddTypeScriptTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    hole,
                    expressionStartInLine + holeStart,
                    context,
                    lineNumber,
                    container);
                i = holeEnd + 1;
                continue;
            }

            i++;
        }

        return text.Length - 1;
    }

    private static int FindMatchingTypeScriptTemplateHoleEnd(string text, int start)
    {
        int braceDepth = 1;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (c is '\'' or '"')
            {
                i = SkipTypeScriptQuotedString(text, i);
                continue;
            }

            if (c == '`')
            {
                i = SkipTypeScriptTemplateLiteral(text, i, new List<ReferenceRecord>(), new HashSet<string>(), 0, 0, string.Empty, 0, null);
                continue;
            }

            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    i = SkipTypeScriptLineComment(text, i + 2);
                    continue;
                }

                if (text[i + 1] == '*')
                {
                    i = SkipTypeScriptBlockComment(text, i + 2);
                    continue;
                }
            }

            if (c == '{')
            {
                braceDepth++;
                continue;
            }

            if (c == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static bool TryGetParameterTypeRelativeSpan(string parameterFragment, string language, out int typeStart, out int typeLength)
    {
        typeStart = -1;
        typeLength = 0;

        int end = FindTopLevelAssignmentIndex(parameterFragment);
        if (end < 0)
            end = parameterFragment.Length;
        var candidate = parameterFragment.Substring(0, end);
        var tokens = GetTopLevelTokenSpans(candidate);
        if (tokens.Count < 2)
            return false;

        int first = 0;
        while (first < tokens.Count)
        {
            var token = candidate.Substring(tokens[first].Start, tokens[first].Length);
            if (token.StartsWith("[", StringComparison.Ordinal) || token.StartsWith("@", StringComparison.Ordinal) || IsParameterModifier(language, token))
            {
                first++;
                continue;
            }

            break;
        }

        if (first >= tokens.Count - 1)
            return false;

        typeStart = tokens[first].Start;
        int lastTypeToken = tokens.Count - 2;
        while (lastTypeToken >= first)
        {
            var token = candidate.Substring(tokens[lastTypeToken].Start, tokens[lastTypeToken].Length);
            if (IsParameterModifier(language, token))
            {
                lastTypeToken--;
                continue;
            }

            break;
        }

        if (lastTypeToken < first)
            return false;
        typeLength = tokens[lastTypeToken].Start + tokens[lastTypeToken].Length - typeStart;
        return true;
    }

    private static bool TryGetSimpleDeclarationTypeSpan(string line, string language, out int typeStart, out int typeLength)
    {
        typeStart = -1;
        typeLength = 0;

        if (IsDefinitelyNotTypeDeclarationLine(line, language))
            return false;

        int firstParen = FindFirstTopLevelChar(line, '(');
        int firstTerminator = FindFirstTopLevelChar(line, ';');
        int firstBrace = FindFirstTopLevelChar(line, '{');
        int firstEquals = FindFirstTopLevelChar(line, '=');
        int firstComma = FindFirstTopLevelChar(line, ',');
        int boundary = int.MaxValue;
        if (firstTerminator >= 0) boundary = Math.Min(boundary, firstTerminator);
        if (firstBrace >= 0) boundary = Math.Min(boundary, firstBrace);
        if (firstEquals >= 0) boundary = Math.Min(boundary, firstEquals);
        if (firstComma >= 0) boundary = Math.Min(boundary, firstComma);
        if (boundary == int.MaxValue)
            return false;
        if (firstParen >= 0 && firstParen < boundary)
            return false;

        var head = line.Substring(0, boundary);
        var tokens = GetTopLevelTokenSpans(head);
        if (tokens.Count < 2)
            return false;

        int first = 0;
        while (first < tokens.Count)
        {
            var token = head.Substring(tokens[first].Start, tokens[first].Length);
            if (token.StartsWith("[", StringComparison.Ordinal) || token.StartsWith("@", StringComparison.Ordinal) || IsDeclarationModifier(language, token))
            {
                first++;
                continue;
            }

            break;
        }

        if (first >= tokens.Count - 1)
            return false;

        var declaredNameToken = head.Substring(tokens[^1].Start, tokens[^1].Length);
        if (!IsSimpleDeclarationIdentifier(language, declaredNameToken))
            return false;

        typeStart = tokens[first].Start;
        int lastTypeToken = tokens.Count - 2;
        typeLength = tokens[lastTypeToken].Start + tokens[lastTypeToken].Length - typeStart;
        return true;
    }

    private static bool IsDefinitelyNotTypeDeclarationLine(string line, string language)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
            return true;
        if (language == "csharp"
            && TryFindFirstTopLevelCSharpArrow(line, out var arrowIndex))
        {
            var commaIndex = FindFirstTopLevelChar(line, ',');
            var semicolonIndex = FindFirstTopLevelChar(line, ';');
            if (commaIndex > arrowIndex && (semicolonIndex < 0 || commaIndex < semicolonIndex))
                return true;
        }

        if (trimmed.StartsWith("using ", StringComparison.Ordinal)
            || trimmed.StartsWith("namespace ", StringComparison.Ordinal)
            || trimmed.StartsWith("package ", StringComparison.Ordinal)
            || trimmed.StartsWith("import ", StringComparison.Ordinal)
            || trimmed.StartsWith("return ", StringComparison.Ordinal)
            || trimmed.StartsWith("throw ", StringComparison.Ordinal)
            || trimmed.StartsWith("if ", StringComparison.Ordinal)
            || trimmed.StartsWith("if(", StringComparison.Ordinal)
            || trimmed.StartsWith("switch ", StringComparison.Ordinal)
            || trimmed.StartsWith("switch(", StringComparison.Ordinal)
            || trimmed.StartsWith("while ", StringComparison.Ordinal)
            || trimmed.StartsWith("while(", StringComparison.Ordinal)
            || trimmed.StartsWith("for ", StringComparison.Ordinal)
            || trimmed.StartsWith("for(", StringComparison.Ordinal)
            || trimmed.StartsWith("foreach ", StringComparison.Ordinal)
            || trimmed.StartsWith("foreach(", StringComparison.Ordinal)
            || trimmed.StartsWith("catch ", StringComparison.Ordinal)
            || trimmed.StartsWith("catch(", StringComparison.Ordinal)
            || trimmed.StartsWith("lock ", StringComparison.Ordinal)
            || trimmed.StartsWith("lock(", StringComparison.Ordinal)
            || trimmed.StartsWith("case ", StringComparison.Ordinal)
            || trimmed.StartsWith("else", StringComparison.Ordinal)
            || trimmed.StartsWith("do", StringComparison.Ordinal))
        {
            return true;
        }

        return trimmed.StartsWith("class ", StringComparison.Ordinal)
            || trimmed.StartsWith("struct ", StringComparison.Ordinal)
            || trimmed.StartsWith("interface ", StringComparison.Ordinal)
            || trimmed.StartsWith("record ", StringComparison.Ordinal)
            || (language == "java" && trimmed.StartsWith("enum ", StringComparison.Ordinal));
    }

    private static bool TryFindFirstTopLevelCSharpArrow(string text, out int arrowIndex)
    {
        arrowIndex = -1;
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        var braceDepth = 0;
        for (var i = 0; i + 1 < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case '=':
                    if (text[i + 1] == '>'
                        && angleDepth == 0
                        && parenDepth == 0
                        && squareDepth == 0
                        && braceDepth == 0)
                    {
                        arrowIndex = i;
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static void AddTypeExpressionSegments(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        if (language == "typescript")
        {
            AddTypeScriptTypeExpressionSegments(
                references,
                seen,
                fileId,
                expression,
                expressionStartInLine,
                context,
                lineNumber,
                container,
                ignoredSegments);
            return;
        }

        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            if (language == "java" && c == '@')
            {
                i = SkipJavaAnnotation(expression, i);
                continue;
            }

            if (!IsTypeExpressionIdentifierStart(language, c))
                continue;

            int segmentStart = i;
            if (language == "csharp" && expression[i] == '@')
                i++;
            while (i < expression.Length && IsTypeExpressionIdentifierPart(language, expression[i]))
                i++;

            var rawSegment = expression.Substring(segmentStart, i - segmentStart);
            var isEscapedCSharpIdentifier = language == "csharp" && rawSegment.Length > 0 && rawSegment[0] == '@';
            var segment = rawSegment;
            if (language == "csharp")
                segment = NormalizeCSharpIdentifier(rawSegment);

            if (i + 1 < expression.Length && expression[i] == ':' && expression[i + 1] == ':')
            {
                i++;
                continue;
            }

            AddTypeReferenceSegment(references, seen, fileId, segment, expressionStartInLine + segmentStart, context, lineNumber, container, language, isEscapedCSharpIdentifier, ignoredSegments);
            i--;
        }
    }

    private static void AddTypeScriptTypeExpressionSegments(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        int i = 0;
        while (i < expression.Length)
        {
            char c = expression[i];
            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(expression, i);
                continue;
            }

            if (c == '`')
            {
                i = ScanTypeScriptTemplateLiteralForTypeExpression(
                    expression,
                    i,
                    expressionStartInLine,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    container,
                    ignoredSegments);
                continue;
            }

            if (!IsJavaIdentifierStart(c))
            {
                i++;
                continue;
            }

            int segmentStart = i;
            i++;
            while (i < expression.Length && IsJavaIdentifierPart(expression[i]))
                i++;

            var segment = expression.Substring(segmentStart, i - segmentStart);
            AddTypeReferenceSegment(
                references,
                seen,
                fileId,
                segment,
                expressionStartInLine + segmentStart,
                context,
                lineNumber,
                container,
                "typescript",
                ignoredSegments: ignoredSegments);
        }
    }

    private static int ScanTypeScriptTemplateLiteralForTypeExpression(
        string expression,
        int startIndex,
        int expressionStartInLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container,
        IReadOnlySet<string>? ignoredSegments)
    {
        int i = startIndex + 1;
        while (i < expression.Length)
        {
            char c = expression[i];
            if (c == '\\')
            {
                i += Math.Min(2, expression.Length - i);
                continue;
            }

            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(expression, i);
                continue;
            }

            if (c == '$' && i + 1 < expression.Length && expression[i + 1] == '{')
            {
                int holeStart = i + 2;
                int holeEnd = FindMatchingTypeScriptHoleEndForTypeExpression(expression, holeStart);
                if (holeEnd < 0)
                    return expression.Length;

                AddTypeScriptTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    expression.Substring(holeStart, holeEnd - holeStart),
                    expressionStartInLine + holeStart,
                    context,
                    lineNumber,
                    container,
                    ignoredSegments);
                i = holeEnd + 1;
                continue;
            }

            if (c == '`')
                return i + 1;

            i++;
        }

        return expression.Length;
    }

    private static int FindMatchingTypeScriptHoleEndForTypeExpression(string text, int startIndex)
    {
        int braceDepth = 0;
        int i = startIndex;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\')
            {
                i += Math.Min(2, text.Length - i);
                continue;
            }

            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(text, i);
                continue;
            }

            if (c == '`')
            {
                i = ScanTypeScriptTemplateLiteralForTypeExpression(
                    text,
                    i,
                    0,
                    string.Empty,
                    0,
                    [],
                    [],
                    0,
                    null,
                    null);
                continue;
            }

            if (c == '{')
            {
                braceDepth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                if (braceDepth == 0)
                    return i;
                braceDepth--;
                i++;
                continue;
            }

            i++;
        }

        return -1;
    }

    private static int SkipTypeScriptStringLiteral(string text, int startIndex)
    {
        char quote = text[startIndex];
        int i = startIndex + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += Math.Min(2, text.Length - i);
                continue;
            }

            if (text[i] == quote)
                return i + 1;

            i++;
        }

        return text.Length;
    }

    private static int SkipTypeScriptBlockCommentForTypeExpression(string text, int startIndex)
    {
        for (int i = startIndex; i + 1 < text.Length; i++)
        {
            if (text[i] == '*' && text[i + 1] == '/')
                return i + 1;
        }

        return text.Length - 1;
    }

    private static int SkipBalanced(string line, int start, char open, char close)
    {
        int depth = 0;
        int i = start;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == open)
                depth++;
            else if (c == close)
            {
                depth--;
                if (depth <= 0)
                    return i + 1;
            }
            i++;
        }
        return i;
    }

    private static int SkipJavaAnnotation(string text, int start)
    {
        int i = start + 1;
        while (i < text.Length && (IsJavaIdentifierPart(text[i]) || text[i] == '.'))
            i++;
        if (i < text.Length && text[i] == '(')
        {
            int close = FindMatchingChar(text, i, '(', ')');
            if (close >= 0)
                return close;
        }

        return i - 1;
    }

    private static int FindMatchingChar(string text, int openIndex, char open, char close)
    {
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open)
                depth++;
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int FindFirstTopLevelChar(string text, char target)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == target && angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0)
                return i;

            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
            }
        }

        return -1;
    }

    private static int FindTopLevelAssignmentIndex(string text)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
                case '=' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    if (i + 1 >= text.Length || text[i + 1] != '>')
                        return i;
                    break;
            }
        }

        return -1;
    }

    private static List<(int Start, int Length)> GetTopLevelTokenSpans(string text)
    {
        var tokens = new List<(int Start, int Length)>();
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        int tokenStart = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
            }

            bool topLevelWhitespace = char.IsWhiteSpace(c) && angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0;
            if (topLevelWhitespace)
            {
                if (tokenStart >= 0)
                {
                    tokens.Add((tokenStart, i - tokenStart));
                    tokenStart = -1;
                }
                continue;
            }

            if (tokenStart < 0)
                tokenStart = i;
        }

        if (tokenStart >= 0)
            tokens.Add((tokenStart, text.Length - tokenStart));
        return tokens;
    }

    private static List<(int Start, int Length)> SplitTopLevelCommaSpans(string text)
    {
        var spans = new List<(int Start, int Length)>();
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
                case ',' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    spans.Add((start, i - start));
                    start = i + 1;
                    break;
            }
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    private static List<(int Start, int Length)> SplitTopLevelAmpersandSpans(string text)
    {
        var spans = new List<(int Start, int Length)>();
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
                case '&' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    spans.Add((start, i - start));
                    start = i + 1;
                    break;
            }
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    private static int CountLeadingWhitespace(string text, int start, int length)
    {
        int count = 0;
        while (count < length && char.IsWhiteSpace(text[start + count]))
            count++;
        return count;
    }

    private static int FindTypeListTerminator(string text, bool allowArrow)
    {
        int brace = FindFirstTopLevelChar(text, '{');
        int semi = FindFirstTopLevelChar(text, ';');
        int end = -1;
        if (brace >= 0) end = brace;
        if (semi >= 0 && (end < 0 || semi < end)) end = semi;
        if (allowArrow)
        {
            int arrow = text.IndexOf("=>", StringComparison.Ordinal);
            if (arrow >= 0 && (end < 0 || arrow < end))
                end = arrow;
        }
        return end;
    }

    private static string TrimTrailingTypeListTerminator(string text)
    {
        int end = FindTypeListTerminator(text, allowArrow: true);
        return end >= 0 ? text.Substring(0, end) : text;
    }

    private static int FindJavaTypeListTerminator(string text, int start)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<')
                angleDepth++;
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (c == '(')
                parenDepth++;
            else if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (angleDepth == 0 && parenDepth == 0)
            {
                if (c == '{' || c == ';')
                    return i;
                if (IsJavaBaseListTerminatorKeyword(text, i, start, "implements")
                    || IsJavaBaseListTerminatorKeyword(text, i, start, "permits")
                    || IsJavaBaseListTerminatorKeyword(text, i, start, "throws"))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int FindTopLevelKeyword(string text, string keyword)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
            }

            if (angleDepth != 0 || parenDepth != 0 || squareDepth != 0 || braceDepth != 0)
                continue;
            if (i > 0 && IsJavaIdentifierPart(text[i - 1]))
                continue;
            if (i + keyword.Length > text.Length || string.CompareOrdinal(text, i, keyword, 0, keyword.Length) != 0)
                continue;
            int after = i + keyword.Length;
            if (after < text.Length && IsJavaIdentifierPart(text[after]))
                continue;
            return i;
        }

        return -1;
    }

    private static bool IsCallablePrefixModifier(string language, string token) =>
        language == "csharp"
            ? token is "public" or "private" or "protected" or "internal" or "file" or "static" or "readonly" or "required" or "volatile" or "const"
                or "unsafe" or "new" or "sealed" or "abstract" or "virtual" or "override" or "extern" or "partial" or "async" or "ref" or "scoped"
            : token is "public" or "private" or "protected" or "static" or "final" or "abstract" or "synchronized" or "native" or "strictfp" or "default";

    private static bool IsParameterModifier(string language, string token) =>
        language == "csharp"
            ? token is "ref" or "out" or "in" or "params" or "this" or "scoped" or "readonly"
            : token is "final";

    private static bool IsDeclarationModifier(string language, string token) =>
        language == "csharp"
            ? token is "public" or "private" or "protected" or "internal" or "file" or "static" or "readonly" or "required" or "volatile" or "const"
                or "unsafe" or "new" or "sealed" or "abstract" or "virtual" or "override" or "extern" or "partial" or "async" or "ref" or "scoped" or "event"
            : token is "public" or "private" or "protected" or "static" or "final" or "abstract" or "volatile" or "transient" or "synchronized" or "native" or "strictfp";

    private static bool IsSimpleDeclarationIdentifier(string language, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        if (!IsTypeExpressionIdentifierStart(language, token[0]))
            return false;
        for (int i = 1; i < token.Length; i++)
        {
            if (!IsTypeExpressionIdentifierPart(language, token[i]))
                return false;
        }

        return true;
    }

    private static bool HasWhitespaceGap(string text, int start)
    {
        if (start >= text.Length)
            return false;
        for (int i = start; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return false;
        }

        return true;
    }

    private static string NormalizeCSharpDocCref(string cref)
    {
        var text = cref.Trim();
        if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':')
            text = text.Substring(2);
        int paren = text.IndexOf('(');
        if (paren >= 0)
            text = text.Substring(0, paren);
        int brace = text.IndexOf('{');
        if (brace >= 0)
            text = text.Substring(0, brace);
        return text.Trim();
    }

    private static bool IsCSharpIdentifierStart(char c) =>
        c == '_' || c == '@' || char.IsLetter(c);

    private static bool IsJavaIdentifierStart(char c) =>
        c == '_' || c == '$' || char.IsLetter(c);

    private static bool IsTypeExpressionIdentifierStart(string language, char c) =>
        language == "csharp" ? IsCSharpIdentifierStart(c) : IsJavaIdentifierStart(c);

    private static bool IsTypeExpressionIdentifierPart(string language, char c) =>
        language == "csharp" ? IsCSharpIdentifierPart(c) : IsJavaIdentifierPart(c);

    private static bool IsTypeScriptTypeQueryContext(
        IReadOnlyList<string> preparedLines,
        int lineIndex,
        string line,
        List<(int Start, int Length)> tokens,
        int keywordIndex)
    {
        for (int i = 0; i < keywordIndex; i++)
        {
            var token = line.Substring(tokens[i].Start, tokens[i].Length);
            if (TypeScriptTypeQueryDisqualifyingTokens.Contains(token))
                return false;

            if (TypeScriptTypeQueryContextTokens.Contains(token))
                return true;
        }

        if (keywordIndex == 0)
            return HasTypeScriptTypeQueryLeadingContext(preparedLines, lineIndex);

        var previousToken = line.Substring(tokens[keywordIndex - 1].Start, tokens[keywordIndex - 1].Length);
        return previousToken.EndsWith(':');
    }

    private static bool HasTypeScriptTypeQueryLeadingContext(IReadOnlyList<string> preparedLines, int lineIndex)
    {
        for (int previousIndex = lineIndex - 1; previousIndex >= 0; previousIndex--)
        {
            var previousLine = preparedLines[previousIndex];
            if (string.IsNullOrWhiteSpace(previousLine))
                continue;

            if (IsTypeScriptTypeQueryLineContext(previousLine))
                return true;

            if (!IsTypeScriptTypeQueryContinuationLine(previousLine))
                return false;
        }

        return false;
    }

    private static bool IsTypeScriptTypeQueryLineContext(string line)
    {
        var tokens = GetTopLevelTokenSpans(line);
        foreach (var token in tokens)
        {
            var text = line.Substring(token.Start, token.Length);
            if (TypeScriptTypeQueryDisqualifyingTokens.Contains(text))
                return false;
            if (TypeScriptTypeQueryContextTokens.Contains(text))
                return true;
        }

        return false;
    }

    private static bool IsTypeScriptTypeQueryContinuationLine(string line)
    {
        var trimmed = line.TrimEnd();
        if (trimmed.Length == 0)
            return false;

        return trimmed[^1] is '<' or '(' or '[' or ',' or '|' or '&' or ':';
    }

    private static bool TryExtractTypeScriptTypeQueryTarget(
        string line,
        int startIndex,
        out int targetStart,
        out int targetLength,
        out string? literalTarget)
    {
        targetStart = 0;
        targetLength = 0;
        literalTarget = null;

        var cursor = startIndex;
        while (cursor < line.Length)
        {
            cursor = SkipWhitespace(line, cursor);
            if (cursor >= line.Length)
                return false;

            if (TryConsumeTypeScriptTypeQueryWrapper(line, cursor, "typeof", out cursor))
                continue;

            if (TryConsumeTypeScriptImportTypeWrapper(line, cursor, out cursor, out var importModuleStart, out var importModuleLength))
            {
                if (cursor >= line.Length || line[cursor] != '.')
                {
                    targetStart = importModuleStart;
                    targetLength = importModuleLength;
                    literalTarget = line.Substring(importModuleStart, importModuleLength);
                    return targetLength > 0;
                }

                continue;
            }

            if (line[cursor] == '(' || line[cursor] == '[')
            {
                cursor++;
                continue;
            }

            break;
        }

        cursor = SkipWhitespace(line, cursor);
        while (cursor < line.Length && line[cursor] == '.')
        {
            cursor++;
            cursor = SkipWhitespace(line, cursor);
        }

        if (cursor >= line.Length || !IsJavaIdentifierStart(line[cursor]))
            return false;

        var end = cursor + 1;
        while (end < line.Length && (IsJavaIdentifierPart(line[end]) || line[end] == '.'))
            end++;

        targetStart = cursor;
        targetLength = end - cursor;
        return targetLength > 0;
    }

    private static bool TryConsumeTypeScriptTypeQueryWrapper(
        string line,
        int cursor,
        string keyword,
        out int nextCursor)
    {
        nextCursor = cursor;
        if (cursor + keyword.Length > line.Length
            || !line.AsSpan(cursor, keyword.Length).Equals(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        var nextIndex = cursor + keyword.Length;
        if (nextIndex < line.Length && (char.IsLetterOrDigit(line[nextIndex]) || line[nextIndex] == '_'))
            return false;

        nextCursor = nextIndex;
        return true;
    }

    private static bool TryConsumeTypeScriptImportTypeWrapper(
        string line,
        int cursor,
        out int nextCursor,
        out int moduleStart,
        out int moduleLength)
    {
        nextCursor = cursor;
        moduleStart = -1;
        moduleLength = 0;
        if (cursor + "import".Length > line.Length
            || !line.AsSpan(cursor, "import".Length).Equals("import", StringComparison.Ordinal))
        {
            return false;
        }

        var nextIndex = cursor + "import".Length;
        if (nextIndex < line.Length && (char.IsLetterOrDigit(line[nextIndex]) || line[nextIndex] == '_'))
            return false;

        nextIndex = SkipWhitespace(line, nextIndex);
        if (nextIndex >= line.Length || line[nextIndex] != '(')
            return false;

        var moduleQuoteIndex = SkipWhitespace(line, nextIndex + 1);
        if (moduleQuoteIndex >= line.Length || line[moduleQuoteIndex] is not '\'' and not '"')
            return false;

        var moduleLiteralStart = moduleQuoteIndex + 1;
        var moduleLiteralEnd = SkipTypeScriptStringLiteral(line, moduleQuoteIndex) - 1;
        if (moduleLiteralEnd < moduleLiteralStart)
            return false;

        var closeIndex = SkipBalanced(line, nextIndex, '(', ')');
        if (closeIndex <= nextIndex)
            return false;

        moduleStart = moduleLiteralStart;
        moduleLength = moduleLiteralEnd - moduleLiteralStart;
        nextCursor = closeIndex;
        return true;
    }

    private readonly record struct CSharpLineColumn(int Line, int Column);
    private readonly record struct CSharpRecursivePatternValueNameRecord(string Name, int Offset, bool IsCasePattern, int ArrowIndex = -1);
    private sealed record CSharpNamespaceScope(string QualifiedName, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpUsingNamespaceScope(string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpContainingTypeScope(string QualifiedName, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpUsingAliasRecord(string AliasName, string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine, bool TargetsType);
    private sealed record CSharpUsingStaticRecord(string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpCastTypeShape(IReadOnlyList<string> IdentifierSegments, string? SimpleQualifiedName, bool HasTypeOnlySyntax, bool AllIdentifiersTypeLike);
    private sealed record CSharpContainingTypeValueReceiverNames(HashSet<string> InstanceNames, HashSet<string> StaticNames);
    private sealed record CSharpFunctionValueReceiverNameRecord(string Name, int ScopeStartLine, int ScopeStartColumn, int ScopeEndLine, int ScopeEndColumn);

    private static List<CSharpUsingAliasRecord> BuildCSharpUsingAliases(string language, IReadOnlyList<SymbolRecord> symbols, IReadOnlySet<string> csharpKnownTypeNames)
    {
        var aliases = new List<CSharpUsingAliasRecord>();
        if (language != "csharp")
            return aliases;

        var namespaceScopes = symbols
            .Where(symbol => symbol.Kind == "namespace")
            .Select(symbol => (
                StartLine: symbol.BodyStartLine ?? symbol.StartLine,
                EndLine: symbol.BodyEndLine ?? symbol.EndLine))
            .Where(scope => scope.StartLine > 0 && scope.EndLine >= scope.StartLine)
            .ToList();

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "import" || string.IsNullOrWhiteSpace(symbol.Signature))
                continue;

            var match = CSharpUsingAliasRegex.Match(symbol.Signature!);
            if (!match.Success)
                continue;

            var alias = NormalizeCSharpIdentifier(match.Groups["alias"].Value);
            var target = TryNormalizeCSharpQualifiedName(match.Groups["target"].Value);
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(target))
                continue;

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (symbol.Line < startLine || symbol.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            aliases.Add(new CSharpUsingAliasRecord(
                alias,
                target,
                symbol.Line,
                scopeStartLine,
                scopeEndLine,
                IsCSharpUsingAliasTypeTarget(target, csharpKnownTypeNames)));
        }

        aliases.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        return aliases;
    }

    private static List<CSharpUsingStaticRecord> BuildCSharpUsingStatics(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var imports = new List<CSharpUsingStaticRecord>();
        if (language != "csharp")
            return imports;

        var namespaceScopes = symbols
            .Where(symbol => symbol.Kind == "namespace")
            .Select(symbol => (
                StartLine: symbol.BodyStartLine ?? symbol.StartLine,
                EndLine: symbol.BodyEndLine ?? symbol.EndLine))
            .Where(scope => scope.StartLine > 0 && scope.EndLine >= scope.StartLine)
            .ToList();

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "import" || string.IsNullOrWhiteSpace(symbol.Signature))
                continue;

            var match = CSharpUsingStaticRegex.Match(symbol.Signature!);
            if (!match.Success)
                continue;

            var target = TryNormalizeCSharpQualifiedName(match.Groups["target"].Value);
            if (string.IsNullOrWhiteSpace(target))
                continue;

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (symbol.Line < startLine || symbol.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            imports.Add(new CSharpUsingStaticRecord(target, symbol.Line, scopeStartLine, scopeEndLine));
        }

        imports.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        return imports;
    }

    private static List<CSharpUsingNamespaceScope> BuildCSharpUsingNamespaceScopes(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var scopes = new List<CSharpUsingNamespaceScope>();
        if (language != "csharp")
            return scopes;

        var namespaceScopes = symbols
            .Where(symbol => symbol.Kind == "namespace")
            .Select(symbol => (
                StartLine: symbol.BodyStartLine ?? symbol.StartLine,
                EndLine: symbol.BodyEndLine ?? symbol.EndLine))
            .Where(scope => scope.StartLine > 0 && scope.EndLine >= scope.StartLine)
            .ToList();

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "import" || string.IsNullOrWhiteSpace(symbol.Signature))
                continue;

            if (!TryParseCSharpUsingNamespaceImport(symbol.Signature!, out var target, out _))
                continue;

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (symbol.Line < startLine || symbol.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            scopes.Add(new CSharpUsingNamespaceScope(target!, symbol.Line, scopeStartLine, scopeEndLine));
        }

        scopes.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        return scopes;
    }

    private static List<CSharpNamespaceScope> BuildCSharpNamespaceScopes(string language, IReadOnlyList<SymbolRecord> symbols, int totalLineCount)
    {
        var scopes = new List<CSharpNamespaceScope>();
        if (language != "csharp")
            return scopes;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "namespace" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            var startLine = symbol.BodyStartLine ?? symbol.StartLine;
            var endLine = symbol.BodyEndLine ?? symbol.EndLine;
            if (!string.IsNullOrWhiteSpace(symbol.Signature)
                && symbol.Signature.TrimEnd().EndsWith(';'))
            {
                endLine = Math.Max(endLine, totalLineCount);
            }

            if (startLine <= 0 || endLine < startLine)
                continue;

            var qualifiedName = TryNormalizeCSharpQualifiedName(symbol.Name) ?? string.Empty;
            scopes.Add(new CSharpNamespaceScope(qualifiedName, startLine, endLine));
        }

        return scopes;
    }

    private static bool TryParseCSharpUsingNamespaceImport(string signature, out string? target, out bool isGlobal)
    {
        target = null;
        isGlobal = false;
        if (string.IsNullOrWhiteSpace(signature) || signature.IndexOf('=') >= 0)
            return false;

        var match = CSharpUsingNamespaceRegex.Match(signature);
        if (!match.Success)
            return false;

        target = TryNormalizeCSharpQualifiedName(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(target))
            return false;

        isGlobal = signature.TrimStart().StartsWith("global using ", StringComparison.Ordinal);
        return true;
    }

    private static List<CSharpContainingTypeScope> BuildCSharpContainingTypeScopes(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var scopes = new List<CSharpContainingTypeScope>();
        if (language != "csharp")
            return scopes;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "interface")
                || string.IsNullOrWhiteSpace(symbol.Name))
            {
                continue;
            }

            var startLine = symbol.BodyStartLine ?? symbol.StartLine;
            var endLine = symbol.BodyEndLine ?? symbol.EndLine;
            if (startLine <= 0 || endLine < startLine)
                continue;

            var qualifiedName = CombineQualifiedName(symbol.ContainerQualifiedName, NormalizeCSharpIdentifier(symbol.Name));
            if (string.IsNullOrWhiteSpace(qualifiedName))
                qualifiedName = NormalizeCSharpIdentifier(symbol.Name);
            if (string.IsNullOrWhiteSpace(qualifiedName))
                continue;

            scopes.Add(new CSharpContainingTypeScope(qualifiedName!, startLine, endLine));
        }

        return scopes;
    }

    private static Dictionary<string, HashSet<string>> BuildCSharpTopLevelTypeNamespacesByName(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "interface" or "enum" or "delegate")
                || string.IsNullOrWhiteSpace(symbol.Name))
            {
                continue;
            }

            if (symbol.ContainerKind is not (null or "namespace"))
                continue;

            var name = NormalizeCSharpIdentifier(symbol.Name);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!lookup.TryGetValue(name, out var namespaces))
            {
                namespaces = new HashSet<string>(StringComparer.Ordinal);
                lookup[name] = namespaces;
            }

            var qualifiedNamespace = symbol.ContainerQualifiedName;
            if (string.IsNullOrWhiteSpace(qualifiedNamespace) && symbol.ContainerKind == "namespace")
                qualifiedNamespace = symbol.ContainerName;
            namespaces.Add(TryNormalizeCSharpQualifiedName(qualifiedNamespace ?? string.Empty) ?? string.Empty);
        }

        return lookup;
    }

    private static Dictionary<string, HashSet<string>> BuildCSharpNestedTypeContainersByName(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "interface" or "enum" or "delegate")
                || string.IsNullOrWhiteSpace(symbol.Name))
            {
                continue;
            }

            if (symbol.ContainerKind is not ("class" or "struct" or "interface"))
                continue;

            var name = NormalizeCSharpIdentifier(symbol.Name);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!lookup.TryGetValue(name, out var containingTypes))
            {
                containingTypes = new HashSet<string>(StringComparer.Ordinal);
                lookup[name] = containingTypes;
            }

            var qualifiedContainer = !string.IsNullOrWhiteSpace(symbol.ContainerQualifiedName)
                ? symbol.ContainerQualifiedName
                : NormalizeCSharpIdentifier(symbol.ContainerName ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(qualifiedContainer))
                containingTypes.Add(qualifiedContainer!);
        }

        return lookup;
    }

    private static HashSet<string> BuildCSharpKnownTypeNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (language != "csharp")
            return names;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "interface" or "enum" or "delegate"))
                continue;

            var normalizedName = NormalizeCSharpIdentifier(symbol.Name);
            if (!string.IsNullOrWhiteSpace(normalizedName))
                names.Add(normalizedName);

            var qualifiedContainer = !string.IsNullOrWhiteSpace(symbol.ContainerQualifiedName)
                ? symbol.ContainerQualifiedName
                : symbol.ContainerKind == "namespace" && !string.IsNullOrWhiteSpace(symbol.ContainerName)
                    ? symbol.ContainerName
                    : null;
            if (!string.IsNullOrWhiteSpace(qualifiedContainer) && !string.IsNullOrWhiteSpace(normalizedName))
                names.Add(qualifiedContainer + "." + normalizedName);
        }

        return names;
    }

    private static HashSet<string>? BuildCallableDefinitionNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "csharp")
            return null;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            var name = language == "csharp"
                ? NormalizeCSharpIdentifier(symbol.Name)
                : symbol.Name;
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names;
    }

    private static HashSet<string>? BuildShellCallableNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "shell")
            return null;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("function" or "alias") || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            names.Add(symbol.Name);
        }

        return names;
    }

    private static HashSet<string>? BuildShellGlobalAliasNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "shell")
            return null;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "alias" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            var signature = symbol.Signature?.TrimStart();
            if (string.IsNullOrWhiteSpace(signature))
                continue;

            if (!Regex.IsMatch(signature, @"^alias(?:\s+-[^\s=]+)*\s+-g\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;

            names.Add(symbol.Name);
        }

        return names;
    }

    private static HashSet<string>? BuildDockerfileStageNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "dockerfile")
            return null;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            names.Add(symbol.Name);
        }

        return names;
    }

    private static bool IsShellGlobalAliasReferenceBoundary(string text, int startIndex, int length)
    {
        if (startIndex < 0 || length <= 0 || startIndex + length > text.Length)
            return false;

        if (startIndex > 0 && !IsShellGlobalAliasBoundarySeparator(text[startIndex - 1]))
            return false;

        var endIndex = startIndex + length;
        return endIndex >= text.Length || IsShellGlobalAliasBoundarySeparator(text[endIndex]);
    }

    private static bool IsShellGlobalAliasBoundarySeparator(char ch) =>
        char.IsWhiteSpace(ch) || ch is '|' or ';' or '&' or '{' or '}' or '(' or ')' or '!';

    private static bool IsCSharpUsingAliasTypeTarget(string targetQualifiedName, IReadOnlySet<string> csharpKnownTypeNames)
    {
        var normalizedTarget = NormalizeCSharpAliasTargetForTypeLookup(targetQualifiedName);
        return normalizedTarget.Length > 0 && csharpKnownTypeNames.Contains(normalizedTarget);
    }

    private static string NormalizeCSharpAliasTargetForTypeLookup(string targetQualifiedName)
    {
        if (string.IsNullOrWhiteSpace(targetQualifiedName))
            return string.Empty;

        var trimmed = targetQualifiedName.Trim();
        var builder = new System.Text.StringBuilder(trimmed.Length);
        var genericDepth = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '<')
            {
                genericDepth++;
                continue;
            }

            if (ch == '>')
            {
                if (genericDepth > 0)
                    genericDepth--;
                continue;
            }

            if (genericDepth == 0)
                builder.Append(ch);
        }

        var normalized = builder.ToString().Trim();
        while (normalized.EndsWith("?", StringComparison.Ordinal))
            normalized = normalized[..^1].TrimEnd();
        while (normalized.EndsWith("[]", StringComparison.Ordinal))
            normalized = normalized[..^2].TrimEnd();

        return normalized;
    }

    private static Dictionary<string, CSharpContainingTypeValueReceiverNames> BuildCSharpValueReceiverNamesByContainingType(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, CSharpContainingTypeValueReceiverNames>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "property" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            var containingType = GetContainingTypeQualifiedName(symbol);
            if (string.IsNullOrWhiteSpace(containingType))
                continue;

            if (!lookup.TryGetValue(containingType!, out var names))
            {
                names = new CSharpContainingTypeValueReceiverNames(
                    new HashSet<string>(StringComparer.Ordinal),
                    new HashSet<string>(StringComparer.Ordinal));
                lookup[containingType!] = names;
            }

            if (IsStaticCSharpSymbol(symbol))
                names.StaticNames.Add(symbol.Name);
            else
                names.InstanceNames.Add(symbol.Name);
        }

        return lookup;
    }

    private static Dictionary<int, List<CSharpFunctionValueReceiverNameRecord>> BuildCSharpValueReceiverNamesByFunctionStartLine(
        string language,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<string> structuralLines,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        var lookup = new Dictionary<int, List<CSharpFunctionValueReceiverNameRecord>>();
        if (language != "csharp")
            return lookup;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("function" or "property") || symbol.StartLine <= 0)
                continue;

            var names = new List<CSharpFunctionValueReceiverNameRecord>();
            if (symbol.BodyStartLine != null && symbol.BodyEndLine != null)
            {
                var start = Math.Max(symbol.BodyStartLine.Value - 1, 0);
                var end = Math.Min(symbol.BodyEndLine.Value - 1, structuralLines.Count - 1);
                var bodyText = string.Join("\n", structuralLines.Skip(start).Take(end - start + 1));
                if (symbol.Kind == "function")
                    AddCSharpParameterNames(names, symbol.Signature, symbol.BodyStartLine.Value, 0, symbol.BodyEndLine.Value, int.MaxValue);
                for (var i = start; i <= end; i++)
                {
                    foreach (Match match in CSharpLocalValueNameRegex.Matches(structuralLines[i]))
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            FindInnermostCSharpBlockEndLine(structuralLines, start, end, i, match.Index),
                            int.MaxValue);
                    foreach (Match match in CSharpForeachValueNameRegex.Matches(structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpQueryRangeValueNameRegex.Matches(structuralLines[i]))
                    {
                        var scopeEnd = FindCSharpQueryExpressionEndPosition(
                            structuralLines,
                            end,
                            i,
                            match.Index,
                            csharpKnownTypeNames,
                            csharpUsingAliases,
                            names);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpDeclarationPatternValueNameRegex.Matches(structuralLines[i]))
                    {
                        if (!TryFindCSharpDeclarationPatternScopeEndPosition(structuralLines, start, end, i, match.Index, out var scopeEnd))
                            continue;

                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpCaseDeclarationPatternValueNameRegex.Matches(structuralLines[i]))
                    {
                        if (!TryFindCSharpSwitchCaseScopeEndPosition(structuralLines, end, i, match.Index, out var scopeEnd))
                            continue;

                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpOutValueNameRegex.Matches(structuralLines[i]))
                        AddCSharpFunctionValueReceiverName(names, NormalizeCSharpIdentifier(match.Groups["name"].Value), i + 1, match.Index, symbol.BodyEndLine.Value, int.MaxValue);
                    foreach (Match match in CSharpCatchValueNameRegex.Matches(structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpUsingStatementValueNameRegex.Matches(structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpFixedValueNameRegex.Matches(structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                }

                AddCSharpRecursivePatternValueReceiverNames(names, bodyText, structuralLines, start, end);
                AddCSharpLambdaParameterNames(
                    names,
                    bodyText,
                    start + 1,
                    symbol.BodyEndLine.Value);
            }

            if (names.Count > 0)
                lookup[symbol.StartLine] = names;
        }

        return lookup;
    }

    private static Dictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>> BuildCSharpQualifiedEnumMemberLookup(
        string language,
        IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        var conflictingNonEnumTypeNames = new HashSet<string>(
            symbols
                .Where(symbol => symbol.Kind is "class" or "struct" or "interface" or "delegate")
                .Select(symbol => symbol.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))!,
            StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "enum" || symbol.ContainerKind != "enum")
                continue;
            if (string.IsNullOrWhiteSpace(symbol.Name) || string.IsNullOrWhiteSpace(symbol.ContainerName))
                continue;

            if (!lookup.TryGetValue(symbol.Name, out var targets))
            {
                targets = [];
                lookup[symbol.Name] = targets;
            }

            bool exists = false;
            foreach (var target in targets)
            {
                if (string.Equals(target.EnumName, symbol.ContainerName, StringComparison.Ordinal)
                    && string.Equals(target.QualifiedEnumName, symbol.ContainerQualifiedName, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                targets.Add((
                    symbol.ContainerName!,
                    symbol.ContainerQualifiedName,
                    AllowShortNameFallback: !conflictingNonEnumTypeNames.Contains(symbol.ContainerName!)));
        }

        return lookup;
    }

    private static Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> BuildCSharpQualifiedConstantPatternMemberLookup(
        string language,
        IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        var conflictingNonEnumTypeNames = new HashSet<string>(
            symbols
                .Where(symbol => symbol.Kind is "class" or "struct" or "interface" or "delegate")
                .Select(symbol => symbol.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))!,
            StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            if (string.IsNullOrWhiteSpace(symbol.Name) || string.IsNullOrWhiteSpace(symbol.ContainerName))
                continue;

            var target = symbol switch
            {
                { Kind: "enum", ContainerKind: "enum" } => (
                    Included: true,
                    AllowShortNameFallback: !conflictingNonEnumTypeNames.Contains(symbol.ContainerName!)),
                _ when IsCSharpConstMemberSymbol(symbol) => (
                    Included: true,
                    AllowShortNameFallback: true),
                _ => (Included: false, AllowShortNameFallback: false)
            };

            if (!target.Included)
                continue;

            if (!lookup.TryGetValue(symbol.Name, out var targets))
            {
                targets = [];
                lookup[symbol.Name] = targets;
            }

            bool exists = false;
            foreach (var existing in targets)
            {
                if (string.Equals(existing.ContainerName, symbol.ContainerName, StringComparison.Ordinal)
                    && string.Equals(existing.QualifiedContainerName, symbol.ContainerQualifiedName, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                targets.Add((
                    symbol.ContainerName!,
                    symbol.ContainerQualifiedName,
                    target.AllowShortNameFallback));
        }

        return lookup;
    }

    private static Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> BuildCSharpQualifiedTypePatternLookup(
        string language,
        IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "interface" or "enum" or "delegate")
                || string.IsNullOrWhiteSpace(symbol.Name)
                || string.IsNullOrWhiteSpace(symbol.ContainerName))
            {
                continue;
            }

            if (!lookup.TryGetValue(symbol.Name, out var targets))
            {
                targets = [];
                lookup[symbol.Name] = targets;
            }

            bool exists = false;
            foreach (var existing in targets)
            {
                if (string.Equals(existing.ContainerName, symbol.ContainerName, StringComparison.Ordinal)
                    && string.Equals(existing.QualifiedContainerName, symbol.ContainerQualifiedName, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                targets.Add((symbol.ContainerName!, symbol.ContainerQualifiedName, AllowShortNameFallback: true));
        }

        return lookup;
    }

    private static bool IsCSharpConstMemberSymbol(SymbolRecord symbol)
    {
        if (symbol.ContainerKind is not ("class" or "struct"))
            return false;
        if (string.IsNullOrWhiteSpace(symbol.Signature))
            return false;

        return symbol.Signature!.Contains(" const ", StringComparison.Ordinal)
            || symbol.Signature.StartsWith("const ", StringComparison.Ordinal);
    }

    private static void EmitCSharpQualifiedEnumMemberReferences(
        string preparedLine,
        IReadOnlyDictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>> enumMemberLookup,
        IReadOnlyList<(int start, int end)>? csharpAttrRangesOnLine,
        IReadOnlyList<CSharpUsingAliasRecord> usingAliases,
        IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames> valueReceiverNamesByContainingType,
        IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> valueReceiverNamesByFunctionStartLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var scan = 0;
        while (scan < preparedLine.Length)
        {
            if (!TryReadCSharpQualifiedAccess(preparedLine, scan, out var parsed))
            {
                scan++;
                continue;
            }

            scan = Math.Max(scan + 1, parsed.NextIndex);
            if (!parsed.LastSeparatorWasDot || parsed.Segments.Count < 2)
                continue;

            var member = parsed.Segments[^1];
            var memberName = preparedLine.Substring(member.Start, member.End - member.Start);
            if (!enumMemberLookup.TryGetValue(memberName, out var targets))
                continue;

            var callContainer = resolveContainerForCall(member.Start);
            var qualifier = TrimLeadingCSharpGlobalQualifier(NormalizeCSharpQualifiedSegments(preparedLine, parsed.Segments, parsed.Segments.Count - 1));
            var resolvedQualifier = parsed.HasLeadingGlobalQualifier
                ? qualifier
                : ResolveCSharpQualifiedAliasTarget(qualifier, lineNumber, usingAliases);
            if (!parsed.HasLeadingGlobalQualifier
                && HasCSharpValueReceiverConflict(qualifier, resolvedQualifier, lineNumber, member.Start, callContainer, valueReceiverNamesByContainingType, valueReceiverNamesByFunctionStartLine))
                continue;
            if (!MatchesQualifiedConstantContainer(
                    resolvedQualifier,
                    targets,
                    allowShortNameFallback: !parsed.HasLeadingGlobalQualifier,
                    allowSingleSegmentQualifiedMatch: parsed.HasLeadingGlobalQualifier))
                continue;

            if (IsCSharpQualifiedConstantPatternReferenceSite(preparedLine, parsed))
                continue;

            var nextTokenIndex = SkipWhitespace(preparedLine, member.End);
            if (nextTokenIndex < preparedLine.Length && preparedLine[nextTokenIndex] == '(')
                continue;

            var insideCSharpAttributeRange = csharpAttrRangesOnLine != null
                && IsInsideCSharpAttributeRange(csharpAttrRangesOnLine, member.Start);
            var referenceKind = TryClassifyMetadataReference("csharp", preparedLine, member.Start, insideCSharpAttributeRange) ?? "call";

            AddReference(
                references,
                seen,
                fileId,
                memberName,
                member.Start,
                referenceKind,
                context,
                lineNumber,
                callContainer);
        }
    }

    private static bool IsCSharpQualifiedConstantPatternReferenceSite(
        string preparedLine,
        (List<(int Start, int End)> Segments, int NextIndex, bool LastSeparatorWasDot, bool HasLeadingGlobalQualifier) parsed)
    {
        if (!parsed.LastSeparatorWasDot || parsed.Segments.Count < 2)
            return false;

        var headCursor = parsed.Segments[0].Start;
        if (parsed.HasLeadingGlobalQualifier
            && headCursor >= "global::".Length
            && preparedLine.AsSpan(headCursor - "global::".Length, "global::".Length).Equals("global::", StringComparison.Ordinal))
        {
            headCursor -= "global::".Length;
        }

        return IsCSharpConstantPatternAnchor(preparedLine, ref headCursor);
    }

    private static bool IsCSharpConstantPatternAnchor(string text, ref int cursor)
    {
        cursor = SkipCSharpTriviaBackward(text, cursor);
        if (TryConsumeTrailingCSharpToken(text, ref cursor, "not"))
            cursor = SkipCSharpTriviaBackward(text, cursor);

        while (true)
        {
            if (TryConsumeTrailingCSharpToken(text, ref cursor, "case"))
                return true;

            if (TryConsumeTrailingCSharpToken(text, ref cursor, "is"))
                return false;

            if (!TryConsumeTrailingCSharpToken(text, ref cursor, "or")
                && !TryConsumeTrailingCSharpToken(text, ref cursor, "and"))
            {
                return false;
            }

            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (!SkipCSharpPatternHeadBackward(text, ref cursor))
                return false;
            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (TryConsumeTrailingCSharpToken(text, ref cursor, "not"))
                cursor = SkipCSharpTriviaBackward(text, cursor);
        }
    }

    private static int SkipCSharpTriviaBackward(string text, int cursor)
    {
        while (cursor > 0)
        {
            if (char.IsWhiteSpace(text[cursor - 1]))
            {
                cursor--;
                continue;
            }

            if (cursor >= 2
                && text[cursor - 1] == '/'
                && text[cursor - 2] == '*')
            {
                var commentStart = text.LastIndexOf("/*", cursor - 2, StringComparison.Ordinal);
                if (commentStart >= 0)
                {
                    cursor = commentStart;
                    continue;
                }
            }

            break;
        }

        return cursor;
    }

      private static bool IsCSharpPatternHeadCallSite(string[] preparedLines, int lineIndex, string preparedLine, int nameIndex)
      {
          var whenOffset = FindTopLevelCSharpWhenKeywordOffset(preparedLine);
          if (whenOffset >= 0 && nameIndex > whenOffset)
              return false;

          var cursor = nameIndex;
          if (IsCSharpConstantPatternAnchor(preparedLine, ref cursor))
              return true;

        cursor = nameIndex;
        cursor = SkipCSharpTriviaBackward(preparedLine, cursor);
        if (TryConsumeTrailingCSharpToken(preparedLine, ref cursor, "not"))
            cursor = SkipCSharpTriviaBackward(preparedLine, cursor);

        if (TryConsumeTrailingCSharpToken(preparedLine, ref cursor, "is"))
            return true;

        for (var previous = lineIndex - 1; previous >= 0; previous--)
        {
            var previousLine = preparedLines[previous];
            if (string.IsNullOrWhiteSpace(previousLine))
                continue;

            if (LineEndsWithCSharpToken(previousLine, "case")
                || LineEndsWithCSharpToken(previousLine, "is")
                || LineEndsWithCSharpToken(previousLine, "not"))
            {
                return true;
            }

            break;
        }

        // Switch-expression arms (`Point(...) => ...`) do not have a `case` / `is` anchor,
        // so the same positional pattern suppression has to look for the trailing arrow.
        if (IsCSharpSwitchExpressionPatternHead(preparedLines, lineIndex, preparedLine, nameIndex))
            return true;

        return false;
    }

    private static bool IsCSharpSwitchExpressionPatternHead(string[] preparedLines, int lineIndex, string preparedLine, int nameIndex)
    {
        var cursor = nameIndex;
        while (cursor < preparedLine.Length && IsCSharpIdentifierPart(preparedLine[cursor]))
            cursor++;

        cursor = SkipCSharpTriviaForward(preparedLine, cursor);

        var openParenIndex = preparedLine.IndexOf('(', cursor);
        if (openParenIndex < 0)
            return false;

        var parenDepth = 0;
        for (var i = openParenIndex; i < preparedLine.Length; i++)
        {
            switch (preparedLine[i])
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        var afterClose = SkipCSharpTriviaForward(preparedLine, i + 1);
                        if (afterClose + 1 < preparedLine.Length
                            && preparedLine[afterClose] == '='
                            && preparedLine[afterClose + 1] == '>')
                        {
                            return true;
                        }

                        for (var next = lineIndex + 1; next < preparedLines.Length; next++)
                        {
                            var nextLine = preparedLines[next];
                            if (string.IsNullOrWhiteSpace(nextLine))
                                continue;

                            var nextCursor = SkipCSharpTriviaForward(nextLine, 0);
                            return nextCursor + 1 < nextLine.Length
                                && nextLine[nextCursor] == '='
                                && nextLine[nextCursor + 1] == '>';
                        }

                        return false;
                    }
                    break;
            }
        }

        return false;
    }

    private static bool LineEndsWithCSharpToken(string text, string token)
    {
        var cursor = text.Length;
        return TryConsumeTrailingCSharpToken(text, ref cursor, token);
    }

    private static bool TryConsumeTrailingCSharpToken(string text, ref int cursor, string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        cursor = SkipCSharpTriviaBackward(text, cursor);
        if (cursor < token.Length)
            return false;

        var tokenStart = cursor - token.Length;
        if (!text.AsSpan(tokenStart, token.Length).Equals(token, StringComparison.Ordinal))
            return false;

        if ((tokenStart > 0 && IsCSharpIdentifierPart(text[tokenStart - 1]))
            || (cursor < text.Length && IsCSharpIdentifierPart(text[cursor])))
        {
            return false;
        }

        cursor = tokenStart;
        return true;
    }

    private static bool SkipCSharpPatternHeadBackward(string text, ref int cursor)
    {
        if (!TryConsumeTrailingCSharpIdentifier(text, ref cursor))
            return false;

        while (true)
        {
            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (cursor >= 2
                && text[cursor - 2] == ':'
                && text[cursor - 1] == ':')
            {
                cursor -= 2;
            }
            else if (cursor > 0 && text[cursor - 1] == '.')
            {
                cursor--;
            }
            else
            {
                break;
            }

            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (!TryConsumeTrailingCSharpIdentifier(text, ref cursor))
                return false;
        }

        return true;
    }

    private static bool TryConsumeTrailingCSharpIdentifier(string text, ref int cursor)
    {
        var end = cursor;
        while (cursor > 0 && IsCSharpIdentifierPart(text[cursor - 1]))
            cursor--;

        if (cursor == end)
            return false;

        if (cursor > 0 && text[cursor - 1] == '@')
            cursor--;

        return true;
    }

    private static bool TryReadCSharpQualifiedAccess(
        string preparedLine,
        int start,
        out (List<(int Start, int End)> Segments, int NextIndex, bool LastSeparatorWasDot, bool HasLeadingGlobalQualifier) parsed)
    {
        parsed = (new List<(int Start, int End)>(), start, false, false);

        if (start > 0 && IsCSharpIdentifierPart(preparedLine[start - 1]))
            return false;
        if (start >= preparedLine.Length || !IsCSharpIdentifierStart(preparedLine[start]))
            return false;

        var segments = new List<(int Start, int End)>();
        var cursor = start;
        var lastSeparatorWasDot = false;
        var hasLeadingGlobalQualifier = false;
        while (true)
        {
            if (!TryConsumeCSharpIdentifier(preparedLine, ref cursor, out var segmentStart, out var segmentEnd))
                return false;

            segments.Add((segmentStart, segmentEnd));

            var separatorStart = SkipWhitespace(preparedLine, cursor);
            if (separatorStart + 1 < preparedLine.Length
                && preparedLine[separatorStart] == ':'
                && preparedLine[separatorStart + 1] == ':')
            {
                if (segments.Count == 1
                    && segmentEnd - segmentStart == "global".Length
                    && string.CompareOrdinal(preparedLine, segmentStart, "global", 0, "global".Length) == 0)
                {
                    hasLeadingGlobalQualifier = true;
                }

                cursor = SkipWhitespace(preparedLine, separatorStart + 2);
                lastSeparatorWasDot = false;
                continue;
            }

            if (separatorStart < preparedLine.Length && preparedLine[separatorStart] == '.')
            {
                cursor = SkipWhitespace(preparedLine, separatorStart + 1);
                lastSeparatorWasDot = true;
                continue;
            }

            parsed = (segments, cursor, lastSeparatorWasDot, hasLeadingGlobalQualifier);
            return true;
        }
    }

    private static bool TryConsumeCSharpIdentifier(
        string preparedLine,
        ref int cursor,
        out int start,
        out int end)
    {
        start = cursor;
        if (cursor >= preparedLine.Length || !IsCSharpIdentifierStart(preparedLine[cursor]))
        {
            end = cursor;
            return false;
        }

        cursor++;
        while (cursor < preparedLine.Length && IsCSharpIdentifierPart(preparedLine[cursor]))
            cursor++;

        end = cursor;
        return true;
    }

    private static bool TryConsumeCSharpPatternKeyword(string preparedLine, ref int cursor, string keyword)
    {
        if (!preparedLine.AsSpan(cursor).StartsWith(keyword, StringComparison.Ordinal))
            return false;

        int afterKeyword = cursor + keyword.Length;
        if (afterKeyword < preparedLine.Length && !char.IsWhiteSpace(preparedLine[afterKeyword]))
            return false;

        cursor = afterKeyword;
        return true;
    }

    private static bool IsCSharpCaseTypePatternContinuation(
        string preparedLine,
        string typeExpression,
        int cursor,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        int lineNumber)
    {
        if (IsCSharpNonTypePatternExpression(typeExpression))
            return false;

        if (cursor >= preparedLine.Length)
            return false;

        return preparedLine[cursor] switch
        {
            ':' => !IsCSharpConstantPatternMemberHead(
                    typeExpression,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate),
            '{' or '(' or '[' => true,
            _ => IsCSharpCaseTypePatternIdentifier(
                preparedLine,
                typeExpression,
                cursor,
                csharpQualifiedConstantPatternMemberLookup,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate,
                lineNumber)
        };
    }

    private static bool IsCSharpCaseTypePatternIdentifier(
        string preparedLine,
        string typeExpression,
        int cursor,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        int lineNumber)
    {
        int tokenCursor = cursor;
        if (!TryConsumeCSharpIdentifier(preparedLine, ref tokenCursor, out var start, out var end))
            return false;

        var rawToken = preparedLine[start..end];
        if (rawToken.Length > 0 && rawToken[0] == '@')
            return true;

        return rawToken switch
        {
            "when" => !IsCSharpConstantPatternMemberHead(
                    typeExpression,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate),
            "or" or "and" => !IsCSharpLogicalConstantPatternHead(
                preparedLine,
                typeExpression,
                tokenCursor,
                lineNumber,
                csharpQualifiedConstantPatternMemberLookup,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate),
            _ => true,
        };
    }

    private static bool TryEmitCSharpLogicalTypePatternHeads(
        string preparedLine,
        string initialTypeExpression,
        int initialTypeIndex,
        int continuationIndex,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        Action<string, int> emitTypeExpression)
    {
        var currentTypeExpression = initialTypeExpression;
        var currentTypeIndex = initialTypeIndex;
        var currentContinuationIndex = continuationIndex;
        var sawLogicalKeyword = false;
        var emittedAny = false;
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
                emitTypeExpression(currentTypeExpression, currentTypeIndex);
                emittedAny = true;
            }

            int nextTypeCursor = nextHeadCursor;
            if (TryConsumeCSharpPatternKeyword(preparedLine, ref nextTypeCursor, "not"))
                nextTypeCursor = SkipWhitespace(preparedLine, nextTypeCursor);

            var nextMatch = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, nextTypeCursor);
            if (!nextMatch.Success)
                return false;

            var nextTypeGroup = nextMatch.Groups["type"];
            currentTypeExpression = nextTypeGroup.Value;
            currentTypeIndex = nextTypeGroup.Index;
            currentContinuationIndex = SkipWhitespace(preparedLine, nextTypeGroup.Index + nextTypeGroup.Length);
        }

        if (sawLogicalKeyword
            && !IsCSharpNonTypePatternExpression(currentTypeExpression)
            && !IsCSharpConstantPatternMemberHead(
                currentTypeExpression,
                lineNumber,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate))
        {
            emitTypeExpression(currentTypeExpression, currentTypeIndex);
            emittedAny = true;
        }

        return emittedAny;
    }

    private static bool IsCSharpLogicalConstantPatternAtCursor(
        string preparedLine,
        string typeExpression,
        int cursor,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate)
    {
        int tokenCursor = cursor;
        if (!TryConsumeCSharpIdentifier(preparedLine, ref tokenCursor, out var start, out var end))
            return false;

        var rawToken = preparedLine[start..end];
        if (rawToken is not ("or" or "and"))
            return false;

        return IsCSharpLogicalConstantPatternHead(
            preparedLine,
            typeExpression,
            tokenCursor,
            lineNumber,
            csharpQualifiedConstantPatternMemberLookup,
            csharpQualifiedTypePatternLookup,
            csharpUsingAliases,
            csharpUsingStatics,
            hasActiveSameFileCSharpTypeCandidate);
    }

    private static bool TryConsumeCSharpLogicalPatternKeyword(
        string preparedLine,
        int cursor,
        out int nextHeadCursor)
    {
        nextHeadCursor = cursor;
        int tokenCursor = cursor;
        if (!TryConsumeCSharpIdentifier(preparedLine, ref tokenCursor, out var start, out var end))
            return false;

        var rawToken = preparedLine[start..end];
        if (rawToken is not ("or" or "and"))
            return false;

        nextHeadCursor = SkipWhitespace(preparedLine, tokenCursor);
        return true;
    }

    private static bool IsCSharpLogicalConstantPatternHead(
        string preparedLine,
        string typeExpression,
        int cursor,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate)
    {
        if (IsCSharpConstantPatternMemberHead(
                typeExpression,
                lineNumber,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate))
        {
            return true;
        }

        if (IsCSharpQualifiedTypePatternHead(
                typeExpression,
                lineNumber,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases))
        {
            return false;
        }

        if (!TryReadCSharpQualifiedAccess(typeExpression, 0, out var currentParsed)
            || !currentParsed.LastSeparatorWasDot
            || currentParsed.Segments.Count < 2)
        {
            return false;
        }

        var currentQualifier = ResolveCSharpQualifiedConstantPatternQualifier(typeExpression, currentParsed, lineNumber, csharpUsingAliases);
        if (string.IsNullOrWhiteSpace(currentQualifier))
            return false;

        int nextCursor = SkipWhitespace(preparedLine, cursor);
        if (TryConsumeCSharpPatternKeyword(preparedLine, ref nextCursor, "not"))
            nextCursor = SkipWhitespace(preparedLine, nextCursor);

        var nextMatch = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, nextCursor);
        if (!nextMatch.Success)
            return false;

        var nextTypeExpression = nextMatch.Groups["type"].Value;
        if (IsCSharpQualifiedTypePatternHead(
                nextTypeExpression,
                lineNumber,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases))
        {
            return false;
        }

        if (!TryReadCSharpQualifiedAccess(nextTypeExpression, 0, out var nextParsed)
            || !nextParsed.LastSeparatorWasDot
            || nextParsed.Segments.Count < 2)
        {
            return false;
        }

        var nextQualifier = ResolveCSharpQualifiedConstantPatternQualifier(nextTypeExpression, nextParsed, lineNumber, csharpUsingAliases);
        return string.Equals(currentQualifier, nextQualifier, StringComparison.Ordinal);
    }

    private static bool IsCSharpQualifiedTypePatternHead(
        string typeExpression,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        if (!TryReadCSharpQualifiedAccess(typeExpression, 0, out var parsed)
            || !parsed.LastSeparatorWasDot
            || parsed.Segments.Count < 2)
        {
            return false;
        }

        var member = parsed.Segments[^1];
        var memberName = typeExpression.Substring(member.Start, member.End - member.Start);
        if (!csharpQualifiedTypePatternLookup.TryGetValue(memberName, out var targets))
            return false;

        var resolvedQualifier = ResolveCSharpQualifiedConstantPatternQualifier(typeExpression, parsed, lineNumber, csharpUsingAliases);
        bool qualifierHasMultipleSegments = resolvedQualifier.Contains('.') || resolvedQualifier.Contains("::", StringComparison.Ordinal);
        return MatchesQualifiedConstantContainer(
            resolvedQualifier,
            targets,
            allowShortNameFallback: !parsed.HasLeadingGlobalQualifier && !qualifierHasMultipleSegments,
            allowSingleSegmentQualifiedMatch: parsed.HasLeadingGlobalQualifier);
    }

    private static string ResolveCSharpQualifiedConstantPatternQualifier(
        string typeExpression,
        (List<(int Start, int End)> Segments, int NextIndex, bool LastSeparatorWasDot, bool HasLeadingGlobalQualifier) parsed,
        int lineNumber,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        var qualifier = TrimLeadingCSharpGlobalQualifier(NormalizeCSharpQualifiedSegments(typeExpression, parsed.Segments, parsed.Segments.Count - 1));
        return parsed.HasLeadingGlobalQualifier
            ? qualifier
            : ResolveCSharpQualifiedAliasTarget(qualifier, lineNumber, csharpUsingAliases);
    }

    private static bool IsCSharpQualifiedConstantPatternMemberHead(
        string typeExpression,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        if (!TryReadCSharpQualifiedAccess(typeExpression, 0, out var parsed)
            || !parsed.LastSeparatorWasDot
            || parsed.Segments.Count < 2)
        {
            return false;
        }

        var member = parsed.Segments[^1];
        var memberName = typeExpression.Substring(member.Start, member.End - member.Start);
        if (!csharpQualifiedConstantPatternMemberLookup.TryGetValue(memberName, out var targets))
            return false;

        var resolvedQualifier = ResolveCSharpQualifiedConstantPatternQualifier(typeExpression, parsed, lineNumber, csharpUsingAliases);
        bool qualifierHasMultipleSegments = resolvedQualifier.Contains('.') || resolvedQualifier.Contains("::", StringComparison.Ordinal);
        return MatchesQualifiedConstantContainer(
            resolvedQualifier,
            targets,
            allowShortNameFallback: !parsed.HasLeadingGlobalQualifier && !qualifierHasMultipleSegments,
            allowSingleSegmentQualifiedMatch: parsed.HasLeadingGlobalQualifier);
    }

    private static bool IsCSharpConstantPatternMemberHead(
        string typeExpression,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate)
    {
        return IsCSharpQualifiedConstantPatternMemberHead(
            typeExpression,
            lineNumber,
            csharpQualifiedConstantPatternMemberLookup,
            csharpUsingAliases);
    }

    private static bool IsCSharpNonTypePatternExpression(string typeExpression)
    {
        var trimmed = typeExpression.Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed[0] == '@')
            return false;

        return trimmed.IndexOf('.') < 0
            && trimmed.IndexOf(':') < 0
            && trimmed.IndexOf('<') < 0
            && trimmed.IndexOf('[') < 0
            && trimmed.IndexOf('?') < 0
            && trimmed.IndexOf(' ') < 0
            && CSharpNonTypePatternTokens.Contains(trimmed);
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static string NormalizeCSharpIdentifier(string identifier) =>
        !string.IsNullOrEmpty(identifier) && identifier[0] == '@'
            ? identifier[1..]
            : identifier;

    private static string NormalizeAtPrefixedIdentifier(string identifier) =>
        !string.IsNullOrEmpty(identifier) && identifier[0] == '@'
            ? identifier[1..]
            : identifier;

    private static string NormalizeRustIdentifier(string identifier)
    {
        if (identifier.Length == 0)
            return identifier;

        var segments = identifier.Split("::");
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.StartsWith("r#", StringComparison.Ordinal))
                segments[i] = segment[2..];
        }

        return string.Join("::", segments);
    }

    private static bool IsRustFunctionDeclarationCallSite(string line, int callIndex)
    {
        if (callIndex <= 0)
            return false;

        var prefix = line[..callIndex].TrimEnd();
        return prefix.EndsWith("fn", StringComparison.Ordinal);
    }

    private static bool IsRustRawIdentifierPrefix(string line, int callIndex) =>
        callIndex >= 2
        && line[callIndex - 2] == 'r'
        && line[callIndex - 1] == '#';

    private static string NormalizeShellSourceTargetToken(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length >= 2)
        {
            var quote = trimmed[0];
            if ((quote == '\'' || quote == '"') && trimmed[^1] == quote)
                return trimmed[1..^1];
        }

        return trimmed;
    }

    private static string StripShellComment(string line)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '"')
                {
                    inDoubleQuote = false;
                    continue;
                }

                if (ch == '\\')
                    escapeNext = true;
                continue;
            }

            if (ch == '#')
                return line[..i];

            if (ch == '\'')
            {
                inSingleQuote = true;
                continue;
            }

            if (ch == '"')
            {
                inDoubleQuote = true;
                continue;
            }

            if (ch == '\\')
                escapeNext = true;
        }

        return line;
    }

    private static void EmitCssScssReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in ScssVariableReferenceRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (ShouldSkipScssVariableReference(preparedLine, nameGroup.Index))
                continue;

            AddReference(
                references,
                seen,
                fileId,
                nameGroup.Value,
                nameGroup.Index,
                "call",
                context,
                lineNumber,
                container);
        }

        foreach (Match match in ScssExtendReferenceRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            AddReference(
                references,
                seen,
                fileId,
                nameGroup.Value,
                nameGroup.Index,
                "call",
                context,
                lineNumber,
                container);
        }
    }

    private static void EmitPhpStaticAccessReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in PhpStaticAccessRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            var rawName = nameGroup.Value;
            var trimmedName = rawName.TrimStart('\\');
            if (trimmedName.Length == 0)
                continue;

            var leadingBackslashCount = rawName.Length - trimmedName.Length;
            var shortNameStart = trimmedName.LastIndexOf('\\') + 1;
            var shortName = trimmedName[shortNameStart..];
            if (shortName.Length == 0)
                continue;

            var qualifiedNameIndex = nameGroup.Index + leadingBackslashCount;
            if (trimmedName.Length > shortName.Length)
            {
                AddReference(
                    references,
                    seen,
                    fileId,
                    trimmedName,
                    qualifiedNameIndex,
                    "type_reference",
                    context,
                    lineNumber,
                    container);
            }

            if (!string.Equals(shortName, "self", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(shortName, "static", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(shortName, "parent", StringComparison.OrdinalIgnoreCase))
            {
                var shortNameIndex = qualifiedNameIndex + shortNameStart;
                AddReference(
                    references,
                    seen,
                    fileId,
                    shortName,
                    shortNameIndex,
                    "type_reference",
                    context,
                    lineNumber,
                    container);
            }
        }
    }

    private static void EmitPhpObjectMemberAccessReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in PhpObjectMemberAccessRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            AddReference(
                references,
                seen,
                fileId,
                nameGroup.Value,
                nameGroup.Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    private static void EmitTerraformReferences(
        string preparedLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        foreach (Match match in TerraformVarReferenceRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (definitionNames != null && definitionNames.Contains(nameGroup.Value))
                continue;

            AddReference(references, seen, fileId, nameGroup.Value, nameGroup.Index, "reference", context, lineNumber, container);
        }

        foreach (Match match in TerraformLocalReferenceRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (definitionNames != null && definitionNames.Contains(nameGroup.Value))
                continue;

            AddReference(references, seen, fileId, nameGroup.Value, nameGroup.Index, "reference", context, lineNumber, container);
        }

        foreach (Match match in TerraformModuleReferenceRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (definitionNames != null && definitionNames.Contains(nameGroup.Value))
                continue;

            AddReference(references, seen, fileId, nameGroup.Value, nameGroup.Index, "reference", context, lineNumber, container);
        }

        foreach (Match match in TerraformDataReferenceRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (definitionNames != null && definitionNames.Contains(nameGroup.Value))
                continue;

            AddReference(references, seen, fileId, nameGroup.Value, nameGroup.Index, "reference", context, lineNumber, container);
        }

        foreach (Match match in TerraformResourceReferenceRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (definitionNames != null && definitionNames.Contains(nameGroup.Value))
                continue;

            if (IsTerraformSpecialReferencePrefix(preparedLine, match.Index))
                continue;

            AddReference(references, seen, fileId, nameGroup.Value, nameGroup.Index, "reference", context, lineNumber, container);
        }
    }

    private static bool IsTerraformSpecialReferencePrefix(string line, int typeStartIndex)
    {
        return HasTerraformPrefix(line, typeStartIndex, "var")
            || HasTerraformPrefix(line, typeStartIndex, "local")
            || HasTerraformPrefix(line, typeStartIndex, "module")
            || HasTerraformPrefix(line, typeStartIndex, "data");
    }

    private static bool HasTerraformPrefix(string line, int typeStartIndex, string prefix)
    {
        int prefixStart = typeStartIndex - prefix.Length - 1;
        if (prefixStart < 0)
            return false;

        return line.AsSpan(prefixStart, prefix.Length).SequenceEqual(prefix)
            && line[prefixStart + prefix.Length] == '.';
    }

    private static bool ShouldSkipScssVariableReference(string preparedLine, int variableIndex)
    {
        var trimmed = preparedLine.TrimStart();
        if (trimmed.StartsWith("$", StringComparison.Ordinal))
        {
            var declarationColonIndex = preparedLine.IndexOf(':', variableIndex);
            if (declarationColonIndex >= 0)
                return true;
        }

        if (trimmed.StartsWith("@mixin", StringComparison.Ordinal)
            || trimmed.StartsWith("@function", StringComparison.Ordinal))
        {
            var braceIndex = preparedLine.IndexOf('{');
            if (braceIndex < 0)
                return true;
            if (variableIndex < braceIndex)
                return true;
        }

        return false;
    }

    private static string NormalizeCSharpQualifiedSegments(
        string preparedLine,
        IReadOnlyList<(int Start, int End)> segments,
        int count)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append('.');
            var (start, end) = segments[i];
            var segment = preparedLine.Substring(start, end - start);
            builder.Append(segment[0] == '@' ? segment[1..] : segment);
        }
        return builder.ToString();
    }

    private static string TrimLeadingCSharpGlobalQualifier(string qualifiedName) =>
        qualifiedName.StartsWith("global.", StringComparison.Ordinal)
            ? qualifiedName["global.".Length..]
            : qualifiedName;

    private static string? TryNormalizeCSharpQualifiedName(string candidate)
    {
        var trimmed = candidate.Trim();
        if (trimmed.StartsWith("global::", StringComparison.Ordinal))
            trimmed = trimmed["global::".Length..];
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;
        if (!TryReadCSharpQualifiedAccess(trimmed, 0, out var parsed))
            return null;
        if (SkipWhitespace(trimmed, parsed.NextIndex) != trimmed.Length)
            return null;
        return NormalizeCSharpQualifiedSegments(trimmed, parsed.Segments, parsed.Segments.Count);
    }

    private static string ResolveCSharpQualifiedAliasTarget(string qualifier, int lineNumber, IReadOnlyList<CSharpUsingAliasRecord> usingAliases)
    {
        if (string.IsNullOrWhiteSpace(qualifier) || usingAliases.Count == 0)
            return qualifier;

        var firstSegment = GetFirstQualifiedSegment(qualifier);
        string? aliasTarget = null;
        for (var i = usingAliases.Count - 1; i >= 0; i--)
        {
            var alias = usingAliases[i];
            if (alias.Line > lineNumber)
                continue;
            if (lineNumber < alias.ScopeStartLine || lineNumber > alias.ScopeEndLine)
                continue;
            if (!string.Equals(alias.AliasName, firstSegment, StringComparison.Ordinal))
                continue;

            aliasTarget = alias.TargetQualifiedName;
            break;
        }

        if (aliasTarget == null)
            return qualifier;

        return qualifier.Length == firstSegment.Length
            ? aliasTarget
            : aliasTarget + qualifier[firstSegment.Length..];
    }

    private static bool TryGetCSharpXmlDocCommentSpan(
        string line,
        bool inDelimitedDocComment,
        bool inOrdinaryBlockComment,
        out int commentStartIndex,
        out int commentEndExclusive,
        out bool nextDelimitedDocComment)
    {
        commentStartIndex = 0;
        commentEndExclusive = 0;
        nextDelimitedDocComment = inDelimitedDocComment;
        if (string.IsNullOrWhiteSpace(line))
        {
            commentEndExclusive = inDelimitedDocComment ? line.Length : 0;
            return inDelimitedDocComment;
        }

        var firstNonWhitespaceIndex = 0;
        while (firstNonWhitespaceIndex < line.Length && char.IsWhiteSpace(line[firstNonWhitespaceIndex]))
            firstNonWhitespaceIndex++;

        if (inDelimitedDocComment)
        {
            var closeIndex = line.IndexOf("*/", StringComparison.Ordinal);
            nextDelimitedDocComment = closeIndex < 0;
            commentStartIndex = 0;
            commentEndExclusive = closeIndex < 0 ? line.Length : closeIndex;
            return true;
        }

        if (inOrdinaryBlockComment)
            return false;

        if (line.AsSpan(firstNonWhitespaceIndex).StartsWith("///", StringComparison.Ordinal))
        {
            if (line.Length != firstNonWhitespaceIndex + 3 && line[firstNonWhitespaceIndex + 3] == '/')
                return false;

            commentStartIndex = firstNonWhitespaceIndex;
            commentEndExclusive = line.Length;
            return true;
        }

        if (!line.AsSpan(firstNonWhitespaceIndex).StartsWith("/**", StringComparison.Ordinal))
            return false;

        var closeAfterOpenIndex = line.IndexOf("*/", firstNonWhitespaceIndex + 3, StringComparison.Ordinal);
        nextDelimitedDocComment = closeAfterOpenIndex < 0;
        commentStartIndex = firstNonWhitespaceIndex;
        commentEndExclusive = closeAfterOpenIndex < 0 ? line.Length : closeAfterOpenIndex;
        return true;
    }

    private static bool HasActiveCSharpUsingStaticTarget(
        string targetQualifiedName,
        int lineNumber,
        IReadOnlyList<CSharpUsingStaticRecord> usingStatics)
    {
        if (string.IsNullOrWhiteSpace(targetQualifiedName))
            return false;

        for (var i = usingStatics.Count - 1; i >= 0; i--)
        {
            var import = usingStatics[i];
            if (import.Line > lineNumber)
                continue;
            if (lineNumber < import.ScopeStartLine || lineNumber > import.ScopeEndLine)
                continue;
            if (string.Equals(import.TargetQualifiedName, targetQualifiedName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool HasCSharpValueReceiverConflict(
        string qualifier,
        string resolvedQualifier,
        int lineNumber,
        int column,
        SymbolRecord? callContainer,
        IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames> valueReceiverNamesByContainingType,
        IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> valueReceiverNamesByFunctionStartLine)
    {
        if (string.IsNullOrWhiteSpace(qualifier)
            || (valueReceiverNamesByContainingType.Count == 0 && valueReceiverNamesByFunctionStartLine.Count == 0))
            return false;
        if (!string.Equals(qualifier, resolvedQualifier, StringComparison.Ordinal))
            return false;

        var receiverName = GetFirstQualifiedSegment(qualifier);
        if (string.IsNullOrWhiteSpace(receiverName))
            return false;

        if (callContainer != null
            && (callContainer.Kind == "function" || callContainer.Kind == "property")
            && valueReceiverNamesByFunctionStartLine.TryGetValue(callContainer.StartLine, out var functionNames)
            && functionNames.Any(record => IsWithinCSharpScope(record, lineNumber, column)
                && string.Equals(record.Name, receiverName, StringComparison.Ordinal)))
        {
            return true;
        }

        var containingType = GetContainingTypeQualifiedName(callContainer);
        return containingType != null
            && valueReceiverNamesByContainingType.TryGetValue(containingType, out var names)
            && (IsStaticCSharpSymbol(callContainer)
                ? names.StaticNames.Contains(receiverName)
                : names.StaticNames.Contains(receiverName) || names.InstanceNames.Contains(receiverName));
    }

    private static string? GetContainingTypeQualifiedName(SymbolRecord? symbol)
    {
        if (symbol == null)
            return null;
        if (IsTypeLikeSymbolKind(symbol.Kind))
            return CombineQualifiedName(symbol.ContainerQualifiedName, symbol.Name);
        return symbol.ContainerQualifiedName;
    }

    private static bool IsTypeLikeSymbolKind(string? kind) =>
        kind is "class" or "struct" or "interface";

    private static string? CombineQualifiedName(string? parentQualifiedName, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (string.IsNullOrWhiteSpace(parentQualifiedName))
            return name;
        return $"{parentQualifiedName}.{name}";
    }

    private static bool IsWithinCSharpScope(CSharpFunctionValueReceiverNameRecord record, int lineNumber, int column)
    {
        var startsBefore = lineNumber > record.ScopeStartLine
            || (lineNumber == record.ScopeStartLine && column >= record.ScopeStartColumn);
        if (!startsBefore)
            return false;

        return lineNumber < record.ScopeEndLine
            || (lineNumber == record.ScopeEndLine && column < record.ScopeEndColumn);
    }

    private static void AddCSharpParameterNames(List<CSharpFunctionValueReceiverNameRecord> names, string? signature, int scopeStartLine, int scopeStartColumn, int scopeEndLine, int scopeEndColumn)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return;

        var openParen = signature.IndexOf('(');
        var closeParen = signature.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen)
            return;

        var parameters = signature[(openParen + 1)..closeParen];
        foreach (var segment in SplitTopLevelCSharpParameterSegments(parameters))
        {
            if (TryExtractTrailingCSharpParameterName(segment, out var name))
                AddCSharpFunctionValueReceiverName(names, name, scopeStartLine, scopeStartColumn, scopeEndLine, scopeEndColumn);
        }
    }

    private static List<string> SplitTopLevelCSharpParameterSegments(string parameters)
    {
        var segments = new List<string>();
        var depthAngle = 0;
        var depthParen = 0;
        var depthBracket = 0;
        var depthBrace = 0;
        var segmentStart = 0;

        for (var i = 0; i < parameters.Length; i++)
        {
            var ch = parameters[i];
            switch (ch)
            {
                case '<':
                    depthAngle++;
                    break;
                case '>':
                    if (depthAngle > 0)
                        depthAngle--;
                    break;
                case '(':
                    depthParen++;
                    break;
                case ')':
                    if (depthParen > 0)
                        depthParen--;
                    break;
                case '[':
                    depthBracket++;
                    break;
                case ']':
                    if (depthBracket > 0)
                        depthBracket--;
                    break;
                case '{':
                    depthBrace++;
                    break;
                case '}':
                    if (depthBrace > 0)
                        depthBrace--;
                    break;
                case ',':
                    if (depthAngle == 0 && depthParen == 0 && depthBracket == 0 && depthBrace == 0)
                    {
                        segments.Add(parameters[segmentStart..i]);
                        segmentStart = i + 1;
                    }
                    break;
            }
        }

        if (segmentStart <= parameters.Length)
            segments.Add(parameters[segmentStart..]);

        return segments;
    }

    private static bool TryExtractTrailingCSharpParameterName(string segment, out string name)
    {
        name = string.Empty;
        var trimmed = segment.Trim();
        if (trimmed.Length == 0 || trimmed == "this")
            return false;

        var end = trimmed.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(trimmed[end]))
            end--;
        while (end >= 0 && (trimmed[end] == '?' || trimmed[end] == '!'))
            end--;
        var start = end;
        while (start >= 0 && IsCSharpIdentifierPart(trimmed[start]))
            start--;
        if (end < 0 || start >= end)
            return false;

        name = NormalizeCSharpIdentifier(trimmed[(start + 1)..(end + 1)]);
        return !string.IsNullOrWhiteSpace(name);
    }

    private static void AddCSharpLambdaParameterNames(List<CSharpFunctionValueReceiverNameRecord> names, string bodyText, int startLineNumber, int scopeEndLine)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return;

        var searchIndex = 0;
        while (searchIndex < bodyText.Length)
        {
            var arrowIndex = bodyText.IndexOf("=>", searchIndex, StringComparison.Ordinal);
            if (arrowIndex < 0)
                break;

            var lambdaScopeEnd = FindCSharpArrowExpressionScopeEndPosition(bodyText, arrowIndex, startLineNumber, scopeEndLine);
            AddCSharpLambdaParametersBeforeArrow(names, bodyText, arrowIndex, startLineNumber, lambdaScopeEnd);
            searchIndex = arrowIndex + 2;
        }
    }

    private static void AddCSharpRecursivePatternValueReceiverNames(
        List<CSharpFunctionValueReceiverNameRecord> names,
        string bodyText,
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int bodyEndIndex)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return;

        var startLineNumber = bodyStartIndex + 1;
        foreach (var pattern in FindCSharpRecursivePatternValueNames(bodyText))
        {
            var position = GetLineColumnFromOffset(bodyText, pattern.Offset, startLineNumber);
            var declarationLineIndex = position.Line - 1;
            if (pattern.ArrowIndex >= 0)
            {
                var scopeEnd = FindCSharpArrowExpressionScopeEndPosition(bodyText, pattern.ArrowIndex, startLineNumber, bodyEndIndex + 1);
                AddCSharpFunctionValueReceiverName(names, pattern.Name, position.Line, position.Column, scopeEnd.Line, scopeEnd.Column);
                continue;
            }

            if (pattern.IsCasePattern)
            {
                if (!TryFindCSharpSwitchCaseScopeEndPosition(structuralLines, bodyEndIndex, declarationLineIndex, position.Column, out var scopeEnd))
                    continue;

                AddCSharpFunctionValueReceiverName(names, pattern.Name, position.Line, position.Column, scopeEnd.Line, scopeEnd.Column);
                continue;
            }

            if (!TryFindCSharpDeclarationPatternScopeEndPosition(structuralLines, bodyStartIndex, bodyEndIndex, declarationLineIndex, position.Column, out var declarationScopeEnd))
                continue;

            AddCSharpFunctionValueReceiverName(names, pattern.Name, position.Line, position.Column, declarationScopeEnd.Line, declarationScopeEnd.Column);
        }
    }

    private static IEnumerable<CSharpRecursivePatternValueNameRecord> FindCSharpRecursivePatternValueNames(string bodyText)
    {
        for (var index = 0; index < bodyText.Length; index++)
        {
            if (!IsCSharpIdentifierStart(bodyText[index]))
                continue;

            var tokenStart = index;
            index++;
            while (index < bodyText.Length && IsCSharpIdentifierPart(bodyText[index]))
                index++;

            var token = bodyText[tokenStart..index];
            if ((string.Equals(token, "is", StringComparison.Ordinal) || string.Equals(token, "case", StringComparison.Ordinal))
                && TryParseCSharpRecursivePatternDesignation(bodyText, index, string.Equals(token, "case", StringComparison.Ordinal), out var name, out var designationOffset))
            {
                yield return new CSharpRecursivePatternValueNameRecord(name, designationOffset, string.Equals(token, "case", StringComparison.Ordinal));
            }

            index--;
        }

        foreach (var pattern in FindCSharpSwitchExpressionPatternValueNames(bodyText))
            yield return pattern;
    }

    private static IEnumerable<CSharpRecursivePatternValueNameRecord> FindCSharpSwitchExpressionPatternValueNames(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            yield break;

        for (var searchIndex = 0; searchIndex < bodyText.Length;)
        {
            var arrowIndex = bodyText.IndexOf("=>", searchIndex, StringComparison.Ordinal);
            if (arrowIndex < 0)
                yield break;

            searchIndex = arrowIndex + 2;
            if (IsPotentialCSharpLambdaArrow(bodyText, arrowIndex))
                continue;

            if (!TryFindCSharpSwitchExpressionArmStartOffset(bodyText, arrowIndex, out var armStartOffset))
                continue;

            if (!TryParseCSharpSwitchExpressionArmPatternDesignation(bodyText, armStartOffset, arrowIndex, out var name, out var designationOffset))
                continue;

            yield return new CSharpRecursivePatternValueNameRecord(name, designationOffset, false, arrowIndex);
        }
    }

    private static bool TryFindCSharpSwitchExpressionArmStartOffset(string bodyText, int arrowIndex, out int armStartOffset)
    {
        armStartOffset = 0;
        if (arrowIndex <= 0 || arrowIndex > bodyText.Length)
            return false;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var index = arrowIndex - 1; index >= 0; index--)
        {
            var current = bodyText[index];
            switch (current)
            {
                case ')':
                    parenDepth++;
                    break;
                case '(':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case ']':
                    bracketDepth++;
                    break;
                case '[':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '}':
                    braceDepth++;
                    break;
                case '{':
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                        break;
                    }

                    if (parenDepth == 0 && bracketDepth == 0)
                    {
                        armStartOffset = SkipWhitespaceForward(bodyText, index + 1);
                        return armStartOffset < arrowIndex;
                    }

                    break;
                case ',':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    {
                        armStartOffset = SkipWhitespaceForward(bodyText, index + 1);
                        return armStartOffset < arrowIndex;
                    }

                    break;
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return false;
                    break;
            }
        }

        return false;
    }

    private static bool TryGetCSharpSwitchExpressionArmTypePatternRange(
        string bodyText,
        int arrowIndex,
        out int bodyStartOffset,
        out int armStartOffset,
        out int armPatternEndOffset)
    {
        bodyStartOffset = 0;
        armStartOffset = 0;
        armPatternEndOffset = 0;
        if (!TryFindCSharpSwitchExpressionBodyStartOffset(bodyText, arrowIndex, out bodyStartOffset))
            return false;

        var segmentStartOffset = bodyStartOffset + 1;
        if (segmentStartOffset >= arrowIndex)
            return false;

        var segmentText = bodyText[segmentStartOffset..arrowIndex];
        var lastCommaOffset = FindLastTopLevelCSharpComma(segmentText);
        var relativeArmStart = lastCommaOffset >= 0
            ? SkipWhitespaceForward(segmentText, lastCommaOffset + 1)
            : SkipWhitespaceForward(segmentText, 0);
        if (relativeArmStart >= segmentText.Length)
            return false;

        var armSegment = segmentText[relativeArmStart..];
        var whenOffset = FindTopLevelCSharpWhenKeywordOffset(armSegment);
        var relativePatternEnd = whenOffset >= 0
            ? relativeArmStart + whenOffset
            : segmentText.Length;
        while (relativePatternEnd > relativeArmStart && char.IsWhiteSpace(segmentText[relativePatternEnd - 1]))
            relativePatternEnd--;
        if (relativePatternEnd <= relativeArmStart)
            return false;

        armStartOffset = segmentStartOffset + relativeArmStart;
        armPatternEndOffset = segmentStartOffset + relativePatternEnd;
        return armStartOffset < armPatternEndOffset;
    }

    private static bool TryFindCSharpSwitchExpressionBodyStartOffset(string bodyText, int arrowIndex, out int bodyStartOffset)
    {
        bodyStartOffset = -1;
        if (arrowIndex <= 0 || arrowIndex > bodyText.Length)
            return false;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var index = arrowIndex - 1; index >= 0; index--)
        {
            var current = bodyText[index];
            switch (current)
            {
                case ')':
                    parenDepth++;
                    break;
                case '(':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case ']':
                    bracketDepth++;
                    break;
                case '[':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '}':
                    braceDepth++;
                    break;
                case '{':
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                        break;
                    }

                    if (parenDepth == 0 && bracketDepth == 0)
                    {
                        bodyStartOffset = index;
                        return true;
                    }

                    break;
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return false;
                    break;
            }
        }

        return false;
    }

    private static int FindLastTopLevelCSharpComma(string text)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var lastComma = -1;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case ',':
                    if (angleDepth == 0 && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        lastComma = i;
                    break;
            }
        }

        return lastComma;
    }

    private static bool TryParseCSharpSwitchExpressionArmPatternDesignation(
        string bodyText,
        int armStartOffset,
        int arrowIndex,
        out string name,
        out int designationOffset)
    {
        name = string.Empty;
        designationOffset = -1;
        if (armStartOffset < 0 || armStartOffset >= arrowIndex || arrowIndex > bodyText.Length)
            return false;

        var armText = bodyText[armStartOffset..arrowIndex];
        var preparedArmLines = StructuralLineMasker.MaskLines("csharp", armText.Split('\n'));
        for (var i = 0; i < preparedArmLines.Length; i++)
            preparedArmLines[i] = PrepareLine("csharp", preparedArmLines[i]);

        var preparedArmText = string.Join("\n", preparedArmLines);
        if (!TryParseCSharpRecursivePatternDesignation(preparedArmText, 0, false, out name, out var relativeOffset)
            && !TryParseCSharpSwitchExpressionArmDeclarationPatternDesignation(preparedArmText, out name, out relativeOffset))
        {
            return false;
        }

        designationOffset = armStartOffset + relativeOffset;
        return designationOffset < arrowIndex;
    }

    private static bool TryParseCSharpSwitchExpressionArmDeclarationPatternDesignation(
        string armText,
        out string name,
        out int designationOffset)
    {
        name = string.Empty;
        designationOffset = -1;
        if (string.IsNullOrWhiteSpace(armText))
            return false;

        var whenOffset = FindTopLevelCSharpWhenKeywordOffset(armText);
        var patternText = whenOffset >= 0 ? armText[..whenOffset] : armText;
        var match = CSharpSwitchExpressionDeclarationPatternValueNameRegex.Match(patternText);
        if (!match.Success)
            return false;

        name = NormalizeCSharpIdentifier(match.Groups["name"].Value);
        designationOffset = match.Groups["name"].Index;
        return designationOffset >= 0;
    }

    private static bool TryParseCSharpRecursivePatternDesignation(
        string bodyText,
        int index,
        bool isCasePattern,
        out string name,
        out int designationOffset)
    {
        name = string.Empty;
        designationOffset = -1;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var sawRecursiveClause = false;
        var previousTopLevelNonWhitespaceChar = '\0';
        for (var i = index; i < bodyText.Length; i++)
        {
            var current = bodyText[i];
            if (char.IsWhiteSpace(current))
                continue;

            if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0 && IsCSharpIdentifierStart(current))
            {
                var tokenStart = i;
                i++;
                while (i < bodyText.Length && IsCSharpIdentifierPart(bodyText[i]))
                    i++;

                var token = bodyText[tokenStart..i];
                i--;
                if (sawRecursiveClause
                    && previousTopLevelNonWhitespaceChar is not '.' and not ':' and not '<' and not '[' and not '?'
                    && !IsCSharpPatternControlKeyword(token))
                {
                    name = NormalizeCSharpIdentifier(token);
                    designationOffset = tokenStart;
                    return true;
                }

                previousTopLevelNonWhitespaceChar = token[^1];
                continue;
            }

            switch (current)
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    sawRecursiveClause = true;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
            }

            if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                previousTopLevelNonWhitespaceChar = current;
        }

        return false;
    }

    private static bool IsCSharpPatternControlKeyword(string token) =>
        token is "and" or "or" or "not" or "when" or "null" or "true" or "false";

    private static int FindTopLevelCSharpWhenKeywordOffset(string text)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            switch (current)
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
            }

            if (parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0
                && TryConsumeCSharpKeyword(text, i, "when", out _))
            {
                return i;
            }
        }

        return -1;
    }

    private static void AddCSharpLambdaParametersBeforeArrow(
        List<CSharpFunctionValueReceiverNameRecord> names,
        string bodyText,
        int arrowIndex,
        int startLineNumber,
        CSharpLineColumn scopeEnd)
    {
        var leftIndex = SkipWhitespaceBackward(bodyText, arrowIndex - 1);
        if (leftIndex < 0)
            return;

        var declarationLine = GetLineNumberFromOffset(bodyText, arrowIndex, startLineNumber);
        if (bodyText[leftIndex] == ')')
        {
            if (!TryFindMatchingOpenParen(bodyText, leftIndex, out var openParenIndex))
                return;

            var scopeStart = GetLineColumnFromOffset(bodyText, openParenIndex, startLineNumber);
            var parameters = bodyText[(openParenIndex + 1)..leftIndex];
            foreach (var segment in SplitTopLevelCSharpParameterSegments(parameters))
            {
                if (TryExtractTrailingCSharpParameterName(segment, out var parameterName))
                    AddCSharpFunctionValueReceiverName(names, parameterName, scopeStart.Line, scopeStart.Column, scopeEnd.Line, scopeEnd.Column);
            }

            return;
        }

        var identifierEnd = leftIndex + 1;
        var identifierStart = leftIndex;
        while (identifierStart >= 0 && IsCSharpIdentifierPart(bodyText[identifierStart]))
            identifierStart--;
        identifierStart++;
        if (identifierStart >= identifierEnd || !IsCSharpIdentifierStart(bodyText[identifierStart]))
            return;

        var parameter = NormalizeCSharpIdentifier(bodyText[identifierStart..identifierEnd]);
        var prefixIndex = SkipWhitespaceBackward(bodyText, identifierStart - 1);
        if (prefixIndex < 0)
            return;

        var prefixChar = bodyText[prefixIndex];
        if (prefixChar is '=' or '(' or ',' or ':'
            || (TryReadPreviousIdentifierToken(bodyText, prefixIndex, out var previousToken)
                && string.Equals(previousToken, "return", StringComparison.Ordinal)))
        {
            AddCSharpFunctionValueReceiverName(names, parameter, declarationLine, identifierStart - GetLineStartOffset(bodyText, arrowIndex), scopeEnd.Line, scopeEnd.Column);
        }
    }

    private static void AddCSharpFunctionValueReceiverName(List<CSharpFunctionValueReceiverNameRecord> names, string name, int scopeStartLine, int scopeStartColumn, int scopeEndLine, int scopeEndColumn)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (names.Any(record =>
            record.ScopeStartLine == scopeStartLine
            && record.ScopeStartColumn == scopeStartColumn
            && record.ScopeEndLine == scopeEndLine
            && record.ScopeEndColumn == scopeEndColumn
            && string.Equals(record.Name, name, StringComparison.Ordinal)))
            return;

        names.Add(new CSharpFunctionValueReceiverNameRecord(name, scopeStartLine, scopeStartColumn, scopeEndLine, scopeEndColumn));
    }

    private static int GetLineNumberFromOffset(string text, int offset, int startLineNumber)
    {
        var lineNumber = startLineNumber;
        var limit = Math.Min(offset, text.Length);
        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
                lineNumber++;
        }

        return lineNumber;
    }

    private static int FindInnermostCSharpBlockEndLine(
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int bodyEndIndex,
        int declarationLineIndex,
        int declarationColumn)
    {
        var depth = 0;
        for (var lineIndex = bodyStartIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var limit = lineIndex == declarationLineIndex ? Math.Min(declarationColumn, line.Length) : line.Length;
            for (var column = 0; column < limit; column++)
            {
                if (line[column] == '{')
                    depth++;
                else if (line[column] == '}' && depth > 0)
                    depth--;
            }

            if (lineIndex != declarationLineIndex)
                continue;

            var declarationDepth = depth;
            for (var scanLine = declarationLineIndex; scanLine <= bodyEndIndex; scanLine++)
            {
                var scan = structuralLines[scanLine];
                var scanStart = scanLine == declarationLineIndex ? declarationColumn : 0;
                for (var column = scanStart; column < scan.Length; column++)
                {
                    if (scan[column] == '{')
                        depth++;
                    else if (scan[column] == '}' && depth > 0)
                    {
                        depth--;
                        if (depth < declarationDepth)
                            return scanLine + 1;
                    }
                }
            }

            break;
        }

        return bodyEndIndex + 1;
    }

    private static CSharpLineColumn FindFollowingCSharpEmbeddedStatementEndPosition(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int headerLineIndex,
        int searchStartColumn)
    {
        var parenDepth = 0;
        var foundHeaderOpenParen = false;
        for (var lineIndex = headerLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var startColumn = lineIndex == headerLineIndex ? Math.Min(searchStartColumn, line.Length) : 0;
            for (var column = startColumn; column < line.Length; column++)
            {
                if (line[column] == '(')
                {
                    parenDepth++;
                    foundHeaderOpenParen = true;
                }
                else if (line[column] == ')' && foundHeaderOpenParen && parenDepth > 0)
                {
                    parenDepth--;
                    if (parenDepth == 0)
                        return FindCSharpStatementEndPosition(structuralLines, bodyEndIndex, lineIndex, column + 1);
                }
            }
        }

        return new CSharpLineColumn(bodyEndIndex + 1, 0);
    }

    private static bool TryFindCSharpDeclarationPatternScopeEndPosition(
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int bodyEndIndex,
        int lineIndex,
        int declarationColumn,
        out CSharpLineColumn scopeEnd)
    {
        scopeEnd = new CSharpLineColumn(0, 0);
        if (lineIndex < 0
            || lineIndex >= structuralLines.Count
            || bodyStartIndex < 0
            || bodyEndIndex < bodyStartIndex)
            return false;

        var bodyText = string.Join("\n", structuralLines.Skip(bodyStartIndex).Take(bodyEndIndex - bodyStartIndex + 1));
        if (string.IsNullOrEmpty(bodyText))
            return false;

        var targetOffset = GetBodyTextOffset(structuralLines, bodyStartIndex, bodyEndIndex, lineIndex, declarationColumn);
        var startLineNumber = bodyStartIndex + 1;
        if (TryFindCSharpConditionalExpressionScopeEndPosition(bodyText, startLineNumber, targetOffset, out scopeEnd))
            return true;

        if (TryFindEnclosingCSharpLambdaScopeEndPosition(
                bodyText,
                startLineNumber,
                bodyEndIndex + 1,
                targetOffset,
                out scopeEnd))
        {
            return true;
        }

        if (!TryFindCSharpConditionalHeaderStartPosition(structuralLines, bodyStartIndex, lineIndex, declarationColumn, out var headerLineIndex, out var headerStartColumn))
            return false;

        scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, bodyEndIndex, headerLineIndex, headerStartColumn);
        return true;
    }

    private static bool TryFindCSharpSwitchCaseScopeEndPosition(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int lineIndex,
        int declarationColumn,
        out CSharpLineColumn scopeEnd)
    {
        scopeEnd = new CSharpLineColumn(0, 0);
        if (lineIndex < 0 || lineIndex >= structuralLines.Count)
            return false;

        var labelLineIndex = -1;
        var labelColumn = -1;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var scanLine = lineIndex; scanLine <= bodyEndIndex; scanLine++)
        {
            var line = structuralLines[scanLine];
            var startColumn = scanLine == lineIndex ? Math.Min(Math.Max(declarationColumn, 0), line.Length) : 0;
            for (var column = startColumn; column < line.Length; column++)
            {
                var current = line[column];
                switch (current)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        if (parenDepth > 0)
                            parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (bracketDepth > 0)
                            bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        break;
                    case ':':
                        if (parenDepth == 0
                            && bracketDepth == 0
                            && braceDepth == 0
                            && (column == 0 || line[column - 1] != ':')
                            && (column + 1 >= line.Length || line[column + 1] != ':'))
                        {
                            labelLineIndex = scanLine;
                            labelColumn = column;
                            break;
                        }

                        break;
                }

                if (labelLineIndex >= 0)
                    break;
            }

            if (labelLineIndex >= 0)
                break;
        }

        if (labelLineIndex < 0)
            return false;

        braceDepth = 0;
        for (var scanLine = labelLineIndex; scanLine <= bodyEndIndex; scanLine++)
        {
            var scan = structuralLines[scanLine];
            if (scanLine > labelLineIndex && braceDepth == 0 && IsCSharpSwitchLabelLine(scan))
            {
                scopeEnd = new CSharpLineColumn(scanLine + 1, 0);
                return true;
            }

            var startColumn = scanLine == labelLineIndex ? Math.Min(labelColumn + 1, scan.Length) : 0;
            for (var column = startColumn; column < scan.Length; column++)
            {
                var current = scan[column];
                if (current == '{')
                {
                    braceDepth++;
                }
                else if (current == '}')
                {
                    if (braceDepth == 0)
                    {
                        scopeEnd = new CSharpLineColumn(scanLine + 1, column);
                        return true;
                    }

                    braceDepth--;
                }
            }
        }

        scopeEnd = new CSharpLineColumn(bodyEndIndex + 1, structuralLines[Math.Min(bodyEndIndex, structuralLines.Count - 1)].Length);
        return true;
    }

    private static bool TryFindEnclosingCSharpLambdaScopeEndPosition(
        string bodyText,
        int startLineNumber,
        int fallbackScopeEndLine,
        int targetOffset,
        out CSharpLineColumn scopeEnd)
    {
        scopeEnd = new CSharpLineColumn(0, 0);
        if (string.IsNullOrEmpty(bodyText))
            return false;

        var foundEnclosingLambda = false;
        for (var searchIndex = 0; searchIndex < bodyText.Length;)
        {
            var arrowIndex = bodyText.IndexOf("=>", searchIndex, StringComparison.Ordinal);
            if (arrowIndex < 0 || arrowIndex >= targetOffset)
                break;

            searchIndex = arrowIndex + 2;
            if (!IsPotentialCSharpLambdaArrow(bodyText, arrowIndex))
                continue;

            var lambdaScopeEnd = FindCSharpArrowExpressionScopeEndPosition(bodyText, arrowIndex, startLineNumber, fallbackScopeEndLine);
            var lambdaScopeEndOffset = GetTextOffsetFromLineColumn(bodyText, startLineNumber, lambdaScopeEnd);
            if (targetOffset > lambdaScopeEndOffset)
                continue;

            scopeEnd = lambdaScopeEnd;
            foundEnclosingLambda = true;
        }

        return foundEnclosingLambda;
    }

    private static bool TryFindCSharpConditionalExpressionScopeEndPosition(
        string bodyText,
        int startLineNumber,
        int targetOffset,
        out CSharpLineColumn scopeEnd)
    {
        scopeEnd = new CSharpLineColumn(0, 0);
        if (string.IsNullOrEmpty(bodyText))
            return false;

        GetCSharpDelimiterDepthsAtOffset(bodyText, targetOffset, out var baseParenDepth, out var baseBracketDepth, out var baseBraceDepth);
        var parenDepth = baseParenDepth;
        var bracketDepth = baseBracketDepth;
        var braceDepth = baseBraceDepth;
        var questionIndex = -1;
        var nestedConditionalDepth = 0;
        for (var i = Math.Min(targetOffset, bodyText.Length); i < bodyText.Length; i++)
        {
            var current = bodyText[i];
            switch (current)
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
            }

            var atBaseDepth = parenDepth == baseParenDepth
                && bracketDepth == baseBracketDepth
                && braceDepth == baseBraceDepth;
            if (!atBaseDepth)
                continue;

            if (questionIndex < 0)
            {
                if (IsCSharpConditionalOperatorQuestionMark(bodyText, i))
                {
                    questionIndex = i;
                    continue;
                }

                if (current is ';' or ',' or ')')
                    return false;

                continue;
            }

            if (IsCSharpConditionalOperatorQuestionMark(bodyText, i))
            {
                nestedConditionalDepth++;
                continue;
            }

            if (current != ':')
                continue;

            if (nestedConditionalDepth == 0)
            {
                scopeEnd = GetLineColumnFromOffset(bodyText, i, startLineNumber);
                return true;
            }

            nestedConditionalDepth--;
        }

        return false;
    }

    private static bool TryFindCSharpConditionalHeaderStartPosition(
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int lineIndex,
        int declarationColumn,
        out int headerLineIndex,
        out int headerStartColumn)
    {
        headerLineIndex = -1;
        headerStartColumn = -1;
        if (lineIndex < bodyStartIndex || lineIndex >= structuralLines.Count)
            return false;

        for (var scanLine = lineIndex; scanLine >= bodyStartIndex; scanLine--)
        {
            var searchColumn = scanLine == lineIndex
                ? declarationColumn
                : structuralLines[scanLine].Length - 1;
            if (!TryFindCSharpConditionalHeaderStartColumn(structuralLines[scanLine], searchColumn, out var column))
                continue;

            headerLineIndex = scanLine;
            headerStartColumn = column;
            return true;
        }

        return false;
    }

    private static bool TryFindCSharpConditionalHeaderStartColumn(string line, int searchLimitColumn, out int headerStartColumn)
    {
        headerStartColumn = -1;
        if (string.IsNullOrEmpty(line))
            return false;

        var limit = Math.Min(searchLimitColumn, line.Length - 1);
        for (var column = limit; column >= 0; column--)
        {
            if (!TryConsumeCSharpKeyword(line, column, "if", out var afterKeyword)
                && !TryConsumeCSharpKeyword(line, column, "while", out afterKeyword))
            {
                continue;
            }

            var openParenColumn = line.IndexOf('(', afterKeyword);
            if (openParenColumn >= 0 && openParenColumn <= limit)
            {
                headerStartColumn = column;
                return true;
            }
        }

        return false;
    }

    private static bool IsCSharpSwitchLabelLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.TrimStart();
        return trimmed.StartsWith("case ", StringComparison.Ordinal)
            || string.Equals(trimmed, "default:", StringComparison.Ordinal)
            || trimmed.StartsWith("default:", StringComparison.Ordinal);
    }

    private static int SkipWhitespaceForward(string text, int index)
    {
        var current = Math.Max(index, 0);
        while (current < text.Length && char.IsWhiteSpace(text[current]))
            current++;

        return current;
    }

    private static int GetBodyTextOffset(
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int bodyEndIndex,
        int lineIndex,
        int column)
    {
        if (bodyEndIndex < bodyStartIndex)
            return 0;

        var clampedLineIndex = Math.Max(bodyStartIndex, Math.Min(lineIndex, bodyEndIndex));
        var offset = 0;
        for (var scanLine = bodyStartIndex; scanLine < clampedLineIndex; scanLine++)
            offset += structuralLines[scanLine].Length + 1;

        var line = structuralLines[clampedLineIndex];
        return offset + Math.Max(0, Math.Min(column, line.Length));
    }

    private static CSharpLineColumn FindCSharpStatementEndPosition(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn)
    {
        if (TrySkipCSharpWhitespace(structuralLines, bodyEndIndex, startLineIndex, startColumn, out var statementLineIndex, out var statementColumn)
            && TryConsumeCSharpKeyword(structuralLines[statementLineIndex], statementColumn, "if", out var afterIfColumn))
        {
            if (TrySkipCSharpWhitespace(structuralLines, bodyEndIndex, statementLineIndex, afterIfColumn, out var openParenLineIndex, out var openParenColumn)
                && openParenColumn < structuralLines[openParenLineIndex].Length
                && structuralLines[openParenLineIndex][openParenColumn] == '('
                && TryFindMatchingCSharpDelimiter(structuralLines, bodyEndIndex, openParenLineIndex, openParenColumn, '(', ')', out var closeParen))
            {
                var thenEnd = FindCSharpStatementEndPosition(structuralLines, bodyEndIndex, closeParen.Line, closeParen.Column + 1);
                if (TrySkipCSharpWhitespace(structuralLines, bodyEndIndex, thenEnd.Line - 1, thenEnd.Column + 1, out var elseLineIndex, out var elseColumn)
                    && TryConsumeCSharpKeyword(structuralLines[elseLineIndex], elseColumn, "else", out var afterElseColumn))
                {
                    return FindCSharpStatementEndPosition(structuralLines, bodyEndIndex, elseLineIndex, afterElseColumn);
                }

                return thenEnd;
            }
        }

        var foundContent = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? Math.Min(startColumn, line.Length) : 0;
            for (var column = columnStart; column < line.Length; column++)
            {
                var current = line[column];
                if (!foundContent)
                {
                    if (char.IsWhiteSpace(current))
                        continue;

                    foundContent = true;
                }

                switch (current)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (parenDepth > 0)
                            parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (bracketDepth > 0)
                            bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                        {
                            braceDepth--;
                            if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                                return new CSharpLineColumn(lineIndex + 1, column);
                        }
                        break;
                    case ';':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        break;
                    case ',':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        break;
                }
            }
        }

        return new CSharpLineColumn(bodyEndIndex + 1, 0);
    }

    private static bool TrySkipCSharpWhitespace(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn,
        out int nextLineIndex,
        out int nextColumn)
    {
        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? Math.Min(startColumn, line.Length) : 0;
            for (var column = columnStart; column < line.Length; column++)
            {
                if (!char.IsWhiteSpace(line[column]))
                {
                    nextLineIndex = lineIndex;
                    nextColumn = column;
                    return true;
                }
            }
        }

        nextLineIndex = bodyEndIndex;
        nextColumn = structuralLines[Math.Min(bodyEndIndex, structuralLines.Count - 1)].Length;
        return false;
    }

    private static bool TryConsumeCSharpKeyword(string line, int startColumn, string keyword, out int nextColumn)
    {
        nextColumn = startColumn;
        if (startColumn < 0 || startColumn + keyword.Length > line.Length)
            return false;
        if (!line.AsSpan(startColumn, keyword.Length).Equals(keyword, StringComparison.Ordinal))
            return false;
        if (startColumn > 0 && IsCSharpIdentifierPart(line[startColumn - 1]))
            return false;
        if (startColumn + keyword.Length < line.Length && IsCSharpIdentifierPart(line[startColumn + keyword.Length]))
            return false;

        nextColumn = startColumn + keyword.Length;
        return true;
    }

    private static bool TryConsumeCSharpQueryClauseKeyword(string line, int startColumn, out string keyword, out int nextColumn)
    {
        keyword = string.Empty;
        nextColumn = startColumn;
        if (startColumn < 0 || startColumn >= line.Length)
            return false;

        if (startColumn > 0)
        {
            if (!char.IsWhiteSpace(line[startColumn - 1]))
                return false;

            for (var probe = startColumn - 1; probe >= 0; probe--)
            {
                if (char.IsWhiteSpace(line[probe]))
                    continue;

                if (line[probe] == '.' || line[probe] == ':')
                    return false;

                break;
            }
        }

        var tokenStart = startColumn;
        if (line[tokenStart] == '@')
            return false;

        if (!IsCSharpIdentifierPart(line[tokenStart]))
        {
            return false;
        }

        var tokenEnd = tokenStart + 1;
        while (tokenEnd < line.Length && IsCSharpIdentifierPart(line[tokenEnd]))
            tokenEnd++;

        keyword = line.Substring(tokenStart, tokenEnd - tokenStart);
        nextColumn = tokenEnd;
        return true;
    }

    private static bool IsCSharpTerminalQueryClauseKeyword(string keyword)
    {
        return string.Equals(keyword, "select", StringComparison.Ordinal)
            || string.Equals(keyword, "group", StringComparison.Ordinal);
    }

    private static bool IsCSharpQueryClauseKeyword(string keyword)
    {
        return IsCSharpTerminalQueryClauseKeyword(keyword)
            || string.Equals(keyword, "from", StringComparison.Ordinal)
            || string.Equals(keyword, "let", StringComparison.Ordinal)
            || string.Equals(keyword, "where", StringComparison.Ordinal)
            || string.Equals(keyword, "orderby", StringComparison.Ordinal)
            || string.Equals(keyword, "join", StringComparison.Ordinal)
            || string.Equals(keyword, "on", StringComparison.Ordinal)
            || string.Equals(keyword, "equals", StringComparison.Ordinal)
            || string.Equals(keyword, "by", StringComparison.Ordinal)
            || string.Equals(keyword, "into", StringComparison.Ordinal)
            || string.Equals(keyword, "ascending", StringComparison.Ordinal)
            || string.Equals(keyword, "descending", StringComparison.Ordinal);
    }

    private static bool IsCSharpQueryClauseKeywordSuffix(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int lineIndex,
        string line,
        int nextColumn,
        string keyword,
        int previousTopLevelSignificantLineIndex,
        int previousTopLevelSignificantColumn,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        if (IsCSharpParenthesizedQueryClauseKeyword(keyword)
            && TryGetNextTopLevelSignificantChar(
                structuralLines,
                lineIndex,
                nextColumn,
                out _,
                out _,
                out var nextTopLevelSignificantChar)
            && nextTopLevelSignificantChar == '(')
        {
            return CanStartCSharpParenthesizedQueryClause(
                structuralLines,
                bodyEndIndex,
                previousTopLevelSignificantLineIndex,
                previousTopLevelSignificantColumn,
                csharpKnownTypeNames,
                csharpUsingAliases,
                csharpFunctionValueReceiverNames);
        }

        if (nextColumn >= line.Length)
            return true;

        var next = line[nextColumn];
        if (char.IsWhiteSpace(next))
            return true;

        return (string.Equals(keyword, "ascending", StringComparison.Ordinal)
                || string.Equals(keyword, "descending", StringComparison.Ordinal))
            && (next == ',' || next == ')' || next == ']' || next == '}' || next == ';');
    }

    private static bool IsCSharpParenthesizedQueryClauseKeyword(string keyword)
    {
        return string.Equals(keyword, "select", StringComparison.Ordinal)
            || string.Equals(keyword, "group", StringComparison.Ordinal)
            || string.Equals(keyword, "orderby", StringComparison.Ordinal);
    }

    private static bool CanStartCSharpParenthesizedQueryClause(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int previousTopLevelSignificantLineIndex,
        int previousTopLevelSignificantColumn,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        if (previousTopLevelSignificantLineIndex < 0 || previousTopLevelSignificantColumn < 0)
            return true;

        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                previousTopLevelSignificantLineIndex,
                previousTopLevelSignificantColumn,
                out var previousTokenLineIndex,
                out var previousTokenStartColumn,
                out var previousTokenEndColumn,
                out var previousIdentifierToken,
                out var previousPunctuationToken))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(previousIdentifierToken))
            return !IsCSharpParenthesizedQueryClausePrefixIdentifier(
                structuralLines[previousTokenLineIndex],
                previousTokenStartColumn,
                previousIdentifierToken);

        return previousPunctuationToken switch
        {
            '(' or '[' or '{' or ',' or ';' or ':' or '*' or '/' or '%' or '&' or '|' or '^' or '=' or '~' or '<' => false,
            ')' => !LooksLikeCSharpCastCloseParen(
                structuralLines,
                previousTokenLineIndex,
                previousTokenStartColumn,
                csharpKnownTypeNames,
                csharpUsingAliases,
                csharpFunctionValueReceiverNames),
            '?' => LooksLikeCSharpNullableTypeSuffixInCastOrTypeTest(
                structuralLines,
                previousTokenLineIndex,
                previousTokenStartColumn),
            '+' or '-' => CanStartCSharpParenthesizedQueryClauseAfterPlusOrMinus(
                structuralLines,
                bodyEndIndex,
                previousTokenLineIndex,
                previousTokenStartColumn,
                previousTokenEndColumn,
                previousPunctuationToken),
            '!' => CanStartCSharpParenthesizedQueryClauseAfterBang(
                structuralLines,
                bodyEndIndex,
                previousTokenLineIndex,
                previousTokenStartColumn),
            '>' => LooksLikeCSharpQueryGenericTypeArgumentClose(
                structuralLines,
                bodyEndIndex,
                previousTokenLineIndex,
                previousTokenStartColumn),
            _ => true
        };
    }

    private static bool LooksLikeCSharpCastCloseParen(
        IReadOnlyList<string> structuralLines,
        int closeParenLineIndex,
        int closeParenColumn,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        if (!TryFindMatchingCSharpOpenParenBackwards(
                structuralLines,
                closeParenLineIndex,
                closeParenColumn,
                out var openParenLineIndex,
                out var openParenColumn))
        {
            return false;
        }

        var castTargetText = GetCSharpTextBetween(
            structuralLines,
            openParenLineIndex,
            openParenColumn + 1,
            closeParenLineIndex,
            closeParenColumn);
        if (!LooksLikeCSharpCastTypeText(
                castTargetText,
                closeParenLineIndex + 1,
                closeParenColumn,
                csharpKnownTypeNames,
                csharpUsingAliases,
                csharpFunctionValueReceiverNames))
            return false;

        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                openParenLineIndex,
                openParenColumn - 1,
                out var previousTokenLineIndex,
                out var previousTokenStartColumn,
                out _,
                out var previousIdentifierToken,
                out var previousPunctuationToken))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(previousIdentifierToken))
            return IsCSharpCastPrefixIdentifier(structuralLines[previousTokenLineIndex], previousTokenStartColumn, previousIdentifierToken);

        return previousPunctuationToken is not (')' or ']' or '}' or '"' or '\'' or '>');
    }

    private static bool IsCSharpCastPrefixIdentifier(string line, int tokenStartColumn, string token)
    {
        if (tokenStartColumn > 0 && line[tokenStartColumn - 1] == '@')
            return false;

        return string.Equals(token, "return", StringComparison.Ordinal)
            || string.Equals(token, "await", StringComparison.Ordinal)
            || string.Equals(token, "throw", StringComparison.Ordinal)
            || IsCSharpQueryClauseKeyword(token);
    }

    private static bool LooksLikeCSharpCastTypeText(
        string text,
        int lineNumber,
        int column,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return false;

        var index = 0;
        if (!TryConsumeCSharpCastType(trimmed, ref index))
            return false;

        SkipCSharpCastTypeWhitespace(trimmed, ref index);
        if (index != trimmed.Length)
            return false;

        var shape = AnalyzeCSharpCastTypeShape(trimmed);
        if (shape.IdentifierSegments.Count == 0)
            return shape.HasTypeOnlySyntax;

        var resolvedQualifiedName = shape.SimpleQualifiedName == null
            ? null
            : ResolveCSharpQualifiedAliasTarget(shape.SimpleQualifiedName, lineNumber, csharpUsingAliases);
        var resolvedBareName = resolvedQualifiedName == null
            ? null
            : ExtractBareTypeName(resolvedQualifiedName);

        var lastSegment = shape.IdentifierSegments[^1];
        if (HasKnownNonTerminalTypeSegment(shape.IdentifierSegments, csharpKnownTypeNames)
            && !IsKnownCSharpCastTypeName(lastSegment, resolvedBareName, csharpKnownTypeNames))
        {
            return false;
        }

        if (IsKnownCSharpCastTypeName(lastSegment, resolvedBareName, csharpKnownTypeNames)
            || (!string.IsNullOrWhiteSpace(resolvedQualifiedName) && csharpKnownTypeNames.Contains(resolvedQualifiedName)))
        {
            return true;
        }

        if (shape.SimpleQualifiedName != null
            && string.Equals(shape.SimpleQualifiedName, resolvedQualifiedName, StringComparison.Ordinal)
            && HasCSharpFunctionValueReceiverConflict(
                GetFirstQualifiedSegment(shape.SimpleQualifiedName),
                lineNumber,
                column,
                csharpFunctionValueReceiverNames))
        {
            return false;
        }

        if (shape.HasTypeOnlySyntax)
            return true;

        return shape.AllIdentifiersTypeLike && shape.IdentifierSegments.Count <= 2;
    }

    private static bool TryConsumeCSharpCastType(string text, ref int index)
    {
        if (!TryConsumeCSharpCastTypeCore(text, ref index))
            return false;

        while (true)
        {
            var checkpoint = index;
            SkipCSharpCastTypeWhitespace(text, ref index);
            if (TryConsumeCSharpCastArraySuffix(text, ref index)
                || TryConsumeCSharpCastNullableSuffix(text, ref index))
            {
                continue;
            }

            index = checkpoint;
            return true;
        }
    }

    private static bool TryConsumeCSharpCastTypeCore(string text, ref int index)
    {
        SkipCSharpCastTypeWhitespace(text, ref index);
        if (index < text.Length && text[index] == '(')
            return TryConsumeCSharpCastTupleType(text, ref index);

        return TryConsumeCSharpCastQualifiedType(text, ref index);
    }

    private static bool TryConsumeCSharpCastQualifiedType(string text, ref int index)
    {
        if (!TryConsumeCSharpCastIdentifier(text, ref index, out var token))
            return false;

        if (!TryConsumeCSharpCastGenericArgumentList(text, ref index))
            return false;

        while (true)
        {
            var checkpoint = index;
            SkipCSharpCastTypeWhitespace(text, ref index);
            if (!TryConsumeCSharpCastQualifiedTypeSeparator(text, ref index))
            {
                index = checkpoint;
                return true;
            }

            if (!TryConsumeCSharpCastIdentifier(text, ref index, out token))
                return false;

            if (!TryConsumeCSharpCastGenericArgumentList(text, ref index))
                return false;
        }
    }

    private static bool TryConsumeCSharpCastTupleType(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '(')
            return false;

        index++;
        while (true)
        {
            if (!TryConsumeCSharpCastType(text, ref index))
                return false;

            var checkpoint = index;
            if (TryConsumeCSharpCastIdentifier(text, ref index, out _))
            {
                // Tuple element names are optional and do not affect type-likeness.
            }
            else
            {
                index = checkpoint;
            }

            SkipCSharpCastTypeWhitespace(text, ref index);
            if (index >= text.Length)
                return false;

            if (text[index] == ')')
            {
                index++;
                return true;
            }

            if (text[index] != ',')
                return false;

            index++;
        }
    }

    private static bool TryConsumeCSharpCastGenericArgumentList(string text, ref int index)
    {
        var checkpoint = index;
        SkipCSharpCastTypeWhitespace(text, ref index);
        if (index >= text.Length || text[index] != '<')
        {
            index = checkpoint;
            return true;
        }

        index++;
        while (true)
        {
            if (!TryConsumeCSharpCastType(text, ref index))
                return false;

            SkipCSharpCastTypeWhitespace(text, ref index);
            if (index >= text.Length)
                return false;

            if (text[index] == '>')
            {
                index++;
                return true;
            }

            if (text[index] != ',')
                return false;

            index++;
        }
    }

    private static bool TryConsumeCSharpCastArraySuffix(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '[')
            return false;

        index++;
        SkipCSharpCastTypeWhitespace(text, ref index);
        while (index < text.Length && text[index] == ',')
        {
            index++;
            SkipCSharpCastTypeWhitespace(text, ref index);
        }

        if (index >= text.Length || text[index] != ']')
            return false;

        index++;
        return true;
    }

    private static bool TryConsumeCSharpCastNullableSuffix(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '?')
            return false;

        index++;
        return true;
    }

    private static bool TryConsumeCSharpCastQualifiedTypeSeparator(string text, ref int index)
    {
        if (index >= text.Length)
            return false;

        if (text[index] == '.')
        {
            index++;
            return true;
        }

        if (index + 1 < text.Length && text[index] == ':' && text[index + 1] == ':')
        {
            index += 2;
            return true;
        }

        return false;
    }

    private static bool TryConsumeCSharpCastIdentifier(string text, ref int index, out string token)
    {
        SkipCSharpCastTypeWhitespace(text, ref index);
        token = string.Empty;
        if (index >= text.Length)
            return false;

        var start = index;
        if (text[index] == '@')
        {
            index++;
            if (index >= text.Length || !IsCSharpIdentifierStart(text[index]))
            {
                index = start;
                return false;
            }
        }
        else if (!IsCSharpIdentifierStart(text[index]))
        {
            return false;
        }

        index++;
        while (index < text.Length && IsCSharpIdentifierPart(text[index]))
            index++;

        token = text.Substring(start, index - start);
        return true;
    }

    private static CSharpCastTypeShape AnalyzeCSharpCastTypeShape(string text)
    {
        var segments = new List<string>();
        var simpleQualifiedName = new System.Text.StringBuilder();
        var hasTypeOnlySyntax = false;
        var allIdentifiersTypeLike = true;
        var simpleQualifiedCandidate = true;

        for (var index = 0; index < text.Length;)
        {
            var current = text[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '@' || IsCSharpIdentifierStart(current))
            {
                var start = index;
                if (current == '@')
                    index++;
                if (index < text.Length)
                    index++;
                while (index < text.Length && IsCSharpIdentifierPart(text[index]))
                    index++;

                var token = text.Substring(start, index - start);
                segments.Add(token);
                allIdentifiersTypeLike &= IsLikelyCSharpTypeIdentifier(token);
                if (simpleQualifiedCandidate)
                    simpleQualifiedName.Append(token);
                continue;
            }

            switch (current)
            {
                case '.':
                    if (simpleQualifiedCandidate)
                        simpleQualifiedName.Append(current);
                    index++;
                    continue;
                case ':':
                    if (index + 1 < text.Length && text[index + 1] == ':')
                    {
                        hasTypeOnlySyntax = true;
                        if (simpleQualifiedCandidate)
                            simpleQualifiedName.Append("::");
                        index += 2;
                        continue;
                    }

                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
                case '<':
                case '[':
                case '?':
                case '(':
                    hasTypeOnlySyntax = true;
                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
                case '>':
                case ']':
                case ')':
                case ',':
                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
                default:
                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
            }
        }

        return new CSharpCastTypeShape(
            segments,
            simpleQualifiedCandidate && simpleQualifiedName.Length > 0 ? simpleQualifiedName.ToString() : null,
            hasTypeOnlySyntax,
            allIdentifiersTypeLike);
    }

    private static bool HasKnownNonTerminalTypeSegment(IReadOnlyList<string> segments, IReadOnlySet<string> csharpKnownTypeNames)
    {
        for (var index = 0; index < segments.Count - 1; index++)
        {
            if (csharpKnownTypeNames.Contains(NormalizeCSharpIdentifier(segments[index])))
                return true;
        }

        return false;
    }

    private static bool IsKnownCSharpCastTypeName(string candidate, string? resolvedCandidate, IReadOnlySet<string> csharpKnownTypeNames)
    {
        return csharpKnownTypeNames.Contains(NormalizeCSharpIdentifier(candidate))
            || (!string.IsNullOrWhiteSpace(resolvedCandidate) && csharpKnownTypeNames.Contains(NormalizeCSharpIdentifier(resolvedCandidate)));
    }

    private static bool HasCSharpFunctionValueReceiverConflict(
        string candidate,
        int lineNumber,
        int column,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        if (string.IsNullOrWhiteSpace(candidate) || csharpFunctionValueReceiverNames.Count == 0)
            return false;

        var normalizedCandidate = NormalizeCSharpIdentifier(candidate);
        return csharpFunctionValueReceiverNames.Any(record =>
            IsWithinCSharpScope(record, lineNumber, column)
            && string.Equals(record.Name, normalizedCandidate, StringComparison.Ordinal));
    }

    private static bool IsLikelyCSharpTypeIdentifier(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        var normalized = token[0] == '@' ? token.Substring(1) : token;
        if (normalized.Length == 0)
            return false;

        return IsCSharpBuiltInTypeKeyword(normalized)
            || char.IsUpper(normalized[0]);
    }

    private static void SkipCSharpCastTypeWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }

    private static bool IsCSharpBuiltInTypeKeyword(string text)
    {
        return string.Equals(text, "bool", StringComparison.Ordinal)
            || string.Equals(text, "byte", StringComparison.Ordinal)
            || string.Equals(text, "sbyte", StringComparison.Ordinal)
            || string.Equals(text, "short", StringComparison.Ordinal)
            || string.Equals(text, "ushort", StringComparison.Ordinal)
            || string.Equals(text, "int", StringComparison.Ordinal)
            || string.Equals(text, "uint", StringComparison.Ordinal)
            || string.Equals(text, "long", StringComparison.Ordinal)
            || string.Equals(text, "ulong", StringComparison.Ordinal)
            || string.Equals(text, "nint", StringComparison.Ordinal)
            || string.Equals(text, "nuint", StringComparison.Ordinal)
            || string.Equals(text, "char", StringComparison.Ordinal)
            || string.Equals(text, "float", StringComparison.Ordinal)
            || string.Equals(text, "double", StringComparison.Ordinal)
            || string.Equals(text, "decimal", StringComparison.Ordinal)
            || string.Equals(text, "string", StringComparison.Ordinal)
            || string.Equals(text, "object", StringComparison.Ordinal)
            || string.Equals(text, "dynamic", StringComparison.Ordinal);
    }

    private static bool CanStartCSharpParenthesizedQueryClauseAfterPlusOrMinus(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int operatorLineIndex,
        int operatorColumn,
        int operatorEndColumn,
        char operatorToken)
    {
        if (operatorLineIndex < 0 || operatorColumn < 0)
            return false;

        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                operatorLineIndex,
                operatorColumn - 1,
                out var previousTokenLineIndex,
                out var previousTokenStartColumn,
                out var previousTokenEndColumn,
                out var previousIdentifierToken,
                out var previousPunctuationToken))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(previousIdentifierToken)
            || previousPunctuationToken != operatorToken
            || previousTokenLineIndex != operatorLineIndex
            || previousTokenEndColumn != operatorEndColumn - 1)
        {
            return false;
        }

        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                previousTokenLineIndex,
                previousTokenStartColumn - 1,
                out var operandTokenLineIndex,
                out var operandTokenStartColumn,
                out _,
                out var operandIdentifierToken,
                out var operandPunctuationToken))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(operandIdentifierToken))
            return true;

        return operandPunctuationToken switch
        {
            ')' or ']' or '}' or '"' or '\'' => true,
            '>' => LooksLikeCSharpQueryGenericTypeArgumentClose(
                structuralLines,
                bodyEndIndex,
                operandTokenLineIndex,
                operandTokenStartColumn),
            _ => false
        };
    }

    private static bool CanStartCSharpParenthesizedQueryClauseAfterBang(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int bangLineIndex,
        int bangColumn)
    {
        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                bangLineIndex,
                bangColumn - 1,
                out var previousTokenLineIndex,
                out var previousTokenStartColumn,
                out _,
                out var previousIdentifierToken,
                out var previousPunctuationToken))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(previousIdentifierToken))
            return !IsCSharpParenthesizedQueryClausePrefixIdentifier(
                structuralLines[previousTokenLineIndex],
                previousTokenStartColumn,
                previousIdentifierToken);

        return previousPunctuationToken switch
        {
            ')' or ']' or '}' or '"' or '\'' => true,
            '>' => LooksLikeCSharpQueryGenericTypeArgumentClose(
                structuralLines,
                bodyEndIndex,
                previousTokenLineIndex,
                previousTokenStartColumn),
            _ => false
        };
    }

    private static bool IsCSharpParenthesizedQueryClausePrefixIdentifier(string line, int tokenStartColumn, string token)
    {
        if (tokenStartColumn > 0 && line[tokenStartColumn - 1] == '@')
            return false;

        return string.Equals(token, "await", StringComparison.Ordinal)
            || string.Equals(token, "throw", StringComparison.Ordinal)
            || IsCSharpQueryClauseKeyword(token);
    }

    private static bool LooksLikeCSharpNullableTypeSuffixInCastOrTypeTest(
        IReadOnlyList<string> structuralLines,
        int questionLineIndex,
        int questionColumn)
    {
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenDepth = 0;
        var currentLineIndex = questionLineIndex;
        var currentColumn = questionColumn - 1;
        while (TryGetPreviousTopLevelToken(
                   structuralLines,
                   currentLineIndex,
                   currentColumn,
                   out var tokenLineIndex,
                   out var tokenStartColumn,
                   out _,
                   out var identifierToken,
                   out var punctuationToken))
        {
            if (!string.IsNullOrEmpty(identifierToken))
            {
                if (angleDepth == 0
                    && bracketDepth == 0
                    && parenDepth == 0
                    && (string.Equals(identifierToken, "as", StringComparison.Ordinal)
                        || string.Equals(identifierToken, "is", StringComparison.Ordinal)))
                {
                    return true;
                }

                currentLineIndex = tokenLineIndex;
                currentColumn = tokenStartColumn - 1;
                continue;
            }

            switch (punctuationToken)
            {
                case '.':
                case '?':
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case ',':
                    if (angleDepth > 0 || bracketDepth > 0 || parenDepth > 0)
                    {
                        currentLineIndex = tokenLineIndex;
                        currentColumn = tokenStartColumn - 1;
                        continue;
                    }

                    return false;
                case '>':
                    angleDepth++;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case '<':
                    if (angleDepth == 0)
                        return false;

                    angleDepth--;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case ']':
                    bracketDepth++;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case '[':
                    if (bracketDepth == 0)
                        return false;

                    bracketDepth--;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case ')':
                    parenDepth++;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case '(':
                    if (parenDepth == 0)
                        return false;

                    parenDepth--;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool TryGetPreviousTopLevelToken(
        IReadOnlyList<string> structuralLines,
        int startLineIndex,
        int startColumn,
        out int tokenLineIndex,
        out int tokenStartColumn,
        out int tokenEndColumn,
        out string identifierToken,
        out char punctuationToken)
    {
        tokenLineIndex = -1;
        tokenStartColumn = -1;
        tokenEndColumn = -1;
        identifierToken = string.Empty;
        punctuationToken = '\0';

        if (!TryGetPreviousTopLevelSignificantChar(
                structuralLines,
                startLineIndex,
                startColumn,
                out tokenLineIndex,
                out tokenEndColumn,
                out var tokenChar))
        {
            return false;
        }

        tokenStartColumn = tokenEndColumn;
        if (IsCSharpIdentifierPart(tokenChar))
        {
            var line = structuralLines[tokenLineIndex];
            while (tokenStartColumn > 0 && IsCSharpIdentifierPart(line[tokenStartColumn - 1]))
                tokenStartColumn--;

            identifierToken = line.Substring(tokenStartColumn, tokenEndColumn - tokenStartColumn + 1);
        }
        else
        {
            punctuationToken = tokenChar;
        }

        return true;
    }

    private static bool TryGetPreviousTopLevelSignificantChar(
        IReadOnlyList<string> structuralLines,
        int startLineIndex,
        int startColumn,
        out int lineIndex,
        out int column,
        out char value)
    {
        lineIndex = -1;
        column = -1;
        value = '\0';

        if (structuralLines.Count == 0)
            return false;

        var clampedLineIndex = Math.Min(startLineIndex, structuralLines.Count - 1);
        for (var currentLineIndex = clampedLineIndex; currentLineIndex >= 0; currentLineIndex--)
        {
            var line = structuralLines[currentLineIndex];
            var currentColumn = currentLineIndex == clampedLineIndex
                ? Math.Min(startColumn, line.Length - 1)
                : line.Length - 1;
            for (var probe = currentColumn; probe >= 0; probe--)
            {
                if (char.IsWhiteSpace(line[probe]))
                    continue;

                lineIndex = currentLineIndex;
                column = probe;
                value = line[probe];
                return true;
            }
        }

        return false;
    }

    private static bool TryGetNextTopLevelSignificantChar(
        IReadOnlyList<string> structuralLines,
        int startLineIndex,
        int startColumn,
        out int lineIndex,
        out int column,
        out char value)
    {
        lineIndex = -1;
        column = -1;
        value = '\0';

        if (structuralLines.Count == 0)
            return false;

        var clampedLineIndex = Math.Max(0, Math.Min(startLineIndex, structuralLines.Count - 1));
        for (var currentLineIndex = clampedLineIndex; currentLineIndex < structuralLines.Count; currentLineIndex++)
        {
            var line = structuralLines[currentLineIndex];
            var currentColumn = currentLineIndex == clampedLineIndex
                ? Math.Max(0, startColumn)
                : 0;
            for (var probe = currentColumn; probe < line.Length; probe++)
            {
                if (char.IsWhiteSpace(line[probe]))
                    continue;

                lineIndex = currentLineIndex;
                column = probe;
                value = line[probe];
                return true;
            }
        }

        return false;
    }

    private static bool TryFindMatchingCSharpOpenParenBackwards(
        IReadOnlyList<string> structuralLines,
        int closeParenLineIndex,
        int closeParenColumn,
        out int openParenLineIndex,
        out int openParenColumn)
    {
        openParenLineIndex = -1;
        openParenColumn = -1;

        var depth = 1;
        for (var lineIndex = closeParenLineIndex; lineIndex >= 0; lineIndex--)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == closeParenLineIndex ? Math.Min(closeParenColumn - 1, line.Length - 1) : line.Length - 1;
            for (var column = columnStart; column >= 0; column--)
            {
                switch (line[column])
                {
                    case ')':
                        depth++;
                        break;
                    case '(':
                        depth--;
                        if (depth == 0)
                        {
                            openParenLineIndex = lineIndex;
                            openParenColumn = column;
                            return true;
                        }

                        break;
                }
            }
        }

        return false;
    }

    private static string GetCSharpTextBetween(
        IReadOnlyList<string> structuralLines,
        int startLineIndex,
        int startColumn,
        int endLineIndex,
        int endColumn)
    {
        var builder = new System.Text.StringBuilder();
        for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var segmentStart = lineIndex == startLineIndex ? Math.Max(0, startColumn) : 0;
            var segmentEnd = lineIndex == endLineIndex ? Math.Min(endColumn, line.Length) : line.Length;
            if (segmentStart < segmentEnd)
                builder.Append(line, segmentStart, segmentEnd - segmentStart);
            if (lineIndex < endLineIndex)
                builder.Append('\n');
        }

        return builder.ToString();
    }

    private static bool LooksLikeCSharpQueryGenericTypeArgumentClose(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int closeLineIndex,
        int closeColumn)
    {
        if (closeLineIndex < 0 || closeLineIndex >= structuralLines.Count)
            return false;

        var angleDepth = 1;
        for (var lineIndex = closeLineIndex; lineIndex >= 0; lineIndex--)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == closeLineIndex ? Math.Min(closeColumn - 1, line.Length - 1) : line.Length - 1;
            for (var column = columnStart; column >= 0; column--)
            {
                var current = line[column];
                switch (current)
                {
                    case '>':
                        angleDepth++;
                        break;
                    case '<':
                        angleDepth--;
                        if (angleDepth == 0)
                            return LooksLikeCSharpQueryGenericTypeArgumentStart(structuralLines, bodyEndIndex, lineIndex, column);
                        break;
                }
            }
        }

        return false;
    }

    private static bool TryFindMatchingCSharpDelimiter(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn,
        char open,
        char close,
        out CSharpLineColumn match)
    {
        var depth = 0;
        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? Math.Min(startColumn, line.Length) : 0;
            for (var column = columnStart; column < line.Length; column++)
            {
                var current = line[column];
                if (current == open)
                {
                    depth++;
                }
                else if (current == close && depth > 0)
                {
                    depth--;
                    if (depth == 0)
                    {
                        match = new CSharpLineColumn(lineIndex + 1, column);
                        return true;
                    }
                }
            }
        }

        match = new CSharpLineColumn(bodyEndIndex + 1, 0);
        return false;
    }

    private static CSharpLineColumn FindCSharpQueryExpressionEndPosition(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        var foundContent = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;
        var terminalClauseSeen = false;
        var queryClauseSeen = false;
        var clauseHasTopLevelExpressionContent = false;
        var lastTopLevelSignificantLineIndex = -1;
        var lastTopLevelSignificantColumn = -1;

        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? Math.Min(startColumn, line.Length) : 0;
            for (var column = columnStart; column < line.Length; column++)
            {
                var current = line[column];
                if (!foundContent)
                {
                    if (char.IsWhiteSpace(current))
                        continue;

                    foundContent = true;
                }

                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0
                    && TryConsumeCSharpQueryClauseKeyword(line, column, out var keyword, out var nextColumn))
                {
                    if ((!queryClauseSeen || clauseHasTopLevelExpressionContent)
                        && IsCSharpQueryClauseKeyword(keyword)
                        && IsCSharpQueryClauseKeywordSuffix(
                            structuralLines,
                            bodyEndIndex,
                            lineIndex,
                            line,
                            nextColumn,
                            keyword,
                            lastTopLevelSignificantLineIndex,
                            lastTopLevelSignificantColumn,
                            csharpKnownTypeNames,
                            csharpUsingAliases,
                            csharpFunctionValueReceiverNames))
                    {
                        if ((string.Equals(keyword, "by", StringComparison.Ordinal)
                                || string.Equals(keyword, "ascending", StringComparison.Ordinal)
                                || string.Equals(keyword, "descending", StringComparison.Ordinal))
                            && terminalClauseSeen)
                        {
                            terminalClauseSeen = true;
                        }
                        else
                        {
                            terminalClauseSeen = IsCSharpTerminalQueryClauseKeyword(keyword);
                        }

                        queryClauseSeen = true;
                        clauseHasTopLevelExpressionContent = false;
                        lastTopLevelSignificantLineIndex = lineIndex;
                        lastTopLevelSignificantColumn = nextColumn - 1;
                        column = nextColumn - 1;
                        continue;
                    }
                }

                switch (current)
                {
                    case '<':
                        if (parenDepth == 0
                            && bracketDepth == 0
                            && braceDepth == 0
                            && LooksLikeCSharpQueryGenericTypeArgumentStart(structuralLines, bodyEndIndex, lineIndex, column))
                        {
                            angleDepth++;
                        }
                        break;
                    case '>':
                        if (angleDepth > 0)
                        {
                            angleDepth--;
                        }
                        break;
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (parenDepth > 0)
                            parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (bracketDepth > 0)
                            bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        break;
                    case ';':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        break;
                    case ',':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0 && terminalClauseSeen)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                        {
                            clauseHasTopLevelExpressionContent = false;
                            lastTopLevelSignificantLineIndex = lineIndex;
                            lastTopLevelSignificantColumn = column;
                        }
                        break;
                }

                if (!char.IsWhiteSpace(current)
                    && parenDepth == 0
                    && bracketDepth == 0
                    && braceDepth == 0
                    && angleDepth == 0
                    && current != ','
                    && current != ';')
                {
                    clauseHasTopLevelExpressionContent = true;
                    lastTopLevelSignificantLineIndex = lineIndex;
                    lastTopLevelSignificantColumn = column;
                }
            }
        }

        return new CSharpLineColumn(bodyEndIndex + 1, 0);
    }

    private static bool LooksLikeCSharpQueryGenericTypeArgumentStart(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn)
    {
        var line = structuralLines[startLineIndex];
        if (startColumn < 0 || startColumn >= line.Length || line[startColumn] != '<')
            return false;
        if (HasCSharpQueryGenericOperatorOnRight(line, startColumn + 1))
            return false;
        if (!HasCSharpQueryGenericReceiverOnLeft(line, startColumn - 1))
            return false;

        var angleDepth = 1;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var currentLine = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? startColumn + 1 : 0;
            for (var column = columnStart; column < currentLine.Length; column++)
            {
                var current = currentLine[column];
                switch (current)
                {
                    case '<':
                        angleDepth++;
                        break;
                    case '>':
                        angleDepth--;
                        if (angleDepth == 0)
                            return HasCSharpQueryGenericSuffix(structuralLines, bodyEndIndex, lineIndex, column + 1);
                        break;
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        if (parenDepth == 0)
                            return false;
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (bracketDepth == 0)
                            return false;
                        bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth == 0)
                            return false;
                        braceDepth--;
                        break;
                    case ';':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return false;
                        break;
                }
            }
        }

        return false;
    }

    private static bool HasCSharpQueryGenericOperatorOnRight(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length)
            return false;

        return line[index] is '<' or '=';
    }

    private static bool HasCSharpQueryGenericReceiverOnLeft(string line, int index)
    {
        while (index >= 0 && char.IsWhiteSpace(line[index]))
            index--;
        if (index < 0)
            return false;

        var current = line[index];
        return IsCSharpIdentifierPart(current) || current is '>' or ']' or ')';
    }

    private static bool HasCSharpQueryGenericSuffix(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn)
    {
        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? startColumn : 0;
            for (var column = columnStart; column < line.Length; column++)
            {
                var current = line[column];
                if (char.IsWhiteSpace(current))
                    continue;

                if (current is '(' or ')' or ']' or '[' or '.' or ',' or ';' or '{' or ':' or '?'
                    || IsCSharpIdentifierStart(current))
                {
                    return true;
                }

                return IsCSharpQueryGenericComparisonOperator(line, column);
            }
        }

        return true;
    }

    private static bool IsCSharpQueryGenericComparisonOperator(string line, int column)
    {
        if (column < 0 || column + 1 >= line.Length)
            return false;

        var current = line[column];
        return (current is '!' or '=') && line[column + 1] == '=';
    }

    private static CSharpLineColumn FindCSharpArrowExpressionScopeEndPosition(string bodyText, int arrowIndex, int startLineNumber, int fallbackScopeEndLine)
    {
        var foundContent = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var i = Math.Min(arrowIndex + 2, bodyText.Length); i < bodyText.Length; i++)
        {
            var current = bodyText[i];
            if (!foundContent)
            {
                if (char.IsWhiteSpace(current))
                    continue;

                foundContent = true;
            }

            switch (current)
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return GetLineColumnFromOffset(bodyText, i, startLineNumber);
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return GetLineColumnFromOffset(bodyText, i, startLineNumber);
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                        return GetLineColumnFromOffset(bodyText, i + 1, startLineNumber);
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                        if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                            return GetLineColumnFromOffset(bodyText, i + 1, startLineNumber);
                    }
                    break;
                case ',':
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return GetLineColumnFromOffset(bodyText, i, startLineNumber);
                    break;
            }
        }

        return new CSharpLineColumn(fallbackScopeEndLine, int.MaxValue);
    }

    private static bool IsCSharpConditionalOperatorQuestionMark(string bodyText, int index)
    {
        if (index < 0 || index >= bodyText.Length || bodyText[index] != '?')
            return false;

        var previous = index > 0 ? bodyText[index - 1] : '\0';
        var next = index + 1 < bodyText.Length ? bodyText[index + 1] : '\0';
        return previous != '?'
            && next is not '?' and not '.' and not '[';
    }

    private static void GetCSharpDelimiterDepthsAtOffset(
        string bodyText,
        int offset,
        out int parenDepth,
        out int bracketDepth,
        out int braceDepth)
    {
        parenDepth = 0;
        bracketDepth = 0;
        braceDepth = 0;
        var limit = Math.Min(offset, bodyText.Length);
        for (var i = 0; i < limit; i++)
        {
            switch (bodyText[i])
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
            }
        }
    }

    private static int GetTextOffsetFromLineColumn(string bodyText, int startLineNumber, CSharpLineColumn position)
    {
        if (string.IsNullOrEmpty(bodyText))
            return 0;

        if (position.Line <= startLineNumber)
            return Math.Max(0, Math.Min(position.Column, bodyText.Length));

        var currentLineNumber = startLineNumber;
        var lineStartOffset = 0;
        while (lineStartOffset < bodyText.Length && currentLineNumber < position.Line)
        {
            var newlineIndex = bodyText.IndexOf('\n', lineStartOffset);
            if (newlineIndex < 0)
                return bodyText.Length;

            currentLineNumber++;
            lineStartOffset = newlineIndex + 1;
        }

        var lineEndOffset = bodyText.IndexOf('\n', lineStartOffset);
        if (lineEndOffset < 0)
            lineEndOffset = bodyText.Length;

        return Math.Min(lineStartOffset + Math.Max(position.Column, 0), lineEndOffset);
    }

    private static bool IsPotentialCSharpLambdaArrow(string bodyText, int arrowIndex)
    {
        var leftIndex = SkipWhitespaceBackward(bodyText, arrowIndex - 1);
        if (leftIndex < 0)
            return false;

        if (bodyText[leftIndex] == ')')
        {
            if (!TryFindMatchingOpenParen(bodyText, leftIndex, out var openParenIndex))
                return false;

            var parenPrefixIndex = SkipWhitespaceBackward(bodyText, openParenIndex - 1);
            if (parenPrefixIndex < 0)
                return true;

            var parenPrefixChar = bodyText[parenPrefixIndex];
            if (parenPrefixChar is '.' or ']' or ')')
                return false;

            if (IsCSharpIdentifierPart(parenPrefixChar))
            {
                var parenIdentifierStart = parenPrefixIndex;
                while (parenIdentifierStart >= 0 && IsCSharpIdentifierPart(bodyText[parenIdentifierStart]))
                    parenIdentifierStart--;
                parenIdentifierStart++;

                var identifierPrefixIndex = SkipWhitespaceBackward(bodyText, parenIdentifierStart - 1);
                if (identifierPrefixIndex < 0)
                    return true;

                var identifierPrefixChar = bodyText[identifierPrefixIndex];
                if (identifierPrefixChar == '.')
                    return false;

                if (IsCSharpIdentifierPart(identifierPrefixChar))
                {
                    if (!TryReadPreviousIdentifierToken(bodyText, identifierPrefixIndex, out var identifierPreviousToken))
                        return false;

                    var normalizedPreviousToken = NormalizeCSharpIdentifier(identifierPreviousToken);
                    return normalizedPreviousToken is not ("when" or "is" or "as" or "and" or "or" or "not"
                        or "return" or "throw" or "new" or "case" or "else" or "do");
                }

                return identifierPrefixChar is '>' or ']' or ')' or '?' or ':' or '=';
            }

            return parenPrefixChar is '=' or '(' or ',' or ':';
        }

        var identifierEnd = leftIndex + 1;
        var identifierStart = leftIndex;
        while (identifierStart >= 0 && IsCSharpIdentifierPart(bodyText[identifierStart]))
            identifierStart--;
        identifierStart++;
        if (identifierStart >= identifierEnd || !IsCSharpIdentifierStart(bodyText[identifierStart]))
            return false;

        var prefixIndex = SkipWhitespaceBackward(bodyText, identifierStart - 1);
        if (prefixIndex < 0)
            return false;

        var prefixChar = bodyText[prefixIndex];
        return prefixChar is '=' or '(' or ',' or ':'
            || (TryReadPreviousIdentifierToken(bodyText, prefixIndex, out var previousToken)
                && (string.Equals(previousToken, "return", StringComparison.Ordinal)
                    || string.Equals(previousToken, "static", StringComparison.Ordinal)
                    || string.Equals(previousToken, "async", StringComparison.Ordinal)));
    }

    private static int GetLineStartOffset(string text, int offset)
    {
        var lineStart = Math.Min(offset, text.Length);
        while (lineStart > 0 && text[lineStart - 1] != '\n')
            lineStart--;
        return lineStart;
    }

    private static CSharpLineColumn GetLineColumnFromOffset(string text, int offset, int startLineNumber)
    {
        var lineNumber = startLineNumber;
        var column = 0;
        var limit = Math.Min(offset, text.Length);
        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
            {
                lineNumber++;
                column = 0;
            }
            else
            {
                column++;
            }
        }

        return new CSharpLineColumn(lineNumber, column);
    }

    private static int SkipWhitespaceBackward(string text, int index)
    {
        while (index >= 0 && char.IsWhiteSpace(text[index]))
            index--;
        return index;
    }

    private static bool TryFindMatchingOpenParen(string text, int closeParenIndex, out int openParenIndex)
    {
        openParenIndex = -1;
        var depth = 0;
        for (var i = closeParenIndex; i >= 0; i--)
        {
            if (text[i] == ')')
            {
                depth++;
            }
            else if (text[i] == '(')
            {
                depth--;
                if (depth == 0)
                {
                    openParenIndex = i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadPreviousIdentifierToken(string text, int index, out string token)
    {
        token = string.Empty;
        var end = index;
        while (end >= 0 && !IsCSharpIdentifierPart(text[end]))
            end--;
        if (end < 0)
            return false;

        var start = end;
        while (start >= 0 && IsCSharpIdentifierPart(text[start]))
            start--;
        start++;
        if (start > end)
            return false;

        token = text[start..(end + 1)];
        return token.Length > 0;
    }

    private static bool IsStaticCSharpSymbol(SymbolRecord? symbol) =>
        symbol?.Signature != null && CSharpStaticModifierRegex.IsMatch(symbol.Signature);

    private static string GetFirstQualifiedSegment(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return string.Empty;

        var firstDot = qualifiedName.IndexOf('.');
        return firstDot < 0 ? qualifiedName : qualifiedName[..firstDot];
    }

    private static bool MatchesQualifiedConstantContainer(
        string qualifier,
        IReadOnlyList<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)> targets,
        bool allowShortNameFallback = true,
        bool allowSingleSegmentQualifiedMatch = false)
    {
        var hasMultipleQualifierSegments = qualifier.Contains('.') || qualifier.Contains("::", StringComparison.Ordinal);
        foreach (var (containerName, qualifiedContainerName, targetAllowsShortNameFallback) in targets)
        {
            if (!string.IsNullOrWhiteSpace(qualifiedContainerName)
                && ((hasMultipleQualifierSegments && QualifiedNameHasSuffix(qualifiedContainerName!, qualifier))
                    || (!hasMultipleQualifierSegments
                        && allowSingleSegmentQualifiedMatch
                        && string.Equals(qualifiedContainerName, qualifier, StringComparison.Ordinal))))
            {
                return true;
            }

            if (allowShortNameFallback
                && targetAllowsShortNameFallback
                && string.Equals(GetLastQualifiedSegment(qualifier), containerName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool QualifiedNameHasSuffix(string fullName, string suffix)
    {
        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(suffix))
            return false;
        if (string.Equals(fullName, suffix, StringComparison.Ordinal))
            return true;
        if (suffix.Length >= fullName.Length)
            return false;

        var start = fullName.Length - suffix.Length;
        return string.Compare(fullName, start, suffix, 0, suffix.Length, StringComparison.Ordinal) == 0
            && fullName[start - 1] == '.';
    }

    private static string GetLastQualifiedSegment(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return string.Empty;

        var lastDot = qualifiedName.LastIndexOf('.');
        var lastColon = qualifiedName.LastIndexOf("::", StringComparison.Ordinal);
        var split = Math.Max(lastDot, lastColon);
        return split < 0 ? qualifiedName : qualifiedName[(split + (split == lastColon ? 2 : 1))..];
    }

    private static void AddTypeReferenceSegment(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string segment,
        int startInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language,
        bool isEscapedCSharpIdentifier = false,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        if (segment.Length == 0 || IsIgnoredTypeReferenceSegment(language, segment, isEscapedCSharpIdentifier, ignoredSegments))
            return;

        int column = startInLine + 1; // 1-based / 1始まり
        var dedupeKey = $"{lineNumber}:{column}:type_reference:{segment}";
        if (!seen.Add(dedupeKey))
            return;

        references.Add(new ReferenceRecord
        {
            FileId = fileId,
            SymbolName = segment,
            ReferenceKind = "type_reference",
            Line = lineNumber,
            Column = column,
            Context = context,
            ContainerKind = container?.Kind,
            ContainerName = container?.Name,
        });
    }

    private static SymbolRecord? FindInnermostContainer(IReadOnlyList<SymbolRecord> candidates, int lineNumber)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.BodyStartLine!.Value <= lineNumber && candidate.BodyEndLine!.Value >= lineNumber)
                return candidate;
        }

        return null;
    }

    private static bool CanAttachCSharpXmlDocCommentToNextDeclaration(
        SymbolRecord? innermostContainer,
        IReadOnlyList<SymbolRecord>? scopeCandidates,
        List<List<(int start, int end)>>? csharpAttrRanges,
        string[] preparedLines,
        int lineNumber,
        SymbolRecord documentedContainer)
    {
        if (!HasOnlyCSharpWhitespaceOrAttributesBetweenCommentAndDeclaration(
                csharpAttrRanges,
                preparedLines,
                lineNumber,
                documentedContainer.StartLine))
        {
            return false;
        }

        if (innermostContainer != null
            && innermostContainer.Kind is not "class" or "struct" or "interface" or "enum" or "namespace")
        {
            return false;
        }

        var enclosingScope = scopeCandidates == null
            ? null
            : FindInnermostContainer(scopeCandidates, lineNumber);
        if (enclosingScope?.BodyStartLine == null)
            return true;

        return IsAtCSharpXmlDocAttachmentDepth(enclosingScope, preparedLines, lineNumber);
    }

    private static bool HasOnlyCSharpWhitespaceOrAttributesBetweenCommentAndDeclaration(
        List<List<(int start, int end)>>? csharpAttrRanges,
        string[] preparedLines,
        int commentLineNumber,
        int declarationLineNumber)
    {
        var startLineIndex = Math.Max(commentLineNumber, 0);
        var endLineIndex = Math.Min(declarationLineNumber - 1, preparedLines.Length);
        for (var lineIndex = startLineIndex; lineIndex < endLineIndex; lineIndex++)
        {
            var line = preparedLines[lineIndex];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (IsCSharpAttributeOnlyLine(line, csharpAttrRanges?[lineIndex]))
                continue;

            return false;
        }

        return true;
    }

    private static bool IsCSharpAttributeOnlyLine(string preparedLine, List<(int start, int end)>? ranges)
    {
        if (ranges == null || ranges.Count == 0)
            return false;

        for (var i = 0; i < preparedLine.Length; i++)
        {
            if (char.IsWhiteSpace(preparedLine[i]))
                continue;

            var covered = false;
            foreach (var (start, end) in ranges)
            {
                if (i >= start && i < end)
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
                return false;
        }

        return true;
    }

    private static bool IsAtCSharpXmlDocAttachmentDepth(
        SymbolRecord enclosingScope,
        string[] preparedLines,
        int lineNumber)
    {
        var scopeBodyStartIndex = enclosingScope.BodyStartLine!.Value - 1;
        var commentLineIndex = lineNumber - 1;
        if (scopeBodyStartIndex < 0
            || scopeBodyStartIndex >= preparedLines.Length
            || scopeBodyStartIndex >= commentLineIndex)
        {
            return true;
        }

        var sawScopeOpenBrace = false;
        var nestedBraceDepth = 0;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var topLevelExecutableContinuation = false;
        var topLevelArrowExpressionContinuation = false;

        for (var i = scopeBodyStartIndex; i < commentLineIndex && i < preparedLines.Length; i++)
        {
            var line = preparedLines[i];
            for (var j = 0; j < line.Length; j++)
            {
                var ch = line[j];
                if (!sawScopeOpenBrace)
                {
                    if (ch == '{')
                        sawScopeOpenBrace = true;

                    continue;
                }

                if (nestedBraceDepth == 0)
                {
                    if (ch == '<')
                    {
                        angleDepth++;
                        continue;
                    }

                    if (ch == '>' && angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }

                    if (IsCSharpTopLevelArrowToken(line, j))
                    {
                        topLevelExecutableContinuation = true;
                        topLevelArrowExpressionContinuation = !IsCSharpArrowBlockStart(line, j + 2);
                        j++;
                        continue;
                    }

                    if (IsCSharpTopLevelAssignmentOperator(line, j))
                    {
                        topLevelExecutableContinuation = true;
                    }
                }

                if (ch == '{')
                {
                    nestedBraceDepth++;
                }
                else if (ch == '}')
                {
                    if (nestedBraceDepth == 0)
                        return false;

                    nestedBraceDepth--;
                }
                else if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                }
                else if (ch == '[')
                {
                    bracketDepth++;
                }
                else if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                }
                else if (nestedBraceDepth == 0
                         && ch == ';'
                         && parenDepth == 0
                         && bracketDepth == 0)
                {
                    topLevelExecutableContinuation = false;
                    topLevelArrowExpressionContinuation = false;
                }
            }
        }

        return !sawScopeOpenBrace
            || (nestedBraceDepth == 0
                && angleDepth == 0
                && parenDepth == 0
                && bracketDepth == 0
                && !topLevelExecutableContinuation
                && !topLevelArrowExpressionContinuation);
    }

    private static bool[] BuildCSharpBlockCommentLines(string[] lines)
    {
        var insideBlockComment = new bool[lines.Length];
        var inBlockComment = false;
        var inVerbatimString = false;
        var rawStringDelimiterLength = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            insideBlockComment[i] = inBlockComment;

            var index = 0;
            while (index < line.Length)
            {
                if (inBlockComment)
                {
                    var closeIndex = line.IndexOf("*/", index, StringComparison.Ordinal);
                    if (closeIndex < 0)
                        break;

                    index = closeIndex + 2;
                    inBlockComment = false;
                    continue;
                }

                if (rawStringDelimiterLength > 0)
                {
                    var closeCandidateIndex = index;
                    while (closeCandidateIndex < line.Length && char.IsWhiteSpace(line[closeCandidateIndex]))
                        closeCandidateIndex++;

                    var closeLength = CountCharacterRun(line, closeCandidateIndex, '"');
                    if (closeLength >= rawStringDelimiterLength
                        && closeLength > 0)
                    {
                        rawStringDelimiterLength = 0;
                        index = closeCandidateIndex + closeLength;
                        continue;
                    }

                    break;
                }

                if (inVerbatimString)
                {
                    if (line[index] == '"' && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        index += 2;
                        continue;
                    }

                    if (line[index] == '"')
                    {
                        index++;
                        inVerbatimString = false;
                        continue;
                    }

                    index++;
                    continue;
                }

                if (StartsWithOrdinal(line, index, "//"))
                    break;

                if (StartsWithOrdinal(line, index, "/*"))
                {
                    inBlockComment = true;
                    index += 2;
                    continue;
                }

                if (TryStartCSharpRawString(line, index, out var rawOpeningLength, out var rawDelimiterLength))
                {
                    rawStringDelimiterLength = rawDelimiterLength;
                    index += rawOpeningLength;
                    continue;
                }

                if (TryStartCSharpVerbatimString(line, index, out var verbatimOpeningLength))
                {
                    inVerbatimString = true;
                    index += verbatimOpeningLength;
                    continue;
                }

                if (TryStartCSharpRegularString(line, index, out var regularOpeningLength))
                {
                    index += regularOpeningLength;
                    while (index < line.Length)
                    {
                        if (line[index] == '\\')
                        {
                            index += Math.Min(2, line.Length - index);
                            continue;
                        }

                        if (line[index] == '"')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                if (line[index] == '\'')
                {
                    index++;
                    while (index < line.Length)
                    {
                        if (line[index] == '\\')
                        {
                            index += Math.Min(2, line.Length - index);
                            continue;
                        }

                        if (line[index] == '\'')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                index++;
            }
        }

        return insideBlockComment;
    }

    private static bool IsCSharpTopLevelAssignmentOperator(string line, int index)
    {
        if (index < 0 || index >= line.Length || line[index] != '=')
            return false;

        var previous = index > 0 ? line[index - 1] : '\0';
        var next = index + 1 < line.Length ? line[index + 1] : '\0';
        return previous is not ('=' or '!' or '<' or '>')
            && next is not ('=' or '>');
    }

    private static bool IsCSharpTopLevelArrowToken(string line, int index) =>
        index >= 0
        && index + 1 < line.Length
        && line[index] == '='
        && line[index + 1] == '>';

    private static bool IsCSharpArrowBlockStart(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        return index < line.Length && line[index] == '{';
    }

    private static int GetCSharpSameLineDocumentedDeclarationStartColumn(
        string originalLine,
        int commentEndExclusive,
        bool nextDelimitedDocComment)
    {
        if (nextDelimitedDocComment
            || commentEndExclusive < 0
            || commentEndExclusive + 1 >= originalLine.Length
            || originalLine[commentEndExclusive] != '*'
            || originalLine[commentEndExclusive + 1] != '/')
        {
            return -1;
        }

        var column = commentEndExclusive + 2;
        while (column < originalLine.Length && char.IsWhiteSpace(originalLine[column]))
            column++;

        return column < originalLine.Length ? column : -1;
    }

    private static bool HasOnlyCSharpWhitespaceOrAttributesAfterColumn(
        string preparedLine,
        List<(int start, int end)>? ranges,
        int startColumn)
    {
        if (startColumn < 0 || startColumn >= preparedLine.Length)
            return true;

        for (var i = startColumn; i < preparedLine.Length; i++)
        {
            if (char.IsWhiteSpace(preparedLine[i]))
                continue;

            if (ranges != null)
            {
                var covered = false;
                foreach (var (start, end) in ranges)
                {
                    if (i >= start && i < end)
                    {
                        covered = true;
                        break;
                    }
                }

                if (covered)
                    continue;
            }

            return false;
        }

        return true;
    }

    private static SymbolRecord? FindDocumentedContainer(
        IReadOnlyList<SymbolRecord> candidates,
        string structuralLine,
        string preparedLine,
        List<(int start, int end)>? csharpAttrRangesOnLine,
        int lineNumber,
        int sameLineDeclarationStartColumn)
    {
        var sameLineCandidate = FindSameLineDocumentedContainer(
            candidates,
            structuralLine,
            lineNumber,
            sameLineDeclarationStartColumn);
        if (sameLineCandidate != null)
            return sameLineCandidate;
        if (sameLineDeclarationStartColumn >= 0
            && !HasOnlyCSharpWhitespaceOrAttributesAfterColumn(
                preparedLine,
                csharpAttrRangesOnLine,
                sameLineDeclarationStartColumn))
        {
            return null;
        }

        SymbolRecord? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.StartLine <= lineNumber)
                continue;

            if (best == null
                || candidate.StartLine < best.StartLine
                || (candidate.StartLine == best.StartLine
                    && ((candidate.BodyEndLine ?? candidate.EndLine) - (candidate.BodyStartLine ?? candidate.StartLine))
                       < ((best.BodyEndLine ?? best.EndLine) - (best.BodyStartLine ?? best.StartLine))))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static SymbolRecord? FindSameLineDocumentedContainer(
        IReadOnlyList<SymbolRecord> candidates,
        string structuralLine,
        int lineNumber,
        int sameLineDeclarationStartColumn)
    {
        if (sameLineDeclarationStartColumn < 0)
            return null;

        SymbolRecord? best = null;
        var bestStartColumn = int.MaxValue;
        var bestSpanLength = int.MaxValue;
        var bestKindRank = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.StartLine != lineNumber
                || candidate.EndLine != lineNumber
                || string.IsNullOrEmpty(candidate.Signature))
            {
                continue;
            }

            if (!TryGetSameLineSignatureSpan(candidate, structuralLine, out var startColumn, out var endColumn)
                || startColumn < sameLineDeclarationStartColumn)
            {
                continue;
            }

            var spanLength = endColumn - startColumn;
            var kindRank = GetSameLineContainerKindRank(candidate.Kind);
            if (best == null
                || startColumn < bestStartColumn
                || (startColumn == bestStartColumn && spanLength < bestSpanLength)
                || (startColumn == bestStartColumn && spanLength == bestSpanLength && kindRank < bestKindRank))
            {
                best = candidate;
                bestStartColumn = startColumn;
                bestSpanLength = spanLength;
                bestKindRank = kindRank;
            }
        }

        return best;
    }

    private static SymbolRecord? FindInnermostSameLineCSharpContainer(
        IReadOnlyList<SymbolRecord> candidates,
        string structuralLine,
        int lineNumber,
        int column)
    {
        SymbolRecord? best = null;
        var bestStartColumn = -1;
        var bestSpanLength = int.MaxValue;
        var bestKindRank = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.BodyStartLine == null
                || candidate.BodyEndLine == null
                || candidate.BodyStartLine.Value > lineNumber
                || candidate.BodyEndLine.Value < lineNumber
                || candidate.StartLine != lineNumber
                || candidate.EndLine != lineNumber
                || string.IsNullOrEmpty(candidate.Signature))
            {
                continue;
            }

            if (!TryGetSameLineSignatureSpan(candidate, structuralLine, out var startColumn, out var endColumn))
                continue;

            if (column < startColumn || column >= endColumn)
                continue;

            var spanLength = endColumn - startColumn;
            var kindRank = GetSameLineContainerKindRank(candidate.Kind);
            if (best == null
                || startColumn > bestStartColumn
                || (startColumn == bestStartColumn && spanLength < bestSpanLength)
                || (startColumn == bestStartColumn && spanLength == bestSpanLength && kindRank < bestKindRank))
            {
                best = candidate;
                bestStartColumn = startColumn;
                bestSpanLength = spanLength;
                bestKindRank = kindRank;
            }
        }

        return best;
    }

    private static bool TryGetSameLineSignatureSpan(
        SymbolRecord candidate,
        string structuralLine,
        out int startColumn,
        out int endColumn)
    {
        startColumn = candidate.StartColumn ?? -1;
        if (startColumn < 0 || startColumn > structuralLine.Length)
        {
            startColumn = FindSignatureOccurrenceStartColumn(
                structuralLine,
                candidate.Signature!,
                candidate.SameLineSignatureOccurrenceIndex ?? 0);
            if (startColumn < 0)
            {
                endColumn = -1;
                return false;
            }
        }

        endColumn = Math.Min(structuralLine.Length, startColumn + candidate.Signature!.Length);
        return endColumn > startColumn;
    }

    private static int FindSignatureOccurrenceStartColumn(string structuralLine, string signature, int occurrenceIndex)
    {
        if (occurrenceIndex < 0 || string.IsNullOrEmpty(structuralLine) || string.IsNullOrEmpty(signature))
            return -1;

        var currentOccurrence = 0;
        var searchStart = 0;
        while (searchStart < structuralLine.Length)
        {
            var matchIndex = structuralLine.IndexOf(signature, searchStart, StringComparison.Ordinal);
            if (matchIndex < 0)
                return -1;

            if (currentOccurrence == occurrenceIndex)
                return matchIndex;

            currentOccurrence++;
            searchStart = matchIndex + signature.Length;
        }

        return -1;
    }

    private static bool[] BuildCSharpMultilineStringContentLines(string[] lines)
    {
        var insideStringContent = new bool[lines.Length];
        var inBlockComment = false;
        var inVerbatimString = false;
        var rawStringDelimiterLength = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            insideStringContent[i] = inVerbatimString || rawStringDelimiterLength > 0;

            var index = 0;
            while (index < line.Length)
            {
                if (inBlockComment)
                {
                    var closeIndex = line.IndexOf("*/", index, StringComparison.Ordinal);
                    if (closeIndex < 0)
                        break;

                    index = closeIndex + 2;
                    inBlockComment = false;
                    continue;
                }

                if (rawStringDelimiterLength > 0)
                {
                    var closeCandidateIndex = index;
                    while (closeCandidateIndex < line.Length && char.IsWhiteSpace(line[closeCandidateIndex]))
                        closeCandidateIndex++;

                    var closeLength = CountCharacterRun(line, closeCandidateIndex, '"');
                    if (closeLength >= rawStringDelimiterLength
                        && closeLength > 0)
                    {
                        rawStringDelimiterLength = 0;
                        index = closeCandidateIndex + closeLength;
                        continue;
                    }

                    break;
                }

                if (inVerbatimString)
                {
                    if (line[index] == '"' && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        index += 2;
                        continue;
                    }

                    if (line[index] == '"')
                    {
                        index++;
                        inVerbatimString = false;
                        continue;
                    }

                    index++;
                    continue;
                }

                if (StartsWithOrdinal(line, index, "//"))
                    break;

                if (StartsWithOrdinal(line, index, "/*"))
                {
                    inBlockComment = true;
                    index += 2;
                    continue;
                }

                if (TryStartCSharpRawString(line, index, out var rawOpeningLength, out var rawDelimiterLength))
                {
                    rawStringDelimiterLength = rawDelimiterLength;
                    index += rawOpeningLength;
                    continue;
                }

                if (TryStartCSharpVerbatimString(line, index, out var verbatimOpeningLength))
                {
                    inVerbatimString = true;
                    index += verbatimOpeningLength;
                    continue;
                }

                if (TryStartCSharpRegularString(line, index, out var regularOpeningLength))
                {
                    index += regularOpeningLength;
                    while (index < line.Length)
                    {
                        if (line[index] == '\\')
                        {
                            index += Math.Min(2, line.Length - index);
                            continue;
                        }

                        if (line[index] == '"')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                if (line[index] == '\'')
                {
                    index++;
                    while (index < line.Length)
                    {
                        if (line[index] == '\\')
                        {
                            index += Math.Min(2, line.Length - index);
                            continue;
                        }

                        if (line[index] == '\'')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                index++;
            }
        }

        return insideStringContent;
    }

    private static bool TryStartCSharpRawString(
        string line,
        int startIndex,
        out int openingLength,
        out int delimiterLength)
    {
        openingLength = 0;
        delimiterLength = 0;

        var quoteIndex = startIndex;
        while (quoteIndex < line.Length && line[quoteIndex] == '$')
            quoteIndex++;

        delimiterLength = CountCharacterRun(line, quoteIndex, '"');
        if (delimiterLength < 3)
            return false;

        openingLength = (quoteIndex - startIndex) + delimiterLength;
        return true;
    }

    private static bool TryStartCSharpVerbatimString(string line, int startIndex, out int openingLength)
    {
        openingLength = 0;
        if (StartsWithOrdinal(line, startIndex, "$@\"") || StartsWithOrdinal(line, startIndex, "@$\""))
        {
            openingLength = 3;
            return true;
        }

        if (!StartsWithOrdinal(line, startIndex, "@\""))
            return false;

        openingLength = 2;
        return true;
    }

    private static bool TryStartCSharpRegularString(string line, int startIndex, out int openingLength)
    {
        openingLength = 0;
        if (StartsWithOrdinal(line, startIndex, "$\""))
        {
            openingLength = 2;
            return true;
        }

        if (line[startIndex] != '"')
            return false;

        openingLength = 1;
        return true;
    }

    private static bool StartsWithOrdinal(string line, int startIndex, string value)
    {
        if (startIndex + value.Length > line.Length)
            return false;

        return string.Compare(line, startIndex, value, 0, value.Length, StringComparison.Ordinal) == 0;
    }

    private static int CountCharacterRun(string line, int startIndex, char value)
    {
        var index = startIndex;
        while (index < line.Length && line[index] == value)
            index++;

        return index - startIndex;
    }

    private static int GetSameLineContainerKindRank(string? kind) => kind switch
    {
        "function" => 0,
        "property" => 1,
        "class" => 2,
        "struct" => 3,
        "interface" => 4,
        "enum" => 5,
        "namespace" => 6,
        _ => 7,
    };

    private static SymbolRecord? FindInnermostClassLike(IReadOnlyList<SymbolRecord> candidates, int lineNumber)
    {
        foreach (var candidate in candidates)
        {
            // class/struct/enum are all ctor-owner kinds across supported languages. Java enum bodies
            // can declare constructors and chain via `this(...)`; C# enum cannot declare constructors
            // at all, so the chain regex will not match inside one even if we pick it up here.
            // class/struct/enum はいずれもコンストラクタを持ちうる宿主種別。Java enum は `this(...)`
            // 連鎖を書けるため含める。C# enum はコンストラクタ自体を持てないので副作用は出ない。
            if (candidate.Kind != "class" && candidate.Kind != "struct" && candidate.Kind != "enum")
                continue;
            if (candidate.BodyStartLine!.Value <= lineNumber && candidate.BodyEndLine!.Value >= lineNumber)
                return candidate;
        }

        return null;
    }

    private static void EmitCSharpCtorChainReferences(
        string preparedLine,
        IReadOnlyList<SymbolRecord> enclosingTypeCandidates,
        IReadOnlyList<SymbolRecord> containerCandidates,
        string[] structuralLines,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var chainMatches = CSharpCtorChainRegex.Matches(preparedLine);
        if (chainMatches.Count == 0)
            return;

        var enclosingType = FindInnermostClassLike(enclosingTypeCandidates, lineNumber);
        if (enclosingType == null)
            return;

        // For cross-line initializers such as:
        //   public A(int x, int y)
        //       : this(x, 0)
        //   { }
        // the chain line precedes the body, so the inner-most "body-covering" container lookup
        // returns the class rather than the constructor. Fall back to a declaration-to-body-end
        // lookup so the reference is attributed to the constructor that owns the chain.
        // クロス行イニシャライザでは body よりも前に連鎖行が現れるため、body 範囲のみで
        // 判定すると外側クラスが選ばれる。宣言〜body 終端の範囲で探し直す。
        var chainContainer = container;
        if (chainContainer == null || chainContainer.Kind != "function")
        {
            chainContainer = FindDeclarationRangeFunction(containerCandidates, lineNumber) ?? chainContainer;
        }

        foreach (Match match in chainMatches)
        {
            var kindToken = match.Groups["kind"].Value;
            string? target;
            if (kindToken == "this")
            {
                target = enclosingType.Name;
            }
            else
            {
                // `base(...)` needs the base type from the enclosing class's signature.
                // SymbolRecord.Signature only captures the first declaration line, so multi-line
                // base-lists (e.g. `class Child\n    : Parent`) lose the `: Parent` continuation.
                // Reconstruct the joined header up to the first `;` or `{` from structuralLines.
                // `base(...)` は外側クラスのシグネチャから基底型を解析する必要がある。
                // SymbolRecord.Signature は宣言 1 行目しか持たないので複数行 base-list が欠落する。
                // structuralLines から最初の `;` / `{` までを連結し直して渡す。
                var (_, _, headerText) = CollectCSharpRecordHeader(structuralLines, enclosingType.StartLine);
                target = ParseCSharpBaseType(headerText);
                if (string.IsNullOrWhiteSpace(target))
                    target = ParseCSharpBaseType(enclosingType.Signature);
                if (string.IsNullOrWhiteSpace(target))
                    continue;
            }

            AddChainReference(
                references, seen, fileId, target!, match.Groups["kind"].Index + 1,
                "call", context, lineNumber, chainContainer);
        }
    }

    private static SymbolRecord? FindDeclarationRangeFunction(
        IReadOnlyList<SymbolRecord> candidates, int lineNumber)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Kind != "function")
                continue;
            if (candidate.StartLine <= lineNumber && candidate.BodyEndLine!.Value >= lineNumber)
                return candidate;
        }

        return null;
    }

    private static void EmitJavaCtorChainReferences(
        string preparedLine,
        IReadOnlyList<SymbolRecord> enclosingTypeCandidates,
        IReadOnlyList<SymbolRecord> symbols,
        string[] structuralLines,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var match = JavaCtorChainRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var enclosingType = FindInnermostClassLike(enclosingTypeCandidates, lineNumber);
        if (enclosingType == null)
            return;

        // Prefer the innermost container when it is already a constructor of the enclosing class.
        // Otherwise, fall back to scanning all function-kind symbols by name match against the enclosing
        // class. Package-private ctors like `Leaf(int x){super(x);}` can appear without body ranges in
        // SymbolExtractor, so they are excluded from containerCandidates and the innermost container
        // becomes the class itself. Same-line ctor bodies are not emitted as function symbols at all
        // (the enum-member regex requires the line to end with `,`/`{`/`;`), so the final fallback
        // synthesizes a container from the current line when it matches a ctor declaration shape.
        // 外側クラスのコンストラクタが innermost container として既に見つかっていれば使う。
        // そうでなければ、関数シンボル全体を走査して外側クラスと同名のコンストラクタを
        // declaration StartLine ベースで引き直す。same-line body ctor は関数シンボル自体が
        // 存在しないので、行の shape から合成 container を作る。
        SymbolRecord? ctorContainer = null;
        if (container != null && container.Kind == "function"
            && string.Equals(container.Name, enclosingType.Name, StringComparison.Ordinal))
        {
            ctorContainer = container;
        }
        else
        {
            ctorContainer = FindEnclosingJavaConstructor(symbols, enclosingType, lineNumber)
                ?? TrySynthesizeSameLineJavaCtor(preparedLine, enclosingType, lineNumber)
                ?? FindEnclosingJavaConstructorFromStructure(structuralLines, enclosingType, lineNumber);
        }

        if (ctorContainer == null)
            return;

        var kindToken = match.Groups["kind"].Value;
        string? target;
        if (kindToken == "this")
        {
            target = enclosingType.Name;
        }
        else
        {
            // Java base-list can span multiple lines (`class Leaf\n    extends Base`).
            // SymbolRecord.Signature only captures the first declaration line, so reconstruct
            // the enclosing type header from structural lines (reusing the C# helper — the
            // scanner stops at `;` / `{`, which also bounds Java class / record / enum headers).
            // Java の base-list も複数行にまたがる (`class Leaf\n    extends Base`)。
            // SymbolRecord.Signature は 1 行目しか持たないため、structural lines からヘッダを
            // 再構築する (C# 用ヘルパーを流用。`;` / `{` 終端は Java にも適用可)。
            var (_, _, headerText) = CollectCSharpRecordHeader(structuralLines, enclosingType.StartLine);
            target = ParseJavaBaseType(headerText);
            if (string.IsNullOrWhiteSpace(target))
                target = ParseJavaBaseType(enclosingType.Signature);
            if (string.IsNullOrWhiteSpace(target))
                return;
        }

        AddChainReference(
            references, seen, fileId, target!, match.Groups["kind"].Index + 1,
            "call", context, lineNumber, ctorContainer);
    }

    /// <summary>
    /// Find the Java constructor symbol that encloses the given line number by name-matching
    /// the enclosing class. Covers package-private ctors that SymbolExtractor records without
    /// body ranges (so they are absent from containerCandidates).
    /// 指定行を内包する Java コンストラクタを、外側クラス名との一致で走査して探す。
    /// body 範囲を持たない package-private ctor でも declaration StartLine 起点で拾える。
    /// </summary>
    private static SymbolRecord? FindEnclosingJavaConstructor(
        IReadOnlyList<SymbolRecord> symbols,
        SymbolRecord enclosingType,
        int lineNumber)
    {
        var classStart = enclosingType.BodyStartLine ?? enclosingType.StartLine;
        var classEnd = enclosingType.BodyEndLine ?? enclosingType.EndLine;

        // Collect all ctor-name function symbols declared inside the class body at or before
        // `lineNumber`. When the symbol carries a body range, we can check the range directly.
        // Otherwise we fall back to the most recent declaration line at or before `lineNumber`,
        // bounded above by the next same-name ctor declaration (so the reference is still
        // attributed to the constructor that actually owns the chain line).
        // body 範囲があるシンボルは範囲判定、body 範囲の無い package-private ctor は
        // 次の同名 ctor 宣言の直前までを占有範囲として扱う。
        List<SymbolRecord>? candidates = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function")
                continue;
            if (!string.Equals(symbol.Name, enclosingType.Name, StringComparison.Ordinal))
                continue;
            if (symbol.StartLine < classStart || symbol.StartLine > classEnd)
                continue;
            if (symbol.StartLine > lineNumber)
                continue;
            (candidates ??= new List<SymbolRecord>()).Add(symbol);
        }

        if (candidates == null)
            return null;

        candidates.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));

        SymbolRecord? best = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            var symbol = candidates[i];
            var hasBodyRange = symbol.BodyStartLine != null && symbol.BodyEndLine != null;
            int rangeEnd;
            if (hasBodyRange)
            {
                rangeEnd = symbol.BodyEndLine!.Value;
            }
            else
            {
                // Extend to just before the next same-name ctor declaration, else to the end
                // of the enclosing class body. This approximates the ctor's true range when
                // SymbolExtractor could not parse the braces (e.g. package-private Java ctors).
                // 次の同名 ctor 宣言の直前、もしくは外側クラス body の終端まで拡張する。
                var nextStart = (i + 1 < candidates.Count) ? candidates[i + 1].StartLine : classEnd + 1;
                rangeEnd = nextStart - 1;
            }

            if (rangeEnd < lineNumber)
                continue;

            // Innermost / most recent declaration wins.
            // 一番近い宣言行を選ぶ。
            if (best == null || symbol.StartLine > best.StartLine)
                best = symbol;
        }

        return best;
    }

    /// <summary>
    /// Walk structural lines within the enclosing type body to recover a Java constructor
    /// whose header does not match SymbolExtractor's return-type-required method regex. Java
    /// constructors have no return type, so plain forms like `Leaf() {` / `Shade(int code) {`
    /// are not emitted as function symbols; this fallback parses them directly so chain calls
    /// can still be attributed to the owning ctor. Handles Allman style (`Leaf()\n{`),
    /// multi-line parameter lists, and multi-line `throws` clauses by delegating body range
    /// resolution to <see cref="SymbolExtractor.FindJavaBraceRange"/> (paren/bracket/angle
    /// depth aware, and string/comment/text-block aware).
    /// 外側型の body 内を走査して、return 型を持たない Java コンストラクタ
    /// （`Leaf() {` / `Shade(int code) {` / Allman 形式の `Leaf()\n{` / 複数行 parameter /
    /// 複数行 `throws` など）を復元する。SymbolExtractor のメソッド regex は戻り値型を必須
    /// とするため function シンボルが作られない。body 範囲の決定は FindJavaBraceRange に
    /// 委譲し、`()` / `[]` / `<>` の深さと文字列・コメント・text block を考慮する。
    /// </summary>
    private static SymbolRecord? FindEnclosingJavaConstructorFromStructure(
        string[] structuralLines,
        SymbolRecord enclosingType,
        int lineNumber)
    {
        var classBodyStart = enclosingType.BodyStartLine ?? enclosingType.StartLine;
        var classBodyEnd = enclosingType.BodyEndLine ?? enclosingType.EndLine;
        if (classBodyStart <= 0 || classBodyEnd < classBodyStart)
            return null;
        if (lineNumber < classBodyStart || lineNumber > classBodyEnd)
            return null;

        int i = classBodyStart - 1; // 0-based
        var lastIndex = Math.Min(structuralLines.Length - 1, classBodyEnd - 1);
        while (i <= lastIndex)
        {
            if (!TryMatchJavaCtorHeaderStart(structuralLines, i, lastIndex, enclosingType.Name))
            {
                i++;
                continue;
            }

            int declStart = i + 1; // 1-based

            // Let FindJavaBraceRange scan the entire header + body from the ctor start line.
            // It tracks paren / bracket / angle depth (so multi-line parameter lists,
            // annotations, and bounded generics don't mislead the body scan) and returns the
            // matching `}` as BodyEndLine. A `;`-terminated line without `{` yields
            // BodyEndLine == null; that shape is not a constructor body and is skipped.
            // FindJavaBraceRange がヘッダ + body を一気に走査する。`()`/`[]`/`<>` 深さを追跡
            // するため、複数行の parameter / annotation / bounded generic で誤検出しない。
            var (endLine, _, bodyEndLine) = SymbolExtractor.FindJavaBraceRange(structuralLines, i, 0);
            if (bodyEndLine is null)
            {
                // Not a body: advance past whatever the scanner consumed to avoid re-scanning.
                // body を持たない宣言（`;` 終端など）は ctor ではないので scanner が消費した
                // 範囲だけ進めて次行へ。
                i = Math.Max(i + 1, endLine);
                continue;
            }

            int bodyEnd = bodyEndLine.Value;
            if (lineNumber >= declStart && lineNumber <= bodyEnd)
            {
                return new SymbolRecord
                {
                    Kind = "function",
                    Name = enclosingType.Name,
                    Line = declStart,
                    StartLine = declStart,
                    EndLine = bodyEnd,
                    BodyStartLine = declStart,
                    BodyEndLine = bodyEnd,
                    ContainerKind = enclosingType.Kind,
                    ContainerName = enclosingType.Name,
                    ContainerQualifiedName = enclosingType.ContainerQualifiedName,
                    Visibility = enclosingType.Visibility,
                };
            }

            // Skip past this ctor's body to avoid re-parsing nested declarations inside it.
            // この ctor の body 以降に飛ばし、内部の宣言を誤って拾わないようにする。
            i = Math.Max(i + 1, bodyEnd);
        }

        return null;
    }

    /// <summary>
    /// Return true when line <paramref name="startIndex"/> starts a Java constructor header
    /// for <paramref name="ctorName"/>. Accepts both same-line forms (`Leaf() {`) and
    /// multi-line forms (Allman `Leaf()\n{`, multi-line parameter lists, multi-line `throws`).
    /// The detector confirms that after modifiers / annotations / optional generics, the ctor
    /// name appears immediately followed by `(` (possibly on the next non-blank line). The
    /// body resolver downstream (<see cref="SymbolExtractor.FindJavaBraceRange"/>) rejects
    /// `;`-terminated declarations without a body, so false positives like enum constants
    /// `Shade(1);` would still be discarded there even if this detector returns true.
    /// 指定行が Java コンストラクタヘッダの先頭かを判定する。modifier / annotation /
    /// optional generics を消費したあと ctor 名が続き、その直後（または次の非空行）に `(`
    /// が現れるかを検査する。body 側の判定は FindJavaBraceRange が担うため、`;` 終端の
    /// enum 定数 `Shade(1);` のような偽陽性は下流で落ちる。
    /// </summary>
    private static bool TryMatchJavaCtorHeaderStart(
        string[] lines,
        int startIndex,
        int endIndex,
        string ctorName)
    {
        if (startIndex < 0 || startIndex >= lines.Length)
            return false;

        var line = lines[startIndex];
        int i = 0;
        int n = line.Length;

        SkipWhitespace(line, ref i);

        // Consume modifiers and @annotations in any order (mirrors TryExtractJavaSameLineCtorSpan).
        // modifier と annotation は順不同で交互に現れ得るため、両方を同一ループで消費する。
        while (true)
        {
            SkipWhitespace(line, ref i);
            if (i < n && line[i] == '@')
            {
                i++;
                if (!ConsumeQualifiedIdentifier(line, ref i))
                    return false;
                SkipWhitespace(line, ref i);
                if (i < n && line[i] == '(')
                {
                    if (!SkipBalancedParens(line, ref i))
                        return false;
                }
                continue;
            }

            int wordStart = i;
            while (i < n && char.IsLetter(line[i]))
                i++;
            if (i == wordStart)
                break;
            var word = line.Substring(wordStart, i - wordStart);
            if (!JavaCtorModifiers.Contains(word))
            {
                i = wordStart;
                break;
            }
        }

        SkipWhitespace(line, ref i);

        if (i < n && line[i] == '<')
        {
            if (!SkipBalancedAngles(line, ref i))
                return false;
            SkipWhitespace(line, ref i);
        }

        int nameStart = i;
        if (!ConsumeIdentifier(line, ref i))
            return false;
        var name = line.Substring(nameStart, i - nameStart);
        if (!string.Equals(name, ctorName, StringComparison.Ordinal))
            return false;

        // The next non-whitespace token (possibly on a later line) must be `(`. Anything else
        // means this is not a ctor header — e.g. `Leaf leaf = ...` (field/variable) or
        // `Leaf method() ...` (method with Leaf as return type).
        // ctor 名の直後の非空白（次行以降でも可）は `(` でなければならない。
        SkipWhitespace(line, ref i);
        if (i < n)
            return line[i] == '(';

        for (int j = startIndex + 1; j <= endIndex && j < lines.Length; j++)
        {
            var next = lines[j];
            int k = 0;
            while (k < next.Length && char.IsWhiteSpace(next[k]))
                k++;
            if (k == next.Length)
                continue;
            return next[k] == '(';
        }
        return false;
    }

    /// <summary>
    /// When the current line itself carries a same-line Java constructor body (for example
    /// `Leaf(int x) { super(x); }`), synthesize a function-kind container so the chain rewrite
    /// still attaches the edge to the owning constructor. SymbolExtractor does not emit a
    /// function symbol for this shape because the enum-member regex requires the line to end
    /// with `,`, `{`, or `;`.
    /// same-line body の Java コンストラクタ（`Leaf(int x){super(x);}` など）では SymbolExtractor
    /// が function 化しないため、行自体を直接スキャンして合成 container を作る。
    /// </summary>
    private static SymbolRecord? TrySynthesizeSameLineJavaCtor(
        string preparedLine,
        SymbolRecord enclosingType,
        int lineNumber)
    {
        var name = TryExtractJavaCtorNameFromLine(preparedLine);
        if (name is null)
            return null;
        if (!string.Equals(name, enclosingType.Name, StringComparison.Ordinal))
            return null;

        return BuildSameLineJavaCtorContainer(enclosingType, lineNumber);
    }

    /// <summary>
    /// Per-line Java same-line ctor span resolved against the enclosing class. Returns the
    /// synthetic function-kind container paired with the 0-based indices of the ctor name,
    /// body-opening `{`, and body-closing `}` on the current line. Used by the main loop to
    /// (1) attribute body-level calls inside the `{ ... }` block to the synthetic ctor and
    /// (2) suppress the bogus declarator self-call on the ctor name.
    /// 同一行 Java ctor の span を外側クラスと突き合わせた結果を返す。合成 function コンテナと、
    /// ctor 名・body `{`・body `}` の 0-based 位置をセットで返し、call 帰属の振り向けと宣言子
    /// の自己 call 抑止に使う。
    /// </summary>
    private static (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex, int CloseBraceIndex)?
        TryBuildJavaSameLineCtorSpan(
            string preparedLine,
            int lineNumber,
            IReadOnlyList<SymbolRecord> enclosingTypeCandidates)
    {
        var span = TryExtractJavaSameLineCtorSpan(preparedLine);
        if (span is null)
            return null;
        var enclosingType = FindInnermostClassLike(enclosingTypeCandidates, lineNumber);
        if (enclosingType == null)
            return null;
        if (!string.Equals(span.Value.Name, enclosingType.Name, StringComparison.Ordinal))
            return null;

        var synthetic = BuildSameLineJavaCtorContainer(enclosingType, lineNumber);
        return (synthetic, span.Value.NameIndex, span.Value.OpenBraceIndex, span.Value.CloseBraceIndex);
    }

    private static SymbolRecord BuildSameLineJavaCtorContainer(SymbolRecord enclosingType, int lineNumber)
        => new SymbolRecord
        {
            Kind = "function",
            Name = enclosingType.Name,
            Line = lineNumber,
            StartLine = lineNumber,
            EndLine = lineNumber,
            BodyStartLine = lineNumber,
            BodyEndLine = lineNumber,
            ContainerKind = enclosingType.Kind,
            ContainerName = enclosingType.Name,
            ContainerQualifiedName = enclosingType.ContainerQualifiedName,
            Visibility = enclosingType.Visibility,
        };

    /// <summary>
    /// Same-line Java ctor span capturing the declarator name plus the 0-based indices of the
    /// ctor name, the opening `{` of the body, and the matching `}` on the same line (or -1
    /// when no matching close brace is found). Used to override the container for body-level
    /// calls and to suppress the bogus declarator self-call on the ctor name.
    /// same-line Java ctor の宣言情報。ctor 名位置・body `{` 位置・body `}` 位置を保持し、
    /// body 内の call に合成 function コンテナを流すのと、宣言子 `CtorName(` が誤って
    /// call として記録されるのを抑止するのに使う。
    /// </summary>
    internal readonly record struct JavaSameLineCtorSpan(
        string Name,
        int NameIndex,
        int OpenBraceIndex,
        int CloseBraceIndex);

    /// <summary>
    /// Depth-aware scanner for `@Annot ... <T extends Comparable<Integer>> Ctor(...) { ... }`
    /// style declarations. Returns the constructor name when the line opens a ctor body, or
    /// null otherwise. Handles qualified annotations (`@demo.Ann`), annotation argument lists
    /// with nested parens, and nested generic bounds that a flat regex cannot balance.
    /// 修飾付きアノテーション・引数付きアノテーション・入れ子の generic 境界を含む
    /// same-line ctor 宣言を depth-aware にスキャンして ctor 名を返すヘルパー。
    /// </summary>
    internal static string? TryExtractJavaCtorNameFromLine(string line)
        => TryExtractJavaSameLineCtorSpan(line)?.Name;

    /// <summary>
    /// Same as <see cref="TryExtractJavaCtorNameFromLine"/> but also returns the ctor name
    /// index, body-open `{` index, and the matching body-close `}` index on the same line.
    /// `TryExtractJavaCtorNameFromLine` と同じスキャナだが、ctor 名位置・`{` 位置・対応する
    /// `}` 位置もまとめて返すバリアント。
    /// </summary>
    internal static JavaSameLineCtorSpan? TryExtractJavaSameLineCtorSpan(string line)
    {
        int i = 0;
        int n = line.Length;

        SkipWhitespace(line, ref i);

        // Consume annotations and access / misc modifiers in any order so that forms such as
        // `public @Deprecated Leaf(...)` and `@demo.Ann private Leaf(...)` are both accepted
        // instead of bailing when an annotation appears after a modifier keyword.
        // アノテーションと access modifier は順不同で交互に現れ得るため、両方を 1 つのループで
        // 反復消費する。途中でどちらでもないトークンが来たら ctor 名（または `<...>`）へ遷移する。
        while (true)
        {
            SkipWhitespace(line, ref i);
            if (i < n && line[i] == '@')
            {
                i++;
                if (!ConsumeQualifiedIdentifier(line, ref i))
                    return null;
                SkipWhitespace(line, ref i);
                if (i < n && line[i] == '(')
                {
                    if (!SkipBalancedParens(line, ref i))
                        return null;
                }
                continue;
            }

            int wordStart = i;
            while (i < n && char.IsLetter(line[i]))
                i++;
            if (i == wordStart)
                break;
            var word = line.Substring(wordStart, i - wordStart);
            if (!JavaCtorModifiers.Contains(word))
            {
                i = wordStart;
                break;
            }
        }

        SkipWhitespace(line, ref i);

        if (i < n && line[i] == '<')
        {
            if (!SkipBalancedAngles(line, ref i))
                return null;
            SkipWhitespace(line, ref i);
        }

        int nameStart = i;
        if (!ConsumeIdentifier(line, ref i))
            return null;
        var name = line.Substring(nameStart, i - nameStart);

        SkipWhitespace(line, ref i);
        if (i >= n || line[i] != '(')
            return null;
        if (!SkipBalancedParens(line, ref i))
            return null;
        SkipWhitespace(line, ref i);

        if (i + 6 <= n && string.CompareOrdinal(line, i, "throws", 0, 6) == 0 &&
            (i + 6 == n || char.IsWhiteSpace(line[i + 6])))
        {
            i += 6;
            while (i < n && line[i] != '{')
                i++;
        }

        SkipWhitespace(line, ref i);
        if (i >= n || line[i] != '{')
            return null;

        int openBrace = i;
        int closeBrace = FindMatchingBraceClose(line, openBrace);
        return new JavaSameLineCtorSpan(name, nameStart, openBrace, closeBrace);
    }

    /// <summary>
    /// Find the `}` that closes the block opened at <paramref name="openBrace"/>, respecting
    /// string / char literals and nested `{ ... }` pairs. Returns -1 when no matching close is
    /// on the same line. Used to bound per-line Java same-line ctor body ranges.
    /// 同一行内で `{` と対応する `}` を探す。文字列・文字リテラルと入れ子の `{}` を尊重する。
    /// </summary>
    private static int FindMatchingBraceClose(string line, int openBrace)
    {
        int depth = 0;
        int n = line.Length;
        for (int i = openBrace; i < n; i++)
        {
            var c = line[i];
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < n)
                {
                    var ch = line[i];
                    if (ch == '\\' && i + 1 < n) { i += 2; continue; }
                    if (ch == quote) break;
                    i++;
                }
                if (i >= n) return -1;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static void SkipWhitespace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
    }

    private static bool ConsumeIdentifier(string text, ref int i)
    {
        if (i >= text.Length)
            return false;
        var c = text[i];
        if (!(char.IsLetter(c) || c == '_' || c == '$'))
            return false;
        i++;
        while (i < text.Length)
        {
            c = text[i];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '$')
                i++;
            else
                break;
        }
        return true;
    }

    private static bool ConsumeQualifiedIdentifier(string text, ref int i)
    {
        if (!ConsumeIdentifier(text, ref i))
            return false;
        while (i < text.Length && text[i] == '.')
        {
            int save = i;
            i++;
            if (!ConsumeIdentifier(text, ref i))
            {
                i = save;
                break;
            }
        }
        return true;
    }

    private static bool SkipBalancedParens(string text, ref int i)
    {
        if (i >= text.Length || text[i] != '(')
            return false;
        int depth = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            // Skip string / char literals so an embedded `)` inside `@Ann(text=")")` does not
            // prematurely close the annotation argument list.
            // 文字列・文字リテラル内の `)` で annotation 引数を早期終了しないようスキップする。
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < text.Length)
                {
                    var ch = text[i];
                    if (ch == '\\' && i + 1 < text.Length) { i += 2; continue; }
                    if (ch == quote) break;
                    i++;
                }
                if (i >= text.Length) return false;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    i++;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool SkipBalancedAngles(string text, ref int i)
    {
        if (i >= text.Length || text[i] != '<')
            return false;
        int depth = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<') depth++;
            else if (c == '>')
            {
                depth--;
                if (depth == 0)
                {
                    i++;
                    return true;
                }
            }
            else if (c == '{' || c == ';')
            {
                return false;
            }
        }
        return false;
    }

    private static void AddChainReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string name,
        int column,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
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

    private static void EmitMethodGroupReferences(
        string language,
        string preparedLine,
        HashSet<string>? callableDefinitionNames,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (callableDefinitionNames == null || callableDefinitionNames.Count == 0)
            return;

        foreach (Match match in MethodGroupReferenceRegex.Matches(preparedLine))
        {
            var contextTargetGroup = match.Groups["contextTarget"];
            if (contextTargetGroup.Success && MethodGroupContextTargetIgnoreNames.Contains(contextTargetGroup.Value))
                continue;
            if (!contextTargetGroup.Success)
            {
                var prefix = preparedLine.AsSpan(0, match.Groups["name"].Index).TrimEnd();
                if (prefix.EndsWith("+=", StringComparison.Ordinal) || prefix.EndsWith("-=", StringComparison.Ordinal))
                    continue;
            }

            var nameGroup = match.Groups["name"];
            var rawName = nameGroup.Value;
            var name = language == "csharp" ? NormalizeCSharpIdentifier(rawName) : rawName;
            if (!callableDefinitionNames.Contains(name))
                continue;

            var container = resolveContainerForColumn(nameGroup.Index);
            AddChainReference(references, seen, fileId, name, nameGroup.Index, "call", context, lineNumber, container);
        }
    }

    private static void EmitJavaMethodReferenceReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in JavaMethodReferenceRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            var container = resolveContainerForColumn(nameGroup.Index);

            if (string.Equals(nameGroup.Value, "new", StringComparison.Ordinal))
            {
                var ownerGroup = match.Groups["owner"];
                if (!ownerGroup.Success || ownerGroup.Value.Length == 0)
                    continue;
                if (ownerGroup.Value is "this" or "super")
                    continue;

                AddReference(references, seen, fileId, ownerGroup.Value, ownerGroup.Index, "instantiate", context, lineNumber, container);
                continue;
            }

            AddChainReference(references, seen, fileId, nameGroup.Value, nameGroup.Index, "call", context, lineNumber, container);
        }
    }

    /// <summary>
    /// Build a list of line ranges paired with synthetic function-kind containers for C# primary
    /// constructor declarations that carry a base primary-constructor call. This covers records
    /// (`record Child(int x) : Parent(x)`), C# 12 classes (`class Child(int x) : Parent(x)`) and
    /// structs (`struct Child(int x) : Parent(x)`), including the multi-line form where
    /// `: Parent(x)` sits on a continuation line. SymbolExtractor does not synthesize a separate
    /// ctor symbol for the implicit primary constructor, so the `Parent(x)` reference would
    /// otherwise land on `container = null` (when the declaration line has no body range) or on
    /// the declaring type itself. The synthetic container covers the header range only; methods
    /// inside a braced body still resolve to their real containers via FindInnermostContainer,
    /// and within the end line the override is limited to columns before the terminator so body
    /// calls sharing the same line (e.g. `record Child(int V) : Parent(V) { ... Add(V, 1); }`)
    /// are not pulled onto the synthetic ctor.
    /// C# の primary constructor 宣言に対して合成 function コンテナの (start, end, endColumn, container)
    /// リストを作る。record だけでなく C# 12 の class / struct primary constructor も対象にし、
    /// 宣言ヘッダーの範囲（end line は終端 `;` / `{` のカラムまで）だけ合成 ctor に差し替えることで、
    /// 同一行 braced body の呼び出しや後続メソッドは本来の container に残る。
    /// </summary>
    private static List<(int StartLine, int StartColumn, int EndLine, int EndColumn, SymbolRecord Container)> BuildCSharpPrimaryCtorContainers(
        string language,
        IReadOnlyList<SymbolRecord> symbols,
        string[] structuralLines)
    {
        var ranges = new List<(int, int, int, int, SymbolRecord)>();
        if (language != "csharp")
            return ranges;

        foreach (var symbol in symbols)
        {
            // SymbolExtractor stores C# records as Kind=class and C# 12 structs as Kind=struct.
            // Interfaces / enums / delegates cannot have primary constructors in C# so skip them.
            // C# record は Kind=class、C# 12 struct は Kind=struct として登録されるため両方対象。
            if (symbol.Kind != "class" && symbol.Kind != "struct")
                continue;
            var signature = symbol.Signature;
            if (string.IsNullOrWhiteSpace(signature))
                continue;

            // SymbolRecord.Signature only captures the first declaration line, so the first-line
            // regex filter misses split-line primary-ctor forms such as
            // `public record Child\n(\n    int Value\n)\n    : Parent(Value);`. Walk the
            // structural-masked lines from StartLine until we hit `;` / `{` and run the
            // primary-ctor detection on the joined header text instead.
            // 宣言の signature は 1 行目だけしか持たないので、`record` / `class` / `struct` と
            // `(` を別行に分ける書式では先頭行 regex の前段フィルタが空振りする。ここでは
            // structuralLines から `;` / `{` までヘッダーを連結し、連結後のテキストで判定する。
            var (headerEndLine, headerEndColumn, headerText) = CollectCSharpRecordHeader(structuralLines, symbol.StartLine);
            if (!IsCSharpPrimaryCtorHeader(headerText))
                continue;
            if (!HasCSharpBasePrimaryCtorCall(headerText))
                continue;

            // Restrict the synthetic container to the actual declaration span, starting at the
            // `class` / `struct` / `record` keyword column on the start line. Without this
            // same-line tokens BEFORE the keyword (e.g. attribute arguments in
            // `[Attr(Helper.Get())] public class Child(int x) : Parent(x) {}`) would get
            // attributed to the synthetic ctor and pollute callers / impact with phantom
            // `Child` callers for `Attr` and `Helper.Get`.
            // 合成 ctor コンテナを本物の宣言範囲に限定する。`class` / `struct` / `record`
            // キーワード位置より前（同一行の属性呼び出しなど）は本来の container に残す。
            var startColumn = FindCSharpPrimaryCtorKeywordColumn(structuralLines, symbol.StartLine);

            var synthetic = new SymbolRecord
            {
                FileId = symbol.FileId,
                Kind = "function",
                Name = symbol.Name,
                Line = symbol.Line,
                StartLine = symbol.StartLine,
                EndLine = headerEndLine,
                BodyStartLine = symbol.StartLine,
                BodyEndLine = headerEndLine,
                Signature = signature,
                ContainerKind = symbol.ContainerKind,
                ContainerName = symbol.ContainerName,
                ContainerQualifiedName = symbol.ContainerQualifiedName,
                FamilyKey = symbol.FamilyKey,
                Visibility = symbol.Visibility,
            };

            ranges.Add((symbol.StartLine, startColumn, headerEndLine, headerEndColumn, synthetic));
        }

        return ranges;
    }

    private static int FindCSharpPrimaryCtorKeywordColumn(string[] structuralLines, int startLine)
    {
        var idx = Math.Max(0, startLine - 1);
        if (idx >= structuralLines.Length)
            return 0;
        var line = structuralLines[idx];
        foreach (var keyword in CSharpPrimaryCtorKeywords)
        {
            int pos = 0;
            while (pos < line.Length)
            {
                var found = line.IndexOf(keyword, pos, StringComparison.Ordinal);
                if (found < 0) break;
                var before = found == 0 ? ' ' : line[found - 1];
                var afterIdx = found + keyword.Length;
                var after = afterIdx < line.Length ? line[afterIdx] : ' ';
                if (!IsCSharpIdentifierPart(before) && !IsCSharpIdentifierPart(after))
                    return found;
                pos = found + 1;
            }
        }
        return 0;
    }

    private static readonly string[] CSharpPrimaryCtorKeywords = { "record", "class", "struct" };

    private static bool IsCSharpIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Walk structural-masked lines starting at the 1-based <paramref name="startLine"/> and collect
    /// the declaration header up to (but not including) the first `;` or `{` that sits outside a
    /// string or comment. Returns the 1-based line number where the terminator was found (or the
    /// final line index when none was found) and the joined header text for further parsing.
    /// Reused for record primary-ctor container synthesis and multi-line `: base(...)` resolution.
    /// structuralLines を使って、class / struct / record 宣言ヘッダーを最初の `;` / `{` まで連結する。
    /// record primary-ctor のコンテナ合成と、複数行 `: base(...)` 解決の両方で使う。
    /// </summary>
    internal static (int EndLine, int EndColumn, string Text) CollectCSharpRecordHeader(string[] structuralLines, int startLine)
    {
        var startIdx = Math.Max(0, startLine - 1);
        if (structuralLines.Length == 0)
            return (startLine, int.MaxValue, string.Empty);

        // Depth-aware termination so that `{` / `;` inside annotation arg lists (e.g. the `{` in
        // `@Ann({A.class, B.class})`) or attribute-argument brackets does not cut the header off
        // before the real base-list terminator, which would silently drop the base type.
        // We intentionally do NOT track `<` / `>` as generic depth here: comparison operators
        // inside annotation / attribute expressions (e.g. `[Attr(Flag = 1 < 2)]` or
        // `@Ann(flag = 1 < 2)`) are raised as `<` without a matching `>`, so angle-depth tracking
        // would leave the counter pinned above zero and silently drop the real top-level `{` / `;`
        // terminator, letting the synthetic primary-ctor container or the Java base-type parse
        // swallow everything up to EOF. `{` / `;` cannot legally appear inside a top-level
        // `<...>` generic arg list in either C# or Java, so paren/bracket masking is sufficient.
        // EndColumn tracks the column index of the top-level terminator on the end line, or
        // int.MaxValue when no terminator was found (end-of-file), so call-site-scoped container
        // overrides can restrict themselves to the header portion of the end line.
        // アノテーション引数の `{` などを本当のヘッダ終端と誤認しないよう、`()` / `[]` の深さを追いながら
        // 最初の top-level `;` / `{` でのみ終了する。`<` / `>` は annotation / attribute 式内の比較演算子で
        // 非対称に現れうるため generic 深度として扱わない。
        // EndColumn は end line 上の終端 `;` / `{` の位置を返す（終端が無ければ int.MaxValue）。
        var sb = new System.Text.StringBuilder();
        int parenDepth = 0;
        int bracketDepth = 0;
        // Comment / string awareness so unbalanced `(` / `[` / `{` / `;` inside a line
        // comment, block comment, or string literal never advances the depth counters,
        // fires the terminator, or leaks into the returned header text. For Java `extends`
        // headers the structuralLines array is an unmasked clone (StructuralLineMasker is a
        // no-op for Java), so this is what keeps `class Leaf extends Root /* ( stray [ */ {`
        // from pinning parenDepth / bracketDepth at 1 and skipping the real `{` terminator,
        // and it also prevents ParseJavaBaseType from seeing the comment body when it parses
        // the header text downstream.
        // コメント・文字列内の不均衡な `(` / `[` / `{` / `;` を terminator 判定・連結テキスト双方から除外する。
        bool inBlockComment = false;
        bool inString = false;
        for (int i = startIdx; i < structuralLines.Length; i++)
        {
            var line = structuralLines[i];
            var masked = line.ToCharArray();
            var terminatorIdx = -1;
            for (int j = 0; j < line.Length; j++)
            {
                var c = line[j];

                if (inBlockComment)
                {
                    masked[j] = ' ';
                    if (c == '*' && j + 1 < line.Length && line[j + 1] == '/')
                    {
                        inBlockComment = false;
                        masked[j + 1] = ' ';
                        j++;
                    }
                    continue;
                }

                if (inString)
                {
                    masked[j] = ' ';
                    if (c == '\\' && j + 1 < line.Length)
                    {
                        masked[j + 1] = ' ';
                        j++;
                        continue;
                    }
                    if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '/' && j + 1 < line.Length)
                {
                    if (line[j + 1] == '/')
                    {
                        for (int k = j; k < line.Length; k++)
                            masked[k] = ' ';
                        break;
                    }
                    if (line[j + 1] == '*')
                    {
                        inBlockComment = true;
                        masked[j] = ' ';
                        masked[j + 1] = ' ';
                        j++;
                        continue;
                    }
                }

                if (c == '"')
                {
                    inString = true;
                    masked[j] = ' ';
                    continue;
                }

                if (c == '\'')
                {
                    // Rust / OCaml lifetime annotation vs. char literal: only skip when a
                    // closing `'` exists within ~12 chars on this line.
                    // Rust の lifetime と char literal を短距離の閉じ `'` の有無で見分ける。
                    var closeIdx = -1;
                    var limit = Math.Min(line.Length, j + 12);
                    for (int k = j + 1; k < limit; k++)
                    {
                        if (line[k] == '\\' && k + 1 < line.Length)
                        {
                            k++;
                            continue;
                        }
                        if (line[k] == '\'')
                        {
                            closeIdx = k;
                            break;
                        }
                    }
                    if (closeIdx > 0)
                    {
                        for (int k = j; k <= closeIdx; k++)
                            masked[k] = ' ';
                        j = closeIdx;
                    }
                    continue;
                }

                if (c == '(') parenDepth++;
                else if (c == ')') { if (parenDepth > 0) parenDepth--; }
                else if (c == '[') bracketDepth++;
                else if (c == ']') { if (bracketDepth > 0) bracketDepth--; }
                else if ((c == ';' || c == '{') && parenDepth == 0 && bracketDepth == 0)
                {
                    terminatorIdx = j;
                    break;
                }
            }

            var maskedLine = new string(masked);
            if (terminatorIdx >= 0)
            {
                sb.Append(maskedLine, 0, terminatorIdx);
                return (i + 1, terminatorIdx, sb.ToString());
            }

            sb.Append(maskedLine);
            sb.Append('\n');
        }

        return (structuralLines.Length, int.MaxValue, sb.ToString());
    }

    /// <summary>
    /// Returns true when the C# type header text carries a base-list entry that looks like a
    /// primary-constructor call (contains `(`). Accepts multi-line header text already joined by
    /// <see cref="CollectCSharpRecordHeader"/>.
    /// C# 型ヘッダー（複数行連結後でも可）の base-list 先頭エントリが `(` を含むかを判定する。
    /// </summary>
    /// <summary>
    /// Return true when a joined C# type-declaration header (possibly spanning multiple lines,
    /// including line-broken primary-ctor parens) looks like a primary-constructor declaration.
    /// Accepts `record Child(...)`, `record class Child(...)`, `record struct Child(...)`,
    /// C# 12 `class Child(...)`, `struct Child(...)`, generic arity such as `class Child<T>(...)`,
    /// and the split-line form where `record Child\n(\n ... )` places the `(` on a continuation line.
    /// 連結済みの C# 宣言ヘッダーが primary-ctor 宣言かを判定する。`record` だけでなく C# 12 の
    /// `class` / `struct` primary constructor も対象にし、`(` が別行に分かれる書式にも対応する。
    /// </summary>
    private static bool IsCSharpPrimaryCtorHeader(string headerText)
    {
        if (string.IsNullOrWhiteSpace(headerText))
            return false;
        return CSharpPrimaryCtorHeaderRegex.IsMatch(headerText);
    }

    private static bool HasCSharpBasePrimaryCtorCall(string headerText)
    {
        var text = headerText.TrimEnd();
        if (text.EndsWith(";", StringComparison.Ordinal))
            text = text.Substring(0, text.Length - 1).TrimEnd();
        if (text.EndsWith("{", StringComparison.Ordinal))
            text = text.Substring(0, text.Length - 1).TrimEnd();

        var colonIndex = FindSignatureColonIndex(text);
        if (colonIndex < 0)
            return false;

        var baseList = text.Substring(colonIndex + 1);
        var whereMatch = CSharpWhereClauseRegex.Match(baseList);
        if (whereMatch.Success)
            baseList = baseList.Substring(0, whereMatch.Index);

        var firstEntry = TakeFirstBaseEntry(baseList).Trim();
        // Only count a `(` that sits at generic / bracket depth 0 — a primary-ctor base call
        // always puts its argument list directly after the bare type name, whereas generic args
        // and array ranks can legally contain `(` (tuple syntax `<(int, int)>`, function types
        // `<Func<(int, int)>>`, or attribute arg brackets). A naive `.Contains('(')` would treat
        // those as primary-ctor calls and synthesize a phantom record ctor container.
        // 先頭エントリのうち generic/bracket 深度 0 の `(` だけを primary-ctor 呼び出し扱いにする。
        // `IBox<(int, int)>` のような tuple を含む interface 実装を連鎖呼び出しと誤認させない。
        int angleDepth = 0;
        int squareDepth = 0;
        for (int i = 0; i < firstEntry.Length; i++)
        {
            var c = firstEntry[i];
            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '(':
                    if (angleDepth == 0 && squareDepth == 0)
                        return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// Parse the first base-class token from a C# class/struct/record signature such as
    /// `class B : A, IFoo`, `record C(int x) : A(x)`, or `class B<T> : A<T> where T : new()`.
    /// Returns null when no base list is present or when the signature is empty.
    /// C# の class/struct/record シグネチャから最初の基底クラストークンを取り出す。
    /// </summary>
    internal static string? ParseCSharpBaseType(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var text = signature.TrimEnd();
        if (text.EndsWith("{", StringComparison.Ordinal))
            text = text.Substring(0, text.Length - 1).TrimEnd();

        var colonIndex = FindSignatureColonIndex(text);
        if (colonIndex < 0)
            return null;

        var baseList = text.Substring(colonIndex + 1);
        var whereMatch = CSharpWhereClauseRegex.Match(baseList);
        if (whereMatch.Success)
            baseList = baseList.Substring(0, whereMatch.Index);

        var firstEntry = TakeFirstBaseEntry(baseList).Trim();
        return ExtractBareTypeName(firstEntry);
    }

    /// <summary>
    /// Parse the first extends-clause type from a Java class/interface/record signature.
    /// 例: `class B extends A implements IFoo` → `A`、
    /// `class Leaf extends Outer<Integer>.Base {` → `Base`。
    /// </summary>
    internal static string? ParseJavaBaseType(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        // Locate `extends` at angle/paren depth 0 so bounded type parameters like
        // `class Leaf<T extends Number> extends Root {` do not resolve to the
        // parameter bound (`Number`) instead of the real base (`Root`).
        // 境界付き型パラメータ（`class Leaf<T extends Number> extends Root {`）で
        // 型パラメータ境界の `extends` を先に拾わないよう、angle / paren 深度 0 の
        // `extends` のみを検出する。
        int start = FindTopLevelExtendsEnd(signature!);
        if (start < 0)
            return null;

        int i = start;
        int angleDepth = 0;
        int parenDepth = 0;
        while (i < signature.Length)
        {
            char c = signature[i];
            if (c == '<')
            {
                angleDepth++;
            }
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (c == '(')
            {
                // Track `(...)` depth so that commas inside annotation arguments such as
                // `@Ann(a = 1, b = 2) Root` or `@Ann({A.class, B.class}) Root` are not mistaken
                // for top-level base-list separators. Without this the scanner breaks at the
                // inner `,`, feeds a truncated segment to the annotation stripper, and the
                // super(...) edge gets misattributed or dropped entirely.
                // annotation 引数内のカンマ（`@Ann(a = 1, b = 2) Root` や
                // `@Ann({A.class, B.class}) Root`）が base-list 区切りと誤認されないよう `(...)` の
                // 深さも追跡する。これをやらないと内側の `,` で走査が切れ、annotation stripper に
                // 壊れたセグメントが渡って super(...) の連鎖エッジが落ちる。
                parenDepth++;
            }
            else if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (angleDepth == 0 && parenDepth == 0)
            {
                if (c == '{' || c == ',' || c == ';')
                    break;
                // Stop at a word-boundary `implements` or `permits` (Java 17+ sealed types).
                // 単語境界の `implements` / `permits` (Java 17+ sealed 型) で停止する。
                if (IsJavaBaseListTerminatorKeyword(signature, i, start, "implements") ||
                    IsJavaBaseListTerminatorKeyword(signature, i, start, "permits"))
                {
                    break;
                }
            }
            i++;
        }

        var segment = signature.Substring(start, i - start).Trim();
        if (segment.Length == 0)
            return null;

        // Strip Java type-use annotations (JLS 9.7.4): `@Ann`, `@pkg.Ann`, `@Ann(value=1)` can
        // appear before the type itself (`extends @Ann Root`) or between nested-type segments
        // (`Outer<Integer>.@Ann Base`). Without this pass the base resolver returns a phantom
        // type name like `@Ann Root` that misattributes references / callers / impact.
        // Java の type-use annotation (JLS 9.7.4) を剥がす。`extends @Ann Root` や
        // `Outer<Integer>.@Ann Base` のような形で基底型の直前やセグメント間に現れるため、
        // 先に除去しないと `@Ann Root` のような幽霊シンボルへ参照が張られてしまう。
        segment = StripJavaTypeAnnotations(segment);
        return segment.Length == 0 ? null : ExtractBareTypeName(segment);
    }

    /// <summary>
    /// Return the index past the first `extends` keyword that appears at angle/paren depth 0,
    /// or -1 when no such occurrence exists. Matches the semantics of the old `\bextends\s+`
    /// regex entrypoint but skips `extends` inside `<...>` (bounded type parameters) and
    /// `(...)` (annotation argument lists).
    /// </summary>
    private static int FindTopLevelExtendsEnd(string signature)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        for (int i = 0; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c == '<')
            {
                angleDepth++;
            }
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (c == '(')
            {
                parenDepth++;
            }
            else if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (angleDepth == 0 && parenDepth == 0 && IsExtendsKeywordAt(signature, i))
            {
                int end = i + 7; // "extends".Length
                while (end < signature.Length && char.IsWhiteSpace(signature[end]))
                    end++;
                return end;
            }
        }
        return -1;
    }

    private static bool IsExtendsKeywordAt(string signature, int i)
    {
        const string Keyword = "extends";
        if (i + Keyword.Length > signature.Length)
            return false;
        if (i > 0 && IsJavaIdentifierPart(signature[i - 1]))
            return false;
        if (string.CompareOrdinal(signature, i, Keyword, 0, Keyword.Length) != 0)
            return false;
        int after = i + Keyword.Length;
        // `\bextends\s+` equivalence: must be followed by whitespace so that names like
        // `extendsFoo` or identifiers containing `extends` do not match.
        // `\bextends\s+` 相当: `extendsFoo` のような識別子や合成語を誤認しないよう、
        // 直後に空白が続くものだけを `extends` キーワードとして扱う。
        if (after >= signature.Length)
            return false;
        return char.IsWhiteSpace(signature[after]);
    }

    private static string StripJavaTypeAnnotations(string text)
    {
        if (text.IndexOf('@') < 0)
            return text;

        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '@')
            {
                // Skip `@` + qualified identifier (`@pkg.Ann`) + optional balanced `(...)`.
                i++;
                while (i < text.Length && (IsJavaIdentifierPart(text[i]) || text[i] == '.'))
                    i++;
                if (i < text.Length && text[i] == '(')
                {
                    int parenDepth = 1;
                    i++;
                    while (i < text.Length && parenDepth > 0)
                    {
                        var ch = text[i];
                        // Skip string / char literals so `@Ann(text=")")` does not close early.
                        // 文字列・文字リテラル内の `)` で早期終了しないようスキップする。
                        if (ch == '"' || ch == '\'')
                        {
                            var quote = ch;
                            i++;
                            while (i < text.Length)
                            {
                                var lc = text[i];
                                if (lc == '\\' && i + 1 < text.Length) { i += 2; continue; }
                                if (lc == quote) { i++; break; }
                                i++;
                            }
                            continue;
                        }
                        if (ch == '(') parenDepth++;
                        else if (ch == ')') parenDepth--;
                        i++;
                    }
                }
                // Drop a single trailing whitespace run so `@Ann Root` collapses to `Root`.
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                continue;
            }
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsJavaIdentifierPart(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static bool IsJavaBaseListTerminatorKeyword(string signature, int i, int start, string keyword)
    {
        if (i + keyword.Length > signature.Length)
            return false;
        if (i != start && IsJavaIdentifierPart(signature[i - 1]))
            return false;
        if (string.CompareOrdinal(signature, i, keyword, 0, keyword.Length) != 0)
            return false;
        if (i + keyword.Length < signature.Length && IsJavaIdentifierPart(signature[i + keyword.Length]))
            return false;
        return true;
    }

    private static int FindSignatureColonIndex(string text)
    {
        var depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;
                case '>':
                case ')':
                case ']':
                    if (depth > 0) depth--;
                    break;
                case ':':
                    if (depth == 0)
                    {
                        // Skip `::` alias qualifier (`global::System.Exception`).
                        // `::` エイリアス修飾子（`global::System.Exception`）はスキップ。
                        if (i + 1 < text.Length && text[i + 1] == ':')
                        {
                            i++;
                            continue;
                        }
                        return i;
                    }
                    break;
            }
        }

        return -1;
    }

    private static string TakeFirstBaseEntry(string baseList)
    {
        var depth = 0;
        for (int i = 0; i < baseList.Length; i++)
        {
            var c = baseList[i];
            switch (c)
            {
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;
                case '>':
                case ')':
                case ']':
                    if (depth > 0) depth--;
                    break;
                case ',':
                    if (depth == 0)
                        return baseList.Substring(0, i);
                    break;
            }
        }

        return baseList;
    }

    private static string? ExtractBareTypeName(string entry)
    {
        var trimmed = entry.Trim();
        if (trimmed.Length == 0)
            return null;

        // Split on `.` / `::` at generic depth 0, then return the last segment with generic
        // args stripped. Naive "first `<`, then last `.`" slicing loses nested types such as
        // `Outer<int>.Base`, `Outer<Integer>.Base`, or `global::Ns.Outer<T>.Inner`.
        // 最初の `<` で切ってから末尾 `.` を探す素朴な方法では `Outer<int>.Base` のような
        // ネスト型を取り違えるため、generic 深度 0 の `.` / `::` でセグメント分割して末尾だけ返す。
        int lastSegmentStart = 0;
        int angleDepth = 0;
        int endIndex = trimmed.Length;
        for (int i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '<')
            {
                angleDepth++;
            }
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (angleDepth == 0)
            {
                if (c == '(')
                {
                    // Strip record primary-ctor args at top level: `A(...)` → `A`.
                    // record のプライマリコンストラクタ引数を剥がす。
                    endIndex = i;
                    break;
                }
                if (c == '.')
                {
                    lastSegmentStart = i + 1;
                }
                else if (c == ':' && i + 1 < trimmed.Length && trimmed[i + 1] == ':')
                {
                    lastSegmentStart = i + 2;
                    i++;
                }
            }
        }

        var segment = trimmed.Substring(lastSegmentStart, endIndex - lastSegmentStart).Trim();
        var ltIndex = segment.IndexOf('<');
        if (ltIndex >= 0)
            segment = segment.Substring(0, ltIndex);

        segment = segment.Trim();
        return segment.Length > 0 ? segment : null;
    }

    private static Dictionary<int, List<SqlDefinitionLeafSpan>> BuildSqlDefinitionLeafSpansByLine(string[] lines, IReadOnlyList<SymbolRecord> symbols)
    {
        var spansByLine = new Dictionary<int, List<SqlDefinitionLeafSpan>>();
        foreach (var symbol in symbols)
        {
            if (symbol.Line < 1 || symbol.Line > lines.Length)
                continue;
            if (!TryFindSqlDefinitionLeafSpan(lines[symbol.Line - 1], symbol.Name, out var span))
                continue;

            if (!spansByLine.TryGetValue(symbol.Line, out var spans))
            {
                spans = [];
                spansByLine[symbol.Line] = spans;
            }

            spans.Add(span);
        }

        return spansByLine;
    }

    private static bool TryFindSqlDefinitionLeafSpan(string line, string qualifiedName, out SqlDefinitionLeafSpan span)
    {
        span = default;
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(qualifiedName))
            return false;

        var leafName = SqlNameResolver.GetLeafName(qualifiedName);
        if (string.IsNullOrWhiteSpace(leafName))
            return false;

        var rawSegments = SplitSqlQualifiedNameSourceSegments(qualifiedName);
        if (rawSegments.Count == 0)
            return false;

        var pattern = new StringBuilder();
        for (var i = 0; i < rawSegments.Count; i++)
        {
            if (i > 0)
                pattern.Append(@"\s*\.\s*");

            var escaped = Regex.Escape(rawSegments[i]);
            if (i == rawSegments.Count - 1)
                pattern.Append("(?<leaf>").Append(escaped).Append(')');
            else
                pattern.Append(escaped);
        }

        var match = Regex.Match(line, pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var leafGroup = match.Groups["leaf"];
        if (!leafGroup.Success)
            return false;

        span = new SqlDefinitionLeafSpan(leafName, leafGroup.Index, leafGroup.Index + leafGroup.Length);
        return true;
    }

    private static List<string> SplitSqlQualifiedNameSourceSegments(string qualifiedName)
    {
        var trimmed = qualifiedName.Trim();
        var segments = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (quote != '\0')
            {
                current.Append(ch);
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < trimmed.Length && trimmed[i + 1] == ']')
                        {
                            current.Append(trimmed[i + 1]);
                            i++;
                        }
                        else
                        {
                            quote = '\0';
                        }
                    }

                    continue;
                }

                if (ch == quote)
                {
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == quote)
                    {
                        current.Append(trimmed[i + 1]);
                        i++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (ch is '[' or '"' or '`')
            {
                quote = ch;
                current.Append(ch);
                continue;
            }

            if (ch == '.')
            {
                AppendSqlQualifiedNameSourceSegment(segments, current);
                continue;
            }

            current.Append(ch);
        }

        AppendSqlQualifiedNameSourceSegment(segments, current);
        return segments;
    }

    private static void AppendSqlQualifiedNameSourceSegment(List<string> segments, StringBuilder current)
    {
        var value = current.ToString().Trim();
        if (value.Length > 0)
            segments.Add(value);
        current.Clear();
    }

    // SQL-aware line sanitizer used only for the SQL source/target and `EXEC` / `EXECUTE` / `CALL`
    // scans. Preserves backtick/bracket/double-quoted identifiers, blanks single-quoted strings
    // (including multiline bodies), multiline `/* ... */` comments, PostgreSQL `$$...$$` /
    // `$tag$...$tag$` bodies, and line comments so non-code regions cannot leak phantom references
    // into the graph.
    // SQL source/target と `EXEC` / `EXECUTE` / `CALL` 抽出向けの SQL 特化サニタイザ。
    // backtick / bracket / double-quoted identifier は保持しつつ、単引用符文字列
    // （複数行本体を含む）、複数行 `/* ... */` コメント、PostgreSQL の `$$...$$` /
    // `$tag$...$tag$` 本体、行コメントを空白化し、非コード領域から phantom reference が
    // 漏れないようにする。
    private static string PrepareSqlLineForIdentifierScan(
        string line,
        SqlIdentifierScanState state,
        string? statementPrefix,
        out bool lineEndedByLineComment,
        out SqlIdentifierScanState nextState)
    {
        lineEndedByLineComment = false;
        if (string.IsNullOrEmpty(line))
        {
            nextState = state;
            return line;
        }

        var sanitized = line.ToCharArray();
        bool inBlockComment = state.InBlockComment;
        string? dollarQuoteDelimiter = state.DollarQuoteDelimiter;
        bool inSingleQuotedString = state.InSingleQuotedString;

        void BlankRange(int start, int endExclusive)
        {
            start = Math.Max(0, start);
            endExclusive = Math.Min(sanitized.Length, endExclusive);
            for (int blankIndex = start; blankIndex < endExclusive; blankIndex++)
                sanitized[blankIndex] = ' ';
        }

        for (int i = 0; i < line.Length;)
        {
            if (inBlockComment)
            {
                int closing = line.IndexOf("*/", i, StringComparison.Ordinal);
                int end = closing >= 0 ? closing + 2 : line.Length;
                BlankRange(i, end);
                if (closing < 0)
                    break;
                i = end;
                inBlockComment = false;
                continue;
            }
            if (!string.IsNullOrEmpty(dollarQuoteDelimiter))
            {
                int closing = line.IndexOf(dollarQuoteDelimiter, i, StringComparison.Ordinal);
                if (closing < 0)
                {
                    BlankRange(i, line.Length);
                    break;
                }

                int nextContent = SkipWhitespaceAhead(line, closing + dollarQuoteDelimiter.Length);
                if (nextContent < line.Length
                    && line[nextContent] != ';'
                    && line[nextContent] != ','
                    && line[nextContent] != ')'
                    && line[nextContent] != ']')
                {
                    int nestedClosing = line.IndexOf(
                        dollarQuoteDelimiter,
                        closing + dollarQuoteDelimiter.Length,
                        StringComparison.Ordinal);
                    if (nestedClosing >= 0)
                    {
                        int end = nestedClosing + dollarQuoteDelimiter.Length;
                        BlankRange(i, end);
                        i = end;
                        continue;
                    }
                }

                int closingEnd = closing + dollarQuoteDelimiter.Length;
                BlankRange(i, closingEnd);
                i = closingEnd;
                dollarQuoteDelimiter = null;
                continue;
            }
            if (inSingleQuotedString)
            {
                int closing = FindClosingSqlSingleQuote(line, i);
                int end = closing >= 0 ? closing + 1 : line.Length;
                BlankRange(i, end);
                i = end;
                if (closing >= 0)
                {
                    inSingleQuotedString = false;
                    continue;
                }

                break;
            }

            char c = line[i];
            if (c == '"')
            {
                int closing = FindClosingSqlDoubleQuote(line, i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '`')
            {
                int closing = line.IndexOf('`', i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '[')
            {
                int closing = line.IndexOf(']', i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '\'')
            {
                int closing = FindClosingSqlSingleQuote(line, i + 1);
                int end = closing >= 0 ? closing + 1 : line.Length;
                BlankRange(i, end);
                i = end;
                if (closing < 0)
                    inSingleQuotedString = true;
                continue;
            }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                BlankRange(i, i + 2);
                i += 2;
                inBlockComment = true;
                continue;
            }
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                lineEndedByLineComment = true;
                BlankRange(i, line.Length);
                break;
            }
            if (c == '#')
            {
                if (ShouldTreatHashAsSqlComment(line, i, statementPrefix))
                {
                    lineEndedByLineComment = true;
                    BlankRange(i, line.Length);
                    break;
                }
            }
            if (c == '$' && TryReadSqlDollarQuoteDelimiter(line, i, out var delimiter))
            {
                BlankRange(i, i + delimiter.Length);
                i += delimiter.Length;
                dollarQuoteDelimiter = delimiter;
                continue;
            }

            i++;
        }

        nextState = new SqlIdentifierScanState(inBlockComment, dollarQuoteDelimiter, inSingleQuotedString);
        return new string(sanitized);
    }

    private static bool ShouldTreatHashAsSqlComment(string line, int hashIndex, string? statementPrefix)
    {
        if (hashIndex < 0 || hashIndex >= line.Length || line[hashIndex] != '#')
            return false;

        int probe = hashIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(line[probe]))
            probe--;
        if (probe < 0 && !string.IsNullOrWhiteSpace(statementPrefix))
        {
            var combined = statementPrefix + "\n" + line;
            return ShouldTreatHashAsSqlCommentCore(combined, statementPrefix.Length + 1 + hashIndex);
        }

        return ShouldTreatHashAsSqlCommentCore(line, hashIndex);
    }

    private static bool ShouldTreatHashAsSqlCommentCore(string line, int hashIndex)
    {
        if (hashIndex < 0 || hashIndex >= line.Length || line[hashIndex] != '#')
            return false;

        int next = hashIndex + 1;
        if (hashIndex > 0
            && line[hashIndex - 1] == '#'
            && next < line.Length
            && (char.IsLetterOrDigit(line[next]) || line[next] == '_'))
            return false;
        if (next + 1 < line.Length
            && line[next] == '#'
            && (char.IsLetterOrDigit(line[next + 1]) || line[next + 1] == '_'))
            return false;
        if (next >= line.Length || !(char.IsLetterOrDigit(line[next]) || line[next] == '_'))
            return true;

        int probe = hashIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(line[probe]))
            probe--;
        while (probe >= 0 && line[probe] == ',')
        {
            var priorListItem = line[..probe];
            int sourceStart = FindLastSqlCommaOutsideQuotedIdentifiers(priorListItem);
            if (sourceStart >= 0)
                sourceStart++;
            else
            {
                var usingMatches = SqlUsingKeywordRegex.Matches(priorListItem);
                if (usingMatches.Count > 0)
                    sourceStart = usingMatches[^1].Index + usingMatches[^1].Length;
                else
                {
                    sourceStart = priorListItem.LastIndexOf('#');
                    if (sourceStart < 0)
                        return true;
                }
            }
            while (sourceStart < priorListItem.Length && char.IsWhiteSpace(priorListItem[sourceStart]))
                sourceStart++;

            var listMatch = SqlTrailingTempIdentifierRegex.Match(priorListItem[sourceStart..]);
            if (!listMatch.Success)
                return true;

            probe = sourceStart - 1;
            while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                probe--;
        }
        if (probe < 0)
            return true;
        if (line[probe] == '.')
            return false;
        if (line[probe] == ')')
        {
            int depth = 1;
            probe--;
            while (probe >= 0 && depth > 0)
            {
                if (line[probe] == ')')
                    depth++;
                else if (line[probe] == '(')
                    depth--;
                probe--;
            }
            while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                probe--;
            if (probe < 0)
                return true;

            int modifierEnd = probe;
            while (probe >= 0 && char.IsLetter(line[probe]))
                probe--;
            int modifierStart = probe + 1;
            if (modifierStart <= modifierEnd
                && string.Equals(line[modifierStart..(modifierEnd + 1)], "TOP", StringComparison.OrdinalIgnoreCase))
            {
                while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                    probe--;
                if (probe < 0)
                    return true;
            }
        }

        int tokenEnd = probe;
        while (probe >= 0 && char.IsLetter(line[probe]))
            probe--;
        int tokenStart = probe + 1;
        if (tokenStart > tokenEnd)
            return true;

        var token = line[tokenStart..(tokenEnd + 1)];
        // issue #656: keep `#name` as a temp-object identifier after keywords that precede an object
        // name in T-SQL. MySQL `# comment` false positives introduced by treating the call-keyword
        // positions as identifier prefixes are then caught downstream by the proc-call / source-ref
        // establishment gate, which requires the same file to first establish `#name` via
        // `CREATE TABLE #x`, `SELECT ... INTO #x`, a mutation target of `#x`, or
        // `CREATE PROCEDURE|FUNCTION #x`. `CALL`, `PROCEDURE`, `PROC`, and `FUNCTION` are added so
        // established temp routines stay callable (`CREATE PROCEDURE #sp ... CALL #sp;`) without
        // reopening the unestablished MySQL-comment regression.
        // issue #656: T-SQL でオブジェクト名の直前に来るキーワードの後ろでは `#name` を識別子として
        // 残し、`EXEC` / `EXECUTE` / `CALL` や FROM/JOIN 側の establishment ゲートに判定を委ねる。
        // `CREATE TABLE #x` / `SELECT ... INTO #x` / `#x` を変更対象とする更新 / `CREATE PROCEDURE|FUNCTION #x`
        // のいずれかで同一ファイル内に establish した `#name` だけを後段で有効な edge として残す。
        // `CALL` / `PROCEDURE` / `PROC` / `FUNCTION` を加えるのは、establish した一時ストアドを
        // `CREATE PROCEDURE #sp ... CALL #sp;` のように呼び戻せるようにするためで、非 establish の
        // MySQL コメント誤索引は establishment ゲートで引き続き抑止する。
        return !string.Equals(token, "FROM", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "JOIN", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "MERGE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "USING", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "INTO", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "UPDATE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "TABLE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "EXEC", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "EXECUTE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "CALL", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "PROCEDURE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "PROC", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "FUNCTION", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindLastSqlCommaOutsideQuotedIdentifiers(string text)
    {
        int lastComma = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                int closing = FindClosingSqlDoubleQuote(text, i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == '`')
            {
                int closing = text.IndexOf('`', i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == '[')
            {
                int closing = text.IndexOf(']', i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == ',')
                lastComma = i;
        }

        return lastComma;
    }

    private static string ReplaceRegexMatchesWithSpaces(Regex regex, string input)
    {
        return regex.Replace(input, static match => match.Length == 0 ? string.Empty : new string(' ', match.Length));
    }

    private static string PrepareLine(string lang, string line)
    {
        var result = lang == "python"
            ? MaskPythonSingleLineFStrings(line)
            : line;
        if (lang != "cobol")
            result = StringLiteralRegex.Replace(result, "\"\"");
        result = InlineBlockCommentRegex.Replace(result, " ");

        if (UsesHashComments(lang))
        {
            var hashIndex = result.IndexOf('#');
            if (hashIndex >= 0)
                result = result[..hashIndex];
        }

        if (UsesSlashComments(lang))
        {
            var slashIndex = result.IndexOf("//", StringComparison.Ordinal);
            if (slashIndex >= 0)
                result = result[..slashIndex];
        }

        // Lua, SQL, Haskell use -- for line comments / Lua、SQL、Haskell は -- を行コメントに使う
        if (UsesDashDashComments(lang))
        {
            var dashCommentIndex = result.IndexOf("--", StringComparison.Ordinal);
            if (dashCommentIndex >= 0)
                result = result[..dashCommentIndex];
        }

        // VB.NET uses Rem and ' for line comments / VB.NET は Rem と ' を行コメントに使う
        if (lang is "vb")
        {
            var remCommentMatch = VisualBasicRemCommentRegex.Match(result);
            if (remCommentMatch.Success)
                result = result[..remCommentMatch.Index];

            var vbCommentIndex = result.IndexOf('\'');
            if (vbCommentIndex >= 0)
                result = result[..vbCommentIndex];
        }

        return result;
    }

    private static readonly Regex VisualBasicRemCommentRegex = new(
        @"(?:^|:)\s*Rem\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string MaskPythonSingleLineFStrings(string line)
    {
        if (line.IndexOf('f') < 0 && line.IndexOf('F') < 0)
            return line;

        var masked = line.ToCharArray();
        for (var i = 0; i < line.Length; i++)
        {
            if (!TryOpenPythonSingleLineString(line, i, out var prefixLength, out var quoteChar, out var isRaw, out var isFString))
                continue;

            if (!isFString)
            {
                i += prefixLength;
                continue;
            }

            var quoteStart = i + prefixLength;
            var openingLength = prefixLength + 1;
            ReplaceWithSpaces(masked, i, openingLength);
            i += openingLength;

            var inExpression = false;
            var expressionDepth = 0;
            while (i < line.Length)
            {
                if (!inExpression)
                {
                    if (!isRaw && line[i] == '\\' && i + 1 < line.Length)
                    {
                        ReplaceWithSpaces(masked, i, 2);
                        i += 2;
                        continue;
                    }

                    if (line[i] == '{' && i + 1 < line.Length && line[i + 1] == '{')
                    {
                        ReplaceWithSpaces(masked, i, 2);
                        i += 2;
                        continue;
                    }

                    if (line[i] == '}' && i + 1 < line.Length && line[i + 1] == '}')
                    {
                        ReplaceWithSpaces(masked, i, 2);
                        i += 2;
                        continue;
                    }

                    if (line[i] == '{')
                    {
                        masked[i] = ' ';
                        inExpression = true;
                        expressionDepth = 1;
                        i++;
                        continue;
                    }

                    if (line[i] == quoteChar)
                    {
                        masked[i] = ' ';
                        i++;
                        break;
                    }

                    masked[i] = ' ';
                    i++;
                    continue;
                }

                if (line[i] == '{')
                {
                    expressionDepth++;
                    i++;
                    continue;
                }

                if (line[i] == '}')
                {
                    expressionDepth--;
                    masked[i] = ' ';
                    i++;
                    if (expressionDepth == 0)
                        inExpression = false;
                    continue;
                }

                if (line[i] == '\'' || line[i] == '"')
                {
                    var nestedQuote = line[i];
                    i++;
                    while (i < line.Length)
                    {
                        if (line[i] == '\\' && i + 1 < line.Length)
                        {
                            i += 2;
                            continue;
                        }

                        if (line[i] == nestedQuote)
                        {
                            i++;
                            break;
                        }

                        i++;
                    }

                    // Leave nested string contents in place; the generic string regex
                    // masks them later while the outer f-string wrapper is already gone.
                    // ネスト文字列は内容を残す。外側 f-string の殻を先に除去しておき、
                    // 内側の generic string regex で後からまとめてマスクする。
                    continue;
                }

                if (line[i] == '#')
                {
                    masked[i] = ' ';
                    i++;
                    continue;
                }

                i++;
            }

            i = Math.Max(i - 1, quoteStart);
        }

        return new string(masked);
    }

    private static void ReplaceWithSpaces(char[] buffer, int start, int length)
    {
        for (var i = start; i < start + length && i < buffer.Length; i++)
            buffer[i] = ' ';
    }

    private static bool TryOpenPythonSingleLineString(
        string line,
        int startIndex,
        out int prefixLength,
        out char quoteChar,
        out bool isRaw,
        out bool isFString)
    {
        prefixLength = 0;
        quoteChar = '\0';
        isRaw = false;
        isFString = false;

        if (startIndex < 0 || startIndex >= line.Length)
            return false;

        if (startIndex > 0 && IsIdentifierChar(line[startIndex - 1]))
            return false;

        var p = startIndex;
        var prefixChars = 0;
        while (p < line.Length && prefixChars < 2 && IsPythonStringPrefixChar(line[p]))
        {
            if (line[p] is 'r' or 'R')
                isRaw = true;
            if (line[p] is 'f' or 'F')
                isFString = true;
            p++;
            prefixChars++;
        }

        if (p >= line.Length || (line[p] != '\'' && line[p] != '"'))
            return false;
        if (p + 2 < line.Length && line[p] == line[p + 1] && line[p] == line[p + 2])
            return false;

        prefixLength = p - startIndex;
        quoteChar = line[p];
        return true;
    }

    private static bool IsIgnoredCallName(string language, string name)
    {
        if (LanguageSpecificCallNameKeeps.TryGetValue(language, out var languageSpecificKeepNames)
            && languageSpecificKeepNames.Contains(name))
        {
            return false;
        }

        if (language == "php")
        {
            if (SharedIgnoredCallNamesCaseInsensitive.Contains(name))
                return true;
        }
        else if (SharedIgnoredCallNames.Contains(name))
        {
            return true;
        }

        return LanguageSpecificIgnoredCallNames.TryGetValue(language, out var languageSpecificIgnoredNames)
            && languageSpecificIgnoredNames.Contains(name);
    }

    private static bool IsRubyIdentifierStart(char ch) => char.IsLetter(ch) || ch == '_';

    private static string NormalizeRubyCommandTargetToken(string token)
    {
        if (token.Length == 0)
            return token;

        if (token[0] == ':')
        {
            token = token[1..];
            if (token.Length >= 2
                && ((token[0] == '\'' && token[^1] == '\'')
                    || (token[0] == '"' && token[^1] == '"')))
            {
                token = token[1..^1];
            }
        }
        else if (token.Length >= 2
            && ((token[0] == '\'' && token[^1] == '\'')
                || (token[0] == '"' && token[^1] == '"')))
        {
            token = token[1..^1];
        }

        return token;
    }

    private static bool IsConstructorCallName(string language, string preparedLine, int nameIndex)
    {
        var probe = nameIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;

        if (probe < 0)
            return false;

        while (probe >= 0)
        {
            char? separator = null;
            if (probe >= 1 && preparedLine[probe] == ':' && preparedLine[probe - 1] == ':')
            {
                separator = ':';
                probe -= 2;
            }
            else if (preparedLine[probe] is '.' or '\\')
            {
                separator = preparedLine[probe];
                probe--;
            }

            if (separator == null)
                break;

            var segmentEnd = probe;
            while (probe >= 0 && IsIdentifierChar(preparedLine[probe]))
                probe--;

            var consumedSegment = segmentEnd >= 0 && segmentEnd >= probe + 1;
            if (!consumedSegment && separator != '\\')
                return false;
        }

        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;

        if (probe < 0)
            return false;

        var tokenEnd = probe;
        while (probe >= 0 && IsIdentifierChar(preparedLine[probe]))
            probe--;

        var tokenStart = probe + 1;
        if (tokenStart > tokenEnd)
            return false;

        var token = preparedLine[tokenStart..(tokenEnd + 1)];
        return language == "php"
            ? string.Equals(token, "new", StringComparison.OrdinalIgnoreCase)
            : string.Equals(token, "new", StringComparison.Ordinal);
    }

    private static bool IsTrailingLambdaInheritanceClause(string preparedLine, int nameIndex)
    {
        var probe = nameIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;

        return probe >= 0 && preparedLine[probe] == ':';
    }

    private static bool NextNonEmptyPreparedLineStartsWithJsContinuation(string[] preparedLines, int currentLineIndex)
    {
        for (var next = currentLineIndex + 1; next < preparedLines.Length; next++)
        {
            var trimmed = preparedLines[next].TrimStart();
            if (trimmed.Length == 0)
                continue;

            return trimmed.StartsWith(".", StringComparison.Ordinal)
                || trimmed.StartsWith("?.", StringComparison.Ordinal)
                || trimmed.StartsWith("[", StringComparison.Ordinal)
                || trimmed.StartsWith("(", StringComparison.Ordinal);
        }

        return false;
    }

    private readonly record struct NestedGenericCallCandidate(string Name, int NameIndex);

    private static IEnumerable<NestedGenericCallCandidate> EnumerateNestedGenericCallCandidates(
        string preparedLine,
        HashSet<int> matchedCallIndices)
    {
        for (var i = 0; i < preparedLine.Length; i++)
        {
            if (!IsAtAwareAsciiIdentifierStart(preparedLine, i))
                continue;
            if (i > 0 && (IsIdentifierChar(preparedLine[i - 1]) || preparedLine[i - 1] == '$' || preparedLine[i - 1] == '@'))
                continue;

            var nameStart = i;
            i = ConsumeAtAwareAsciiIdentifier(preparedLine, i);

            if (matchedCallIndices.Contains(nameStart))
            {
                i--;
                continue;
            }

            var scan = i;
            if (scan + 1 < preparedLine.Length
                && preparedLine[scan] == '?'
                && preparedLine[scan + 1] == '.')
            {
                scan += 2;
            }

            if (scan >= preparedLine.Length || preparedLine[scan] != '<')
            {
                i--;
                continue;
            }

            if (!TrySkipBalancedGenericArgs(preparedLine, ref scan, out var sawNestedGeneric) || !sawNestedGeneric)
            {
                i--;
                continue;
            }

            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (scan < preparedLine.Length && preparedLine[scan] == '(')
                yield return new NestedGenericCallCandidate(preparedLine[nameStart..i], nameStart);

            i--;
        }
    }

    private static IEnumerable<NestedGenericCallCandidate> EnumerateNestedGenericInitializerCandidates(
        string preparedLine,
        HashSet<int> matchedInitializerIndices,
        bool requireOpeningBrace)
    {
        for (var i = 0; i < preparedLine.Length; i++)
        {
            if (!IsStandaloneNewKeyword(preparedLine, i))
                continue;

            var scan = i + 3;
            if (!TryReadQualifiedTypeName(preparedLine, ref scan, out var name, out var nameIndex))
            {
                i += 2;
                continue;
            }

            if (matchedInitializerIndices.Contains(nameIndex))
            {
                i = scan - 1;
                continue;
            }

            if (!TrySkipBalancedGenericArgs(preparedLine, ref scan, out var sawNestedGeneric) || !sawNestedGeneric)
            {
                i = scan - 1;
                continue;
            }

            if (!TrySkipArraySuffixes(preparedLine, ref scan))
            {
                i = scan - 1;
                continue;
            }

            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (requireOpeningBrace)
            {
                if (scan < preparedLine.Length && preparedLine[scan] == '{')
                    yield return new NestedGenericCallCandidate(name, nameIndex);
            }
            else if (scan == preparedLine.Length)
            {
                yield return new NestedGenericCallCandidate(name, nameIndex);
            }

            i = scan - 1;
        }
    }

    private static bool TryReadQualifiedTypeName(
        string preparedLine,
        ref int scan,
        out string name,
        out int nameIndex)
    {
        name = string.Empty;
        nameIndex = -1;

        while (true)
        {
            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (scan >= preparedLine.Length || !IsAtAwareAsciiIdentifierStart(preparedLine, scan))
                return false;

            var segmentStart = scan;
            scan = ConsumeAtAwareAsciiIdentifier(preparedLine, scan);

            name = preparedLine[segmentStart..scan];
            nameIndex = segmentStart;

            var separatorScan = scan;
            while (separatorScan < preparedLine.Length && char.IsWhiteSpace(preparedLine[separatorScan]))
                separatorScan++;

            if (separatorScan + 1 < preparedLine.Length
                && preparedLine[separatorScan] == ':'
                && preparedLine[separatorScan + 1] == ':')
            {
                scan = separatorScan + 2;
                continue;
            }

            if (separatorScan < preparedLine.Length && preparedLine[separatorScan] == '.')
            {
                scan = separatorScan + 1;
                continue;
            }

            scan = separatorScan;
            return true;
        }
    }

    private static bool TrySkipArraySuffixes(string preparedLine, ref int scan)
    {
        while (true)
        {
            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (scan >= preparedLine.Length || preparedLine[scan] != '[')
                return true;

            scan++;
            while (scan < preparedLine.Length && preparedLine[scan] != ']')
                scan++;

            if (scan >= preparedLine.Length || preparedLine[scan] != ']')
                return false;

            scan++;
        }
    }

    private static bool ShouldSkipInitializerName(string language, string name) =>
        (language == "csharp" && CSharpBuiltInTypeNames.Contains(name))
        || (language == "java" && JavaPrimitiveTypeNames.Contains(name))
        || IsIgnoredCallName(language, name);

    private static bool IsStandaloneNewKeyword(string preparedLine, int index)
    {
        if (index < 0 || index + 3 > preparedLine.Length)
            return false;
        if (preparedLine[index] != 'n'
            || preparedLine[index + 1] != 'e'
            || preparedLine[index + 2] != 'w')
        {
            return false;
        }

        if (index > 0 && IsIdentifierChar(preparedLine[index - 1]))
            return false;

        return index + 3 >= preparedLine.Length || !IsIdentifierChar(preparedLine[index + 3]);
    }

    private static bool TrySkipBalancedGenericArgs(string preparedLine, ref int scan, out bool sawNestedGeneric)
    {
        sawNestedGeneric = false;
        if (scan >= preparedLine.Length || preparedLine[scan] != '<')
            return false;

        var depth = 0;
        while (scan < preparedLine.Length)
        {
            var ch = preparedLine[scan++];
            if (ch == '<')
            {
                depth++;
                if (depth > 1)
                    sawNestedGeneric = true;
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

    private static bool IsAsciiIdentifierStartChar(char ch) =>
        ch == '_' || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    private static bool IsAtAwareAsciiIdentifierStart(string text, int index)
    {
        if (index < 0 || index >= text.Length)
            return false;

        if (text[index] == '@')
            return index + 1 < text.Length && IsAsciiIdentifierStartChar(text[index + 1]);

        return IsAsciiIdentifierStartChar(text[index]);
    }

    private static int ConsumeAtAwareAsciiIdentifier(string text, int startIndex)
    {
        var index = startIndex;
        if (index < text.Length && text[index] == '@')
            index++;

        if (index >= text.Length || !IsAsciiIdentifierStartChar(text[index]))
            return startIndex;

        index++;
        while (index < text.Length && IsIdentifierChar(text[index]))
            index++;

        return index;
    }

    private static bool IsIdentifierChar(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_';

    /// <summary>
    /// Classify a call-looking identifier as an attribute/annotation when it appears inside
    /// a C# `[...]` attribute list or is preceded by a Java-family `@` marker. Returns null
    /// for ordinary method calls so the caller emits the default `call` reference kind.
    /// 呼び出しに見える識別子を、C# の `[...]` 属性リスト内や Java 系 `@` 付き注釈に該当する
    /// 場合に専用の reference kind へ分類する。通常の呼び出しは null を返して既定の `call` を維持する。
    /// </summary>
    private static string? TryClassifyMetadataReference(
        string language,
        string preparedLine,
        int nameIndex,
        bool insideCSharpAttributeRange)
    {
        if (language == "csharp")
            return insideCSharpAttributeRange ? "attribute" : null;

        if (nameIndex >= 0
            && nameIndex < preparedLine.Length
            && preparedLine[nameIndex] == '@'
            && AnnotationLanguages.Contains(language))
        {
            return "annotation";
        }

        var probe = nameIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;
        if (probe < 0)
            return null;

        if (AnnotationLanguages.Contains(language))
            return IsAnnotationContext(preparedLine, probe) ? "annotation" : null;

        return null;
    }

    /// <summary>
    /// Build per-line column ranges that identify C# `[...]` attribute sections. Handles
    /// declaration-position detection (including parameter attributes preceded by `(` / `,`
    /// via forward look-ahead) and multi-line `[\n ... \n]` sections. Each inner list holds
    /// ordered `(startColumn, endColumnExclusive)` ranges that are inside an attribute section
    /// on that line. Call sites whose name column falls inside one of these ranges are
    /// reclassified as `attribute` instead of `call`.
    /// C# の `[...]` 属性セクションを行ごとの列範囲で表すテーブルを構築する。
    /// `(` / `,` の直後に置かれるパラメータ属性を forward lookahead で、複数行にわたる
    /// `[\n ... \n]` 属性を跨行トラッキングで検出する。各行のリストは属性セクションに含まれる
    /// `(開始列, 終端列 (exclusive))` のレンジを保持し、呼び出し名の列がどれかのレンジに含まれる場合に
    /// `call` ではなく `attribute` へ再分類する。
    /// </summary>
    private static (List<List<(int start, int end)>>, List<List<(int start, int end)>>) BuildCSharpAttributeRanges(string[] preparedLines)
    {
        var perLine = new List<List<(int, int)>>(preparedLines.Length);
        var perLineTopLevel = new List<List<(int, int)>>(preparedLines.Length);
        for (var i = 0; i < preparedLines.Length; i++)
        {
            perLine.Add(new List<(int, int)>());
            perLineTopLevel.Add(new List<(int, int)>());
        }

        // Stack entries capture the opening `[` position, whether that bracket was at
        // a C# declaration (attribute) position, and a snapshot of the global paren depth
        // at that moment. The snapshot lets us compute an attribute-section-local paren
        // depth (`parenDepth - parenDepthAtOpen`), which is what the top-level zone tracking
        // uses so that parameter attributes like `void M([Attr] int x)` still have their
        // attribute-list top level at section-local depth 0 even though the global depth
        // is inside the method's parameter list.
        // スタックは `[` の位置、その bracket が属性位置だったか、および開いた瞬間の
        // グローバル paren 深さのスナップショットを保持する。スナップショットを使うと
        // 属性セクション内ローカルの paren 深さ (`parenDepth - parenDepthAtOpen`) が
        // 得られるので、`void M([Attr] int x)` のように外側の method 引数リストの中で
        // 開く属性セクションでも、セクション内では top-level (local depth 0) として扱える。
        var bracketStack = new Stack<(int li, int ci, bool isAttr, int parenDepthAtOpen)>();
        char lastMeaningful = '\0';
        int parenDepth = 0;
        bool lastClosedBracketWasAttribute = false;

        // Top-level zone tracking: while we are inside an attribute section and the paren
        // depth is at the section's open snapshot (section-local depth 0), the current zone
        // span is open. When parens open inside the section we close it; when they fully
        // close again we reopen. When the attribute section itself closes, we emit the span.
        // top-level ゾーン追跡: 属性セクション内かつセクションローカルの paren 深さが 0 の
        // あいだだけゾーンを開いておき、セクション内の `(` で閉じ、`)` で再び開く。
        // セクションが閉じる `]` で確定させる。
        int topZoneStartLi = -1;
        int topZoneStartCi = 0;

        void EmitTopZone(int endLi, int endCi)
        {
            if (topZoneStartLi < 0)
                return;
            for (var l = topZoneStartLi; l <= endLi; l++)
            {
                int s = (l == topZoneStartLi) ? topZoneStartCi : 0;
                int e = (l == endLi) ? endCi : preparedLines[l].Length;
                if (e > s)
                    perLineTopLevel[l].Add((s, e));
            }
            topZoneStartLi = -1;
        }

        for (var li = 0; li < preparedLines.Length; li++)
        {
            var line = preparedLines[li];
            for (var ci = 0; ci < line.Length; ci++)
            {
                var c = line[ci];
                if (char.IsWhiteSpace(c))
                    continue;

                if (c == '(')
                {
                    // If the innermost enclosing bracket is an attribute section and we are
                    // currently at that section's local top level, close the top-level zone
                    // just before the `(`. Use the stack top's `parenDepthAtOpen` snapshot so
                    // parameter attributes inside an outer `(...)` still get their top level
                    // tracked correctly.
                    // 直近の `[` が属性セクションで、かつその section-local 深さで top-level のとき、
                    // `(` 直前でゾーンを閉じる。外側の `(...)` の中で開く属性セクションにも対応するため、
                    // グローバル depth ではなくスタック top の開いたときの snapshot と比較する。
                    if (bracketStack.Count > 0)
                    {
                        var top = bracketStack.Peek();
                        if (top.isAttr && parenDepth == top.parenDepthAtOpen && topZoneStartLi >= 0)
                            EmitTopZone(li, ci);
                    }
                    parenDepth++;
                    lastMeaningful = c;
                    continue;
                }
                if (c == ')')
                {
                    if (parenDepth > 0)
                    {
                        parenDepth--;
                        // If the innermost `[` is an attribute section and we just returned
                        // to that section's local top level, reopen the top-level zone.
                        // 直近の `[` が属性セクションで、section-local top-level に戻ってきたら
                        // top-level ゾーンを再開する。
                        if (bracketStack.Count > 0)
                        {
                            var top = bracketStack.Peek();
                            if (top.isAttr && parenDepth == top.parenDepthAtOpen && topZoneStartLi < 0)
                            {
                                topZoneStartLi = li;
                                topZoneStartCi = ci + 1;
                            }
                        }
                    }
                    lastMeaningful = c;
                    continue;
                }

                if (c == '[')
                {
                    bool isAttr = EvaluateCSharpAttributePosition(
                        lastMeaningful, lastClosedBracketWasAttribute, preparedLines, li, ci);
                    bracketStack.Push((li, ci, isAttr, parenDepth));
                    if (isAttr && topZoneStartLi < 0)
                    {
                        // Start top-level zone just after the `[` so the `[` itself is not
                        // inside the zone. Section-local depth is 0 by construction at the
                        // open bracket.
                        // `[` 直後から top-level ゾーンを開始する。開いた瞬間は section-local 深さ 0。
                        topZoneStartLi = li;
                        topZoneStartCi = ci + 1;
                    }
                    lastMeaningful = c;
                    continue;
                }

                if (c == ']')
                {
                    if (bracketStack.Count > 0)
                    {
                        var opened = bracketStack.Pop();
                        lastClosedBracketWasAttribute = opened.isAttr;
                        if (opened.isAttr)
                        {
                            // Record the attribute section span for every line it covers so
                            // cross-line `[\n Foo("x")\n]` also classifies `Foo` as attribute.
                            // 属性セクションがまたぐ全ての行に対して範囲を記録し、
                            // `[\n Foo("x")\n]` のような跨行ケースでも `Foo` が属性として分類されるようにする。
                            for (var l = opened.li; l <= li; l++)
                            {
                                int s = (l == opened.li) ? opened.ci : 0;
                                int e = (l == li) ? ci + 1 : preparedLines[l].Length;
                                perLine[l].Add((s, e));
                            }
                            // Close the top-level zone at the `]`. Section-local depth should
                            // be 0 here (we are at the closing bracket of this section) — if
                            // it is not, we drop the open zone because paren balancing was
                            // malformed.
                            // `]` で top-level ゾーンを確定する。section-local 深さが 0 のはず。
                            // 不整合入力ならゾーンを捨てる。
                            if (parenDepth == opened.parenDepthAtOpen)
                            {
                                EmitTopZone(li, ci + 1);
                            }
                            else
                            {
                                topZoneStartLi = -1;
                            }
                        }
                    }
                    else
                    {
                        lastClosedBracketWasAttribute = false;
                    }
                    lastMeaningful = c;
                    continue;
                }

                lastMeaningful = c;
            }
        }

        return (perLine, perLineTopLevel);
    }

    /// <summary>
    /// Decide whether a `[` token sits at a C# attribute position based on the immediately
    /// preceding meaningful character. `(` / `,` (parameter attributes) are disambiguated via
    /// forward look-ahead because both attributes and C# 12 collection expressions can follow.
    /// `[` が C# の属性位置にあるかを、直前の非空白文字から判定する。`(` / `,` の直後は
    /// パラメータ属性にも collection expression にもなりうるため、forward lookahead で区別する。
    /// </summary>
    private static bool EvaluateCSharpAttributePosition(
        char lastMeaningful,
        bool lastClosedBracketWasAttribute,
        string[] preparedLines,
        int startLi,
        int startCi)
    {
        // Start of file or after a scope/statement boundary — attribute position.
        // ファイル先頭、あるいはスコープ・文境界の直後は属性位置。
        if (lastMeaningful is '\0' or '{' or '}' or ';')
            return true;

        // Chained attribute list `[A][B]`: the prior `]` must have closed an attribute section.
        // `arr[i][Compute()]` → the prior `]` closed an indexer, so stays `call`.
        // 連続した属性リスト `[A][B]` は、直前の `]` が属性セクションを閉じていたときのみ属性扱い。
        // `arr[i][Compute()]` の `]` は indexer を閉じているため `call` のまま。
        if (lastMeaningful == ']')
            return lastClosedBracketWasAttribute;

        // Parameter / type-parameter / lambda attribute candidates (`(`, `,`, `<`, `=`):
        // `void M([Attr] T x)`, `class C<[Attr] T>`, `var f = [Attr] () => body`, or
        // `Consume([Make()])`. Disambiguate by scanning forward to the matching `]` and
        // checking whether the next meaningful token begins a declaration (identifier /
        // `@` / `(` for tuple types or lambda parameter lists / `[` chained).
        // パラメータ / 型パラメータ / ラムダ属性候補 (`(`, `,`, `<`, `=`) は
        // `void M([Attr] T x)`・`class C<[Attr] T>`・`var f = [Attr] () => body`・
        // `Consume([Make()])` いずれにもなりうる。対応する `]` まで進んで次トークンが
        // 宣言やラムダを開始するか（識別子 / `@` / tuple・ラムダ仮引数の `(` / chained `[`）で区別する。
        if (lastMeaningful is '(' or ',' or '<' or '=')
            return IsCSharpAttributeFollowedByDeclaration(preparedLines, startLi, startCi);

        return false;
    }

    /// <summary>
    /// Keywords that indicate the preceding `[...]` is an expression (collection / pattern /
    /// switch target) rather than an attribute section when they appear after `]`.
    /// `]` の直後に現れると、直前の `[...]` が属性ではなく式（collection / pattern / switch 対象）
    /// であることを示す C# のキーワード集合。
    /// </summary>
    private static readonly HashSet<string> CSharpExpressionContinuationKeywords = new(StringComparer.Ordinal)
    {
        "is", "as", "switch", "with", "when",
    };

    /// <summary>
    /// Scan forward from a `[` to its matching `]` (skipping balanced parens) and return true
    /// when the next meaningful character begins an identifier-like token. Works across lines so
    /// `void M(\n    [Attr]\n    T x\n)` is recognized as a parameter attribute.
    /// `[` から対応する `]` まで進んで、`]` の次の非空白文字が識別子を始める場合に true を返す。
    /// 行を跨ぐ走査にも対応しているため `void M(\n    [Attr]\n    T x\n)` も属性として認識される。
    /// </summary>
    private static bool IsCSharpAttributeFollowedByDeclaration(string[] preparedLines, int startLi, int startCi)
    {
        var bracketDepth = 1;
        var parenDepth = 0;
        var li = startLi;
        var ci = startCi + 1;
        while (li < preparedLines.Length)
        {
            var line = preparedLines[li];
            while (ci < line.Length)
            {
                var c = line[ci];
                if (c == '(')
                {
                    parenDepth++;
                    ci++;
                    continue;
                }
                if (c == ')')
                {
                    if (parenDepth > 0)
                        parenDepth--;
                    ci++;
                    continue;
                }
                if (parenDepth > 0)
                {
                    ci++;
                    continue;
                }
                if (c == '[')
                {
                    bracketDepth++;
                    ci++;
                    continue;
                }
                if (c == ']')
                {
                    bracketDepth--;
                    if (bracketDepth == 0)
                    {
                        ci++;
                        return NextTokenStartsDeclaration(preparedLines, li, ci);
                    }
                    ci++;
                    continue;
                }
                ci++;
            }
            li++;
            ci = 0;
        }
        return false;
    }

    /// <summary>
    /// After the closing `]` of a candidate `[...]`, inspect the next meaningful token to decide
    /// whether it begins a declaration. Accepts identifiers (except expression-continuation
    /// keywords like `is` / `as` / `switch` / `with` / `when`), leading `@` (verbatim identifier),
    /// `(` (tuple-typed parameter), and chained `[` (recurse for `[A][B]`).
    /// 閉じ `]` の直後のトークンで宣言が始まるかを判定する。識別子（式継続の `is` / `as` /
    /// `switch` / `with` / `when` は除外）、`@`（verbatim 識別子）、`(`（tuple パラメータ型）、
    /// `[`（`[A][B]` の連結）を受け入れる。
    /// </summary>
    private static bool NextTokenStartsDeclaration(string[] preparedLines, int li, int ci)
    {
        while (li < preparedLines.Length)
        {
            var line = preparedLines[li];
            while (ci < line.Length && char.IsWhiteSpace(line[ci]))
                ci++;
            if (ci < line.Length)
            {
                var first = line[ci];
                if (first == '@' || first == '(')
                    return true;
                if (first == '[')
                    return IsCSharpAttributeFollowedByDeclaration(preparedLines, li, ci);
                if (!IsIdentifierChar(first))
                    return false;
                var start = ci;
                while (ci < line.Length && IsIdentifierChar(line[ci]))
                    ci++;
                var token = line.Substring(start, ci - start);
                return !CSharpExpressionContinuationKeywords.Contains(token);
            }
            li++;
            ci = 0;
        }
        return false;
    }

    private static bool IsInsideCSharpAttributeRange(IReadOnlyList<(int start, int end)> ranges, int index)
    {
        foreach (var (start, end) in ranges)
        {
            if (index >= start && index < end)
                return true;
        }
        return false;
    }

    private static bool IsAnnotationContext(string line, int probe)
    {
        // `@Annotation(args)` — direct marker. 直接 `@Annotation(args)` の場合。
        if (line[probe] == '@')
            return true;

        // `@module.Annotation(args)` — walk past the dotted qualifier chain first so that
        // both `@module.Annotation` and `@field:com.example.Annotation` land the probe on
        // either `@` or the Kotlin use-site target `:`.
        // `@module.Annotation(args)` や `@field:com.example.Annotation(args)` のように修飾子が
        // 付く場合も対応するため、先にドット区切り修飾子チェーンを剥がしてから `@` または
        // Kotlin の use-site target `:` を判定する。
        while (probe >= 0 && line[probe] == '.')
        {
            probe--;
            while (probe >= 0 && IsIdentifierChar(line[probe]))
                probe--;
            while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                probe--;
        }

        if (probe < 0)
            return false;

        if (line[probe] == '@')
            return true;

        // Kotlin use-site target: `@field:Deprecated("msg")` or
        // `@field:com.example.Deprecated("msg")`. After unwinding the dotted qualifier, the
        // probe lands on `:`; walk past the target identifier and confirm `@`.
        // Kotlin の use-site target `@field:Deprecated("msg")` や
        // `@field:com.example.Deprecated("msg")` では、ドット修飾子を剥がしたあと probe が `:`
        // に着地するため、target 識別子を読み飛ばして `@` を確認する。
        if (line[probe] == ':')
        {
            var j = probe - 1;
            var idEnd = j;
            while (j >= 0 && IsIdentifierChar(line[j]))
                j--;
            if (j + 1 <= idEnd)
            {
                var target = line[(j + 1)..(idEnd + 1)];
                if (KotlinAnnotationTargets.Contains(target))
                {
                    var k = j;
                    while (k >= 0 && char.IsWhiteSpace(line[k]))
                        k--;
                    if (k >= 0 && line[k] == '@')
                        return true;
                }
            }
        }

        return false;
    }

    private static bool UsesHashComments(string lang) =>
        lang is "python" or "ruby" or "perl" or "php" or "elixir" or "r" or "powershell"
            or "shell" or "makefile" or "terraform" or "dockerfile" or "protobuf";

    private static bool UsesSlashComments(string lang) =>
        lang is not "python" and not "ruby" and not "r" and not "haskell"
            and not "makefile" and not "terraform" and not "dockerfile"
            and not "css";

    private static bool UsesDashDashComments(string lang) =>
        lang is "lua" or "sql" or "haskell";

    private static bool IsPythonStringPrefixChar(char c) =>
        c is 'r' or 'R' or 'u' or 'U' or 'b' or 'B' or 'f' or 'F';

    private static string MaskJavaTextBlocks(string content)
    {
        var chars = content.ToCharArray();

        for (var i = 0; i + 2 < chars.Length; i++)
        {
            if (!IsJavaTextBlockOpening(chars, i))
                continue;

            // Mask the body but keep line breaks so all existing line/column logic stays valid.
            i += 3;
            while (i < chars.Length)
            {
                if (i + 2 < chars.Length
                    && chars[i] == '"'
                    && chars[i + 1] == '"'
                    && chars[i + 2] == '"'
                    && !IsEscapedByBackslashes(chars, i))
                {
                    i += 2;
                    break;
                }

                if (chars[i] != '\r' && chars[i] != '\n')
                    chars[i] = ' ';
                i++;
            }
        }

        return new string(chars);
    }

    private static bool IsJavaTextBlockOpening(IReadOnlyList<char> chars, int index)
    {
        if (index + 2 >= chars.Count)
            return false;

        if (chars[index] != '"' || chars[index + 1] != '"' || chars[index + 2] != '"')
            return false;

        if (IsEscapedByBackslashes(chars, index))
            return false;

        for (var i = index + 3; i < chars.Count; i++)
        {
            var c = chars[i];
            if (c == '\r' || c == '\n')
                return true;
            if (!char.IsWhiteSpace(c))
                return false;
        }

        return true;
    }

    private static bool IsEscapedByBackslashes(IReadOnlyList<char> chars, int index)
    {
        var backslashCount = 0;
        for (var i = index - 1; i >= 0 && chars[i] == '\\'; i--)
            backslashCount++;

        return (backslashCount & 1) == 1;
    }

    /// <summary>
    /// Return true when a batch (.bat / .cmd) line is a comment, i.e. `::` / `:::` / `rem` /
    /// `@rem` (with optional leading whitespace and case-insensitive `rem`).
    /// batch (.bat / .cmd) のコメント行 (`::` / `:::` / `rem` / `@rem`、先頭空白可、`rem` は大小文字不問) のときに
    /// true を返す。
    /// </summary>
    private static bool IsBatchCommentLine(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;

        if (i >= line.Length)
            return false;

        if (line[i] == ':' && i + 1 < line.Length && line[i + 1] == ':')
            return true;

        if (line[i] == '@')
        {
            var j = i + 1;
            while (j < line.Length && (line[j] == ' ' || line[j] == '\t'))
                j++;
            return IsBatchRemKeyword(line, j);
        }

        return IsBatchRemKeyword(line, i);
    }

    private static bool IsBatchRemKeyword(string line, int start)
    {
        if (start + 3 > line.Length)
            return false;
        if ((line[start] | 0x20) != 'r')
            return false;
        if ((line[start + 1] | 0x20) != 'e')
            return false;
        if ((line[start + 2] | 0x20) != 'm')
            return false;
        if (start + 3 == line.Length)
            return true;
        var next = line[start + 3];
        return next == ' ' || next == '\t' || next == '\r' || next == '\n';
    }

    private static bool IsBatchCommandSeparator(string line, int index)
    {
        var c = line[index];
        if (c is not '&' and not '|')
            return false;

        var caretCount = 0;
        for (var i = index - 1; i >= 0 && line[i] == '^'; i--)
            caretCount++;

        return (caretCount & 1) == 0;
    }

    private static void ProcessBatchJumpSegment(
        string segment,
        int segmentOffset,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        string context,
        long fileId,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var trimmed = segment.TrimStart();
        if (trimmed.Length == 0)
            return;

        if (trimmed[0] == '@')
        {
            trimmed = trimmed[1..].TrimStart();
            if (trimmed.Length == 0)
                return;
        }

        if (trimmed.StartsWith("::", StringComparison.Ordinal) || IsBatchRemKeyword(trimmed, 0))
            return;

        if (StartsWithBatchWord(trimmed, "else") || StartsWithBatchWord(trimmed, "do"))
        {
            var keywordEnd = 0;
            while (keywordEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[keywordEnd]))
                keywordEnd++;
            while (keywordEnd < trimmed.Length && char.IsWhiteSpace(trimmed[keywordEnd]))
                keywordEnd++;
            if (keywordEnd >= trimmed.Length)
                return;
            trimmed = trimmed[keywordEnd..].TrimStart();
        }

        var match = BatchJumpTargetRegex.Match(trimmed);
        if (!match.Success)
            return;

        var name = match.Groups["name"].Value;
        if (string.Equals(name, "eof", StringComparison.OrdinalIgnoreCase))
            return;

        var commandIndex = segmentOffset + match.Groups["command"].Index;
        var callContainer = resolveContainerForCall(commandIndex);
        AddReference(references, seen, fileId, name, commandIndex, "call", context, lineNumber, callContainer);
    }

    private static bool StartsWithBatchWord(string text, string word)
    {
        if (!text.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            return false;
        return text.Length == word.Length || char.IsWhiteSpace(text[word.Length]);
    }
}
