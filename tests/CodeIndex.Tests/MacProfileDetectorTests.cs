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
    public void DetectFromProcAttrs_SELinuxCurrent_ReturnsContext()
    {
        var profile = MacProfileDetector.DetectFromProcAttrs("user_u:user_r:user_t:s0", null);

        Assert.Equal("selinux:user_u:user_r:user_t:s0", profile);
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
