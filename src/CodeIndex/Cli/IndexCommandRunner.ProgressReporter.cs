namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class IndexProgressReporter(
        IndexCommandOptions options,
        string spinnerMessage,
        string[] spinnerFrames,
        Action<string> writeJsonVerbose,
        Func<bool>? canResume = null,
        bool clearProgressLineBeforeWrite = false)
    {
        private CancellationTokenSource? spinner;

        internal bool Interactive { get; } =
            !options.Json
            && !options.Quiet
            && ConsoleUi.ShouldUseInteractiveConsole();

        internal void Start()
        {
            if (!Interactive || spinner != null)
                return;

            spinner = ConsoleUi.StartSpinner(spinnerMessage, spinnerFrames);
        }

        internal void Pause()
        {
            if (spinner == null)
                return;

            ConsoleUi.StopSpinner(spinner);
            spinner = null;
        }

        internal void Resume()
        {
            if (!Interactive || canResume?.Invoke() == false)
                return;

            Start();
        }

        internal void WriteVerbose(string message)
        {
            if (!options.Verbose || options.Quiet)
                return;

            if (options.Json)
            {
                writeJsonVerbose(message);
                return;
            }

            Pause();
            if (clearProgressLineBeforeWrite)
                ConsoleUi.ClearProgressLine();
            CommandOutputWriter.WriteLine(message);
            Resume();
        }
    }
}
