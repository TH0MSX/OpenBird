using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBird.Core;

/// <summary>
/// User settings. The JSON file and its snake_case keys are shared with the
/// Python app (%LOCALAPPDATA%\OpenBird\config.json), so both read the same data.
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("user_name")] public string UserName { get; set; } = "";
    [JsonPropertyName("db_path")] public string DbPath { get; set; } = "";
    [JsonPropertyName("storage_dir")] public string StorageDir { get; set; } = "";
    [JsonPropertyName("recordings_dir")] public string RecordingsDir { get; set; } = "";
    [JsonPropertyName("screenshots_dir")] public string ScreenshotsDir { get; set; } = "";
    [JsonPropertyName("whisper_model_dir")] public string WhisperModelDir { get; set; } = "";
    [JsonPropertyName("whisper_threads")] public int WhisperThreads { get; set; } = 4;
    [JsonPropertyName("sample_rate")] public int SampleRate { get; set; } = 16000;
    [JsonPropertyName("screen_ocr_enabled")] public bool ScreenOcrEnabled { get; set; } = true;
    [JsonPropertyName("screen_ocr_interval_seconds")] public int ScreenOcrIntervalSeconds { get; set; } = 20;
    [JsonPropertyName("screenshot_retention_days")] public int ScreenshotRetentionDays { get; set; } = 7;
    /// <summary>Master switch for the continuous screen memory. Off: the screen is never captured.</summary>
    [JsonPropertyName("screen_memory_enabled")] public bool ScreenMemoryEnabled { get; set; } = true;
    /// <summary>Set once the first-run setup has been completed in the native app.</summary>
    [JsonPropertyName("onboarding_done")] public bool OnboardingDone { get; set; }
    [JsonPropertyName("capture_all_monitors")] public bool CaptureAllMonitors { get; set; } = true;
    [JsonPropertyName("capture_exclusions")] public List<string> CaptureExclusions { get; set; } = new(Privacy.DefaultExclusions);
    [JsonPropertyName("auto_transcribe")] public bool AutoTranscribe { get; set; } = true;
    /// <summary>True: any sustained sound starts a meeting. False: only speech on the microphone does.</summary>
    [JsonPropertyName("auto_start_on_system_audio")] public bool AutoStartOnSystemAudio { get; set; } = true;
    /// <summary>Windows notifications for recording, notes, suggestions and the digest.</summary>
    [JsonPropertyName("notifications")] public bool NotificationsEnabled { get; set; } = true;
    /// <summary>Shows "Tasks and digest" in the sidebar. The daily digest itself is controlled by <see cref="DigestTime"/>.</summary>
    [JsonPropertyName("show_tasks_tab")] public bool ShowTasksTab { get; set; } = true;
    /// <summary>Time of day ("HH:mm") at which the daily digest is written automatically. Empty: off.</summary>
    [JsonPropertyName("digest_time")] public string DigestTime { get; set; } = "";
    [JsonPropertyName("auto_transcribe_threshold")] public double AutoTranscribeThreshold { get; set; } = 0.015;
    [JsonPropertyName("min_speech_seconds")] public double MinSpeechSeconds { get; set; } = 2.0;
    [JsonPropertyName("silence_timeout_seconds")] public double SilenceTimeoutSeconds { get; set; } = 25.0;
    [JsonPropertyName("default_template")] public string DefaultTemplate { get; set; } = "executive";
    [JsonPropertyName("hotkey")] public string Hotkey { get; set; } = "win+alt+r";
    /// <summary>Local only by default: nothing leaves the PC unless this is true AND a key is set.</summary>
    [JsonPropertyName("cloud_ai_enabled")] public bool CloudAiEnabled { get; set; }
    /// <summary>
    /// Which model writes summaries, answers and suggestions: "off" (rules only, nothing
    /// generated), "local" (a model server on this PC) or "cloud". Empty means "decide from
    /// cloud_ai_enabled", which keeps settings files from earlier versions working.
    /// </summary>
    [JsonPropertyName("ai_mode")] public string AiMode { get; set; } = "";
    /// <summary>Address of a model server on this PC that speaks the OpenAI chat API (Ollama, LM Studio, llama.cpp).</summary>
    [JsonPropertyName("local_ai_url")] public string LocalAiUrl { get; set; } = "http://localhost:11434";
    /// <summary>Model name on the local server. Empty: use the first model the server lists.</summary>
    [JsonPropertyName("local_ai_model")] public string LocalAiModel { get; set; } = "";
    [JsonPropertyName("gemini_api_key")] public string GeminiApiKey { get; set; } = "";
    [JsonPropertyName("groq_api_key")] public string GroqApiKey { get; set; } = "";
    [JsonPropertyName("gemini_model")] public string GeminiModel { get; set; } = "gemini-2.5-flash";
    [JsonPropertyName("groq_model")] public string GroqModel { get; set; } = "llama-3.3-70b-versatile";

    /// <summary>Unknown keys written by another version are preserved on save.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Keys that may be used for a cloud call right now (empty when local only).</summary>
    [JsonIgnore] public string EffectiveAiMode => AiMode is "off" or "local" or "cloud" ? AiMode : CloudAiEnabled ? "cloud" : "off";
    [JsonIgnore] public string EffectiveGeminiKey => EffectiveAiMode == "cloud" ? GeminiApiKey : "";
    [JsonIgnore] public string EffectiveGroqKey => EffectiveAiMode == "cloud" ? GroqApiKey : "";

    /// <summary>Sets the mode and keeps the older cloud_ai_enabled flag in step with it.</summary>
    public void SetAiMode(string mode)
    {
        AiMode = mode is "local" or "cloud" ? mode : "off";
        CloudAiEnabled = AiMode == "cloud";
    }
}

public sealed class SettingsStore
{
    public const string AppName = "OpenBird";
    public const string WhisperModelName = "sherpa-onnx-whisper-base.en";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string AppDataDir { get; }
    public string DocumentsDir { get; }
    public string ConfigPath => Path.Combine(AppDataDir, "config.json");
    public string LogDir => Path.Combine(AppDataDir, "logs");

    public SettingsStore(string? appDataDir = null, string? documentsDir = null)
    {
        AppDataDir = appDataDir
            ?? Environment.GetEnvironmentVariable("OPENBIRD_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        DocumentsDir = documentsDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public AppSettings Defaults()
    {
        var data = Path.Combine(AppDataDir, "data");
        return new AppSettings
        {
            DbPath = Path.Combine(data, "openbird.db"),
            StorageDir = Path.Combine(DocumentsDir, AppName),
            RecordingsDir = Path.Combine(data, "recordings"),
            ScreenshotsDir = Path.Combine(data, "screenshots"),
            WhisperModelDir = Path.Combine(AppDataDir, "models", WhisperModelName),
        };
    }

    /// <summary>Loads settings; creates the file with defaults on first run. Empty paths fall back to defaults.</summary>
    public AppSettings Load()
    {
        var defaults = Defaults();
        AppSettings? loaded = null;
        if (File.Exists(ConfigPath))
        {
            try
            {
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath), JsonOptions);
            }
            catch (JsonException)
            {
                // Keep the unreadable file for inspection and continue with defaults.
                File.Copy(ConfigPath, ConfigPath + ".unreadable", overwrite: true);
            }
        }

        var s = loaded ?? defaults;
        if (string.IsNullOrWhiteSpace(s.DbPath)) s.DbPath = defaults.DbPath;
        if (string.IsNullOrWhiteSpace(s.StorageDir)) s.StorageDir = defaults.StorageDir;
        if (string.IsNullOrWhiteSpace(s.RecordingsDir)) s.RecordingsDir = defaults.RecordingsDir;
        if (string.IsNullOrWhiteSpace(s.ScreenshotsDir)) s.ScreenshotsDir = defaults.ScreenshotsDir;
        if (string.IsNullOrWhiteSpace(s.WhisperModelDir)) s.WhisperModelDir = defaults.WhisperModelDir;
        if (string.IsNullOrWhiteSpace(s.Hotkey)) s.Hotkey = defaults.Hotkey;
        if (string.IsNullOrWhiteSpace(s.DefaultTemplate)) s.DefaultTemplate = defaults.DefaultTemplate;
        s.CaptureExclusions ??= new List<string>(Privacy.DefaultExclusions);
        if (string.IsNullOrWhiteSpace(s.UserName)) s.UserName = DetectUserName();

        if (loaded is null && !File.Exists(ConfigPath)) Save(s);
        return s;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataDir);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    private static string DetectUserName()
    {
        var name = Environment.UserName;
        return string.IsNullOrEmpty(name) ? "Me" : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
