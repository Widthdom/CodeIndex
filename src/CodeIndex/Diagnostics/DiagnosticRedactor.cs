using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Diagnostics;

internal static class DiagnosticRedactor
{
    internal const int DefaultDiagnosticValueCharLimit = 120;
    internal const string AngleRedacted = "<redacted>";
    internal const string SuggestionRedactedAwsAccessKey = "[REDACTED:aws_access_key]";
    internal const string SuggestionRedactedBearerToken = "[REDACTED:bearer_token]";
    internal const string SuggestionRedactedCredential = "[REDACTED:credential]";
    internal const string SuggestionRedactedHighEntropyToken = "[REDACTED:high_entropy_token]";
    internal const string SuggestionRedactedRegexTimeout = RegexTimeoutPolicy.SuggestionTextTimeoutFallback;
    internal const int SuggestionRedactionFieldLengthLimit = 32768;
    internal const string SuggestionRedactionTruncationMarker = "[REDACTED:truncated]";
    internal const int MaxReportLogJsonLineChars = 64 * 1024;
    internal const int MaxReportLogJsonDepth = 32;

    private static readonly TimeSpan RegexTimeout = RegexTimeoutPolicy.RedactionRegexTimeout;
    private static readonly Regex UriUserInfoPattern = new(
        @"(?<scheme>[a-z][a-z0-9+\-.]*://)(?<user>[^:@/\s]+):(?<password>[^@/\s]+)@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex BearerTokenPattern = new(
        @"\bBearer\s+[A-Za-z0-9._~+\-/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SuggestionBearerTokenPattern = new(
        @"\bBearer\s+[A-Za-z0-9._~+/=-]{16,}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?<![\w.-])(?<name>--?[\w.-]*(?:token|password|passwd|pwd|secret|auth|apikey|api-key|api_key|access-key|access_key|credential)[\w.-]*)(?<sep>=|:)(?<value>[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SuggestionNamedSecretPattern = new(
        @"(?i)(^|[^\p{L}\p{N}_-])(?<name>[\p{L}\p{N}_-]*(?:password|passwd|pwd|secret|token|api[-_]?key|access[-_]?key|credential)[\p{L}\p{N}_-]*)=(?<value>[^&\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex SensitiveSeparatedArgumentPattern = new(
        @"(?<![\w.-])(?<name>--?[\w.-]*(?:token|password|passwd|pwd|secret|auth|apikey|api-key|api_key|access-key|access_key|credential)[\w.-]*)\s+(?<value>""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex AwsAccessKeyPattern = new(
        @"\bAKIA[0-9A-Z]{16}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex GitHubTokenPattern = new(
        @"\b(?:ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{20,}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex HighEntropyTokenPattern = new(
        @"\b(?=[A-Za-z0-9._~+/=-]{32,}\b)(?=.*[A-Z])(?=.*[a-z])(?=.*\d)[A-Za-z0-9._~+/=-]+\b",
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

    internal static string RedactSqlLiterals(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return sql;

        var builder = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var ch = sql[i];
            if ((ch == 'x' || ch == 'X') && i + 1 < sql.Length && sql[i + 1] == '\'')
            {
                builder.Append(ch).Append('\'').Append(AngleRedacted).Append('\'');
                i = ConsumeSqlSingleQuotedLiteral(sql, i + 1);
                continue;
            }

            if (ch == '\'')
            {
                builder.Append('\'').Append(AngleRedacted).Append('\'');
                i = ConsumeSqlSingleQuotedLiteral(sql, i);
                continue;
            }

            if (IsSqlNumericLiteralStart(sql, i))
            {
                builder.Append("<number>");
                i = ConsumeSqlNumericLiteral(sql, i);
                continue;
            }

            builder.Append(ch);
            i++;
        }

        return builder.ToString();
    }

    internal static string RedactReportLogLine(string line, bool includeArgs, string placeholder = AngleRedacted)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        if (TryRedactReportJsonLogLine(line, includeArgs, placeholder, out var redactedJsonLine))
            return redactedJsonLine;

        return RedactReportKeyValueLogLine(line, includeArgs, placeholder);
    }

    private static string RedactReportKeyValueLogLine(string line, bool includeArgs, string placeholder)
    {
        var builder = new StringBuilder(line.Length);
        var position = 0;
        while (TryFindReportKey(line, position, out var keyStart, out var keyEnd, out var valueStart))
        {
            builder.Append(RedactSensitiveText(line[position..valueStart], placeholder, redactPaths: true));
            var valueEnd = FindReportValueEnd(line, valueStart);
            var key = line[keyStart..keyEnd];
            var value = line[valueStart..valueEnd];
            builder.Append(RedactReportLogValue(key, value, includeArgs, placeholder));
            position = valueEnd;
        }

        builder.Append(RedactSensitiveText(line[position..], placeholder, redactPaths: true));
        return builder.ToString();
    }

    internal static string RedactSensitiveText(string value, string placeholder = AngleRedacted, bool redactPaths = false)
    {
        return RegexTimeoutPolicy.RedactOrFallback(
            RegexRedactionSurface.DiagnosticText,
            () => RedactSensitiveTextCore(value, placeholder, redactPaths),
            placeholder);
    }

    private static string RedactSensitiveTextCore(string value, string placeholder, bool redactPaths)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var redacted = UriUserInfoPattern.Replace(value, match =>
            match.Groups["scheme"].Value + match.Groups["user"].Value + ":" + placeholder + "@");
        redacted = BearerTokenPattern.Replace(redacted, "Bearer " + placeholder);
        redacted = SensitiveAssignmentPattern.Replace(redacted, match =>
            match.Groups["name"].Value + match.Groups["sep"].Value + placeholder);
        redacted = SensitiveSeparatedArgumentPattern.Replace(redacted, match =>
            match.Groups["name"].Value + " " + placeholder);
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

    internal static string RedactSuggestionText(string text, out IReadOnlyCollection<string> redactedTypes)
    {
        var types = new SortedSet<string>(StringComparer.Ordinal);
        var truncated = false;
        if (text.Length > SuggestionRedactionFieldLengthLimit)
        {
            text = text[..SuggestionRedactionFieldLengthLimit];
            truncated = true;
            types.Add("truncated");
        }

        try
        {
            var redacted = AwsAccessKeyPattern.Replace(text, _ =>
            {
                types.Add("aws_access_key");
                return SuggestionRedactedAwsAccessKey;
            });
            redacted = SuggestionBearerTokenPattern.Replace(redacted, _ =>
            {
                types.Add("bearer_token");
                return SuggestionRedactedBearerToken;
            });
            redacted = SuggestionNamedSecretPattern.Replace(redacted, match =>
            {
                types.Add("credential");
                return $"{match.Groups[1].Value}{match.Groups["name"].Value}={SuggestionRedactedCredential}";
            });
            redacted = HighEntropyTokenPattern.Replace(redacted, match =>
            {
                if (match.Value.StartsWith("[REDACTED:", StringComparison.Ordinal))
                    return match.Value;
                types.Add("high_entropy_token");
                return SuggestionRedactedHighEntropyToken;
            });

            redactedTypes = types;
            return truncated ? redacted + SuggestionRedactionTruncationMarker : redacted;
        }
        catch (RegexMatchTimeoutException)
        {
            types.Add(RegexTimeoutPolicy.RedactionTimeoutType);
            redactedTypes = types;
            return RegexTimeoutPolicy.RedactionFallback(RegexRedactionSurface.SuggestionText);
        }
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

    internal static bool IsSensitiveName(string? name) =>
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

    private static bool TryRedactReportJsonLogLine(
        string line,
        bool includeArgs,
        string placeholder,
        out string redacted)
    {
        redacted = line;
        if (!LooksLikeJsonObject(line))
            return false;
        if (line.Length > MaxReportLogJsonLineChars)
        {
            redacted = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"redaction\":\"json_line_too_large\",\"original_length\":{line.Length},\"max_length\":{MaxReportLogJsonLineChars}}}");
            return true;
        }

        try
        {
            using var document = BoundedJson.ParseDocument(line, MaxReportLogJsonLineChars * 4, MaxReportLogJsonDepth);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStringValue(RedactReportJsonStringProperty(
                            property.Name,
                            property.Value.GetString() ?? string.Empty,
                            includeArgs,
                            placeholder));
                    }
                    else
                    {
                        property.Value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            redacted = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool LooksLikeJsonObject(string line)
    {
        var start = 0;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
            start++;
        if (start >= line.Length || line[start] != '{')
            return false;

        var end = line.Length - 1;
        while (end >= start && char.IsWhiteSpace(line[end]))
            end--;

        return end > start && line[end] == '}';
    }

    private static string RedactReportJsonStringProperty(string key, string value, bool includeArgs, string placeholder)
    {
        if (key.Equals("msg", StringComparison.Ordinal))
        {
            var redactedMessage = RedactReportKeyValueLogLine(value, includeArgs, placeholder);
            return RedactSensitiveText(redactedMessage, placeholder, redactPaths: true);
        }

        return RedactReportLogValue(key, value, includeArgs, placeholder);
    }

    private static string RedactReportLogValue(string key, string value, bool includeArgs, string placeholder)
    {
        if (key.Equals("args", StringComparison.Ordinal))
        {
            return includeArgs
                ? RedactSensitiveText(value, placeholder, redactPaths: true)
                : placeholder;
        }

        if (IsReportPathKey(key) || IsSensitiveName(key))
            return placeholder;

        return RedactSensitiveText(value, placeholder, redactPaths: true);
    }

    private static bool IsReportPathKey(string key) =>
        key is "cwd" or "process_path" or "base_dir" or "db" or "path";

    private static bool TryFindReportKey(
        string line,
        int start,
        out int keyStart,
        out int keyEnd,
        out int valueStart)
    {
        for (var i = start; i < line.Length; i++)
        {
            if (i > 0 && !char.IsWhiteSpace(line[i - 1]))
                continue;
            if (TryReadReportKeyAt(line, i, out keyEnd, out valueStart))
            {
                keyStart = i;
                return true;
            }
        }

        keyStart = -1;
        keyEnd = -1;
        valueStart = -1;
        return false;
    }

    private static bool TryReadReportKeyAt(string line, int index, out int keyEnd, out int valueStart)
    {
        keyEnd = -1;
        valueStart = -1;
        if (index >= line.Length || !IsReportKeyStart(line[index]))
            return false;

        var i = index + 1;
        while (i < line.Length && IsReportKeyChar(line[i]))
            i++;
        if (i >= line.Length || line[i] != '=')
            return false;

        keyEnd = i;
        valueStart = i + 1;
        return true;
    }

    private static int FindReportValueEnd(string line, int valueStart)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var i = valueStart; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (!inSingleQuote && !inDoubleQuote && char.IsWhiteSpace(ch))
            {
                var next = i + 1;
                if (next < line.Length && TryReadReportKeyAt(line, next, out _, out _))
                    return i;
            }
        }

        return line.Length;
    }

    private static bool IsReportKeyStart(char ch) =>
        char.IsLetter(ch) || ch == '_';

    private static bool IsReportKeyChar(char ch) =>
        char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.';

    private static bool LooksLikeOpaqueToken(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsDigit(ch) || ch is '+' or '/' or '=')
                return true;
        }

        return false;
    }

    private static int ConsumeSqlSingleQuotedLiteral(string value, int quoteIndex)
    {
        var i = quoteIndex + 1;
        while (i < value.Length)
        {
            if (value[i] != '\'')
            {
                i++;
                continue;
            }

            if (i + 1 < value.Length && value[i + 1] == '\'')
            {
                i += 2;
                continue;
            }

            return i + 1;
        }

        return value.Length;
    }

    private static bool IsSqlNumericLiteralStart(string value, int index)
    {
        var ch = value[index];
        if (!char.IsDigit(ch) && (ch != '.' || index + 1 >= value.Length || !char.IsDigit(value[index + 1])))
            return false;

        return index == 0 || !IsSqlIdentifierPart(value[index - 1]);
    }

    private static int ConsumeSqlNumericLiteral(string value, int index)
    {
        var i = index;
        if (i + 1 < value.Length && value[i] == '0' && (value[i + 1] == 'x' || value[i + 1] == 'X'))
        {
            i += 2;
            while (i < value.Length && Uri.IsHexDigit(value[i]))
                i++;
            return i;
        }

        while (i < value.Length && char.IsDigit(value[i]))
            i++;
        if (i < value.Length && value[i] == '.')
        {
            i++;
            while (i < value.Length && char.IsDigit(value[i]))
                i++;
        }
        if (i < value.Length && (value[i] == 'e' || value[i] == 'E'))
        {
            var exponent = i + 1;
            if (exponent < value.Length && (value[exponent] == '+' || value[exponent] == '-'))
                exponent++;
            var digitStart = exponent;
            while (exponent < value.Length && char.IsDigit(value[exponent]))
                exponent++;
            if (exponent > digitStart)
                i = exponent;
        }

        return i;
    }

    private static bool IsSqlIdentifierPart(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_' || ch == '$';

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
