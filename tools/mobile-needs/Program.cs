// Writes every graph the phone can submit to <out>/<name>.json, built by FlipPix.Remote's own
// recipes. tools/starter_manifest.py reads them to list the node classes and model files.
using FlipPix.Remote.Engine;

var outDir = args.Length > 0 ? args[0] : "mobile-graphs";
Directory.CreateDirectory(outDir);

void Write(string name, System.Text.Json.Nodes.JsonObject g) =>
    File.WriteAllText(Path.Combine(outDir, name + ".json"), g.ToJsonString());

foreach (var look in ImageLook.All)
    Write("image-" + look.Key, look.Build("a test prompt", ImageShape.Square, 1));

// Four references is the widest graph the Video and Story pages build.
Write("video", VideoRecipe.Build(new[] { "a.png", "b.png", "c.png", "d.png" }, "a test", 15, "16:9 (Widescreen)", 1, "FlipPixMobile/Video"));

Console.WriteLine("Wrote " + Directory.GetFiles(outDir, "*.json").Length + " graphs to " + Path.GetFullPath(outDir));
