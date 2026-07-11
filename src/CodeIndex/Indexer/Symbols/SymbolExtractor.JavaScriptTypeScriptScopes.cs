namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static JavaScriptScopePrivacyFlags GetJavaScriptTypeScriptPrivacyFlags(Stack<JavaScriptScopeKind> scopeStack, bool arrowExpressionActive)
    {
        var flags = JavaScriptScopePrivacyFlags.None;
        if (arrowExpressionActive)
            flags |= JavaScriptScopePrivacyFlags.FunctionLike;

        foreach (var scopeKind in scopeStack)
        {
            if (scopeKind is JavaScriptScopeKind.Function or JavaScriptScopeKind.StaticBlock)
                flags |= JavaScriptScopePrivacyFlags.FunctionLike;
            else if (scopeKind == JavaScriptScopeKind.Block)
                flags |= JavaScriptScopePrivacyFlags.Block;
            else if (scopeKind == JavaScriptScopeKind.Namespace)
                flags |= JavaScriptScopePrivacyFlags.Namespace;

            if (flags == (JavaScriptScopePrivacyFlags.FunctionLike | JavaScriptScopePrivacyFlags.Block | JavaScriptScopePrivacyFlags.Namespace))
                break;
        }

        return flags;
    }

    private static bool IsInsideJavaScriptTypeScriptMethodContainer(Stack<JavaScriptScopeKind> scopeStack)
    {
        return scopeStack.Count > 0 && scopeStack.Peek() is JavaScriptScopeKind.Class or JavaScriptScopeKind.Object;
    }
}
