using System.Text;

namespace CodeIndex.Database;

public partial class DbReader
{
    private const int MaxStructuralGuardEvidenceCandidates = 8;
    private const int MaxStructuralInvocationLines = 32;

    private SearchGuardEvaluation FindStructuralGuardEvidence(
        string path,
        SearchPrimaryMatch primaryMatch,
        SearchGuardFilter filter,
        string? lang,
        Dictionary<SearchGuardLineWindowKey, SortedDictionary<int, string>> lineWindowCache)
    {
        if (!string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            var rejected = CreateStructuralEvidence(
                path,
                primaryMatch.LineNumber,
                primaryMatch.Text,
                filter,
                "rejected",
                "structural guard evidence is only available for C# source",
                "language_mismatch",
                subject: null,
                container: null,
                lang);
            return new SearchGuardEvaluation(primaryMatch.LineNumber, primaryMatch.LineNumber, null, [rejected]);
        }

        var container = FindStructuralGuardContainer(path, primaryMatch.LineNumber);
        if (container == null)
        {
            var rejected = CreateStructuralEvidence(
                path,
                primaryMatch.LineNumber,
                primaryMatch.Text,
                filter,
                "rejected",
                "the primary match is not inside an indexed executable container",
                "container_unresolved",
                subject: null,
                container: null,
                lang);
            return new SearchGuardEvaluation(primaryMatch.LineNumber, primaryMatch.LineNumber, null, [rejected]);
        }

        var lines = ReadLineWindow(path, container.StartLine, container.EndLine, lineWindowCache);
        return filter.EvidenceKind switch
        {
            SearchGuardEvidenceKind.CSharpBoundedFileRead => FindBoundedFileReadEvidence(
                path,
                primaryMatch,
                filter,
                lang,
                container,
                lines,
                lineWindowCache),
            SearchGuardEvidenceKind.CSharpEnumerationOptions => FindEnumerationOptionsEvidence(
                path,
                primaryMatch,
                filter,
                lang,
                container,
                lines,
                lineWindowCache),
            _ => new SearchGuardEvaluation(container.StartLine, container.EndLine, null),
        };
    }

    private SearchGuardEvaluation FindBoundedFileReadEvidence(
        string path,
        SearchPrimaryMatch primaryMatch,
        SearchGuardFilter filter,
        string? lang,
        SearchGuardContainer container,
        SortedDictionary<int, string> lines,
        Dictionary<SearchGuardLineWindowKey, SortedDictionary<int, string>> lineWindowCache)
    {
        var rejected = new List<SearchGuardEvidence>();
        var invocation = ExtractInvocation(lines, primaryMatch.LineNumber, "ReadAllText");
        if (invocation == null || invocation.Arguments.Count == 0)
        {
            AddRejectedStructuralEvidence(
                rejected,
                path,
                primaryMatch.LineNumber,
                primaryMatch.Text,
                filter,
                "the ReadAllText path argument could not be resolved",
                "read_path_unresolved",
                null,
                container.Name,
                lang);
            return new SearchGuardEvaluation(container.StartLine, container.EndLine, null, rejected);
        }

        var readPath = NormalizeCSharpExpression(invocation.Arguments[0]);
        if (!IsSimpleCSharpValueExpression(readPath))
        {
            AddRejectedStructuralEvidence(
                rejected,
                path,
                primaryMatch.LineNumber,
                primaryMatch.Text,
                filter,
                "the ReadAllText path is not a stable identifier or member access",
                "read_path_not_stable",
                readPath,
                container.Name,
                lang);
            return new SearchGuardEvaluation(container.StartLine, container.EndLine, null, rejected);
        }

        var sizeSources = FindFileSizeSources(
            path,
            lines,
            container,
            primaryMatch.LineNumber,
            readPath,
            filter,
            lang,
            rejected);
        foreach (var source in sizeSources)
        {
            foreach (var (lineNumber, text) in lines)
            {
                if (lineNumber < source.Line || lineNumber >= primaryMatch.LineNumber ||
                    !text.Contains("if", StringComparison.Ordinal))
                    continue;

                var condition = ExtractCondition(lines, lineNumber);
                if (condition == null || !ConditionReferencesSizeSource(condition.Value.Text, source.Expression))
                    continue;

                if (source.Alias != null && HasAssignmentBetween(lines, source.Alias, source.Line + 1, lineNumber - 1))
                {
                    AddRejectedStructuralEvidence(
                        rejected,
                        path,
                        lineNumber,
                        text,
                        filter,
                        $"size alias '{source.Alias}' is reassigned before the guard",
                        "size_alias_reassigned",
                        readPath,
                        container.Name,
                        lang);
                    continue;
                }

                if (IsRejectingUpperBound(condition.Value.Text, source.Expression) &&
                    GuardBranchTerminates(lines, lineNumber, condition.Value.EndLine, primaryMatch.LineNumber))
                {
                    return new SearchGuardEvaluation(
                        container.StartLine,
                        container.EndLine,
                        CreateStructuralEvidence(
                            path,
                            lineNumber,
                            text,
                            filter,
                            "accepted",
                            "the same ReadAllText path is size-checked and the oversized branch terminates before the read",
                            "same_path_size_guard",
                            readPath,
                            container.Name,
                            lang),
                        rejected.Count == 0 ? null : rejected);
                }

                if (IsAcceptingUpperBound(condition.Value.Text, source.Expression) &&
                    IsLineInsideGuardBranch(lines, lineNumber, condition.Value.EndLine, primaryMatch.LineNumber))
                {
                    return new SearchGuardEvaluation(
                        container.StartLine,
                        container.EndLine,
                        CreateStructuralEvidence(
                            path,
                            lineNumber,
                            text,
                            filter,
                            "accepted",
                            "the same ReadAllText path is read only inside the bounded-size branch",
                            "same_path_control_guard",
                            readPath,
                            container.Name,
                            lang),
                        rejected.Count == 0 ? null : rejected);
                }

                AddRejectedStructuralEvidence(
                    rejected,
                    path,
                    lineNumber,
                    text,
                    filter,
                    "the size comparison is inverted or does not terminate/control the path to ReadAllText",
                    "size_guard_control_path_rejected",
                    readPath,
                    container.Name,
                    lang);
            }
        }

        var helperEvidence = FindResolvedBoundedWriterEvidence(
            path,
            primaryMatch,
            filter,
            lang,
            container,
            lines,
            readPath,
            rejected,
            lineWindowCache);
        if (helperEvidence != null)
            return new SearchGuardEvaluation(container.StartLine, container.EndLine, helperEvidence, rejected.Count == 0 ? null : rejected);

        if (rejected.Count == 0)
        {
            AddRejectedStructuralEvidence(
                rejected,
                path,
                primaryMatch.LineNumber,
                primaryMatch.Text,
                filter,
                "no same-path size guard or resolved bounded writer reaches this ReadAllText call",
                "bounded_read_evidence_missing",
                readPath,
                container.Name,
                lang);
        }

        return new SearchGuardEvaluation(container.StartLine, container.EndLine, null, rejected);
    }

    private SearchGuardEvaluation FindEnumerationOptionsEvidence(
        string path,
        SearchPrimaryMatch primaryMatch,
        SearchGuardFilter filter,
        string? lang,
        SearchGuardContainer container,
        SortedDictionary<int, string> lines,
        Dictionary<SearchGuardLineWindowKey, SortedDictionary<int, string>> lineWindowCache)
    {
        var rejected = new List<SearchGuardEvidence>();
        var invocation = ExtractInvocation(lines, primaryMatch.LineNumber, "Directory.Enumerate");
        if (invocation == null || invocation.Arguments.Count < 3)
        {
            AddRejectedStructuralEvidence(
                rejected,
                path,
                primaryMatch.LineNumber,
                primaryMatch.Text,
                filter,
                "the Directory.Enumerate* call does not pass an EnumerationOptions argument",
                "enumeration_options_argument_missing",
                null,
                container.Name,
                lang);
            return new SearchGuardEvaluation(container.StartLine, container.EndLine, null, rejected);
        }

        var optionsExpression = RemoveNamedArgumentPrefix(invocation.Arguments[^1]);
        var normalizedOptions = NormalizeCSharpExpression(optionsExpression);
        if (normalizedOptions.StartsWith("newEnumerationOptions", StringComparison.Ordinal) ||
            normalizedOptions.StartsWith("new()", StringComparison.Ordinal))
        {
            return new SearchGuardEvaluation(
                container.StartLine,
                container.EndLine,
                CreateStructuralEvidence(
                    path,
                    primaryMatch.LineNumber,
                    primaryMatch.Text,
                    filter,
                    "accepted",
                    "the enumeration call receives an inline EnumerationOptions value",
                    "same_call_options_argument",
                    normalizedOptions,
                    container.Name,
                    lang));
        }

        if (!IsSimpleCSharpValueExpression(normalizedOptions))
        {
            AddRejectedStructuralEvidence(
                rejected,
                path,
                primaryMatch.LineNumber,
                primaryMatch.Text,
                filter,
                "the final enumeration argument is not a resolvable local or same-container options expression",
                "enumeration_options_symbol_unresolved",
                normalizedOptions,
                container.Name,
                lang);
            return new SearchGuardEvaluation(container.StartLine, container.EndLine, null, rejected);
        }

        var optionsParts = normalizedOptions.Split('.');
        var optionsName = optionsParts[^1];
        if (optionsParts.Length > 1 && !IsSameContainerReceiver(optionsParts[..^1], container.ContainerName))
        {
            AddRejectedStructuralEvidence(
                rejected,
                path,
                primaryMatch.LineNumber,
                primaryMatch.Text,
                filter,
                $"the EnumerationOptions receiver in '{normalizedOptions}' is not the current type/container",
                "enumeration_options_receiver_rejected",
                normalizedOptions,
                container.Name,
                lang);
            return new SearchGuardEvaluation(container.StartLine, container.EndLine, null, rejected);
        }

        var definition = FindEnumerationOptionsDefinition(
            path,
            container,
            primaryMatch.LineNumber,
            optionsName,
            lineWindowCache);
        if (definition == null)
        {
            AddRejectedStructuralEvidence(
                rejected,
                path,
                primaryMatch.LineNumber,
                primaryMatch.Text,
                filter,
                $"'{normalizedOptions}' does not resolve to EnumerationOptions in the same executable/type container",
                "enumeration_options_definition_rejected",
                normalizedOptions,
                container.Name,
                lang);
            return new SearchGuardEvaluation(container.StartLine, container.EndLine, null, rejected);
        }

        return new SearchGuardEvaluation(
            container.StartLine,
            container.EndLine,
            CreateStructuralEvidence(
                path,
                definition.Line,
                definition.Text,
                filter,
                "accepted",
                "the enumeration argument resolves to an EnumerationOptions definition in the same executable/type container",
                "same_argument_options_definition",
                normalizedOptions,
                definition.Container,
                lang));
    }

    private List<FileSizeSource> FindFileSizeSources(
        string path,
        SortedDictionary<int, string> lines,
        SearchGuardContainer container,
        int readLine,
        string readPath,
        SearchGuardFilter filter,
        string? lang,
        List<SearchGuardEvidence> rejected)
    {
        var sources = new List<FileSizeSource>();
        foreach (var (lineNumber, text) in lines)
        {
            if (lineNumber >= readLine)
                break;

            var normalized = NormalizeCSharpExpression(text);
            var marker = "newFileInfo(";
            var markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
                continue;

            var argumentStart = markerIndex + marker.Length;
            var argumentEnd = FindMatchingDelimiter(normalized, argumentStart - 1, '(', ')');
            if (argumentEnd < argumentStart)
                continue;
            var hasInlineLength = normalized.AsSpan(argumentEnd).StartsWith(").Length", StringComparison.Ordinal);

            var candidatePath = normalized[argumentStart..argumentEnd];
            if (!string.Equals(candidatePath, readPath, StringComparison.Ordinal))
            {
                AddRejectedStructuralEvidence(
                    rejected,
                    path,
                    lineNumber,
                    text,
                    filter,
                    "FileInfo.Length targets a different path than ReadAllText",
                    "different_path_size_source",
                    candidatePath,
                    container.Name,
                    lang);
                continue;
            }

            var assignmentIndex = text.IndexOf('=');
            string? alias = null;
            if (assignmentIndex >= 0 &&
                !IsComparisonOperator(text, assignmentIndex))
                alias = LastIdentifier(text[..assignmentIndex]);

            if (!hasInlineLength && alias == null)
                continue;
            var expression = hasInlineLength
                ? alias ?? $"newFileInfo({readPath}).Length"
                : alias + ".Length";
            sources.Add(new FileSizeSource(lineNumber, expression, alias));
        }

        return sources;
    }

    private SearchGuardEvidence? FindResolvedBoundedWriterEvidence(
        string path,
        SearchPrimaryMatch primaryMatch,
        SearchGuardFilter filter,
        string? lang,
        SearchGuardContainer container,
        SortedDictionary<int, string> callerLines,
        string readPath,
        List<SearchGuardEvidence> rejected,
        Dictionary<SearchGuardLineWindowKey, SortedDictionary<int, string>> lineWindowCache)
    {
        foreach (var call in FindResolvedCallsBefore(container.SymbolId, primaryMatch.LineNumber))
        {
            var invocation = ExtractInvocation(callerLines, call.Line, call.Name);
            if (invocation == null)
                continue;

            var argumentIndex = invocation.Arguments.FindIndex(argument =>
                string.Equals(NormalizeCSharpExpression(RemoveNamedArgumentPrefix(argument)), readPath, StringComparison.Ordinal));
            if (argumentIndex < 0)
                continue;

            if (IsTaskLike(call.ReturnType) &&
                !invocation.Text.Contains("await", StringComparison.Ordinal) &&
                !invocation.Text.Contains("GetResult", StringComparison.Ordinal))
            {
                AddRejectedStructuralEvidence(
                    rejected,
                    path,
                    call.Line,
                    callerLines.GetValueOrDefault(call.Line, call.Name),
                    filter,
                    "the same-path helper is asynchronous but is not awaited before ReadAllText",
                    "bounded_writer_not_awaited",
                    readPath,
                    container.Name,
                    lang);
                continue;
            }

            var parameters = ParseParameterNames(call.Signature);
            if (argumentIndex >= parameters.Count)
                continue;

            var targetParameter = parameters[argumentIndex];
            var targetLines = ReadLineWindow(call.Path, call.StartLine, call.EndLine, lineWindowCache);
            foreach (var (lineNumber, text) in targetLines)
            {
                if (!text.Contains("Bounded", StringComparison.Ordinal))
                    continue;

                var boundedInvocation = ExtractInvocation(targetLines, lineNumber, "Bounded");
                if (boundedInvocation == null)
                    continue;
                var normalized = NormalizeCSharpExpression(boundedInvocation.Text);
                if (!normalized.Contains(targetParameter, StringComparison.Ordinal) ||
                    (!normalized.Contains("Write", StringComparison.Ordinal) &&
                     !normalized.Contains("Copy", StringComparison.Ordinal) &&
                     !normalized.Contains("Save", StringComparison.Ordinal) &&
                     !normalized.Contains("Download", StringComparison.Ordinal)) ||
                    !ContainsBoundToken(normalized))
                    continue;

                return CreateStructuralEvidence(
                    call.Path,
                    lineNumber,
                    text,
                    filter,
                    "accepted",
                    $"resolved helper '{call.Name}' writes the same path through a bounded operation before ReadAllText",
                    "same_path_resolved_bounded_writer",
                    readPath,
                    call.Name,
                    lang);
            }

            AddRejectedStructuralEvidence(
                rejected,
                path,
                call.Line,
                callerLines.GetValueOrDefault(call.Line, call.Name),
                filter,
                $"resolved helper '{call.Name}' receives the same path but has no bounded write contract",
                "resolved_helper_not_bounded",
                readPath,
                container.Name,
                lang);
        }

        return null;
    }

    private SearchGuardContainer? FindStructuralGuardContainer(string path, int line)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.id, s.name, s.container_name, s.start_line, s.end_line
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = @path
              AND s.start_line <= @line
              AND s.end_line >= @line
              AND s.kind IN ('function', 'method', 'async_function', 'async_generator', 'generator', 'property', 'accessor', 'lambda')
            ORDER BY (s.end_line - s.start_line) ASC, s.start_line DESC, s.id ASC
            LIMIT 1;";
        SqliteCommandPolicy.Add(cmd, "@path", path);
        SqliteCommandPolicy.Add(cmd, "@line", line);
        using var reader = cmd.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return null;

        return new SearchGuardContainer(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    private List<ResolvedStructuralCall> FindResolvedCallsBefore(long sourceSymbolId, int line)
    {
        var calls = new List<ResolvedStructuralCall>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT r.line, r.symbol_name, s.signature, s.return_type, s.start_line, s.end_line, f.path
            FROM symbol_references r
            JOIN symbols s ON s.id = r.target_symbol_id
            JOIN files f ON f.id = s.file_id
            WHERE r.source_symbol_id = @sourceSymbolId
              AND r.reference_kind = 'call'
              AND r.line < @line
              AND r.target_symbol_id IS NOT NULL
            ORDER BY r.line DESC, r.id ASC;";
        SqliteCommandPolicy.Add(cmd, "@sourceSymbolId", sourceSymbolId);
        SqliteCommandPolicy.Add(cmd, "@line", line);
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            calls.Add(new ResolvedStructuralCall(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6)));
        }

        return calls;
    }

    private EnumerationOptionsDefinition? FindEnumerationOptionsDefinition(
        string path,
        SearchGuardContainer container,
        int primaryLine,
        string name,
        Dictionary<SearchGuardLineWindowKey, SortedDictionary<int, string>> lineWindowCache)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.line, s.start_line, s.end_line, s.return_type, s.signature, s.container_name
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = @path
              AND s.name = @name COLLATE BINARY
              AND s.start_line <= @primaryLine
              AND (
                    (s.start_line >= @containerStart AND s.end_line <= @containerEnd)
                    OR s.container_name = @typeContainer COLLATE BINARY
                  )
            ORDER BY
                CASE WHEN s.start_line >= @containerStart AND s.end_line <= @containerEnd THEN 0 ELSE 1 END,
                s.start_line DESC,
                s.id ASC;";
        SqliteCommandPolicy.Add(cmd, "@path", path);
        SqliteCommandPolicy.Add(cmd, "@name", name);
        SqliteCommandPolicy.Add(cmd, "@primaryLine", primaryLine);
        SqliteCommandPolicy.Add(cmd, "@containerStart", container.StartLine);
        SqliteCommandPolicy.Add(cmd, "@containerEnd", container.EndLine);
        SqliteCommandPolicy.Add(cmd, "@typeContainer", container.ContainerName ?? string.Empty);
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var line = reader.GetInt32(0);
                var startLine = reader.GetInt32(1);
                var endLine = reader.GetInt32(2);
                var returnType = reader.IsDBNull(3) ? null : reader.GetString(3);
                var signature = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                var definitionLines = ReadLineWindow(path, startLine, endLine, lineWindowCache);
                var text = definitionLines.GetValueOrDefault(line, signature);
                var definitionText = string.Join('\n', definitionLines.Values);
                if (!string.Equals(returnType, "EnumerationOptions", StringComparison.Ordinal) &&
                    !definitionText.Contains("EnumerationOptions", StringComparison.Ordinal))
                    continue;

                return new EnumerationOptionsDefinition(
                    line,
                    text,
                    reader.IsDBNull(5) ? container.Name : reader.GetString(5));
            }
        }

        var containerLines = ReadLineWindow(path, container.StartLine, Math.Min(container.EndLine, primaryLine), lineWindowCache);
        foreach (var (line, text) in containerLines.Reverse())
        {
            var normalized = NormalizeCSharpExpression(text);
            var explicitTypeMarker = "EnumerationOptions" + name;
            var inferredTypeMarker = "var" + name + "=newEnumerationOptions";
            if (!normalized.Contains(explicitTypeMarker, StringComparison.Ordinal) &&
                !normalized.Contains(inferredTypeMarker, StringComparison.Ordinal))
                continue;

            return new EnumerationOptionsDefinition(line, text, container.Name);
        }

        return null;
    }

    private static InvocationText? ExtractInvocation(
        SortedDictionary<int, string> lines,
        int startLine,
        string marker)
    {
        var text = JoinFollowingLines(lines, startLine, MaxStructuralInvocationLines);
        var markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        var openParen = text.IndexOf('(', markerIndex + marker.Length);
        if (openParen < 0)
            return null;

        var closeParen = FindMatchingDelimiter(text, openParen, '(', ')');
        if (closeParen < 0)
            return null;

        var invocationEnd = closeParen + 1;
        var tailLimit = Math.Min(text.Length, invocationEnd + 160);
        var tail = text[invocationEnd..tailLimit];
        var statementEnd = tail.IndexOf(';');
        if (statementEnd >= 0)
            invocationEnd += statementEnd + 1;

        return new InvocationText(
            text[..Math.Min(text.Length, invocationEnd)],
            SplitTopLevelArguments(text[(openParen + 1)..closeParen]));
    }

    private static (string Text, int EndLine)? ExtractCondition(
        SortedDictionary<int, string> lines,
        int startLine)
    {
        var text = JoinFollowingLines(lines, startLine, 12);
        var ifIndex = text.IndexOf("if", StringComparison.Ordinal);
        if (ifIndex < 0)
            return null;
        var openParen = text.IndexOf('(', ifIndex + 2);
        if (openParen < 0)
            return null;
        var closeParen = FindMatchingDelimiter(text, openParen, '(', ')');
        if (closeParen < 0)
            return null;

        var lineCount = text[..(closeParen + 1)].Count(ch => ch == '\n');
        return (text[(openParen + 1)..closeParen], startLine + lineCount);
    }

    private static List<string> SplitTopLevelArguments(string arguments)
    {
        var result = new List<string>();
        var start = 0;
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var angle = 0;
        var inString = false;
        var stringDelimiter = '\0';
        var escaped = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            var ch = arguments[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == stringDelimiter)
                    inString = false;
                continue;
            }

            if (ch is '\'' or '"')
            {
                inString = true;
                stringDelimiter = ch;
                continue;
            }

            switch (ch)
            {
                case '(':
                    paren++;
                    break;
                case ')':
                    paren--;
                    break;
                case '[':
                    bracket++;
                    break;
                case ']':
                    bracket--;
                    break;
                case '{':
                    brace++;
                    break;
                case '}':
                    brace--;
                    break;
                case '<':
                    angle++;
                    break;
                case '>':
                    if (angle > 0)
                        angle--;
                    break;
                case ',' when paren == 0 && bracket == 0 && brace == 0 && angle == 0:
                    result.Add(arguments[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        result.Add(arguments[start..].Trim());
        return result;
    }

    private static int FindMatchingDelimiter(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        var inString = false;
        var delimiter = '\0';
        var escaped = false;
        for (var i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == delimiter)
                    inString = false;
                continue;
            }

            if (ch is '\'' or '"')
            {
                inString = true;
                delimiter = ch;
                continue;
            }
            if (ch == open)
                depth++;
            else if (ch == close && --depth == 0)
                return i;
        }

        return -1;
    }

    private static string JoinFollowingLines(SortedDictionary<int, string> lines, int startLine, int maxLines)
    {
        var builder = new StringBuilder();
        var count = 0;
        foreach (var (lineNumber, text) in lines)
        {
            if (lineNumber < startLine)
                continue;
            if (count++ >= maxLines)
                break;
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(text);
        }
        return builder.ToString();
    }

    private static string NormalizeCSharpExpression(string expression)
    {
        var builder = new StringBuilder(expression.Length);
        foreach (var ch in expression)
        {
            if (!char.IsWhiteSpace(ch) && ch != '@')
                builder.Append(ch);
        }
        return builder.ToString().Trim(';');
    }

    private static string RemoveNamedArgumentPrefix(string expression)
    {
        var colon = expression.IndexOf(':');
        if (colon <= 0 || expression[..colon].Any(ch => !char.IsLetterOrDigit(ch) && ch != '_' && ch != '@'))
            return expression;
        return expression[(colon + 1)..];
    }

    private static bool IsSimpleCSharpValueExpression(string expression)
        => expression.Length > 0 && expression.Split('.').All(IsSimpleCSharpIdentifier);

    private static bool IsSimpleCSharpIdentifier(string identifier)
        => identifier.Length > 0 &&
           (char.IsLetter(identifier[0]) || identifier[0] == '_') &&
           identifier.Skip(1).All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static bool IsSameContainerReceiver(string[] receiverParts, string? containerName)
    {
        if (receiverParts.Length == 1 && receiverParts[0] == "this")
            return true;
        if (string.IsNullOrWhiteSpace(containerName))
            return false;

        var containerParts = containerName.Split('.');
        return receiverParts.SequenceEqual(containerParts, StringComparer.Ordinal) ||
               (receiverParts.Length == 1 &&
                string.Equals(receiverParts[0], containerParts[^1], StringComparison.Ordinal));
    }

    private static string? LastIdentifier(string text)
    {
        var end = text.Length - 1;
        while (end >= 0 && !char.IsLetterOrDigit(text[end]) && text[end] != '_')
            end--;
        if (end < 0)
            return null;
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            start--;
        var identifier = text[start..(end + 1)];
        return IsSimpleCSharpIdentifier(identifier) ? identifier : null;
    }

    private static bool ConditionReferencesSizeSource(string condition, string source)
        => NormalizeCSharpExpression(condition).Contains(source, StringComparison.Ordinal);

    private static bool IsRejectingUpperBound(string condition, string source)
    {
        var normalized = NormalizeCSharpExpression(condition);
        if (normalized.Contains("&&", StringComparison.Ordinal))
            return false;
        var index = normalized.IndexOf(source, StringComparison.Ordinal);
        if (index < 0)
            return false;
        var suffix = normalized[(index + source.Length)..];
        var prefix = normalized[..index];
        return suffix.StartsWith('>') || prefix.EndsWith('<');
    }

    private static bool IsAcceptingUpperBound(string condition, string source)
    {
        var normalized = NormalizeCSharpExpression(condition);
        if (normalized.Contains("||", StringComparison.Ordinal))
            return false;
        var index = normalized.IndexOf(source, StringComparison.Ordinal);
        if (index < 0)
            return false;
        var suffix = normalized[(index + source.Length)..];
        var prefix = normalized[..index];
        return suffix.StartsWith('<') || prefix.EndsWith('>');
    }

    private static bool GuardBranchTerminates(
        SortedDictionary<int, string> lines,
        int ifLine,
        int conditionEndLine,
        int readLine)
    {
        var depth = 0;
        var sawBlock = false;
        foreach (var (lineNumber, text) in lines)
        {
            if (lineNumber < conditionEndLine || lineNumber >= readLine || lineNumber > ifLine + 24)
                continue;

            var trimmed = text.Trim();
            if (!sawBlock && lineNumber > conditionEndLine &&
                (trimmed.StartsWith("return", StringComparison.Ordinal) || trimmed.StartsWith("throw", StringComparison.Ordinal)))
                return true;

            foreach (var ch in text)
            {
                if (ch == '{')
                {
                    depth++;
                    sawBlock = true;
                }
                else if (ch == '}')
                {
                    depth--;
                }
            }

            if (sawBlock && depth == 1 &&
                (trimmed.StartsWith("return", StringComparison.Ordinal) || trimmed.StartsWith("throw", StringComparison.Ordinal)))
                return true;
            if (sawBlock && depth <= 0)
                return false;
        }

        return false;
    }

    private static bool IsLineInsideGuardBranch(
        SortedDictionary<int, string> lines,
        int ifLine,
        int conditionEndLine,
        int targetLine)
    {
        var depth = 0;
        var sawOpen = false;
        foreach (var (lineNumber, text) in lines)
        {
            if (lineNumber < ifLine)
                continue;
            if (lineNumber > targetLine)
                break;
            foreach (var ch in text)
            {
                if (ch == '{')
                {
                    depth++;
                    if (lineNumber >= conditionEndLine)
                        sawOpen = true;
                }
                else if (ch == '}')
                {
                    depth--;
                }
            }
            if (sawOpen && depth <= 0 && lineNumber < targetLine)
                return false;
        }
        return sawOpen && depth > 0;
    }

    private static bool HasAssignmentBetween(
        SortedDictionary<int, string> lines,
        string alias,
        int startLine,
        int endLine)
    {
        foreach (var (lineNumber, text) in lines)
        {
            if (lineNumber < startLine || lineNumber > endLine)
                continue;
            var normalized = NormalizeCSharpExpression(text);
            var marker = alias + "=";
            var index = normalized.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && !IsComparisonOperator(normalized, index + alias.Length))
                return true;
        }
        return false;
    }

    private static bool IsComparisonOperator(string text, int equalsIndex)
        => (equalsIndex > 0 && text[equalsIndex - 1] is '!' or '<' or '>' or '=') ||
           (equalsIndex + 1 < text.Length && text[equalsIndex + 1] is '=' or '>');

    private static bool ContainsBoundToken(string normalized)
        => normalized.Contains("max", StringComparison.OrdinalIgnoreCase) ||
           normalized.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
           normalized.Contains("cap", StringComparison.OrdinalIgnoreCase) ||
           normalized.Any(char.IsDigit);

    private static bool IsTaskLike(string? returnType)
        => returnType != null &&
           (returnType.Contains("Task", StringComparison.Ordinal) || returnType.Contains("ValueTask", StringComparison.Ordinal));

    private static List<string> ParseParameterNames(string signature)
    {
        var openParen = signature.IndexOf('(');
        if (openParen < 0)
            return [];
        var closeParen = FindMatchingDelimiter(signature, openParen, '(', ')');
        if (closeParen < 0)
            return [];
        return SplitTopLevelArguments(signature[(openParen + 1)..closeParen])
            .Select(parameter => parameter.Split('=')[0])
            .Select(LastIdentifier)
            .Where(name => name != null)
            .Select(name => name!)
            .ToList();
    }

    private void AddRejectedStructuralEvidence(
        List<SearchGuardEvidence> rejected,
        string path,
        int line,
        string text,
        SearchGuardFilter filter,
        string reason,
        string relationship,
        string? subject,
        string? container,
        string? lang)
    {
        if (rejected.Count >= MaxStructuralGuardEvidenceCandidates)
            return;
        rejected.Add(CreateStructuralEvidence(
            path,
            line,
            text,
            filter,
            "rejected",
            reason,
            relationship,
            subject,
            container,
            lang));
    }

    private static SearchGuardEvidence CreateStructuralEvidence(
        string path,
        int line,
        string text,
        SearchGuardFilter filter,
        string decision,
        string reason,
        string relationship,
        string? subject,
        string? container,
        string? lang)
    {
        var matchText = subject ?? filter.Query;
        var columnIndex = text.IndexOf(matchText, StringComparison.Ordinal);
        if (columnIndex < 0)
            columnIndex = 0;
        var length = Math.Max(1, Math.Min(matchText.Length, Math.Max(1, text.Length - columnIndex)));
        var facet = SearchMatchClassifier.Classify(path, lang, line, text, columnIndex + 1, length);
        return new SearchGuardEvidence
        {
            Role = FormatSearchGuardRole(filter.Role),
            Direction = FormatSearchGuardDirection(filter.Direction),
            Scope = "container",
            Query = filter.Query,
            Name = FormatSearchGuardName(filter),
            Pattern = filter.Query,
            Relationship = relationship,
            Decision = decision,
            Reason = reason,
            EvidencePath = path,
            Subject = subject,
            Container = container,
            Span = new SearchGuardSpan
            {
                Line = line,
                Column = columnIndex + 1,
                Length = length,
            },
            Line = line,
            Column = columnIndex + 1,
            Length = length,
            Origin = facet.Origin,
            Text = text,
        };
    }

    private sealed record SearchGuardContainer(
        long SymbolId,
        string Name,
        string? ContainerName,
        int StartLine,
        int EndLine);

    private sealed record FileSizeSource(int Line, string Expression, string? Alias);

    private sealed record ResolvedStructuralCall(
        int Line,
        string Name,
        string Signature,
        string? ReturnType,
        int StartLine,
        int EndLine,
        string Path);

    private sealed record EnumerationOptionsDefinition(int Line, string Text, string Container);

    private sealed record InvocationText(string Text, List<string> Arguments);
}
