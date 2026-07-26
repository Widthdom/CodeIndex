using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool TryPopHdlScope(
        string language,
        string structuralLine,
        List<HdlScope> scopes)
    {
        if (scopes.Count == 0)
            return false;

        if (language != "vhdl")
        {
            var match = VerilogScopeEndRegex.Match(structuralLine);
            if (!match.Success)
                return false;

            PopHdlScope(scopes, NormalizeVerilogScopeKind(match.Groups["kind"].Value), name: null, ignoreCase: false);
            return true;
        }

        var vhdlMatch = VhdlScopeEndRegex.Match(structuralLine);
        if (!vhdlMatch.Success)
            return false;

        var kind = vhdlMatch.Groups["kind"].Success
            ? NormalizeVhdlScopeKind(vhdlMatch.Groups["kind"].Value)
            : null;
        var name = vhdlMatch.Groups["name"].Success
            ? vhdlMatch.Groups["name"].Value
            : null;
        if (name != null && VhdlControlEndNames.Contains(name))
            return true;

        if (kind == null && name == null)
            scopes.RemoveAt(scopes.Count - 1);
        else
            PopHdlScope(scopes, kind, name, ignoreCase: true);
        return true;
    }

    private static void PopHdlScope(
        List<HdlScope> scopes,
        string? kind,
        string? name,
        bool ignoreCase)
    {
        var comparison = ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (var index = scopes.Count - 1; index >= 0; index--)
        {
            var scope = scopes[index].Symbol;
            if ((kind == null || string.Equals(scope.Kind, kind, comparison))
                && (name == null || string.Equals(scope.Name, name, comparison)))
            {
                scopes.RemoveRange(index, scopes.Count - index);
                return;
            }
        }
    }

    private static void TryPushHdlScope(
        string language,
        string structuralLine,
        List<HdlScope> scopes,
        ref int nextDesignUnitId)
    {
        if (language != "vhdl")
        {
            var match = VerilogScopeStartRegex.Match(structuralLine);
            if (match.Success)
            {
                AddHdlScope(
                    scopes,
                    NormalizeVerilogScopeKind(match.Groups["kind"].Value),
                    match.Groups["name"].Value,
                    ref nextDesignUnitId);
                return;
            }

            match = SystemVerilogClassStartRegex.Match(structuralLine);
            if (match.Success)
            {
                AddHdlScope(scopes, "class", match.Groups["name"].Value, ref nextDesignUnitId);
                return;
            }

            match = VerilogFunctionStartRegex.Match(structuralLine);
            if (match.Success)
            {
                AddHdlScope(scopes, "function", match.Groups["name"].Value, ref nextDesignUnitId);
                return;
            }

            match = VerilogTaskStartRegex.Match(structuralLine);
            if (match.Success)
                AddHdlScope(scopes, "function", match.Groups["name"].Value, ref nextDesignUnitId);
            return;
        }

        if (TryMatchHdlScope(VhdlArchitectureStartRegex, structuralLine, "module", scopes, ref nextDesignUnitId)
            || TryMatchHdlScope(VhdlEntityStartRegex, structuralLine, "module", scopes, ref nextDesignUnitId)
            || TryMatchHdlScope(VhdlPackageStartRegex, structuralLine, "package", scopes, ref nextDesignUnitId)
            || TryMatchHdlScope(VhdlConfigurationStartRegex, structuralLine, "module", scopes, ref nextDesignUnitId))
        {
            return;
        }

        TryMatchHdlScope(
            VhdlProcessStartRegex,
            structuralLine,
            "function",
            scopes,
            ref nextDesignUnitId);
    }

    private static bool TryMatchHdlScope(
        Regex regex,
        string line,
        string kind,
        List<HdlScope> scopes,
        ref int nextDesignUnitId)
    {
        var match = regex.Match(line);
        if (!match.Success)
            return false;

        AddHdlScope(scopes, kind, match.Groups["name"].Value, ref nextDesignUnitId);
        return true;
    }

    private static void AddHdlScope(
        List<HdlScope> scopes,
        string kind,
        string name,
        ref int nextDesignUnitId,
        IReadOnlySet<string>? shadowedNames = null)
    {
        var designUnitId = scopes.Count == 0
            ? nextDesignUnitId++
            : scopes[0].DesignUnitId;
        scopes.Add(new HdlScope(
            new SymbolRecord
            {
                Kind = kind,
                Name = name,
            },
            designUnitId,
            shadowedNames == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(shadowedNames, StringComparer.OrdinalIgnoreCase)));
    }

    private static int[] BuildVhdlDesignUnitIds(
        string[] lines,
        int lineCount,
        CancellationToken cancellationToken)
    {
        var result = new int[lines.Length];
        var scopes = new List<HdlScope>();
        var designUnitIdsByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nextDesignUnitId = 1;
        VhdlPendingSubprogramHeader? pendingSubprogram = null;
        var unusedBlockCommentState = false;
        for (var index = 0; index < lineCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var structuralLine = MaskHdlCommentsAndStrings(
                lines[index],
                "vhdl",
                ref unusedBlockCommentState);
            if (string.IsNullOrWhiteSpace(structuralLine))
                continue;

            TryPopHdlScope("vhdl", structuralLine, scopes);
            var wasOutsideDesignUnit = scopes.Count == 0;
            AdvanceVhdlSubprogramHeader(
                structuralLine,
                ref pendingSubprogram,
                out var completedSubprogram);
            if (completedSubprogram != null)
            {
                AddHdlScope(
                    scopes,
                    "function",
                    completedSubprogram.Name,
                    ref nextDesignUnitId,
                    completedSubprogram.ShadowedNames);
            }
            else
            {
                TryPushHdlScope("vhdl", structuralLine, scopes, ref nextDesignUnitId);
            }
            if (wasOutsideDesignUnit
                && scopes.Count > 0
                && TryGetVhdlDesignUnitKey(structuralLine, out var designUnitKey))
            {
                if (!designUnitIdsByKey.TryGetValue(designUnitKey, out var designUnitId))
                {
                    designUnitId = scopes[0].DesignUnitId;
                    designUnitIdsByKey[designUnitKey] = designUnitId;
                }
                scopes[0] = scopes[0] with { DesignUnitId = designUnitId };
            }
            if (scopes.Count > 0)
                result[index] = scopes[0].DesignUnitId;
        }

        return result;
    }

    private static HashSet<string>? AdvanceVhdlSubprogramHeader(
        string line,
        ref VhdlPendingSubprogramHeader? pending,
        out VhdlCompletedSubprogramHeader? completed)
    {
        completed = null;
        if (pending == null)
        {
            var startMatch = VhdlFunctionStartRegex.Match(line);
            if (!startMatch.Success)
                startMatch = VhdlProcedureStartRegex.Match(line);
            if (!startMatch.Success)
                return null;
            pending = new VhdlPendingSubprogramHeader(startMatch.Groups["name"].Value);
        }

        var declaredOnLine = GetVhdlParameterNames(line);
        if (declaredOnLine != null)
            pending.ShadowedNames.UnionWith(declaredOnLine);

        foreach (var character in line)
        {
            if (character == '(')
                pending.ParenthesisDepth++;
            else if (character == ')' && pending.ParenthesisDepth > 0)
                pending.ParenthesisDepth--;
        }

        if (pending.ParenthesisDepth == 0
            && VhdlSubprogramBodyMarkerRegex.IsMatch(line))
        {
            completed = new VhdlCompletedSubprogramHeader(
                pending.Name,
                new HashSet<string>(pending.ShadowedNames, StringComparer.OrdinalIgnoreCase));
            pending = null;
        }
        else if (pending.ParenthesisDepth == 0 && line.Contains(';'))
        {
            pending = null;
        }

        return declaredOnLine;
    }

    private static HashSet<string>? GetVhdlParameterNames(string line)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match parameterMatch in BoundedRegex.EnumerateMatches(VhdlParameterNamesRegex, line))
            AddVhdlDeclaredNames(result, parameterMatch.Groups["names"].Value);
        return result.Count == 0 ? null : result;
    }

    private static HashSet<string>? MergeVhdlDeclaredNames(
        HashSet<string>? first,
        HashSet<string>? second)
    {
        if (first == null)
            return second;
        if (second != null)
            first.UnionWith(second);
        return first;
    }

    private static bool TryGetVhdlDesignUnitKey(string line, out string key)
    {
        var architectureMatch = VhdlArchitectureRegex.Match(line);
        if (architectureMatch.Success)
        {
            key = $"entity:{architectureMatch.Groups["entity"].Value}";
            return true;
        }

        var entityMatch = VhdlEntityStartRegex.Match(line);
        if (entityMatch.Success)
        {
            key = $"entity:{entityMatch.Groups["name"].Value}";
            return true;
        }

        var packageMatch = VhdlPackageStartRegex.Match(line);
        if (packageMatch.Success)
        {
            key = $"package:{packageMatch.Groups["name"].Value}";
            return true;
        }

        var configurationMatch = VhdlConfigurationStartRegex.Match(line);
        if (configurationMatch.Success)
        {
            key = $"configuration:{configurationMatch.Groups["name"].Value}";
            return true;
        }

        key = string.Empty;
        return false;
    }

    private static HashSet<string>? GetVhdlDeclaredNames(string line)
    {
        Match? declarationMatch = null;
        if (VhdlFunctionStartRegex.IsMatch(line) || VhdlProcedureStartRegex.IsMatch(line))
        {
            var openParenthesis = line.IndexOf('(');
            var closeParenthesis = line.LastIndexOf(')');
            if (openParenthesis >= 0 && closeParenthesis > openParenthesis)
            {
                var parameters = line.Substring(
                    openParenthesis + 1,
                    closeParenthesis - openParenthesis - 1);
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match parameterMatch in BoundedRegex.EnumerateMatches(VhdlParameterNamesRegex, parameters))
                    AddVhdlDeclaredNames(result, parameterMatch.Groups["names"].Value);
                return result.Count == 0 ? null : result;
            }
        }
        else
        {
            declarationMatch = VhdlLocalDeclarationRegex.Match(line);
        }

        if (declarationMatch is not { Success: true })
            return null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddVhdlDeclaredNames(names, declarationMatch.Groups["names"].Value);
        return names.Count == 0 ? null : names;
    }

    private static void AddVhdlDeclaredNames(HashSet<string> names, string value)
    {
        foreach (var name in new DelimitedSpanEnumerable(
                     value.AsSpan(),
                     ',',
                     trimEntries: true,
                     removeEmptyEntries: true))
        {
            names.Add(name.ToString());
        }
    }

    private static string NormalizeVerilogScopeKind(string kind)
        => kind switch
        {
            "macromodule" or "primitive" or "program" => "module",
            "task" => "function",
            _ => kind,
        };

    private static string NormalizeVhdlScopeKind(string kind)
        => kind switch
        {
            "architecture" or "entity" or "configuration" => "module",
            "procedure" or "process" => "function",
            _ => kind,
        };

}
