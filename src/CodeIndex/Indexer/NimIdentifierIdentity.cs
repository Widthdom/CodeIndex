using System.Text;

namespace CodeIndex.Indexer;

internal static class NimIdentifierIdentity
{
    internal static string? Fold(string? value)
    {
        if (value == null)
            return null;

        var folded = new StringBuilder(value.Length);
        var atIdentifierStart = true;
        foreach (var character in value)
        {
            if (character == '_')
                continue;

            if (char.IsLetterOrDigit(character))
            {
                folded.Append(atIdentifierStart ? character : char.ToLowerInvariant(character));
                atIdentifierStart = false;
                continue;
            }

            folded.Append(character);
            atIdentifierStart = true;
        }

        return folded.ToString();
    }
}
