namespace FlipPix.Core;

/// <summary>
/// Centralized constants used throughout the FlipPix application.
/// Eliminates magic numbers and provides a single source of truth for configuration values.
/// </summary>
public static class Constants
{
    /// <summary>WebSocket and network communication constants.</summary>
    public static class Network
    {
        /// <summary>Default WebSocket receive buffer size in bytes.</summary>
        public const int WebSocketBufferSize = 4096;

        /// <summary>Maximum number of WebSocket reconnection attempts.</summary>
        public const int MaxReconnectAttempts = 10;

        /// <summary>Base delay in milliseconds between reconnection attempts.</summary>
        public const int ReconnectBaseDelayMs = 2000;

        /// <summary>Maximum delay in milliseconds between reconnection attempts (caps exponential backoff).</summary>
        public const int MaxReconnectDelayMs = 30000;

        /// <summary>Default HTTP connection timeout in milliseconds.</summary>
        public const int DefaultConnectionTimeoutMs = 10000;

        /// <summary>Default upload timeout in milliseconds (10 minutes for large files).</summary>
        public const int DefaultUploadTimeoutMs = 600000;

        /// <summary>Cache TTL for object info responses in seconds.</summary>
        public const int ObjectInfoCacheTtlSeconds = 60;
    }

    /// <summary>Workflow execution constants.</summary>
    public static class Workflow
    {
        /// <summary>Default workflow execution timeout in minutes.</summary>
        public const int DefaultExecutionTimeoutMinutes = 30;

        /// <summary>Wait budget in minutes for server restart before giving up on lost jobs.</summary>
        public const int ServerRestartWaitMinutes = 5;

        /// <summary>Polling interval in seconds for history checks.</summary>
        public const int HistoryPollIntervalSeconds = 5;

        /// <summary>Server readiness check interval in seconds.</summary>
        public const int ReadinessCheckIntervalSeconds = 10;

        /// <summary>Grace period in seconds before considering a prompt lost.</summary>
        public const int PromptLostGracePeriodSeconds = 30;

        /// <summary>Number of consecutive misses before declaring a prompt lost.</summary>
        public const int PromptLostThreshold = 3;

        /// <summary>Default retry count for workflow operations.</summary>
        public const int DefaultMaxRetries = 3;

        /// <summary>Default retry delay in milliseconds.</summary>
        public const int DefaultRetryDelayMs = 2000;
    }

    /// <summary>ComfyUI process management constants.</summary>
    public static class Process
    {
        /// <summary>Default delay in seconds before attempting to restart ComfyUI.</summary>
        public const int RestartDelaySeconds = 10;

        /// <summary>Maximum time in seconds to wait for ComfyUI to start (for large model loading).</summary>
        public const int StartupTimeoutSeconds = 300;
    }

    /// <summary>Prompt and storage constants.</summary>
    public static class Storage
    {
        /// <summary>Maximum number of prompts to keep in history.</summary>
        public const int MaxPromptHistoryCount = 50;

        /// <summary>Folder probe cache TTL in seconds.</summary>
        public const int FolderProbeCacheTtlSeconds = 30;

        /// <summary>Global browse folder key in settings.</summary>
        public const string GlobalBrowseFolderKey = "__global__";
    }

    /// <summary>UI constants.</summary>
    public static class UI
    {
        /// <summary>Default animation duration in milliseconds.</summary>
        public const int DefaultAnimationDurationMs = 200;

        /// <summary>Tooltip show delay in milliseconds.</summary>
        public const int TooltipDelayMs = 500;

        /// <summary>Debounce delay for search/filter operations in milliseconds.</summary>
        public const int SearchDebounceMs = 300;
    }

    /// <summary>Video processing constants.</summary>
    public static class Video
    {
        /// <summary>Default frame rate for generated videos.</summary>
        public const int DefaultFrameRate = 24;

        /// <summary>Default CRF (Constant Rate Factor) for video encoding.</summary>
        public const int DefaultCrf = 19;

        /// <summary>Default video batch size for SCAIL processing (frames per pass).</summary>
        public const int DefaultVideoBatchSize = 40;

        /// <summary>Default LTX frame count.</summary>
        public const int DefaultLtxFrameCount = 240;
    }

    /// <summary>Image processing constants.</summary>
    public static class Image
    {
        /// <summary>Maximum image dimension for uploads (prevents memory issues).</summary>
        public const int MaxUploadDimension = 8192;

        /// <summary>Default JPEG quality for compressed outputs.</summary>
        public const int DefaultJpegQuality = 85;

        /// <summary>Standard stable resolutions for AI image generation.</summary>
        public static readonly (int Width, int Height)[] StableResolutions = new[]
        {
            (512, 512),
            (768, 512),
            (512, 768),
            (1024, 576),
            (576, 1024),
            (1024, 768),
            (768, 1024),
            (1024, 1024),
            (1280, 720),
            (720, 1280),
            (1920, 1080),
            (1080, 1920)
        };
    }

    /// <summary>File path and naming constants.</summary>
    public static class Paths
    {
        /// <summary>Application data folder name.</summary>
        public const string AppDataFolderName = "FlipPix";

        /// <summary>Settings file name.</summary>
        public const string SettingsFileName = "settings.json";

        /// <summary>Log folder name.</summary>
        public const string LogsFolderName = "logs";

        /// <summary>Default output folder name.</summary>
        public const string OutputFolderName = "output";

        /// <summary>Default input folder name.</summary>
        public const string InputFolderName = "input";
    }
}
