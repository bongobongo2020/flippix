using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FlipPix.Core.Models;
using FlipPix.Core.Services;

namespace FlipPix.Tests;

/// <summary>
/// Concurrent saves (a browse folder remembered while settings are saved) used to share one temp file,
/// so one writer could move the other's file away and fail with "Could not find file settings.json".
/// </summary>
public sealed class SettingsServiceTests
{
    [Fact]
    public void ConcurrentSaves_AllSucceed_AndTheFileHoldsTheNewestState()
    {
        var dir = Directory.CreateTempSubdirectory("flippix-settings-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            var service = new SettingsService(path);
            var folders = Enumerable.Range(0, 20).Select(i => dir.CreateSubdirectory($"f{i}").FullName).ToArray();

            // SaveSettings throws if the write fails; Parallel.For surfaces that as an AggregateException.
            Parallel.For(0, 200, i =>
            {
                if (i % 2 == 0) service.SaveSettings(service.Settings);
                else service.SetLastBrowseFolder($"k{i % 7}", folders[i % folders.Length]);
            });
            service.SetLastBrowseFolder("final", folders[0]);

            var saved = JsonSerializer.Deserialize<ComfyUISettings>(File.ReadAllText(path))!;
            Assert.Equal(folders[0], saved.LastBrowseFolders!["final"]);
            Assert.Empty(Directory.GetFiles(dir.FullName, "*.tmp"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
