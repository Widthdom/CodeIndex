using System.Reflection;

namespace CodeIndex.Indexer.Hooks;

internal static class PostExtractionHookCallbackInvoker
{
    internal static PostExtractionHookCallbackProtocol.WorkerResponse Invoke(
        IPostExtractionHook hook,
        PostExtractionHookCallbackProtocol.WorkerRequest request,
        int capturedConsoleMaxChars)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var capturedOut = new BoundedTextWriter(capturedConsoleMaxChars);
        using var capturedError = new BoundedTextWriter(capturedConsoleMaxChars);
        Exception? callbackFailure = null;
        try
        {
            Console.SetOut(capturedOut);
            Console.SetError(capturedError);
            if (request.Callback == nameof(IPostExtractionHook.OnSymbolsExtracted))
            {
                if (request.Symbols == null)
                    throw new InvalidOperationException("symbol callback request omitted symbols.");
                hook.OnSymbolsExtracted(request.Context, request.Symbols);
            }
            else if (request.Callback == nameof(IPostExtractionHook.OnReferencesExtracted))
            {
                if (request.References == null)
                    throw new InvalidOperationException("reference callback request omitted references.");
                hook.OnReferencesExtracted(request.Context, request.References);
            }
            else
            {
                throw new InvalidOperationException($"unknown hook callback `{request.Callback}`.");
            }
        }
        catch (Exception ex)
        {
            callbackFailure = ex is TargetInvocationException { InnerException: not null } ? ex.InnerException : ex;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        var symbolsTruncated = PostExtractionHookMutationMaterializer.TrimToLimit(request.Symbols, request.MaxSymbols);
        var referencesTruncated = PostExtractionHookMutationMaterializer.TrimToLimit(request.References, request.MaxReferences);
        return new PostExtractionHookCallbackProtocol.WorkerResponse(
            request.Symbols,
            request.References,
            callbackFailure is null ? null : SafeDiagnosticFormatter.FormatExceptionCategory("hook_callback_failed", callbackFailure),
            null,
            symbolsTruncated,
            referencesTruncated);
    }
}
