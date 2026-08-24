using System.Globalization;
using System.Text;
using CodeIndex.Database;
using CodeIndex.Semantics;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private const int ParserGuardContextLinesBefore = 48;
    private const int ParserGuardContextLinesAfter = 24;
    private const int ParserGuardInvocationCharacterLimit = 4096;
    private const int ParserGuardSourcePrefixCharacterLimit = 64 * 1024;
    private const int ParserGuardEvidenceIdentifierLimit = 128;

    private static int GetParserGuardRequiredLine(SearchDisplayRow row)
    {
        var maximumMatchLine = GetParserGuardMatchLines(row)
            .DefaultIfEmpty(row.Result.StartLine)
            .Max();
        if (maximumMatchLine <= 0
            || maximumMatchLine > CSharpSemanticTokenClassifier.DefaultExcerptSourceLineLimit)
        {
            return maximumMatchLine;
        }

        var containingSymbolEndLine = row.Compact.EnclosingSymbolEndLine.GetValueOrDefault(maximumMatchLine);
        return Math.Min(
            Math.Max(maximumMatchLine, containingSymbolEndLine),
            maximumMatchLine + ParserGuardContextLinesAfter);
    }

    private static SearchAuditClassificationJsonResult ClassifyParserGuardEvidence(
        SearchRecipeClassifierJsonResult classifier,
        string recipeQuery,
        SearchDisplayRow row,
        DbReader reader,
        JsonTrustLexicalContextCache lexicalContextCache)
    {
        var matchLines = GetParserGuardMatchLines(row).ToList();
        if (matchLines.Count == 0)
            matchLines.Add(row.Result.StartLine);

        var requiredLine = GetParserGuardRequiredLine(row);
        var lexicalContext = requiredLine > 0
            && requiredLine <= CSharpSemanticTokenClassifier.DefaultExcerptSourceLineLimit
                ? GetJsonTrustLexicalContext(reader, row, requiredLine, lexicalContextCache)
                : null;
        var matchEvidence = matchLines
            .Select(line => ClassifyParserGuardMatch(recipeQuery, row, line, lexicalContext))
            .ToList();

        // A compact result can represent more than one parser call. Keep that row
        // actionable if any represented operation has no related guard evidence.
        var selected = matchEvidence.FirstOrDefault(evidence => evidence.Category == "unbounded_materialization")
            ?? matchEvidence.FirstOrDefault(evidence => evidence.Category == "bounded_payload")
            ?? matchEvidence[0];
        var categoryMetadata = classifier.Categories
            .First(category => string.Equals(category.Name, selected.Category, StringComparison.Ordinal));
        var details = new List<string>
        {
            "authority:triage_hint_not_proof",
            "operation_precedence:bounded_payload_over_streaming_or_cancelable",
            $"operation:{selected.Operation}",
            $"match_line:{selected.Line.ToString(CultureInfo.InvariantCulture)}",
        };
        if (!string.IsNullOrWhiteSpace(selected.PayloadIdentifier))
            details.Add($"payload_identifier:{selected.PayloadIdentifier}");
        details.AddRange(selected.Signals);

        var representedCategories = matchEvidence
            .Select(evidence => evidence.Category)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToList();
        if (representedCategories.Count > 1)
        {
            details.Add($"represented_categories:{string.Join(',', representedCategories)}");
            if (selected.Category == "unbounded_materialization")
                details.Add("row_precedence:any_unbounded_match_remains_unbounded");
        }

        return new SearchAuditClassificationJsonResult(
            classifier.Name,
            categoryMetadata.Name,
            categoryMetadata.Description,
            categoryMetadata.ReviewGuidance,
            details);
    }

    private static IEnumerable<int> GetParserGuardMatchLines(SearchDisplayRow row)
        => row.Compact.MatchLines
            .Where(line => line > 0)
            .Distinct()
            .OrderBy(line => line);

    private static ParserGuardMatchEvidence ClassifyParserGuardMatch(
        string recipeQuery,
        SearchDisplayRow row,
        int matchLine,
        JsonTrustLexicalContext? lexicalContext)
    {
        var operationName = recipeQuery.Trim();
        if (lexicalContext == null
            || matchLine <= 0
            || matchLine > lexicalContext.MaskedLines.Length
            || !TryBuildParserGuardOperation(
                recipeQuery,
                row,
                matchLine,
                lexicalContext,
                out var operation))
        {
            return new ParserGuardMatchEvidence(
                "unbounded_materialization",
                operationName,
                null,
                matchLine,
                ["materialization:no_related_bound_or_streaming_signal"]);
        }

        var streamingSignals = GetParserGuardStreamingSignals(operation);
        if (TryFindParserGuardBound(operation, out var boundSignal))
        {
            var signals = new List<string> { boundSignal };
            signals.AddRange(streamingSignals.Select(signal => $"coincident_{signal}"));
            return new ParserGuardMatchEvidence(
                "bounded_payload",
                operation.OperationName,
                operation.PayloadIdentifier,
                matchLine,
                signals);
        }

        if (streamingSignals.Count > 0)
        {
            return new ParserGuardMatchEvidence(
                "streaming_or_cancelable",
                operation.OperationName,
                operation.PayloadIdentifier,
                matchLine,
                streamingSignals);
        }

        return new ParserGuardMatchEvidence(
            "unbounded_materialization",
            operation.OperationName,
            operation.PayloadIdentifier,
            matchLine,
            ["materialization:no_related_bound_or_streaming_signal"]);
    }

    private static bool TryBuildParserGuardOperation(
        string recipeQuery,
        SearchDisplayRow row,
        int matchLine,
        JsonTrustLexicalContext lexicalContext,
        out ParserGuardOperation operation)
    {
        operation = default!;
        var query = recipeQuery.Trim();
        if (query.Length == 0)
            return false;

        var matchText = lexicalContext.MaskedLines[matchLine - 1];
        var queryIndex = matchText.IndexOf(query, StringComparison.Ordinal);
        if (queryIndex < 0)
            queryIndex = matchText.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (queryIndex < 0)
            return false;

        var symbolStartLine = GetParserGuardScopeStartLine(
            row,
            matchLine,
            queryIndex,
            lexicalContext);
        var sourcePrefix = new StringBuilder();
        for (var line = symbolStartLine; line < matchLine; line++)
            AppendParserGuardSourcePrefix(sourcePrefix, lexicalContext.MaskedLines[line - 1], appendNewline: true);
        AppendParserGuardSourcePrefix(sourcePrefix, matchText.AsSpan(0, queryIndex), appendNewline: false);

        var invocation = new StringBuilder();
        var matchInvocation = matchText.AsSpan(queryIndex);
        invocation.Append(matchInvocation[..Math.Min(matchInvocation.Length, ParserGuardInvocationCharacterLimit)]);
        var invocationComplete = matchInvocation.Contains(';');
        var maximumLine = Math.Min(
            lexicalContext.MaskedLines.Length,
            Math.Min(
                row.Compact.EnclosingSymbolEndLine.GetValueOrDefault(matchLine + ParserGuardContextLinesAfter),
                matchLine + ParserGuardContextLinesAfter));
        for (var line = matchLine + 1;
             line <= maximumLine
             && invocation.Length < ParserGuardInvocationCharacterLimit
             && !invocationComplete;
             line++)
        {
            invocation.Append('\n');
            var continuation = lexicalContext.MaskedLines[line - 1].AsSpan();
            var available = ParserGuardInvocationCharacterLimit - invocation.Length;
            invocation.Append(continuation[..Math.Min(continuation.Length, available)]);
            invocationComplete = continuation.Contains(';');
        }

        var invocationText = invocation.ToString();
        var operationName = GetParserGuardOperationName(query, invocationText);
        var firstArgument = TryGetParserGuardFirstArgument(invocationText);
        operation = new ParserGuardOperation(
            operationName,
            GetParserGuardPayloadIdentifier(firstArgument),
            invocationText,
            sourcePrefix.ToString());
        return true;
    }

    private static void AppendParserGuardSourcePrefix(
        StringBuilder builder,
        ReadOnlySpan<char> value,
        bool appendNewline)
    {
        if (appendNewline)
            builder.Append('\n');
        if (value.Length >= ParserGuardSourcePrefixCharacterLimit)
        {
            builder.Clear();
            builder.Append(value[^ParserGuardSourcePrefixCharacterLimit..]);
            return;
        }

        var overflow = builder.Length + value.Length - ParserGuardSourcePrefixCharacterLimit;
        if (overflow > 0)
            builder.Remove(0, overflow);
        builder.Append(value);
    }

    private static int GetParserGuardScopeStartLine(
        SearchDisplayRow row,
        int matchLine,
        int queryIndex,
        JsonTrustLexicalContext lexicalContext)
    {
        var boundedWindowStart = Math.Max(1, matchLine - ParserGuardContextLinesBefore);
        var declaredStart = row.Compact.EnclosingSymbolStartLine.GetValueOrDefault();
        var declaredEnd = row.Compact.EnclosingSymbolEndLine.GetValueOrDefault();
        if (declaredStart > 0 && matchLine >= declaredStart && matchLine <= declaredEnd)
            return Math.Max(boundedWindowStart, declaredStart);

        var matchPrefix = lexicalContext.MaskedLines[matchLine - 1].AsSpan(0, queryIndex);
        if (matchPrefix.Contains("=>", StringComparison.Ordinal))
            return matchLine;

        var openBlocks = new Stack<int>();
        for (var line = 1; line <= matchLine; line++)
        {
            var source = lexicalContext.MaskedLines[line - 1];
            var length = line == matchLine ? Math.Min(queryIndex, source.Length) : source.Length;
            for (var index = 0; index < length; index++)
            {
                if (source[index] == '{')
                    openBlocks.Push(line);
                else if (source[index] == '}' && openBlocks.Count > 0)
                    openBlocks.Pop();
            }
        }

        return openBlocks.Count > 0
            ? Math.Max(boundedWindowStart, openBlocks.Peek())
            : boundedWindowStart;
    }

    private static string GetParserGuardOperationName(string query, string invocationText)
    {
        var length = Math.Min(query.Length, invocationText.Length);
        while (length < invocationText.Length
               && length < 128
               && IsParserGuardIdentifierCharacter(invocationText[length]))
        {
            length++;
        }
        return invocationText[..length].Trim();
    }

    private static string TryGetParserGuardFirstArgument(string invocationText)
    {
        var openingParenthesis = invocationText.IndexOf('(');
        if (openingParenthesis < 0)
            return string.Empty;

        var nestedParentheses = 0;
        var nestedBrackets = 0;
        var nestedBraces = 0;
        for (var index = openingParenthesis + 1; index < invocationText.Length; index++)
        {
            switch (invocationText[index])
            {
                case '(':
                    nestedParentheses++;
                    break;
                case ')':
                    if (nestedParentheses == 0 && nestedBrackets == 0 && nestedBraces == 0)
                        return invocationText[(openingParenthesis + 1)..index].Trim();
                    nestedParentheses = Math.Max(0, nestedParentheses - 1);
                    break;
                case '[':
                    nestedBrackets++;
                    break;
                case ']':
                    nestedBrackets = Math.Max(0, nestedBrackets - 1);
                    break;
                case '{':
                    nestedBraces++;
                    break;
                case '}':
                    nestedBraces = Math.Max(0, nestedBraces - 1);
                    break;
                case ',' when nestedParentheses == 0 && nestedBrackets == 0 && nestedBraces == 0:
                    return invocationText[(openingParenthesis + 1)..index].Trim();
            }
        }

        return string.Empty;
    }

    private static string? GetParserGuardPayloadIdentifier(string firstArgument)
    {
        string? fallback = null;
        for (var index = 0; index < firstArgument.Length;)
        {
            if (!IsParserGuardIdentifierStart(firstArgument[index]))
            {
                index++;
                continue;
            }

            var start = index++;
            while (index < firstArgument.Length && IsParserGuardIdentifierCharacter(firstArgument[index]))
                index++;
            var identifier = firstArgument[start..index].TrimStart('@');
            if (identifier is "await" or "default" or "in" or "new" or "ref" or "this")
                continue;
            fallback = identifier;
            if (identifier.Length > 0 && (char.IsLower(identifier[0]) || identifier[0] == '_'))
                return BoundParserGuardEvidenceIdentifier(identifier);
        }
        return fallback == null ? null : BoundParserGuardEvidenceIdentifier(fallback);
    }

    private static bool TryFindParserGuardBound(ParserGuardOperation operation, out string signal)
    {
        if (ContainsParserGuardBoundTerm(operation.InvocationText, "MaxDepth")
            || ContainsParserGuardBoundTerm(operation.InvocationText, "maxDepth"))
        {
            signal = "bound:max_depth_option";
            return true;
        }

        if (string.IsNullOrWhiteSpace(operation.PayloadIdentifier))
        {
            signal = string.Empty;
            return false;
        }

        var payload = operation.PayloadIdentifier;
        if (TryFindParserGuardValidationCall(operation.SourcePrefix, payload, out var validationCall))
        {
            signal = $"bound:validation_call:{validationCall}";
            return true;
        }
        if (TryFindParserGuardBoundedAssignment(operation.SourcePrefix, payload, out var boundedSource))
        {
            signal = $"bound:bounded_source:{boundedSource}";
            return true;
        }
        if (HasParserGuardSizeComparison(operation.SourcePrefix, payload))
        {
            signal = "bound:payload_size_comparison";
            return true;
        }

        signal = string.Empty;
        return false;
    }

    private static bool TryFindParserGuardValidationCall(string source, string payload, out string methodName)
    {
        foreach (var call in EnumerateParserGuardCalls(source))
        {
            if (!IsParserGuardValidationMethod(call.Name)
                || !ContainsParserGuardIdentifier(call.Arguments, payload)
                || !ContainsParserGuardBoundVocabulary(call.Name + " " + call.Arguments))
            {
                continue;
            }

            methodName = BoundParserGuardEvidenceIdentifier(call.Name);
            return true;
        }

        methodName = string.Empty;
        return false;
    }

    private static bool TryFindParserGuardBoundedAssignment(string source, string payload, out string methodName)
    {
        var payloadIndex = IndexOfParserGuardIdentifier(source, payload, 0);
        while (payloadIndex >= 0)
        {
            var statementStart = source.LastIndexOfAny([';', '{', '}'], Math.Max(0, payloadIndex - 1));
            var statementEnd = source.IndexOf(';', payloadIndex);
            if (statementEnd < 0)
                statementEnd = source.Length - 1;
            var statement = source[(statementStart + 1)..(statementEnd + 1)];
            var relativePayloadIndex = payloadIndex - statementStart - 1;
            var assignmentIndex = statement.IndexOf('=', Math.Max(0, relativePayloadIndex + payload.Length));
            if (assignmentIndex >= 0)
            {
                foreach (var call in EnumerateParserGuardCalls(statement[(assignmentIndex + 1)..]))
                {
                    if (call.Name.Contains("WithinLimit", StringComparison.OrdinalIgnoreCase)
                        || call.Name.Contains("Bounded", StringComparison.OrdinalIgnoreCase))
                    {
                        methodName = BoundParserGuardEvidenceIdentifier(call.Name);
                        return true;
                    }
                }
            }
            payloadIndex = IndexOfParserGuardIdentifier(source, payload, payloadIndex + payload.Length);
        }

        methodName = string.Empty;
        return false;
    }

    private static bool HasParserGuardSizeComparison(string source, string payload)
    {
        var payloadIndex = IndexOfParserGuardIdentifier(source, payload, 0);
        while (payloadIndex >= 0)
        {
            var statementStart = source.LastIndexOfAny([';', '{', '}'], Math.Max(0, payloadIndex - 1));
            var statementEnd = source.IndexOf(';', payloadIndex);
            if (statementEnd < 0)
                statementEnd = source.Length - 1;
            var statement = source[(statementStart + 1)..(statementEnd + 1)];
            if (ContainsParserGuardPayloadMemberComparison(statement, payload, "Length")
                || ContainsParserGuardPayloadMemberComparison(statement, payload, "LongLength")
                || ContainsParserGuardPayloadMemberComparison(statement, payload, "Count")
                || ContainsParserGuardByteCountComparison(statement, payload))
            {
                return true;
            }
            payloadIndex = IndexOfParserGuardIdentifier(source, payload, payloadIndex + payload.Length);
        }
        return false;
    }

    private static bool ContainsParserGuardPayloadMemberComparison(
        string statement,
        string payload,
        string member)
    {
        var index = IndexOfParserGuardIdentifier(statement, payload, 0);
        while (index >= 0)
        {
            var cursor = index + payload.Length;
            while (cursor < statement.Length && char.IsWhiteSpace(statement[cursor]))
                cursor++;
            if (cursor < statement.Length && statement[cursor] == '.')
            {
                cursor++;
                while (cursor < statement.Length && char.IsWhiteSpace(statement[cursor]))
                    cursor++;
                if (cursor + member.Length <= statement.Length
                    && statement.AsSpan(cursor, member.Length).Equals(member, StringComparison.Ordinal)
                    && (cursor + member.Length == statement.Length
                        || !IsParserGuardIdentifierCharacter(statement[cursor + member.Length])))
                {
                    var expressionEnd = cursor + member.Length;
                    if (HasParserGuardAdjacentRelationalComparison(statement, index, expressionEnd))
                        return true;
                }
            }
            index = IndexOfParserGuardIdentifier(statement, payload, index + payload.Length);
        }
        return false;
    }

    private static bool ContainsParserGuardByteCountComparison(string statement, string payload)
        => EnumerateParserGuardCalls(statement).Any(call =>
            call.Name.Contains("ByteCount", StringComparison.OrdinalIgnoreCase)
            && ContainsParserGuardIdentifier(call.Arguments, payload)
            && HasParserGuardAdjacentRelationalComparison(
                statement,
                call.NameStartIndex,
                call.ClosingParenthesisIndex + 1));

    private static bool HasParserGuardAdjacentRelationalComparison(
        string statement,
        int expressionStart,
        int expressionEnd)
    {
        var after = expressionEnd;
        while (after < statement.Length && char.IsWhiteSpace(statement[after]))
            after++;
        if (after < statement.Length
            && statement[after] is '<' or '>'
            && (after + 1 >= statement.Length || statement[after + 1] != statement[after]))
        {
            return true;
        }

        var before = expressionStart - 1;
        while (before >= 0 && char.IsWhiteSpace(statement[before]))
            before--;
        if (before >= 0 && statement[before] == '=')
            before--;
        if (before < 0 || statement[before] is not ('<' or '>'))
            return false;
        if (before > 0 && statement[before - 1] == statement[before])
            return false;
        if (statement[before] == '>' && before > 0 && statement[before - 1] == '=')
            return false;
        return true;
    }

    private static List<string> GetParserGuardStreamingSignals(ParserGuardOperation operation)
    {
        var signals = new List<string>();
        if (operation.OperationName.EndsWith("Async", StringComparison.Ordinal))
            signals.Add("streaming:async_parser_api");
        if (!string.IsNullOrWhiteSpace(operation.PayloadIdentifier)
            && operation.PayloadIdentifier.Contains("stream", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("streaming:stream_payload");
        }
        if (EnumerateParserGuardIdentifiers(operation.InvocationText).Any(identifier =>
                identifier.Contains("cancellationToken", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("streaming:cancellation_token");
        }
        return signals.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<ParserGuardCall> EnumerateParserGuardCalls(string source)
    {
        for (var index = 0; index < source.Length;)
        {
            if (!IsParserGuardIdentifierStart(source[index]))
            {
                index++;
                continue;
            }

            var start = index++;
            while (index < source.Length && IsParserGuardIdentifierCharacter(source[index]))
                index++;
            var name = source[start..index].TrimStart('@');
            var openingParenthesis = index;
            while (openingParenthesis < source.Length && char.IsWhiteSpace(source[openingParenthesis]))
                openingParenthesis++;
            if (openingParenthesis >= source.Length || source[openingParenthesis] != '(')
                continue;
            var closingParenthesis = FindParserGuardClosingParenthesis(source, openingParenthesis);
            if (closingParenthesis < 0)
                continue;
            yield return new ParserGuardCall(
                name,
                source[(openingParenthesis + 1)..closingParenthesis],
                start,
                closingParenthesis);
            index = openingParenthesis + 1;
        }
    }

    private static int FindParserGuardClosingParenthesis(string source, int openingParenthesis)
    {
        var depth = 0;
        for (var index = openingParenthesis; index < source.Length; index++)
        {
            if (source[index] == '(')
                depth++;
            else if (source[index] == ')' && --depth == 0)
                return index;
        }
        return -1;
    }

    private static IEnumerable<string> EnumerateParserGuardIdentifiers(string source)
    {
        for (var index = 0; index < source.Length;)
        {
            if (!IsParserGuardIdentifierStart(source[index]))
            {
                index++;
                continue;
            }
            var start = index++;
            while (index < source.Length && IsParserGuardIdentifierCharacter(source[index]))
                index++;
            yield return source[start..index].TrimStart('@');
        }
    }

    private static bool IsParserGuardValidationMethod(string name)
        => name.StartsWith("Validate", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Ensure", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Check", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Enforce", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Require", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ThrowIf", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsParserGuardBoundVocabulary(string value)
        => new[] { "bound", "byte", "capacity", "count", "depth", "length", "limit", "max", "size" }
            .Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsParserGuardBoundTerm(string value, string term)
        => value.Contains(term, StringComparison.Ordinal);

    private static bool ContainsParserGuardIdentifier(string value, string identifier)
        => IndexOfParserGuardIdentifier(value, identifier, 0) >= 0;

    private static string BoundParserGuardEvidenceIdentifier(string value)
        => value.Length <= ParserGuardEvidenceIdentifierLimit
            ? value
            : value[..ParserGuardEvidenceIdentifierLimit];

    private static int IndexOfParserGuardIdentifier(string value, string identifier, int startIndex)
    {
        var index = value.IndexOf(identifier, startIndex, StringComparison.Ordinal);
        while (index >= 0)
        {
            var beforeIsIdentifier = index > 0 && IsParserGuardIdentifierCharacter(value[index - 1]);
            var end = index + identifier.Length;
            var afterIsIdentifier = end < value.Length && IsParserGuardIdentifierCharacter(value[end]);
            if (!beforeIsIdentifier && !afterIsIdentifier)
                return index;
            index = value.IndexOf(identifier, end, StringComparison.Ordinal);
        }
        return -1;
    }

    private static bool IsParserGuardIdentifierStart(char value)
        => value == '@' || value == '_' || char.IsLetter(value);

    private static bool IsParserGuardIdentifierCharacter(char value)
        => value == '_' || char.IsLetterOrDigit(value);

    private sealed record ParserGuardMatchEvidence(
        string Category,
        string Operation,
        string? PayloadIdentifier,
        int Line,
        List<string> Signals);

    private sealed record ParserGuardOperation(
        string OperationName,
        string? PayloadIdentifier,
        string InvocationText,
        string SourcePrefix);

    private sealed record ParserGuardCall(
        string Name,
        string Arguments,
        int NameStartIndex,
        int ClosingParenthesisIndex);
}
