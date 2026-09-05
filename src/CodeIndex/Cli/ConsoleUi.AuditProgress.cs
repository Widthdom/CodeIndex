using System.Diagnostics;
using System.Globalization;

namespace CodeIndex.Cli;

public static partial class ConsoleUi
{
    internal static bool ShouldUseInteractiveStandardError()
        => ShouldUseInteractiveConsole(
            Console.IsErrorRedirected, Console.Error.Encoding, Console.Error is StringWriter,
            HasTerminalEnvironmentHint(), IsTerminalEnvironmentDisabled(), OperatingSystem.IsWindows());

    // Only ordinal identifiers enter this sink: custom recipe/query names can contain
    // paths, source text, or credentials. Ordinals refer to the ordered result registry.
    internal sealed class AuditProgress : IDisposable
    {
        private readonly object _gate = new();
        private readonly TextWriter _output;
        private readonly bool _interactive;
        private readonly int _width;
        private readonly Func<TimeSpan> _elapsed;
        private Timer? _timer;
        private readonly bool _startTimer;
        private readonly int _selectedRecipes;
        private readonly long _selectedQueries;
        private int _activeRecipe;
        private int _activeQuery;
        private int _completedRecipes;
        private long _completedQueries;
        private long _failedQueries;
        private TimeSpan _lastWrite;
        private bool _finished;
        private bool _started;
        private bool _paused;
        private int _lastWidth;

        internal AuditProgress(int recipes, long queries, TextWriter output, bool interactive,
            int width = 256, Func<TimeSpan>? elapsed = null, bool startTimer = true, bool startImmediately = true)
        {
            _selectedRecipes = recipes;
            _selectedQueries = queries;
            _output = output;
            _interactive = interactive;
            _width = Math.Clamp(width - 1, 1, 256);
            var stopwatch = Stopwatch.StartNew();
            _elapsed = elapsed ?? (() => stopwatch.Elapsed);
            _startTimer = startTimer;
            if (startImmediately)
                Start();
        }

        internal void Start()
        {
            lock (_gate)
            {
                if (_started || _finished)
                    return;
                _started = true;
                Write("running");
                if (_startTimer && !_finished)
                    _timer = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }

        internal void PauseForOutput()
        {
            lock (_gate)
            {
                _paused = true;
                if (!_interactive || _lastWidth == 0)
                    return;
                try
                {
                    lock (TerminalLock)
                    {
                        _output.Write("\r" + new string(' ', _lastWidth) + "\r");
                        _output.Flush();
                        _lastWidth = 0;
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    _finished = true;
                }
            }
        }

        internal void SetActive(int recipe, int query)
        {
            lock (_gate)
            {
                _activeRecipe = recipe;
                _activeQuery = query;
            }
        }

        internal void SetCompleted(int recipes, long queries, long failedQueries)
        {
            lock (_gate)
            {
                _completedRecipes = recipes;
                _completedQueries = queries;
                _failedQueries = failedQueries;
            }
        }

        internal void Heartbeat()
        {
            lock (_gate)
            {
                if (_started && !_finished && !_paused && _elapsed() - _lastWrite >= TimeSpan.FromSeconds(1))
                    Write("running");
            }
        }

        internal void Finish(string status)
        {
            lock (_gate)
            {
                if (_finished)
                    return;
                _activeRecipe = 0;
                _activeQuery = 0;
                Write(status);
                _finished = true;
            }
            _timer?.Dispose();
        }

        private void Write(string status)
        {
            _lastWrite = _elapsed();
            var line = string.Create(CultureInfo.InvariantCulture,
                $"audit: {status} active_recipe={_activeRecipe} active_query={_activeQuery} elapsed_ms={(long)_lastWrite.TotalMilliseconds} recipes_completed={_completedRecipes}/{_selectedRecipes} queries_completed={_completedQueries}/{_selectedQueries} queries_failed={_failedQueries}");
            if (_interactive)
                line = string.Create(CultureInfo.InvariantCulture,
                    $"audit: {status} r={_activeRecipe} q={_activeQuery} done={_completedRecipes}/{_selectedRecipes}r,{_completedQueries}/{_selectedQueries}q fail={_failedQueries} ms={(long)_lastWrite.TotalMilliseconds}");
            var limit = _interactive ? _width : 256;
            if (line.Length > limit)
                line = line[..limit];
            try
            {
                lock (TerminalLock)
                {
                    if (_interactive)
                    {
                        _output.Write("\r" + line + new string(' ', Math.Max(0, _lastWidth - line.Length)));
                        _lastWidth = line.Length;
                        if (status != "running")
                            _output.WriteLine();
                    }
                    else
                        _output.WriteLine(line);
                    _output.Flush();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A closed diagnostic stream must not change the audit result.
                _finished = true;
            }
        }

        public void Dispose()
        {
            Finish("failed");
            _timer?.Dispose();
        }
    }
}
