using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

// Child stdout is untrusted. Never copy arbitrary properties or inspect stderr.
internal static class BatchChildErrorParser
{
    internal const int MaxUtf8Bytes = 64 * 1024;
    internal const int MaxDepth = 16;
    internal const int MaxTextChars = 1024;

    internal static JsonObject? Parse(string stdout, string command, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(stdout) || exitCode == CommandExitCodes.Success)
            return null;

        try
        {
            using var document = BoundedJson.ParseDocument(stdout, MaxUtf8Bytes, MaxDepth);
            var root = document.RootElement;
            if (!IsUniqueObject(root))
                return null;

            var source = root;
            if (root.TryGetProperty("metadata", out var metadata))
            {
                if (!IsUniqueObject(metadata)
                    || !metadata.TryGetProperty("error", out var nestedError)
                    || !IsUniqueObject(nestedError)
                    || !metadata.TryGetProperty("exit_code", out var envelopeExit)
                    || envelopeExit.ValueKind != JsonValueKind.Number
                    || !envelopeExit.TryGetInt32(out var envelopeExitCode)
                    || envelopeExitCode != exitCode
                    || !IdentityMatches(metadata, command, exitCode))
                    return null;
                source = nestedError;
            }
            else if (!root.TryGetProperty("status", out var status)
                     || status.ValueKind != JsonValueKind.String || status.GetString() != "error")
            {
                return null;
            }

            if (!IdentityMatches(source, command, exitCode)
                || !TryGetMachineString(source, "error_code", out var errorCode)
                || !source.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(message.GetString()))
                return null;

            var (_, defaultCategory) = CommandErrorWriter.ResolveMachineContract(exitCode);
            var category = defaultCategory;
            if (source.TryGetProperty("category", out _)
                && !TryGetMachineString(source, "category", out category))
                return null;

            var result = new JsonObject
            {
                ["error_code"] = errorCode,
                ["category"] = category,
                ["message"] = Sanitize(message.GetString()!),
                ["scope"] = "command",
            };
            foreach (var property in source.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "hint":
                    case "minimum_required_bytes_unavailable_reason":
                    case "minimum_required_bytes_uncertainty_reason":
                        if (!CopyString(property, result))
                            return null;
                        break;
                    case "requested_bytes":
                    case "effective_bytes":
                    case "minimum_required_bytes":
                        if (!CopyBytes(property, result))
                            return null;
                        break;
                    case "minimum_required_bytes_known":
                    case "minimum_required_bytes_uncertain":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return null;
                        result[property.Name] = property.Value.GetBoolean();
                        break;
                    case "retry":
                        if (!IsUniqueObject(property.Value))
                            return null;
                        var retry = new JsonObject();
                        foreach (var child in property.Value.EnumerateObject())
                        {
                            if (child.Name is "action" or "option" or "command")
                            {
                                if (!CopyString(child, retry))
                                    return null;
                            }
                            else if (child.Name is "recommended_bytes" or "maximum_effective_bytes")
                            {
                                if (!CopyBytes(child, retry))
                                    return null;
                            }
                        }
                        result["retry"] = retry;
                        break;
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // Malformed or over-budget output retains the exit-code fallback.
            return null;
        }
    }

    private static bool IdentityMatches(JsonElement source, string command, int exitCode)
        => (!source.TryGetProperty("command", out var name)
            || (name.ValueKind == JsonValueKind.String
                && name.GetString() == JsonEnvelopeWrapper.CanonicalizeCommandName(command)))
           && (!source.TryGetProperty("exit_code", out var code)
               || (code.ValueKind == JsonValueKind.Number
                   && code.TryGetInt32(out var value) && value == exitCode));

    private static bool IsUniqueObject(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return source.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static bool TryGetMachineString(JsonElement source, string name, out string value)
    {
        value = string.Empty;
        if (!source.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString()!;
        return value.Length is > 0 and <= 128
               && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')
               && Sanitize(value) == value;
    }

    private static bool CopyString(JsonProperty property, JsonObject target)
    {
        if (property.Value.ValueKind == JsonValueKind.Null)
            target[property.Name] = null;
        else if (property.Value.ValueKind == JsonValueKind.String)
            target[property.Name] = Sanitize(property.Value.GetString()!);
        else
            return false;
        return true;
    }

    private static bool CopyBytes(JsonProperty property, JsonObject target)
    {
        if (property.Value.ValueKind == JsonValueKind.Null)
            target[property.Name] = null;
        else if (property.Value.ValueKind == JsonValueKind.Number
                 && property.Value.TryGetInt64(out var bytes) && bytes >= 0)
            target[property.Name] = bytes;
        else
            return false;
        return true;
    }

    private static string Sanitize(string value)
        => DiagnosticSanitizer.ForMessage(
            new string(value.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray()),
            MaxTextChars - 3);
}
