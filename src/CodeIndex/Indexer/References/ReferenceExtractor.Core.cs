using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static readonly HashSet<int> EmptyMatchedIndices = new();

    private static bool MayContainNestedGenericSyntax(string preparedLine)
    {
        var firstGenericStart = preparedLine.IndexOf('<');
        return firstGenericStart >= 0
            && preparedLine.IndexOf('<', firstGenericStart + 1) >= 0;
    }
}
