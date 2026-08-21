using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbReader
{
    private static class ImpactNodeIdentity
    {
        internal static string TraversalKey(long? symbolId, string name)
            => symbolId is long canonicalId ? CanonicalIdKey(canonicalId) : $"name:{name}";

        internal static string VisitedKey(
            CallerResult caller,
            string callerName,
            bool useCanonicalIdentity,
            bool deduplicateLogicalNodes)
        {
            var identity = useCanonicalIdentity && caller.CallerSymbolId is long callerId
                ? CanonicalIdKey(callerId)
                : $"{caller.Path}:{callerName}";
            return deduplicateLogicalNodes ? identity : $"{identity}:{caller.ReferenceKind}";
        }

        internal static ImpactCycleNode? CycleNode(
            long? symbolId,
            string name,
            bool hasResolvedIdentityGraph)
        {
            if (!hasResolvedIdentityGraph)
                return new ImpactCycleNode($"name:{NameFold.Fold(name) ?? name}", null, name);
            return symbolId is long canonicalId
                ? new ImpactCycleNode(CanonicalIdKey(canonicalId), canonicalId, name)
                : null;
        }

        private static string CanonicalIdKey(long symbolId) => $"id:{symbolId}";
    }

    private sealed class ImpactTraversalState
    {
        private readonly int _resultOffset;
        private readonly int _graphStateEntryBudget;
        private readonly Dictionary<string, int>? _resultIndexByVisitedKey;

        internal ImpactTraversalState(
            DbReader owner,
            ImpactTraversalRequest request,
            ImpactTraversalRoot root)
        {
            _resultOffset = Math.Max(0, request.ResultOffset);
            ResultWindowEnd = checked(_resultOffset + request.Limit);
            _graphStateEntryBudget = owner.GetImpactGraphStateEntryBudget(ResultWindowEnd);
            BoundaryProbeBudget = owner.GetImpactBoundaryCallerProbeBudget();
            Queue = CreateInitialQueue(root);
            Visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.ResolvedName };
            Cycles = new ImpactCycleTracker(root);
            Paths = new ImpactPathTracker(owner, request.WithPaths, root);
            _resultIndexByVisitedKey = root.IsLogicalPartialFamily
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : null;
            Truncated = root.InitiallyTruncated;
            TruncatedReason = root.InitiallyTruncated
                ? ImpactTruncatedReasons.SafetyCap
                : null;
        }

        internal Queue<ImpactTraversalFrontierNode> Queue { get; }
        internal HashSet<string> Visited { get; }
        internal ImpactCycleTracker Cycles { get; }
        internal ImpactPathTracker Paths { get; }
        internal List<ImpactResult> Results { get; } = [];
        internal int ResultWindowEnd { get; }
        internal int BoundaryProbeBudget { get; }
        internal int DiscoveredResultCount { get; private set; }
        internal bool Truncated { get; private set; }
        internal string? TruncatedReason { get; private set; }
        internal bool MaxDepthReached { get; set; }
        internal bool GraphStateBudgetHit { get; private set; }
        internal bool BoundaryProbeBudgetHit { get; private set; }

        internal bool CanTraverse
            => Queue.Count > 0
               && DiscoveredResultCount < ResultWindowEnd
               && !GraphStateBudgetHit
               && !BoundaryProbeBudgetHit;

        internal bool CanFetchCurrentNode
            => DiscoveredResultCount < ResultWindowEnd
               && !GraphStateBudgetHit
               && !BoundaryProbeBudgetHit;

        internal bool IncludeNextResult => DiscoveredResultCount >= _resultOffset;

        private static Queue<ImpactTraversalFrontierNode> CreateInitialQueue(
            ImpactTraversalRoot root)
        {
            var capacity = root.IsLogicalPartialFamily || root.InitialTargetSymbolIds is { Count: > 0 }
                ? 1
                : Math.Max(1, root.IdentitySymbolIds.Count);
            var queue = new Queue<ImpactTraversalFrontierNode>(capacity);
            if (root.IsLogicalPartialFamily)
            {
                queue.Enqueue(new ImpactTraversalFrontierNode(
                    root.ResolvedName,
                    SymbolId: null,
                    root.IdentitySymbolIds.Order().ToArray(),
                    root.NodeKey,
                    Depth: 0));
            }
            else if (root.InitialTargetSymbolIds is { Count: > 0 } initialTargetSymbolIds)
            {
                queue.Enqueue(new ImpactTraversalFrontierNode(
                    root.ResolvedName,
                    root.IdentitySymbolIds.Count == 1 ? root.IdentitySymbolIds.Single() : null,
                    initialTargetSymbolIds,
                    root.NodeKey,
                    Depth: 0));
            }
            else if (root.IdentitySymbolIds.Count > 0)
            {
                foreach (var symbolId in root.IdentitySymbolIds.Order())
                {
                    queue.Enqueue(new ImpactTraversalFrontierNode(
                        root.ResolvedName,
                        symbolId,
                        TargetSymbolIds: null,
                        root.NodeKey,
                        Depth: 0));
                }
            }
            else
            {
                queue.Enqueue(new ImpactTraversalFrontierNode(
                    root.ResolvedName,
                    SymbolId: null,
                    TargetSymbolIds: null,
                    root.NodeKey,
                    Depth: 0));
            }
            return queue;
        }

        internal bool TryVisit(string key) => Visited.Add(key);

        internal int AddResult(ImpactResult? result, string visitedKey)
        {
            var resultIndex = -1;
            if (IncludeNextResult)
            {
                Results.Add(result!);
                resultIndex = Results.Count - 1;
                _resultIndexByVisitedKey?.Add(visitedKey, resultIndex);
            }
            DiscoveredResultCount++;
            return resultIndex;
        }

        internal void MergeReferenceEvidence(string visitedKey, CallerResult caller)
        {
            if (_resultIndexByVisitedKey != null
                && _resultIndexByVisitedKey.TryGetValue(visitedKey, out var resultIndex))
            {
                MergeImpactReferenceEvidence(Results[resultIndex], caller);
            }
        }

        internal bool CheckGraphStateBudget()
        {
            if (Paths.GraphStateEntryCount + Cycles.GraphStateEntryCount <= _graphStateEntryBudget)
                return false;
            GraphStateBudgetHit = true;
            Truncated = true;
            TruncatedReason = ImpactTruncatedReasons.GraphStateBudget;
            return true;
        }

        internal void MarkBoundaryProbeBudget()
        {
            BoundaryProbeBudgetHit = true;
            Truncated = true;
            TruncatedReason = ImpactTruncatedReasons.BoundaryProbeBudget;
        }

        internal void MarkUserLimit()
        {
            Truncated = true;
            TruncatedReason ??= ImpactTruncatedReasons.UserLimit;
        }

        internal void MarkSafetyCap()
        {
            Truncated = true;
            TruncatedReason = ImpactTruncatedReasons.SafetyCap;
        }

        internal void CompleteTraversal()
        {
            if (Queue.Count > 0 && DiscoveredResultCount >= ResultWindowEnd)
                MarkUserLimit();
        }

        internal string ResolveTerminationReason()
            => TruncatedReason switch
            {
                ImpactTruncatedReasons.GraphStateBudget => ImpactTerminationReasons.GraphStateBudget,
                ImpactTruncatedReasons.BoundaryProbeBudget => ImpactTerminationReasons.BoundaryProbeBudget,
                ImpactTruncatedReasons.SafetyCap => ImpactTerminationReasons.SafetyCap,
                ImpactTruncatedReasons.UserLimit => ImpactTerminationReasons.RowLimitTruncated,
                _ when Cycles.Results.Count > 0 => ImpactTerminationReasons.CycleDetected,
                _ when MaxDepthReached => ImpactTerminationReasons.MaxDepthReached,
                _ => ImpactTerminationReasons.Completed,
            };

        private static void MergeImpactReferenceEvidence(ImpactResult result, CallerResult caller)
        {
            var counts = result.ReferenceKindCounts.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            foreach (var (kind, count) in caller.ReferenceKindCounts)
            {
                counts[kind] = counts.TryGetValue(kind, out var existingCount)
                    ? Math.Max(existingCount, count)
                    : count;
            }

            result.ReferenceKindCounts = counts;
            result.ReferenceKinds = counts.Keys.Order(StringComparer.Ordinal).ToArray();
            result.ReferenceCount = counts.Values.Sum();
            var callerColumn = caller.FirstColumn > 0 ? caller.FirstColumn : (int?)null;
            if (caller.FirstLine < result.FirstLine
                || caller.FirstLine == result.FirstLine
                && (callerColumn ?? int.MaxValue) < (result.FirstColumn ?? int.MaxValue))
            {
                result.FirstLine = caller.FirstLine;
                result.FirstColumn = callerColumn;
                result.FirstLength = caller.FirstLength;
                result.CalleeName = caller.CalleeName;
            }
        }
    }
}
