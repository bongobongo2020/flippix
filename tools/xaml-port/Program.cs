using System.Xml.Linq;
using XamlPort;

// xaml-port --assembly <FlipPix.UI.Linux.dll> --in <Window.xaml> --out <Window.axaml>
//           --class <x:Class> [--datatype <view-model type>] [--root <element, e.g. win:AppWindow>]
//           [--report <file>]

var opts = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2)
    opts[args[i].TrimStart('-')] = args[i + 1];

string Need(string k) => opts.TryGetValue(k, out var v) ? v : throw new ArgumentException($"--{k} is required");

var types = new Types(Need("assembly"));
var input = Need("in");
var doc = XDocument.Load(input, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
var converter = new Converter(types, input);
if (opts.GetValueOrDefault("kind") == "styles")
{
    var styles = converter.ConvertStyles(doc);
    File.WriteAllText(Need("out"), XamlWriter.Write(styles));
    Console.WriteLine($"{Path.GetFileName(input)} → {Path.GetFileName(Need("out"))}: {converter.Report.Count} notes");
    foreach (var line in converter.Report) Console.WriteLine("  " + line);
    if (opts.GetValueOrDefault("report") is { } r) File.AppendAllLines(r, converter.Report);
    return;
}
// Two passes: the first learns element names and template item types, the second uses them.
converter.Convert(new XDocument(doc), Need("class"), opts.GetValueOrDefault("datatype"), opts.GetValueOrDefault("root"));
converter.BeginPass();
var result = converter.Convert(doc, Need("class"), opts.GetValueOrDefault("datatype"), opts.GetValueOrDefault("root"));

var text = XamlWriter.Write(result);
File.WriteAllText(Need("out"), text);

var report = opts.GetValueOrDefault("report");
if (report != null) File.AppendAllLines(report, converter.Report);
Console.WriteLine($"{Path.GetFileName(input)} → {Path.GetFileName(Need("out"))}: {converter.Report.Count} notes");
foreach (var line in converter.Report.Take(opts.ContainsKey("quiet") ? 0 : 400))
    Console.WriteLine("  " + line);
