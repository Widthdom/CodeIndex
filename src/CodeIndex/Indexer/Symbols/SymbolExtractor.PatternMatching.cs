using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static PatternScanResult TryCapturePatternMatch(
        PatternLineScanContext lineContext,
        SymbolPattern pattern,
        int patternIndex,
        int patternStartOffset,
        int lineOffset,
        ref string patternMatchLine,
        ref string? csharpWrappedModifierPrefix,
        ref PatternStartScanState patternStartState,
        out CapturedPatternMatch capturedMatch)
    {
        capturedMatch = default;
        var extraction = lineContext.Extraction;
        var lang = extraction.Lang;
        var i = lineContext.LineIndex;
        var matchLine = lineContext.PreparedLine.MatchLine;
        var applicablePatterns = extraction.ApplicablePatterns;
        var applyRequiredLiteralMatchInputGate = extraction.ApplyRequiredLiteralMatchInputGate;
        var requiredLiteralGateCounts = extraction.RequiredLiteralGateCounts;
        var applyCSharpRegexProbeOptimizations = extraction.ApplyCSharpRegexProbeOptimizations;
        var csharpRegexProbeCounts = extraction.CSharpRegexProbeCounts;
        var csharpMatchLines = extraction.ScanInputs.CSharpMatchLines;
        var javaLeadingAnnotationOffset = 0;
        var patternMatchTimedOut = false;
        // BuildCSharpPropertyMatchLine may replace the physical line with a merged multiline
        // input. A miss on one representation proves nothing about the other, so recovery
        // sharing is restricted to the exact PreparedLine.MatchLine at this candidate start.
        // multiline 結合 input と物理行の miss は相互に証明にならないため、同じ candidate
        // start の PreparedLine.MatchLine そのものだけを recovery cache の対象にする。
        var canUseCSharpPhysicalInputNegativePrefix = lang == "csharp"
            && applyCSharpRegexProbeOptimizations
            && lineOffset == patternStartOffset
            && ReferenceEquals(patternMatchLine, matchLine);
        Match match;
        if (canUseCSharpPhysicalInputNegativePrefix
            && patternStartState.CSharpPhysicalInputNegativePrefix.IsKnownNegative(patternIndex))
        {
            if (csharpRegexProbeCounts != null)
                csharpRegexProbeCounts.PhysicalInputNegativePrefixCacheHitCount++;
            match = Match.Empty;
        }
        else if (lang is "java" or "kotlin")
        {
            var javaPatternMatched = TryMatchJavaDeclarationPatternSegment(
                pattern,
                patternMatchLine,
                lineOffset,
                lang == "kotlin",
                applyRequiredLiteralMatchInputGate,
                requiredLiteralGateCounts,
                out match,
                out javaLeadingAnnotationOffset,
                out var initialJavaInputAttempted);
            if (!javaPatternMatched
                && initialJavaInputAttempted
                && ShouldAttemptPatternRegex(
                    pattern,
                    patternMatchLine.AsSpan(lineOffset),
                    applyRequiredLiteralMatchInputGate,
                    requiredLiteralGateCounts,
                    applyCSharpRegexProbeOptimizations,
                    csharpRegexProbeCounts))
            {
                // Preserve the existing failed-helper fallback attempt. This is
                // intentionally gated again because it is a distinct regex call.
                match = pattern.Regex.Match(patternMatchLine[lineOffset..]);
            }
        }
        else if (ShouldAttemptPatternRegex(
                     pattern,
                     patternMatchLine.AsSpan(lineOffset),
                     applyRequiredLiteralMatchInputGate,
                     requiredLiteralGateCounts,
                     applyCSharpRegexProbeOptimizations,
                     csharpRegexProbeCounts))
        {
            if (csharpRegexProbeCounts != null)
                csharpRegexProbeCounts.DeclarationPatternRegexAttemptCount++;
            match = canUseCSharpPhysicalInputNegativePrefix
                ? pattern.Regex.MatchWithTimeoutStatus(
                    patternMatchLine[lineOffset..],
                    out patternMatchTimedOut)
                : pattern.Regex.Match(patternMatchLine[lineOffset..]);
        }
        else
        {
            match = Match.Empty;
        }

        if (canUseCSharpPhysicalInputNegativePrefix && !match.Success)
        {
            patternStartState.CSharpPhysicalInputNegativePrefix.RecordFailedProbe(
                patternIndex,
                patternMatchTimedOut);
        }

        if (!match.Success
            && lang == "csharp"
            && pattern.Kind == "function"
            && lineOffset == 0
            && csharpMatchLines != null
            && csharpWrappedModifierPrefix == null)
        {
            // Wrapped leading modifier recovery: when a C# function-kind pattern
            // fails at column 0 of the identifier line, try prepending the
            // modifier prefix accumulated from preceding modifier-only lines
            // (`static\nFoo() { ... }`, `public\nBar() { ... }`, etc.) and retry.
            // The method regex already tolerates an omitted modifier run, so it
            // matches on the identifier line alone — this branch only fires for
            // constructor / static-constructor shapes that require the modifier
            // on the same line as the name. Closes #348.
            // ラップされた先頭モディファイアの救済: C# の function 系パターンが
            // 識別子行の先頭マッチに失敗した場合、直前のモディファイアのみ行
            // （`static\nFoo() { ... }` や `public\nBar() { ... }` 等）から
            // 再構築した prefix を付け直して再試行する。メソッド regex は
            // 先頭モディファイアが無くても識別子行単体でマッチするため、この
            // 分岐は修飾子が識別子と同行に必要な constructor / static ctor
            // シェイプでのみ発火する。Closes #348.
            var wrappedInfo = GetCSharpWrappedHeaderModifier(
                csharpMatchLines!,
                i,
                ref patternStartState,
                applyCSharpRegexProbeOptimizations,
                csharpRegexProbeCounts);
            if (wrappedInfo != null)
            {
                foreach (var candidatePrefix in EnumerateCSharpWrappedModifierCandidates(wrappedInfo.Value))
                {
                    var wrappedMatchLine = BuildCSharpWrappedPatternMatchLine(
                        candidatePrefix,
                        patternMatchLine,
                        csharpRegexProbeCounts);
                    if (!ShouldAttemptPatternRegex(
                            pattern,
                            wrappedMatchLine.AsSpan(),
                            applyRequiredLiteralMatchInputGate,
                            requiredLiteralGateCounts,
                            applyCSharpRegexProbeOptimizations,
                            csharpRegexProbeCounts))
                    {
                        continue;
                    }

                    if (csharpRegexProbeCounts != null)
                        csharpRegexProbeCounts.DeclarationPatternRegexAttemptCount++;
                    var wrappedMatch = pattern.Regex.Match(wrappedMatchLine);
                    if (wrappedMatch.Success)
                    {
                        match = wrappedMatch;
                        patternMatchLine = wrappedMatchLine;
                        // Preserve the full prefix in the stored signature so
                        // declarations like `public\nstatic\nP1()` retain
                        // `public static P1()`, even when the matching regex
                        // variant only accepted `static P1()`. Closes #348.
                        // シグネチャには完全な prefix を残し、`public\nstatic\nP1()`
                        // のような宣言を `public static P1()` として保存する。
                        // マッチした regex 変種が `static P1()` 形だけを受け付けた
                        // 場合でも、保存シグネチャは完全な prefix を保持する。Closes #348.
                        csharpWrappedModifierPrefix = wrappedInfo.Value.Prefix;
                        break;
                    }
                }
            }
        }

        if (!match.Success)
        {
            if (lang == "csharp"
                && pattern.Kind == "property"
                && pattern.BodyStyle == BodyStyle.Brace
                && (lineOffset != patternStartOffset
                    ? ShouldDeferCSharpBracePropertySameLineAdvance(matchLine, lineOffset)
                    : patternStartState.DeferCSharpBraceProperty ??=
                        ShouldDeferCSharpBracePropertySameLineAdvance(matchLine, lineOffset)))
            {
                return PatternScanResult.NextPattern;
            }

            if (lang == "csharp"
                && pattern.Kind == "function"
                && (lineOffset != patternStartOffset
                    ? ShouldDeferCSharpFunctionSameLineAdvance(matchLine, lineOffset)
                    : patternStartState.DeferCSharpFunction ??=
                        ShouldDeferCSharpFunctionSameLineAdvance(matchLine, lineOffset)))
            {
                return PatternScanResult.NextPattern;
            }

            if (lang == "csharp"
                && pattern.Kind is "event" or "delegate"
                && pattern.BodyStyle == BodyStyle.None
                && (lineOffset != patternStartOffset
                    ? ShouldDeferCSharpEventOrDelegateSameLineAdvance(matchLine, lineOffset, pattern.Kind)
                    : pattern.Kind == "event"
                        ? patternStartState.DeferCSharpEvent ??=
                            ShouldDeferCSharpEventOrDelegateSameLineAdvance(matchLine, lineOffset, pattern.Kind)
                        : patternStartState.DeferCSharpDelegate ??=
                            ShouldDeferCSharpEventOrDelegateSameLineAdvance(matchLine, lineOffset, pattern.Kind)))
            {
                return PatternScanResult.NextPattern;
            }

            if (lang is "javascript" or "typescript" or "css" or "java"
                || (lang == "csharp"
                    && pattern.Kind == "enum"
                    && pattern.BodyStyle == BodyStyle.Brace
                    && patternStartOffset > 0)
                || (lang == "csharp"
                    && pattern.Kind == "property"
                    && pattern.BodyStyle == BodyStyle.None
                    && !(lineOffset != patternStartOffset
                        ? TryMatchAnyRecoverableCSharpPattern(
                            matchLine,
                            lineOffset,
                            insideEnumBody: false,
                            attributeParenDepth: 0,
                            applicablePatterns,
                            applyRequiredLiteralMatchInputGate,
                            requiredLiteralGateCounts,
                            applyCSharpRegexProbeOptimizations,
                            csharpRegexProbeCounts)
                        : patternStartState.RecoverableCSharpPattern ??=
                            TryMatchAnyRecoverableCSharpPatternAtPatternStart(
                                matchLine,
                                lineOffset,
                                insideEnumBody: false,
                                attributeParenDepth: 0,
                                applicablePatterns,
                                applyRequiredLiteralMatchInputGate,
                                requiredLiteralGateCounts,
                                applyCSharpRegexProbeOptimizations,
                                csharpRegexProbeCounts,
                                ref patternStartState.CSharpPhysicalInputNegativePrefix))))
            {
                lineOffset = FindNextSameLineBraceStatementStart(matchLine, lineOffset + 1, lang);
                return PatternScanResult.ContinueAt(lineOffset);
            }

            return PatternScanResult.NextPattern;
        }

        var absoluteStartColumn = lineOffset + match.Index;
        if (lang is "java" or "kotlin" && javaLeadingAnnotationOffset > 0)
            absoluteStartColumn = lineOffset + javaLeadingAnnotationOffset;

        capturedMatch = new CapturedPatternMatch(match, absoluteStartColumn);
        return PatternScanResult.Accepted;
    }
}
