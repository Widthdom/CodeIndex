using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit.Sdk;

namespace CodeIndex.Tests;

internal readonly record struct PublishedCli(string EntryPointPath, string PublishDirectory);

internal static class TrimmedCliTestHelper
{
    private static readonly string SharedPublishDirectory = Path.Combine(
        Path.GetTempPath(),
        $"cdidx_trimmed_publish_shared_{Environment.ProcessId}_{Guid.NewGuid():N}");

    private static readonly Lazy<PublishedCli> SharedTrimmedCliLazy = new(
        () => PublishTrimmedCli(SharedPublishDirectory),
        LazyThreadSafetyMode.ExecutionAndPublication);

    static TrimmedCliTestHelper()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TestProjectHelper.DeleteDirectory(SharedPublishDirectory);
    }

    internal static PublishedCli SharedTrimmedCli => SharedTrimmedCliLazy.Value;

    internal static PublishedCli PublishTrimmedCli(string outputDir, bool publishSingleFile = false)
    {
        Directory.CreateDirectory(outputDir);
        var buildOutputDir = Path.Combine(outputDir, "bin", "publish") + Path.DirectorySeparatorChar;
        var intermediateDir = Path.Combine(outputDir, "obj", "publish") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(intermediateDir);
        var lockFilePath = Path.Combine(intermediateDir, "packages.lock.json");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = GetRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add(Path.Combine("src", "CodeIndex", "CodeIndex.csproj"));
        psi.ArgumentList.Add("--configuration");
        psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--runtime");
        psi.ArgumentList.Add(RuntimeInformation.RuntimeIdentifier);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(outputDir);
        psi.ArgumentList.Add("-p:PublishTrimmed=true");
        psi.ArgumentList.Add("-p:SelfContained=true");
        psi.ArgumentList.Add($"-p:PublishSingleFile={publishSingleFile.ToString().ToLowerInvariant()}");
        psi.ArgumentList.Add($"-p:OutputPath={buildOutputDir}");
        psi.ArgumentList.Add($"-p:IntermediateOutputPath={intermediateDir}");
        psi.ArgumentList.Add($"-p:NuGetLockFilePath={lockFilePath}");
        psi.ArgumentList.Add("-p:NuGetAudit=false");
        psi.ArgumentList.Add("-p:UseSharedCompilation=false");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet publish / dotnet publish の起動に失敗");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var output = string.Join(Environment.NewLine, stdout, stderr).Trim();
            if (IsMissingDotNetRuntimeFailure(output))
                throw SkipException.ForSkip(BuildMissingDotNetRuntimeSkipReason(output));

            throw new InvalidOperationException($"dotnet publish failed: {output}");
        }

        var publishedAppHost = Path.Combine(outputDir, OperatingSystem.IsWindows() ? "cdidx.exe" : "cdidx");
        if (File.Exists(publishedAppHost))
            return new PublishedCli(publishedAppHost, outputDir);

        var publishedDll = Path.Combine(outputDir, "cdidx.dll");
        if (File.Exists(publishedDll))
            return new PublishedCli(publishedDll, outputDir);

        throw new InvalidOperationException(
            $"Published cdidx entry point not found. Expected {publishedDll} or {publishedAppHost}");
    }

    private static bool IsMissingDotNetRuntimeFailure(string output)
        => output.Contains("You must install or update .NET to run this application.", StringComparison.OrdinalIgnoreCase)
            && output.Contains("Framework: 'Microsoft.NETCore.App'", StringComparison.OrdinalIgnoreCase);

    private static string BuildMissingDotNetRuntimeSkipReason(string output)
    {
        const string reason = "Skipping trimmed publish test because the SDK/ILLink tool requires a .NET runtime that is not installed (#3571).";
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var frameworkLine = lines.FirstOrDefault(line => line.StartsWith("Framework:", StringComparison.OrdinalIgnoreCase));
        return frameworkLine == null ? reason : $"{reason} {frameworkLine}";
    }

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeIndex.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root / リポジトリルートを特定できませんでした");
    }
}
