using System.Reflection;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public class MacProfileDetectorTests
{
    [Fact]
    public void BuildDatabaseHint_AppArmor_NamesAuditTools()
    {
        var hint = MacProfileDetector.BuildDatabaseHint("apparmor:snap.cdidx.cdidx");

        Assert.Contains("AppArmor", hint);
        Assert.Contains("snap.cdidx.cdidx", hint);
        Assert.Contains("aa-status", hint);
    }

    [Fact]
    public void BuildDatabaseHint_AppArmor_TruncatesOversizedProfile_Issue3095()
    {
        var profileTail = new string('a', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);
        var profile = "apparmor:" + profileTail;

        var hint = MacProfileDetector.BuildDatabaseHint(profile);

        Assert.Contains("AppArmor", hint);
        Assert.Contains("<truncated; original length", hint);
        Assert.DoesNotContain(profile, hint);
        Assert.DoesNotContain(profileTail, hint);
    }

    [Fact]
    public void BuildDatabaseHint_SELinux_NamesAuditTools()
    {
        var hint = MacProfileDetector.BuildDatabaseHint("selinux:user_u:user_r:user_t:s0");

        Assert.Contains("SELinux", hint);
        Assert.Contains("getenforce", hint);
        Assert.Contains("audit2why", hint);
    }

    [Fact]
    public void DetectFromProcAttrs_AppArmorCurrent_ReturnsProfile()
    {
        var profile = MacProfileDetector.DetectFromProcAttrs("snap.cdidx.cdidx (enforce)", null);

        Assert.Equal("apparmor:snap.cdidx.cdidx", profile);
    }

    [Fact]
    public void DetectFromProcAttrs_OverlongAppArmorValueIsCappedBeforeParsing_Issue3095()
    {
        var current = new string('a', MacProfileDetector.MaxProcAttrReadChars + 1) + " (enforce)";

        var profile = MacProfileDetector.DetectFromProcAttrs(current, null);

        Assert.Null(profile);
    }

    [Fact]
    public void DetectFromProcAttrs_SELinuxCurrent_ReturnsContext()
    {
        var profile = MacProfileDetector.DetectFromProcAttrs("user_u:user_r:user_t:s0", null);

        Assert.Equal("selinux:user_u:user_r:user_t:s0", profile);
    }

    [Fact]
    public void ReadProcAttrFile_CapsReadLength_Issue3095()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_proc_attr_{Guid.NewGuid():N}.txt");
        var content = new string('p', MacProfileDetector.MaxProcAttrReadChars + 100);
        try
        {
            File.WriteAllText(path, content);

            var value = MacProfileDetector.ReadProcAttrFileForTesting(path);

            Assert.Equal(MacProfileDetector.MaxProcAttrReadChars, value.Length);
            Assert.Equal(new string('p', MacProfileDetector.MaxProcAttrReadChars), value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(23)]
    public void IsPermissionStyleSqliteError_IncludesPermissionAndOpenFailures(int sqliteErrorCode)
    {
        var ex = CreateSyntheticSqliteError(sqliteErrorCode, "permission denied");

        Assert.True(MacProfileDetector.IsPermissionStyleSqliteError(ex));
    }

    [Fact]
    public void StatusResult_JsonIncludesMacProfileWhenDetected()
    {
        var status = new StatusResult { MacProfile = "apparmor:snap.cdidx.cdidx" };

        var json = JsonSerializer.Serialize(status);

        Assert.Contains("\"mac_profile\":\"apparmor:snap.cdidx.cdidx\"", json);
    }

    private static SqliteException CreateSyntheticSqliteError(int errorCode, string message)
    {
        var exception = Activator.CreateInstance(
            typeof(SqliteException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [message, errorCode],
            culture: null) as SqliteException;

        return exception ?? throw new InvalidOperationException("Failed to create synthetic SqliteException.");
    }
}
