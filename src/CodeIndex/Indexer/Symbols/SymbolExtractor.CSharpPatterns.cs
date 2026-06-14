using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

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
    private const string CSharpUnicodeEscapePattern = @"\\(?:u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8})";
    private const string CSharpIdentifierPattern =
        @"@?(?:[_\p{L}]|" + CSharpUnicodeEscapePattern + @")(?:\w|" + CSharpUnicodeEscapePattern + @")*";
    private const string CSharpNamespacePattern = CSharpIdentifierPattern + @"(?:\." + CSharpIdentifierPattern + @")*";
    private const string CSharpTypeTokenCharsPattern = @"[\w@?.<>\[\],:*]";
    private const string CSharpTypeTokenPattern = @"(?:" + CSharpUnicodeEscapePattern + @"|" + CSharpTypeTokenCharsPattern + @")";
    private const string CSharpTypeSegmentPattern =
        @"(?:" + CSharpTypeTokenPattern + @"+(?:" + CSharpTupleGroupPattern + CSharpTypeTokenPattern + @"*)*|" + CSharpTupleGroupPattern + CSharpTypeTokenPattern + @"*)";
    private const string CSharpTypePattern =
        @"(?:(?:global::)?(?:" + CSharpTypeSegmentPattern + @")(?:\s+(?:" + CSharpTypeSegmentPattern + @"))*" + CSharpTupleSuffixPattern + @")";
    private const string CSharpMethodTypeParameterListPattern =
        @"(?:<(?:(?>[^<>]+)|<(?<CSharpMethodTypeParameterDepth>)|>(?<-CSharpMethodTypeParameterDepth>))*(?(CSharpMethodTypeParameterDepth)(?!))>\s*)?";
    private static readonly Regex CSharpPartialFunctionDeclarationSignatureRegex = new(
        $@"^(?:(?:{CSharpVisibilityPattern}|abstract|async|extern|new|override|sealed|static|unsafe|virtual)\s+)*partial\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpTestMethodAttributeRegex = new(
        @"(?:^|,)\s*(?:(?:\w+\.)*)?(?:Fact|Theory|Test|TestCase|TestCaseSource|TestMethod|DataTestMethod)(?:Attribute)?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // `delegate` is a non-type keyword only when it is NOT followed by `*` — `delegate*<...>` is a valid return type.
    // `delegate` は `*` を伴わないときだけ非型キーワード扱い。`delegate*<...>` は戻り値型として有効。
    private const string CSharpNonTypeKeywordPattern = @"(?:(?:public|private|protected|internal|static|sealed|partial|readonly|unsafe|extern|virtual|override|abstract|async|new|file|required|ref)\b|delegate\b(?!\s*\*))";
}
