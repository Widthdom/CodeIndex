using System.Text;

namespace CodeIndex;

internal sealed class BoundedTextWriter(int maxChars) : TextWriter
{
    private readonly StringBuilder builder = new();
    private bool truncated;

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (builder.Length < maxChars)
        {
            builder.Append(value);
            return;
        }

        truncated = true;
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        Append(value.AsSpan());
    }

    public override void Write(char[] buffer, int index, int count)
        => Append(buffer.AsSpan(index, count));

    internal string GetCapturedText()
    {
        if (!truncated)
            return builder.ToString();

        return builder
            .AppendLine()
            .Append("[cdidx] captured worker console output truncated.")
            .ToString();
    }

    private void Append(ReadOnlySpan<char> value)
    {
        var remaining = maxChars - builder.Length;
        if (remaining <= 0)
        {
            truncated = true;
            return;
        }

        if (value.Length <= remaining)
        {
            builder.Append(value);
            return;
        }

        builder.Append(value[..remaining]);
        truncated = true;
    }
}
