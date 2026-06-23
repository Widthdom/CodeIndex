using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;

namespace CodeIndex.Indexer;

internal static class IsolatedWorkerProcessLauncher
{
    internal static ProcessStartInfo CreateStartInfo()
    {
        var startInfo = CodeIndex.ProcessLaunchPolicy.CreateUtf8RedirectedWorkerStartInfo();
        CodeIndex.SubprocessEnvironmentPolicy.ApplyIsolatedWorkerEnvironment(startInfo);
        return startInfo;
    }

    internal static bool ShouldStartCurrentExecutable(
        string? currentProcessPath,
        string? runnerAssemblyPath,
        Assembly runnerAssembly)
    {
        if (string.IsNullOrWhiteSpace(currentProcessPath) || DotnetHostPathResolver.IsDotnetHostPath(currentProcessPath))
            return false;

        var processName = Path.GetFileNameWithoutExtension(currentProcessPath);
        var appName = runnerAssembly.GetName().Name;
        if (!string.IsNullOrWhiteSpace(appName)
            && string.Equals(processName, appName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(runnerAssemblyPath);
    }

    internal static string? ResolveCurrentRunnerAssemblyPath(Assembly runnerAssembly)
    {
        var assemblyName = runnerAssembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
            return null;

        var candidate = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    internal static bool TryPrepareFrameworkDependentStartInfo(
        ProcessStartInfo startInfo,
        string? currentProcessPath,
        string? runnerAssemblyPath,
        Assembly runnerAssembly,
        string missingAssemblyError,
        string missingDotnetHostError,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(runnerAssemblyPath))
        {
            error = missingAssemblyError;
            return false;
        }

        var dotnetHostPath = DotnetHostPathResolver.Resolve(currentProcessPath);
        if (dotnetHostPath == null)
        {
            error = missingDotnetHostError;
            return false;
        }

        startInfo.FileName = dotnetHostPath;
        startInfo.ArgumentList.Add(runnerAssemblyPath);
        ApplyCurrentRuntimeRollForward(startInfo, runnerAssembly);
        error = string.Empty;
        return true;
    }

    private static void ApplyCurrentRuntimeRollForward(ProcessStartInfo startInfo, Assembly runnerAssembly)
    {
        var targetMajor = GetRunnerTargetFrameworkMajor(runnerAssembly);
        if (targetMajor.HasValue && Environment.Version.Major > targetMajor.Value)
            startInfo.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
    }

    private static int? GetRunnerTargetFrameworkMajor(Assembly runnerAssembly)
    {
        var frameworkName = runnerAssembly
            .GetCustomAttribute<TargetFrameworkAttribute>()
            ?.FrameworkName;
        if (string.IsNullOrWhiteSpace(frameworkName))
            return null;

        const string versionPrefix = "Version=v";
        var versionIndex = frameworkName.IndexOf(versionPrefix, StringComparison.OrdinalIgnoreCase);
        if (versionIndex < 0)
            return null;

        var majorStart = versionIndex + versionPrefix.Length;
        var majorEnd = frameworkName.IndexOf('.', majorStart);
        var majorText = majorEnd < 0
            ? frameworkName[majorStart..]
            : frameworkName[majorStart..majorEnd];
        return int.TryParse(
            majorText,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var major)
            ? major
            : null;
    }
}
