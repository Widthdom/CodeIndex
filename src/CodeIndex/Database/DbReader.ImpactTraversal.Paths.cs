using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class ImpactPathTracker
    {
        private readonly DbReader _owner;
        private readonly bool _withPaths;
        private readonly string _rootNodeKey;
        private readonly Dictionary<string, HashSet<string>> _parentsByNodeKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _depthByNodeKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<int>>? _resultIndicesByNodeKey;
        private readonly Dictionary<string, ImpactPathNode>? _nodesByKey;

        internal ImpactPathTracker(DbReader owner, bool withPaths, ImpactTraversalRoot root)
        {
            _owner = owner;
            _withPaths = withPaths;
            _rootNodeKey = root.NodeKey;
            _depthByNodeKey[root.NodeKey] = 0;
            if (!withPaths)
                return;

            _resultIndicesByNodeKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            _nodesByKey = new Dictionary<string, ImpactPathNode>(StringComparer.OrdinalIgnoreCase)
            {
                [root.NodeKey] = root.PathNode!,
            };
        }

        internal int GraphStateEntryCount
        {
            get
            {
                var count = _depthByNodeKey.Count
                            + _parentsByNodeKey.Count
                            + (_resultIndicesByNodeKey?.Count ?? 0);
                foreach (var parents in _parentsByNodeKey.Values)
                    count += parents.Count;
                if (_resultIndicesByNodeKey != null)
                {
                    foreach (var indices in _resultIndicesByNodeKey.Values)
                        count += indices.Count;
                }
                return count;
            }
        }

        internal bool RecordSameDepthParent(
            string callerNodeKey,
            string currentNodeKey,
            int callerDepth)
        {
            if (!_withPaths
                || !_depthByNodeKey.TryGetValue(callerNodeKey, out var existingDepth)
                || existingDepth != callerDepth)
            {
                return false;
            }

            _parentsByNodeKey[callerNodeKey].Add(currentNodeKey);
            return true;
        }

        internal void RecordCaller(
            string callerNodeKey,
            string currentNodeKey,
            int callerDepth,
            int resultIndex,
            CallerResult caller,
            string callerName,
            long? callerSymbolId,
            string? requestLang)
        {
            if (_withPaths)
            {
                _nodesByKey!.TryAdd(
                    callerNodeKey,
                    _owner.ResolveImpactPathNode(
                        callerName,
                        callerSymbolId,
                        caller.CallerKind,
                        caller.Lang ?? requestLang,
                        caller.Path,
                        caller.FirstLine));
                _depthByNodeKey.TryAdd(callerNodeKey, callerDepth);
                if (resultIndex >= 0)
                {
                    if (!_resultIndicesByNodeKey!.TryGetValue(callerNodeKey, out var indices))
                    {
                        indices = [];
                        _resultIndicesByNodeKey[callerNodeKey] = indices;
                    }
                    indices.Add(resultIndex);
                }
            }
            else
            {
                _depthByNodeKey.TryAdd(callerNodeKey, callerDepth);
            }

            if (!_parentsByNodeKey.TryGetValue(callerNodeKey, out var parents))
            {
                parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _parentsByNodeKey[callerNodeKey] = parents;
            }
            parents.Add(currentNodeKey);
        }

        internal void Materialize(List<ImpactResult> results, int maxPathsPerResult)
        {
            if (!_withPaths)
                return;

            var effectiveCap = maxPathsPerResult > 0
                ? maxPathsPerResult
                : DefaultImpactPathsPerResult;
            foreach (var (callerNodeKey, indices) in _resultIndicesByNodeKey!)
            {
                var (pathKeys, pathsTruncated) = EnumeratePaths(callerNodeKey, effectiveCap);
                var paths = pathKeys
                    .Select(path => path.Select(nodeKey => _nodesByKey![nodeKey].Name).ToList())
                    .ToList();
                foreach (var resultIndex in indices)
                {
                    results[resultIndex].Paths = paths;
                    results[resultIndex].PathDetails = BuildPathDetails(pathKeys, results[resultIndex]);
                    results[resultIndex].PathsTruncated = pathsTruncated;
                }
            }
        }

        private (List<List<string>> Paths, bool Truncated) EnumeratePaths(
            string callerNodeKey,
            int maxPathsPerResult)
        {
            var paths = new List<List<string>>();
            var stack = new Stack<string>();
            var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var truncated = false;

            stack.Push(callerNodeKey);
            onStack.Add(callerNodeKey);
            Dfs(callerNodeKey);
            return (paths, truncated);

            void Dfs(string node)
            {
                if (string.Equals(node, _rootNodeKey, StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(stack.ToList());
                    return;
                }
                if (!_parentsByNodeKey.TryGetValue(node, out var parents))
                    return;
                foreach (var parent in parents)
                {
                    if (onStack.Contains(parent))
                        continue;
                    if (paths.Count >= maxPathsPerResult)
                    {
                        truncated = true;
                        return;
                    }
                    stack.Push(parent);
                    onStack.Add(parent);
                    Dfs(parent);
                    stack.Pop();
                    onStack.Remove(parent);
                }
            }
        }

        private List<List<ImpactPathNode>> BuildPathDetails(
            List<List<string>> pathKeys,
            ImpactResult result)
        {
            var details = new List<List<ImpactPathNode>>(pathKeys.Count);
            foreach (var path in pathKeys)
            {
                var detailPath = new List<ImpactPathNode>(path.Count);
                for (var i = 0; i < path.Count; i++)
                {
                    var nodeKey = path[i];
                    var isResultNode = i == path.Count - 1;
                    if (!_nodesByKey!.TryGetValue(nodeKey, out var node))
                        node = new ImpactPathNode { Name = nodeKey };
                    detailPath.Add(isResultNode
                        ? ClonePathNodeForResult(node, result)
                        : ClonePathNode(node));
                }
                details.Add(detailPath);
            }
            return details;
        }

        private static ImpactPathNode ClonePathNodeForResult(
            ImpactPathNode node,
            ImpactResult result)
        {
            var clone = ClonePathNode(node);
            clone.Kind ??= result.CallerKind;
            clone.Lang ??= result.Lang;
            clone.ReferencePath = result.Path;
            clone.ReferenceLine = result.FirstLine;
            return clone;
        }

        private static ImpactPathNode ClonePathNode(ImpactPathNode node)
            => new()
            {
                SymbolId = node.SymbolId,
                Name = node.Name,
                Kind = node.Kind,
                Lang = node.Lang,
                DefinitionPath = node.DefinitionPath,
                DefinitionLine = node.DefinitionLine,
                Container = node.Container,
                FamilyKey = node.FamilyKey,
                PartialFamilyId = node.PartialFamilyId,
                LogicalTargetKey = node.LogicalTargetKey,
                ReferencePath = node.ReferencePath,
                ReferenceLine = node.ReferenceLine,
            };
    }
}
