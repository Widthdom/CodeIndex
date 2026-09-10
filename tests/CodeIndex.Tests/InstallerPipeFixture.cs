using System.Diagnostics;
using System.Globalization;

namespace CodeIndex.Tests;

// An intermediate shell exits so the pipe holder is no longer in the installer's kill tree.
// 中間シェルを終了させ、パイプ保持プロセスをインストーラーの kill ツリーから切り離します。
internal sealed class InstallerPipeFixture : IDisposable
{
    private readonly string _root = TestProjectHelper.CreateTempProject("cdidx_installer_pipe");
    private Process? _parent;
    private Process? _holder;
    private bool _disposed;

    internal string Script { get; }

    internal InstallerPipeFixture(int exitCode, string pipe, bool alignedOutput = false)
    {
        var redirect = pipe == "stdout" ? "2>/dev/null" : pipe == "stderr" ? ">/dev/null" : "";
        Script = $"""
            #!/bin/sh
            echo $$ > {Quote("parent.pid")}
            (sleep 60 {redirect} & echo $! > {Quote("holder.pid")})
            {(alignedOutput ? "printf '%01024d' 0" : "echo stdout-before-exit")}
            {(alignedOutput ? "printf '%01024d' 0 >&2" : "echo stderr-before-exit >&2")}
            echo ready > {Quote("ready")}
            while [ ! -f {Quote("release")} ]; do sleep 0.02; done
            exit {exitCode}
            """;
    }

    private string Quote(string name)
        => "'" + Path.Combine(_root, name).Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal ProcessStartInfo CreateStartInfo()
    {
        var script = Path.Combine(_root, "installer.sh");
        File.WriteAllText(script, Script);
        var start = new ProcessStartInfo("/bin/bash")
        {
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        return start;
    }

    internal async Task WaitUntilReadyAsync()
    {
        _parent = await ReadProcessAsync("parent.pid");
        _holder = await ReadProcessAsync("holder.pid");
        var ready = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(_root, "ready")))
        {
            if (ready.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Installer fixture did not finish orphaning its pipe holder.");
            await Task.Delay(20);
        }
        Assert.False(_holder.HasExited);
    }

    internal void ReleaseParent() => File.WriteAllText(Path.Combine(_root, "release"), "release");

    internal Task WaitForParentExitAsync() => _parent!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

    internal bool HolderIsRunning => _holder is { HasExited: false };

    private async Task<Process> ReadProcessAsync(string name)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (TryReadProcess(name) is { } process)
                return process;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Installer fixture did not publish {name}.");
    }

    private Process? TryReadProcess(string name)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(_root, name));
            return text.EndsWith('\n') && int.TryParse(text.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var pid) && pid > 0
                ? Process.GetProcessById(pid)
                : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Only private fixture PID files are consulted, including after failed readiness assertions.
        // readiness 検証失敗時も、この fixture 専用ディレクトリの PID だけを使用します。
        _parent ??= TryReadProcess("parent.pid");
        _holder ??= TryReadProcess("holder.pid");
        Stop(_parent);
        Stop(_holder);
        TestProjectHelper.DeleteDirectory(_root);
    }

    private static void Stop(Process? process)
    {
        if (process is null)
            return;
        using (process)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            Assert.True(process.WaitForExit(10_000), "Fixture-owned process did not stop.");
        }
    }
}
