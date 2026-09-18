using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia.Controls;

namespace FlipPix.UI.Linux.Services;

public class WindowPositionService
{
    private static readonly string ConfigDir = UserPaths.ConfigDir;
    private static readonly string ConfigFile = Path.Combine(ConfigDir, "window-positions.json");
    private Dictionary<string, WindowPosition> _positions = new();

    public WindowPositionService()
    {
        LoadAll();
    }

    private void LoadAll()
    {
        try
        {
            if (File.Exists(ConfigFile))
                _positions = JsonSerializer.Deserialize<Dictionary<string, WindowPosition>>(File.ReadAllText(ConfigFile)) ?? new();
        }
        catch { _positions = new(); }
    }

    private void SaveAll()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(_positions));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save window positions: {ex.Message}");
        }
    }

    public void SavePosition(string windowName, Window window)
    {
        _positions[windowName] = new WindowPosition
        {
            Left = window.Position.X,
            Top = window.Position.Y,
            Width = window.Width,
            Height = window.Height
        };
        SaveAll();
    }

    public bool LoadPosition(string windowName, Window window)
    {
        if (!_positions.TryGetValue(windowName, out var pos)) return false;
        window.Position = new Avalonia.PixelPoint(pos.Left, pos.Top);
        window.Width = pos.Width;
        window.Height = pos.Height;
        return true;
    }

    private class WindowPosition
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
