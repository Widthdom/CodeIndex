using System.Text;
using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

internal readonly record struct BoundedMcpText(string Text, int OriginalLength, bool Truncated)
{
    internal void AddMetadata(JsonObject target, string prefix)
    {
        if (!Truncated)
            return;

        target[$"{prefix}_length"] = OriginalLength;
        target[$"{prefix}_truncated"] = true;
    }
}

internal static class McpBoundedText
{
    internal const int MaxScalarArgumentChars = 512;
    internal const int MaxDiagnosticDisplayChars = 128;
    internal const int MaxToolNameChars = 128;
    internal const int MaxProtocolVersionChars = 128;
    internal const int MaxClientInfoChars = 128;
    internal const int MaxClientIdentityChars = (MaxClientInfoChars * 2) + 1;
    internal const int MaxPromptNameChars = 128;
    internal const int MaxPromptArgumentChars = 512;
    internal const int MaxResourceUriChars = 4096;
    internal const int MaxProgressTokenStringChars = 256;
    internal const int MaxProgressTokenPropertyNameChars = 64;
    internal const int MaxProgressTokenNodeCount = 32;
    internal const int MaxProgressTokenDepth = 4;
    internal const int MaxProgressTokenJsonBytes = 1024;

    internal static BoundedMcpText ForDisplay(string value, int maxChars = MaxDiagnosticDisplayChars)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);

        var truncated = value.Length > maxChars;
        var displayLength = Math.Min(value.Length, maxChars);
        var sb = new StringBuilder(displayLength + (truncated ? 3 : 0));
        for (var i = 0; i < displayLength; i++)
        {
            var ch = value[i];
            sb.Append(ch < 0x20 || ch == 0x7F ? '?' : ch);
        }
        if (truncated)
            sb.Append("...");

        return new BoundedMcpText(sb.ToString(), value.Length, truncated);
    }
}
