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
            RecoveryCommandShell.PosixSh);

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
        Assert.True(recovery.CommandDisplayOnly);
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
            RecoveryCommandShell.PowerShell);

        Assert.Equal("powershell", recovery.CommandShell);
        Assert.True(recovery.CommandDisplayOnly);
        Assert.Equal(
            """& 'C:\Program Files\dotnet\dotnet.exe' 'C:\repo\cdidx''build.dll' excerpt 'src/space ''quote'' $dollar &meta.py' --db 'file:C:\db path''quote?$x&mode=ro' --start 2 --end 3 --max-line-width 0 --json""",
            recovery.Command);
        Assert.Equal(path, recovery.Argv[3]);
        Assert.Equal(dbPath, recovery.Argv[5]);
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
