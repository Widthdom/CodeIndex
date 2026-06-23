using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CodeIndex;

internal static class ProcessLaunchPolicy
{
    internal static ProcessStartInfo CreateNoShellStartInfo(
        string? fileName = null,
        string? workingDirectory = null,
        bool redirectStandardInput = false,
        bool redirectStandardOutput = false,
        bool redirectStandardError = false,
        bool createNoWindow = false)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = redirectStandardOutput,
            RedirectStandardError = redirectStandardError,
            CreateNoWindow = createNoWindow,
        };
        if (!string.IsNullOrEmpty(fileName))
            startInfo.FileName = fileName;
        if (workingDirectory != null)
            startInfo.WorkingDirectory = workingDirectory;
        return startInfo;
    }

    internal static ProcessStartInfo CreateUtf8RedirectedWorkerStartInfo()
    {
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = CreateNoShellStartInfo(
            redirectStandardInput: true,
            redirectStandardOutput: true,
            redirectStandardError: true,
            createNoWindow: true);
        startInfo.StandardInputEncoding = utf8NoBom;
        startInfo.StandardOutputEncoding = utf8NoBom;
        startInfo.StandardErrorEncoding = utf8NoBom;
        return startInfo;
    }

    internal static void AddArguments(ProcessStartInfo startInfo, params string[] args)
    {
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
    }

    internal static void AddInvariantIntArgument(ProcessStartInfo startInfo, string optionName, int value)
    {
        startInfo.ArgumentList.Add(optionName);
        startInfo.ArgumentList.Add(value.ToString(CultureInfo.InvariantCulture));
    }
}
