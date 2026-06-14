namespace CodeIndex.Database;

internal static class SqliteIdentifier
{
    public static string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            throw new ArgumentException("SQLite identifier must not be empty.", nameof(identifier));
        if (identifier.IndexOf('\0') >= 0)
            throw new ArgumentException("SQLite identifier must not contain NUL characters.", nameof(identifier));

        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public static string ValidatePragmaName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("SQLite pragma name must not be empty.", nameof(name));

        if (!IsBareIdentifier(name))
            throw new ArgumentException($"SQLite pragma name is not a safe bare identifier: {name}", nameof(name));

        return name;
    }

    private static bool IsBareIdentifier(string value)
    {
        if (!(value[0] == '_' || IsAsciiLetter(value[0])))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch != '_' && !IsAsciiLetter(ch) && !char.IsAsciiDigit(ch))
                return false;
        }

        return true;
    }

    private static bool IsAsciiLetter(char ch)
        => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');
}
