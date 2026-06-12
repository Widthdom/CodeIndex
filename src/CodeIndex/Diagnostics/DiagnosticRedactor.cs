using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Diagnostics;

internal static class DiagnosticRedactor
{
    internal const int DefaultDiagnosticValueCharLimit = 120;
    internal const string AngleRedacted = "<redacted>";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex UriUserInfoPattern = new(
        @"(?<scheme>[a-z][a-z0-9+\-.]*://)(?<user>[^:@/\s]+):(?<password>[^@/\s]+)@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex BearerTokenPattern = new(
        @"\bBearer\s+[A-Za-z0-9._~+\-/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?<![\w.-])(?<name>--?[\w.-]*(?:token|password|passwd|pwd|secret|auth|apikey|api-key|api_key|access-key|access_key|credential)[\w.-]*)(?<sep>=|:)(?<value>[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex GitHubTokenPattern = new(
        @"\b(?:ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{20,}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex LongHexPattern = new(
        @"\b[0-9a-f]{32,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex LongBase64Pattern = new(
        @"\b[A-Za-z0-9+/]{40,}={0,2}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex WindowsAbsolutePathPattern = new(
        @"\b[A-Za-z]:[\\/][^\s""'<>|]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex UriPathPattern = new(
        @"\b(?<prefix>[a-z][a-z0-9+\-.]*://[^/\s""'<>?#]*)(?<tail>[/?#][^\s""'<>]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex UnixAbsolutePathPattern = new(
        @"(?<![A-Za-z0-9+\-.]:)(?<!/)/[^\s""'<>]+(?:/[^\s""'<>]+)*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    internal static string ClassifyException(Exception ex)
    {
        return ex switch
        {
            UnauthorizedAccessException => "access_denied",
            PathTooLongException => "path_too_long",
            DirectoryNotFoundException => "directory_not_found",
            FileNotFoundException => "file_not_found",
            IOException => "io_error",
            DecoderFallbackException => "decoder_error",
            OperationCanceledException => "operation_canceled",
            TimeoutException => "timeout",
            SqliteException => "sqlite_error",
            ArgumentException => "argument_error",
            InvalidOperationException => "invalid_operation",
            NotSupportedException => "not_supported",
            _ => "exception_message_redacted",
        };
    }

    internal static string FormatExceptionStackLine(string line, int maxChars = 512) =>
        BoundDiagnosticText(RedactSensitiveText(line, AngleRedacted, redactPaths: true), maxChars);

    internal static string FormatEnvironmentValue(string? raw, int maxChars = DefaultDiagnosticValueCharLimit) =>
        FormatEnvironmentValue(envVarName: null, raw, maxChars);

    internal static string FormatEnvironmentValue(string? envVarName, string? raw, int maxChars = DefaultDiagnosticValueCharLimit)
    {
        if (raw is null)
            return "<null>";
        if (raw.Length == 0)
            return "<empty>";
        if (IsSensitiveName(envVarName))
            return AngleRedacted;

        return BoundDiagnosticText(RedactSensitiveText(raw, AngleRedacted, redactPaths: true), maxChars);
    }

    internal static string RedactSensitiveText(string value, string placeholder = AngleRedacted, bool redactPaths = false)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var redacted = UriUserInfoPattern.Replace(value, match =>
            match.Groups["scheme"].Value + match.Groups["user"].Value + ":" + placeholder + "@");
        redacted = BearerTokenPattern.Replace(redacted, "Bearer " + placeholder);
        redacted = SensitiveAssignmentPattern.Replace(redacted, match =>
            match.Groups["name"].Value + match.Groups["sep"].Value + placeholder);
        redacted = GitHubTokenPattern.Replace(redacted, placeholder);
        redacted = LongHexPattern.Replace(redacted, placeholder);
        redacted = LongBase64Pattern.Replace(redacted, match =>
            LooksLikeOpaqueToken(match.Value) ? placeholder : match.Value);
        if (redactPaths)
        {
            redacted = UriPathPattern.Replace(redacted, match => match.Groups["prefix"].Value + placeholder);
            redacted = WindowsAbsolutePathPattern.Replace(redacted, placeholder);
            redacted = UnixAbsolutePathPattern.Replace(redacted, placeholder);
        }

        return redacted;
    }

    internal static string BoundDiagnosticText(string? value, int maxChars = DefaultDiagnosticValueCharLimit)
    {
        if (maxChars < 0)
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "Diagnostic limit must be non-negative.");

        if (value is null)
            return "<null>";

        var flattened = FlattenDiagnosticControlChars(value);
        if (flattened.Length <= maxChars)
            return flattened;

        var marker = string.Create(CultureInfo.InvariantCulture, $"... <truncated; original length {flattened.Length} chars>");
        return maxChars == 0
            ? marker.TrimStart('.', ' ')
            : flattened[..maxChars] + marker;
    }

    private static bool IsSensitiveName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("passwd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("pwd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("access-key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("access_key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("credential", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeOpaqueToken(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsDigit(ch) || ch is '+' or '/' or '=')
                return true;
        }

        return false;
    }

    private static string FlattenDiagnosticControlChars(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '\r' || ch == '\n' || ch == '\t')
            {
                builder.Append(' ');
            }
            else if (char.IsControl(ch))
            {
                builder.Append(string.Create(CultureInfo.InvariantCulture, $"\\u{(int)ch:x4}"));
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
