using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class ImpactTraversalEngine
    {
        private const int MaxFetchIterations = 1000;

        private readonly DbReader _owner;
        private readonly ImpactTraversalRequest _request;
        private readonly ImpactTraversalRoot _root;
        private readonly ImpactTraversalState _state;

        internal ImpactTraversalEngine(
            DbReader owner,
            ImpactTraversalRequest request,
            ImpactTraversalRoot root)
        {
            _owner = owner;
            _request = request;
            _root = root;
            _state = new ImpactTraversalState(owner, request, root);
        }

        internal (List<ImpactResult> Results, bool Truncated, string? TruncatedReason, string TerminationReason, List<ImpactCycleResult> Cycles) Run()
        {
            while (_state.CanTraverse)
            {
                var node = _state.Queue.Dequeue();
                TraverseNode(in node);
            }

            _state.CompleteTraversal();
            _state.Paths.Materialize(_state.Results, _request.MaxPathsPerResult);
            return (
                _state.Results,
                _state.Truncated,
                _state.TruncatedReason,
                _state.ResolveTerminationReason(),
                _state.Cycles.Results);
        }

        private void TraverseNode(in ImpactTraversalFrontierNode node)
        {
            var needed = _state.ResultWindowEnd - _state.DiscoveredResultCount;
            var pageSize = Math.Max(1, needed + 1);
            var pageOffset = 0;
            var fetchIterations = 0;

            while (_state.CanFetchCurrentNode && fetchIterations < MaxFetchIterations)
            {
                fetchIterations++;
                var page = _owner.ReadImpactCallerPage(
                    in node,
                    _request,
                    pageSize,
                    pageOffset,
                    _root.IncludeAmbiguousMSource);
                if (page.Count == 0)
                    break;

                ProcessPage(in node, page);
                pageOffset += page.Count;
                if (page.Count < pageSize)
                    break;
            }

            if (fetchIterations >= MaxFetchIterations)
                _state.MarkSafetyCap();
        }

        private void ProcessPage(
            in ImpactTraversalFrontierNode node,
            IReadOnlyList<CallerResult> page)
        {
            foreach (var caller in page)
            {
                if (_state.DiscoveredResultCount >= _state.ResultWindowEnd)
                {
                    _state.MarkUserLimit();
                    break;
                }
                if (!ProcessCaller(in node, caller))
                    break;
            }
        }

        private bool ProcessCaller(
            in ImpactTraversalFrontierNode node,
            CallerResult caller)
        {
            var callerName = caller.CallerName ?? SyntheticTopLevelCallerName;
            var callerSymbolId = _root.HasResolvedIdentityGraph ? caller.CallerSymbolId : null;
            var calleeSymbolId = _root.HasResolvedIdentityGraph ? caller.CalleeSymbolId : null;
            var cycleEdges = _state.Cycles.Observe(caller, callerName, node.Symbol);
            if (IsRootCaller(caller, callerName))
                return true;

            var callerNodeKey = ImpactNodeIdentity.TraversalKey(callerSymbolId, callerName);
            var visitedKey = ImpactNodeIdentity.VisitedKey(
                caller,
                callerName,
                _root.HasResolvedIdentityGraph,
                _root.IsLogicalPartialFamily);
            _state.Cycles.CommitParents(cycleEdges);
            if (_state.CheckGraphStateBudget())
                return false;

            if (!_state.TryVisit(visitedKey))
            {
                _state.MergeReferenceEvidence(visitedKey, caller);
                if (_state.Paths.RecordSameDepthParent(
                        callerNodeKey,
                        node.NodeKey,
                        node.Depth + 1)
                    && _state.CheckGraphStateBudget())
                {
                    return false;
                }
                return true;
            }

            var result = _state.IncludeNextResult
                ? BuildResult(caller, callerSymbolId, calleeSymbolId, node.Depth + 1)
                : null;
            var resultIndex = _state.AddResult(result, visitedKey);
            _state.Paths.RecordCaller(
                callerNodeKey,
                node.NodeKey,
                node.Depth + 1,
                resultIndex,
                caller,
                callerName,
                callerSymbolId,
                _request.Lang);
            if (_state.CheckGraphStateBudget())
                return false;

            return ContinueTraversal(in node, caller, callerSymbolId, callerNodeKey);
        }

        private bool ContinueTraversal(
            in ImpactTraversalFrontierNode node,
            CallerResult caller,
            long? callerSymbolId,
            string callerNodeKey)
        {
            if (caller.CallerName != null
                && caller.CallerName != SyntheticTopLevelCallerName
                && node.Depth + 1 < _request.MaxDepth)
            {
                _state.Queue.Enqueue(new ImpactTraversalFrontierNode(
                    caller.CallerName,
                    callerSymbolId,
                    TargetSymbolIds: null,
                    callerNodeKey,
                    node.Depth + 1));
            }
            else if (caller.CallerName != null
                     && caller.CallerName != SyntheticTopLevelCallerName
                     && node.Depth + 1 == _request.MaxDepth)
            {
                var inspection = InspectBoundary(caller.CallerName, callerSymbolId);
                _state.MaxDepthReached |= inspection.HasUnvisitedCaller;
                if (inspection.ProbeBudgetHit)
                {
                    _state.MarkBoundaryProbeBudget();
                    return false;
                }
            }
            return true;
        }

        private ImpactBoundaryInspection InspectBoundary(
            string symbolName,
            long? symbolId)
        {
            var pageOffset = 0;
            var probes = 0;
            while (true)
            {
                if (probes >= _state.BoundaryProbeBudget)
                    return new ImpactBoundaryInspection(true, true);

                var pageSize = Math.Min(
                    ImpactBoundaryCallerProbePageSize,
                    _state.BoundaryProbeBudget - probes);
                var node = new ImpactTraversalFrontierNode(
                    symbolName,
                    symbolId,
                    TargetSymbolIds: null,
                    NodeKey: string.Empty,
                    Depth: 0);
                var page = _owner.ReadImpactCallerPage(
                    in node,
                    _request,
                    pageSize,
                    pageOffset,
                    _root.IncludeAmbiguousMSource);
                if (page.Count == 0)
                    return new ImpactBoundaryInspection(false, false);
                probes += page.Count;

                foreach (var caller in page)
                {
                    var callerName = caller.CallerName ?? SyntheticTopLevelCallerName;
                    var cycleEdges = _state.Cycles.Observe(caller, callerName, symbolName);
                    if (IsRootCaller(caller, callerName))
                        continue;

                    _state.Cycles.CommitParents(cycleEdges);
                    var visitedKey = ImpactNodeIdentity.VisitedKey(
                        caller,
                        callerName,
                        _root.HasResolvedIdentityGraph,
                        _root.IsLogicalPartialFamily);
                    if (!_state.Visited.Contains(visitedKey))
                        return new ImpactBoundaryInspection(true, false);
                }

                if (page.Count < pageSize)
                    return new ImpactBoundaryInspection(false, false);
                pageOffset += page.Count;
            }
        }

        private bool IsRootCaller(CallerResult caller, string callerName)
        {
            if (_root.IdentitySymbolIds.Count > 0
                && caller.CallerSymbolId is long callerSymbolId)
            {
                return _root.IdentitySymbolIds.Contains(callerSymbolId);
            }
            return string.Equals(
                       callerName,
                       _root.ResolvedName,
                       StringComparison.OrdinalIgnoreCase)
                   && (_root.DefinitionPaths.Count == 0
                       || _root.DefinitionPaths.Contains(caller.Path));
        }

        private static ImpactResult BuildResult(
            CallerResult caller,
            long? callerSymbolId,
            long? calleeSymbolId,
            int depth)
            => new()
            {
                Path = caller.Path,
                Lang = caller.Lang,
                CallerKind = caller.CallerKind,
                CallerName = caller.CallerName,
                CalleeName = caller.CalleeName,
                CallerSymbolId = callerSymbolId,
                CalleeSymbolId = calleeSymbolId,
                Depth = depth,
                FirstLine = caller.FirstLine,
                FirstColumn = caller.FirstColumn > 0 ? caller.FirstColumn : null,
                ReferenceCount = caller.ReferenceCount,
                ReferenceKind = caller.ReferenceKind,
                ReferenceKinds = caller.ReferenceKinds,
                ReferenceKindCounts = caller.ReferenceKindCounts,
            };
    }
}
