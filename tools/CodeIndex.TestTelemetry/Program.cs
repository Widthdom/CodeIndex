using System.Globalization;

namespace CodeIndex.TestTelemetry;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            switch (args[0])
            {
                case "summarize":
                    {
                        var options = ParseSummarizeOptions(args[1..]);
                        var summary = TrxTelemetry.Load(options.ResultsDirectory, options.Top);
                        Console.Out.Write(TrxTelemetryRenderer.Render(summary));
                        return 0;
                    }

                case "skips":
                    {
                        var options = ParseSkipOptions(args[1..]);
                        var summary = SkipTelemetry.Load(options.TestsDirectory, options.Top);
                        Console.Out.Write(SkipTelemetryRenderer.Render(summary));
                        return 0;
                    }

                default:
                    throw new TelemetryException($"Unknown command '{args[0]}'.");
            }
        }
        catch (TelemetryException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool IsHelp(string arg) => arg is "-h" or "--help" or "help";

    private static void PrintUsage()
    {
        Console.Out.WriteLine("Usage:");
        Console.Out.WriteLine("  dotnet run --project tools/CodeIndex.TestTelemetry -- summarize --results-directory ./TestResults [--top 10]");
        Console.Out.WriteLine("  dotnet run --project tools/CodeIndex.TestTelemetry -- skips --tests-directory tests/CodeIndex.Tests [--top 25]");
    }

    private static SummarizeOptions ParseSummarizeOptions(string[] args)
    {
        var resultsDirectory = "TestResults";
        var top = 10;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--results-directory")
            {
                if (i + 1 >= args.Length)
                    throw new TelemetryException("Missing value for --results-directory.");

                resultsDirectory = args[++i];
                continue;
            }

            if (arg == "--top")
            {
                if (i + 1 >= args.Length)
                    throw new TelemetryException("Missing value for --top.");

                if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out top) ||
                    top <= 0 ||
                    top > TrxTelemetry.MaxTop)
                {
                    throw new TelemetryException($"--top must be between 1 and {TrxTelemetry.MaxTop}.");
                }

                continue;
            }

            throw new TelemetryException($"Unknown option '{arg}'.");
        }

        return new SummarizeOptions(resultsDirectory, top);
    }

    private static SkipOptions ParseSkipOptions(string[] args)
    {
        var testsDirectory = Path.Combine("tests", "CodeIndex.Tests");
        var top = 25;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--tests-directory")
            {
                if (i + 1 >= args.Length)
                    throw new TelemetryException("Missing value for --tests-directory.");

                testsDirectory = args[++i];
                continue;
            }

            if (arg == "--top")
            {
                if (i + 1 >= args.Length)
                    throw new TelemetryException("Missing value for --top.");

                if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out top) ||
                    top <= 0 ||
                    top > SkipTelemetry.MaxTop)
                {
                    throw new TelemetryException($"--top must be between 1 and {SkipTelemetry.MaxTop}.");
                }

                continue;
            }

            throw new TelemetryException($"Unknown option '{arg}'.");
        }

        return new SkipOptions(testsDirectory, top);
    }

    private sealed record SummarizeOptions(string ResultsDirectory, int Top);

    private sealed record SkipOptions(string TestsDirectory, int Top);
}

public sealed class TelemetryException(string message) : Exception(message);
