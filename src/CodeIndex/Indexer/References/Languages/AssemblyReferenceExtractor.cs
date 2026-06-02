using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class AssemblyReferenceExtractor
{
    private static readonly Regex InstructionRegex = new(
        @"^\s*(?:(?:[A-Za-z_.$?@][A-Za-z0-9_.$?@]*|\d+)\s*:\s*)?(?<mnemonic>[A-Za-z.][A-Za-z0-9.]*)\b(?<operands>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TargetNameRegex = new(
        @"(?<name>[A-Za-z_.$?@][A-Za-z0-9_.$?@]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> RegisterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "zero", "at", "v0", "v1", "a0", "a1", "a2", "a3",
        "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8", "t9",
        "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8",
        "k0", "k1", "gp", "sp", "fp", "ra", "pc", "lr",
        "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp",
        "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp",
        "ax", "bx", "cx", "dx", "al", "ah", "bl", "bh", "cl", "ch", "dl", "dh",
    };

    private static readonly HashSet<string> BranchMnemonics = new(StringComparer.OrdinalIgnoreCase)
    {
        "beq", "bne", "bcs", "bhs", "bcc", "blo", "bmi", "bpl", "bvs", "bvc",
        "bhi", "bls", "bge", "blt", "bgt", "ble", "bal", "bnv",
        "beqz", "bnez", "blez", "bgez", "bltz", "bgtz", "bltu", "bgeu",
    };

    public static void EmitInstructionTargetReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var codeLine = SymbolExtractor.StripAssemblyComment(originalLine, preserveHashImmediates: true);
        if (string.IsNullOrWhiteSpace(codeLine))
            return;

        var match = InstructionRegex.Match(codeLine);
        if (!match.Success)
            return;

        var mnemonic = match.Groups["mnemonic"].Value;
        if (!IsTargetBearingInstruction(mnemonic))
            return;

        var operands = match.Groups["operands"].Value;
        if (!TryResolveTargetOperand(mnemonic, operands, out var targetOperand))
            return;

        if (!TryExtractTargetName(targetOperand, out var targetName))
            return;

        var targetIndexInOperands = operands.IndexOf(targetOperand, StringComparison.Ordinal);
        if (targetIndexInOperands < 0)
            return;

        var targetNameIndexInOperand = targetOperand.IndexOf(targetName, StringComparison.Ordinal);
        if (targetNameIndexInOperand < 0)
            return;

        var targetColumn = match.Groups["operands"].Index + targetIndexInOperands + targetNameIndexInOperand;
        var container = resolveContainerForCall(targetColumn);
        ReferenceExtractor.AddReference(references, seen, fileId, targetName, targetColumn, "call", context, lineNumber, container);
    }

    private static bool IsTargetBearingInstruction(string mnemonic)
    {
        var lower = NormalizeTargetMnemonic(mnemonic);
        return IsBaseTargetBearingInstruction(lower);
    }

    private static bool IsBaseTargetBearingInstruction(string lower)
    {
        return lower is "call" or "callq" or "lcall" or "bl" or "blx" or "bsr" or "jsr" or "rcall"
            || lower is "jmp" or "jmpq" or "ljmp" or "bra" or "br" or "b"
            || lower.StartsWith("j", StringComparison.Ordinal)
            || lower.StartsWith("b.", StringComparison.Ordinal)
            || BranchMnemonics.Contains(lower)
            || lower is "cbz" or "cbnz" or "tbz" or "tbnz"
            || lower is "jal" or "jalr"
            || lower is "loop" or "loope" or "loopne" or "loopnz" or "loopz";
    }

    private static string NormalizeTargetMnemonic(string mnemonic)
    {
        var lower = mnemonic.ToLowerInvariant();
        if (lower.EndsWith(".n", StringComparison.Ordinal) || lower.EndsWith(".w", StringComparison.Ordinal))
        {
            var baseMnemonic = lower[..^2];
            if (IsBaseTargetBearingInstruction(baseMnemonic))
                return baseMnemonic;
        }

        return lower;
    }

    private static bool TryResolveTargetOperand(string mnemonic, string operands, out string targetOperand)
    {
        targetOperand = string.Empty;
        var parts = SplitOperands(operands);
        if (parts.Count == 0)
            return false;

        var lower = NormalizeTargetMnemonic(mnemonic);
        targetOperand = lower is "call" or "callq" or "lcall" or "bl" or "blx" or "bsr" or "jsr" or "rcall"
            ? parts[0]
            : parts[^1];
        targetOperand = targetOperand.Trim();
        return targetOperand.Length > 0;
    }

    private static List<string> SplitOperands(string operands)
    {
        var parts = new List<string>();
        var start = 0;
        var bracketDepth = 0;
        var parenDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < operands.Length; i++)
        {
            var c = operands[i];
            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (inSingleQuote || inDoubleQuote)
                continue;

            if (c == '[')
                bracketDepth++;
            else if (c == ']' && bracketDepth > 0)
                bracketDepth--;
            else if (c == '(')
                parenDepth++;
            else if (c == ')' && parenDepth > 0)
                parenDepth--;
            else if (c == ',' && bracketDepth == 0 && parenDepth == 0)
            {
                parts.Add(operands[start..i]);
                start = i + 1;
            }
        }

        parts.Add(operands[start..]);
        return parts;
    }

    private static bool TryExtractTargetName(string operand, out string targetName)
    {
        targetName = string.Empty;
        var candidate = StripAssemblyTargetDecorators(operand.Trim());
        if (candidate.Length == 0 || IsIndirectAssemblyTarget(candidate))
            return false;

        var match = TargetNameRegex.Match(candidate);
        if (!match.Success || match.Index != 0)
            return false;

        targetName = NormalizeAssemblyReferenceName(match.Groups["name"].Value);
        return targetName.Length > 0 && !IsRegisterLikeName(targetName);
    }

    private static string StripAssemblyTargetDecorators(string operand)
    {
        var candidate = operand;
        string[] prefixes =
        [
            "short", "near", "far", "ptr", "offset",
            "byte", "word", "dword", "qword", "tword", "oword", "xmmword", "ymmword", "zmmword",
            "byte ptr", "word ptr", "dword ptr", "qword ptr", "tword ptr", "oword ptr",
        ];

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var prefix in prefixes)
            {
                var stripped = StripAssemblyTargetPrefix(candidate, prefix);
                if (stripped is not null)
                {
                    candidate = stripped;
                    changed = true;
                }
            }
        }

        return candidate;
    }

    private static string? StripAssemblyTargetPrefix(string candidate, string prefix)
    {
        if (candidate.Length <= prefix.Length
            || !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(candidate[prefix.Length]))
            return null;

        var index = prefix.Length + 1;
        while (index < candidate.Length && char.IsWhiteSpace(candidate[index]))
            index++;

        return candidate[index..];
    }

    private static bool IsIndirectAssemblyTarget(string candidate)
    {
        if (candidate.Length == 0)
            return true;

        if (candidate[0] is '*' or '%' or '[' or '(')
            return true;

        if (candidate[0] == '$' && (candidate.Length == 1 || !IsAssemblyIdentifierChar(candidate[1])))
            return true;

        return false;
    }

    private static string NormalizeAssemblyReferenceName(string name)
    {
        string[] relocationSuffixes =
        [
            "@PLT", "@GOT", "@GOTPCREL", "@GOTOFF", "@TLSGD", "@TPOFF", "@PAGE", "@PAGEOFF",
        ];

        foreach (var suffix in relocationSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];
        }

        return name;
    }

    private static bool IsAssemblyIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c is '_' or '.' or '$' or '?' or '@';

    private static bool IsRegisterLikeName(string name)
    {
        var normalized = name.TrimStart('%', '$');
        if (normalized.Length == 0)
            return true;

        if (RegisterNames.Contains(normalized))
            return true;

        if (normalized.Length < 2)
            return false;

        if (normalized[0] is 'r' or 'x' or 'w')
            return normalized[1..].All(char.IsDigit);

        return false;
    }
}
