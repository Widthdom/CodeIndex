namespace CodeIndex.Cli;

internal interface IScopedConsoleOutputRouter
{
    IDisposable Push(TextWriter target);
}

internal static class ScopedConsoleOutput
{
    private static readonly AsyncLocal<IScopedConsoleOutputRouter?> s_activeRouter = new();

    internal static IDisposable Register(IScopedConsoleOutputRouter router)
    {
        var previous = s_activeRouter.Value;
        s_activeRouter.Value = router;
        return new DelegateScope(() => s_activeRouter.Value = previous);
    }

    internal static IDisposable Redirect(TextWriter target)
    {
        if (s_activeRouter.Value is { } router)
            return router.Push(target);

        var ownership = ConsoleStreamOwnership.Enter();
        var original = Console.Out;
        try
        {
            Console.SetOut(target);
            return new DelegateScope(() =>
            {
                try
                {
                    ConsoleStreamOwnership.RestoreOut(original);
                }
                finally
                {
                    ownership.Dispose();
                }
            });
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    private sealed class DelegateScope(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }
}

internal static class ScopedConsoleError
{
    private static readonly AsyncLocal<IScopedConsoleOutputRouter?> s_activeRouter = new();

    internal static IDisposable Register(IScopedConsoleOutputRouter router)
    {
        var previous = s_activeRouter.Value;
        s_activeRouter.Value = router;
        return new DelegateScope(() => s_activeRouter.Value = previous);
    }

    internal static IDisposable Redirect(TextWriter target)
    {
        if (s_activeRouter.Value is { } router)
            return router.Push(target);

        var ownership = ConsoleStreamOwnership.Enter();
        var original = Console.Error;
        try
        {
            Console.SetError(target);
            return new DelegateScope(() =>
            {
                try
                {
                    ConsoleStreamOwnership.RestoreError(original);
                }
                finally
                {
                    ownership.Dispose();
                }
            });
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    private sealed class DelegateScope(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }
}
