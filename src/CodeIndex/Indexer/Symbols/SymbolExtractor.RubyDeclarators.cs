using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    // Ruby attr_accessor / attr_reader / attr_writer declarations can list multiple
    // names on one line (`attr_accessor :a, :b, :c`). The primary regex only captures
    // the first entry, so scan the tail for additional `:name` tokens and return the
    // complete declarator list when there is real fan-out.
    // Ruby の attr_accessor / attr_reader / attr_writer は 1 行に複数名を並べられる
    // (`attr_accessor :a, :b, :c`)。primary regex は先頭の 1 件しか捕まえないため、
    // tail を走査して残りの `:name` トークンを拾い、実際に fan-out があるときだけ
    // 完全な declarator list を返す。
    private static List<string>? TryExpandRubyAttrDeclaratorList(
        string patternMatchLine,
        int absoluteStartColumn,
        Match match,
        string firstName)
    {
        if (string.IsNullOrWhiteSpace(firstName))
            return null;

        var results = new List<string> { firstName };
        var tailStart = absoluteStartColumn + match.Length;
        if (tailStart >= patternMatchLine.Length)
            return null;

        var tail = patternMatchLine[tailStart..];
        var i = 0;
        while (i < tail.Length)
        {
            while (i < tail.Length && char.IsWhiteSpace(tail[i]))
                i++;
            if (i >= tail.Length)
                break;
            if (tail[i] != ',')
                break;

            i++;
            while (i < tail.Length && char.IsWhiteSpace(tail[i]))
                i++;
            if (i >= tail.Length || tail[i] != ':')
                return null;

            i++;
            var nameStart = i;
            while (i < tail.Length && (tail[i] == '_' || char.IsLetterOrDigit(tail[i])))
                i++;

            var name = tail[nameStart..i];
            if (name.Length == 0 || !IsRubyIdentifier(name))
                return null;

            results.Add(name);
        }

        return results.Count > 1 ? results : null;
    }

    private static bool IsRubyIdentifier(string value)
    {
        if (value.Length == 0)
            return false;

        if (value[0] != '_' && !char.IsLetter(value[0]))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch != '_' && !char.IsLetterOrDigit(ch))
                return false;
        }

        return true;
    }
}
