using System.Text;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
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
}
