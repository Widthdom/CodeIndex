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
        if (ElixirDoShorthandRegex.IsMatch(maskedFirstLine))
            return (startIndex + 1, startIndex + 1, startIndex + 1);

        var openerMatch = ElixirBlockTokenRegex.Match(maskedFirstLine);
        if (!openerMatch.Success || openerMatch.Value != "do")
            return (startIndex + 1, null, null);

        var depth = 1;
        int? bodyStartLine = null;

        var firstLineTail = maskedFirstLine[(openerMatch.Index + openerMatch.Length)..];
        if (!string.IsNullOrWhiteSpace(firstLineTail))
            bodyStartLine = startIndex + 1;

        foreach (Match token in ElixirBlockTokenRegex.Matches(firstLineTail))
        {
            if (token.Value == "end")
                depth--;
            else
                depth++;

            if (depth == 0)
                return (startIndex + 1, bodyStartLine ?? startIndex + 1, startIndex + 1);
        }

        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            var masked = MaskElixirLineForBodyScan(lines[i], ref scanState);
            if (string.IsNullOrWhiteSpace(masked))
                continue;

            bodyStartLine ??= i + 1;

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

        var chars = line.ToCharArray();

        for (int i = 0; i < chars.Length; i++)
        {
            var current = chars[i];

            switch (state.Mode)
            {
                case ElixirMaskMode.Normal:
                    if (current == '#')
                    {
                        for (int j = i; j < chars.Length; j++)
                            chars[j] = ' ';
                        return new string(chars);
                    }

                    if (current == '"' || current == '\'')
                    {
                        bool triple = i + 2 < chars.Length && chars[i + 1] == current && chars[i + 2] == current;
                        if (triple)
                        {
                            chars[i] = chars[i + 1] = chars[i + 2] = ' ';
                            state.Mode = current == '"' ? ElixirMaskMode.TripleDoubleQuote : ElixirMaskMode.TripleSingleQuote;
                            i += 2;
                        }
                        else
                        {
                            chars[i] = ' ';
                            state.Mode = current == '"' ? ElixirMaskMode.DoubleQuote : ElixirMaskMode.SingleQuote;
                        }
                        break;
                    }

                    if (current == '~' && i + 2 < chars.Length && char.IsLetter(chars[i + 1]))
                    {
                        var sigilOpen = chars[i + 2];
                        if (TryGetElixirSigilClose(sigilOpen, out var sigilClose, out var nested))
                        {
                            chars[i] = chars[i + 1] = chars[i + 2] = ' ';
                            state.Mode = ElixirMaskMode.Sigil;
                            state.SigilOpen = sigilOpen;
                            state.SigilClose = sigilClose;
                            state.SigilDepth = nested ? 1 : 0;
                            i += 2;
                        }
                    }
                    break;

                case ElixirMaskMode.DoubleQuote:
                    chars[i] = ' ';
                    if (current == '\\' && i + 1 < chars.Length)
                    {
                        chars[++i] = ' ';
                        continue;
                    }

                    if (current == '"')
                        state.Mode = ElixirMaskMode.Normal;
                    break;

                case ElixirMaskMode.SingleQuote:
                    chars[i] = ' ';
                    if (current == '\\' && i + 1 < chars.Length)
                    {
                        chars[++i] = ' ';
                        continue;
                    }

                    if (current == '\'')
                        state.Mode = ElixirMaskMode.Normal;
                    break;

                case ElixirMaskMode.TripleDoubleQuote:
                    chars[i] = ' ';
                    if (current == '"' && i + 2 < chars.Length && chars[i + 1] == '"' && chars[i + 2] == '"')
                    {
                        chars[i] = chars[i + 1] = chars[i + 2] = ' ';
                        state.Mode = ElixirMaskMode.Normal;
                        i += 2;
                    }
                    break;

                case ElixirMaskMode.TripleSingleQuote:
                    chars[i] = ' ';
                    if (current == '\'' && i + 2 < chars.Length && chars[i + 1] == '\'' && chars[i + 2] == '\'')
                    {
                        chars[i] = chars[i + 1] = chars[i + 2] = ' ';
                        state.Mode = ElixirMaskMode.Normal;
                        i += 2;
                    }
                    break;

                case ElixirMaskMode.Sigil:
                    chars[i] = ' ';
                    if (current == '\\' && i + 1 < chars.Length)
                    {
                        chars[++i] = ' ';
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

        return new string(chars);
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
