using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class PythonImportBindingResolver
{
    public static bool ResolvesDependency(string? sourcePath, string? targetPath, string? referenceName, string? referenceKind, string? context, long? columnNumber, string? signature)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath)
            || string.IsNullOrWhiteSpace(referenceName) || string.IsNullOrWhiteSpace(signature))
            return false;

        var targetModule = ModuleNameFromPath(targetPath);
        referenceName = QualifyReferenceFromContext(referenceName, context, columnNumber);
        foreach (var binding in Parse(signature, sourcePath))
        {
            if (referenceName == binding.LocalName
                || (referenceKind == "instantiate" && referenceName == binding.ImportedName)
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
        => TryResolveImportedTypeCall(candidate, preparedLine, callIndex, BuildImportedTypeCallLookup(symbols), out canonicalName);

    internal static bool TryResolveImportedTypeCall(
        string candidate,
        string preparedLine,
        int callIndex,
        ImportedTypeCallLookup lookup,
        out string canonicalName)
    {
        canonicalName = candidate;
        var receiver = GetReceiver(preparedLine, callIndex);
        if (receiver != null && lookup.ModuleAliases.Contains(receiver) && IsTypeLike(candidate))
        {
            return true;
        }

        if (lookup.ImportedTypesByLocalName.TryGetValue(candidate, out var importedName)
            && IsTypeLike(importedName))
        {
            canonicalName = importedName;
            return true;
        }

        return false;
    }

    internal static ImportedTypeCallLookup BuildImportedTypeCallLookup(IReadOnlyList<SymbolRecord> symbols)
    {
        HashSet<string>? seenSignatures = null;
        HashSet<string>? moduleAliases = null;
        Dictionary<string, string>? importedTypesByLocalName = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "import"
                || string.IsNullOrWhiteSpace(symbol.Signature)
                || !(seenSignatures ??= new HashSet<string>(StringComparer.Ordinal)).Add(symbol.Signature))
            {
                continue;
            }

            foreach (var binding in Parse(symbol.Signature, sourcePath: null))
            {
                if (binding.IsModule)
                {
                    (moduleAliases ??= new HashSet<string>(StringComparer.Ordinal)).Add(binding.LocalName);
                }
                else
                {
                    (importedTypesByLocalName ??= new Dictionary<string, string>(StringComparer.Ordinal))
                        .TryAdd(binding.LocalName, binding.ImportedName);
                }
            }
        }

        return new ImportedTypeCallLookup(
            moduleAliases ?? EmptyStringSet,
            importedTypesByLocalName ?? EmptyImportedTypeMap);
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
            {
                var leafStart = qualified.LastIndexOf('.') + 1;
                return qualified[leafStart..];
            }
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
        var dots = 0;
        while (dots < module.Length && module[dots] == '.')
            dots++;

        var normalizedSourcePath = sourcePath.Replace('\\', '/');
        var packageEnd = normalizedSourcePath.LastIndexOf('/');
        for (var i = 1; i < dots && packageEnd > 0; i++)
            packageEnd = normalizedSourcePath.LastIndexOf('/', packageEnd - 1);
        var tail = module[dots..];
        var package = packageEnd > 0
            ? normalizedSourcePath[..packageEnd].Replace('/', '.')
            : string.Empty;
        if (tail.Length == 0)
            return package;
        return package.Length == 0 ? tail : package + "." + tail;
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

    private static bool IsTypeLike(string name)
    {
        if (name.Length <= 1 || !char.IsUpper(name[0]))
            return false;

        for (var index = 1; index < name.Length; index++)
        {
            if (char.IsLower(name[index]))
                return true;
        }

        return false;
    }

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

    internal sealed record ImportedTypeCallLookup(
        IReadOnlySet<string> ModuleAliases,
        IReadOnlyDictionary<string, string> ImportedTypesByLocalName);

    private static readonly IReadOnlySet<string> EmptyStringSet = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> EmptyImportedTypeMap =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
