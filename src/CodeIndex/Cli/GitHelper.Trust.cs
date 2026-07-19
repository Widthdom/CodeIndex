using System.Buffers.Binary;
using System.Runtime.InteropServices;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class GitHelper
{
    private const int UnixExecuteAccess = 1;
    private const int MaxPortableExecutableHeaderOffset = 16 * 1024 * 1024;
    private static readonly TimeSpan GitExecutableProbeTimeout = TimeSpan.FromSeconds(5);

    private static bool TryValidateGitExecutableAncestors(string executablePath, uint? effectiveUserId)
    {
        try
        {
            var current = Directory.GetParent(executablePath)?.FullName;
            while (current != null)
            {
                var probe = FileSystemBoundary.TryGetAttributes(current, out var attributes);
                if (probe != FileSystemBoundaryProbeStatus.Found
                    || (attributes & FileAttributes.Directory) == 0
                    || FileSystemBoundary.IsSymlinkOrReparsePoint(attributes)
                    || FileSystemBoundary.IsDevice(attributes))
                {
                    return false;
                }

                if (effectiveUserId is uint userId && !OperatingSystem.IsWindows())
                {
                    var mode = File.GetUnixFileMode(LongPath.EnsureWindowsPrefix(current));
                    if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0
                        || !FileIndexer.TryGetUnixFileOwnerId(LongPath.EnsureWindowsPrefix(current), out var ownerId)
                        || (ownerId != userId && ownerId != 0))
                    {
                        return false;
                    }
                }
                else if (effectiveUserId.HasValue)
                {
                    return false;
                }

                current = Directory.GetParent(current)?.FullName;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryValidateWindowsExecutableImage(string path)
    {
        try
        {
            using var stream = new FileStream(
                LongPath.EnsureWindowsPrefix(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            Span<byte> dosHeader = stackalloc byte[64];
            stream.ReadExactly(dosHeader);
            if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
                return false;

            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3c..]);
            if (peOffset < dosHeader.Length
                || peOffset > MaxPortableExecutableHeaderOffset
                || peOffset > stream.Length - 4)
            {
                return false;
            }

            stream.Position = peOffset;
            Span<byte> peSignature = stackalloc byte[4];
            stream.ReadExactly(peSignature);
            return peSignature.SequenceEqual("PE\0\0"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryProbeGitVersion(string executablePath)
    {
        var workingDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(workingDirectory))
            return false;

        var startInfo = CodeIndex.ProcessLaunchPolicy.CreateNoShellStartInfo(
            fileName: executablePath,
            workingDirectory: workingDirectory,
            redirectStandardOutput: true,
            redirectStandardError: true,
            createNoWindow: true);
        startInfo.ArgumentList.Add("--version");
        CodeIndex.SubprocessEnvironmentPolicy.ApplyGitEnvironment(startInfo);

        var result = GitProcessRunner.RunCapturingResult(startInfo, GitExecutableProbeTimeout);
        return result is { ExitCode: 0, FailureKind: GitCommandFailureKind.None }
               && result.Value.Output.Trim().StartsWith("git version ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveRealUnixPath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        IntPtr pointer = IntPtr.Zero;
        try
        {
            pointer = UnixRealPath(path, IntPtr.Zero);
            if (pointer == IntPtr.Zero)
                return false;

            var value = Marshal.PtrToStringUTF8(pointer);
            if (string.IsNullOrEmpty(value))
                return false;

            resolvedPath = PathCasing.NormalizeBoundaryPath(value);
            return HasExpectedGitExecutableName(resolvedPath);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
                UnixFree(pointer);
        }
    }

    private static bool TryGetEffectiveUnixUserId(out uint userId)
    {
        userId = 0;
        try
        {
            userId = UnixGetEffectiveUserId();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryAccessUnixExecutable(string path)
    {
        try
        {
            return UnixAccess(path, UnixExecuteAccess) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr UnixRealPath(string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void UnixFree(IntPtr pointer);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint UnixGetEffectiveUserId();

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int UnixAccess(string path, int mode);
}
