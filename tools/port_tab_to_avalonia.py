#!/usr/bin/env python3
"""
Port one WPF tab into an Avalonia UserControl.

The Linux build (FlipPix.UI.Linux) is an Avalonia port of the WPF app, and its two
generator windows lag WPF's by thousands of lines of XAML. The differences between the
two dialects are mostly mechanical, so this does the mechanical part and reports every
construct that needs a human: WPF triggers, MediaElement, and anything Avalonia has no
control for.

    python tools/port_tab_to_avalonia.py \
        --source FlipPix.UI/VideoGeneratorWindow.xaml \
        --start 57 --end 494 \
        --class Scail2View --datacontext Scail2VM \
        --out FlipPix.UI.Linux/Views/Video/Scail2View.axaml

--start/--end are 1-based inclusive line numbers of the <TabItem> block in the WPF file.
The generated file always needs reading afterwards: the report lists what was dropped.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

# --- attributes WPF has and Avalonia does not; dropping them changes nothing visible ---
DROP_ATTRS = [
    "SnapsToDevicePixels",
    "UseLayoutRounding",
    "TextOptions.TextFormattingMode",
    "TextOptions.TextRenderingMode",
    "ScrollViewer.CanContentScroll",
    "VirtualizingStackPanel.IsVirtualizing",
    "VirtualizingStackPanel.VirtualizationMode",
    "KeyboardNavigation.TabNavigation",
    "ScrubbingEnabled",
    "LoadedBehavior",
    "UnloadedBehavior",
]

# --- WPF cursor names that Avalonia spells out in full ---
CURSORS = {
    "SizeWE": "SizeWestEast",
    "SizeNS": "SizeNorthSouth",
    "SizeNWSE": "TopLeftCorner",
    "SizeNESW": "TopRightCorner",
    "IBeam": "Ibeam",
}

# --- WPF property triggers and the Avalonia pseudo-class that replaces them ---
PSEUDO_CLASSES = {
    ("IsMouseOver", "True"): ":pointerover",
    ("IsPressed", "True"): ":pressed",
    ("IsEnabled", "False"): ":disabled",
    ("IsChecked", "True"): ":checked",
    ("IsSelected", "True"): ":selected",
    ("IsFocused", "True"): ":focus",
    ("IsKeyboardFocused", "True"): ":focus",
}

# --- properties the Fluent templates paint on the ContentPresenter rather than the control ---
TEMPLATE_PART_PROPERTIES = {"Background", "BorderBrush", "Foreground", "Opacity"}

# --- types with no Avalonia equivalent: reported, never silently dropped ---
UNSUPPORTED_TYPES = [
    "GroupBox",
    "Hyperlink",
    "DropShadowEffect",
    "BlurEffect",
    "VisualBrush",
    "InkCanvas",
    "Frame",
    "WebBrowser",
    "DocumentViewer",
    "AdornerDecorator",
]


def find_open_tag(text: str, style_start: int, owner: str) -> tuple[int, int] | None:
    """
    Locate the opening tag of the element whose <owner.Style> block starts at style_start.
    Scans backwards for the tag's '<', then forwards for its '>', skipping quoted values so a
    '>' inside a tooltip does not end the tag early.
    """
    open_at = text.rfind("<" + owner, 0, style_start)
    while open_at != -1:
        after = text[open_at + 1 + len(owner): open_at + 2 + len(owner)]
        if after in (" ", "\t", "\n", "\r", ">", "/"):
            break
        open_at = text.rfind("<" + owner, 0, open_at)
    if open_at == -1:
        return None

    i = open_at
    quote = ""
    while i < style_start:
        c = text[i]
        if quote:
            if c == quote:
                quote = ""
        elif c in "\"'":
            quote = c
        elif c == ">":
            return open_at, i
        i += 1
    return None


def parse_setters(fragment: str) -> list[tuple[str, str]]:
    """Property/Value pairs from <Setter .../> elements, attribute form only."""
    out = []
    for m in re.finditer(r'<Setter\s+Property="([\w.]+)"\s+Value="([^"]*)"\s*/>', fragment):
        out.append((m.group(1), m.group(2)))
    return out


def rewrite_style_block(owner: str, body: str, indent: str, report: list[str],
                        has_own_template: bool = False) -> tuple[str, str]:
    """
    Turn one WPF <X.Style> trigger block into Avalonia.

    Returns (attributes to add to the opening tag, markup to put inside the element).
    A block that only flips Visibility becomes an IsVisible binding; anything else becomes a
    Classes.<name> binding plus an inline style, which is Avalonia's equivalent of a DataTrigger.
    Setters are never lifted onto the element as attributes: a local value in Avalonia beats
    every style, so the trigger would not be able to override it.
    """
    triggers_m = re.search(r"<Style\.Triggers>(.*?)</Style\.Triggers>", body, re.S)
    triggers_body = triggers_m.group(1) if triggers_m else ""
    base_body = body[: triggers_m.start()] if triggers_m else body
    base = parse_setters(base_body)

    data_triggers = []
    for m in re.finditer(r'<DataTrigger\s+Binding="\{Binding\s+([^}]+)\}"\s+Value="([^"]*)"\s*>(.*?)</DataTrigger>',
                         triggers_body, re.S):
        data_triggers.append((m.group(1).strip(), m.group(2), parse_setters(m.group(3))))

    # --- property triggers (IsMouseOver, IsEnabled, ...) become pseudo-class selectors ---
    property_triggers = []
    for m in re.finditer(r'<Trigger\s+Property="(\w+)"\s+Value="(\w+)"\s*>(.*?)</Trigger>', triggers_body, re.S):
        pseudo = PSEUDO_CLASSES.get((m.group(1), m.group(2)))
        if pseudo is None:
            property_triggers = None
            break
        property_triggers.append((pseudo, parse_setters(m.group(3))))

    if property_triggers and not data_triggers:
        # Background and friends live on a template part, not on the control: Fluent paints
        # the ContentPresenter, while an element with its own inline template paints its Border.
        # A setter on the control itself would lose to the theme's own hover/disabled rules.
        part_selector = "Border" if has_own_template else "ContentPresenter"
        # A Border has no Foreground; on the Fluent ContentPresenter it is the only place a
        # hover/disabled text colour sticks.
        part_properties = TEMPLATE_PART_PROPERTIES if part_selector == "ContentPresenter" \
            else TEMPLATE_PART_PROPERTIES - {"Foreground"}
        styles = []
        if base:
            setters = "".join(f'\n{indent}        <Setter Property="{p}" Value="{v}"/>' for p, v in base)
            styles.append(f'\n{indent}    <Style Selector="{owner}">{setters}\n{indent}    </Style>')
        for pseudo, setters in property_triggers:
            part = [(p, v) for p, v in setters if p in part_properties]
            direct = [(p, v) for p, v in setters if p not in part_properties]
            if direct:
                rules = "".join(f'\n{indent}        <Setter Property="{p}" Value="{v}"/>' for p, v in direct)
                styles.append(f'\n{indent}    <Style Selector="{owner}{pseudo}">{rules}\n{indent}    </Style>')
            if part:
                rules = "".join(f'\n{indent}        <Setter Property="{p}" Value="{v}"/>' for p, v in part)
                styles.append(f'\n{indent}    <Style Selector="{owner}{pseudo} /template/ {part_selector}">'
                              f'{rules}\n{indent}    </Style>')
        if not styles:
            return "", ""
        inner = f"{indent}<{owner}.Styles>" + "".join(styles) + f"\n{indent}</{owner}.Styles>"
        return "", inner

    other = re.findall(r"<(MultiDataTrigger|EventTrigger|Trigger)\b", triggers_body)
    if other or (triggers_body.strip() and not data_triggers):
        report.append(f"<{owner}.Style> uses {', '.join(sorted(set(other))) or 'a trigger form'} "
                      f"this tool cannot translate - port it by hand")
        return "", ""

    # --- the common case: one DataTrigger that only flips Visibility ---
    all_props = {p for p, _ in base} | {p for _, _, s in data_triggers for p, _ in s}
    if all_props == {"Visibility"} and len(data_triggers) == 1:
        binding, value, setters = data_triggers[0]
        base_visible = next((v for p, v in base if p == "Visibility"), "Visible") == "Visible"
        trigger_visible = next((v for p, v in setters if p == "Visibility"), "Visible") == "Visible"

        # "no image yet" placeholders trigger on {x:Null} rather than on a bool.
        if value == "{x:Null}":
            conv = "IsNullConverter" if trigger_visible else "IsNotNullConverter"
            return f' IsVisible="{{Binding {binding}, Converter={{StaticResource {conv}}}}}"', ""

        # Any other literal (an index, a mode number) becomes an equality test.
        if value not in ("True", "False"):
            conv = "EqualsConverter" if trigger_visible else "NotEqualsConverter"
            return (f' IsVisible="{{Binding {binding}, Converter={{StaticResource {conv}}},'
                    f' ConverterParameter={value}}}"'), ""

        # Visible when the binding equals the trigger value and the trigger shows it, or when it
        # does not and the base shows it.
        show_when_true = trigger_visible if value == "True" else base_visible
        inverse = not show_when_true
        conv = ', Converter={StaticResource InverseBoolConverter}' if inverse else ""
        return f' IsVisible="{{Binding {binding}{conv}}}"', ""

    # --- everything else: classes + an inline style per trigger ---
    attrs = ""
    styles = []
    if base:
        setters = "".join(f'\n{indent}        <Setter Property="{p}" Value="{v}"/>' for p, v in base)
        styles.append(f'\n{indent}    <Style Selector="{owner}">{setters}\n{indent}    </Style>')
    for i, (binding, value, setters) in enumerate(data_triggers):
        if value not in ("True", "False"):
            report.append(f"<{owner}.Style> triggers on Value=\"{value}\" (not a bool) - port it by hand")
            return "", ""
        cls = f"t{i}" if len(data_triggers) > 1 else "on"
        conv = ', Converter={StaticResource InverseBoolConverter}' if value == "False" else ""
        attrs += f' Classes.{cls}="{{Binding {binding}{conv}}}"'
        rules = "".join(f'\n{indent}        <Setter Property="{p}" Value="{v}"/>' for p, v in setters)
        styles.append(f'\n{indent}    <Style Selector="{owner}.{cls}">{rules}\n{indent}    </Style>')

    if not styles:
        return attrs, ""
    inner = f"\n{indent}<{owner}.Styles>" + "".join(styles) + f"\n{indent}</{owner}.Styles>"
    return attrs, inner


def convert(xaml: str, report: list[str]) -> str:
    text = xaml

    # ---- inline <X.Style> blocks are WPF triggers; Avalonia expresses the same thing as
    # ---- an IsVisible binding or as a Classes binding with an inline style.
    while True:
        m = re.search(r"[ \t]*<(\w+)\.Style>.*?</\1\.Style>[ \t]*\n?", text, flags=re.S)
        if not m:
            break
        owner = m.group(1)
        body = re.search(r"<%s\.Style>(.*?)</%s\.Style>" % (owner, owner), m.group(0), re.S).group(1)
        indent = " " * (len(m.group(0)) - len(m.group(0).lstrip()))
        tag = find_open_tag(text, m.start(), owner)
        has_own_template = False
        if tag:
            end_at = text.find(f"</{owner}>", tag[1])
            scope = text[tag[1]: end_at if end_at != -1 else len(text)]
            has_own_template = f"<{owner}.Template>" in scope
        attrs, inner = rewrite_style_block(owner, body, indent, report, has_own_template)
        text = text[: m.start()] + inner.lstrip("\n").rjust(0) + ("\n" if inner else "") + text[m.end():]
        if attrs and tag:
            open_at, close_at = tag
            text = text[:close_at] + attrs + text[close_at:]

    # ---- ControlTemplate.Triggers inside inline templates: same story.
    def drop_template_triggers(m: re.Match) -> str:
        report.append("dropped <ControlTemplate.Triggers> - rewrite as ^:pointerover / ^:disabled styles")
        return ""

    text = re.sub(r"[ \t]*<ControlTemplate\.Triggers>.*?</ControlTemplate\.Triggers>\s*\n",
                  drop_template_triggers, text, flags=re.S)

    # ---- keyed styles become ControlThemes (see Themes/SharedStyles.axaml) ----
    text = text.replace('Style="{StaticResource ', 'Theme="{StaticResource ')

    # ---- the element form of a tooltip, <X.ToolTip><ToolTip>...</ToolTip></X.ToolTip> ----
    def unwrap_tooltip(m: re.Match) -> str:
        indent, attrs, inner = m.group(1), m.group(3).strip(), m.group(4).strip()
        maxw = re.search(r'MaxWidth="[\d.]+"', attrs)
        if maxw and "MaxWidth=" not in inner:
            inner = inner.replace("/>", " " + maxw.group(0) + "/>", 1)
        return f"{indent}<ToolTip.Tip>\n{indent}    {inner}\n{indent}</ToolTip.Tip>"

    text = re.sub(r"([ \t]*)<(\w+)\.ToolTip>\s*<ToolTip([^>]*)>(.*?)</ToolTip>\s*</\2\.ToolTip>",
                  unwrap_tooltip, text, flags=re.S)

    # ---- a ContentPresenter inside an inline template renders nothing until Content is
    # ---- bound: WPF infers it from the templated parent, Avalonia does not ----
    def bind_presenter(m: re.Match) -> str:
        tag = m.group(0)
        if "Content=" in tag:
            return tag
        return (tag[:-2].rstrip() + ' Content="{TemplateBinding Content}"'
                ' ContentTemplate="{TemplateBinding ContentTemplate}"/>')

    text = re.sub(r"<ContentPresenter\b[^<>]*/>", bind_presenter, text)

    # ---- Avalonia projects an item with a binding, not a property path ----
    text = re.sub(r'DisplayMemberPath="(\w+)"', r'DisplayMemberBinding="{Binding \1}"', text)

    # ---- renamed / absent control properties ----
    text = text.replace("RenderOptions.BitmapScalingMode=", "RenderOptions.BitmapInterpolationMode=")
    text = re.sub(r'\s*IsMoveToPointEnabled="[^"]*"', "", text)

    # ---- ItemContainerStyle becomes a style on the container inside the items host ----
    for host in ("ItemsControl", "ListBox", "ListView", "ComboBox"):
        text = text.replace(f"<{host}.ItemContainerStyle>", f"<{host}.Styles>")
        text = text.replace(f"</{host}.ItemContainerStyle>", f"</{host}.Styles>")
    text = re.sub(r'<Style TargetType="(\w+)">', r'<Style Selector="\1">', text)

    # ---- a MouseBinding with no Command was dead markup in WPF too; one with a Command
    # ---- has no Avalonia equivalent and needs a Button or a PointerPressed handler ----
    text = re.sub(r"\s*<(\w+)\.InputBindings>\s*<MouseBinding(?![^>]*Command)[^>]*/>\s*</\1\.InputBindings>",
                  "", text)
    for m in re.finditer(r"<MouseBinding[^>]*Command=[^>]*/>", text):
        line = text[: m.start()].count("\n") + 1
        report.append(f"line {line}: <MouseBinding> with a Command - rewrite as a Button or a "
                      f"PointerPressed handler")

    # ---- Visibility -> IsVisible ----
    text = re.sub(
        r'Visibility="\{Binding\s+([^},]+?)\s*,\s*Converter=\{StaticResource Bool(?:ean)?ToVisibilityConverter\}'
        r'\s*,\s*ConverterParameter=Invert\}"',
        r'IsVisible="{Binding \1, Converter={StaticResource InverseBoolConverter}}"', text)
    text = re.sub(
        r'Visibility="\{Binding\s+([^},]+?)\s*,\s*Converter=\{StaticResource StringToVisibilityConverter\}\}"',
        r'IsVisible="{Binding \1, Converter={StaticResource StringToVisibilityConverter}}"', text)
    text = re.sub(
        r'Visibility="\{Binding\s+([^},]+?)\s*,\s*Converter=\{StaticResource Bool(?:ean)?ToVisibilityConverter\}\}"',
        r'IsVisible="{Binding \1}"', text)
    text = re.sub(
        r'Visibility="\{Binding\s+([^}]+?)\}"',
        r'IsVisible="{Binding \1}"', text)
    text = text.replace('Visibility="Collapsed"', 'IsVisible="False"')
    text = text.replace('Visibility="Hidden"', 'IsVisible="False"')
    text = text.replace('Visibility="Visible"', 'IsVisible="True"')

    # ---- binding syntax ----
    text = text.replace(", UpdateSourceTrigger=PropertyChanged", "")
    text = text.replace(",UpdateSourceTrigger=PropertyChanged", "")
    text = re.sub(r"RelativeSource=\{RelativeSource AncestorType=\{x:Type (\w+)\}\}",
                  r"RelativeSource={RelativeSource AncestorType=\1}", text)
    text = re.sub(r"\{Binding\s+([^},]*?),?\s*RelativeSource=\{RelativeSource AncestorType=(\w+)\}([^}]*)\}",
                  lambda m: "{Binding $parent[%s].%s%s}" % (m.group(2), m.group(1).strip() or "DataContext", m.group(3)),
                  text)

    # ---- tooltips are an attached property in Avalonia ----
    text = re.sub(r'\bToolTip="', 'ToolTip.Tip="', text)
    text = re.sub(r'\bToolTipService\.\w+="[^"]*"\s*', "", text)

    # ---- attached-property renames ----
    text = text.replace("Panel.ZIndex=", "ZIndex=")

    # ---- Avalonia picks the value out of an item with a binding, not a path ----
    text = re.sub(r'SelectedValuePath="(\w+)"', r'SelectedValueBinding="{Binding \1}"', text)

    # ---- an element left empty by a dropped trigger block should close itself ----
    text = re.sub(r"<(\w+)([^<>]*[^<>/])>\s*\n\s*</\1>", r"<\1\2/>", text)

    # ---- cursors ----
    for wpf, avalonia in CURSORS.items():
        text = text.replace(f'Cursor="{wpf}"', f'Cursor="{avalonia}"')

    # ---- attributes to drop outright ----
    for attr in DROP_ATTRS:
        text = re.sub(rf'\s*\b{re.escape(attr)}="[^"]*"', "", text)

    # ---- scrollbars on a TextBox are attached properties in Avalonia ----
    def fix_textbox(m: re.Match) -> str:
        block = m.group(0)
        block = re.sub(r'(?<!ScrollViewer\.)\b(Horizontal|Vertical)ScrollBarVisibility=',
                       r'ScrollViewer.\1ScrollBarVisibility=', block)
        return block

    text = re.sub(r"<TextBox\b[^>]*?/?>", fix_textbox, text, flags=re.S)

    # ---- Image.Source binds a WPF BitmapSource, which Avalonia cannot paint directly ----
    def fix_image(m: re.Match) -> str:
        block = m.group(0)
        if "Converter=" in block:
            return block
        return re.sub(r'Source="\{Binding ([^}]+)\}"',
                      r'Source="{Binding \1, Converter={StaticResource PathToBitmapConverter}}"',
                      block)

    text = re.sub(r"<Image\b[^>]*?/?>", fix_image, text, flags=re.S)

    # ---- MediaElement has no Avalonia equivalent; controls:VideoPreview stands in ----
    def fix_media(m: re.Match) -> str:
        block = m.group(0)
        name = re.search(r'x:Name="(\w+)"', block)
        source = re.search(r'Source="(\{[^"]+\}|[^"]+)"', block)
        handlers = re.findall(r'\b(Media\w+)="(\w+)"', block)
        for _, handler in handlers:
            report.append(f"MediaElement handler {handler} has no VideoPreview equivalent - check the code-behind")
        attrs = []
        if name:
            attrs.append(f'x:Name="{name.group(1)}"')
        if source:
            attrs.append(f'Source="{source.group(1)}"')
        vis = re.search(r'IsVisible="(\{[^"]+\}|[^"]+)"', block)
        if vis:
            attrs.append(f'IsVisible="{vis.group(1)}"')
        report.append("replaced a MediaElement with controls:VideoPreview (poster frame + external player)")
        return "<controls:VideoPreview " + " ".join(attrs) + "/>"

    text = re.sub(r"<MediaElement\b.*?/>", fix_media, text, flags=re.S)

    # ---- report anything Avalonia simply does not have ----
    for kind in UNSUPPORTED_TYPES:
        for m in re.finditer(rf"<{kind}\b", text):
            line = text[: m.start()].count("\n") + 1
            report.append(f"line {line}: <{kind}> has no Avalonia equivalent - needs a hand-written replacement")
    for m in re.finditer(r"<(DataTrigger|Trigger|MultiDataTrigger|EventTrigger)\b", text):
        line = text[: m.start()].count("\n") + 1
        report.append(f"line {line}: <{m.group(1)}> survived the rewrite - convert it by hand")

    return text


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source", required=True, help="WPF .xaml file to lift the tab out of")
    ap.add_argument("--start", type=int, required=True, help="first line of the <TabItem> block (1-based)")
    ap.add_argument("--end", type=int, required=True, help="last line of the <TabItem> block (inclusive)")
    ap.add_argument("--class", dest="cls", required=True, help="Avalonia UserControl class name, e.g. Scail2View")
    ap.add_argument("--datacontext", help="sub-VM the tab binds to, recorded as a comment")
    ap.add_argument("--namespace", default="FlipPix.UI.Linux.Views.Video")
    ap.add_argument("--out", required=True, help="destination .axaml path")
    args = ap.parse_args()

    lines = Path(args.source).read_text(encoding="utf-8-sig").splitlines()
    block = "\n".join(lines[args.start - 1: args.end])

    # Strip the <TabItem Header="..."> shell: the header moves to the window, the body
    # becomes the UserControl's content.
    header = None
    m = re.match(r'\s*<TabItem\b[^>]*Header="([^"]*)"[^>]*>\s*\n(.*)\n\s*</TabItem>\s*$', block, flags=re.S)
    if m:
        header, block = m.group(1), m.group(2)

    # The tab's root Grid usually carries DataContext={Binding FooVM}; the window sets that
    # on the view instead, so drop it here.
    block = re.sub(r'\s*DataContext="\{Binding \w+\}"', "", block, count=1)

    report: list[str] = []
    converted = convert(block, report)

    # Re-indent from "inside a TabItem inside a TabControl" to "inside a UserControl".
    dedent = min((len(l) - len(l.lstrip()) for l in converted.splitlines() if l.strip()), default=0)
    dedent = max(0, dedent - 4)
    converted = "\n".join(l[dedent:] if len(l) > dedent else l for l in converted.splitlines())

    handlers = sorted(set(re.findall(r'\b(?:Click|DragDelta|SizeChanged|SelectionChanged|Checked|'
                                     r'Unchecked|TextChanged|MouseLeftButtonDown|MouseDown|MouseUp|'
                                     r'MouseMove|Loaded|DragCompleted)="(\w+)"', converted)))

    origin = f"{Path(args.source).name} lines {args.start}-{args.end}"
    doc = [
        '<UserControl xmlns="https://github.com/avaloniaui"',
        '             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
        '             xmlns:controls="clr-namespace:FlipPix.UI.Linux.Controls"',
    ]
    if "ctrl:" in converted:
        doc.append('             xmlns:ctrl="clr-namespace:FlipPix.UI.Linux.Controls"')
    if "sys:" in converted:
        doc.append('             xmlns:sys="clr-namespace:System;assembly=System.Runtime"')
    doc += [
        f'             x:Class="{args.namespace}.{args.cls}">',
        "",
        f"    <!-- Ported from {origin}" + (f' ("{header}").' if header else ".") ,
    ]
    if args.datacontext:
        doc.append(f"         DataContext is the window's {args.datacontext}. -->")
    else:
        doc.append("      -->")
    doc.append("")
    doc.append(converted)
    doc.append("")
    doc.append("</UserControl>")

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("\n".join(doc) + "\n", encoding="utf-8")

    print(f"wrote {out} ({len(converted.splitlines())} lines of tab body)")
    if handlers:
        print("\ncode-behind handlers this view needs:")
        for h in handlers:
            print(f"  {h}")
    if report:
        print("\nneeds a human:")
        for item in report:
            print(f"  {item}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
