using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class CallableContainerSelection
{
    internal static bool IsCallableKind(string? kind)
        => kind is "function"
            or "test.method"
            or "operator"
            or "lambda"
            or "async_function"
            or "generator"
            or "async_generator";

    internal static int CompareInnermost(
        SymbolRecord left,
        int leftOriginalIndex,
        SymbolRecord right,
        int rightOriginalIndex,
        bool preferCallable)
    {
        var compare = GetSpanLength(left).CompareTo(GetSpanLength(right));
        if (compare != 0)
            return compare;

        if (preferCallable)
        {
            compare = GetCallableRank(left.Kind).CompareTo(GetCallableRank(right.Kind));
            if (compare != 0)
                return compare;
        }

        return leftOriginalIndex.CompareTo(rightOriginalIndex);
    }

    internal static int GetSpanLength(SymbolRecord symbol)
        => (symbol.BodyEndLine ?? symbol.EndLine) - (symbol.BodyStartLine ?? symbol.StartLine);

    private static int GetCallableRank(string? kind)
        => IsCallableKind(kind) ? 0 : 1;
}
