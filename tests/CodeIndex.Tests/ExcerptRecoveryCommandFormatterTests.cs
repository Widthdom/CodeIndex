using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public class ExcerptRecoveryCommandFormatterTests
{
    [Fact]
    public void ApplyDbPath_PosixSh_ReturnsArgvAndEscapesDisplayMetacharacters_Issue4567()
    {
        const string path = "src/space 'quote' $dollar &meta.py";
        const string dbPath = "file:/tmp/db path'quote?$x&mode=ro";
        var recovery = FileExcerptResult.CreateRecoveryHint(path, 2, 3);

        ExcerptRecoveryCommandFormatter.ApplyDbPath(
            recovery,
            path,
            dbPath,
            ["/opt/dot net/dotnet", "/repo/cdidx'build.dll"],
            RecoveryCommandShell.PosixSh,
            redactPaths: false);

        Assert.Equal(
            [
                "/opt/dot net/dotnet",
                "/repo/cdidx'build.dll",
                "excerpt",
                path,
                "--db",
                dbPath,
                "--start",
                "2",
                "--end",
                "3",
                "--max-line-width",
                "0",
                "--json",
            ],
            recovery.Argv);
        Assert.Equal("posix-sh", recovery.CommandShell);
        Assert.False(recovery.CommandDisplayOnly);
        Assert.False(recovery.PathsRedacted);
        Assert.False(recovery.RequiresLocalPathSubstitution);
        Assert.Equal(
            """'/opt/dot net/dotnet' '/repo/cdidx'\''build.dll' excerpt 'src/space '\''quote'\'' $dollar &meta.py' --db 'file:/tmp/db path'\''quote?$x&mode=ro' --start 2 --end 3 --max-line-width 0 --json""",
            recovery.Command);
    }

    [Fact]
    public void ApplyDbPath_PowerShell_ReturnsArgvAndEscapesDisplayMetacharacters_Issue4567()
    {
        const string path = "src/space 'quote' $dollar &meta.py";
        const string dbPath = "file:C:\\db path'quote?$x&mode=ro";
        var recovery = FileExcerptResult.CreateRecoveryHint(path, 2, 3);

        ExcerptRecoveryCommandFormatter.ApplyDbPath(
            recovery,
            path,
            dbPath,
            [@"C:\Program Files\dotnet\dotnet.exe", @"C:\repo\cdidx'build.dll"],
            RecoveryCommandShell.PowerShell,
            redactPaths: false);

        Assert.Equal("powershell", recovery.CommandShell);
        Assert.False(recovery.CommandDisplayOnly);
        Assert.False(recovery.PathsRedacted);
        Assert.False(recovery.RequiresLocalPathSubstitution);
        Assert.Equal(
            """& 'C:\Program Files\dotnet\dotnet.exe' 'C:\repo\cdidx''build.dll' excerpt 'src/space ''quote'' $dollar &meta.py' --db 'file:C:\db path''quote?$x&mode=ro' --start 2 --end 3 --max-line-width 0 --json""",
            recovery.Command);
        Assert.Equal(path, recovery.Argv[3]);
        Assert.Equal(dbPath, recovery.Argv[5]);
    }

    [Theory]
    [InlineData(
        "/Users/alice/My Repo/src/space file.cs",
        "file:/Users/alice/My Repo/.cdidx/codeindex.db?mode=ro",
        "/Users/alice/.dotnet/dotnet",
        "/Users/alice/My Repo/cdidx.dll",
        "dotnet",
        "space file.cs",
        "file:.cdidx/codeindex.db?mode=ro")]
    [InlineData(
        @"C:\Users\alice\My Repo\src\space file.cs",
        @"file:C:\Users\alice\My Repo\.cdidx\codeindex.db?mode=ro",
        @"C:\Users\alice\Program Files\dotnet.exe",
        @"\\build-server\alice\share\cdidx.dll",
        "dotnet.exe",
        "space file.cs",
        "file:.cdidx/codeindex.db?mode=ro")]
    [InlineData(
        @"\\workstation\alice\repo\src\space file.cs",
        @"file:\\workstation\alice\repo\.cdidx\codeindex.db?mode=ro",
        @"\\workstation\alice\tools\cdidx.exe",
        @"C:\Users\alice\repo\cdidx.dll",
        "cdidx.exe",
        "space file.cs",
        "file:.cdidx/codeindex.db?mode=ro")]
    public void ApplyDbPath_DefaultRedaction_RemovesMachineSpecificAbsolutePaths_Issue4860(
        string path,
        string dbPath,
        string executablePath,
        string assemblyPath,
        string expectedExecutable,
        string expectedSourcePath,
        string expectedDbPath)
    {
        var recovery = FileExcerptResult.CreateRecoveryHint(path, 2, 3);

        ExcerptRecoveryCommandFormatter.ApplyDbPath(
            recovery,
            path,
            dbPath,
            [executablePath, assemblyPath],
            RecoveryCommandShell.PowerShell);

        Assert.Equal([expectedExecutable, "cdidx.dll"], recovery.Argv.Take(2));
        Assert.Equal(expectedSourcePath, recovery.Argv[3]);
        Assert.Equal(expectedDbPath, recovery.Argv[5]);
        Assert.True(recovery.CommandDisplayOnly);
        Assert.True(recovery.PathsRedacted);
        Assert.True(recovery.RequiresLocalPathSubstitution);
        Assert.DoesNotContain("alice", recovery.Command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", recovery.Command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workstation", recovery.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyDbPath_DefaultRedaction_DoesNotTreatOptionLikeSourceAsDbFlag_Issue4860()
    {
        const string path = "--db";
        const string dbPath = "/tmp/private-workspace/codeindex.db";
        var recovery = FileExcerptResult.CreateRecoveryHint(path, 2, 3);

        ExcerptRecoveryCommandFormatter.ApplyDbPath(
            recovery,
            path,
            dbPath,
            ["/opt/cdidx"],
            RecoveryCommandShell.PosixSh);

        Assert.Equal(
            ["cdidx", "excerpt", "--", "--db", "--db", "codeindex.db"],
            recovery.Argv.Take(6));
        Assert.DoesNotContain("/tmp/private-workspace", recovery.Command, StringComparison.Ordinal);
        Assert.True(recovery.PathsRedacted);
        Assert.True(recovery.RequiresLocalPathSubstitution);
    }

    [Fact]
    public void ApplyDbPath_DefaultRedaction_SanitizesFileUriQueryPathsAndSecrets_Issue4860()
    {
        const string dbPath =
            "file:/tmp/private-workspace/codeindex.db?mode=ro&aux=/Users/alice/private-cache&aux2=/Users/alice/cache-token=visible4860&%74oken=encoded4860&encoded=%2FUsers%2Falice%2Fsecret";
        var recovery = FileExcerptResult.CreateRecoveryHint("src/app.cs", 2, 3);

        ExcerptRecoveryCommandFormatter.ApplyDbPath(
            recovery,
            "src/app.cs",
            dbPath,
            ["/opt/cdidx"],
            RecoveryCommandShell.PosixSh);

        Assert.Contains(
            "file:codeindex.db?mode=ro&aux=private-cache&aux2=cache-token%3D<redacted>&token=<redacted>&encoded=secret",
            recovery.Argv);
        Assert.DoesNotContain("private-workspace", recovery.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", recovery.Command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", recovery.Command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("visible4860", recovery.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("encoded4860", recovery.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveInvocationPrefix_PreservesDotnetAssemblyOrNativeApphost_Issue4567()
    {
        var assemblyPath = Path.Combine("relative path", "cdidx.dll");

        Assert.Equal(
            ["/usr/local/bin/dotnet", Path.GetFullPath(assemblyPath)],
            ExcerptRecoveryCommandFormatter.ResolveInvocationPrefix("/usr/local/bin/dotnet", assemblyPath));
        Assert.Equal(
            [@"C:\tools\dotnet.exe", Path.GetFullPath(assemblyPath)],
            ExcerptRecoveryCommandFormatter.ResolveInvocationPrefix(@"C:\tools\dotnet.exe", assemblyPath));
        Assert.Equal(
            ["/opt/cdidx/cdidx"],
            ExcerptRecoveryCommandFormatter.ResolveInvocationPrefix("/opt/cdidx/cdidx", assemblyPath));
        Assert.Equal(
            ["cdidx"],
            ExcerptRecoveryCommandFormatter.ResolveInvocationPrefix(null, assemblyPath));
    }
}
