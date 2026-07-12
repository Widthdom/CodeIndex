using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class PythonImportBindingResolver
{
    public static bool ResolvesDependency(string? sourcePath, string? targetPath, string? referenceName, string? context, long? columnNumber, string? signature)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath)
            || string.IsNullOrWhiteSpace(referenceName) || string.IsNullOrWhiteSpace(signature))
            return false;

        var targetModule = ModuleNameFromPath(targetPath);
        referenceName = QualifyReferenceFromContext(referenceName, context, columnNumber);
        foreach (var binding in Parse(signature, sourcePath))
        {
            if (referenceName == binding.LocalName
                || referenceName == binding.ImportedName
                || referenceName.StartsWith(binding.LocalName + ".", StringComparison.Ordinal))
            {
                if (ModuleMatches(targetModule, binding.Module))
                    return true;
            }
        }
        return false;
    }

    public static bool IsImportedTypeCall(string candidate, string preparedLine, int callIndex, IReadOnlyList<SymbolRecord> symbols)
        => TryResolveImportedTypeCall(candidate, preparedLine, callIndex, symbols, out _);

    public static bool TryResolveImportedTypeCall(string candidate, string preparedLine, int callIndex, IReadOnlyList<SymbolRecord> symbols, out string canonicalName)
    {
        canonicalName = candidate;
        var receiver = GetReceiver(preparedLine, callIndex);
        foreach (var signature in symbols.Where(symbol => symbol.Kind == "import").Select(symbol => symbol.Signature).Distinct(StringComparer.Ordinal))
        {
            foreach (var binding in Parse(signature, sourcePath: null))
            {
                if (receiver != null && binding.IsModule && receiver == binding.LocalName && IsTypeLike(candidate))
                {
                    canonicalName = candidate;
                    return true;
                }
                if (!binding.IsModule && candidate == binding.LocalName && IsTypeLike(binding.ImportedName))
                {
                    canonicalName = binding.ImportedName;
                    return true;
                }
            }
        }
        return false;
    }

    public static string? ResolveTargetName(string? sourcePath, string? referenceName, string? context, long? columnNumber, string? signature)
    {
        if (string.IsNullOrWhiteSpace(referenceName))
            return null;
        var qualified = QualifyReferenceFromContext(referenceName, context, columnNumber);
        foreach (var binding in Parse(signature, sourcePath))
        {
            if (qualified == binding.LocalName)
                return binding.IsModule ? referenceName : binding.ImportedName;
            if (qualified.StartsWith(binding.LocalName + ".", StringComparison.Ordinal))
                return qualified[(binding.LocalName.Length + 1)..].Split('.')[^1];
        }
        return null;
    }

    private static IEnumerable<Binding> Parse(string? signature, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(signature))
            yield break;
        var text = signature.Trim();
        if (text.StartsWith("from ", StringComparison.Ordinal))
        {
            var importIndex = text.IndexOf(" import ", StringComparison.Ordinal);
            if (importIndex < 6)
                yield break;
            var module = ResolveRelativeModule(text[5..importIndex].Trim(), sourcePath);
            foreach (var item in text[(importIndex + 8)..].Split(','))
            {
                var (imported, local) = ParseAlias(item);
                if (imported.Length > 0 && imported != "*")
                    yield return new Binding(module, imported, local, IsModule: false);
            }
            yield break;
        }
        if (!text.StartsWith("import ", StringComparison.Ordinal))
            yield break;
        foreach (var item in text[7..].Split(','))
        {
            var (module, local) = ParseAlias(item);
            if (module.Length > 0)
                yield return new Binding(module, module, local.Length > 0 ? local : module.Split('.')[0], IsModule: true);
        }
    }

    private static (string Imported, string Local) ParseAlias(string value)
    {
        var parts = value.Trim().Split(" as ", 2, StringSplitOptions.TrimEntries);
        var imported = parts[0].Trim('(', ')', ' ');
        return (imported, parts.Length == 2 ? parts[1].Trim('(', ')', ' ') : imported);
    }

    private static string ResolveRelativeModule(string module, string? sourcePath)
    {
        if (!module.StartsWith(".", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(sourcePath))
            return module.TrimStart('.');
        var dots = module.TakeWhile(ch => ch == '.').Count();
        var package = sourcePath.Replace('\\', '/').Split('/').SkipLast(1).ToList();
        for (var i = 1; i < dots && package.Count > 0; i++)
            package.RemoveAt(package.Count - 1);
        var tail = module[dots..];
        if (tail.Length > 0)
            package.AddRange(tail.Split('.'));
        return string.Join('.', package);
    }

    private static string ModuleNameFromPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.EndsWith("/__init__.py", StringComparison.Ordinal))
            normalized = normalized[..^12];
        else if (normalized.EndsWith(".py", StringComparison.Ordinal))
            normalized = normalized[..^3];
        return normalized.Replace('/', '.').Trim('.');
    }

    private static bool ModuleMatches(string target, string imported) =>
        target == imported || target.EndsWith("." + imported, StringComparison.Ordinal);

    private static string QualifyReferenceFromContext(string referenceName, string? context, long? columnNumber)
    {
        if (string.IsNullOrEmpty(context) || columnNumber is null)
            return referenceName;
        var index = checked((int)columnNumber.Value - 1);
        if (index < 0 || index >= context.Length)
            return referenceName;
        var tokenEnd = index;
        while (tokenEnd < context.Length && (char.IsLetterOrDigit(context[tokenEnd]) || context[tokenEnd] == '_'))
            tokenEnd++;
        var actualName = tokenEnd > index ? context[index..tokenEnd] : referenceName;
        if (index <= 1 || context[index - 1] != '.')
            return actualName;
        var end = index - 1;
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(context[start - 1]) || context[start - 1] == '_'))
            start--;
        return context[start..end] + "." + actualName;
    }

    private static bool IsTypeLike(string name) =>
        name.Length > 1 && char.IsUpper(name[0]) && name.Skip(1).Any(char.IsLower);

    private static string? GetReceiver(string line, int callIndex)
    {
        if (callIndex <= 1 || line[callIndex - 1] != '.')
            return null;
        var end = callIndex - 1;
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'))
            start--;
        return line[start..end];
    }

    private readonly record struct Binding(string Module, string ImportedName, string LocalName, bool IsModule);
}
