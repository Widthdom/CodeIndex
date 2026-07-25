using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
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
    private static readonly Regex CSharpUsingNamespaceRegex = new(
        @"^\s*(?:global\s+)?using\s+(?!static\b)(?<target>[^;=]+?)\s*;?\s*$",
        RegexOptions.Compiled);
    private static readonly Regex CSharpUsingStaticRegex = new(
        @"^\s*(?:global\s+)?using\s+static\s+(?<target>[^;]+)",
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
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
        "java", "kotlin", "scala", "typescript", "javascript", "swift", "gradle", "groovy", "dart",
    };

    // Kotlin use-site target prefixes for annotations (e.g. `@field:Deprecated("msg")`,
    // `@file:JvmName("Foo")`). Keep aligned with the Kotlin language spec use-site targets.
    // Kotlin の use-site target 付き注釈用の接頭辞。
    private static readonly HashSet<string> KotlinAnnotationTargets = new(StringComparer.Ordinal)
    {
        "field", "get", "set", "param", "setparam", "property", "receiver", "file", "delegate", "all",
    };

}
