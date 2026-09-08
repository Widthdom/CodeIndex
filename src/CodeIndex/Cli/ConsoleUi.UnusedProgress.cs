using System.Diagnostics;
using System.Globalization;

namespace CodeIndex.Cli;

public static partial class ConsoleUi
{
    internal sealed class UnusedProgress : IDisposable
    {
        private static readonly AsyncLocal<TextWriter?> PreservedOutput = new();
        internal static IDisposable PreserveOutput() => new OutputScope();

        private sealed class OutputScope : IDisposable
        {
            private readonly TextWriter? _previous = PreservedOutput.Value;
            internal OutputScope() => PreservedOutput.Value = _previous ?? Console.Error;
            public void Dispose() => PreservedOutput.Value = _previous;
        }

        private readonly object _gate = new();
        private readonly TextWriter _output;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly Timer _timer;
        private bool _finished;

        internal UnusedProgress(TextWriter output)
        {
            _output = PreservedOutput.Value ?? output;
            Write("running");
            _timer = new Timer(_ => Write("running"), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        private void Write(string state)
        {
            lock (_gate)
            {
                if (_finished)
                    return;
                try
                {
                    lock (TerminalLock)
                    {
                        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"unused: {state} elapsed_ms={_elapsed.ElapsedMilliseconds}"));
                        _output.Flush();
                    }
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    _finished = true;
                }
            }
        }

        internal void Finish(string state)
        {
            lock (_gate)
            {
                Write(state);
                _finished = true;
            }
            _timer.Dispose();
        }

        public void Dispose() => Finish("failed");
    }
}
