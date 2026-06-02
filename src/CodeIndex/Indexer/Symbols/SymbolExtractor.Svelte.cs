using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractSvelteReactiveSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var match = SvelteReactivePropertyRegex.Match(lines[i]);
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = name,
                Line = i + 1,
                StartLine = i + 1,
                EndLine = i + 1,
                Signature = lines[i].Trim(),
            });
        }
    }

}
