using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Diagnostics;

internal static class DiagnosticRedactor
{
    internal const int DefaultDiagnosticValueCharLimit = 120;
    internal const string AngleRedacted = "<redacted>";
    internal const string TruncationMarker = "... <truncated>";

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

    private static readonly Regex UnixAbsolutePathPattern = new(
        @"(?<![A-Za-z0-9+\-.]:)/[^\s""'<>]+(?:/[^\s""'<>]+)*",
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
        redacted = LongBase64Pattern.Replace(redacted, placeholder);
        if (redactPaths)
        {
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

        if (maxChars == 0)
            return TruncationMarker.TrimStart('.', ' ');
        if (maxChars <= TruncationMarker.Length)
            return TruncationMarker[..maxChars];

        return flattened[..(maxChars - TruncationMarker.Length)] + TruncationMarker;
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
