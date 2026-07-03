using System.Text;
using System.Text.Json;
using CodeIndex.Diagnostics;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    internal const int DockerfileJsonFormMaxDepth = 8;
    internal const int DockerfileJsonFormMaxItems = 128;
    internal const int DockerfileJsonFormMaxBodyLength = 32 * 1024;
    internal const int DockerfileJsonFormMaxUtf8Bytes = DockerfileJsonFormMaxBodyLength * 4;
    internal const int DockerfileJsonFormMaxStringLength = 4096;

    internal static JsonDocument ParseDockerfileJsonFormPayload(string payload)
        => BoundedJson.ParseDocument(payload, DockerfileJsonFormMaxUtf8Bytes, DockerfileJsonFormMaxDepth);

    private static void AddDockerfileAdditionalSymbols(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols,
        HashSet<string> stageNames)
    {
        var instruction = GetDockerfileInstructionName(line);
        if (instruction.IsEmpty)
            return;

        if (instruction.Equals("ENV", StringComparison.OrdinalIgnoreCase))
            AddDockerfileAdditionalEnvSymbols(fileId, line, lineNumber, symbols);
        else if (instruction.Equals("LABEL", StringComparison.OrdinalIgnoreCase))
            AddDockerfileAdditionalLabelSymbols(fileId, line, lineNumber, symbols);
        else if (instruction.Equals("EXPOSE", StringComparison.OrdinalIgnoreCase))
            AddDockerfileAdditionalExposeSymbols(fileId, line, lineNumber, symbols);
        else if (instruction.Equals("VOLUME", StringComparison.OrdinalIgnoreCase))
            AddDockerfileAdditionalVolumeSymbols(fileId, line, lineNumber, symbols);
        else if (instruction.Equals("FROM", StringComparison.OrdinalIgnoreCase))
            AddDockerfileNamedStageBaseImageSymbol(fileId, line, lineNumber, symbols, stageNames);
        else if (instruction.Equals("SHELL", StringComparison.OrdinalIgnoreCase))
            AddDockerfileShellSymbol(fileId, line, lineNumber, symbols);
        else if (instruction.Equals("COPY", StringComparison.OrdinalIgnoreCase))
            AddDockerfileCopyDestinationSymbol(fileId, line, lineNumber, symbols);
        else if (instruction.Equals("ADD", StringComparison.OrdinalIgnoreCase))
            AddDockerfileAddDestinationSymbol(fileId, line, lineNumber, symbols);
        else if (instruction.Equals("RUN", StringComparison.OrdinalIgnoreCase))
            AddDockerfileRunSymbol(fileId, line, lineNumber, symbols);
    }

    private static ReadOnlySpan<char> GetDockerfileInstructionName(string line)
    {
        var instructionStart = GetDockerfileFirstNonWhitespaceIndex(line, 0);
        if (instructionStart >= line.Length)
            return ReadOnlySpan<char>.Empty;

        var instructionEnd = instructionStart;
        while (instructionEnd < line.Length && !char.IsWhiteSpace(line[instructionEnd]))
            instructionEnd++;

        var instruction = line.AsSpan(instructionStart, instructionEnd - instructionStart);
        if (!instruction.Equals("ONBUILD", StringComparison.OrdinalIgnoreCase))
            return instruction;

        var nestedInstructionStart = GetDockerfileFirstNonWhitespaceIndex(line, instructionEnd);
        if (nestedInstructionStart >= line.Length)
            return ReadOnlySpan<char>.Empty;

        var nestedInstructionEnd = nestedInstructionStart;
        while (nestedInstructionEnd < line.Length && !char.IsWhiteSpace(line[nestedInstructionEnd]))
            nestedInstructionEnd++;

        return line.AsSpan(nestedInstructionStart, nestedInstructionEnd - nestedInstructionStart);
    }

    private static void AddDockerfileAdditionalEnvSymbols(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols)
    {
        var instructionStart = GetDockerfileFirstNonWhitespaceIndex(line, 0);
        if (line.Length - instructionStart <= 3
            || !line.AsSpan(instructionStart, "ENV".Length).Equals("ENV", StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(line[instructionStart + "ENV".Length]))
        {
            return;
        }

        var first = true;
        var bodyStart = GetDockerfileFirstNonWhitespaceIndex(line, instructionStart + "ENV".Length);
        foreach (var name in EnumerateDockerfileKeyValueNames(line, bodyStart, IsDockerfileVariableName))
        {
            if (first)
            {
                first = false;
                continue;
            }

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "environment",
                    Name = name,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    EndLine = lineNumber,
                    Signature = GetDockerfileTrimmedLine(line),
                },
                line);
        }
    }

    private static void AddDockerfileAdditionalLabelSymbols(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols)
    {
        var instructionStart = GetDockerfileFirstNonWhitespaceIndex(line, 0);
        if (line.Length - instructionStart <= 5
            || !line.AsSpan(instructionStart, "LABEL".Length).Equals("LABEL", StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(line[instructionStart + "LABEL".Length]))
        {
            return;
        }

        var first = true;
        var bodyStart = GetDockerfileFirstNonWhitespaceIndex(line, instructionStart + "LABEL".Length);
        foreach (var name in EnumerateDockerfileKeyValueNames(line, bodyStart, IsDockerfileLabelName))
        {
            if (first)
            {
                first = false;
                continue;
            }

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "label",
                    Name = name,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    EndLine = lineNumber,
                    Signature = GetDockerfileTrimmedLine(line),
                },
                line);
        }
    }

    private static int GetDockerfileFirstNonWhitespaceIndex(string text, int startIndex)
    {
        while (startIndex < text.Length && char.IsWhiteSpace(text[startIndex]))
            startIndex++;

        return startIndex;
    }

    private static string GetDockerfileTrimmedLine(string line)
    {
        var startIndex = GetDockerfileFirstNonWhitespaceIndex(line, 0);
        var endIndex = line.Length;
        while (endIndex > startIndex && char.IsWhiteSpace(line[endIndex - 1]))
            endIndex--;

        return startIndex == 0 && endIndex == line.Length ? line : line[startIndex..endIndex];
    }

    private static IEnumerable<string> EnumerateDockerfileKeyValueNames(string body, Func<string, bool> isName)
        => EnumerateDockerfileKeyValueNames(body, 0, isName);

    private static IEnumerable<string> EnumerateDockerfileKeyValueNames(string body, int startIndex, Func<string, bool> isName)
    {
        var index = startIndex;
        while (index < body.Length)
        {
            while (index < body.Length && char.IsWhiteSpace(body[index]))
                index++;
            if (index >= body.Length)
                yield break;

            var tokenStart = index;
            var inSingleQuote = false;
            var inDoubleQuote = false;
            while (index < body.Length)
            {
                var ch = body[index];
                if (ch == '\\' && index + 1 < body.Length)
                {
                    index += 2;
                    continue;
                }

                if (ch == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                    index++;
                    continue;
                }

                if (ch == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    index++;
                    continue;
                }

                if (!inSingleQuote && !inDoubleQuote && char.IsWhiteSpace(ch))
                    break;

                index++;
            }

            var equalsIndex = body.IndexOf('=', tokenStart, index - tokenStart);
            if (equalsIndex > tokenStart)
            {
                var name = body[tokenStart..equalsIndex];
                if (isName(name))
                    yield return name;
            }
        }
    }

    private static bool IsDockerfileVariableName(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
            return false;

        for (var i = 1; i < name.Length; i++)
        {
            var ch = name[i];
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_'))
                return false;
        }

        return true;
    }

    private static bool IsDockerfileLabelName(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetterOrDigit(name[0]) || name[0] == '_'))
            return false;

        for (var i = 1; i < name.Length; i++)
        {
            var ch = name[i];
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or '-'))
                return false;
        }

        return true;
    }

    private static void AddDockerfileAdditionalExposeSymbols(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.Length <= 6
            || !trimmed.StartsWith("EXPOSE", StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(trimmed[6]))
        {
            return;
        }

        var trimmedText = trimmed.Length == line.Length ? line : trimmed.ToString();
        var first = true;
        foreach (var token in EnumerateDockerfileWhitespaceTokens(trimmedText, 6))
        {
            if (first)
            {
                first = false;
                continue;
            }

            if (!IsDockerfileExposePort(token))
                continue;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "expose",
                    Name = token,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                },
                line);
        }
    }

    private static bool IsDockerfileExposePort(string token)
    {
        var slash = token.IndexOf('/');
        var port = slash >= 0 ? token[..slash] : token;
        if (port.Length == 0)
            return false;
        foreach (var ch in port)
        {
            if (!char.IsAsciiDigit(ch))
                return false;
        }

        if (slash < 0)
            return true;

        var protocol = token[(slash + 1)..];
        return protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase)
            || protocol.Equals("udp", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddDockerfileAdditionalVolumeSymbols(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.Length <= 6
            || !trimmed.StartsWith("VOLUME", StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(trimmed[6]))
        {
            return;
        }

        var body = trimmed[6..].TrimStart();
        if (body.StartsWith("[", StringComparison.Ordinal))
        {
            AddDockerfileJsonVolumeSymbols(fileId, line, lineNumber, symbols, body.ToString());
            return;
        }

        var trimmedText = trimmed.Length == line.Length ? line : trimmed.ToString();
        var first = true;
        foreach (var token in EnumerateDockerfileWhitespaceTokens(trimmedText, 6))
        {
            if (first)
            {
                first = false;
                continue;
            }

            if (token[0] == '#')
                break;

            if (!IsDockerfileVolumePath(token))
                continue;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "volume",
                    Name = token,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                },
                line);
        }
    }

    private static IEnumerable<string> EnumerateDockerfileWhitespaceTokens(string text, int startIndex)
    {
        var tokenStart = -1;
        for (var index = startIndex; index <= text.Length; index++)
        {
            var atEnd = index == text.Length;
            if (!atEnd && !char.IsWhiteSpace(text[index]))
            {
                if (tokenStart < 0)
                    tokenStart = index;
                continue;
            }

            if (tokenStart < 0)
                continue;

            yield return text[tokenStart..index];
            tokenStart = -1;
        }
    }

    private static bool IsDockerfileVolumePath(string token)
        => token.Length > 0
           && token[0] != '#'
           && token[0] != '[';

    private static void AddDockerfileJsonVolumeSymbols(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols,
        string body)
    {
        if (body.Length > DockerfileJsonFormMaxBodyLength)
        {
            AddDockerfileJsonFormDiagnostic(symbols, fileId, lineNumber, line, "dockerfile_json_form_body_budget_exceeded", "Dockerfile JSON-form body exceeded the parse budget; JSON-form symbols were truncated.");
            return;
        }

        try
        {
            using var document = ParseDockerfileJsonFormPayload(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return;

            var itemCount = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (itemCount >= DockerfileJsonFormMaxItems)
                {
                    AddDockerfileJsonFormDiagnostic(symbols, fileId, lineNumber, line, "dockerfile_json_form_array_budget_exceeded", "Dockerfile JSON-form array exceeded the item budget; remaining items were truncated.");
                    break;
                }
                itemCount++;

                if (!TryGetDockerfileJsonFormString(item, out var name))
                    continue;

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    lineNumber,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "volume",
                        Name = name,
                        Line = lineNumber,
                        StartLine = lineNumber,
                        EndLine = lineNumber,
                        Signature = line.Trim(),
                    },
                    line);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
        }
    }

    private static bool TryGetDockerfileJsonFormString(JsonElement item, out string value)
    {
        value = string.Empty;
        if (item.ValueKind != JsonValueKind.String)
            return false;

        var text = item.GetString();
        if (string.IsNullOrWhiteSpace(text)
            || text.Length > DockerfileJsonFormMaxStringLength)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static void AddDockerfileNamedStageBaseImageSymbol(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols,
        HashSet<string> stageNames)
    {
        if (line.IndexOf("as", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        var match = DockerfileNamedFromImageRegex.Match(line);
        if (!match.Success)
            return;

        var name = match.Groups["name"].Value;
        if (stageNames.Contains(name))
            return;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "base_image",
                Name = name,
                Line = lineNumber,
                StartLine = lineNumber,
                EndLine = lineNumber,
                Signature = line.Trim(),
            },
            line);
    }

    private static void AddDockerfileShellSymbol(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.Length <= 5
            || !trimmed.StartsWith("SHELL", StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(trimmed[5]))
        {
            return;
        }

        var body = trimmed[5..].TrimStart();
        if (!body.StartsWith("[", StringComparison.Ordinal))
            return;
        if (body.Length > DockerfileJsonFormMaxBodyLength)
        {
            AddDockerfileJsonFormDiagnostic(symbols, fileId, lineNumber, line, "dockerfile_json_form_body_budget_exceeded", "Dockerfile JSON-form body exceeded the parse budget; JSON-form symbols were truncated.");
            return;
        }

        try
        {
            using var document = ParseDockerfileJsonFormPayload(body.ToString());
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return;

            var first = document.RootElement.EnumerateArray().FirstOrDefault();
            if (!TryGetDockerfileJsonFormString(first, out var name))
                return;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "shell",
                    Name = name,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                },
                line);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
        }
    }

    private static void AddDockerfileCopyDestinationSymbol(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols)
        => AddDockerfileInstructionDestinationSymbol(fileId, line, lineNumber, symbols, "COPY", includeJsonForm: true, allowOnbuild: true);

    private static void AddDockerfileAddDestinationSymbol(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols)
        => AddDockerfileInstructionDestinationSymbol(fileId, line, lineNumber, symbols, "ADD", includeJsonForm: true, allowOnbuild: true);

    private static void AddDockerfileInstructionDestinationSymbol(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols,
        string instruction,
        bool includeJsonForm,
        bool allowOnbuild)
    {
        if (!TryGetDockerfileInstructionBody(line, instruction, allowOnbuild, out var body))
            return;

        string? destination;
        var jsonArrayBudgetExceeded = false;
        if (includeJsonForm && DockerfileInstructionBodyStartsWithJsonForm(body))
        {
            if (DockerfileJsonFormBodyExceedsBudget(body))
            {
                AddDockerfileJsonFormDiagnostic(symbols, fileId, lineNumber, line, "dockerfile_json_form_body_budget_exceeded", "Dockerfile JSON-form body exceeded the parse budget; JSON-form symbols were truncated.");
                return;
            }

            destination = GetDockerfileJsonFormDestination(body, out jsonArrayBudgetExceeded);
        }
        else
        {
            destination = GetDockerfileShellFormDestination(body);
        }

        if (jsonArrayBudgetExceeded)
        {
            AddDockerfileJsonFormDiagnostic(symbols, fileId, lineNumber, line, "dockerfile_json_form_array_budget_exceeded", "Dockerfile JSON-form array exceeded the item budget; remaining items were truncated.");
            return;
        }
        if (destination is null or "." or "./")
            return;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = instruction.Equals("COPY", StringComparison.OrdinalIgnoreCase) ? "copy" : "add",
                Name = destination,
                Line = lineNumber,
                StartLine = lineNumber,
                EndLine = lineNumber,
                Signature = line.Trim(),
            },
            line);
    }

    private static void AddDockerfileRunSymbol(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols)
    {
        if (!TryGetDockerfileInstructionBody(line, "RUN", allowOnbuild: true, out var body))
            return;

        var name = body.Trim();
        if (name.Length == 0 || name[0] == '#')
            return;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "run",
                Name = name,
                Line = lineNumber,
                StartLine = lineNumber,
                EndLine = lineNumber,
                Signature = line.Trim(),
            },
            line);
    }

    private static bool TryGetDockerfileInstructionBody(string line, string instruction, bool allowOnbuild, out string body)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (allowOnbuild
            && trimmed.Length > "ONBUILD".Length
            && trimmed.StartsWith("ONBUILD", StringComparison.OrdinalIgnoreCase)
            && char.IsWhiteSpace(trimmed["ONBUILD".Length]))
        {
            trimmed = trimmed["ONBUILD".Length..].TrimStart();
        }

        body = string.Empty;
        if (trimmed.Length <= instruction.Length
            || !trimmed.StartsWith(instruction, StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(trimmed[instruction.Length]))
        {
            return false;
        }

        body = trimmed[instruction.Length..].TrimStart().ToString();
        return true;
    }

    private static string? GetDockerfileShellFormDestination(string body)
    {
        var arguments = new List<string>();
        foreach (var token in EnumerateDockerfileInstructionTokens(body))
        {
            if (token.StartsWith("--", StringComparison.Ordinal))
                continue;
            if (token.StartsWith("[", StringComparison.Ordinal))
                return null;

            arguments.Add(token);
        }

        return arguments.Count >= 2 ? arguments[^1] : null;
    }

    private static string? GetDockerfileJsonFormDestination(string body, out bool arrayBudgetExceeded)
    {
        arrayBudgetExceeded = false;
        var jsonStart = SkipDockerfileInstructionOptions(body);
        if (jsonStart >= body.Length || body[jsonStart] != '[')
            return null;
        if (body.Length - jsonStart > DockerfileJsonFormMaxBodyLength)
            return null;

        try
        {
            using var document = ParseDockerfileJsonFormPayload(body[jsonStart..]);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            string? last = null;
            var count = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (count >= DockerfileJsonFormMaxItems)
                {
                    arrayBudgetExceeded = true;
                    return null;
                }
                count++;

                if (!TryGetDockerfileJsonFormString(item, out var value))
                    return null;

                last = value;
            }

            return count >= 2 ? last : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool DockerfileJsonFormBodyExceedsBudget(string body)
    {
        var jsonStart = SkipDockerfileInstructionOptions(body);
        return jsonStart < body.Length
               && body[jsonStart] == '['
               && body.Length - jsonStart > DockerfileJsonFormMaxBodyLength;
    }

    private static bool DockerfileInstructionBodyStartsWithJsonForm(string body)
    {
        var jsonStart = SkipDockerfileInstructionOptions(body);
        return jsonStart < body.Length && body[jsonStart] == '[';
    }

    private static void AddDockerfileJsonFormDiagnostic(
        List<SymbolRecord> symbols,
        long fileId,
        int lineNumber,
        string line,
        string category,
        string message)
    {
        var added = false;
        AddStructuredDataDiagnosticSymbol(symbols, fileId, category, lineNumber, [line], message, ref added);
    }

    private static int SkipDockerfileInstructionOptions(string body)
    {
        var index = 0;
        while (index < body.Length)
        {
            while (index < body.Length && char.IsWhiteSpace(body[index]))
                index++;

            if (index + 2 > body.Length || body[index] != '-' || body[index + 1] != '-')
                return index;

            index = ScanDockerfileInstructionToken(body, index);
        }

        return index;
    }

    private static IEnumerable<string> EnumerateDockerfileInstructionTokens(string body)
    {
        var index = 0;
        while (index < body.Length)
        {
            while (index < body.Length && char.IsWhiteSpace(body[index]))
                index++;
            if (index >= body.Length || body[index] == '#')
                yield break;

            var token = new StringBuilder(Math.Min(64, body.Length - index));
            var quote = '\0';
            while (index < body.Length)
            {
                var c = body[index];
                if (quote != '\0')
                {
                    if (c == '\\' && index + 1 < body.Length)
                    {
                        token.Append(body[index + 1]);
                        index += 2;
                        continue;
                    }

                    if (c == quote)
                    {
                        quote = '\0';
                        index++;
                        continue;
                    }

                    token.Append(c);
                    index++;
                    continue;
                }

                if (c is '"' or '\'')
                {
                    quote = c;
                    index++;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                    break;

                token.Append(c);
                index++;
            }

            if (token.Length > 0)
                yield return token.ToString();
        }
    }

    private static int ScanDockerfileInstructionToken(string body, int index)
    {
        var quote = '\0';
        while (index < body.Length)
        {
            var c = body[index];
            if (quote != '\0')
            {
                if (c == '\\' && index + 1 < body.Length)
                {
                    index += 2;
                    continue;
                }

                if (c == quote)
                    quote = '\0';

                index++;
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                index++;
                continue;
            }

            if (char.IsWhiteSpace(c))
                break;

            index++;
        }

        return index;
    }

}
