using System.Text.RegularExpressions;

namespace CodeIndex.Tests;

public sealed class InstallScriptSafetyLintTests
{
    [Fact]
    public void InstallShellSafetyLint_RejectsUnreviewedDestructiveOperations_Issue4150()
    {
        var activeLines = ActiveShellLines(ReadRepositoryText("install.sh")).ToArray();
        var activeText = string.Join("\n", activeLines);
        var rmRfLines = activeLines
            .Where(line => line.Contains("rm -rf", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(activeLines, line => IsEvalCommand(line) || line.Contains("$(eval", StringComparison.Ordinal));

        var allowedRmRfLines = new HashSet<string>(StringComparer.Ordinal)
        {
            "rm -rf \"$TMPDIR_CLEANUP\"",
            "rm -rf \"$STAGE_DIR_CLEANUP\"",
            "rm -rf \"$BACKUP_DIR_CLEANUP\"",
            "rm -rf \"$LOCAL_MIRROR_DIR_CLEANUP\"",
            "rm -rf \"$SELF_TEST_INSTALL_DIR_CLEANUP\"",
            "rm -rf \"$REINSTALL_SCRATCH_CLEANUP\"",
            "rm -rf \"$INSTALL_LOCK_DIR_CLEANUP\"",
            "rm -rf \"$backup_dir\"",
            "rm -rf \"$stage_dir\"",
            "if ! rm -rf \"${install_dir}/${asset}\"; then",
            "if rm -rf \"$path\"; then",
        };

        var unexpected = rmRfLines
            .Where(line => !allowedRmRfLines.Contains(line))
            .ToArray();
        Assert.True(
            unexpected.Length == 0,
            "Unexpected destructive installer cleanup lines:" + Environment.NewLine + string.Join(Environment.NewLine, unexpected));
        Assert.Single(rmRfLines.Where(line => line == "if rm -rf \"$path\"; then"));
        Assert.Contains("if rm -rf \"$path\"; then", ExtractShellFunction(activeText, "remove_uninstall_directory"), StringComparison.Ordinal);
    }

    [Fact]
    public void InstallShellSafetyLint_RequiresTrackedCleanupForTempDirectories_Issue4150()
    {
        var activeLines = ActiveShellLines(ReadRepositoryText("install.sh")).ToArray();
        var activeText = string.Join("\n", activeLines);
        var cleanupBody = ExtractShellFunction(activeText, "cleanup");
        var cleanupByLocalVariable = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tmpdir"] = "TMPDIR_CLEANUP",
            ["stage_dir"] = "STAGE_DIR_CLEANUP",
            ["backup_dir"] = "BACKUP_DIR_CLEANUP",
            ["local_mirror_root"] = "LOCAL_MIRROR_DIR_CLEANUP",
            ["self_test_install_dir"] = "SELF_TEST_INSTALL_DIR_CLEANUP",
            ["reinstall_dir"] = "SELF_TEST_INSTALL_DIR_CLEANUP",
            ["scratch_project"] = "REINSTALL_SCRATCH_CLEANUP",
        };

        var mktempDirectoryVariables = Regex.Matches(activeText, "\\b(?<name>[A-Za-z_][A-Za-z0-9_]*)=\\\"\\$\\(mktemp -d(?:[\\s)]|$)")
            .Cast<Match>()
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(cleanupByLocalVariable.Keys.Order(StringComparer.Ordinal).ToArray(), mktempDirectoryVariables);
        Assert.Contains("trap cleanup EXIT", activeText, StringComparison.Ordinal);
        foreach (var (localVariable, cleanupVariable) in cleanupByLocalVariable)
        {
            Assert.Contains(string.Concat(cleanupVariable, "=\"$", localVariable, "\""), activeText, StringComparison.Ordinal);
            Assert.Contains(string.Concat("rm -rf \"$", cleanupVariable, "\""), cleanupBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InstallShellSafetyLint_RoutesDownloadsThroughReviewedCurlWrapper_Issue4150()
    {
        var activeLines = ActiveShellLines(ReadRepositoryText("install.sh")).ToArray();
        var activeText = string.Join("\n", activeLines);
        var rawCurlLines = activeLines
            .Where(line => line.StartsWith("curl ", StringComparison.Ordinal) || line == "curl")
            .ToArray();

        Assert.Equal(
            [
                "curl --noproxy 127.0.0.1,localhost --proto '=http,https' --proto-redir '=https' --max-redirs 0 \\",
                "curl --proto '=https' --proto-redir '=https' \\",
            ],
            rawCurlLines);
        var curlWrapper = ExtractShellFunction(activeText, "run_curl_with_optional_loopback_bypass");
        Assert.Contains("--proto '=http,https' --proto-redir '=https' --max-redirs 0", curlWrapper, StringComparison.Ordinal);
        Assert.Contains("--proto '=https' --proto-redir '=https'", curlWrapper, StringComparison.Ordinal);
        Assert.Contains("--connect-timeout \"$CURL_CONNECT_TIMEOUT_SECONDS\" --max-time \"$CURL_MAX_TIME_SECONDS\"", curlWrapper, StringComparison.Ordinal);
        Assert.Contains("--speed-limit \"$CURL_LOW_SPEED_LIMIT_BYTES\" --speed-time \"$CURL_LOW_SPEED_TIME_SECONDS\"", curlWrapper, StringComparison.Ordinal);
        Assert.Contains("--retry \"$CURL_RETRY_COUNT\" --retry-delay \"$CURL_RETRY_DELAY_SECONDS\"", curlWrapper, StringComparison.Ordinal);
        Assert.Contains("--retry-max-time \"$CURL_MAX_TIME_SECONDS\"", curlWrapper, StringComparison.Ordinal);

        var downloadBody = ExtractShellFunction(activeText, "curl_http_get");
        Assert.Contains("run_curl_with_optional_loopback_bypass \"$url\" -sSL --max-filesize \"$max_bytes\" -o \"$output_path\" -w '%{http_code}' \"$url\" 2>\"$curl_stderr\"", downloadBody, StringComparison.Ordinal);
        Assert.Contains("read_bounded_file_sample \"$curl_stderr\" \"$CURL_STDERR_SAMPLE_BYTES\" \"curl stderr for ${source_label}\"", downloadBody, StringComparison.Ordinal);

        var doctorProbeBody = ExtractShellFunction(activeText, "probe_doctor_url");
        Assert.Contains("run_curl_with_optional_loopback_bypass \"$url\" -sSI -o /dev/null -w '%{http_code}' \"$url\" 2>\"$curl_stderr\"", doctorProbeBody, StringComparison.Ordinal);
        Assert.Contains("read_bounded_file_sample \"$curl_stderr\" \"$CURL_STDERR_SAMPLE_BYTES\" \"curl stderr for ${label}\"", doctorProbeBody, StringComparison.Ordinal);
        Assert.Contains("CURL_STDERR_SAMPLE_BYTES=8192", activeText, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallShellSafetyLint_RequiresPrivateBoundedArchiveExtraction_Issue4605()
    {
        var activeText = string.Join("\n", ActiveShellLines(ReadRepositoryText("install.sh")));
        var validationBody = ExtractShellFunction(activeText, "validate_archive_members");
        var extractionBody = ExtractShellFunction(activeText, "download_and_install");

        Assert.Contains("RELEASE_ARCHIVE_MAX_BYTES=536870912", activeText, StringComparison.Ordinal);
        Assert.Contains("ARCHIVE_MEMBER_MAX_COUNT=4096", activeText, StringComparison.Ordinal);
        Assert.Contains("ARCHIVE_DECLARED_MAX_BYTES=1073741824", activeText, StringComparison.Ordinal);
        Assert.Contains("ARCHIVE_EXPANDED_STREAM_MAX_BYTES=1207959552", activeText, StringComparison.Ordinal);
        Assert.Contains("ARCHIVE_COMPRESSION_RATIO_MAX=250", activeText, StringComparison.Ordinal);
        Assert.Contains("EXTRACTED_PAYLOAD_MAX_BYTES=1073741824", activeText, StringComparison.Ordinal);
        Assert.Contains("[archive_link_rejected]", validationBody, StringComparison.Ordinal);
        Assert.Contains("[archive_member_type_rejected]", validationBody, StringComparison.Ordinal);
        Assert.Contains("chmod 700 \"$tmpdir\"", extractionBody, StringComparison.Ordinal);
        Assert.Contains("chmod 700 \"$extract_dir\"", extractionBody, StringComparison.Ordinal);
        Assert.Contains("download_release_file \"$archive_url\" \"${tmpdir}/${archive_name}\" \"${archive_name}\" \"$RELEASE_ARCHIVE_MAX_BYTES\"", extractionBody, StringComparison.Ordinal);
        Assert.Contains("download_release_file \"$checksums_url\" \"${tmpdir}/sha256sums.txt\" \"sha256sums.txt\" \"$RELEASE_METADATA_MAX_BYTES\"", extractionBody, StringComparison.Ordinal);
        Assert.Contains("tar -xzkf \"${tmpdir}/${archive_name}\" -C \"$extract_dir\" --no-same-owner --no-same-permissions", extractionBody, StringComparison.Ordinal);
        Assert.Contains("validate_extracted_payload_size \"$extract_dir\"", extractionBody, StringComparison.Ordinal);
    }

    private static bool IsEvalCommand(string line)
        => Regex.IsMatch(line, @"(^|[;&|(){}\s])eval($|[;&|(){}\s])");

    private static IEnumerable<string> ActiveShellLines(string script)
        => script.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal));

    private static string ExtractShellFunction(string activeText, string functionName)
    {
        var marker = functionName + "() {";
        var start = activeText.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find shell function {functionName}.");
        var end = activeText.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find end of shell function {functionName}.");
        return activeText[start..end];
    }

    private static string ReadRepositoryText(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Could not locate repository file: {relativePath}");
    }
}
