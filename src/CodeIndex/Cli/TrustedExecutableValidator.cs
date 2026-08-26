using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal sealed record TrustedExecutableValidation(
    string? Path,
    string Source,
    bool Accepted,
    string Reason,
    string? DiagnosticPath,
    bool? OwnerOnlyWritable,
    string? UnixMode,
    bool? Executable,
    string? Owner,
    bool? OwnerTrusted,
    bool? AncestorDirectoriesTrusted);

internal static class TrustedExecutableValidator
{
    private const int UnixExecuteAccess = 1;
    private const int MaxPortableExecutableHeaderOffset = 16 * 1024 * 1024;

    internal static TrustedExecutableValidation Evaluate(
        string path,
        string source,
        string expectedUnixFileName,
        string expectedWindowsFileName,
        Func<string, bool>? executionProbe,
        bool allowMacHomebrewAdminGroupWrite = false)
    {
        var before = EvaluateWithoutExecutionProbe(
            path,
            source,
            expectedUnixFileName,
            expectedWindowsFileName,
            allowMacHomebrewAdminGroupWrite);
        if (!before.Accepted || executionProbe == null)
            return before;

        FileIndexer.FileIdentity? initialIdentity =
            FileIndexer.TryGetFileIdentity(before.Path!, out var observedIdentity)
                ? observedIdentity
                : null;
        if (!executionProbe(before.Path!))
            return RejectFrom(before, "execution_probe_failed", executable: false);

        var after = EvaluateWithoutExecutionProbe(
            before.Path!,
            source,
            expectedUnixFileName,
            expectedWindowsFileName,
            allowMacHomebrewAdminGroupWrite);
        if (!after.Accepted)
            return after;
        if (!string.Equals(
                before.Path,
                after.Path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return RejectFrom(after, "identity_changed", executable: false);
        if (initialIdentity.HasValue
            && (!FileIndexer.TryGetFileIdentity(after.Path!, out var finalIdentity)
                || finalIdentity != initialIdentity.Value))
        {
            return RejectFrom(after, "identity_changed", executable: false);
        }

        return after with { Executable = true };
    }

    private static TrustedExecutableValidation EvaluateWithoutExecutionProbe(
        string path,
        string source,
        string expectedUnixFileName,
        string expectedWindowsFileName,
        bool allowMacHomebrewAdminGroupWrite)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Reject(source, "path_empty", null);
        if (!Path.IsPathFullyQualified(path))
            return Reject(source, "path_not_absolute", null);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return Reject(source, "invalid_path", null);
        }

        var diagnosticPath = DiagnosticSanitizer.ForPath(fullPath);
        if (fullPath.IndexOfAny(['\r', '\n']) >= 0)
            return Reject(source, "path_contains_line_break", diagnosticPath);
        if (!HasExpectedExecutableName(fullPath, expectedUnixFileName, expectedWindowsFileName))
            return Reject(source, "unexpected_filename", diagnosticPath);

        if (!TryValidateRegularEntry(fullPath, source, diagnosticPath, out var entryFailure))
            return entryFailure!;
        if (OperatingSystem.IsWindows()
            && !TryValidateExecutableAncestors(fullPath, effectiveUserId: null))
            return Reject(source, "ancestor_untrusted", diagnosticPath, ancestorDirectoriesTrusted: false);

        if (OperatingSystem.IsWindows())
        {
            if (!TryValidateWindowsExecutableAcl(fullPath, out var windowsOwner, out var windowsOwnerTrusted))
            {
                return Reject(
                    source,
                    "acl_untrusted",
                    diagnosticPath,
                    owner: windowsOwner,
                    ownerTrusted: windowsOwnerTrusted,
                    ancestorDirectoriesTrusted: false);
            }
            if (!TryValidateWindowsExecutableImage(fullPath))
            {
                return Reject(
                    source,
                    "invalid_executable_format",
                    diagnosticPath,
                    executable: false,
                    owner: windowsOwner,
                    ownerTrusted: windowsOwnerTrusted,
                    ancestorDirectoriesTrusted: true);
            }

            return Accept(
                fullPath,
                source,
                diagnosticPath,
                executable: true,
                owner: windowsOwner,
                ownerTrusted: windowsOwnerTrusted,
                ancestorDirectoriesTrusted: true);
        }

        if (!TryGetEffectiveUnixUserId(out var effectiveUserId))
            return Reject(source, "owner_probe_failed", diagnosticPath);
        if (!TryResolveRealUnixPath(fullPath, out var canonicalPath))
            return Reject(source, "canonicalization_failed", diagnosticPath);
        if (!HasExpectedExecutableName(canonicalPath, expectedUnixFileName, expectedWindowsFileName))
            return Reject(source, "unexpected_filename", DiagnosticSanitizer.ForPath(canonicalPath));

        fullPath = canonicalPath;
        diagnosticPath = DiagnosticSanitizer.ForPath(fullPath);
        if (fullPath.IndexOfAny(['\r', '\n']) >= 0)
            return Reject(source, "path_contains_line_break", diagnosticPath);
        if (!TryValidateRegularEntry(fullPath, source, diagnosticPath, out entryFailure))
            return entryFailure!;

        UnixFileMode mode;
        try
        {
            mode = File.GetUnixFileMode(LongPath.EnsureWindowsPrefix(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Reject(source, "mode_probe_failed", diagnosticPath);
        }

        var ownerOnlyWritable = (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        var unixMode = FormatUnixMode(mode);
        if (!ownerOnlyWritable)
        {
            return Reject(
                source,
                "shared_writable",
                diagnosticPath,
                ownerOnlyWritable,
                unixMode);
        }

        if (!FileIndexer.TryGetUnixFileOwnerId(LongPath.EnsureWindowsPrefix(fullPath), out var ownerId))
        {
            return Reject(
                source,
                "owner_probe_failed",
                diagnosticPath,
                ownerOnlyWritable,
                unixMode);
        }

        var owner = ownerId == effectiveUserId ? "current_user" : ownerId == 0 ? "root" : "other";
        var ownerTrusted = ownerId == effectiveUserId || ownerId == 0;
        if (!ownerTrusted)
        {
            return Reject(
                source,
                "owner_untrusted",
                diagnosticPath,
                ownerOnlyWritable,
                unixMode,
                owner: owner,
                ownerTrusted: false);
        }

        var ancestorsTrusted = TryValidateExecutableAncestors(
            fullPath,
            effectiveUserId,
            allowMacHomebrewAdminGroupWrite);
        if (!ancestorsTrusted)
        {
            return Reject(
                source,
                "ancestor_untrusted",
                diagnosticPath,
                ownerOnlyWritable,
                unixMode,
                owner: owner,
                ownerTrusted: true,
                ancestorDirectoriesTrusted: false);
        }

        var executable = TryAccessUnixExecutable(fullPath);
        if (!executable)
        {
            return Reject(
                source,
                "not_executable",
                diagnosticPath,
                ownerOnlyWritable,
                unixMode,
                executable: false,
                owner,
                ownerTrusted: true,
                ancestorDirectoriesTrusted: true);
        }

        return Accept(
            fullPath,
            source,
            diagnosticPath,
            ownerOnlyWritable,
            unixMode,
            executable: true,
            owner,
            ownerTrusted: true,
            ancestorDirectoriesTrusted: true);
    }

    private static bool TryValidateRegularEntry(
        string path,
        string source,
        string diagnosticPath,
        out TrustedExecutableValidation? failure)
    {
        failure = null;
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(LongPath.EnsureWindowsPrefix(path));
        }
        catch (FileNotFoundException)
        {
            failure = Reject(source, "not_found", diagnosticPath);
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            failure = Reject(source, "not_found", diagnosticPath);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            failure = Reject(source, "attribute_probe_failed", diagnosticPath);
            return false;
        }

        if ((attributes & FileAttributes.Directory) != 0)
            failure = Reject(source, "not_regular_file", diagnosticPath);
        else if (FileSystemBoundary.IsSymlinkOrReparsePoint(attributes))
            failure = Reject(source, "symlink_or_reparse_point", diagnosticPath);
        else if (FileSystemBoundary.IsDevice(attributes))
            failure = Reject(source, "device", diagnosticPath);
        return failure == null;
    }

    private static TrustedExecutableValidation Accept(
        string path,
        string source,
        string diagnosticPath,
        bool? ownerOnlyWritable = null,
        string? unixMode = null,
        bool? executable = null,
        string? owner = null,
        bool? ownerTrusted = null,
        bool? ancestorDirectoriesTrusted = null)
        => new(
            path,
            source,
            Accepted: true,
            "accepted",
            diagnosticPath,
            ownerOnlyWritable,
            unixMode,
            executable,
            owner,
            ownerTrusted,
            ancestorDirectoriesTrusted);

    private static TrustedExecutableValidation Reject(
        string source,
        string reason,
        string? diagnosticPath,
        bool? ownerOnlyWritable = null,
        string? unixMode = null,
        bool? executable = null,
        string? owner = null,
        bool? ownerTrusted = null,
        bool? ancestorDirectoriesTrusted = null)
        => new(
            Path: null,
            source,
            Accepted: false,
            reason,
            diagnosticPath,
            ownerOnlyWritable,
            unixMode,
            executable,
            owner,
            ownerTrusted,
            ancestorDirectoriesTrusted);

    private static TrustedExecutableValidation RejectFrom(
        TrustedExecutableValidation status,
        string reason,
        bool? executable)
        => status with
        {
            Path = null,
            Accepted = false,
            Reason = reason,
            Executable = executable,
        };

    private static bool HasExpectedExecutableName(
        string path,
        string expectedUnixFileName,
        string expectedWindowsFileName)
        => string.Equals(
            Path.GetFileName(path),
            OperatingSystem.IsWindows() ? expectedWindowsFileName : expectedUnixFileName,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string FormatUnixMode(UnixFileMode mode)
        => Convert.ToString((int)mode, 8).PadLeft(4, '0');

    private static bool TryValidateExecutableAncestors(
        string executablePath,
        uint? effectiveUserId,
        bool allowMacHomebrewAdminGroupWrite = false)
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
                    var rootOwnedStickyDirectory = ownerId == 0 && (mode & UnixFileMode.StickyBit) != 0;
                    var trustedMacHomebrewCellar = allowMacHomebrewAdminGroupWrite
                        && IsTrustedMacHomebrewCellarAncestor(current, ownerId, userId, mode);
                    if (sharedWritable && !rootOwnedStickyDirectory && !trustedMacHomebrewCellar)
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

    private static bool IsTrustedMacHomebrewCellarAncestor(
        string path,
        uint ownerId,
        uint effectiveUserId,
        UnixFileMode mode)
    {
        const uint macOsAdminGroupId = 80;
        if (!OperatingSystem.IsMacOS()
            || ownerId != effectiveUserId
            || (mode & UnixFileMode.OtherWrite) != 0
            || !FileIndexer.TryGetUnixFileOwnerAndGroupIds(
                LongPath.EnsureWindowsPrefix(path),
                out var observedOwnerId,
                out var groupId)
            || observedOwnerId != ownerId
            || groupId != macOsAdminGroupId)
        {
            return false;
        }

        var comparison = StringComparison.Ordinal;
        return string.Equals(path, "/opt/homebrew/Cellar", comparison)
               || string.Equals(path, "/usr/local/Cellar", comparison);
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
                "S-1-5-18",
                "S-1-5-32-544",
                "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
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

    internal static bool TryResolveRealUnixPath(string path, out string resolvedPath)
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
