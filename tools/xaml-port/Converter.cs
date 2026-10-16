using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace XamlPort;

/// <summary>
/// WPF XAML → Avalonia XAML. Mechanical where the two frameworks line up; everything it cannot
/// translate faithfully is left as a <!-- PORT: ... --> comment and listed in the report, so the
/// remaining hand-work is explicit.
///
/// The key differences it bridges:
///  - Visibility (enum) → IsVisible (bool), including the BooleanToVisibility converter idiom.
///  - Keyed Styles → ControlThemes (applied with Theme=), implicit Styles → Styles with selectors.
///  - Style/ControlTemplate triggers → pseudo-class selectors (:pointerover, :checked, ...);
///    DataTriggers → a bound style class (Classes.x="{Binding ...}") plus a selector on it.
///  - Compiled bindings: every DataContext / ItemsSource is resolved against the real view-model
///    types (via reflection), so each scope gets an x:DataType and the Avalonia compiler checks
///    every binding path. Paths that do not exist fall back to ReflectionBinding and are reported.
/// </summary>
public sealed class Converter
{
    public static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    public static readonly XNamespace A = "https://github.com/avaloniaui";
    public static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    public static readonly XNamespace D = "http://schemas.microsoft.com/expression/blend/2008";
    public static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    public static readonly XNamespace Ctrl = "using:FlipPix.UI.Linux.Controls";
    public static readonly XNamespace Conv = "using:FlipPix.UI.Linux.Converters";

    private readonly Types _types;
    private readonly string _file;
    private readonly Dictionary<string, string> _srcNamespaces = new(); // wpf prefix → clr namespace
    private readonly Dictionary<string, string> _clrPrefixes = new();   // clr namespace → avalonia prefix
    private int _seq;

    // Learned on the first pass, used on the second: x:Name → that element's data type, and a keyed
    // DataTemplate → the item type of the list that uses it.
    private readonly Dictionary<string, Type> _namedTypes = new();
    private readonly Dictionary<string, Type> _templateTypes = new();
    private readonly Dictionary<string, (string Host, Type Type)> _templateHosts = new();

    public List<string> Report { get; } = new();

    /// <summary>Clears per-pass state; the learned maps are kept.</summary>
    public void BeginPass()
    {
        Report.Clear();
        _seq = 0;
    }

    public Converter(Types types, string file)
    {
        _types = types;
        _file = Path.GetFileName(file);
        _clrPrefixes["FlipPix.UI.ViewModels"] = "vm";
        _clrPrefixes["FlipPix.UI.ViewModels.Video"] = "vmv";
        _clrPrefixes["FlipPix.UI.Models"] = "m";
        _clrPrefixes["FlipPix.UI.Services"] = "svc";
        _clrPrefixes["System"] = "sys";
    }

    // ───────────────────────────── context ─────────────────────────────

    private sealed class Ctx
    {
        public Type? DataType;
        public bool CompileOff;          // x:CompileBindings="False" in effect
        public bool InTemplate;          // inside a ControlTemplate
        public string ElementName = "";  // local name of the element this ctx belongs to
        public Type? TagType;            // what this element's Tag binds to (ContextMenu DataContext via PlacementTarget.Tag)
        public Ctx? Parent;

        public Ctx Child(string element, Type? type, bool compileOff) => new()
        {
            DataType = type,
            CompileOff = compileOff,
            InTemplate = InTemplate,
            ElementName = element,
            TagType = TagType,
            Parent = this
        };

        public Ctx? FindAncestor(string elementName)
        {
            for (var c = this; c != null; c = c.Parent)
                if (c.ElementName == elementName) return c;
            return null;
        }
    }

    private void Note(XObject? src, string message)
    {
        var line = (src as IXmlLineInfo)?.HasLineInfo() == true ? ((IXmlLineInfo)src).LineNumber : 0;
        Report.Add($"{_file}:{line}: {message}");
    }

    // ───────────────────────────── entry ─────────────────────────────

    /// <summary>A WPF ResourceDictionary of design tokens and styles → an Avalonia Styles file.</summary>
    public XDocument ConvertStyles(XDocument wpf)
    {
        var src = wpf.Root!;
        foreach (var a in src.Attributes().Where(a => a.IsNamespaceDeclaration))
            if (a.Value.StartsWith("clr-namespace:"))
                _srcNamespaces[a.Name.LocalName] = a.Value["clr-namespace:".Length..].Split(';')[0];

        var ctx = new Ctx { CompileOff = true, ElementName = "Styles" };
        var root = new XElement(A + "Styles");
        ConvertResources(src, root, ctx, "Styles");
        RemapPrefixes(root);
        var implicitStyles = root.Element(A + "Styles.Styles");
        if (implicitStyles != null)
        {
            implicitStyles.Remove();
            root.Add(implicitStyles.Elements());
        }
        root.Add(new XAttribute("xmlns", A.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "conv", Conv.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "ctrl", Ctrl.NamespaceName));
        var attrs = root.Attributes().ToList();
        root.RemoveAttributes();
        root.Add(attrs.OrderBy(a => a.IsNamespaceDeclaration ? 0 : 1));
        return new XDocument(root);
    }

    public XDocument Convert(XDocument wpf, string xClass, string? rootTypeName, string? rootElement)
    {
        var src = wpf.Root!;
        foreach (var a in src.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var v = a.Value;
            if (v.StartsWith("clr-namespace:"))
            {
                var ns = v["clr-namespace:".Length..].Split(';')[0];
                _srcNamespaces[a.Name.LocalName] = ns;
            }
        }

        var rootType = rootTypeName != null ? _types.Find(rootTypeName) : null;
        if (rootTypeName != null && rootType == null)
            Note(src, $"root data type {rootTypeName} not found in assembly");

        var ctx = new Ctx { DataType = rootType, CompileOff = rootType == null, ElementName = src.Name.LocalName };
        var root = ConvertElement(src, ctx, isRoot: true)!;

        if (rootElement != null)
        {
            var parts = rootElement.Split(':');
            root.Name = parts.Length == 2 ? XNamespace.Get(parts[0] == "win" ? "using:FlipPix.UI.Linux.Windows" : parts[0]) + parts[1] : A + rootElement;
        }

        RemapPrefixes(root);
        root.SetAttributeValue(X + "Class", xClass);
        if (rootType != null)
        {
            root.SetAttributeValue(X + "DataType", TypeRef(rootType));
            root.SetAttributeValue(X + "CompileBindings", "True");
        }

        // Namespace declarations, Avalonia first.
        var decls = new List<XAttribute>
        {
            new(XNamespace.Xmlns + "x", X.NamespaceName),
            new(XNamespace.Xmlns + "d", D.NamespaceName),
            new(XNamespace.Xmlns + "mc", MC.NamespaceName),
            new(XNamespace.Xmlns + "ctrl", Ctrl.NamespaceName),
            new(XNamespace.Xmlns + "conv", Conv.NamespaceName),
            new(XNamespace.Xmlns + "win", "using:FlipPix.UI.Linux.Windows"),
        };
        foreach (var (ns, prefix) in _clrPrefixes)
            decls.Add(new(XNamespace.Xmlns + prefix, "using:" + ns));

        var others = root.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        root.RemoveAttributes();
        root.Add(new XAttribute("xmlns", A.NamespaceName));
        root.Add(decls);
        if (!others.Any(a => a.Name == MC + "Ignorable")) root.Add(new XAttribute(MC + "Ignorable", "d"));
        root.Add(others);

        return new XDocument(root);
    }

    /// <summary>{x:Static local:Foo.Bar} / {x:Type local:Foo}: WPF prefixes → this file's prefixes.</summary>
    private void RemapPrefixes(XElement root)
    {
        var re = new Regex(@"(x:Static|x:Type|DataType=)\s*([A-Za-z][A-Za-z0-9]*):");
        foreach (var a in root.DescendantsAndSelf().SelectMany(e => e.Attributes()).Where(a => !a.IsNamespaceDeclaration).ToList())
        {
            if (!a.Value.Contains(':')) continue;
            a.Value = re.Replace(a.Value, m =>
            {
                var p = m.Groups[2].Value;
                if (!_srcNamespaces.TryGetValue(p, out var clr)) return m.Value;
                if (clr is "FlipPix.UI.Controls") return m.Groups[1].Value + " ctrl:";
                if (clr is "FlipPix.UI.Converters") return m.Groups[1].Value + " conv:";
                if (!_clrPrefixes.TryGetValue(clr, out var np))
                {
                    np = "n" + (_clrPrefixes.Count + 1);
                    _clrPrefixes[clr] = np;
                }
                var sep = m.Groups[1].Value.EndsWith("=") ? "" : " ";
                return m.Groups[1].Value + sep + np + ":";
            });
        }
    }

    private string TypeRef(Type t)
    {
        var ns = t.Namespace ?? "";
        if (!_clrPrefixes.TryGetValue(ns, out var prefix))
        {
            prefix = "n" + (_clrPrefixes.Count + 1);
            _clrPrefixes[ns] = prefix;
        }
        var name = t.IsNested ? t.DeclaringType!.Name + "+" + t.Name : t.Name;
        return prefix + ":" + name;
    }

    // ───────────────────────────── elements ─────────────────────────────

    private static readonly HashSet<string> DroppedElements = new()
    {
        "Storyboard", "BeginStoryboard", "EventTrigger"
    };

    private XName MapName(XElement src, out string? special)
    {
        special = null;
        var ns = src.Name.Namespace;
        var local = src.Name.LocalName;

        if (ns == X) return X + local;
        if (ns == D || ns == MC) return ns + local;

        if (ns != W)
        {
            var prefix = src.GetPrefixOfNamespace(ns) ?? "";
            var clr = ns.NamespaceName.StartsWith("clr-namespace:")
                ? ns.NamespaceName["clr-namespace:".Length..].Split(';')[0]
                : ns.NamespaceName;

            if (clr == "System")
            {
                // sys:Double / sys:String → x:Double / x:String
                return X + (local == "Int" ? "Int32" : local);
            }
            if (clr == "FlipPix.UI.Controls") return Ctrl + local;
            if (clr == "FlipPix.UI.Converters") return Conv + local;
            if (clr.StartsWith("FlipPix.UI"))
            {
                if (!_clrPrefixes.ContainsKey(clr)) _clrPrefixes[clr] = "n" + (_clrPrefixes.Count + 1);
                return XNamespace.Get("using:" + clr) + local;
            }
            Note(src, $"unknown element namespace {prefix}:{local} ({clr})");
            return XNamespace.Get("using:" + clr) + local;
        }

        switch (local)
        {
            case "MediaElement": return Ctrl + "VideoPreview";
            case "InkCanvas": special = "InkCanvas"; return Ctrl + "MaskPaintCanvas";
            case "GroupBox": special = "GroupBox"; return A + "Border";
            case "StatusBar": special = "StatusBar"; return A + "Border";
            case "StatusBarItem": return A + "ContentControl";
            case "Hyperlink": return A + "Run";
        }
        return A + local;
    }

    private XElement? ConvertElement(XElement src, Ctx ctx, bool isRoot = false)
    {
        var local = src.Name.LocalName;
        if (src.Name.Namespace == W && DroppedElements.Contains(local))
        {
            Note(src, $"dropped <{local}> (animations are not ported)");
            return null;
        }

        var name = MapName(src, out var special);
        var dst = new XElement(name);
        var elementLocal = name.LocalName;

        // DataContext changes the data type for this element and everything inside it.
        var myType = ctx.DataType;
        var myCompileOff = ctx.CompileOff;
        var dcAttr = src.Attribute("DataContext");
        Type? dcSpecial = null;
        if (dcAttr != null && Markup.Parse(dcAttr.Value) is { Name: "Binding" } dcx)
        {
            if (dcx.Path == "PlacementTarget.Tag") dcSpecial = ctx.TagType;
            else if (dcx.Get("ElementName") is { } en && dcx.Path is "DataContext" && _namedTypes.TryGetValue(en, out var nt)) dcSpecial = nt;
        }
        if (dcSpecial != null && Types.IsNameable(dcSpecial))
        {
            myType = dcSpecial;
            myCompileOff = false;
            dst.SetAttributeValue(X + "DataType", TypeRef(dcSpecial));
            if (ctx.CompileOff) dst.SetAttributeValue(X + "CompileBindings", "True");
        }
        else if (dcAttr != null && Markup.Parse(dcAttr.Value) is { Name: "Binding" } dcm && dcm.Get("RelativeSource") == null && dcm.Get("ElementName") == null && dcm.Get("Source") == null)
        {
            var resolved = _types.ResolvePath(ctx.DataType, dcm.Path, out var err);
            if (resolved != null && Types.IsNameable(resolved))
            {
                myType = resolved;
                myCompileOff = false;
                dst.SetAttributeValue(X + "DataType", TypeRef(resolved));
                if (ctx.CompileOff) dst.SetAttributeValue(X + "CompileBindings", "True");
            }
            else
            {
                myType = null;
                myCompileOff = true;
                dst.SetAttributeValue(X + "CompileBindings", "False");
                Note(src, $"DataContext {dcAttr.Value}: type unknown ({err ?? resolved?.Name ?? "unresolved"}) - bindings below use reflection");
            }
        }
        else if (dcAttr != null && Markup.IsMarkup(dcAttr.Value))
        {
            myType = null;
            myCompileOff = true;
            dst.SetAttributeValue(X + "CompileBindings", "False");
            Note(src, $"DataContext {dcAttr.Value}: type unknown - bindings below use reflection");
        }

        // DataTemplates: their data type comes from the items they template (set by the caller via ctx).
        if (local == "DataTemplate" || local == "HierarchicalDataTemplate")
        {
            var dtAttr = src.Attribute("DataType");
            Type? dt = null;
            if (dtAttr != null)
            {
                dt = ResolveXamlType(src, dtAttr.Value);
                if (dt != null) dst.SetAttributeValue("DataType", TypeRef(dt));
            }
            else if (src.Attribute(X + "Key")?.Value is { } tkey)
                dt = _templateTypes.GetValueOrDefault(tkey);
            else if (ctx.ElementName == "#template")
                dt = ctx.DataType;

            if (dt != null && Types.IsNameable(dt))
            {
                myType = dt;
                myCompileOff = false;
                dst.SetAttributeValue(X + "DataType", TypeRef(dt));
            }
            else
            {
                myType = null;
                myCompileOff = true;
                dst.SetAttributeValue(X + "CompileBindings", "False");
                Note(src, $"DataTemplate item type unknown - bindings use reflection");
            }
        }

        // A keyed template sees, as its ancestor, the list that uses it.
        if (local == "DataTemplate" && src.Attribute(X + "Key")?.Value is { } hk && _templateHosts.TryGetValue(hk, out var host))
            ctx = ctx.Child(host.Host, host.Type, false);

        var me = ctx.Child(elementLocal, myType, myCompileOff);
        if (local == "ControlTemplate") me.InTemplate = true;

        var xname = src.Attribute(X + "Name")?.Value ?? src.Attribute("Name")?.Value;
        if (xname != null && myType != null && !myCompileOff) _namedTypes[xname] = myType;

        if (src.Attribute("Tag")?.Value is { } tagv && Markup.Parse(tagv) is { Name: "Binding" } tagm)
        {
            if (tagm.Get("ElementName") is { } ten && tagm.Path == "DataContext")
                me.TagType = _namedTypes.GetValueOrDefault(ten);
            else if (tagm.Get("ElementName") == null && tagm.Get("RelativeSource") == null)
                me.TagType = _types.ResolvePath(myType, tagm.Path, out _);
        }

        // Attributes.
        string? itemsSourcePath = null;
        foreach (var attr in src.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            if (attr.Name.LocalName == "DataContext" && attr.Name.Namespace == XNamespace.None)
            {
                // Resolved against the parent's data type.
                var v = Markup.IsMarkup(attr.Value) ? ConvertMarkupValue(attr, attr.Value, ctx, "DataContext", elementLocal) : attr.Value;
                dst.SetAttributeValue("DataContext", v);
                continue;
            }
            if (attr.Name.LocalName == "ItemsSource" && Markup.Parse(attr.Value) is { Name: "Binding" } im)
                itemsSourcePath = im.Path;
            if (local is "DataTemplate" or "HierarchicalDataTemplate" && attr.Name.LocalName == "DataType") continue;
            ConvertAttribute(src, attr, dst, me, elementLocal);
        }

        if (isRoot)
        {
            foreach (var n in new[] { "Icon", "WindowStyle", "ResizeMode", "AllowsTransparency" })
                dst.Attribute(n)?.Remove();
        }

        // The item type for templates of an items control.
        Type? itemType = null;
        if (itemsSourcePath != null && myType != null)
        {
            var coll = _types.ResolvePath(myType, itemsSourcePath, out _);
            if (coll != null) itemType = Types.ElementType(coll);
        }
        Type? contentType = null;
        var contentAttr = src.Attribute("Content");
        if (contentAttr != null && Markup.Parse(contentAttr.Value) is { Name: "Binding" } cm && myType != null)
            contentType = _types.ResolvePath(myType, cm.Path, out _);

        // A keyed template used as this list's ItemTemplate learns the list's item type.
        if (itemType != null && src.Attribute("ItemTemplate")?.Value is { } itv && ResourceKey(itv) is { } itk)
        {
            _templateTypes[itk] = itemType;
            if (myType != null) _templateHosts[itk] = (elementLocal, myType);
        }
        if (contentType != null && src.Attribute("ContentTemplate")?.Value is { } ctv && ResourceKey(ctv) is { } ctk)
            _templateTypes[ctk] = contentType;

        // Children.
        XElement? layoutTransform = null;
        foreach (var node in src.Nodes())
        {
            switch (node)
            {
                case XComment c:
                    dst.Add(new XComment(c.Value));
                    break;
                case XText t when !string.IsNullOrWhiteSpace(t.Value):
                    dst.Add(new XText(t.Value.Trim()));
                    break;
                case XElement child:
                    var childLocal = child.Name.LocalName;
                    var dot = childLocal.IndexOf('.');
                    if (dot > 0 && child.Name.Namespace == src.Name.Namespace || dot > 0 && child.Name.Namespace == W)
                    {
                        var owner = childLocal[..dot];
                        var prop = childLocal[(dot + 1)..];
                        if (prop == "LayoutTransform")
                        {
                            layoutTransform = child;
                            continue;
                        }
                        ConvertPropertyElement(src, child, owner, prop, dst, me, elementLocal, itemType, contentType);
                    }
                    else
                    {
                        var converted = ConvertElement(child, me);
                        if (converted != null) dst.Add(converted);
                    }
                    break;
            }
        }

        // Shape-changing translations.
        switch (special)
        {
            case "GroupBox": dst = ConvertGroupBox(src, dst); break;
            case "StatusBar": dst = ConvertStatusBar(dst); break;
            case "InkCanvas": Note(src, "InkCanvas → MaskPaintCanvas: wire strokes in code-behind"); break;
        }

        if (layoutTransform != null)
            return WrapLayoutTransform(dst, layoutTransform, me);

        return dst;
    }

    private Type? ResolveXamlType(XElement context, string value)
    {
        var v = value.Trim();
        if (Markup.Parse(v) is { Name: "x:Type" } tm) v = tm.Positional ?? tm.Get("TypeName") ?? "";
        var parts = v.Split(':');
        if (parts.Length != 2) return null;
        if (!_srcNamespaces.TryGetValue(parts[0], out var ns))
        {
            var xns = context.GetNamespaceOfPrefix(parts[0]);
            if (xns == null) return null;
            ns = xns.NamespaceName.Replace("clr-namespace:", "").Split(';')[0];
        }
        return _types.Find(ns + "." + parts[1]);
    }

    // ───────────────────────────── property elements ─────────────────────────────

    private void ConvertPropertyElement(XElement srcOwner, XElement child, string owner, string prop, XElement dst,
        Ctx me, string elementLocal, Type? itemType, Type? contentType)
    {
        var ownerName = owner == srcOwner.Name.LocalName ? elementLocal : owner;
        var children = child.Elements().ToList();

        switch (prop)
        {
            case "Style" when owner == srcOwner.Name.LocalName:
            {
                var style = children.FirstOrDefault(e => e.Name.LocalName == "Style");
                if (style != null) ConvertInlineStyle(style, dst, me, elementLocal);
                return;
            }
            case "Template" when owner == srcOwner.Name.LocalName:
            {
                var tpl = children.FirstOrDefault(e => e.Name.LocalName == "ControlTemplate");
                if (tpl == null) break;
                var converted = ConvertControlTemplate(tpl, me, elementLocal, out var triggers);
                dst.Add(new XElement(dst.Name.Namespace + (elementLocal + ".Template"), converted));
                // A WPF template replaces the control's whole look. In Avalonia the Fluent theme's
                // styles would still reach into it (e.g. Button:disabled recolours PART_ContentPresenter),
                // so an element with its own template gets an empty theme as well.
                if (dst.Attribute("Theme") == null && dst.Element(dst.Name.Namespace + (elementLocal + ".Theme")) == null)
                    dst.Add(new XElement(dst.Name.Namespace + (elementLocal + ".Theme"),
                        new XElement(A + "ControlTheme", new XAttribute("TargetType", SelectorType(dst).Replace("ctrl|", "ctrl:")))));
                if (triggers.Count > 0)
                {
                    var id = "t" + (++_seq);
                    AddClass(dst, id);
                    var styles = GetStyles(dst, elementLocal);
                    var k = 0;
                    foreach (var tr in triggers)
                    {
                        var trig = tr;
                        foreach (var (ph, binding, value) in tr.DataConditions)
                        {
                            // The template's DataContext is the templated control's, so the trigger
                            // binding becomes a class on that control.
                            var cls = $"{id}_{k++}";
                            dst.SetAttributeValue("Classes." + cls, BoolBinding(tpl, binding, value, me, elementLocal));
                            trig = trig with { Suffix = trig.Suffix.Replace(ph, "." + cls) };
                        }
                        styles.Add(TriggerStyle($"{SelectorType(dst)}.{id}", trig));
                    }
                }
                return;
            }
            case "ToolTip":
            {
                var tip = new XElement(A + "ToolTip.Tip");
                foreach (var c in child.Nodes())
                    if (c is XElement ce) { var cv = ConvertElement(ce, me); if (cv != null) tip.Add(cv); }
                    else if (c is XText t && !string.IsNullOrWhiteSpace(t.Value)) tip.Add(new XText(t.Value.Trim()));
                dst.Add(tip);
                return;
            }
            case "InputBindings":
                ConvertInputBindings(child, dst, me, elementLocal);
                return;
            case "Resources":
                ConvertResources(child, dst, me, elementLocal);
                return;
            case "Effect":
            {
                var e = children.FirstOrDefault();
                if (e?.Name.LocalName == "DropShadowEffect")
                {
                    var de = new XElement(A + "DropShadowDirectionEffect");
                    foreach (var a in e.Attributes())
                        de.SetAttributeValue(a.Name.LocalName, a.Value);
                    dst.Add(new XElement(A + (elementLocal + ".Effect"), de));
                }
                else Note(child, $"effect {e?.Name.LocalName} not ported");
                return;
            }
            case "ItemTemplate":
            case "ContentTemplate":
            case "ItemsPanel":
            case "HeaderTemplate":
            {
                var tctx = me.Child("#template", prop == "ContentTemplate" ? contentType : itemType, false);
                if (prop == "ItemsPanel") tctx = me;
                var pe = new XElement(A + (ownerName + "." + prop));
                foreach (var c in children)
                {
                    var cv = ConvertElement(c, tctx);
                    if (cv != null) pe.Add(cv);
                }
                dst.Add(pe);
                return;
            }
            case "ItemContainerStyle":
            {
                var style = children.FirstOrDefault(e => e.Name.LocalName == "Style");
                if (style == null) break;
                var target = StripType(style.Attribute("TargetType")?.Value) ?? "ContentPresenter";
                var ictx = me.Child("#template", itemType, itemType == null);
                var theme = ConvertKeyedStyle(style, ictx, key: null, forceTarget: target);
                dst.Add(new XElement(A + (ownerName + ".ItemContainerTheme"), theme));
                return;
            }
            case "ContextMenu":
            {
                var pe = new XElement(A + (ownerName + ".ContextMenu"));
                foreach (var c in children)
                {
                    var cv = ConvertElement(c, me.Child("ContextMenu", me.DataType, me.CompileOff));
                    if (cv != null) pe.Add(cv);
                }
                dst.Add(pe);
                return;
            }
            case "Triggers":
                Note(child, $"{owner}.Triggers dropped (event triggers/animations)");
                return;
        }

        // Generic property element: <Border.Background>, <Grid.ColumnDefinitions>, <Setter.Value>...
        var name = prop switch
        {
            "ColumnDefinitions" or "RowDefinitions" => ownerName + "." + prop,
            _ => ownerName + "." + MapPropertyName(prop, elementLocal)
        };
        var outEl = new XElement((owner == srcOwner.Name.LocalName ? dst.Name.Namespace : A) + name);
        foreach (var c in child.Nodes())
        {
            if (c is XElement ce)
            {
                var cv = ConvertElement(ce, me);
                if (cv != null) outEl.Add(cv);
            }
            else if (c is XText t && !string.IsNullOrWhiteSpace(t.Value)) outEl.Add(new XText(t.Value.Trim()));
            else if (c is XComment cm) outEl.Add(new XComment(cm.Value));
        }
        // <Expander.Header>"text"</Expander.Header> etc. are fine as-is.
        dst.Add(outEl);
    }

    private static string MapPropertyName(string prop, string elementLocal) => prop switch
    {
        "ToolTip" => "Tip",
        _ => prop
    };

    // ───────────────────────────── attributes ─────────────────────────────

    private static readonly HashSet<string> DroppedAttributes = new()
    {
        "SnapsToDevicePixels", "SpellCheck.IsEnabled", "IsMoveToPointEnabled", "FocusVisualStyle",
        "OverridesDefaultStyle", "ScrubbingEnabled", "LoadedBehavior", "UnloadedBehavior", "CanContentScroll",
        "ScrollViewer.CanContentScroll", "ScrollViewer.PanningMode", "AutoToolTipPlacement", "AutoToolTipPrecision",
        "ToolTipService.ShowDuration", "ToolTipService.BetweenShowDelay", "ToolTipService.Placement",
        "TextOptions.TextFormattingMode", "TextOptions.TextRenderingMode", "Stylus.IsPressAndHoldEnabled",
        "VirtualizingPanel.IsVirtualizing", "VirtualizingPanel.VirtualizationMode", "VirtualizingStackPanel.IsVirtualizing",
        "VirtualizingStackPanel.VirtualizationMode", "VirtualizingPanel.ScrollUnit", "KeyboardNavigation.TabNavigation",
        "KeyboardNavigation.DirectionalNavigation", "KeyboardNavigation.IsTabStop", "IsDeferredScrollingEnabled",
        "UseLayoutRounding", "PixelsPerSecond"
    };

    private static readonly Dictionary<string, string> EventMap = new()
    {
        ["MouseLeftButtonDown"] = "PointerPressed",
        ["PreviewMouseLeftButtonDown"] = "PointerPressed",
        ["MouseLeftButtonUp"] = "PointerReleased",
        ["PreviewMouseLeftButtonUp"] = "PointerReleased",
        ["MouseRightButtonUp"] = "PointerReleased",
        ["MouseDown"] = "PointerPressed",
        ["MouseUp"] = "PointerReleased",
        ["MouseDoubleClick"] = "DoubleTapped",
        ["MouseEnter"] = "PointerEntered",
        ["MouseLeave"] = "PointerExited",
        ["MouseMove"] = "PointerMoved",
        ["PreviewMouseMove"] = "PointerMoved",
        ["MouseWheel"] = "PointerWheelChanged",
        ["PreviewMouseWheel"] = "PointerWheelChanged",
        ["PreviewKeyDown"] = "KeyDown",
        ["PreviewTextInput"] = "TextInput",
        ["Drop"] = "DragDrop.Drop",
        ["DragOver"] = "DragDrop.DragOver",
        ["DragEnter"] = "DragDrop.DragEnter",
        ["DragLeave"] = "DragDrop.DragLeave",
    };

    private void ConvertAttribute(XElement src, XAttribute attr, XElement dst, Ctx ctx, string elementLocal)
    {
        var n = attr.Name;
        var v = attr.Value;

        if (n.Namespace == X)
        {
            if (n.LocalName is "Shared" or "FieldModifier" or "ClassModifier") return;
            if (n.LocalName == "Class") return;
            dst.SetAttributeValue(X + n.LocalName, v);
            return;
        }
        if (n.Namespace == D || n.Namespace == MC)
        {
            if (n.LocalName is "DesignWidth" or "DesignHeight" or "Ignorable") dst.SetAttributeValue(n, v);
            return;
        }

        var name = n.LocalName;
        if (DroppedAttributes.Contains(name)) return;

        switch (name)
        {
            case "Visibility":
                ConvertVisibility(src, v, dst, ctx, elementLocal);
                return;
            case "ToolTip":
                if (v == "{x:Null}") return;
                dst.SetAttributeValue("ToolTip.Tip", ConvertMarkupValue(attr, v, ctx, name, elementLocal));
                return;
            case "ToolTipService.ShowOnDisabled":
                dst.SetAttributeValue("ToolTip.ShowOnDisabled", v);
                return;
            case "ToolTipService.InitialShowDelay":
                dst.SetAttributeValue("ToolTip.ShowDelay", v);
                return;
            case "Style":
                dst.SetAttributeValue("Theme", v);
                return;
            case "Cursor":
                dst.SetAttributeValue("Cursor", MapCursor(v));
                return;
            case "FontFamily":
                dst.SetAttributeValue("FontFamily", MapFont(v));
                return;
            case "RenderOptions.BitmapScalingMode":
                dst.SetAttributeValue("RenderOptions.BitmapInterpolationMode", v switch
                {
                    "NearestNeighbor" => "None",
                    "LowQuality" => "LowQuality",
                    _ => "HighQuality"
                });
                return;
            case "Panel.ZIndex":
                dst.SetAttributeValue("ZIndex", v);
                return;
            case "TextWrapping":
                dst.SetAttributeValue("TextWrapping", v == "WrapWithOverflow" ? "Wrap" : v);
                return;
            case "Width" or "Height" when v == "Auto" && elementLocal is not ("RowDefinition" or "ColumnDefinition"):
                return;
            case "VerticalScrollBarVisibility" or "HorizontalScrollBarVisibility" when elementLocal != "ScrollViewer":
                dst.SetAttributeValue("ScrollViewer." + name, v);
                return;
            case "SelectedValuePath":
                dst.SetAttributeValue("SelectedValueBinding", "{ReflectionBinding " + v + "}");
                return;
            case "DisplayMemberPath":
                dst.SetAttributeValue("DisplayMemberBinding", "{ReflectionBinding " + v + "}");
                return;
            case "AllowDrop":
                dst.SetAttributeValue("DragDrop.AllowDrop", v);
                return;
            case "WindowStyle":
                if (v == "None") dst.SetAttributeValue("SystemDecorations", "None");
                return;
            case "ResizeMode":
                if (v is "NoResize" or "CanMinimize") dst.SetAttributeValue("CanResize", "False");
                return;
            case "Icon" when elementLocal is "Window" or "AppWindow":
                return;
            case "StaysOpen":
                dst.SetAttributeValue("IsLightDismissEnabled", v == "False" ? "True" : "False");
                return;
            case "StartPoint" or "EndPoint" when elementLocal == "LinearGradientBrush":
                dst.SetAttributeValue(name, ToRelativePoint(v));
                return;
            case "IsCheckable" when elementLocal == "MenuItem":
                dst.SetAttributeValue("ToggleType", v == "True" ? "CheckBox" : "None");
                return;
            case "ShadowDepth" or "Direction" or "BlurRadius" or "Color" or "Opacity" when elementLocal == "DropShadowEffect":
                dst.SetAttributeValue(name, v);
                return;
            case "Source" when elementLocal == "Image" && Markup.Parse(v) is { Name: "Binding" } sm:
                if (sm.Get("Converter") == null) sm.Set("Converter", "{StaticResource PathToBitmapConverter}");
                dst.SetAttributeValue("Source", ConvertBinding(attr, sm, ctx, "Source", elementLocal, out _));
                return;
            case "Source" when elementLocal == "Image" && !Markup.IsMarkup(v):
                dst.SetAttributeValue("Source", "/Assets/" + Path.GetFileName(v.Replace("pack://application:,,,/", "")));
                Note(attr, $"image source {v}: copy the file to FlipPix.UI.Linux/Assets");
                return;
            case "Source" when elementLocal == "VideoPreview":
                dst.SetAttributeValue("Source", ConvertMarkupValue(attr, v, ctx, name, elementLocal));
                return;
            case "Header" when elementLocal == "TabItem":
                dst.SetAttributeValue("Header", v);
                return;
            case "HorizontalContentAlignment" or "VerticalContentAlignment" when elementLocal is "ListBox" or "ItemsControl" or "ListView":
            {
                // Avalonia sets these on the item containers, not the list.
                var styles = GetStyles(dst, elementLocal);
                var container = elementLocal == "ListBox" ? "ListBoxItem" : "ContentPresenter";
                styles.Add(new XElement(A + "Style", new XAttribute("Selector", $"{elementLocal} > {container}"),
                    new XElement(A + "Setter", new XAttribute("Property", name), new XAttribute("Value", v))));
                return;
            }
            case "ItemContainerStyle":
                dst.SetAttributeValue("ItemContainerTheme", v);
                return;
            case "IsEditable" when elementLocal == "ComboBox" && v == "False":
                return;
            case "IsEditable" when elementLocal == "ComboBox":
                Note(attr, "ComboBox IsEditable has no Avalonia 11.2 equivalent: port by hand");
                dst.SetAttributeValue("IsEditable", v);
                return;
        }

        if (EventMap.TryGetValue(name, out var ev))
        {
            dst.SetAttributeValue(ev, v);
            if (name.StartsWith("Preview")) Note(attr, $"event {name} → {ev} (bubbling, not tunnelling)");
            return;
        }

        // Attached property owner renames.
        if (name.StartsWith("TextBlock.") && elementLocal != "TextBlock")
            name = "TextElement." + name["TextBlock.".Length..];
        if (name == "TextElement.Foreground" && elementLocal is "ContentPresenter")
            name = "Foreground";

        dst.SetAttributeValue(name, ConvertMarkupValue(attr, v, ctx, name, elementLocal));
    }

    private static string ToRelativePoint(string v)
    {
        var parts = v.Split(',');
        if (parts.Length != 2) return v;
        static string P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? (d * 100).ToString(CultureInfo.InvariantCulture) + "%" : s;
        return P(parts[0].Trim()) + "," + P(parts[1].Trim());
    }

    private static string MapCursor(string v) => v switch
    {
        "SizeWE" => "SizeWestEast",
        "SizeNS" => "SizeNorthSouth",
        "IBeam" => "Ibeam",
        "SizeNWSE" => "TopLeftCorner",
        "SizeNESW" => "TopRightCorner",
        "ScrollAll" => "SizeAll",
        "AppStarting" => "AppStarting",
        _ => v
    };

    private static string MapFont(string v) => v switch
    {
        "Consolas" => "Consolas, DejaVu Sans Mono, Liberation Mono, monospace",
        "Segoe UI Symbol" or "Segoe UI Emoji" => "Noto Color Emoji, Noto Sans Symbols 2, DejaVu Sans",
        "Segoe UI" => "avares://FlipPix.UI.Linux/Assets/Fonts#Selawik",
        "Segoe UI Semibold" => "avares://FlipPix.UI.Linux/Assets/Fonts#Selawik",
        "Cascadia Code" or "Cascadia Mono" => "Cascadia Code, DejaVu Sans Mono, monospace",
        _ => v
    };

    private void ConvertVisibility(XElement src, string v, XElement dst, Ctx ctx, string elementLocal)
    {
        if (!Markup.IsMarkup(v))
        {
            if (v is "Collapsed" or "Hidden") dst.SetAttributeValue("IsVisible", "False");
            return;
        }
        var m = Markup.Parse(v)!;
        if (m.Name is "StaticResource" or "DynamicResource" or "x:Static")
        {
            Note(src, $"Visibility={v} not ported");
            return;
        }
        if (m.Name != "Binding")
        {
            Note(src, $"Visibility markup {m.Name} not ported");
            return;
        }
        var conv = ResourceKey(m.Get("Converter"));
        var param = m.Get("ConverterParameter");
        switch (conv)
        {
            case "BooleanToVisibilityConverter":
            case "BoolToVisibilityConverter":
                m.Remove("Converter");
                m.Remove("ConverterParameter");
                if (param is "Inverse" or "Invert" or "Inverted" or "True" or "Collapsed")
                    NegatePath(m, src);
                break;
            case "InverseBooleanToVisibilityConverter":
            case "InverseBoolToVisibilityConverter":
                m.Remove("Converter");
                NegatePath(m, src);
                break;
            case "StringToVisibilityConverter":
                m.Set("Converter", "{x:Static StringConverters.IsNotNullOrEmpty}");
                break;
            case "NotNullToVisibilityConverter":
                m.Set("Converter", "{x:Static ObjectConverters.IsNotNull}");
                break;
            case "NullToVisibilityConverter":
                m.Set("Converter", "{x:Static ObjectConverters.IsNull}");
                break;
            case null:
                m.Set("Converter", "{x:Static conv:WpfConverters.VisibilityToBool}");
                break;
            default:
                m.Set("Converter", "{x:Static conv:WpfConverters.VisibilityToBool}");
                Note(src, $"Visibility converter {conv} chained: check the result is a bool/Visibility");
                break;
        }
        dst.SetAttributeValue("IsVisible", ConvertBinding(src, m, ctx, "IsVisible", elementLocal, out _));
    }

    private static void NegatePath(Markup m, XObject src)
    {
        var p = m.Path;
        m.Path = "!" + (string.IsNullOrEmpty(p) ? "." : p);
    }

    private static string? ResourceKey(string? v)
    {
        if (v == null) return null;
        var m = Markup.Parse(v);
        return m is { Name: "StaticResource" or "DynamicResource" } ? m.Positional ?? m.Get("ResourceKey") : v;
    }

    private static string? StripType(string? v)
    {
        if (v == null) return null;
        var m = Markup.Parse(v);
        if (m is { Name: "x:Type" }) v = m.Positional ?? "";
        return v.Contains(':') && !v.StartsWith("ctrl:") ? v.Split(':')[1] : v;
    }

    // ───────────────────────────── markup values & bindings ─────────────────────────────

    private string ConvertMarkupValue(XObject src, string v, Ctx ctx, string attrName, string elementLocal)
    {
        var m = Markup.Parse(v);
        if (m == null) return v;
        switch (m.Name)
        {
            case "Binding":
                return ConvertBinding(src, m, ctx, attrName, elementLocal, out _);
            case "MultiBinding":
                Note(src, "inline MultiBinding not ported");
                return v;
            case "StaticResource" or "DynamicResource":
                return v;
            case "x:Static":
                return MapStatic(src, v);
            case "TemplateBinding":
                return v;
            case "RelativeSource":
                return v;
            default:
                return v;
        }
    }

    private string MapStatic(XObject src, string v)
    {
        if (v.Contains("SystemColors") || v.Contains("SystemParameters"))
            Note(src, $"{v} has no Avalonia equivalent");
        return v;
    }

    /// <summary>
    /// Rewrites one {Binding}. Resolves its path against the scope's data type so compiled
    /// bindings check it; returns the source type of the bound value via <paramref name="resultType"/>.
    /// </summary>
    private string ConvertBinding(XObject src, Markup m, Ctx ctx, string attrName, string elementLocal, out Type? resultType)
    {
        resultType = null;
        foreach (var k in new[] { "NotifyOnValidationError", "ValidatesOnDataErrors", "ValidatesOnExceptions", "ValidatesOnNotifyDataErrors", "IsAsync", "NotifyOnTargetUpdated", "NotifyOnSourceUpdated", "ConverterCulture", "BindsDirectlyToSource" })
            m.Remove(k);
        if (m.Get("Delay") != null)
        {
            m.Remove("Delay");
            Note(src, $"{attrName}: binding Delay dropped (not in Avalonia 11.2)");
        }
        var ust = m.Get("UpdateSourceTrigger");
        if (ust is "PropertyChanged" or "Default") m.Remove("UpdateSourceTrigger");

        var sf = m.Get("StringFormat");
        if (sf != null)
        {
            var raw = sf.Trim('\'');
            if (!raw.Contains("{0") && !raw.Contains("{}"))
                m.Set("StringFormat", "{}{0:" + raw + "}");
        }

        var rel = m.Get("RelativeSource");
        var elementName = m.Get("ElementName");

        var conv = ResourceKey(m.Get("Converter"));
        if (conv is "BooleanToVisibilityConverter" or "BoolToVisibilityConverter")
        {
            m.Remove("Converter");
            Note(src, $"{attrName}: BooleanToVisibility converter removed (target must be IsVisible)");
        }
        if (conv is "InverseBooleanConverter" or "InverseBoolConverter")
        {
            m.Remove("Converter");
            NegatePath(m, src);
        }

        var path = m.Path;
        if (path != null)
        {
            // WPF layout read-backs.
            path = Regex.Replace(path, @"\bActualWidth\b", "Bounds.Width");
            path = Regex.Replace(path, @"\bActualHeight\b", "Bounds.Height");
            if (rel != null || elementName != null || path.StartsWith("$self"))
            {
                // WPF UI property names → Avalonia's.
                path = Regex.Replace(path, @"\bIsMouseOver\b", "IsPointerOver");
                path = Regex.Replace(path, @"\bIsKeyboardFocused\b", "IsFocused");
                path = Regex.Replace(path, @"\bIsKeyboardFocusWithin\b", "IsKeyboardFocusWithin");
            }
            m.Path = path;
        }

        if (rel != null && path != null && path.StartsWith("PlacementTarget"))
        {
            // Avalonia's ContextMenu has no bindable PlacementTarget; ctrl:Menus.Target is set as it opens.
            var rmx = Markup.Parse(rel);
            var anc = StripType(rmx?.Get("AncestorType"));
            if (rmx?.Positional is "Self" || anc == "ContextMenu")
            {
                // Compiled, so the ctrl: prefix resolves at build time (reflection bindings cannot).
                var rest = path["PlacementTarget".Length..];
                m.Remove("RelativeSource");
                m.Path = (anc == "ContextMenu" ? "$parent[ContextMenu]" : "$self") + ".(ctrl:Menus.Target)" + rest;
                m.Name = "CompiledBinding";
                return m.ToString();
            }
        }

        if (rel != null)
        {
            var rm = Markup.Parse(rel);
            var ancestor = StripType(rm?.Get("AncestorType"));
            var isSelf = rm?.Positional is "Self";
            var isTemplated = rm?.Positional is "TemplatedParent";
            if (ancestor != null && path != null && path.StartsWith("DataContext."))
            {
                var rest = path["DataContext.".Length..];
                var anc = ctx.FindAncestor(ancestor);
                if (ancestor is "Window" or "UserControl" && anc == null)
                    anc = Root(ctx);
                var t = anc?.DataType;
                if (t != null && Types.IsNameable(t))
                {
                    resultType = _types.ResolvePath(t, rest, out var err);
                    if (resultType == null && err != null)
                    {
                        Note(src, $"binding {m}: {err}");
                        m.Name = "ReflectionBinding";
                        return m.ToString();
                    }
                    m.Remove("RelativeSource");
                    m.Path = $"$parent[{ancestor}].(({TypeRef(t)})DataContext).{rest}";
                    if (ctx.CompileOff || ctx.InTemplate) m.Name = "Binding";
                    return Finish(m, ctx, forceCompiled: true);
                }
                m.Name = "ReflectionBinding";
                Note(src, $"binding {m}: ancestor {ancestor} data type unknown - reflection binding");
                return m.ToString();
            }
            if (isTemplated)
            {
                // {Binding X, RelativeSource={RelativeSource TemplatedParent}} → {TemplateBinding X} semantics
                m.Name = "ReflectionBinding";
                return m.ToString();
            }
            // Self / ancestor UI properties: the compiler knows those types.
            if (ctx.CompileOff || ctx.InTemplate) m.Name = "ReflectionBinding";
            return m.ToString();
        }

        if (elementName != null)
        {
            if (path != null && path.StartsWith("DataContext"))
            {
                if (_namedTypes.TryGetValue(elementName, out var nt) && Types.IsNameable(nt) && !ctx.InTemplate)
                {
                    var rest = path.Length > "DataContext".Length ? path["DataContext.".Length..] : "";
                    if (rest.Length == 0)
                    {
                        resultType = nt;
                        m.Remove("ElementName");
                        m.Path = $"#{elementName}.DataContext";
                        if (ctx.CompileOff) m.Name = "CompiledBinding";
                        return m.ToString();
                    }
                    resultType = _types.ResolvePath(nt, rest, out var err);
                    if (resultType != null || err == null)
                    {
                        m.Remove("ElementName");
                        m.Path = $"#{elementName}.(({TypeRef(nt)})DataContext).{rest}";
                        if (ctx.CompileOff) m.Name = "CompiledBinding";
                        return m.ToString();
                    }
                    Note(src, $"binding {m}: {err}");
                }
                m.Name = "ReflectionBinding";
                return m.ToString();
            }
            if (ctx.CompileOff || ctx.InTemplate) m.Name = "ReflectionBinding";
            return m.ToString();
        }

        if (m.Get("Source") != null)
        {
            m.Name = "ReflectionBinding";
            return m.ToString();
        }

        if (ctx.InTemplate)
        {
            m.Name = "ReflectionBinding";
            return m.ToString();
        }

        if (ctx.DataType != null && !ctx.CompileOff)
        {
            resultType = _types.ResolvePath(ctx.DataType, path?.TrimStart('!'), out var err);
            if (resultType == null && err != null)
            {
                Note(src, $"binding {{Binding {path}}} on {elementLocal}.{attrName}: {err} - reflection binding (dead binding in WPF too?)");
                m.Name = "ReflectionBinding";
                return m.ToString();
            }
            // WPF value types the view model exposes (Brush, Visibility, Thickness...) need converting.
            if (resultType?.Namespace?.StartsWith("System.Windows") == true && m.Get("Converter") == null
                && attrName is not ("DataContext" or "ItemsSource" or "CommandParameter" or "Tag" or "Content" or "SelectedItem"))
            {
                m.Set("Converter", attrName is "IsVisible" or "Classes"
                    ? "{x:Static conv:WpfConverters.VisibilityToBool}"
                    : "{x:Static conv:WpfConverters.ToAvalonia}");
            }
        }
        return Finish(m, ctx, forceCompiled: false);
    }

    private static string Finish(Markup m, Ctx ctx, bool forceCompiled)
    {
        if (forceCompiled && ctx.CompileOff) m.Name = "CompiledBinding";
        return m.ToString();
    }

    private static Ctx Root(Ctx c)
    {
        while (c.Parent != null) c = c.Parent;
        return c;
    }

    // ───────────────────────────── styles ─────────────────────────────

    private static readonly Dictionary<(string Prop, string Value), string> Pseudo = new()
    {
        [("IsMouseOver", "True")] = ":pointerover",
        [("IsMouseOver", "False")] = ":not(:pointerover)",
        [("IsPressed", "True")] = ":pressed",
        [("IsEnabled", "False")] = ":disabled",
        [("IsEnabled", "True")] = ":not(:disabled)",
        [("IsChecked", "True")] = ":checked",
        [("IsChecked", "False")] = ":unchecked",
        [("IsChecked", "{x:Null}")] = ":indeterminate",
        [("IsSelected", "True")] = ":selected",
        [("IsFocused", "True")] = ":focus",
        [("IsKeyboardFocused", "True")] = ":focus-visible",
        [("IsKeyboardFocusWithin", "True")] = ":focus-within",
        [("IsExpanded", "True")] = ":expanded",
        [("IsDropDownOpen", "True")] = ":dropdownopen",
    };

    /// <summary>One converted trigger: the selector suffix, the named template part it targets, and its setters.</summary>
    private sealed record Trig(string Suffix, List<(string? TargetName, XElement Setter)> Setters)
    {
        /// <summary>DataTrigger conditions: "{dt0}" placeholders in Suffix, bound as classes on the templated control.</summary>
        public List<(string Placeholder, string Binding, string Value)> DataConditions { get; } = new();
    }

    private XElement GetStyles(XElement dst, string elementLocal)
    {
        var stylesName = dst.Name.Namespace + (elementLocal + ".Styles");
        var styles = dst.Element(stylesName);
        if (styles == null)
        {
            styles = new XElement(stylesName);
            dst.AddFirst(styles);
        }
        return styles;
    }

    private static void AddClass(XElement dst, string cls)
    {
        var existing = dst.Attribute("Classes")?.Value;
        dst.SetAttributeValue("Classes", string.IsNullOrEmpty(existing) ? cls : existing + " " + cls);
    }

    private static string SelectorType(XElement dst)
    {
        var ns = dst.Name.Namespace;
        if (ns == A) return dst.Name.LocalName;
        if (ns == Ctrl) return "ctrl|" + dst.Name.LocalName;
        return dst.Name.LocalName;
    }

    /// <summary>
    /// An element's own &lt;X.Style&gt;: base setters become a style on a class unique to the element;
    /// each trigger becomes a style that also requires a pseudo-class or a bound class.
    /// </summary>
    private void ConvertInlineStyle(XElement style, XElement dst, Ctx ctx, string elementLocal)
    {
        // Plain setters go into a local ControlTheme derived from the base style. In WPF a style's
        // setters lose to every trigger, including the BasedOn style's (:disabled, :pointerover);
        // in Avalonia a theme's setters lose to its triggers too, whereas an element style would
        // outrank them. The DataTriggers below stay element styles: they outrank both, as in WPF.
        var basedOn = style.Attribute("BasedOn")?.Value ?? dst.Attribute("Theme")?.Value;
        var targetType = StripType(style.Attribute("TargetType")?.Value) ?? elementLocal;
        if (basedOn == null && Templated.Contains(targetType) && dst.Name.Namespace == A)
            basedOn = "{StaticResource {x:Type " + targetType + "}}";

        var theme = new XElement(A + "ControlTheme", new XAttribute("TargetType", SelectorType(dst).Replace("ctrl|", "ctrl:")));
        if (basedOn != null) theme.SetAttributeValue("BasedOn", basedOn);
        if (ctx.DataType != null && !ctx.CompileOff && Types.IsNameable(ctx.DataType))
            theme.SetAttributeValue(X + "DataType", TypeRef(ctx.DataType));
        var themeHolder = new XElement("holder");
        foreach (var s in style.Elements().Where(e => e.Name.LocalName == "Setter"))
            foreach (var cs in ConvertSetter(s, ctx, elementLocal, theme, themeHolder, "^", inTheme: true))
                theme.Add(cs);
        foreach (var h in themeHolder.Elements()) theme.Add(h);
        if (theme.HasElements)
        {
            dst.Attribute("Theme")?.Remove();
            dst.Add(new XElement(dst.Name.Namespace + (elementLocal + ".Theme"), theme));
        }
        else if (basedOn != null && dst.Attribute("Theme") == null && style.Attribute("BasedOn") != null)
        {
            dst.SetAttributeValue("Theme", basedOn);
        }

        var triggers = style.Elements().FirstOrDefault(e => e.Name.LocalName == "Style.Triggers");
        if (triggers == null || !triggers.HasElements) return;

        var id = "s" + (++_seq);
        AddClass(dst, id);
        var sel = $"{SelectorType(dst)}.{id}";
        var styles = GetStyles(dst, elementLocal);
        var n = 0;
        foreach (var tr in triggers.Elements())
        {
            var conds = TriggerConditions(tr);
            if (conds == null) { Note(tr, $"trigger <{tr.Name.LocalName}> not ported"); continue; }

            var trigSel = sel;
            foreach (var c in conds)
            {
                if (c.Binding == null && Pseudo.TryGetValue((c.Property!, c.Value), out var ps))
                {
                    trigSel += ps;
                    continue;
                }
                var cls = $"{id}_{n++}";
                var b = c.Binding != null
                    ? BoolBinding(tr, c.Binding, c.Value, ctx, elementLocal)
                    : BoolBinding(tr, "{Binding $self." + c.Property + "}", c.Value, ctx, with_self: true, elementLocal);
                dst.SetAttributeValue("Classes." + cls, b);
                trigSel += "." + cls;
            }

            var ts = NewStyle(trigSel, ctx);
            foreach (var s in tr.Elements().Where(e => e.Name.LocalName == "Setter"))
                foreach (var cs in ConvertSetter(s, ctx, elementLocal, ts, styles, trigSel))
                    ts.Add(cs);
            if (tr.Elements().Any(e => e.Name.LocalName.EndsWith("Actions")))
                Note(tr, "trigger Enter/ExitActions (animations) dropped");
            styles.Add(ts);
        }
    }

    private XElement NewStyle(string selector, Ctx ctx)
    {
        var s = new XElement(A + "Style", new XAttribute("Selector", selector));
        if (ctx.DataType != null && !ctx.CompileOff && Types.IsNameable(ctx.DataType))
            s.SetAttributeValue(X + "DataType", TypeRef(ctx.DataType));
        return s;
    }

    private sealed record Cond(string? Property, string? Binding, string Value);

    private static List<Cond>? TriggerConditions(XElement tr)
    {
        switch (tr.Name.LocalName)
        {
            case "Trigger":
                return new() { new(tr.Attribute("Property")?.Value, null, tr.Attribute("Value")?.Value ?? "") };
            case "DataTrigger":
                return new() { new(null, tr.Attribute("Binding")?.Value, tr.Attribute("Value")?.Value ?? "") };
            case "MultiTrigger":
            case "MultiDataTrigger":
                var list = new List<Cond>();
                foreach (var c in tr.Descendants().Where(e => e.Name.LocalName == "Condition"))
                    list.Add(new(c.Attribute("Property")?.Value, c.Attribute("Binding")?.Value, c.Attribute("Value")?.Value ?? ""));
                return list;
            default:
                return null;
        }
    }

    /// <summary>A binding that is true when the trigger's condition holds, for Classes.x.</summary>
    private string BoolBinding(XElement src, string binding, string value, Ctx ctx, string elementLocal)
    {
        var m = Markup.Parse(binding);
        if (m == null || m.Name != "Binding")
        {
            Note(src, $"trigger binding {binding} not ported");
            return "False";
        }

        var hasConverter = m.Get("Converter") != null;
        if (!hasConverter && value.Equals("True", StringComparison.OrdinalIgnoreCase))
        {
            // plain bool
        }
        else if (!hasConverter && value.Equals("False", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(m.Path))
        {
            NegatePath(m, src);
        }
        else if (value == "{x:Null}")
        {
            if (hasConverter) Note(src, $"trigger {binding}=null with a converter: check");
            m.Set("Converter", "{x:Static ObjectConverters.IsNull}");
        }
        else if (value.Length == 0)
        {
            m.Set("Converter", "{x:Static StringConverters.IsNullOrEmpty}");
        }
        else
        {
            if (hasConverter)
            {
                var inner = m.Get("Converter")!;
                var innerParam = m.Get("ConverterParameter");
                m.Set("Converter", "{x:Static conv:WpfConverters.Equal}");
                m.Remove("ConverterParameter");
                Note(src, $"trigger {binding} == {value} lost its converter {inner}{(innerParam != null ? " (" + innerParam + ")" : "")}: check");
            }
            else m.Set("Converter", "{x:Static conv:WpfConverters.Equal}");
            m.Set("ConverterParameter", NeedsQuote(value) ? "'" + value + "'" : value);
        }
        return ConvertBinding(src, m, ctx, "Classes", elementLocal, out _);
    }

    private string BoolBinding(XElement src, string binding, string value, Ctx ctx, bool with_self, string elementLocal)
    {
        var m = Markup.Parse(binding)!;
        if (value.Length == 0) m.Set("Converter", "{x:Static StringConverters.IsNullOrEmpty}");
        else if (value == "{x:Null}") m.Set("Converter", "{x:Static ObjectConverters.IsNull}");
        else if (!value.Equals("True", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Equals("False", StringComparison.OrdinalIgnoreCase)) m.Path = "!" + m.Path;
            else
            {
                m.Set("Converter", "{x:Static conv:WpfConverters.Equal}");
                m.Set("ConverterParameter", NeedsQuote(value) ? "'" + value + "'" : value);
            }
        }
        return m.ToString();
    }

    private static bool NeedsQuote(string v) => v.Contains(',') || v.Contains(' ') || v.Contains('{') || v.Contains('=');

    /// <summary>
    /// Converts one Setter. A Template setter's ControlTemplate triggers come back as extra styles
    /// added to <paramref name="styles"/> under <paramref name="selector"/>.
    /// </summary>
    private IEnumerable<XElement> ConvertSetter(XElement s, Ctx ctx, string targetType, XElement owner, XElement? styles, string selector, bool inTheme = false)
    {
        var prop = s.Attribute("Property")?.Value;
        if (prop == null) { Note(s, "setter without Property"); yield break; }
        if (s.Attribute("TargetName") != null && !inTheme)
            Note(s, $"setter TargetName={s.Attribute("TargetName")!.Value} outside a template ignored");

        var valueAttr = s.Attribute("Value")?.Value;
        var valueEl = s.Elements().FirstOrDefault(e => e.Name.LocalName == "Setter.Value");

        if (prop.Contains(':')) prop = prop.Split(':')[1];

        switch (prop)
        {
            case "Visibility":
            {
                if (valueAttr != null && !Markup.IsMarkup(valueAttr))
                {
                    yield return Setter("IsVisible", valueAttr == "Visible" ? "True" : "False");
                    yield break;
                }
                if (valueAttr != null)
                {
                    var tmp = new XElement("tmp");
                    ConvertVisibility(s, valueAttr, tmp, ctx, targetType);
                    var iv = tmp.Attribute("IsVisible")?.Value;
                    if (iv != null) yield return Setter("IsVisible", iv);
                }
                yield break;
            }
            case "Template":
            {
                var tpl = valueEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "ControlTemplate");
                if (tpl == null) { Note(s, "Template setter without ControlTemplate"); yield break; }
                var ct = ConvertControlTemplate(tpl, ctx, targetType, out var triggers);
                yield return new XElement(A + "Setter", new XAttribute("Property", "Template"), new XElement(A + "Setter.Value", ct));
                if (styles != null)
                    foreach (var tr in triggers)
                    {
                        if (tr.DataConditions.Count > 0)
                        {
                            Note(s, "DataTrigger inside a styled template: needs a class or converter at the use sites");
                            continue;
                        }
                        styles.Add(TriggerStyle(inTheme ? "^" : selector, tr));
                    }
                yield break;
            }
            case "ToolTip":
                prop = "ToolTip.Tip";
                if (valueAttr == "{x:Null}") { yield return Setter(prop, "{x:Null}"); yield break; }
                break;
            case "Cursor":
                if (valueAttr != null) valueAttr = MapCursor(valueAttr);
                break;
            case "FontFamily":
                if (valueAttr != null) valueAttr = MapFont(valueAttr);
                break;
            case "Style":
                if (valueAttr != null) { prop = "Theme"; }
                break;
            case "OverridesDefaultStyle" or "FocusVisualStyle" or "SnapsToDevicePixels":
                yield break;
            case "TextWrapping" when valueAttr == "WrapWithOverflow":
                valueAttr = "Wrap";
                break;
            case "Panel.ZIndex":
                prop = "ZIndex";
                break;
            case "Effect":
                Note(s, "Effect setter not ported");
                yield break;
            case "HorizontalScrollBarVisibility" or "VerticalScrollBarVisibility" when targetType != "ScrollViewer":
                prop = "ScrollViewer." + prop;
                break;
        }

        if (valueAttr != null)
        {
            var v = Markup.IsMarkup(valueAttr) ? ConvertMarkupValue(s, valueAttr, ctx, prop, targetType) : valueAttr;
            yield return Setter(prop, v);
        }
        else if (valueEl != null)
        {
            var sv = new XElement(A + "Setter.Value");
            foreach (var c in valueEl.Elements())
            {
                var cv = ConvertElement(c, ctx);
                if (cv != null) sv.Add(cv);
            }
            yield return new XElement(A + "Setter", new XAttribute("Property", prop), sv);
        }
    }

    private static XElement Setter(string prop, string value) =>
        new(A + "Setter", new XAttribute("Property", prop), new XAttribute("Value", value));

    private XElement TriggerStyle(string baseSelector, Trig tr)
    {
        // Group setters by template part: one style per part.
        var groups = tr.Setters.GroupBy(s => s.TargetName).ToList();
        if (groups.Count == 1)
            return OneTriggerStyle(baseSelector, tr.Suffix, groups[0].Key, groups[0].Select(g => g.Setter));

        // Several parts: an outer style carrying the condition with nested part styles.
        var outer = new XElement(A + "Style", new XAttribute("Selector", baseSelector + tr.Suffix));
        foreach (var g in groups.Where(g => g.Key == null))
            foreach (var s in g) outer.Add(s.Setter);
        foreach (var g in groups.Where(g => g.Key != null))
            outer.Add(OneTriggerStyle("^", "", g.Key, g.Select(x => x.Setter)));
        return outer;
    }

    private XElement OneTriggerStyle(string baseSelector, string suffix, string? part, IEnumerable<XElement> setters)
    {
        var sel = baseSelector + suffix;
        if (part != null)
        {
            var (type, name) = SplitPart(part);
            sel += $" /template/ {type}#{name}";
        }
        var st = new XElement(A + "Style", new XAttribute("Selector", sel));
        foreach (var s in setters) st.Add(s);
        return st;
    }

    private static (string Type, string Name) SplitPart(string part)
    {
        var i = part.IndexOf('#');
        return i < 0 ? ("Control", part) : (part[..i], part[(i + 1)..]);
    }

    // ───────────────────────────── control templates ─────────────────────────────

    private XElement ConvertControlTemplate(XElement tpl, Ctx ctx, string targetType, out List<Trig> triggers)
    {
        triggers = new();
        var target = StripType(tpl.Attribute("TargetType")?.Value) ?? targetType;
        var tctx = ctx.Child("ControlTemplate", null, true);
        tctx.InTemplate = true;

        var ct = new XElement(A + "ControlTemplate");
        ct.SetAttributeValue("TargetType", target);

        // Named parts → their element type, for /template/ Type#Name selectors.
        var parts = new Dictionary<string, string>();
        foreach (var e in tpl.Descendants())
        {
            var nm = e.Attribute(X + "Name")?.Value ?? e.Attribute("Name")?.Value;
            if (nm != null)
            {
                var mapped = MapName(e, out _);
                parts[nm] = mapped.Namespace == Ctrl ? "ctrl|" + mapped.LocalName : mapped.LocalName;
            }
        }

        foreach (var c in tpl.Nodes())
        {
            if (c is XElement ce)
            {
                if (ce.Name.LocalName == "ControlTemplate.Triggers")
                {
                    foreach (var tr in ce.Elements())
                    {
                        var conds = TriggerConditions(tr);
                        if (conds == null) { Note(tr, $"template trigger <{tr.Name.LocalName}> not ported"); continue; }
                        var suffix = "";
                        var dataConds = new List<(string, string, string)>();
                        foreach (var cond in conds)
                        {
                            if (cond.Binding == null && Pseudo.TryGetValue((cond.Property!, cond.Value), out var ps)) suffix += ps;
                            else if (cond.Binding == null && cond.Property != null)
                                suffix += $"[{cond.Property}={cond.Value}]";
                            else
                            {
                                var ph = "{dt" + dataConds.Count + "}";
                                dataConds.Add((ph, cond.Binding!, cond.Value));
                                suffix += ph;
                            }
                        }
                        var setters = new List<(string?, XElement)>();
                        foreach (var s in tr.Elements().Where(e => e.Name.LocalName == "Setter"))
                        {
                            var tn = s.Attribute("TargetName")?.Value;
                            var partRef = tn != null ? (parts.TryGetValue(tn, out var pt) ? pt + "#" + tn : "Control#" + tn) : null;
                            foreach (var cs in ConvertSetter(s, ctx, target, new XElement("x"), null, "", inTheme: true))
                                setters.Add((partRef, cs));
                        }
                        var trig = new Trig(suffix, setters);
                        trig.DataConditions.AddRange(dataConds);
                        triggers.Add(trig);
                    }
                    continue;
                }
                var cv = ConvertElement(ce, tctx);
                if (cv != null) ct.Add(cv);
            }
            else if (c is XComment cm) ct.Add(new XComment(cm.Value));
        }

        // ContentPresenter needs its content bound explicitly in Avalonia.
        foreach (var cp in ct.Descendants(A + "ContentPresenter"))
        {
            var source = cp.Attribute("ContentSource")?.Value;
            cp.Attribute("ContentSource")?.Remove();
            if (cp.Attribute("Content") == null)
            {
                var what = source ?? "Content";
                cp.SetAttributeValue("Content", "{TemplateBinding " + what + "}");
                if (cp.Attribute("ContentTemplate") == null)
                    cp.SetAttributeValue("ContentTemplate", "{TemplateBinding " + what + "Template}");
            }
            if (cp.Attribute("Name") == null && cp.Attribute(X + "Name") == null && source == null)
                cp.SetAttributeValue("Name", "PART_ContentPresenter");
            if (cp.Attribute("RecognizesAccessKey") != null) cp.Attribute("RecognizesAccessKey")!.Remove();
        }
        foreach (var panel in ct.Descendants().Where(e => e.Attribute("IsItemsHost")?.Value == "True").ToList())
        {
            var ip = new XElement(A + "ItemsPresenter", new XAttribute("Name", "PART_ItemsPresenter"));
            foreach (var a in panel.Attributes().Where(a => a.Name.LocalName is "Margin" or "HorizontalAlignment" or "VerticalAlignment" or "Grid.Row" or "Grid.Column"))
                ip.SetAttributeValue(a.Name, a.Value);
            var orientation = panel.Attribute("Orientation")?.Value;
            var panelTemplate = new XElement(A + "ItemsPanelTemplate", new XElement(A + panel.Name.LocalName,
                orientation != null ? new XAttribute("Orientation", orientation) : null));
            ip.Add(new XElement(A + "ItemsPresenter.ItemsPanel", panelTemplate));
            panel.ReplaceWith(ip);
        }
        foreach (var cp in ct.Descendants(A + "ContentPresenter").Where(e => e.Attribute("Content")?.Value == "{TemplateBinding SelectedContent}"))
            cp.SetAttributeValue("Name", "PART_SelectedContentHost");

        foreach (var sv in ct.Descendants(A + "ScrollViewer").Where(e => (e.Attribute(X + "Name")?.Value ?? e.Attribute("Name")?.Value) == "PART_ContentHost"))
            Note(tpl, "template uses PART_ContentHost (TextBox template): port by hand");

        return ct;
    }

    // ───────────────────────────── keyed styles / resources ─────────────────────────────

    private static readonly HashSet<string> Templated = new()
    {
        "Button", "CheckBox", "RadioButton", "ComboBox", "ComboBoxItem", "TextBox", "Slider", "ProgressBar",
        "Expander", "TabItem", "TabControl", "ListBox", "ListBoxItem", "ToggleButton", "ScrollViewer", "MenuItem",
        "ContextMenu", "Label", "ContentControl", "RepeatButton", "ScrollBar", "Thumb", "ItemsControl", "ToolTip",
        "Window", "UserControl", "DataGrid", "ListView", "ListViewItem", "GridSplitter", "Separator", "TreeView", "Menu"
    };

    private XElement ConvertKeyedStyle(XElement style, Ctx ctx, string? key, string? forceTarget = null)
    {
        var target = forceTarget ?? StripType(style.Attribute("TargetType")?.Value) ?? "Control";
        var ct = new XElement(A + "ControlTheme");
        if (key != null) ct.SetAttributeValue(X + "Key", key);
        ct.SetAttributeValue("TargetType", target);
        if (ctx.DataType != null && !ctx.CompileOff && Types.IsNameable(ctx.DataType))
            ct.SetAttributeValue(X + "DataType", TypeRef(ctx.DataType));

        var basedOn = style.Attribute("BasedOn")?.Value;
        var hasTemplate = style.Elements().Any(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == "Template");
        if (basedOn != null) ct.SetAttributeValue("BasedOn", basedOn);
        else if (!hasTemplate && Templated.Contains(target))
            ct.SetAttributeValue("BasedOn", "{StaticResource {x:Type " + target + "}}");

        var nested = new List<XElement>();
        var holder = new XElement("holder");
        foreach (var s in style.Elements().Where(e => e.Name.LocalName == "Setter"))
            foreach (var cs in ConvertSetter(s, ctx, target, ct, holder, "^", inTheme: true))
                ct.Add(cs);
        foreach (var es in style.Elements().Where(e => e.Name.LocalName == "EventSetter"))
            Note(es, $"EventSetter {es.Attribute("Event")?.Value}={es.Attribute("Handler")?.Value} on style {key}: hook up in code-behind");
        nested.AddRange(holder.Elements());

        var triggers = style.Elements().FirstOrDefault(e => e.Name.LocalName == "Style.Triggers");
        if (triggers != null)
        {
            foreach (var tr in triggers.Elements())
            {
                var conds = TriggerConditions(tr);
                if (conds == null) { Note(tr, $"trigger <{tr.Name.LocalName}> not ported"); continue; }
                var suffix = "";
                var ok = true;
                foreach (var c in conds)
                {
                    if (c.Binding == null && Pseudo.TryGetValue((c.Property!, c.Value), out var ps)) suffix += ps;
                    else if (c.Binding == null && c.Property != null) suffix += $"[{c.Property}={c.Value}]";
                    else
                    {
                        ok = false;
                        Note(tr, $"DataTrigger {c.Binding}={c.Value} in keyed style {key}: needs a class or converter at the use sites");
                    }
                }
                if (!ok) continue;
                var st = new XElement(A + "Style", new XAttribute("Selector", "^" + suffix));
                foreach (var s in tr.Elements().Where(e => e.Name.LocalName == "Setter"))
                    foreach (var cs in ConvertSetter(s, ctx, target, st, null, "^", inTheme: true))
                        st.Add(cs);
                nested.Add(st);
            }
        }
        foreach (var n in nested) ct.Add(n);
        return ct;
    }

    private XElement ConvertImplicitStyle(XElement style, Ctx ctx)
    {
        var target = StripType(style.Attribute("TargetType")?.Value) ?? "Control";
        var st = new XElement(A + "Style", new XAttribute("Selector", target));
        var holder = new XElement("holder");
        foreach (var s in style.Elements().Where(e => e.Name.LocalName == "Setter"))
            foreach (var cs in ConvertSetter(s, ctx, target, st, holder, "^", inTheme: true))
                st.Add(cs);
        foreach (var h in holder.Elements()) st.Add(h);
        var triggers = style.Elements().FirstOrDefault(e => e.Name.LocalName == "Style.Triggers");
        if (triggers != null)
            foreach (var tr in triggers.Elements())
            {
                var conds = TriggerConditions(tr);
                if (conds == null) continue;
                var suffix = "";
                var ok = true;
                foreach (var c in conds)
                    if (c.Binding == null && Pseudo.TryGetValue((c.Property!, c.Value), out var ps)) suffix += ps;
                    else { ok = false; Note(tr, $"trigger {c.Property ?? c.Binding}={c.Value} in implicit {target} style not ported"); }
                if (!ok) continue;
                var ts = new XElement(A + "Style", new XAttribute("Selector", "^" + suffix));
                foreach (var s in tr.Elements().Where(e => e.Name.LocalName == "Setter"))
                    foreach (var cs in ConvertSetter(s, ctx, target, ts, null, "^", inTheme: true))
                        ts.Add(cs);
                st.Add(ts);
            }
        return st;
    }

    private static readonly HashSet<string> EliminatedConverters = new()
    {
        "BooleanToVisibilityConverter", "BoolToVisibilityConverter", "StringToVisibilityConverter",
        "NotNullToVisibilityConverter", "NullToVisibilityConverter", "InverseBooleanToVisibilityConverter"
    };

    private void ConvertResources(XElement resources, XElement dst, Ctx ctx, string elementLocal)
    {
        var res = new XElement(dst.Name.Namespace + (elementLocal + ".Resources"));
        foreach (var node in resources.Nodes())
        {
            if (node is XComment c) { res.Add(new XComment(c.Value)); continue; }
            if (node is not XElement e) continue;
            var key = e.Attribute(X + "Key")?.Value;
            switch (e.Name.LocalName)
            {
                case "Style" when key != null:
                    res.Add(ConvertKeyedStyle(e, ctx, key));
                    break;
                case "Style":
                    GetStyles(dst, elementLocal).Add(ConvertImplicitStyle(e, ctx));
                    break;
                case "ResourceDictionary":
                    Note(e, "nested ResourceDictionary: merge by hand");
                    break;
                default:
                    if (key != null && EliminatedConverters.Contains(key)) break;
                    var cv = ConvertElement(e, ctx);
                    if (cv != null) res.Add(cv);
                    break;
            }
        }
        if (res.HasElements) dst.AddFirst(res);
    }

    // ───────────────────────────── input bindings ─────────────────────────────

    private void ConvertInputBindings(XElement ib, XElement dst, Ctx ctx, string elementLocal)
    {
        foreach (var b in ib.Elements())
        {
            var cmd = b.Attribute("Command")?.Value;
            var param = b.Attribute("CommandParameter")?.Value;
            if (b.Name.LocalName == "MouseBinding")
            {
                var action = b.Attribute("MouseAction")?.Value ?? b.Attribute("Gesture")?.Value ?? "LeftClick";
                var prefix = action switch
                {
                    "LeftDoubleClick" => "ctrl:Click.DoubleCommand",
                    "RightClick" => "ctrl:Click.RightCommand",
                    _ => "ctrl:Click.Command"
                };
                if (cmd == null) { Note(b, "MouseBinding without Command"); continue; }
                dst.Add(new XAttribute(Ctrl + prefix["ctrl:".Length..], ConvertMarkupValue(b, cmd, ctx, "Command", elementLocal)));
                if (param != null)
                    dst.Add(new XAttribute(Ctrl + (prefix["ctrl:".Length..] + "Parameter"), ConvertMarkupValue(b, param, ctx, "CommandParameter", elementLocal)));
            }
            else if (b.Name.LocalName == "KeyBinding")
            {
                var gesture = b.Attribute("Gesture")?.Value;
                if (gesture == null)
                {
                    var key = b.Attribute("Key")?.Value ?? "";
                    var mods = (b.Attribute("Modifiers")?.Value ?? "").Replace("Control", "Ctrl").Replace(",", "+").Replace(" ", "");
                    gesture = string.IsNullOrEmpty(mods) ? key : mods + "+" + key;
                }
                var kbs = dst.Element(dst.Name.Namespace + (elementLocal + ".KeyBindings"));
                if (kbs == null) { kbs = new XElement(dst.Name.Namespace + (elementLocal + ".KeyBindings")); dst.Add(kbs); }
                var kb = new XElement(A + "KeyBinding", new XAttribute("Gesture", gesture));
                if (cmd != null) kb.SetAttributeValue("Command", ConvertMarkupValue(b, cmd, ctx, "Command", elementLocal));
                if (param != null) kb.SetAttributeValue("CommandParameter", ConvertMarkupValue(b, param, ctx, "CommandParameter", elementLocal));
                kbs.Add(kb);
            }
        }
    }

    // ───────────────────────────── shape changes ─────────────────────────────

    private static readonly string[] LayoutAttrs =
    {
        "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan", "DockPanel.Dock", "Canvas.Left", "Canvas.Top",
        "Margin", "HorizontalAlignment", "VerticalAlignment", "IsVisible"
    };

    private XElement WrapLayoutTransform(XElement dst, XElement lt, Ctx ctx)
    {
        var wrapper = new XElement(A + "LayoutTransformControl");
        foreach (var n in LayoutAttrs)
        {
            var a = dst.Attribute(n);
            if (a == null) continue;
            wrapper.SetAttributeValue(n, a.Value);
            a.Remove();
        }
        var prop = new XElement(A + "LayoutTransformControl.LayoutTransform");
        foreach (var c in lt.Elements())
        {
            var cv = ConvertElement(c, ctx);
            if (cv != null) prop.Add(cv);
        }
        wrapper.Add(prop, dst);
        return wrapper;
    }

    private XElement ConvertGroupBox(XElement src, XElement dst)
    {
        // <GroupBox Header="X">content</GroupBox> → a bordered panel with a header line.
        var header = src.Attribute("Header")?.Value;
        dst.Attribute("Header")?.Remove();
        if (dst.Attribute("BorderBrush") == null) dst.SetAttributeValue("BorderBrush", "#D5DFE5");
        if (dst.Attribute("BorderThickness") == null) dst.SetAttributeValue("BorderThickness", "1");
        if (dst.Attribute("CornerRadius") == null) dst.SetAttributeValue("CornerRadius", "4");
        var padding = dst.Attribute("Padding")?.Value ?? "6";
        dst.SetAttributeValue("Padding", padding);

        var content = dst.Nodes().Where(n => n is not XElement e || !e.Name.LocalName.Contains('.')).ToList();
        foreach (var n in content) n.Remove();
        var panel = new XElement(A + "DockPanel");
        if (header != null)
            panel.Add(new XElement(A + "TextBlock",
                new XAttribute("DockPanel.Dock", "Top"),
                new XAttribute("Text", header),
                new XAttribute("FontWeight", "SemiBold"),
                new XAttribute("Margin", "0,0,0,6")));
        foreach (var n in content) panel.Add(n);
        dst.Add(panel);
        return dst;
    }

    private static XElement ConvertStatusBar(XElement dst)
    {
        // WPF's StatusBar lays items out in a DockPanel; Right-aligned items dock right.
        var items = dst.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
        foreach (var i in items) i.Remove();
        var dock = new XElement(A + "DockPanel", new XAttribute("LastChildFill", "False"));
        foreach (var i in items)
        {
            if (i.Name.LocalName == "Separator")
            {
                dock.Add(new XElement(A + "Border", new XAttribute("Width", "1"), new XAttribute("Margin", "6,2"),
                    new XAttribute("Background", "#22000000")));
                continue;
            }
            if (i.Attribute("HorizontalAlignment")?.Value == "Right")
                i.SetAttributeValue("DockPanel.Dock", "Right");
            dock.Add(i);
        }
        if (dst.Attribute("Background") == null) dst.SetAttributeValue("Background", "#F0F0F0");
        dst.Add(dock);
        return dst;
    }
}
