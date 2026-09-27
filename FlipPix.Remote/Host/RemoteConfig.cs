using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlipPix.Remote.Contracts;

namespace FlipPix.Remote.Host;

/// <summary>A phone allowed in. Only a hash of its token is kept, so the file grants nothing if read.</summary>
public sealed class PairedDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastSeen { get; set; }
}

/// <summary>
/// The remote's own settings, in <c>%AppData%\FlipPix\remote.json</c> rather than the main settings
/// file: SettingsWindow saves by rewriting ComfyUISettings, and paired phones must not depend on every
/// field of that round-trip surviving.
/// </summary>
public sealed class RemoteConfig
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = RemoteApi.DefaultPort;
    public List<PairedDevice> Devices { get; set; } = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix", "remote.json");

    public static RemoteConfig Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<RemoteConfig>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception) { /* unreadable: start clean; phones pair again */ }
        return new RemoteConfig();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public PairedDevice? Match(string token)
    {
        var hash = Encoding.ASCII.GetBytes(Hash(token));
        return Devices.FirstOrDefault(d =>
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(d.TokenHash), hash));
    }
}
