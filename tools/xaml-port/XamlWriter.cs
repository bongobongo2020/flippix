using System.Text;
using System.Xml.Linq;

namespace XamlPort;

/// <summary>Writes XAML the way people format it: short elements on one line, long ones one attribute per line.</summary>
public static class XamlWriter
{
    public static string Write(XDocument doc)
    {
        var sb = new StringBuilder();
        WriteElement(sb, doc.Root!, 0, isRoot: true);
        return sb.ToString();
    }

    private static string Name(XElement e, XName n)
    {
        if (n.Namespace == XNamespace.None) return n.LocalName;
        if (n.Namespace == XNamespace.Xmlns) return "xmlns:" + n.LocalName;
        var prefix = e.GetPrefixOfNamespace(n.Namespace);
        var root = e.Document?.Root ?? e;
        if (prefix == null)
        {
            // Default namespace of the document.
            var def = root.Attribute("xmlns")?.Value;
            if (def == n.Namespace.NamespaceName) return n.LocalName;
            foreach (var a in root.Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.Xmlns))
                if (a.Value == n.Namespace.NamespaceName) return a.Name.LocalName + ":" + n.LocalName;
            return n.LocalName;
        }
        return prefix + ":" + n.LocalName;
    }

    private static string Escape(string v) =>
        v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")
         .Replace("\n", "&#10;").Replace("\r", "&#13;").Replace("\t", "&#9;");

    private static string EscapeText(string v) => v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static void WriteElement(StringBuilder sb, XElement e, int depth, bool isRoot = false)
    {
        var indent = new string(' ', depth * 4);
        var name = ElementName(e);
        var attrs = e.Attributes().Select(a => a.Name.ToString() == "xmlns" ? $"xmlns=\"{Escape(a.Value)}\"" : $"{Name(e, a.Name)}=\"{Escape(a.Value)}\"").ToList();

        sb.Append(indent).Append('<').Append(name);
        var oneLine = string.Join(" ", attrs);
        if (!isRoot && indent.Length + name.Length + oneLine.Length < 150)
        {
            if (attrs.Count > 0) sb.Append(' ').Append(oneLine);
        }
        else
        {
            var pad = new string(' ', indent.Length + name.Length + 2);
            for (var i = 0; i < attrs.Count; i++)
            {
                if (i == 0) sb.Append(' ').Append(attrs[i]);
                else sb.Append('\n').Append(pad).Append(attrs[i]);
            }
        }

        var nodes = e.Nodes().ToList();
        if (nodes.Count == 0)
        {
            sb.Append("/>\n");
            return;
        }
        if (nodes.Count == 1 && nodes[0] is XText onlyText)
        {
            sb.Append('>').Append(EscapeText(onlyText.Value)).Append("</").Append(name).Append(">\n");
            return;
        }
        sb.Append(">\n");
        foreach (var n in nodes)
        {
            switch (n)
            {
                case XElement ce:
                    WriteElement(sb, ce, depth + 1);
                    break;
                case XComment c:
                    sb.Append(new string(' ', (depth + 1) * 4)).Append("<!--").Append(c.Value).Append("-->\n");
                    break;
                case XText t:
                    sb.Append(new string(' ', (depth + 1) * 4)).Append(EscapeText(t.Value)).Append('\n');
                    break;
            }
        }
        sb.Append(indent).Append("</").Append(name).Append(">\n");
    }

    private static string ElementName(XElement e) => Name(e, e.Name);
}
