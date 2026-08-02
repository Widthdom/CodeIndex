using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodeIndex.Diagnostics;

/// <summary>
/// JsonWriterOptions for local append-only JSONL diagnostics files only.
/// </summary>
internal static class LocalJsonlJsonWriterOptions
{
    internal static JsonWriterOptions Create()
    {
        // UnsafeRelaxedJsonEscaping is intentionally limited to private local JSONL
        // files that operators read with tail/grep. Do not reuse these options for
        // HTTP responses, HTML/script embedding, copied snippets, or any externally
        // embedded JSON surface.
        return new JsonWriterOptions
        {
            Indented = false,
            // cdidx-audit: json-trust origin=private_local direction=write sensitivity=diagnostic trust=controlled rationale=operator_only_local_jsonl
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}
