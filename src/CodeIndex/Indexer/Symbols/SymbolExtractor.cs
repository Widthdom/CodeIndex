using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Extracts symbols (functions, classes, imports) using regex patterns.
/// 正規表現を使ってシンボル（関数、クラス、インポート）を抽出する。
/// </summary>
public static partial class SymbolExtractor
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
    // Swift declarations commonly carry attributes on the same line as the declaration keyword.
    // Allow those prefixes so annotated declarations still index by their actual names.
    // Swift の宣言では、宣言キーワードと同じ行に属性が付くことが多い。
    // その前置きを許容し、注釈付き宣言でも実際の名前でインデックスできるようにする。
    private const string SwiftAttributePattern = @"(?:@\w+(?:\([^)]*\))?\s+)*";
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
        @"(?:__attribute__\s*\(\((?:(?>[^()]+)|\((?<CAttributeDepth>)|\)(?<-CAttributeDepth>))*(?(CAttributeDepth)(?!))\)\)\s*|__declspec\s*\((?:(?>[^()]+)|\((?<CAttributeDepth>)|\)(?<-CAttributeDepth>))*(?(CAttributeDepth)(?!))\)\s*|_Noreturn\s+)";
    private const string CFunctionReturnTypePattern =
        @"(?<returnType>(?:(?:\w+[\s*]+)|" + CAttributeSpecifierTokenPattern + @")+)";
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
    private static readonly Regex CobolProgramIdLineRegex = new(
        @"^\s*(?:IDENTIFICATION\s+DIVISION\.\s*)?PROGRAM-ID\.\s*(?<name>[A-Z0-9][A-Z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolProcedureDivisionRegex = new(
        @"^\s*PROCEDURE\s+DIVISION\.\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolSectionHeaderRegex = new(
        @"^\s{0,6}(?<name>[A-Z0-9][A-Z0-9-]*)\s+SECTION\.\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolParagraphHeaderRegex = new(
        @"^\s{0,6}(?<name>[A-Z0-9][A-Z0-9-]*)\.\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CobolEndProgramRegex = new(
        @"^\s*END\s+PROGRAM(?:\s+(?<name>[A-Z0-9][A-Z0-9-]*))?\.\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PhpGroupUseRegex = new(
        @"^\s*use\s+(?:(?<type>function|const)\s+)?(?<prefix>[\w\\]+\\)\{\s*(?<items>[^{}]+?)\s*\}\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PhpUseRegex = new(
        @"^\s*use\s+(?:(?<type>function|const)\s+)?(?<name>[\w\\]+)(?:\s+as\s+(?<alias>\w+))?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PhpRequireIncludeRegex = new(
        @"^\s*(?:require|include)(?:_once)?\s*\(?\s*(?:'(?<singleName>[^']+)'|""(?<doubleName>[^""]+)"")\s*\)?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PhpPrefixedRequireIncludeRegex = new(
        @"^\s*(?:require|include)(?:_once)?\s*\(?\s*(?<prefix>(?:(?:__DIR__|__FILE__|dirname\s*\(\s*__FILE__\s*\))\s*\.\s*)+)\s*(?:'(?<singleName>[^']+)'|""(?<doubleName>[^""]+)"")\s*\)?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // `delegate` is a non-type keyword only when it is NOT followed by `*` — `delegate*<...>` is a valid return type.
    // `delegate` は `*` を伴わないときだけ非型キーワード扱い。`delegate*<...>` は戻り値型として有効。
    private const string CSharpNonTypeKeywordPattern = @"(?:(?:public|private|protected|internal|static|sealed|partial|readonly|unsafe|extern|virtual|override|abstract|async|new|file|required|ref)\b|delegate\b(?!\s*\*))";
    private const string CFunctionStartBlacklistPattern = @"^(?!\s*typedef\b)(?!\s*(?:if|else|for|while|switch|return|sizeof)\s*[\(\{;])";
    private const string CFunctionNameBlacklistPattern = @"(?!(?:int|void|char|short|long|float|double|signed|unsigned|bool|_Bool|size_t|ssize_t|intptr_t|uintptr_t|int8_t|int16_t|int32_t|int64_t|uint8_t|uint16_t|uint32_t|uint64_t)\b)";
    private const string CppFunctionStartBlacklistPattern = @"^(?!\s*typedef\b)(?!\s*(?:if|else|for|while|switch|return|sizeof|using|namespace)\s*[\(\{;<])";
    private const string CppTemplatePrefixPattern = @"(?:template\s*<[^>]*>\s*)*";
    private const string CppTypeAtomPattern = @"(?:decltype\s*\(\s*auto\s*\)|[\w:<>~]+)";
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
        @"^\s*(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoInterfaceEmbeddedTypeRegex = new(
        @"^\s*(?:~\s*)?(?<name>[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)(?:\[[^\]\r\n]+\])?\s*$",
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
    private static readonly Regex RustUseStartRegex = new(
        @"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?use\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RustUseStatementRegex = new(
        @"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?use\s+(?<body>.+);\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private readonly record struct RustUseSymbolOccurrence(string Name, int Line, int Column);
    private const string RustIdentifierPattern = @"(?:r#)?\w+";
    private static readonly Regex RustMultilineImplForRegex = new(
        @"^\s*(?:unsafe\s+)?impl(?:<[^>]+>)?\s+.+?\s+for\s+(?<name>" + RustIdentifierPattern + @")\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex RustMultilineImplTypeRegex = new(
        @"^\s*(?:unsafe\s+)?impl(?:<[^>]+>)?\s+(?<name>" + RustIdentifierPattern + @")(?!\s+for\b)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
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
    private static readonly Regex XamlEventHandlerRegex = new(
        @"\b(?:Clicked|Tapped|Loaded|Unloaded|SelectionChanged|TextChanged|CheckedChanged|Unchecked|SelectedIndexChanged|PointerPressed|PointerReleased|PointerEntered|PointerExited|Drop|DragOver|Completed|Appearing|Disappearing|NavigatedTo|NavigatedFrom|SizeChanged)\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XamlBindingRegex = new(
        @"\{(?<kind>Binding|x:Bind)\b(?<content>(?:[^{}]|{[^{}]*})*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObjCCategoryDeclarationRegex = new(
        @"^\s*@(?:interface|implementation)\s+(?<class>\w+)\s*\(\s*(?<category>[^)]+?)\s*\)(?:\s*<[^>]+>)?",
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
        VisualBasicEnd,
        PascalEnd,
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

    private readonly record struct FortranContinuationMatchCandidate(
        string MatchLine,
        int LastConsumedLineIndex);

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
        $@"^\s*(?:module\.exports|exports)(?:\.(?<name>{JavaScriptTypeScriptIdentifierPattern})|\[\s*['""](?<bracketName>[^'""]*)['""]\s*\])(?:\s*:\s*[^=]+?)?\s*(?<![=!<>])=(?![=>])\s*(?<rhs>.*)$",
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
    private const string VbMemberModifierPattern = @"(?:Shared|Overrides|Overridable|MustOverride|Overloads|Shadows|Async|Partial)";
    private const string VbOperatorModifierPattern = @"(?:Shared|Overrides|Overridable|MustOverride|Overloads|Shadows|Async|Partial|Widening|Narrowing)";
    private const string VbPropertyModifierPattern = @"(?:Shared|Overrides|Overridable|MustOverride|Overloads|Shadows|Default|ReadOnly|WriteOnly)";
    private const string VbEventModifierPattern = @"(?:Shared|Overloads|Shadows|Custom)";

    private static readonly Dictionary<string, List<SymbolPattern>> PatternCache = new()
    {
        ["python"] =
        [
            new("function", new Regex(@"^\s*(?:async\s+)?def\s+(?<name>\w+)\s*(?:\[[^\]]*\])?\s*\(", RegexOptions.Compiled), BodyStyle.Indent),
            new("class",    new Regex(@"^\s*class\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Indent),
            new("import",   new Regex(@"^\s*type\s+(?<name>\w+)\s*(?:\[[^\]]*\])?\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*(?:from\s+(?<name>(?:\.+[\w.]*|[\w.]+))\s+import\b|import\s+(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["cobol"] =
        [
            // COBOL is organized around program IDs rather than brace-scoped members.
            // Keep the extraction deliberately small and conservative: one symbol per program.
            // COBOL は brace ではなく program ID 単位で構成されるため、抽出は保守的に
            // program ひとつにつき 1 symbol に絞る。
            new("class", new Regex(@"^\s*(?:IDENTIFICATION\s+DIVISION\.\s*)?PROGRAM-ID\.\s*(?<name>[A-Z0-9][A-Z0-9-]*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
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
            new("import", new Regex(@"^\s*(?:(?<visibility>export)\s+)?(?:declare\s+)?type\s+(?<name>\w+)" + TypeScriptOptionalTypeParameterListPattern + @"\s*=", RegexOptions.Compiled), BodyStyle.None, "visibility"),
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
            new("function", new Regex(@"^func\s+(?:\([^)]+\)\s+)?(?<name>\w+)(?:\[[^\]\r\n]+\])?\s*[\(\[]", RegexOptions.Compiled), BodyStyle.Brace),
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
            // Subroutines / サブルーチン
            new("function", new Regex(@"^\s*(?:(?:pure|elemental|recursive|module|impure)\s+)*subroutine\s+(?<name>[A-Za-z_]\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), BodyStyle.FortranEnd),
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
            new("struct",   new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?(?:struct|union)\s+(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("enum",     new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?enum\s+(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // Enum variants / `Red`, `Ok(T)`, `Circle { radius: f64 }`, `Point`
            new("property", new Regex(@"^\s{4,}(?<name>[A-Z][A-Za-z0-9_]*)\s*(?:\([^()\r\n]*\)|\{[^{}\r\n]*\})?\s*,?\s*$", RegexOptions.Compiled), BodyStyle.None),
            new("interface", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?trait\s+(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // impl Trait for Type / `unsafe impl Trait for Type` should attach to the type being extended.
            // `impl Trait for Type` / `unsafe impl Trait for Type` は、拡張先の型に紐づける。
            new("class",    new Regex(@"^\s*(?:unsafe\s+)?impl(?:<[^>]+>)?\s+.+?\s+for\s+(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*(?:unsafe\s+)?impl(?:<[^>]+>)?\s+(?<name>(?:r#)?\w+)(?!\s+for\b)", RegexOptions.Compiled), BodyStyle.Brace),
            // mod / モジュール
            new("namespace", new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?mod\s+(?<name>(?:r#)?\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            // type alias / 型エイリアス
            new("import",   new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?type\s+(?<name>(?:r#)?\w+)(?:\s*<[^=]+>)?", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("import",   new Regex(@"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?use\s+(?<name>.+);", RegexOptions.Compiled), BodyStyle.None, "visibility"),
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
            // Kotlin fun interface / Kotlin の fun interface も interface として扱う。
            new("interface", new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:sealed|expect|actual)\s+)*(?:fun\s+)?interface\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
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
        ["perl"] =
        [
            // Perl package declarations / Perl の package 宣言
            new("namespace", new Regex(@"^\s*package\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b(?:\s+v?[\d._]+)?\s*;", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            // Perl constants are compile-time subroutines, so expose them as functions for navigation.
            // Perl constant はコンパイル時 subroutine なので、ナビゲーション用に function として出す。
            new("function", new Regex(@"^\s*use\s+constant\s+(?<name>" + PerlIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            // Perl module imports / Perl の module import
            new("import", new Regex(@"^\s*use\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            new("import", new Regex(@"^\s*require\s+(?<name>" + PerlQualifiedIdentifierPattern + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.None),
            // Perl subroutines / Perl の subroutine
            new("function", new Regex(@"^\s*sub\s+(?<name>" + PerlIdentifierPattern + @")\b(?:\s*:[^{;]+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant), BodyStyle.Brace),
        ],
        ["c"] =
        [
            new("function", new Regex(CFunctionStartBlacklistPattern + CFunctionReturnTypePattern + CFunctionNameBlacklistPattern + @"(?<name>\w+)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            // #define macros / #define マクロ
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)\(", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)(?=\s|$)", RegexOptions.Compiled), BodyStyle.None),
            new("struct",   new Regex(@"^\s*typedef\s+struct\s+(?:\w+\s*)?(?:\*+\s*)?(?<name>\w+)\s*;", RegexOptions.Compiled), BodyStyle.None),
            new("struct",   new Regex(@"^\s*(?:typedef\s+)?struct\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*typedef\s+enum\s+(?:\w+\s+)?(?<name>\w+)\s*;", RegexOptions.Compiled), BodyStyle.None),
            new("enum",     new Regex(@"^\s*(?:typedef\s+)?enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*#include\s+(?:<(?<name>[^>]+)>|""(?<name>[^""]+)""|(?<name>[^\s]+))", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["cpp"] =
        [
            new("namespace", new Regex(CppFunctionStartBlacklistPattern + @"(?:export\s+)?module\s+(?<name>[\w.]+)\b", RegexOptions.Compiled), BodyStyle.None),
            new("namespace", new Regex(CppFunctionStartBlacklistPattern + @"inline\s+namespace\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("interface", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?:export\s+)?concept\s+(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?:(?<returnType>(?:(?:" + CppFunctionReturnTypeAtomPattern + @")[\s*&]+)+))?(?:(?:[\w:<>]+\s*::\s*)+)?" + CFunctionNameBlacklistPattern + @"(?<name>~?\w+|operator(?:\s*\(\)|\s*\[\]|\s*[^\s(]+(?:\s+[^\s(]+)?))(?:\s*<[^>]+>)?\s*\(", RegexOptions.Compiled), BodyStyle.Brace, ReturnTypeGroup: "returnType"),
            // Type alias / 型エイリアス
            new("import", new Regex(CppFunctionStartBlacklistPattern + @"template\s*<[^>]+>\s*(?:export\s+)?using\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("import", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?:export\s+)?using\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("import", new Regex(CppFunctionStartBlacklistPattern + @"template\s*<[^>]+>\s*(?:export\s+)?using\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.Brace),
            new("import", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?:export\s+)?using\s+(?<name>\w+)\s*=", RegexOptions.Compiled), BodyStyle.Brace),
            new("import", new Regex(@"^\s*typedef\s+(?![^;]*\().*\b(?<name>\w+)\s*;", RegexOptions.Compiled), BodyStyle.None),
            new("import", new Regex(@"^\s*typedef\s+(?![^;]*\().*\b(?<name>\w+)\s*;", RegexOptions.Compiled), BodyStyle.Brace),
            // #define macros / #define マクロ
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)\(", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)(?=\s|$)", RegexOptions.Compiled), BodyStyle.None),
            new("property", new Regex(CppFunctionStartBlacklistPattern + CppTemplatePrefixPattern + @"(?<returnType>(?:[\w:<>~]+[\s*&]+)+)(?:(?:[\w:<>]+\s*::\s*)+)(?<name>\w+)\s*=\s*[^;]+;", RegexOptions.Compiled), BodyStyle.None, ReturnTypeGroup: "returnType"),
            new("class",    new Regex(@"^\s*class\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("struct",   new Regex(@"^\s*struct\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("namespace", new Regex(@"^\s*namespace\s+(?!\w+\s*=)(?<name>\w+(?:::\w+)*)", RegexOptions.Compiled), BodyStyle.Brace),
            new("enum",     new Regex(@"^\s*(?:typedef\s+)?enum\s+(?:class\s+)?(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
            new("import",   new Regex(@"^\s*#include\s+(?:<(?<name>[^>]+)>|""(?<name>[^""]+)""|(?<name>[^\s]+))", RegexOptions.Compiled), BodyStyle.None),
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
            new("trait", new Regex(@"^\s*trait\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace),
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
            new("function", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:static|class|nonisolated|mutating|nonmutating)\s+)*(?:override\s+)?func\s+(?<name>`[^`]+`|\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:required|convenience|nonisolated|mutating|nonmutating|override)\s+)*(?<name>init)(?:\?)?\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:nonisolated)\s+)*(?<name>deinit)\s*(?:\{|$)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("function", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:static|class|nonisolated|mutating|nonmutating|override)\s+)*(?<name>subscript)\s*\(", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("struct",    new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:(?:final)\s+)*struct\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("enum",      new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:indirect\s+)?enum\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
            new("property",  new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"(?:indirect\s+)?case\s+(?<name>\w+)(?:\s*\([^)]*\))?(?:\s*=\s*(?<returnType>.+?))?\s*$", RegexOptions.Compiled), BodyStyle.None, "visibility", ReturnTypeGroup: "returnType"),
            new("interface", new Regex(@"^\s*" + SwiftAttributePattern + @"(?<visibility>public|private|internal|open|fileprivate|package)?\s*" + SwiftAttributePattern + @"protocol\s+(?<name>\w+)", RegexOptions.Compiled), BodyStyle.Brace, "visibility"),
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
            new("import",   new Regex(@"^\s*" + SwiftAttributePattern + @"import\s+(?<name>.+)", RegexOptions.Compiled), BodyStyle.None),
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
            new("function", new Regex(@"^\s*let\s+(?:(?:rec|mutable|inline|private|internal|public)\s+)*(?<name>(?:``[^`]+``|\w+))(?:\s+(?:\w+|\())?", RegexOptions.Compiled), BodyStyle.None),
            new("struct", new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>\w+)(?:\s*<[^>]+>)?\s*=\s*\{", RegexOptions.Compiled), BodyStyle.Brace),
            // Generic abbreviations such as `type Result<'T> = Choice<'T, string>` should not be
            // mistaken for union cases just because the right-hand side starts with a capitalized
            // type name.
            // `type Result<'T> = Choice<'T, string>` のような generic abbreviation は、
            // 右辺が大文字始まりの型名でも union case と誤認しない。
            new("typealias", new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>(?:``[^`]+``|\w+))\s*<[^=]+?>\s*(?:when\b[^=]+)?\s*=\s*(?!(?:class|interface|struct|enum|exception)\b)(?!\{)(?!\|)(?!\()", RegexOptions.Compiled), BodyStyle.None),
            new("enum", new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>\w+)(?:\s*<[^>]+>)?\s*=\s*(?:\|?\s*[A-Z][\w']*\b(?:\s*\|[^=].*)?)", RegexOptions.Compiled), BodyStyle.Brace),
            // Simple aliases without generic parameters stay searchable as `typealias`.
            // generic 引数なしの単純な alias も `typealias` として検索可能にする。
            new("typealias", new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>(?:``[^`]+``|\w+))\s*=\s*(?!(?:class|interface|struct|enum|exception)\b)(?!\{)(?!\|)(?!\()", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>\w+)\s*(?:\([^)]*\))\s*=", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>\w+)\s*=\s*class\b", RegexOptions.Compiled), BodyStyle.None),
            new("struct",   new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>\w+)\s*=\s*\{", RegexOptions.Compiled), BodyStyle.None),
            new("enum",     new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>\w+)\s*=\s*(?:\|\s*)?\w+(?:\s*\|\s*\w+)+", RegexOptions.Compiled), BodyStyle.None),
            new("exception", new Regex(@"^\s*exception\s+(?:(?:private|internal)\s+)?(?<name>(?:``[^`]+``|\w+))", RegexOptions.Compiled), BodyStyle.None),
            new("import",   new Regex(@"^\s*type\s+(?:(?:private|internal)\s+)?(?<name>(?:``[^`]+``|\w+))\s*=\s*(?!\{)(?!\|)(?!class\b)(?!struct\b)(?!interface\b)(?!enum\b).+", RegexOptions.Compiled), BodyStyle.None),
            new("namespace", new Regex(@"^\s*namespace\s+(?:(?:rec|global)\s+)*(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.None),
            new("namespace", new Regex(@"^\s*module\s+(?:(?:(?:private|internal)\s+|rec\s+))*(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?<visibility>private|internal|public)\s+)?override\s+(?:(?:this|_|\w+)\.)?(?<name>\w+)\s*(?:\(|=|:)", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("function", new Regex(@"^\s*(?:(?<visibility>private|internal|public)\s+)?(?:(?:static|abstract|override|default)\s+)*member\s+(?:(?:private|internal)\s+)?(?:(?:inline)\s+)?(?:(?:this|_|\w+)\.)?(?!val\b)(?<name>\w+)\b", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("import",   new Regex(@"^\s*open\s+(?<name>[\w.]+)", RegexOptions.Compiled), BodyStyle.None),
        ],
        ["vb"] =
        [
            new("namespace", new Regex(@"^\s*Namespace\s+(?<name>(?:Global\.)?[\w.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.VisualBasicEnd),
            new("delegate", new Regex(@$"^\s*(?:(?:{VbMemberModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?Delegate\s+(?:Sub|Function)\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None, "visibility"),
            new("function", new Regex(@$"^\s*(?:(?:{VbMemberModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbMemberModifierPattern})\s+)*(?:Sub|Function)\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None, "visibility"),
            new("operator", new Regex(@$"^\s*(?:(?:{VbOperatorModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbOperatorModifierPattern})\s+)*(?<name>Operator\s+[^\s(]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.VisualBasicEnd, "visibility"),
            new("property", new Regex(@$"^\s*(?:(?:{VbPropertyModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbPropertyModifierPattern})\s+)*Property\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None, "visibility"),
            new("event",    new Regex(@$"^\s*(?:(?:{VbEventModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbEventModifierPattern})\s+)*Event\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None, "visibility"),
            new("interface", new Regex(@$"^\s*(?:(?:{VbTypeModifierPattern})\s+)*(?:(?<visibility>{VbVisibilityPattern})\s+)?(?:(?:{VbTypeModifierPattern})\s+)*Interface\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.VisualBasicEnd, "visibility"),
            new("enum",     new Regex(@$"^\s*(?:(?<visibility>{VbVisibilityPattern})\s+)?Enum\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.VisualBasicEnd, "visibility"),
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
            new("function", new Regex(@"^\s*`(?<name>[^`]+)`\s*<-\s*function\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?<name>[\w.]+)\s*<-\s*function\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            new("function", new Regex(@"^\s*(?<name>[\w.]+)\s*=\s*function\s*\(", RegexOptions.Compiled), BodyStyle.Brace),
            new("class",    new Regex(@"^\s*(?:(?:[\w.]+)::)?(?:setClass|setRefClass|setClassUnion|setOldClass|R6Class)\s*\(\s*(?:(?:Class|classes|className|classname|name)\s*=\s*)?(?:['""](?<name>[^'""]+)['""]|(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*(?:(?:[\w.]+)::)?setIs\s*\(.*?\b(?:class2|to)\s*=\s*(?:['""](?<name>[^'""]+)['""]|(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
            new("class",    new Regex(@"^\s*inherit\s*=\s*(?:c\(\s*)?(?:['""](?<name>[^'""]+)['""]|(?<name>[A-Z][\w.]*))", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?:[\w.]+)::)?setValidity\s*\(\s*(?:(?:Class|class|classes|name)\s*=\s*)?(?:['""](?<name>[^'""]+)['""]|(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?:[\w.]+)::)?setGeneric\s*\(\s*(?:(?:f|generic|name)\s*=\s*)?['""](?<name>[^'""]+)['""]", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?:(?:[\w.]+)::)?setMethod\s*\(\s*(?:(?:f|generic|name)\s*=\s*)?(?:['""](?<name>[^'""]+)['""]|(?<name>[\w.]+))\s*,", RegexOptions.Compiled), BodyStyle.None),
            new("function", new Regex(@"^\s*(?<visibility>public|private|active)\s*=\s*list\(\s*(?<name>[\w.]+)\s*=\s*function\s*\(", RegexOptions.Compiled), BodyStyle.None, "visibility"),
            new("import",   new Regex(@"^\s*(?:library|require|requireNamespace)\s*\(\s*(?:['""](?<name>[^'""]+)['""]|(?<name>[\w.]+))", RegexOptions.Compiled), BodyStyle.None),
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
            new("function", new Regex(@"^(?<name>[\w.%-]+)\s*:(?!=|:=)", RegexOptions.Compiled), BodyStyle.None),  // Makefile targets / Makefileターゲット
        ],
        ["dockerfile"] =
        [
            new("property", new Regex(@"^\s*(?:ARG|ENV)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            new("function", new Regex(@"^\s*FROM\s+(?:--platform=\S+\s+)?\S+\s+(?:AS|as)\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),  // Named stage / 名前付きステージ
            new("class",    new Regex(@"^\s*FROM\s+(?:--platform=\S+\s+)?(?<name>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),  // Base image / ベースイメージ
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
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:(?:(?:GLOBAL|LOCAL)\s+)?(?:TEMP|TEMPORARY)\s+|UNLOGGED\s+)?(?:TABLE|(?:MATERIALIZED\s+)?VIEW)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
            // CREATE PROCEDURE / PROC / FUNCTION / TRIGGER — Postgres `OR REPLACE` and T-SQL `OR ALTER` / `PROC` short form
            // Uses BodyStyle.SqlProcBody so the body range covers the BEGIN...END / dollar-quoted body,
            // letting ReferenceExtractor.ResolveContainerForCall attribute calls inside the body to the
            // enclosing procedure (see issue #429).
            // CREATE PROCEDURE / PROC / FUNCTION / TRIGGER — Postgres の `OR REPLACE` と T-SQL の `OR ALTER` / 短縮形 `PROC` に対応
            // BodyStyle.SqlProcBody により BEGIN...END / dollar-quoted の本体範囲を求め、ReferenceExtractor の
            // ResolveContainerForCall が本体内の呼び出しを外側のプロシージャに帰属させられるようにする（issue #429）。
            new("function", new Regex($@"^\s*CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:PROCEDURE|PROC|FUNCTION|TRIGGER)\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.SqlProcBody),
            // SQL Server aggregate definitions are callable search anchors too, but they do not have
            // a statement body to scan, so they stay on the BodyStyle.None path.
            // SQL Server の aggregate 定義も検索アンカーとして有用だが、走査すべき statement body は
            // 持たないため BodyStyle.None のまま扱う。
            new("function", new Regex($@"^\s*CREATE\s+AGGREGATE\b\s+(?<name>{SqlQualifiedIdentifierPattern})\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
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
            // SQL Server legacy scalar-object definitions still appear in older T-SQL codebases.
            // The `AS <expression>` tail is part of the definition, not a body to track.
            // SQL Server の legacy な scalar-object 定義は古い T-SQL コードベースに残っている。
            // 末尾の `AS <expression>` は定義の一部であり、追跡すべき body ではない。
            new("class",    new Regex($@"^\s*CREATE\s+(?:RULE|DEFAULT)\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
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
            new("class",    new Regex($@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:DATABASE|LOGIN|USER|ROLE|CERTIFICATE|DIRECTORY|CONTEXT|PROFILE|ASSEMBLY|XML\s+SCHEMA\s+COLLECTION|SECURITY\s+POLICY)\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
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
            new("function", new Regex($@"^\s*ALTER\s+AGGREGATE\b\s+(?<name>{SqlQualifiedIdentifierPattern})\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
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
            new("class",    new Regex($@"^\s*ALTER\s+(?:TABLE|(?:MATERIALIZED\s+)?VIEW|SEQUENCE|SYNONYM|LOGIN|USER|ROLE|DATABASE|CERTIFICATE|INDEX|PACKAGE|TYPE|DOMAIN|DIRECTORY|PROFILE|ASSEMBLY|XML\s+SCHEMA\s+COLLECTION|PARTITION\s+SCHEME|FULLTEXT\s+CATALOG|SECURITY\s+POLICY)\b\s+(?<name>{SqlQualifiedIdentifierPattern})", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
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
            // Alias definitions / エイリアス定義
            new("alias", new Regex(@"^\s*(?:Set-Alias|New-Alias)\s+(?:-Name\s+)?(?<name>[\w-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), BodyStyle.None),
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

    /// <summary>
    /// Return the set of languages that have symbol-extraction patterns.
    /// シンボル抽出パターンを持つ言語のセットを返す。
    /// </summary>
    public static IReadOnlyCollection<string> GetSupportedLanguages()
      => PatternCache.Keys.Concat(new[] { "commonlisp", "racket", "vue", "svelte", "markdown" }).ToArray();

    private static string? NormalizeLanguage(string? lang)
        => lang is "vue" or "svelte" ? "typescript" : lang;


    private static readonly HashSet<string> ContainerKinds =
    [
        "class", "struct", "interface", "namespace", "enum", "heading"
    ];

    /// <summary>
    /// Extract symbols from the given source content.
    /// 指定されたソース内容からシンボルを抽出する。
    /// </summary>
    /// <param name="fileId">The file ID in the database / データベース上のファイルID</param>
    /// <param name="lang">Detected language / 検出された言語</param>
    /// <param name="content">Full file content / ファイル全体の内容</param>
    /// <param name="filePath">Relative file path when available / 利用可能なら相対ファイルパス</param>
    /// <returns>List of extracted symbols / 抽出されたシンボルのリスト</returns>
    public static List<SymbolRecord> Extract(long fileId, string? lang, string content, string? filePath = null)
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

        if (lang == "markdown")
        {
            if (content.Contains('\r'))
                content = content.Replace("\r\n", "\n").Replace("\r", "\n");
            content = FileIndexer.StripLineLeadingBom(content);
            var markdownLines = content.Split('\n');
            var markdownSymbols = ExtractMarkdownSymbols(fileId, markdownLines);
            AssignContainers(markdownSymbols, markdownLines, null);
            PopulateDeclaredContainerQualifiedNames(markdownSymbols);
            return markdownSymbols;
        }

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
        var pythonModulePrefix = lang == "python"
            ? GetPythonModulePrefix(filePath)
            : null;

        if (lang is "commonlisp" or "racket")
            return ExtractLispSymbols(fileId, lang, lines);

        if (!PatternCache.TryGetValue(lang, out var patterns))
            return [];

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

        if (lang == "assembly")
            return ExtractAssemblySymbols(fileId, lines);

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
        var fsharpTypeBodyState = FSharpTypeBodyState.None;
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
                && TryHandleGoBlockLine(fileId, line, i, symbols, ref goImportBlock))
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

            var fortranContinuationCandidate = lang == "fortran"
                ? TryBuildFortranContinuationMatchLine(lines, i)
                : null;
            if (fortranContinuationCandidate != null)
                matchLine = fortranContinuationCandidate.Value.MatchLine;

            if (lang == "fsharp")
                TryAddFSharpTypeMemberSymbols(symbols, fileId, line, i + 1, ref fsharpTypeBodyState);

            if (lang == "fsharp"
                && TryAddFSharpRecordFieldsFromContext(symbols, fileId, lines, i, line, i + 1))
            {
                continue;
            }

            if (lang == "fsharp" && TryAddFSharpActivePatternSymbols(symbols, fileId, line, i + 1))
                continue;

            if (lang == "fsharp" && TryAddFSharpOperatorSymbols(symbols, fileId, line, i + 1))
                continue;

            if (lang == "php")
                ExtractPhpImportSymbols(symbols, line, i + 1);

            if (lang is "javascript" or "typescript")
                ExtractJavaScriptTypeScriptDynamicImportSymbols(fileId, line, structuralLine, i + 1, symbols);

            if (lang is "javascript" or "typescript"
                && TryHandleJavaScriptTypeScriptImportEqualsLine(fileId, line, i + 1, symbols))
            {
                continue;
            }

            if (lang == "cpp" && TryAddCppIndentedAlias(fileId, line, i + 1, symbols))
                continue;

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
                    if (fortranContinuationCandidate != null)
                        patternMatchLine = fortranContinuationCandidate.Value.MatchLine;
                    var lineOffset = patternStartOffset;
                    string? csharpWrappedModifierPrefix = null;
                    while (lineOffset >= 0 && lineOffset < patternMatchLine.Length)
                    {
                        var javaLeadingAnnotationOffset = 0;
                        var match = lang is "java" or "kotlin"
                            ? (TryMatchJavaDeclarationSegment(pattern.Regex, patternMatchLine[lineOffset..], lang == "kotlin", out var javaMatch, out javaLeadingAnnotationOffset)
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
                        if (lang is "java" or "kotlin" && javaLeadingAnnotationOffset > 0)
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
                    if (fortranContinuationCandidate != null)
                        endLine = Math.Max(endLine, fortranContinuationCandidate.Value.LastConsumedLineIndex + 1);
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
                        signature = lang == "fortran"
                            ? patternMatchLine[absoluteStartColumn..].Trim()
                            : line[absoluteStartColumn..].Trim();
                    }

                    List<string>? fortranProcedureNames = null;
                    if (lang == "fortran"
                        && pattern.Kind == "function"
                        && name.Contains(',')
                        && signature.Contains("procedure", StringComparison.OrdinalIgnoreCase))
                    {
                        var names = name.Split(',');
                        for (var index = 0; index < names.Length; index++)
                            names[index] = names[index].Trim();

                        if (names.Any(static candidate => candidate.Length > 0))
                            fortranProcedureNames = names.Where(static candidate => candidate.Length > 0).ToList();
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
                        var pythonImportEntries = lang == "python" && pattern.Kind == "import"
                            ? TryExpandPythonImportSymbols(lines, i, absoluteStartColumn, pythonModulePrefix)
                            : null;
                        var declaratorEntries = lang == "csharp"
                            && pattern.Kind == "property"
                            && pattern.BodyStyle == BodyStyle.None
                            ? TryExpandCSharpFieldDeclaratorList(patternMatchLine, absoluteStartColumn, match, pattern.ReturnTypeGroup, name)
                            : null;

                        if (pythonImportEntries != null)
                        {
                            foreach (var entry in pythonImportEntries)
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
                                        StartColumn = entry.StartColumn,
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
                        else if (declaratorEntries != null)
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
                        else if (fortranProcedureNames != null)
                        {
                            foreach (var procedureName in fortranProcedureNames)
                            {
                                AddSymbolRecord(
                                    symbols,
                                    cssSeenSymbols,
                                    startLine,
                                    new SymbolRecord
                                    {
                                        FileId = fileId,
                                        Kind = kind,
                                        Name = procedureName,
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
                                        StartColumn = lang == "rust" && pattern.Kind == "function"
                                            ? match.Groups["name"].Index
                                            : (csharpSingleLineCollapsedMatch
                                                ? csharpSignatureRawStartColumn
                                                : absoluteStartColumn),
                                        EndLine = Math.Max(startLine, endLine),
                                        BodyStartLine = bodyStartLine,
                                        BodyEndLine = bodyEndLine,
                                    Signature = signature,
                                    Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                    ReturnType = NormalizeMetadata(rawReturnType),
                                },
                                line);

                            if (lang == "objc"
                                && pattern.Kind == "class"
                                && TryGetObjCCategoryDisplayName(patternMatchLine[absoluteStartColumn..], name, out var categoryDisplayName))
                            {
                                AddSymbolRecord(
                                    symbols,
                                    cssSeenSymbols,
                                    startLine,
                                    new SymbolRecord
                                    {
                                        FileId = fileId,
                                        Kind = "class",
                                        Name = categoryDisplayName,
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

        if (lang == "javascript")
            ExtractJavaScriptBareMethods(fileId, lines, symbols, privateScopeColumns!);
        else if (lang == "typescript")
            ExtractTypeScriptBareMethods(fileId, lines, symbols, privateScopeColumns!);
        else if (lang == "csharp")
            ExtractCSharpEnumMembers(fileId, lines, structuralLines, csharpMatchLines!, symbols);
        else if (lang == "java")
        {
            ExtractJavaEnumMembers(fileId, lines, symbols);
            ExtractJavaCompactConstructors(fileId, lines, symbols);
            ExtractJavaModuleDirectiveSymbols(fileId, lines, structuralLines, symbols);
        }
        else if (lang == "vb")
            ExtractVisualBasicEnumMembers(fileId, lines, symbols);
        if (lang == "cobol")
            ExtractCobolParagraphSymbols(fileId, lines, symbols);

        if (string.Equals(originalLang, "svelte", StringComparison.Ordinal))
            ExtractSvelteReactiveSymbols(fileId, lines, symbols);
        if (lang == "rust")
            ExtractRustUseSymbols(fileId, lines, symbols);
        if (lang == "rust")
            ExtractRustMultilineImplSymbols(fileId, lines, symbols);
        if (lang == "go")
            ExtractGoGroupedDeclarations(fileId, lines, symbols);
        if (lang == "cpp")
            ExtractCppSameLineClassBodyMembers(fileId, lines, symbols);
        if (lang == "python")
            ExtractPythonAllExportSymbols(fileId, lines, symbols, pythonModulePrefix);
        if (lang == "perl")
            ExtractPerlHashConstantSymbols(fileId, lines, symbols);
        AssignContainers(symbols, lines, csharpLineStartStates);
        MaterializeRecordPrimaryComponentSymbols(symbols, pendingRecordPrimaryComponents);
        KotlinSymbolNameNormalizer.NormalizeSecondaryConstructorNames(symbols);
        if (lang == "shell")
            ExpandShellAliasSymbols(fileId, lines, symbols);
        PopulateDeclaredContainerQualifiedNames(symbols);
        return symbols;
    }


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
                BodyStyle.FortranEnd => FindFortranRange(lines, startIndex),
                BodyStyle.ElixirEnd => FindElixirRange(lines, startIndex),
                BodyStyle.VisualBasicEnd => FindVisualBasicRange(lines, startIndex),
                BodyStyle.PascalEnd => FindPascalRange(lines, startIndex),
                BodyStyle.SmalltalkMethod => FindSmalltalkMethodRange(lines, startIndex),
                BodyStyle.SqlProcBody => FindSqlProcBodyRange(lines, startIndex),
              _ => (startIndex + 1, null, null),
          };
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
        var allowKrParameterDeclarations = lang is "c" or "cpp";
        bool sawTopLevelClosingParen = false;

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
                {
                    parenDepth--;
                    if (allowKrParameterDeclarations && parenDepth == 0)
                        sawTopLevelClosingParen = true;
                }
                else if (c == '[')
                    bracketDepth++;
                else if (c == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (c == ';' && !opened)
                {
                    if (allowKrParameterDeclarations && sawTopLevelClosingParen && i > startIndex)
                        continue;
                    return (startIndex + 1, null, null);
                }
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
        var recordMatch = lang is "java" or "kotlin"
            ? (TryMatchJavaDeclarationSegment(recordRegex, declaration, lang == "kotlin", out var javaRecordMatch, out javaLeadingAnnotationOffset)
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
            var stripped = StripLeadingJavaRecordComponentAnnotations(normalized, allowKotlinUseSiteTargets: true);
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
                : StripLeadingJavaRecordComponentAnnotations(normalized, allowKotlinUseSiteTargets: lang == "kotlin");
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

    private static StrippedRecordComponentText StripLeadingJavaRecordComponentAnnotations(string component, bool allowKotlinUseSiteTargets)
    {
        var consumedNewlines = 0;
        var trimmed = TrimLeadingWhitespaceAndCountNewlines(component, ref consumedNewlines);
        while (trimmed.StartsWith("@", StringComparison.Ordinal))
        {
            var index = 1;
            while (index < trimmed.Length && (char.IsLetterOrDigit(trimmed[index]) || trimmed[index] is '_' or '$' or '.'))
                index++;

            if (allowKotlinUseSiteTargets && index < trimmed.Length && trimmed[index] == ':' && KotlinAnnotationTargets.Contains(trimmed[1..index]))
            {
                index++;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
                    index++;
                while (index < trimmed.Length && (char.IsLetterOrDigit(trimmed[index]) || trimmed[index] is '_' or '$' or '.'))
                    index++;
            }

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

    private static bool TryGetObjCCategoryDisplayName(string objcDeclaration, string baseName, out string displayName)
    {
        var match = ObjCCategoryDeclarationRegex.Match(objcDeclaration);
        if (!match.Success || !string.Equals(match.Groups["class"].Value, baseName, StringComparison.Ordinal))
        {
            displayName = string.Empty;
            return false;
        }

        var categoryName = match.Groups["category"].Value.Trim();
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

    private static bool StartsWithKeyword(string line, int startIndex, string keyword)
    {
        if (startIndex < 0 || startIndex + keyword.Length > line.Length)
            return false;

        if (!string.Equals(line.Substring(startIndex, keyword.Length), keyword, StringComparison.Ordinal))
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
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static string NormalizeExtractedSymbolName(string? lang, string name, Match match, string matchLine)
    {
        return lang switch
        {
            "csharp" => CSharpSymbolNameNormalizer.Normalize(name, match, matchLine),
            "cobol" => CobolSymbolNameNormalizer.Normalize(name),
            "fsharp" => FSharpSymbolNameNormalizer.Normalize(name),
            "kotlin" => KotlinSymbolNameNormalizer.Normalize(name, matchLine),
            "rust" => RustSymbolNameNormalizer.Normalize(name),
            "smalltalk" => NormalizeSmalltalkSelectorName(name),
            "swift" => SwiftSymbolNameNormalizer.Normalize(name),
            "sql" => SqlSymbolNameNormalizer.Normalize(name),
            _ => name,
        };
    }

    private static string NormalizeSmalltalkSelectorName(string name)
    {
        var trimmed = name.Trim();
        if (!trimmed.Contains(':'))
            return trimmed;

        var builder = new StringBuilder(trimmed.Length);
        foreach (var token in trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.EndsWith(':'))
                builder.Append(token);
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
