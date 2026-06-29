using System.Diagnostics;
using System.Text;

namespace CodeIndex.Tests;

public partial class ReleaseWorkflowTests
{
    [Fact]
    public void ReleaseWorkflow_DockerfileDocumentsSdkRuntimeSplit()
    {
        var dockerfile = RepositoryTestPaths.ReadText("Dockerfile");

        Assert.Contains("Build uses the repository-pinned .NET 9 SDK", dockerfile);
        Assert.Contains("runtime-deps because cdidx targets net8.0", dockerfile);
        Assert.Contains("docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:9.0.301-alpine3.22", dockerfile);
        Assert.Contains("docker buildx imagetools inspect mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine", dockerfile);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:9.0.301-alpine3.22@sha256:", dockerfile);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine@sha256:", dockerfile);
    }

    [Fact]
    public void ReleaseWorkflow_DockerfileLocksRuntimePackagesAndRidMapping()
    {
        var dockerfile = RepositoryTestPaths.ReadText("Dockerfile").ReplaceLineEndings("\n");

        Assert.Equal(2, dockerfile.Split('\n').Count(line => line.Contains("amd64) rid=\"linux-musl-x64\" ;;", StringComparison.Ordinal)));
        Assert.Equal(2, dockerfile.Split('\n').Count(line => line.Contains("arm64) rid=\"linux-musl-arm64\" ;;", StringComparison.Ordinal)));
        Assert.Equal(2, dockerfile.Split('\n').Count(line => line.Contains("Unsupported container architecture: $TARGETARCH", StringComparison.Ordinal)));

        var apkAddLines = dockerfile.Split('\n')
            .Where(line => line.TrimStart().StartsWith("RUN apk add ", StringComparison.Ordinal))
            .ToArray();
        var apkAddLine = Assert.Single(apkAddLines);
        Assert.Equal("RUN apk add --no-cache ca-certificates su-exec \\", apkAddLine);
        Assert.DoesNotContain(" apk add --update ", dockerfile);
        Assert.DoesNotContain(" apk add --upgrade ", dockerfile);
        Assert.DoesNotContain(" apk add --no-cache bash", dockerfile);
        Assert.DoesNotContain(" apk add --no-cache curl", dockerfile);
        Assert.DoesNotContain(" apk add --no-cache git", dockerfile);
        Assert.DoesNotContain(" apk add --no-cache shadow", dockerfile);
    }

    [Fact]
    public void ReleaseWorkflow_DockerEntrypointDropsRootToRequestedUidGid()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_entrypoint_drop_root");
        try
        {
            var binDir = Path.Combine(projectRoot, "bin");
            var tracePath = Path.Combine(projectRoot, "trace.txt");
            Directory.CreateDirectory(binDir);
            WriteExecutable(Path.Combine(binDir, "id"), """
                #!/bin/sh
                if [ "$1" = "-u" ]; then
                    echo 0
                    exit 0
                fi
                exit 97
                """);
            WriteExecutable(Path.Combine(binDir, "su-exec"), """
                #!/bin/sh
                {
                    printf 'su-exec:%s\n' "$*"
                    printf 'HOME=%s\n' "$HOME"
                } > "$CDIDX_TRACE"
                """);
            WriteExecutable(Path.Combine(binDir, "cdidx"), """
                #!/bin/sh
                {
                    printf 'cdidx:%s\n' "$*"
                    printf 'HOME=%s\n' "${HOME:-}"
                } > "$CDIDX_TRACE"
                """);

            var result = RunDockerEntrypoint(binDir, tracePath, "123", "456", "--version");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Stderr);
            var trace = File.ReadAllText(tracePath);
            Assert.Contains("su-exec:123:456 cdidx --version", trace);
            Assert.Contains("HOME=/repo", trace);
            Assert.DoesNotContain("cdidx:", trace);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ReleaseWorkflow_DockerEntrypointCanKeepRootWhenExplicitlyRequested()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_entrypoint_keep_root");
        try
        {
            var binDir = Path.Combine(projectRoot, "bin");
            var tracePath = Path.Combine(projectRoot, "trace.txt");
            Directory.CreateDirectory(binDir);
            WriteExecutable(Path.Combine(binDir, "id"), """
                #!/bin/sh
                if [ "$1" = "-u" ]; then
                    echo 0
                    exit 0
                fi
                exit 97
                """);
            WriteExecutable(Path.Combine(binDir, "su-exec"), """
                #!/bin/sh
                printf 'unexpected su-exec:%s\n' "$*" > "$CDIDX_TRACE"
                exit 98
                """);
            WriteExecutable(Path.Combine(binDir, "cdidx"), """
                #!/bin/sh
                {
                    printf 'cdidx:%s\n' "$*"
                    printf 'HOME=%s\n' "${HOME:-}"
                } > "$CDIDX_TRACE"
                """);

            var result = RunDockerEntrypoint(binDir, tracePath, "0", "0", "status", "--json");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Stderr);
            var trace = File.ReadAllText(tracePath);
            Assert.Contains("cdidx:status --json", trace);
            Assert.DoesNotContain("unexpected su-exec", trace);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunDockerEntrypoint(
        string binDir,
        string tracePath,
        string targetUid,
        string targetGid,
        params string[] args)
    {
        var entrypointPath = RepositoryTestPaths.Combine("scripts", "docker-entrypoint.sh");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.StartInfo.ArgumentList.Add(entrypointPath);
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["PATH"] = binDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        process.StartInfo.Environment["CDIDX_TRACE"] = tracePath;
        process.StartInfo.Environment["CDIDX_RUN_UID"] = targetUid;
        process.StartInfo.Environment["CDIDX_RUN_GID"] = targetGid;

        process.Start();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("docker-entrypoint.sh fixture did not exit within 5 seconds.");
        }

        return (process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Executable fixture permissions require a Unix filesystem.");

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
