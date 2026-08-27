using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Mcp;
using CodeIndex.Models;
using CodeIndex.Security;

namespace CodeIndex.Lsp;

internal sealed partial class LspServer : IDisposable
{
    private List<DefinitionResult> ResolveLspDefinitions(PositionTokenContext context)
    {
        var localDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true, pathPatterns: [context.IndexedPath]);
        if (localDefinitions.Count > 0)
        {
            var positionDefinitions = FindDefinitionsAtPosition(localDefinitions, context);
            if (positionDefinitions.Count > 0)
                return positionDefinitions;

            var localReferenceTargets = ResolveReferenceTargetsAtPosition(
                context,
                out var authoritativeUnresolved);
            if (authoritativeUnresolved)
                return [];
            return localReferenceTargets.Count == 0 ? localDefinitions : localReferenceTargets;
        }

        var workspaceDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true);
        if (workspaceDefinitions.Count > 1
            || workspaceDefinitions.Any(IsCSharpLexicalLocalFunctionCandidate))
        {
            var referenceTargets = ResolveReferenceTargetsAtPosition(context);
            if (referenceTargets.Count > 0)
                return referenceTargets;
            if (workspaceDefinitions.All(IsCSharpLexicalLocalFunctionCandidate))
                return [];
        }
        return workspaceDefinitions;
    }

    private IReadOnlyList<ReferenceResult> ResolveLspReferences(PositionTokenContext context)
    {
        var localDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true, pathPatterns: [context.IndexedPath]);
        if (localDefinitions.Count > 0)
        {
            var positionDefinitions = FindDefinitionsAtPosition(localDefinitions, context);
            if (positionDefinitions.Count == 1)
                return _reader.GetReferencesForDefinition(positionDefinitions[0], DefaultLimit);

            var localReferenceTargets = ResolveReferenceTargetsAtPosition(
                context,
                out var authoritativeUnresolved);
            if (localReferenceTargets.Count == 1)
                return _reader.GetReferencesForDefinition(localReferenceTargets[0], DefaultLimit);
            if (localReferenceTargets.Count > 1
                && localReferenceTargets.All(IsCSharpLexicalLocalFunctionCandidate))
            {
                return GetReferencesForCSharpLexicalLocalFunctionTargets(localReferenceTargets);
            }
            if (authoritativeUnresolved)
                return [];

            return _reader.SearchReferences(
                context.Token,
                DefaultLimit,
                pathPatterns: [context.IndexedPath],
                exact: true);
        }

        var workspaceDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true);
        if (workspaceDefinitions.Count > 1
            || workspaceDefinitions.Any(IsCSharpLexicalLocalFunctionCandidate))
        {
            var referenceTargets = ResolveReferenceTargetsAtPosition(context);
            if (referenceTargets.Count == 1)
                return _reader.GetReferencesForDefinition(referenceTargets[0], DefaultLimit);
            if (referenceTargets.Count > 1
                && referenceTargets.All(IsCSharpLexicalLocalFunctionCandidate))
            {
                return GetReferencesForCSharpLexicalLocalFunctionTargets(referenceTargets);
            }
            if (workspaceDefinitions.All(IsCSharpLexicalLocalFunctionCandidate))
                return [];
        }

        if (workspaceDefinitions.Count == 0 || !HasSingleLspDefinitionTarget(workspaceDefinitions))
            return _reader.AnalyzeSymbol(context.Token, DefaultLimit, pathPatterns: [context.IndexedPath], exact: true).References;

        return _reader.AnalyzeSymbol(context.Token, DefaultLimit, exact: true).References;
    }

    private DefinitionResult? ResolveReferenceTargetAtPosition(PositionTokenContext context)
    {
        var targets = ResolveReferenceTargetsAtPosition(context);
        return targets.Count == 1 ? targets[0] : null;
    }

    private List<DefinitionResult> ResolveReferenceTargetsAtPosition(PositionTokenContext context)
        => ResolveReferenceTargetsAtPosition(context, out _);

    private List<DefinitionResult> ResolveReferenceTargetsAtPosition(
        PositionTokenContext context,
        out bool authoritativeUnresolved)
    {
        var resolution = _reader.GetReferencePositionResolution(
            context.IndexedPath,
            context.Token,
            context.Line + 1,
            context.StartCharacter + 1,
            MaxReferencePositionCandidates);
        // Only extraction-owned shadow/incomplete markers are authoritative negative evidence;
        // ordinary zero-candidate rows still need the legacy same-name fallback.
        // extraction所有のshadow/incomplete markerだけを権威ある否定根拠とし、通常の候補0件rowは
        // 従来どおり同名fallbackを許可する。
        authoritativeUnresolved = resolution.IdentityAvailable
            && !resolution.CandidatesTruncated
            && resolution.ExplicitNegativeEvidence;
        if (!resolution.IdentityAvailable || resolution.CandidatesTruncated)
            return [];

        var selected = resolution.Candidates
            .Where(candidate => candidate.Authoritative)
            .Take(2)
            .ToList();
        if (selected.Count == 1)
        {
            var authoritativeDefinition = _reader.GetDefinitionForSymbol(selected[0].Definition);
            return authoritativeDefinition == null ? [] : [authoritativeDefinition];
        }

        if (TryGetCSharpInvocationArgumentCount(context, out var argumentCount))
        {
            selected = resolution.Candidates
                .Where(candidate => TryGetCSharpDefinitionParameterCount(candidate.Definition, out var parameterCount) &&
                    parameterCount == argumentCount)
                .ToList();
            if (selected.Count > 0)
            {
                return selected
                    .Select(candidate => _reader.GetDefinitionForSymbol(candidate.Definition))
                    .OfType<DefinitionResult>()
                    .OrderBy(definition => definition.Path, StringComparer.Ordinal)
                    .ThenBy(definition => definition.StartLine)
                    .ToList();
            }
        }

        if (resolution.Candidates.Count == 1)
        {
            var onlyDefinition = _reader.GetDefinitionForSymbol(resolution.Candidates[0].Definition);
            return onlyDefinition == null ? [] : [onlyDefinition];
        }

        // Generic candidate paths exclude lexical local functions, so a persisted all-local
        // candidate set is the winning lexical overload family even without invocation arity.
        // 汎用candidate経路は字句local functionを除外するため、全候補がlocalなら、invocation
        // arityが無いmethod groupでも永続化済み集合が勝者の字句overload familyとなる。
        if (resolution.Candidates.Count > 0
            && resolution.Candidates.All(candidate =>
                IsCSharpLexicalLocalFunctionCandidate(candidate.Definition)))
        {
            return resolution.Candidates
                .Select(candidate => _reader.GetDefinitionForSymbol(candidate.Definition))
                .OfType<DefinitionResult>()
                .OrderBy(definition => definition.Path, StringComparer.Ordinal)
                .ThenBy(definition => definition.StartLine)
                .ToList();
        }

        var typeFamilyKeys = resolution.Candidates
            .Select(candidate =>
                LogicalPartialSymbolGrouper.TryBuildTypeFamilyKeyForReferenceResolution(
                    candidate.Definition,
                    out var key)
                    ? key
                    : null)
            .Where(static key => key != null)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();
        if (typeFamilyKeys.Count != 1 ||
            resolution.Candidates.Any(candidate =>
                !LogicalPartialSymbolGrouper.TryBuildTypeFamilyKeyForReferenceResolution(
                    candidate.Definition,
                    out _)))
        {
            return [];
        }

        return resolution.Candidates
            .Select(candidate => _reader.GetDefinitionForSymbol(candidate.Definition))
            .OfType<DefinitionResult>()
            .OrderBy(definition => definition.Path, StringComparer.Ordinal)
            .ThenBy(definition => definition.StartLine)
            .ToList();
    }

    private static bool IsCSharpLexicalLocalFunctionCandidate(SymbolResult definition) =>
        string.Equals(definition.Lang, "csharp", StringComparison.OrdinalIgnoreCase)
        && definition.Kind == "function"
        && (definition.ContainerKind is "function" or "test.method" or "lambda" or "property"
            || (definition.ContainerKind == null
                && definition.ContainerName == null
                && definition.ContainerQualifiedName == null));

    private IReadOnlyList<ReferenceResult> GetReferencesForCSharpLexicalLocalFunctionTargets(
        IReadOnlyList<DefinitionResult> definitions) =>
        definitions
            .SelectMany(definition =>
                _reader.GetReferencesForDefinition(definition, DefaultLimit))
            .GroupBy(reference => (
                reference.Path,
                reference.Line,
                reference.Column,
                reference.ReferenceKind))
            .Select(group => group.First())
            .OrderBy(reference => reference.Path, StringComparer.Ordinal)
            .ThenBy(reference => reference.Line)
            .ThenBy(reference => reference.Column)
            .Take(DefaultLimit)
            .ToList();

    private bool TryGetCSharpInvocationArgumentCount(PositionTokenContext context, out int argumentCount)
    {
        argumentCount = 0;
        if (!TryReadPositionLine(context.ResolvedPath, context.Line, out var sourceLine, out _))
            return false;

        var openParenthesis = context.EndCharacter;
        while (openParenthesis < sourceLine.Length && char.IsWhiteSpace(sourceLine[openParenthesis]))
            openParenthesis++;
        return openParenthesis < sourceLine.Length &&
            sourceLine[openParenthesis] == '(' &&
            TryCountCommaSeparatedItems(sourceLine, openParenthesis, allowAngleBrackets: false, out argumentCount);
    }

    private static bool TryGetCSharpDefinitionParameterCount(SymbolResult definition, out int parameterCount)
    {
        parameterCount = 0;
        if (!string.Equals(definition.Lang, "csharp", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(definition.Signature))
        {
            return false;
        }

        var count = CSharpTypeReferenceArity.GetConstructorParameterCount(
            definition.Signature,
            definition.Name,
            definition.Kind);
        if (!count.HasValue)
        {
            if (!string.Equals(definition.Kind, "function", StringComparison.Ordinal))
                return false;
            var nameStart = FindIdentifierOccurrence(definition.Signature, definition.Name, 0);
            if (nameStart < 0)
                return false;
            var openParenthesis = definition.Signature.IndexOf(
                '(',
                nameStart + definition.Name.Length);
            return openParenthesis >= 0 &&
                TryCountCommaSeparatedItems(
                    definition.Signature,
                    openParenthesis,
                    allowAngleBrackets: true,
                    out parameterCount);
        }

        parameterCount = count.Value;
        return true;
    }

    private static bool TryCountCommaSeparatedItems(
        string text,
        int openParenthesis,
        bool allowAngleBrackets,
        out int itemCount)
    {
        itemCount = 0;
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;
        var hasItemContent = false;
        for (var index = openParenthesis + 1; index < text.Length; index++)
        {
            var value = text[index];
            if (value is '\'' or '"' ||
                (value == '/' && index + 1 < text.Length && text[index + 1] is '/' or '*'))
            {
                return false;
            }

            switch (value)
            {
                case '(':
                    parenthesisDepth++;
                    hasItemContent = true;
                    break;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    hasItemContent = true;
                    break;
                case ')' when bracketDepth == 0 && braceDepth == 0 && angleDepth == 0:
                    itemCount = hasItemContent ? itemCount + 1 : 0;
                    return true;
                case '[':
                    bracketDepth++;
                    hasItemContent = true;
                    break;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    hasItemContent = true;
                    break;
                case '{':
                    braceDepth++;
                    hasItemContent = true;
                    break;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    hasItemContent = true;
                    break;
                case '<' when allowAngleBrackets:
                    angleDepth++;
                    hasItemContent = true;
                    break;
                case '>' when allowAngleBrackets && angleDepth > 0:
                    angleDepth--;
                    hasItemContent = true;
                    break;
                case '<' or '>':
                    return false;
                case ',' when parenthesisDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0:
                    if (!hasItemContent)
                        return false;
                    itemCount++;
                    hasItemContent = false;
                    break;
                default:
                    hasItemContent |= !char.IsWhiteSpace(value);
                    break;
            }
        }

        return false;
    }

    private List<DefinitionResult> FindDefinitionsAtPosition(
        List<DefinitionResult> definitions,
        PositionTokenContext context)
    {
        var sourceLine = context.Line + 1;
        return definitions.Where(definition =>
        {
            var identifier = GetSymbolIdentifierPosition(definition, context.ResolvedPath);
            if (identifier.Line != sourceLine)
                return false;

            var definitionStart = identifier.StartColumn - 1;
            var definitionEnd = identifier.EndColumn - 1;
            return context.StartCharacter < definitionEnd && context.EndCharacter > definitionStart;
        }).ToList();
    }

    private static bool HasSingleLspDefinitionTarget(IReadOnlyList<DefinitionResult> definitions)
    {
        if (definitions.Count <= 1)
            return true;

        var firstKey = BuildLspDefinitionTargetKey(definitions[0]);
        return definitions.Skip(1).All(definition => string.Equals(BuildLspDefinitionTargetKey(definition), firstKey, StringComparison.Ordinal));
    }

    private static string BuildLspDefinitionTargetKey(DefinitionResult definition)
        => string.Join('\0', definition.Path, definition.Kind, definition.ContainerKind, definition.ContainerName, definition.Name);

    private bool TryExtractPositionToken(JsonElement root, out PositionTokenContext context, out string? failureReason)
    {
        context = default;
        failureReason = null;
        var path = GetDocumentPath(root);
        var position = ReadRequiredLspPosition(root, "params", "position");
        var line = position.Line;
        var character = position.Character;

        if (!TryResolveDocumentPath(path, out var resolvedPath, out var projectRelativePath, out var workspaceRoot, out failureReason))
            return false;

        var indexedPath = ResolveIndexedPath(path, resolvedPath, projectRelativePath, workspaceRoot);
        if (indexedPath == null)
        {
            failureReason = FailureFileNotIndexed;
            return false;
        }

        var indexedPathRoot = _projectRoot == null ? workspaceRoot : null;
        if (!TryResolveIndexedFilePath(indexedPath, indexedPathRoot, out var indexedFullPath))
        {
            failureReason = FailureIndexedFileUnresolved;
            return false;
        }

        if (!string.Equals(resolvedPath, indexedFullPath, _pathStringComparison))
        {
            failureReason = FailurePathCasingMismatch;
            return false;
        }

        if (!TryReadPositionLine(indexedFullPath, line, out var sourceLine, out failureReason))
            return false;

        var token = ExtractTokenAtUtf16Position(sourceLine, character);
        if (string.IsNullOrWhiteSpace(token))
        {
            failureReason = FailureNoTokenAtPosition;
            return false;
        }

        var (startCharacter, endCharacter) = FindTokenRangeAtUtf16Position(sourceLine, character);
        context = new PositionTokenContext(token, indexedFullPath, indexedPath, workspaceRoot, line, startCharacter, endCharacter);
        return true;
    }

    private bool TryResolveIndexedDocument(JsonElement root, out IndexedDocumentContext context)
    {
        context = default;
        var documentPath = GetDocumentPath(root);
        if (!TryResolveDocumentPath(documentPath, out var resolvedPath, out var projectRelativePath, out var workspaceRoot))
            return false;

        var indexedPath = ResolveIndexedPath(documentPath, resolvedPath, projectRelativePath, workspaceRoot);
        if (indexedPath == null)
            return false;

        var indexedPathRoot = _projectRoot == null ? workspaceRoot : null;
        if (!TryResolveIndexedFilePath(indexedPath, indexedPathRoot, out var indexedFullPath))
            return false;

        if (!string.Equals(resolvedPath, indexedFullPath, _pathStringComparison))
            return false;

        context = new IndexedDocumentContext(documentPath, resolvedPath, indexedPath, workspaceRoot);
        return true;
    }

    private List<SymbolResult> GetDocumentSymbols(string indexedPath, int limit, int? startLine = null, int? endLine = null)
        => _reader.SearchSymbols((string?)null, limit, pathPatterns: [indexedPath], startLine: startLine, endLine: endLine)
            .OrderBy(s => s.StartLine)
            .ThenByDescending(s => s.EndLine)
            .ThenBy(s => s.ContainerName == null ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    private bool TryReadPositionLine(string path, int targetLine, out string sourceLine, out string? failureReason)
    {
        if (_liveDocumentStore.TryGetText(Path.GetFullPath(path), out var liveText))
            return TryReadPositionLineFromText(liveText, targetLine, out sourceLine, out failureReason);

        return TryReadPositionLineFromFile(path, targetLine, out sourceLine, out failureReason);
    }

    private bool TryReadPositionLineCached(
        string path,
        int targetLine,
        Dictionary<int, string?>? lineCache,
        out string sourceLine)
    {
        if (targetLine < 0)
        {
            sourceLine = string.Empty;
            return false;
        }

        if (lineCache != null && lineCache.TryGetValue(targetLine, out var cachedLine))
        {
            sourceLine = cachedLine ?? string.Empty;
            return cachedLine != null;
        }

        if (lineCache is { Count: 0 } && TryReadAllPositionLines(path, out var sourceLines))
        {
            for (var line = 0; line < sourceLines.Count; line++)
                lineCache[line] = sourceLines[line];

            if (lineCache.TryGetValue(targetLine, out cachedLine))
            {
                sourceLine = cachedLine ?? string.Empty;
                return cachedLine != null;
            }

            sourceLine = string.Empty;
            return false;
        }

        var found = TryReadPositionLine(path, targetLine, out sourceLine, out _);
        if (lineCache != null)
            lineCache[targetLine] = found ? sourceLine : null;
        return found;
    }

    private bool TryReadAllPositionLines(string path, out IReadOnlyList<string?> sourceLines)
    {
        sourceLines = [];
        if (_liveDocumentStore.TryGetText(Path.GetFullPath(path), out var liveText))
        {
            if (Encoding.UTF8.GetByteCount(liveText) > MaxPositionDocumentBytes)
                return false;
            sourceLines = SplitPositionLines(liveText);
            return true;
        }

        return TryReadAllPositionLinesFromFile(path, out sourceLines, out _);
    }

    internal static bool TryReadAllPositionLinesFromFile(
        string path,
        out IReadOnlyList<string?> sourceLines,
        out string? failureReason)
    {
        sourceLines = [];
        failureReason = null;
        try
        {
            using var stream = BoundedFile.OpenReadForLengthCheckedText(path);
            if (stream.Length > MaxPositionDocumentBytes)
            {
                failureReason = FailurePositionFileTooLarge;
                return false;
            }

            PositionFileLengthCheckedForTesting?.Invoke(path);
            using var boundedStream = new PositionFileReadStream(stream, MaxPositionDocumentBytes);
            using var reader = new StreamReader(
                boundedStream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: BoundedFile.SmallReadBufferSize);
            sourceLines = ReadPositionLines(reader);
            return true;
        }
        catch (PositionFileTooLargeException)
        {
            failureReason = FailurePositionFileTooLarge;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            failureReason = FailurePositionFileUnreadable;
            return false;
        }
    }

    private static IReadOnlyList<string?> ReadPositionLines(TextReader reader)
    {
        var lines = new List<string?>();
        var line = new StringBuilder();
        var lineLength = 0;
        var lineTooLong = false;
        var previousWasCarriageReturn = false;
        var buffer = new char[4096];
        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;

            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (previousWasCarriageReturn)
                {
                    previousWasCarriageReturn = false;
                    if (value == '\n')
                        continue;
                }

                if (value is '\r' or '\n')
                {
                    lines.Add(lineTooLong ? null : line.ToString());
                    line.Clear();
                    lineLength = 0;
                    lineTooLong = false;
                    previousWasCarriageReturn = value == '\r';
                    continue;
                }

                lineLength++;
                if (lineLength <= MaxPositionLineChars)
                    line.Append(value);
                else if (!lineTooLong)
                {
                    line.Clear();
                    lineTooLong = true;
                }
            }
        }

        lines.Add(lineTooLong ? null : line.ToString());
        return lines;
    }

    private static IReadOnlyList<string?> SplitPositionLines(string text)
    {
        using var reader = new StringReader(text);
        return ReadPositionLines(reader);
    }

    private sealed class PositionFileTooLargeException : IOException
    {
    }

    private sealed class PositionFileReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _remaining = maxBytes;
        private bool _disposed;

        public override bool CanRead => !_disposed && inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_remaining > 0)
            {
                var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
                _remaining -= read;
                return read;
            }

            return ProbeForOverflow();
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_remaining > 0)
            {
                var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
                _remaining -= read;
                return read;
            }

            return ProbeForOverflow();
        }

        private int ProbeForOverflow()
        {
            Span<byte> probe = stackalloc byte[1];
            if (inner.Read(probe) != 0)
                throw new PositionFileTooLargeException();
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }

    private static bool TryReadPositionLineFromText(string text, int targetLine, out string sourceLine, out string? failureReason)
    {
        sourceLine = string.Empty;
        failureReason = null;
        if (targetLine < 0)
        {
            failureReason = FailureInvalidPosition;
            return false;
        }

        var currentLine = 0;
        var lineStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            var isLineBreak = !atEnd && (text[i] == '\r' || text[i] == '\n');
            if (!atEnd && !isLineBreak)
                continue;

            if (currentLine == targetLine)
            {
                var length = i - lineStart;
                if (length > MaxPositionLineChars)
                {
                    failureReason = FailurePositionLineTooLong;
                    return false;
                }

                sourceLine = text.Substring(lineStart, length);
                return true;
            }

            if (atEnd)
                break;

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            currentLine++;
            lineStart = i + 1;
        }

        failureReason = FailurePositionLineMissing;
        return false;
    }

    private static bool TryReadPositionLineFromFile(string path, int targetLine, out string sourceLine, out string? failureReason)
    {
        sourceLine = string.Empty;
        failureReason = null;
        try
        {
            using var stream = BoundedFile.OpenReadForLengthCheckedText(path);
            if (stream.Length > MaxPositionDocumentBytes)
            {
                failureReason = FailurePositionFileTooLarge;
                return false;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var currentLine = 0;
            var currentLineLength = 0;
            StringBuilder? builder = targetLine == 0 ? new StringBuilder() : null;
            while (true)
            {
                var next = reader.Read();
                if (stream.Position > MaxPositionDocumentBytes)
                {
                    failureReason = FailurePositionFileTooLarge;
                    return false;
                }

                if (next < 0)
                {
                    if (currentLine == targetLine && currentLineLength <= MaxPositionLineChars && builder != null)
                    {
                        sourceLine = builder.ToString();
                        return true;
                    }

                    failureReason = FailurePositionLineMissing;
                    return false;
                }

                var c = (char)next;
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && reader.Peek() == '\n')
                    {
                        reader.Read();
                        if (stream.Position > MaxPositionDocumentBytes)
                        {
                            failureReason = FailurePositionFileTooLarge;
                            return false;
                        }
                    }

                    if (currentLine == targetLine)
                    {
                        sourceLine = builder?.ToString() ?? string.Empty;
                        return true;
                    }

                    currentLine++;
                    currentLineLength = 0;
                    builder = currentLine == targetLine ? new StringBuilder() : null;
                    continue;
                }

                currentLineLength++;
                if (currentLineLength > MaxPositionLineChars)
                {
                    if (currentLine == targetLine)
                    {
                        failureReason = FailurePositionLineTooLong;
                        return false;
                    }
                    continue;
                }

                builder?.Append(c);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            failureReason = FailurePositionFileUnreadable;
            return false;
        }
    }

    internal static string? ExtractTokenAtUtf16Position(string line, int character)
    {
        if (character < 0)
            return null;
        var index = Math.Min(character, line.Length);
        while (index > 0 && index == line.Length)
            index--;
        if (index < line.Length && !IsTokenChar(line[index]) && index > 0 && IsTokenChar(line[index - 1]))
            index--;
        if (index >= line.Length || !IsTokenChar(line[index]))
            return null;

        var start = index;
        while (start > 0 && IsTokenChar(line[start - 1]))
            start--;
        var end = index + 1;
        while (end < line.Length && IsTokenChar(line[end]))
            end++;
        return line[start..end].TrimStart('@');
    }

    private static (int Start, int End) FindTokenRangeAtUtf16Position(string line, int character)
    {
        if (character < 0)
            return (0, 0);
        var index = Math.Min(character, line.Length);
        while (index > 0 && index == line.Length)
            index--;
        if (index < line.Length && !IsTokenChar(line[index]) && index > 0 && IsTokenChar(line[index - 1]))
            index--;
        if (index >= line.Length || !IsTokenChar(line[index]))
            return (Math.Max(0, Math.Min(character, line.Length)), Math.Max(0, Math.Min(character, line.Length)));

        var start = index;
        while (start > 0 && IsTokenChar(line[start - 1]))
            start--;
        var end = index + 1;
        while (end < line.Length && IsTokenChar(line[end]))
            end++;
        return (start, end);
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

    private bool MatchesDocumentPath(string indexedPath, string documentPath, string? projectRelativePath, string resolvedPath, string? workspaceRoot)
    {
        if (TryResolveIndexedFilePath(indexedPath, null, out var indexedFullPath)
            && string.Equals(resolvedPath, indexedFullPath, _pathStringComparison))
            return true;

        if (Path.IsPathRooted(indexedPath))
            return false;

        var normalizedIndexed = indexedPath.Replace('\\', '/');
        if (projectRelativePath != null)
            return _projectRoot == null
                && workspaceRoot != null
                && string.Equals(normalizedIndexed, projectRelativePath.Replace('\\', '/'), _pathStringComparison);

        if (string.Equals(indexedPath, documentPath, StringComparison.Ordinal))
            return true;

        if (_projectRoot == null && workspaceRoot == null)
            return false;

        var normalizedDocument = documentPath.Replace('\\', '/');
        return normalizedDocument.EndsWith("/" + normalizedIndexed, StringComparison.Ordinal);
    }

    private string? ResolveIndexedPath(string documentPath)
    {
        if (!TryResolveDocumentPath(documentPath, out var resolvedPath, out var projectRelativePath, out var workspaceRoot))
            return null;

        return ResolveIndexedPath(documentPath, resolvedPath, projectRelativePath, workspaceRoot);
    }

    private string? ResolveIndexedPath(string documentPath, string resolvedPath, string? projectRelativePath, string? workspaceRoot)
    {
        if (projectRelativePath != null)
        {
            var exactPath = projectRelativePath.Replace('\\', '/');
            var exactFile = _reader.GetFileByPath(exactPath);
            if (exactFile != null && MatchesDocumentPath(exactFile.Path, documentPath, projectRelativePath, resolvedPath, workspaceRoot))
                return exactFile.Path;
        }

        var fileName = Path.GetFileName(documentPath);
        if (string.IsNullOrEmpty(fileName))
            fileName = Path.GetFileName(resolvedPath);
        if (string.IsNullOrEmpty(fileName))
            return null;

        var files = _reader.ListFiles(fileName, MaxDocumentPathFallbackCandidates);
        var matches = files
            .Where(file => MatchesDocumentPath(file.Path, documentPath, projectRelativePath, resolvedPath, workspaceRoot))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0].Path : null;
    }

    private bool TryResolveDocumentPath(string documentPath, out string resolvedPath, out string? projectRelativePath) =>
        TryResolveDocumentPath(documentPath, out resolvedPath, out projectRelativePath, out _, out _);

    private bool TryResolveDocumentPath(
        string documentPath,
        out string resolvedPath,
        out string? projectRelativePath,
        out string? workspaceRoot) =>
        TryResolveDocumentPath(documentPath, out resolvedPath, out projectRelativePath, out workspaceRoot, out _);

    private bool TryResolveDocumentPath(
        string documentPath,
        out string resolvedPath,
        out string? projectRelativePath,
        out string? workspaceRoot,
        out string? failureReason)
    {
        resolvedPath = string.Empty;
        projectRelativePath = null;
        workspaceRoot = null;
        failureReason = null;
        try
        {
            resolvedPath = Path.IsPathRooted(documentPath)
                ? Path.GetFullPath(documentPath)
                : Path.GetFullPath(documentPath, _projectRoot ?? Environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            failureReason = FailureDocumentPathUnresolved;
            return false;
        }

        if (_workspaceFolders.Count == 0)
            return true;

        if (TryGetWorkspaceRelativePath(resolvedPath, out projectRelativePath, out workspaceRoot))
            return true;

        failureReason = FailureOutsideProject;
        return false;
    }

    private bool TryResolveIndexedFilePath(string indexedPath, out string resolvedPath)
        => TryResolveIndexedFilePath(indexedPath, null, out resolvedPath);

    private bool TryResolveIndexedFilePath(string indexedPath, string? workspaceRoot, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            resolvedPath = Path.IsPathRooted(indexedPath)
                ? Path.GetFullPath(indexedPath)
                : Path.GetFullPath(indexedPath, workspaceRoot ?? _projectRoot ?? Environment.CurrentDirectory);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryGetWorkspaceRelativePath(string resolvedPath, out string? relativePath, out string? workspaceRoot)
    {
        relativePath = null;
        workspaceRoot = null;
        foreach (var candidateRoot in _workspaceFolders)
        {
            if (!TryGetRelativePath(candidateRoot, resolvedPath, out var candidateRelativePath))
                continue;

            relativePath = candidateRelativePath;
            workspaceRoot = candidateRoot;
            return true;
        }

        return false;
    }

    private static bool TryGetRelativePath(string root, string resolvedPath, out string? relativePath)
    {
        relativePath = null;
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedPath = Path.GetFullPath(resolvedPath);
            if (PathCasing.PathsEqual(normalizedRoot, normalizedPath)
                || !PathCasing.IsPathEqualOrParent(normalizedRoot, normalizedPath))
            {
                return false;
            }

            var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
            if (relative == "."
                || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                return false;
            }

            relativePath = relative;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

}
