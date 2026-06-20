using System.Reflection;
using CodeIndex.Diagnostics;

namespace CodeIndex.Indexer.Extensibility;

internal static class ExtensionLoadDiagnosticClassifier
{
    private const int MaxLoaderExceptionDetails = 3;

    internal static ExtensionLoadDiagnostic ClassifyAssemblyLoad(string subject, Exception ex)
    {
        var category = IsDependencyResolutionException(ex)
            ? "dependency_resolution_failed"
            : "assembly_load_failed";
        return new ExtensionLoadDiagnostic(
            category,
            $"{subject} failed: {FormatException(category, ex)}.");
    }

    internal static ExtensionLoadDiagnostic ClassifyTypeLoad(string subject, ReflectionTypeLoadException ex)
    {
        const string category = "type_load_failed";
        var loaderDetails = FormatLoaderExceptions(ex.LoaderExceptions);
        var suffix = string.IsNullOrEmpty(loaderDetails)
            ? string.Empty
            : $" Loader exceptions: {loaderDetails}.";
        return new ExtensionLoadDiagnostic(
            category,
            $"{subject} failed: {FormatException(category, ex)}.{suffix}");
    }

    internal static ExtensionLoadDiagnostic ClassifyConstructorFailure(string subject, Exception ex)
    {
        const string category = "constructor_failed";
        var failure = UnwrapTargetInvocation(ex);
        return new ExtensionLoadDiagnostic(
            category,
            $"{subject} failed: {FormatException(category, failure)}.");
    }

    private static string FormatLoaderExceptions(IEnumerable<Exception?> loaderExceptions)
    {
        var details = loaderExceptions
            .Where(exception => exception != null)
            .Select(exception => FormatException("loader_exception", exception!))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxLoaderExceptionDetails)
            .ToList();

        return string.Join(", ", details);
    }

    private static string FormatException(string category, Exception ex)
    {
        var formatted = SafeDiagnosticFormatter.FormatExceptionCategory(category, ex);
        var dependencyName = TryGetDependencyName(ex);
        return dependencyName == null
            ? formatted
            : $"{formatted} ({dependencyName})";
    }

    private static string? TryGetDependencyName(Exception ex)
    {
        var fileName = ex switch
        {
            FileNotFoundException fileNotFound => fileNotFound.FileName,
            FileLoadException fileLoad => fileLoad.FileName,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        return "dependency " + DiagnosticSanitizer.ForMessage(fileName);
    }

    private static bool IsDependencyResolutionException(Exception ex)
        => ex is FileNotFoundException or FileLoadException;

    private static Exception UnwrapTargetInvocation(Exception ex)
        => ex is TargetInvocationException { InnerException: not null }
            ? ex.InnerException
            : ex;
}

internal sealed record ExtensionLoadDiagnostic(string Category, string Message);
