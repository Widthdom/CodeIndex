namespace CodeIndex.Diagnostics;

internal static class BackgroundTaskObserver
{
    private const string DefaultComponent = "cdidx";
    private const string DefaultOperation = "background task";

    internal static Task Run(
        Func<Task> action,
        string component,
        string operation,
        Action<string>? warningWriter = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        return Observe(Task.Run(action, CancellationToken.None), component, operation, warningWriter);
    }

    internal static Task Run(
        Func<CancellationToken, Task> action,
        string component,
        string operation,
        CancellationToken cancellationToken,
        Action<string>? warningWriter = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        return Observe(Task.Run(() => action(cancellationToken), CancellationToken.None), component, operation, warningWriter);
    }

    internal static Task Observe(
        Task task,
        string component,
        string operation,
        Action<string>? warningWriter = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var (componentName, operationName, writer) = ((string Component, string Operation, Action<string>? Writer))state!;
                var exception = completed.Exception?.Flatten().InnerExceptions.FirstOrDefault();
                if (exception is null)
                    return;

                WriteWarning(writer, FormatFailureMessage(componentName, operationName, exception));
            },
            (NormalizeLabel(component, DefaultComponent), NormalizeLabel(operation, DefaultOperation), warningWriter),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    private static string FormatFailureMessage(string component, string operation, Exception exception)
    {
        var exceptionType = NormalizeLabel(exception.GetType().Name, nameof(Exception));
        var category = DiagnosticRedactor.ClassifyException(exception);
        return $"Warning: background task '{operation}' failed in {component} ({category}: {exceptionType}).";
    }

    private static string NormalizeLabel(string? value, string fallback)
    {
        var sanitized = DiagnosticRedactor.RedactSensitiveText(DiagnosticSanitizer.ForMessage(value), redactPaths: true);
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static void WriteWarning(Action<string>? warningWriter, string message)
    {
        try
        {
            if (warningWriter is not null)
                warningWriter(message);
            else
                Console.Error.WriteLine(message);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }
}
