namespace CodeIndex.Indexer;

internal static partial class StructuralLineMasker
{
    private static void MaskCSharpRawStringContents(string[] lines)
    {
        var frames = new Stack<ScannerFrame>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;

            char[]? masked = null;
            var searchStart = 0;

            char[] GetMaskedLine()
                => masked ??= line.ToCharArray();

            void MaskAt(int index)
                => GetMaskedLine()[index] = ' ';

            void MaskRange(int start, int length)
                => ReplaceWithSpaces(GetMaskedLine(), start, length);

            while (searchStart < line.Length)
            {
                if (frames.TryPeek(out var activeFrame))
                {
                    if (activeFrame is BlockCommentFrame)
                    {
                        if (StartsWith(line, searchStart, "*/"))
                        {
                            MaskRange(searchStart, 2);
                            searchStart += 2;
                            frames.Pop();
                            continue;
                        }

                        MaskAt(searchStart);
                        searchStart++;
                        continue;
                    }

                    if (activeFrame is CharLiteralFrame)
                    {
                        if (line[searchStart] == '\\')
                        {
                            searchStart += Math.Min(2, line.Length - searchStart);
                            continue;
                        }

                        if (line[searchStart] == '\'')
                            frames.Pop();

                        searchStart++;
                        continue;
                    }

                    if (activeFrame is StringFrame stringFrame)
                    {
                        if (stringFrame.InterpolationBraceCount == 1 && stringFrame.Kind != StringKind.Raw)
                        {
                            if (StartsWith(line, searchStart, "{{") || StartsWith(line, searchStart, "}}"))
                            {
                                MaskRange(searchStart, 2);
                                searchStart += 2;
                                continue;
                            }
                        }

                        if (stringFrame.InterpolationBraceCount > 0)
                        {
                            var openBraceRun = CountRun(line, searchStart, '{');
                            if (openBraceRun >= stringFrame.InterpolationBraceCount)
                            {
                                MaskRange(searchStart, stringFrame.InterpolationBraceCount);
                                searchStart += stringFrame.InterpolationBraceCount;
                                frames.Push(new InterpolationFrame { CloseBraceCount = stringFrame.InterpolationBraceCount });
                                continue;
                            }
                        }

                        if (stringFrame.Kind == StringKind.Raw)
                        {
                            var closeLength = CountQuoteRun(line, searchStart);
                            if (closeLength == stringFrame.DelimiterLength)
                            {
                                MaskRange(searchStart, closeLength);
                                searchStart += closeLength;
                                frames.Pop();
                                continue;
                            }

                            if (closeLength > 0)
                            {
                                MaskRange(searchStart, closeLength);
                                searchStart += closeLength;
                                continue;
                            }

                            MaskAt(searchStart++);
                            continue;
                        }

                        if (stringFrame.Kind == StringKind.Verbatim)
                        {
                            if (line[searchStart] == '"' && searchStart + 1 < line.Length && line[searchStart + 1] == '"')
                            {
                                MaskRange(searchStart, 2);
                                searchStart += 2;
                                continue;
                            }

                            MaskAt(searchStart);
                            if (line[searchStart] == '"')
                                frames.Pop();

                            searchStart++;
                            continue;
                        }

                        MaskAt(searchStart);
                        if (line[searchStart] == '\\')
                        {
                            if (searchStart + 1 < line.Length)
                                MaskAt(searchStart + 1);

                            searchStart += Math.Min(2, line.Length - searchStart);
                            continue;
                        }

                        if (line[searchStart] == '"')
                            frames.Pop();

                        searchStart++;
                        continue;
                    }

                    if (activeFrame is InterpolationFrame interpolationFrame)
                    {
                        if (StartsWith(line, searchStart, "//"))
                            break;

                        if (StartsWith(line, searchStart, "/*"))
                        {
                            MaskRange(searchStart, 2);
                            frames.Push(SharedBlockCommentFrame);
                            searchStart += 2;
                            continue;
                        }

                        if (TryStartString(line, searchStart, out var nestedStringLength, out var nestedStringFrame))
                        {
                            MaskRange(searchStart, nestedStringLength);
                            searchStart += nestedStringLength;
                            frames.Push(nestedStringFrame);
                            continue;
                        }

                        if (line[searchStart] == '\'')
                        {
                            frames.Push(SharedCharLiteralFrame);
                            searchStart++;
                            continue;
                        }

                        var closeBraceRun = CountRun(line, searchStart, '}');
                        if (interpolationFrame.NestedBraceDepth == 0 && closeBraceRun >= interpolationFrame.CloseBraceCount)
                        {
                            MaskRange(searchStart, interpolationFrame.CloseBraceCount);
                            searchStart += interpolationFrame.CloseBraceCount;
                            frames.Pop();
                            continue;
                        }

                        if (line[searchStart] == '{')
                        {
                            interpolationFrame.NestedBraceDepth++;
                            searchStart++;
                            continue;
                        }

                        if (line[searchStart] == '}' && interpolationFrame.NestedBraceDepth > 0)
                        {
                            interpolationFrame.NestedBraceDepth--;
                            searchStart++;
                            continue;
                        }

                        searchStart++;
                        continue;
                    }
                }

                if (StartsWith(line, searchStart, "//"))
                    break;

                if (StartsWith(line, searchStart, "/*"))
                {
                    MaskRange(searchStart, 2);
                    frames.Push(SharedBlockCommentFrame);
                    searchStart += 2;
                    continue;
                }

                if (TryStartString(line, searchStart, out var openingLength, out var openingFrame))
                {
                    MaskRange(searchStart, openingLength);
                    searchStart += openingLength;
                    frames.Push(openingFrame);
                    continue;
                }

                if (line[searchStart] == '\'')
                {
                    frames.Push(SharedCharLiteralFrame);
                    searchStart++;
                    continue;
                }

                searchStart++;
            }

            if (masked != null)
                lines[i] = new string(masked);
        }
    }


}
