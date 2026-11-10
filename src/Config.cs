namespace GpuMonitor
{
    // Central place for every threshold and timing value, so that tuning the
    // monitor does not require searching through the other files.
    static class Config
    {
        // Telemetry sampling.
        public const int SampleIntervalMs = 1000;
        public const int RingSeconds = 150;

        // Incident windows, in seconds relative to the moment of the incident.
        public const int PreSeconds = 60;
        public const int PostSeconds = 10;
        public const int FinalizeDelaySeconds = 15;
        public const int MergeSeconds = 5;
        public const int EventLookbackSeconds = 30;

        // Freeze detector. The heartbeat thread sleeps HeartbeatMs and treats
        // any extra delay above FreezeThresholdMs as a system-wide stall.
        public const int HeartbeatMs = 50;
        public const int FreezeThresholdMs = 250;
        public const int MaxPlausibleFreezeMs = 60000;

        // Logging.
        public const int CsvFlushSeconds = 2;
        public const int LogRetentionDays = 7;
        public const int ReportDays = 30;
        public const int MaxIncidentsInReport = 60;

        // Processes that are worth listing in an incident because they are
        // known to affect the GPU, overlays, audio latency or video playback.
        public static readonly string[] InterestingProcesses = new string[]
        {
            "obs", "obs64", "Streamlabs", "Discord", "Spotify", "chrome", "msedge", "firefox",
            "opera", "brave", "vivaldi", "mercury", "floorp", "librewolf", "waterfox", "zen", "PrimeVideo", "Netflix", "vlc", "mpc-hc", "Twitch",
            "MSIAfterburner", "RTSS", "EncoderServer", "Armoury", "ArmouryCrate", "ASUS",
            "voicemeeter", "VoicemeeterClient", "audiodg", "wallpaper32", "wallpaper64",
            "lively", "iCUE", "SignalRgb", "RGBFusion", "OpenRGB", "NVIDIA Overlay", "NVIDIA Share",
            "nvcontainer", "Steam", "steamwebhelper", "EpicGamesLauncher", "Elgato", "StreamDeck",
            "4KCaptureUtility", "GameCapture", "DroidCam", "Iriun", "HWiNFO64", "CapFrameX"
        };

        // Apps (by process name) and window title fragments that usually mean
        // DRM protected video is playing, which is relevant to HDCP flicker.
        public static readonly string[] DrmProcesses = new string[]
        {
            "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "mercury", "floorp", "librewolf",
            "waterfox", "zen", "Spotify", "PrimeVideo", "Netflix"
        };

        public static readonly string[] DrmTitles = new string[]
        {
            "Prime Video", "Amazon", "Netflix", "Disney", "Hulu", "Max", "Crunchyroll", "Apple TV",
            "Spotify", "YouTube", "Twitch", "Peacock", "Paramount"
        };
    }
}
