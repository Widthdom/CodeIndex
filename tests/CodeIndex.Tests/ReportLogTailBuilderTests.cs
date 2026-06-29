using CodeIndex.Cli;

namespace CodeIndex.Tests;

public sealed class ReportLogTailBuilderTests
{
    [Fact]
    public void ReadResultReportsLineCharTruncation_Issue4179()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_report_log_tail_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var path = Path.Combine(workDir, "stderr-20260629.log");
        try
        {
            File.WriteAllText(path, "prefix " + new string('x', ReportCommandRunner.MaxLogTailLineChars * 2));

            var result = ReportLogTailBuilder.ReadLogFileTailLinesResult(path, 1);

            var line = Assert.Single(result.Lines);
            Assert.True(result.LineCharsTruncated);
            Assert.False(result.LinesTruncated);
            Assert.True(line.Length <= ReportCommandRunner.MaxLogTailLineChars);
            Assert.Contains("[line truncated]", line);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }
}
