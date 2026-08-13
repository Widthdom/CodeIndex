using System.Diagnostics;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class FullScanProgressSession : IDisposable
    {
        private readonly IndexCommandOptions options;
        private readonly int filesCount;
        private readonly IndexProgressReporter indexProgress;
        private readonly FullScanPreWriteSelectionState selection;
        private bool redirectedIndexingMessagePrinted;
        private long lastJsonProgressAt = Stopwatch.GetTimestamp();
        private CancellationTokenSource? jsonHeartbeatCts;
        private Task? jsonHeartbeatTask;
        internal bool IndexProgressVisible { get; set; }
        internal string? CurrentJsonIndexFile { get; set; }
        internal ActiveExtractionPhase?[] ActiveExtractionPhases { get; set; } = [];

        internal FullScanProgressSession(
            IndexCommandOptions options,
            int filesCount,
            IndexProgressReporter indexProgress,
            FullScanPreWriteSelectionState selection)
        {
            this.options = options;
            this.filesCount = filesCount;
            this.indexProgress = indexProgress;
            this.selection = selection;
        }

        internal void EnsureIndexingActivityVisible()
        {
            if (options.Json || options.Quiet || IndexProgressVisible)
                return;

            if (indexProgress.Interactive)
            {
                indexProgress.Start();
                return;
            }

            if (redirectedIndexingMessagePrinted)
                return;

            CommandOutputWriter.WriteLine("Indexing...");
            redirectedIndexingMessagePrinted = true;
        }

        internal void ReportJsonIndexProgressIfNeeded()
        {
            if (!options.Json || options.Quiet || filesCount == 0)
                return;

            var processed = selection.Processed;
            var now = Stopwatch.GetTimestamp();
            if (processed == 0
                || processed == filesCount
                || processed % 100 == 0
                || Stopwatch.GetElapsedTime(lastJsonProgressAt, now)
                    >= TimeSpan.FromSeconds(5))
            {
                ConsoleUi.TryWriteErrorLine(
                    $"cdidx: indexed {processed:N0}/{filesCount:N0} file(s)...");
                lastJsonProgressAt = now;
            }
        }

        internal void StartJsonHeartbeatIfNeeded()
        {
            if (!options.Json
                || options.Quiet
                || filesCount == 0
                || jsonHeartbeatCts != null)
            {
                return;
            }

            jsonHeartbeatCts = new CancellationTokenSource();
            var token = jsonHeartbeatCts.Token;
            jsonHeartbeatTask = Task.Run(
                async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        if (token.IsCancellationRequested)
                            break;

                        var file = GetJsonIndexHeartbeatPath(
                            CurrentJsonIndexFile,
                            FormatActiveExtractionPhases(
                                ActiveExtractionPhases));
                        var fileSuffix = string.IsNullOrEmpty(file)
                            ? string.Empty
                            : $": {file}";
                        ConsoleUi.TryWriteErrorLine(
                            $"cdidx: still indexing {selection.Processed:N0}/{filesCount:N0} file(s){fileSuffix}...");
                    }
                },
                token);
        }

        internal void StopJsonHeartbeat()
        {
            if (jsonHeartbeatCts == null)
                return;

            jsonHeartbeatCts.Cancel();
            try
            {
                jsonHeartbeatTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex) when (
                ex.InnerExceptions.All(
                    inner => inner is OperationCanceledException
                        or TaskCanceledException))
            {
            }
            jsonHeartbeatCts.Dispose();
            jsonHeartbeatCts = null;
            jsonHeartbeatTask = null;
        }

        public void Dispose() => StopJsonHeartbeat();
    }
}
