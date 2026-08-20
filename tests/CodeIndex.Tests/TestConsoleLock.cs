using System.Collections.Concurrent;
using CodeIndex.Cli;
using CodeIndex.PluginIsolationFixture;

namespace CodeIndex.Tests;

[CollectionDefinition("Console sensitive", DisableParallelization = true)]
public sealed class ConsoleSensitiveCollection
{
}

[CollectionDefinition("Plugin registry sensitive", DisableParallelization = true)]
public sealed class PluginRegistrySensitiveCollection : ICollectionFixture<TrustedPluginAssemblyFixture>
{
}

public sealed class TrustedPluginAssemblyFixture : IDisposable
{
    private readonly string root;

    public TrustedPluginAssemblyFixture()
    {
        root = OperatingSystem.IsWindows()
            ? TestProjectHelper.CreateTrustedWindowsGitDirectory("cdidx_plugin_fixture")
            : TestProjectHelper.CreateTempProject("cdidx_plugin_fixture");
        PluginPath = Path.Combine(root, "CodeIndex.PluginIsolationFixture.dll");
        File.Copy(typeof(CollectiblePluginSymbolExtractor).Assembly.Location, PluginPath);
    }

    internal string PluginPath { get; }

    public void Dispose()
        => TestProjectHelper.DeleteDirectory(root);
}

internal static class TestConsoleLock
{
    internal static object Gate => ConsoleStreamOwnership.Gate;
}

internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter? originalOut;
    private readonly TextWriter? originalError;
    private readonly TextReader? originalIn;
    private readonly bool restoreOut;
    private readonly bool restoreError;
    private readonly bool restoreIn;
    private readonly IDisposable ownership;
    private bool disposed;

    private ConsoleCapture(bool captureOut, bool captureError, TextWriter? outWriter = null, TextWriter? errorWriter = null, TextReader? inputReader = null)
    {
        restoreOut = captureOut;
        restoreError = captureError;
        restoreIn = inputReader is not null;
        if (captureOut)
            Out = outWriter ?? new StringWriter();
        if (captureError)
            Error = errorWriter ?? new StringWriter();

        ownership = ConsoleStreamOwnership.Enter();
        try
        {
            if (captureOut)
            {
                originalOut = Console.Out;
                Console.SetOut(Out!);
            }

            if (captureError)
            {
                originalError = Console.Error;
                Console.SetError(Error!);
            }

            if (inputReader is not null)
            {
                originalIn = Console.In;
                Console.SetIn(inputReader);
            }
        }
        catch
        {
            try
            {
                Restore();
            }
            finally
            {
                ownership.Dispose();
            }
            throw;
        }
    }

    internal TextWriter? Out { get; }

    internal TextWriter? Error { get; }

    internal static ConsoleCapture Start(bool captureOut = false, bool captureError = false) => new(captureOut, captureError);

    internal static ConsoleCapture Start(TextWriter? output, TextWriter? error)
        => new(output is not null, error is not null, output, error);

    internal static ConsoleCapture Start(TextWriter? output, TextWriter? error, TextReader? input)
        => new(output is not null, error is not null, output, error, input);

    internal static ConsoleCapture StartWithInput(TextReader input, bool captureOut = false, bool captureError = false)
        => new(captureOut, captureError, inputReader: input);

    internal static Task CaptureAsync(
        Func<CancellationToken, Task> action,
        TextWriter? output = null,
        TextWriter? error = null,
        TextReader? input = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Task.Run(() =>
        {
            var effectiveTimeout = timeout ?? TestDeterminism.DefaultTimeout;
            using var timeoutCancellation = new CancellationTokenSource(effectiveTimeout);
            using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);

            // Monitor ownership must enter and exit on this worker thread. Pump captured
            // continuations here so awaited code can re-enter console ownership safely.
            // Timeout only requests cooperative cancellation; this task does not return
            // until the callback exits and the capture has restored the global writers.
            using var capture = Start(output, error, input);
            try
            {
                SingleThreadAsyncPump.Run(() => action(captureCancellation.Token));
            }
            catch (Exception ex) when (
                timeoutCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Console capture callback did not complete within {effectiveTimeout}.",
                    ex);
            }

            var timedOut = timeoutCancellation.IsCancellationRequested;
            timeoutCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            if (timedOut && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Console capture callback did not complete within {effectiveTimeout}.");
            }
        });
    }

    internal static string CaptureError(Action action)
    {
        using var capture = Start(captureError: true);
        action();
        return capture.Error!.ToString()!;
    }

    internal static (int ExitCode, string Stdout, string Stderr) Capture(Func<int> action)
    {
        using var capture = Start(captureOut: true, captureError: true);
        return (action(), capture.Out!.ToString()!, capture.Error!.ToString()!);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        try
        {
            Restore();
            disposed = true;
        }
        finally
        {
            ownership.Dispose();
        }
    }

    private void Restore()
    {
        if (restoreIn && originalIn is not null)
            Console.SetIn(originalIn);
        if (restoreOut && originalOut is not null && restoreError && originalError is not null)
            ConsoleStreamOwnership.Restore(originalOut, originalError);
        else if (restoreError && originalError is not null)
            ConsoleStreamOwnership.RestoreError(originalError);
        else if (restoreOut && originalOut is not null)
            ConsoleStreamOwnership.RestoreOut(originalOut);
    }

    private sealed class SingleThreadAsyncPump : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> work = new();

        internal static void Run(Func<Task> action)
        {
            using var pump = new SingleThreadAsyncPump();
            var previous = Current;
            SetSynchronizationContext(pump);
            try
            {
                var task = action()
                    ?? throw new InvalidOperationException("The console capture callback returned a null task.");
                _ = task.ContinueWith(
                    static (_, state) => ((SingleThreadAsyncPump)state!).work.CompleteAdding(),
                    pump,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                foreach (var item in pump.work.GetConsumingEnumerable())
                    item.Callback(item.State);

                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public override void Post(SendOrPostCallback d, object? state)
            => work.Add((d, state));

        public void Dispose()
            => work.Dispose();
    }
}
