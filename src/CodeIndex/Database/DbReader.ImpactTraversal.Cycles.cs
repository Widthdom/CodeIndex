using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbReader
{
    private readonly record struct ImpactCycleNode(string Key, long? SymbolId, string Name);

    private readonly record struct ImpactCycleEdge(ImpactCycleNode Caller, ImpactCycleNode Callee);

    private sealed class ImpactCycleTracker
    {
        private readonly bool _hasResolvedIdentityGraph;
        private readonly IReadOnlySet<long>? _logicalRootSymbolIds;
        private readonly string _logicalRootKey;
        private readonly string _logicalRootName;
        private readonly Dictionary<string, HashSet<string>> _parentsByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImpactCycleMemberResult> _nodesByKey = new(StringComparer.Ordinal);
        private readonly HashSet<string> _cycleKeys = new(StringComparer.Ordinal);

        internal ImpactCycleTracker(ImpactTraversalRoot root)
        {
            _hasResolvedIdentityGraph = root.HasResolvedIdentityGraph;
            _logicalRootSymbolIds = root.IsLogicalPartialFamily ? root.IdentitySymbolIds : null;
            _logicalRootKey = root.NodeKey;
            _logicalRootName = root.ResolvedName;
        }

        internal List<ImpactCycleResult> Results { get; } = [];

        internal int GraphStateEntryCount
        {
            get
            {
                var count = _parentsByKey.Count;
                foreach (var parents in _parentsByKey.Values)
                    count += parents.Count;
                return count;
            }
        }

        internal IReadOnlyList<ImpactCycleEdge> Observe(
            CallerResult caller,
            string callerName,
            string calleeName)
        {
            var edges = BuildEdges(caller, callerName, calleeName);
            foreach (var edge in edges)
            {
                RegisterNode(edge.Caller);
                RegisterNode(edge.Callee);
                if (IsCycleEdge(edge.Caller.Key, edge.Callee.Key))
                    AddCycle(BuildCycleMembers(edge.Caller.Key, edge.Callee.Key));
            }
            return edges;
        }

        internal void CommitParents(IReadOnlyList<ImpactCycleEdge> edges)
        {
            foreach (var edge in edges)
            {
                if (!_parentsByKey.TryGetValue(edge.Caller.Key, out var parents))
                {
                    parents = new HashSet<string>(StringComparer.Ordinal);
                    _parentsByKey[edge.Caller.Key] = parents;
                }
                parents.Add(edge.Callee.Key);
            }
        }

        private List<ImpactCycleEdge> BuildEdges(
            CallerResult caller,
            string callerName,
            string calleeName)
        {
            var callerNode = NormalizeLogicalRoot(
                ImpactNodeIdentity.CycleNode(
                    caller.CallerSymbolId,
                    callerName,
                    _hasResolvedIdentityGraph));
            if (callerNode is not { } canonicalCaller)
                return [];

            if (!_hasResolvedIdentityGraph)
            {
                var calleeNode = ImpactNodeIdentity.CycleNode(null, calleeName, false)!.Value;
                return [new ImpactCycleEdge(canonicalCaller, calleeNode)];
            }

            var calleeSymbolIds = caller.CalleeSymbolIds.Count > 0
                ? caller.CalleeSymbolIds
                : caller.CalleeSymbolId is long calleeSymbolId
                    ? [calleeSymbolId]
                    : Array.Empty<long>();
            return calleeSymbolIds
                .Distinct()
                .Order()
                .Select(calleeSymbolId => new ImpactCycleEdge(
                    canonicalCaller,
                    NormalizeLogicalRoot(new ImpactCycleNode(
                        $"id:{calleeSymbolId}",
                        calleeSymbolId,
                        calleeName))!.Value))
                .ToList();
        }

        private ImpactCycleNode? NormalizeLogicalRoot(ImpactCycleNode? node)
        {
            if (node is not { SymbolId: long symbolId }
                || _logicalRootSymbolIds is not { Count: > 0 }
                || !_logicalRootSymbolIds.Contains(symbolId))
            {
                return node;
            }
            return new ImpactCycleNode(_logicalRootKey, null, _logicalRootName);
        }

        private void RegisterNode(ImpactCycleNode node)
            => _nodesByKey.TryAdd(node.Key, new ImpactCycleMemberResult
            {
                SymbolId = node.SymbolId,
                Name = node.Name,
            });

        private bool IsCycleEdge(string callerKey, string calleeKey)
            => string.Equals(callerKey, calleeKey, StringComparison.Ordinal)
               || HasAncestor(calleeKey, callerKey);

        private bool HasAncestor(string node, string target)
        {
            var stack = new Stack<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            stack.Push(node);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!seen.Add(current))
                    continue;
                if (string.Equals(current, target, StringComparison.Ordinal))
                    return true;
                if (!_parentsByKey.TryGetValue(current, out var parents))
                    continue;
                foreach (var parent in parents)
                    stack.Push(parent);
            }
            return false;
        }

        private List<string> BuildCycleMembers(string callerKey, string calleeKey)
        {
            var members = new HashSet<string>(StringComparer.Ordinal);
            if (!TryBuildAncestorPath(calleeKey, callerKey, members))
            {
                members.Add(callerKey);
                members.Add(calleeKey);
            }
            var result = members.ToList();
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private bool TryBuildAncestorPath(
            string node,
            string target,
            HashSet<string> members)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return TryBuildAncestorPathCore(node, target, members, seen);
        }

        private bool TryBuildAncestorPathCore(
            string node,
            string target,
            HashSet<string> members,
            HashSet<string> seen)
        {
            if (!seen.Add(node))
                return false;
            members.Add(node);
            if (string.Equals(node, target, StringComparison.Ordinal))
                return true;
            if (_parentsByKey.TryGetValue(node, out var parents))
            {
                foreach (var parent in parents)
                {
                    if (TryBuildAncestorPathCore(parent, target, members, seen))
                        return true;
                }
            }

            members.Remove(node);
            return false;
        }

        private void AddCycle(List<string> memberKeys)
        {
            if (memberKeys.Count == 0)
                return;
            var key = string.Join("\u001F", memberKeys);
            if (!_cycleKeys.Add(key))
                return;
            var identities = memberKeys
                .Select(memberKey => _nodesByKey[memberKey])
                .OrderBy(static node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static node => node.SymbolId)
                .Select(static node => new ImpactCycleMemberResult
                {
                    SymbolId = node.SymbolId,
                    Name = node.Name,
                })
                .ToList();
            Results.Add(new ImpactCycleResult
            {
                Members = identities.Select(static identity => identity.Name).ToList(),
                MemberIdentities = identities.Any(static identity => identity.SymbolId != null)
                    ? identities
                    : null,
            });
        }
    }
}
