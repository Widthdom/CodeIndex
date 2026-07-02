using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindElixirRange(string[] lines, int startIndex)
    {
        var firstLine = lines[startIndex];
        if (!ElixirBlockStartRegex.IsMatch(firstLine))
            return (startIndex + 1, null, null);

        var scanState = default(ElixirMaskState);
        var maskedFirstLine = MaskElixirLineForBodyScan(firstLine, ref scanState);
        if (maskedFirstLine.Contains("do:", StringComparison.Ordinal)
            && ElixirDoShorthandRegex.IsMatch(maskedFirstLine))
        {
            return (startIndex + 1, startIndex + 1, startIndex + 1);
        }

        if (!MayContainElixirBlockToken(maskedFirstLine))
            return (startIndex + 1, null, null);

        var openerMatch = ElixirBlockTokenRegex.Match(maskedFirstLine);
        if (!openerMatch.Success || openerMatch.Value != "do")
            return (startIndex + 1, null, null);

        var depth = 1;
        int? bodyStartLine = null;

        var firstLineTail = maskedFirstLine[(openerMatch.Index + openerMatch.Length)..];
        if (!string.IsNullOrWhiteSpace(firstLineTail))
            bodyStartLine = startIndex + 1;

        if (MayContainElixirBlockToken(firstLineTail))
        {
            foreach (Match token in ElixirBlockTokenRegex.Matches(firstLineTail))
            {
                if (token.Value == "end")
                    depth--;
                else
                    depth++;

                if (depth == 0)
                    return (startIndex + 1, bodyStartLine ?? startIndex + 1, startIndex + 1);
            }
        }

        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            var masked = MaskElixirLineForBodyScan(lines[i], ref scanState);
            if (string.IsNullOrWhiteSpace(masked))
                continue;

            bodyStartLine ??= i + 1;
            if (!MayContainElixirBlockToken(masked))
                continue;

            foreach (Match token in ElixirBlockTokenRegex.Matches(masked))
            {
                if (token.Value == "end")
                    depth--;
                else
                    depth++;

                if (depth == 0)
                    return (i + 1, bodyStartLine, i + 1);
            }
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (lines.Length, bodyStartLine, lines.Length);
    }

    private static bool MayContainElixirBlockToken(string text) =>
        text.Contains("do", StringComparison.Ordinal)
        || text.Contains("fn", StringComparison.Ordinal)
        || text.Contains("end", StringComparison.Ordinal);

    private enum ElixirMaskMode
    {
        Normal,
        DoubleQuote,
        SingleQuote,
        TripleDoubleQuote,
        TripleSingleQuote,
        Sigil,
    }

    private struct ElixirMaskState
    {
        public ElixirMaskMode Mode;
        public char SigilOpen;
        public char SigilClose;
        public int SigilDepth;
    }

    private static string MaskElixirLineForBodyScan(string line, ref ElixirMaskState state)
    {
        if (line.Length == 0)
            return line;

        char[]? chars = null;

        void MaskAt(int index) =>
            (chars ??= line.ToCharArray())[index] = ' ';

        void MaskToEnd(int start)
        {
            var masked = chars ??= line.ToCharArray();
            for (int index = start; index < line.Length; index++)
                masked[index] = ' ';
        }

        for (int i = 0; i < line.Length; i++)
        {
            var current = line[i];

            switch (state.Mode)
            {
                case ElixirMaskMode.Normal:
                    if (current == '#')
                    {
                        MaskToEnd(i);
                        return new string(chars);
                    }

                    if (current == '"' || current == '\'')
                    {
                        bool triple = i + 2 < line.Length && line[i + 1] == current && line[i + 2] == current;
                        if (triple)
                        {
                            MaskAt(i);
                            MaskAt(i + 1);
                            MaskAt(i + 2);
                            state.Mode = current == '"' ? ElixirMaskMode.TripleDoubleQuote : ElixirMaskMode.TripleSingleQuote;
                            i += 2;
                        }
                        else
                        {
                            MaskAt(i);
                            state.Mode = current == '"' ? ElixirMaskMode.DoubleQuote : ElixirMaskMode.SingleQuote;
                        }
                        break;
                    }

                    if (current == '~' && i + 2 < line.Length && char.IsLetter(line[i + 1]))
                    {
                        var sigilOpen = line[i + 2];
                        if (TryGetElixirSigilClose(sigilOpen, out var sigilClose, out var nested))
                        {
                            MaskAt(i);
                            MaskAt(i + 1);
                            MaskAt(i + 2);
                            state.Mode = ElixirMaskMode.Sigil;
                            state.SigilOpen = sigilOpen;
                            state.SigilClose = sigilClose;
                            state.SigilDepth = nested ? 1 : 0;
                            i += 2;
                        }
                    }
                    break;

                case ElixirMaskMode.DoubleQuote:
                    MaskAt(i);
                    if (current == '\\' && i + 1 < line.Length)
                    {
                        MaskAt(++i);
                        continue;
                    }

                    if (current == '"')
                        state.Mode = ElixirMaskMode.Normal;
                    break;

                case ElixirMaskMode.SingleQuote:
                    MaskAt(i);
                    if (current == '\\' && i + 1 < line.Length)
                    {
                        MaskAt(++i);
                        continue;
                    }

                    if (current == '\'')
                        state.Mode = ElixirMaskMode.Normal;
                    break;

                case ElixirMaskMode.TripleDoubleQuote:
                    MaskAt(i);
                    if (current == '"' && i + 2 < line.Length && line[i + 1] == '"' && line[i + 2] == '"')
                    {
                        MaskAt(i);
                        MaskAt(i + 1);
                        MaskAt(i + 2);
                        state.Mode = ElixirMaskMode.Normal;
                        i += 2;
                    }
                    break;

                case ElixirMaskMode.TripleSingleQuote:
                    MaskAt(i);
                    if (current == '\'' && i + 2 < line.Length && line[i + 1] == '\'' && line[i + 2] == '\'')
                    {
                        MaskAt(i);
                        MaskAt(i + 1);
                        MaskAt(i + 2);
                        state.Mode = ElixirMaskMode.Normal;
                        i += 2;
                    }
                    break;

                case ElixirMaskMode.Sigil:
                    MaskAt(i);
                    if (current == '\\' && i + 1 < line.Length)
                    {
                        MaskAt(++i);
                        continue;
                    }

                    if (state.SigilDepth > 0)
                    {
                        if (current == state.SigilOpen)
                            state.SigilDepth++;
                        else if (current == state.SigilClose)
                        {
                            state.SigilDepth--;
                            if (state.SigilDepth == 0)
                                state.Mode = ElixirMaskMode.Normal;
                        }
                    }
                    else if (current == state.SigilClose)
                    {
                        state.Mode = ElixirMaskMode.Normal;
                    }
                    break;
            }
        }

        return chars is null ? line : new string(chars);
    }

    private static bool TryGetElixirSigilClose(char open, out char close, out bool nested)
    {
        nested = true;
        close = open;
        return open switch
        {
            '(' => SetSigilClose(')', true, out close, out nested),
            '[' => SetSigilClose(']', true, out close, out nested),
            '{' => SetSigilClose('}', true, out close, out nested),
            '<' => SetSigilClose('>', true, out close, out nested),
            '/' => SetSigilClose('/', false, out close, out nested),
            '|' => SetSigilClose('|', false, out close, out nested),
            '"' => SetSigilClose('"', false, out close, out nested),
            '\'' => SetSigilClose('\'', false, out close, out nested),
            _ => false,
        };
    }

    private static bool SetSigilClose(char sigilClose, bool isNested, out char close, out bool nested)
    {
        close = sigilClose;
        nested = isNested;
        return true;
    }

    private static readonly Regex ElixirBlockStartRegex = new(@"^\s*(?:defmodule|defprotocol|defimpl|defmacro|defguardp?|defp?)\b", RegexOptions.Compiled);
    private static readonly Regex ElixirBlockTokenRegex = new(@"\b(?:do|fn|end)\b(?!:)", RegexOptions.Compiled);
    private static readonly Regex ElixirDoShorthandRegex = new(@",\s*do:\s*", RegexOptions.Compiled);

}
