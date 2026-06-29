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
            ["curl --noproxy 127.0.0.1,localhost \"$@\"", "curl \"$@\""],
            rawCurlLines);
        var curlWrapper = ExtractShellFunction(activeText, "run_curl_with_optional_loopback_bypass");
        Assert.Contains("curl --noproxy 127.0.0.1,localhost \"$@\"", curlWrapper, StringComparison.Ordinal);
        Assert.Contains("curl \"$@\"", curlWrapper, StringComparison.Ordinal);

        var downloadBody = ExtractShellFunction(activeText, "curl_http_get");
        Assert.Contains("run_curl_with_optional_loopback_bypass \"$url\" -sSL -o \"$output_path\" -w '%{http_code}' \"$url\" 2>\"$curl_stderr\"", downloadBody, StringComparison.Ordinal);
        Assert.Contains("read_bounded_file_sample \"$curl_stderr\" \"$CURL_STDERR_SAMPLE_BYTES\" \"curl stderr for ${source_label}\"", downloadBody, StringComparison.Ordinal);

        var doctorProbeBody = ExtractShellFunction(activeText, "probe_doctor_url");
        Assert.Contains("run_curl_with_optional_loopback_bypass \"$url\" -sSI -o /dev/null -w '%{http_code}' \"$url\" 2>\"$curl_stderr\"", doctorProbeBody, StringComparison.Ordinal);
        Assert.Contains("read_bounded_file_sample \"$curl_stderr\" \"$CURL_STDERR_SAMPLE_BYTES\" \"curl stderr for ${label}\"", doctorProbeBody, StringComparison.Ordinal);
        Assert.Contains("CURL_STDERR_SAMPLE_BYTES=8192", activeText, StringComparison.Ordinal);
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
