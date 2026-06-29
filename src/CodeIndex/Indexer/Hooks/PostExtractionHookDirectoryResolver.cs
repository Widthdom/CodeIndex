using CodeIndex.Diagnostics;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Indexer.Hooks;

internal sealed record PostExtractionHookDirectoryResolution(
    string? Directory,
    IReadOnlyList<PostExtractionHookDiagnostic> Diagnostics,
    IReadOnlyList<ExtensionTrustOverride> TrustOverrides);

internal static class PostExtractionHookDirectoryResolver
{
    internal static PostExtractionHookDirectoryResolution ResolveDefault(bool includeAcceptedOverrideDiagnostic)
    {
        var overridePath = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return ResolveOverride(overridePath, includeAcceptedOverrideDiagnostic);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new PostExtractionHookDirectoryResolution(
            string.IsNullOrWhiteSpace(home)
                ? null
                : Path.Combine(home, ".config", "cdidx", "hooks"),
            [],
            []);
    }

    private static PostExtractionHookDirectoryResolution ResolveOverride(
        string overridePath,
        bool includeAcceptedOverrideDiagnostic)
    {
        var diagnostics = new List<PostExtractionHookDiagnostic>();
        var trustOverrides = new List<ExtensionTrustOverride>();
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(overridePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            diagnostics.Add(PostExtractionHookDiagnosticFactory.Create(
                overridePath,
                null,
                "Hook directory override rejected: path could not be resolved.",
                category: "hook_directory_override_invalid_path"));
            return new PostExtractionHookDirectoryResolution(null, diagnostics, []);
        }

        try
        {
            var directoryInfo = new DirectoryInfo(fullPath);
            if (!directoryInfo.Exists)
            {
                diagnostics.Add(PostExtractionHookDiagnosticFactory.Create(
                    fullPath,
                    null,
                    "Hook directory override rejected: directory does not exist.",
                    category: "hook_directory_override_missing"));
                return new PostExtractionHookDirectoryResolution(null, diagnostics, []);
            }

            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0
                || !string.IsNullOrEmpty(directoryInfo.LinkTarget))
            {
                diagnostics.Add(PostExtractionHookDiagnosticFactory.Create(
                    fullPath,
                    null,
                    "Hook directory override rejected: symbolic links and reparse points are not supported.",
                    category: "hook_directory_override_rejected"));
                return new PostExtractionHookDirectoryResolution(null, diagnostics, []);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostics.Add(PostExtractionHookDiagnosticFactory.Create(
                fullPath,
                null,
                "Hook directory override rejected: directory could not be inspected.",
                category: "hook_directory_override_inspection_failed"));
            return new PostExtractionHookDirectoryResolution(null, diagnostics, []);
        }

        AddUnixPermissionDiagnostic(fullPath, diagnostics);
        if (includeAcceptedOverrideDiagnostic)
        {
            diagnostics.Add(PostExtractionHookDiagnosticFactory.Create(
                fullPath,
                null,
                "Hook directory override accepted: hook assemblies execute local extension code from this trusted directory.",
                category: "hook_directory_override_accepted"));
            trustOverrides.Add(new ExtensionTrustOverride(
                "hook_directory_override",
                PostExtractionHookRunner.HooksDirectoryEnvironmentVariable,
                DiagnosticSanitizer.ForPath(overridePath),
                DiagnosticSanitizer.ForPath(fullPath),
                "Hook directory override accepted by environment; hook assemblies execute local extension code from this trusted directory."));
        }

        return new PostExtractionHookDirectoryResolution(fullPath, diagnostics, trustOverrides);
    }

    private static void AddUnixPermissionDiagnostic(string fullPath, List<PostExtractionHookDiagnostic> diagnostics)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var mode = File.GetUnixFileMode(fullPath);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                diagnostics.Add(PostExtractionHookDiagnosticFactory.Create(
                    fullPath,
                    null,
                    "Hook directory override warning: directory is group- or world-writable; only trusted users should be able to modify hook assemblies.",
                    category: "hook_directory_override_unsafe_permissions"));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostics.Add(PostExtractionHookDiagnosticFactory.Create(
                fullPath,
                null,
                "Hook directory override warning: directory permissions could not be inspected.",
                category: "hook_directory_override_permission_inspection_failed"));
        }
    }
}
