using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Small per-user store for UI layout values that should survive a restart — splitter
    /// positions and the like — kept in %AppData%\FlipPix\layout.json as name → number.
    /// Reads and writes never throw: a missing or broken file just means defaults.
    /// </summary>
    public static class LayoutStateStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix", "layout.json");

        private static readonly object Gate = new();
        private static Dictionary<string, double>? _values;

        private static Dictionary<string, double> Values
        {
            get
            {
                if (_values != null) return _values;
                try
                {
                    if (File.Exists(FilePath))
                        _values = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Layout state unreadable, using defaults: {ex.Message}");
                }
                return _values ??= new Dictionary<string, double>();
            }
        }

        public static bool TryGet(string key, out double value)
        {
            lock (Gate) return Values.TryGetValue(key, out value);
        }

        public static void Set(string key, double value)
        {
            lock (Gate)
            {
                Values[key] = value;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(Values, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(tmp, FilePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to save layout state '{key}': {ex.Message}");
                }
            }
        }
    }
}
