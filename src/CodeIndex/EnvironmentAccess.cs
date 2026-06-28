using System.Collections;
using System.Threading;

namespace CodeIndex;

internal static class EnvironmentAccess
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.Ordinal);

    internal static string? GetEnvironmentVariable(string name)
    {
        for (var scope = Current.Value; scope is not null; scope = scope.Parent)
        {
            if (scope.Values.TryGetValue(name, out var value))
                return value;
        }

        return GetProcessEnvironmentVariable(name);
    }

    internal static string? GetProcessEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name);

    internal static IEnumerable<(string Key, string Value)> EnumerateProcessEnvironmentVariables()
    {
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            if (item.Key is string key && item.Value is string value)
                yield return (key, value);
        }
    }

    internal static string? GetConfigSource(string name, string processEnvironmentPrefix)
    {
        for (var scope = Current.Value; scope is not null; scope = scope.Parent)
        {
            if (scope.Sources.TryGetValue(name, out var source))
                return source;
        }

        return GetProcessEnvironmentVariable(processEnvironmentPrefix + name);
    }

    internal static IDisposable Push(
        IReadOnlyDictionary<string, string>? values,
        IReadOnlyDictionary<string, string>? sources = null)
    {
        if ((values is null || values.Count == 0) && (sources is null || sources.Count == 0))
            return NoopScope.Instance;

        var previous = Current.Value;
        Current.Value = new Scope(previous, Copy(values), Copy(sources));
        return new ScopeToken(previous);
    }

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string>? values)
        => values is null || values.Count == 0
            ? Empty
            : new Dictionary<string, string>(values, StringComparer.Ordinal);

    private sealed record Scope(
        Scope? Parent,
        IReadOnlyDictionary<string, string> Values,
        IReadOnlyDictionary<string, string> Sources);

    private sealed class ScopeToken(Scope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            Current.Value = previous;
            _disposed = true;
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
