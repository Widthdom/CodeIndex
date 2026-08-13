using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private sealed class CoreReferenceLineContainerResolver
    {
        private readonly CoreReferenceLoopContext _loop;
        private readonly int _lineIndex;
        private readonly int _lineNumber;
        private readonly SymbolRecord? _container;
        private readonly bool _csharpLineHasWhereClause;
        private readonly (
            SymbolRecord Synthetic,
            int NameIndex,
            int OpenBraceIndex,
            int CloseBraceIndex)? _javaSameLineCtor;

        internal CoreReferenceLineContainerResolver(
            CoreReferenceLoopContext loop,
            int lineIndex,
            int lineNumber,
            SymbolRecord? container,
            bool csharpLineHasWhereClause,
            (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex,
                int CloseBraceIndex)? javaSameLineCtor)
        {
            _loop = loop;
            _lineIndex = lineIndex;
            _lineNumber = lineNumber;
            _container = container;
            _csharpLineHasWhereClause = csharpLineHasWhereClause;
            _javaSameLineCtor = javaSameLineCtor;
            ResolveContainerForCall = ResolveContainer;
            ResolveSwiftPropertyContainerForCall =
                ResolveSwiftPropertyContainer;
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

            return primaryCtorOwner != null
                ? (true, primaryCtorOwner)
                : (false, null);
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
