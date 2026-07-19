using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class GitHelper
{
    private const int UnixExecuteAccess = 1;
    private const int MaxPortableExecutableHeaderOffset = 16 * 1024 * 1024;
    private static readonly TimeSpan GitExecutableProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<Func<string, bool>?> GitVersionProbeOverride = new();

    internal static Func<string, bool>? GitVersionProbeForTesting
    {
        get => GitVersionProbeOverride.Value;
        set => GitVersionProbeOverride.Value = value;
    }

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
                    if (!FileIndexer.TryGetUnixFileOwnerId(LongPath.EnsureWindowsPrefix(current), out var ownerId)
                        || (ownerId != userId && ownerId != 0))
                    {
                        return false;
                    }

                    var sharedWritable = (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
                    var rootOwnedStickyDirectory = ownerId == 0
                                                   && (mode & UnixFileMode.StickyBit) != 0;
                    if (sharedWritable && !rootOwnedStickyDirectory)
                        return false;
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

    [SupportedOSPlatform("windows")]
    private static bool TryValidateWindowsExecutableAcl(
        string executablePath,
        out string? ownerCategory,
        out bool ownerTrusted)
    {
        ownerCategory = null;
        ownerTrusted = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var currentUser = identity.User;
            if (currentUser == null)
                return false;

            var trustedSids = new HashSet<string>(StringComparer.Ordinal)
            {
                currentUser.Value,
                "S-1-5-18", // LocalSystem
                "S-1-5-32-544", // Builtin Administrators
                "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
            };

            var current = executablePath;
            var finalEntry = true;
            while (current != null)
            {
                var attributes = File.GetAttributes(LongPath.EnsureWindowsPrefix(current));
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                FileSystemSecurity security = isDirectory
                    ? FileSystemAclExtensions.GetAccessControl(
                        new DirectoryInfo(current),
                        AccessControlSections.Owner | AccessControlSections.Access)
                    : FileSystemAclExtensions.GetAccessControl(
                        new FileInfo(current),
                        AccessControlSections.Owner | AccessControlSections.Access);

                var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner == null)
                    return false;

                var trustedOwner = trustedSids.Contains(owner.Value);
                if (finalEntry)
                {
                    ownerCategory = owner.Value == currentUser.Value
                        ? "current_user"
                        : owner.Value == "S-1-5-18"
                            ? "system"
                            : owner.Value == "S-1-5-32-544"
                                ? "administrators"
                                : owner.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
                                    ? "trusted_installer"
                                    : "other";
                    ownerTrusted = trustedOwner;
                }

                if (!trustedOwner || HasUntrustedWindowsWriteRule(security, trustedSids, finalEntry))
                    return false;

                finalEntry = false;
                current = Directory.GetParent(current)?.FullName;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasUntrustedWindowsWriteRule(
        FileSystemSecurity security,
        IReadOnlySet<string> trustedSids,
        bool finalEntry)
    {
        var dangerousRights = FileSystemRights.Delete
                              | FileSystemRights.DeleteSubdirectoriesAndFiles
                              | FileSystemRights.ChangePermissions
                              | FileSystemRights.TakeOwnership;
        if (finalEntry)
        {
            dangerousRights |= FileSystemRights.WriteData
                               | FileSystemRights.AppendData
                               | FileSystemRights.WriteAttributes
                               | FileSystemRights.WriteExtendedAttributes;
        }

        foreach (var rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>())
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0
                || rule.IdentityReference is not SecurityIdentifier sid
                || trustedSids.Contains(sid.Value)
                || sid.Value is "S-1-3-0" or "S-1-3-4")
            {
                continue;
            }

            if ((rule.FileSystemRights & dangerousRights) != 0)
                return true;
        }

        return false;
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
        var probeOverride = GitVersionProbeOverride.Value;
        if (probeOverride != null)
            return probeOverride(executablePath);

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
        => TryResolveRealUnixPathCore(path, out resolvedPath)
           && HasExpectedGitExecutableName(resolvedPath);

    private static bool TryResolveRealUnixPathCore(string path, out string resolvedPath)
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
            return true;
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
