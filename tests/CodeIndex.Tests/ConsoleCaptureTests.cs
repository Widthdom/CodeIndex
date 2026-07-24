using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public class ConsoleCaptureTests
{
    [Fact]
    public void ConsoleSensitiveCollection_AssignsImportCancellationAndDisablesParallelization_Issues4650_4798()
    {
        var attribute = Assert.Single(
            typeof(ExportImportCommandRunnerCancellationTests).CustomAttributes,
            static candidate => candidate.AttributeType == typeof(CollectionAttribute));
        var collectionName = Assert.Single(attribute.ConstructorArguments);
        var definition = Assert.Single(
            typeof(ConsoleSensitiveCollection).CustomAttributes,
            static candidate => candidate.AttributeType == typeof(CollectionDefinitionAttribute));
        var disableParallelization = Assert.Single(
            definition.NamedArguments,
            static candidate => candidate.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization));

        Assert.Equal("Console sensitive", collectionName.Value);
        Assert.True(Assert.IsType<bool>(disableParallelization.TypedValue.Value));
    }

    [Fact]
    public void CaptureError_RestoresConsoleError_WhenActionThrows()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                ConsoleCapture.CaptureError(() => throw new InvalidOperationException("boom")));

            Assert.Equal("boom", ex.Message);
            Assert.Same(originalError, Console.Error);
        }
    }

    [Fact]
    public void Capture_RestoresConsoleStreams_WhenActionThrows()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                ConsoleCapture.Capture(() => throw new InvalidOperationException("boom")));

            Assert.Equal("boom", ex.Message);
            Assert.Same(originalOut, Console.Out);
            Assert.Same(originalError, Console.Error);
        }
    }

    [Fact]
    public void Dispose_DoesNotCloseCapturedWriters()
    {
        lock (TestConsoleLock.Gate)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            using (var capture = ConsoleCapture.Start(stdout, stderr))
            {
                Console.Write("out");
                Console.Error.Write("err");
            }

            stdout.Write(" after");
            stderr.Write(" after");

            Assert.Equal("out after", stdout.ToString());
            Assert.Equal("err after", stderr.ToString());
        }
    }

    [Fact]
    public void CaptureError_RestoresUsableConsoleError_WhenActionIsCancelled_Issue4749()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var baselineError = new StringWriter();
            try
            {
                Console.SetError(baselineError);
                var installedBaselineError = Console.Error;

                Assert.Throws<OperationCanceledException>(() =>
                    ConsoleCapture.CaptureError(() => throw new OperationCanceledException()));

                Assert.Same(installedBaselineError, Console.Error);
                CommandErrorWriter.WriteStderr("recovered");
                Assert.Contains("recovered", baselineError.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                ConsoleStreamOwnership.RestoreError(originalError);
            }
        }
    }

    [Fact]
    public async Task EnsureConsoleWritersSynchronized_WaitsForActiveCapture_Issue4749()
    {
        TextWriter originalOut;
        TextWriter originalError;
        using (ConsoleStreamOwnership.Enter())
        {
            originalOut = Console.Out;
            originalError = Console.Error;
        }
        using var releaseCapture = new ManualResetEventSlim();
        var captureReady = new TaskCompletionSource<(TextWriter Out, TextWriter Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronizationAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var captureOwner = Task.Run(() =>
        {
            using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
            captureReady.SetResult((capture.Out!, capture.Error!));
            if (!releaseCapture.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timed out waiting to release the console capture.");
        });
        var synchronization = Task.CompletedTask;
        var restored = false;
        try
        {
            var (capturedOut, capturedError) = await captureReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            synchronization = Task.Run(() =>
            {
                synchronizationAttempted.SetResult();
                ConsoleUi.EnsureConsoleWritersSynchronized();
            });

            await synchronizationAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await TestDeterminism.AssertTaskRemainsBlockedAsync(synchronization);

            releaseCapture.Set();

            await captureOwner.WaitAsync(TimeSpan.FromSeconds(5));
            await synchronization.WaitAsync(TimeSpan.FromSeconds(5));
            using var ownership = ConsoleStreamOwnership.Enter();
            try
            {
                Assert.NotSame(capturedOut, Console.Out);
                Assert.NotSame(capturedError, Console.Error);
                Assert.Null(Record.Exception(() => Console.Out.Flush()));
                Assert.Null(Record.Exception(() => Console.Error.Flush()));
            }
            finally
            {
                ConsoleStreamOwnership.Restore(originalOut, originalError);
                restored = true;
            }
        }
        finally
        {
            releaseCapture.Set();
            try
            {
                await Task.WhenAll(captureOwner, synchronization)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                if (!restored)
                {
                    using var ownership = ConsoleStreamOwnership.Enter();
                    ConsoleStreamOwnership.Restore(originalOut, originalError);
                }
            }
        }
    }

    [Fact]
    public async Task CaptureAsync_AwaitedContinuationCanReenterConsoleOwnership_Issue4749()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await ConsoleCapture.CaptureAsync(
            async cancellationToken =>
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                ConsoleUi.EnsureConsoleWritersSynchronized();
                Console.Write("out");
                Console.Error.Write("err");
            },
            stdout,
            stderr,
            timeout: TimeSpan.FromSeconds(5));

        Assert.Equal("out", stdout.ToString());
        Assert.Equal("err", stderr.ToString());
    }

    [Fact]
    public async Task CaptureAsync_TimeoutRestoresOnlyAfterCallbackObservesCancellation_Issue4749()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ConsoleCapture.CaptureAsync(
                async cancellationToken =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    finally
                    {
                        callbackExited.SetResult();
                    }
                },
                stdout,
                stderr,
                timeout: TimeSpan.FromMilliseconds(250)));

        Assert.Contains("within", exception.Message, StringComparison.Ordinal);
        Assert.True(callbackExited.Task.IsCompletedSuccessfully);
        Assert.Same(originalOut, Console.Out);
        Assert.Same(originalError, Console.Error);
        Assert.Null(Record.Exception(() => Console.Out.Flush()));
        Assert.Null(Record.Exception(() => Console.Error.Flush()));
    }
}
