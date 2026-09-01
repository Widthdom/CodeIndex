using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private sealed class CoreReferenceLineContainerResolver
    {
        private readonly CoreReferenceLoopContext _loop;
        private int _lineIndex;
        private int _lineNumber;
        private SymbolRecord? _container;
        private bool _csharpLineHasWhereClause;
        private (
            SymbolRecord Synthetic,
            int NameIndex,
            int OpenBraceIndex,
            int CloseBraceIndex)? _javaSameLineCtor;

        internal CoreReferenceLineContainerResolver(CoreReferenceLoopContext loop)
        {
            _loop = loop;
            ResolveContainerForCall = ResolveContainer;
            ResolveSwiftPropertyContainerForCall =
                ResolveSwiftPropertyContainer;
        }

        // One resolver belongs to one CoreReferenceLoopState. The extraction
        // loop resets it immediately before processing a non-empty line, and
        // all consumers invoke these bound delegates synchronously before the
        // next reset. The delegates must not escape the prepared line.
        internal void ResetForLine(
            int lineIndex,
            int lineNumber,
            SymbolRecord? container,
            bool csharpLineHasWhereClause,
            (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex,
                int CloseBraceIndex)? javaSameLineCtor)
        {
            _lineIndex = lineIndex;
            _lineNumber = lineNumber;
            _container = container;
            _csharpLineHasWhereClause = csharpLineHasWhereClause;
            _javaSameLineCtor = javaSameLineCtor;
        }

        internal Func<int, SymbolRecord?> ResolveContainerForCall { get; }

        internal Func<int, SymbolRecord?> ResolveSwiftPropertyContainerForCall
        {
            get;
        }

        private SymbolRecord? ResolveContainer(int column)
        {
            var language = _loop.Request.Language;
            if (language == "csharp")
            {
                var recordContainer = ResolveCSharpRecordContainer(column);
                if (recordContainer.IsResolved)
                    return recordContainer.Container;
            }

            if (_javaSameLineCtor != null)
            {
                var info = _javaSameLineCtor.Value;
                if (info.CloseBraceIndex >= 0
                    && column > info.OpenBraceIndex
                    && column < info.CloseBraceIndex)
                {
                    return info.Synthetic;
                }
            }

            if (language == "csharp")
            {
                if (_csharpLineHasWhereClause)
                {
                    var declarationRangeContainer =
                        FindInnermostCSharpDeclarationRangeContainer(
                            _loop.ContainerCandidates,
                            _loop.Preparation.StructuralLines[_lineIndex],
                            _lineNumber,
                            column);
                    if (declarationRangeContainer != null)
                        return declarationRangeContainer;
                }

                var sameLineContainer =
                    FindInnermostSameLineCSharpContainer(
                        _loop.Lookups
                            .GetCSharpSameLineContainerCandidatesByLine(),
                        _loop.Preparation.StructuralLines[_lineIndex],
                        _lineNumber,
                        column);
                if (sameLineContainer != null)
                    return sameLineContainer;

                if (_container is { StartLine: var startLine, EndLine: var endLine }
                    && startLine == _lineNumber
                    && endLine == _lineNumber
                    && _container.SubKind != SyntheticSymbolIdentity.CSharpTopLevelScopeSubKind)
                {
                    var topLevelContainer = _loop.ContainerCandidates.FirstOrDefault(candidate =>
                        candidate.SubKind == SyntheticSymbolIdentity.CSharpTopLevelScopeSubKind
                        && candidate.BodyStartLine <= _lineNumber
                        && candidate.BodyEndLine >= _lineNumber);
                    if (topLevelContainer != null)
                        return topLevelContainer;
                }

                if (_csharpLineHasWhereClause
                    && _container?.Kind == "function"
                    && _container.StartLine == _lineNumber
                    && (!TryFindCSharpFunctionNameColumn(
                            _loop.Preparation.StructuralLines[_lineIndex],
                            _container.Name,
                            out var containerNameColumn)
                        || column < containerNameColumn))
                {
                    return null;
                }
            }

            return _loop.DynamicDeclarativeState?.ResolveContainer(
                _lineNumber,
                column,
                _container) ?? _container;
        }

        private (bool IsResolved, SymbolRecord? Container)
            ResolveCSharpRecordContainer(int column)
        {
            if (TryResolveAfterCSharpSemicolonRecord(
                    _container,
                    column,
                    out var enclosingContainer))
                return (true, enclosingContainer);

            SymbolRecord? primaryCtorOwner = null;
            foreach (var (
                         rangeStart,
                         rangeStartColumn,
                         rangeEnd,
                         rangeEndColumn,
                         syntheticRecordCtor,
                         owner) in
                     _loop.Lookups.GetRecordPrimaryCtorRanges())
            {
                if (ReferenceEquals(_container, syntheticRecordCtor)
                    || (_container?.Kind == "function"
                        && _container.FileId == syntheticRecordCtor.FileId
                        && _container.StartLine == syntheticRecordCtor.StartLine
                        && (_container.StartLine < rangeEnd
                            || (_container.StartColumn
                                    is int containerStartColumn
                                && containerStartColumn < rangeEndColumn))
                        && string.Equals(
                            _container.Name,
                            syntheticRecordCtor.Name,
                            StringComparison.Ordinal)))
                {
                    primaryCtorOwner ??= owner;
                }
                if (_lineNumber < rangeStart || _lineNumber > rangeEnd)
                    continue;
                if (_lineNumber == rangeStart && column < rangeStartColumn)
                    return (true, owner);
                if (_lineNumber == rangeEnd && column >= rangeEndColumn)
                    continue;
                return (true, syntheticRecordCtor);
            }

            if (primaryCtorOwner != null)
            {
                if (TryResolveAfterCSharpSemicolonRecord(
                        primaryCtorOwner,
                        column,
                        out enclosingContainer))
                {
                    return (true, enclosingContainer);
                }

                return (true, primaryCtorOwner);
            }

            return (false, null);
        }

        private bool TryResolveAfterCSharpSemicolonRecord(
            SymbolRecord? candidate,
            int column,
            out SymbolRecord? enclosingContainer)
        {
            enclosingContainer = null;
            if (candidate is not { Kind: "class" or "struct" } recordOwner)
                return false;
            if (string.IsNullOrWhiteSpace(recordOwner.Signature)
                || !CSharpRecordDeclarationSignatureRegex.IsMatch(recordOwner.Signature))
            {
                return false;
            }

            var structuralLines = _loop.Preparation.StructuralLines;
            var (endLine, endColumn, isRecordDeclaration) =
                _loop.Lookups.GetCSharpRecordHeaderBoundary(recordOwner);
            if (endLine != _lineNumber
                || endColumn == int.MaxValue
                || endLine <= 0
                || endLine > structuralLines.Length
                || endColumn < 0
                || endColumn >= structuralLines[endLine - 1].Length
                || structuralLines[endLine - 1][endColumn] != ';'
                || column <= endColumn
                || !isRecordDeclaration)
            {
                return false;
            }

            enclosingContainer = FindInnermostSameLineCSharpContainer(
                    _loop.Lookups.GetCSharpSameLineContainerCandidatesByLine(),
                    structuralLines[_lineIndex],
                    _lineNumber,
                    column,
                    recordOwner)
                ?? FindCSharpDeclaredParentContainer(recordOwner);
            return true;
        }

        private SymbolRecord? FindCSharpDeclaredParentContainer(SymbolRecord owner)
        {
            SymbolRecord? best = null;
            var bestSpan = int.MaxValue;
            foreach (var candidate in _loop.ContainerCandidates)
            {
                if (ReferenceEquals(candidate, owner)
                    || candidate.FileId != owner.FileId
                    || !string.Equals(candidate.Kind, owner.ContainerKind, StringComparison.Ordinal)
                    || !string.Equals(candidate.Name, owner.ContainerName, StringComparison.Ordinal)
                    || candidate.BodyStartLine is not int bodyStart
                    || candidate.BodyEndLine is not int bodyEnd
                    || bodyStart > owner.StartLine
                    || bodyEnd < owner.EndLine)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(owner.ContainerQualifiedName)
                    && !string.Equals(
                        CombineQualifiedName(candidate.ContainerQualifiedName, candidate.Name),
                        owner.ContainerQualifiedName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var span = bodyEnd - bodyStart;
                if (best == null || span < bestSpan)
                {
                    best = candidate;
                    bestSpan = span;
                }
            }

            return best ?? _loop.ContainerCandidates.FirstOrDefault(candidate =>
                candidate.SubKind == SyntheticSymbolIdentity.CSharpTopLevelScopeSubKind
                && candidate.BodyStartLine <= _lineNumber
                && candidate.BodyEndLine >= _lineNumber);
        }

        private SymbolRecord? ResolveSwiftPropertyContainer(int column)
        {
            if (_loop.SwiftPropertyDefinitionsByLine != null
                && _loop.SwiftPropertyDefinitionsByLine.TryGetValue(
                    _lineNumber,
                    out var sameLineProperties))
            {
                foreach (var property in sameLineProperties)
                {
                    if ((property.StartColumn ?? 0) <= column)
                        return property;
                }
            }

            return ResolveContainer(column);
        }
    }
}
