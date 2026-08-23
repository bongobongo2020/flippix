using System;
using System.IO;

namespace FlipPix.UI.Linux.Services
{
    /// <summary>
    /// Per-user storage locations, following the XDG Base Directory spec on Linux.
    ///
    /// Two things this fixes over the ad-hoc Path.Combine calls it replaces. First, call sites
    /// disagreed on the folder's capitalisation ("FlipPix" vs "flippix"); Windows folded those
    /// together but ext4 does not, so the app silently used two config directories. Second,
    /// logs belong in the XDG state directory rather than next to the settings.
    /// </summary>
    public static class UserPaths
    {
        /// <summary>Canonical folder name. Must stay in sync with FlipPix.Core's SettingsService.</summary>
        public const string AppFolderName = "FlipPix";

        /// <summary>Settings, saved prompts and scene libraries. ~/.config/FlipPix</summary>
        public static string ConfigDir => Ensure(Path.Combine(BaseConfigDir, AppFolderName));

        /// <summary>Persisted queues, which are state rather than user-editable config.</summary>
        public static string QueueDir => Ensure(Path.Combine(ConfigDir, "queue"));

        /// <summary>Log files. ~/.local/state/FlipPix/logs</summary>
        public static string LogDir => Ensure(Path.Combine(BaseStateDir, AppFolderName, "logs"));

        /// <summary>Disposable working files. ~/.cache/FlipPix</summary>
        public static string CacheDir => Ensure(Path.Combine(BaseCacheDir, AppFolderName));

        /// <summary>Where generated stills are mirrored for the user.</summary>
        public static string PicturesDir =>
            Path.Combine(UserDir(Environment.SpecialFolder.MyPictures, "Pictures"), "flippix-images");

        /// <summary>Where generated clips are mirrored for the user.</summary>
        public static string VideosDir =>
            Path.Combine(UserDir(Environment.SpecialFolder.MyVideos, "Videos"), "flippix-vids");

        /// <summary>Full path to a file inside the queue directory.</summary>
        public static string Queue(string fileName) => Path.Combine(QueueDir, fileName);

        private static string BaseConfigDir =>
            FromXdg("XDG_CONFIG_HOME", ".config", Environment.SpecialFolder.ApplicationData);

        private static string BaseStateDir =>
            FromXdg("XDG_STATE_HOME", Path.Combine(".local", "state"), Environment.SpecialFolder.ApplicationData);

        private static string BaseCacheDir =>
            FromXdg("XDG_CACHE_HOME", ".cache", Environment.SpecialFolder.LocalApplicationData);

        /// <summary>
        /// Honours the XDG variable when set and absolute, else the spec's default under $HOME.
        /// On Windows there is no XDG, so the matching known folder is used instead.
        /// </summary>
        private static string FromXdg(string variable, string defaultRelative, Environment.SpecialFolder windowsFallback)
        {
            if (OperatingSystem.IsWindows())
                return Environment.GetFolderPath(windowsFallback);

            var configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
                return configured;

            var home = Home();
            return Path.Combine(home, defaultRelative);
        }

        /// <summary>
        /// SpecialFolder.MyPictures resolves via xdg-user-dirs, which a minimal Arch install
        /// may not have. Fall back to the conventional $HOME subfolder rather than to $HOME
        /// itself, which is what .NET returns when the lookup fails.
        /// </summary>
        private static string UserDir(Environment.SpecialFolder folder, string defaultName)
        {
            var resolved = Environment.GetFolderPath(folder);
            var home = Home();

            if (string.IsNullOrWhiteSpace(resolved) ||
                string.Equals(Path.TrimEndingDirectorySeparator(resolved),
                              Path.TrimEndingDirectorySeparator(home),
                              StringComparison.Ordinal))
            {
                return Path.Combine(home, defaultName);
            }

            return resolved;
        }

        private static string Home()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                home = Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
            return home;
        }

        private static string Ensure(string path)
        {
            try { Directory.CreateDirectory(path); }
            catch (Exception) { /* surfaced by the caller's own file operation */ }
            return path;
        }
    }
}
