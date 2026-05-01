using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Extracts symbols (functions, classes, imports) using regex patterns.
/// 正規表現を使ってシンボル（関数、クラス、インポート）を抽出する。
/// </summary>
public static class SymbolExtractor
{
    private const string CSharpVisibilityPattern = @"protected\s+internal|private\s+protected|public|protected|internal|private";
    // Return-type character class includes `*` so pointer and function-pointer returns
    // (`int*`, `void**`, `delegate*<int, int>`, `int*[]`) are not silently dropped.
    // The trailing CSharpTupleSuffixPattern lets a tuple group carry suffixes
    // (`(int, int)[]`, `(int, int)?`, `(int, int)[][]`, `(int, int)[,]`, and whitespaced
    // variants like `(int, int) []` / `(int, int) ?`) so tuple-array and nullable-tuple
    // return types are captured on methods, properties, indexers, and explicit interface
    // implementations. The shared segment matcher also allows tuple groups inside generic
    // arguments (`Task<(int, int)>`, `Dictionary<string, (int x, int y)>`,
    // `List<(int, int)> IFoo.GetList()`), so ordinary methods and explicit-interface
    // implementations stay aligned. Delegate and event declarations with tuple-array returns
    // remain blocked by the pre-existing pattern-order issue (#340); the identifier branch
    // already absorbs non-tuple suffix characters via its char class, but keeping the suffix
    // loop outside both branches is harmless and makes the tuple branch's responsibilities
    // explicit.
    // 戻り値型のクラスに `*` を含め、ポインタ / 関数ポインタ戻り値型（`int*` / `void**` / `delegate*<int, int>` / `int*[]`）を取りこぼさない。
    // 末尾の CSharpTupleSuffixPattern で tuple 分岐にも `[]` / `?` / `[][]` / `[,]` と、
    // `(int, int) []` / `(int, int) ?` のような空白を挟んだ整形バリエーションまで許容し、
    // tuple-array / nullable-tuple 戻り値をメソッド・プロパティ・インデクサ・明示的
    // インターフェース実装で捕捉できるようにする。共有の segment matcher により
    // `Task<(int, int)>` / `Dictionary<string, (int x, int y)>` /
    // `List<(int, int)> IFoo.GetList()` のような generic-over-tuple も通常メソッドと
    // 明示的インターフェース実装の両方で同じ経路で扱える。delegate / event 宣言で
    // tuple-array 戻り値を扱う件は既存のパターン評価順問題 (#340) が残っており、この
    // ループの範囲外。識別子側の分岐は文字クラスに `[`/`]`/`?` を既に含むため無害な冗長だが、
    // tuple 分岐側の責務が明確になる。
    // Tuple / array / nullable suffix tokens that may trail a C# return type. Each iteration
    // matches a single `?` or a bracketed `[]` / `[,]` / `[,,]` group and allows whitespace
    // between the preceding `)` / identifier and the suffix token (the `\s*` sits inside the
    // group so a type with no suffix still matches zero iterations and consumes no
    // whitespace). Shared by CSharpTypePattern and the C# constructor regex negative
    // lookahead so legal formatting variants like `public required (int, int) [] R4 { ... }`
    // and `public readonly (int, int) ? M3() => default;` are both rejected as ctor shapes
    // (via the lookahead) and accepted as property / method shapes (via the upstream rows).
    // Closes #349 follow-up.
    // C# の戻り値型末尾に付きうる tuple / 配列 / nullable サフィックストークン列。各繰り返しは
    // `?` 1 個または `[]` / `[,]` / `[,,]` の bracket ブロック 1 個を受理し、先行する `)` や
    // 識別子とサフィックストークンの間に空白を許容する（`\s*` を繰り返しの内側に入れているため、
    // サフィックスを持たない型は 0 回繰り返しで一致し、空白を消費しない）。CSharpTypePattern と
    // C# コンストラクタ regex の否定先読みで共有し、`public required (int, int) [] R4 { ... }`
    // や `public readonly (int, int) ? M3() => default;` のような合法な整形を、
    // 否定先読みで ctor 形状として弾きつつ、上流の property / method 行で本来のシンボルとして
    // 拾えるようにする。#349 のフォローアップ。
    private const string CSharpTupleSuffixPattern = @"(?:\s*(?:\?|\[[\],\s]*\]))*";
    // Embedded tuple groups must contain a comma at the OUTER tuple level so ordinary
    // call/ctor parens (`Make()`, `Parent(value)`) keep falling through, while real tuple
    // segments inside generics can nest arbitrarily deep (`Task<((int A, int B), string Name)>`,
    // `Task<(((int A, int B), int C), string Name)>`). The balancing-group variant tracks nested
    // parens and only records commas seen at depth 0.
    // 埋め込み tuple group は最外 tuple レベルの comma を必須にし、`Make()` / `Parent(value)` の
    // ような通常の call/ctor 括弧列は従来どおり不一致に落としつつ、generic 内の実 tuple segment
    // は `Task<((int A, int B), string Name)>` / `Task<(((int A, int B), int C), string Name)>`
    // のような深い入れ子まで通せるようにする。balancing-group 版で入れ子括弧を追跡し、
    // 深さ 0 で見えた comma だけを tuple 判定に使う。
    private const string CSharpTupleGroupPattern =
        @"\((?>(?:[^(),]+|\((?<TupleDepth>)|\)(?<-TupleDepth>)|(?(TupleDepth),|(?<TupleComma>,))))*(?(TupleDepth)(?!))(?(TupleComma)|(?!))\)";
    private const string CSharpIdentifierPattern = @"@?[_\p{L}]\w*";
    private const string CSharpNamespacePattern = CSharpIdentifierPattern + @"(?:\." + CSharpIdentifierPattern + @")*";
    private const string CSharpTypeTokenCharsPattern = @"[\w@?.<>\[\],:*]";
    private const string SqlQualifiedIdentifierSegmentPattern = @"(?:\[(?:[^\]\r\n]|\]\])+\]|""[^""]+""|[\w$#]+)";
    private const string SqlQualifiedIdentifierPattern =
        @"(?:" + SqlQualifiedIdentifierSegmentPattern + @")(?:\s*\.\s*(?:" + SqlQualifiedIdentifierSegmentPattern + @"))*";
    private const string CSharpTypeSegmentPattern =
        @"(?:" + CSharpTypeTokenCharsPattern + @"+(?:" + CSharpTupleGroupPattern + CSharpTypeTokenCharsPattern + @"*)*|" + CSharpTupleGroupPattern + CSharpTypeTokenCharsPattern + @"*)";
    private const string CSharpTypePattern =
        @"(?:(?:global::)?(?:" + CSharpTypeSegmentPattern + @")(?:\s+(?:" + CSharpTypeSegmentPattern + @"))*" + CSharpTupleSuffixPattern + @")";
    private const string CSharpMethodTypeParameterListPattern =
        @"(?:<(?:(?>[^<>]+)|<(?<CSharpMethodTypeParameterDepth>)|>(?<-CSharpMethodTypeParameterDepth>))*(?(CSharpMethodTypeParameterDepth)(?!))>\s*)?";
    private const string JavaIdentifierPattern = @"[\p{L}_$][\p{L}\p{Nd}_$]*";
    private const string JavaQualifiedIdentifierPattern = JavaIdentifierPattern + @"(?:\s*\.\s*" + JavaIdentifierPattern + @")*";
    private const string JavaMethodTypeParameterPattern =
        @"(?:<(?:(?>[^<>]+)|<(?<JavaMethodTypeParameterDepth>)|>(?<-JavaMethodTypeParameterDepth>))*(?(JavaMethodTypeParameterDepth)(?!))>\s+)?";
    private const string JavaReturnTypePattern =
        @"(?:" + JavaQualifiedIdentifierPattern + @"(?:\s*<[^;=(){}]+>)?(?:\s*\[\s*\])*)";
    private static readonly Regex PhpGroupUseRegex = new(
        @"^\s*use\s+(?:(?<type>function|const)\s+)?(?<prefix>[\w\\]+\\)\{\s*(?<items>[^{}]+?)\s*\}\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PhpUseRegex = new(
        @"^\s*use\s+(?:(?<type>function|const)\s+)?(?<name>[\w\\]+)(?:\s+as\s+(?<alias>\w+))?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PhpRequireIncludeRegex = new(
        @"^\s*(?:require|include)(?:_once)?\s*\(?\s*(?:'(?<singleName>[^']+)'|""(?<doubleName>[^""]+)"")\s*\)?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // `delegate` is a non-type keyword only when it is NOT followed by `*` — `delegate*<...>` is a valid return type.
    // `delegate` は `*` を伴わないときだけ非型キーワード扱い。`delegate*<...>` は戻り値型として有効。
    private const string CSharpNonTypeKeywordPattern = @"(?:(?:public|private|protected|internal|static|sealed|partial|readonly|unsafe|extern|virtual|override|abstract|async|new|file|required|ref)\b|delegate\b(?!\s*\*))";
    private const string CFunctionStartBlacklistPattern = @"^(?!\s*typedef\b)(?!\s*(?:if|else|for|while|switch|return|sizeof)\s*[\(\{;])";
    private const string CFunctionNameBlacklistPattern = @"(?!(?:int|void|char|short|long|float|double|signed|unsigned|bool|_Bool|size_t|ssize_t|intptr_t|uintptr_t|int8_t|int16_t|int32_t|int64_t|uint8_t|uint16_t|uint32_t|uint64_t)\b)";
    private const string CppFunctionStartBlacklistPattern = @"^(?!\s*typedef\b)(?!\s*(?:if|else|for|while|switch|return|sizeof|using|namespace)\s*[\(\{;<])";
    private const string CppTemplatePrefixPattern = @"(?:template\s*<[^>]*>\s*)*";
    private static readonly Regex PartialModifierRegex = new(@"\bpartial\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GoImportSpecRegex = new(
        @"^(?<name>(?:(?:[._]|[\p{L}_][\p{L}\p{Nd}_]*)\s+)?""(?:\\.|[^""\\])*"")(?:\s*;)?(?:\s*(?://.*|/\*.*\*/))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XamlClassRegex = new(
        @"\bx:Class\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XamlNameRegex = new(
        @"\bx:Name\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    private enum BodyStyle
    {
        None,
        Brace,
        Indent,
        RubyEnd,
        ElixirEnd,
        VisualBasicEnd,
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
        string? PreviousIdentifier = null,
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
        // (Mode = Code with InterpolationBraceDepth > 0). Only one level of nesting
        // is tracked — matches the single-line TrySkipCSharpStringOrCharLiteral
        // approximation in DbSymbolReader.
        // 補間 verbatim / raw 文字列のホール追跡。IsInterpolated / InterpolationDollarCount は
        // 現在のモード（string 系モードのときだけ意味を持つ）を表し、Return* は
        // ホール内（Mode = Code かつ InterpolationBraceDepth > 0）の間、外側の
        // 補間文字列情報を退避する。ネストは 1 レベルのみで、単一行版
        // TrySkipCSharpStringOrCharLiteral と同等の近似とする。
        bool IsInterpolated = false,
        int InterpolationDollarCount = 0,
        int InterpolationBraceDepth = 0,
        CSharpLexMode InterpolationReturnMode = CSharpLexMode.Code,
        int InterpolationReturnRawDelimiterLength = 0,
        int InterpolationReturnDollarCount = 0);

    private readonly record struct CSharpLexedLine(
        string SanitizedLine,
        CSharpLexState EndState);

    private readonly record struct CSharpPropertyMatchCandidate(
        string MatchLine,
        int LastConsumedLineIndex,
        int SignatureLastLineIndex,
        int? SignatureLastLineExclusiveEndColumn = null,
        int? ExpressionBodyEndLineIndex = null);

    private enum CSharpAccessorProbeStatus
    {
        Pending,
        Found,
        Rejected
    }

    private readonly record struct RecordPrimaryComponent(
        string Name,
        string Type,
        string Signature,
        int Line,
        string? Visibility = null);

    private readonly record struct RecordPrimaryComponentSlice(
        string Text,
        int Line);

    private readonly record struct PendingRecordPrimaryComponents(
        long FileId,
        string Kind,
        string RecordName,
        int RecordStartLine,
        List<RecordPrimaryComponent> Components);

    private readonly record struct StrippedRecordComponentText(
        string Text,
        int ConsumedNewlines);

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
    private static readonly Regex CSharpEnumMemberRegex = new(@"^\s*(?<name>@?[_\p{L}]\w*)\s*(?:=\s*(?:-?\d|0x|@?[_\p{L}]\w*(?:\s*\|\s*@?[_\p{L}]\w*)*)[^""']*)?,?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpEnumMemberNameRegex = new(@"^\s*(?<name>@?[_\p{L}]\w*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JavaCompactConstructorRegex = new(
        @"^\s*(?:(?<visibility>public|private|protected)\s+)?(?<name>\w+)\s*(?=\{|$)",
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

    private static readonly Regex JavaScriptTypeScriptCommonJsNamedExportAssignmentRegex = new(
        $@"^\s*(?:module\.exports|exports)\.(?<name>{JavaScriptTypeScriptIdentifierPattern})(?:\s*:\s*[^=]+?)?\s*(?<![=!<>])=(?![=>])\s*(?<rhs>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptQualifiedAssignmentRegex = new(
        $@"^\s*(?<name>[A-Z][\w$]*(?:\.[\w$]+)+)\s*(?<![=!<>])=(?![=>])\s*(?<rhs>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptArrowAssignmentValueRegex = new(
        $@"^(?:async\s+)?(?:\([^)]*\)|{JavaScriptTypeScriptIdentifierPattern})\s*=>",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptExportedObjectLiteralPropertyRegex = new(
        $@"^\s*(?<name>{JavaScriptTypeScriptIdentifierPattern})\s*:",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptTypeScriptExportedObjectLiteralShorthandPropertyRegex = new(
        $@"^\s*(?<name>{JavaScriptTypeScriptIdentifierPattern})\s*(?:(?=,)|(?=}})|$)",
        RegexOptions.Compiled);

    private static readonly Regex SvelteReactivePropertyRegex = new(
        @"^\s*\$:\s*(?<name>\w+)\s*=",
        RegexOptions.Compiled);

    private const string VbVisibilityPattern = @"(?:Public|Private|Protected|Friend)(?:\s+(?:Protected|Friend))?";
    private const string VbTypeModifierPattern = @"(?:Partial|MustInherit|NotInheritable)";
    private const string VbMemberModifierPattern = @"(?:Shared|Overrides|Overridable|MustOverride|Async|Partial)";
    private const string VbPropertyModifierPattern = @"(?:Shared|Overrides|Overridable|MustOverride|Default|ReadOnly|WriteOnly)";
    private const string VbEventModifierPattern = @"(?:Shared|Custom)";

    private static readonly Dictionary<string, List<SymbolPattern>> PatternCache = new()
    {
        ["python"] =
        [
            new("function", new Regex(@"^\s*(?:async\s+)?def\s+(?<name>\w+)\s*(?:\[[^\]]*\])?\s*\(", RegexOptions.Compiled), BodyStyle.Indent),
            new("class",    new Regex(@"^\s*class\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Indent),
            new("import",   new Regex(@"^\s*type\s+(?<name>\w+)\s*(?:\[[^\]]*\])?\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*(?:from\s+(?<name>[\w.]+)\s+import\b|import\s+(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["javascript"] =
        [
            // Include optional `*` between `function` and name for generator functions (e.g. `function* gen()`, `async function* asyncGen()`)
            // `function` と名前の間に任意の `*` を許容し、ジェネレータ関数 (`function* gen()`, `async function* asyncGen()`) にも対応
            new("function", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:async\s+)?function(?:\s+|\s*\*\s*)(?<name>\w+)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:const|let|var)\s+(?<name>\w+)\s*=\s*(?:async\s+)?(?:\([^)]*\)|[^=])\s*=>", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:const|let|var)\s+(?<name>\w+)\s*(?::\s*.+?)?\s*=\s*(?:async\s+)?function(?:\s+|\s*\*\s*)(?:\w+)?\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
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
            new("function", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:declare\s+)?(?:async\s+)?function(?:\s+|\s*\*\s*)(?<name>\w+)\s*[\(<]", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("import", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:declare\s+)?type\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("property", new Regex(@"^\s*(?:(?<visibility>export)\s+)?declare\s+(?:const|let|var)\s+(?<name>\w+)(?::\s*[^;=]+)?\s*;", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:const|let|var)\s+(?<name>\w+)\s*(?::\s*.+?)?\s*=\s*(?:async\s+)?(?:\([^)]*\)|[^=])\s*=>", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
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
            // Verbatim-identifier segments (`@Foo.@Bar`) are accepted per segment via
            // `CSharpNamespacePattern` / `CSharpIdentifierPattern` (both built from `@?[_\p{L}]\w*`)
            // and later canonicalized to `Foo.Bar` by `NormalizeCSharpSymbolName`. See the iter-6 /
            // iter-7 notes around `StripCSharpVerbatimPrefixes` for the shared canonical-form policy.
            // verbatim 識別子の各セグメント（`@Foo.@Bar`）を `CSharpNamespacePattern` /
            // `CSharpIdentifierPattern`（どちらも `@?[_\p{L}]\w*`）経由で受け入れ、
            // `NormalizeCSharpSymbolName` で `Foo.Bar` に canonical 化する。iter-6 / iter-7 の
            // `StripCSharpVerbatimPrefixes` 周辺コメントを参照。
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
            // `readonly public static` / `static public readonly` still classify as kind `function`
            // instead of falling through to the plain-field (kind `property`) row. Closes #355.
            // static/readonly の順序は自由で、`new`（メンバー隠蔽）も任意位置に置ける。visibility も
            // 先頭以外の位置に現れることを許容し、`readonly public static` や `static public readonly`
            // のような旧来の並びでも kind `function` で取り扱う。通常フィールド（kind `property`）の
            // 正規表現に流れ落ちないようにする。Closes #355.
            new("function",  new Regex(
                $@"^\s*"
              + $@"(?=(?:(?:{CSharpVisibilityPattern}|new|static|readonly)\s+)*static\s+)"
              + $@"(?=(?:(?:{CSharpVisibilityPattern}|new|static|readonly)\s+)*readonly\s+)"
              + $@"(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:new|static|readonly)\s+)+"
              + @"(?<returnType>[\w@?.<>\[\],:\s]+?)\s+(?<name>" + CSharpIdentifierPattern + @")\s*[=;]",
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
            new("function",  new Regex(
                $@"^\s*"
              + $@"(?=(?:(?:{CSharpVisibilityPattern}|static|abstract|virtual|sealed|override|new|unsafe|extern)\s+)*static\s+)"
              + $@"(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|abstract|virtual|sealed|override|new|unsafe|extern)\s+)+"
              + $@"(?<conversionKind>implicit|explicit)\s+operator\b",
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
            new("function",  new Regex(
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
            new("function",  new Regex($@"^\s*(?!\[\s*(?:assembly|module|type|return|param|field|property|event|method)\s*:)(?![?:])(?!(?:await|return|throw|yield|var|typeof|sizeof|nameof|default|if|for|foreach|while|switch|catch|lock|using|case|else|when|break|continue|goto|from|where|select|orderby|group|join|let|into|on|equals|ascending|descending|by)\b)(?!\s*(?:(?:{CSharpVisibilityPattern}|static|sealed|partial|readonly|unsafe|extern|virtual|override|abstract|async|new|file|ref(?:\s+readonly)?)\s+)*delegate\b(?!\s*\*))(?:(?<visibility>{CSharpVisibilityPattern})\s+|(?:static|sealed|partial|readonly|unsafe|extern|virtual|override|abstract|async|new|file|ref(?:\s+readonly)?)\s+)*(?!{CSharpNonTypeKeywordPattern})(?<returnType>{CSharpTypePattern})\s+(?!(?:base|this)\b)(?<name>{CSharpIdentifierPattern})\s*{CSharpMethodTypeParameterListPattern}\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility", "returnType"),
            // Constructor (no return type, name followed by parenthesis) — needs visibility.
            // `unsafe` / `extern` can appear before or after visibility so declarations like
            // `unsafe public S(int* p) {}` and `extern public S(int x);` are still captured
            // with visibility populated. Closes #355.
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
            // `unsafe` / `extern` は visibility の前後どちらにも置けるため、
            // `unsafe public S(int* p) {}` や `extern public S(int x);` でも visibility を
            // 拾える。Closes #355.
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
            new("function",  new Regex($@"^\s*(?:(?:unsafe|extern)\s+)*(?<visibility>{CSharpVisibilityPattern})\s+(?:(?:unsafe|extern)\s+)*(?<name>{CSharpIdentifierPattern})\s*\((?!.*\){CSharpTupleSuffixPattern}\s*{CSharpIdentifierPattern}\s*(?:[{{(;]|=>|=(?![=>])))", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
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
            new("function", new Regex(@"^func\s+(?:\([^)]+\)\s+)?(?<name>\w+)\s*[\(\[]", RegexOptions.Compiled), BodyStyle.Brace),
            new("struct",   new Regex(@"^type\s+(?<name>\w+)(?:\[[^\]]+\])?\s+struct\b", RegexOptions.Compiled), BodyStyle.Brace),
            new("interface", new Regex(@"^type\s+(?<name>\w+)(?:\[[^\]]+\])?\s+interface\b", RegexOptions.Compiled), BodyStyle.Brace),
            // Type alias (type Name = OtherType or type Name OtherType) / 型エイリアス
            new("import",   new Regex(@"^type\s+(?<name>\w+)(?:\[[^\]]+\])?\s+[=\w]", RegexOptions.Compiled), BodyStyle.None),
            // Top-level const declarations / トップレベル const 宣言
            new("property", new Regex(@"^const\s+(?<name>\w+)(?:\s+\w[\w.*\[\]]*)?\s*=", RegexOptions.Compiled), BodyStyle.None),
            // Const declaration inside const block / const ブロック内の定数宣言
            new("property", new Regex(@"^\s+(?<name>[A-Z]\w*)\s*=\s*", RegexOptions.Compiled), BodyStyle.None),
            // Package-level var / パッケージレベル変数
            new("property", new Regex(@"^var\s+(?<name>\w+)\s", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["rust"] =
        [
            // macro_rules! / マクロ定義
            new("function", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?macro_rules!\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // const/static items / 定数・静的変数
            new("function", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?(?:const|static)\s+(?<name>\w+)\s*:", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            // fn with expanded modifiers: async, const, unsafe, default, extern (ABI optional) /
            // 拡張修飾子: async, const, unsafe, default, extern（ABI は省略可）
            new("function", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?(?:(?:async|const|unsafe|default|extern(?:\s+""[^""]+"")?)\s+)*fn\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("struct",   new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?(?:struct|union)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("enum",     new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Enum variants / `Red`, `Ok(T)`, `Circle { radius: f64 }`, `Point`
            new("property", new Regex(@"^\s{4,}(?<name>[A-Z][A-Za-z0-9_]*)\s*(?:\([^()\r\n]*\)|\{[^{}\r\n]*\})?\s*,?\s*$", RegexOptions.Compiled), BodyStyle.None),
            new("interface", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?trait\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // impl Trait for Type / `unsafe impl Trait for Type` should attach to the type being extended.
            // `impl Trait for Type` / `unsafe impl Trait for Type` は、拡張先の型に紐づける。
            new("class",    new Regex(@"^\s*(?:unsafe\s+)?impl(?:<[^>]+>)?\s+.+?\s+for\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*(?:unsafe\s+)?impl(?:<[^>]+>)?\s+(?<name>\w+)(?!\s+for\b)", RegexOptions.Compiled), BodyStyle.Brace),
            // mod / モジュール
            new("namespace", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?mod\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // type alias / 型エイリアス
            new("import",   new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?type\s+(?<name>\w+)(?:\s*<[^=]+>)?", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("import",   new Regex(@"^\s*use\s+(?<name>.+);", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["java"] =
        [
            // Package declaration / package 宣言
            new("namespace", new Regex(@"^\s*package\s+(?<name>[\w.]+)\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            // Module declaration (Java 9+ module-info.java) / モジュール宣言（Java 9+ の module-info.java）
            new("namespace", new Regex(@"^\s*(?:open\s+)?module\s+(?<name>[\w.]+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
            // Annotation type (@interface) / アノテーション型
            new("class",    new Regex(@"^\s*(?<visibility>public|private|protected)?\s*@interface\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, "visibility"),
            // record (Java 16+) — must come before general class pattern / record は一般クラスパターンの前に配置
            new("class",    new Regex(@"^\s*(?<visibility>public|private|protected)?\s*(?:(?:static|final|abstract|sealed|non-sealed|strictfp)\s+)*record\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, "visibility"),
            // Interface / インターフェース
            new("interface", new Regex(@"^\s*(?<visibility>public|private|protected)?\s*(?:(?:static|abstract|sealed|non-sealed|strictfp)\s+)*interface\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, "visibility"),
            // Enum / enum
            new("enum",     new Regex(@"^\s*(?<visibility>public|private|protected)?\s*(?:(?:static|strictfp)\s+)*enum\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, "visibility"),
            // Class — with extended modifiers (final, sealed, static, abstract, strictfp)
            // クラス — 拡張修飾子対応（final, sealed, static, abstract, strictfp）
            new("class",    new Regex(@"^\s*(?<visibility>public|private|protected)?\s*(?:(?:static|final|abstract|sealed|non-sealed|strictfp)\s+)*class\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace, "visibility"),
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
            new("class",    new Regex(@"^\s*companion\s+object(?:\s+(?<name>\w+))?", RegexOptions.Compiled), BodyStyle.Brace),
            // Interface / インターフェース
            new("interface", new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:sealed|expect|actual)\s+)*interface\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Enum class / enum クラス
            new("enum",     new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:expect|actual)\s+)*enum\s+class\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Class/object with expanded modifiers: data, sealed, value, inner, annotation, expect, actual
            // クラス/オブジェクト — 拡張修飾子対応: data, sealed, value, inner, annotation, expect, actual
            new("class",    new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:abstract|data|sealed|open|inner|value|annotation|expect|actual)\s+)*(?:class|object)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Function / 関数 (including extension, secondary constructor, override, and abstract forms)
            // 関数 — 拡張・セカンダリコンストラクタ・override・abstract 形を含む
            new("function", new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:suspend|inline|infix|operator|tailrec|external|expect|actual|abstract|override|open|final)\s+)*fun\s+(?:\w+(?:<[^>]+>)?\.)?(?<name>\w+)\s*[\(<](?:.*?\))?(?::\s*(?<returnType>[^ {=]+))?", RegexOptions.Compiled), BodyStyle.Brace, "visibility", "returnType"),
            // Secondary constructor / セカンダリコンストラクタ
            new("function", new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*constructor\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Enum entry / enum エントリ
            new("property", new Regex(@"^\s{2,}(?<name>[A-Z][A-Z0-9_]*)\s*(?:\((?<returnType>[^)]*)\))?\s*(?:,|\{|;)?\s*$", RegexOptions.Compiled), BodyStyle.Brace, "returnType"),
            // Top-level val/var property / トップレベルプロパティ
            new("property", new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:const|lateinit|override)\s+)?(?:val|var)\s+(?<name>\w+)\s*[=:]", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            // Type alias / 型エイリアス
            new("import",   new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*typealias\s+(?<name>\w+)(?:\s*<[^=]+>)?\s*=", RegexOptions.Compiled), BodyStyle.None, "visibility"),
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
            new("function", new Regex(@"^\s*def\s+(?:self\.)?(?<name>\w+[?!=]?)", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("class",    new Regex(@"^\s*class\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("class",    new Regex(@"^\s*module\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.RubyEnd),
            new("import",   new Regex(@"^\s*require(?:_relative)?\s+(?<name>.+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["c"] =
        [
            new("function", new Regex(CFunctionStartBlacklistPattern + @"(?<returnType>(?:\w+[\s*]+)+)" + CFunctionNameBlacklistPattern + @"(?<name>\w+)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            // #define macros / #define マクロ
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)\(", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)(?=\s|$)", RegexOptions.Compiled), BodyStyle.None),
            new("struct",   new Regex(@"^\s*(?:typedef\s+)?struct\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*(?:typedef\s+)?enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*#include\s+(?<name>.+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["cpp"] =
        [
            new("namespace", new Regex(CppFunctionStartBlacklistPattern + @"(?:export\s+)?module\s+(?<name>[\w.]+)\b", RegexOptions.Compiled), BodyStyle.None),
            new("namespace", new Regex(CppFunctionStartBlacklistPattern + @"inline\s+namespace\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("interface", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?:export\s+)?concept\s+(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?:(?<returnType>(?:[\w:<>~]+[\s*&]+)+))?(?:(?:[\w:<>]+\s*::\s*)+)?" + CFunctionNameBlacklistPattern + @"(?<name>~?\w+|operator(?:\s*\(\)|\s*\[\]|\s*[^\s(]+(?:\s+[^\s(]+)?))(?:\s*<[^>]+>)?\s*\(", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            // #define macros / #define マクロ
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)\(", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)(?=\s|$)", RegexOptions.Compiled), BodyStyle.None),
            new("property", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?<returnType>(?:[\w:<>~]+[\s*&]+)+)(?:(?:[\w:<>]+\s*::\s*)+)(?<name>\w+)\s*=\s*[^;]+;", RegexOptions.Compiled), BodyStyle.None, ReturnTypeGroup: "returnType"),
            new("class",    new Regex(@"^\s*class\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("struct",   new Regex(@"^\s*struct\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("namespace", new Regex(@"^\s*namespace\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*(?:typedef\s+)?enum\s+(?:class\s+)?(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*#include\s+(?<name>.+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["php"] =
        [
            // Const declaration / 定数宣言
            new("function", new Regex(@"^\s*(?:(?<visibility>public|private|protected)\s+)?const\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>public|private|protected)\s+)?(?:(?:static|abstract|final)\s+)*function\s+(?<name>\w+)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Class with expanded modifiers: abstract, final, readonly (PHP 8.2+)
            // 拡張修飾子対応: abstract, final, readonly (PHP 8.2+)
            new("class",    new Regex(@"^\s*(?:(?:abstract|final|readonly)\s+)*class\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("interface", new Regex(@"^\s*interface\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("interface", new Regex(@"^\s*trait\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
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
            new("function", new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*(?:(?:static|class|nonisolated|mutating|nonmutating)\s+)*(?:override\s+)?func\s+(?<name>`[^`]+`|\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*(?:(?:required|convenience|nonisolated|mutating|nonmutating|override)\s+)*(?<name>init)(?:\?)?\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*(?:(?:nonisolated)\s+)*(?<name>deinit)\s*(?:\{|$)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*(?:(?:static|class|nonisolated|mutating|nonmutating|override)\s+)*(?<name>subscript)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("struct",    new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*(?:(?:final)\s+)*struct\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("enum",      new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*(?:indirect\s+)?enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("property",  new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*(?:indirect\s+)?case\s+(?<name>\w+)(?:\s*\([^)]*\))?(?:\s*=\s*(?<returnType>.+?))?\s*$", RegexOptions.Compiled), BodyStyle.None, "visibility", ReturnTypeGroup: "returnType"),
            new("interface", new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*protocol\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("import",   new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*associatedtype\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("property", new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*(?:(?:lazy|weak|unowned|final|static|class|nonisolated)\s+)*(?:let|var)\s+(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            // Extension declarations are important search anchors in Swift-heavy codebases.
            // extension 宣言は Swift コード検索における重要なアンカー。
            new("class",    new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*(?:(?:final)\s+)?extension\s+(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // actor (Swift 5.5+) / アクター
            new("class",    new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*(?:(?:final|distributed)\s+)*(?:class|actor)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Type alias / 型エイリアス
            new("import",   new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*typealias\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("function", new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*macro\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("interface", new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*precedencegroup\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*(?<visibility>public|private|internal|open|fileprivate|package)?\s*(?:prefix|infix|postfix)\s+operator\s+(?<name>\S+)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("import",   new Regex(@"^\s*import\s+(?<name>.+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["objc"] =
        [
            new("class",    new Regex(@"^\s*@interface\s+(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*@implementation\s+(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.Brace),
            new("interface", new Regex(@"^\s*@protocol\s+(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.Brace),
            new("property", new Regex(@"^\s*@property\b(?:\s*\([^)]*\))?.*?(?<name>\w+)\s*;", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*[+-]\s*\([^)]*\)\s*(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*#(?:import|include)\s+[<""](?<name>[^"">]+)[>""]", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["fsharp"] =
        [
            new("function", new Regex(@"^\s*let\s+(?:(?:rec|mutable|inline|private|internal|public)\s+)*(?<name>(?:``[^`]+``|\w+))(?:\s+(?:\w+|\())?", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>\w+)\s*(?:\([^)]*\))\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>\w+)\s*=\s*class\b", RegexOptions.Compiled), BodyStyle.None),
            new("struct",   new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>\w+)\s*=\s*\{", RegexOptions.Compiled), BodyStyle.None),
            new("enum",     new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>\w+)\s*=\s*(?:\|\s*)?\w+(?:\s*\|\s*\w+)+", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>\w+)\s*=\s*(?!\{)(?!\|)(?!class\b)(?!struct\b)(?!interface\b)(?!enum\b).+", RegexOptions.Compiled), BodyStyle.None),
            new("namespace", new Regex(@"^\s*namespace\s+(?:(?:rec|global)\s+)*(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.None),
            new("namespace", new Regex(@"^\s*module\s+(?:(?:(?:private|internal)\s+|rec\s+))*(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?<visibility>private|internal|public)\s+)?override\s+(?:(?:this|_|\w+)\.)?(?<name>\w+)\s*(?:\(|=|:)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>private|internal|public)\s+)?(?:(?:static|abstract|override|default)\s+)*member\s+(?:(?:private|internal)\s+)?(?:(?:inline)\s+)?(?:(?:this|_|\w+)\.)?(?!val\b)(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("import",   new Regex(@"^\s*open\s+(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["vb"] =
        [
            new("namespace", new Regex(@"^\s*Namespace\s+(?<name>(?:Global\.)?[\w.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.VisualBasicEnd),
            new("function", new Regex(@$"^\s*(?:(?:{VbMemberModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbMemberModifierPattern})\s+)*(?:Sub|Function)\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None, "visibility"),
            new("property", new Regex(@$"^\s*(?:(?:{VbPropertyModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbPropertyModifierPattern})\s+)*Property\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None, "visibility"),
            new("event",    new Regex(@$"^\s*(?:(?:{VbEventModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbEventModifierPattern})\s+)*Event\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None, "visibility"),
            new("interface", new Regex(@$"^\s*(?:(?:{VbTypeModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbTypeModifierPattern})\s+)*Interface\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.VisualBasicEnd, "visibility"),
            new("enum",     new Regex(@$"^\s*(?:(?<visibility>{VbVisibilityPattern})\s+)?Enum\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None, "visibility"),
            new("struct",   new Regex(@$"^\s*(?:(?:Partial)\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:Partial)\s+)*Structure\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.VisualBasicEnd, "visibility"),
            new("class",    new Regex(@$"^\s*(?:(?:{VbTypeModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbTypeModifierPattern})\s+)*(?:Class|Module)\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.VisualBasicEnd, "visibility"),
            new("import",   new Regex(@"^\s*Imports\s+(?<name>.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
        ],
        ["scala"] =
        [
            new("function", new Regex(@"^\s*(?<visibility>private|protected)?\s*(?:override\s+)?def\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("interface", new Regex(@"^\s*(?<visibility>private|protected)?\s*(?:sealed\s+)?trait\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("enum",     new Regex(@"^\s*(?<visibility>private|protected)?\s*enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("class",    new Regex(@"^\s*(?<visibility>private|protected)?\s*(?:abstract\s+|sealed\s+|final\s+)?(?:case\s+)?(?:class|object)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
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
            new("function", new Regex(@"^\s*(?<name>[\w.]+)\s*<-\s*function\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*(?:library|require)\s*\(\s*(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["lua"] =
        [
            new("function", new Regex(@"^\s*(?:local\s+)?function\s+(?<name>[\w.:]+)\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*local\s+(?<name>[\w]+)\s*=\s*function\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?<name>[\w]+(?:[.:][\w]+)+)\s*=\s*function\s*\(", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*(?:local\s+\w+\s*=\s*)?require\s*\(?['""](?<name>[^'""]+)['""]", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["elixir"] =
        [
            new("function", new Regex(@"^\s*(?:def|defp|defmacro|defguardp?)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.ElixirEnd),
            new("class",    new Regex(@"^\s*defmodule\s+(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.ElixirEnd),
            new("interface", new Regex(@"^\s*defprotocol\s+(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.ElixirEnd),
            new("import",   new Regex(@"^\s*(?:import|alias|use|require)\s+(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.None),
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
            new("function", new Regex(@"^(?<name>[\w.%-]+)\s*:(?!=|:=)", RegexOptions.Compiled), BodyStyle.None),  // Makefile targets / Makefileターゲット
        ],
        ["dockerfile"] =
        [
            new("function", new Regex(@"^\s*FROM\s+\S+\s+(?:AS|as)\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.None),  // Named stage / 名前付きステージ
            new("class",    new Regex(@"^\s*FROM\s+(?<name>\S+)", RegexOptions.Compiled), BodyStyle.None),  // Base image / ベースイメージ
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
        ["shell"] =
        [
            // Bash/Zsh function declarations / Bash/Zsh 関数宣言
            new("function", new Regex(@"^\s*(?:function\s+)?(?<name>\w+)\s*\(\s*\)\s*\{?", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*function\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
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
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:(?:(?:GLOBAL|LOCAL)\s+)?(?:TEMP|TEMPORARY)\s+|UNLOGGED\s+)?(?:TABLE|(?:MATERIALIZED\s+)?VIEW)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            // CREATE PROCEDURE / PROC / FUNCTION / TRIGGER — Postgres `OR REPLACE` and T-SQL `OR ALTER` / `PROC` short form
            // Uses BodyStyle.SqlProcBody so the body range covers the BEGIN...END / dollar-quoted body,
            // letting ReferenceExtractor.ResolveContainerForCall attribute calls inside the body to the
            // enclosing procedure (see issue #429).
            // CREATE PROCEDURE / PROC / FUNCTION / TRIGGER — Postgres の `OR REPLACE` と T-SQL の `OR ALTER` / 短縮形 `PROC` に対応
            // BodyStyle.SqlProcBody により BEGIN...END / dollar-quoted の本体範囲を求め、ReferenceExtractor の
            // ResolveContainerForCall が本体内の呼び出しを外側のプロシージャに帰属させられるようにする（issue #429）。
            new("function", new Regex($@"^\s*CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:PROCEDURE|PROC|FUNCTION|TRIGGER)\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.SqlProcBody),
            new("enum",     new Regex($@"^\s*CREATE\s+TYPE\s+(?<name>{SqlQualifiedIdentifierPattern})\s+AS\s+ENUM\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            // Oracle: CREATE [OR REPLACE] TYPE BODY <name> and CREATE [OR REPLACE] PACKAGE [BODY] <name>.
            // These must precede the bare CREATE TYPE / CREATE PACKAGE rows so the `BODY` keyword is
            // not absorbed as the object name.
            // Oracle: CREATE [OR REPLACE] TYPE BODY <name> と CREATE [OR REPLACE] PACKAGE [BODY] <name>。
            // 裸の CREATE TYPE / CREATE PACKAGE 行より前に置き、`BODY` キーワードを name として
            // 飲み込まないようにする。
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?TYPE\s+BODY\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:EDITIONABLE\s+|NONEDITIONABLE\s+)?PACKAGE\s+BODY\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:EDITIONABLE\s+|NONEDITIONABLE\s+)?PACKAGE\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?TYPE\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("namespace", new Regex($@"^\s*CREATE\s+SCHEMA\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?<name>(?!AUTHORIZATION\b){SqlQualifiedIdentifierPattern})|AUTHORIZATION\s+(?<name>{SqlQualifiedIdentifierPattern}))", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+(?:SEQUENCE|DOMAIN)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("import",   new Regex($@"^\s*CREATE\s+EXTENSION\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            // T-SQL SYNONYM (also Oracle / DB2)
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:PUBLIC\s+)?SYNONYM\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            // Oracle: CREATE [SHARED] [PUBLIC] DATABASE LINK <name> — must precede the bare CREATE DATABASE row
            // so the `LINK` token is not taken as a name. SHARED and PUBLIC may appear together in that order.
            // Oracle: CREATE [SHARED] [PUBLIC] DATABASE LINK <name> — 裸の CREATE DATABASE 行より前に置き、
            // `LINK` を name として飲み込まないようにする。SHARED と PUBLIC はこの順で 2 語並ぶことがある。
            new("class",    new Regex($@"^\s*CREATE\s+(?:SHARED\s+)?(?:PUBLIC\s+)?DATABASE\s+LINK\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            // T-SQL server-level / database-level principals and objects, plus Oracle-only DIRECTORY / CONTEXT / PROFILE.
            // Include T-SQL SECURITY POLICY so row-level-security policy definitions are discoverable.
            // T-SQL のサーバ/データベースレベルのプリンシパル・オブジェクトと、Oracle 固有の DIRECTORY / CONTEXT / PROFILE。
            // T-SQL の SECURITY POLICY も含め、行レベルセキュリティポリシー定義を検索可能にする。
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:DATABASE|LOGIN|USER|ROLE|CERTIFICATE|DIRECTORY|CONTEXT|PROFILE|SECURITY\s+POLICY)\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            // T-SQL partitioning and full-text catalogs
            // T-SQL のパーティション関連と全文検索カタログ
            new("function", new Regex($@"^\s*CREATE\s+PARTITION\s+FUNCTION\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+PARTITION\s+SCHEME\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+FULLTEXT\s+CATALOG\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?!ON\b)(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
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
            new("function", new Regex($@"^\s*ALTER\s+(?:PROCEDURE|PROC|FUNCTION|TRIGGER)\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.SqlProcBody),
            new("function", new Regex($@"^\s*ALTER\s+PARTITION\s+FUNCTION\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("namespace", new Regex($@"^\s*ALTER\s+SCHEMA\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("import",   new Regex($@"^\s*ALTER\s+EXTENSION\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            // Oracle: ALTER DATABASE LINK <name> — must precede the bare ALTER DATABASE row so `LINK`
            // is not absorbed as the object name. Real Oracle body compilation is expressed as
            // `ALTER PACKAGE <name> COMPILE BODY` / `ALTER TYPE <name> COMPILE BODY` and falls through
            // to the generic ALTER row below; there is no `ALTER PACKAGE BODY <name>` syntax in Oracle.
            // Oracle: ALTER DATABASE LINK <name> — 裸の ALTER DATABASE 行より前に置き `LINK` を name
            // として飲み込まないようにする。Oracle の body コンパイルは実際には
            // `ALTER PACKAGE <name> COMPILE BODY` / `ALTER TYPE <name> COMPILE BODY` の形で、下の
            // generic ALTER 行で拾う。`ALTER PACKAGE BODY <name>` のような構文は Oracle に存在しない。
            new("class",    new Regex($@"^\s*ALTER\s+DATABASE\s+LINK\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("class",    new Regex($@"^\s*ALTER\s+(?:TABLE|(?:MATERIALIZED\s+)?VIEW|SEQUENCE|SYNONYM|LOGIN|USER|ROLE|DATABASE|CERTIFICATE|INDEX|PACKAGE|TYPE|DOMAIN|DIRECTORY|PROFILE|PARTITION\s+SCHEME|FULLTEXT\s+CATALOG|SECURITY\s+POLICY)\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
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
            new("function", new Regex(@"^\s*configuration\s+(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.Brace),
            new("function", new Regex(@"^\s*workflow\s+(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.Brace),
            // Function/filter declarations with optional scope prefixes / scope プレフィックス付き関数・フィルタ宣言
            new("function", new Regex(@"^\s*(?:function|filter)\s+(?:(?:script|global|local|private):)?(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.Brace),
            // PowerShell class members / PowerShell クラスメンバー
            // Return-typed methods and modifiers such as `static` / `hidden` / `static hidden`
            // stay on the function path.
            // 戻り値付き method と `static` / `hidden` / `static hidden` のような修飾子は
            // function パスで扱う。
            new("function", new Regex(@"^\s*(?:(?:static|hidden)\s+)*(?:\[[^\]]+\]\s+)+(?<name>[\w-]+)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.Brace),
            // Constructors are bare class-name declarations inside a class body, so the
            // PascalCase gate keeps most cmdlet-style calls out while still catching the
            // canonical PS5+ shape.
            // コンストラクタは class 本体内に置かれる bare な class-name 宣言なので、
            // PascalCase の条件で cmdlet 風の呼び出しを大半弾きつつ、PS5+ の標準形を拾う。
            new("function", new Regex(@"^\s*(?<name>[A-Z]\w*)\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            // Attributes and typed properties / 属性付きプロパティと型付きプロパティ
            new("property", new Regex(@"^\s*(?:(?:static|hidden)\s+)*(?:\[[^\]]+\]\s*)+\$(?<name>\w+)\s*(?:=|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            // Class (PowerShell 5+) / クラス (PowerShell 5+)
            new("class",    new Regex(@"^\s*class\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.Brace),
            // Enum (PowerShell 5+) / enum (PowerShell 5+)
            new("enum",     new Regex(@"^\s*enum\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.Brace),
            // Enum values / enum 値
            new("enum",     new Regex(@"^\s{2,}(?<name>[\w-]+)\s*(?:=\s*[^#\r\n]+)?\s*$", RegexOptions.Compiled), BodyStyle.None),
            // Import-Module / using module / using namespace / using assembly / モジュールインポート
            new("import",   new Regex(@"^\s*(?:Import-Module|using\s+(?:module|namespace|assembly))\s+(?<name>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
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
            new("function", new Regex(@"^\s*:(?!eof(?![\w.-]))(?<name>[\w.\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
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
            new("property", new Regex(@"(?:(?:^|&|\()\s*|(?:\belse|\bdo)\s+)(?:@\s*)?(?:if\s+.+?\s+)?set\s+(?:/[aApP]\s+)?""?(?<name>[A-Za-z_][\w]*)\s*(?:[+\-*/%&^|]|<<|>>)?=", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
        ],
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

    /// <summary>
    /// Return the set of languages that have symbol-extraction patterns.
    /// シンボル抽出パターンを持つ言語のセットを返す。
    /// </summary>
    public static IReadOnlyCollection<string> GetSupportedLanguages()
        => PatternCache.Keys.Concat(new[] { "vue", "svelte" }).ToArray();

    private static string? NormalizeLanguage(string? lang)
        => lang is "vue" or "svelte" ? "typescript" : lang;

    private static bool TryHandleGoImportLine(
        long fileId,
        string line,
        int lineIndex,
        List<SymbolRecord> symbols,
        ref bool inImportBlock)
    {
        var trimmed = line.TrimStart();

        if (inImportBlock)
        {
            if (trimmed.Length == 0
                || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                return true;
            }

            if (trimmed.StartsWith(")", StringComparison.Ordinal))
            {
                inImportBlock = false;
                return true;
            }

            var closingParenIndex = trimmed.IndexOf(')');
            if (closingParenIndex >= 0)
            {
                var blockImportText = trimmed[..closingParenIndex].TrimEnd();
                if (blockImportText.Length > 0)
                    TryAddGoImportSymbol(fileId, line, lineIndex, symbols, blockImportText);

                inImportBlock = false;
                return true;
            }

            return TryAddGoImportSymbol(fileId, line, lineIndex, symbols, trimmed);
        }

        if (!trimmed.StartsWith("import", StringComparison.Ordinal)
            || (trimmed.Length > "import".Length
                && !char.IsWhiteSpace(trimmed["import".Length])
                && trimmed["import".Length] != '('))
            return false;

        var afterImport = trimmed["import".Length..].TrimStart();
        if (afterImport.StartsWith("(", StringComparison.Ordinal))
        {
            var blockRemainder = afterImport[1..].TrimStart();
            if (blockRemainder.Length > 0)
            {
                var closingParenIndex = blockRemainder.IndexOf(')');
                if (closingParenIndex >= 0)
                {
                    var blockImportText = blockRemainder[..closingParenIndex].TrimEnd();
                    if (blockImportText.Length > 0)
                        TryAddGoImportSymbol(fileId, line, lineIndex, symbols, blockImportText);

                    inImportBlock = false;
                    return true;
                }

                TryAddGoImportSymbol(fileId, line, lineIndex, symbols, blockRemainder);
            }

            inImportBlock = true;
            return true;
        }

        return TryAddGoImportSymbol(fileId, line, lineIndex, symbols, afterImport);
    }

    private static bool TryAddGoImportSymbol(
        long fileId,
        string rawLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        string importText)
    {
        var match = GoImportSpecRegex.Match(importText);
        if (!match.Success)
            return true;

        var name = match.Groups["name"].Value.Trim();
        var startColumn = rawLine.IndexOf(name, StringComparison.Ordinal);
        if (startColumn < 0)
            startColumn = rawLine.IndexOf(importText, StringComparison.Ordinal);
        if (startColumn < 0)
            startColumn = rawLine.Length - rawLine.TrimStart().Length;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineIndex + 1,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "import",
                Name = name,
                Line = lineIndex + 1,
                StartLine = lineIndex + 1,
                StartColumn = startColumn,
                EndLine = lineIndex + 1,
                Signature = name,
            },
            rawLine);
        return true;
    }

    private static readonly HashSet<string> ContainerKinds =
    [
        "class", "struct", "interface", "namespace", "enum"
    ];

    private static readonly Regex RubyBlockStartRegex = new(@"^\s*(?:class|module|def|if|unless|case|begin|do|while|until|for)\b", RegexOptions.Compiled);
    private static readonly Regex RubyBlockTokenRegex = new(@"\b(?:class|module|def|if|unless|case|begin|do|while|until|for|end)\b", RegexOptions.Compiled);
    private static readonly Regex ElixirBlockStartRegex = new(@"^\s*(?:defmodule|defprotocol|defimpl|defmacro|defguardp?|defp?)\b", RegexOptions.Compiled);
    private static readonly Regex ElixirBlockTokenRegex = new(@"\b(?:do|fn|end)\b(?!:)", RegexOptions.Compiled);
    private static readonly Regex ElixirDoShorthandRegex = new(@",\s*do:\s*", RegexOptions.Compiled);
    private static readonly Regex VisualBasicContainerStartRegex = new(@$"^(?:Namespace\b|(?:(?:{VbTypeModifierPattern})\s+)*(?:(?:{VbVisibilityPattern})\s+)?(?:(?:{VbTypeModifierPattern})\s+)*(?:Class|Module|Structure|Interface)\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VisualBasicContainerEndRegex = new(@"^End\s+(?:Namespace|Class|Module|Structure|Interface)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Explicit-interface implementations reuse CSharpTypePattern for the return type so nested
    // generics and function pointers (`delegate*<List<int>, int>`, `delegate*<delegate*<int, void>, int>`)
    // are handled uniformly with the regular method / property / indexer / delegate paths. The
    // qualifier itself also has to span multi-argument generics (`IMap<string, int>.Prop`),
    // nullable / array / pointer type arguments (`IFoo<string?>.X`, `IFoo<int[]>.X`, `IFoo<int*>.X`),
    // and nested type paths (`Outer.Inner<T>.Bar`). The shape mirrors CSharpTypePattern's token set
    // so comma + whitespace combinations inside generic argument lists are not dropped.
    // 明示的インターフェース実装の戻り値型は CSharpTypePattern を共有するため、入れ子の generic や
    // `delegate*<...>` / `delegate* unmanaged[Cdecl]<...>` も通常メソッドと同じ経路で扱える。
    // qualifier 側も複数型引数 generic (`IMap<string, int>.Prop`)、nullable / array / pointer 型引数
    // (`IFoo<string?>.X` / `IFoo<int[]>.X` / `IFoo<int*>.X`)、入れ子型パス (`Outer.Inner<T>.Bar`)
    // を通せるように CSharpTypePattern と同じトークン集合へ揃え、generic 引数リスト内の
    // `,` + 空白の組み合わせを落とさないようにする。
    private const string CSharpExplicitInterfaceQualifierPattern =
        @"(?:global::)?(?:" + CSharpIdentifierPattern + @"|" + CSharpIdentifierPattern + @"::" + CSharpIdentifierPattern + @")[\w@?.<>\[\],:*]*(?:\s+[\w@?.<>\[\],:*]+)*";
    private static readonly Regex CssFontFaceDeclarationRegex = new(@"(?:^|[;{])\s*font-family\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CssInlineCustomPropertyRegex = new(@"(?<name>--[\w-]+)\s*:", RegexOptions.Compiled);
    // Accepts `Type Name`, `Type`, and `Type Name {` (bare brace at end of declaration
    // line) so BuildCSharpPropertyMatchLine also merges the Microsoft-style
    // `public int Wrap {` + next-line `get { ... }` form with its accessor. Without the
    // optional trailing `{`, that shape would early-return unmerged and get rejected by
    // ShouldSkipCSharpBracePropertyCandidate.
    // Closes #233.
    // `Type Name`、`Type`、および宣言行末の bare `{` を含む `Type Name {` を受け付ける。
    // これにより BuildCSharpPropertyMatchLine が `public int Wrap {` の次行 `get { ... }`
    // も accessor と結合できる。末尾 `{` を許さないと、この形が未結合のまま
    // ShouldSkipCSharpBracePropertyCandidate で弾かれてしまう。
    // Closes #233.
    // Visibility / modifier ordering is free so that multi-line declarations like
    // `static public Dictionary<string, int>` + next-line `Map = new();` or
    // `new public const int` + next-line `C = 1;` merge into a single match line.
    // `const` is included alongside the other field-eligible modifiers for the
    // multi-line const field case. Closes #355.
    // visibility / 修飾子の順序は自由にしておき、`static public Dictionary<string, int>`
    // + 次行 `Map = new();` や `new public const int` + 次行 `C = 1;` のような
    // 複数行宣言も 1 つのマッチ行に結合できるようにする。複数行 const フィールド向けに
    // `const` も他の field 対応修飾子と一緒に列挙する。Closes #355.
    private static readonly Regex CSharpPropertyHeaderPrefixRegex = new($@"^\s*(?:(?:{CSharpVisibilityPattern})\s+|(?:static|virtual|override|abstract|sealed|new|required|partial|readonly|volatile|unsafe|extern|const|ref(?:\s+readonly)?)\s+)*(?:{CSharpTypePattern})\s*(?:{CSharpIdentifierPattern})?\s*\{{?\s*$", RegexOptions.Compiled);
    // Limit only the lightweight confirmation phase. Once a candidate looks like a real
    // declaration (`name =`, or a named member header before `{`), BuildCSharpPropertyMatchLine
    // switches to a linear terminator/accessor scan so long raw strings / initializers are not
    // truncated. The cap exists solely to stop false-positive statement fragments such as
    // `return o switch` from repeatedly re-normalizing the rest of a large file. Closes #447.
    // 上限は軽量な確認フェーズにだけ適用する。候補が実際の宣言らしく見えた時点
    // （`name =`、または `{` 前まで到達した named member header）で
    // BuildCSharpPropertyMatchLine は線形な終端 / accessor 走査へ切り替え、長い raw string /
    // initializer を途中で切らない。上限の目的は `return o switch` のような false positive 文断片が
    // 大きいファイルの残り全体を何度も再正規化するのを止めることだけ。Closes #447.
    private const int CSharpPropertyMatchLookaheadLineLimit = 16;
    private const int CSharpPropertyMatchLookaheadCharLimit = 4096;
    private static readonly Regex CSharpConfirmedMemberPrefixRegex = new(
        $@"^\s*(?:(?:{CSharpVisibilityPattern})\s+|(?:static|virtual|override|abstract|sealed|new|required|partial|readonly|volatile|unsafe|extern|const|ref(?:\s+readonly)?)\s+)*(?!(?:class|struct|interface|enum|record|namespace|delegate\b(?!\*)|event|using|return|throw|yield|var|typeof|sizeof|nameof|default|if|for|foreach|while|switch|catch|lock|case|else|when|break|continue|goto|await|try|do|operator|this|base)\b)(?:{CSharpTypePattern})\s+(?:{CSharpExplicitInterfaceQualifierPattern}\s*\.\s*)?(?:{CSharpIdentifierPattern})\s*\{{?\s*$",
        RegexOptions.Compiled);
    private static readonly Regex CSharpStandaloneAccessorRegex = new(
        @"^\s*(?:(?:protected\s+internal|private\s+protected|protected|internal|private|public)\s+)*(?:readonly\s+)*(?:get|set|init)\b",
        RegexOptions.Compiled);

    // Detect physical lines that consist solely of C# modifier keywords (no identifier,
    // no parentheses, no punctuation). Used by TryFindCSharpWrappedHeaderModifier to
    // re-assemble wrapped declarations such as `static\nFoo() { ... }` or
    // `public\nBar() { ... }` whose identifier line alone does not satisfy the
    // constructor / static-constructor regexes. Closes #348.
    // 識別子・括弧・句読点を含まず、C# のモディファイアキーワードのみで構成される物理行を検出する。
    // `static\nFoo() { ... }` や `public\nBar() { ... }` のようにラップされた宣言の
    // 識別子行だけでは constructor / static constructor の regex を満たせないため、
    // TryFindCSharpWrappedHeaderModifier が prefix を再構築する用途で使う。Closes #348.
    private static readonly Regex CSharpWrappedHeaderModifierLineRegex = new(
        @"^\s*(?:public|private|protected|internal|static|partial|readonly|abstract|sealed|virtual|override|async|new|file|unsafe|extern|required|volatile)(?:\s+(?:public|private|protected|internal|static|partial|readonly|abstract|sealed|virtual|override|async|new|file|unsafe|extern|required|volatile))*\s*$",
        RegexOptions.Compiled);

    private readonly record struct CSharpWrappedHeaderModifierInfo(string Prefix);


    /// <summary>
    /// Extract symbols from the given source content.
    /// 指定されたソース内容からシンボルを抽出する。
    /// </summary>
    /// <param name="fileId">The file ID in the database / データベース上のファイルID</param>
    /// <param name="lang">Detected language / 検出された言語</param>
    /// <param name="content">Full file content / ファイル全体の内容</param>
    /// <returns>List of extracted symbols / 抽出されたシンボルのリスト</returns>
    public static List<SymbolRecord> Extract(long fileId, string? lang, string content)
    {
        var originalLang = lang;
        lang = NormalizeLanguage(lang);
        if (lang == null)
            return [];

        // Null / empty fast path — keep the direct-call null-safe contract that
        // FileIndexer.StripLineLeadingBom's IsNullOrEmpty check used to provide
        // before the CRLF normalization step was added in front of it. Closes #183.
        // null / 空入力は早期 return。CRLF 正規化を StripLineLeadingBom の前に
        // 入れたことで helper 側の IsNullOrEmpty による null 許容が効かなくなる
        // ため、direct call の null セーフ契約をここで復元する。Closes #183.
        if (string.IsNullOrEmpty(content))
            return [];

        if (lang == "xml")
        {
            if (content.Contains('\r'))
                content = content.Replace("\r\n", "\n").Replace("\r", "\n");
            content = FileIndexer.StripLineLeadingBom(content);
            return ExtractXmlSymbols(fileId, content.Split('\n'));
        }

        if (!PatternCache.TryGetValue(lang, out var patterns))
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
        var lines = content.Split('\n');

        // HTML has no brace/indent-scoped bodies, so the generic pattern loop's
        // "first match per line" semantics drop every additional symbol on the
        // same line. HTML also needs cross-line masking of `<!-- ... -->` and
        // raw-text children of `<script>` / `<style>` before patterns run, or
        // phantom imports/classes/properties leak out of commented-out tags
        // and inline template string literals. Closes #215 codex review blocker.
        // HTML は brace/indent スコープの本体を持たないため、汎用パターンループの
        // 「1 行の先勝ち」意味論を通すと同一行の追加シンボルを取りこぼす。加えて
        // `<!-- ... -->` と `<script>` / `<style>` の raw-text 子要素を跨ぎ行で
        // マスクしておかないと、コメントアウトされたタグやインラインテンプレート
        // 文字列から phantom な import / class / property が漏れる。#215 の codex
        // レビュー blocker 対応としてここで専用抽出に分岐する。
        if (lang == "html")
            return ExtractHtmlSymbols(fileId, lines);

        var structuralLines = StructuralLineMasker.MaskLines(lang, lines);
        var cssScannerLines = lang == "css"
            ? MaskCssScannerLines(lines)
            : null;
        int[]?[] csharpMatchColumnToRaw = null!;
        var csharpMatchLines = lang == "csharp"
            ? BuildCSharpMatchLines(lines, out csharpMatchColumnToRaw)
            : null;
        var csharpLineStartStates = lang == "csharp"
            ? BuildCSharpLineStartStates(lines)
            : null;
        var dartInsideClassBody = lang == "dart"
            ? BuildDartClassBodyScope(structuralLines)
            : null;
        var privateScopeColumns = lang is "javascript" or "typescript"
            ? BuildJavaScriptTypeScriptPrivateScopeColumns(lines, lang)
            : null;
        var csharpSwitchExpressionLines = lang == "csharp"
            ? FindCSharpSwitchExpressionLines(structuralLines)
            : null;
        var csharpInsideTypeBody = lang == "csharp"
            ? BuildCSharpTypeBodyScope(structuralLines)
            : null;
        var cssQualifiedRuleAncestors = lang == "css"
            ? FindCssQualifiedRuleAncestors(cssScannerLines!)
            : null;
        var symbols = new List<SymbolRecord>();
        var pendingRecordPrimaryComponents = new List<PendingRecordPrimaryComponents>();
        var cssSeenSymbols = lang == "css"
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        var csharpSuppressedContinuationUntil = -1;
        var goImportBlock = false;

        for (int i = 0; i < lines.Length; i++)
        {
            if (lang == "csharp" && i <= csharpSuppressedContinuationUntil)
                continue;

            var line = lines[i];
            if (lang == "go"
                && TryHandleGoImportLine(fileId, line, i, symbols, ref goImportBlock))
            {
                continue;
            }
            var structuralLine = structuralLines[i];
            var cssScannerLine = cssScannerLines?[i];
            var matchLine = structuralLine;
            if (lang == "css" && cssScannerLine != null)
            {
                // Use raw CSS text for symbol-name matching so quoted selector payloads and
                // @import values stay queryable, while brace/depth scans still rely on the
                // separately masked scanner lines.
                // CSS のシンボル名マッチは raw line を使い、引用付きセレクタや @import 値を
                // 保持する。brace/depth 判定だけ別の scanner line を使う。
                matchLine = line;
            }
            else if (lang == "csharp")
            {
                matchLine = csharpMatchLines![i];
            }

            if (lang == "php")
                ExtractPhpImportSymbols(symbols, line, i + 1);

            // Batch `rem` / `@rem` / `::` comment lines contain the same `&` / `(` / `else` /
            // `do` boundary tokens that the property regex now accepts for inline `set`
            // capture, so `REM & set FAKE=1` or `:: else set FAKE=2` would otherwise leak a
            // phantom property. Short-circuit those lines before any pattern fires — batch
            // labels never match on `::` / `rem` lines anyway because the label regex
            // requires `:<name-char>`, not `::` or `r`.
            // batch の `rem` / `@rem` / `::` コメント行は、inline `set` 捕捉のために property 正規表現が
            // 受け付ける `&` / `(` / `else` / `do` の境界トークンを含みうるため、`REM & set FAKE=1` や
            // `:: else set FAKE=2` が偽 property を出す恐れがある。パターン適用前に当該行ごと
            // 早期スキップする — batch ラベル側は `::` / `rem` 行ではそもそも `:<名前文字>` の要件を
            // 満たさないため影響を受けない。
            if (lang == "batch" && IsBatchCommentLine(line))
                continue;

            var patternStartOffset = lang is "javascript" or "typescript"
                ? FindNextJavaScriptTypeScriptStatementStart(matchLine, 0)
                : 0;
            if (lang == "csharp" && patternStartOffset == 0)
            {
                var firstNonWhitespace = 0;
                while (firstNonWhitespace < matchLine.Length && char.IsWhiteSpace(matchLine[firstNonWhitespace]))
                    firstNonWhitespace++;

                if (firstNonWhitespace < matchLine.Length
                    && matchLine[firstNonWhitespace] is '}' or ';' or '"')
                    patternStartOffset = FindNextSameLineNonClosingBraceStatementStart(matchLine, firstNonWhitespace + 1, lang);
            }
            while (patternStartOffset >= 0 && patternStartOffset < matchLine.Length)
            {
                var stopAfterFirstPatternMatch = false;
                var restartPatternScanOffset = -1;
                foreach (var pattern in patterns)
                {
                    if (lang == "csharp" && ReferenceEquals(pattern.Regex, CSharpEnumMemberRegex))
                        continue;
                    // Merge multi-line field headers for C# regardless of kind. Kind "property" (plain
                    // fields) and kind "function" (const / static readonly fields) both need the
                    // merge. Non-field function patterns (methods, constructors, operators, indexers)
                    // are unaffected because CSharpPropertyHeaderPrefixRegex requires the line to end
                    // before `(` or `{`, so lines like `public int Foo()` never satisfy the header
                    // prefix and the merger returns the original line. Closes #355.
                    // C# の複数行フィールドヘッダ結合は kind に依らず適用する。kind "property"（通常
                    // フィールド）と kind "function"（`const` / `static readonly` フィールド）の両方で
                    // 結合が必要。method / constructor / operator / indexer のような非フィールド
                    // function パターンは `CSharpPropertyHeaderPrefixRegex` が `(` や `{` を含む行を
                    // 受け付けないため影響を受けず、merger は元の行をそのまま返す。Closes #355.
                    var csharpPropertyCandidate = lang == "csharp" && pattern.Kind is "property" or "function"
                        ? BuildCSharpPropertyMatchLine(lines, csharpMatchLines!, i)
                        : new CSharpPropertyMatchCandidate(matchLine, i, i);
                    var patternMatchLine = csharpPropertyCandidate.MatchLine;
                    var lineOffset = patternStartOffset;
                    string? csharpWrappedModifierPrefix = null;
                    while (lineOffset >= 0 && lineOffset < patternMatchLine.Length)
                    {
                        var javaLeadingAnnotationOffset = 0;
                        var match = lang == "java"
                            ? (TryMatchJavaDeclarationSegment(pattern.Regex, patternMatchLine[lineOffset..], out var javaMatch, out javaLeadingAnnotationOffset)
                                ? javaMatch
                                : pattern.Regex.Match(patternMatchLine[lineOffset..]))
                            : pattern.Regex.Match(patternMatchLine[lineOffset..]);
                        if (!match.Success
                            && lang == "csharp"
                            && pattern.Kind == "function"
                            && lineOffset == 0
                            && csharpMatchLines != null
                            && csharpWrappedModifierPrefix == null)
                        {
                            // Wrapped leading modifier recovery: when a C# function-kind pattern
                            // fails at column 0 of the identifier line, try prepending the
                            // modifier prefix accumulated from preceding modifier-only lines
                            // (`static\nFoo() { ... }`, `public\nBar() { ... }`, etc.) and retry.
                            // The method regex already tolerates an omitted modifier run, so it
                            // matches on the identifier line alone — this branch only fires for
                            // constructor / static-constructor shapes that require the modifier
                            // on the same line as the name. Closes #348.
                            // ラップされた先頭モディファイアの救済: C# の function 系パターンが
                            // 識別子行の先頭マッチに失敗した場合、直前のモディファイアのみ行
                            // （`static\nFoo() { ... }` や `public\nBar() { ... }` 等）から
                            // 再構築した prefix を付け直して再試行する。メソッド regex は
                            // 先頭モディファイアが無くても識別子行単体でマッチするため、この
                            // 分岐は修飾子が識別子と同行に必要な constructor / static ctor
                            // シェイプでのみ発火する。Closes #348.
                            var wrappedInfo = TryFindCSharpWrappedHeaderModifier(csharpMatchLines!, i);
                            if (wrappedInfo != null)
                            {
                                foreach (var candidatePrefix in EnumerateCSharpWrappedModifierCandidates(wrappedInfo.Value.Prefix))
                                {
                                    var wrappedMatchLine = candidatePrefix + " " + patternMatchLine.TrimStart();
                                    var wrappedMatch = pattern.Regex.Match(wrappedMatchLine);
                                    if (wrappedMatch.Success)
                                    {
                                        match = wrappedMatch;
                                        patternMatchLine = wrappedMatchLine;
                                        // Preserve the full prefix in the stored signature so
                                        // declarations like `public\nstatic\nP1()` retain
                                        // `public static P1()`, even when the matching regex
                                        // variant only accepted `static P1()`. Closes #348.
                                        // シグネチャには完全な prefix を残し、`public\nstatic\nP1()`
                                        // のような宣言を `public static P1()` として保存する。
                                        // マッチした regex 変種が `static P1()` 形だけを受け付けた
                                        // 場合でも、保存シグネチャは完全な prefix を保持する。Closes #348.
                                        csharpWrappedModifierPrefix = wrappedInfo.Value.Prefix;
                                        break;
                                    }
                                }
                            }
                        }

                        if (!match.Success)
                        {
                            if (lang == "csharp"
                                && pattern.Kind == "property"
                                && pattern.BodyStyle == BodyStyle.Brace
                                && ShouldDeferCSharpBracePropertySameLineAdvance(matchLine, lineOffset))
                            {
                                break;
                            }

                            if (lang == "csharp"
                                && pattern.Kind == "function"
                                && ShouldDeferCSharpFunctionSameLineAdvance(matchLine, lineOffset))
                            {
                                break;
                            }

                            if (lang == "csharp"
                                && pattern.Kind is "event" or "delegate"
                                && pattern.BodyStyle == BodyStyle.None
                                && ShouldDeferCSharpEventOrDelegateSameLineAdvance(matchLine, lineOffset, pattern.Kind))
                            {
                                break;
                            }

                            if (lang is "javascript" or "typescript" or "css" or "java"
                                || (lang == "csharp"
                                    && pattern.Kind == "enum"
                                    && pattern.BodyStyle == BodyStyle.Brace
                                    && patternStartOffset > 0)
                                || (lang == "csharp"
                                    && pattern.Kind == "property"
                                    && pattern.BodyStyle == BodyStyle.None
                                    && !TryMatchAnyRecoverableCSharpPattern(
                                        matchLine[lineOffset..],
                                        insideEnumBody: false,
                                        attributeParenDepth: 0)))
                            {
                                lineOffset = FindNextSameLineBraceStatementStart(matchLine, lineOffset + 1, lang);
                                continue;
                            }

                            break;
                        }

                        var absoluteStartColumn = lineOffset + match.Index;
                        if (lang == "java" && javaLeadingAnnotationOffset > 0)
                            absoluteStartColumn = lineOffset + javaLeadingAnnotationOffset;
                        var nextSameLineOffsetAfterRejectedCSharpProperty = -1;
                    if (ShouldSkipCSharpSwitchExpressionPropertyCandidate(lang, pattern, patternMatchLine, csharpSwitchExpressionLines, i)
                        || TrySkipCSharpBracePropertyCandidate(
                            lang,
                            pattern,
                            patternMatchLine,
                            absoluteStartColumn,
                            match.Value.Contains("=>", StringComparison.Ordinal),
                            out nextSameLineOffsetAfterRejectedCSharpProperty))
                    {
                        // False-positive C# property matches can happen at the start of a
                        // same-line type header (`public class C { ... }`) because the
                        // property regex allows omitted visibility/modifier runs and can
                        // initially treat the header as `returnType + name + {`. Do not break
                        // the whole same-line scan on that rejection — advance to the next
                        // brace-delimited statement so a real nested property later on the
                        // same physical line still gets a chance to match. Closes #470.
                        // C# の property 正規表現は visibility / modifier 省略を許すため、
                        // 同一行の型ヘッダ先頭 (`public class C { ... }`) を一旦
                        // `returnType + name + {` と誤認することがある。この偽候補を弾いた
                        // ときに同一行スキャン全体を break せず、次の brace 区切り宣言へ進めて
                        // 後続の本物 property にもマッチ機会を残す。Closes #470.
                        lineOffset = nextSameLineOffsetAfterRejectedCSharpProperty >= 0
                            ? nextSameLineOffsetAfterRejectedCSharpProperty
                            : FindNextSameLineBraceStatementStart(
                                matchLine,
                                absoluteStartColumn + Math.Max(1, match.Length),
                                lang);
                        continue;
                    }

                    // Gate the C# plain-field pattern (kind `property`, BodyStyle.None) to
                    // lines that sit directly inside a type body. Without this gate, local
                    // variable declarations inside method / property / accessor / lambda
                    // bodies match the same shape and leak into `symbols`, `definition`,
                    // `outline`, `inspect`, and `unused` as phantom property symbols.
                    // Closes #298 follow-up (codex review blocker).
                    // C# の通常フィールド用パターン（kind `property` かつ BodyStyle.None）は
                    // 型本体（class / struct / interface / record / enum の直下）でしか
                    // 許可しない。このゲートを入れないと、メソッド・プロパティ・アクセサ・
                    // ラムダの内部にあるローカル変数宣言が同じ形でマッチしてしまい、
                    // `symbols` / `definition` / `outline` / `inspect` / `unused` に
                    // 擬似シンボルが混入する。Closes #298 の codex レビュー blocker 対応。
                    if (ShouldSkipCssNestedSelectorCandidate(lang, pattern, patternMatchLine, cssQualifiedRuleAncestors, i))
                        break;

                    // JS/TS HOC binding gate: the `styled.` / `styled(` / `styled\`` regex
                    // branch matches three shapes — factory capture (`const F = styled.div;`),
                    // plain call (`const F = styled(Component);`), and tagged template
                    // (`const F = styled.div\`...\``). Only the tagged-template shape
                    // actually declares a styled-component binding; the other two produce
                    // a factory / a styled wrapper-of-component without a component body
                    // on that line and must stay 0-symbol. This gate looks at the raw
                    // (unmasked) line because StructuralLineMasker.MaskJsTsTemplateLiteralContents
                    // replaces template-literal delimiters with space, so the masked
                    // `patternMatchLine` cannot see the backtick. Closes #240 follow-up
                    // (codex review #5 blocker).
                    // JS/TS HOC 束縛ゲート: `styled.` / `styled(` / `styled\`` の regex
                    // 分岐は 3 形状にマッチする — factory 捕捉（`const F = styled.div;`）、
                    // 素の呼び出し（`const F = styled(Component);`）、タグ付きテンプレート
                    // （`const F = styled.div\`...\``）。実際に styled-component 束縛を
                    // 生むのはタグ付きテンプレート形のみで、前者 2 つはその行で component
                    // 本体を生やさないため 0 シンボルに保つ必要がある。このゲートは raw 行
                    // （マスク前）を参照する — `StructuralLineMasker.MaskJsTsTemplateLiteralContents`
                    // がテンプレート区切りを空白にマスクするため、マスク後の
                    // `patternMatchLine` ではバッククォートが見えないことへの対処。
                    // Closes #240 follow-up（codex レビュー #5 の blocker 対応）。
                    if (ShouldSkipJavaScriptTypeScriptStyledFactoryCandidate(lang, pattern, match, lineOffset, lines, i))
                    {
                        lineOffset = FindNextJavaScriptTypeScriptStatementStart(patternMatchLine, lineOffset + Math.Max(1, match.Length));
                        continue;
                    }

                    // For C#, collapsed-space column (from CollapseCSharpGenericTypeWhitespace)
                    // has to be translated back to raw-space before it can be compared against
                    // CSharpTypeBodyScope's per-line transitions, which were built from
                    // structural (raw) columns. Only translate when the pattern match runs on
                    // the per-line collapsed string (single-line case); multi-line merged
                    // candidates use a different composed string whose column domain does not
                    // line up with a single line's map, so we leave the column alone there to
                    // preserve pre-existing behavior. Closes #400.
                    // C# では CollapseCSharpGenericTypeWhitespace で空白を取り除いた列を、
                    // structural 行の生列で構築された CSharpTypeBodyScope に渡す前に
                    // raw 列へ戻す必要がある。複数行を結合した match では単一行の map が
                    // 使えないため、単一行ケース（per-line collapsed line そのものにマッチした
                    // 場合）だけ変換する。Closes #400.
                    var csharpNormalizedStartColumn = lang == "csharp"
                        ? SkipWhitespace(patternMatchLine, absoluteStartColumn)
                        : absoluteStartColumn;
                    var csharpGateRawStartColumn = csharpNormalizedStartColumn;
                    if (lang == "csharp"
                        && csharpMatchLines != null
                        && ReferenceEquals(patternMatchLine, csharpMatchLines[i]))
                    {
                        csharpGateRawStartColumn = TranslateCSharpCollapsedColumnToRaw(
                            csharpMatchColumnToRaw,
                            i,
                            csharpNormalizedStartColumn,
                            line.Length);
                    }

                    if (lang == "dart"
                        && ReferenceEquals(pattern.Regex, DartBareConstConstructorRegex)
                        && !dartInsideClassBody!.IsInsideClassBodyAt(i))
                    {
                        // Bare `const` constructors need class-body context; otherwise
                        // `const Widget(key: k)` expressions become phantom symbols.
                        // bare な `const` コンストラクタは class 本体内でのみ許可する。
                        // そうしないと `const Widget(key: k)` の式を phantom symbol にしてしまう。
                        lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
                        continue;
                    }

                    // C# candidates that only become visible after string-literal content is
                    // blanked (for example, code inside an interpolation hole of an outer
                    // string) must not be emitted as declarations. A real declaration starts in
                    // root code, not in nested interpolation code. Gate on the raw-line start
                    // column so exact definition / inspect lookups do not pick up call-site
                    // fragments from interpolated log strings. Closes #790.
                    // C# では、外側文字列本文を空白化した結果として見えるようになった候補
                    // （例: 補間文字列ホール内のコード）を宣言として emit してはならない。
                    // 本物の宣言は root code から始まり、入れ子の補間コードからは始まらない。
                    // raw 行上の開始列でゲートし、補間ログ文字列内の呼び出し断片が
                    // exact definition / inspect に混入しないようにする。Closes #790.
                    if (lang == "csharp"
                        && csharpLineStartStates != null
                        && !IsCSharpRootCodePosition(line, csharpLineStartStates[i], csharpGateRawStartColumn))
                    {
                        lineOffset = FindNextSameLineBraceStatementStart(
                            matchLine,
                            absoluteStartColumn + Math.Max(1, match.Length),
                            lang);
                        continue;
                    }

                    if (lang == "csharp"
                        && pattern.Kind == "function"
                        && HasCSharpTokenBeforeIndex(matchLine, "when", absoluteStartColumn + match.Groups["name"].Index))
                    {
                        lineOffset = absoluteStartColumn + Math.Max(1, match.Length);
                        continue;
                      }
                      if (lang == "csharp"
                          && pattern.BodyStyle == BodyStyle.None
                        && (pattern.Kind == "property" || IsCSharpFieldLikeFunctionPattern(pattern))
                        && csharpInsideTypeBody != null
                        && !csharpInsideTypeBody.IsInsideTypeBodyAt(i, csharpGateRawStartColumn))
                    {
                        // Move the cursor past this same-line candidate so a later
                        // column on the same line (e.g. a real field that lives after
                        // a same-line method body or similar non-type-body scope) can
                        // still be evaluated against its own column-aware scope.
                        // Without this advance, the outer `while` would exit the line
                        // entirely on the first rejection and drop any following match.
                        // 同一行に続く別候補（例: 同一行の method 本体など非型本体の
                        // 後ろにある実フィールド）を取りこぼさないよう、次の候補探索
                        // 位置へ進める。この進行が無いと最初の拒否で while ループが
                        // 行を抜けてしまい、後続候補が失われる。Closes #400.
                        lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
                        continue;
                    }
                    if (lang == "csharp"
                        && pattern.BodyStyle == BodyStyle.None
                        && (pattern.Kind == "property" || IsCSharpFieldLikeFunctionPattern(pattern))
                        && IsInsidePreviouslyEmittedCSharpMemberBody(lines, symbols, i + 1, csharpGateRawStartColumn))
                    {
                        // Brace-based type-body scope tracking correctly rejects locals inside
                        // block bodies, but multi-line expression-bodied members have no brace
                        // transition for their continuation lines. Without an additional guard,
                        // those later lines can still match the plain-field regex and emit
                        // phantom `property` rows like `Red` from `value is\n Red\n or Red;`.
                        // Only reject lines after the member's declaration line so same-line
                        // siblings such as `int M() => 0; int X;` keep working through the
                        // existing column-aware scope gate. Closes #779.
                        // brace ベースの型本体スコープ追跡は block body 内の local を弾けるが、
                        // 複数行の式本体メンバーには continuation 行用の brace 遷移が無い。
                        // そのため追加ガードが無いと `value is\n Red\n or Red;` の後続行が
                        // plain-field regex にマッチして `property Red` の phantom を出してしまう。
                        // `int M() => 0; int X;` のような same-line sibling は既存の列単位
                        // ゲートで扱えるよう、宣言行そのものではなく後続行だけを拒否する。
                        // Closes #779.
                        lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
                        continue;
                    }
                    var rawReturnType = TryGetGroup(match, pattern.ReturnTypeGroup);
                      if (lang == "csharp"
                          && pattern.ReturnTypeGroup != null
                          && HasInvalidCSharpReturnTypeSuffix(rawReturnType))
                      {
                          lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
                          continue;
                      }
                      if (lang == "csharp"
                          && pattern.Kind == "function"
                          && HasCSharpTokenBeforeIndex(matchLine, "when", absoluteStartColumn + match.Groups["name"].Index))
                      {
                          lineOffset = absoluteStartColumn + Math.Max(1, match.Length);
                          continue;
                      }
                      if (lang == "csharp"
                          && pattern.Kind == "property"
                          && IsStandaloneCSharpAccessorCandidate(patternMatchLine))
                    {
                        lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
                        continue;
                    }
                    if (privateScopeColumns != null
                        && pattern.Kind == "class"
                        && IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, absoluteStartColumn, matchLine, includeBlockScope: true))
                    {
                        if (lang is "javascript" or "typescript")
                        {
                            var skippedEndColumn = pattern.BodyStyle == BodyStyle.Brace
                                ? FindJavaScriptTypeScriptSameLineBraceEndColumn(line, absoluteStartColumn, lang)
                                : -1;
                            lineOffset = skippedEndColumn >= absoluteStartColumn
                                ? FindNextJavaScriptTypeScriptStatementStart(patternMatchLine, skippedEndColumn + 1)
                                : FindNextJavaScriptTypeScriptStatementStart(patternMatchLine, absoluteStartColumn + Math.Max(1, match.Length));
                            continue;
                        }

                        break;
                    }

                    if (privateScopeColumns != null
                        && pattern.Kind == "class"
                        && TryGetGroup(match, pattern.VisibilityGroup) != "export"
                        && IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, i, absoluteStartColumn, matchLine))
                    {
                        if (lang is "javascript" or "typescript")
                        {
                            var skippedEndColumn = pattern.BodyStyle == BodyStyle.Brace
                                ? FindJavaScriptTypeScriptSameLineBraceEndColumn(line, absoluteStartColumn, lang)
                                : -1;
                            lineOffset = skippedEndColumn >= absoluteStartColumn
                                ? FindNextJavaScriptTypeScriptStatementStart(patternMatchLine, skippedEndColumn + 1)
                                : FindNextJavaScriptTypeScriptStatementStart(patternMatchLine, absoluteStartColumn + Math.Max(1, match.Length));
                            continue;
                        }

                        break;
                    }

                    var name = match.Groups["name"].Success
                        ? match.Groups["name"].Value.Trim()
                        : match.Value.Trim();
                    name = NormalizeExtractedSymbolName(lang, name, match, matchLine);
                    var rubyAttrNames = lang == "ruby"
                        && pattern.Kind == "property"
                        ? TryExpandRubyAttrDeclaratorList(patternMatchLine, absoluteStartColumn, match, name)
                        : null;

                    var rangeLines = lang == "css" && cssScannerLines != null
                        ? cssScannerLines
                        : structuralLines;
                    var (endLine, bodyStartLine, bodyEndLine) = lang is "kotlin" or "scala"
                        && pattern.Kind == "function"
                        && TryFindKotlinScalaExpressionBodyEndLine(line, absoluteStartColumn)
                            ? (i + 1, null, null)
                            : ResolveRange(rangeLines, i, pattern.BodyStyle, lang, absoluteStartColumn);
                    var startLine = i + 1;
                    if (lang == "csharp"
                        && pattern.Kind == "property"
                        && pattern.BodyStyle == BodyStyle.None
                        && csharpPropertyCandidate.ExpressionBodyEndLineIndex.HasValue)
                    {
                        endLine = Math.Max(endLine, csharpPropertyCandidate.ExpressionBodyEndLineIndex.Value + 1);
                    }

                    // Python @property decorator: reclassify the def as property
                    // Python @property デコレータ: def を property に再分類
                    var kind = pattern.Kind;
                    if (kind == "function" && lang == "python" && i > 0 && lines[i - 1].TrimStart().StartsWith("@property"))
                        kind = "property";

                    if (lang == "css")
                        name = ResolveCssSymbolName(matchLine[absoluteStartColumn..], name, lines, i, endLine);

                    if (lang == "css" && string.IsNullOrWhiteSpace(name))
                    {
                        var skippedEndColumn = pattern.BodyStyle == BodyStyle.Brace
                            && bodyEndLine == startLine
                            ? FindSameLineBraceEndColumn(line, absoluteStartColumn, lang, kind)
                            : -1;
                        if (skippedEndColumn >= absoluteStartColumn)
                        {
                            lineOffset = FindNextSameLineBraceStatementStart(matchLine, skippedEndColumn + 1, lang);
                            continue;
                        }

                        stopAfterFirstPatternMatch = true;
                        break;
                    }

                    var csharpSingleLineCollapsedMatch = lang == "csharp"
                        && csharpMatchLines != null
                        && ReferenceEquals(patternMatchLine, csharpMatchLines[i]);
                    var csharpSignatureRawStartColumn = csharpGateRawStartColumn;
                    var csharpSameLineBraceStartColumn = csharpSingleLineCollapsedMatch
                        ? absoluteStartColumn
                        : csharpSignatureRawStartColumn;
                    var sameLineEndColumn = pattern.BodyStyle == BodyStyle.Brace
                        && bodyEndLine == startLine
                        ? (lang == "csharp" && csharpSingleLineCollapsedMatch
                            ? FindCSharpSameLineBraceEndColumnFromSanitized(patternMatchLine, csharpSameLineBraceStartColumn)
                            : FindSameLineBraceEndColumn(line, csharpSameLineBraceStartColumn, lang, kind))
                        : -1;
                    var sameLineEndUsesRawColumns = pattern.BodyStyle == BodyStyle.Brace
                        && bodyEndLine == startLine
                        && !(lang == "csharp" && csharpSingleLineCollapsedMatch);
                    if (lang == "csharp"
                        && csharpSingleLineCollapsedMatch
                        && CanUseCSharpSameLineSemicolonEndColumn(kind))
                    {
                        var semicolonEndColumn = FindCSharpSameLineSemicolonEndColumn(patternMatchLine, absoluteStartColumn);
                        if (semicolonEndColumn >= absoluteStartColumn
                            && (sameLineEndColumn < absoluteStartColumn || semicolonEndColumn < sameLineEndColumn))
                        {
                            sameLineEndColumn = semicolonEndColumn;
                            sameLineEndUsesRawColumns = false;
                        }
                    }
                    if (lang == "csharp"
                        && kind == "event"
                        && pattern.BodyStyle == BodyStyle.None
                        && HasCSharpEventAccessorStart(patternMatchLine[absoluteStartColumn..]))
                    {
                        // Same-line accessor events (`event E { add {} remove {} }`) share the
                        // sibling-stream requirement with semicolon-bodied members: their
                        // signature must stop at the accessor block so later same-line siblings
                        // can restart the full pattern scan. Without this brace clamp, the
                        // stored event signature swallows the following declaration and the
                        // later sibling never reaches earlier patterns such as property.
                        // Closes #520.
                        // 同一行 accessor event (`event E { add {} remove {} }`) も semicolon 系
                        // member と同様に sibling stream として扱う必要がある。そのため
                        // accessor block の閉じ `}` で signature を切り、後続の same-line
                        // sibling が property など先頭側 pattern へ再到達できるようにする。
                        // これが無いと event signature が後続宣言を飲み込み、後続 sibling が
                        // earlier pattern に届かない。Closes #520.
                        var braceEndColumn = csharpSingleLineCollapsedMatch
                            ? FindCSharpSameLineBraceEndColumnFromSanitized(patternMatchLine, csharpSameLineBraceStartColumn)
                            : FindSameLineBraceEndColumn(line, csharpSameLineBraceStartColumn, lang, kind);
                        if (braceEndColumn >= absoluteStartColumn
                            && (sameLineEndColumn < absoluteStartColumn || braceEndColumn < sameLineEndColumn))
                        {
                            sameLineEndColumn = braceEndColumn;
                            sameLineEndUsesRawColumns = !(lang == "csharp" && csharpSingleLineCollapsedMatch);
                        }
                    }
                    if (sameLineEndColumn < absoluteStartColumn
                        && lang == "csharp"
                        && kind == "enum"
                        && pattern.BodyStyle == BodyStyle.None)
                    {
                        sameLineEndColumn = FindCSharpSameLineEnumMemberEndColumn(patternMatchLine, absoluteStartColumn);
                        sameLineEndUsesRawColumns = false;
                    }
                    string signature;
                    if (csharpWrappedModifierPrefix != null)
                    {
                        // Wrapped ctor signature: prepend the modifier prefix recovered from
                        // preceding modifier-only lines so the stored signature reflects the
                        // full declaration (`static Foo() { ... }`) rather than only the name
                        // line. Honor the same-line brace body truncation when present so the
                        // signature does not absorb the entire ctor body. Closes #348.
                        // ラップされたコンストラクタのシグネチャ: 直前のモディファイアのみ行から
                        // 復元した prefix を付与し、識別子行だけでなく宣言全体
                        // (`static Foo() { ... }`) を保存する。同一行に brace 本体が閉じる
                        // ケースではその末尾で切り詰め、シグネチャが本体全体を飲み込まない
                        // ようにする。Closes #348.
                        var nameLineStartColumn = csharpSingleLineCollapsedMatch
                            ? (sameLineEndUsesRawColumns
                                ? csharpSignatureRawStartColumn
                                : csharpSignatureRawStartColumn)
                            : absoluteStartColumn;
                        var nameLineEndExclusive = sameLineEndColumn >= absoluteStartColumn
                            ? (sameLineEndUsesRawColumns
                                ? Math.Min(sameLineEndColumn + 1, line.Length)
                                : Math.Min(
                                    TranslateCSharpCollapsedColumnToRaw(
                                        csharpMatchColumnToRaw,
                                        i,
                                        sameLineEndColumn,
                                        line.Length) + 1,
                                    line.Length))
                            : line.Length;
                        var nameLineContent = sameLineEndColumn >= absoluteStartColumn
                            ? line[nameLineStartColumn..nameLineEndExclusive]
                            : line[nameLineStartColumn..];
                        signature = (csharpWrappedModifierPrefix + " " + nameLineContent.TrimStart()).Trim();
                    }
                    else if (sameLineEndColumn >= absoluteStartColumn)
                    {
                        if (lang == "csharp"
                            && csharpSingleLineCollapsedMatch)
                        {
                            var rawStart = csharpSignatureRawStartColumn;
                            var rawEndInclusive = sameLineEndUsesRawColumns
                                ? sameLineEndColumn
                                : TranslateCSharpCollapsedColumnToRaw(
                                    csharpMatchColumnToRaw,
                                    i,
                                    sameLineEndColumn,
                                    line.Length);
                            var rawEndExclusive = Math.Min(rawEndInclusive + 1, line.Length);
                            if (rawStart > line.Length)
                                rawStart = line.Length;
                            if (rawEndExclusive <= rawStart)
                                rawEndExclusive = Math.Min(rawStart + Math.Max(1, match.Length), line.Length);
                            signature = line[rawStart..rawEndExclusive].Trim();
                        }
                        else
                        {
                            var signatureStartColumn = csharpSingleLineCollapsedMatch && sameLineEndUsesRawColumns
                                ? csharpSignatureRawStartColumn
                                : absoluteStartColumn;
                            var signatureEndExclusive = Math.Min(sameLineEndColumn + 1, line.Length);
                            if (signatureEndExclusive <= signatureStartColumn)
                                signatureEndExclusive = Math.Min(signatureStartColumn + Math.Max(1, match.Length), line.Length);
                            signature = line[signatureStartColumn..signatureEndExclusive].Trim();
                        }
                    }
                    else if (lang == "csharp"
                        && pattern.BodyStyle == BodyStyle.None
                        && TryFindCSharpSemicolonTerminatedSignatureExtent(
                            lines,
                            i,
                            csharpGateRawStartColumn,
                            out var csharpFieldSignatureLastLineIndex,
                            out var csharpFieldSignatureLastLineExclusiveEndColumn)
                        && csharpFieldSignatureLastLineIndex > i)
                    {
                        signature = BuildCSharpMultilineSignature(
                            lines,
                            i,
                            csharpGateRawStartColumn,
                            csharpFieldSignatureLastLineIndex,
                            csharpFieldSignatureLastLineExclusiveEndColumn);
                    }
                    else if (lang == "csharp"
                        && pattern.BodyStyle == BodyStyle.Brace
                        && IsCSharpMultilineExpressionBodiedMember(
                            lines,
                            i,
                            csharpSignatureRawStartColumn)
                        && TryFindCSharpSemicolonTerminatedSignatureExtent(
                            lines,
                            i,
                            csharpSignatureRawStartColumn,
                            out var csharpSemicolonSignatureLastLineIndex,
                            out var csharpSemicolonSignatureLastLineExclusiveEndColumn)
                        && csharpSemicolonSignatureLastLineIndex > i)
                    {
                        signature = BuildCSharpMultilineSignature(
                            lines,
                            i,
                            csharpSignatureRawStartColumn,
                            csharpSemicolonSignatureLastLineIndex,
                            csharpSemicolonSignatureLastLineExclusiveEndColumn);
                    }
                    else if (lang == "csharp" && csharpPropertyCandidate.LastConsumedLineIndex > i)
                    {
                        signature = BuildCSharpMultilineSignature(
                            lines,
                            i,
                            csharpSignatureRawStartColumn,
                            csharpPropertyCandidate.SignatureLastLineIndex,
                            csharpPropertyCandidate.SignatureLastLineExclusiveEndColumn);
                    }
                    else if (lang == "csharp"
                        && pattern.Kind is "class" or "struct" or "interface" or "enum"
                        && TryFindCSharpTypeHeaderExtent(
                            lines,
                            i,
                            csharpSignatureRawStartColumn,
                            out var csharpTypeHeaderLastLineIndex,
                            out var csharpTypeHeaderLastLineExclusiveEndColumn)
                        && csharpTypeHeaderLastLineIndex > i)
                    {
                        // Wrapped C# type header: base list and `where` clauses often continue
                        // onto following lines before the body-opening `{` or primary-ctor `;`.
                        // Join them so consumers like ReferenceExtractor can resolve the base
                        // type from the stored signature instead of silently treating the class
                        // as having no base. Uses the comment-stripping variant so trailing or
                        // interleaved `//` / `/* */` comments do not leak into the signature.
                        // Closes #382.
                        // 折り返された C# 型ヘッダ: base リストや `where` 句は本体開きの `{`
                        // または primary-ctor 終端の `;` までに複数行へまたがることが多い。
                        // 継続行を連結して保存し、ReferenceExtractor などが保存済み
                        // シグネチャから base 型を解決できるようにする。末尾や途中に混じる
                        // `//` / `/* */` コメントを signature から除去する variant を使う。
                        // Closes #382.
                        signature = BuildCSharpTypeHeaderSignature(
                            lines,
                            i,
                            csharpSignatureRawStartColumn,
                            csharpTypeHeaderLastLineIndex,
                            csharpTypeHeaderLastLineExclusiveEndColumn);
                    }
                    else if (lang == "csharp"
                        && pattern.Kind is "event" or "delegate"
                        && pattern.BodyStyle == BodyStyle.None)
                    {
                        // Same-line C# semicolon-style declarations such as
                        // `event EventHandler E; }` or `delegate void D(); }` must stop at the
                        // declaration terminator instead of absorbing the enclosing type's
                        // closing brace into the stored signature. Reuse the same statement-end
                        // scanner as plain fields so nested `{}` inside accessor-style events
                        // still stay balanced while the outer `}` remains excluded.
                        // Closes #473 follow-up.
                        // `event EventHandler E; }` や `delegate void D(); }` のような
                        // 同一行 C# のセミコロン終端宣言は、囲む型本体の `}` を signature に
                        // 含めてはならない。plain field と同じ statement-end scanner を再利用し、
                        // アクセサ式 event 内部の `{}` は釣り合いを保ったまま、外側 `}` だけを
                        // 除外する。Closes #473 follow-up.
                        var statementEnd = FindCSharpSameLineStatementEnd(patternMatchLine, absoluteStartColumn);
                        if (statementEnd > line.Length)
                            statementEnd = line.Length;
                        if (statementEnd <= absoluteStartColumn)
                            statementEnd = Math.Min(absoluteStartColumn + Math.Max(1, match.Length), line.Length);
                        signature = line[absoluteStartColumn..statementEnd].Trim();
                    }
                    else if (lang == "java"
                        && pattern.BodyStyle == BodyStyle.Brace
                        && bodyStartLine == null)
                    {
                        var statementEnd = FindJavaSameLineStatementEnd(line, absoluteStartColumn);
                        if (statementEnd > line.Length)
                            statementEnd = line.Length;
                        if (statementEnd <= absoluteStartColumn)
                            statementEnd = Math.Min(absoluteStartColumn + Math.Max(1, match.Length), line.Length);
                        signature = line[absoluteStartColumn..statementEnd].Trim();
                    }
                    else if (lang == "csharp"
                        && pattern.Kind == "property"
                        && pattern.BodyStyle == BodyStyle.None)
                    {
                        // For a plain C# field (kind `property`, BodyStyle.None), clamp the
                        // signature to the end of the field's declaration statement (the
                        // terminating `;`, or — if an unbalanced `}` from a same-line
                        // enclosing type body is hit first — the position of that `}`).
                        // This keeps initializer-backed fields such as
                        // `private int _x = 42;` carrying a full `private int _x = 42;`
                        // signature instead of being truncated at `=`, and still prevents
                        // `public int X; } }` inside a same-line nested type from leaking
                        // the trailing `} }` into X's signature (which would break the
                        // same-line `ContainsSymbol` check in `AssignContainers` and make
                        // X attach to `Outer` instead of `Inner`). Closes #400.
                        // C# の通常フィールド（kind `property`、BodyStyle.None）では、signature を
                        // 宣言文の終端（`;` まで、または同一行の囲む型本体の閉じ `}` が先に
                        // 来ればその位置）までで clamp する。`private int _x = 42;` のような
                        // 初期化子付きフィールドでも signature が `=` で切れず完全に残り、かつ
                        // `public int X; } }` のような同一行ネスト型内のフィールドでも
                        // trailing `} }` が signature に混入せず、AssignContainers の
                        // ContainsSymbol 判定が正しく動いて X が Inner ではなく Outer に
                        // ぶら下がる事故が起きない。Closes #400.
                        var statementEnd = FindCSharpSameLineStatementEnd(patternMatchLine, absoluteStartColumn);
                        if (csharpMatchLines != null
                            && ReferenceEquals(patternMatchLine, csharpMatchLines[i]))
                        {
                            // Single-line candidate: translate both endpoints through the
                            // per-line collapsed→raw column map so the raw slice keeps the
                            // `;` terminator and does not absorb a phantom leading `;` from
                            // the next declarator on the same line. Without this, a line like
                            // `public Dictionary<string, int> Map = new(); public int B;`
                            // returned `Map` without `;` and `B` with a leading `;` because
                            // the collapsed-space endpoints no longer lined up with raw
                            // character positions. Closes #400.
                            // 単一行候補では、per-line collapsed→raw map で両端点を raw 列に
                            // 戻してから slice する。こうしないと、
                            // `public Dictionary<string, int> Map = new(); public int B;` のような行で
                            // `Map` の終端 `;` が欠け、後続の `B` の先頭に `;` が混入する。Closes #400.
                            var rawStart = TranslateCSharpCollapsedColumnToRaw(
                                csharpMatchColumnToRaw,
                                i,
                                absoluteStartColumn,
                                line.Length);
                            var rawEnd = TranslateCSharpCollapsedColumnToRaw(
                                csharpMatchColumnToRaw,
                                i,
                                statementEnd,
                                line.Length);
                            if (rawEnd > line.Length)
                                rawEnd = line.Length;
                            if (rawStart > line.Length)
                                rawStart = line.Length;
                            if (rawEnd <= rawStart)
                                rawEnd = Math.Min(rawStart + Math.Max(1, match.Length), line.Length);
                            signature = line[rawStart..rawEnd].Trim();
                        }
                        else
                        {
                            if (statementEnd > line.Length)
                                statementEnd = line.Length;
                            if (statementEnd <= absoluteStartColumn)
                                statementEnd = Math.Min(absoluteStartColumn + Math.Max(1, match.Length), line.Length);
                            signature = line[absoluteStartColumn..statementEnd].Trim();
                        }
                    }
                    else
                    {
                        signature = line[absoluteStartColumn..].Trim();
                    }

                    var suppressJavaStatementSymbol = false;
                    if (lang == "java" && pattern.Kind == "function")
                    {
                        var trimmedSignature = signature.TrimStart();
                        suppressJavaStatementSymbol = name == "switch"
                            || trimmedSignature.StartsWith("return ", StringComparison.Ordinal)
                            || trimmedSignature.StartsWith("switch ", StringComparison.Ordinal)
                            || trimmedSignature.StartsWith("case ", StringComparison.Ordinal);
                    }

                    if (!suppressJavaStatementSymbol)
                    {
                        var declaratorEntries = lang == "csharp"
                            && pattern.Kind == "property"
                            && pattern.BodyStyle == BodyStyle.None
                            ? TryExpandCSharpFieldDeclaratorList(patternMatchLine, absoluteStartColumn, match, pattern.ReturnTypeGroup, name)
                            : null;

                        if (declaratorEntries != null)
                        {
                            foreach (var entry in declaratorEntries)
                            {
                                AddSymbolRecord(
                                    symbols,
                                    cssSeenSymbols,
                                    startLine,
                                    new SymbolRecord
                                    {
                                        FileId = fileId,
                                        Kind = kind,
                                        Name = entry.Name,
                                        Line = startLine,
                                        StartLine = startLine,
                                        StartColumn = csharpSingleLineCollapsedMatch
                                            ? csharpSignatureRawStartColumn
                                            : absoluteStartColumn,
                                        EndLine = Math.Max(startLine, endLine),
                                        BodyStartLine = bodyStartLine,
                                        BodyEndLine = bodyEndLine,
                                        Signature = signature,
                                        Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                        ReturnType = NormalizeMetadata(entry.ReturnType),
                                    },
                                    line);
                            }
                        }
                        else if (rubyAttrNames != null)
                        {
                            var rubyAttrSearchStart = absoluteStartColumn;
                            foreach (var rubyAttrName in rubyAttrNames)
                            {
                                var rubyAttrStartColumn = rubyAttrSearchStart;
                                if (!string.Equals(rubyAttrName, name, StringComparison.Ordinal))
                                {
                                    var foundRubyAttrStart = patternMatchLine.IndexOf(rubyAttrName, rubyAttrSearchStart, StringComparison.Ordinal);
                                    if (foundRubyAttrStart >= 0)
                                        rubyAttrStartColumn = foundRubyAttrStart;
                                }

                                AddSymbolRecord(
                                    symbols,
                                    cssSeenSymbols,
                                    startLine,
                                    new SymbolRecord
                                    {
                                        FileId = fileId,
                                        Kind = kind,
                                        Name = rubyAttrName,
                                        Line = startLine,
                                        StartLine = startLine,
                                        StartColumn = rubyAttrStartColumn,
                                        EndLine = Math.Max(startLine, endLine),
                                        BodyStartLine = bodyStartLine,
                                        BodyEndLine = bodyEndLine,
                                        Signature = signature,
                                        Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                        ReturnType = NormalizeMetadata(rawReturnType),
                                    },
                                    line);

                                rubyAttrSearchStart = rubyAttrStartColumn + Math.Max(1, rubyAttrName.Length);
                            }
                        }
                        else
                        {
                            AddSymbolRecord(
                                symbols,
                                cssSeenSymbols,
                                startLine,
                                new SymbolRecord
                                {
                                    FileId = fileId,
                                    Kind = kind,
                                    Name = name,
                                    Line = startLine,
                                    StartLine = startLine,
                                    StartColumn = csharpSingleLineCollapsedMatch
                                        ? csharpSignatureRawStartColumn
                                        : absoluteStartColumn,
                                    EndLine = Math.Max(startLine, endLine),
                                    BodyStartLine = bodyStartLine,
                                    BodyEndLine = bodyEndLine,
                                    Signature = signature,
                                    Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                    ReturnType = NormalizeMetadata(rawReturnType),
                                },
                                line);
                        }
                    }

                    if (lang == "css"
                        && pattern.Kind == "class"
                        && pattern.BodyStyle == BodyStyle.Brace
                        && cssScannerLines != null)
                    {
                        var openingBraceIndex = cssScannerLines[i].IndexOf('{', absoluteStartColumn);
                        if (openingBraceIndex > absoluteStartColumn)
                        {
                            TryAddCssSelectorListSegments(
                                fileId,
                                line[absoluteStartColumn..openingBraceIndex],
                                cssScannerLines[i][absoluteStartColumn..openingBraceIndex],
                                cssScannerLines,
                                i,
                                openingBraceIndex,
                                patterns,
                                symbols,
                                cssSeenSymbols);
                        }
                    }

                    if (lang == "csharp"
                        && pattern.Kind == "property"
                        && csharpPropertyCandidate.ExpressionBodyEndLineIndex.HasValue)
                    {
                        csharpSuppressedContinuationUntil = Math.Max(csharpSuppressedContinuationUntil, csharpPropertyCandidate.ExpressionBodyEndLineIndex.Value);
                    }

                    if (lang == "csharp"
                        && pattern.Kind is "event" or "delegate"
                        && pattern.BodyStyle == BodyStyle.None
                        && (TryGetCSharpSameLineEventSiblingOffset(patternMatchLine, absoluteStartColumn, out var nextSemicolonSiblingOffset)
                            || TryGetCSharpSameLineSemicolonSiblingOffset(patternMatchLine, absoluteStartColumn, out nextSemicolonSiblingOffset)))
                    {
                        restartPatternScanOffset = nextSemicolonSiblingOffset;
                        break;
                    }

                    if (lang == "java"
                        && pattern.BodyStyle == BodyStyle.Brace
                        && bodyStartLine == null
                        && TryGetJavaSameLineSemicolonSiblingOffset(patternMatchLine, absoluteStartColumn, out var nextJavaSiblingOffset))
                    {
                        // Body-less Java members inside `interface` / `@interface` / abstract-style
                        // declarations can share one physical line (`String[] value(); int age();`).
                        // Restart at the next sibling after the top-level `;` instead of stopping at
                        // the first match, or later members on the same line disappear. Closes #788.
                        // Java の body-less member（`interface` / `@interface` / abstract 形）は
                        // `String[] value(); int age();` のように 1 行へ並ぶ。top-level `;`
                        // の直後から sibling へ再開しないと、同一行の後続 member が最初の 1 個で
                        // 途切れて消える。Closes #788.
                        restartPatternScanOffset = nextJavaSiblingOffset;
                        break;
                    }

                    CollectRecordPrimaryComponentSymbols(
                        fileId,
                        lang,
                        lines,
                        i,
                        absoluteStartColumn,
                        kind,
                        name,
                        pendingRecordPrimaryComponents,
                        symbols);

                    // C# plain-field (kind `property`, BodyStyle.None) matches need their own
                    // advance path. The generic `sameLineEndColumn`-based advance below resolves
                    // to -1 for BodyStyle.None and would set `stopAfterFirstPatternMatch`, which
                    // prevents structural siblings on the same line (e.g. the enclosing
                    // `public class C` in `public class C { public int X; }`) from being
                    // captured by later patterns. Instead, advance past the field terminator
                    // and continue the same-pattern scan so multiple same-line fields are
                    // still collected, and skip the stop flag so later patterns can still run.
                    // Closes #400.
                    // C# 通常フィールド（kind `property`、BodyStyle.None）は専用の前進経路を使う。
                    // 既定の `sameLineEndColumn` ベースの前進は BodyStyle.None では -1 に落ち、
                    // `stopAfterFirstPatternMatch` を立ててしまうため、同一行に存在する構造宣言
                    // （例: `public class C { public int X; }` の外側 class）を後続パターンで
                    // 取得できなくなる。代わりにフィールド終端を越えて同一パターンのスキャンを
                    // 続け、stop フラグを立てずに次のパターンにも機会を残す。Closes #400.
                    if (lang == "csharp"
                        && pattern.Kind == "property"
                        && pattern.BodyStyle == BodyStyle.None)
                    {
                        // Advance past the end of the full field declaration statement
                        // (the top-level `;`, with paren / bracket / brace depth tracking
                        // so `{` / `;` inside an initializer cannot short-circuit the
                        // scan) and continue. Using the statement end rather than the
                        // regex match end keeps later same-line field statements visible
                        // to the same pattern: without this, `A = 1; B;` stopped after
                        // capturing `A` and dropped `B`, and `A, B; C;` stopped after
                        // expanding `A, B` as a declarator list and dropped `C`. It also
                        // avoids the earlier regression where advancing to the match end
                        // (which sits on `=` when the field has an initializer) made the
                        // regex re-match the tail `1, _b, _c =` as a bogus field with
                        // `return_type = "1, _b,"`. If the scanner hits an unbalanced
                        // `}` (the closing brace of the enclosing type body) before a
                        // `;`, break out without setting `stopAfterFirstPatternMatch` so
                        // later unrelated patterns on the same line still get a chance
                        // to run. Closes #400.
                        // フィールド宣言文全体の終端（`;`、paren / bracket / brace 深さを
                        // 追って初期化子内の `{` や `;` で途切れないようにする）まで進めて
                        // 同一パターンで scan を続ける。regex match の末尾ではなく文の
                        // 終端で advance するのが肝心で、これが無いと `A = 1; B;` は
                        // `A` を拾った時点で止まって `B` を取り落とし、`A, B; C;` は
                        // `A, B` を declarator list として展開した時点で `C` を取り落とす。
                        // さらに、match の末尾（初期化子付きなら `=`）まで進めて continue
                        // すると正規表現が残りの `1, _b, _c =` を `return_type = "1, _b,"`
                        // の偽フィールドとして再マッチしていた旧 regression も再発しない。
                        // `;` より先に囲む型本体の閉じ `}`（深さ 0）に到達した場合は、
                        // `stopAfterFirstPatternMatch` を立てずに break して同一行の他
                        // パターン（class 等）へ機会を残す。Closes #400.
                        var statementEnd = FindCSharpSameLineStatementEnd(patternMatchLine, absoluteStartColumn);
                        if (statementEnd < patternMatchLine.Length
                            && patternMatchLine[statementEnd] == '}')
                        {
                            break;
                        }
                        // Only continue the same-pattern same-line scan when the regex
                        // ran on a per-line single-line candidate (patternMatchLine ===
                        // csharpMatchLines[i]). For multi-line merged candidates,
                        // BuildCSharpPropertyMatchLine joined the header line with one
                        // or more continuation lines, so absoluteStartColumn sits in
                        // the merged-string column domain and does not line up with
                        // lines[i]'s raw columns. Continuing past statementEnd into a
                        // second regex hit would then feed a column > lines[i].Length
                        // into BuildCSharpMultilineSignature (which slices
                        // lines[startLineIndex][startColumn..]) and crash indexing with
                        // `startIndex cannot be larger than length of string`. The
                        // continuation line is revisited by the outer physical-line
                        // loop anyway (csharpSuppressedContinuationUntil is only bumped
                        // for expression-bodied properties), so for multi-line merged
                        // candidates we break here and let the outer loop handle any
                        // additional fields on that line. Closes #400.
                        // same-pattern での同一行 scan 継続は、per-line の単一行候補
                        // （patternMatchLine === csharpMatchLines[i]）のときだけ許す。
                        // BuildCSharpPropertyMatchLine が header 行と continuation 行を
                        // マージした複数行候補では、absoluteStartColumn がマージ後文字列の
                        // 列を指しており lines[i] の raw 列として使えない。この状態で
                        // statementEnd を越えて 2 個目の regex ヒットに進むと、
                        // BuildCSharpMultilineSignature の lines[startLineIndex][startColumn..]
                        // で範囲外アクセスとなり
                        // 「startIndex cannot be larger than length of string」で indexing が
                        // 落ちる。continuation 行は外側の物理行ループが再訪する
                        // （csharpSuppressedContinuationUntil は expression-bodied property
                        // でしか進まない）ため、複数行候補ではここで break して後続の
                        // 同一行フィールド抽出を外側ループに任せる。Closes #400.
                        if (csharpMatchLines == null
                            || !ReferenceEquals(patternMatchLine, csharpMatchLines[i]))
                        {
                            break;
                        }
                        var advance = statementEnd;
                        if (advance <= lineOffset)
                            advance = lineOffset + 1;
                        if (advance >= patternMatchLine.Length)
                            break;
                        lineOffset = advance;
                        continue;
                    }

                    if (!CanContinueScanningSameLineBraceBody(lang, kind, pattern.BodyStyle, bodyEndLine, startLine, sameLineEndColumn, absoluteStartColumn))
                    {
                        if (lang == "csharp"
                            && pattern.BodyStyle == BodyStyle.Brace
                            && bodyStartLine == startLine
                            && kind is "class" or "struct" or "interface" or "enum" or "namespace")
                        {
                            // Hybrid same-line C# type headers can open the body on the header
                            // line and still close on a later line (`class C { int P { get; }`
                            // + next-line `}`). They are not compact same-line bodies, so the
                            // generic same-line brace-body path does not restart inside them.
                            // Explicitly restart just after the opening `{` so the first member
                            // that shares the header line is still visible to the full pattern
                            // list. Closes #580.
                            // ハイブリッドな C# の same-line 型ヘッダは、本体開始 `{` がヘッダ行に
                            // ありつつ閉じ `}` は後続行に置かれうる (`class C { int P { get; }`
                            // + 次行 `}`)。これは compact な same-line body ではないため、
                            // 既定の same-line brace-body 経路だけでは本体内へ再開できない。
                            // そこで開始 `{` の直後から明示的に再開し、ヘッダ行を共有する最初の
                            // member も通常の pattern 列で拾えるようにする。Closes #580.
                            var nextHeaderLineMemberOffset = FindNextSameLineNonClosingBraceStatementStart(
                                matchLine,
                                absoluteStartColumn + Math.Max(1, match.Length),
                                lang);
                            if (nextHeaderLineMemberOffset > absoluteStartColumn
                                && nextHeaderLineMemberOffset < matchLine.Length)
                            {
                                restartPatternScanOffset = nextHeaderLineMemberOffset;
                                break;
                            }
                        }

                        if (lang == "csharp"
                            && sameLineEndColumn >= absoluteStartColumn
                            && CanRestartCSharpSameLineSiblingScan(kind))
                        {
                            // Compact same-line C# members form a sibling stream rather than a
                            // single terminal match: after `event E;`, `void M();`, or
                            // `int P { get; set; }`, later same-line declarations still need
                            // to reach earlier patterns in the list. Restart from the next
                            // top-level statement boundary so mixed-kind siblings like
                            // `event + property`, `method + property`, and `property + event`
                            // are all visible. When there is no later statement, keep the old
                            // stop-after-first-match behavior to avoid reopening duplicate
                            // paths on ordinary single-declaration lines. Closes #470 / #473.
                            // 同一行のコンパクトな C# member は 1 回限りの terminal match ではなく、
                            // sibling 宣言のストリームとして扱う。`event E;` や `void M();`、
                            // `int P { get; set; }` の後ろに続く宣言も、pattern 列の先頭側にある
                            // property などへ到達できる必要がある。そこで次の top-level 文境界から
                            // pattern 列全体を再走査し、`event + property`、`method + property`、
                            // `property + event` のような mixed-kind sibling をすべて可視化する。
                            // 後続宣言が無い行では従来どおり stop-after-first-match を維持し、
                            // 通常の単独宣言行で duplicate 経路を再び開かない。Closes #470 / #473.
                            if (csharpSingleLineCollapsedMatch && sameLineEndUsesRawColumns)
                            {
                                var rawNextSiblingOffset = FindNextSameLineNonClosingBraceStatementStart(line, sameLineEndColumn + 1, lang);
                                if (rawNextSiblingOffset > sameLineEndColumn)
                                {
                                    restartPatternScanOffset = TranslateCSharpRawColumnToCollapsed(
                                        csharpMatchColumnToRaw,
                                        i,
                                        rawNextSiblingOffset,
                                        matchLine.Length,
                                        line.Length);
                                    break;
                                }
                            }
                            else
                            {
                                var nextSiblingOffset = FindNextSameLineNonClosingBraceStatementStart(matchLine, sameLineEndColumn + 1, lang);
                                if (nextSiblingOffset > sameLineEndColumn
                                    && nextSiblingOffset < matchLine.Length)
                                {
                                    restartPatternScanOffset = nextSiblingOffset;
                                    break;
                                }
                            }
                        }

                        // Batch `set` assignments can legitimately repeat on a single line via
                        // `&` command-chaining (`set A=1 & set B=2`), parenthesized grouping
                        // (`if ... ( set P=1 ) else set Q=2`), or `for`-loop bodies
                        // (`for %%I in (1) do set LOOPVAR=%%I`). The brace-body rescan path
                        // above is JS/TS/CSS/C#-only, so drive the advance explicitly for the
                        // batch property pattern instead of short-circuiting after the first
                        // match. Forward progress is guaranteed because `match.Length >= 1`
                        // (the regex requires a literal `set\s+NAME=` tail).
                        // batch の `set` 代入は `&` 連結や `( ... ) else ... `、`for ... do ...` で
                        // 1 行に複数回現れうる。上の brace-body 再スキャンは JS/TS/CSS/C# 限定なので、
                        // batch の property パターンだけは explicit に advance して追加マッチも拾う。
                        // 前進は `match.Length >= 1` (正規表現が `set\s+NAME=` を要求するため) で保証される。
                        if (lang == "batch"
                            && pattern.BodyStyle == BodyStyle.None
                            && pattern.Kind == "property")
                        {
                            var nextBatchOffset = absoluteStartColumn + Math.Max(1, match.Length);
                            if (nextBatchOffset <= lineOffset)
                                break;
                            lineOffset = nextBatchOffset;
                            continue;
                        }

                        // Stop after first match per line to avoid duplicate symbols
                        // (e.g. C# method pattern + constructor pattern both matching)
                        // 1行につき最初のマッチのみ採用し重複を防ぐ
                        stopAfterFirstPatternMatch = true;
                        break;
                    }

                    // For C# class-like kinds with a same-line brace body, step into the body
                    // (advance just past the match header) instead of jumping past the closing
                    // `}`. This lets nested same-line declarations be captured, e.g.
                    // `public class Outer { public class Inner { public int X; } }` matches
                    // Outer and Inner, with X correctly attached to Inner. JavaScript/TypeScript
                    // does not need this because class-body members there are extracted via the
                    // separate JS/TS lexer/state machine; the brace-skip path only handles
                    // same-line siblings like `class A {} class B {}`. Closes #400.
                    // C# の class 系 kind は同一行の `{...}` 本体を飛び越えず、ヘッダ直後へ
                    // 進めて本体内部の宣言（例: `public class Outer { public class Inner { ... } }`
                    // の Inner）を拾えるようにする。JavaScript/TypeScript は class body の
                    // member 抽出を専用 lexer/state machine で行うため従来通り終端の後ろへ
                    // 進め、同一行 sibling（`class A {} class B {}` など）だけを扱う。Closes #400.
                    var sameLineRestartComparisonColumn = csharpSingleLineCollapsedMatch && sameLineEndUsesRawColumns
                        ? TranslateCSharpRawColumnToCollapsed(
                            csharpMatchColumnToRaw,
                            i,
                            sameLineEndColumn,
                            matchLine.Length,
                            line.Length)
                        : sameLineEndColumn;
                    if (CanStepIntoSameLineTypeBody(lang, kind))
                    {
                        var nextTypeBodyOffset = FindNextSameLineNonClosingBraceStatementStart(
                            matchLine,
                            absoluteStartColumn + Math.Max(1, match.Length),
                            lang);
                        if (nextTypeBodyOffset > absoluteStartColumn
                            && nextTypeBodyOffset < sameLineRestartComparisonColumn
                            && (nextTypeBodyOffset >= matchLine.Length || matchLine[nextTypeBodyOffset] != '}'))
                        {
                            restartPatternScanOffset = nextTypeBodyOffset;
                            break;
                        }
                    }

                    var nextSameLineOffset = -1;
                    if (csharpSingleLineCollapsedMatch && sameLineEndUsesRawColumns)
                    {
                        var rawNextSameLineOffset = FindNextSameLineNonClosingBraceStatementStart(line, sameLineEndColumn + 1, lang);
                        if (rawNextSameLineOffset > sameLineEndColumn)
                        {
                            nextSameLineOffset = TranslateCSharpRawColumnToCollapsed(
                                csharpMatchColumnToRaw,
                                i,
                                rawNextSameLineOffset,
                                matchLine.Length,
                                line.Length);
                        }
                    }
                    else
                    {
                        nextSameLineOffset = FindNextSameLineNonClosingBraceStatementStart(matchLine, sameLineEndColumn + 1, lang);
                    }
                    var sameLineAdvanceComparisonColumn = sameLineRestartComparisonColumn;
                    if (CanStepIntoSameLineTypeBody(lang, kind)
                        && nextSameLineOffset > sameLineAdvanceComparisonColumn
                        && nextSameLineOffset < matchLine.Length
                        && matchLine[nextSameLineOffset] != '}')
                    {
                        restartPatternScanOffset = nextSameLineOffset;
                        break;
                    }
                    if (lang == "csharp"
                        && kind == "property"
                        && pattern.BodyStyle == BodyStyle.Brace
                        && nextSameLineOffset > sameLineAdvanceComparisonColumn
                        && nextSameLineOffset < matchLine.Length)
                    {
                        // A same-line brace-body property that is followed by another sibling
                        // declaration (`P { get; set; } public void M() { }`) must hand control
                        // back to the whole pattern list at the next statement start, otherwise
                        // earlier rows like the C# method regex never get a chance to see the
                        // trailing sibling and mixed-kind lines lose one side.
                        // Closes #473 follow-up.
                        // 後続 sibling 宣言を伴う same-line brace-body property
                        // (`P { get; set; } public void M() { }`) は、次の文開始位置から
                        // pattern 全体へ制御を戻す必要がある。そうしないと、C# method regex
                        // のような earlier row が後続 sibling を見られず、mixed-kind の
                        // 同一行で片側が欠落する。Closes #473 follow-up.
                        restartPatternScanOffset = nextSameLineOffset;
                        break;
                    }

                    lineOffset = nextSameLineOffset;
                }

                if (restartPatternScanOffset >= 0 || stopAfterFirstPatternMatch)
                    break;
                }

                if (restartPatternScanOffset >= 0)
                {
                    patternStartOffset = restartPatternScanOffset;
                    continue;
                }

                break;
            }

            if (lang == "css" && cssScannerLine != null)
            {
                foreach (Match match in CssInlineCustomPropertyRegex.Matches(cssScannerLine))
                {
                    var propertyName = match.Groups["name"].Value.Trim();
                    if (propertyName.Length == 0)
                        continue;

                    AddSymbolRecord(
                        symbols,
                        cssSeenSymbols,
                        i + 1,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "property",
                            Name = propertyName,
                            Line = i + 1,
                            StartLine = i + 1,
                            EndLine = i + 1,
                            Signature = line.Trim(),
                        });
                }

                ExtractCssInlineGroupingSelectors(
                    fileId,
                    line,
                    cssScannerLine,
                    cssScannerLines!,
                    i,
                    patterns,
                    symbols,
                    cssSeenSymbols);
            }
        }

        if (lang is "javascript" or "typescript")
            ExtractJavaScriptTypeScriptBareMethods(fileId, lang, lines, symbols, privateScopeColumns!);
        else if (lang == "csharp")
            ExtractCSharpEnumMembers(fileId, lines, structuralLines, csharpMatchLines!, symbols);
        else if (lang == "java")
        {
            ExtractJavaEnumMembers(fileId, lines, symbols);
            ExtractJavaCompactConstructors(fileId, lines, symbols);
            ExtractJavaModuleDirectiveSymbols(fileId, lines, structuralLines, symbols);
        }

        if (string.Equals(originalLang, "svelte", StringComparison.Ordinal))
            ExtractSvelteReactiveSymbols(fileId, lines, symbols);
        AssignContainers(symbols, lines, csharpLineStartStates);
        MaterializeRecordPrimaryComponentSymbols(symbols, pendingRecordPrimaryComponents);
        NormalizeKotlinSecondaryConstructorNames(symbols);
        PopulateDeclaredContainerQualifiedNames(symbols);
        return symbols;
    }

    private static void ExtractSvelteReactiveSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var match = SvelteReactivePropertyRegex.Match(lines[i]);
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = name,
                Line = i + 1,
                StartLine = i + 1,
                EndLine = i + 1,
                Signature = lines[i].Trim(),
            });
        }
    }

    private static void ExtractJavaModuleDirectiveSymbols(long fileId, string[] rawLines, string[] structuralLines, List<SymbolRecord> symbols)
    {
        var moduleDeclarations = symbols
            .Where(symbol => symbol.Kind == "namespace" && symbol.BodyStartLine != null && symbol.BodyEndLine != null)
            .OrderBy(symbol => symbol.StartLine)
            .ThenByDescending(symbol => symbol.EndLine)
            .ToList();

        foreach (var moduleDeclaration in moduleDeclarations)
        {
            foreach (var statement in EnumerateJavaModuleDirectiveStatements(rawLines, structuralLines, moduleDeclaration))
            {
                if (!TryParseJavaModuleDirectiveName(statement.StructuralText, out var name))
                    continue;

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    statement.StartLine,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "import",
                        Name = name,
                        Line = statement.StartLine,
                        StartLine = statement.StartLine,
                        StartColumn = statement.StartColumn,
                        EndLine = statement.EndLine,
                        Signature = statement.Signature,
                    },
                    rawLines[statement.StartLine - 1]);
            }
        }
    }

    private static IEnumerable<JavaModuleDirectiveStatement> EnumerateJavaModuleDirectiveStatements(
        string[] rawLines,
        string[] structuralLines,
        SymbolRecord moduleDeclaration)
    {
        var bodyStartLine = moduleDeclaration.BodyStartLine.GetValueOrDefault();
        var bodyEndLine = moduleDeclaration.BodyEndLine.GetValueOrDefault();
        if (bodyStartLine <= 0 || bodyEndLine < bodyStartLine)
            yield break;

        var startLineIndex = bodyStartLine - 1;
        var endLineIndex = Math.Min(bodyEndLine, rawLines.Length) - 1;
        if (startLineIndex < 0 || startLineIndex >= rawLines.Length || endLineIndex < startLineIndex)
            yield break;

        var rawBuilder = new StringBuilder();
        var statementStartLine = -1;
        var statementStartColumn = -1;

        for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            var rawLine = rawLines[lineIndex];
            var structuralLine = structuralLines[lineIndex];
            var sliceStart = 0;
            var sliceEnd = rawLine.Length;

            if (lineIndex == startLineIndex)
            {
                var openingBrace = structuralLine.IndexOf('{');
                if (openingBrace >= 0)
                    sliceStart = Math.Min(openingBrace + 1, rawLine.Length);
            }

            if (lineIndex == endLineIndex && bodyEndLine == moduleDeclaration.EndLine)
            {
                var closingBrace = structuralLine.LastIndexOf('}');
                if (closingBrace >= 0)
                    sliceEnd = Math.Min(closingBrace, rawLine.Length);
            }

            if (sliceStart >= sliceEnd)
                continue;

            var rawSlice = rawLine[sliceStart..sliceEnd];
            var structuralSlice = structuralLine[sliceStart..sliceEnd];
            var offset = 0;
            while (offset < structuralSlice.Length)
            {
                if (rawBuilder.Length == 0)
                {
                    offset = SkipWhitespace(structuralSlice, offset);
                    if (offset >= structuralSlice.Length)
                        break;

                    if (!TryGetJavaModuleDirectiveKeyword(structuralSlice, offset, out _))
                        break;

                    statementStartLine = lineIndex + 1;
                    statementStartColumn = sliceStart + offset;
                }

                var semicolonIndex = structuralSlice.IndexOf(';', offset);
                var segmentEnd = semicolonIndex >= 0
                    ? semicolonIndex + 1
                    : structuralSlice.Length;
                rawBuilder.Append(rawSlice, offset, segmentEnd - offset);

                if (semicolonIndex >= 0)
                {
                    var structuralText = CollapseWhitespaceRuns(MaskJavaModuleDirectiveComments(rawBuilder.ToString()));
                    yield return new JavaModuleDirectiveStatement(
                        statementStartLine,
                        statementStartColumn,
                        lineIndex + 1,
                        NormalizeJavaModuleDirectiveSignature(rawBuilder.ToString()),
                        structuralText);
                    rawBuilder.Clear();
                    statementStartLine = -1;
                    statementStartColumn = -1;
                    offset = semicolonIndex + 1;
                    continue;
                }

                rawBuilder.Append('\n');
                break;
            }
        }
    }

    private static bool TryParseJavaModuleDirectiveName(string statement, out string name)
    {
        name = string.Empty;
        var match = JavaModuleRequiresDirectiveRegex.Match(statement);
        if (!match.Success)
            match = JavaModuleExportsOrOpensDirectiveRegex.Match(statement);
        if (!match.Success)
            match = JavaModuleUsesOrProvidesDirectiveRegex.Match(statement);
        if (!match.Success)
            return false;

        name = match.Groups["name"].Value.Trim();
        return name.Length > 0;
    }

    private static bool TryGetJavaModuleDirectiveKeyword(string line, int offset, out string keyword)
    {
        foreach (var candidate in JavaModuleDirectiveKeywords)
        {
            if (!line.AsSpan(offset).StartsWith(candidate, StringComparison.Ordinal))
                continue;

            var boundaryIndex = offset + candidate.Length;
            if (boundaryIndex < line.Length && (char.IsLetterOrDigit(line[boundaryIndex]) || line[boundaryIndex] == '_'))
                continue;

            keyword = candidate;
            return true;
        }

        keyword = string.Empty;
        return false;
    }

    private static string NormalizeJavaModuleDirectiveSignature(string statement)
    {
        return CollapseWhitespaceRuns(statement);
    }

    private static string MaskJavaModuleDirectiveComments(string text)
    {
        if (text.Length == 0)
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '/' && index + 1 < text.Length)
            {
                if (text[index + 1] == '/')
                {
                    if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                        builder.Append(' ');

                    index += 2;
                    while (index < text.Length && text[index] != '\n')
                        index++;
                    if (index < text.Length && text[index] == '\n')
                        builder.Append('\n');
                    continue;
                }

                if (text[index + 1] == '*')
                {
                    if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                        builder.Append(' ');

                    index += 2;
                    while (index < text.Length)
                    {
                        if (text[index] == '\n')
                            builder.Append('\n');

                        if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '/')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }
                    continue;
                }
            }

            builder.Append(text[index]);
        }

        return builder.ToString();
    }

    private static string CollapseWhitespaceRuns(string text)
    {
        if (text.Length == 0)
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    private readonly record struct JavaModuleDirectiveStatement(
        int StartLine,
        int StartColumn,
        int EndLine,
        string Signature,
        string StructuralText);

    private static void ExtractCSharpEnumMembers(long fileId, string[] rawLines, string[] enumScannerLines, string[] csharpMatchLines, List<SymbolRecord> symbols)
    {
        var enumDeclarations = symbols
            .Where(s => s.Kind == "enum" && s.BodyStartLine != null && s.BodyEndLine != null)
            .OrderBy(s => s.StartLine)
            .ThenByDescending(s => s.EndLine)
            .ToList();

        foreach (var enumSymbol in enumDeclarations)
        {
            if (!TryFindCSharpEnumBodyBounds(rawLines, csharpMatchLines, enumSymbol, out var bodyStartLineIndex, out var bodyStartColumn, out var bodyEndLineIndex, out var bodyEndColumnExclusive))
                continue;

            ExtractCSharpEnumMembersFromBody(
                fileId,
                enumSymbol,
                rawLines,
                enumScannerLines,
                bodyStartLineIndex,
                bodyStartColumn,
                bodyEndLineIndex,
                bodyEndColumnExclusive,
                symbols);
        }
    }

    private static bool TryFindCSharpEnumBodyBounds(
        string[] rawLines,
        string[] csharpMatchLines,
        SymbolRecord enumSymbol,
        out int bodyStartLineIndex,
        out int bodyStartColumn,
        out int bodyEndLineIndex,
        out int bodyEndColumnExclusive)
    {
        bodyStartLineIndex = 0;
        bodyStartColumn = 0;
        bodyEndLineIndex = 0;
        bodyEndColumnExclusive = 0;

        var declarationLineIndex = enumSymbol.StartLine - 1;
        if (declarationLineIndex < 0 || declarationLineIndex >= csharpMatchLines.Length)
            return false;

        var declarationLine = csharpMatchLines[declarationLineIndex];
        var declarationStartColumn = FindCSharpDeclarationStartColumn(rawLines[declarationLineIndex], enumSymbol.Signature);
        if (declarationStartColumn < 0 || declarationStartColumn >= declarationLine.Length)
            declarationStartColumn = 0;

        var declarationMatch = CSharpEnumDeclarationRegex.Match(declarationLine[declarationStartColumn..]);
        if (!declarationMatch.Success)
            return false;

        var depth = 0;
        var opened = false;
        var scanEndLineIndex = Math.Min(enumSymbol.EndLine, csharpMatchLines.Length) - 1;
        for (int lineIndex = declarationLineIndex; lineIndex <= scanEndLineIndex; lineIndex++)
        {
            var line = csharpMatchLines[lineIndex];
            var scanStartColumn = lineIndex == declarationLineIndex
                ? declarationStartColumn + declarationMatch.Index
                : 0;

            for (int column = scanStartColumn; column < line.Length; column++)
            {
                var ch = line[column];
                if (ch == '{')
                {
                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLineIndex = lineIndex;
                        bodyStartColumn = column + 1;
                    }
                }
                else if (ch == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                    {
                        bodyEndLineIndex = lineIndex;
                        bodyEndColumnExclusive = column;
                        return true;
                    }
                }
            }
        }

        if (!opened)
            return false;

        bodyEndLineIndex = scanEndLineIndex;
        bodyEndColumnExclusive = csharpMatchLines[scanEndLineIndex].Length;
        return true;
    }

    private static void ExtractCSharpEnumMembersFromBody(
        long fileId,
        SymbolRecord enumSymbol,
        string[] rawLines,
        string[] enumScannerLines,
        int bodyStartLineIndex,
        int bodyStartColumn,
        int bodyEndLineIndex,
        int bodyEndColumnExclusive,
        List<SymbolRecord> symbols)
    {
        (int LineIndex, int Column)? currentStart = null;
        var parenDepth = 0;
        var bracketDepth = 0;
        var lineIndex = bodyStartLineIndex;
        var column = bodyStartColumn;

        while (lineIndex <= bodyEndLineIndex)
        {
            var maskedLine = enumScannerLines[lineIndex];
            var lineScanStartColumn = lineIndex == bodyStartLineIndex
                ? Math.Min(bodyStartColumn, maskedLine.Length)
                : 0;
            var scanEndColumnExclusive = lineIndex == bodyEndLineIndex
                ? Math.Min(bodyEndColumnExclusive, maskedLine.Length)
                : maskedLine.Length;

            if (column >= scanEndColumnExclusive)
            {
                lineIndex++;
                column = 0;
                continue;
            }

            if (TryGetFirstNonWhitespaceColumn(maskedLine, lineScanStartColumn, scanEndColumnExclusive, out var firstNonWhitespaceColumn)
                && column == firstNonWhitespaceColumn
                && maskedLine[column] == '#')
            {
                currentStart = null;
                parenDepth = 0;
                bracketDepth = 0;
                lineIndex++;
                column = 0;
                continue;
            }

            var ch = maskedLine[column];
            if (currentStart == null)
            {
                if (char.IsWhiteSpace(ch) || ch == ',')
                {
                    column++;
                    continue;
                }

                if (ch == '['
                    && TrySkipLeadingCSharpAttributeListsInEnumBody(
                        enumScannerLines,
                        lineIndex,
                        column,
                        bodyEndLineIndex,
                        bodyEndColumnExclusive,
                        out var nextPosition))
                {
                    lineIndex = nextPosition.LineIndex;
                    column = nextPosition.Column;
                    continue;
                }

                if (ch == '['
                    && TryRecoverBrokenCSharpEnumAttributeInBody(
                        enumScannerLines,
                        lineIndex,
                        bodyEndLineIndex,
                        bodyEndColumnExclusive,
                        out var recoveredPosition))
                {
                    lineIndex = recoveredPosition.LineIndex;
                    column = recoveredPosition.Column;
                    continue;
                }

                currentStart = (lineIndex, column);
            }

            if (ch == '(')
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
            else if (ch == ',' && parenDepth == 0 && bracketDepth == 0 && currentStart != null)
            {
                TryAddCSharpEnumMemberFromSpan(fileId, enumSymbol, rawLines, enumScannerLines, currentStart.Value, (lineIndex, column + 1), symbols);
                currentStart = null;
            }

            column++;
        }

        if (currentStart != null)
            TryAddCSharpEnumMemberFromSpan(fileId, enumSymbol, rawLines, enumScannerLines, currentStart.Value, (bodyEndLineIndex, bodyEndColumnExclusive), symbols);
    }

    // Java identifier start: Unicode letter / letter-number / underscore / dollar. Continue chars also
    // allow digits, connector punctuation, and combining marks so enum members like `RÉSUMÉ` survive intact.
    // Java 識別子の先頭: Unicode の letter / letter-number / underscore / dollar。
    // 継続文字は数字・connector punctuation・結合文字も許可し、`RÉSUMÉ` のような enum member を切らない。
    private static readonly Regex JavaEnumMemberNameRegex = new(
        @"(?<name>[\p{L}\p{Nl}_$][\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}_$]*)",
        RegexOptions.Compiled);

    // Line-based fallback used only when the primary body scanner exits with unbalanced delimiters,
    // which signals malformed input. Mirrors the pre-body-scope regex so mid-edit states still emit
    // obvious uppercase-identifier members.
    // malformed 入力を primary scanner が検知した場合に限って使う line-based fallback。以前の行単位正規表現と同等。
    private static readonly Regex JavaEnumMemberLineFallbackRegex = new(
        @"^\s+(?<name>[A-Z]\w*)\s*(?:\([^)]*\))?\s*(?:,|\{|;)\s*$",
        RegexOptions.Compiled);

    // Raw-text / RCDATA element names that must be masked before the symbol state
    // machine runs. `<script>` / `<style>` are raw-text, `<textarea>` / `<title>`
    // are RCDATA. Using a HashSet keeps the mask state machine branch-free per
    // opening tag.
    // state machine がシンボル抽出する前にマスクしなければならない raw-text / RCDATA
    // 要素名。`<script>` / `<style>` は raw-text、`<textarea>` / `<title>` は RCDATA。
    private static readonly HashSet<string> HtmlRawTextElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title",
    };

    // Native HTML/SVG/MathML tag names that happen to contain a hyphen but are
    // reserved by the spec, so they must NOT be treated as custom-element class
    // symbols. See https://html.spec.whatwg.org/multipage/custom-elements.html#valid-custom-element-name
    // for the PotentialCustomElementName / reserved names production.
    // ハイフンを含むが仕様で予約されている標準 HTML / SVG / MathML タグ名。custom
    // element の class シンボルとして扱ってはいけない。
    private static readonly HashSet<string> HtmlReservedHyphenatedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "annotation-xml",
        "color-profile",
        "font-face",
        "font-face-src",
        "font-face-uri",
        "font-face-format",
        "font-face-name",
        "missing-glyph",
    };

    private static string MaskHtmlRawTextRegions(string text)
    {
        // Walk `text` character by character, masking the body of raw-text /
        // RCDATA elements (`<script>` / `<style>` / `<textarea>` / `<title>`)
        // and `<!-- ... -->` comments. Regex-based masking could not reliably
        // handle cases like `<script data-note="a > b" src="/app.js">` (quoted
        // `>` inside an attribute terminated the naive `[^>]*` pattern) or
        // `<script data-note="oops\nconst tpl = '<evil-card id="phantom">';`
        // (unterminated quote let nested `"..."` pairs match across script
        // body content). The state machine uses the same quote-handling logic
        // as the symbol extractor's state machine so both agree on where a
        // raw-text opener ends, and falls back to masking through EOF when an
        // opener is unterminated — that matches HTML's spec behavior (an
        // unclosed raw-text element swallows everything until EOF or
        // `</name>`) and prevents script-body content from leaking as phantom
        // HTML symbols.
        // マスクを正規表現ではなく文字単位の state machine で行い、`<script>` /
        // `<style>` / `<textarea>` / `<title>` の本体と `<!-- ... -->` コメントを
        // マスクする。正規表現だと `<script data-note="a > b" src="/app.js">` の
        // ように属性値内の引用符付き `>` で早期終了したり、未終端引用符を持つ
        // `<script data-note="oops\nconst tpl = '<evil-card id="phantom">';`
        // のような入力で引用符ペアが script 本体をまたいで誤マッチする問題が
        // あった。state machine は symbol extractor と同じ引用符処理を共有して
        // 開始タグの境界を一致させ、開始タグが未終端の場合は EOF までマスクする
        // （仕様上、未閉鎖 raw-text 要素は EOF か `</name>` まで本体を飲むため）。
        var chars = text.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] != '<')
            {
                i++;
                continue;
            }

            // `<!-- ... -->` comment. Closing `-->` is optional (masked through
            // EOF) so mid-edit working-tree HTML with an unclosed comment does
            // not leak following tags as phantom symbols.
            // 未閉鎖コメントは EOF までマスクし、以降のタグが phantom にならないようにする。
            if (i + 3 < chars.Length && chars[i + 1] == '!' && chars[i + 2] == '-' && chars[i + 3] == '-')
            {
                var commentClose = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                var commentEnd = commentClose < 0 ? chars.Length : commentClose + 3;
                BlankPreservingNewlines(chars, i, commentEnd);
                i = commentEnd;
                continue;
            }

            // `<![CDATA[ ... ]]>` section. In XHTML / SVG / MathML these are
            // valid and must not leak their content as phantom tags. The
            // terminator is specifically `]]>`, not the first `>`, so a naive
            // `IndexOf('>', ...)` would stop early on inner markup and let the
            // remaining CDATA body be parsed as real HTML. Unterminated CDATA
            // masks through EOF, matching the comment-branch behavior.
            // `<![CDATA[ ... ]]>` は XHTML / SVG / MathML で有効。終端は
            // `]]>` のみであり、単純な `>` 検索では内部のタグで早期終了して
            // 残り本体が phantom として抽出される。未閉鎖は EOF までマスクする。
            if (i + 8 < chars.Length && chars[i + 1] == '!' && chars[i + 2] == '[' &&
                chars[i + 3] == 'C' && chars[i + 4] == 'D' && chars[i + 5] == 'A' &&
                chars[i + 6] == 'T' && chars[i + 7] == 'A' && chars[i + 8] == '[')
            {
                var cdataClose = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                var cdataEnd = cdataClose < 0 ? chars.Length : cdataClose + 3;
                BlankPreservingNewlines(chars, i, cdataEnd);
                i = cdataEnd;
                continue;
            }

            // Other `<!...>` declarations (DOCTYPE and similar). Content
            // between `<!` and the first unquoted `>` is a declaration, not a
            // tag body, so mask it to prevent attribute-lookalike tokens from
            // being emitted as symbols. Quoted values inside DOCTYPE PUBLIC /
            // SYSTEM are walked via FindHtmlQuoteClose so embedded `>` does
            // not terminate the declaration early.
            // DOCTYPE などの `<!...>` 宣言は `FindHtmlTagOpenerEnd` で閉じ `>` を
            // 探して丸ごとマスクする。引用符内の `>` で早期終了しないようにする。
            if (i + 1 < chars.Length && chars[i + 1] == '!')
            {
                var declEnd = FindHtmlTagOpenerEnd(text, i);
                if (declEnd < 0)
                {
                    BlankPreservingNewlines(chars, i, chars.Length);
                    i = chars.Length;
                    continue;
                }
                BlankPreservingNewlines(chars, i, declEnd + 1);
                i = declEnd + 1;
                continue;
            }

            // Processing instructions `<?...?>` (XML prolog, XSLT PIs, PHP
            // short tags embedded in XHTML). Terminator is `?>`, not bare `>`.
            // Content between can include tag-like markup that must not leak.
            // `<?...?>` 処理命令。終端は `?>` で、内部のタグ様テキストは漏らさない。
            if (i + 1 < chars.Length && chars[i + 1] == '?')
            {
                var piClose = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                var piEnd = piClose < 0 ? chars.Length : piClose + 2;
                BlankPreservingNewlines(chars, i, piEnd);
                i = piEnd;
                continue;
            }

            var rawName = TryMatchHtmlRawTextOpenerName(text, i);
            if (rawName != null)
            {
                // Walk the opening tag to find its closing `>`. Multi-line
                // quoted attribute values are allowed; the helper only returns
                // -1 if the opener cannot be closed before EOF.
                // 開始タグの `>` を探す。複数行に跨る引用符付き属性値は OK。
                // EOF 前に閉じられない場合のみ -1 を返す。
                var openerEnd = FindHtmlTagOpenerEnd(text, i);
                if (openerEnd < 0)
                {
                    // Unterminated raw-text opener. Mask from `<` to EOF — this
                    // matches HTML spec behavior and prevents script-body
                    // content from leaking as phantom symbols.
                    // 開始タグが未終端の場合、仕様どおり EOF までマスクする。
                    BlankPreservingNewlines(chars, i, chars.Length);
                    i = chars.Length;
                    continue;
                }

                var bodyStart = openerEnd + 1;
                var closeIdx = FindHtmlRawTextClose(text, bodyStart, rawName);
                var bodyEnd = closeIdx < 0 ? chars.Length : closeIdx;
                BlankPreservingNewlines(chars, bodyStart, bodyEnd);

                if (closeIdx < 0)
                {
                    i = chars.Length;
                    continue;
                }

                var closeGt = text.IndexOf('>', closeIdx);
                i = closeGt < 0 ? chars.Length : closeGt + 1;
                continue;
            }

            // Non-raw-text tag opener (including closing tags `</...`). Walk
            // past the whole opener so quoted attribute values like
            // `<div title="<script>">` or `<div title="<!--">` do not re-enter
            // the raw-text / comment branches on the next character and get
            // misidentified as raw-text/comment openers. Without this skip,
            // the char-by-char scan would re-encounter `<script>` / `<!--`
            // inside the attribute value and mask through EOF.
            // raw-text 以外のタグ opener（`</...` を含む）に遭遇したら、opener 全体を
            // 飛ばして属性値内の `<script>` / `<!--` が次の文字で raw-text / comment
            // として再解釈されないようにする。これを入れないと属性値内の `<script>`
            // を raw-text 本体マスク対象と誤認して以降の兄弟タグを全部飲み込む。
            if (i + 1 < chars.Length && (IsHtmlTagNameStart(chars[i + 1]) || chars[i + 1] == '/'))
            {
                var openerEnd = FindHtmlTagOpenerEnd(text, i);
                if (openerEnd >= 0)
                {
                    i = openerEnd + 1;
                    continue;
                }

                // Unterminated non-raw-text tag opener (mid-edit quoted attribute
                // like `<div title="<!--` or `<div title="<script>`). Advance
                // past the current line so the `<!--` / `<script>` inside the
                // broken quoted value is not re-encountered on the very next
                // character and misidentified as a real comment / raw-text
                // opener that would mask through EOF. Sibling tags on later
                // lines still get their chance to be walked.
                // 未終端の non-raw-text タグ opener（`<div title="<!--` のような
                // 編集途中の引用属性）に遭遇した場合、`i++` で戻ると引用値内の
                // `<!--` / `<script>` が次文字で comment / raw-text opener として
                // 再解釈されて EOF までマスクされるため、現在行末まで一気に進めて
                // 次行以降の兄弟タグを拾えるようにする。
                var eolIdx = text.IndexOf('\n', i);
                i = eolIdx < 0 ? chars.Length : eolIdx + 1;
                continue;
            }

            i++;
        }
        return new string(chars);
    }

    private static string? TryMatchHtmlRawTextOpenerName(string text, int start)
    {
        // Check if `text[start]` (must be `<`) begins `<script` / `<style` /
        // `<textarea` / `<title` followed by a non-tag-name-char (so `<scriptx`
        // is NOT matched as `<script`).
        // `start` は `<` の位置。`<script` / `<style` / `<textarea` / `<title`
        // に続く文字がタグ名文字でないもののみ一致させる（`<scriptx` は除外）。
        foreach (var name in HtmlRawTextElementNames)
        {
            var nameStart = start + 1;
            if (nameStart + name.Length > text.Length)
                continue;
            var match = true;
            for (var j = 0; j < name.Length; j++)
            {
                if (char.ToLowerInvariant(text[nameStart + j]) != name[j])
                {
                    match = false;
                    break;
                }
            }
            if (!match)
                continue;
            var after = nameStart + name.Length;
            if (after >= text.Length || !IsHtmlTagNameChar(text[after]))
                return name;
        }
        return null;
    }

    private static int FindHtmlTagOpenerEnd(string text, int start)
    {
        // Walk from `start` (position of `<`) forward to find the opening `>`,
        // skipping over quoted attribute values. Multi-line quoted values are
        // allowed per HTML5 spec.
        // `start` は `<` の位置。引用符付き属性値を `FindHtmlQuoteClose` で飛ばしつつ
        // 開始タグの閉じ `>` を探す。HTML5 仕様どおり複数行値も許容する。
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '>')
                return i;
            if (c == '"' || c == '\'')
            {
                var closeIdx = FindHtmlQuoteClose(text, i + 1, c);
                if (closeIdx < 0)
                    return -1;
                i = closeIdx + 1;
                continue;
            }
            i++;
        }
        return -1;
    }

    private static int FindHtmlQuoteClose(string text, int start, char quote)
    {
        // Scan forward for the matching closing quote. HTML5 allows newlines
        // inside quoted attribute values (`<meta description="line1\nline2">`)
        // and tag-like content (`<div title="<section id=x>">`), so we cross
        // line boundaries and tag-name-like bytes without bailing. A quote is
        // accepted as the close when it has "strong valid" post-value context:
        // per HTML5 the char immediately after a quoted attribute value must
        // be whitespace, `>`, or EOF. `/` alone is intentionally excluded from
        // strong context — a `/` following a quote is ambiguous between the
        // self-closing marker (`attr="v"/>`) and the opening `"` of a later
        // path-like attribute (`href="/app.css"`). Accepting bare `/` would
        // let an earlier unterminated `title="...` silently steal the opening
        // quote of `href="/app.css"` and swallow every sibling tag between
        // them. The self-closing form `"/>` IS accepted (ambiguity gone —
        // the `/` is followed by `>`), so void-element tags like
        // `<link href="/app.css"/>` still close cleanly without triggering
        // the nested-attribute fallback on the following sibling tag.
        //
        // When a non-strong `"` is encountered and it matches an "attribute-
        // start" pattern (preceded by `[attr-name-chars]+=` with whitespace
        // before the ident), the scanner treats it as a nested attribute
        // opening: it walks past that attribute's value (finding the matching
        // inner quote) and resumes scanning, instead of mis-taking the inner
        // opening for our close. This preserves strict-HTML5 behavior on
        // well-formed multi-line quoted values (they contain no spurious
        // `ident="` patterns) while keeping mid-edit resilience — if the
        // outer quote is truly unterminated, we'll walk through all nested
        // attributes without finding a strong close, and return -1 so the
        // attribute parser can bail at EOL and recover sibling tags on the
        // next lines.
        //
        // If neither a strong close nor a nested pattern is ever seen, fall
        // back to the first bare `"` candidate (matches spec tokenizer
        // recovery for malformed content like `<div id="foo"bar>`). If nested
        // patterns WERE seen but no strong close was found, return -1 to
        // signal the attribute is effectively unterminated for our purposes.
        //
        // 閉じ引用符を探す。HTML5 は属性値内の改行とタグ様テキストを許容するため、
        // 改行やタグ様の文字では早期中断しない。引用符を閉じとして採用する条件は、
        // 直後が空白 / `>` / EOF の「strong な属性値終端」であること。`/` は
        // self-closing (`attr="v"/>`) と後続属性の開始引用符 (`href="/app.css"`)
        // の区別が文脈無しでは付かないため、`/` 単独は strong には含めない。
        // bare `/` を許容すると、未終端の `title="...` が後続 `href="/app.css"`
        // の開き `"` を奪って兄弟タグを丸呑みする。
        //
        // strong でない `"` が「属性開始パターン」(`[attr-name-chars]+=` の前が
        // 空白) にマッチしたら、それは nested な属性開始と判断し、その属性の値を
        // 次の引用符まで飛ばして外側 scan を再開する。これにより Blocker 2
        // (`<div title="line1\n<section></section>\nline3" id="real">`) のような
        // 真に妥当な複数行引用属性値は strong 終端まで到達して通り、一方で未終端な
        // 外側 `"` は nested を何個かスキップしても strong 終端に到達せず、最終的に
        // -1 を返して属性パーサが EOL で bail → 次行以降の兄弟タグを拾える。
        //
        // strong 終端にも nested にも該当しない `"` は弱い候補として記録し、EOF
        // 到達時に nested を見ていなければ fallback として返す（`<div id="foo"bar>`
        // のような malformed でも spec に近い形で拾う）。nested を見ていれば -1 を
        // 返して、未終端扱いにする。
        var firstCandidate = -1;
        var sawNested = false;
        var i = start;
        while (i < text.Length)
        {
            if (text[i] == quote)
            {
                var after = i + 1;
                if (after >= text.Length)
                    return i;
                var nextCh = text[after];
                if (nextCh == '>' || char.IsWhiteSpace(nextCh))
                    return i;
                // Accept the XML-style self-closing marker `"/>` as strong
                // post-context. Bare `/` is still rejected because it cannot
                // be distinguished from a path-like `href="/app.css"` opener.
                // 自己閉鎖タグの `"/>` は strong として受理する。bare `/` は
                // `href="/app.css"` の開きとの区別が付かないため受理しない。
                if (nextCh == '/' && after + 1 < text.Length && text[after + 1] == '>')
                    return i;

                if (IsPrecededByHtmlAttributeStart(text, i, start))
                {
                    sawNested = true;
                    var inner = i + 1;
                    while (inner < text.Length && text[inner] != quote)
                        inner++;
                    if (inner >= text.Length)
                        break;
                    i = inner + 1;
                    continue;
                }

                if (firstCandidate < 0)
                    firstCandidate = i;
            }
            i++;
        }
        if (sawNested)
            return -1;
        return firstCandidate;
    }

    private static bool IsPrecededByHtmlAttributeStart(string text, int quotePos, int scanStart)
    {
        // Return true if the characters immediately before `quotePos` form a
        // `[attr-name-chars]+=` pattern AND the ident is preceded by whitespace
        // within the current scan — i.e. it looks like the start of a new
        // attribute inside an outer quoted value. This is the signal that the
        // `"` is more likely a nested attribute opening than the true close of
        // the outer value.
        // `quotePos` の直前が `[attr-name-chars]+=` で、その ident の前が
        // scan 範囲内の空白文字なら true。外側引用値の中で新しい属性が
        // 始まっているパターンと判定する。
        if (quotePos <= scanStart)
            return false;
        if (text[quotePos - 1] != '=')
            return false;
        var j = quotePos - 2;
        var identEnd = j + 1;
        while (j >= scanStart && IsHtmlAttrNameChar(text[j]))
            j--;
        if (j + 1 >= identEnd)
            return false;
        if (j < scanStart)
            return false;
        return char.IsWhiteSpace(text[j]);
    }

    private static int FindHtmlRawTextClose(string text, int start, string tagName)
    {
        // Locate the next `</tagName` (case-insensitive) at or after `start`.
        // Returns the position of `<`, or -1 if none.
        // `</tagName` を大文字小文字非区別で `start` 以降から探し、`<` の位置を返す。
        var i = start;
        while (i < text.Length - tagName.Length - 2)
        {
            if (text[i] == '<' && text[i + 1] == '/')
            {
                var match = true;
                for (var j = 0; j < tagName.Length; j++)
                {
                    if (char.ToLowerInvariant(text[i + 2 + j]) != tagName[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    var after = i + 2 + tagName.Length;
                    if (after >= text.Length)
                        return i;
                    var nc = text[after];
                    if (nc == '>' || nc == '/' || char.IsWhiteSpace(nc))
                        return i;
                }
            }
            i++;
        }
        return -1;
    }

    private static void BlankPreservingNewlines(char[] chars, int start, int end)
    {
        var limit = Math.Min(end, chars.Length);
        for (var i = start; i < limit; i++)
        {
            if (chars[i] != '\n' && chars[i] != '\r')
                chars[i] = ' ';
        }
    }

    private static List<SymbolRecord> ExtractHtmlSymbols(long fileId, string[] lines)
    {
        // HTML needs proper tag-structure awareness so attribute lookalikes inside
        // other attributes' quoted values (e.g. `<link title="href=evil.css" href="/real.css">`)
        // don't leak phantom imports AND real attributes on the same tag aren't
        // skipped. Regex alone can't do this — the outer tag context is lost once
        // an attribute inside it is rejected — so walk the masked text with a
        // character state machine that enumerates each tag's attributes in order.
        // HTML は同一タグ内で別属性の引用符付き値に書かれた attribute 名の文字列（例:
        // `<link title="href=evil.css" href="/real.css">`）から phantom な import を
        // 漏らさず、かつ本物の属性を飛ばさないために、タグ構造を理解した走査が必要。
        // regex だけでは、タグ内のある属性を mask で落とした瞬間に外側タグのコンテキスト
        // を失うため不可能。マスク済みテキストを文字単位の state machine で走査し、タグ
        // ごとに属性を列挙していく。
        var rawText = string.Join('\n', lines);
        var maskedText = MaskHtmlRawTextRegions(rawText);

        // Precompute per-line absolute offsets for O(log n) line lookup via binary
        // search. Each lines[i] does not include the joining '\n', so lineStarts[i]
        // points at the first character of line i.
        // 各シンボルの行番号を O(log n) で引けるように行ごとの絶対 offset を事前計算。
        // lines[i] 自体は連結に使う '\n' を含まないため、lineStarts[i] は i 行目の
        // 先頭文字位置を指す。
        var lineStarts = new int[lines.Length];
        var lineCursor = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            lineStarts[i] = lineCursor;
            lineCursor += lines[i].Length + 1;
        }

        var symbols = new List<SymbolRecord>();
        var pos = 0;
        while (pos < maskedText.Length)
        {
            if (maskedText[pos] != '<')
            {
                pos++;
                continue;
            }

            // Skip closing tags, comments/doctypes/CDATA, and processing instructions.
            // Raw-text bodies (<script>/<style>) and comments have already been masked
            // by MaskHtmlRawTextRegions, but the opening/closing tags themselves remain.
            // 閉じタグ / コメント / doctype / 処理命令はここで読み飛ばす。raw-text 本文と
            // HTML コメントは MaskHtmlRawTextRegions で既に空白化されているが、開始タグ
            // 自体はそのまま残っているため通常の属性走査対象になる。
            if (pos + 1 < maskedText.Length && (maskedText[pos + 1] == '/' || maskedText[pos + 1] == '!' || maskedText[pos + 1] == '?'))
            {
                pos = IndexOfOrEnd(maskedText, '>', pos + 1) + 1;
                continue;
            }

            var tagNameStart = pos + 1;
            if (tagNameStart >= maskedText.Length || !IsHtmlTagNameStart(maskedText[tagNameStart]))
            {
                pos++;
                continue;
            }

            var tagNameEnd = tagNameStart;
            while (tagNameEnd < maskedText.Length && IsHtmlTagNameChar(maskedText[tagNameEnd]))
                tagNameEnd++;

            var tagName = maskedText[tagNameStart..tagNameEnd];
            var tagNameLower = tagName.ToLowerInvariant();

            // Emit custom Web Components (hyphenated opening tag) at the `<` position,
            // but skip the standard HTML/SVG/MathML tags that happen to contain a hyphen
            // (`<font-face>`, `<color-profile>`, `<annotation-xml>`, etc.). Those are
            // native elements, not user components, so labeling them as `class` symbols
            // would pollute `symbols` / `definition` / `outline` on any project with
            // inline SVG / MathML content.
            // 開始タグ名にハイフンを含むカスタム Web Components を `<` の位置で emit する。
            // ただしハイフン付きでも仕様で予約されている `<font-face>` / `<color-profile>`
            // / `<annotation-xml>` などの標準タグは除外する。SVG / MathML を埋め込んだ
            // ファイルで `symbols` / `definition` / `outline` が汚染されるのを防ぐ。
            if (tagName.Contains('-') && !HtmlReservedHyphenatedTags.Contains(tagNameLower))
            {
                var startLine = FindHtmlLineNumber(lineStarts, pos);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = tagName,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = startLine,
                    Signature = lines[signatureIndex].Trim(),
                });
            }

            // Walk the tag body, enumerating attribute name/value pairs until `>` or EOF.
            // タグ本体を走査し、`>` か EOF まで属性 name/value を順に列挙する。
            var cursor = tagNameEnd;
            while (cursor < maskedText.Length && maskedText[cursor] != '>')
            {
                // Skip whitespace and stray '/' (self-closing marker).
                // 空白文字と self-closing の `/` を読み飛ばす。
                if (char.IsWhiteSpace(maskedText[cursor]) || maskedText[cursor] == '/')
                {
                    cursor++;
                    continue;
                }

                // Read attribute name. HTML5 allows broad attribute-name charsets, but for
                // our emit rules we only need to recognize ASCII names plus `:` / `-` / `.`
                // (xml:id, data-*, aria-*, etc.). Anything else aborts the parse of this tag
                // gracefully by treating it as a non-matching attribute start.
                // 属性名を読む。HTML5 の属性名は広いが、emit 対象の判定には ASCII の名前と
                // `:` / `-` / `.` が拾えれば十分（xml:id, data-*, aria-* 等を含めるため）。
                // それ以外の文字が来たら、このタグのパースは壊さずに 1 文字進めるだけで抜ける。
                if (!IsHtmlAttrNameStart(maskedText[cursor]))
                {
                    cursor++;
                    continue;
                }
                var attrNameStart = cursor;
                while (cursor < maskedText.Length && IsHtmlAttrNameChar(maskedText[cursor]))
                    cursor++;
                var attrName = maskedText[attrNameStart..cursor];
                var attrNameLower = attrName.ToLowerInvariant();

                // Skip whitespace between name and `=`.
                while (cursor < maskedText.Length && char.IsWhiteSpace(maskedText[cursor]))
                    cursor++;

                string? attrValue = null;
                int attrValueStart = -1;
                if (cursor < maskedText.Length && maskedText[cursor] == '=')
                {
                    cursor++;
                    while (cursor < maskedText.Length && char.IsWhiteSpace(maskedText[cursor]))
                        cursor++;
                    if (cursor < maskedText.Length && (maskedText[cursor] == '"' || maskedText[cursor] == '\''))
                    {
                        var quote = maskedText[cursor];
                        cursor++;
                        attrValueStart = cursor;
                        // Use the shared FindHtmlQuoteClose helper so this and the raw-text
                        // mask agree on where quoted attribute values end. The helper allows
                        // multi-line quoted values (valid HTML5 like `<div title="line1\n
                        // line2" id="real">` where `id="real"` must still be emitted) and
                        // tag-like content inside quoted values, identifying the close by
                        // post-value context (`>`, `/`, whitespace, or EOF). Only truly
                        // unterminated quotes (no matching `"` at all) return -1, so the
                        // caller can bail to EOL without walking to EOF.
                        // 共有ヘルパー `FindHtmlQuoteClose` を使い、mask 側とも引用符終端の
                        // 判断を一致させる。複数行 quoted 属性値 (`<div title="line1\n
                        // line2" id="real">` など) とタグ様テキストを含む引用符付き値を
                        // 許容し、`>` / `/` / 空白 / EOF が直後に来る位置を終端として検出する。
                        // 真に未終端（マッチ `"` が存在しない）場合のみ -1 を返し、呼び出し側が
                        // EOF まで走らず行末で被害を止められるようにする。
                        var valueEnd = FindHtmlQuoteClose(maskedText, cursor, quote);
                        if (valueEnd < 0)
                        {
                            // Unterminated: bail to end of current line so the outer tag
                            // loop can restart at the beginning of the next line's `<`.
                            // 未終端: 当該行末まで進め、次行先頭の `<` から外側ループが再開できるようにする。
                            attrValue = null;
                            var eol = maskedText.IndexOf('\n', cursor);
                            cursor = eol < 0 ? maskedText.Length : eol;
                            break;
                        }
                        attrValue = maskedText[cursor..valueEnd];
                        cursor = valueEnd + 1;
                    }
                    else if (cursor < maskedText.Length && maskedText[cursor] != '>')
                    {
                        // Unquoted value: HTML5 excludes space, `"`, `'`, `=`, `<`, `>`, backtick.
                        // 引用符なし値: HTML5 では空白、`"`、`'`、`=`、`<`、`>`、バッククォートを除外。
                        attrValueStart = cursor;
                        while (cursor < maskedText.Length && !IsHtmlUnquotedValueTerminator(maskedText[cursor]))
                            cursor++;
                        attrValue = maskedText[attrValueStart..cursor];
                    }
                }

                if (attrValue == null || attrValue.Length == 0)
                    continue;

                string? emitKind = null;
                if (attrNameLower == "src" && tagNameLower == "script")
                    emitKind = "import";
                else if (attrNameLower == "href" && tagNameLower == "link")
                    emitKind = "import";
                else if (attrNameLower == "id" && !attrName.Contains(':') && !attrName.Contains('-') && !attrName.Contains('.'))
                    emitKind = "property";

                if (emitKind == null)
                    continue;

                var name = attrValue.Trim();
                if (name.Length == 0)
                    continue;

                // Anchor the symbol at the attribute value so cross-line tags like
                // `<script\n  type="module"\n  src="/app.js">` land on the line that
                // actually carries the value.
                // 属性値の位置でシンボルを固定し、属性が折り返されたタグでも値が書かれた
                // 行にジャンプできるようにする。
                var anchor = attrValueStart >= 0 ? attrValueStart : pos;
                var startLine = FindHtmlLineNumber(lineStarts, anchor);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = emitKind,
                    Name = name,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = startLine,
                    Signature = lines[signatureIndex].Trim(),
                });
            }

            pos = cursor < maskedText.Length ? cursor + 1 : cursor;
        }

        AssignContainers(symbols, lines, null);
        PopulateDeclaredContainerQualifiedNames(symbols);
        return symbols;
    }

    private static List<SymbolRecord> ExtractXmlSymbols(long fileId, string[] lines)
    {
        var hasXamlNamespace = false;
        foreach (var line in lines)
        {
            if (line.IndexOf("xmlns:x=", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (line.IndexOf("schemas.microsoft.com/winfx/2006/xaml", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("github.com/avaloniaui", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasXamlNamespace = true;
                break;
            }
        }

        if (!hasXamlNamespace)
            return [];

        var symbols = new List<SymbolRecord>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (Match classMatch in XamlClassRegex.Matches(line))
            {
                var value = classMatch.Groups["value"].Value.Trim();
                if (value.Length == 0)
                    continue;
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = value,
                    Line = i + 1,
                    StartLine = i + 1,
                    EndLine = i + 1,
                    Signature = line.Trim(),
                });
            }

            foreach (Match nameMatch in XamlNameRegex.Matches(line))
            {
                var value = nameMatch.Groups["value"].Value.Trim();
                if (value.Length == 0)
                    continue;
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = value,
                    Line = i + 1,
                    StartLine = i + 1,
                    EndLine = i + 1,
                    Signature = line.Trim(),
                });
            }
        }

        return symbols;
    }

    private static bool IsHtmlTagNameStart(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    private static bool IsHtmlTagNameChar(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';

    private static bool IsHtmlAttrNameStart(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == ':';

    private static bool IsHtmlAttrNameChar(char c) =>
        IsHtmlAttrNameStart(c) || (c >= '0' && c <= '9') || c == '-' || c == '.';

    private static bool IsHtmlUnquotedValueTerminator(char c) =>
        char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '=' || c == '<' || c == '>' || c == '`';

    private static int IndexOfOrEnd(string text, char needle, int start)
    {
        var idx = text.IndexOf(needle, start);
        return idx < 0 ? text.Length : idx;
    }

    private static int FindHtmlLineNumber(int[] lineStarts, int offset)
    {
        if (lineStarts.Length == 0)
            return 1;
        var lo = 0;
        var hi = lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= offset)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo + 1;
    }

    private static void ExtractJavaEnumMembers(long fileId, string[] rawLines, List<SymbolRecord> symbols)
    {
        // Snapshot enum declarations first — we mutate the list during iteration.
        // 反復中に list を書き換えるため、先に enum 宣言を snapshot しておく。
        var enumDeclarations = symbols
            .Where(s => s.Kind == "enum" && s.BodyStartLine != null && s.BodyEndLine != null)
            .OrderBy(s => s.StartLine)
            .ThenByDescending(s => s.EndLine)
            .ToList();

        foreach (var enumSymbol in enumDeclarations)
        {
            if (!TryFindJavaEnumBodyBounds(rawLines, enumSymbol, out var bodyStartLineIndex, out var bodyStartColumn, out var bodyEndLineIndex, out var bodyEndColumnExclusive))
                continue;

            ExtractJavaEnumMembersFromBody(
                fileId,
                enumSymbol,
                rawLines,
                bodyStartLineIndex,
                bodyStartColumn,
                bodyEndLineIndex,
                bodyEndColumnExclusive,
                symbols);
        }
    }

    private static void ExtractJavaCompactConstructors(long fileId, string[] rawLines, List<SymbolRecord> symbols)
    {
        var recordDeclarations = symbols
            .Where(symbol =>
                symbol.FileId == fileId
                && symbol.Kind == "class"
                && symbol.BodyStartLine != null
                && symbol.BodyEndLine != null
                && IsJavaRecordSymbol(rawLines, symbol))
            .OrderBy(symbol => symbol.StartLine)
            .ThenByDescending(symbol => symbol.EndLine)
            .ToList();

        foreach (var recordSymbol in recordDeclarations)
        {
            if (!TryFindJavaSymbolBodyBounds(rawLines, recordSymbol, out var bodyStartLineIndex, out var bodyStartColumn, out var bodyEndLineIndex, out var bodyEndColumnExclusive))
                continue;

            var mode = JavaScanMode.Normal;
            var braceDepth = 0;
            for (int i = bodyStartLineIndex; i <= bodyEndLineIndex; i++)
            {
                if (mode == JavaScanMode.LineComment)
                    mode = JavaScanMode.Normal;

                var line = rawLines[i];
                var segmentStart = i == bodyStartLineIndex
                    ? Math.Min(bodyStartColumn, line.Length)
                    : 0;
                var segmentEndExclusive = i == bodyEndLineIndex
                    ? Math.Min(bodyEndColumnExclusive, line.Length)
                    : line.Length;
                var lineStartBraceDepth = braceDepth;
                var lineStartMode = mode;

                if (lineStartBraceDepth == 0
                    && lineStartMode == JavaScanMode.Normal
                    && segmentStart < segmentEndExclusive)
                {
                    var segment = line[segmentStart..segmentEndExclusive];
                    var compactConstructorOffset = 0;
                    while (compactConstructorOffset >= 0 && compactConstructorOffset < segment.Length)
                    {
                        var candidateSegment = segment[compactConstructorOffset..];
                        if (TryMatchJavaDeclarationSegment(JavaCompactConstructorRegex, candidateSegment, out var match, out var javaLeadingAnnotationOffset)
                            && match.Groups["name"].Value == recordSymbol.Name)
                        {
                            var absoluteStartColumn = segmentStart + compactConstructorOffset + javaLeadingAnnotationOffset + match.Index;
                            var visibility = TryGetGroup(match, "visibility");
                            var (endLine, bodyStartLine, bodyEndLine) = ResolveRange(rawLines, i, BodyStyle.Brace, "java", absoluteStartColumn);
                            var sameLineEndColumn = bodyEndLine == i + 1
                                ? FindSameLineBraceEndColumn(line, absoluteStartColumn, "java", "function")
                                : -1;
                            var existingSymbols = symbols
                                .Where(symbol =>
                                    symbol.FileId == fileId
                                    && symbol.Kind == "function"
                                    && symbol.Name == recordSymbol.Name
                                    && symbol.StartLine == i + 1
                                    && (symbol.ContainerName == null || symbol.ContainerName == recordSymbol.Name)
                                    && (symbol.ContainerKind == null || symbol.ContainerKind == "class"))
                                .ToList();
                            foreach (var existingSymbol in existingSymbols)
                            {
                                if (LooksLikeJavaCompactConstructorSymbol(existingSymbol, recordSymbol.Name))
                                    continue;
                                symbols.Remove(existingSymbol);
                            }

                            if (!symbols.Any(symbol => LooksLikeJavaCompactConstructorSymbol(symbol, recordSymbol.Name)
                                    && symbol.FileId == fileId
                                    && symbol.StartLine == i + 1))
                            {
                                symbols.Add(new SymbolRecord
                                {
                                    FileId = fileId,
                                    Kind = "function",
                                    Name = recordSymbol.Name,
                                    Line = i + 1,
                                    StartLine = i + 1,
                                    StartColumn = absoluteStartColumn,
                                    EndLine = Math.Max(i + 1, endLine),
                                    BodyStartLine = bodyStartLine,
                                    BodyEndLine = bodyEndLine,
                                    Signature = sameLineEndColumn >= absoluteStartColumn
                                        ? line[absoluteStartColumn..(sameLineEndColumn + 1)].Trim()
                                        : line[absoluteStartColumn..].Trim(),
                                    ContainerKind = "class",
                                    ContainerName = recordSymbol.Name,
                                    Visibility = visibility,
                                });
                            }

                            if (sameLineEndColumn < absoluteStartColumn)
                                break;

                            compactConstructorOffset = FindNextSameLineBraceStatementStart(segment, sameLineEndColumn - segmentStart + 1, "java");
                            continue;
                        }

                        compactConstructorOffset = FindNextSameLineBraceStatementStart(segment, compactConstructorOffset + 1, "java");
                    }
                }

                var column = segmentStart;
                while (column < segmentEndExclusive)
                {
                    if (TryConsumeJavaNonCode(line, ref column, ref mode))
                        continue;

                    var ch = line[column];
                    if (ch == '{')
                        braceDepth++;
                    else if (ch == '}' && braceDepth > 0)
                        braceDepth--;

                    column++;
                }
            }
        }
    }

    private static bool LooksLikeJavaCompactConstructorSymbol(SymbolRecord symbol, string recordName)
    {
        if (symbol.Kind != "function"
            || symbol.Name != recordName
            || symbol.ContainerKind != "class"
            || symbol.ContainerName != recordName)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(symbol.ReturnType))
            return false;

        var signature = symbol.Signature?.TrimStart();
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        if (signature.Contains(" record ", StringComparison.Ordinal)
            || signature.StartsWith("record ", StringComparison.Ordinal)
            || signature.StartsWith("@", StringComparison.Ordinal))
        {
            return false;
        }

        return signature.Contains($"{recordName} {{", StringComparison.Ordinal);
    }

    private static bool IsJavaRecordSymbol(string[] rawLines, SymbolRecord symbol)
    {
        var declarationLineIndex = symbol.StartLine - 1;
        if (declarationLineIndex < 0 || declarationLineIndex >= rawLines.Length)
            return false;

        return TryMatchJavaDeclarationSegment(
            GetCurrentDeclarationRecordRegex("java", symbol.Kind, symbol.Name),
            rawLines[declarationLineIndex],
            out _,
            out _);
    }

    private static bool TryFindJavaSymbolBodyBounds(
        string[] rawLines,
        SymbolRecord containerSymbol,
        out int bodyStartLineIndex,
        out int bodyStartColumn,
        out int bodyEndLineIndex,
        out int bodyEndColumnExclusive)
    {
        bodyStartLineIndex = 0;
        bodyStartColumn = 0;
        bodyEndLineIndex = 0;
        bodyEndColumnExclusive = 0;

        var declarationLineIndex = containerSymbol.StartLine - 1;
        if (declarationLineIndex < 0 || declarationLineIndex >= rawLines.Length)
            return false;

        var scanEndLineIndex = Math.Min(containerSymbol.EndLine, rawLines.Length) - 1;
        if (scanEndLineIndex < declarationLineIndex)
            return false;

        return TryFindJavaBraceDelimitedBodyBounds(
            rawLines,
            declarationLineIndex,
            scanEndLineIndex,
            ignoreLeadingAnnotationArrayBraces: true,
            out bodyStartLineIndex,
            out bodyStartColumn,
            out bodyEndLineIndex,
            out bodyEndColumnExclusive);
    }

    private static bool TryFindJavaEnumBodyBounds(
        string[] rawLines,
        SymbolRecord enumSymbol,
        out int bodyStartLineIndex,
        out int bodyStartColumn,
        out int bodyEndLineIndex,
        out int bodyEndColumnExclusive)
    {
        return TryFindJavaSymbolBodyBounds(
            rawLines,
            enumSymbol,
            out bodyStartLineIndex,
            out bodyStartColumn,
            out bodyEndLineIndex,
            out bodyEndColumnExclusive);
    }

    // Track Java source-code scanner state (strings, char literals, comments, text blocks).
    // Java ソース scanner の state（文字列・char literal・コメント・text block）を表す。
    private enum JavaScanMode
    {
        Normal,
        LineComment,
        BlockComment,
        String,
        TextBlock,
        Char,
    }

    private static bool TryFindJavaBraceDelimitedBodyBounds(
        string[] rawLines,
        int declarationLineIndex,
        int scanEndLineIndex,
        bool ignoreLeadingAnnotationArrayBraces,
        out int bodyStartLineIndex,
        out int bodyStartColumn,
        out int bodyEndLineIndex,
        out int bodyEndColumnExclusive)
    {
        bodyStartLineIndex = 0;
        bodyStartColumn = 0;
        bodyEndLineIndex = 0;
        bodyEndColumnExclusive = 0;

        var mode = JavaScanMode.Normal;
        var depth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var opened = false;

        var lineIndex = declarationLineIndex;
        var column = 0;
        while (lineIndex <= scanEndLineIndex)
        {
            if (mode == JavaScanMode.LineComment)
                mode = JavaScanMode.Normal;

            var line = rawLines[lineIndex];
            while (column < line.Length)
            {
                if (TryConsumeJavaNonCode(line, ref column, ref mode))
                    continue;

                var ch = line[column];
                if (ch == '(')
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
                else if (ch == '{')
                {
                    if (!opened)
                    {
                        if (ignoreLeadingAnnotationArrayBraces && (parenDepth > 0 || bracketDepth > 0))
                        {
                            column++;
                            continue;
                        }

                        opened = true;
                        depth = 1;
                        bodyStartLineIndex = lineIndex;
                        bodyStartColumn = column + 1;
                    }
                    else
                    {
                        depth++;
                    }
                }
                else if (ch == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                    {
                        bodyEndLineIndex = lineIndex;
                        bodyEndColumnExclusive = column;
                        return true;
                    }
                }

                column++;
            }

            lineIndex++;
            column = 0;
        }

        if (!opened)
            return false;

        bodyEndLineIndex = scanEndLineIndex;
        bodyEndColumnExclusive = rawLines[scanEndLineIndex].Length;
        return true;
    }

    // Consume strings / chars / comments / text blocks, updating mode and advancing column.
    // Returns true if one or more characters were consumed; caller must NOT increment column again.
    // Returns false if the current character is structural code and caller should handle it.
    // 文字列・char・コメント・text block を読み飛ばして column を進める。
    // 消費したら true を返し、呼び出し元は column を再度進めないこと。
    // 構造的コードなら false を返して呼び出し元に処理を委ねる。
    private static bool TryConsumeJavaNonCode(string line, ref int column, ref JavaScanMode mode)
    {
        if (column >= line.Length)
            return false;

        var ch = line[column];
        switch (mode)
        {
            case JavaScanMode.LineComment:
                column = line.Length;
                return true;
            case JavaScanMode.BlockComment:
                if (ch == '*' && column + 1 < line.Length && line[column + 1] == '/')
                {
                    mode = JavaScanMode.Normal;
                    column += 2;
                    return true;
                }
                column++;
                return true;
            case JavaScanMode.String:
                if (ch == '\\' && column + 1 < line.Length)
                {
                    column += 2;
                    return true;
                }
                if (ch == '"')
                {
                    mode = JavaScanMode.Normal;
                    column++;
                    return true;
                }
                column++;
                return true;
            case JavaScanMode.TextBlock:
                if (ch == '"' && column + 2 < line.Length && line[column + 1] == '"' && line[column + 2] == '"')
                {
                    mode = JavaScanMode.Normal;
                    column += 3;
                    return true;
                }
                if (ch == '\\' && column + 1 < line.Length)
                {
                    column += 2;
                    return true;
                }
                column++;
                return true;
            case JavaScanMode.Char:
                if (ch == '\\' && column + 1 < line.Length)
                {
                    column += 2;
                    return true;
                }
                if (ch == '\'')
                {
                    mode = JavaScanMode.Normal;
                    column++;
                    return true;
                }
                column++;
                return true;
            default:
                if (ch == '/' && column + 1 < line.Length && line[column + 1] == '/')
                {
                    mode = JavaScanMode.LineComment;
                    column = line.Length;
                    return true;
                }
                if (ch == '/' && column + 1 < line.Length && line[column + 1] == '*')
                {
                    mode = JavaScanMode.BlockComment;
                    column += 2;
                    return true;
                }
                if (ch == '"' && column + 2 < line.Length && line[column + 1] == '"' && line[column + 2] == '"')
                {
                    mode = JavaScanMode.TextBlock;
                    column += 3;
                    return true;
                }
                if (ch == '"')
                {
                    mode = JavaScanMode.String;
                    column++;
                    return true;
                }
                if (ch == '\'')
                {
                    mode = JavaScanMode.Char;
                    column++;
                    return true;
                }
                return false;
        }
    }

    private static void ExtractJavaEnumMembersFromBody(
        long fileId,
        SymbolRecord enumSymbol,
        string[] rawLines,
        int bodyStartLineIndex,
        int bodyStartColumn,
        int bodyEndLineIndex,
        int bodyEndColumnExclusive,
        List<SymbolRecord> symbols)
    {
        var mode = JavaScanMode.Normal;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0; // depth inside the enum body (member anonymous bodies push this).
        (int LineIndex, int Column)? memberStart = null;
        var lineIndex = bodyStartLineIndex;
        var column = bodyStartColumn;

        while (lineIndex <= bodyEndLineIndex)
        {
            if (mode == JavaScanMode.LineComment)
                mode = JavaScanMode.Normal;

            var line = rawLines[lineIndex];
            var scanEndColumnExclusive = lineIndex == bodyEndLineIndex
                ? Math.Min(bodyEndColumnExclusive, line.Length)
                : line.Length;

            while (column < scanEndColumnExclusive)
            {
                if (TryConsumeJavaNonCode(line, ref column, ref mode))
                    continue;

                var ch = line[column];

                if (ch == '(')
                {
                    if (memberStart == null)
                        memberStart = (lineIndex, column);
                    parenDepth++;
                    column++;
                    continue;
                }
                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    column++;
                    continue;
                }
                if (ch == '[')
                {
                    if (memberStart == null)
                        memberStart = (lineIndex, column);
                    bracketDepth++;
                    column++;
                    continue;
                }
                if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    column++;
                    continue;
                }
                if (ch == '{')
                {
                    if (memberStart == null)
                        memberStart = (lineIndex, column);
                    braceDepth++;
                    column++;
                    continue;
                }
                if (ch == '}' && braceDepth > 0)
                {
                    braceDepth--;
                    column++;
                    continue;
                }

                if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                {
                    if (ch == ',')
                    {
                        if (memberStart != null)
                        {
                            TryAddJavaEnumMemberFromSpan(fileId, enumSymbol, rawLines, memberStart.Value, (lineIndex, column), symbols);
                            memberStart = null;
                        }
                        column++;
                        continue;
                    }
                    if (ch == ';')
                    {
                        if (memberStart != null)
                        {
                            TryAddJavaEnumMemberFromSpan(fileId, enumSymbol, rawLines, memberStart.Value, (lineIndex, column), symbols);
                            memberStart = null;
                        }
                        return;
                    }
                }

                if (!char.IsWhiteSpace(ch) && memberStart == null)
                    memberStart = (lineIndex, column);

                column++;
            }

            lineIndex++;
            column = 0;
        }

        if (memberStart != null)
            TryAddJavaEnumMemberFromSpan(fileId, enumSymbol, rawLines, memberStart.Value, (bodyEndLineIndex, bodyEndColumnExclusive), symbols);

        // Malformed-input recovery: if the scanner exited with unbalanced paren/bracket depths, the
        // body almost certainly contains an unterminated annotation. Fall back to the pre-body-scope
        // line regex so obvious enum members aren't suppressed wholesale by a single syntax error.
        // Depths > 0 mean the primary scan could not find clean boundaries — well-formed code always
        // closes back to 0 at the body end.
        // 入力不整形に対する recovery: primary scan が paren/bracket 深さを 0 に戻せずに終わった場合、未閉鎖の
        // annotation である可能性が高い。line regex を使って明白な enum member を救済する。
        if (parenDepth > 0 || bracketDepth > 0)
        {
            RecoverJavaEnumMembersByLine(
                fileId,
                enumSymbol,
                rawLines,
                bodyStartLineIndex,
                bodyStartColumn,
                bodyEndLineIndex,
                bodyEndColumnExclusive,
                symbols);
        }
    }

    private static void RecoverJavaEnumMembersByLine(
        long fileId,
        SymbolRecord enumSymbol,
        string[] rawLines,
        int bodyStartLineIndex,
        int bodyStartColumn,
        int bodyEndLineIndex,
        int bodyEndColumnExclusive,
        List<SymbolRecord> symbols)
    {
        // Dedup by member name. The primary scanner stamps StartLine at the first non-whitespace
        // (often the annotation line), while this fallback stamps the member-name line, so
        // StartLine-based dedup would miss matches. Java enum member names are unique.
        // メンバー名で重複排除する。primary scanner と recovery で StartLine 基準が揃わないため。
        var alreadyEmittedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in symbols)
        {
            if (existing.FileId == enumSymbol.FileId
                && existing.ContainerKind == "enum"
                && existing.ContainerName == enumSymbol.Name)
            {
                alreadyEmittedNames.Add(existing.Name);
            }
        }

        // Track brace depth across the body so lines inside anonymous member bodies or methods
        // don't spuriously match the line regex. Depth 0 means "top of the enum body member list."
        // 匿名メンバー本体やメソッド本体内の行を誤って member として拾わないよう brace 深さを追う。
        var mode = JavaScanMode.Normal;
        var braceDepth = 0;

        for (int i = bodyStartLineIndex; i <= bodyEndLineIndex && i < rawLines.Length; i++)
        {
            if (mode == JavaScanMode.LineComment)
                mode = JavaScanMode.Normal;

            var line = rawLines[i];
            var lineStartBraceDepth = braceDepth;
            var lineStartMode = mode;

            // Only try the fallback regex when this line starts at the enum body's top level and
            // not inside a string / comment / text block carried over from the previous line.
            // 行頭が enum 本体の top-level で、かつ非コード状態でもないときだけ fallback regex を試す。
            if (lineStartBraceDepth == 0 && lineStartMode == JavaScanMode.Normal)
            {
                var match = JavaEnumMemberLineFallbackRegex.Match(line);
                if (match.Success)
                {
                    var name = match.Groups["name"].Value;
                    if (!alreadyEmittedNames.Contains(name))
                    {
                        symbols.Add(new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "function",
                            Name = name,
                            Line = i + 1,
                            StartLine = i + 1,
                            EndLine = i + 1,
                            Signature = line.Trim(),
                            ContainerKind = "enum",
                            ContainerName = enumSymbol.Name,
                        });
                        alreadyEmittedNames.Add(name);
                    }
                }
            }

            // Advance mode / brace depth across this line so subsequent lines see correct state.
            // A top-level `;` (braceDepth == 0) terminates the member list — stop recovery.
            // 次行の状態を正しく保つため行内の mode / brace 深さを更新する。top-level の `;` は終端。
            var startColumn = (i == bodyStartLineIndex) ? bodyStartColumn : 0;
            var endColumnExclusive = (i == bodyEndLineIndex)
                ? Math.Min(bodyEndColumnExclusive, line.Length)
                : line.Length;
            var column = startColumn;
            while (column < endColumnExclusive)
            {
                if (TryConsumeJavaNonCode(line, ref column, ref mode))
                    continue;

                var ch = line[column];
                if (ch == '{')
                    braceDepth++;
                else if (ch == '}' && braceDepth > 0)
                    braceDepth--;
                else if (ch == ';' && braceDepth == 0)
                    return;

                column++;
            }
        }
    }

    private static void TryAddJavaEnumMemberFromSpan(
        long fileId,
        SymbolRecord enumSymbol,
        string[] rawLines,
        (int LineIndex, int Column) start,
        (int LineIndex, int Column) endExclusive,
        List<SymbolRecord> symbols)
    {
        var rawSignature = GetSourceSpanText(rawLines, start, endExclusive).Trim();
        if (rawSignature.Length == 0)
            return;

        // Skip leading `@Annotation(...)` annotations before the member name.
        // メンバー名の前にある `@Annotation(...)` を読み飛ばす。
        var nameSearchStart = SkipLeadingJavaAnnotations(rawSignature);
        if (nameSearchStart >= rawSignature.Length)
            return;

        var match = JavaEnumMemberNameRegex.Match(rawSignature, nameSearchStart);
        if (!match.Success || match.Index != nameSearchStart)
            return;

        var name = match.Groups["name"].Value;
        if (string.IsNullOrEmpty(name))
            return;

        int? bodyStartLine = null;
        int? bodyEndLine = null;
        if (TryFindJavaEnumMemberBodyBounds(rawLines, start, endExclusive, out var anonymousBodyStartLine, out var anonymousBodyEndLine))
        {
            bodyStartLine = anonymousBodyStartLine;
            bodyEndLine = anonymousBodyEndLine;
        }

        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            Name = name,
            Line = start.LineIndex + 1,
            StartLine = start.LineIndex + 1,
            StartColumn = start.Column,
            EndLine = endExclusive.LineIndex + 1,
            BodyStartLine = bodyStartLine,
            BodyEndLine = bodyEndLine,
            Signature = rawSignature,
            ContainerKind = "enum",
            ContainerName = enumSymbol.Name,
        });
    }

    private static bool TryFindJavaEnumMemberBodyBounds(
        string[] rawLines,
        (int LineIndex, int Column) start,
        (int LineIndex, int Column) endExclusive,
        out int bodyStartLine,
        out int bodyEndLine)
    {
        bodyStartLine = 0;
        bodyEndLine = 0;

        var mode = JavaScanMode.Normal;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var foundBody = false;

        for (int lineIndex = start.LineIndex; lineIndex <= endExclusive.LineIndex && lineIndex < rawLines.Length; lineIndex++)
        {
            if (mode == JavaScanMode.LineComment)
                mode = JavaScanMode.Normal;

            var line = rawLines[lineIndex];
            var column = lineIndex == start.LineIndex
                ? start.Column
                : 0;
            var scanEndColumnExclusive = lineIndex == endExclusive.LineIndex
                ? Math.Min(endExclusive.Column, line.Length)
                : line.Length;

            while (column < scanEndColumnExclusive)
            {
                if (TryConsumeJavaNonCode(line, ref column, ref mode))
                    continue;

                var ch = line[column];
                if (ch == '(')
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
                else if (ch == '{')
                {
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    {
                        foundBody = true;
                        bodyStartLine = lineIndex + 1;
                    }

                    braceDepth++;
                }
                else if (ch == '}' && braceDepth > 0)
                {
                    braceDepth--;
                    if (foundBody && braceDepth == 0)
                    {
                        bodyEndLine = lineIndex + 1;
                        return true;
                    }
                }

                column++;
            }
        }

        return false;
    }

    private static int SkipLeadingJavaAnnotations(string span)
    {
        var mode = JavaScanMode.Normal;
        var index = SkipJavaWhitespaceAndComments(span, 0, ref mode);

        while (index < span.Length && mode == JavaScanMode.Normal && span[index] == '@')
        {
            index++; // consume '@'
            index = SkipJavaWhitespaceAndComments(span, index, ref mode);
            if (mode != JavaScanMode.Normal)
                return index;

            while (index < span.Length && (char.IsLetterOrDigit(span[index]) || span[index] == '_' || span[index] == '.' || span[index] == '$'))
                index++;

            index = SkipJavaWhitespaceAndComments(span, index, ref mode);
            if (mode != JavaScanMode.Normal)
                return index;

            if (index < span.Length && span[index] == '(')
            {
                var depth = 1;
                index++;
                while (index < span.Length && depth > 0)
                {
                    if (TryConsumeJavaNonCodeAcrossLines(span, ref index, ref mode))
                        continue;

                    var ch = span[index];
                    if (ch == '(') depth++;
                    else if (ch == ')') depth--;
                    index++;
                }
            }

            index = SkipJavaWhitespaceAndComments(span, index, ref mode);
        }

        return index;
    }

    private static bool TryMatchJavaDeclarationSegment(
        Regex regex,
        string segment,
        out Match match,
        out int leadingAnnotationOffset)
    {
        match = regex.Match(segment);
        leadingAnnotationOffset = 0;
        if (match.Success)
            return true;

        var skippedOffset = SkipLeadingJavaAnnotations(segment);
        if (skippedOffset <= 0 || skippedOffset >= segment.Length)
            return false;

        var strippedMatch = regex.Match(segment[skippedOffset..]);
        if (!strippedMatch.Success)
            return false;

        match = strippedMatch;
        leadingAnnotationOffset = skippedOffset;
        return true;
    }

    // Walk whitespace, comments, and newlines in a multi-line span until the next non-whitespace code position.
    // 複数行 span 内の空白・コメント・改行をまとめて読み飛ばす。
    private static int SkipJavaWhitespaceAndComments(string span, int index, ref JavaScanMode mode)
    {
        while (index < span.Length)
        {
            if (mode == JavaScanMode.Normal && char.IsWhiteSpace(span[index]))
            {
                index++;
                continue;
            }

            if (TryConsumeJavaNonCodeAcrossLines(span, ref index, ref mode))
                continue;

            if (mode == JavaScanMode.Normal)
                return index;

            index++;
        }
        return index;
    }

    // Multi-line-aware variant of TryConsumeJavaNonCode. Handles `\n` explicitly so it can run on
    // a single span string that contains newlines (the line-based caller uses the per-line variant).
    // 複数行 span 対応版。`\n` を明示的に処理し、改行跨ぎの line-comment / 文字列終端も扱う。
    private static bool TryConsumeJavaNonCodeAcrossLines(string span, ref int index, ref JavaScanMode mode)
    {
        if (index >= span.Length)
            return false;

        var ch = span[index];
        switch (mode)
        {
            case JavaScanMode.LineComment:
                if (ch == '\n')
                    mode = JavaScanMode.Normal;
                index++;
                return true;
            case JavaScanMode.String:
            case JavaScanMode.Char:
                // Non-text-block Java string / char literals cannot cross raw newlines.
                // Treat a newline as an implicit terminator so the annotation skip stays sane.
                // Java の非 text-block 文字列 / char は生の改行を跨げないため、改行で暗黙終端する。
                if (ch == '\n')
                {
                    mode = JavaScanMode.Normal;
                    index++;
                    return true;
                }
                if (ch == '\\' && index + 1 < span.Length)
                {
                    index += 2;
                    return true;
                }
                if (mode == JavaScanMode.String && ch == '"')
                {
                    mode = JavaScanMode.Normal;
                    index++;
                    return true;
                }
                if (mode == JavaScanMode.Char && ch == '\'')
                {
                    mode = JavaScanMode.Normal;
                    index++;
                    return true;
                }
                index++;
                return true;
            case JavaScanMode.BlockComment:
                if (ch == '*' && index + 1 < span.Length && span[index + 1] == '/')
                {
                    mode = JavaScanMode.Normal;
                    index += 2;
                    return true;
                }
                index++;
                return true;
            case JavaScanMode.TextBlock:
                if (ch == '"' && index + 2 < span.Length && span[index + 1] == '"' && span[index + 2] == '"')
                {
                    mode = JavaScanMode.Normal;
                    index += 3;
                    return true;
                }
                if (ch == '\\' && index + 1 < span.Length)
                {
                    index += 2;
                    return true;
                }
                index++;
                return true;
            default:
                if (ch == '/' && index + 1 < span.Length && span[index + 1] == '/')
                {
                    mode = JavaScanMode.LineComment;
                    index += 2;
                    return true;
                }
                if (ch == '/' && index + 1 < span.Length && span[index + 1] == '*')
                {
                    mode = JavaScanMode.BlockComment;
                    index += 2;
                    return true;
                }
                if (ch == '"' && index + 2 < span.Length && span[index + 1] == '"' && span[index + 2] == '"')
                {
                    mode = JavaScanMode.TextBlock;
                    index += 3;
                    return true;
                }
                if (ch == '"')
                {
                    mode = JavaScanMode.String;
                    index++;
                    return true;
                }
                if (ch == '\'')
                {
                    mode = JavaScanMode.Char;
                    index++;
                    return true;
                }
                return false;
        }
    }

    private static bool TryGetFirstNonWhitespaceColumn(string line, int startColumn, int endColumnExclusive, out int column)
    {
        for (column = Math.Min(startColumn, line.Length); column < Math.Min(endColumnExclusive, line.Length); column++)
        {
            if (!char.IsWhiteSpace(line[column]))
                return true;
        }

        column = -1;
        return false;
    }

    private static int FindCSharpDeclarationStartColumn(string rawLine, string? signature)
    {
        if (!string.IsNullOrWhiteSpace(signature))
        {
            var signatureIndex = rawLine.IndexOf(signature, StringComparison.Ordinal);
            if (signatureIndex >= 0)
                return signatureIndex;
        }

        return rawLine.IndexOf("enum ", StringComparison.Ordinal);
    }

    private static bool TryRecoverBrokenCSharpEnumAttributeInBody(
        string[] csharpMatchLines,
        int startLineIndex,
        int bodyEndLineIndex,
        int bodyEndColumnExclusive,
        out (int LineIndex, int Column) recoveredPosition)
    {
        recoveredPosition = default;
        for (var lineIndex = startLineIndex + 1; lineIndex <= bodyEndLineIndex; lineIndex++)
        {
            var line = csharpMatchLines[lineIndex];
            var scanEndColumnExclusive = lineIndex == bodyEndLineIndex
                ? Math.Min(bodyEndColumnExclusive, line.Length)
                : line.Length;

            if (!TryGetFirstNonWhitespaceColumn(line, 0, scanEndColumnExclusive, out var firstNonWhitespaceColumn))
                continue;

            var first = line[firstNonWhitespaceColumn];
            if (first is '#' or '[' or '}')
                continue;

            if (!CSharpEnumMemberNameRegex.IsMatch(line[firstNonWhitespaceColumn..scanEndColumnExclusive]))
                continue;

            recoveredPosition = (lineIndex, firstNonWhitespaceColumn);
            return true;
        }

        return false;
    }

    private static bool TrySkipLeadingCSharpAttributeListsInEnumBody(
        string[] csharpMatchLines,
        int startLineIndex,
        int startColumn,
        int bodyEndLineIndex,
        int bodyEndColumnExclusive,
        out (int LineIndex, int Column) nextPosition)
    {
        nextPosition = (startLineIndex, startColumn);
        var lineIndex = startLineIndex;
        var column = startColumn;

        while (lineIndex <= bodyEndLineIndex)
        {
            var line = csharpMatchLines[lineIndex];
            var lineEndExclusive = lineIndex == bodyEndLineIndex
                ? Math.Min(bodyEndColumnExclusive, line.Length)
                : line.Length;
            var probe = column;

            while (probe < lineEndExclusive && char.IsWhiteSpace(line[probe]))
                probe++;

            if (probe >= lineEndExclusive)
            {
                lineIndex++;
                column = 0;
                continue;
            }

            if (line[probe] != '[')
            {
                if (probe == startColumn && lineIndex == startLineIndex)
                    return false;

                nextPosition = (lineIndex, probe);
                return true;
            }

            var bracketDepth = 0;
            var parenDepth = 0;
            var currentLineIndex = lineIndex;
            var currentColumn = probe;
            var matchedAttribute = false;

            while (currentLineIndex <= bodyEndLineIndex)
            {
                var currentLine = csharpMatchLines[currentLineIndex];
                var currentLineEndExclusive = currentLineIndex == bodyEndLineIndex
                    ? Math.Min(bodyEndColumnExclusive, currentLine.Length)
                    : currentLine.Length;

                while (currentColumn < currentLineEndExclusive)
                {
                    var ch = currentLine[currentColumn++];
                    if (ch == '[')
                    {
                        bracketDepth++;
                    }
                    else if (ch == '(')
                    {
                        parenDepth++;
                    }
                    else if (ch == ')' && parenDepth > 0)
                    {
                        parenDepth--;
                    }
                    else if (ch == ']')
                    {
                        if (bracketDepth > 0)
                            bracketDepth--;

                        if (bracketDepth == 0)
                        {
                            matchedAttribute = true;
                            break;
                        }
                    }
                }

                if (matchedAttribute)
                {
                    lineIndex = currentLineIndex;
                    column = currentColumn;
                    break;
                }

                currentLineIndex++;
                currentColumn = 0;
            }

            if (!matchedAttribute)
                return false;
        }

        nextPosition = (bodyEndLineIndex, bodyEndColumnExclusive);
        return true;
    }

    private static void TryAddCSharpEnumMemberFromSpan(
        long fileId,
        SymbolRecord enumSymbol,
        string[] rawLines,
        string[] csharpMatchLines,
        (int LineIndex, int Column) start,
        (int LineIndex, int Column) endExclusive,
        List<SymbolRecord> symbols)
    {
        endExclusive = TrimTrailingCSharpEnumMemberSpan(rawLines, start, endExclusive);
        var maskedSnippet = GetSourceSpanText(csharpMatchLines, start, endExclusive);
        if (string.IsNullOrWhiteSpace(maskedSnippet))
            return;

        var match = CSharpEnumMemberNameRegex.Match(maskedSnippet);
        if (!match.Success)
            return;

        var rawSignature = GetSourceSpanText(rawLines, start, endExclusive).Trim();
        if (rawSignature.Length == 0)
            return;

        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "enum",
            Name = match.Groups["name"].Value.Trim(),
            Line = start.LineIndex + 1,
            StartLine = start.LineIndex + 1,
            EndLine = endExclusive.LineIndex + 1,
            Signature = rawSignature,
            ContainerKind = "enum",
            ContainerName = enumSymbol.Name,
        });
    }

    private static (int LineIndex, int Column) TrimTrailingCSharpEnumMemberSpan(
        string[] rawLines,
        (int LineIndex, int Column) start,
        (int LineIndex, int Column) endExclusive)
    {
        var lineIndex = Math.Min(endExclusive.LineIndex, rawLines.Length - 1);
        var column = lineIndex >= 0
            ? Math.Min(endExclusive.Column, rawLines[lineIndex].Length)
            : 0;

        while (lineIndex > start.LineIndex || (lineIndex == start.LineIndex && column > start.Column))
        {
            if (column == 0)
            {
                lineIndex--;
                if (lineIndex < 0)
                    break;
                column = rawLines[lineIndex].Length;
                continue;
            }

            var previous = rawLines[lineIndex][column - 1];
            if (!char.IsWhiteSpace(previous) && previous != '}')
                break;

            column--;
        }

        return (lineIndex, column);
    }

    private static string GetSourceSpanText(
        string[] lines,
        (int LineIndex, int Column) start,
        (int LineIndex, int Column) endExclusive)
    {
        if (start.LineIndex > endExclusive.LineIndex
            || start.LineIndex < 0
            || endExclusive.LineIndex >= lines.Length)
        {
            return string.Empty;
        }

        if (start.LineIndex == endExclusive.LineIndex)
        {
            var line = lines[start.LineIndex];
            var effectiveLength = GetLineLengthExcludingTrailingCr(line);
            var startColumn = Math.Min(start.Column, effectiveLength);
            var endColumn = Math.Min(Math.Max(endExclusive.Column, startColumn), effectiveLength);
            return line[startColumn..endColumn];
        }

        var builder = new StringBuilder();
        for (int lineIndex = start.LineIndex; lineIndex <= endExclusive.LineIndex; lineIndex++)
        {
            var line = lines[lineIndex];
            // Content was split on '\n', so CRLF lines carry a trailing '\r'. Exclude it from
            // the effective length so the multi-line separator stays '\n' regardless of source
            // line endings and signatures stay OS-independent (see #382 / #405).
            // content は '\n' で分割しているため、CRLF 行は末尾に '\r' が残る。multi-line の
            // 区切りを OS に依存せず '\n' に揃え signature の一致判定を保つため、effective
            // length からは '\r' を除外する（#382 / #405 参照）。
            var effectiveLength = GetLineLengthExcludingTrailingCr(line);
            var startColumn = lineIndex == start.LineIndex
                ? Math.Min(start.Column, effectiveLength)
                : 0;
            var endColumn = lineIndex == endExclusive.LineIndex
                ? Math.Min(Math.Max(endExclusive.Column, startColumn), effectiveLength)
                : effectiveLength;

            builder.Append(line[startColumn..endColumn]);
            if (lineIndex < endExclusive.LineIndex)
                builder.Append('\n');
        }

        return builder.ToString();
    }

    private static int GetLineLengthExcludingTrailingCr(string line)
    {
        var length = line.Length;
        if (length > 0 && line[length - 1] == '\r')
            length--;
        return length;
    }

    private static string StripTrailingCr(string line)
    {
        if (line.Length > 0 && line[^1] == '\r')
            return line[..^1];
        return line;
    }

    private static void ExtractJavaScriptTypeScriptBareMethods(long fileId, string lang, string[] lines, List<SymbolRecord> symbols, JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        var existingClassTargets = GetJavaScriptTypeScriptExistingClassScanTargets(lang, lines, symbols);
        ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, existingClassTargets);

        var syntheticClassTargets = CollectJavaScriptTypeScriptSyntheticClassScanTargets(fileId, lang, lines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, syntheticClassTargets);

        var objectLiteralTargets = CollectJavaScriptTypeScriptObjectLiteralScanTargets(lang, lines, privateScopeColumns);
        ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, objectLiteralTargets);
        ExtractJavaScriptTypeScriptExportSurfaceSymbols(fileId, lang, lines, symbols, privateScopeColumns, objectLiteralTargets);
        ExtractJavaScriptTypeScriptQualifiedAssignments(fileId, lang, lines, symbols, privateScopeColumns);
    }

    // Scans for object literal declarations (`const obj = { ... }`, `module.exports = { ... }`
    // etc.) and builds class-body scan targets with ContainerKind="object". The class-body
    // scanner already handles method shorthand (`name()`, `get/set name()`, `*name()`,
    // `async name()`), so routing object literals through the same scanner picks up those
    // members without a separate pass. Nested function/class scopes are skipped via
    // privateScopeColumns so method bodies don't leak inner-object methods back to the top level.
    // `const obj = { ... }` や `module.exports = { ... }` 等のオブジェクトリテラル宣言を走査し、
    // ContainerKind="object" のクラスボディ用スキャンターゲットを構築する。クラスボディスキャナは
    // 既に method shorthand (`name()`, `get/set name()`, `*name()`, `async name()`) を扱うため、
    // 同じスキャナ経由でオブジェクトリテラルのメンバを抽出できる。ネストされた function/class
    // スコープは privateScopeColumns で弾き、内側のオブジェクトメンバをトップレベルに漏らさない。
    private static List<JavaScriptClassScanTarget> CollectJavaScriptTypeScriptObjectLiteralScanTargets(
        string lang,
        string[] lines,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        var targets = new List<JavaScriptClassScanTarget>();
        var lexState = new JavaScriptLexState();
        for (int i = 0; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            var bindingMatch = JavaScriptTypeScriptObjectLiteralBindingRegex.Match(sanitizedLine);
            Match? exportDefaultMatch = null;
            if (!bindingMatch.Success)
            {
                var edm = JavaScriptTypeScriptExportDefaultObjectLiteralRegex.Match(sanitizedLine);
                if (!edm.Success)
                    continue;
                exportDefaultMatch = edm;
            }
            var match = exportDefaultMatch ?? bindingMatch;
            var isExportDefault = exportDefaultMatch != null;

            // Skip declarations nested inside a function/class body, and — for non-exported
            // const/let bindings — also inside block scopes or namespace scopes. The object
            // literal itself may be legitimate, but its method-shorthand members are already
            // reachable via the enclosing scope, and emitting them would leak non-public names
            // to the top level. `var` stays function-scoped so block-scope skip is not applied;
            // `module.exports` / `exports.X` / `export const` / `export default` are treated as
            // exported and kept.
            // function/class 本体内のネストした宣言はスキップする。加えて非 export の const/let は
            // ブロックスコープや namespace スコープも private 扱いにする。var は function スコープのため
            // ブロックスコープは除外せず、module.exports / exports.X / export const / export default は
            // export 扱いで維持する。
            var includeBlockScope = !isExportDefault
                && bindingMatch.Groups["bindingKind"].Success
                && bindingMatch.Groups["bindingKind"].Value is "const" or "let";
            if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, match.Index, sanitizedLine, includeBlockScope))
                continue;

            var isExported = isExportDefault
                || TryGetGroup(bindingMatch, "visibility") == "export"
                || bindingMatch.Groups["exportsAlias"].Success
                || bindingMatch.Groups["moduleExportsAlias"].Success
                || bindingMatch.Groups["moduleExports"].Success;
            if (!isExported
                && IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, i, match.Index, sanitizedLine))
            {
                continue;
            }

            if (!TryFindJavaScriptTypeScriptObjectLiteralOpenBrace(
                    lines,
                    i,
                    match.Index + match.Length,
                    sanitizedLine,
                    lexState,
                    out var openBraceLineIndex,
                    out var openBraceColumn))
            {
                continue;
            }

            var (_, bodyStartLine, bodyEndLine) = ResolveRange(lines, openBraceLineIndex, BodyStyle.Brace, lang, openBraceColumn);
            if (bodyStartLine == null || bodyEndLine == null)
                continue;

            var containerName = isExportDefault
                ? "default"
                : (TryGetGroup(bindingMatch, "alias")
                    ?? TryGetGroup(bindingMatch, "exportsAlias")
                    ?? TryGetGroup(bindingMatch, "moduleExportsAlias")
                    ?? (bindingMatch.Groups["moduleExports"].Success ? "module.exports" : null)
                    ?? "object");

            var candidate = CreateJavaScriptClassScanTarget(
                lines,
                lang,
                i,
                match.Index,
                bodyStartLine,
                bodyEndLine,
                containerKind: "object",
                containerName: containerName,
                isExported: isExported);

            if (!targets.Any(t => t.StartIndex == candidate.StartIndex
                && t.ScanStartIndex == candidate.ScanStartIndex
                && t.ScanEndExclusive == candidate.ScanEndExclusive
                && t.ContainerName == candidate.ContainerName))
            {
                targets.Add(candidate);
            }
        }

        return targets
            .OrderBy(t => t.StartIndex)
            .ThenByDescending(t => t.ScanEndExclusive)
            .ToList();
    }

    private static void ExtractJavaScriptTypeScriptExportSurfaceSymbols(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns,
        List<JavaScriptClassScanTarget> objectLiteralTargets)
    {
        var sanitizedLines = BuildJavaScriptTypeScriptSanitizedLines(lines);
        ExtractJavaScriptTypeScriptReExportSymbols(fileId, lang, lines, sanitizedLines, symbols);
        ExtractJavaScriptTypeScriptCommonJsNamedExportAssignments(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptExportedObjectLiteralProperties(fileId, lines, sanitizedLines, symbols, objectLiteralTargets);
    }

    private static void ExtractJavaScriptTypeScriptQualifiedAssignments(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        var sanitizedLines = BuildJavaScriptTypeScriptSanitizedLines(lines);
        var syntheticClassTargets = new List<JavaScriptClassScanTarget>();

        for (int i = 0; i < lines.Length; i++)
        {
            var sanitizedLine = sanitizedLines[i];
            var statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (statementStart >= 0)
            {
                var statementSlice = sanitizedLine[statementStart..];
                var match = JavaScriptTypeScriptQualifiedAssignmentRegex.Match(statementSlice);
                if (!match.Success)
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var absoluteMatchIndex = statementStart + match.Index;
                if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, absoluteMatchIndex, sanitizedLine, includeBlockScope: false)
                    || IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, i, absoluteMatchIndex, sanitizedLine))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var name = match.Groups["name"].Value;
                if (!TryCollectJavaScriptTypeScriptAssignedRhs(
                        lines,
                        sanitizedLines,
                        i,
                        absoluteMatchIndex,
                        statementStart + match.Groups["rhs"].Index,
                        lang,
                        out var rhs,
                        out var rhsStartLineIndex,
                        out var rhsStartColumn,
                        out var rhsEndLineIndex,
                        out var rhsEndColumn,
                        out var signature))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var classificationRhs = StartsJavaScriptTypeScriptPotentialGenericArrowAssignmentValue(rhs)
                    ? CollectJavaScriptTypeScriptAssignedRhsHeader(sanitizedLines, rhsStartLineIndex, rhsStartColumn)
                    : rhs;

                if (!StartsJavaScriptTypeScriptFunctionAssignmentValue(classificationRhs)
                    && TryFindJavaScriptTypeScriptAssignedRhsStart(
                        sanitizedLines,
                        i,
                        statementStart + match.Groups["rhs"].Index,
                        out var fallbackRhsStartLineIndex,
                        out var fallbackRhsStartColumn))
                {
                    var fallbackClassificationRhs = CollectJavaScriptTypeScriptAssignedRhsHeader(
                        sanitizedLines,
                        fallbackRhsStartLineIndex,
                        fallbackRhsStartColumn);
                    if (StartsJavaScriptTypeScriptFunctionAssignmentValue(fallbackClassificationRhs))
                        classificationRhs = fallbackClassificationRhs;
                }

                if (classificationRhs.Length == 0)
                {
                    if (rhsEndLineIndex > i)
                        i = rhsEndLineIndex;
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, rhsEndColumn + 1);
                    continue;
                }

                if (StartsJavaScriptTypeScriptClassAssignmentValue(classificationRhs))
                {
                    if (!TryGetJavaScriptTypeScriptNextToken(
                        lines,
                        rhsStartLineIndex,
                        rhsStartColumn,
                        skipWrappingParens: true,
                        out var classTokenLineIndex,
                        out var classTokenStartColumn,
                        out _))
                    {
                        statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                        continue;
                    }

                    AddJavaScriptTypeScriptSyntheticClassTarget(
                        fileId,
                        lang,
                        lines,
                        symbols,
                        syntheticClassTargets,
                        i,
                        absoluteMatchIndex,
                        classTokenLineIndex,
                        classTokenStartColumn,
                        name,
                        visibility: null);

                    if (rhsEndLineIndex > i)
                        i = rhsEndLineIndex;
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, rhsEndColumn + 1);
                    continue;
                }

                var kind = StartsJavaScriptTypeScriptFunctionAssignmentValue(classificationRhs)
                    ? "function"
                    : "property";

                int? bodyStartLine = null;
                int? bodyEndLine = null;
                if (kind == "function"
                    && TryFindJavaScriptTypeScriptAssignedFunctionBodyOpenBrace(
                        lines,
                        rhsStartLineIndex,
                        rhsStartColumn,
                        lang,
                        out var openBraceLineIndex,
                        out var openBraceColumn))
                {
                    var (_, resolvedBodyStartLine, resolvedBodyEndLine) = ResolveRange(lines, openBraceLineIndex, BodyStyle.Brace, lang, openBraceColumn);
                    bodyStartLine = resolvedBodyStartLine;
                    bodyEndLine = resolvedBodyEndLine;
                }

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    i + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = kind,
                        Name = name,
                        Line = i + 1,
                        StartLine = i + 1,
                        StartColumn = absoluteMatchIndex,
                        EndLine = Math.Max(i + 1, bodyEndLine ?? (i + 1)),
                        BodyStartLine = bodyStartLine,
                        BodyEndLine = bodyEndLine,
                        Signature = signature,
                    },
                    lines[i]);

                if (rhsEndLineIndex > i)
                    i = rhsEndLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, rhsEndColumn + 1);
            }
        }

        if (syntheticClassTargets.Count > 0)
            ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, syntheticClassTargets);
    }

    private static string[] BuildJavaScriptTypeScriptSanitizedLines(string[] lines)
    {
        var sanitizedLines = new string[lines.Length];
        var lexState = new JavaScriptLexState();
        for (int i = 0; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            sanitizedLines[i] = lexedLine.SanitizedLine;
            lexState = lexedLine.EndState;
        }

        return sanitizedLines;
    }

    private static void ExtractJavaScriptTypeScriptReExportSymbols(long fileId, string lang, string[] rawLines, string[] sanitizedLines, List<SymbolRecord> symbols)
    {
        for (int i = 0; i < sanitizedLines.Length; i++)
        {
            var line = sanitizedLines[i];
            var statementStart = FindNextJavaScriptTypeScriptStatementStart(line, 0);
            while (statementStart >= 0)
            {
                if (TryCollectJavaScriptTypeScriptStarReExportClause(
                        lang,
                        rawLines,
                        sanitizedLines,
                        i,
                        statementStart,
                        out var starEndLineIndex,
                        out var starEndColumn,
                        out var starClause,
                        out var starSignature,
                        out var starStartColumn))
                {
                    var starMatch = JavaScriptTypeScriptStarReExportRegex.Match(starClause);
                    if (starMatch.Success)
                    {
                        if (TryExtractJavaScriptTypeScriptReExportModuleName(
                                rawLines,
                                sanitizedLines,
                                i,
                                starEndLineIndex,
                                starStartColumn,
                                waitForClosedSpecifierList: false,
                                out var moduleName))
                        {
                            AddSymbolRecord(
                                symbols,
                                cssSeenSymbols: null,
                                i + 1,
                                new SymbolRecord
                                {
                                    FileId = fileId,
                                    Kind = "import",
                                    Name = moduleName,
                                    Line = i + 1,
                                    StartLine = i + 1,
                                    StartColumn = starStartColumn,
                                    EndLine = starEndLineIndex + 1,
                                    Signature = starSignature,
                                    Visibility = "export",
                                },
                                rawLines[i]);
                        }

                        var namespaceName = starMatch.Groups["namespace"].Value;
                        if (namespaceName.Length > 0)
                        {
                            AddSymbolRecord(
                                symbols,
                                cssSeenSymbols: null,
                                i + 1,
                                new SymbolRecord
                                {
                                    FileId = fileId,
                                    Kind = "property",
                                    Name = namespaceName,
                                    Line = i + 1,
                                    StartLine = i + 1,
                                    StartColumn = starStartColumn,
                                    EndLine = starEndLineIndex + 1,
                                    Signature = starSignature,
                                    Visibility = "export",
                                },
                                rawLines[i]);
                        }
                    }

                    if (starEndLineIndex > i)
                    {
                        i = starEndLineIndex;
                        statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], starEndColumn + 1);
                    }
                    else
                    {
                        statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], starEndColumn + 1);
                    }

                    continue;
                }

                if (!TryCollectJavaScriptTypeScriptNamedReExportClause(
                        lang,
                        rawLines,
                        sanitizedLines,
                        i,
                        statementStart,
                        out var endLineIndex,
                        out var endColumn,
                        out var clause,
                        out var signatureText,
                        out var startColumnText))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], statementStart + 1);
                    continue;
                }

                var namedMatch = JavaScriptTypeScriptNamedReExportRegex.Match(clause);
                if (!namedMatch.Success)
                {
                    if (endLineIndex > i)
                        i = endLineIndex;
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], endColumn + 1);
                    continue;
                }

                if (TryExtractJavaScriptTypeScriptReExportModuleName(
                        rawLines,
                        sanitizedLines,
                        i,
                        endLineIndex,
                        startColumnText,
                        waitForClosedSpecifierList: true,
                        out var namedModuleName))
                {
                    AddSymbolRecord(
                        symbols,
                        cssSeenSymbols: null,
                        i + 1,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "import",
                            Name = namedModuleName,
                            Line = i + 1,
                            StartLine = i + 1,
                            StartColumn = startColumnText,
                            EndLine = endLineIndex + 1,
                            Signature = signatureText,
                            Visibility = "export",
                        },
                        rawLines[i]);
                }

                foreach (var exportedName in ParseJavaScriptTypeScriptReExportedNames(namedMatch.Groups["specifiers"].Value))
                {
                    AddSymbolRecord(
                        symbols,
                        cssSeenSymbols: null,
                        i + 1,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "property",
                            Name = exportedName,
                            Line = i + 1,
                            StartLine = i + 1,
                            StartColumn = startColumnText,
                            EndLine = endLineIndex + 1,
                            Signature = signatureText,
                            Visibility = "export",
                        },
                        rawLines[i]);
                }

                if (endLineIndex > i)
                    i = endLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], endColumn + 1);
            }
        }
    }

    private static bool TryCollectJavaScriptTypeScriptStarReExportClause(
        string lang,
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        out int endLineIndex,
        out int endColumn,
        out string clause,
        out string signature,
        out int startColumnText)
    {
        endLineIndex = startLineIndex;
        endColumn = -1;
        clause = string.Empty;
        signature = string.Empty;

        var startLine = sanitizedLines[startLineIndex];
        if (startColumn < 0 || startColumn >= startLine.Length)
        {
            startColumnText = -1;
            return false;
        }

        var startLineSlice = startLine[startColumn..];
        var trimmedStartLine = startLineSlice.TrimStart();
        if (trimmedStartLine.Length == 0
            || !trimmedStartLine.StartsWith("export", StringComparison.Ordinal))
        {
            startColumnText = -1;
            return false;
        }

        var exportRemainder = trimmedStartLine["export".Length..].TrimStart();
        var starRemainder = SkipJavaScriptTypeScriptTypeOnlyExportModifier(exportRemainder);
        if (starRemainder.Length > 0 && starRemainder[0] != '*')
        {
            startColumnText = -1;
            return false;
        }

        startColumnText = startColumn + startLineSlice.IndexOf("export", StringComparison.Ordinal);

        var clauseBuilder = new System.Text.StringBuilder();
        var signatureBuilder = new System.Text.StringBuilder();

        for (int lineIndex = startLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var rawLine = rawLines[lineIndex];
            var lineStartColumn = lineIndex == startLineIndex ? startColumnText : 0;
            var lineEndColumn = FindJavaScriptTypeScriptSameLineStatementEndColumn(sanitizedLine, lineStartColumn, lang);
            var lineEndExclusive = lineEndColumn >= lineStartColumn
                ? lineEndColumn + 1
                : sanitizedLine.Length;

            var sanitizedSlice = sanitizedLine[lineStartColumn..lineEndExclusive].Trim();
            if (sanitizedSlice.Length > 0)
            {
                if (clauseBuilder.Length > 0)
                    clauseBuilder.Append(' ');
                clauseBuilder.Append(sanitizedSlice);
            }

            var rawSlice = rawLine[lineStartColumn..Math.Min(rawLine.Length, lineEndExclusive)].Trim();
            if (rawSlice.Length > 0)
            {
                if (signatureBuilder.Length > 0)
                    signatureBuilder.Append(' ');
                signatureBuilder.Append(rawSlice);
            }

            endLineIndex = lineIndex;
            endColumn = lineEndColumn >= lineStartColumn ? lineEndColumn : sanitizedLine.Length - 1;

            clause = clauseBuilder.ToString().Trim();
            if (!clause.StartsWith("export", StringComparison.Ordinal))
                break;

            var clauseRemainder = SkipJavaScriptTypeScriptTypeOnlyExportModifier(clause["export".Length..].TrimStart());
            if (clauseRemainder.Length == 0 || clauseRemainder[0] != '*')
                break;

            if (JavaScriptTypeScriptStarReExportRegex.IsMatch(clause))
            {
                signature = signatureBuilder.ToString().Trim();
                return true;
            }

            if (lineEndColumn >= lineStartColumn)
                break;
        }

        endLineIndex = startLineIndex;
        endColumn = -1;
        clause = string.Empty;
        signature = string.Empty;
        startColumnText = -1;
        return false;
    }

    private static bool TryCollectJavaScriptTypeScriptNamedReExportClause(
        string lang,
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        out int endLineIndex,
        out int endColumn,
        out string clause,
        out string signature,
        out int startColumnText)
    {
        endLineIndex = startLineIndex;
        endColumn = -1;
        clause = string.Empty;
        signature = string.Empty;

        var startLine = sanitizedLines[startLineIndex];
        if (startColumn < 0 || startColumn >= startLine.Length)
        {
            startColumnText = -1;
            return false;
        }

        var startLineSlice = startLine[startColumn..];
        var trimmedStartLine = startLineSlice.TrimStart();
        if (trimmedStartLine.Length == 0
            || !trimmedStartLine.StartsWith("export", StringComparison.Ordinal))
        {
            startColumnText = -1;
            return false;
        }

        var exportRemainder = trimmedStartLine["export".Length..].TrimStart();
        if (exportRemainder.Length > 0)
        {
            if (exportRemainder[0] == '{')
            {
                // Valid same-line named re-export.
            }
            else if (exportRemainder.StartsWith("type", StringComparison.Ordinal))
            {
                var typeRemainder = exportRemainder["type".Length..].TrimStart();
                if (typeRemainder.Length > 0 && typeRemainder[0] != '{')
                {
                    startColumnText = -1;
                    return false;
                }
            }
            else
            {
                startColumnText = -1;
                return false;
            }
        }

        startColumnText = startColumn + startLineSlice.IndexOf("export", StringComparison.Ordinal);

        var clauseBuilder = new System.Text.StringBuilder();
        var signatureBuilder = new System.Text.StringBuilder();

        for (int lineIndex = startLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var rawLine = rawLines[lineIndex];
            var lineStartColumn = lineIndex == startLineIndex ? startColumnText : 0;
            var lineEndColumn = FindJavaScriptTypeScriptSameLineStatementEndColumn(sanitizedLine, lineStartColumn, lang);
            var lineEndExclusive = lineEndColumn >= lineStartColumn
                ? lineEndColumn + 1
                : sanitizedLine.Length;

            var sanitizedSlice = sanitizedLine[lineStartColumn..lineEndExclusive].Trim();
            if (sanitizedSlice.Length > 0)
            {
                if (clauseBuilder.Length > 0)
                    clauseBuilder.Append(' ');
                clauseBuilder.Append(sanitizedSlice);
            }

            var rawSlice = rawLine[lineStartColumn..Math.Min(rawLine.Length, lineEndExclusive)].Trim();
            if (rawSlice.Length > 0)
            {
                if (signatureBuilder.Length > 0)
                    signatureBuilder.Append(' ');
                signatureBuilder.Append(rawSlice);
            }

            endLineIndex = lineIndex;
            endColumn = lineEndColumn >= lineStartColumn ? lineEndColumn : sanitizedLine.Length - 1;

            clause = clauseBuilder.ToString().Trim();
            if (JavaScriptTypeScriptNamedReExportRegex.IsMatch(clause))
            {
                signature = signatureBuilder.ToString().Trim();
                return true;
            }

            if (lineEndColumn >= lineStartColumn)
                break;
        }

        endLineIndex = startLineIndex;
        endColumn = -1;
        clause = string.Empty;
        signature = string.Empty;
        startColumnText = -1;
        return false;
    }

    private static void ExtractJavaScriptTypeScriptCommonJsNamedExportAssignments(
        long fileId,
        string lang,
        string[] rawLines,
        string[] sanitizedLines,
        List<SymbolRecord> symbols,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        for (int i = 0; i < sanitizedLines.Length; i++)
        {
            var sanitizedLine = sanitizedLines[i];
            var statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (statementStart >= 0)
            {
                var statementSlice = sanitizedLine[statementStart..];
                var match = JavaScriptTypeScriptCommonJsNamedExportAssignmentRegex.Match(statementSlice);
                if (!match.Success)
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var absoluteMatchIndex = statementStart + match.Index;
                if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, absoluteMatchIndex, sanitizedLine, includeBlockScope: false)
                    || IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, i, absoluteMatchIndex, sanitizedLine))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var name = match.Groups["name"].Value;
                if (!TryCollectJavaScriptTypeScriptAssignedRhs(
                        rawLines,
                        sanitizedLines,
                        i,
                        absoluteMatchIndex,
                        statementStart + match.Groups["rhs"].Index,
                        lang,
                        out var rhs,
                        out var rhsStartLineIndex,
                        out var rhsStartColumn,
                        out var rhsEndLineIndex,
                        out var rhsEndColumn,
                        out var signature))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var classificationRhs = StartsJavaScriptTypeScriptPotentialGenericArrowAssignmentValue(rhs)
                    ? CollectJavaScriptTypeScriptAssignedRhsHeader(sanitizedLines, rhsStartLineIndex, rhsStartColumn)
                    : rhs;

                if (!StartsJavaScriptTypeScriptFunctionAssignmentValue(classificationRhs)
                    && TryFindJavaScriptTypeScriptAssignedRhsStart(
                             sanitizedLines,
                             i,
                             statementStart + match.Groups["rhs"].Index,
                             out var fallbackRhsStartLineIndex,
                             out var fallbackRhsStartColumn))
                {
                    var fallbackClassificationRhs = CollectJavaScriptTypeScriptAssignedRhsHeader(
                        sanitizedLines,
                        fallbackRhsStartLineIndex,
                        fallbackRhsStartColumn);
                    if (StartsJavaScriptTypeScriptFunctionAssignmentValue(fallbackClassificationRhs))
                        classificationRhs = fallbackClassificationRhs;
                }

                if (classificationRhs.Length == 0
                    || StartsJavaScriptTypeScriptClassAssignmentValue(classificationRhs))
                {
                    if (rhsEndLineIndex > i)
                        i = rhsEndLineIndex;
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], rhsEndColumn + 1);
                    continue;
                }

                var kind = StartsJavaScriptTypeScriptFunctionAssignmentValue(classificationRhs)
                    ? "function"
                    : "property";

                int? bodyStartLine = null;
                int? bodyEndLine = null;
                if (kind == "function")
                {
                    if (TryFindJavaScriptTypeScriptAssignedFunctionBodyOpenBrace(
                            rawLines,
                            rhsStartLineIndex,
                            rhsStartColumn,
                            lang,
                            out var openBraceLineIndex,
                            out var openBraceColumn))
                    {
                        var (_, resolvedBodyStartLine, resolvedBodyEndLine) = ResolveRange(rawLines, openBraceLineIndex, BodyStyle.Brace, lang, openBraceColumn);
                        bodyStartLine = resolvedBodyStartLine;
                        bodyEndLine = resolvedBodyEndLine;
                    }
                }

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    i + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = kind,
                        Name = name,
                        Line = i + 1,
                        StartLine = i + 1,
                        StartColumn = absoluteMatchIndex,
                        EndLine = Math.Max(i + 1, bodyEndLine ?? (i + 1)),
                        BodyStartLine = bodyStartLine,
                        BodyEndLine = bodyEndLine,
                        Signature = signature,
                        Visibility = "export",
                    },
                    rawLines[i]);

                if (rhsEndLineIndex > i)
                    i = rhsEndLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], rhsEndColumn + 1);
            }
        }
    }

    private static void ExtractJavaScriptTypeScriptExportedObjectLiteralProperties(
        long fileId,
        string[] rawLines,
        string[] sanitizedLines,
        List<SymbolRecord> symbols,
        List<JavaScriptClassScanTarget> objectLiteralTargets)
    {
        foreach (var target in objectLiteralTargets.Where(t => t.IsExported))
        {
            var braceDepth = 0;
            var parenDepth = 0;
            var bracketDepth = 0;
            var skippingPropertyValue = false;

            for (int lineIndex = target.ScanStartIndex; lineIndex < target.ScanEndExclusive; lineIndex++)
            {
                var sanitizedLine = sanitizedLines[lineIndex];
                var scanColumn = lineIndex == target.ScanStartIndex
                    ? target.FirstLineScanOffset
                    : 0;

                while (scanColumn < sanitizedLine.Length)
                {
                    var ch = sanitizedLine[scanColumn];
                    if (skippingPropertyValue)
                    {
                        if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                        {
                            if (ch == ',')
                            {
                                skippingPropertyValue = false;
                                scanColumn++;
                                continue;
                            }

                            if (ch == '}')
                            {
                                skippingPropertyValue = false;
                                continue;
                            }
                        }

                        switch (ch)
                        {
                            case '{':
                                braceDepth++;
                                break;
                            case '}':
                                if (braceDepth > 0)
                                    braceDepth--;
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
                        }

                        scanColumn++;
                        continue;
                    }

                    if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                    {
                        while (scanColumn < sanitizedLine.Length
                            && (char.IsWhiteSpace(sanitizedLine[scanColumn]) || sanitizedLine[scanColumn] is ',' or ';'))
                        {
                            scanColumn++;
                        }

                        if (scanColumn >= sanitizedLine.Length)
                            break;

                        var remainingLine = sanitizedLine[scanColumn..];
                        if (remainingLine.StartsWith("...", StringComparison.Ordinal))
                        {
                            scanColumn += 3;
                            skippingPropertyValue = true;
                            continue;
                        }

                        var propertyMatch = JavaScriptTypeScriptExportedObjectLiteralPropertyRegex.Match(remainingLine);
                        if (propertyMatch.Success)
                        {
                            var propertyName = propertyMatch.Groups["name"].Value;
                            var hasExistingContainerSymbol = symbols.Any(s =>
                                s.Name == propertyName
                                && s.ContainerKind == "object"
                                && s.ContainerName == target.ContainerName);
                            if (!hasExistingContainerSymbol)
                            {
                                AddSymbolRecord(
                                    symbols,
                                    cssSeenSymbols: null,
                                    lineIndex + 1,
                                    new SymbolRecord
                                    {
                                        FileId = fileId,
                                        Kind = "property",
                                        Name = propertyName,
                                        Line = lineIndex + 1,
                                        StartLine = lineIndex + 1,
                                        StartColumn = scanColumn + propertyMatch.Index,
                                        EndLine = lineIndex + 1,
                                        Signature = rawLines[lineIndex].Trim(),
                                        ContainerKind = "object",
                                        ContainerName = target.ContainerName,
                                        Visibility = "export",
                                    },
                                    rawLines[lineIndex]);
                            }

                            scanColumn += propertyMatch.Length;
                            skippingPropertyValue = true;
                            continue;
                        }

                        if (TrySkipJavaScriptTypeScriptNonIdentifierObjectLiteralKey(sanitizedLine, ref scanColumn))
                        {
                            skippingPropertyValue = true;
                            continue;
                        }

                        var shorthandMatch = JavaScriptTypeScriptExportedObjectLiteralShorthandPropertyRegex.Match(remainingLine);
                        if (shorthandMatch.Success)
                        {
                            var propertyName = shorthandMatch.Groups["name"].Value;
                            var hasExistingContainerSymbol = symbols.Any(s =>
                                s.Name == propertyName
                                && s.ContainerKind == "object"
                                && s.ContainerName == target.ContainerName);
                            if (!hasExistingContainerSymbol)
                            {
                                AddSymbolRecord(
                                    symbols,
                                    cssSeenSymbols: null,
                                    lineIndex + 1,
                                    new SymbolRecord
                                    {
                                        FileId = fileId,
                                        Kind = "property",
                                        Name = propertyName,
                                        Line = lineIndex + 1,
                                        StartLine = lineIndex + 1,
                                        StartColumn = scanColumn + shorthandMatch.Index,
                                        EndLine = lineIndex + 1,
                                        Signature = rawLines[lineIndex].Trim(),
                                        ContainerKind = "object",
                                        ContainerName = target.ContainerName,
                                        Visibility = "export",
                                    },
                                    rawLines[lineIndex]);
                            }

                            scanColumn += shorthandMatch.Length;
                            continue;
                        }
                    }

                    switch (ch)
                    {
                        case '{':
                            braceDepth++;
                            break;
                        case '}':
                            if (braceDepth > 0)
                                braceDepth--;
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
                    }

                    scanColumn++;
                }
            }
        }
    }

    private static bool StartsJavaScriptTypeScriptFunctionAssignmentValue(string rhs)
    {
        rhs = rhs.TrimStart();
        while (rhs.Length > 0)
        {
            if (IsJavaScriptTypeScriptKeywordAt(rhs, 0, "function")
                || StartsJavaScriptTypeScriptAsyncFunctionAssignmentValue(rhs)
                || StartsJavaScriptTypeScriptGenericArrowAssignmentValue(rhs)
                || JavaScriptTypeScriptArrowAssignmentValueRegex.IsMatch(rhs))
            {
                return true;
            }

            if (rhs[0] != '(')
                return false;

            rhs = rhs[1..].TrimStart();
        }

        return false;
    }

    private static bool StartsJavaScriptTypeScriptClassAssignmentValue(string rhs)
    {
        rhs = rhs.TrimStart();
        while (rhs.Length > 0)
        {
            if (IsJavaScriptTypeScriptKeywordAt(rhs, 0, "class"))
                return true;

            if (rhs[0] != '(')
                return false;

            rhs = rhs[1..].TrimStart();
        }

        return false;
    }

    private static bool StartsJavaScriptTypeScriptAsyncFunctionAssignmentValue(string rhs)
    {
        if (!IsJavaScriptTypeScriptKeywordAt(rhs, 0, "async"))
            return false;

        var asyncRemainder = rhs["async".Length..].TrimStart();
        return IsJavaScriptTypeScriptKeywordAt(asyncRemainder, 0, "function");
    }

    private static bool StartsJavaScriptTypeScriptPotentialGenericArrowAssignmentValue(string rhs)
    {
        rhs = rhs.TrimStart();
        if (IsJavaScriptTypeScriptKeywordAt(rhs, 0, "async"))
            rhs = rhs["async".Length..].TrimStart();

        return rhs.Length > 0 && rhs[0] == '<';
    }

    private static string CollectJavaScriptTypeScriptAssignedRhsHeader(string[] sanitizedLines, int startLineIndex, int startColumn)
    {
        var builder = new System.Text.StringBuilder();
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var genericDepth = 0;
        var sawGenericStart = false;

        for (int lineIndex = startLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex
                ? Math.Max(0, startColumn)
                : 0;
            if (column >= sanitizedLine.Length)
                continue;

            if (builder.Length > 0)
                builder.Append(' ');

            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                builder.Append(ch);

                if (!sawGenericStart)
                {
                    if (char.IsWhiteSpace(ch))
                        continue;

                    if (ch == '<')
                    {
                        sawGenericStart = true;
                        genericDepth = 1;
                    }

                    continue;
                }

                switch (ch)
                {
                    case '<':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            genericDepth++;
                        break;
                    case '>':
                        if (parenDepth == 0
                            && bracketDepth == 0
                            && braceDepth == 0
                            && genericDepth > 0
                            && (column == 0 || sanitizedLine[column - 1] != '='))
                        {
                            genericDepth--;
                        }
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
                        if (genericDepth == 0 && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return builder.ToString().Trim();

                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        break;
                    case '=':
                        if (column + 1 < sanitizedLine.Length
                            && sanitizedLine[column + 1] == '>'
                            && genericDepth == 0
                            && parenDepth == 0
                            && bracketDepth == 0
                            && braceDepth == 0)
                        {
                            builder.Append('>');
                            column++;
                            return builder.ToString().Trim();
                        }
                        break;
                }
            }

            if (sawGenericStart
                && genericDepth == 0
                && parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static bool StartsJavaScriptTypeScriptGenericArrowAssignmentValue(string rhs)
    {
        rhs = rhs.TrimStart();
        if (IsJavaScriptTypeScriptKeywordAt(rhs, 0, "async"))
            rhs = rhs["async".Length..].TrimStart();

        if (rhs.Length == 0 || rhs[0] != '<')
            return false;

        var genericEnd = FindJavaScriptTypeScriptBalancedGenericListEnd(rhs, 0);
        if (genericEnd < 0)
            return false;

        var remainder = rhs[(genericEnd + 1)..].TrimStart();
        if (remainder.Length == 0)
            return false;

        if (remainder[0] == '(')
        {
            var parameterListEnd = FindJavaScriptTypeScriptBalancedDelimiterEnd(remainder, 0, '(', ')');
            if (parameterListEnd < 0)
                return false;

            remainder = remainder[(parameterListEnd + 1)..].TrimStart();
        }
        else
        {
            var parameterNameLength = ReadJavaScriptTypeScriptIdentifierLength(remainder, 0);
            if (parameterNameLength <= 0)
                return false;

            remainder = remainder[parameterNameLength..].TrimStart();
        }

        return remainder.StartsWith("=>", StringComparison.Ordinal);
    }

    private static bool TryCollectJavaScriptTypeScriptAssignedRhs(
        string[] rawLines,
        string[] sanitizedLines,
        int assignmentLineIndex,
        int assignmentStartColumn,
        int sameLineRhsColumn,
        string lang,
        out string rhs,
        out int rhsStartLineIndex,
        out int rhsStartColumn,
        out int rhsEndLineIndex,
        out int rhsEndColumn,
        out string signature)
    {
        rhs = string.Empty;
        rhsStartLineIndex = assignmentLineIndex;
        rhsStartColumn = sameLineRhsColumn;
        rhsEndLineIndex = assignmentLineIndex;
        rhsEndColumn = -1;
        signature = string.Empty;

        var rhsBuilder = new System.Text.StringBuilder();
        var signatureBuilder = new System.Text.StringBuilder();
        var pendingWrapperParenClose = false;

        for (int lineIndex = assignmentLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var column = lineIndex == assignmentLineIndex
                ? Math.Max(0, sameLineRhsColumn)
                : 0;

            if (!TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(sanitizedLines, ref lineIndex, ref column))
                continue;

            var sanitizedLine = sanitizedLines[lineIndex];
            while (sanitizedLines[lineIndex][column] == '('
                && HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(sanitizedLines[lineIndex], column))
            {
                column++;
                pendingWrapperParenClose = true;
                if (!TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(sanitizedLines, ref lineIndex, ref column))
                    return false;
            }

            if (pendingWrapperParenClose && column < sanitizedLine.Length && sanitizedLine[column] == ')')
            {
                column++;
                pendingWrapperParenClose = false;
            }

            var statementEndColumn = FindJavaScriptTypeScriptSameLineStatementEndColumn(sanitizedLine, column, lang);
            var sliceEndExclusive = statementEndColumn >= column
                ? statementEndColumn + 1
                : sanitizedLine.Length;

            var rhsStartSliceColumn = Math.Min(column, sanitizedLine.Length);
            var statementSliceEndColumn = Math.Min(sliceEndExclusive, sanitizedLine.Length);
            var rhsSlice = rhsStartSliceColumn < statementSliceEndColumn
                ? sanitizedLine[rhsStartSliceColumn..statementSliceEndColumn].TrimEnd()
                : string.Empty;
            if (rhsSlice.Length > 0)
            {
                if (rhsBuilder.Length == 0)
                {
                    rhsStartLineIndex = lineIndex;
                    rhsStartColumn = rhsStartSliceColumn;
                }

                if (rhsBuilder.Length > 0)
                    rhsBuilder.Append(' ');
                rhsBuilder.Append(rhsSlice);
            }

            var signatureSlice = lineIndex == assignmentLineIndex
                ? rawLines[lineIndex][Math.Min(assignmentStartColumn, rawLines[lineIndex].Length)..Math.Min(rawLines[lineIndex].Length, statementSliceEndColumn)].Trim()
                : rawLines[lineIndex].Trim();
            if (signatureSlice.Length > 0)
            {
                if (signatureBuilder.Length > 0)
                    signatureBuilder.Append(' ');
                signatureBuilder.Append(signatureSlice);
            }

            if (statementEndColumn >= column)
            {
                rhsEndLineIndex = lineIndex;
                rhsEndColumn = statementEndColumn;
                rhs = rhsBuilder.ToString().Trim();
                signature = signatureBuilder.ToString().Trim();
                return true;
            }
        }

        if (rhsBuilder.Length > 0)
        {
            rhs = rhsBuilder.ToString().Trim();
            signature = signatureBuilder.ToString().Trim();
            rhsEndLineIndex = Math.Max(assignmentLineIndex, sanitizedLines.Length - 1);
            rhsEndColumn = sanitizedLines[rhsEndLineIndex].Length - 1;
            return true;
        }

        rhs = string.Empty;
        signature = string.Empty;
        return false;
    }

    private static bool TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(string[] sanitizedLines, ref int lineIndex, ref int column)
    {
        while (lineIndex < sanitizedLines.Length)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            while (column < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[column]))
                column++;

            if (column < sanitizedLine.Length)
                return true;

            lineIndex++;
            column = 0;
        }

        return false;
    }

    private static bool TryFindJavaScriptTypeScriptAssignedRhsStart(
        string[] sanitizedLines,
        int assignmentLineIndex,
        int sameLineRhsColumn,
        out int startLineIndex,
        out int startColumn)
    {
        for (int lineIndex = assignmentLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == assignmentLineIndex
                ? Math.Max(0, sameLineRhsColumn)
                : 0;

            while (column < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[column]))
                column++;

            if (column >= sanitizedLine.Length)
                continue;

            if (sanitizedLine[column] == '('
                && HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(sanitizedLine, column))
            {
                continue;
            }

            if (sanitizedLine[column] == ')')
            {
                var remainder = sanitizedLine[column..].Trim();
                if (remainder.Length == 0 || remainder == ")" || remainder == ");")
                    continue;
            }

            startLineIndex = lineIndex;
            startColumn = column;
            return true;
        }

        startLineIndex = assignmentLineIndex;
        startColumn = sameLineRhsColumn;
        return false;
    }

    private static bool TryFindJavaScriptTypeScriptAssignedFunctionBodyOpenBrace(
        string[] rawLines,
        int startLineIndex,
        int startColumn,
        string? lang,
        out int openBraceLineIndex,
        out int openBraceColumn)
    {
        openBraceLineIndex = -1;
        openBraceColumn = -1;

        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var awaitingFunctionBody = false;
        var awaitingArrowBody = false;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        var lexState = new JavaScriptLexState();

        for (int lineIndex = startLineIndex; lineIndex < rawLines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(rawLines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            var column = lineIndex == startLineIndex
                ? Math.Max(0, startColumn)
                : 0;

            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                var wasFunctionHeaderActive = functionHeaderState.Active;

                if (!functionHeaderState.Active && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    var tokenEnd = column + 1;
                    while (tokenEnd < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenEnd]))
                        tokenEnd++;

                    if (sanitizedLine[tokenStart..tokenEnd] == "function")
                    {
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        column = tokenEnd - 1;
                        continue;
                    }
                }

                var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                    ref functionHeaderState,
                    sanitizedLine,
                    column,
                    lang ?? "javascript",
                    out var functionHeaderAdvanceColumns);
                if (wasFunctionHeaderActive && !functionHeaderState.Active)
                    awaitingFunctionBody = true;

                if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                {
                    column += functionHeaderAdvanceColumns;
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                    continue;

                if (awaitingFunctionBody)
                {
                    if (ch == '{')
                    {
                        openBraceLineIndex = lineIndex;
                        openBraceColumn = column;
                        return true;
                    }

                    return false;
                }

                if (awaitingArrowBody)
                {
                    if (ch == '{')
                    {
                        openBraceLineIndex = lineIndex;
                        openBraceColumn = column;
                        return true;
                    }

                    return false;
                }

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

                if (lang == "typescript" && ch == '<' && parenDepth == 0 && bracketDepth == 0)
                {
                    angleDepth++;
                    continue;
                }

                if (ch == '>' && angleDepth > 0 && (column == 0 || sanitizedLine[column - 1] != '='))
                {
                    angleDepth--;
                    continue;
                }

                if (ch == '='
                    && column + 1 < sanitizedLine.Length
                    && sanitizedLine[column + 1] == '>'
                    && parenDepth == 0
                    && bracketDepth == 0
                    && angleDepth == 0)
                {
                    awaitingArrowBody = true;
                    column++;
                }
            }
        }

        return false;
    }

    private static bool HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(string sanitizedLine, int startColumn)
    {
        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (char.IsWhiteSpace(ch) || ch == '(')
                continue;

            return false;
        }

        return true;
    }

    private static bool TrySkipJavaScriptTypeScriptNonIdentifierObjectLiteralKey(string sanitizedLine, ref int index)
    {
        var probe = index;
        if (TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref probe, out _)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref probe, out _))
        {
            while (probe < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[probe]))
                probe++;

            if (probe >= sanitizedLine.Length || sanitizedLine[probe] != ':')
                return false;

            index = probe + 1;
            return true;
        }

        if (probe >= sanitizedLine.Length || sanitizedLine[probe] != '[')
            return false;

        var bracketDepth = 1;
        probe++;
        while (probe < sanitizedLine.Length && bracketDepth > 0)
        {
            if (sanitizedLine[probe] == '[')
            {
                bracketDepth++;
            }
            else if (sanitizedLine[probe] == ']')
            {
                bracketDepth--;
            }

            probe++;
        }

        if (bracketDepth != 0)
            return false;

        while (probe < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[probe]))
            probe++;

        if (probe >= sanitizedLine.Length || sanitizedLine[probe] != ':')
            return false;

        index = probe + 1;
        return true;
    }

    private static string TrimJavaScriptTypeScriptQuotedModuleName(string moduleName)
    {
        if (moduleName.Length >= 2
            && moduleName[0] == moduleName[^1]
            && (moduleName[0] == '\'' || moduleName[0] == '"'))
        {
            return moduleName[1..^1];
        }

        return moduleName;
    }

    private static bool TryExtractJavaScriptTypeScriptReExportModuleName(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int endLineIndex,
        int startColumn,
        bool waitForClosedSpecifierList,
        out string moduleName)
    {
        moduleName = string.Empty;
        var braceDepth = 0;
        var sawOpeningBrace = !waitForClosedSpecifierList;

        for (int lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex ? Math.Max(0, startColumn) : 0;
            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (waitForClosedSpecifierList)
                {
                    if (ch == '{')
                    {
                        braceDepth++;
                        sawOpeningBrace = true;
                        continue;
                    }

                    if (!sawOpeningBrace)
                        continue;

                    if (ch == '}' && braceDepth > 0)
                    {
                        braceDepth--;
                        continue;
                    }

                    if (braceDepth > 0)
                        continue;
                }

                if (!IsJavaScriptTypeScriptKeywordAt(sanitizedLine, column, "from"))
                    continue;

                if (!TryFindJavaScriptTypeScriptReExportModuleQuote(rawLines, sanitizedLines, lineIndex, endLineIndex, column + "from".Length, out var quoteLineIndex, out var quoteColumn))
                    return false;

                var rawLine = rawLines[quoteLineIndex];
                var quoteChar = rawLine[quoteColumn];
                var closeQuoteColumn = rawLine.IndexOf(quoteChar, quoteColumn + 1);
                if (closeQuoteColumn <= quoteColumn)
                    return false;

                moduleName = TrimJavaScriptTypeScriptQuotedModuleName(rawLine[quoteColumn..(closeQuoteColumn + 1)]);
                return moduleName.Length > 0;
            }
        }

        return false;
    }

    private static bool TryFindJavaScriptTypeScriptReExportModuleQuote(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int endLineIndex,
        int startColumn,
        out int quoteLineIndex,
        out int quoteColumn)
    {
        quoteLineIndex = -1;
        quoteColumn = -1;

        for (int lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex ? startColumn : 0;
            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                    continue;

                if (ch is '\'' or '"')
                {
                    quoteLineIndex = lineIndex;
                    quoteColumn = column;
                    return column < rawLines[lineIndex].Length;
                }

                return false;
            }
        }

        return false;
    }

    private static bool IsJavaScriptTypeScriptKeywordAt(string text, int index, string keyword)
    {
        if (index < 0
            || index + keyword.Length > text.Length
            || !text.AsSpan(index, keyword.Length).SequenceEqual(keyword.AsSpan()))
        {
            return false;
        }

        var before = index > 0 ? text[index - 1] : '\0';
        if (char.IsLetterOrDigit(before) || before is '_' or '$')
            return false;

        var afterIndex = index + keyword.Length;
        if (afterIndex >= text.Length)
            return true;

        var after = text[afterIndex];
        return !(char.IsLetterOrDigit(after) || after is '_' or '$');
    }

    private static int FindJavaScriptTypeScriptKeywordIndex(string text, string keyword)
    {
        for (int index = 0; index <= text.Length - keyword.Length; index++)
        {
            if (IsJavaScriptTypeScriptKeywordAt(text, index, keyword))
                return index;
        }

        return -1;
    }

    private static bool ContainsJavaScriptTypeScriptKeyword(string text, string keyword)
    {
        return FindJavaScriptTypeScriptKeywordIndex(text, keyword) >= 0;
    }

    private static bool HasPendingJavaScriptTypeScriptImportAttributes(string clause)
    {
        var withIndex = FindJavaScriptTypeScriptKeywordIndex(clause, "with");
        var assertIndex = FindJavaScriptTypeScriptKeywordIndex(clause, "assert");
        var attributeIndex = withIndex >= 0 && assertIndex >= 0
            ? Math.Min(withIndex, assertIndex)
            : Math.Max(withIndex, assertIndex);
        if (attributeIndex < 0)
            return false;

        var braceDepth = 0;
        var sawOpeningBrace = false;
        for (int index = attributeIndex; index < clause.Length; index++)
        {
            var ch = clause[index];
            if (ch == '{')
            {
                braceDepth++;
                sawOpeningBrace = true;
            }
            else if (ch == '}' && braceDepth > 0)
            {
                braceDepth--;
            }
        }

        return !sawOpeningBrace || braceDepth > 0;
    }

    private static string SkipJavaScriptTypeScriptTypeOnlyExportModifier(string exportRemainder)
    {
        if (IsJavaScriptTypeScriptKeywordAt(exportRemainder, 0, "type"))
            return exportRemainder["type".Length..].TrimStart();

        return exportRemainder;
    }

    private static int FindJavaScriptTypeScriptBalancedGenericListEnd(string text, int startIndex)
    {
        if (startIndex < 0
            || startIndex >= text.Length
            || text[startIndex] != '<')
        {
            return -1;
        }

        var depth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (int index = startIndex; index < text.Length; index++)
        {
            var ch = text[index];
            switch (ch)
            {
                case '<':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        depth++;
                    break;
                case '>':
                    if (parenDepth == 0
                        && bracketDepth == 0
                        && braceDepth == 0
                        && depth > 0
                        && (index == 0 || text[index - 1] != '='))
                    {
                        depth--;
                        if (depth == 0)
                            return index;
                    }
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
            }
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptBalancedDelimiterEnd(string text, int startIndex, char openChar, char closeChar)
    {
        if (startIndex < 0
            || startIndex >= text.Length
            || text[startIndex] != openChar)
        {
            return -1;
        }

        var depth = 0;
        for (int index = startIndex; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == openChar)
            {
                depth++;
            }
            else if (ch == closeChar)
            {
                depth--;
                if (depth == 0)
                    return index;
            }
        }

        return -1;
    }

    private static int ReadJavaScriptTypeScriptIdentifierLength(string text, int startIndex)
    {
        if (startIndex < 0 || startIndex >= text.Length)
            return 0;

        var first = text[startIndex];
        if (!(char.IsLetter(first) || first is '_' or '$'))
            return 0;

        var index = startIndex + 1;
        while (index < text.Length)
        {
            var ch = text[index];
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '$'))
                break;

            index++;
        }

        return index - startIndex;
    }

    private static IEnumerable<string> ParseJavaScriptTypeScriptReExportedNames(string specifierList)
    {
        foreach (var rawSpecifier in specifierList.Split(','))
        {
            var specifier = rawSpecifier.Trim();
            if (specifier.Length == 0)
                continue;

            if (specifier.StartsWith("type ", StringComparison.Ordinal))
                specifier = specifier["type ".Length..].TrimStart();

            var asIndex = specifier.LastIndexOf(" as ", StringComparison.Ordinal);
            var exportedName = asIndex >= 0
                ? specifier[(asIndex + " as ".Length)..].Trim()
                : specifier;
            if (exportedName.Length == 0)
                continue;

            yield return exportedName;
        }
    }

    // Scans forward from (`startLineIndex`, `startColumn`) through the lex-sanitized source for
    // the first `{`, hopping across lines when only whitespace (including newlines) remains. The
    // passed `sanitizedStartLine` is the already-sanitized version of lines[startLineIndex] and
    // `lineEndState` is the lexer state AFTER that line. Any non-whitespace, non-`{` character
    // aborts the scan (returns false) so we don't misclassify arbitrary RHS expressions as object
    // literals. Strings / comments stay masked because we drive the scan through LexJavaScriptLine.
    // (`startLineIndex`, `startColumn`) から lex sanitized のソースを前方に走査し、最初の `{` を探す。
    // 空白 (改行を含む) だけなら行を跨いで続行する。`sanitizedStartLine` は lines[startLineIndex] の
    // sanitized 版で、`lineEndState` はそのライン終了時の lexer state。`{` 以外の非空白文字が現れた時点で
    // 走査を打ち切る (false を返す) ので、オブジェクトリテラルでない右辺を誤って拾わない。
    // LexJavaScriptLine を介するため、文字列・コメントは常にマスクされた状態で判定できる。
    private static bool TryFindJavaScriptTypeScriptObjectLiteralOpenBrace(
        string[] lines,
        int startLineIndex,
        int startColumn,
        string sanitizedStartLine,
        JavaScriptLexState lineEndState,
        out int openBraceLineIndex,
        out int openBraceColumn)
    {
        openBraceLineIndex = -1;
        openBraceColumn = -1;

        for (int c = Math.Max(0, startColumn); c < sanitizedStartLine.Length; c++)
        {
            var ch = sanitizedStartLine[c];
            if (char.IsWhiteSpace(ch))
                continue;
            if (ch == '{')
            {
                openBraceLineIndex = startLineIndex;
                openBraceColumn = c;
                return true;
            }
            return false;
        }

        var lexState = lineEndState;
        for (int li = startLineIndex + 1; li < lines.Length; li++)
        {
            var lexed = LexJavaScriptLine(lines[li], lexState);
            lexState = lexed.EndState;
            var nextSan = lexed.SanitizedLine;
            for (int c = 0; c < nextSan.Length; c++)
            {
                var ch = nextSan[c];
                if (char.IsWhiteSpace(ch))
                    continue;
                if (ch == '{')
                {
                    openBraceLineIndex = li;
                    openBraceColumn = c;
                    return true;
                }
                return false;
            }
        }

        return false;
    }

    private static List<JavaScriptClassScanTarget> GetJavaScriptTypeScriptExistingClassScanTargets(string lang, string[] lines, List<SymbolRecord> symbols)
    {
        return symbols
            .Where(s => s.Kind is "class" or "interface" && s.BodyStartLine != null && s.BodyEndLine != null)
            .OrderBy(s => s.StartLine)
            .ThenByDescending(s => s.EndLine)
            .Select(s => CreateJavaScriptClassScanTarget(
                lines,
                lang,
                s.StartLine - 1,
                FindJavaScriptTypeScriptSymbolStartColumn(lines[s.StartLine - 1], s.Signature),
                s.BodyStartLine,
                s.BodyEndLine,
                s.Kind,
                s.Name))
            .ToList();
    }

    private static List<JavaScriptClassScanTarget> CollectJavaScriptTypeScriptSyntheticClassScanTargets(long fileId, string lang, string[] lines, List<SymbolRecord> symbols, JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        var targets = new List<JavaScriptClassScanTarget>();
        var lexState = new JavaScriptLexState();
        for (int i = 0; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var lineOffset = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (lineOffset >= 0 && lineOffset < sanitizedLine.Length)
            {
                TryAddJavaScriptTypeScriptSyntheticClassTarget(fileId, lang, lines, symbols, targets, i, lineOffset, sanitizedLine, privateScopeColumns);
                lineOffset = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, lineOffset + 1);
            }
        }

        return targets
            .OrderBy(t => t.StartIndex)
            .ThenByDescending(t => t.ScanEndExclusive)
            .ToList();
    }

    private static bool IsInsideJavaScriptTypeScriptPrivateScope(Stack<JavaScriptScopeKind> scopeStack)
    {
        return scopeStack.Any(scopeKind => scopeKind is JavaScriptScopeKind.Function or JavaScriptScopeKind.StaticBlock);
    }

    private static JavaScriptScopePrivacyFlags GetJavaScriptTypeScriptPrivacyFlags(Stack<JavaScriptScopeKind> scopeStack, bool arrowExpressionActive)
    {
        var flags = JavaScriptScopePrivacyFlags.None;
        if (arrowExpressionActive || IsInsideJavaScriptTypeScriptPrivateScope(scopeStack))
            flags |= JavaScriptScopePrivacyFlags.FunctionLike;
        if (scopeStack.Any(scopeKind => scopeKind == JavaScriptScopeKind.Block))
            flags |= JavaScriptScopePrivacyFlags.Block;
        if (scopeStack.Any(scopeKind => scopeKind == JavaScriptScopeKind.Namespace))
            flags |= JavaScriptScopePrivacyFlags.Namespace;

        return flags;
    }

    private static bool IsInsideJavaScriptTypeScriptMethodContainer(Stack<JavaScriptScopeKind> scopeStack)
    {
        return scopeStack.Count > 0 && scopeStack.Peek() is JavaScriptScopeKind.Class or JavaScriptScopeKind.Object;
    }

    private static void BeginJavaScriptTypeScriptFunctionHeader(ref JavaScriptTypeScriptFunctionHeaderState state)
    {
        state = new JavaScriptTypeScriptFunctionHeaderState
        {
            Active = true,
        };
    }

    private static JavaScriptTypeScriptFunctionHeaderConsumeResult ConsumeJavaScriptTypeScriptFunctionHeaderChar(
        ref JavaScriptTypeScriptFunctionHeaderState state,
        string sanitizedLine,
        int column,
        string lang,
        out int advanceColumns)
    {
        advanceColumns = 0;
        if (!state.Active)
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.NotActive;

        var ch = sanitizedLine[column];
        if (char.IsWhiteSpace(ch))
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;

        if (state.InReturnType)
        {
            if (ch == ';'
                && state.ReturnParenDepth == 0
                && state.ReturnBracketDepth == 0
                && state.ReturnAngleDepth == 0
                && state.ReturnBraceDepth == 0)
            {
                state = default;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '(')
            {
                state.ReturnParenDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "(";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == ')' && state.ReturnParenDepth > 0)
            {
                state.ReturnParenDepth--;
                state.PreviousReturnToken = ")";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '[')
            {
                state.ReturnBracketDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "[";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == ']' && state.ReturnBracketDepth > 0)
            {
                state.ReturnBracketDepth--;
                state.PreviousReturnToken = "]";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '<')
            {
                state.ReturnAngleDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "<";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '>' && state.ReturnAngleDepth > 0)
            {
                state.ReturnAngleDepth--;
                state.PreviousReturnToken = ">";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '{')
            {
                if (state.ReturnParenDepth == 0
                    && state.ReturnBracketDepth == 0
                    && state.ReturnAngleDepth == 0
                    && state.ReturnBraceDepth == 0)
                {
                    if (CanStartJavaScriptTypeScriptReturnTypeObjectLiteral(state.PreviousReturnToken))
                    {
                        state.ReturnBraceDepth++;
                        state.ReturnSawToken = true;
                        state.PreviousReturnToken = "{";
                        return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
                    }

                    if (state.ReturnSawToken)
                    {
                        state = default;
                        return JavaScriptTypeScriptFunctionHeaderConsumeResult.BodyStart;
                    }
                }

                state.ReturnBraceDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "{";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '}' && state.ReturnBraceDepth > 0)
            {
                state.ReturnBraceDepth--;
                state.PreviousReturnToken = "}";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch is '?' or ':' or '|' or '&' or ',')
            {
                state.ReturnSawToken = true;
                state.PreviousReturnToken = ch.ToString();
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '=' && column + 1 < sanitizedLine.Length && sanitizedLine[column + 1] == '>')
            {
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "=>";
                advanceColumns = 1;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            var returnTypeIndex = column;
            if (TrySkipTypeScriptTypeToken(sanitizedLine, ref returnTypeIndex, out var returnTypeToken))
            {
                state.ReturnSawToken = true;
                state.PreviousReturnToken = returnTypeToken;
                advanceColumns = returnTypeIndex - column - 1;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            state.ReturnSawToken = true;
            state.PreviousReturnToken = ch.ToString();
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (lang == "typescript" && state.SawParameterList && ch == ':')
        {
            state.InReturnType = true;
            state.ReturnParenDepth = 0;
            state.ReturnBracketDepth = 0;
            state.ReturnAngleDepth = 0;
            state.ReturnBraceDepth = 0;
            state.ReturnSawToken = false;
            state.PreviousReturnToken = ":";
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '(')
        {
            state.ParenDepth++;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == ')' && state.ParenDepth > 0)
        {
            state.ParenDepth--;
            if (state.ParenDepth == 0 && state.BracketDepth == 0 && state.BraceDepth == 0)
                state.SawParameterList = true;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '[' && (state.ParenDepth > 0 || state.BracketDepth > 0 || state.BraceDepth > 0 || !state.SawParameterList))
        {
            state.BracketDepth++;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == ']' && state.BracketDepth > 0)
        {
            state.BracketDepth--;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '{')
        {
            if (state.SawParameterList && state.ParenDepth == 0 && state.BracketDepth == 0 && state.BraceDepth == 0)
            {
                state = default;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.BodyStart;
            }

            state.BraceDepth++;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '}' && state.BraceDepth > 0)
        {
            state.BraceDepth--;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == ';')
        {
            state = default;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        var tokenIndex = column;
        if (TrySkipTypeScriptTypeToken(sanitizedLine, ref tokenIndex, out _))
        {
            advanceColumns = tokenIndex - column - 1;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
    }

    private static JavaScriptScopePrivacyFlags[][] BuildJavaScriptTypeScriptPrivateScopeColumns(string[] lines, string lang)
    {
        var privateColumns = new JavaScriptScopePrivacyFlags[lines.Length][];
        var lexState = new JavaScriptLexState();
        var scopeStack = new Stack<JavaScriptScopeKind>();
        var pendingFunctionScope = false;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        var pendingStaticBlockScope = false;
        var pendingClassScope = false;
        var pendingNamespaceScope = false;
        var pendingConciseMethodScope = false;
        var pendingConciseMethodReturnType = false;
        var conciseMethodReturnParenDepth = 0;
        var conciseMethodReturnBracketDepth = 0;
        var conciseMethodReturnAngleDepth = 0;
        var conciseMethodReturnBraceDepth = 0;
        var conciseMethodReturnSawToken = false;
        string? previousConciseMethodReturnToken = null;
        var pendingArrowBody = false;
        var arrowExpressionActive = false;
        var arrowExpressionParenDepth = 0;
        var arrowExpressionBracketDepth = 0;
        var arrowExpressionBraceDepth = 0;
        var previousTokenKind = JavaScriptPrevTokenKind.None;
        string? previousIdentifier = null;
        char previousSignificantChar = '\0';

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var linePrivateColumns = new JavaScriptScopePrivacyFlags[sanitizedLine.Length];

            if (arrowExpressionActive
                && arrowExpressionBraceDepth == 0
                && arrowExpressionParenDepth == 0
                && arrowExpressionBracketDepth == 0
                && !StartsJavaScriptTypeScriptExpressionContinuation(sanitizedLine))
            {
                arrowExpressionActive = false;
            }

            for (int column = 0; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);

                if (char.IsWhiteSpace(ch))
                    continue;

                if (pendingArrowBody)
                {
                    if (ch == '{')
                    {
                        scopeStack.Push(JavaScriptScopeKind.Function);
                        linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                        pendingArrowBody = false;
                        continue;
                    }

                    arrowExpressionActive = true;
                    linePrivateColumns[column] = JavaScriptScopePrivacyFlags.FunctionLike;
                    pendingArrowBody = false;
                }

                if (pendingConciseMethodReturnType)
                {
                    if (ch == '(')
                    {
                        conciseMethodReturnParenDepth++;
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = "(";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == ')' && conciseMethodReturnParenDepth > 0)
                    {
                        conciseMethodReturnParenDepth--;
                        previousConciseMethodReturnToken = ")";
                        previousTokenKind = JavaScriptPrevTokenKind.CloseParen;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '[')
                    {
                        conciseMethodReturnBracketDepth++;
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = "[";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == ']' && conciseMethodReturnBracketDepth > 0)
                    {
                        conciseMethodReturnBracketDepth--;
                        previousConciseMethodReturnToken = "]";
                        previousTokenKind = JavaScriptPrevTokenKind.CloseBracket;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '<')
                    {
                        conciseMethodReturnAngleDepth++;
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = "<";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '>' && conciseMethodReturnAngleDepth > 0)
                    {
                        conciseMethodReturnAngleDepth--;
                        previousConciseMethodReturnToken = ">";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '{')
                    {
                        if (conciseMethodReturnParenDepth == 0
                            && conciseMethodReturnBracketDepth == 0
                            && conciseMethodReturnAngleDepth == 0
                            && conciseMethodReturnBraceDepth == 0)
                        {
                            if (CanStartJavaScriptTypeScriptReturnTypeObjectLiteral(previousConciseMethodReturnToken))
                            {
                                conciseMethodReturnBraceDepth++;
                                conciseMethodReturnSawToken = true;
                                previousConciseMethodReturnToken = "{";
                                previousTokenKind = JavaScriptPrevTokenKind.Other;
                                previousIdentifier = null;
                                previousSignificantChar = ch;
                                continue;
                            }

                            if (conciseMethodReturnSawToken)
                            {
                                linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                                scopeStack.Push(JavaScriptScopeKind.Function);
                                pendingConciseMethodScope = false;
                                pendingConciseMethodReturnType = false;
                                conciseMethodReturnParenDepth = 0;
                                conciseMethodReturnBracketDepth = 0;
                                conciseMethodReturnAngleDepth = 0;
                                conciseMethodReturnBraceDepth = 0;
                                conciseMethodReturnSawToken = false;
                                previousConciseMethodReturnToken = null;
                                previousTokenKind = JavaScriptPrevTokenKind.CloseBrace;
                                previousIdentifier = null;
                                previousSignificantChar = ch;
                                continue;
                            }
                        }

                        conciseMethodReturnBraceDepth++;
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = "{";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '}' && conciseMethodReturnBraceDepth > 0)
                    {
                        conciseMethodReturnBraceDepth--;
                        previousConciseMethodReturnToken = "}";
                        previousTokenKind = JavaScriptPrevTokenKind.CloseBrace;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '?' || ch == ':' || ch == '|' || ch == '&' || ch == ',')
                    {
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = ch.ToString();
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '=' && column + 1 < sanitizedLine.Length && sanitizedLine[column + 1] == '>')
                    {
                        linePrivateColumns[column + 1] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = "=>";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = '>';
                        column++;
                        continue;
                    }

                    if (IsJavaScriptTypeScriptIdentifierStart(ch))
                    {
                        var returnTokenStart = column;
                        column++;
                        while (column < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[column]))
                        {
                            linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                            column++;
                        }

                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = sanitizedLine[returnTokenStart..column];
                        previousTokenKind = JavaScriptPrevTokenKind.Identifier;
                        previousIdentifier = previousConciseMethodReturnToken;
                        previousSignificantChar = sanitizedLine[column - 1];
                        column--;
                        continue;
                    }

                    conciseMethodReturnSawToken = true;
                    previousConciseMethodReturnToken = ch.ToString();
                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (pendingFunctionScope)
                {
                    var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                        ref functionHeaderState,
                        sanitizedLine,
                        column,
                        lang,
                        out var functionHeaderAdvanceColumns);
                    if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                    {
                        previousTokenKind = ch switch
                        {
                            ')' => JavaScriptPrevTokenKind.CloseParen,
                            ']' => JavaScriptPrevTokenKind.CloseBracket,
                            _ => JavaScriptPrevTokenKind.Other,
                        };
                        previousIdentifier = null;
                        previousSignificantChar = sanitizedLine[Math.Min(column + functionHeaderAdvanceColumns, sanitizedLine.Length - 1)];
                        column += functionHeaderAdvanceColumns;
                        continue;
                    }
                }

                if (IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    column++;
                    while (column < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[column]))
                    {
                        linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);

                        column++;
                    }

                    var token = sanitizedLine[tokenStart..column];
                    if (token == "function")
                    {
                        pendingFunctionScope = true;
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        pendingStaticBlockScope = false;
                        pendingConciseMethodScope = false;
                        pendingConciseMethodReturnType = false;
                    }
                    else if (token == "static")
                    {
                        pendingStaticBlockScope = IsInsideJavaScriptTypeScriptMethodContainer(scopeStack);
                    }
                    else if (token == "class")
                    {
                        pendingClassScope = true;
                        pendingStaticBlockScope = false;
                        pendingConciseMethodScope = false;
                        pendingConciseMethodReturnType = false;
                    }
                    else if (lang == "typescript" && token is "namespace" or "module")
                    {
                        pendingNamespaceScope = true;
                        pendingStaticBlockScope = false;
                    }
                    else
                    {
                        pendingStaticBlockScope = false;
                    }

                    previousTokenKind = JavaScriptPrevTokenKind.Identifier;
                    previousIdentifier = token;
                    previousSignificantChar = sanitizedLine[column - 1];
                    column--;
                    continue;
                }

                if (ch == '=' && column + 1 < sanitizedLine.Length && sanitizedLine[column + 1] == '>')
                {
                    linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                    linePrivateColumns[column + 1] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                    pendingArrowBody = true;
                    pendingFunctionScope = false;
                    functionHeaderState = default;
                    pendingStaticBlockScope = false;
                    pendingClassScope = false;
                    pendingNamespaceScope = false;
                    pendingConciseMethodScope = false;
                    pendingConciseMethodReturnType = false;
                    conciseMethodReturnParenDepth = 0;
                    conciseMethodReturnBracketDepth = 0;
                    conciseMethodReturnAngleDepth = 0;
                    conciseMethodReturnBraceDepth = 0;
                    conciseMethodReturnSawToken = false;
                    previousConciseMethodReturnToken = null;
                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = '>';
                    column++;
                    continue;
                }

                if (ch == '(')
                {
                    if (arrowExpressionActive)
                        arrowExpressionParenDepth++;

                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == ')')
                {
                    if (arrowExpressionActive && arrowExpressionParenDepth > 0)
                        arrowExpressionParenDepth--;

                    if (IsInsideJavaScriptTypeScriptMethodContainer(scopeStack))
                    {
                        pendingConciseMethodScope = true;
                        pendingConciseMethodReturnType = false;
                        conciseMethodReturnParenDepth = 0;
                        conciseMethodReturnBracketDepth = 0;
                        conciseMethodReturnAngleDepth = 0;
                        conciseMethodReturnBraceDepth = 0;
                        conciseMethodReturnSawToken = false;
                        previousConciseMethodReturnToken = null;
                    }

                    previousTokenKind = JavaScriptPrevTokenKind.CloseParen;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == '[')
                {
                    if (arrowExpressionActive)
                        arrowExpressionBracketDepth++;

                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == ']')
                {
                    if (arrowExpressionActive && arrowExpressionBracketDepth > 0)
                        arrowExpressionBracketDepth--;

                    previousTokenKind = JavaScriptPrevTokenKind.CloseBracket;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == '{')
                {
                    var scopeKind = JavaScriptScopeKind.Other;
                    if (pendingFunctionScope)
                        scopeKind = JavaScriptScopeKind.Function;
                    else if (pendingConciseMethodScope)
                        scopeKind = JavaScriptScopeKind.Function;
                    else if (pendingStaticBlockScope)
                        scopeKind = JavaScriptScopeKind.StaticBlock;
                    else if (pendingClassScope)
                        scopeKind = JavaScriptScopeKind.Class;
                    else if (pendingNamespaceScope)
                        scopeKind = JavaScriptScopeKind.Namespace;
                    else if (CanStartJavaScriptTypeScriptObjectLiteral(previousTokenKind, previousIdentifier, previousSignificantChar))
                        scopeKind = JavaScriptScopeKind.Object;
                    else
                        scopeKind = JavaScriptScopeKind.Block;

                    linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);

                    scopeStack.Push(scopeKind);
                    if (arrowExpressionActive)
                        arrowExpressionBraceDepth++;

                    pendingFunctionScope = false;
                    functionHeaderState = default;
                    pendingStaticBlockScope = false;
                    pendingClassScope = false;
                    pendingNamespaceScope = false;
                    pendingConciseMethodScope = false;
                    pendingConciseMethodReturnType = false;
                    conciseMethodReturnParenDepth = 0;
                    conciseMethodReturnBracketDepth = 0;
                    conciseMethodReturnAngleDepth = 0;
                    conciseMethodReturnBraceDepth = 0;
                    conciseMethodReturnSawToken = false;
                    previousConciseMethodReturnToken = null;
                    previousTokenKind = JavaScriptPrevTokenKind.CloseBrace;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == '}')
                {
                    if (arrowExpressionActive)
                    {
                        linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                        if (arrowExpressionBraceDepth > 0)
                            arrowExpressionBraceDepth--;
                    }

                    if (scopeStack.Count > 0)
                        scopeStack.Pop();

                    pendingFunctionScope = false;
                    functionHeaderState = default;
                    pendingStaticBlockScope = false;
                    pendingClassScope = false;
                    pendingNamespaceScope = false;
                    pendingConciseMethodScope = false;
                    pendingConciseMethodReturnType = false;
                    conciseMethodReturnParenDepth = 0;
                    conciseMethodReturnBracketDepth = 0;
                    conciseMethodReturnAngleDepth = 0;
                    conciseMethodReturnBraceDepth = 0;
                    conciseMethodReturnSawToken = false;
                    previousConciseMethodReturnToken = null;
                    previousTokenKind = JavaScriptPrevTokenKind.CloseBrace;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch is ';' or ',')
                {
                    if (arrowExpressionActive
                        && arrowExpressionBraceDepth == 0
                        && arrowExpressionParenDepth == 0
                        && arrowExpressionBracketDepth == 0)
                    {
                        linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                        arrowExpressionActive = false;
                    }

                    pendingFunctionScope = false;
                    functionHeaderState = default;
                    pendingStaticBlockScope = false;
                    pendingClassScope = false;
                    pendingNamespaceScope = false;
                    pendingConciseMethodScope = false;
                    pendingConciseMethodReturnType = false;
                    conciseMethodReturnParenDepth = 0;
                    conciseMethodReturnBracketDepth = 0;
                    conciseMethodReturnAngleDepth = 0;
                    conciseMethodReturnBraceDepth = 0;
                    conciseMethodReturnSawToken = false;
                    previousConciseMethodReturnToken = null;
                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == ':')
                {
                    if (pendingConciseMethodScope && lang == "typescript")
                    {
                        pendingConciseMethodReturnType = true;
                        conciseMethodReturnParenDepth = 0;
                        conciseMethodReturnBracketDepth = 0;
                        conciseMethodReturnAngleDepth = 0;
                        conciseMethodReturnBraceDepth = 0;
                        conciseMethodReturnSawToken = false;
                        previousConciseMethodReturnToken = ":";
                    }

                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (pendingStaticBlockScope && ch != '{')
                    pendingStaticBlockScope = false;
                if (pendingNamespaceScope && ch is not '{' and not '.')
                    pendingNamespaceScope = false;

                previousTokenKind = JavaScriptPrevTokenKind.Other;
                previousIdentifier = null;
                previousSignificantChar = ch;
            }

            privateColumns[lineIndex] = linePrivateColumns;
        }

        return privateColumns;
    }

    private static bool CanStartJavaScriptTypeScriptObjectLiteral(
        JavaScriptPrevTokenKind previousTokenKind,
        string? previousIdentifier,
        char previousSignificantChar)
    {
        if (previousSignificantChar is '=' or '(' or '[' or ',' or ':' or '?' or '!')
            return true;

        if (previousIdentifier is "return" or "throw" or "case" or "else")
            return true;

        return previousTokenKind == JavaScriptPrevTokenKind.None;
    }

    private static bool StartsJavaScriptTypeScriptExpressionContinuation(string sanitizedLine)
    {
        var index = 0;
        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        if (index >= sanitizedLine.Length)
            return false;

        var remaining = sanitizedLine[index..];
        if (remaining.StartsWith(".", StringComparison.Ordinal)
            || remaining.StartsWith("?.", StringComparison.Ordinal)
            || remaining.StartsWith("[", StringComparison.Ordinal)
            || remaining.StartsWith("(", StringComparison.Ordinal)
            || remaining.StartsWith("`", StringComparison.Ordinal)
            || remaining.StartsWith("?.[", StringComparison.Ordinal))
            return true;

        if (Regex.IsMatch(remaining, @"^(?:\+\+|--|\+|-|\*|/|%|\*\*|&&|\|\||\?\?|\?|:|==|===|!=|!==|<=|>=|<|>|=>|&|\||\^|<<|>>|>>>|,)\b?"))
            return true;

        if (Regex.IsMatch(remaining, @"^(?:instanceof|in|as|satisfies)\b"))
            return true;

        return false;
    }

    private static bool IsAnonymousJavaScriptTypeScriptDefaultClassDeclaration(string[] lines, int startIndex, int startColumn)
    {
        if (!TryGetAnonymousJavaScriptTypeScriptDefaultClassToken(lines, startIndex, startColumn, "javascript", out var tokenLineIndex, out var tokenStartColumn))
            return false;

        startIndex = tokenLineIndex;
        startColumn = tokenStartColumn + "class".Length;
        if (!TryAdvanceJavaScriptTypeScriptClassHeaderContinuation(lines, startIndex, startColumn, "javascript", out startIndex, out startColumn))
            return false;

        var lexState = new JavaScriptLexState();
        for (int lineIndex = startIndex; lineIndex < lines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var column = lineIndex == startIndex ? startColumn : 0;

            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    column++;
                    while (column < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[column]))
                        column++;

                    var nextToken = sanitizedLine[tokenStart..column];
                    return nextToken is "extends" or "implements";
                }

                return ch == '{';
            }
        }

        return false;
    }

    private static bool TryGetAnonymousJavaScriptTypeScriptDefaultClassToken(
        string[] lines,
        int startIndex,
        int startColumn,
        string lang,
        out int classLineIndex,
        out int classStartColumn)
    {
        classLineIndex = -1;
        classStartColumn = -1;

        if (!TryGetJavaScriptTypeScriptNextToken(lines, startIndex, startColumn, skipWrappingParens: true, out classLineIndex, out classStartColumn, out var token))
            return false;

        if (lang == "typescript" && token == "abstract")
        {
            if (!TryGetJavaScriptTypeScriptNextToken(lines, classLineIndex, classStartColumn + token.Length, skipWrappingParens: false, out classLineIndex, out classStartColumn, out token))
                return false;
        }

        if (token != "class")
            return false;

        var inspectLineIndex = classLineIndex;
        var inspectStartColumn = classStartColumn + token.Length;
        if (!TryAdvanceJavaScriptTypeScriptClassHeaderContinuation(lines, inspectLineIndex, inspectStartColumn, lang, out inspectLineIndex, out inspectStartColumn))
            return false;

        var lexState = new JavaScriptLexState();
        for (int lineIndex = inspectLineIndex; lineIndex < lines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var column = lineIndex == inspectLineIndex ? inspectStartColumn : 0;

            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    column++;
                    while (column < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[column]))
                        column++;

                    var nextToken = sanitizedLine[tokenStart..column];
                    return nextToken is "extends" or "implements";
                }

                return ch == '{';
            }
        }

        return false;
    }

    private static bool TryAdvanceJavaScriptTypeScriptClassHeaderContinuation(
        string[] lines,
        int startIndex,
        int startColumn,
        string lang,
        out int nextLineIndex,
        out int nextColumn)
    {
        nextLineIndex = startIndex;
        nextColumn = startColumn;
        if (lang != "typescript")
            return true;

        var lexState = new JavaScriptLexState();
        var sawTypeParameterList = false;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (int lineIndex = startIndex; lineIndex < lines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var column = lineIndex == startIndex ? startColumn : 0;

            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (!sawTypeParameterList)
                {
                    if (ch != '<')
                    {
                        nextLineIndex = lineIndex;
                        nextColumn = column;
                        return true;
                    }

                    sawTypeParameterList = true;
                    angleDepth = 1;
                    column++;
                    continue;
                }

                if (ch == '(')
                {
                    parenDepth++;
                    column++;
                    continue;
                }

                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    column++;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                    column++;
                    continue;
                }

                if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    column++;
                    continue;
                }

                if (ch == '{')
                {
                    braceDepth++;
                    column++;
                    continue;
                }

                if (ch == '}' && braceDepth > 0)
                {
                    braceDepth--;
                    column++;
                    continue;
                }

                if (ch == '<')
                {
                    angleDepth++;
                    column++;
                    continue;
                }

                if (ch == '=' && column + 1 < sanitizedLine.Length && sanitizedLine[column + 1] == '>')
                {
                    column += 2;
                    continue;
                }

                if (ch == '>' && angleDepth > 0 && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                {
                    angleDepth--;
                    column++;
                    if (angleDepth == 0)
                    {
                        while (column < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[column]))
                            column++;

                        nextLineIndex = lineIndex;
                        nextColumn = column;
                        return true;
                    }

                    continue;
                }

                column++;
            }
        }

        return !sawTypeParameterList;
    }

    private static bool IsJavaScriptTypeScriptMatchInPrivateScope(
        JavaScriptScopePrivacyFlags[][] privateScopeColumns,
        int lineIndex,
        int startColumn,
        string matchLine,
        bool includeBlockScope)
    {
        if (lineIndex < 0 || lineIndex >= privateScopeColumns.Length)
            return false;

        var linePrivateColumns = privateScopeColumns[lineIndex];
        if (linePrivateColumns.Length == 0)
            return false;

        var column = Math.Max(0, startColumn);
        while (column < matchLine.Length && char.IsWhiteSpace(matchLine[column]))
            column++;

        if (column >= linePrivateColumns.Length)
            return false;

        var flags = linePrivateColumns[column];
        if ((flags & JavaScriptScopePrivacyFlags.FunctionLike) != 0)
            return true;

        return includeBlockScope && (flags & JavaScriptScopePrivacyFlags.Block) != 0;
    }

    private static bool IsJavaScriptTypeScriptMatchInNamespaceScope(
        JavaScriptScopePrivacyFlags[][] privateScopeColumns,
        int lineIndex,
        int startColumn,
        string matchLine)
    {
        if (lineIndex < 0 || lineIndex >= privateScopeColumns.Length)
            return false;

        var linePrivateColumns = privateScopeColumns[lineIndex];
        if (linePrivateColumns.Length == 0)
            return false;

        var column = Math.Max(0, startColumn);
        while (column < matchLine.Length && char.IsWhiteSpace(matchLine[column]))
            column++;

        if (column >= linePrivateColumns.Length)
            return false;

        return (linePrivateColumns[column] & JavaScriptScopePrivacyFlags.Namespace) != 0;
    }

    private static bool IsJavaScriptTypeScriptControlFlowHeader(string sanitizedLine, int startColumn)
    {
        var index = Math.Max(0, startColumn);
        if (index >= sanitizedLine.Length || !IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[index]))
            return false;

        var tokenStart = index;
        index++;
        while (index < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index]))
            index++;

        var token = sanitizedLine[tokenStart..index];
        if (!JavaScriptTypeScriptControlFlowHeaderKeywords.Contains(token))
            return false;

        if (index >= sanitizedLine.Length || !char.IsWhiteSpace(sanitizedLine[index]))
            return false;

        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        return index < sanitizedLine.Length && sanitizedLine[index] == '(';
    }

    private static void ExtractJavaScriptTypeScriptBareMethodsInTargets(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        List<JavaScriptClassScanTarget> classScanTargets)
    {
        foreach (var classScanTarget in classScanTargets)
            ExtractJavaScriptTypeScriptBareMethodsInClass(fileId, lang, lines, symbols, classScanTarget);
    }

    private static void TryAddJavaScriptTypeScriptSyntheticClassTarget(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        List<JavaScriptClassScanTarget> targets,
        int startIndex,
        int startColumn,
        string sanitizedLine,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        var lineRemainder = sanitizedLine[startColumn..];
        var anonymousDefaultMatch = JavaScriptTypeScriptAnonymousDefaultExportRegex.Match(lineRemainder);
        if (anonymousDefaultMatch.Success)
        {
            if (!TryGetAnonymousJavaScriptTypeScriptDefaultClassToken(
                lines,
                startIndex,
                startColumn + anonymousDefaultMatch.Index + anonymousDefaultMatch.Length,
                lang,
                out var classTokenLineIndex,
                out var classTokenStartColumn))
                return;

            if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, startIndex, startColumn + anonymousDefaultMatch.Index, sanitizedLine, includeBlockScope: false))
                return;

            if (TryGetGroup(anonymousDefaultMatch, "visibility") != "export"
                && IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, startIndex, startColumn + anonymousDefaultMatch.Index, sanitizedLine))
            {
                return;
            }

        AddJavaScriptTypeScriptSyntheticClassTarget(
            fileId,
            lang,
            lines,
            symbols,
            targets,
            startIndex,
            startColumn + anonymousDefaultMatch.Index,
            classTokenLineIndex,
            classTokenStartColumn,
            containerName: "default",
            visibility: TryGetGroup(anonymousDefaultMatch, "visibility"));
            return;
        }

        if (lang == "typescript")
        {
            var exportEqualsMatch = TypeScriptExportEqualsRegex.Match(lineRemainder);
            if (exportEqualsMatch.Success)
            {
                if (!IsJavaScriptTypeScriptClassExpressionDeclaration(lines, startIndex, startColumn + exportEqualsMatch.Index + exportEqualsMatch.Length))
                    return;

                if (!TryGetJavaScriptTypeScriptNextToken(
                    lines,
                    startIndex,
                    startColumn + exportEqualsMatch.Index + exportEqualsMatch.Length,
                    skipWrappingParens: true,
                    out var exportEqualsClassTokenLineIndex,
                    out var exportEqualsClassTokenStartColumn,
                    out _))
                {
                    return;
                }

                if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, startIndex, startColumn + exportEqualsMatch.Index, sanitizedLine, includeBlockScope: false))
                    return;

                if (IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, startIndex, startColumn + exportEqualsMatch.Index, sanitizedLine))
                    return;

                AddJavaScriptTypeScriptSyntheticClassTarget(
                    fileId,
                    lang,
                    lines,
                    symbols,
                    targets,
                    startIndex,
                    startColumn + exportEqualsMatch.Index,
                    exportEqualsClassTokenLineIndex,
                    exportEqualsClassTokenStartColumn,
                    containerName: "default",
                    visibility: "export");
                return;
            }
        }

        var classExpressionBindingMatch = JavaScriptTypeScriptClassExpressionBindingRegex.Match(lineRemainder);
        if (!classExpressionBindingMatch.Success)
            return;

        if (!IsJavaScriptTypeScriptClassExpressionDeclaration(lines, startIndex, startColumn + classExpressionBindingMatch.Index + classExpressionBindingMatch.Length))
            return;

        if (!TryGetJavaScriptTypeScriptNextToken(
            lines,
            startIndex,
            startColumn + classExpressionBindingMatch.Index + classExpressionBindingMatch.Length,
            skipWrappingParens: true,
            out var classExpressionTokenLineIndex,
            out var classExpressionTokenStartColumn,
            out _))
        {
            return;
        }

        var includeBlockScope = classExpressionBindingMatch.Groups["bindingKind"].Success
            && classExpressionBindingMatch.Groups["bindingKind"].Value is "const" or "let";
        if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, startIndex, startColumn + classExpressionBindingMatch.Index, sanitizedLine, includeBlockScope))
            return;

        if (TryGetGroup(classExpressionBindingMatch, "visibility") != "export"
            && IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, startIndex, startColumn + classExpressionBindingMatch.Index, sanitizedLine))
        {
            return;
        }

        var containerName = TryGetGroup(classExpressionBindingMatch, "alias")
            ?? TryGetGroup(classExpressionBindingMatch, "exportsAlias")
            ?? TryGetGroup(classExpressionBindingMatch, "moduleExportsAlias")
            ?? (classExpressionBindingMatch.Groups["moduleExports"].Success ? "default" : null)
            ?? "class";
        AddJavaScriptTypeScriptSyntheticClassTarget(
            fileId,
            lang,
            lines,
            symbols,
            targets,
            startIndex,
            startColumn + classExpressionBindingMatch.Index,
            classExpressionTokenLineIndex,
            classExpressionTokenStartColumn,
            containerName,
            TryGetGroup(classExpressionBindingMatch, "visibility"));
    }

    private static bool IsJavaScriptTypeScriptClassExpressionDeclaration(string[] lines, int startIndex, int startColumn)
    {
        return TryGetJavaScriptTypeScriptNextToken(lines, startIndex, startColumn, skipWrappingParens: true, out _, out _, out var token)
            && token == "class";
    }

    private static void AddJavaScriptTypeScriptSyntheticClassTarget(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        List<JavaScriptClassScanTarget> targets,
        int declarationStartIndex,
        int declarationStartColumn,
        int classTokenLineIndex,
        int classTokenStartColumn,
        string containerName,
        string? visibility)
    {
        var (endLine, bodyStartLine, bodyEndLine) = ResolveRange(lines, classTokenLineIndex, BodyStyle.Brace, lang, classTokenStartColumn);
        if (bodyStartLine == null || bodyEndLine == null)
            return;

        var existingClass = symbols.FirstOrDefault(s => s.Kind == "class" && s.Line == declarationStartIndex + 1 && s.Name == containerName);
        if (existingClass == null)
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = containerName,
                Line = declarationStartIndex + 1,
                StartLine = declarationStartIndex + 1,
                EndLine = Math.Max(declarationStartIndex + 1, endLine),
                BodyStartLine = bodyStartLine,
                BodyEndLine = bodyEndLine,
                Signature = BuildJavaScriptTypeScriptSyntheticClassSignature(lines, declarationStartIndex, declarationStartColumn, classTokenLineIndex, classTokenStartColumn, bodyStartLine, bodyEndLine, lang),
                Visibility = visibility,
            });
        }

        var candidate = CreateJavaScriptClassScanTarget(lines, lang, classTokenLineIndex, classTokenStartColumn, bodyStartLine, bodyEndLine, "class", containerName);
        if (!targets.Any(t => t.StartIndex == candidate.StartIndex
            && t.StartColumn == candidate.StartColumn
            && t.ScanStartIndex == candidate.ScanStartIndex
            && t.ScanEndExclusive == candidate.ScanEndExclusive
            && t.FirstLineScanOffset == candidate.FirstLineScanOffset
            && t.ContainerKind == candidate.ContainerKind
            && t.ContainerName == candidate.ContainerName))
        {
            targets.Add(candidate);
        }
    }

    private static string BuildJavaScriptTypeScriptSyntheticClassSignature(
        string[] lines,
        int declarationStartIndex,
        int declarationStartColumn,
        int classTokenLineIndex,
        int classTokenStartColumn,
        int? bodyStartLine,
        int? bodyEndLine,
        string lang)
    {
        var line = lines[declarationStartIndex];
        if (declarationStartColumn >= line.Length)
            return string.Empty;

        if (bodyEndLine == declarationStartIndex + 1)
        {
            var sameLineEndColumn = FindJavaScriptTypeScriptSameLineBraceEndColumn(line, classTokenStartColumn, lang);
            if (sameLineEndColumn >= declarationStartColumn)
                return line[declarationStartColumn..(sameLineEndColumn + 1)].Trim();
        }

        if (bodyStartLine == null)
            return line[declarationStartColumn..].Trim();

        var bodyStartIndex = bodyStartLine.Value - 1;
        var bodyOpenBraceColumn = FindJavaScriptBodyOpenBraceIndex(lines, classTokenLineIndex, bodyStartIndex, lang, classTokenStartColumn);
        if (bodyOpenBraceColumn < 0)
            return line[declarationStartColumn..].Trim();

        var signatureBuilder = new System.Text.StringBuilder();
        for (int lineIndex = declarationStartIndex; lineIndex <= bodyStartIndex; lineIndex++)
        {
            var sourceLine = lines[lineIndex];
            var startColumn = lineIndex == declarationStartIndex
                ? Math.Min(declarationStartColumn, sourceLine.Length)
                : 0;
            var endExclusive = lineIndex == bodyStartIndex
                ? Math.Min(bodyOpenBraceColumn + 1, sourceLine.Length)
                : sourceLine.Length;
            if (startColumn >= endExclusive)
                continue;

            var segment = sourceLine[startColumn..endExclusive].Trim();
            if (segment.Length == 0)
                continue;

            if (signatureBuilder.Length > 0)
                signatureBuilder.Append(' ');

            signatureBuilder.Append(segment);
        }

        return signatureBuilder.Length > 0
            ? signatureBuilder.ToString()
            : line[declarationStartColumn..].Trim();
    }

    private static JavaScriptClassScanTarget CreateJavaScriptClassScanTarget(string[] lines, string lang, int startIndex, int startColumn, int? bodyStartLine, int? bodyEndLine, string containerKind, string containerName, bool isExported = false)
    {
        var scanStartIndex = bodyStartLine!.Value - 1;
        var scanEndExclusive = bodyEndLine!.Value;
        var firstLineScanOffset = FindJavaScriptBodyOpenBraceIndex(lines, startIndex, scanStartIndex, lang, startColumn);
        if (firstLineScanOffset >= 0)
            firstLineScanOffset++;
        else
            firstLineScanOffset = 0;

        return new JavaScriptClassScanTarget(
            startIndex,
            startColumn,
            scanStartIndex,
            scanEndExclusive,
            firstLineScanOffset,
            containerKind,
            containerName,
            isExported);
    }

    private static void ExtractJavaScriptTypeScriptBareMethodsInClass(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        JavaScriptClassScanTarget classScanTarget)
    {
        if (classScanTarget.ScanStartIndex >= classScanTarget.ScanEndExclusive)
            return;

        var scanStartIndex = classScanTarget.ScanStartIndex;
        var scanEndExclusive = classScanTarget.ScanEndExclusive;
        var nestedBraceDepth = 0;
        var inFieldInitializer = false;
        var initializerParenDepth = 0;
        var initializerBracketDepth = 0;
        var initializerBraceDepth = 0;
        var lexState = new JavaScriptLexState();
        var seenMethodStarts = new HashSet<(int Line, int Column)>();
        var pendingHeaderEndLineIndex = -1;
        var pendingHeaderEndColumn = -1;
        var pendingBodyStartLineIndex = -1;
        var pendingBodyStartColumn = -1;

        for (int i = scanStartIndex; i < scanEndExclusive; i++)
        {
            var line = lines[i];
            var lexedLine = LexJavaScriptLine(line, lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            if (pendingHeaderEndLineIndex >= 0)
            {
                if (i < pendingHeaderEndLineIndex)
                    continue;
            }

            if (pendingBodyStartLineIndex >= 0)
            {
                if (i < pendingBodyStartLineIndex)
                    continue;

                if (i == pendingBodyStartLineIndex)
                {
                    if (pendingBodyStartColumn >= 0 && pendingBodyStartColumn < sanitizedLine.Length)
                    {
                        nestedBraceDepth += CountBraces(sanitizedLine[pendingBodyStartColumn..]);
                        if (nestedBraceDepth < 0)
                            nestedBraceDepth = 0;
                    }

                    pendingBodyStartLineIndex = -1;
                    pendingBodyStartColumn = -1;
                    continue;
                }
            }

            var scanStartColumn = i == scanStartIndex
                ? Math.Min(classScanTarget.FirstLineScanOffset, sanitizedLine.Length)
                : 0;
            if (pendingHeaderEndLineIndex == i)
            {
                scanStartColumn = Math.Max(scanStartColumn, Math.Min(pendingHeaderEndColumn + 1, sanitizedLine.Length));
                pendingHeaderEndLineIndex = -1;
                pendingHeaderEndColumn = -1;
            }

            if (inFieldInitializer
                && initializerParenDepth == 0
                && initializerBracketDepth == 0
                && initializerBraceDepth == 0)
            {
                var continuationInput = scanStartColumn >= sanitizedLine.Length
                    ? string.Empty
                    : sanitizedLine[scanStartColumn..];
                if (continuationInput.Any(ch => !char.IsWhiteSpace(ch))
                    && !StartsJavaScriptTypeScriptFieldInitializerContinuation(continuationInput, lang))
                {
                    inFieldInitializer = false;
                }
            }

            var column = scanStartColumn;
            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (classScanTarget.ContainerKind == "object"
                    && nestedBraceDepth == 0
                    && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var propertyStartColumn = column;
                    var propertyEndColumn = propertyStartColumn + 1;
                    while (propertyEndColumn < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[propertyEndColumn]))
                        propertyEndColumn++;

                    var propertyScanColumn = propertyEndColumn;
                    while (propertyScanColumn < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[propertyScanColumn]))
                        propertyScanColumn++;

                    if (propertyScanColumn < sanitizedLine.Length && sanitizedLine[propertyScanColumn] == ':')
                    {
                        var valueStartColumn = propertyScanColumn + 1;
                        while (valueStartColumn < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[valueStartColumn]))
                            valueStartColumn++;

                        if (valueStartColumn < sanitizedLine.Length
                            && StartsJavaScriptTypeScriptFunctionAssignmentValue(sanitizedLine[valueStartColumn..]))
                        {
                            var propertyName = sanitizedLine[propertyStartColumn..propertyEndColumn];
                            if (seenMethodStarts.Add((i + 1, propertyStartColumn)))
                            {
                                var propertyBodyOpenBraceLineIndex = -1;
                                var propertyBodyOpenBraceColumn = -1;
                                int propertyEndLine;
                                int? propertyBodyStartLine;
                                int? propertyBodyEndLine;
                                int propertySameLineEndColumn;
                                if (TryFindJavaScriptTypeScriptAssignedFunctionBodyOpenBrace(
                                    lines,
                                    i,
                                    valueStartColumn,
                                    lang,
                                    out var foundPropertyBodyOpenBraceLineIndex,
                                    out var foundPropertyBodyOpenBraceColumn))
                                {
                                    propertyBodyOpenBraceLineIndex = foundPropertyBodyOpenBraceLineIndex;
                                    propertyBodyOpenBraceColumn = foundPropertyBodyOpenBraceColumn;
                                    (propertyEndLine, propertyBodyStartLine, propertyBodyEndLine) = ResolveRange(
                                        lines, propertyBodyOpenBraceLineIndex, BodyStyle.Brace, lang, propertyBodyOpenBraceColumn);
                                    propertySameLineEndColumn = propertyBodyEndLine == i + 1
                                        ? FindSameLineBraceEndColumn(line, valueStartColumn, lang, "function")
                                        : -1;
                                }
                                else
                                {
                                    propertyEndLine = i + 1;
                                    propertyBodyStartLine = null;
                                    propertyBodyEndLine = null;
                                    propertySameLineEndColumn = -1;
                                }

                                symbols.Add(new SymbolRecord
                                {
                                    FileId = fileId,
                                    Kind = "function",
                                    Name = propertyName,
                                    Line = i + 1,
                                    StartLine = i + 1,
                                    EndLine = Math.Max(i + 1, propertyEndLine),
                                    BodyStartLine = propertyBodyStartLine,
                                    BodyEndLine = propertyBodyEndLine,
                                    Signature = line.Trim(),
                                    ContainerKind = classScanTarget.ContainerKind,
                                    ContainerName = classScanTarget.ContainerName,
                                    Visibility = classScanTarget.IsExported ? "export" : null,
                                });

                                if (propertySameLineEndColumn >= column)
                                {
                                    column = propertySameLineEndColumn + 1;
                                    continue;
                                }

                                if (propertyBodyStartLine.HasValue
                                    && propertyBodyStartLine.Value - 1 > i)
                                {
                                    pendingBodyStartLineIndex = propertyBodyStartLine.Value - 1;
                                    pendingBodyStartColumn = propertyBodyOpenBraceColumn;
                                    break;
                                }

                                if (propertyBodyStartLine.HasValue
                                    && propertyBodyStartLine.Value - 1 == i
                                    && propertyBodyOpenBraceColumn >= 0
                                    && propertyBodyOpenBraceColumn < sanitizedLine.Length)
                                {
                                    nestedBraceDepth += CountBraces(sanitizedLine[propertyBodyOpenBraceColumn..]);
                                    if (nestedBraceDepth < 0)
                                        nestedBraceDepth = 0;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (inFieldInitializer)
                {
                    AdvanceJavaScriptTypeScriptFieldInitializerState(
                        ref inFieldInitializer,
                        ref initializerParenDepth,
                        ref initializerBracketDepth,
                        ref initializerBraceDepth,
                        ch);
                    column++;
                    continue;
                }

                if (nestedBraceDepth == 0
                    && classScanTarget.ContainerKind is "interface" or "class"
                    && StartsJavaScriptTypeScriptClassMemberAt(sanitizedLine, column))
                {
                    var requireAbstractModifier = classScanTarget.ContainerKind == "class";
                    if (TryParseJavaScriptTypeScriptMemberPropertyHeader(
                        sanitizedLine,
                        column,
                        lang,
                        requireAbstractModifier,
                        out var propertyName,
                        out var propertyVisibility,
                        out var propertyTypeStartColumn,
                        out var propertyTypeEndColumn,
                        out var propertyHeaderEndColumn))
                    {
                        var propertyStartLine = i + 1;
                        if (seenMethodStarts.Add((propertyStartLine, column)))
                        {
                            var propertySignatureEnd = propertyHeaderEndColumn < line.Length
                                ? propertyHeaderEndColumn + 1
                                : line.Length;
                            symbols.Add(new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = "property",
                                Name = propertyName,
                                Line = propertyStartLine,
                                StartLine = propertyStartLine,
                                EndLine = propertyStartLine,
                                BodyStartLine = null,
                                BodyEndLine = null,
                                Signature = line[column..propertySignatureEnd].Trim(),
                                ContainerKind = classScanTarget.ContainerKind,
                                ContainerName = classScanTarget.ContainerName,
                                Visibility = propertyVisibility,
                                ReturnType = NormalizeMetadata(
                                    line[(propertyTypeStartColumn + 1)..(propertyTypeEndColumn + 1)]),
                            });
                        }

                        column = propertyHeaderEndColumn + 1;
                        continue;
                    }
                }

                if (nestedBraceDepth == 0
                    && IsJavaScriptTypeScriptMethodCandidateStart(sanitizedLine, column)
                    && !IsJavaScriptTypeScriptControlFlowHeader(sanitizedLine, column))
                {
                    if (TryCaptureJavaScriptTypeScriptMethodHeader(
                        lines,
                        i,
                        column,
                        scanEndExclusive,
                        sanitizedLine,
                        lexState,
                        lang,
                        out var methodCapture))
                    {
                        var methodHeader = methodCapture.HeaderInfo;
                        var startLine = i + 1;
                        if (seenMethodStarts.Add((startLine, column)))
                        {
                            var (endLine, bodyStartLine, bodyEndLine) = methodHeader.HasBody
                                ? ResolveRange(lines, i, BodyStyle.Brace, lang, column)
                                : (methodCapture.HeaderEndLineIndex + 1, null, null);
                            var sameLineMethodEndColumn = methodHeader.HasBody && bodyEndLine == startLine
                                ? FindJavaScriptSameLineBodyEndColumn(line, column, lang)
                                : methodCapture.HeaderEndLineIndex == i
                                    ? methodCapture.HeaderEndColumn
                                    : -1;
                            symbols.Add(new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = "function",
                                Name = GetJavaScriptTypeScriptMethodNameFromSource(methodCapture.SourceHeader, 0) ?? methodHeader.Name,
                                Line = startLine,
                                StartLine = startLine,
                                EndLine = Math.Max(startLine, endLine),
                                BodyStartLine = bodyStartLine,
                                BodyEndLine = bodyEndLine,
                                Signature = BuildJavaScriptTypeScriptBareMethodSignature(
                                    lines,
                                    i,
                                    column,
                                    bodyEndLine,
                                    sameLineMethodEndColumn,
                                    methodCapture,
                                    lang),
                                ContainerKind = classScanTarget.ContainerKind,
                                ContainerName = classScanTarget.ContainerName,
                                Visibility = methodHeader.Visibility,
                                ReturnType = GetJavaScriptTypeScriptBareMethodReturnType(methodCapture.SourceHeader, methodHeader, lang),
                            });

                            if (sameLineMethodEndColumn >= column)
                            {
                                column = sameLineMethodEndColumn + 1;
                                continue;
                            }

                            if (methodHeader.HasBody && methodCapture.BodyStartLineIndex > i)
                            {
                                pendingBodyStartLineIndex = methodCapture.BodyStartLineIndex;
                                pendingBodyStartColumn = methodCapture.BodyStartColumn;
                                break;
                            }

                            if (methodCapture.HeaderEndLineIndex > i)
                            {
                                pendingHeaderEndLineIndex = methodCapture.HeaderEndLineIndex;
                                pendingHeaderEndColumn = methodCapture.HeaderEndColumn;
                                break;
                            }
                        }

                        if (methodHeader.HasBody
                            && methodCapture.BodyStartLineIndex == i
                            && methodCapture.BodyStartColumn >= 0
                            && methodCapture.BodyStartColumn < sanitizedLine.Length)
                        {
                            nestedBraceDepth += CountBraces(sanitizedLine[methodCapture.BodyStartColumn..]);
                            if (nestedBraceDepth < 0)
                                nestedBraceDepth = 0;
                            break;
                        }

                        column++;
                        continue;
                    }

                    // Fallback: class-field arrow function (`handleClick = () => { ... }`).
                    // The method-header parser rejects these because they have no method-style
                    // parameter list before the body; handle them with a dedicated arrow parser so
                    // they still surface as function symbols instead of being consumed by the
                    // field-initializer state machine.
                    // クラスフィールドのアロー関数 (`handleClick = () => { ... }`) のフォールバック。
                    // メソッドヘッダーパーサは body 直前に method 形式の引数リストが来ないことを理由に
                    // これを弾くため、専用パーサで処理してフィールド初期化子ステートに吸われる前に
                    // function シンボルとして emit する。
                    if (TryCaptureJavaScriptTypeScriptClassFieldArrow(
                        lines,
                        i,
                        column,
                        scanEndExclusive,
                        sanitizedLine,
                        lexState,
                        lang,
                        out var arrowCapture))
                    {
                        var arrowHeader = arrowCapture.HeaderInfo;
                        var arrowStartLine = i + 1;
                        var isExpressionBody = arrowHeader.ExpressionBodyEndColumn != null
                            && arrowCapture.BodyEndLineIndex != null
                            && arrowCapture.BodyEndColumn != null;
                        if (seenMethodStarts.Add((arrowStartLine, column)))
                        {
                            int arrowEndLine;
                            int? arrowBodyStartLine;
                            int? arrowBodyEndLine;
                            int arrowSameLineEndColumn;
                            if (isExpressionBody)
                            {
                                arrowBodyStartLine = arrowCapture.BodyStartLineIndex + 1;
                                arrowBodyEndLine = arrowCapture.BodyEndLineIndex!.Value + 1;
                                arrowEndLine = arrowBodyEndLine.Value;
                                arrowSameLineEndColumn = arrowBodyEndLine == arrowStartLine
                                    ? arrowCapture.BodyEndColumn!.Value
                                    : -1;
                            }
                            else
                            {
                                (arrowEndLine, arrowBodyStartLine, arrowBodyEndLine) = ResolveRange(
                                    lines, i, BodyStyle.Brace, lang, arrowCapture.BodyStartColumn);
                                arrowSameLineEndColumn = arrowBodyEndLine == arrowStartLine
                                    ? FindJavaScriptSameLineArrowBodyEndColumn(line, arrowCapture.BodyStartColumn)
                                    : -1;
                            }
                            symbols.Add(new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = "function",
                                Name = arrowHeader.Name,
                                Line = arrowStartLine,
                                StartLine = arrowStartLine,
                                EndLine = Math.Max(arrowStartLine, arrowEndLine),
                                BodyStartLine = arrowBodyStartLine,
                                BodyEndLine = arrowBodyEndLine,
                                Signature = BuildJavaScriptTypeScriptClassFieldArrowSignature(
                                    lines,
                                    i,
                                    column,
                                    arrowBodyEndLine,
                                    arrowSameLineEndColumn,
                                    arrowCapture),
                                ContainerKind = classScanTarget.ContainerKind,
                                ContainerName = classScanTarget.ContainerName,
                                Visibility = arrowHeader.Visibility,
                                ReturnType = GetJavaScriptTypeScriptBareMethodReturnType(arrowCapture.SourceHeader, arrowHeader, lang),
                            });

                            if (arrowSameLineEndColumn >= column)
                            {
                                column = arrowSameLineEndColumn + 1;
                                continue;
                            }

                            if (isExpressionBody)
                            {
                                // Expression-body spanned multiple lines; resume scanning just
                                // after the terminating `;` using the header-end pending channel
                                // (which only skips columns up to the sentinel, never entire lines)
                                // so the next field declaration on a subsequent line is still scanned.
                                // 式本体が複数行にまたがった場合、pendingHeaderEndLineIndex / Column で
                                // 終端 `;` 直後から再開する。列単位のスキップしかしないため、直後の行に
                                // ある field 宣言 (`runInline = ...`) を取りこぼさない。
                                pendingHeaderEndLineIndex = arrowCapture.BodyEndLineIndex!.Value;
                                pendingHeaderEndColumn = arrowCapture.BodyEndColumn!.Value;
                                break;
                            }

                            if (arrowCapture.BodyStartLineIndex > i)
                            {
                                pendingBodyStartLineIndex = arrowCapture.BodyStartLineIndex;
                                pendingBodyStartColumn = arrowCapture.BodyStartColumn;
                                break;
                            }
                        }

                        if (!isExpressionBody
                            && arrowCapture.BodyStartLineIndex == i
                            && arrowCapture.BodyStartColumn >= 0
                            && arrowCapture.BodyStartColumn < sanitizedLine.Length)
                        {
                            nestedBraceDepth += CountBraces(sanitizedLine[arrowCapture.BodyStartColumn..]);
                            if (nestedBraceDepth < 0)
                                nestedBraceDepth = 0;
                            break;
                        }

                        column++;
                        continue;
                    }
                }

                if (nestedBraceDepth == 0 && CanStartJavaScriptTypeScriptClassFieldInitializer(sanitizedLine, column))
                {
                    inFieldInitializer = true;
                    initializerParenDepth = 0;
                    initializerBracketDepth = 0;
                    initializerBraceDepth = 0;
                    column++;
                    continue;
                }

                if (ch == '{')
                {
                    nestedBraceDepth++;
                }
                else if (ch == '}' && nestedBraceDepth > 0)
                {
                    nestedBraceDepth--;
                }

                column++;
            }
        }
    }

    private static void AdvanceJavaScriptTypeScriptFieldInitializerState(
        ref bool inFieldInitializer,
        ref int initializerParenDepth,
        ref int initializerBracketDepth,
        ref int initializerBraceDepth,
        char ch)
    {
        if (ch == '(')
        {
            initializerParenDepth++;
            return;
        }

        if (ch == ')' && initializerParenDepth > 0)
        {
            initializerParenDepth--;
            return;
        }

        if (ch == '[')
        {
            initializerBracketDepth++;
            return;
        }

        if (ch == ']' && initializerBracketDepth > 0)
        {
            initializerBracketDepth--;
            return;
        }

        if (ch == '{')
        {
            initializerBraceDepth++;
            return;
        }

        if (ch == '}' && initializerBraceDepth > 0)
        {
            initializerBraceDepth--;
            return;
        }

        if (ch == ';'
            && initializerParenDepth == 0
            && initializerBracketDepth == 0
            && initializerBraceDepth == 0)
        {
            inFieldInitializer = false;
        }
    }

    private static bool StartsJavaScriptTypeScriptFieldInitializerContinuation(string continuationInput, string? lang)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < continuationInput.Length && char.IsWhiteSpace(continuationInput[firstNonWhitespace]))
            firstNonWhitespace++;

        if (firstNonWhitespace >= continuationInput.Length)
            return false;

        if (IsJavaScriptTypeScriptMethodCandidateStart(continuationInput, firstNonWhitespace))
        {
            var matchCandidate = lang == "typescript"
                ? NormalizeTypeScriptBareMethodMatchInput(continuationInput[firstNonWhitespace..])
                : continuationInput[firstNonWhitespace..];
            if (TryParseJavaScriptTypeScriptMethodHeader(matchCandidate, 0, lang, out _))
                return false;
        }

        return StartsJavaScriptTypeScriptExpressionContinuation(continuationInput);
    }

    private static bool CanStartJavaScriptTypeScriptClassFieldInitializer(string sanitizedLine, int index)
    {
        if (index < 0 || index >= sanitizedLine.Length || sanitizedLine[index] != '=')
            return false;

        return index + 1 >= sanitizedLine.Length || sanitizedLine[index + 1] != '>';
    }

    private static bool IsJavaScriptTypeScriptMethodCandidateStart(string sanitizedLine, int index)
    {
        if (index < 0 || index >= sanitizedLine.Length)
            return false;

        var ch = sanitizedLine[index];
        if (ch != '#'
            && ch != '@'
            && ch != '*'
            && ch != '['
            && ch != '\''
            && ch != '"'
            && !char.IsDigit(ch)
            && !IsJavaScriptTypeScriptIdentifierStart(ch))
            return false;

        return index == 0 || !IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index - 1]);
    }

    private static JavaScriptLexedLine LexJavaScriptLine(string line, JavaScriptLexState state)
    {
        var sanitized = new char[line.Length];
        var i = 0;

        while (i < line.Length)
        {
            var ch = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (state.Mode == JavaScriptLexMode.BlockComment)
            {
                sanitized[i] = ' ';
                if (ch == '*' && next == '/')
                {
                    sanitized[i + 1] = ' ';
                    state = state with { Mode = JavaScriptLexMode.Code };
                    i++;
                }

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.SingleQuote)
            {
                sanitized[i] = ch is '\'' or '\\' ? ch : ' ';

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '\'')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.DoubleQuote)
            {
                sanitized[i] = ch is '"' or '\\' ? ch : ' ';

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '"')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.TemplateString)
            {
                sanitized[i] = ch is '`' or '\\' ? ch : ' ';

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '`')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (ch == '/' && next == '/')
            {
                while (i < line.Length)
                {
                    sanitized[i] = ' ';
                    i++;
                }

                break;
            }

            if (ch == '/' && next == '*')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = ' ';
                state = state with { Mode = JavaScriptLexMode.BlockComment };
                i++;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                sanitized[i] = ch;
                i++;
                continue;
            }

            if (state.ExpectingControlFlowOpenParen && ch != '(')
                state = state with { ExpectingControlFlowOpenParen = false };

            if (state.RegexAllowedAfterControlFlowParen && ch != '/')
            {
                state = state with
                {
                    RegexAllowedAfterControlFlowParen = false
                };
            }

            if (ch == '\'')
            {
                sanitized[i] = ch;
                state = state with { Mode = JavaScriptLexMode.SingleQuote, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '"')
            {
                sanitized[i] = ch;
                state = state with { Mode = JavaScriptLexMode.DoubleQuote, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '`')
            {
                sanitized[i] = ch;
                state = state with { Mode = JavaScriptLexMode.TemplateString, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '/' && CanStartJavaScriptRegexLiteral(state))
            {
                sanitized[i] = ' ';
                i = SkipJavaScriptRegexLiteral(line, sanitized, i);
                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Other,
                    PreviousIdentifier = null
                };
                i++;
                continue;
            }

            if (char.IsLetter(ch) || ch == '_' || ch == '$')
            {
                var tokenStart = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '$'))
                {
                    sanitized[i] = line[i];
                    i++;
                }

                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Identifier,
                    PreviousIdentifier = line[tokenStart..i],
                    ExpectingControlFlowOpenParen = IsJavaScriptControlFlowKeyword(line[tokenStart..i])
                };
                continue;
            }

            if (char.IsDigit(ch))
            {
                sanitized[i] = ch;
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '.'))
                {
                    sanitized[i] = line[i];
                    i++;
                }

                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Number,
                    PreviousIdentifier = null,
                    ExpectingControlFlowOpenParen = false
                };
                continue;
            }

            sanitized[i] = ch;
            if (!char.IsWhiteSpace(ch))
            {
                var controlFlowParenDepth = state.ControlFlowParenDepth;
                var regexAllowedAfterControlFlowParen = state.RegexAllowedAfterControlFlowParen;

                if (ch == '(')
                {
                    if (state.ExpectingControlFlowOpenParen)
                    {
                        controlFlowParenDepth = 1;
                        regexAllowedAfterControlFlowParen = false;
                    }
                    else if (controlFlowParenDepth > 0)
                    {
                        controlFlowParenDepth++;
                    }
                }
                else if (ch == ')' && controlFlowParenDepth > 0)
                {
                    controlFlowParenDepth--;
                    if (controlFlowParenDepth == 0)
                        regexAllowedAfterControlFlowParen = true;
                }

                state = state with
                {
                    PreviousTokenKind = ch switch
                    {
                        ')' => JavaScriptPrevTokenKind.CloseParen,
                        ']' => JavaScriptPrevTokenKind.CloseBracket,
                        '}' => JavaScriptPrevTokenKind.CloseBrace,
                        _ => JavaScriptPrevTokenKind.Other
                    },
                    PreviousIdentifier = null,
                    ExpectingControlFlowOpenParen = false,
                    ControlFlowParenDepth = controlFlowParenDepth,
                    RegexAllowedAfterControlFlowParen = regexAllowedAfterControlFlowParen
                };
            }

            i++;
        }

        return new JavaScriptLexedLine(new string(sanitized), state);
    }

    // Sanitize a contiguous block of C# source lines for cross-line structural
    // analysis (attribute boundaries, bracket depth). String / char / comment
    // content is blanked to spaces while preserving original line lengths, and
    // the lexer state (VerbatimString / RawString / BlockComment / ...) is
    // threaded across line boundaries so multi-line literals no longer leak
    // stray `[` / `]` / `"` characters into downstream parsers.
    // After `LexCSharpLine` sanitization, string delimiters themselves (`"`,
    // `'`, `\`) are also blanked so continuation lines (e.g. `]")] decl` closing
    // a verbatim string from the previous physical line) do not look like they
    // open a fresh string literal when the caller scans them line-by-line.
    // C# ソース行の塊を、横断的な構造解析（属性境界や bracket depth）向けに
    // sanitize する。文字列 / 文字 / コメント内容は空白で置換し元の行長を保持、
    // lexer state（VerbatimString / RawString / BlockComment など）を行をまたいで
    // 持ち越すことで、複数行リテラル由来の `[` / `]` / `"` が下流パーサへ漏れない。
    // `LexCSharpLine` による sanitize 後、文字列区切りそのもの（`"`, `'`, `\`）も
    // 空白化する。こうしないと、前行の verbatim 文字列を閉じる継続行
    // （例: `]")] decl`）が単独で走査された際に新たな文字列リテラル開始と
    // 誤読されてしまう。
    internal static string[] SanitizeCSharpLinesForCrossLineScan(string[] lines)
    {
        if (lines.Length == 0)
            return lines;

        var result = new string[lines.Length];
        var state = new CSharpLexState();
        for (var i = 0; i < lines.Length; i++)
        {
            var lexed = LexCSharpLine(lines[i], state);
            var chars = lexed.SanitizedLine.ToCharArray();
            for (var k = 0; k < chars.Length; k++)
            {
                var ch = chars[k];
                if (ch == '"' || ch == '\'' || ch == '\\')
                    chars[k] = ' ';
            }
            result[i] = new string(chars);
            state = lexed.EndState;
        }
        return result;
    }

    private static CSharpLexedLine LexCSharpLine(string line, CSharpLexState state)
    {
        var sanitized = new char[line.Length];
        var i = 0;

        while (i < line.Length)
        {
            var ch = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (state.Mode == CSharpLexMode.BlockComment)
            {
                sanitized[i] = ' ';
                if (ch == '*' && next == '/')
                {
                    sanitized[i + 1] = ' ';
                    state = state with { Mode = CSharpLexMode.Code };
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (state.Mode == CSharpLexMode.String)
            {
                sanitized[i] = ch is '"' or '\\' ? ch : ' ';

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (state.IsInterpolated && ch == '{')
                {
                    if (next == '{')
                    {
                        sanitized[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    sanitized[i] = ' ';
                    state = state with
                    {
                        Mode = CSharpLexMode.Code,
                        InterpolationReturnMode = CSharpLexMode.String,
                        InterpolationReturnRawDelimiterLength = 0,
                        InterpolationReturnDollarCount = state.InterpolationDollarCount,
                        InterpolationBraceDepth = 1,
                        IsInterpolated = false,
                        InterpolationDollarCount = 0,
                    };
                    i++;
                    continue;
                }

                if (state.IsInterpolated && ch == '}' && next == '}')
                {
                    sanitized[i + 1] = ' ';
                    i += 2;
                    continue;
                }

                if (ch == '"')
                    state = state with { Mode = CSharpLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == CSharpLexMode.Char)
            {
                sanitized[i] = ch is '\'' or '\\' ? ch : ' ';

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '\'')
                    state = state with { Mode = CSharpLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == CSharpLexMode.VerbatimString)
            {
                sanitized[i] = ch == '"' ? '"' : ' ';

                // Interpolation hole handling for $@"..." / @$"...".
                // { opens a hole (unless {{, which is a literal {). Entering a hole
                // switches to Code mode so inner strings / brackets are parsed normally;
                // Return* fields preserve the outer verbatim-interp context.
                // 補間 verbatim 文字列（$@"..." / @$"..."）のホール処理。
                // { 単独でホール開始（{{ は literal {）。ホール進入時は Code モードへ切替。
                if (state.IsInterpolated && ch == '{')
                {
                    if (next == '{')
                    {
                        sanitized[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    sanitized[i] = ' ';
                    state = state with
                    {
                        Mode = CSharpLexMode.Code,
                        InterpolationReturnMode = CSharpLexMode.VerbatimString,
                        InterpolationReturnRawDelimiterLength = 0,
                        InterpolationReturnDollarCount = state.InterpolationDollarCount,
                        InterpolationBraceDepth = 1,
                        IsInterpolated = false,
                        InterpolationDollarCount = 0,
                    };
                    i++;
                    continue;
                }

                if (state.IsInterpolated && ch == '}' && next == '}')
                {
                    sanitized[i + 1] = ' ';
                    i += 2;
                    continue;
                }

                if (ch == '"' && next == '"')
                {
                    sanitized[i + 1] = '"';
                    i += 2;
                    continue;
                }

                if (ch == '"')
                {
                    state = state with { Mode = CSharpLexMode.Code };
                    if (state.InterpolationReturnMode == CSharpLexMode.Code || state.InterpolationBraceDepth == 0)
                    {
                        state = state with
                        {
                            IsInterpolated = false,
                            InterpolationDollarCount = 0,
                        };
                    }
                }

                i++;
                continue;
            }

            if (state.Mode == CSharpLexMode.RawString)
            {
                sanitized[i] = ' ';

                // Interpolation hole handling for $"""..."""  (and multi-$ forms).
                // A run of N consecutive `{` where N = InterpolationDollarCount opens
                // a hole; fewer are literal string content. Closing mirrors this but
                // is handled in the Code-mode hole tracking below.
                // 補間 raw 文字列（$"""..."""  と $$"""..."""  など）のホール処理。
                // `{` 連続数 N が InterpolationDollarCount と一致したらホール開始。
                if (state.IsInterpolated && ch == '{')
                {
                    var openRun = 0;
                    while (i + openRun < line.Length && line[i + openRun] == '{')
                        openRun++;

                    var dollarCount = state.InterpolationDollarCount;
                    if (openRun >= dollarCount)
                    {
                        for (var j = 0; j < dollarCount && i + j < line.Length; j++)
                            sanitized[i + j] = ' ';

                        state = state with
                        {
                            Mode = CSharpLexMode.Code,
                            InterpolationReturnMode = CSharpLexMode.RawString,
                            InterpolationReturnRawDelimiterLength = state.RawDelimiterLength,
                            InterpolationReturnDollarCount = dollarCount,
                            InterpolationBraceDepth = 1,
                            IsInterpolated = false,
                            InterpolationDollarCount = 0,
                            RawDelimiterLength = 0,
                        };
                        i += dollarCount;
                        continue;
                    }

                    for (var j = 0; j < openRun && i + j < line.Length; j++)
                        sanitized[i + j] = ' ';
                    i += openRun;
                    continue;
                }

                if (ch == '"' && HasCSharpQuoteRun(line, i, state.RawDelimiterLength))
                {
                    var quoteRunLength = GetCSharpQuoteRunLength(line, i);
                    for (var j = 0; j < quoteRunLength && i + j < line.Length; j++)
                        sanitized[i + j] = ' ';

                    state = state with { Mode = CSharpLexMode.Code };
                    if (state.InterpolationReturnMode == CSharpLexMode.Code || state.InterpolationBraceDepth == 0)
                    {
                        state = state with
                        {
                            RawDelimiterLength = 0,
                            IsInterpolated = false,
                            InterpolationDollarCount = 0,
                        };
                    }
                    i += quoteRunLength;
                    continue;
                }

                i++;
                continue;
            }

            // Interpolation hole tracking. Only active when we are inside a hole of
            // an outer interpolated string (Mode = Code, InterpolationReturnMode set).
            // { increments depth; } decrements, and at depth 1 tries to close the hole
            // using the outer string's dollar count.
            // ホール内の括弧追跡。外側補間文字列のホール内（Mode=Code かつ Return* セット時）
            // のみ有効。{ で深さ++、} で --。深さ 1 で外側 dollar count を満たせば閉じる。
            if (state.Mode == CSharpLexMode.Code
                && state.InterpolationReturnMode != CSharpLexMode.Code
                && state.InterpolationBraceDepth > 0)
            {
                if (ch == '{')
                {
                    sanitized[i] = ch;
                    state = state with { InterpolationBraceDepth = state.InterpolationBraceDepth + 1 };
                    i++;
                    continue;
                }

                if (ch == '}')
                {
                    if (state.InterpolationBraceDepth > 1)
                    {
                        sanitized[i] = ch;
                        state = state with { InterpolationBraceDepth = state.InterpolationBraceDepth - 1 };
                        i++;
                        continue;
                    }

                    if (state.InterpolationReturnMode == CSharpLexMode.String)
                    {
                        sanitized[i] = ' ';
                        state = state with
                        {
                            Mode = CSharpLexMode.String,
                            IsInterpolated = true,
                            InterpolationDollarCount = state.InterpolationReturnDollarCount,
                            InterpolationBraceDepth = 0,
                            InterpolationReturnMode = CSharpLexMode.Code,
                            InterpolationReturnRawDelimiterLength = 0,
                            InterpolationReturnDollarCount = 0,
                        };
                        i++;
                        continue;
                    }

                    if (state.InterpolationReturnMode == CSharpLexMode.VerbatimString)
                    {
                        sanitized[i] = ' ';
                        state = state with
                        {
                            Mode = CSharpLexMode.VerbatimString,
                            IsInterpolated = true,
                            InterpolationDollarCount = state.InterpolationReturnDollarCount,
                            InterpolationBraceDepth = 0,
                            InterpolationReturnMode = CSharpLexMode.Code,
                            InterpolationReturnRawDelimiterLength = 0,
                            InterpolationReturnDollarCount = 0,
                        };
                        i++;
                        continue;
                    }

                    if (state.InterpolationReturnMode == CSharpLexMode.RawString)
                    {
                        var closeRun = 0;
                        while (i + closeRun < line.Length && line[i + closeRun] == '}')
                            closeRun++;

                        var dollarCount = state.InterpolationReturnDollarCount;
                        if (closeRun >= dollarCount)
                        {
                            for (var j = 0; j < dollarCount && i + j < line.Length; j++)
                                sanitized[i + j] = ' ';

                            state = state with
                            {
                                Mode = CSharpLexMode.RawString,
                                RawDelimiterLength = state.InterpolationReturnRawDelimiterLength,
                                IsInterpolated = true,
                                InterpolationDollarCount = dollarCount,
                                InterpolationBraceDepth = 0,
                                InterpolationReturnMode = CSharpLexMode.Code,
                                InterpolationReturnRawDelimiterLength = 0,
                                InterpolationReturnDollarCount = 0,
                            };
                            i += dollarCount;
                            continue;
                        }

                        // Not enough } — fall through to normal code handling.
                        // dollar count に満たない } — 通常の Code ハンドリングへ。
                    }
                }
            }

            if (ch == '/' && next == '/')
            {
                while (i < line.Length)
                {
                    sanitized[i] = ' ';
                    i++;
                }

                break;
            }

            if (ch == '/' && next == '*')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = ' ';
                state = state with { Mode = CSharpLexMode.BlockComment };
                i += 2;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                sanitized[i] = ch;
                i++;
                continue;
            }

            if (TryReadCSharpRawStringStart(line, i, out var rawPrefixLength, out var rawDelimiterLength))
            {
                for (var j = 0; j < rawPrefixLength + rawDelimiterLength && i + j < line.Length; j++)
                    sanitized[i + j] = ' ';

                state = state with
                {
                    Mode = CSharpLexMode.RawString,
                    RawDelimiterLength = rawDelimiterLength,
                    IsInterpolated = rawPrefixLength > 0,
                    InterpolationDollarCount = rawPrefixLength,
                };
                i += rawPrefixLength + rawDelimiterLength;
                continue;
            }

            if (ch == '@' && next == '"')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = '"';
                state = state with
                {
                    Mode = CSharpLexMode.VerbatimString,
                    IsInterpolated = false,
                    InterpolationDollarCount = 0,
                };
                i += 2;
                continue;
            }

            if (ch == '$' && next == '@' && i + 2 < line.Length && line[i + 2] == '"')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = ' ';
                sanitized[i + 2] = '"';
                state = state with
                {
                    Mode = CSharpLexMode.VerbatimString,
                    IsInterpolated = true,
                    InterpolationDollarCount = 1,
                };
                i += 3;
                continue;
            }

            if (ch == '@' && next == '$' && i + 2 < line.Length && line[i + 2] == '"')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = ' ';
                sanitized[i + 2] = '"';
                state = state with
                {
                    Mode = CSharpLexMode.VerbatimString,
                    IsInterpolated = true,
                    InterpolationDollarCount = 1,
                };
                i += 3;
                continue;
            }

            if (ch == '$' && next == '"')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = '"';
                state = state with
                {
                    Mode = CSharpLexMode.String,
                    IsInterpolated = true,
                    InterpolationBraceDepth = 0,
                    InterpolationDollarCount = 1,
                    InterpolationReturnMode = CSharpLexMode.Code,
                    InterpolationReturnRawDelimiterLength = 0,
                    InterpolationReturnDollarCount = 0
                };
                i += 2;
                continue;
            }

            if (ch == '"')
            {
                sanitized[i] = '"';
                state = state with { Mode = CSharpLexMode.String };
                i++;
                continue;
            }

            if (ch == '\'')
            {
                sanitized[i] = '\'';
                state = state with { Mode = CSharpLexMode.Char };
                i++;
                continue;
            }

            sanitized[i] = ch;
            i++;
        }

        return new CSharpLexedLine(new string(sanitized), state);
    }

    private static bool TryReadCSharpRawStringStart(string line, int index, out int prefixLength, out int delimiterLength)
    {
        prefixLength = 0;
        delimiterLength = 0;
        var probe = index;

        while (probe < line.Length && line[probe] == '$')
        {
            prefixLength++;
            probe++;
        }

        delimiterLength = GetCSharpQuoteRunLength(line, probe);
        return delimiterLength >= 3;
    }

    private static int GetCSharpQuoteRunLength(string line, int index)
    {
        var length = 0;
        while (index + length < line.Length && line[index + length] == '"')
            length++;

        return length;
    }

    private static bool HasCSharpQuoteRun(string line, int index, int requiredLength)
    {
        if (requiredLength <= 0)
            return false;

        return GetCSharpQuoteRunLength(line, index) >= requiredLength;
    }

    private static bool CanStartJavaScriptRegexLiteral(JavaScriptLexState state)
    {
        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.None)
            return true;

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.Other)
            return true;

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.Identifier)
        {
            return IsJavaScriptRegexPrefixKeyword(state.PreviousIdentifier);
        }

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.CloseParen)
            return state.RegexAllowedAfterControlFlowParen;

        return false;
    }

    private static bool IsJavaScriptControlFlowKeyword(string identifier)
    {
        return identifier is "if" or "for" or "while" or "switch" or "catch" or "with";
    }

    private static bool IsJavaScriptRegexPrefixKeyword(string? identifier)
    {
        return identifier is
            "return" or "throw" or "case" or "delete" or "typeof" or "void" or "new" or
            "in" or "of" or "instanceof" or "yield" or "await" or "else" or "do" or "finally";
    }

    private static int SkipJavaScriptRegexLiteral(string line, char[] sanitized, int slashIndex)
    {
        var i = slashIndex + 1;
        var inCharacterClass = false;

        while (i < line.Length)
        {
            sanitized[i] = ' ';
            var ch = line[i];
            if (ch == '\\')
            {
                if (i + 1 < line.Length)
                {
                    sanitized[i + 1] = ' ';
                    i += 2;
                    continue;
                }

                return i;
            }

            if (ch == '[')
            {
                inCharacterClass = true;
                i++;
                continue;
            }

            if (ch == ']' && inCharacterClass)
            {
                inCharacterClass = false;
                i++;
                continue;
            }

            if (ch == '/' && !inCharacterClass)
            {
                i++;
                while (i < line.Length && char.IsLetter(line[i]))
                {
                    sanitized[i] = ' ';
                    i++;
                }

                return i - 1;
            }

            i++;
        }

        return line.Length - 1;
    }

    private static int CountBraces(string sanitizedLine)
    {
        var delta = 0;
        foreach (var ch in sanitizedLine)
        {
            if (ch == '{')
                delta++;
            else if (ch == '}')
                delta--;
        }

        return delta;
    }

    public static void ApplyFamilyScope(IEnumerable<SymbolRecord> symbols, string scopeKey)
    {
        foreach (var symbol in symbols)
        {
            if (string.IsNullOrWhiteSpace(symbol.FamilyKey))
                continue;

            symbol.FamilyKey = $"{scopeKey}|{symbol.FamilyKey}";
        }
    }

    private static readonly ConditionalWeakTable<List<SymbolRecord>, SymbolAddState> SymbolAddStates = new();

    private sealed class SymbolAddState
    {
        private readonly Dictionary<SymbolRecordIdentity, int> _exactCounts = new();
        private readonly Dictionary<SameLineSignatureKey, int> _sameLineSignatureCounts = new();

        public int GetExactDuplicateCount(SymbolRecord symbol)
        {
            var key = new SymbolRecordIdentity(symbol);
            return _exactCounts.TryGetValue(key, out var count) ? count : 0;
        }

        public int? GetSameLineSignatureOccurrenceIndex(SymbolRecord symbol)
        {
            if (!TryGetSameLineSignatureKey(symbol, out var key))
                return null;

            return _sameLineSignatureCounts.TryGetValue(key, out var count) ? count : 0;
        }

        public void Record(SymbolRecord symbol)
        {
            var exactKey = new SymbolRecordIdentity(symbol);
            _exactCounts[exactKey] = _exactCounts.TryGetValue(exactKey, out var exactCount)
                ? exactCount + 1
                : 1;

            if (TryGetSameLineSignatureKey(symbol, out var sameLineKey))
            {
                _sameLineSignatureCounts[sameLineKey] = _sameLineSignatureCounts.TryGetValue(sameLineKey, out var sameLineCount)
                    ? sameLineCount + 1
                    : 1;
            }
        }

        public void Remove(SymbolRecord symbol)
        {
            var exactKey = new SymbolRecordIdentity(symbol);
            if (_exactCounts.TryGetValue(exactKey, out var exactCount))
            {
                if (exactCount <= 1)
                    _exactCounts.Remove(exactKey);
                else
                    _exactCounts[exactKey] = exactCount - 1;
            }

            if (!TryGetSameLineSignatureKey(symbol, out var sameLineKey))
                return;

            if (!_sameLineSignatureCounts.TryGetValue(sameLineKey, out var sameLineCount))
                return;

            if (sameLineCount <= 1)
                _sameLineSignatureCounts.Remove(sameLineKey);
            else
                _sameLineSignatureCounts[sameLineKey] = sameLineCount - 1;
        }
    }

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

    private readonly record struct SameLineSignatureKey(int Line, int StartLine, string Signature);

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

    private static void AddSymbolRecord(
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols,
        int lineNumber,
        SymbolRecord symbol,
        string? rawLine = null)
    {
        if (string.IsNullOrWhiteSpace(symbol.Name))
            return;

        var state = SymbolAddStates.GetValue(symbols, _ => new SymbolAddState());

        if (cssSeenSymbols != null)
        {
            var key = $"{lineNumber}:{symbol.Kind}:{symbol.Name}";
            if (!cssSeenSymbols.Add(key))
                return;
        }

        if (symbol.Kind == "function"
            && (symbol.BodyStartLine != null || symbol.BodyEndLine != null))
        {
            RemoveTrailingSameNameDeclarationOnlyFunctions(symbols, symbol);
        }

        symbol.SameLineSignatureOccurrenceIndex = state.GetSameLineSignatureOccurrenceIndex(symbol);

        // Same-line restart paths can legitimately revisit the same declaration from a
        // different regex row or restart offset. Suppress only exact duplicate symbol
        // records so mixed-kind recovery does not emit the same declaration twice while
        // still allowing legitimate overloads / siblings with the same short name but
        // different ranges or signatures. Closes #472 / #473 follow-up.
        // same-line の restart 経路では、別 regex 行や別 restart offset から同じ宣言を
        // 再訪しうる。ここでは exact duplicate の `SymbolRecord` だけを抑止し、
        // mixed-kind 回復で同じ宣言が二重出力されるのを防ぎつつ、範囲や signature が
        // 異なる正当な overload / sibling はそのまま残す。Closes #472 / #473 follow-up.
        var duplicateCount = state.GetExactDuplicateCount(symbol);
        if (duplicateCount > 0
            && !HasRemainingSameLineSignatureOccurrence(symbol, rawLine, duplicateCount))
        {
            return;
        }

        state.Record(symbol);
        symbols.Add(symbol);
    }

    private static void ExtractPhpImportSymbols(List<SymbolRecord> symbols, string line, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var groupUseMatch = PhpGroupUseRegex.Match(line);
        if (groupUseMatch.Success)
        {
            var prefix = groupUseMatch.Groups["prefix"].Value;
            var signature = line.Trim();
            foreach (var rawItem in groupUseMatch.Groups["items"].Value.Split(','))
            {
                var item = rawItem.Trim();
                if (item.Length == 0)
                    continue;

                var importedName = item;
                var alias = string.Empty;
                var aliasIndex = item.IndexOf(" as ", StringComparison.OrdinalIgnoreCase);
                if (aliasIndex >= 0)
                {
                    importedName = item[..aliasIndex].Trim();
                    alias = item[(aliasIndex + 4)..].Trim();
                }

                var symbolName = alias.Length > 0 ? alias : prefix + importedName;
                if (symbolName.Length == 0)
                    continue;

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    lineNumber,
                    new SymbolRecord
                    {
                        Kind = "import",
                        Name = symbolName,
                        Line = lineNumber,
                        StartLine = lineNumber,
                        EndLine = lineNumber,
                        Signature = signature
                    });
            }

            return;
        }

        var useMatch = PhpUseRegex.Match(line);
        if (useMatch.Success)
        {
            var symbolName = useMatch.Groups["alias"].Success
                ? useMatch.Groups["alias"].Value.Trim()
                : useMatch.Groups["name"].Value.Trim();
            if (symbolName.Length > 0)
            {
                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    lineNumber,
                    new SymbolRecord
                    {
                        Kind = "import",
                        Name = symbolName,
                        Line = lineNumber,
                        StartLine = lineNumber,
                        EndLine = lineNumber,
                        Signature = line.Trim()
                    });
            }

            return;
        }

        var requireMatch = PhpRequireIncludeRegex.Match(line);
        if (!requireMatch.Success)
            return;

        var importedPath = requireMatch.Groups["singleName"].Success
            ? requireMatch.Groups["singleName"].Value.Trim()
            : requireMatch.Groups["doubleName"].Value.Trim();
        if (importedPath.Length == 0)
            return;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                Kind = "import",
                Name = importedPath,
                Line = lineNumber,
                StartLine = lineNumber,
                EndLine = lineNumber,
                Signature = line.Trim()
            });
    }

    private static void RemoveTrailingSameNameDeclarationOnlyFunctions(List<SymbolRecord> symbols, SymbolRecord symbol)
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
            if (signature != null && signature.StartsWith("declare ", StringComparison.Ordinal))
                break;

            symbols.RemoveAt(index);
        }
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

    private static int CountNonOverlappingOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            return 0;

        var count = 0;
        var startIndex = 0;
        while (startIndex < text.Length)
        {
            var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (index < 0)
                break;

            count++;
            startIndex = index + value.Length;
        }

        return count;
    }

    private static void ExtractCssInlineGroupingSelectors(
        long fileId,
        string rawLine,
        string maskedLine,
        string[] cssScannerLines,
        int lineIndex,
        IReadOnlyList<SymbolPattern> patterns,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        var groupingDepth = 0;
        var qualifiedDepth = 0;
        var segmentStart = 0;

        for (int i = 0; i < maskedLine.Length; i++)
        {
            var ch = maskedLine[i];
            if (ch == ';')
            {
                if (groupingDepth == 0 && qualifiedDepth == 0)
                    TryAddCssLayerListSymbols(fileId, rawLine[segmentStart..i], maskedLine[segmentStart..i], lineIndex, symbols, cssSeenSymbols);

                segmentStart = i + 1;
                continue;
            }

            if (ch == '{')
            {
                var maskedSegment = maskedLine[segmentStart..i].Trim();
                var rawSegment = rawLine[segmentStart..i].Trim();
                var isGroupingAtRule = maskedSegment.StartsWith('@');

                if (isGroupingAtRule)
                    groupingDepth++;
                else
                    qualifiedDepth++;

                segmentStart = i + 1;
                continue;
            }

            if (ch == '}')
            {
                if (qualifiedDepth > 0)
                    qualifiedDepth--;
                else if (groupingDepth > 0)
                    groupingDepth--;

                segmentStart = i + 1;
            }
        }
    }

    private static void TryAddCssInlineSelectorSegment(
        long fileId,
        string rawSegment,
        string maskedSegment,
        string[] cssScannerLines,
        int lineIndex,
        int openingBraceIndex,
        IReadOnlyList<SymbolPattern> patterns,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        if (string.IsNullOrWhiteSpace(maskedSegment))
            return;

        var matchLine = $"{rawSegment} {{";
        foreach (var pattern in patterns)
        {
            if (pattern.BodyStyle != BodyStyle.Brace)
                continue;

            var match = pattern.Regex.Match(matchLine);
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Success
                ? match.Groups["name"].Value.Trim()
                : match.Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            var (endLine, bodyStartLine, bodyEndLine) = FindBraceRange(cssScannerLines, lineIndex, openingBraceIndex);
            var startLine = lineIndex + 1;
            AddSymbolRecord(
                symbols,
                cssSeenSymbols,
                startLine,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = pattern.Kind,
                    Name = name,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = Math.Max(startLine, endLine),
                    BodyStartLine = bodyStartLine,
                    BodyEndLine = bodyEndLine,
                    Signature = rawSegment.Length > 0 ? $"{rawSegment} {{" : "{",
                });
            return;
        }
    }

    private static void TryAddCssSelectorListSegments(
        long fileId,
        string rawSegment,
        string maskedSegment,
        string[] cssScannerLines,
        int lineIndex,
        int openingBraceIndex,
        IReadOnlyList<SymbolPattern> patterns,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        foreach (var (rawPart, maskedPart) in EnumerateCssCommaSeparatedSegments(rawSegment, maskedSegment))
        {
            TryAddCssInlineSelectorSegment(
                fileId,
                rawPart,
                maskedPart,
                cssScannerLines,
                lineIndex,
                openingBraceIndex,
                patterns,
                symbols,
                cssSeenSymbols);
        }
    }

    private static void TryAddCssLayerListSymbols(
        long fileId,
        string rawSegment,
        string maskedSegment,
        int lineIndex,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        var trimmedMaskedSegment = maskedSegment.TrimStart();
        if (!trimmedMaskedSegment.StartsWith("@layer", StringComparison.OrdinalIgnoreCase))
            return;

        var trimmedRawSegment = rawSegment.Trim();
        if (trimmedRawSegment.Length == 0)
            return;

        const string atLayerPrefix = "@layer";
        if (trimmedRawSegment.Length <= atLayerPrefix.Length)
            return;

        var rawNames = trimmedRawSegment[atLayerPrefix.Length..].Trim();
        var maskedNames = trimmedMaskedSegment[atLayerPrefix.Length..].Trim();
        if (rawNames.Length == 0 || maskedNames.Length == 0)
            return;

        foreach (var (rawName, maskedName) in EnumerateCssCommaSeparatedSegments(rawNames, maskedNames))
        {
            var name = rawName.Trim();
            if (name.Length == 0 || maskedName.Length == 0)
                continue;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols,
                lineIndex + 1,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "namespace",
                    Name = name,
                    Line = lineIndex + 1,
                    StartLine = lineIndex + 1,
                    EndLine = lineIndex + 1,
                    Signature = trimmedRawSegment,
                });
        }
    }

    private static IEnumerable<(string Raw, string Masked)> EnumerateCssCommaSeparatedSegments(string rawText, string maskedText)
    {
        var segmentStart = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var index = 0; index < maskedText.Length; index++)
        {
            var ch = maskedText[index];
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
                yield return (rawText[segmentStart..index].Trim(), maskedText[segmentStart..index].Trim());
                segmentStart = index + 1;
            }
        }

        yield return (rawText[segmentStart..].Trim(), maskedText[segmentStart..].Trim());
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) ResolveRange(string[] lines, int startIndex, BodyStyle bodyStyle) =>
        ResolveRange(lines, startIndex, bodyStyle, null, 0);

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) ResolveRange(string[] lines, int startIndex, BodyStyle bodyStyle, string? lang = null, int startColumn = 0)
    {
        return bodyStyle switch
        {
            BodyStyle.Brace when lang is "javascript" or "typescript" => FindJavaScriptBraceRange(lines, startIndex, lang, startColumn),
            BodyStyle.Brace when lang == "csharp" => FindCSharpBraceRange(lines, startIndex, startColumn),
            BodyStyle.Brace when lang == "java" => FindJavaBraceRange(lines, startIndex, startColumn),
            BodyStyle.Brace => FindBraceRange(lines, startIndex, startColumn, lang),
            BodyStyle.Indent => FindIndentRange(lines, startIndex),
            BodyStyle.RubyEnd => FindRubyRange(lines, startIndex),
            BodyStyle.ElixirEnd => FindElixirRange(lines, startIndex),
            BodyStyle.VisualBasicEnd => FindVisualBasicRange(lines, startIndex),
            BodyStyle.SqlProcBody => FindSqlProcBodyRange(lines, startIndex),
            _ => (startIndex + 1, null, null),
        };
    }

    private static bool TryFindKotlinScalaExpressionBodyEndLine(string line, int startColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inBlockComment = false;
        var inString = false;
        var inChar = false;

        for (var i = Math.Max(0, startColumn); i < line.Length; i++)
        {
            var c = line[i];

            if (inBlockComment)
            {
                if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                if (c == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (c == '"')
                    inString = false;
                continue;
            }

            if (inChar)
            {
                if (c == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (c == '\'')
                    inChar = false;
                continue;
            }

            if (c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                    break;

                if (line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '\'')
            {
                inChar = true;
                continue;
            }

            if (c == '(')
            {
                parenDepth++;
                continue;
            }

            if (c == ')' && parenDepth > 0)
            {
                parenDepth--;
                continue;
            }

            if (c == '[')
            {
                bracketDepth++;
                continue;
            }

            if (c == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                continue;
            }

            if (c == '{')
            {
                braceDepth++;
                continue;
            }

            if (c == '}' && braceDepth > 0)
            {
                braceDepth--;
                continue;
            }

            if (c != '=' || parenDepth > 0 || bracketDepth > 0 || braceDepth > 0)
                continue;

            if (i + 1 < line.Length && (line[i + 1] == '=' || line[i + 1] == '>'))
                continue;

            var next = i + 1;
            while (next < line.Length)
            {
                if (char.IsWhiteSpace(line[next]))
                {
                    next++;
                    continue;
                }

                if (line[next] == '/' && next + 1 < line.Length)
                {
                    if (line[next + 1] == '/')
                        return false;

                    if (line[next + 1] == '*')
                    {
                        var commentEnd = line.IndexOf("*/", next + 2, StringComparison.Ordinal);
                        if (commentEnd < 0)
                            return false;

                        next = commentEnd + 2;
                        continue;
                    }
                }

                return line[next] != '{';
            }

            return false;
        }

        return false;
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindJavaScriptBraceRange(string[] lines, int startIndex, string? lang, int startColumn = 0)
    {
        var depth = 0;
        var opened = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var pendingArrowBody = false;
        var arrowExpressionActive = false;
        var arrowExpressionParenDepth = 0;
        var arrowExpressionBracketDepth = 0;
        var arrowExpressionBraceDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        int? bodyStartLine = null;
        var lexState = new JavaScriptLexState();

        for (int i = startIndex; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var effectiveStartColumn = startColumn;
            if (i == startIndex
                && startColumn > 0
                && TryParseJavaScriptTypeScriptMethodHeader(lexedLine.SanitizedLine, startColumn, lang, out var methodHeader))
            {
                effectiveStartColumn = methodHeader.BodyStartColumn;
            }

            var scanLine = i == startIndex && effectiveStartColumn > 0 && effectiveStartColumn < lexedLine.SanitizedLine.Length
                ? lexedLine.SanitizedLine[effectiveStartColumn..]
                : i == startIndex && effectiveStartColumn >= lexedLine.SanitizedLine.Length
                    ? string.Empty
                    : lexedLine.SanitizedLine;

            if (arrowExpressionActive
                && arrowExpressionBraceDepth == 0
                && arrowExpressionParenDepth == 0
                && arrowExpressionBracketDepth == 0
                && !StartsJavaScriptTypeScriptExpressionContinuation(scanLine))
            {
                return (i, bodyStartLine ?? startIndex + 1, i);
            }

            for (int column = 0; column < scanLine.Length; column++)
            {
                var ch = scanLine[column];
                if (!opened
                    && !arrowExpressionActive
                    && !functionHeaderState.Active
                    && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    var tokenEnd = column + 1;
                    while (tokenEnd < scanLine.Length && IsJavaScriptTypeScriptIdentifierPart(scanLine[tokenEnd]))
                        tokenEnd++;

                    if (scanLine[tokenStart..tokenEnd] == "function")
                    {
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        column = tokenEnd - 1;
                        continue;
                    }
                }

                if (!opened && !arrowExpressionActive)
                {
                    var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                        ref functionHeaderState,
                        scanLine,
                        column,
                        lang ?? "javascript",
                        out var functionHeaderAdvanceColumns);
                    if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                    {
                        column += functionHeaderAdvanceColumns;
                        continue;
                    }
                }

                if (!opened && !arrowExpressionActive && i == startIndex && ch == '=' && column + 1 < scanLine.Length && scanLine[column + 1] == '>')
                {
                    pendingArrowBody = true;
                    column++;
                    continue;
                }

                if (pendingArrowBody)
                {
                    if (char.IsWhiteSpace(ch))
                        continue;

                    bodyStartLine ??= i + 1;
                    if (ch == '{')
                        pendingArrowBody = false;
                    else
                    {
                        arrowExpressionActive = true;
                        pendingArrowBody = false;
                    }
                }

                if (arrowExpressionActive)
                {
                    if (ch == '(')
                    {
                        arrowExpressionParenDepth++;
                        continue;
                    }

                    if (ch == ')' && arrowExpressionParenDepth > 0)
                    {
                        arrowExpressionParenDepth--;
                        continue;
                    }

                    if (ch == '[')
                    {
                        arrowExpressionBracketDepth++;
                        continue;
                    }

                    if (ch == ']' && arrowExpressionBracketDepth > 0)
                    {
                        arrowExpressionBracketDepth--;
                        continue;
                    }

                    if (ch == '{')
                    {
                        arrowExpressionBraceDepth++;
                        continue;
                    }

                    if (ch == '}' && arrowExpressionBraceDepth > 0)
                    {
                        arrowExpressionBraceDepth--;
                        continue;
                    }

                    if (ch == ';'
                        && arrowExpressionParenDepth == 0
                        && arrowExpressionBracketDepth == 0
                        && arrowExpressionBraceDepth == 0)
                    {
                        return (i + 1, bodyStartLine ?? startIndex + 1, i + 1);
                    }

                    continue;
                }

                if (!opened)
                {
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

                    if (ch == '<')
                    {
                        if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                            angleDepth++;
                        continue;
                    }

                    if (ch == '>' && angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }
                }

                if (ch == '{')
                {
                    if (!opened && (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0))
                        continue;

                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLine = i + 1;
                    }
                }
                else if (ch == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                        return (i + 1, bodyStartLine, i + 1);
                }
            }

            if (!opened
                && !arrowExpressionActive
                && !functionHeaderState.Active
                && parenDepth == 0
                && bracketDepth == 0
                && angleDepth == 0
                && scanLine.TrimEnd().EndsWith(';'))
                return (startIndex + 1, null, null);
        }

        if (arrowExpressionActive)
            return (lines.Length, bodyStartLine ?? startIndex + 1, lines.Length);

        return opened
            ? (lines.Length, bodyStartLine, lines.Length)
            : (startIndex + 1, null, null);
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindBraceRange(string[] lines, int startIndex, int startColumn = 0, string? lang = null)
    {
        int depth = 0;
        bool opened = false;
        int? bodyStartLine = null;
        // In languages where `'...'` is a regular string literal (PHP) rather than a char
        // literal (Java/Kotlin/Scala/Swift/Go/C/C++/Dart) or a lifetime annotation (Rust / OCaml),
        // we must scan to the next unescaped `'` regardless of length so that unbalanced `(`,
        // `[`, `{`, `}` tokens inside long single-quoted strings do not leak into brace-depth
        // counters and collapse the enclosing body range.
        // PHP のように `'...'` が通常の文字列リテラルである言語では、閉じ `'` まで距離を制限せず
        // スキップしないと、文字列内の `(` / `[` / `{` / `}` で body 範囲が壊れる。
        bool singleQuoteIsString = lang == "php";
        // Track () and [] depth so `{` / `}` inside annotation arguments, function-default lambdas,
        // and similar paren/bracket-delimited contexts do not advance the body brace counter.
        // Without this, Java headers like `class Leaf extends @Ann({A.class, B.class}) Root {`
        // count the annotation-arg `{A.class, B.class}` as the body open/close pair, flip
        // `opened=true` on the inner `{`, close depth to 0 on the inner `}`, and return a 1-line
        // body range that stops before the real class body opens. Subsequent ctor-chain emission
        // then loses the enclosing type, silently dropping `super(...)` edges for annotated Java
        // hierarchies. Same issue applies to Kotlin / Scala default-argument lambdas inside `()`.
        // Comments and string/char literals are also skipped so that unbalanced `(` `)` `[` `]`
        // `{` `}` inside them (e.g. `class Leaf extends Root /* ( */ { ... }`, Kotlin docstrings,
        // or Rust attribute comment bodies) do not leave depth counters stuck above zero and
        // silently collapse the body range. This mirrors the C# path which already routes through
        // LexCSharpLine before counting braces.
        // アノテーション引数内の `{` / `}` を本物の本体ブレースと誤認しないよう `(` / `[` 深度を追い、
        // コメント・文字列・文字リテラル内の不均衡な括弧やブレースを無視する。
        int parenDepth = 0;
        int bracketDepth = 0;
        bool inBlockComment = false;
        bool inString = false;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var scanLine = i == startIndex && startColumn > 0 && startColumn < lines[i].Length
                ? lines[i][startColumn..]
                : i == startIndex && startColumn >= lines[i].Length
                    ? string.Empty
                    : lines[i];

            int sawTerminator = -1;
            for (int j = 0; j < scanLine.Length; j++)
            {
                char c = scanLine[j];

                if (inBlockComment)
                {
                    if (c == '*' && j + 1 < scanLine.Length && scanLine[j + 1] == '/')
                    {
                        inBlockComment = false;
                        j++;
                    }
                    continue;
                }

                if (inString)
                {
                    if (c == '\\' && j + 1 < scanLine.Length)
                    {
                        j++;
                        continue;
                    }
                    if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '/' && j + 1 < scanLine.Length)
                {
                    if (scanLine[j + 1] == '/')
                        break;
                    if (scanLine[j + 1] == '*')
                    {
                        inBlockComment = true;
                        j++;
                        continue;
                    }
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '\'')
                {
                    if (singleQuoteIsString)
                    {
                        // PHP-style: `'...'` is a full string literal. Scan to the next
                        // unescaped `'` on this line regardless of length so long strings
                        // with `[` / `{` / `(` inside cannot leak into brace-depth counters.
                        // PHP の `'...'` はフルの文字列リテラル。閉じ `'` まで距離制限なく走査する。
                        var closeIdx = -1;
                        for (int k = j + 1; k < scanLine.Length; k++)
                        {
                            if (scanLine[k] == '\\' && k + 1 < scanLine.Length)
                            {
                                k++;
                                continue;
                            }
                            if (scanLine[k] == '\'')
                            {
                                closeIdx = k;
                                break;
                            }
                        }
                        // If no close on this line, swallow the rest of the line so multi-line
                        // PHP single-quoted strings do not corrupt brace depth mid-scan.
                        // その行に閉じが無ければ行末までスキップする（PHP の複数行 '...' 文字列対応）。
                        j = closeIdx > 0 ? closeIdx : scanLine.Length;
                        continue;
                    }

                    // Distinguish char literals (`'x'`, `'\n'`, `'\u{1}'`) from Rust / OCaml
                    // lifetime annotations (`'a`, `'static`, `'_`) and from possessive text
                    // in comments/strings we already skipped. A char literal has a closing
                    // `'` within a short distance; a lifetime does not. If we cannot locate
                    // a matching close within ~12 chars on this line, treat the `'` as a
                    // regular character so `Holder<'a>` does not swallow the `{` that follows.
                    // Rust の lifetime (`'a`) と char literal (`'x'`) を区別する。対応する閉じ `'`
                    // が近傍に無ければ lifetime として `'` を普通の文字扱いで読み飛ばす。
                    {
                        var closeIdx = -1;
                        var limit = Math.Min(scanLine.Length, j + 12);
                        for (int k = j + 1; k < limit; k++)
                        {
                            if (scanLine[k] == '\\' && k + 1 < scanLine.Length)
                            {
                                k++;
                                continue;
                            }
                            if (scanLine[k] == '\'')
                            {
                                closeIdx = k;
                                break;
                            }
                        }
                        if (closeIdx > 0)
                        {
                            j = closeIdx;
                        }
                    }
                    continue;
                }

                if (c == '(')
                    parenDepth++;
                else if (c == ')' && parenDepth > 0)
                    parenDepth--;
                else if (c == '[')
                    bracketDepth++;
                else if (c == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (c == ';' && !opened)
                    return (startIndex + 1, null, null);
                else if (c == '{')
                {
                    if (parenDepth > 0 || bracketDepth > 0)
                        continue;
                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLine = i + 1;
                    }
                }
                else if (c == '}' && opened)
                {
                    if (parenDepth > 0 || bracketDepth > 0)
                        continue;
                    depth--;
                    if (depth == 0)
                    {
                        sawTerminator = j;
                        break;
                    }
                }
            }

            if (sawTerminator >= 0)
                return (i + 1, bodyStartLine, i + 1);
            // Line comments reset at end of line (handled by the break above).
        }

        return opened
            ? (lines.Length, bodyStartLine, lines.Length)
            : (startIndex + 1, null, null);
    }

    // Java-aware variant of FindBraceRange. Tracks strings, char literals, comments, and text blocks
    // via the same lexer state machine used by the enum member extractor, so a `}` inside a text
    // block or quoted string does not prematurely close the containing brace range.
    // Java 用の FindBraceRange。文字列 / char / コメント / text block を enum member 抽出と同じ
    // lexer で追跡し、text block や文字列内の `}` で本体範囲が早期終了しないようにする。
    internal static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindJavaBraceRange(string[] lines, int startIndex, int startColumn = 0)
    {
        var depth = 0;
        var opened = false;
        int? bodyStartLine = null;
        var mode = JavaScanMode.Normal;
        var sawOpenParen = false;
        var annotationDefaultValue = false;
        // Track paren/bracket/angle nesting before the body opens so that `{` / `}` appearing
        // inside `@Ann({A.class, B.class})` type-use annotations or bounded generic arguments
        // don't open/close the outer class body prematurely. Once the body is opened, only
        // string/char/comment tracking matters for the depth counter, so the header-level
        // counters are frozen.
        // body `{` が開く前に `@Ann({...})` や `List<Map<String,Integer>>` のような annotation
        // 引数・入れ子 generic 内の `{` / `}` で誤って開閉しないよう、header 段階の `()` / `[]`
        // / `<>` 深さを追跡する。body が開いた後は深さ計測は不要（lexer の文字列・コメント
        // 追跡で十分）。
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;

        for (int i = startIndex; i < lines.Length; i++)
        {
            if (mode == JavaScanMode.LineComment)
                mode = JavaScanMode.Normal;

            var line = lines[i];
            var column = i == startIndex ? Math.Min(startColumn, line.Length) : 0;

            while (column < line.Length)
            {
                if (TryConsumeJavaNonCode(line, ref column, ref mode))
                    continue;

                var ch = line[column];
                if (!opened)
                {
                    if (sawOpenParen
                        && !annotationDefaultValue
                        && parenDepth == 0
                        && bracketDepth == 0
                        && angleDepth == 0
                        && StartsWithKeyword(line, column, "default"))
                    {
                        // Annotation members use `default { ... }` for array defaults, but that
                        // brace pair is part of the default value, not a real member body.
                        // `default` after a Java parameter list therefore flips the scanner into
                        // a body-less statement mode until the terminating `;`.
                        // Java の annotation member は `default { ... }` で配列デフォルト値を
                        // 持つが、この `{ ... }` は member 本体ではなく default 値の一部。
                        // Java の parameter list の後に現れた `default` は、終端 `;` まで
                        // body-less statement として扱う。
                        annotationDefaultValue = true;
                        column += "default".Length;
                        continue;
                    }

                    if (ch == '(') { parenDepth++; sawOpenParen = true; column++; continue; }
                    if (ch == ')' && parenDepth > 0) { parenDepth--; column++; continue; }
                    if (ch == '[') { bracketDepth++; column++; continue; }
                    if (ch == ']' && bracketDepth > 0) { bracketDepth--; column++; continue; }
                    if (ch == '<') { angleDepth++; column++; continue; }
                    if (ch == '>' && angleDepth > 0) { angleDepth--; column++; continue; }
                    if ((parenDepth > 0 || bracketDepth > 0 || angleDepth > 0))
                    {
                        column++;
                        continue;
                    }
                    if (annotationDefaultValue && (ch == '{' || ch == '}'))
                    {
                        column++;
                        continue;
                    }
                    if (ch == ';')
                        return (i + 1, null, null);
                }

                if (ch == '{')
                {
                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLine = i + 1;
                    }
                }
                else if (ch == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                        return (i + 1, bodyStartLine, i + 1);
                }
                column++;
            }

            if (!opened && mode == JavaScanMode.Normal
                && parenDepth == 0 && bracketDepth == 0 && angleDepth == 0
                && line.TrimEnd().EndsWith(';'))
                return (startIndex + 1, null, null);
        }

        return opened
            ? (lines.Length, bodyStartLine, lines.Length)
            : (startIndex + 1, null, null);
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindCSharpBraceRange(string[] lines, int startIndex, int startColumn = 0)
    {
        int depth = 0;
        bool opened = false;
        int? bodyStartLine = null;
        // Expression-bodied member (`=> expr;`) detection. Tracks paren/bracket depth only
        // until '=>' is observed at top level, so default-value lambdas in params
        // (e.g. `Action a = () => ...`) don't trigger expression-body mode.
        // 式本体メンバー (`=> expr;`) の検出。param のデフォルト値に出てくるラムダ
        // (`Action a = () => ...` 等) を誤検出しないよう、paren/bracket の深さを追う。
        bool expressionBody = false;
        int parenDepth = 0;
        int bracketDepth = 0;
        // `;` early-return safety check. Required so the new top-level `;` clamp
        // only fires once we have observed a parameter list (`(`), which body-less
        // function-like declarations (`void M();`, ctor signatures) always carry.
        // Without this guard, the column-space mismatch where `absoluteStartColumn`
        // arrives in collapsed-generic / attribute-stripped match-line space and is
        // sliced into raw `structuralLines` can place an unrelated leading `;` (the
        // sibling member that came before this declaration, e.g. `event ... E;`
        // ahead of a same-line `class Wrapped<T>`) at scan position 0 and trick the
        // clamp into ending the body range before the real declaration even
        // begins. Function/delegate signatures by definition pass through `(` first.
        // Closes #515 review follow-up.
        // `;` 早期 return 用の安全ガード。新たに追加する top-level `;` クランプは
        // パラメータリスト `(` を一度でも見たあとでのみ発火するようにする。これは
        // body-less な関数系宣言 (`void M();`、コンストラクタ等) は必ず `(` を通る
        // ため安全であり、逆に column-space mismatch (collapsed-generic 列や
        // attribute-strip された match-line 列を raw な `structuralLines` に
        // スライスするケース) で scan 位置 0 が直前 sibling の `;` (例: same-line
        // `class Wrapped<T>` 直前の `event ... E;`) に重なってしまった場合に、
        // 宣言が始まる前にクランプが暴発するのを防ぐ。Closes #515 review follow-up.
        bool sawOpenParen = false;
        var lexState = new CSharpLexState();

        for (int i = startIndex; i < lines.Length; i++)
        {
            var lexedLine = LexCSharpLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var scanLine = i == startIndex && startColumn > 0 && startColumn < sanitizedLine.Length
                ? sanitizedLine[startColumn..]
                : i == startIndex && startColumn >= sanitizedLine.Length
                    ? string.Empty
                    : sanitizedLine;

            for (int j = 0; j < scanLine.Length; j++)
            {
                var c = scanLine[j];

                if (expressionBody)
                {
                    // In expression-body mode: track nested (), [], {} and stop at ';' at top level.
                    // 式本体モード: ()/[]/{} の深さを追い、トップレベルの ';' で終端する。
                    if (c == '(') parenDepth++;
                    else if (c == ')' && parenDepth > 0) parenDepth--;
                    else if (c == '[') bracketDepth++;
                    else if (c == ']' && bracketDepth > 0) bracketDepth--;
                    else if (c == '{') depth++;
                    else if (c == '}' && depth > 0) depth--;
                    else if (c == ';' && parenDepth == 0 && bracketDepth == 0 && depth == 0)
                        return (i + 1, bodyStartLine, i + 1);
                    continue;
                }

                if (c == '(') { parenDepth++; sawOpenParen = true; continue; }
                if (c == ')' && parenDepth > 0) { parenDepth--; continue; }
                if (c == '[') { bracketDepth++; continue; }
                if (c == ']' && bracketDepth > 0) { bracketDepth--; continue; }

                if (c == '{' && parenDepth == 0 && bracketDepth == 0)
                {
                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLine = i + 1;
                    }
                    continue;
                }

                if (c == '}' && opened && parenDepth == 0 && bracketDepth == 0)
                {
                    depth--;
                    if (depth == 0)
                    {
                        var trailingSiblingOffset = FindNextSameLineNonClosingBraceStatementStart(scanLine, j + 1, "csharp");
                        var bodyEndLine = trailingSiblingOffset >= 0
                            && bodyStartLine.HasValue
                            && bodyStartLine.Value < i + 1
                            ? i
                            : i + 1;
                        return (i + 1, bodyStartLine, bodyEndLine);
                    }
                    continue;
                }

                // Top-level `;` after a parameter list and before any block body opened
                // terminates a body-less function-like declaration (`void M();`,
                // ctor signatures, etc.). Without this in-loop guard, recovery for
                // same-line siblings whose own line ends with the enclosing type's `}`
                // (`{ int P { get; } void M(); }`) falls through the trailing
                // `EndsWith(';')` fallback because the physical line ends with `}`,
                // then the scanner bleeds into later lines and attributes their brace
                // ranges to this body-less symbol. The `sawOpenParen` guard keeps the
                // clamp inert when `absoluteStartColumn` arrived in collapsed-generic
                // / attribute-stripped column space and slid the scan onto an earlier
                // sibling's `;`, since real function-like signatures always pass
                // through `(` before `;`. Closes #515.
                // ブロック本体が開く前に、かつパラメータリスト `(` を通過したあとで
                // 出現した top-level `;` は、本体を持たない関数系宣言
                // (`void M();`、コンストラクタ等) の終端としてその場で確定する。
                // これがないと、`{ int P { get; } void M(); }` のように物理行末が
                // `}` で閉じる same-line sibling 復元では末尾の `EndsWith(';')`
                // フォールバックが効かず、次行以降の brace を本体と誤認して
                // body-less symbol に取り込んでしまう。`sawOpenParen` ガードに
                // よって、`absoluteStartColumn` が collapsed-generic / attribute-strip
                // 後の列で渡って raw `structuralLines` 上の直前 sibling の `;` に
                // 落ち込むケースでもクランプが暴発しない (関数系シグネチャは必ず
                // `(` を通過するため)。Closes #515.
                if (c == ';' && sawOpenParen && !opened && parenDepth == 0 && bracketDepth == 0)
                    return (i + 1, null, null);

                // Detect '=>' at top level (outside any (), [], {}) before any block body opened.
                // This marks an expression-bodied member; body spans the declaration line
                // through the line holding the terminating ';'.
                // () / [] / {} の外側で、かつブロック本体がまだ開いていない状態で '=>' を検出すると
                // 式本体メンバー開始。本体は宣言行から終端 ';' の行までとする。
                if (c == '=' && j + 1 < scanLine.Length && scanLine[j + 1] == '>'
                    && !opened && parenDepth == 0 && bracketDepth == 0)
                {
                    expressionBody = true;
                    bodyStartLine = startIndex + 1;
                    j++; // consume '>' / '>' を消費
                    continue;
                }
            }

            if (!opened && !expressionBody && scanLine.TrimEnd().EndsWith(';'))
                return (startIndex + 1, null, null);
        }

        if (expressionBody)
            return (lines.Length, bodyStartLine, lines.Length);

        return opened
            ? (lines.Length, bodyStartLine, lines.Length)
            : (startIndex + 1, null, null);
    }

    private static int FindJavaScriptBodyOpenBraceIndex(string[] lines, int startIndex, int bodyStartIndex, string? lang, int startColumn = 0)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        var lexState = new JavaScriptLexState();

        for (int i = startIndex; i <= bodyStartIndex; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            var initialColumn = i == startIndex ? Math.Max(0, startColumn) : 0;
            for (int column = initialColumn; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (!functionHeaderState.Active && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    var tokenEnd = column + 1;
                    while (tokenEnd < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenEnd]))
                        tokenEnd++;

                    if (sanitizedLine[tokenStart..tokenEnd] == "function")
                    {
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        column = tokenEnd - 1;
                        continue;
                    }
                }

                var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                    ref functionHeaderState,
                    sanitizedLine,
                    column,
                    lang ?? "javascript",
                    out var functionHeaderAdvanceColumns);
                if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                {
                    column += functionHeaderAdvanceColumns;
                    continue;
                }

                if (!char.IsWhiteSpace(ch))
                {
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

                    if (ch == '<')
                    {
                        if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                            angleDepth++;
                        continue;
                    }

                    if (ch == '>' && angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }
                }

                if (ch != '{')
                    continue;

                if (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0)
                    continue;

                return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptSameLineBodyEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        if (!TryParseJavaScriptTypeScriptMethodHeader(sanitizedLine, startColumn, lang, out var methodHeader))
            return -1;

        return FindJavaScriptSameLineBraceBodyEndColumn(sanitizedLine, methodHeader.BodyStartColumn);
    }

    // Same-line body end finder for class-field arrow functions. The scanner already knows the
    // sanitized body-open column from the arrow capture, so we walk braces from that column
    // without re-parsing the header (which the method-header parser would reject).
    // クラスフィールドのアロー関数向けの同一行 body 終了列探索。スキャナが arrow capture の段階で
    // sanitized 上の body 開始列を把握しているので、ヘッダを再パースせずそこから brace を辿る。
    private static int FindJavaScriptSameLineArrowBodyEndColumn(string line, int bodyStartColumn)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        return FindJavaScriptSameLineBraceBodyEndColumn(sanitizedLine, bodyStartColumn);
    }

    private static int FindJavaScriptSameLineBraceBodyEndColumn(string sanitizedLine, int bodyStartColumn)
    {
        var depth = 0;
        var opened = false;

        for (int column = Math.Max(0, bodyStartColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptSymbolStartColumn(string line, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return 0;

        var startColumn = line.IndexOf(signature, StringComparison.Ordinal);
        return startColumn >= 0 ? startColumn : 0;
    }

    private static int FindJavaScriptTypeScriptSameLineBraceEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        var bodyStartColumn = FindJavaScriptTypeScriptBodyOpenBraceColumn(sanitizedLine, startColumn, lang);
        if (bodyStartColumn < 0)
            return -1;

        var depth = 0;
        var opened = false;

        for (int column = bodyStartColumn; column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptBodyOpenBraceColumn(string sanitizedLine, int startColumn, string? lang)
    {
        if (TryParseJavaScriptTypeScriptMethodHeader(sanitizedLine, startColumn, lang, out var methodHeader))
            return methodHeader.BodyStartColumn;

        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (!functionHeaderState.Active && IsJavaScriptTypeScriptIdentifierStart(ch))
            {
                var tokenStart = column;
                var tokenEnd = column + 1;
                while (tokenEnd < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenEnd]))
                    tokenEnd++;

                if (sanitizedLine[tokenStart..tokenEnd] == "function")
                {
                    BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                    column = tokenEnd - 1;
                    continue;
                }
            }

            var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                ref functionHeaderState,
                sanitizedLine,
                column,
                lang ?? "javascript",
                out var functionHeaderAdvanceColumns);
            if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
            {
                column += functionHeaderAdvanceColumns;
                continue;
            }

            if (!char.IsWhiteSpace(ch))
            {
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

                if (ch == '<')
                {
                    if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                        angleDepth++;
                    continue;
                }

                if (ch == '>' && angleDepth > 0)
                {
                    angleDepth--;
                    continue;
                }
            }

            if (ch != '{')
                continue;

            if (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0)
                continue;

            return column;
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptSameLineStatementEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (char.IsWhiteSpace(ch))
                continue;

            switch (ch)
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
                case '<':
                    if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                        angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                        return column;
                    break;
            }
        }

        return -1;
    }

    private static string NormalizeTypeScriptBareMethodMatchInput(string input)
    {
        if (!input.Contains('<', StringComparison.Ordinal) && !input.Contains('{', StringComparison.Ordinal))
            return input;

        if (!TryParseJavaScriptTypeScriptMethodHeader(input, 0, "typescript", out var methodHeader))
            return input;

        var chars = input.ToCharArray();
        if (methodHeader.GenericStartColumn != null && methodHeader.GenericEndColumn != null)
        {
            for (int replaceIndex = methodHeader.GenericStartColumn.Value; replaceIndex <= methodHeader.GenericEndColumn.Value; replaceIndex++)
                chars[replaceIndex] = ' ';
        }

        if (methodHeader.ReturnTypeStartColumn != null && methodHeader.ReturnTypeEndColumn != null)
        {
            for (int replaceIndex = methodHeader.ReturnTypeStartColumn.Value; replaceIndex <= methodHeader.ReturnTypeEndColumn.Value; replaceIndex++)
            {
                if (chars[replaceIndex] == '{')
                    chars[replaceIndex] = '(';
                else if (chars[replaceIndex] == '}')
                    chars[replaceIndex] = ')';
            }
        }

        return new string(chars);
    }

    // Class-field arrow like `handleClick = () => { ... }` is not matched by the method-header
    // parser because the identifier is followed by `=` instead of `(`. This parser handles that
    // shape (with optional TS modifiers, field type annotation, generics, and return type).
    // 正規表現や method-header パーサは `name = ... =>` 形式のクラスフィールド矢印関数を拾えないため、
    // 専用パーサでそのシェイプだけ（修飾子・フィールド型注釈・ジェネリクス・戻り値型を含む）をパースする。
    private static bool TryParseJavaScriptTypeScriptClassFieldArrowHeader(
        string sanitizedHeader,
        int startColumn,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderInfo arrowInfo)
    {
        arrowInfo = default;
        var index = Math.Max(0, startColumn);
        string? visibility = null;

        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        TrySkipJavaScriptTypeScriptDecorators(sanitizedHeader, ref index);

        string? candidateName = null;
        while (index < sanitizedHeader.Length)
        {
            if (!TryReadJavaScriptTypeScriptMethodToken(sanitizedHeader, ref index, out var token))
                return false;

            if (token == "*")
                return false;

            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;

            if (TypeScriptBareMethodModifiers.Contains(token)
                && CanTreatJavaScriptTypeScriptMethodTokenAsModifier(sanitizedHeader, index))
            {
                // `get`/`set`/`async`/`abstract` as leading modifier here would turn the construct
                // back into a method (not an arrow field); bail so the method-header parser owns it.
                // `get`/`set`/`async`/`abstract` が先頭修飾子に来るケースは arrow field ではなく
                // method なので、method-header パーサ側に委ねるためここで諦める。
                if (token is "get" or "set" or "async" or "abstract")
                    return false;
                if (token is "public" or "private" or "protected")
                    visibility = token;
                continue;
            }

            candidateName = token;
            break;
        }

        if (candidateName == null)
            return false;

        if (index < sanitizedHeader.Length && (sanitizedHeader[index] == '?' || sanitizedHeader[index] == '!'))
        {
            index++;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == ':')
        {
            if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilFieldEquals(sanitizedHeader, ref index))
                return false;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != '=')
            return false;
        if (index + 1 < sanitizedHeader.Length && (sanitizedHeader[index + 1] == '=' || sanitizedHeader[index + 1] == '>'))
            return false;
        index++;
        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        if (index + 5 <= sanitizedHeader.Length
            && string.CompareOrdinal(sanitizedHeader, index, "async", 0, 5) == 0
            && (index + 5 == sanitizedHeader.Length || !IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[index + 5])))
        {
            index += 5;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        int? genericStartColumn = null;
        int? genericEndColumn = null;
        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == '<')
        {
            genericStartColumn = index;
            var angleDepth = 0;
            while (index < sanitizedHeader.Length)
            {
                var ch = sanitizedHeader[index];
                if (ch == '<')
                {
                    angleDepth++;
                }
                else if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
                {
                    index += 2;
                    continue;
                }
                else if (ch == '>')
                {
                    angleDepth--;
                    if (angleDepth == 0)
                    {
                        genericEndColumn = index;
                        index++;
                        break;
                    }
                }
                index++;
            }
            if (genericEndColumn == null)
                return false;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index >= sanitizedHeader.Length)
            return false;

        if (sanitizedHeader[index] == '(')
        {
            var parenDepth = 0;
            while (index < sanitizedHeader.Length)
            {
                var ch = sanitizedHeader[index];
                if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')')
                {
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        index++;
                        break;
                    }
                }
                index++;
            }
            if (parenDepth != 0)
                return false;
        }
        else if (IsJavaScriptTypeScriptIdentifierStart(sanitizedHeader[index]))
        {
            index++;
            while (index < sanitizedHeader.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[index]))
                index++;
        }
        else
        {
            return false;
        }

        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        int? returnTypeStartColumn = null;
        int? returnTypeEndColumn = null;
        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == ':')
        {
            returnTypeStartColumn = index;
            if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilArrow(sanitizedHeader, ref index, out var rtEnd))
                return false;
            returnTypeEndColumn = rtEnd;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index + 1 >= sanitizedHeader.Length
            || sanitizedHeader[index] != '='
            || sanitizedHeader[index + 1] != '>')
            return false;

        index += 2;
        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        if (index >= sanitizedHeader.Length)
            return false;

        // Block-body arrow (`=> { ... }`). HeaderEndColumn == BodyStartColumn, both point at `{`.
        // ブロック本体矢印 (`=> { ... }`)。header 終端と body 開始は同じ `{` を指す。
        if (sanitizedHeader[index] == '{')
        {
            arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                candidateName,
                index,
                visibility,
                genericStartColumn,
                genericEndColumn,
                returnTypeStartColumn,
                returnTypeEndColumn,
                index);
            return true;
        }

        // Expression-body arrow (`=> expr;`). Walk until a class-field terminator at depth 0.
        // Explicit `;` always terminates; implicit ASI also terminates when we hit the enclosing
        // class body `}` or a newline followed by a new class-member start (identifier+`=`/`(`,
        // `#private`, `*name`, decorator, or modifier keyword). `[` is treated as continuation
        // here because a bare `[` is ambiguous between computed-member access and a computed
        // method name; see StartsJavaScriptTypeScriptClassMemberAt for the full rationale.
        // `{}` / `()` / `[]` stay balanced; strings / comments are already masked by the upstream
        // lexer. If the accumulated header ends at depth 0 with expression tokens but no visible
        // terminator, return false so TryCapture pulls another line and retries.
        // 式本体矢印 (`=> expr;`)。深さ 0 でのクラスフィールド終端まで歩く。明示的な `;` は常に終端し、
        // 暗黙の ASI は囲みクラス body の `}` か、改行直後に新しいクラスメンバの開始 (identifier+`=`/`(`、
        // `#private`、`*name`、decorator、修飾子キーワード) が来た場合にも終端する。`[` は computed
        // member access の継続と computed method 名の両方になり得るためここでは継続扱いとする
        // (詳細は StartsJavaScriptTypeScriptClassMemberAt のコメント参照)。
        // 括弧類はバランスを取り、文字列・コメントは上流の lexer でマスク済み。終端が見えないまま
        // 蓄積ヘッダの末尾に達したら false を返し、TryCapture に次の行を積ませる。
        var expressionStart = index;
        var parenDepth2 = 0;
        var bracketDepth2 = 0;
        var braceDepth2 = 0;
        int? lastNonWhitespace = null;
        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == ';' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0)
            {
                if (lastNonWhitespace == null)
                    return false;
                arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                    candidateName,
                    expressionStart,
                    visibility,
                    genericStartColumn,
                    genericEndColumn,
                    returnTypeStartColumn,
                    returnTypeEndColumn,
                    expressionStart,
                    HasBody: true,
                    ExpressionBodyEndColumn: lastNonWhitespace);
                return true;
            }

            if (ch == '}' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0)
            {
                // Enclosing class body `}` at depth 0. If we already have expression tokens that
                // can validly end a statement (identifier/number/`)`/`]`/`}`), treat it as ASI and
                // emit. Otherwise bail so the class scanner handles the closer.
                // 囲みクラス body の `}` (深さ 0)。識別子/数値/`)`/`]`/`}` のように文末になり得るトークンが
                // 既に見えていれば ASI として終端扱いで emit する。無ければクラススキャナに委ねるため false。
                if (lastNonWhitespace != null
                    && CanJavaScriptTypeScriptExpressionEndAt(sanitizedHeader[lastNonWhitespace.Value]))
                {
                    arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                        candidateName,
                        expressionStart,
                        visibility,
                        genericStartColumn,
                        genericEndColumn,
                        returnTypeStartColumn,
                        returnTypeEndColumn,
                        expressionStart,
                        HasBody: true,
                        ExpressionBodyEndColumn: lastNonWhitespace);
                    return true;
                }
                return false;
            }

            if (ch == '\n' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0
                && lastNonWhitespace != null
                && CanJavaScriptTypeScriptExpressionEndAt(sanitizedHeader[lastNonWhitespace.Value]))
            {
                var peek = index + 1;
                while (peek < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[peek]))
                    peek++;
                // peek == sanitizedHeader.Length means we exhausted the accumulated header after
                // this newline — need more input from TryCapture. Break out of the heuristic and
                // fall through to the normal end-of-input `return false` path.
                // peek が末尾に達した場合は、この改行以降に蓄積ヘッダ上の文字が尽きたということなので
                // TryCapture に次の行を積ませる必要がある。ヒューリスティックは停止し、ループ末尾の
                // end-of-input `return false` に任せる。
                if (peek < sanitizedHeader.Length
                    && StartsJavaScriptTypeScriptClassMemberAt(sanitizedHeader, peek))
                {
                    arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                        candidateName,
                        expressionStart,
                        visibility,
                        genericStartColumn,
                        genericEndColumn,
                        returnTypeStartColumn,
                        returnTypeEndColumn,
                        expressionStart,
                        HasBody: true,
                        ExpressionBodyEndColumn: lastNonWhitespace);
                    return true;
                }
            }

            if (ch == '(') parenDepth2++;
            else if (ch == ')' && parenDepth2 > 0) parenDepth2--;
            else if (ch == '[') bracketDepth2++;
            else if (ch == ']' && bracketDepth2 > 0) bracketDepth2--;
            else if (ch == '{') braceDepth2++;
            else if (ch == '}' && braceDepth2 > 0) braceDepth2--;

            if (!char.IsWhiteSpace(ch))
                lastNonWhitespace = index;
            index++;
        }

        return false;
    }

    // Returns true when `ch` is a token that can validly end a JavaScript / TypeScript expression
    // (identifier/digit tail, closing bracket, `$`/`_`, or the closing delimiter of a string /
    // template literal). The upstream lexer preserves the opening and closing `"`/`'`/`` ` `` in
    // the sanitized header (only the body content is blanked to spaces), so a string-returning
    // arrow such as `only = () => "x"` ends with a visible quote character here.
    // Operator-like characters (`+`, `.`, `,`, etc.) return false so multi-line expression
    // continuations are not accidentally cut off by the ASI heuristic.
    // `ch` が JavaScript / TypeScript の式を終端できるトークン (識別子/数字末尾、閉じ括弧、`$`/`_`、
    // 文字列・テンプレートリテラルの閉じデリミタ) なら true。上流の lexer は sanitized header 上で
    // `"` / `'` / `` ` `` の開き/閉じ文字は残し、リテラル本体だけをスペースに blank する。
    // そのため `only = () => "x"` のような文字列を返す式は、ここでは閉じクォートが lastNonWhitespace と
    // して可視のまま残る。演算子類 (`+`、`.`、`,` 等) は false を返すことで、複数行の式継続が ASI
    // ヒューリスティックで誤って途中終端されないようにする。
    private static bool CanJavaScriptTypeScriptExpressionEndAt(char ch)
    {
        if (char.IsLetterOrDigit(ch))
            return true;
        return ch is '_' or '$' or ')' or ']' or '}' or '"' or '\'' or '`';
    }

    // Returns true when the position starts a new class-body member declaration: `}` (class body
    // close), `;` (stray empty statement), `#` / `@` / `*<name>` lead tokens, or an identifier that
    // is either a well-known class-member modifier keyword or is followed by a class-field /
    // method-shorthand syntactic marker (`=`, `(`, `<`, `?`, `!`, `:`, `;`).
    // Note: `[` is intentionally NOT a member-start signal here. A bare `[` after a newline is
    // ambiguous between a computed method name (`[Symbol.iterator]()`) and a computed member
    // access continuation (`foo\n  [bar]`). JavaScript's ASI rule explicitly forbids inserting a
    // `;` before a line that starts with `[`, so any source file that wants the computed-method
    // reading must write an explicit `;` — which the outer loop's `;` branch already handles. That
    // makes "treat `[` as continuation" the safe default for this heuristic.
    // Feed a sanitized (lex-masked) header string; strings/comments must already be blanked.
    // 指定位置がクラスボディの新しいメンバ宣言を始めるかを判定する: `}` (クラス body 閉じ)、
    // `;` (空文)、`#` / `@` / `*<name>` の先頭トークン、あるいは識別子で「クラスメンバ修飾キーワード」
    // または直後が `=` / `(` / `<` / `?` / `!` / `:` / `;` の場合。
    // 注意: `[` はあえて member-start として扱わない。改行直後の素の `[` は computed method name
    // (`[Symbol.iterator]()`) と computed member access の継続 (`foo\n  [bar]`) の両方に見えてしまう。
    // JavaScript の ASI 規則は `[` で始まる行の前に自動で `;` を挿入しないため、計算メンバ名を意図する
    // ソースは明示的に `;` を書く必要があり、そのケースは外側ループの `;` 分岐で既に拾える。よって
    // この ASI ヒューリスティックでは `[` を継続として扱うのが安全な既定。
    // 呼び出し側は lexer でマスク済み (文字列/コメントが blanked) の sanitizedHeader を渡すこと。
    private static bool StartsJavaScriptTypeScriptClassMemberAt(string sanitizedHeader, int index)
    {
        if (index < 0 || index >= sanitizedHeader.Length)
            return false;
        var ch = sanitizedHeader[index];
        if (ch is '}' or ';' or '#' or '@')
            return true;
        if (ch == '*')
        {
            var j = index + 1;
            while (j < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[j]))
                j++;
            if (j >= sanitizedHeader.Length)
                return false;
            var next = sanitizedHeader[j];
            return IsJavaScriptTypeScriptIdentifierStart(next) || next is '#' or '[';
        }
        if (!IsJavaScriptTypeScriptIdentifierStart(ch))
            return false;

        var end = index + 1;
        while (end < sanitizedHeader.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[end]))
            end++;
        var word = sanitizedHeader[index..end];
        if (word is "async" or "static" or "get" or "set" or "public" or "private" or "protected"
            or "readonly" or "override" or "abstract" or "declare" or "accessor" or "constructor")
        {
            return true;
        }

        var after = end;
        while (after < sanitizedHeader.Length && sanitizedHeader[after] != '\n' && char.IsWhiteSpace(sanitizedHeader[after]))
            after++;
        if (after >= sanitizedHeader.Length)
            return false;
        var follow = sanitizedHeader[after];
        return follow is '=' or '(' or '<' or '?' or '!' or ':' or ';';
    }

    // Walks a TypeScript type annotation starting at ':' through to the outer '=' that terminates
    // it (i.e., the class-field assignment operator). `=>` inside the type (arrow types) is
    // treated as a two-char token and skipped; `==` is likewise skipped so we do not terminate on
    // a stray comparison.
    // 型注釈 `:` から、フィールド代入の外側 `=` までを歩く。型内部の `=>` (arrow type) は 2 文字ひと組で
    // 読み飛ばし、`==` も比較演算子として読み飛ばして誤終端しないようにする。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilFieldEquals(string sanitizedHeader, ref int index)
    {
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                index += 2;
                continue;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
            {
                if (ch == '=')
                {
                    if (index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '=')
                    {
                        index += 2;
                        continue;
                    }
                    return true;
                }
                if (ch == ';' || ch == ',')
                    return false;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            index++;
        }

        return false;
    }

    // Walks a TypeScript member-property type annotation from `:` to the terminating `;`.
    // Arrow types inside nested parens / angles / brackets are skipped as two-char tokens so
    // `=>` in function types does not terminate the walk early.
    // TypeScript の member-property 型注釈を `:` から終端 `;` まで歩く。入れ子の
    // 括弧 / 山括弧 / 角括弧内の arrow type は 2 文字トークンとして読み飛ばし、
    // function type 内の `=>` で早期終了しないようにする。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilSemicolon(string sanitizedHeader, ref int index, out int typeEndColumn)
    {
        typeEndColumn = -1;
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        var lastNonWs = index;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                    lastNonWs = index + 1;
                index += 2;
                continue;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
            {
                if (ch == ';')
                {
                    typeEndColumn = lastNonWs;
                    return true;
                }
                if (!char.IsWhiteSpace(ch))
                    lastNonWs = index;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            index++;
        }

        return false;
    }

    // Walks a TypeScript return-type annotation from ':' to the terminating '=>'. Inner arrow
    // types inside parens/angles/brackets are skipped as two-char tokens without decrementing
    // depth. Returns the inclusive column of the last non-whitespace character of the type.
    // 戻り値型 `:` から最外殻の `=>` までを歩く。括弧/角括弧/山括弧内の arrow type は 2 文字単位で
    // 読み飛ばし深さを下げない。型末尾の非空白位置 (inclusive) を返す。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilArrow(
        string sanitizedHeader,
        ref int index,
        out int typeEndColumn)
    {
        typeEndColumn = -1;
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        var lastNonWs = index;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                {
                    typeEndColumn = lastNonWs;
                    return true;
                }
                lastNonWs = index + 1;
                index += 2;
                continue;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            if (!char.IsWhiteSpace(ch))
                lastNonWs = index;
            index++;
        }

        return false;
    }

    private static bool TryParseJavaScriptTypeScriptMethodHeader(string sanitizedLine, int startColumn, string? lang, out JavaScriptTypeScriptMethodHeaderInfo methodHeader)
    {
        return ParseJavaScriptTypeScriptMethodHeader(sanitizedLine, startColumn, lang, out methodHeader)
            == JavaScriptTypeScriptMethodHeaderParseStatus.Parsed;
    }

    private static bool TryParseJavaScriptTypeScriptMemberPropertyHeader(
        string sanitizedLine,
        int startColumn,
        string? lang,
        bool requireAbstractModifier,
        out string name,
        out string? visibility,
        out int typeStartColumn,
        out int typeEndColumn,
        out int headerEndColumn)
    {
        name = string.Empty;
        visibility = null;
        typeStartColumn = -1;
        typeEndColumn = -1;
        headerEndColumn = -1;

        if (lang != "typescript")
            return false;

        var index = Math.Max(0, startColumn);
        var sawAbstract = false;
        var sawName = false;

        while (index < sanitizedLine.Length)
        {
            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;

            if (index >= sanitizedLine.Length)
                return false;

            if (!TryReadJavaScriptTypeScriptSourceMethodName(sanitizedLine, ref index, out var token))
                return false;

            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;

            if (token is "public" or "private" or "protected")
            {
                visibility = token;
                continue;
            }

            if (token is "static" or "readonly" or "override" or "declare")
            {
                continue;
            }

            if (token == "abstract")
            {
                sawAbstract = true;
                continue;
            }

            if (!IsJavaScriptTypeScriptIdentifierStart(token[0]))
                return false;

            name = token;
            sawName = true;
            break;
        }

        if (!sawName)
            return false;

        if (requireAbstractModifier && !sawAbstract)
            return false;

        if (index < sanitizedLine.Length && sanitizedLine[index] == '?')
        {
            index++;
            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;
        }

        if (index >= sanitizedLine.Length || sanitizedLine[index] != ':')
            return false;

        typeStartColumn = index;
        if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilSemicolon(sanitizedLine, ref index, out typeEndColumn))
            return false;

        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        if (index >= sanitizedLine.Length || sanitizedLine[index] != ';')
            return false;

        headerEndColumn = index;
        return true;
    }

    private static JavaScriptTypeScriptMethodHeaderParseStatus ParseJavaScriptTypeScriptMethodHeader(string sanitizedLine, int startColumn, string? lang, out JavaScriptTypeScriptMethodHeaderInfo methodHeader)
    {
        methodHeader = default;
        var index = Math.Max(0, startColumn);
        string? visibility = null;

        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        TrySkipJavaScriptTypeScriptDecorators(sanitizedLine, ref index);

        while (index < sanitizedLine.Length)
        {
            while (true)
            {
                if (!TryReadJavaScriptTypeScriptMethodToken(sanitizedLine, ref index, out var token))
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                    index++;

                if (TypeScriptBareMethodModifiers.Contains(token)
                    && CanTreatJavaScriptTypeScriptMethodTokenAsModifier(sanitizedLine, index))
                {
                    if (token is "public" or "private" or "protected")
                        visibility = token;
                    continue;
                }

                var isGenerator = token == "*";
                if (!isGenerator && index < sanitizedLine.Length && sanitizedLine[index] == '*')
                {
                    isGenerator = true;
                    index++;
                    while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                        index++;
                }

                if (isGenerator)
                {
                    if (!TryReadJavaScriptTypeScriptMethodName(sanitizedLine, ref index, out var generatorName))
                        return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                    token = generatorName;
                }

                var name = token;

                int? genericStartColumn = null;
                int? genericEndColumn = null;
                if (lang == "typescript" && index < sanitizedLine.Length && sanitizedLine[index] == '<')
                {
                    genericStartColumn = index;
                    var angleDepth = 0;
                    while (index < sanitizedLine.Length)
                    {
                        if (sanitizedLine[index] == '<')
                        {
                            angleDepth++;
                        }
                        else if (sanitizedLine[index] == '=' && index + 1 < sanitizedLine.Length && sanitizedLine[index + 1] == '>')
                        {
                            index += 2;
                            continue;
                        }
                        else if (sanitizedLine[index] == '>')
                        {
                            angleDepth--;
                            if (angleDepth == 0)
                            {
                                genericEndColumn = index;
                                index++;
                                break;
                            }
                        }

                        index++;
                    }

                    if (genericEndColumn == null)
                        return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                    while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                        index++;
                }

                if (index >= sanitizedLine.Length || sanitizedLine[index] != '(')
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                var parenDepth = 0;
                while (index < sanitizedLine.Length)
                {
                    if (sanitizedLine[index] == '(')
                    {
                        parenDepth++;
                    }
                    else if (sanitizedLine[index] == ')')
                    {
                        parenDepth--;
                        if (parenDepth == 0)
                        {
                            index++;
                            break;
                        }
                    }

                    index++;
                }

                if (parenDepth != 0)
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                    index++;

                int? returnTypeStartColumn = null;
                int? returnTypeEndColumn = null;
                if (lang == "typescript" && index < sanitizedLine.Length && sanitizedLine[index] == ':')
                {
                    returnTypeStartColumn = index;
                    index++;
                    var returnParenDepth = 0;
                    var returnBracketDepth = 0;
                    var returnAngleDepth = 0;
                    var returnBraceDepth = 0;
                    var sawReturnTypeToken = false;
                    string? previousReturnToken = ":";

                    while (index < sanitizedLine.Length)
                    {
                        var ch = sanitizedLine[index];
                        if (char.IsWhiteSpace(ch))
                        {
                            index++;
                            continue;
                        }

                        if (ch == ';'
                            && returnParenDepth == 0
                            && returnBracketDepth == 0
                            && returnAngleDepth == 0
                            && returnBraceDepth == 0)
                        {
                            returnTypeEndColumn ??= index - 1;
                            methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, -1, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index, false);
                            return JavaScriptTypeScriptMethodHeaderParseStatus.DeclarationOnly;
                        }

                        if (ch == '(')
                        {
                            returnParenDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "(";
                            index++;
                            continue;
                        }

                        if (ch == ')' && returnParenDepth > 0)
                        {
                            returnParenDepth--;
                            previousReturnToken = ")";
                            index++;
                            continue;
                        }

                        if (ch == '[')
                        {
                            returnBracketDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "[";
                            index++;
                            continue;
                        }

                        if (ch == ']' && returnBracketDepth > 0)
                        {
                            returnBracketDepth--;
                            previousReturnToken = "]";
                            index++;
                            continue;
                        }

                        if (ch == '<')
                        {
                            returnAngleDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "<";
                            index++;
                            continue;
                        }

                        if (ch == '>' && returnAngleDepth > 0)
                        {
                            returnAngleDepth--;
                            previousReturnToken = ">";
                            index++;
                            continue;
                        }

                        if (ch == '{')
                        {
                            if (returnParenDepth == 0 && returnBracketDepth == 0 && returnAngleDepth == 0 && returnBraceDepth == 0)
                            {
                                if (CanStartJavaScriptTypeScriptReturnTypeObjectLiteral(previousReturnToken))
                                {
                                    returnBraceDepth++;
                                    sawReturnTypeToken = true;
                                    previousReturnToken = "{";
                                    index++;
                                    continue;
                                }

                                if (sawReturnTypeToken)
                                {
                                    returnTypeEndColumn = index - 1;
                                    methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, index, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index);
                                    return JavaScriptTypeScriptMethodHeaderParseStatus.Parsed;
                                }
                            }

                            returnBraceDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "{";
                            index++;
                            continue;
                        }

                        if (ch == '}' && returnBraceDepth > 0)
                        {
                            returnBraceDepth--;
                            previousReturnToken = "}";
                            index++;
                            continue;
                        }

                        if (ch == '?' || ch == ':' || ch == '|' || ch == '&' || ch == ',')
                        {
                            sawReturnTypeToken = true;
                            previousReturnToken = ch.ToString();
                            index++;
                            continue;
                        }

                        if (ch == '=' && index + 1 < sanitizedLine.Length && sanitizedLine[index + 1] == '>')
                        {
                            sawReturnTypeToken = true;
                            previousReturnToken = "=>";
                            index += 2;
                            continue;
                        }

                        if (TrySkipTypeScriptTypeToken(sanitizedLine, ref index, out var typeToken))
                        {
                            sawReturnTypeToken = true;
                            previousReturnToken = typeToken;
                            continue;
                        }

                        sawReturnTypeToken = true;
                        previousReturnToken = ch.ToString();
                        index++;
                    }

                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;
                }

                if (lang == "typescript" && index < sanitizedLine.Length && sanitizedLine[index] == ';')
                {
                    methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, -1, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index, false);
                    return JavaScriptTypeScriptMethodHeaderParseStatus.DeclarationOnly;
                }

                if (index >= sanitizedLine.Length || sanitizedLine[index] != '{')
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, index, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index);
                return JavaScriptTypeScriptMethodHeaderParseStatus.Parsed;
            }
        }

        return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;
    }

    private static bool TryCaptureJavaScriptTypeScriptMethodHeader(
        string[] lines,
        int startIndex,
        int startColumn,
        int scanEndExclusive,
        string firstSanitizedLine,
        JavaScriptLexState nextLineLexState,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture methodCapture)
    {
        methodCapture = default;
        var sourceBuilder = new System.Text.StringBuilder();
        var sanitizedBuilder = new System.Text.StringBuilder();

        // Content was split on '\n', so CRLF lines carry a trailing '\r'. Strip it from both
        // builders in lockstep so inter-line separators stay '\n' regardless of source line
        // endings; the sanitized lex output preserves '\r' at the same column as the source,
        // so dropping it from both keeps column mapping aligned (see #382 / #405).
        // content は '\n' で分割しているため、CRLF 行は末尾に '\r' が残る。sanitized 側も
        // source と同じ列に '\r' を保持するため、両方から一律に '\r' を落とせば column
        // mapping はズレず、行間セパレータも OS に依存せず '\n' に揃う（#382 / #405 参照）。
        var firstSourceSegmentRaw = startColumn < lines[startIndex].Length
            ? lines[startIndex][startColumn..]
            : string.Empty;
        var firstSanitizedSegmentRaw = startColumn < firstSanitizedLine.Length
            ? firstSanitizedLine[startColumn..]
            : string.Empty;
        var firstSourceSegment = StripTrailingCr(firstSourceSegmentRaw);
        var firstSanitizedSegment = StripTrailingCr(firstSanitizedSegmentRaw);
        sourceBuilder.Append(firstSourceSegment);
        sanitizedBuilder.Append(lang == "typescript"
            ? NormalizeTypeScriptBareMethodMatchInput(firstSanitizedSegment)
            : firstSanitizedSegment);

        if (TryFinalizeJavaScriptTypeScriptMethodHeaderCapture(
            sourceBuilder.ToString(),
            sanitizedBuilder.ToString(),
            startIndex,
            startColumn,
            lang,
            out methodCapture))
        {
            return true;
        }

        var lexState = nextLineLexState;
        for (int lineIndex = startIndex + 1; lineIndex < scanEndExclusive; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;

            var sourceLine = StripTrailingCr(lines[lineIndex]);
            var sanitizedLine = StripTrailingCr(lexedLine.SanitizedLine);
            sourceBuilder.Append('\n');
            sourceBuilder.Append(sourceLine);
            sanitizedBuilder.Append('\n');
            sanitizedBuilder.Append(lang == "typescript"
                ? NormalizeTypeScriptBareMethodMatchInput(sanitizedLine)
                : sanitizedLine);

            if (TryFinalizeJavaScriptTypeScriptMethodHeaderCapture(
                sourceBuilder.ToString(),
                sanitizedBuilder.ToString(),
                startIndex,
                startColumn,
                lang,
                out methodCapture))
            {
                return true;
            }
        }

        return false;
    }

    // Multi-line accumulating wrapper for class-field arrow functions. Mirrors
    // TryCaptureJavaScriptTypeScriptMethodHeader: accumulates sanitized/source lines, calls the
    // arrow-header parser on each accumulation step, and maps the sanitized body-open column back
    // to a source (lineIndex, column) pair. Returns a JavaScriptTypeScriptMethodHeaderCapture so
    // the scanner can emit an arrow field symbol with the same machinery as method headers.
    // クラスフィールドのアロー関数に対する複数行蓄積ラッパー。
    // TryCaptureJavaScriptTypeScriptMethodHeader と同じく sanitized/source を行単位で蓄積し、
    // 蓄積ごとにアローヘッダーパーサを呼び、sanitized 上の body 開始列を source の
    // (行, 列) に逆写像する。戻り値は JavaScriptTypeScriptMethodHeaderCapture を使い回すため、
    // 呼び出し元の emit 処理はメソッドヘッダーと同じフローで扱える。
    private static bool TryCaptureJavaScriptTypeScriptClassFieldArrow(
        string[] lines,
        int startIndex,
        int startColumn,
        int scanEndExclusive,
        string firstSanitizedLine,
        JavaScriptLexState nextLineLexState,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture arrowCapture)
    {
        arrowCapture = default;
        var sourceBuilder = new System.Text.StringBuilder();
        var sanitizedBuilder = new System.Text.StringBuilder();

        var firstSourceSegmentRaw = startColumn < lines[startIndex].Length
            ? lines[startIndex][startColumn..]
            : string.Empty;
        var firstSanitizedSegmentRaw = startColumn < firstSanitizedLine.Length
            ? firstSanitizedLine[startColumn..]
            : string.Empty;
        sourceBuilder.Append(StripTrailingCr(firstSourceSegmentRaw));
        sanitizedBuilder.Append(StripTrailingCr(firstSanitizedSegmentRaw));

        if (TryFinalizeJavaScriptTypeScriptClassFieldArrowCapture(
            sourceBuilder.ToString(),
            sanitizedBuilder.ToString(),
            startIndex,
            startColumn,
            lang,
            out arrowCapture))
        {
            return true;
        }

        var lexState = nextLineLexState;
        for (int lineIndex = startIndex + 1; lineIndex < scanEndExclusive; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;

            sourceBuilder.Append('\n');
            sourceBuilder.Append(StripTrailingCr(lines[lineIndex]));
            sanitizedBuilder.Append('\n');
            sanitizedBuilder.Append(StripTrailingCr(lexedLine.SanitizedLine));

            if (TryFinalizeJavaScriptTypeScriptClassFieldArrowCapture(
                sourceBuilder.ToString(),
                sanitizedBuilder.ToString(),
                startIndex,
                startColumn,
                lang,
                out arrowCapture))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFinalizeJavaScriptTypeScriptClassFieldArrowCapture(
        string sourceHeader,
        string sanitizedHeader,
        int startIndex,
        int startColumn,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture arrowCapture)
    {
        arrowCapture = default;
        if (!TryParseJavaScriptTypeScriptClassFieldArrowHeader(sanitizedHeader, 0, lang, out var arrowInfo))
            return false;

        if (!TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
            sourceHeader,
            startIndex,
            startColumn,
            arrowInfo.BodyStartColumn,
            out var bodyStartLineIndex,
            out var bodyStartColumn))
        {
            return false;
        }

        int? bodyEndLineIndex = null;
        int? bodyEndColumn = null;
        if (arrowInfo.ExpressionBodyEndColumn is int expressionEnd)
        {
            if (!TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
                sourceHeader,
                startIndex,
                startColumn,
                expressionEnd,
                out var expressionEndLineIndex,
                out var expressionEndColumn))
            {
                return false;
            }
            bodyEndLineIndex = expressionEndLineIndex;
            bodyEndColumn = expressionEndColumn;
        }

        // For brace-body arrow fields, header end == body start (both point at `{`). For
        // expression-body arrow fields, BodyStartColumn points at the first expression char
        // and BodyEndLineIndex/Column describe the last expression char before `;`.
        // block body 矢印 field は header end と body start が同じ `{` を指す。式本体矢印 field は
        // BodyStartColumn が式の先頭、BodyEndLineIndex/Column が `;` 直前の式末尾を指す。
        arrowCapture = new JavaScriptTypeScriptMethodHeaderCapture(
            sourceHeader,
            arrowInfo,
            bodyStartLineIndex,
            bodyStartColumn,
            bodyStartLineIndex,
            bodyStartColumn,
            bodyEndLineIndex,
            bodyEndColumn);
        return true;
    }

    private static bool TryFinalizeJavaScriptTypeScriptMethodHeaderCapture(
        string sourceHeader,
        string sanitizedHeader,
        int startIndex,
        int startColumn,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture methodCapture)
    {
        methodCapture = default;
        var parseStatus = ParseJavaScriptTypeScriptMethodHeader(sanitizedHeader, 0, lang, out var methodHeader);
        if (parseStatus == JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid)
            return false;

        var headerEndLocationColumn = methodHeader.HasBody
            ? methodHeader.BodyStartColumn
            : methodHeader.HeaderEndColumn ?? -1;
        if (!TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
            sourceHeader,
            startIndex,
            startColumn,
            headerEndLocationColumn,
            out var headerEndLineIndex,
            out var headerEndColumn))
        {
            return false;
        }

        var bodyStartLineIndex = -1;
        var bodyStartColumn = -1;
        if (methodHeader.HasBody && !TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
            sourceHeader,
            startIndex,
            startColumn,
            methodHeader.BodyStartColumn,
            out bodyStartLineIndex,
            out bodyStartColumn))
        {
            return false;
        }

        methodCapture = new JavaScriptTypeScriptMethodHeaderCapture(
            sourceHeader,
            methodHeader,
            headerEndLineIndex,
            headerEndColumn,
            bodyStartLineIndex,
            bodyStartColumn);
        return true;
    }

    private static bool TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
        string sourceHeader,
        int startIndex,
        int startColumn,
        int headerColumn,
        out int lineIndex,
        out int column)
    {
        lineIndex = startIndex;
        column = startColumn;
        if (headerColumn < 0 || headerColumn >= sourceHeader.Length)
            return false;

        for (int i = 0; i < headerColumn; i++)
        {
            if (sourceHeader[i] == '\n')
            {
                lineIndex++;
                column = 0;
            }
            else
            {
                column++;
            }
        }

        return true;
    }

    private static string BuildJavaScriptTypeScriptBareMethodSignature(
        string[] lines,
        int startIndex,
        int startColumn,
        int? bodyEndLine,
        int sameLineMethodEndColumn,
        JavaScriptTypeScriptMethodHeaderCapture methodCapture,
        string? lang)
    {
        if (!methodCapture.HeaderInfo.HasBody)
        {
            if (methodCapture.HeaderEndLineIndex == startIndex && methodCapture.HeaderEndColumn >= startColumn)
                return lines[startIndex][startColumn..(methodCapture.HeaderEndColumn + 1)].Trim();

            if (methodCapture.HeaderInfo.HeaderEndColumn != null
                && methodCapture.HeaderInfo.HeaderEndColumn.Value >= 0
                && methodCapture.HeaderInfo.HeaderEndColumn.Value < methodCapture.SourceHeader.Length)
            {
                return methodCapture.SourceHeader[..(methodCapture.HeaderInfo.HeaderEndColumn.Value + 1)].Trim();
            }

            return methodCapture.SourceHeader.Trim();
        }

        if (bodyEndLine == startIndex + 1 && sameLineMethodEndColumn >= startColumn)
            return lines[startIndex][startColumn..(sameLineMethodEndColumn + 1)].Trim();

        if (methodCapture.HeaderInfo.BodyStartColumn < 0
            || methodCapture.HeaderInfo.BodyStartColumn >= methodCapture.SourceHeader.Length)
        {
            return methodCapture.SourceHeader.Trim();
        }

        return methodCapture.SourceHeader[..(methodCapture.HeaderInfo.BodyStartColumn + 1)].Trim();
    }

    // Build a signature string for a class-field arrow function. Same shape as the method-header
    // signature builder (same-line bodies quote the source slice verbatim, multi-line bodies stop
    // at the '{' that opens the block body).
    // クラスフィールドのアロー関数向けのシグネチャ文字列を組み立てる。メソッドヘッダー版と同じ方針で、
    // 同一行 body は source をそのまま切り出し、複数行 body はブロック本体を開く '{' まで切り出す。
    private static string BuildJavaScriptTypeScriptClassFieldArrowSignature(
        string[] lines,
        int startIndex,
        int startColumn,
        int? bodyEndLine,
        int sameLineArrowEndColumn,
        JavaScriptTypeScriptMethodHeaderCapture arrowCapture)
    {
        if (bodyEndLine == startIndex + 1 && sameLineArrowEndColumn >= startColumn)
            return lines[startIndex][startColumn..(sameLineArrowEndColumn + 1)].Trim();

        // For expression-body arrow fields that span multiple lines, include the full source up
        // to and including the last expression char (before `;`) so the signature reflects the
        // whole `name = (args) => expr` shape.
        // 複数行にわたる式本体矢印 field では、`;` 直前の式末尾までをシグネチャに含めて
        // `name = (args) => expr` 全体が見えるようにする。
        if (arrowCapture.HeaderInfo.ExpressionBodyEndColumn is int expressionEnd
            && expressionEnd >= 0
            && expressionEnd + 1 <= arrowCapture.SourceHeader.Length)
        {
            return arrowCapture.SourceHeader[..(expressionEnd + 1)].Trim();
        }

        if (arrowCapture.HeaderInfo.BodyStartColumn < 0
            || arrowCapture.HeaderInfo.BodyStartColumn >= arrowCapture.SourceHeader.Length)
        {
            return arrowCapture.SourceHeader.Trim();
        }

        return arrowCapture.SourceHeader[..(arrowCapture.HeaderInfo.BodyStartColumn + 1)].Trim();
    }

    private static string? GetJavaScriptTypeScriptBareMethodReturnType(string sourceHeader, JavaScriptTypeScriptMethodHeaderInfo methodHeader, string? lang)
    {
        if (lang != "typescript"
            || methodHeader.ReturnTypeStartColumn == null
            || methodHeader.ReturnTypeEndColumn == null)
            return null;

        var returnTypeStartColumn = methodHeader.ReturnTypeStartColumn.Value + 1;
        var returnTypeEndColumn = methodHeader.ReturnTypeEndColumn.Value;
        if (returnTypeEndColumn < returnTypeStartColumn || returnTypeEndColumn >= sourceHeader.Length)
            return null;

        return NormalizeMetadata(sourceHeader[returnTypeStartColumn..(returnTypeEndColumn + 1)]);
    }

    private static bool TryGetJavaScriptTypeScriptNextToken(
        string[] lines,
        int startIndex,
        int startColumn,
        bool skipWrappingParens,
        out int tokenLineIndex,
        out int tokenStartColumn,
        out string? token)
    {
        tokenLineIndex = -1;
        tokenStartColumn = -1;
        token = null;

        var lexState = new JavaScriptLexState();
        for (int lineIndex = startIndex; lineIndex < lines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var column = lineIndex == startIndex ? startColumn : 0;

            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (skipWrappingParens && ch == '(')
                {
                    column++;
                    continue;
                }

                if (!IsJavaScriptTypeScriptIdentifierStart(ch))
                    return false;

                var tokenStart = column;
                column++;
                while (column < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[column]))
                    column++;

                tokenLineIndex = lineIndex;
                tokenStartColumn = tokenStart;
                token = sanitizedLine[tokenStart..column];
                return true;
            }
        }

        return false;
    }

    private static bool IsJavaScriptTypeScriptIdentifierStart(char ch) =>
        char.IsLetter(ch) || ch == '_' || ch == '$';

    private static bool IsJavaScriptTypeScriptIdentifierPart(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_' || ch == '$';

    private static bool TrySkipTypeScriptTypeToken(string sanitizedLine, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= sanitizedLine.Length)
            return false;

        return TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref index, out token)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref index, out token)
            || TryReadJavaScriptTypeScriptIdentifierToken(sanitizedLine, ref index, out token);
    }

    private static bool TryReadJavaScriptTypeScriptIdentifierToken(string input, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= input.Length || !IsJavaScriptTypeScriptIdentifierStart(input[index]))
            return false;

        var tokenStart = index;
        index++;
        while (index < input.Length && IsJavaScriptTypeScriptIdentifierPart(input[index]))
            index++;

        token = input[tokenStart..index];
        return true;
    }

    private static bool TryReadJavaScriptTypeScriptQuotedLiteralToken(string input, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= input.Length || input[index] is not ('\'' or '"' or '`'))
            return false;

        var probe = index;
        var delimiter = input[probe];
        var tokenStart = probe;
        var escapeNext = false;
        probe++;
        while (probe < input.Length)
        {
            var ch = input[probe];
            if (escapeNext)
            {
                escapeNext = false;
                probe++;
                continue;
            }

            if (ch == '\\')
            {
                escapeNext = true;
                probe++;
                continue;
            }

            if (ch == delimiter)
            {
                probe++;
                index = probe;
                token = input[tokenStart..index];
                return true;
            }

            probe++;
        }

        return false;
    }

    private static bool TryReadJavaScriptTypeScriptNumericLiteralToken(string input, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= input.Length || !char.IsDigit(input[index]))
            return false;

        var tokenStart = index;
        if (input[index] == '0' && index + 1 < input.Length && input[index + 1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
        {
            index += 2;
            while (index < input.Length && IsJavaScriptTypeScriptNumericLiteralPart(input[index], allowDecimalPoint: false))
                index++;
        }
        else
        {
            while (index < input.Length && IsJavaScriptTypeScriptNumericLiteralPart(input[index], allowDecimalPoint: true))
                index++;
        }

        token = input[tokenStart..index];
        return true;
    }

    private static bool IsJavaScriptTypeScriptNumericLiteralPart(char ch, bool allowDecimalPoint)
    {
        if (char.IsLetterOrDigit(ch) || ch == '_')
            return true;

        return allowDecimalPoint && ch == '.';
    }

    private static bool TryReadJavaScriptTypeScriptMethodToken(string sanitizedLine, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= sanitizedLine.Length)
            return false;

        if (sanitizedLine[index] == '*')
        {
            token = "*";
            index++;
            return true;
        }

        return TryReadJavaScriptTypeScriptMethodName(sanitizedLine, ref index, out token);
    }

    private static bool TryReadJavaScriptTypeScriptMethodName(string sanitizedLine, ref int index, out string name)
    {
        name = string.Empty;
        if (index >= sanitizedLine.Length)
            return false;

        var tokenStart = index;
        if (TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref index, out name)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref index, out name))
        {
            return true;
        }

        if (sanitizedLine[index] == '[')
        {
            var bracketDepth = 0;
            while (index < sanitizedLine.Length)
            {
                if (sanitizedLine[index] == '[')
                    bracketDepth++;
                else if (sanitizedLine[index] == ']')
                {
                    bracketDepth--;
                    if (bracketDepth == 0)
                    {
                        index++;
                        name = sanitizedLine[tokenStart..index];
                        return true;
                    }
                }

                index++;
            }

            return false;
        }

        if (sanitizedLine[index] == '#')
        {
            index++;
            if (index >= sanitizedLine.Length || !IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[index]))
                return false;
        }
        else if (!IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[index]))
        {
            return false;
        }

        index++;
        while (index < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index]))
            index++;

        name = sanitizedLine[tokenStart..index];
        return true;
    }

    private static bool TrySkipJavaScriptTypeScriptDecorators(string line, ref int index)
    {
        var skippedAny = false;

        while (index < line.Length)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (index >= line.Length || line[index] != '@')
                return skippedAny;

            skippedAny = true;
            index++;

            var parenDepth = 0;
            var bracketDepth = 0;
            var braceDepth = 0;

            while (index < line.Length)
            {
                var ch = line[index];
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && char.IsWhiteSpace(ch))
                    break;

                if (ch == '(')
                    parenDepth++;
                else if (ch == ')' && parenDepth > 0)
                    parenDepth--;
                else if (ch == '[')
                    bracketDepth++;
                else if (ch == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (ch == '{')
                    braceDepth++;
                else if (ch == '}' && braceDepth > 0)
                    braceDepth--;

                index++;
            }
        }

        return skippedAny;
    }

    private static string? GetJavaScriptTypeScriptMethodNameFromSource(string line, int startColumn)
    {
        var index = Math.Max(0, startColumn);
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        TrySkipJavaScriptTypeScriptDecorators(line, ref index);

        while (index < line.Length)
        {
            if (!TryReadJavaScriptTypeScriptSourceMethodToken(line, ref index, out var token))
                return null;

            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (TypeScriptBareMethodModifiers.Contains(token)
                && CanTreatJavaScriptTypeScriptMethodTokenAsModifier(line, index))
            {
                continue;
            }

            var isGenerator = token == "*";
            if (!isGenerator && index < line.Length && line[index] == '*')
            {
                isGenerator = true;
                index++;
                while (index < line.Length && char.IsWhiteSpace(line[index]))
                    index++;
            }

            if (isGenerator)
                return TryReadJavaScriptTypeScriptSourceMethodName(line, ref index, out var generatorName) ? generatorName : null;

            return token;
        }

        return null;
    }

    private static bool TryReadJavaScriptTypeScriptSourceMethodToken(string line, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= line.Length)
            return false;

        if (line[index] == '*')
        {
            token = "*";
            index++;
            return true;
        }

        return TryReadJavaScriptTypeScriptSourceMethodName(line, ref index, out token);
    }

    private static bool TryReadJavaScriptTypeScriptSourceQuotedLiteralToken(string line, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= line.Length || line[index] is not ('\'' or '"' or '`'))
            return false;

        var delimiter = line[index];
        var tokenStart = index;
        var escapeNext = false;
        index++;
        while (index < line.Length)
        {
            var ch = line[index];
            if (escapeNext)
            {
                escapeNext = false;
                index++;
                continue;
            }

            if (ch == '\\')
            {
                escapeNext = true;
                index++;
                continue;
            }

            if (ch == delimiter)
            {
                index++;
                token = line[tokenStart..index];
                return true;
            }

            index++;
        }

        return false;
    }

    private static bool TryReadJavaScriptTypeScriptSourceMethodName(string line, ref int index, out string name)
    {
        name = string.Empty;
        if (index >= line.Length)
            return false;

        var tokenStart = index;
        if (TryReadJavaScriptTypeScriptSourceQuotedLiteralToken(line, ref index, out name)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(line, ref index, out name))
        {
            return true;
        }

        if (line[index] == '[')
        {
            var bracketDepth = 0;
            var inSingleQuote = false;
            var inDoubleQuote = false;
            var inTemplateString = false;
            var escapeNext = false;
            while (index < line.Length)
            {
                var ch = line[index];
                if (escapeNext)
                {
                    escapeNext = false;
                    index++;
                    continue;
                }

                if (inSingleQuote)
                {
                    if (ch == '\\')
                        escapeNext = true;
                    else if (ch == '\'')
                        inSingleQuote = false;
                    index++;
                    continue;
                }

                if (inDoubleQuote)
                {
                    if (ch == '\\')
                        escapeNext = true;
                    else if (ch == '"')
                        inDoubleQuote = false;
                    index++;
                    continue;
                }

                if (inTemplateString)
                {
                    if (ch == '\\')
                        escapeNext = true;
                    else if (ch == '`')
                        inTemplateString = false;
                    index++;
                    continue;
                }

                if (ch == '\'')
                {
                    inSingleQuote = true;
                    index++;
                    continue;
                }

                if (ch == '"')
                {
                    inDoubleQuote = true;
                    index++;
                    continue;
                }

                if (ch == '`')
                {
                    inTemplateString = true;
                    index++;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                }
                else if (ch == ']')
                {
                    bracketDepth--;
                    if (bracketDepth == 0)
                    {
                        index++;
                        name = line[tokenStart..index];
                        return true;
                    }
                }

                index++;
            }

            return false;
        }

        if (line[index] == '#')
        {
            index++;
            if (index >= line.Length || !IsJavaScriptTypeScriptIdentifierStart(line[index]))
                return false;
        }
        else if (!IsJavaScriptTypeScriptIdentifierStart(line[index]))
        {
            return false;
        }

        index++;
        while (index < line.Length && IsJavaScriptTypeScriptIdentifierPart(line[index]))
            index++;

        name = line[tokenStart..index];
        return true;
    }

    private static int FindNextJavaScriptTypeScriptTokenStart(string sanitizedLine, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < sanitizedLine.Length)
        {
            if (!IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[index]))
            {
                index++;
                continue;
            }

            if (index > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index - 1]))
            {
                index++;
                continue;
            }

            return index;
        }

        return -1;
    }

    private static int FindNextJavaScriptTypeScriptMethodCandidateStart(string sanitizedLine, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < sanitizedLine.Length)
        {
            var ch = sanitizedLine[index];
            if (ch != '#'
                && ch != '@'
                && ch != '*'
                && ch != '['
                && ch != '\''
                && ch != '"'
                && !char.IsDigit(ch)
                && !IsJavaScriptTypeScriptIdentifierStart(ch))
            {
                index++;
                continue;
            }

            if (index > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index - 1]))
            {
                index++;
                continue;
            }

            return index;
        }

        return -1;
    }

    private static int FindNextJavaScriptTypeScriptStatementStart(string sanitizedLine, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < sanitizedLine.Length)
        {
            index = FindNextJavaScriptTypeScriptTokenStart(sanitizedLine, index);
            if (index < 0)
                return -1;

            var previous = index - 1;
            while (previous >= 0 && char.IsWhiteSpace(sanitizedLine[previous]))
                previous--;

            if (previous < 0 || sanitizedLine[previous] is ';' or '{' or '}')
                return index;

            index++;
        }

        return -1;
    }

    private static bool CanTreatJavaScriptTypeScriptMethodTokenAsModifier(string sanitizedLine, int index)
    {
        var lookahead = index;
        while (lookahead < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[lookahead]))
            lookahead++;

        if (lookahead >= sanitizedLine.Length)
            return false;

        var ch = sanitizedLine[lookahead];
        if (ch is '(' or '<')
            return false;

        if (ch == '*')
        {
            lookahead++;
            while (lookahead < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[lookahead]))
                lookahead++;

            if (lookahead >= sanitizedLine.Length)
                return false;

            return sanitizedLine[lookahead] is '[' or '#'
                || IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[lookahead]);
        }

        return ch is '[' or '#'
            || IsJavaScriptTypeScriptIdentifierStart(ch);
    }

    private static bool CanStartJavaScriptTypeScriptReturnTypeObjectLiteral(string? previousReturnToken)
    {
        return previousReturnToken is ":" or "?" or "|" or "&" or "," or "(" or "[" or "=>" or "extends";
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindIndentRange(string[] lines, int startIndex)
    {
        var currentLine = lines[startIndex];
        var currentIndent = CountIndent(currentLine);
        var trimmedCurrent = currentLine.Trim();

        if (trimmedCurrent.Contains(':'))
        {
            var suffix = trimmedCurrent[(trimmedCurrent.IndexOf(':') + 1)..].Trim();
            if (suffix.Length > 0 && !suffix.StartsWith('#'))
                return (startIndex + 1, startIndex + 1, startIndex + 1);
        }

        int? bodyStartLine = null;
        int endLine = startIndex + 1;

        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            var indent = CountIndent(lines[i]);
            if (bodyStartLine == null)
            {
                if (indent <= currentIndent)
                    return (endLine, null, null);

                bodyStartLine = i + 1;
                endLine = i + 1;
                continue;
            }

            if (indent <= currentIndent)
                return (endLine, bodyStartLine, endLine);

            endLine = i + 1;
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (endLine, bodyStartLine, endLine);
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindRubyRange(string[] lines, int startIndex)
    {
        var firstLine = lines[startIndex];
        if (!RubyBlockStartRegex.IsMatch(firstLine))
            return (startIndex + 1, null, null);

        int depth = 0;
        int? bodyStartLine = null;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            if (i > startIndex && bodyStartLine == null)
                bodyStartLine = i + 1;

            foreach (Match token in RubyBlockTokenRegex.Matches(trimmed))
            {
                if (token.Value == "end")
                    depth--;
                else
                    depth++;
            }

            if (depth <= 0)
            {
                if (bodyStartLine == null || bodyStartLine > i + 1)
                    return (i + 1, null, null);

                return (i + 1, bodyStartLine, i + 1);
            }
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (lines.Length, bodyStartLine, lines.Length);
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindElixirRange(string[] lines, int startIndex)
    {
        var firstLine = lines[startIndex];
        if (!ElixirBlockStartRegex.IsMatch(firstLine))
            return (startIndex + 1, null, null);

        var scanState = default(ElixirMaskState);
        var maskedFirstLine = MaskElixirLineForBodyScan(firstLine, ref scanState);
        if (ElixirDoShorthandRegex.IsMatch(maskedFirstLine))
            return (startIndex + 1, startIndex + 1, startIndex + 1);

        var openerMatch = ElixirBlockTokenRegex.Match(maskedFirstLine);
        if (!openerMatch.Success || openerMatch.Value != "do")
            return (startIndex + 1, null, null);

        var depth = 1;
        int? bodyStartLine = null;

        var firstLineTail = maskedFirstLine[(openerMatch.Index + openerMatch.Length)..];
        if (!string.IsNullOrWhiteSpace(firstLineTail))
            bodyStartLine = startIndex + 1;

        foreach (Match token in ElixirBlockTokenRegex.Matches(firstLineTail))
        {
            if (token.Value == "end")
                depth--;
            else
                depth++;

            if (depth == 0)
                return (startIndex + 1, bodyStartLine ?? startIndex + 1, startIndex + 1);
        }

        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            var masked = MaskElixirLineForBodyScan(lines[i], ref scanState);
            if (string.IsNullOrWhiteSpace(masked))
                continue;

            bodyStartLine ??= i + 1;

            foreach (Match token in ElixirBlockTokenRegex.Matches(masked))
            {
                if (token.Value == "end")
                    depth--;
                else
                    depth++;

                if (depth == 0)
                    return (i + 1, bodyStartLine, i + 1);
            }
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (lines.Length, bodyStartLine, lines.Length);
    }

    private enum ElixirMaskMode
    {
        Normal,
        DoubleQuote,
        SingleQuote,
        TripleDoubleQuote,
        TripleSingleQuote,
        Sigil,
    }

    private struct ElixirMaskState
    {
        public ElixirMaskMode Mode;
        public char SigilOpen;
        public char SigilClose;
        public int SigilDepth;
    }

    private static string MaskElixirLineForBodyScan(string line, ref ElixirMaskState state)
    {
        if (line.Length == 0)
            return line;

        var chars = line.ToCharArray();

        for (int i = 0; i < chars.Length; i++)
        {
            var current = chars[i];

            switch (state.Mode)
            {
                case ElixirMaskMode.Normal:
                    if (current == '#')
                    {
                        for (int j = i; j < chars.Length; j++)
                            chars[j] = ' ';
                        return new string(chars);
                    }

                    if (current == '"' || current == '\'')
                    {
                        bool triple = i + 2 < chars.Length && chars[i + 1] == current && chars[i + 2] == current;
                        if (triple)
                        {
                            chars[i] = chars[i + 1] = chars[i + 2] = ' ';
                            state.Mode = current == '"' ? ElixirMaskMode.TripleDoubleQuote : ElixirMaskMode.TripleSingleQuote;
                            i += 2;
                        }
                        else
                        {
                            chars[i] = ' ';
                            state.Mode = current == '"' ? ElixirMaskMode.DoubleQuote : ElixirMaskMode.SingleQuote;
                        }
                        break;
                    }

                    if (current == '~' && i + 2 < chars.Length && char.IsLetter(chars[i + 1]))
                    {
                        var sigilOpen = chars[i + 2];
                        if (TryGetElixirSigilClose(sigilOpen, out var sigilClose, out var nested))
                        {
                            chars[i] = chars[i + 1] = chars[i + 2] = ' ';
                            state.Mode = ElixirMaskMode.Sigil;
                            state.SigilOpen = sigilOpen;
                            state.SigilClose = sigilClose;
                            state.SigilDepth = nested ? 1 : 0;
                            i += 2;
                        }
                    }
                    break;

                case ElixirMaskMode.DoubleQuote:
                    chars[i] = ' ';
                    if (current == '\\' && i + 1 < chars.Length)
                    {
                        chars[++i] = ' ';
                        continue;
                    }

                    if (current == '"')
                        state.Mode = ElixirMaskMode.Normal;
                    break;

                case ElixirMaskMode.SingleQuote:
                    chars[i] = ' ';
                    if (current == '\\' && i + 1 < chars.Length)
                    {
                        chars[++i] = ' ';
                        continue;
                    }

                    if (current == '\'')
                        state.Mode = ElixirMaskMode.Normal;
                    break;

                case ElixirMaskMode.TripleDoubleQuote:
                    chars[i] = ' ';
                    if (current == '"' && i + 2 < chars.Length && chars[i + 1] == '"' && chars[i + 2] == '"')
                    {
                        chars[i] = chars[i + 1] = chars[i + 2] = ' ';
                        state.Mode = ElixirMaskMode.Normal;
                        i += 2;
                    }
                    break;

                case ElixirMaskMode.TripleSingleQuote:
                    chars[i] = ' ';
                    if (current == '\'' && i + 2 < chars.Length && chars[i + 1] == '\'' && chars[i + 2] == '\'')
                    {
                        chars[i] = chars[i + 1] = chars[i + 2] = ' ';
                        state.Mode = ElixirMaskMode.Normal;
                        i += 2;
                    }
                    break;

                case ElixirMaskMode.Sigil:
                    chars[i] = ' ';
                    if (current == '\\' && i + 1 < chars.Length)
                    {
                        chars[++i] = ' ';
                        continue;
                    }

                    if (state.SigilDepth > 0)
                    {
                        if (current == state.SigilOpen)
                            state.SigilDepth++;
                        else if (current == state.SigilClose)
                        {
                            state.SigilDepth--;
                            if (state.SigilDepth == 0)
                                state.Mode = ElixirMaskMode.Normal;
                        }
                    }
                    else if (current == state.SigilClose)
                    {
                        state.Mode = ElixirMaskMode.Normal;
                    }
                    break;
            }
        }

        return new string(chars);
    }

    private static bool TryGetElixirSigilClose(char open, out char close, out bool nested)
    {
        nested = true;
        close = open;
        return open switch
        {
            '(' => SetSigilClose(')', true, out close, out nested),
            '[' => SetSigilClose(']', true, out close, out nested),
            '{' => SetSigilClose('}', true, out close, out nested),
            '<' => SetSigilClose('>', true, out close, out nested),
            '/' => SetSigilClose('/', false, out close, out nested),
            '|' => SetSigilClose('|', false, out close, out nested),
            '"' => SetSigilClose('"', false, out close, out nested),
            '\'' => SetSigilClose('\'', false, out close, out nested),
            _ => false,
        };
    }

    private static bool SetSigilClose(char sigilClose, bool isNested, out char close, out bool nested)
    {
        close = sigilClose;
        nested = isNested;
        return true;
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindVisualBasicRange(string[] lines, int startIndex)
    {
        int depth = 0;
        int? bodyStartLine = null;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            if (VisualBasicContainerStartRegex.IsMatch(trimmed))
            {
                depth++;
                if (i > startIndex && bodyStartLine == null)
                    bodyStartLine = i + 1;
                continue;
            }

            if (VisualBasicContainerEndRegex.IsMatch(trimmed))
            {
                depth--;
                if (depth <= 0)
                {
                    if (bodyStartLine == null || bodyStartLine > i + 1)
                        return (i + 1, null, null);

                    return (i + 1, bodyStartLine, i + 1);
                }
            }
            else if (i > startIndex && bodyStartLine == null)
            {
                bodyStartLine = i + 1;
            }
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (lines.Length, bodyStartLine, lines.Length);
    }

    // Regex helpers for SQL procedure body scanning / SQL プロシージャ本体走査用の正規表現ヘルパー
    private static readonly Regex SqlGoSeparatorRegex = new(
        @"^\s*GO\s*(?:;[\s;]*)?(?:--.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Only close a SQL proc body when the next top-level statement looks like another proc-like
    // header (`CREATE|ALTER|DROP PROCEDURE|PROC|FUNCTION|TRIGGER`, optionally with `OR REPLACE` for
    // PostgreSQL or `OR ALTER` for T-SQL / SQL Server 2016+). Body-internal `CREATE TABLE` /
    // `ALTER TABLE` / `GRANT` / `USE` etc. must not prematurely close the enclosing procedure body.
    // The `OR REPLACE` / `OR ALTER` alternation must match the CREATE-side symbol regex above so a
    // `CREATE OR ALTER PROCEDURE` sibling actually terminates the previous body range. See issue #429.
    // 次のトップレベル文が別の proc 系ヘッダ（`CREATE|ALTER|DROP` + `PROCEDURE|PROC|FUNCTION|TRIGGER`、
    // PostgreSQL の `OR REPLACE` / T-SQL・SQL Server 2016+ の `OR ALTER` 付きも許容）だった場合のみ
    // SQL の proc 本体を閉じる。本体内の `CREATE TABLE` / `ALTER TABLE` / `GRANT` / `USE` などで
    // 先走って閉じないこと。`OR REPLACE` / `OR ALTER` の分岐は上の CREATE 側シンボル正規表現と揃え、
    // `CREATE OR ALTER PROCEDURE` の隣接宣言でも前の body 範囲を確実に終端させる。issue #429 参照。
    private static readonly Regex SqlTopLevelDdlStartRegex = new(
        @"^\s*(?:CREATE|ALTER|DROP)\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:PROCEDURE|PROC|FUNCTION|TRIGGER)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Dollar-quoted body tags: `$$` or `$tagname$` (PostgreSQL). Tag must be empty or an identifier.
    // Dollar-quoted の本体タグ: `$$` または `$タグ名$`（PostgreSQL）。タグは空か識別子のみ。
    private static readonly Regex SqlDollarTagRegex = new(
        @"\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$",
        RegexOptions.Compiled);

    /// <summary>
    /// Resolve the body range of a SQL `CREATE|ALTER PROCEDURE|FUNCTION|TRIGGER` symbol.
    /// Closes the body at: balanced dollar-quoted (`$$ ... $$`), `GO` batch separator at line start,
    /// a new top-level DDL statement (`CREATE`/`ALTER`/`DROP`/...) at line start, or end-of-file.
    /// Multi-line scanning respects SQL string literals (`'...'` / `"..."`) and line/block comments
    /// so terminators embedded in comments or strings do not prematurely close the body.
    /// Body boundaries are best-effort — they only need to contain calls inside the procedure for
    /// ReferenceExtractor's container attribution, not reconstruct the exact parser-level body.
    /// See issue #429.
    /// SQL の `CREATE|ALTER PROCEDURE|FUNCTION|TRIGGER` シンボルの本体範囲を求める。
    /// 本体は、`$$ ... $$` 等のドル引用の閉じ、行頭の `GO` バッチ区切り、行頭の新たなトップレベル DDL
    /// （`CREATE`/`ALTER`/`DROP`/...）、または EOF で閉じる。文字列リテラル（`'...'` / `"..."`）と
    /// 行/ブロックコメントは尊重するため、これらの中に入った終端語で誤って閉じない。
    /// 本体境界は ReferenceExtractor のコンテナ帰属のためにプロシージャ内部の呼び出しを包含できれば
    /// 十分で、パーサレベルの正確な本体を再構築する必要はない。issue #429 参照。
    /// </summary>
    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindSqlProcBodyRange(string[] lines, int startIndex)
    {
        int bodyStartLine = startIndex + 1;
        int endLine = startIndex + 1;
        string? openDollarTag = null;
        int blockCommentDepth = 0;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var raw = lines[i];

            if (openDollarTag != null)
            {
                // Inside a dollar-quoted body; look for the matching close tag on any column.
                // ドル引用ボディ内。どの列にあっても閉じタグを探す。
                if (raw.IndexOf(openDollarTag, StringComparison.Ordinal) >= 0)
                {
                    openDollarTag = null;
                    endLine = i + 1;
                    return (endLine, bodyStartLine, endLine);
                }
                endLine = i + 1;
                continue;
            }

            var masked = MaskSqlLineForBodyScan(raw, ref blockCommentDepth);

            // Detect any unpaired dollar-quote opening on this line. Paired openings on the same
            // line (e.g. `AS $$ SELECT 1 $$`) are consumed without opening cross-line state.
            // 同一行でペアにならない dollar-quote 開きを検出する。同じ行で開閉が揃う（`AS $$ SELECT 1 $$`）
            // 場合はクロス行状態を開かずそのまま消費する。
            var dollarMatches = SqlDollarTagRegex.Matches(masked);
            if (dollarMatches.Count > 0)
            {
                var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Match m in dollarMatches)
                    tagCounts[m.Value] = tagCounts.TryGetValue(m.Value, out var c) ? c + 1 : 1;

                string? stillOpen = null;
                foreach (var kv in tagCounts)
                {
                    if (kv.Value % 2 != 0)
                    {
                        stillOpen = kv.Key;
                        break;
                    }
                }

                if (stillOpen != null)
                {
                    openDollarTag = stillOpen;
                    endLine = i + 1;
                    continue;
                }
            }

            if (i > startIndex)
            {
                // Use `masked` for GO detection too so a bare `GO` appearing inside a multi-line
                // block comment does not prematurely close the body (the mask blanks out
                // comment-interior content). See issue #429 follow-up.
                // `GO` 判定にも `masked` を使い、複数行ブロックコメント内の `GO` 単独行で本体を
                // 早期終了させない（マスクでコメント内部は空白化される）。issue #429 追補参照。
                if (SqlGoSeparatorRegex.IsMatch(masked))
                {
                    // `GO` is a T-SQL batch separator that is not part of the body; close at the
                    // previous line so the `GO` line itself is outside the procedure.
                    // `GO` は本体の一部ではない T-SQL のバッチ区切り。前行で本体を閉じ、`GO` 行自体は
                    // プロシージャの外に置く。
                    return (i, bodyStartLine, i);
                }

                if (SqlTopLevelDdlStartRegex.IsMatch(masked))
                {
                    // A new top-level DDL statement on the next line always closes the previous
                    // procedure's body (even without `GO`).
                    // 次の行に新しいトップレベル DDL が来たら、`GO` が無くても前のプロシージャ本体は
                    // ここで閉じる。
                    return (i, bodyStartLine, i);
                }
            }

            endLine = i + 1;
        }

        return (endLine, bodyStartLine, endLine);
    }

    /// <summary>
    /// Strip SQL line comments (`--`), block comments (`/* ... */`, including multi-line and
    /// PostgreSQL-style nested `/* /* ... */ ... */`), and string literals (`'...'` / `"..."`)
    /// from a single line so body-terminator checks do not trip on text inside comments or
    /// strings. Bracket identifiers (`[name]`) and backtick identifiers (`` `name` ``) are left
    /// untouched since they never contain SQL tokens.
    /// `blockCommentDepth` is threaded across lines by the caller so a `/* ... */` block opened on
    /// one line continues to mask terminators like bare `GO` / `CREATE` appearing on subsequent
    /// lines until all open `/*` are balanced. PostgreSQL allows nested block comments; T-SQL /
    /// MySQL / Oracle do not, but using depth here is strictly safer for the dialects that do not
    /// (every outer `*/` still closes when depth returns to 0). See issue #429 follow-up.
    /// SQL の行コメント（`--`）、ブロックコメント（`/* ... */`、複数行にまたがるものと PostgreSQL 風の
    /// ネスト `/* /* ... */ ... */` を含む）、および文字列リテラル（`'...'` / `"..."`）を除去して、
    /// コメントや文字列中の語で本体終端が誤検出されないようにする。角括弧識別子 `[name]` と
    /// バッククォート識別子 `` `name` `` はそのまま残す（SQL トークンを含まないため）。
    /// `blockCommentDepth` は呼び出し側で行間に持ち越し、ある行で開いた `/* ... */` がすべての `/*`
    /// と均衡するまで、後続行の `GO` / `CREATE` のような終端語をマスクし続ける。PostgreSQL は
    /// ブロックコメントのネストを許容する一方、T-SQL / MySQL / Oracle は許容しないが、ここで depth を
    /// 使っても後者では単に外側の `*/` で depth が 0 に戻って閉じるだけなので、厳密に safer。
    /// issue #429 追補参照。
    /// </summary>
    private static string MaskSqlLineForBodyScan(string line, ref int blockCommentDepth)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        var sb = new StringBuilder(line.Length);
        bool inSingle = false;
        bool inDouble = false;
        int i = 0;

        while (i < line.Length)
        {
            if (blockCommentDepth > 0)
            {
                // Inside a (possibly nested) block comment. Look for the next `/*` (increases depth)
                // or `*/` (decreases depth), whichever comes first. Blank every column until we
                // either close back to depth 0 or hit end of line.
                // ブロックコメント内（ネスト可）。次に来る `/*`（深さ増）か `*/`（深さ減）のうち早い方を探し、
                // depth が 0 に戻るか行末に到達するまで各列を空白化する。
                int open = line.IndexOf("/*", i, StringComparison.Ordinal);
                int close = line.IndexOf("*/", i, StringComparison.Ordinal);

                if (close < 0 && open < 0)
                {
                    for (int k = i; k < line.Length; k++)
                        sb.Append(' ');
                    return sb.ToString();
                }

                if (close >= 0 && (open < 0 || close < open))
                {
                    for (int k = i; k <= close + 1; k++)
                        sb.Append(' ');
                    blockCommentDepth--;
                    i = close + 2;
                    continue;
                }

                // `/*` comes first (or is tied — but `close < open` already handles the tie in favor
                // of `*/`, so here we know `open <= close` strictly and `/*` is the next token).
                // `/*` が先に現れる（`close < open` の場合は close 優先で既に分岐済みなので、ここでは
                // `open <= close` かつ `/*` が次のトークン）。
                for (int k = i; k <= open + 1; k++)
                    sb.Append(' ');
                blockCommentDepth++;
                i = open + 2;
                continue;
            }

            char c = line[i];

            if (!inSingle && !inDouble)
            {
                if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
                {
                    for (int k = i; k < line.Length; k++)
                        sb.Append(' ');
                    return sb.ToString();
                }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    // Open a block comment; let the `blockCommentDepth > 0` branch close it on this
                    // or a later line.
                    // ブロックコメントを開始する。閉じ処理は `blockCommentDepth > 0` 分岐で同じ行か
                    // 後続行のどちらかが担当する。
                    sb.Append(' ');
                    sb.Append(' ');
                    blockCommentDepth = 1;
                    i += 2;
                    continue;
                }
                if (c == '\'')
                {
                    inSingle = true;
                    sb.Append(' ');
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    inDouble = true;
                    sb.Append(' ');
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            else if (inSingle)
            {
                if (c == '\'' && i + 1 < line.Length && line[i + 1] == '\'')
                {
                    sb.Append(' ');
                    sb.Append(' ');
                    i += 2;
                    continue;
                }
                if (c == '\'')
                {
                    inSingle = false;
                    sb.Append(' ');
                    i++;
                    continue;
                }
                sb.Append(' ');
                i++;
            }
            else
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append(' ');
                    sb.Append(' ');
                    i += 2;
                    continue;
                }
                if (c == '"')
                {
                    inDouble = false;
                    sb.Append(' ');
                    i++;
                    continue;
                }
                sb.Append(' ');
                i++;
            }
        }

        return sb.ToString();
    }

    private static string StripLeadingCSharpAttributeLists(
        string line,
        ref bool inLeadingAttributeBlock,
        ref int attributeBracketDepth,
        ref int attributeParenDepth,
        bool insideEnumBody)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        if (index >= line.Length)
            return line;

        if (!inLeadingAttributeBlock && line[index] != '[')
            return line;

        if (inLeadingAttributeBlock && ShouldRecoverFromIncompleteLeadingCSharpAttribute(line, index, insideEnumBody, attributeParenDepth))
        {
            inLeadingAttributeBlock = false;
            attributeBracketDepth = 0;
            attributeParenDepth = 0;
            return line;
        }

        var cursor = index;
        var blankUntil = index;
        while (cursor < line.Length)
        {
            if (!inLeadingAttributeBlock)
            {
                if (line[cursor] != '[')
                    break;

                inLeadingAttributeBlock = true;
                attributeBracketDepth = 0;
                attributeParenDepth = 0;
            }

            while (cursor < line.Length)
            {
                var ch = line[cursor++];
                if (ch == '[')
                {
                    attributeBracketDepth++;
                }
                else if (ch == '(')
                {
                    attributeParenDepth++;
                }
                else if (ch == ')' && attributeParenDepth > 0)
                {
                    attributeParenDepth--;
                }
                else if (ch == ']')
                {
                    attributeBracketDepth--;
                    if (attributeBracketDepth == 0)
                    {
                        inLeadingAttributeBlock = false;
                        attributeParenDepth = 0;
                        break;
                    }
                }
            }

            if (inLeadingAttributeBlock)
                return line[..index] + new string(' ', line.Length - index);

            while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
                cursor++;

            blankUntil = cursor;
            if (cursor >= line.Length || line[cursor] != '[')
                break;
        }

        return blankUntil < line.Length
            ? line[..index] + new string(' ', blankUntil - index) + line[blankUntil..]
            : line[..index] + new string(' ', blankUntil - index);
    }

    private static bool ShouldRecoverFromIncompleteLeadingCSharpAttribute(
        string line,
        int firstNonWhitespaceIndex,
        bool insideEnumBody,
        int attributeParenDepth)
    {
        if (firstNonWhitespaceIndex >= line.Length || line[firstNonWhitespaceIndex] == '[')
            return false;

        return TryMatchAnyRecoverableCSharpPattern(line, insideEnumBody, attributeParenDepth);
    }

    private static bool TryMatchAnyRecoverableCSharpPattern(string line, bool insideEnumBody, int attributeParenDepth)
    {
        if (PatternCache.TryGetValue("csharp", out var patterns))
        {
            foreach (var pattern in patterns)
            {
                if (ReferenceEquals(pattern.Regex, CSharpEnumMemberRegex))
                    continue;

                if (pattern.Regex.IsMatch(line))
                    return true;
            }
        }

        return insideEnumBody
            && attributeParenDepth == 0
            && CSharpEnumMemberNameRegex.IsMatch(line);
    }

    /// <summary>
    /// Return true when a batch (.bat / .cmd) line is a comment, i.e. `::` / `:::` / `rem` /
    /// `@rem` (with optional leading whitespace and case-insensitive `rem`). Comment lines
    /// must not contribute `set` property symbols even when they contain the boundary tokens
    /// (`&`, `(`, `else`, `do`) that the new inline-set-capture regex accepts.
    /// batch (.bat / .cmd) のコメント行 (`::` / `:::` / `rem` / `@rem`、先頭空白可、`rem` は大小文字不問) のときに
    /// true を返す。新しい inline `set` 捕捉正規表現が受け付ける境界トークン (`&` / `(` / `else` / `do`) を
    /// 含んでいても、コメント行からは `set` property を拾わない。
    /// </summary>
    private static bool IsBatchCommentLine(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;

        if (i >= line.Length)
            return false;

        // `::` (and therefore also `:::`, `::: ...`) opens a batch comment that consumes the
        // rest of the line. The label regex does not match these because it requires a name
        // char after the first `:`, but the property regex could match their inline tokens.
        // `::` 以降はコメント (`:::`、`::: ...` も同様)。ラベル正規表現は `:` の後ろに名前文字を
        // 要求するため影響を受けないが、property 正規表現は inline トークンを拾ってしまう。
        if (line[i] == ':' && i + 1 < line.Length && line[i + 1] == ':')
            return true;

        // `@rem` (echo-suppression prefix + rem). Accept optional whitespace between `@` and
        // `rem` to mirror how the property regex tolerates `@ set`.
        // `@rem` (echo 抑止プレフィクス + rem) 。property 正規表現が `@ set` を許すのに合わせて
        // `@` と `rem` の間の空白も許容する。
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
        // A bare `rem` or `rem` followed by whitespace / end-of-line is a comment.
        // Case-insensitive: `REM`, `rem`, `Rem`, `rEM`, etc. are all comments.
        // 単独の `rem` または `rem` の直後が空白か行末ならコメント扱い。
        // 大小文字不問 — `REM` / `rem` / `Rem` / `rEM` などすべてコメント。
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

    private static bool CanContinueScanningSameLineBraceBody(
        string? lang,
        string kind,
        BodyStyle bodyStyle,
        int? endLine,
        int startLine,
        int sameLineEndColumn,
        int absoluteStartColumn)
    {
        if (bodyStyle != BodyStyle.Brace || endLine != startLine || sameLineEndColumn < absoluteStartColumn)
            return false;

        return lang is "javascript" or "typescript" or "css" or "java"
            || (lang == "csharp" && CanContinueScanningSameLineCSharpBraceBody(kind));
    }

    private static bool CanStepIntoSameLineTypeBody(string? lang, string kind)
    {
        if (kind is not ("class" or "struct" or "interface" or "enum" or "namespace"))
            return false;

        return lang is "csharp" or "java";
    }

    private static bool IsCSharpFieldLikeFunctionPattern(SymbolPattern pattern)
        => pattern.Kind == "function"
            && pattern.BodyStyle == BodyStyle.None
            && pattern.ReturnTypeGroup != null;

    private static bool HasInvalidCSharpReturnTypeSuffix(string? returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
            return true;

        var trimmed = returnType.TrimEnd();
        if (trimmed.Length == 0)
            return true;

        var lastChar = trimmed[^1];
        if (lastChar is '<' or '=' or ':' or '+' or '-' or '/' or '%' or '!' or '&' or '|' or '^' or '~' or '.')
            return true;

        var tokenStart = trimmed.Length - 1;
        while (tokenStart > 0
            && (char.IsLetterOrDigit(trimmed[tokenStart - 1]) || trimmed[tokenStart - 1] == '_'))
        {
            tokenStart--;
        }

        if (tokenStart > 0
            && trimmed[tokenStart - 1] == '@'
            && IsCSharpVerbatimIdentifierPrefix(trimmed, tokenStart - 1))
        {
            return false;
        }

        var lastToken = trimmed[tokenStart..];
        return lastToken is "as" or "is" or "return" or "throw" or "new";
    }

    private static bool IsInsidePreviouslyEmittedCSharpMemberBody(
        string[] lines,
        List<SymbolRecord> symbols,
        int candidateLine,
        int candidateColumn)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not "function" and not "property" and not "event")
                continue;
            if (!symbol.BodyStartLine.HasValue || !symbol.BodyEndLine.HasValue)
                continue;
            if (candidateLine <= symbol.StartLine)
                continue;
            if (candidateLine < symbol.BodyStartLine.Value || candidateLine > symbol.BodyEndLine.Value)
                continue;
            if (candidateLine == symbol.BodyEndLine.Value
                && TryFindCSharpSemicolonTerminatedSignatureExtent(
                    lines,
                    Math.Max(0, symbol.StartLine - 1),
                    symbol.StartColumn ?? 0,
                    out var signatureLastLineIndex,
                    out var signatureLastLineExclusiveEndColumn)
                && signatureLastLineIndex + 1 == candidateLine
                && signatureLastLineExclusiveEndColumn.HasValue
                && candidateColumn >= signatureLastLineExclusiveEndColumn.Value)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static int FindNextSameLineBraceStatementStart(string matchLine, int startIndex, string? lang)
    {
        return lang is "javascript" or "typescript"
            ? FindNextJavaScriptTypeScriptStatementStart(matchLine, startIndex)
            : FindNextBraceStatementStart(matchLine, startIndex);
    }

    // C# same-line restarts can legitimately hit a container-closing `}`, an empty
    // statement `;`, or a carried verbatim-string closing `"` before the next real sibling
    // declaration (`... P { get; } } public int Q { get; }`, or a carried multiline string
    // continuation like `"; public class Child { }`). Keep advancing until we reach a
    // non-`}` / non-`;` / non-`"` statement start so the later real declaration stays visible.
    // C# の同一行再開は、次の実 sibling 宣言の前に container を閉じる `}`、空文の `;`、
    // あるいは継続 verbatim string の閉じ `"` に当たりうる（`... P { get; } } public int Q { get; }`
    // や、`"; public class Child { }` のような継続文字列の閉じ直後）。後続の実宣言を落とさないよう、
    // 非 `}` / 非 `;` / 非 `"` の statement start に当たるまで再開位置を進める。
    private static int FindNextSameLineNonClosingBraceStatementStart(string matchLine, int startIndex, string? lang)
    {
        var nextOffset = FindNextSameLineBraceStatementStart(matchLine, startIndex, lang);
        while (lang == "csharp"
               && nextOffset >= 0
               && nextOffset < matchLine.Length
               && matchLine[nextOffset] is '}' or ';' or '"')
        {
            nextOffset = FindNextSameLineBraceStatementStart(matchLine, nextOffset + 1, lang);
        }

        return nextOffset;
    }

    private static int FindNextBraceStatementStart(string line, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < line.Length)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (index >= line.Length)
                return -1;

            var previous = index - 1;
            while (previous >= 0 && char.IsWhiteSpace(line[previous]))
                previous--;

            if (previous < 0 || line[previous] is ';' or '{' or '}')
                return index;

            index++;
        }

        return -1;
    }

    // For C# plain fields (kind `property`, BodyStyle.None), find the end of the
    // field's declaration statement on the same (merged) match line so the
    // signature can be clamped to the full declaration text and the same-line
    // pattern scanner can resume after the terminating `;`. Walks with paren /
    // bracket / brace depth tracking so `{` / `}` inside an initializer
    // (collection or object initializer, lambda body) does not short-circuit
    // the scan; when an unbalanced `}` is encountered (the closing brace of
    // the enclosing type body) the position of that `}` is returned instead
    // so signature and advance both stop before the wrapper terminator. Input
    // is expected to be the structurally-masked match line so string-literal
    // `{` / `;` cannot poison the depth tracker.
    // C# 通常フィールド（kind `property`、BodyStyle.None）向けに、結合済みマッチ行での
    // 宣言文の終端位置を返す。signature を `;` まで含む完全な宣言文字列に揃え、かつ
    // 同一行のパターンスキャンを `;` の次から再開できるようにするために使う。paren /
    // bracket / brace の深さを追うので、初期化子（コレクション / オブジェクト初期化子や
    // ラムダ本体）内の `{` / `}` で判定が途切れない。深さ 0 で出現する `}`（囲む型本体の
    // 閉じ括弧）は、その位置をそのまま返すため signature と advance の両方がラッパー
    // 終端の手前で止まる。入力は構造的にマスク済みのマッチ行を想定し、文字列リテラル内の
    // `{` / `;` が深さトラッカを誤認させないようにしている。
    private static int FindCSharpSameLineStatementEnd(string maskedLine, int startIndex)
    {
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        var index = Math.Max(0, startIndex);
        while (index < maskedLine.Length)
        {
            var ch = maskedLine[index];
            if (ch == '(')
            {
                parenDepth++;
            }
            else if (ch == ')')
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (ch == '[')
            {
                bracketDepth++;
            }
            else if (ch == ']')
            {
                if (bracketDepth > 0) bracketDepth--;
            }
            else if (ch == '{')
            {
                braceDepth++;
            }
            else if (ch == '}')
            {
                if (braceDepth > 0)
                {
                    braceDepth--;
                }
                else
                {
                    return index;
                }
            }
            else if (ch == ';' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                return index + 1;
            }

            index++;
        }

        return maskedLine.Length;
    }

    // Reuse the same top-level `;` scan as plain fields for other compact same-line C#
    // members (`event E;`, interface/abstract methods like `void M();`, delegates, etc.).
    // Returns the inclusive `;` column when one exists on the same physical line and -1
    // when the declaration instead runs into the enclosing `}` or simply has no same-line
    // semicolon terminator. Closes #473.
    // 通常フィールドと同じ top-level `;` 探索を、他のコンパクトな同一行 C# member
    // (`event E;`、`void M();` 形の interface/abstract method、delegate など) にも
    // 再利用する。同一物理行に `;` があればその包含列を返し、囲み `}` にぶつかる、
    // あるいは同一行終端 `;` 自体が無い場合は -1 を返す。Closes #473.
    private static int FindCSharpSameLineSemicolonEndColumn(string maskedLine, int startIndex)
    {
        var statementEnd = FindCSharpSameLineStatementEnd(maskedLine, startIndex);
        var semicolonIndex = statementEnd - 1;
        return semicolonIndex >= startIndex
            && semicolonIndex < maskedLine.Length
            && maskedLine[semicolonIndex] == ';'
            ? semicolonIndex
            : -1;
    }

    private static int FindCSharpSameLineEnumMemberEndColumn(string maskedLine, int startIndex)
    {
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        for (var index = Math.Max(0, startIndex); index < maskedLine.Length; index++)
        {
            var ch = maskedLine[index];
            if (ch == '(')
            {
                parenDepth++;
            }
            else if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
            }
            else if (ch == '[')
            {
                bracketDepth++;
            }
            else if (ch == ']')
            {
                if (bracketDepth > 0)
                    bracketDepth--;
            }
            else if (ch == '{')
            {
                braceDepth++;
            }
            else if (ch == '}')
            {
                if (braceDepth > 0)
                {
                    braceDepth--;
                }
                else
                {
                    return index;
                }
            }
            else if (ch == ',' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindSameLineBraceEndColumn(string line, int startColumn, string? lang, string kind)
    {
        return lang switch
        {
            "javascript" or "typescript" => FindJavaScriptTypeScriptSameLineBraceEndColumn(line, startColumn, lang),
            "css" => FindCssSameLineBraceEndColumn(line, startColumn),
            "csharp" => FindCSharpSameLineBraceEndColumn(line, startColumn),
            "java" => FindJavaSameLineBraceEndColumn(line, startColumn),
            _ => -1,
        };
    }

    private static int FindJavaSameLineBraceEndColumn(string line, int startColumn)
    {
        var mode = JavaScanMode.Normal;
        var depth = 0;
        var opened = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var column = Math.Max(0, startColumn);

        while (column < line.Length)
        {
            if (TryConsumeJavaNonCode(line, ref column, ref mode))
                continue;

            var ch = line[column];
            if (!opened)
            {
                if (ch == '(')
                {
                    parenDepth++;
                    column++;
                    continue;
                }

                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    column++;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                    column++;
                    continue;
                }

                if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    column++;
                    continue;
                }

                if (ch == '<')
                {
                    angleDepth++;
                    column++;
                    continue;
                }

                if (ch == '>' && angleDepth > 0)
                {
                    angleDepth--;
                    column++;
                    continue;
                }

                if (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0)
                {
                    column++;
                    continue;
                }
            }

            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return column;
            }

            column++;
        }

        return -1;
    }

    private static int FindCssSameLineBraceEndColumn(string line, int startColumn)
    {
        var maskedLine = MaskCssScannerLines([line])[0];
        var depth = 0;
        var opened = false;

        for (var index = Math.Max(0, startColumn); index < maskedLine.Length; index++)
        {
            var ch = maskedLine[index];
            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return index;
            }
        }

        return -1;
    }

    private static bool CanContinueScanningSameLineCSharpBraceBody(string kind)
    {
        return kind is "namespace" or "class" or "struct" or "interface" or "enum" or "property";
    }

    private static bool CanUseCSharpSameLineSemicolonEndColumn(string kind)
    {
        return kind is "function" or "event" or "delegate";
    }

    private static bool CanRestartCSharpSameLineSiblingScan(string kind)
    {
        return kind is "function" or "property" or "event" or "delegate" or "enum";
    }

    private static int FindCSharpSameLineBraceEndColumn(string line, int startColumn)
    {
        return FindCSharpSameLineBraceEndColumnFromSanitized(
            LexCSharpLine(line, new CSharpLexState()).SanitizedLine,
            startColumn);
    }

    private static int FindCSharpSameLineBraceEndColumnFromSanitized(string sanitizedLine, int startColumn)
    {
        var depth = 0;
        var opened = false;
        var expressionBody = false;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var index = Math.Max(0, startColumn); index < sanitizedLine.Length; index++)
        {
            var ch = sanitizedLine[index];

            if (expressionBody)
            {
                if (ch == '(')
                    parenDepth++;
                else if (ch == ')' && parenDepth > 0)
                    parenDepth--;
                else if (ch == '[')
                    bracketDepth++;
                else if (ch == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (ch == '{')
                    depth++;
                else if (ch == '}' && depth > 0)
                    depth--;
                else if (ch == ';' && parenDepth == 0 && bracketDepth == 0 && depth == 0)
                    return index;

                continue;
            }

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

            if (ch == '{' && parenDepth == 0 && bracketDepth == 0)
            {
                depth++;
                opened = true;
                continue;
            }

            if (ch == '}' && opened && parenDepth == 0 && bracketDepth == 0)
            {
                depth--;
                if (depth == 0)
                    return index;

                continue;
            }

            // Expression-bodied members (`=> expr;`) have no surrounding `{}` to anchor the
            // same-line end column. Detect the top-level `=>` so later sibling declarations
            // on the same physical line are not swallowed into the current signature / body
            // extent. Closes #470 review follow-up.
            // 式本体 member (`=> expr;`) には `{}` が無いため、same-line 終端列を
            // top-level の `=>` から `;` までで判定する。これにより、同じ物理行の後続
            // sibling 宣言を現在の signature / body 範囲へ飲み込まないようにする。
            // Closes #470 review follow-up.
            if (ch == '='
                && index + 1 < sanitizedLine.Length
                && sanitizedLine[index + 1] == '>'
                && !opened
                && parenDepth == 0
                && bracketDepth == 0)
            {
                expressionBody = true;
                index++;
            }
        }

        return -1;
    }

    // Body-less Java members (`void a();`, `String[] value();`, `int[] v() default {1};`) need a
    // same-line statement-end scanner so later siblings on the same physical line stay reachable.
    // Track comments / strings / text blocks with the Java lexer and balance `()`, `[]`, and
    // annotation/default-value braces. If the enclosing `}` arrives before a top-level `;`, return
    // that `}` position so callers can stop without absorbing the wrapper close.
    // Java の body-less member（`void a();` / `String[] value();` / `int[] v() default {1};`）向けの
    // same-line statement-end scanner。Java lexer で comment / 文字列 / text block を避けつつ、
    // `()` / `[]` / annotation / default 値の `{}` を釣り合わせる。top-level `;` より先に
    // 囲み `}` が来た場合はその位置を返し、呼び出し側が wrapper close を飲み込まず止まれるようにする。
    private static int FindJavaSameLineStatementEnd(string line, int startColumn)
    {
        var mode = JavaScanMode.Normal;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var column = Math.Max(0, startColumn);
        while (column < line.Length)
        {
            if (TryConsumeJavaNonCode(line, ref column, ref mode))
                continue;

            var ch = line[column];
            if (ch == '(')
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
            else if (ch == '{')
            {
                braceDepth++;
            }
            else if (ch == '}')
            {
                if (braceDepth > 0)
                {
                    braceDepth--;
                }
                else
                {
                    return column;
                }
            }
            else if (ch == ';'
                && parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0)
            {
                return column + 1;
            }

            column++;
        }

        return line.Length;
    }

    // Walk upward from the identifier line looking for a contiguous run of modifier-only
    // physical lines, skipping blank lines and attribute-stripped whitespace. Returns the
    // concatenated modifier prefix (in declaration order) so callers can prepend it to the
    // identifier line for regex matching. Returns null when no modifier-only predecessor
    // exists. Used to recover wrapped C# constructors whose leading `static` / `public` /
    // etc. sits on its own physical line. Closes #348.
    // 識別子行から上に遡り、空行や属性ストリップで空白化された行をスキップしつつ、
    // モディファイアのみの物理行を連続して連結する。宣言順に連結したプレフィックスを返し、
    // 呼び出し元は識別子行の先頭に付けて regex マッチに使える。先頭モディファイア行が
    // 見つからなければ null を返す。C# の `static` / `public` などが単独行に書かれた
    // ラップ型コンストラクタを拾うために使う。Closes #348.
    private static CSharpWrappedHeaderModifierInfo? TryFindCSharpWrappedHeaderModifier(
        string[] csharpMatchLines,
        int nameLineIndex)
    {
        if (nameLineIndex <= 0)
            return null;

        string? prefix = null;
        for (int index = nameLineIndex - 1; index >= 0; index--)
        {
            var structural = csharpMatchLines[index];
            if (string.IsNullOrWhiteSpace(structural))
                continue;

            if (!CSharpWrappedHeaderModifierLineRegex.IsMatch(structural))
                break;

            var structuralTrimmed = structural.Trim();
            prefix = prefix == null
                ? structuralTrimmed
                : structuralTrimmed + " " + prefix;
        }

        if (prefix == null)
            return null;

        return new CSharpWrappedHeaderModifierInfo(prefix);
    }

    // Enumerate candidate prefixes to retry against the C# function-kind regexes when the
    // full wrapped-modifier prefix fails. Multi-modifier shapes like
    // `public\nstatic\nP1()` synthesize `public static P1()` which neither the ctor regex
    // (accepts only unsafe/extern between visibility and name) nor the static-ctor regex
    // (requires static first) will match. Falling back to `static`-only and
    // visibility-only variants lets the respective regex still fire so the wrapped ctor
    // is not silently dropped. Closes #348.
    // ラップされた先頭モディファイア prefix で C# function 系パターンに失敗した場合に
    // 試す候補 prefix を列挙する。`public\nstatic\nP1()` のような複数モディファイア形は
    // `public static P1()` と合成されるが、ctor regex は visibility と name の間に
    // `unsafe` / `extern` しか許さず、静的 ctor regex は `static` 先頭を要求するため、
    // このままではどちらもマッチしない。`static` 単独や visibility 単独の variant に
    // フォールバックして、適合する regex を拾えるようにする。Closes #348.
    private static IEnumerable<string> EnumerateCSharpWrappedModifierCandidates(string prefix)
    {
        yield return prefix;

        var tokens = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 1)
            yield break;

        var hasStatic = false;
        string? visibility = null;
        foreach (var token in tokens)
        {
            if (token == "static")
                hasStatic = true;
            else if (visibility == null
                && token is "public" or "private" or "protected" or "internal" or "file")
                visibility = token;
        }

        if (hasStatic)
            yield return "static";
        if (visibility != null)
            yield return visibility;
    }

    /// <summary>
    /// Track multi-line C# `[...]` bracket sections across lines and blank out any text that
    /// sits inside those sections, so downstream symbol regexes do not treat interior identifiers
    /// as declarations. Activates whenever a `[` opens without a matching `]` on the same line,
    /// regardless of whether the `[` sits at the start of the line (leading attribute) or deeper
    /// inside the line (parameter attribute like `void M([\n Attr\n] T x)`, type-parameter
    /// attribute like `class C<[\n Attr\n] T>`, delegate/lambda parameter attributes, etc.).
    /// Single-line attribute lists continue to be handled by `StripLeadingCSharpAttributeLists`.
    /// 複数行にまたがる C# `[...]` セクションを跨行で追跡し、内部の文字列を空白化することで
    /// 下流のシンボル regex が内部の識別子を宣言として誤解釈しないようにする。`[` が行頭
    /// （空白の後）にある場合だけでなく、`void M([\n Attr\n] T x)` のようなパラメータ属性、
    /// `class C<[\n Attr\n] T>` のような型パラメータ属性、delegate / lambda のパラメータ属性など、
    /// 行の途中で開いて同一行で閉じない `[` でも作動する。同一行で完結する属性リストは
    /// `StripLeadingCSharpAttributeLists` が引き続き担当する。
    /// </summary>
    private static string StripMultiLineCSharpAttributeInterior(string line, ref int depth)
    {
        if (depth == 0)
        {
            // Scan the line for a `[` that is NOT closed on the same line. Everything before
            // that `[` is real code (method header text like `void M(`, generic opener like
            // `class C<`, etc.) and must be preserved so downstream declaration regexes can
            // still recognize the surrounding construct. Everything from the unclosed `[`
            // onward is blanked, and subsequent lines are blanked until the matching `]`.
            // Only attribute-position `[` should trigger blanking — a multi-line indexer
            // declaration such as `public int this[\n    int i\n] => _items[i];` opens `[`
            // immediately after the identifier `this`, which is NOT an attribute and must
            // not be stripped (otherwise the indexer regex sees only `public int this` and
            // the indexer silently disappears from symbols / definition / outline). Treat
            // `[` as an attribute opener only when the immediately preceding non-whitespace
            // character is not a word character (`[_A-Za-z0-9]`) and not `)` / `]` (which
            // indicate indexer / array access on an expression result or chained indexer).
            // 行内を走査し、同一行で閉じない `[` を探す。その `[` より前は通常のコード
            // （`void M(` のようなメソッドヘッダ、`class C<` のようなジェネリック開口など）
            // であり、下流の宣言 regex が外側の構文を認識できるように残す必要がある。
            // 閉じない `[` 以降は空白化し、対応する `]` が現れるまで後続行も空白化する。
            // `[` が属性位置にあるときだけ空白化する — `public int this[\n    int i\n]`
            // のような複数行インデクサ宣言では `this` 直後の `[` が属性でないため、
            // ここを削ってしまうとインデクサがシンボルから消える。直前の非空白文字が
            // 語文字（`[_A-Za-z0-9]`）でも `)` / `]` でもない場合にのみ属性開口と判定する。
            int openIndex = -1;
            int localDepth = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '[')
                {
                    if (localDepth == 0)
                    {
                        // Look back past whitespace for the character that introduces the `[`.
                        // 先行する非空白文字を探して `[` の導入子を判定する。
                        int p = i - 1;
                        while (p >= 0 && (line[p] == ' ' || line[p] == '\t'))
                            p--;
                        if (p >= 0)
                        {
                            char prev = line[p];
                            if (prev == '_' || (prev >= 'A' && prev <= 'Z') || (prev >= 'a' && prev <= 'z') || (prev >= '0' && prev <= '9') || prev == ')' || prev == ']')
                            {
                                // Not an attribute opener (e.g. `this[`, `arr[`, `(expr)[`, `arr[i][`).
                                // Treat this `[` as opaque — do not track depth, do not blank.
                                // 属性開口ではない（`this[`・`arr[`・`(expr)[`・`arr[i][` など）。
                                // この `[` は追跡も空白化もしない。
                                continue;
                            }
                        }
                        openIndex = i;
                    }
                    localDepth++;
                }
                else if (line[i] == ']')
                {
                    if (localDepth > 0)
                    {
                        localDepth--;
                        if (localDepth == 0)
                            openIndex = -1;
                    }
                }
            }

            if (openIndex < 0 || localDepth <= 0)
                return line;

            depth = localDepth;
            return line.Substring(0, openIndex);
        }

        // We are inside a multi-line attribute section. Walk the line, closing brackets when we
        // see `]`. Once depth returns to zero, the remainder of the line is real code.
        int index = 0;
        while (index < line.Length && depth > 0)
        {
            if (line[index] == '[') depth++;
            else if (line[index] == ']') depth--;
            index++;
        }
        if (depth > 0)
            return string.Empty;
        return line[index..];
    }

    private static CSharpPropertyMatchCandidate BuildCSharpPropertyMatchLine(string[] lines, string[] csharpMatchLines, int startLineIndex)
    {
        var matchLine = csharpMatchLines[startLineIndex];
        if (string.IsNullOrWhiteSpace(matchLine)
            || !CSharpPropertyHeaderPrefixRegex.IsMatch(matchLine)
            || HasCSharpPropertyAccessorStart(matchLine)
            || CSharpWrappedHeaderModifierLineRegex.IsMatch(matchLine))
        {
            // Modifier-only lines (`static`, `public`, etc. on their own physical line)
            // are handled by the name-line wrapped-modifier recovery in the extraction
            // loop. If the field-header merger runs here, it joins the next line and
            // produces a phantom emission at the modifier line with a truncated
            // signature. Returning the raw matchLine lets the pattern match fail at the
            // modifier line so only the name line emits a symbol. Closes #348.
            // 単独行に書かれたモディファイア（`static`、`public` 等）は、抽出ループ側の
            // 名前行ラップド救済が処理する。フィールドヘッダ結合がここで動くと、次の行を
            // 結合してモディファイア行に signature の切れた幻のエミットを残してしまう。
            // 生の matchLine を返してモディファイア行ではパターンに失敗させ、名前行のみが
            // シンボルを emit するようにする。Closes #348.
            return new CSharpPropertyMatchCandidate(matchLine, startLineIndex, startLineIndex);
        }

        if (TryFindCSharpExpressionArrow(lines, startLineIndex, startLineIndex, out var sameLineArrowLineIndex, out var sameLineArrowColumn))
        {
            var expressionEndLineIndex = FindCSharpExpressionBodyEndLine(lines, sameLineArrowLineIndex, sameLineArrowColumn);
            return new CSharpPropertyMatchCandidate(matchLine, startLineIndex, startLineIndex, null, expressionEndLineIndex);
        }

        var builder = new StringBuilder(matchLine.TrimEnd());

        // Detect `{` on the sanitized line so braces inside string literals or comments
        // don't flip a plain field into property-body handling.
        // サニタイズ済みの行で `{` を検出し、文字列やコメント内の `{` で通常フィールドが
        // property 本体扱いに切り替わらないようにする。
        var openBraceLineIndex = csharpMatchLines[startLineIndex].IndexOf('{') >= 0
            ? startLineIndex
            : -1;
        var openBraceExclusiveEndColumn = openBraceLineIndex == startLineIndex
            ? ResolveCSharpBraceColumn(lines[startLineIndex], csharpMatchLines[startLineIndex]) + 1
            : (int?)null;

        if (HasCSharpTopLevelFieldInitializer(matchLine)
            || openBraceLineIndex >= 0 && CSharpConfirmedMemberPrefixRegex.IsMatch(matchLine))
        {
            return ContinueConfirmedCSharpPropertyMatch(
                lines,
                csharpMatchLines,
                builder,
                startLineIndex,
                startLineIndex,
                matchLine,
                openBraceLineIndex,
                openBraceExclusiveEndColumn);
        }

        var lookaheadLimitExclusive = Math.Min(csharpMatchLines.Length, startLineIndex + CSharpPropertyMatchLookaheadLineLimit + 1);
        for (int i = startLineIndex + 1; i < lookaheadLimitExclusive; i++)
        {
            var nextLine = csharpMatchLines[i].Trim();
            if (nextLine.Length == 0)
                continue;

            if (builder.Length + 1 + nextLine.Length > CSharpPropertyMatchLookaheadCharLimit)
                break;

            builder.Append(' ').Append(nextLine);
            var normalizedCombined = CollapseCSharpGenericTypeWhitespace(builder.ToString());

            if (openBraceLineIndex < 0 && csharpMatchLines[i].IndexOf('{') >= 0)
            {
                openBraceLineIndex = i;
                openBraceExclusiveEndColumn = ResolveCSharpBraceColumn(lines[i], csharpMatchLines[i]) + 1;
            }

            if (HasCSharpPropertyAccessorStart(normalizedCombined))
            {
                return new CSharpPropertyMatchCandidate(
                    normalizedCombined,
                    i,
                    openBraceLineIndex >= 0 ? openBraceLineIndex : i,
                    openBraceLineIndex >= 0 ? openBraceExclusiveEndColumn : null);
            }

            if (TryFindCSharpExpressionArrow(lines, startLineIndex, i, out var arrowLineIndex, out var arrowColumn))
            {
                var expressionEndLineIndex = FindCSharpExpressionBodyEndLine(lines, arrowLineIndex, arrowColumn);
                return new CSharpPropertyMatchCandidate(normalizedCombined, i, i, null, expressionEndLineIndex);
            }

            // Plain-field multi-line declaration: continuation reaches a top-level `;`.
            // Object / collection initializers (`= new() { ... };`) balance their own braces,
            // so `HasCSharpTopLevelSemicolon` fires only at the real terminator — regardless
            // of whether an earlier line contained `{`. `HasCSharpPropertyAccessorStart` above
            // already claimed true property bodies, so reaching this point means the `{`
            // belongs to an initializer, not an accessor block.
            // 複数行にまたがる通常フィールド宣言: 継続行でトップレベルの `;` に到達する形に対応する。
            // `= new() { ... };` のようなオブジェクト/コレクション初期化子は自身の brace を閉じるため、
            // `HasCSharpTopLevelSemicolon` は真の終端 `;` のみで発火し、先行行に `{` があっても
            // 問題ない。上の `HasCSharpPropertyAccessorStart` が真の property 本体を先に拾うため、
            // ここに到達する `{` は初期化子側のものと確定している。
            if (HasCSharpTopLevelSemicolon(normalizedCombined))
            {
                return new CSharpPropertyMatchCandidate(normalizedCombined, i, i);
            }

            if (HasCSharpTopLevelFieldInitializer(normalizedCombined)
                || openBraceLineIndex >= 0 && CSharpConfirmedMemberPrefixRegex.IsMatch(normalizedCombined))
            {
                return ContinueConfirmedCSharpPropertyMatch(
                    lines,
                    csharpMatchLines,
                    builder,
                    startLineIndex,
                    i,
                    normalizedCombined,
                    openBraceLineIndex,
                    openBraceExclusiveEndColumn);
            }

            if (nextLine.StartsWith(";", StringComparison.Ordinal))
                break;
        }

        return new CSharpPropertyMatchCandidate(matchLine, startLineIndex, startLineIndex);
    }

    private static CSharpPropertyMatchCandidate ContinueConfirmedCSharpPropertyMatch(
        string[] lines,
        string[] csharpMatchLines,
        StringBuilder builder,
        int startLineIndex,
        int currentLineIndex,
        string normalizedCombined,
        int openBraceLineIndex,
        int? openBraceExclusiveEndColumn)
    {
        var semicolonTracker = new CSharpTopLevelSemicolonTracker();
        semicolonTracker.Scan(normalizedCombined);

        StringBuilder? accessorProbeBuilder = null;
        var accessorProbeStatus = CSharpAccessorProbeStatus.Rejected;
        if (openBraceLineIndex >= 0 && openBraceExclusiveEndColumn.HasValue)
        {
            accessorProbeBuilder = BuildCSharpAccessorProbeBuilder(
                csharpMatchLines,
                openBraceLineIndex,
                openBraceExclusiveEndColumn.Value,
                currentLineIndex);
            accessorProbeStatus = ClassifyCSharpAccessorProbe(accessorProbeBuilder.ToString());
            if (accessorProbeStatus == CSharpAccessorProbeStatus.Found)
            {
                return new CSharpPropertyMatchCandidate(
                    normalizedCombined,
                    currentLineIndex,
                    openBraceLineIndex,
                    openBraceExclusiveEndColumn);
            }
        }

        for (int i = currentLineIndex + 1; i < csharpMatchLines.Length; i++)
        {
            var nextLine = csharpMatchLines[i].Trim();
            if (nextLine.Length == 0)
                continue;

            builder.Append(' ').Append(nextLine);
            semicolonTracker.Scan(nextLine);

            if (openBraceLineIndex < 0 && csharpMatchLines[i].IndexOf('{') >= 0)
            {
                openBraceLineIndex = i;
                openBraceExclusiveEndColumn = ResolveCSharpBraceColumn(lines[i], csharpMatchLines[i]) + 1;
                accessorProbeBuilder = BuildCSharpAccessorProbeBuilder(
                    csharpMatchLines,
                    openBraceLineIndex,
                    openBraceExclusiveEndColumn.Value,
                    i);
                accessorProbeStatus = ClassifyCSharpAccessorProbe(accessorProbeBuilder.ToString());
            }
            else if (accessorProbeBuilder != null
                && accessorProbeStatus == CSharpAccessorProbeStatus.Pending)
            {
                AppendCSharpAccessorProbeLine(accessorProbeBuilder, csharpMatchLines[i], null);
                accessorProbeStatus = ClassifyCSharpAccessorProbe(accessorProbeBuilder.ToString());
            }

            if (accessorProbeStatus == CSharpAccessorProbeStatus.Found)
            {
                return new CSharpPropertyMatchCandidate(
                    CollapseCSharpGenericTypeWhitespace(builder.ToString()),
                    i,
                    openBraceLineIndex,
                    openBraceExclusiveEndColumn);
            }

            if (semicolonTracker.HasTopLevelSemicolon)
            {
                return new CSharpPropertyMatchCandidate(
                    CollapseCSharpGenericTypeWhitespace(builder.ToString()),
                    i,
                    i);
            }
        }

        return new CSharpPropertyMatchCandidate(normalizedCombined, currentLineIndex, currentLineIndex);
    }

    // Prefer the raw line's `{` column (to preserve original positioning for body slicing),
    // falling back to the sanitized line only when the raw line hides the brace in a string
    // literal — in that case the sanitized position is the only safe signal we have.
    // 本体抽出で元の位置を保つため raw 行の `{` 列を優先し、raw 側で文字列リテラル内に隠れて
    // いる場合のみサニタイズ済み行の位置にフォールバックする。
    private static int ResolveCSharpBraceColumn(string rawLine, string sanitizedLine)
    {
        var rawColumn = rawLine.IndexOf('{');
        if (rawColumn >= 0)
            return rawColumn;

        return sanitizedLine.IndexOf('{');
    }

    private static bool HasCSharpPropertyAccessorStart(string text)
    {
        var braceIndex = text.IndexOf('{');
        if (braceIndex < 0)
            return false;

        var cursor = SkipWhitespace(text, braceIndex + 1);
        while (TrySkipCSharpAttributeList(text, ref cursor))
            cursor = SkipWhitespace(text, cursor);

        cursor = SkipWhitespace(text, cursor);
        if (TrySkipCSharpAccessorAccessibility(text, ref cursor))
            cursor = SkipWhitespace(text, cursor);
        while (TrySkipCSharpAccessorModifier(text, ref cursor))
            cursor = SkipWhitespace(text, cursor);

        return StartsWithCSharpAccessorKeyword(text, cursor);
    }

    private static bool HasCSharpEventAccessorStart(string text)
    {
        var braceIndex = text.IndexOf('{');
        if (braceIndex < 0)
            return false;

        var cursor = SkipWhitespace(text, braceIndex + 1);
        while (TrySkipCSharpAttributeList(text, ref cursor))
            cursor = SkipWhitespace(text, cursor);

        return StartsWithCSharpEventAccessorKeyword(text, cursor);
    }

    private static bool ShouldDeferCSharpFunctionSameLineAdvance(string matchLine, int startColumn)
    {
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var remaining = matchLine[startColumn..];
        return !CSharpTypeBodyDeclarationMarker.IsMatch(remaining)
            && (CSharpSameLinePropertyStatementStartRegex.IsMatch(remaining)
                || CSharpSameLineEventOrDelegateStatementStartRegex.IsMatch(remaining));
    }

    private static bool HasCSharpTokenBeforeIndex(string text, string token, int exclusiveEnd)
    {
        if (string.IsNullOrEmpty(token) || exclusiveEnd <= 0)
            return false;

        if (exclusiveEnd > text.Length)
            exclusiveEnd = text.Length;

        var searchStart = 0;
        while (searchStart < exclusiveEnd)
        {
            var remaining = exclusiveEnd - searchStart;
            if (remaining <= 0)
                return false;

            var tokenIndex = text.IndexOf(token, searchStart, remaining, StringComparison.Ordinal);
            if (tokenIndex < 0)
                return false;

            var tokenEnd = tokenIndex + token.Length;
            if ((tokenIndex == 0 || !char.IsLetterOrDigit(text[tokenIndex - 1]) && text[tokenIndex - 1] != '_')
                && (tokenEnd >= text.Length || !char.IsLetterOrDigit(text[tokenEnd]) && text[tokenEnd] != '_'))
            {
                return true;
            }

            searchStart = tokenIndex + 1;
        }

        return false;
    }

    private static bool ShouldDeferCSharpBracePropertySameLineAdvance(string matchLine, int startColumn)
    {
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var remaining = matchLine[startColumn..];
        return !CSharpTypeBodyDeclarationMarker.IsMatch(remaining)
            && !HasCSharpPropertyAccessorStart(remaining)
            && CSharpSameLinePropertyStatementStartRegex.IsMatch(remaining);
    }

    private static bool ShouldDeferCSharpEventOrDelegateSameLineAdvance(string matchLine, int startColumn, string kind)
    {
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var remaining = matchLine[startColumn..];
        if (CSharpTypeBodyDeclarationMarker.IsMatch(remaining))
            return false;

        return kind switch
        {
            "event" => CSharpSameLineDelegateStatementStartRegex.IsMatch(remaining),
            "delegate" => CSharpSameLineEventStatementStartRegex.IsMatch(remaining),
            _ => false,
        };
    }

    private static bool TryGetCSharpSameLineSemicolonSiblingOffset(string matchLine, int startColumn, out int nextSameLineOffset)
    {
        nextSameLineOffset = -1;
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var statementEnd = FindCSharpSameLineStatementEnd(matchLine, startColumn);
        if (statementEnd <= startColumn)
            return false;

        var nextOffset = FindNextSameLineNonClosingBraceStatementStart(matchLine, statementEnd, "csharp");
        if (nextOffset <= statementEnd
            || nextOffset >= matchLine.Length)
        {
            return false;
        }

        nextSameLineOffset = nextOffset;
        return true;
    }

    private static bool TryGetJavaSameLineSemicolonSiblingOffset(string matchLine, int startColumn, out int nextSameLineOffset)
    {
        nextSameLineOffset = -1;
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var statementEnd = FindJavaSameLineStatementEnd(matchLine, startColumn);
        var semicolonIndex = statementEnd - 1;
        if (semicolonIndex < startColumn
            || semicolonIndex >= matchLine.Length
            || matchLine[semicolonIndex] != ';')
        {
            return false;
        }

        var nextOffset = FindNextSameLineBraceStatementStart(matchLine, statementEnd, "java");
        while (nextOffset >= 0
            && nextOffset < matchLine.Length
            && matchLine[nextOffset] == '}')
        {
            nextOffset = FindNextSameLineBraceStatementStart(matchLine, nextOffset + 1, "java");
        }

        if (nextOffset <= statementEnd
            || nextOffset >= matchLine.Length)
        {
            return false;
        }

        nextSameLineOffset = nextOffset;
        return true;
    }

    private static bool TryGetCSharpSameLineEventSiblingOffset(string matchLine, int startColumn, out int nextSameLineOffset)
    {
        nextSameLineOffset = -1;
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var remaining = matchLine[startColumn..];
        if (!CSharpSameLineEventStatementStartRegex.IsMatch(remaining)
            || !HasCSharpEventAccessorStart(remaining))
            return false;

        var bodyEnd = FindCSharpSameLineBraceEndColumnFromSanitized(matchLine, startColumn);
        if (bodyEnd < startColumn)
            return false;

        var nextOffset = FindNextSameLineNonClosingBraceStatementStart(matchLine, bodyEnd + 1, "csharp");
        if (nextOffset <= bodyEnd
            || nextOffset >= matchLine.Length)
        {
            return false;
        }

        nextSameLineOffset = nextOffset;
        return true;
    }

    private static bool StartsWithCSharpEventAccessorKeyword(string text, int start)
    {
        return StartsWithCSharpEventAccessorKeyword(text, start, "add")
            || StartsWithCSharpEventAccessorKeyword(text, start, "remove");
    }

    private static bool StartsWithCSharpEventAccessorKeyword(string text, int start, string keyword)
    {
        if (start < 0)
            return false;
        if (start + keyword.Length > text.Length)
            return false;
        if (!text.AsSpan(start, keyword.Length).SequenceEqual(keyword))
            return false;

        var end = start + keyword.Length;
        return end >= text.Length || !char.IsLetterOrDigit(text[end]) && text[end] != '_';
    }

    private static bool HasCSharpTopLevelFieldInitializer(string text)
    {
        int paren = 0, bracket = 0, brace = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '(':
                    paren++;
                    continue;
                case ')' when paren > 0:
                    paren--;
                    continue;
                case '[':
                    bracket++;
                    continue;
                case ']' when bracket > 0:
                    bracket--;
                    continue;
                case '{':
                    brace++;
                    continue;
                case '}' when brace > 0:
                    brace--;
                    continue;
                case '=' when paren == 0 && bracket == 0 && brace == 0:
                    var previous = i > 0 ? text[i - 1] : '\0';
                    var next = i + 1 < text.Length ? text[i + 1] : '\0';
                    if (previous is not ('=' or '!' or '<' or '>')
                        && next is not ('=' or '>'))
                    {
                        return true;
                    }

                    continue;
            }
        }

        return false;
    }

    private static int SkipWhitespace(string text, int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            cursor++;

        return cursor;
    }

    private static bool TrySkipCSharpAttributeList(string text, ref int cursor)
    {
        var start = SkipWhitespace(text, cursor);
        if (start >= text.Length || text[start] != '[')
            return false;

        var depth = 0;
        var current = start;
        while (current < text.Length)
        {
            var ch = text[current++];
            if (ch == '[')
            {
                depth++;
            }
            else if (ch == ']')
            {
                depth--;
                if (depth == 0)
                {
                    cursor = current;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TrySkipCSharpAccessorAccessibility(string text, ref int cursor)
    {
        foreach (var modifier in new[] { "protected internal", "private protected", "protected", "internal", "private", "public" })
        {
            if (StartsWithWord(text, cursor, modifier))
            {
                cursor += modifier.Length;
                return true;
            }
        }

        return false;
    }

    private static bool TrySkipCSharpAccessorModifier(string text, ref int cursor)
    {
        if (!StartsWithWord(text, cursor, "readonly"))
            return false;

        cursor += "readonly".Length;
        return true;
    }

    private static bool StartsWithCSharpAccessorKeyword(string text, int cursor) =>
        StartsWithWord(text, cursor, "get")
        || StartsWithWord(text, cursor, "set")
        || StartsWithWord(text, cursor, "init");

    private static bool StartsWithWord(string text, int cursor, string word)
    {
        if (cursor < 0 || cursor + word.Length > text.Length)
            return false;

        if (!text.AsSpan(cursor, word.Length).SequenceEqual(word.AsSpan()))
            return false;

        var end = cursor + word.Length;
        return end >= text.Length || !char.IsLetterOrDigit(text[end]) && text[end] != '_';
    }

    private static StringBuilder BuildCSharpAccessorProbeBuilder(
        string[] csharpMatchLines,
        int openBraceLineIndex,
        int openBraceExclusiveEndColumn,
        int endLineIndex)
    {
        var builder = new StringBuilder();
        var openBraceColumn = Math.Max(0, openBraceExclusiveEndColumn - 1);
        for (int i = openBraceLineIndex; i <= endLineIndex && i < csharpMatchLines.Length; i++)
        {
            AppendCSharpAccessorProbeLine(
                builder,
                csharpMatchLines[i],
                i == openBraceLineIndex ? openBraceColumn : null);
        }

        return builder;
    }

    private static void AppendCSharpAccessorProbeLine(StringBuilder builder, string sanitizedLine, int? startColumn)
    {
        var start = Math.Clamp(startColumn ?? 0, 0, sanitizedLine.Length);
        var trimmed = sanitizedLine[start..].Trim();
        if (trimmed.Length == 0)
            return;

        if (builder.Length > 0)
            builder.Append(' ');
        builder.Append(trimmed);
    }

    private static CSharpAccessorProbeStatus ClassifyCSharpAccessorProbe(string text)
    {
        var braceIndex = text.IndexOf('{');
        if (braceIndex < 0)
            return CSharpAccessorProbeStatus.Rejected;

        var cursor = SkipWhitespace(text, braceIndex + 1);
        while (true)
        {
            while (TrySkipCSharpAttributeList(text, ref cursor))
                cursor = SkipWhitespace(text, cursor);

            if (cursor >= text.Length)
                return CSharpAccessorProbeStatus.Pending;

            if (TrySkipCSharpAccessorAccessibility(text, ref cursor))
            {
                cursor = SkipWhitespace(text, cursor);
                if (cursor >= text.Length)
                    return CSharpAccessorProbeStatus.Pending;
            }
            while (TrySkipCSharpAccessorModifier(text, ref cursor))
            {
                cursor = SkipWhitespace(text, cursor);
                if (cursor >= text.Length)
                    return CSharpAccessorProbeStatus.Pending;
            }

            break;
        }

        return StartsWithCSharpAccessorKeyword(text, cursor)
            ? CSharpAccessorProbeStatus.Found
            : CSharpAccessorProbeStatus.Rejected;
    }

    private static bool IsStandaloneCSharpAccessorCandidate(string text) =>
        CSharpStandaloneAccessorRegex.IsMatch(text);

    private struct CSharpTopLevelSemicolonTracker
    {
        private int _parenDepth;
        private int _bracketDepth;
        private int _braceDepth;

        public bool HasTopLevelSemicolon { get; private set; }

        public void Scan(string text)
        {
            if (HasTopLevelSemicolon)
                return;

            for (int i = 0; i < text.Length; i++)
            {
                switch (text[i])
                {
                    case '(':
                        _parenDepth++;
                        break;
                    case ')' when _parenDepth > 0:
                        _parenDepth--;
                        break;
                    case '[':
                        _bracketDepth++;
                        break;
                    case ']' when _bracketDepth > 0:
                        _bracketDepth--;
                        break;
                    case '{':
                        _braceDepth++;
                        break;
                    case '}' when _braceDepth > 0:
                        _braceDepth--;
                        break;
                    case ';' when _parenDepth == 0 && _bracketDepth == 0 && _braceDepth == 0:
                        HasTopLevelSemicolon = true;
                        return;
                }
            }
        }
    }

    private static bool TryFindCSharpExpressionArrow(string[] lines, int startLineIndex, int endLineIndex, out int arrowLineIndex, out int arrowColumn)
    {
        var lexState = new CSharpLexState();
        for (int i = startLineIndex; i <= endLineIndex && i < lines.Length; i++)
        {
            var lexedLine = LexCSharpLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var column = lexedLine.SanitizedLine.IndexOf("=>", StringComparison.Ordinal);
            if (column >= 0)
            {
                arrowLineIndex = i;
                arrowColumn = column;
                return true;
            }
        }

        arrowLineIndex = -1;
        arrowColumn = -1;
        return false;
    }

    private static bool IsCSharpMultilineExpressionBodiedMember(string[] lines, int startLineIndex, int startColumn)
    {
        var lexState = new CSharpLexState();
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (int i = startLineIndex; i < lines.Length; i++)
        {
            var lexedLine = LexCSharpLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var fromColumn = i == startLineIndex
                ? Math.Min(Math.Max(0, startColumn), sanitizedLine.Length)
                : 0;

            for (int column = fromColumn; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                switch (ch)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')' when parenDepth > 0:
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']' when bracketDepth > 0:
                        bracketDepth--;
                        break;
                    case '{' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                        return false;
                    case '{':
                        braceDepth++;
                        break;
                    case '}' when braceDepth > 0:
                        braceDepth--;
                        break;
                    case ';' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                        return false;
                    case '=' when parenDepth == 0
                        && bracketDepth == 0
                        && braceDepth == 0
                        && column + 1 < sanitizedLine.Length
                        && sanitizedLine[column + 1] == '>':
                        return true;
                }
            }
        }

        return false;
    }

    private static int FindCSharpExpressionBodyEndLine(string[] lines, int arrowLineIndex, int arrowColumn)
    {
        var lexState = new CSharpLexState();
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (int i = arrowLineIndex; i < lines.Length; i++)
        {
            var lexedLine = LexCSharpLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var startColumn = i == arrowLineIndex
                ? Math.Min(sanitizedLine.Length, arrowColumn + 2)
                : 0;

            for (int column = startColumn; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                switch (ch)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')' when parenDepth > 0:
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']' when bracketDepth > 0:
                        bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}' when braceDepth > 0:
                        braceDepth--;
                        break;
                    case ';' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                        return i;
                }
            }
        }

        return arrowLineIndex;
    }

    private static string BuildCSharpMultilineSignature(
        string[] lines,
        int startLineIndex,
        int startColumn,
        int signatureLastLineIndex,
        int? signatureLastLineExclusiveEndColumn = null)
    {
        var builder = new StringBuilder(lines[startLineIndex].Length);
        builder.Append(lines[startLineIndex][startColumn..].TrimEnd());

        for (int i = startLineIndex + 1; i <= signatureLastLineIndex && i < lines.Length; i++)
        {
            var slice = i == signatureLastLineIndex && signatureLastLineExclusiveEndColumn.HasValue
                ? lines[i][..Math.Min(signatureLastLineExclusiveEndColumn.Value, lines[i].Length)]
                : lines[i];
            var trimmed = slice.Trim();
            if (trimmed.Length == 0)
                continue;

            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(trimmed);
        }

        return builder.ToString().Trim();
    }

    private static bool TryFindCSharpSemicolonTerminatedSignatureExtent(
        string[] lines,
        int startLineIndex,
        int startColumn,
        out int lastLineIndex,
        out int? lastLineExclusiveEndColumn)
    {
        var lexState = new CSharpLexState();
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (int i = startLineIndex; i < lines.Length; i++)
        {
            var lexedLine = LexCSharpLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var fromColumn = i == startLineIndex
                ? Math.Min(Math.Max(0, startColumn), sanitizedLine.Length)
                : 0;

            for (int column = fromColumn; column < sanitizedLine.Length; column++)
            {
                switch (sanitizedLine[column])
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')' when parenDepth > 0:
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']' when bracketDepth > 0:
                        bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}' when braceDepth > 0:
                        braceDepth--;
                        break;
                    case '}' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                        lastLineIndex = i;
                        lastLineExclusiveEndColumn = column;
                        return true;
                    case ';' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                        lastLineIndex = i;
                        lastLineExclusiveEndColumn = column + 1;
                        return true;
                }
            }
        }

        lastLineIndex = startLineIndex;
        lastLineExclusiveEndColumn = null;
        return false;
    }

    // Scan forward from a C# type declaration header (`class` / `struct` / `interface` /
    // `enum`) and find where the header ends — either at the body-opening `{` or the
    // primary-constructor / forward-declaration terminator `;`. Returns the line index
    // and column of that terminator so the signature builder can concatenate the header
    // lines up to (but not including) the terminator. Respects paren depth for primary
    // ctors, bracket depth for attributes on type / generic parameters, and uses the
    // same lexer as the rest of the C# path so that `{` / `;` inside string literals,
    // comments, or verbatim / raw strings do not short-circuit the scan. A line cap
    // prevents runaway scans on unterminated input. Closes #382.
    //
    // C# 型宣言ヘッダ（`class` / `struct` / `interface` / `enum`）の終端位置を探す。
    // 本体開きの `{` か、primary ctor / 前方宣言の `;` を終端とする。終端行と列を
    // 返すので、シグネチャ組立側はその直前までを連結できる。primary ctor 用の
    // 括弧深度、型 / ジェネリック引数へのアトリビュート用の角括弧深度を追跡し、
    // 文字列リテラル、コメント、verbatim / raw 文字列の中の `{` / `;` を
    // 誤検出しないよう、他の C# 経路と同じ lexer を共有する。未終端入力に対する
    // 暴走防止に行数上限を設ける。Closes #382.
    private const int CSharpTypeHeaderLookaheadLineLimit = 64;

    private static bool TryFindCSharpTypeHeaderExtent(
        string[] lines,
        int startLineIndex,
        int startColumn,
        out int lastLineIndex,
        out int? lastLineExclusiveEndColumn)
    {
        var lexState = new CSharpLexState();
        var parenDepth = 0;
        var bracketDepth = 0;

        var limit = Math.Min(lines.Length, startLineIndex + CSharpTypeHeaderLookaheadLineLimit);
        for (int i = startLineIndex; i < limit; i++)
        {
            var lexedLine = LexCSharpLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var fromColumn = i == startLineIndex ? startColumn : 0;

            for (int column = fromColumn; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                switch (ch)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')' when parenDepth > 0:
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']' when bracketDepth > 0:
                        bracketDepth--;
                        break;
                    case '{' when parenDepth == 0 && bracketDepth == 0:
                        lastLineIndex = i;
                        lastLineExclusiveEndColumn = column;
                        return true;
                    case ';' when parenDepth == 0 && bracketDepth == 0:
                        lastLineIndex = i;
                        lastLineExclusiveEndColumn = column;
                        return true;
                }
            }
        }

        lastLineIndex = -1;
        lastLineExclusiveEndColumn = null;
        return false;
    }

    // Build a multi-line C# type-header signature like BuildCSharpMultilineSignature, but
    // strip inline `//` and `/* ... */` comments that would otherwise leak into the stored
    // `symbols.signature` when a base list or `where` clause has a trailing or interleaved
    // comment. Uses the shared C# lexer so comment boundaries cannot be confused by `//`
    // or `/*` characters inside string, char, verbatim, or raw string literals. String
    // content is preserved (primary constructor default arguments, etc.). Closes #382.
    //
    // BuildCSharpMultilineSignature と同じく折り返された C# 型ヘッダを連結する。ただし
    // base リストや `where` 句に混ざる `//` / `/* ... */` コメントを除去し、保存される
    // `symbols.signature` に漏れないようにする。`//` / `/*` が文字列リテラル・char・
    // verbatim・raw 文字列内にある場合を誤検出しないよう共有 C# lexer を使う。文字列の
    // 中身（primary constructor のデフォルト引数など）はそのまま保持する。Closes #382.
    private static string BuildCSharpTypeHeaderSignature(
        string[] lines,
        int startLineIndex,
        int startColumn,
        int lastLineIndex,
        int? lastLineExclusiveEndColumn)
    {
        // Assemble the raw slice preserving '\n' between physical lines so multi-line raw
        // and verbatim string literals keep their newlines and leading indentation. The
        // dedicated sanitizer handles lex mode (Code / String / Verbatim / Raw / Char /
        // LineComment / BlockComment) and interpolation holes in one pass.
        // 物理行を跨ぐとき `\n` をそのまま入れてスライスを組み立て、multi-line raw や
        // verbatim 文字列リテラルの改行と行頭インデントを保持する。専用サニタイザが
        // lex モード（Code / String / Verbatim / Raw / Char / LineComment / BlockComment）
        // と補間ホールを 1 パスで処理する。
        var rawSlice = new StringBuilder();
        for (int i = startLineIndex; i <= lastLineIndex && i < lines.Length; i++)
        {
            var line = lines[i];
            // Content was split on '\n', so CRLF lines carry a trailing '\r'. Trim it so the
            // inter-line separator stays '\n' regardless of source-file line endings.
            // content は '\n' で分割しているため、CRLF 行は末尾に '\r' が残る。行間を
            // 必ず '\n' に揃えるため末尾の '\r' を落とす。
            int length = line.Length;
            if (length > 0 && line[length - 1] == '\r')
                length--;
            int from = i == startLineIndex ? Math.Clamp(startColumn, 0, length) : 0;
            int to = i == lastLineIndex && lastLineExclusiveEndColumn.HasValue
                ? Math.Clamp(lastLineExclusiveEndColumn.Value, 0, length)
                : length;
            if (to < from) to = from;
            if (i > startLineIndex)
                rawSlice.Append('\n');
            if (from < to)
                rawSlice.Append(line, from, to - from);
        }

        return SanitizeCSharpTypeHeaderSlice(rawSlice.ToString()).Trim();
    }

    private enum CSharpHeaderFrameKind
    {
        Code,
        LineComment,
        BlockComment,
        String,
        Verbatim,
        Raw,
        Char,
    }

    private struct CSharpHeaderFrame
    {
        public CSharpHeaderFrameKind Kind;
        public bool Interpolated;   // String / Verbatim / Raw: true if $-prefixed.
        public int DollarCount;     // Raw: number of '$' prefixes; also the '{' count needed to open a hole.
        public int QuoteCount;      // Raw: number of '"' in the opening delimiter; same run closes the string.
        public int HoleBraceDepth;  // Code frame inside an interpolation hole: counts nested '{' depth. 0 means next unmatched '}' exits the hole.
        public bool EscapeNext;     // String / Char: true if a preceding backslash awaits its escaped char.
    }

    // Sanitize a C# type header slice: strip `//` line comments and `/* ... */` block
    // comments, collapse runs of Code-mode whitespace (including '\n' between lines) to a
    // single space, preserve all String / Verbatim / Raw / Char literal contents verbatim
    // (including literal whitespace runs, line breaks inside raw / verbatim strings, and
    // escape sequences), and keep interpolation holes (`$"{expr}"`, `$@"{expr}"`, raw
    // `$"""{expr}"""` / `$$"""{{expr}}"""`) correctly classified as Code-mode content so
    // whitespace inside holes is collapsed while literal content outside holes is not.
    // Closes #382.
    //
    // C# 型ヘッダスライスのサニタイザ: `//` 行コメントと `/* ... */` ブロックコメントを
    // 除去し、Code モードの空白列（行間の `\n` も含む）を 1 つのスペースに畳み、String /
    // Verbatim / Raw / Char リテラルの中身（リテラル内の空白、raw / verbatim の行末改行、
    // エスケープ列）は verbatim に残し、補間ホール（`$"{expr}"`、`$@"{expr}"`、raw
    // `$"""{expr}"""` / `$$"""{{expr}}"""`）内部は Code モードとして分類してホール内の
    // 空白だけを畳み、ホール外のリテラル内容は畳まないようにする。Closes #382.
    private static string SanitizeCSharpTypeHeaderSlice(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var output = new StringBuilder(input.Length);
        var stack = new Stack<CSharpHeaderFrame>();
        stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Code });
        bool prevWasCodeSpace = false;
        int i = 0;

        while (i < input.Length)
        {
            var frame = stack.Peek();
            var ch = input[i];
            var next = i + 1 < input.Length ? input[i + 1] : '\0';

            if (frame.Kind == CSharpHeaderFrameKind.LineComment)
            {
                // `//` swallows everything to the next '\n', which then flows through
                // Code-mode whitespace handling as a single space. `//` は次の '\n' まで
                // 食いつぶし、'\n' は Code モードで 1 スペースに畳まれる。
                if (ch == '\n')
                {
                    stack.Pop();
                    continue;
                }
                i++;
                continue;
            }

            if (frame.Kind == CSharpHeaderFrameKind.BlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    stack.Pop();
                    i += 2;
                    continue;
                }
                i++;
                continue;
            }

            if (frame.Kind == CSharpHeaderFrameKind.String)
            {
                output.Append(ch);
                prevWasCodeSpace = false;

                if (frame.EscapeNext)
                {
                    frame.EscapeNext = false;
                    stack.Pop();
                    stack.Push(frame);
                    i++;
                    continue;
                }
                if (ch == '\\')
                {
                    frame.EscapeNext = true;
                    stack.Pop();
                    stack.Push(frame);
                    i++;
                    continue;
                }
                if (ch == '{' && frame.Interpolated)
                {
                    if (next == '{')
                    {
                        // `{{` is a literal brace inside an interpolated string.
                        // `{{` は補間文字列内のリテラル波括弧エスケープ。
                        output.Append(next);
                        i += 2;
                        continue;
                    }
                    stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Code });
                    i++;
                    continue;
                }
                if (ch == '}' && frame.Interpolated && next == '}')
                {
                    output.Append(next);
                    i += 2;
                    continue;
                }
                if (ch == '"')
                {
                    stack.Pop();
                    i++;
                    continue;
                }
                i++;
                continue;
            }

            if (frame.Kind == CSharpHeaderFrameKind.Verbatim)
            {
                output.Append(ch);
                prevWasCodeSpace = false;

                if (ch == '"' && next == '"')
                {
                    // `""` is a literal '"' escape inside a verbatim string.
                    // verbatim 文字列内の `""` はリテラル '"' エスケープ。
                    output.Append(next);
                    i += 2;
                    continue;
                }
                if (ch == '{' && frame.Interpolated)
                {
                    if (next == '{')
                    {
                        output.Append(next);
                        i += 2;
                        continue;
                    }
                    stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Code });
                    i++;
                    continue;
                }
                if (ch == '}' && frame.Interpolated && next == '}')
                {
                    output.Append(next);
                    i += 2;
                    continue;
                }
                if (ch == '"')
                {
                    stack.Pop();
                    i++;
                    continue;
                }
                i++;
                continue;
            }

            if (frame.Kind == CSharpHeaderFrameKind.Raw)
            {
                // In a raw string, a hole opens on a run of '{' at least DollarCount long
                // (for $$-prefixed raw strings, {{ is literal, only {{{ opens), and the
                // string closes on a run of '"' at least QuoteCount long.
                // raw 文字列では、`{` が DollarCount 個以上並んでいればホール開始、
                // 不足するならリテラルの波括弧。`"` が QuoteCount 個以上並べば文字列終端。
                if (ch == '{' && frame.Interpolated && frame.DollarCount > 0)
                {
                    int runLen = 0;
                    while (i + runLen < input.Length && input[i + runLen] == '{')
                        runLen++;
                    if (runLen >= frame.DollarCount)
                    {
                        for (int k = 0; k < frame.DollarCount; k++)
                            output.Append('{');
                        i += frame.DollarCount;
                        stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Code });
                        prevWasCodeSpace = false;
                        continue;
                    }
                    for (int k = 0; k < runLen; k++)
                        output.Append('{');
                    i += runLen;
                    prevWasCodeSpace = false;
                    continue;
                }
                if (ch == '"')
                {
                    int runLen = 0;
                    while (i + runLen < input.Length && input[i + runLen] == '"')
                        runLen++;
                    if (runLen >= frame.QuoteCount)
                    {
                        for (int k = 0; k < frame.QuoteCount; k++)
                            output.Append('"');
                        i += frame.QuoteCount;
                        stack.Pop();
                        prevWasCodeSpace = false;
                        continue;
                    }
                    for (int k = 0; k < runLen; k++)
                        output.Append('"');
                    i += runLen;
                    prevWasCodeSpace = false;
                    continue;
                }
                output.Append(ch);
                prevWasCodeSpace = false;
                i++;
                continue;
            }

            if (frame.Kind == CSharpHeaderFrameKind.Char)
            {
                output.Append(ch);
                prevWasCodeSpace = false;

                if (frame.EscapeNext)
                {
                    frame.EscapeNext = false;
                    stack.Pop();
                    stack.Push(frame);
                    i++;
                    continue;
                }
                if (ch == '\\')
                {
                    frame.EscapeNext = true;
                    stack.Pop();
                    stack.Push(frame);
                    i++;
                    continue;
                }
                if (ch == '\'')
                {
                    stack.Pop();
                    i++;
                    continue;
                }
                i++;
                continue;
            }

            // Code mode (root code or an open interpolation hole).
            // Code モード（ルート コード または 開いている補間ホール）。
            if (ch == '}' && stack.Count > 1 && frame.HoleBraceDepth == 0)
            {
                stack.Pop();
                output.Append(ch);
                prevWasCodeSpace = false;
                i++;
                continue;
            }
            if (ch == '{' && stack.Count > 1)
            {
                frame.HoleBraceDepth++;
                stack.Pop();
                stack.Push(frame);
                output.Append(ch);
                prevWasCodeSpace = false;
                i++;
                continue;
            }
            if (ch == '}' && stack.Count > 1)
            {
                frame.HoleBraceDepth--;
                stack.Pop();
                stack.Push(frame);
                output.Append(ch);
                prevWasCodeSpace = false;
                i++;
                continue;
            }

            if (ch == '/' && next == '/')
            {
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.LineComment });
                if (!prevWasCodeSpace)
                {
                    output.Append(' ');
                    prevWasCodeSpace = true;
                }
                i += 2;
                continue;
            }
            if (ch == '/' && next == '*')
            {
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.BlockComment });
                if (!prevWasCodeSpace)
                {
                    output.Append(' ');
                    prevWasCodeSpace = true;
                }
                i += 2;
                continue;
            }

            if (TryReadCSharpRawStringStart(input, i, out var rawPrefixLength, out var rawDelimiterLength))
            {
                int total = rawPrefixLength + rawDelimiterLength;
                for (int k = 0; k < total && i + k < input.Length; k++)
                    output.Append(input[i + k]);
                stack.Push(new CSharpHeaderFrame
                {
                    Kind = CSharpHeaderFrameKind.Raw,
                    Interpolated = rawPrefixLength > 0,
                    DollarCount = rawPrefixLength,
                    QuoteCount = rawDelimiterLength,
                });
                i += total;
                prevWasCodeSpace = false;
                continue;
            }

            if (ch == '$' && next == '@' && i + 2 < input.Length && input[i + 2] == '"')
            {
                output.Append(ch);
                output.Append(next);
                output.Append(input[i + 2]);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Verbatim, Interpolated = true });
                i += 3;
                prevWasCodeSpace = false;
                continue;
            }
            if (ch == '@' && next == '$' && i + 2 < input.Length && input[i + 2] == '"')
            {
                output.Append(ch);
                output.Append(next);
                output.Append(input[i + 2]);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Verbatim, Interpolated = true });
                i += 3;
                prevWasCodeSpace = false;
                continue;
            }
            if (ch == '@' && next == '"')
            {
                output.Append(ch);
                output.Append(next);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Verbatim, Interpolated = false });
                i += 2;
                prevWasCodeSpace = false;
                continue;
            }
            if (ch == '$' && next == '"')
            {
                output.Append(ch);
                output.Append(next);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.String, Interpolated = true });
                i += 2;
                prevWasCodeSpace = false;
                continue;
            }
            if (ch == '"')
            {
                output.Append(ch);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.String, Interpolated = false });
                i++;
                prevWasCodeSpace = false;
                continue;
            }
            if (ch == '\'')
            {
                output.Append(ch);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Char });
                i++;
                prevWasCodeSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!prevWasCodeSpace)
                {
                    output.Append(' ');
                    prevWasCodeSpace = true;
                }
                i++;
                continue;
            }

            output.Append(ch);
            prevWasCodeSpace = false;
            i++;
        }

        return output.ToString();
    }

    private static string CollapseCSharpGenericTypeWhitespace(string line)
        => CollapseCSharpGenericTypeWhitespace(line, out _);

    // Collapse only the whitespace that sits between generic type-argument angle brackets
    // so patterns like `Dictionary<string, int>` normalize to `Dictionary<string,int>`.
    // Preserve the separator space between a tuple element type and its element name so
    // `Dictionary<string, (int x, int y)>` still normalizes to a readable tuple shape
    // instead of merging the tokens into `intx` / `inty`.
    // tuple 要素の型と要素名の間にある区切り空白だけは残し、
    // `Dictionary<string, (int x, int y)>` が `intx` / `inty` に潰れないようにする。
    // Also emits a column-mapping array so callers can translate a column in the collapsed
    // string back to the corresponding column in the raw source. `collapsedToRaw[c]` is
    // the raw index of the character at collapsed column `c`; the final element
    // (`collapsedToRaw[collapsed.Length]`) is the sentinel `raw.Length`, which lets
    // translation use exclusive-end indices safely. When nothing collapses (early return
    // path), the map is emitted as `null` to signal identity — callers fall back to the
    // original collapsed column in that case. Closes #400.
    // ジェネリック型引数の `<...>` 内部の空白だけを取り除き、`Dictionary<string, int>` の
    // ような型を `Dictionary<string,int>` に正規化する。併せて column map を出力する。
    // `collapsedToRaw[c]` は collapsed 列 `c` に対応する raw 列で、末尾 sentinel には
    // `raw.Length` を入れているため、排他終端インデックスの変換にもそのまま使える。
    // 折り畳みが発生しない early return 経路では `null` を返し、呼び出し元は識別写像を
    // 用いる運用にしている。Closes #400.
    private static string CollapseCSharpGenericTypeWhitespace(string line, out int[]? collapsedToRaw)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains('<') || !line.Contains(' '))
        {
            collapsedToRaw = null;
            return line;
        }

        var builder = new StringBuilder(line.Length);
        var angleDepth = 0;
        var tupleDepth = 0;
        var map = new int[line.Length + 1];
        var mapLength = 0;
        var collapsed = false;

        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '<' && LooksLikeRecordGenericAngleStart(line, i))
            {
                angleDepth++;
                map[mapLength++] = i;
                builder.Append(ch);
                continue;
            }

            if (ch == '>' && angleDepth > 0)
            {
                angleDepth--;
                map[mapLength++] = i;
                builder.Append(ch);
                continue;
            }

            if (angleDepth > 0 && ch == '(')
            {
                tupleDepth++;
                map[mapLength++] = i;
                builder.Append(ch);
                continue;
            }

            if (angleDepth > 0 && ch == ')' && tupleDepth > 0)
            {
                tupleDepth--;
                map[mapLength++] = i;
                builder.Append(ch);
                continue;
            }

            if (angleDepth > 0 && char.IsWhiteSpace(ch))
            {
                collapsed = true;
                int whitespaceEnd = i + 1;
                while (whitespaceEnd < line.Length && char.IsWhiteSpace(line[whitespaceEnd]))
                    whitespaceEnd++;

                if (tupleDepth > 0 && ShouldPreserveCSharpTupleElementWhitespace(line, i, whitespaceEnd))
                {
                    if (builder.Length == 0 || builder[builder.Length - 1] != ' ')
                    {
                        map[mapLength++] = i;
                        builder.Append(' ');
                    }
                }

                i = whitespaceEnd - 1;
                continue;
            }

            map[mapLength++] = i;
            builder.Append(ch);
        }

        if (!collapsed)
        {
            collapsedToRaw = null;
            return line;
        }

        map[mapLength] = line.Length;
        if (mapLength + 1 != map.Length)
            Array.Resize(ref map, mapLength + 1);
        collapsedToRaw = map;
        return builder.ToString();
    }

    private static bool ShouldPreserveCSharpTupleElementWhitespace(string line, int whitespaceStart, int whitespaceEnd)
    {
        int previous = whitespaceStart - 1;
        while (previous >= 0 && char.IsWhiteSpace(line[previous]))
            previous--;

        if (previous < 0)
            return false;

        int next = whitespaceEnd;
        while (next < line.Length && char.IsWhiteSpace(line[next]))
            next++;

        if (next >= line.Length || !IsCSharpIdentifierStart(line[next]))
            return false;

        return IsCSharpTupleElementTypeTokenEnd(line[previous]);
    }

    private static bool IsCSharpTupleElementTypeTokenEnd(char ch)
    {
        return char.IsLetterOrDigit(ch)
            || ch == '_'
            || ch == '@'
            || ch == ')'
            || ch == ']'
            || ch == '>'
            || ch == '?'
            || ch == '*';
    }

    private static bool ShouldSkipCSharpSwitchExpressionPropertyCandidate(
        string? lang,
        SymbolPattern pattern,
        string matchLine,
        bool[]? csharpSwitchExpressionLines,
        int lineIndex) =>
        lang == "csharp"
        && pattern.Kind == "property"
        && csharpSwitchExpressionLines != null
        && csharpSwitchExpressionLines[lineIndex]
        && matchLine.Contains("=>", StringComparison.Ordinal);

    private static string[] BuildCSharpMatchLines(string[] structuralLines)
        => BuildCSharpMatchLines(structuralLines, out _);

    private static string[] BuildCSharpMatchLines(string[] structuralLines, out int[]?[] collapsedToRaw)
    {
        var matchLines = new string[structuralLines.Length];
        collapsedToRaw = new int[]?[structuralLines.Length];
        var csharpLexState = new CSharpLexState();
        var inLeadingAttributeBlock = false;
        var attributeBracketDepth = 0;
        var attributeParenDepth = 0;
        var pendingEnumDeclaration = false;
        var activeEnumBodyDepth = 0;
        for (int lineIndex = 0; lineIndex < structuralLines.Length; lineIndex++)
        {
            var lexedLine = LexCSharpLine(structuralLines[lineIndex], csharpLexState);
            csharpLexState = lexedLine.EndState;
            matchLines[lineIndex] = CollapseCSharpGenericTypeWhitespace(
                StripLeadingCSharpAttributeLists(
                    lexedLine.SanitizedLine,
                    ref inLeadingAttributeBlock,
                    ref attributeBracketDepth,
                    ref attributeParenDepth,
                    activeEnumBodyDepth > 0),
                out var lineCollapsedToRaw);
            collapsedToRaw[lineIndex] = lineCollapsedToRaw;

            var matchLine = matchLines[lineIndex];
            var trimmed = matchLine.Trim();
            var isEnumDeclarationLine = CSharpEnumDeclarationRegex.IsMatch(matchLine);
            if (isEnumDeclarationLine)
                pendingEnumDeclaration = true;

            foreach (var ch in matchLine)
            {
                if (ch == '{')
                {
                    if (pendingEnumDeclaration)
                    {
                        activeEnumBodyDepth++;
                        pendingEnumDeclaration = false;
                    }
                }
                else if (ch == '}' && activeEnumBodyDepth > 0)
                {
                    activeEnumBodyDepth--;
                }
            }

            if (pendingEnumDeclaration && trimmed.Length > 0 && trimmed != "{" && !isEnumDeclarationLine)
                pendingEnumDeclaration = false;
        }

        return matchLines;
    }

    private static CSharpLexState[] BuildCSharpLineStartStates(string[] lines)
    {
        var result = new CSharpLexState[lines.Length];
        var state = new CSharpLexState();
        for (var i = 0; i < lines.Length; i++)
        {
            result[i] = state;
            state = LexCSharpLine(lines[i], state).EndState;
        }

        return result;
    }

    private static bool IsCSharpRootCodePosition(string line, CSharpLexState lineStartState, int rawColumn)
    {
        var clampedColumn = Math.Clamp(rawColumn, 0, line.Length);
        var stateAtColumn = clampedColumn == 0
            ? lineStartState
            : LexCSharpLine(line[..clampedColumn], lineStartState).EndState;

        return stateAtColumn.Mode == CSharpLexMode.Code
            && stateAtColumn.InterpolationReturnMode == CSharpLexMode.Code
            && stateAtColumn.InterpolationBraceDepth == 0;
    }

    // Translate a column in a CollapseCSharpGenericTypeWhitespace-collapsed match line back
    // to the matching column in the raw source line. Used by the plain-field scope gate and
    // signature clamp so `public class C<T1, T2>{int X;}` does not misalign the type-body
    // scope lookup when internal generic whitespace has been collapsed away, and so field
    // signatures sliced out of the raw line preserve the original separators instead of
    // picking up phantom leading `;` from the next declarator on the same line. Closes #400.
    // CollapseCSharpGenericTypeWhitespace で空白を詰めた match 行上の列を、元の raw 行の
    // 列に戻す。`public class C<T1, T2>{int X;}` のような行で CSharpTypeBodyScope の参照列が
    // ずれないようにしたり、同一行に続くフィールドを raw から slice したときに
    // 先頭に余計な `;` が混入しないようにするため、プレーンフィールドのゲートと
    // signature clamp で利用する。Closes #400.
    private static int TranslateCSharpCollapsedColumnToRaw(int[]?[] mapPerLine, int lineIndex, int collapsedColumn, int rawLength)
    {
        if (mapPerLine == null || lineIndex < 0 || lineIndex >= mapPerLine.Length)
            return collapsedColumn;
        var map = mapPerLine[lineIndex];
        if (map == null)
            return collapsedColumn;
        if (collapsedColumn < 0)
            return 0;
        if (collapsedColumn >= map.Length)
            return rawLength;
        return map[collapsedColumn];
    }

    // Convert a raw-line column back into the per-line collapsed C# match-line domain.
    // Same-line brace-bodied generic members now keep raw columns for signature slicing,
    // but sibling rescan still runs on `csharpMatchLines[i]` (collapsed). Map the
    // closing-brace column back before calling `FindNextSameLineBraceStatementStart`, or
    // a raw column shifted right by removed generic whitespace can restart inside/past the
    // next compact sibling and make later declarations disappear. Closes #533.
    // raw 行の列を、per-line collapsed な C# match 行の列へ戻す。same-line の
    // brace-bodied generic member は signature 切り出しのため raw 列を保持するが、
    // sibling 再スキャン自体は `csharpMatchLines[i]`（collapsed）上で動く。そこで
    // `FindNextSameLineBraceStatementStart` に渡す前に閉じ brace 列を collapsed 側へ戻し、
    // generic 内で消えた空白ぶん右へずれた raw 列が次 sibling の途中/後ろから再開して
    // 後続宣言を落とすのを防ぐ。Closes #533.
    private static int TranslateCSharpRawColumnToCollapsed(int[]?[] mapPerLine, int lineIndex, int rawColumn, int collapsedLength, int rawLength)
    {
        if (mapPerLine == null || lineIndex < 0 || lineIndex >= mapPerLine.Length)
            return rawColumn;
        var map = mapPerLine[lineIndex];
        if (map == null)
            return rawColumn;
        if (rawColumn <= 0)
            return 0;
        if (map.Length == 0)
            return Math.Clamp(rawColumn, 0, collapsedLength);
        if (rawColumn >= rawLength)
            return collapsedLength;

        var lo = 0;
        var hi = map.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var mappedRaw = map[mid];
            if (mappedRaw == rawColumn)
                return mid;
            if (mappedRaw < rawColumn)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        if (hi < 0)
            return 0;
        if (hi >= map.Length)
            return collapsedLength;
        return hi;
    }

    // Gate only the block-bodied property pattern (requires `{ get|set|init ... }`).
    // Expression-bodied properties (`Name => expr;`) now also use BodyStyle.Brace so
    // FindCSharpBraceRange can detect `=>` and compute a body range, but they never
    // carry `{ get|set|init` on the match line — skipping them here would throw away
    // every expression-bodied property. Closes #233.
    // block-bodied プロパティパターン（`{ get|set|init ... }` を要求）のみガードする。
    // 式本体プロパティ（`Name => expr;`）も FindCSharpBraceRange で '=>' 本体範囲を
    // 取るため BodyStyle.Brace を使うが、match 行に `{ get|set|init` は来ないので
    // ここで弾くと式本体プロパティが全滅してしまう。Closes #233.
    private static bool TrySkipCSharpBracePropertyCandidate(
        string? lang,
        SymbolPattern pattern,
        string matchLine,
        int matchStartColumn,
        bool matchedExpressionArrow,
        out int nextSameLineOffset)
    {
        nextSameLineOffset = -1;
        if (lang != "csharp"
            || pattern.Kind != "property"
            || pattern.BodyStyle != BodyStyle.Brace)
        {
            return false;
        }

        if (matchStartColumn < 0)
            matchStartColumn = 0;
        if (matchStartColumn > matchLine.Length)
            matchStartColumn = matchLine.Length;

        // Same-line type headers can still false-positive as brace properties because the
        // C# property regex accepts omitted visibility/modifier runs. Detect a real
        // class/struct/interface/record header up front and restart from the first member
        // inside that type body, rather than from the regex match tail. The regex tail can
        // overrun into a later sibling expression-bodied property (`A => 1`) or brace-body
        // property (`P { get; set; }`), which would otherwise skip the real member that
        // should be matched next. Closes #472.
        // 同一行の型ヘッダは、visibility / modifier 省略を許す C# property regex により
        // brace-property 偽陽性になりうる。ここでは実際の
        // class/struct/interface/record ヘッダを先に検出し、regex マッチ末尾ではなく
        // 型本体の最初の member 位置から再開する。regex 末尾基準だと後続の
        // 式本体 property (`A => 1`) や brace-body property (`P { get; set; }`) まで
        // 飛び越してしまい、次に取るべき本物の member をスキップしてしまう。Closes #472.
        var matchedDeclaration = matchLine[matchStartColumn..];
        if (CSharpTypeBodyDeclarationMarker.IsMatch(matchedDeclaration))
        {
            var typeBodyOpenBrace = matchedDeclaration.IndexOf('{');
            if (typeBodyOpenBrace >= 0)
            {
                nextSameLineOffset = FindNextSameLineNonClosingBraceStatementStart(
                    matchLine,
                    matchStartColumn + typeBodyOpenBrace + 1,
                    lang);
            }

            return true;
        }

        return !matchedExpressionArrow
            && !HasCSharpPropertyAccessorStart(matchedDeclaration);
    }

    // Mark every line that sits directly inside a C# type body (class / struct /
    // interface / record / enum). Used to gate the plain-field pattern so that
    // local variable declarations inside a method, property accessor, lambda, or
    // other non-type body are not misclassified as kind `property`. The scan uses
    // `structuralLines` (strings / chars / comments already masked), so it is not
    // fooled by braces or type-declaration-looking text inside literals. Only
    // brace-delimited types push a type-body frame — `new { ... }`, collection
    // initializers, and lambda bodies all carry the `class|struct|interface|record|enum`
    // keyword absent from the preceding buffer, so they correctly stay non-type.
    // Closes #298 follow-up (codex review blocker).
    // C# の「現在この行は型本体（class / struct / interface / record / enum）の
    // 直下にあるか」を行単位で事前計算する。新しい通常フィールド抽出パターンが
    // メソッド本体・プロパティアクセサ・ラムダなど「非型本体」に含まれる
    // ローカル変数宣言を kind `property` として誤抽出しないよう、このフラグで
    // ゲートする。走査は既に文字列・文字・コメントを空白化した
    // `structuralLines` を使うため、リテラル内の `{` や `class` 相当の文字列に
    // 騙されない。`new { ... }` や collection initializer、ラムダ本体の `{` は
    // 直前バッファに `class|struct|interface|record|enum` を含まないため
    // 非型本体として扱われる。Closes #298 の codex レビュー blocker 対応。
    // Marks `{` that opens a class-like body where C# plain fields are legal.
    // `enum` is intentionally excluded: enum bodies contain enum members (not
    // fields), and the field regex would otherwise match enum member shapes like
    // `[Obsolete] A = (int)B,` as phantom `property` symbols. The column-aware
    // scope gate relies on this distinction to reject field candidates inside
    // enum bodies while still accepting legitimate fields inside class / struct
    // / interface / record bodies. Closes #400.
    // 型本体に相当する `{` を識別する正規表現。`enum` を意図的に除外することで、
    // enum 本体内の `[Obsolete] A = (int)B,` のような enum member を plain field
    // regex が `property` として拾ってしまう問題を防ぐ。列意識スコープゲートは
    // この区別を使って、enum 本体内の field 候補は拒否し、class / struct /
    // interface / record 本体内の本物のフィールドは引き続き許容する。Closes #400.
    private static readonly Regex CSharpTypeBodyDeclarationMarker = new(
        @"\b(?:class|struct|interface|record)\b\s+\w",
        RegexOptions.Compiled);

    // Expand a C# plain-field regex match into one entry per declarator when the
    // declaration is a declarator list such as `int _x, _y;`, `int _x = 5, _y;`,
    // or `int _x = 5, _y = 10;`. Two shapes need to be stitched back together:
    //
    //  1. When the later declarators have no initializer, the field regex backtracks
    //     until the first declarator with `=` or `;` terminates. Earlier names get
    //     swallowed into `returnType` (e.g. `int _x, _y;` → returnType=`int _x,`,
    //     name=`_y`). Recover them by splitting `returnType` on top-level commas and
    //     treating the last captured name as the trailing declarator.
    //
    //  2. When the first declarator carries an initializer, the regex terminates at
    //     `=` and leaves the comma-separated tail unconsumed (e.g. `int _x = 5, _y;`
    //     → returnType=`int`, name=`_x`, tail=` 5, _y;`). Walk the tail after the
    //     match to pick up additional names and their optional initializers.
    //
    // Returns null when the match is a single declarator.
    // C# の通常フィールド用 regex が `int _x, _y;` / `int _x = 5, _y;` /
    // `int _x = 5, _y = 10;` のような declarator list を捕まえた場合に、
    // 各 declarator を 1 件ずつのシンボルに展開する。復元すべき形は 2 通り:
    //
    //  1. 後段 declarator に初期化式が無い場合、regex は最初の `=` か `;` まで
    //     バックトラックし、前段の名前は returnType に吸収される
    //     （`int _x, _y;` → returnType=`int _x,`、name=`_y`）。returnType を
    //     トップレベルの `,` で分割し、regex が捕まえた最後の name を末尾の
    //     declarator として繋ぎ直す。
    //
    //  2. 先頭 declarator が初期化式を持つ場合、regex は `=` で終了し、
    //     `,` で続く後段 declarator はマッチ後のテールに残る
    //     （`int _x = 5, _y;` → returnType=`int`、name=`_x`、tail=` 5, _y;`）。
    //     マッチ末尾以降のテールを走査して追加の declarator を拾う。
    //
    // declarator list でないときは null を返す。
    private static List<(string Name, string? ReturnType)>? TryExpandCSharpFieldDeclaratorList(
        string patternMatchLine,
        int absoluteStartColumn,
        Match match,
        string? returnTypeGroup,
        string finalName)
    {
        if (string.IsNullOrEmpty(returnTypeGroup))
            return null;
        var returnTypeGroupMatch = match.Groups[returnTypeGroup];
        if (!returnTypeGroupMatch.Success)
            return null;

        var returnTypeRaw = returnTypeGroupMatch.Value;
        if (string.IsNullOrEmpty(returnTypeRaw))
            return null;

        var matchEnd = absoluteStartColumn + match.Length;
        if (matchEnd > patternMatchLine.Length)
            matchEnd = patternMatchLine.Length;
        var matchEndedAtEquals = matchEnd > 0 && patternMatchLine[matchEnd - 1] == '=';
        var tailText = matchEnd < patternMatchLine.Length
            ? patternMatchLine[matchEnd..]
            : string.Empty;

        var hasCommaInReturnType = ContainsCSharpTopLevelComma(returnTypeRaw);
        var tailDeclaratorNames = ScanCSharpTailDeclaratorNames(tailText, matchEndedAtEquals);

        if (!hasCommaInReturnType && tailDeclaratorNames.Count == 0)
            return null;

        string actualType;
        var results = new List<(string Name, string? ReturnType)>();

        if (hasCommaInReturnType)
        {
            var segments = SplitCSharpTopLevelComma(returnTypeRaw);
            if (segments.Count < 2)
                return null;
            // Expect a trailing empty segment (returnType ends with `,`). If not,
            // the comma is at an unexpected position — bail out.
            // returnType は末尾が `,` なので最後のセグメントは空のはず。そうで
            // なければ想定外の位置にある `,` なので展開を諦める。
            if (segments[^1].Trim().Length != 0)
                return null;
            segments.RemoveAt(segments.Count - 1);
            if (segments.Count < 1)
                return null;

            var firstSegment = segments[0].Trim();
            if (!TrySplitCSharpFieldTypeAndName(firstSegment, out actualType, out var firstDeclaratorName))
                return null;
            results.Add((firstDeclaratorName, actualType));

            for (int i = 1; i < segments.Count; i++)
            {
                var segment = segments[i].Trim();
                var declaratorName = StripCSharpDeclaratorInitializer(segment);
                if (string.IsNullOrEmpty(declaratorName) || !IsCSharpIdentifier(declaratorName))
                    return null;
                results.Add((declaratorName, actualType));
            }

            if (!string.IsNullOrEmpty(finalName) && IsCSharpIdentifier(finalName))
                results.Add((finalName, actualType));
        }
        else
        {
            actualType = returnTypeRaw.Trim();
            if (!string.IsNullOrEmpty(finalName) && IsCSharpIdentifier(finalName))
                results.Add((finalName, actualType));
        }

        foreach (var tailName in tailDeclaratorNames)
        {
            results.Add((tailName, actualType));
        }

        return results.Count > 1 ? results : null;
    }

    // Ruby attr_accessor / attr_reader / attr_writer declarations can list multiple
    // names on one line (`attr_accessor :a, :b, :c`). The primary regex only captures
    // the first entry, so scan the tail for additional `:name` tokens and return the
    // complete declarator list when there is real fan-out.
    // Ruby の attr_accessor / attr_reader / attr_writer は 1 行に複数名を並べられる
    // (`attr_accessor :a, :b, :c`)。primary regex は先頭の 1 件しか捕まえないため、
    // tail を走査して残りの `:name` トークンを拾い、実際に fan-out があるときだけ
    // 完全な declarator list を返す。
    private static List<string>? TryExpandRubyAttrDeclaratorList(
        string patternMatchLine,
        int absoluteStartColumn,
        Match match,
        string firstName)
    {
        if (string.IsNullOrWhiteSpace(firstName))
            return null;

        var results = new List<string> { firstName };
        var tailStart = absoluteStartColumn + match.Length;
        if (tailStart >= patternMatchLine.Length)
            return null;

        var tail = patternMatchLine[tailStart..];
        var i = 0;
        while (i < tail.Length)
        {
            while (i < tail.Length && char.IsWhiteSpace(tail[i]))
                i++;
            if (i >= tail.Length)
                break;
            if (tail[i] != ',')
                break;

            i++;
            while (i < tail.Length && char.IsWhiteSpace(tail[i]))
                i++;
            if (i >= tail.Length || tail[i] != ':')
                return null;

            i++;
            var nameStart = i;
            while (i < tail.Length && (tail[i] == '_' || char.IsLetterOrDigit(tail[i])))
                i++;

            var name = tail[nameStart..i];
            if (name.Length == 0 || !IsRubyIdentifier(name))
                return null;

            results.Add(name);
        }

        return results.Count > 1 ? results : null;
    }

    private static bool IsRubyIdentifier(string value)
    {
        if (value.Length == 0)
            return false;

        if (value[0] != '_' && !char.IsLetter(value[0]))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch != '_' && !char.IsLetterOrDigit(ch))
                return false;
        }

        return true;
    }

    private static List<string> ScanCSharpTailDeclaratorNames(string tail, bool matchEndedAtEquals)
    {
        var result = new List<string>();
        var i = 0;

        if (matchEndedAtEquals)
        {
            // Skip the initializer value until the next top-level `,` or `;`.
            // 初期化式を `,` / `;` に到達するまで読み飛ばす。
            i = SkipCSharpTopLevelValue(tail, 0);
            if (i >= tail.Length || tail[i] == ';')
                return result;
            if (tail[i] == ',')
                i++;
        }
        else
        {
            // A plain-field match that ended at `;` is a complete declaration with no
            // continuation declarators — whatever follows on the same line belongs to a
            // separate statement (e.g. a second `public int B;` on the same line inside
            // a same-line class body). Treating that residual text as `A, <tail>` would
            // pick up stray tokens like `public` and emit phantom declarator symbols.
            // Multi-declarator forms like `public int A, B;` already flow through the
            // `hasCommaInReturnType` branch in TryExpandCSharpFieldDeclaratorList, so
            // returning empty here only disables the buggy `;`-separated path.
            // Closes #400.
            // `;` で終わった plain-field マッチは、それ自体で宣言が完結しており、同一行に
            // 続く内容（例: 同一行 class 本体内の 2 つ目の `public int B;`）は別の文。
            // ここで tail をスキャンすると `public` のような周辺トークンが declarator 名
            // として拾われ、phantom シンボルになる。`public int A, B;` のような多重
            // declarator は TryExpandCSharpFieldDeclaratorList の `hasCommaInReturnType`
            // 経路で既に処理されるため、このガードは `;` 区切り経路の誤検出だけを
            // 無効化する。Closes #400.
            return result;
        }

        while (i < tail.Length)
        {
            while (i < tail.Length && char.IsWhiteSpace(tail[i]))
                i++;
            if (i >= tail.Length || tail[i] == ';')
                break;
            if (tail[i] != '_' && !char.IsLetter(tail[i]))
                break;

            var start = i;
            while (i < tail.Length && (tail[i] == '_' || char.IsLetterOrDigit(tail[i])))
                i++;
            var name = tail[start..i];
            if (!IsCSharpIdentifier(name))
                break;
            result.Add(name);

            i = SkipCSharpTopLevelValue(tail, i);
            if (i >= tail.Length || tail[i] == ';')
                break;
            if (tail[i] == ',')
            {
                i++;
                continue;
            }
            break;
        }

        return result;
    }

    private static int SkipCSharpTopLevelValue(string text, int start)
    {
        int paren = 0, bracket = 0, brace = 0;
        int i = start;
        while (i < text.Length)
        {
            var ch = text[i];
            // Distinguish generic `<...>` brackets from comparison operators by
            // looking ahead for a matching `>` before any char that cannot appear
            // inside a type expression. Without this guard, `_a = x < y ? 1 : 2, _b;`
            // inflates angle depth indefinitely and silently drops `_b`.
            // `<...>` を比較演算子と区別するため、型表現に含まれない文字が現れる前に
            // 対応する `>` が見つかるかを先読みする。これを入れないと
            // `_a = x < y ? 1 : 2, _b;` のように比較演算子を含む初期化式で angle 深さが
            // 0 に戻らず後続 declarator が静かに消える。
            if (ch == '<')
            {
                if (TryMatchCSharpGenericBracket(text, i, out var genericEnd))
                {
                    i = genericEnd + 1;
                    continue;
                }

                i++;
                continue;
            }

            switch (ch)
            {
                case '(': paren++; i++; continue;
                case ')' when paren > 0: paren--; i++; continue;
                case '[': bracket++; i++; continue;
                case ']' when bracket > 0: bracket--; i++; continue;
                case '{': brace++; i++; continue;
                case '}' when brace > 0: brace--; i++; continue;
            }

            if ((ch == ',' || ch == ';') && paren == 0 && bracket == 0 && brace == 0)
                return i;

            i++;
        }
        return text.Length;
    }

    // Look ahead from `<` at `ltIndex` and report the position of the matching `>`
    // if the span looks like a generic type argument list. Returns false when the
    // span contains a character that cannot appear inside a type expression, in
    // which case callers should treat the original `<` as a comparison operator.
    // `<` の位置から先読みし、型引数リストに見える範囲で対応する `>` の位置を返す。
    // 型に現れない文字が途中で出てきた時点で false を返し、呼び出し側はその `<` を
    // 比較演算子として扱う。
    private static bool TryMatchCSharpGenericBracket(string text, int ltIndex, out int endIndex)
    {
        int depth = 1;
        for (int i = ltIndex + 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '<')
            {
                depth++;
                continue;
            }
            if (ch == '>')
            {
                depth--;
                if (depth == 0)
                {
                    endIndex = i;
                    return true;
                }
                continue;
            }
            if (ch == ';' || ch == '{' || ch == '}' || ch == '=' || ch == '+' || ch == '-'
                || ch == '/' || ch == '%' || ch == '&' || ch == '|' || ch == '!'
                || ch == '^' || ch == '~')
            {
                endIndex = -1;
                return false;
            }
        }
        endIndex = -1;
        return false;
    }

    // Return true when the accumulated field header text reaches a top-level `;`.
    // Tracks paren/bracket/brace depth so `;` inside an initializer such as
    // `for (; ; ) { … }` never falsely marks the declaration as complete.
    // 累積ヘッダが paren/bracket/brace の深さ 0 にある `;` に到達したら true を返す。
    // `for (; ; ) { … }` のような初期化式内の `;` を完了と誤認しないよう深さを追跡する。
    private static bool HasCSharpTopLevelSemicolon(string text)
    {
        int paren = 0, bracket = 0, brace = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(': paren++; continue;
                case ')' when paren > 0: paren--; continue;
                case '[': bracket++; continue;
                case ']' when bracket > 0: bracket--; continue;
                case '{': brace++; continue;
                case '}' when brace > 0: brace--; continue;
                case ';' when paren == 0 && bracket == 0 && brace == 0:
                    return true;
            }
        }
        return false;
    }

    private static bool ContainsCSharpTopLevelComma(string text)
    {
        int angle = 0, paren = 0, bracket = 0, brace = 0;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '<': angle++; break;
                case '>' when angle > 0: angle--; break;
                case '(': paren++; break;
                case ')' when paren > 0: paren--; break;
                case '[': bracket++; break;
                case ']' when bracket > 0: bracket--; break;
                case '{': brace++; break;
                case '}' when brace > 0: brace--; break;
                case ',' when angle == 0 && paren == 0 && bracket == 0 && brace == 0:
                    return true;
            }
        }
        return false;
    }

    private static List<string> SplitCSharpTopLevelComma(string text)
    {
        var result = new List<string>();
        var segment = new StringBuilder();
        int angle = 0, paren = 0, bracket = 0, brace = 0;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '<': angle++; segment.Append(ch); continue;
                case '>' when angle > 0: angle--; segment.Append(ch); continue;
                case '(': paren++; segment.Append(ch); continue;
                case ')' when paren > 0: paren--; segment.Append(ch); continue;
                case '[': bracket++; segment.Append(ch); continue;
                case ']' when bracket > 0: bracket--; segment.Append(ch); continue;
                case '{': brace++; segment.Append(ch); continue;
                case '}' when brace > 0: brace--; segment.Append(ch); continue;
            }

            if (ch == ',' && angle == 0 && paren == 0 && bracket == 0 && brace == 0)
            {
                result.Add(segment.ToString());
                segment.Clear();
                continue;
            }

            segment.Append(ch);
        }
        result.Add(segment.ToString());
        return result;
    }

    private static bool TrySplitCSharpFieldTypeAndName(string segment, out string type, out string name)
    {
        type = string.Empty;
        name = string.Empty;
        if (string.IsNullOrEmpty(segment))
            return false;

        // Strip initializer portion if any (e.g. `int _x = 5` → `int _x`).
        segment = StripCSharpDeclaratorInitializer(segment);
        if (string.IsNullOrEmpty(segment))
            return false;

        int angle = 0, paren = 0, bracket = 0;
        var lastWhitespaceIndex = -1;
        for (int i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            switch (ch)
            {
                case '<': angle++; continue;
                case '>' when angle > 0: angle--; continue;
                case '(': paren++; continue;
                case ')' when paren > 0: paren--; continue;
                case '[': bracket++; continue;
                case ']' when bracket > 0: bracket--; continue;
            }

            if (angle == 0 && paren == 0 && bracket == 0 && char.IsWhiteSpace(ch))
                lastWhitespaceIndex = i;
        }

        if (lastWhitespaceIndex <= 0)
            return false;

        type = segment[..lastWhitespaceIndex].TrimEnd();
        name = segment[(lastWhitespaceIndex + 1)..].Trim();
        return !string.IsNullOrEmpty(type) && IsCSharpIdentifier(name);
    }

    private static string StripCSharpDeclaratorInitializer(string segment)
    {
        int angle = 0, paren = 0, bracket = 0, brace = 0;
        for (int i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            switch (ch)
            {
                case '<': angle++; continue;
                case '>' when angle > 0: angle--; continue;
                case '(': paren++; continue;
                case ')' when paren > 0: paren--; continue;
                case '[': bracket++; continue;
                case ']' when bracket > 0: bracket--; continue;
                case '{': brace++; continue;
                case '}' when brace > 0: brace--; continue;
            }

            if (ch == '=' && angle == 0 && paren == 0 && bracket == 0 && brace == 0)
            {
                // Skip `==` / `=>` — not initializers.
                if (i + 1 < segment.Length && (segment[i + 1] == '=' || segment[i + 1] == '>'))
                    continue;
                return segment[..i].TrimEnd();
            }
        }
        return segment.TrimEnd();
    }

    private static bool IsCSharpIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        if (text[0] != '_' && !char.IsLetter(text[0]))
            return false;
        for (int i = 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '_' && !char.IsLetterOrDigit(ch))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Column-aware record of the C# type-body scope on each line. Captures the state
    /// at the start of the line plus every same-line `{` / `}` transition, so a plain-field
    /// candidate at any column can be gated against the scope that actually applies there.
    /// Closes #400.
    /// 各行の C# 型本体スコープを列位置まで含めて保持する。行頭の状態と、同一行内で
    /// 発生する `{` / `}` による遷移を記録することで、任意の列にある field 候補を
    /// その位置で実際に効いているスコープで判定できるようにする。Closes #400.
    /// </summary>
    private sealed class CSharpTypeBodyScope
    {
        private readonly bool[] _lineStartInsideTypeBody;
        private readonly List<(int Column, bool IsTypeBody)>?[] _transitions;

        public CSharpTypeBodyScope(bool[] lineStartInsideTypeBody, List<(int Column, bool IsTypeBody)>?[] transitions)
        {
            _lineStartInsideTypeBody = lineStartInsideTypeBody;
            _transitions = transitions;
        }

        /// <summary>
        /// Returns whether the given (lineIndex, column) position is directly inside a type body.
        /// `{` / `}` at column X flips the state starting at column X+1, so a candidate whose
        /// match starts at column C sees every transition with `transitionColumn &lt; C`.
        /// 指定の (lineIndex, column) が型本体の直下にあるかを返す。列 X の `{` / `}` は
        /// 列 X+1 以降に状態を反映するため、列 C から始まる候補は
        /// `transitionColumn &lt; C` を満たす遷移だけを適用する。
        /// </summary>
        public bool IsInsideTypeBodyAt(int lineIndex, int column)
        {
            var state = _lineStartInsideTypeBody[lineIndex];
            var transitions = _transitions[lineIndex];
            if (transitions == null)
                return state;
            foreach (var (col, isTypeBody) in transitions)
            {
                if (col >= column)
                    break;
                state = isTypeBody;
            }
            return state;
        }
    }

    private static CSharpTypeBodyScope BuildCSharpTypeBodyScope(string[] structuralLines)
    {
        var lineStartInsideTypeBody = new bool[structuralLines.Length];
        var transitions = new List<(int Column, bool IsTypeBody)>?[structuralLines.Length];
        var scopeStack = new Stack<bool>();
        scopeStack.Push(false);
        var declBuffer = new StringBuilder();

        for (int lineIndex = 0; lineIndex < structuralLines.Length; lineIndex++)
        {
            lineStartInsideTypeBody[lineIndex] = scopeStack.Peek();

            var line = structuralLines[lineIndex];
            for (int cursor = 0; cursor < line.Length; cursor++)
            {
                var ch = line[cursor];
                if (ch == '{')
                {
                    var isTypeBody = CSharpTypeBodyDeclarationMarker.IsMatch(declBuffer.ToString());
                    scopeStack.Push(isTypeBody);
                    (transitions[lineIndex] ??= new List<(int, bool)>()).Add((cursor, isTypeBody));
                    declBuffer.Clear();
                }
                else if (ch == '}')
                {
                    if (scopeStack.Count > 1)
                        scopeStack.Pop();
                    (transitions[lineIndex] ??= new List<(int, bool)>()).Add((cursor, scopeStack.Peek()));
                    declBuffer.Clear();
                }
                else if (ch == ';')
                {
                    declBuffer.Clear();
                }
                else
                {
                    declBuffer.Append(ch);
                }
            }
        }

        return new CSharpTypeBodyScope(lineStartInsideTypeBody, transitions);
    }

    private sealed class DartClassBodyScope
    {
        private readonly bool[] _lineStartInsideClassBody;

        public DartClassBodyScope(bool[] lineStartInsideClassBody)
        {
            _lineStartInsideClassBody = lineStartInsideClassBody;
        }

        public bool IsInsideClassBodyAt(int lineIndex) => _lineStartInsideClassBody[lineIndex];
    }

    private static DartClassBodyScope BuildDartClassBodyScope(string[] structuralLines)
    {
        var lineStartInsideClassBody = new bool[structuralLines.Length];
        var scopeStack = new Stack<bool>();
        scopeStack.Push(false);
        var declBuffer = new StringBuilder();

        for (int lineIndex = 0; lineIndex < structuralLines.Length; lineIndex++)
        {
            lineStartInsideClassBody[lineIndex] = scopeStack.Peek();

            var line = structuralLines[lineIndex];
            for (int cursor = 0; cursor < line.Length; cursor++)
            {
                var ch = line[cursor];
                if (ch == '{')
                {
                    var isClassBody = DartClassDeclarationRegex.IsMatch(declBuffer.ToString());
                    scopeStack.Push(isClassBody);
                    declBuffer.Clear();
                }
                else if (ch == '}')
                {
                    if (scopeStack.Count > 1)
                        scopeStack.Pop();
                    declBuffer.Clear();
                }
                else if (ch == ';')
                {
                    declBuffer.Clear();
                }
                else
                {
                    declBuffer.Append(ch);
                }
            }
        }

        return new DartClassBodyScope(lineStartInsideClassBody);
    }

    private static bool[] FindCSharpSwitchExpressionLines(string[] structuralLines)
    {
        var switchExpressionLines = new bool[structuralLines.Length];
        var braceKinds = new Stack<bool>();
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
                    braceKinds.Push(startsSwitchExpression);
                    if (startsSwitchExpression)
                    {
                        pendingSwitchExpression--;
                        activeSwitchExpressionDepth++;
                    }

                    continue;
                }

                if (line[cursor] == '}' && braceKinds.Count > 0)
                {
                    if (braceKinds.Pop())
                        activeSwitchExpressionDepth--;
                }
            }
        }

        return switchExpressionLines;
    }

    private static string ResolveCssSymbolName(string matchLine, string name, string[] lines, int startIndex, int endLine)
    {
        if (!matchLine.TrimStart().StartsWith("@font-face", StringComparison.OrdinalIgnoreCase))
            return name;

        return TryGetCssFontFaceFamilyName(lines, startIndex, endLine, out var fontFamily)
            ? fontFamily
            : string.Empty;
    }

    private static bool TryGetCssFontFaceFamilyName(string[] lines, int startIndex, int endLine, out string fontFamily)
    {
        fontFamily = string.Empty;
        var blockLines = lines.Skip(startIndex).Take(Math.Max(1, endLine - startIndex)).ToArray();
        var maskedBlockText = string.Join('\n', MaskCssScannerLines(blockLines));
        var match = CssFontFaceDeclarationRegex.Match(maskedBlockText);
        if (!match.Success)
            return false;

        var valueStart = match.Index + match.Length;
        var valueEnd = valueStart;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        while (valueEnd < maskedBlockText.Length)
        {
            var ch = maskedBlockText[valueEnd];
            if (ch == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (ch == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;
            else if (!inSingleQuote && !inDoubleQuote && ch is ';' or '}')
                break;

            valueEnd++;
        }

        if (valueEnd == valueStart)
            return false;

        var rawBlockText = string.Join('\n', blockLines);
        var rawName = valueEnd <= rawBlockText.Length
            ? rawBlockText[valueStart..valueEnd]
            : rawBlockText[valueStart..];
        rawName = RemoveCssBlockComments(rawName).Trim();
        if (rawName.Length == 0)
            return false;

        if ((rawName[0] == '"' && rawName[^1] == '"') || (rawName[0] == '\'' && rawName[^1] == '\''))
            rawName = rawName[1..^1].Trim();

        if (rawName.Length == 0)
            return false;

        fontFamily = rawName;
        return true;
    }

    private static string RemoveCssBlockComments(string value)
    {
        if (value.Length == 0)
            return value;

        var builder = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (i + 1 < value.Length && value[i] == '/' && value[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < value.Length && !(value[i] == '*' && value[i + 1] == '/'))
                    i++;

                if (i + 1 < value.Length)
                    i++;

                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static bool ShouldSkipCssNestedSelectorCandidate(
        string? lang,
        SymbolPattern pattern,
        string matchLine,
        bool[]? cssQualifiedRuleAncestors,
        int lineIndex) =>
        lang == "css"
        && cssQualifiedRuleAncestors != null
        && cssQualifiedRuleAncestors[lineIndex]
        && pattern.Kind == "class"
        && !matchLine.TrimStart().StartsWith('@');

    // Reject JS/TS HOC candidate matches whose captured RHS uses the bare `styled.`
    // or `styled(` forms without a tagged-template backtick on the same statement.
    // The HOC regex accepts `styled[.(` `]` as the first post-identifier token so
    // the real tagged-template bindings (`styled.div\`...\``, `styled(Box)\`...\``)
    // still match, but it also lets through the factory-capture and plain-call
    // shapes (`const F = styled.div;`, `const F = styled(Box);`) which do not
    // declare a rendered component and must not be surfaced as function symbols.
    // The gate reads the raw (unmasked) source because
    // StructuralLineMasker.MaskJsTsTemplateLiteralContents replaces template
    // delimiters with space, so the masked line cannot distinguish the shapes.
    // The backtick scan is statement-local: only characters between the match end
    // and the next `;` (or next statement) are inspected, so an unrelated template
    // literal on another statement does not reopen the gate. The scanner is also
    // multi-line aware — Prettier-style styled bindings place the backtick on the
    // line after `styled.div` / `styled(Component)`, so the scan walks forward
    // across raw lines while carrying block-comment state, bounded to a short
    // lookahead window. A line that starts with a JS/TS statement-starter keyword
    // (`const`, `let`, `var`, `function`, `class`, `return`, `import`, etc.)
    // terminates the scan to model implicit ASI: `const X = styled.div\nconst Y =
    // 5;` must stay rejected even though no `;` appears on the `styled.div` line.
    // The scanner also understands line comments (`//`), block comments
    // (`/* ... */`), and plain string literals (`'...'`, `"..."`), so a backtick
    // that only lives inside a comment or string does not keep a non-template
    // binding alive, and a `;` that only lives inside a comment does not fence
    // a real backtick off from a subsequent tagged template on the same
    // statement. Closes #240 follow-up (codex review #5, #7, #8, and #9 blockers).
    // JS/TS 行における HOC 候補のうち、`styled.` / `styled(` を素のまま使い、同じ文内に
    // タグ付きテンプレートのバッククォートを持たない形（`const F = styled.div;`、
    // `const F = styled(Box);`）を弾く。HOC regex は識別子直後の `styled[.(`、`]`
    // を受け付けるためタグ付きテンプレート形（`styled.div\`...\``、`styled(Box)\`...\``）
    // はマッチさせつつ、factory 捕捉 / 素の呼び出し形も通過させてしまう。これらは
    // コンポーネントを生成しないため function シンボルとして surface してはいけない。
    // ゲートは raw 行（マスク前）を参照する — `StructuralLineMasker.MaskJsTsTemplateLiteralContents`
    // がテンプレート区切りを空白にマスクするため、マスク後では形状を区別できないのが理由。
    // バッククォート探索は文ローカル（match 終端から次の `;` または次の文まで）に限定し、
    // 別の文として配置された無関係なテンプレートリテラルでゲートを誤って解除しない。
    // さらに Prettier 整形のように `styled.div` / `styled(Component)` の次行にバッククォートを
    // 置くケースへ対応するため、スキャナはブロックコメント状態を引き継ぎつつ複数行を前方走査する
    // （行数上限付き）。継続行の最初の実トークンがタグ付きテンプレートの継続として妥当な
    // 文字（バッククォート・`.`・`<`）でない場合は ASI による文終端として走査を打ち切る。
    // これにより `const X = styled.div\nfoo(\`...\`)` や `const X = styled.div\nawait foo(\`...\`)`
    // のような「次行が式文」のケースでも phantom `function` シンボルを出さない。さらに
    // `const X = styled.div\nconst Y = 5;` のような「次行が宣言文」のケースも引き続き除外される。
    // 加えて行コメント（`//`）・ブロックコメント（`/* ... */`）・通常の文字列リテラル
    // （`'...'` / `"..."`）を構文として理解し、コメントや文字列内のバッククォートが非テンプレート
    // 束縛を延命させたり、コメント内の `;` が同一文内の本物のバッククォートより先に文終端として
    // 扱われて実タグ付きテンプレートを落とすことを防ぐ。
    // Closes #240 follow-up（codex レビュー #5・#7・#8・#9・#10・#13 の blocker 対応）。
    // The lookahead window is intentionally generous — Prettier-formatted
    // styled bindings with long `.attrs((props) => ({ ... }))` argument
    // objects routinely span more than ten lines before the backtick, and
    // truncating the scan would silently drop the binding's `function`
    // symbol. 32 lines is large enough for realistic shapes while still
    // keeping the cost bounded per match.
    // lookahead window は意図的に広めに取る — Prettier 整形で
    // `.attrs((props) => ({ ... }))` の引数オブジェクトを持つ styled 束縛は
    // 10 行を超えてからバッククォートに到達することが珍しくなく、走査を
    // 短く打ち切ると binding の `function` シンボルを silently 落としてしまう。
    // 32 行あれば実運用で見られる形は概ねカバーでき、1 マッチあたりの
    // コストも有限に保てる。
    private const int JsTsStyledFactoryGateMaxLookaheadLines = 32;

    private static bool ShouldSkipJavaScriptTypeScriptStyledFactoryCandidate(
        string? lang,
        SymbolPattern pattern,
        Match match,
        int matchOffset,
        string[] lines,
        int lineIndex)
    {
        if (lang is not ("javascript" or "typescript"))
            return false;
        if (pattern.Kind != "function" || pattern.BodyStyle != BodyStyle.None)
            return false;

        var matched = match.Value;
        var styledIdx = matched.IndexOf("styled", StringComparison.Ordinal);
        if (styledIdx < 0)
            return false;

        var afterStyled = styledIdx + "styled".Length;
        if (afterStyled >= matched.Length)
            return false;

        var next = matched[afterStyled];
        if (next != '.' && next != '(' && next != '`')
            return false;

        // `styled\`...\`` form — the match itself ends with a backtick, so it is a
        // tagged-template binding and must be kept.
        // `styled\`...\`` 形 — match 自身がバッククォートで終わるため、タグ付きテンプレート
        // 束縛として維持する。
        if (next == '`')
            return false;

        // Forward-scan raw source starting from the match's absolute end
        // position, walking across raw lines within a bounded lookahead
        // window so that Prettier-style multi-line tagged templates still
        // resolve to a real backtick. Comments (`//`, `/* ... */`) and plain
        // string literals (`'...'`, `"..."`) are skipped so only real source
        // characters drive the accept/reject decision. Block-comment state
        // carries across line boundaries.
        //
        // Two phases of operator-rejection are needed:
        //
        //   (a) BEFORE the tag-head backtick: a depth-0 operator character
        //       between the match end and the backtick (e.g.
        //       `styled.div + \`not a tag\``) breaks the tag-head chain and
        //       must reject.
        //   (b) AFTER the tag-head backtick: an operator character at depth 0
        //       that follows the closing backtick on the same expression
        //       (e.g. `styled.div\`color: red\` + theme`) also indicates the
        //       binding is a composition expression rather than a styled
        //       component, so it must reject too.
        //
        // To support (b), once a real depth-0 backtick is seen the scanner
        // walks across the entire template body — including substitutions
        // (`${ ... }`) and across raw line boundaries — to the matching
        // closing backtick, sets `tagHeadConsumed`, and continues scanning
        // for post-template operators. After tagHeadConsumed:
        //   - depth-0 `;` → accept (statement terminator).
        //   - depth-0 operator → reject (binary continuation).
        //   - End of lookahead window → accept (binding is complete).
        //
        // On every continuation line (li > lineIndex) the first real
        // (non-whitespace, non-comment) character is checked:
        //   - tagHeadConsumed=false: must be `.` or backtick (tagged-template
        //     continuation), else ASI-inserted statement termination →
        //     reject. `<` is intentionally NOT whitelisted because
        //     `<Foo>...` at statement start is a JSX element (or TS cast),
        //     not a tagged-template generic continuation — styled-components
        //     generics always appear before the backtick on the same
        //     expression.
        //   - tagHeadConsumed=true: an operator character means binary
        //     continuation of the styled expression (`styled.div\`...\`\n  +
        //     theme`) → reject; anything else (identifier, `<`, `;`, `.`)
        //     indicates the binding has cleanly terminated → accept.
        //
        // Within the scan we additionally track parenthesis / bracket /
        // angle / brace depth so that a backtick belonging to a nested
        // expression (e.g. inside `.attrs({ ... })`) does not count as the
        // tag head. When the pattern match already consumed an opening
        // paren (styled(, memo(, connect(, etc.) the scan starts with depth
        // -1 so the upcoming matching `)` restores the balance to 0 rather
        // than going further negative.
        // match 終端から raw ソースを前方走査する。Prettier 整形で複数行に
        // 跨がるタグ付きテンプレートにも追従できるよう、所定の行数まで改行を
        // またいで走査する。コメント（`//`、`/* ... */`）と通常文字列（`'...'`、
        // `"..."`）はスキップし、実ソース文字だけで判定する。ブロックコメント
        // 状態は行境界を跨いで持ち越す。
        //
        // 演算子による除外は 2 段階必要:
        //   (a) tag-head バッククォートの **前** — 例
        //       `styled.div + \`not a tag\``。match 終端と最初の depth 0
        //       バッククォートの間に depth 0 の演算子があれば即除外。
        //   (b) tag-head バッククォートの **後** — 例
        //       `styled.div\`color: red\` + theme`。closing backtick 以降で
        //       depth 0 の演算子が現れた場合、それは合成式（テーマ計算等）
        //       であって styled component の束縛ではないため除外する。
        //
        // (b) を成立させるため、depth 0 の本物のバッククォートを検出した
        // 時点でテンプレート本体（substitution `${ ... }` と複数行を含む）を
        // 閉じバッククォートまで一括スキップし、`tagHeadConsumed` を立てて
        // post-template operator 判定を続行する。tagHeadConsumed 後:
        //   - depth 0 の `;` → 採用（文の終端）。
        //   - depth 0 の演算子 → 除外（二項演算子継続）。
        //   - lookahead window の終端 → 採用（束縛は完成）。
        //
        // 継続行（li > lineIndex）の最初の実文字に対して:
        //   - tagHeadConsumed=false: `.` または backtick でなければ ASI に
        //     よる文終端として除外。`<` は JSX 要素 / TS キャストの開始に
        //     もなるため意図的に許可しない（styled-components の generics は
        //     常に同一式内で backtick の前に書かれ、新しい行の先頭には
        //     現れない）。
        //   - tagHeadConsumed=true: 演算子文字なら二項演算子の継続として
        //     除外、それ以外（識別子・`<`・`;`・`.` 等）なら束縛は綺麗に
        //     終わったとして採用する。
        //
        // 走査中は paren / bracket / angle / brace の depth を追跡し、ネスト
        // 式（例: `.attrs({ ... })` の内側）のバッククォートを tag head と
        // 誤認しないようにする。match 側が既に開き括弧（`styled(`、`memo(`
        // 等）を消費している場合は depth を -1 から始め、対応する `)` で
        // 0 に戻るようにする。
        int depth = matched.Length > 0 && matched[^1] == '(' ? -1 : 0;
        bool inBlockComment = false;
        bool tagHeadConsumed = false;
        int maxLine = Math.Min(lines.Length - 1, lineIndex + JsTsStyledFactoryGateMaxLookaheadLines);
        int li = lineIndex;
        int i = matchOffset + match.Index + match.Length;
        bool firstCharChecked = true;
        while (li <= maxLine)
        {
            var raw = lines[li];
            while (i < raw.Length)
            {
                if (inBlockComment)
                {
                    if (i + 1 < raw.Length && raw[i] == '*' && raw[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i += 2;
                        continue;
                    }
                    i++;
                    continue;
                }
                var c = raw[i];
                // Whitespace — skip so the first-meaningful-char check sees
                // the actual continuation token.
                // 空白 — 継続行先頭判定は実トークンまで進めるためスキップする。
                if (c == ' ' || c == '\t')
                {
                    i++;
                    continue;
                }
                // Line comment — the rest of this raw line is comment.
                // 行コメント — 同一 raw 行の残りは全てコメント。
                if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '/')
                    break;
                // Block comment — skip through to the matching `*/`, possibly on
                // a later raw line (state carries via `inBlockComment`).
                // ブロックコメント — `*/` まで読み飛ばし、閉じない場合は `inBlockComment`
                // を次行へ持ち越す。
                if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '*')
                {
                    inBlockComment = true;
                    i += 2;
                    continue;
                }
                if (!firstCharChecked)
                {
                    firstCharChecked = true;
                    if (depth > 0)
                    {
                        // Inside a nested expression (e.g. line 2+ of a
                        // multi-line `.attrs((props) => ({ ... }))` argument
                        // object). Continuation lines here are just
                        // expression continuation — ASI does not insert,
                        // and the leading character can be anything
                        // (identifier, `}`, etc.). Skip the first-char
                        // check and let the regular scan handle it.
                        // ネスト式の内側（例: 複数行 `.attrs((props) => ({ ... }))`
                        // 引数オブジェクトの 2 行目以降）では、継続行は単なる
                        // 式の継続であり ASI は入らない。先頭文字は識別子でも
                        // `}` でもよいので first-char 判定はスキップし通常走査
                        // に委ねる。
                    }
                    else if (tagHeadConsumed)
                    {
                        // Tag head already consumed on a previous line.
                        // Operator at the start of this line means binary
                        // continuation (`\`...\`\n  + theme`) — reject.
                        // Anything else means the styled binding has ended
                        // cleanly — accept.
                        // tag head は既に消費済み。継続行の先頭が演算子なら
                        // 二項継続なので除外、それ以外（識別子・`;`・`.` 等）
                        // なら束縛は綺麗に終わったとして採用する。
                        if (IsJsTsStyledTagHeadBreakingOperator(c))
                            return true;
                        return false;
                    }
                    else if (c != '`' && c != '.')
                    {
                        return true;
                    }
                }
                // Plain string literal — skip to the matching closing quote on
                // the same raw line. Unterminated plain strings are invalid JS/TS
                // and fall off the end of the line.
                // 通常の文字列リテラル — 同一 raw 行内の閉じクォートまで読み飛ばす。
                // 閉じない文字列は JS/TS として不正だが、そのまま行末で抜ける。
                if (c == '"' || c == '\'')
                {
                    var quote = c;
                    i++;
                    while (i < raw.Length)
                    {
                        if (raw[i] == '\\' && i + 1 < raw.Length)
                        {
                            i += 2;
                            continue;
                        }
                        if (raw[i] == quote)
                        {
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }
                if (c == '`')
                {
                    if (depth <= 0)
                    {
                        // Real tag head. Skip across the entire template body
                        // (potentially multi-line, including `${ ... }`
                        // substitutions) so that post-template operators on
                        // the same expression can still reject. Set
                        // `tagHeadConsumed` to switch the gate into
                        // post-template mode.
                        // 本物の tag head。post-template operator を検出できる
                        // よう、テンプレート本体（複数行・`${ ... }` 補間を
                        // 含む）を閉じバッククォートまで一括で読み飛ばし、
                        // `tagHeadConsumed` を立てて post-template モードに
                        // 切り替える。
                        i++;
                        int subDepth = 0;
                        bool closed = false;
                        while (li <= maxLine && !closed)
                        {
                            raw = lines[li];
                            while (i < raw.Length)
                            {
                                var tc = raw[i];
                                if (tc == '\\' && i + 1 < raw.Length)
                                {
                                    i += 2;
                                    continue;
                                }
                                if (subDepth == 0 && tc == '`')
                                {
                                    closed = true;
                                    i++;
                                    break;
                                }
                                if (subDepth == 0 && tc == '$' && i + 1 < raw.Length && raw[i + 1] == '{')
                                {
                                    subDepth = 1;
                                    i += 2;
                                    continue;
                                }
                                if (subDepth > 0)
                                {
                                    if (tc == '{') subDepth++;
                                    else if (tc == '}') subDepth--;
                                }
                                i++;
                            }
                            if (!closed)
                            {
                                li++;
                                i = 0;
                            }
                        }
                        if (!closed)
                        {
                            // Template did not close within the lookahead
                            // window — accept conservatively (the candidate
                            // still looks like a tagged template).
                            // テンプレートが lookahead window 内で閉じなかった
                            // — タグ付きテンプレート束縛と推定して保守的に採用。
                            return false;
                        }
                        tagHeadConsumed = true;
                        continue;
                    }
                    // depth > 0: nested template literal (e.g. an argument inside
                    // `.attrs(...)`). Not our tag head — skip over its body on
                    // this raw line to the matching closing backtick without
                    // interpreting `${...}` interpolation (good enough for the
                    // operator-detection pass since depth > 0 content is already
                    // outside the tag-head continuation chain).
                    // depth > 0: ネストしたテンプレートリテラル（例: `.attrs(...)` の引数内）。
                    // tag head ではないため、同一 raw 行内で閉じバッククォートまで読み飛ばす。
                    // `${...}` の補間は解釈しないが、depth > 0 のコンテンツは既に tag-head
                    // チェーン外なので operator 判定には影響しない。
                    i++;
                    while (i < raw.Length && raw[i] != '`')
                    {
                        if (raw[i] == '\\' && i + 1 < raw.Length)
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                    }
                    if (i < raw.Length) i++;
                    continue;
                }
                // Arrow function token `=>` — skip as a unit so neither the
                // `=` operator branch nor the `>` close-bracket branch fires.
                // Without this, `(props) => ({})` would falsely treat `>` as
                // closing an angle-bracket and decrement depth, exposing
                // subsequent depth-0 operator characters (e.g. `?`, `:`,
                // `+`) inside the arrow body to false rejection.
                // 矢印関数 `=>` を一括スキップ。これがないと `(props) => ({})`
                // で `>` が close-bracket と誤解釈され、depth が不正に減って
                // arrow body 内の depth 0 演算子（`?`・`:`・`+` 等）が誤除外
                // されてしまう。
                if (c == '=' && i + 1 < raw.Length && raw[i + 1] == '>')
                {
                    i += 2;
                    continue;
                }
                if (c == ';')
                {
                    if (depth <= 0)
                        return !tagHeadConsumed;
                    i++;
                    continue;
                }
                if (tagHeadConsumed && depth <= 0 && (c == '<' || c == '>'))
                    return true;
                if (c == '(' || c == '[' || c == '<' || c == '{')
                {
                    depth++;
                    i++;
                    continue;
                }
                if (c == ')' || c == ']' || c == '>' || c == '}')
                {
                    depth--;
                    i++;
                    continue;
                }
                // Depth-0 operator characters break the tag-head continuation
                // chain — the candidate is not a styled tagged-template binding.
                // After tagHeadConsumed, the same operator characters indicate
                // a post-template binary expression (`\`...\` + theme`), which
                // is also not a styled binding.
                // depth 0 の演算子文字は tag-head 継続チェーンを切るため除外する。
                // tagHeadConsumed 後でも同様で、テンプレート後の二項演算式
                // （`\`...\` + theme` 等）は styled 束縛ではない。
                if (depth <= 0 && IsJsTsStyledTagHeadBreakingOperator(c))
                    return true;
                i++;
            }
            li++;
            i = 0;
            firstCharChecked = false;
        }
        return !tagHeadConsumed;
    }

    private static bool IsJsTsStyledTagHeadBreakingOperator(char c) => c switch
    {
        '+' or '-' or '*' or '%' or '?' or '!' or '&' or '|' or '^' or '=' or ',' or ':' or '<' or '>' or '/' => true,
        _ => false,
    };

    private static bool[] FindCssQualifiedRuleAncestors(string[] lines)
    {
        var ancestors = new bool[lines.Length];
        var contexts = new Stack<CssContextKind>();

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            ancestors[lineIndex] = contexts.Contains(CssContextKind.QualifiedRule);
            var line = lines[lineIndex];
            var segmentStart = 0;
            for (int cursor = 0; cursor < line.Length; cursor++)
            {
                var ch = line[cursor];
                if (ch == '{')
                {
                    var segment = line[segmentStart..cursor].Trim();
                    var contextKind = segment.StartsWith("@", StringComparison.Ordinal)
                        ? CssContextKind.GroupingAtRule
                        : CssContextKind.QualifiedRule;
                    contexts.Push(contextKind);
                    segmentStart = cursor + 1;
                }
                else if (ch == '}' && contexts.Count > 0)
                {
                    contexts.Pop();
                    segmentStart = cursor + 1;
                }
                else if (ch == ';')
                {
                    segmentStart = cursor + 1;
                }
            }
        }

        return ancestors;
    }

    private static string[] MaskCssScannerLines(string[] originalLines)
    {
        var maskedLines = new string[originalLines.Length];
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inUrlToken = false;
        var urlParenDepth = 0;

        for (int lineIndex = 0; lineIndex < originalLines.Length; lineIndex++)
        {
            var line = originalLines[lineIndex];
            var chars = line.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (inBlockComment)
                {
                    chars[i] = ' ';
                    if (i + 1 < chars.Length && line[i] == '*' && line[i + 1] == '/')
                    {
                        chars[i + 1] = ' ';
                        inBlockComment = false;
                        i++;
                    }

                    continue;
                }

                if (!inSingleQuote && !inDoubleQuote && i + 1 < chars.Length && line[i] == '/' && line[i + 1] == '*')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    inBlockComment = true;
                    i++;
                    continue;
                }

                if (inUrlToken)
                {
                    chars[i] = ' ';

                    if (line[i] == '"' && !inSingleQuote)
                    {
                        inDoubleQuote = !inDoubleQuote;
                        continue;
                    }

                    if (line[i] == '\'' && !inDoubleQuote)
                    {
                        inSingleQuote = !inSingleQuote;
                        continue;
                    }

                    if ((inSingleQuote || inDoubleQuote) && line[i] == '\\' && i + 1 < chars.Length)
                    {
                        chars[i + 1] = ' ';
                        i++;
                        continue;
                    }

                    if (!inSingleQuote && !inDoubleQuote)
                    {
                        if (line[i] == '(')
                            urlParenDepth++;
                        else if (line[i] == ')')
                        {
                            urlParenDepth--;
                            if (urlParenDepth <= 0)
                            {
                                inUrlToken = false;
                                urlParenDepth = 0;
                            }
                        }
                    }

                    continue;
                }

                if (!inSingleQuote
                    && !inDoubleQuote
                    && !inUrlToken
                    && i + 3 < chars.Length
                    && (line[i] == 'u' || line[i] == 'U')
                    && (line[i + 1] == 'r' || line[i + 1] == 'R')
                    && (line[i + 2] == 'l' || line[i + 2] == 'L')
                    && line[i + 3] == '(')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    chars[i + 2] = ' ';
                    chars[i + 3] = ' ';
                    inUrlToken = true;
                    urlParenDepth = 1;
                    i += 3;
                    continue;
                }

                if (!inSingleQuote && !inDoubleQuote && !inUrlToken && i + 1 < chars.Length && line[i] == '/' && line[i + 1] == '/')
                {
                    for (int j = i; j < chars.Length; j++)
                        chars[j] = ' ';

                    break;
                }

                if ((inSingleQuote || inDoubleQuote) && line[i] == '\\' && i + 1 < chars.Length)
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i++;
                    continue;
                }

                if (line[i] == '"' && !inSingleQuote)
                {
                    chars[i] = ' ';
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (line[i] == '\'' && !inDoubleQuote)
                {
                    chars[i] = ' ';
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (inSingleQuote || inDoubleQuote)
                    chars[i] = ' ';
            }

            maskedLines[lineIndex] = new string(chars);
        }

        return maskedLines;
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

    private static void CollectRecordPrimaryComponentSymbols(
        long fileId,
        string lang,
        string[] lines,
        int declarationLineIndex,
        int declarationStartColumn,
        string kind,
        string recordName,
        List<PendingRecordPrimaryComponents> pendingRecordPrimaryComponents,
        List<SymbolRecord> symbols)
    {
        if (lang == "kotlin")
        {
            if (kind is not "class" and not "enum")
                return;
        }
        else if (kind is not "class" and not "struct")
        {
            return;
        }

        if (!TryGetRecordPrimaryComponents(
            lang,
            lines,
            declarationLineIndex,
            declarationStartColumn,
            kind,
            recordName,
            out var components,
            out var declarationEndLine))
            return;

        var parentSymbol = symbols.LastOrDefault(symbol =>
            symbol.FileId == fileId
            && symbol.Kind == kind
            && symbol.Name == recordName
            && symbol.StartLine == declarationLineIndex + 1);
        if (parentSymbol != null)
            parentSymbol.EndLine = Math.Max(parentSymbol.EndLine, declarationEndLine);

        if (components.Count > 0)
        {
            pendingRecordPrimaryComponents.Add(new PendingRecordPrimaryComponents(
                fileId,
                kind,
                recordName,
                declarationLineIndex + 1,
                components));
        }
    }

    private static void MaterializeRecordPrimaryComponentSymbols(
        List<SymbolRecord> symbols,
        List<PendingRecordPrimaryComponents> pendingRecordPrimaryComponents)
    {
        foreach (var pending in pendingRecordPrimaryComponents)
        {
            var parentSymbol = symbols.LastOrDefault(symbol =>
                symbol.FileId == pending.FileId
                && symbol.Kind == pending.Kind
                && symbol.Name == pending.RecordName
                && symbol.StartLine == pending.RecordStartLine);
            if (parentSymbol == null)
                continue;

            foreach (var component in pending.Components)
            {
                if (symbols.Any(symbol =>
                    symbol.FileId == pending.FileId
                    && symbol.Kind == "property"
                    && symbol.Name == component.Name
                    && symbol.ContainerKind == pending.Kind
                    && symbol.ContainerName == pending.RecordName
                    && symbol.StartLine >= parentSymbol.StartLine
                    && symbol.EndLine <= parentSymbol.EndLine))
                {
                    continue;
                }

                symbols.Add(new SymbolRecord
                {
                    FileId = pending.FileId,
                    Kind = "property",
                    Name = component.Name,
                    Line = component.Line,
                    StartLine = component.Line,
                    EndLine = component.Line,
                    Signature = component.Signature,
                    ContainerKind = pending.Kind,
                    ContainerName = pending.RecordName,
                    Visibility = component.Visibility ?? "public",
                    ReturnType = component.Type,
                });
            }
        }
    }

    private static bool TryGetRecordPrimaryComponents(
        string lang,
        string[] lines,
        int declarationLineIndex,
        int declarationStartColumn,
        string kind,
        string recordName,
        out List<RecordPrimaryComponent> components,
        out int declarationEndLine)
    {
        components = [];
        declarationEndLine = declarationLineIndex + 1;

        if (lang is not "csharp" and not "java" and not "kotlin")
            return false;

        var declaration = lang == "kotlin"
            ? CollectKotlinPrimaryConstructorDeclarationText(lines, declarationLineIndex, declarationStartColumn)
            : CollectRecordDeclarationText(lines, declarationLineIndex, declarationStartColumn);
        if (string.IsNullOrWhiteSpace(declaration))
            return false;

        var recordRegex = GetCurrentDeclarationRecordRegex(lang, kind, recordName);
        var javaLeadingAnnotationOffset = 0;
        var recordMatch = lang == "java"
            ? (TryMatchJavaDeclarationSegment(recordRegex, declaration, out var javaRecordMatch, out javaLeadingAnnotationOffset)
                ? javaRecordMatch
                : recordRegex.Match(declaration))
            : recordRegex.Match(declaration);
        if (!recordMatch.Success)
            return false;

        var parameterOpenIndex = FindRecordPrimaryComponentListStart(
            declaration,
            recordMatch.Index + recordMatch.Length + javaLeadingAnnotationOffset);
        if (parameterOpenIndex < 0)
            return false;

        var parameterCloseIndex = FindMatchingRecordPrimaryComponentListEnd(declaration, parameterOpenIndex);
        if (parameterCloseIndex <= parameterOpenIndex)
            return false;
        var declarationTerminatorIndex = FindRecordDeclarationTerminatorIndex(declaration, parameterCloseIndex + 1);
        var declarationLineSpanEnd = declarationTerminatorIndex >= 0 ? declarationTerminatorIndex + 1 : parameterCloseIndex + 1;
        declarationEndLine = declarationLineIndex + 1 + declaration[..declarationLineSpanEnd].Count(ch => ch == '\n');

        var rawParameterList = StripRecordComponentComments(declaration[(parameterOpenIndex + 1)..parameterCloseIndex]);
        foreach (var rawComponent in SplitTopLevelRecordPrimaryComponents(rawParameterList, declarationLineIndex + 1))
        {
            if (TryParseRecordPrimaryComponent(lang, rawComponent, out var component))
                components.Add(component);
        }

        return true;
    }

    private static string CollectRecordDeclarationText(string[] lines, int declarationLineIndex, int declarationStartColumn)
    {
        var builder = new System.Text.StringBuilder();
        var parameterOpenIndex = -1;
        var parameterCloseIndex = -1;
        for (int i = declarationLineIndex; i < lines.Length; i++)
        {
            if (builder.Length > 0)
                builder.Append('\n');

            // Content was split on '\n', so CRLF lines carry a trailing '\r'. Strip it before
            // appending so intermediate separators stay '\n' and the collected declaration
            // text is stable across OS line endings (#405 follow-up to #382).
            // content は '\n' で分割しているため、CRLF 行は末尾に '\r' が残る。行間の区切りを
            // '\n' に揃え、OS 差分で collected text が変わらないよう '\r' を落として追加する
            // （#382 に続く #405 対応）。
            var line = lines[i];
            var lineText = i == declarationLineIndex
                ? line[Math.Min(declarationStartColumn, line.Length)..]
                : line;
            builder.Append(StripTrailingCr(lineText));

            var declaration = builder.ToString();
            if (parameterOpenIndex < 0)
            {
                parameterOpenIndex = FindRecordPrimaryComponentListStart(declaration, 0);
                if (parameterOpenIndex < 0)
                    continue;
            }

            if (parameterCloseIndex < 0)
            {
                parameterCloseIndex = FindMatchingRecordPrimaryComponentListEnd(declaration, parameterOpenIndex);
                if (parameterCloseIndex <= parameterOpenIndex)
                    continue;
            }

            if (FindRecordDeclarationTerminatorIndex(declaration, parameterCloseIndex + 1) >= 0)
                return declaration;
        }

        return builder.ToString();
    }

    private static string CollectKotlinPrimaryConstructorDeclarationText(string[] lines, int declarationLineIndex, int declarationStartColumn)
    {
        const int KotlinPrimaryConstructorDeclarationLookaheadLineLimit = 32;

        var builder = new System.Text.StringBuilder();
        var parameterOpenIndex = -1;
        var parameterCloseIndex = -1;
        for (int i = declarationLineIndex; i < lines.Length && i < declarationLineIndex + KotlinPrimaryConstructorDeclarationLookaheadLineLimit; i++)
        {
            if (builder.Length > 0)
                builder.Append('\n');

            // Kotlin class headers may split across a few physical lines. Keep the collected
            // declaration stable across line endings while bounding the scan so a class without
            // a primary constructor does not cause a whole-file read.
            // Kotlin の class ヘッダは数行に分割されうる。改行差を吸収しつつ、primary
            // constructor を持たない class でファイル全体を走査しないよう、収集範囲を
            // ほどよい行数に制限する。
            var line = lines[i];
            var lineText = i == declarationLineIndex
                ? line[Math.Min(declarationStartColumn, line.Length)..]
                : line;
            builder.Append(StripTrailingCr(lineText));

            var declaration = builder.ToString();
            if (parameterOpenIndex < 0)
            {
                parameterOpenIndex = FindRecordPrimaryComponentListStart(declaration, 0);
                if (parameterOpenIndex < 0)
                {
                    if (FindRecordDeclarationTerminatorIndex(declaration, 0) >= 0)
                        return declaration;

                    continue;
                }
            }

            if (parameterCloseIndex < 0)
            {
                parameterCloseIndex = FindMatchingRecordPrimaryComponentListEnd(declaration, parameterOpenIndex);
                if (parameterCloseIndex <= parameterOpenIndex)
                    continue;
            }

            return declaration[..(parameterCloseIndex + 1)];
        }

        return builder.ToString();
    }

    private static Regex GetCurrentDeclarationRecordRegex(string lang, string kind, string recordName)
    {
        if (lang == "csharp")
        {
            return kind == "struct"
                ? new Regex(@"^\s*(?:(?:public|private|protected\s+internal|private\s+protected|protected|internal)\s+)?(?:(?:static|partial|readonly|file|new|ref|unsafe)\s+)*record\s+struct\s+" + Regex.Escape(recordName) + @"\b", RegexOptions.CultureInvariant)
                : new Regex(@"^\s*(?:(?:public|private|protected\s+internal|private\s+protected|protected|internal)\s+)?(?:(?:static|partial|abstract|sealed|readonly|file|new|unsafe)\s+)*record(?:\s+class)?\s+" + Regex.Escape(recordName) + @"\b", RegexOptions.CultureInvariant);
        }

        if (lang == "kotlin")
        {
            return kind == "enum"
                ? new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:abstract|data|sealed|open|inner|value|annotation|expect|actual)\s+)*enum\s+class\s+" + Regex.Escape(recordName) + @"\b", RegexOptions.CultureInvariant)
                : new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:abstract|data|sealed|open|inner|value|annotation|expect|actual)\s+)*(?:class|object)\s+" + Regex.Escape(recordName) + @"\b", RegexOptions.CultureInvariant);
        }

        return new Regex(@"^\s*(?:public|private|protected)?\s*(?:(?:static|final|abstract|sealed|non-sealed|strictfp)\s+)*record\s+" + Regex.Escape(recordName) + @"\b", RegexOptions.CultureInvariant);
    }

    private static int FindRecordPrimaryComponentListStart(string declaration, int startIndex)
    {
        var angleDepth = 0;
        for (int i = Math.Max(0, startIndex); i < declaration.Length; i++)
        {
            var ch = declaration[i];
            if (ch == '<')
            {
                angleDepth++;
                continue;
            }

            if (ch == '>')
            {
                if (angleDepth > 0)
                    angleDepth--;
                continue;
            }

            if (ch == '(' && angleDepth == 0)
                return i;
        }

        return -1;
    }

    private static int FindMatchingRecordPrimaryComponentListEnd(string declaration, int openIndex)
    {
        var parenDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inLineComment = false;
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = openIndex; i < declaration.Length; i++)
        {
            var ch = declaration[i];
            var next = i + 1 < declaration.Length ? declaration[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '/' when next == '/':
                    inLineComment = true;
                    i++;
                    continue;
                case '/' when next == '*':
                    inBlockComment = true;
                    i++;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    parenDepth--;
                    if (parenDepth == 0)
                        return i;
                    continue;
                case '<' when LooksLikeRecordGenericAngleStart(declaration, i):
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
            }
        }

        return -1;
    }

    private static int FindRecordDeclarationTerminatorIndex(string declaration, int startIndex)
    {
        var parenDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inLineComment = false;
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = Math.Max(0, startIndex); i < declaration.Length; i++)
        {
            var ch = declaration[i];
            var next = i + 1 < declaration.Length ? declaration[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '/' when next == '/':
                    inLineComment = true;
                    i++;
                    continue;
                case '/' when next == '*':
                    inBlockComment = true;
                    i++;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '<' when LooksLikeRecordGenericAngleStart(declaration, i):
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{' when parenDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return i;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
                case ';' when parenDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return i;
            }
        }

        return -1;
    }

    private static List<RecordPrimaryComponentSlice> SplitTopLevelRecordPrimaryComponents(string parameterList, int firstLineNumber)
    {
        var components = new List<RecordPrimaryComponentSlice>();
        var builder = new System.Text.StringBuilder();
        var parenDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;
        var currentLineNumber = firstLineNumber;
        var componentLineNumber = firstLineNumber;
        var componentHasToken = false;

        for (int index = 0; index < parameterList.Length; index++)
        {
            var ch = parameterList[index];
            if (!componentHasToken && !char.IsWhiteSpace(ch))
            {
                componentHasToken = true;
                componentLineNumber = currentLineNumber;
            }

            if (escapeNext)
            {
                builder.Append(ch);
                escapeNext = false;
                if (ch == '\n')
                    currentLineNumber++;
                continue;
            }

            if (inSingleQuote)
            {
                builder.Append(ch);
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                else if (ch == '\n')
                    currentLineNumber++;
                continue;
            }

            if (inDoubleQuote)
            {
                builder.Append(ch);
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                else if (ch == '\n')
                    currentLineNumber++;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    builder.Append(ch);
                    continue;
                case '"':
                    inDoubleQuote = true;
                    builder.Append(ch);
                    continue;
                case '(':
                    parenDepth++;
                    builder.Append(ch);
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    builder.Append(ch);
                    continue;
                case '<' when LooksLikeRecordGenericAngleStart(parameterList, index):
                    angleDepth++;
                    builder.Append(ch);
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    builder.Append(ch);
                    continue;
                case '[':
                    bracketDepth++;
                    builder.Append(ch);
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    builder.Append(ch);
                    continue;
                case '{':
                    braceDepth++;
                    builder.Append(ch);
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    builder.Append(ch);
                    continue;
                case ',' when parenDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    var component = builder.ToString().Trim();
                    if (component.Length > 0)
                        components.Add(new RecordPrimaryComponentSlice(component, componentLineNumber));
                    builder.Clear();
                    componentHasToken = false;
                    componentLineNumber = currentLineNumber;
                    continue;
                default:
                    builder.Append(ch);
                    if (ch == '\n')
                        currentLineNumber++;
                    continue;
            }
        }

        var trailingComponent = builder.ToString().Trim();
        if (trailingComponent.Length > 0)
            components.Add(new RecordPrimaryComponentSlice(trailingComponent, componentLineNumber));

        return components;
    }

    private static string StripRecordComponentComments(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var inLineComment = false;
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                {
                    inLineComment = false;
                    builder.Append(ch);
                }

                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                    builder.Append(' ');
                }
                else if (ch == '\n')
                {
                    builder.Append(ch);
                }

                continue;
            }

            if (escapeNext)
            {
                builder.Append(ch);
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                builder.Append(ch);
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                builder.Append(ch);
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            if (ch == '\'' )
            {
                inSingleQuote = true;
                builder.Append(ch);
                continue;
            }

            if (ch == '"')
            {
                inDoubleQuote = true;
                builder.Append(ch);
                continue;
            }

            if (ch == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (ch == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool TryParseRecordPrimaryComponent(string lang, RecordPrimaryComponentSlice rawComponent, out RecordPrimaryComponent component)
    {
        component = default;
        if (string.IsNullOrWhiteSpace(rawComponent.Text))
            return false;

        var normalized = TrimAfterTopLevelEquals(rawComponent.Text).Trim();
        if (normalized.Length == 0)
            return false;

        var componentLine = rawComponent.Line;
        string? visibility = null;
        if (lang == "kotlin")
        {
            var stripped = StripLeadingJavaRecordComponentAnnotations(normalized);
            normalized = stripped.Text;
            componentLine += stripped.ConsumedNewlines;

            stripped = StripLeadingKotlinConstructorPropertyModifiers(normalized, out visibility);
            normalized = stripped.Text;
            componentLine += stripped.ConsumedNewlines;

            if (!StartsWithKotlinPropertyKeyword(normalized, out var propertyKeywordLength))
                return false;

            normalized = normalized[propertyKeywordLength..];
            var keywordWhitespaceConsumed = 0;
            normalized = TrimLeadingWhitespaceAndCountNewlines(normalized, ref keywordWhitespaceConsumed);
            componentLine += keywordWhitespaceConsumed;

            var separatorIndex = FindKotlinPropertyTypeSeparatorIndex(normalized);
            if (separatorIndex <= 0)
                return false;

            var kotlinComponentName = normalized[..separatorIndex].Trim();
            var kotlinComponentType = normalized[(separatorIndex + 1)..].Trim();
            if (kotlinComponentName.Length == 0 || kotlinComponentType.Length == 0)
                return false;

            component = new RecordPrimaryComponent(kotlinComponentName, kotlinComponentType, normalized, componentLine, visibility);
            return true;
        }
        else
        {
            var stripped = lang == "csharp"
                ? StripLeadingCSharpRecordComponentAttributes(normalized)
                : StripLeadingJavaRecordComponentAnnotations(normalized);
            normalized = stripped.Text;
            componentLine += stripped.ConsumedNewlines;

            stripped = StripLeadingRecordComponentModifiers(lang, normalized);
            normalized = stripped.Text;
            componentLine += stripped.ConsumedNewlines;
        }
        if (normalized.Length == 0)
            return false;

        var nameMatch = Regex.Match(normalized, @"(?<name>@?[\p{L}_$][\p{L}\p{Nd}_$]*)\s*$", RegexOptions.CultureInvariant);
        if (!nameMatch.Success)
            return false;

        var componentName = nameMatch.Groups["name"].Value.TrimStart('@');
        var componentType = normalized[..nameMatch.Index].Trim();
        if (componentName.Length == 0 || componentType.Length == 0)
            return false;

        component = new RecordPrimaryComponent(componentName, componentType, normalized, componentLine, visibility);
        return true;
    }

    private static string TrimAfterTopLevelEquals(string text)
    {
        var parenDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '<' when LooksLikeRecordGenericAngleStart(text, i):
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
                case '=' when parenDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return text[..i].TrimEnd();
            }
        }

        return text;
    }

    private static StrippedRecordComponentText StripLeadingCSharpRecordComponentAttributes(string component)
    {
        var consumedNewlines = 0;
        var trimmed = TrimLeadingWhitespaceAndCountNewlines(component, ref consumedNewlines);
        while (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            var endIndex = FindMatchingBracket(trimmed, 0, '[', ']');
            if (endIndex < 0)
                return new(component.Trim(), 0);

            consumedNewlines += CountNewlines(trimmed.AsSpan(0, endIndex + 1));
            trimmed = TrimLeadingWhitespaceAndCountNewlines(trimmed[(endIndex + 1)..], ref consumedNewlines);
        }

        return new(trimmed, consumedNewlines);
    }

    private static bool LooksLikeRecordGenericAngleStart(string text, int index)
    {
        if (index < 0 || index >= text.Length || text[index] != '<')
            return false;

        var previousIndex = FindPreviousNonWhitespaceIndex(text, index - 1);
        if (previousIndex < 0)
            return false;

        var nextIndex = FindNextNonWhitespaceIndex(text, index + 1);
        if (nextIndex < 0)
            return false;

        var previous = text[previousIndex];
        var next = text[nextIndex];
        if (!IsRecordGenericAnglePredecessor(previous)
            || !IsRecordGenericAngleSuccessor(next))
        {
            return false;
        }

        return TryFindRecordGenericAngleEnd(text, index, out _);
    }

    private static bool IsRecordGenericAnglePredecessor(char ch) =>
        char.IsLetterOrDigit(ch)
        || ch is '_' or '$' or '@' or '.' or '>' or ')' or ']' or '?';

    private static bool IsRecordGenericAngleSuccessor(char ch) =>
        char.IsLetter(ch)
        || ch is '_' or '$' or '@' or '?' or '(';

    private static int FindPreviousNonWhitespaceIndex(string text, int index)
    {
        for (int i = Math.Min(index, text.Length - 1); i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
                return i;
        }

        return -1;
    }

    private static int FindNextNonWhitespaceIndex(string text, int index)
    {
        for (int i = Math.Max(0, index); i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return i;
        }

        return -1;
    }

    private static bool TryFindRecordGenericAngleEnd(string text, int openIndex, out int closeIndex)
    {
        closeIndex = -1;

        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inLineComment = false;
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '/' when next == '/':
                    inLineComment = true;
                    i++;
                    continue;
                case '/' when next == '*':
                    inBlockComment = true;
                    i++;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
                case '<' when i == openIndex || LooksLikeRecordGenericAngleCandidate(text, i):
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth == 0)
                        continue;

                    angleDepth--;
                    if (angleDepth == 0)
                    {
                        if (!IsRecordGenericAnglePayloadTypeLike(text.AsSpan(openIndex + 1, i - openIndex - 1)))
                            return false;

                        closeIndex = i;
                        return true;
                    }

                    continue;
            }
        }

        return false;
    }

    private static bool LooksLikeRecordGenericAngleCandidate(string text, int index)
    {
        if (index < 0 || index >= text.Length || text[index] != '<')
            return false;

        var previousIndex = FindPreviousNonWhitespaceIndex(text, index - 1);
        if (previousIndex < 0)
            return false;

        var nextIndex = FindNextNonWhitespaceIndex(text, index + 1);
        if (nextIndex < 0)
            return false;

        return IsRecordGenericAnglePredecessor(text[previousIndex])
            && IsRecordGenericAngleSuccessor(text[nextIndex]);
    }

    private static bool IsRecordGenericAnglePayloadTypeLike(ReadOnlySpan<char> text)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inLineComment = false;
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '/' when next == '/':
                    inLineComment = true;
                    i++;
                    continue;
                case '/' when next == '*':
                    inBlockComment = true;
                    i++;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
            }

            if (parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0
                && ch is '=' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '!' or '^' or '~' or ';')
            {
                return false;
            }
        }

        return true;
    }

    private static StrippedRecordComponentText StripLeadingJavaRecordComponentAnnotations(string component)
    {
        var consumedNewlines = 0;
        var trimmed = TrimLeadingWhitespaceAndCountNewlines(component, ref consumedNewlines);
        while (trimmed.StartsWith("@", StringComparison.Ordinal))
        {
            var index = 1;
            while (index < trimmed.Length && (char.IsLetterOrDigit(trimmed[index]) || trimmed[index] is '_' or '$' or '.'))
                index++;

            if (index < trimmed.Length && trimmed[index] == ':')
            {
                index++;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
                    index++;
                while (index < trimmed.Length && (char.IsLetterOrDigit(trimmed[index]) || trimmed[index] is '_' or '$' or '.'))
                    index++;
            }

            while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
                index++;

            if (index < trimmed.Length && trimmed[index] == '(')
            {
                var endIndex = FindMatchingBracket(trimmed, index, '(', ')');
                if (endIndex < 0)
                    return new(component.Trim(), 0);

                index = endIndex + 1;
            }

            consumedNewlines += CountNewlines(trimmed.AsSpan(0, index));
            trimmed = TrimLeadingWhitespaceAndCountNewlines(trimmed[index..], ref consumedNewlines);
        }

        return new(trimmed, consumedNewlines);
    }

    private static StrippedRecordComponentText StripLeadingKotlinConstructorPropertyModifiers(
        string component,
        out string? visibility)
    {
        visibility = null;
        var consumedNewlines = 0;
        var trimmed = TrimLeadingWhitespaceAndCountNewlines(component, ref consumedNewlines);
        string[] modifiers = ["public", "private", "protected", "internal", "vararg"];
        var removedModifier = true;
        while (removedModifier)
        {
            removedModifier = false;
            foreach (var modifier in modifiers)
            {
                if (trimmed.StartsWith(modifier, StringComparison.Ordinal)
                    && trimmed.Length > modifier.Length
                    && char.IsWhiteSpace(trimmed[modifier.Length]))
                {
                    if (modifier is "public" or "private" or "protected" or "internal")
                        visibility ??= modifier;

                    trimmed = TrimLeadingWhitespaceAndCountNewlines(trimmed[(modifier.Length + 1)..], ref consumedNewlines);
                    removedModifier = true;
                    break;
                }
            }
        }

        return new(trimmed, consumedNewlines);
    }

    private static int FindKotlinPropertyTypeSeparatorIndex(string text)
    {
        var parenDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '<':
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
                case ':' when parenDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return i;
            }
        }

        return -1;
    }

    private static bool StartsWithKotlinPropertyKeyword(string text, out int keywordLength)
    {
        if (text.StartsWith("val", StringComparison.Ordinal)
            && (text.Length == 3 || !IsKotlinPropertyKeywordPart(text[3])))
        {
            keywordLength = 3;
            return true;
        }

        if (text.StartsWith("var", StringComparison.Ordinal)
            && (text.Length == 3 || !IsKotlinPropertyKeywordPart(text[3])))
        {
            keywordLength = 3;
            return true;
        }

        keywordLength = 0;
        return false;
    }

    private static bool IsKotlinPropertyKeywordPart(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_';

    private static StrippedRecordComponentText StripLeadingRecordComponentModifiers(string lang, string component)
    {
        ReadOnlySpan<string> modifiers = lang == "csharp"
            ? ["params", "this", "ref", "out", "in", "scoped", "readonly"]
            : ["final"];

        var consumedNewlines = 0;
        var trimmed = TrimLeadingWhitespaceAndCountNewlines(component, ref consumedNewlines);
        var removedModifier = true;
        while (removedModifier)
        {
            removedModifier = false;
            foreach (var modifier in modifiers)
            {
                if (trimmed.StartsWith(modifier, StringComparison.Ordinal)
                    && trimmed.Length > modifier.Length
                    && char.IsWhiteSpace(trimmed[modifier.Length]))
                {
                    trimmed = TrimLeadingWhitespaceAndCountNewlines(trimmed[(modifier.Length + 1)..], ref consumedNewlines);
                    removedModifier = true;
                    break;
                }
            }
        }

        return new(trimmed, consumedNewlines);
    }

    private static string TrimLeadingWhitespaceAndCountNewlines(string text, ref int consumedNewlines)
    {
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            if (text[index] == '\n')
                consumedNewlines++;
            index++;
        }

        return text[index..];
    }

    private static int CountNewlines(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
                count++;
        }

        return count;
    }

    private static int FindMatchingBracket(string text, int openIndex, char openBracket, char closeBracket)
    {
        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

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

            if (ch == openBracket)
            {
                depth++;
                continue;
            }

            if (ch == closeBracket)
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static void PopulateDeclaredContainerQualifiedNames(List<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.ContainerKind == null
                || symbol.ContainerName == null)
            {
                continue;
            }

            var container = symbols
                .Where(candidate =>
                candidate.FileId == symbol.FileId
                && candidate.Kind == symbol.ContainerKind
                && candidate.Name == symbol.ContainerName
                && candidate.StartLine <= symbol.StartLine
                && candidate.EndLine >= symbol.EndLine)
                .OrderByDescending(candidate => candidate.StartLine)
                .ThenBy(candidate => candidate.EndLine)
                .FirstOrDefault();
            if (container == null)
                continue;

            symbol.ContainerQualifiedName = container.ContainerQualifiedName != null
                ? $"{container.ContainerQualifiedName}.{container.Name}"
                : container.Name;
        }
    }

    private static void AssignContainers(
        List<SymbolRecord> symbols,
        string[]? rawLines = null,
        CSharpLexState[]? csharpLineStartStates = null)
    {
        var ordered = symbols
            .Select((symbol, originalIndex) => new { Symbol = symbol, OriginalIndex = originalIndex })
            .OrderBy(entry => entry.Symbol.StartLine)
            .ThenBy(entry => entry.Symbol.StartColumn.HasValue ? 0 : 1)
            .ThenBy(entry => entry.Symbol.StartColumn ?? int.MaxValue)
            .ThenByDescending(entry => entry.Symbol.EndLine)
            .ThenByDescending(entry => entry.Symbol.Signature?.Length ?? 0)
            .ThenBy(entry => entry.OriginalIndex)
            .Select(entry => entry.Symbol)
            .ToList();

        var stack = new Stack<SymbolRecord>();
        foreach (var symbol in ordered)
        {
            while (stack.Count > 0 && !IsFileScopedNamespace(stack.Peek()) && symbol.StartLine > stack.Peek().EndLine)
                stack.Pop();

            var containerPath = GetEffectiveContainerPath(stack, symbol, rawLines, csharpLineStartStates);

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
                        var effectiveParentPath = containerPath.Take(containerPath.Count - 1);
                        symbol.ContainerQualifiedName = BuildQualifiedContainerName(effectiveParentPath);
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

    private static IReadOnlyList<SymbolRecord> GetEffectiveContainerPath(
        IEnumerable<SymbolRecord> containers,
        SymbolRecord symbol,
        string[]? rawLines = null,
        CSharpLexState[]? csharpLineStartStates = null)
    {
        var orderedContainers = containers.Reverse().ToList();
        var containingContainers = orderedContainers
            .Where(container => ContainsSymbol(container, symbol, rawLines, csharpLineStartStates))
            .ToList();

        if (containingContainers.Count == 0)
            return [];

        if (symbol.Kind == "enum" && symbol.BodyStartLine == null)
        {
            var enumIndex = containingContainers.FindLastIndex(container => container.Kind == "enum");
            if (enumIndex >= 0)
                return containingContainers.Take(enumIndex + 1).ToList();
        }

        return containingContainers;
    }

    private static string? BuildQualifiedContainerName(IEnumerable<SymbolRecord> containers)
    {
        var names = containers
            .Select(container => container.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        return names.Count > 0
            ? string.Join(".", names)
            : null;
    }

    private static string? BuildInheritedFamilyKey(SymbolRecord container, string? qualifiedContainerName) =>
        SupportsCrossFileFamily(container)
            ? qualifiedContainerName
            : null;

    private static string? BuildSelfFamilyKey(SymbolRecord symbol, IEnumerable<SymbolRecord> containers)
    {
        if (!SupportsCrossFileFamily(symbol))
            return null;

        var names = containers
            .Select(container => container.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Append(symbol.Name)
            .ToList();

        return names.Count > 0
            ? string.Join(".", names)
            : null;
    }

    private static bool SupportsCrossFileFamily(SymbolRecord symbol) =>
        symbol.Kind is "class" or "interface" or "struct"
        && !string.IsNullOrWhiteSpace(symbol.Signature)
        && PartialModifierRegex.IsMatch(symbol.Signature);

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
        CSharpLexState[]? csharpLineStartStates = null)
    {
        if (IsFileScopedNamespace(container))
            return candidate.StartLine > container.StartLine;

        if (container.BodyStartLine == null || container.BodyEndLine == null)
            return false;

        if (candidate.StartLine == container.StartLine)
        {
            if (TryContainsCSharpSameLineSymbolByRawLine(container, candidate, rawLines, csharpLineStartStates, out var containsSameLineSymbol))
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

        return IsInsideCSharpClosingBraceLineContainer(container, candidate, rawLines, csharpLineStartStates);
    }

    private static bool TryContainsCSharpSameLineSymbolByRawLine(
        SymbolRecord container,
        SymbolRecord candidate,
        string[]? rawLines,
        CSharpLexState[]? csharpLineStartStates,
        out bool contains)
    {
        contains = false;
        if (rawLines == null
            || container.Signature == null
            || candidate.Signature == null
            || container.StartLine != candidate.StartLine
            || container.StartLine <= 0
            || container.StartLine > rawLines.Length
            || csharpLineStartStates == null
            || container.StartLine > csharpLineStartStates.Length
            || !CanContainSameLineSymbol(container, candidate))
        {
            return false;
        }

        var lineIndex = container.StartLine - 1;
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
        CSharpLexState[]? csharpLineStartStates)
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
            csharpLineStartStates != null && lineIndex < csharpLineStartStates.Length
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

    private static string? TryGetGroup(Match match, string? groupName)
    {
        if (groupName == null || !match.Groups[groupName].Success)
            return null;

        return NormalizeMetadata(match.Groups[groupName].Value);
    }

    private static string? NormalizeMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static string NormalizeExtractedSymbolName(string? lang, string name, Match match, string matchLine)
    {
        return lang switch
        {
            "csharp" => NormalizeCSharpSymbolName(name, match, matchLine),
            "fsharp" => NormalizeFSharpSymbolName(name),
            "kotlin" => NormalizeKotlinSymbolName(name, matchLine),
            "sql" => NormalizeSqlSymbolName(name),
            _ => name,
        };
    }

    private static string NormalizeFSharpSymbolName(string name)
    {
        if (name.Length >= 4 && name.StartsWith("``", StringComparison.Ordinal) && name.EndsWith("``", StringComparison.Ordinal))
            return name[2..^2];

        return name;
    }

    private static string NormalizeCSharpSymbolName(string name, Match match, string matchLine)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        if (match.Groups["conversionKind"].Success
            && TryReadCSharpConversionOperatorName(match, matchLine, out var conversionOperatorName))
        {
            return conversionOperatorName;
        }

        if (name == "this" && match.Value.Contains("this", StringComparison.Ordinal) && match.Value.Contains('[', StringComparison.Ordinal))
            return "Item";

        // Canonicalize verbatim identifier prefixes (`@` escape) so the persisted
        // symbol name matches the writer-side import / base-resolution canonical
        // form in `DbWriter.StripCSharpVerbatimPrefixes`. `@BaseAttr` -> `BaseAttr`,
        // `@Foo.@Bar` -> `Foo.Bar` (namespaces). Without this, iter 5's import-aware
        // resolver and iter 6's base normalizer cannot match a class declared as
        // `public class @BaseAttr : Attribute` since its persisted name would stay
        // `@BaseAttr` while imports / qualified lookups use `BaseAttr`. Mirrors the
        // one-way canonicalization policy: the `@` escape is purely syntactic.
        // verbatim 識別子（`@` エスケープ）を canonical 化し、永続化されたシンボル名と
        // `DbWriter.StripCSharpVerbatimPrefixes` の正規化側のキーが一致するようにする。
        // `@BaseAttr` -> `BaseAttr`、`@Foo.@Bar` -> `Foo.Bar`（名前空間）。これをしないと
        // `public class @BaseAttr : Attribute` の class 行が `@BaseAttr` のまま永続化され、
        // iter 5 の import-aware resolver と iter 6 の base 正規化では一致しない。
        // `@` エスケープは純粋に構文上のものであるという一方向 canonical 化の方針に従う。
        return NormalizeCSharpVerbatimIdentifiers(name);
    }

    private static string NormalizeKotlinSymbolName(string name, string matchLine)
    {
        var trimmedLine = matchLine.TrimStart();
        if (!trimmedLine.StartsWith("companion object", StringComparison.Ordinal))
            return name;

        var trimmedName = name.Trim();
        return string.IsNullOrWhiteSpace(trimmedName)
            || string.Equals(trimmedName, "companion object", StringComparison.Ordinal)
            ? "Companion"
            : name;
    }

    private static void NormalizeKotlinSecondaryConstructorNames(List<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function"
                || symbol.ContainerKind != "class"
                || string.IsNullOrWhiteSpace(symbol.ContainerName))
            {
                continue;
            }

            var signature = symbol.Signature?.TrimStart();
            if (string.IsNullOrWhiteSpace(signature))
                continue;

            var isSecondaryConstructor = signature.StartsWith("constructor", StringComparison.Ordinal)
                || signature.StartsWith("public constructor", StringComparison.Ordinal)
                || signature.StartsWith("private constructor", StringComparison.Ordinal)
                || signature.StartsWith("protected constructor", StringComparison.Ordinal)
                || signature.StartsWith("internal constructor", StringComparison.Ordinal);
            if (!isSecondaryConstructor)
                continue;

            symbol.Name = symbol.ContainerName;
        }
    }

    private static string NormalizeSqlSymbolName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        var trimmed = name.Trim();
        var normalized = new StringBuilder(trimmed.Length);
        char quote = '\0';
        var pendingWhitespace = false;

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (quote != '\0')
            {
                normalized.Append(ch);
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < trimmed.Length && trimmed[i + 1] == ']')
                        {
                            normalized.Append(']');
                            i++;
                        }
                        else
                        {
                            quote = '\0';
                        }
                    }
                }
                else if (ch == quote)
                {
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == quote)
                    {
                        normalized.Append(quote);
                        i++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                pendingWhitespace = normalized.Length > 0;
                continue;
            }

            if (ch == '.')
            {
                if (normalized.Length == 0 || normalized[^1] == '.')
                    continue;

                normalized.Append('.');
                pendingWhitespace = false;
                while (i + 1 < trimmed.Length && char.IsWhiteSpace(trimmed[i + 1]))
                    i++;
                continue;
            }

            if (pendingWhitespace)
            {
                normalized.Append(' ');
                pendingWhitespace = false;
            }

            normalized.Append(ch);
            if (ch is '[' or '"' or '`')
                quote = ch;
        }

        return normalized.ToString();
    }

    // Mirror of DbWriter.StripCSharpVerbatimPrefixes — strip `@` verbatim escapes from
    // identifier starts with segment boundaries at string start, `.`, and `::`. Kept
    // local to SymbolExtractor so the extractor has no dependency on the Database layer.
    // DbWriter.StripCSharpVerbatimPrefixes のミラー。文字列先頭、`.`、`::` を境界として
    // 各識別子先頭の verbatim `@` を剥がす。Extractor が Database 層に依存しないようローカルに置く。
    private static string StripCSharpVerbatimPrefixes(string qualified)
    {
        if (qualified.Length == 0 || qualified.IndexOf('@') < 0)
            return qualified;
        var sb = new System.Text.StringBuilder(qualified.Length);
        bool atBoundary = true;
        for (int i = 0; i < qualified.Length; i++)
        {
            char c = qualified[i];
            if (atBoundary && c == '@'
                && i + 1 < qualified.Length
                && (qualified[i + 1] == '_' || char.IsLetter(qualified[i + 1])))
            {
                atBoundary = false;
                continue;
            }
            sb.Append(c);
            if (c == '.')
            {
                atBoundary = true;
            }
            else if (c == ':' && i + 1 < qualified.Length && qualified[i + 1] == ':')
            {
                sb.Append(':');
                i++;
                atBoundary = true;
            }
            else
            {
                atBoundary = false;
            }
        }
        return sb.Length == qualified.Length ? qualified : sb.ToString();
    }

    private static bool TryReadCSharpConversionOperatorName(Match match, string matchLine, out string name)
    {
        name = string.Empty;

        var conversionKind = match.Groups["conversionKind"].Value.Trim();
        if (conversionKind.Length == 0)
            return false;

        var cursor = match.Index + match.Length;
        while (cursor < matchLine.Length && char.IsWhiteSpace(matchLine[cursor]))
            cursor++;

        var hasChecked = false;
        if (StartsWithKeyword(matchLine, cursor, "checked"))
        {
            hasChecked = true;
            cursor += "checked".Length;
            while (cursor < matchLine.Length && char.IsWhiteSpace(matchLine[cursor]))
                cursor++;
        }

        if (!TryReadCSharpTypeUntilParameterList(matchLine, cursor, out var targetType))
            return false;

        var normalizedTargetType = NormalizeCSharpTypeDisplayName(targetType);
        name = hasChecked
            ? $"{conversionKind} operator checked {normalizedTargetType}"
            : $"{conversionKind} operator {normalizedTargetType}";
        return true;
    }

    private static bool TryReadCSharpTypeUntilParameterList(string line, int startIndex, out string typeName)
    {
        typeName = string.Empty;
        var builder = new StringBuilder();
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenDepth = 0;
        var sawAnyTypeToken = false;

        for (var index = startIndex; index < line.Length; index++)
        {
            var ch = line[index];
            switch (ch)
            {
                case '(':
                    if (angleDepth == 0 && bracketDepth == 0 && parenDepth == 0 && sawAnyTypeToken)
                    {
                        typeName = builder.ToString().Trim();
                        return typeName.Length > 0;
                    }

                    parenDepth++;
                    builder.Append(ch);
                    if (!char.IsWhiteSpace(ch))
                        sawAnyTypeToken = true;
                    break;

                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    builder.Append(ch);
                    if (!char.IsWhiteSpace(ch))
                        sawAnyTypeToken = true;
                    break;

                case '<':
                    angleDepth++;
                    builder.Append(ch);
                    sawAnyTypeToken = true;
                    break;

                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    builder.Append(ch);
                    sawAnyTypeToken = true;
                    break;

                case '[':
                    bracketDepth++;
                    builder.Append(ch);
                    sawAnyTypeToken = true;
                    break;

                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    builder.Append(ch);
                    sawAnyTypeToken = true;
                    break;

                default:
                    builder.Append(ch);
                    if (!char.IsWhiteSpace(ch))
                        sawAnyTypeToken = true;
                    break;
            }
        }

        return false;
    }

    private static bool StartsWithKeyword(string line, int startIndex, string keyword)
    {
        if (startIndex < 0 || startIndex + keyword.Length > line.Length)
            return false;

        if (!string.Equals(line.Substring(startIndex, keyword.Length), keyword, StringComparison.Ordinal))
            return false;

        var nextIndex = startIndex + keyword.Length;
        return nextIndex >= line.Length || char.IsWhiteSpace(line[nextIndex]);
    }

    private static string NormalizeCSharpTypeDisplayName(string typeName)
    {
        var normalized = CSharpTypeWhitespaceRegex.Replace(typeName.Trim(), " ");
        normalized = CSharpTypeDoubleColonWhitespaceRegex.Replace(normalized, "::");
        normalized = CSharpTypeDotWhitespaceRegex.Replace(normalized, ".");
        normalized = NormalizeCSharpTypeTokenSpacing(normalized);
        return NormalizeCSharpVerbatimIdentifiers(normalized);
    }

    private static string NormalizeCSharpVerbatimIdentifiers(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('@', StringComparison.Ordinal) < 0)
            return value;

        StringBuilder? builder = null;
        var segmentStart = 0;

        for (var index = 0; index < value.Length; index++)
        {
            if (!IsCSharpVerbatimIdentifierPrefix(value, index))
                continue;

            builder ??= new StringBuilder(value.Length);
            if (index > segmentStart)
                builder.Append(value, segmentStart, index - segmentStart);
            segmentStart = index + 1;
        }

        if (builder is null)
            return value;

        if (segmentStart < value.Length)
            builder.Append(value, segmentStart, value.Length - segmentStart);
        return builder.ToString();
    }

    private static bool IsCSharpVerbatimIdentifierPrefix(string value, int index)
    {
        if (value[index] != '@' || index + 1 >= value.Length || !IsCSharpIdentifierStart(value[index + 1]))
            return false;

        return index == 0 || !IsCSharpIdentifierChar(value[index - 1]);
    }

    private static bool IsCSharpIdentifierStart(char ch) =>
        ch == '_' || char.IsLetter(ch);

    private static bool IsCSharpIdentifierChar(char ch) =>
        ch == '_' || char.IsLetterOrDigit(ch);

    private static string NormalizeCSharpTypeTokenSpacing(string typeName)
    {
        var builder = new StringBuilder(typeName.Length);

        for (var index = 0; index < typeName.Length; index++)
        {
            var ch = typeName[index];
            switch (ch)
            {
                case ' ':
                    var previous = GetLastNonWhitespace(builder);
                    var next = FindNextNonWhitespace(typeName, index + 1);
                    if (!previous.HasValue || !next.HasValue)
                        continue;

                    if (ShouldInsertCSharpTypeSpace(previous.Value, next.Value) && (builder.Length == 0 || builder[^1] != ' '))
                        builder.Append(' ');
                    break;

                case ',':
                    TrimTrailingWhitespace(builder);
                    builder.Append(',');
                    var nextAfterComma = FindNextNonWhitespace(typeName, index + 1);
                    if (nextAfterComma.HasValue && nextAfterComma.Value is not ')' and not '>' and not ']')
                        builder.Append(' ');
                    break;

                case '<':
                case '>':
                case '[':
                case ']':
                case '(':
                case ')':
                case '?':
                    TrimTrailingWhitespace(builder);
                    builder.Append(ch);
                    break;

                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    private static char? GetLastNonWhitespace(StringBuilder builder)
    {
        for (var index = builder.Length - 1; index >= 0; index--)
        {
            if (!char.IsWhiteSpace(builder[index]))
                return builder[index];
        }

        return null;
    }

    private static char? FindNextNonWhitespace(string text, int startIndex)
    {
        for (var index = startIndex; index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
                return text[index];
        }

        return null;
    }

    private static void TrimTrailingWhitespace(StringBuilder builder)
    {
        while (builder.Length > 0 && char.IsWhiteSpace(builder[^1]))
            builder.Length--;
    }

    private static bool ShouldInsertCSharpTypeSpace(char previous, char next)
    {
        if (IsCSharpTypeIdentifierChar(previous) && IsCSharpTypeIdentifierStart(next))
            return true;

        return previous is '>' or ']' or ')' or '?' or '*'
            && IsCSharpTypeIdentifierStart(next);
    }

    private static bool IsCSharpTypeIdentifierStart(char ch)
    {
        return ch == '@' || ch == '_' || char.IsLetter(ch);
    }

    private static bool IsCSharpTypeIdentifierChar(char ch)
    {
        return IsCSharpTypeIdentifierStart(ch) || char.IsDigit(ch);
    }

    private static readonly Regex ComplexityRegex = new(
        @"\b(?:if|else\s+if|elif|elsif|elseif|case|catch|except|when|while|for|foreach|guard)\b|(?:\?\?|&&|\|\||[?:](?!=))",
        RegexOptions.Compiled);
    private static readonly Regex CSharpTypeWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex CSharpTypeDoubleColonWhitespaceRegex = new(@"\s*::\s*", RegexOptions.Compiled);
    private static readonly Regex CSharpTypeDotWhitespaceRegex = new(@"\s*\.\s*", RegexOptions.Compiled);
    private static readonly Regex JavaModuleRequiresDirectiveRegex = new(
        @"^\s*requires\s+(?:transitive\s+|static\s+)*(?<name>[\w.]+)\s*;$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JavaModuleExportsOrOpensDirectiveRegex = new(
        @"^\s*(?:exports|opens)\s+(?<name>[\w.]+)(?:\s+to\s+[\w.,\s]+)?\s*;$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JavaModuleUsesOrProvidesDirectiveRegex = new(
        @"^\s*(?:uses|provides)\s+(?<name>[\w.]+)(?:\s+with\s+[\w.,\s]+)?\s*;$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] JavaModuleDirectiveKeywords = ["requires", "exports", "opens", "uses", "provides"];

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
