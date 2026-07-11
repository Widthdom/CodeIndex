using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
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
        Func<JavaScriptScopePrivacyFlags[][]> getPrivateScopeColumns)
    {
        if (!LinesContain(lines, '{'))
            return [];

        var privateScopeColumns = getPrivateScopeColumns();
        List<JavaScriptClassScanTarget>? targets = null;
        HashSet<(int StartIndex, int ScanStartIndex, int ScanEndExclusive, string ContainerName)>? targetIdentities = null;
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
                || bindingMatch.Groups["bracketName"].Success
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

            var targetIdentity = (candidate.StartIndex, candidate.ScanStartIndex, candidate.ScanEndExclusive, candidate.ContainerName);
            if ((targetIdentities ??= []).Add(targetIdentity))
                (targets ??= []).Add(candidate);
        }

        if (targets is null)
            return [];

        SortJavaScriptTypeScriptClassScanTargets(targets);
        return targets;
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
}
