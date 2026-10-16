using System.Text;

namespace XamlPort;

/// <summary>
/// A XAML markup extension, e.g. {Binding Path, Converter={StaticResource X}, StringFormat={}{0:F1}}.
/// Parsed shallowly: nested extensions stay as raw text in argument values.
/// </summary>
public sealed class Markup
{
    public string Name { get; set; } = "";
    public string? Positional { get; set; }
    public List<KeyValuePair<string, string>> Args { get; } = new();

    public string? Get(string key) =>
        Args.FirstOrDefault(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    public void Set(string key, string value)
    {
        var i = Args.FindIndex(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Args[i] = new(Args[i].Key, value);
        else Args.Add(new(key, value));
    }

    public void Remove(string key) =>
        Args.RemoveAll(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The binding path: the positional argument or Path=.</summary>
    public string? Path
    {
        get => Positional ?? Get("Path");
        set
        {
            Remove("Path");
            Positional = string.IsNullOrEmpty(value) ? null : value;
        }
    }

    public static bool IsMarkup(string? s) =>
        s != null && s.Length > 1 && s[0] == '{' && !s.StartsWith("{}");

    public static Markup? Parse(string s)
    {
        if (!IsMarkup(s)) return null;
        var t = s.Trim();
        if (!t.EndsWith("}")) return null;
        var inner = t[1..^1].Trim();

        var nameEnd = 0;
        while (nameEnd < inner.Length && !char.IsWhiteSpace(inner[nameEnd]) && inner[nameEnd] != ',') nameEnd++;
        var m = new Markup { Name = inner[..nameEnd] };
        var rest = inner[nameEnd..].Trim();
        if (rest.Length == 0) return m;

        foreach (var part in SplitTopLevel(rest))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            var eq = TopLevelEquals(p);
            if (eq < 0)
            {
                m.Positional = p;
            }
            else
            {
                m.Args.Add(new(p[..eq].Trim(), p[(eq + 1)..].Trim()));
            }
        }
        return m;
    }

    private static int TopLevelEquals(string s)
    {
        int depth = 0;
        bool quote = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'' ) quote = !quote;
            else if (quote) continue;
            else if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == '=' && depth == 0) return i;
        }
        return -1;
    }

    public static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        bool quote = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length) { sb.Append(c).Append(s[++i]); continue; }
            if (c == '\'') quote = !quote;
            if (!quote)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }
            }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    public override string ToString()
    {
        var sb = new StringBuilder("{").Append(Name);
        var first = true;
        if (!string.IsNullOrEmpty(Positional))
        {
            sb.Append(' ').Append(Positional);
            first = false;
        }
        foreach (var (k, v) in Args)
        {
            sb.Append(first ? " " : ", ").Append(k).Append('=').Append(v);
            first = false;
        }
        return sb.Append('}').ToString();
    }
}
