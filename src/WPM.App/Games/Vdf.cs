using System.Text;

namespace WPM.App.Games;

internal sealed class VdfNode
{
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VdfNode> children = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<KeyValuePair<string, VdfNode>> Children => children;
    public IEnumerable<KeyValuePair<string, string>> Values => values;
    public string? Get(string key) => values.GetValueOrDefault(key);
    public VdfNode? Child(string key) => children.GetValueOrDefault(key);
    public void Add(string key, string value) => values.TryAdd(key, value);
    public void Add(string key, VdfNode child) => children.TryAdd(key, child);
}

internal static class Vdf
{
    public static VdfNode Parse(string text)
    {
        int index = 0;
        return ReadBlock(text, ref index, 0);
    }

    private static VdfNode ReadBlock(string text, ref int index, int depth)
    {
        var node = new VdfNode();
        if (depth >= 32)
        {
            index = text.Length;
            return node;
        }

        while (ReadToken(text, ref index) is { } key)
        {
            SkipSpace(text, ref index);
            if (index < text.Length && text[index] == '{')
            {
                index++;
                node.Add(key, ReadBlock(text, ref index, depth + 1));
            }
            else if (ReadToken(text, ref index) is { } value)
            {
                node.Add(key, value);
            }
            else
            {
                break;
            }
        }

        return node;
    }

    private static string? ReadToken(string text, ref int index)
    {
        SkipSpace(text, ref index);
        if (index == text.Length)
        {
            return null;
        }

        char first = text[index++];
        if (first is '{' or '}')
        {
            return null;
        }

        if (first != '"')
        {
            int start = index - 1;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('{' or '}' or '"'))
            {
                index++;
            }

            return text[start..index];
        }

        var value = new StringBuilder();
        while (index < text.Length)
        {
            char current = text[index++];
            if (current == '"')
            {
                return value.ToString();
            }

            if (current == '\\' && index < text.Length && text[index] is '\\' or '"')
            {
                current = text[index++];
            }

            value.Append(current);
        }

        return null;
    }

    private static void SkipSpace(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
            }
            else if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n')
                {
                    index++;
                }
            }
            else
            {
                break;
            }
        }
    }
}
