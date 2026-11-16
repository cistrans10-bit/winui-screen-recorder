using System.Text.Json;

namespace ScreenRecorder;

internal sealed class AppSettings
{
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Recordings");
    public int FrameRate { get; set; } = 60;
    public int QualityIndex { get; set; } = 1;
    public int ResolutionIndex { get; set; } = 0;
    public bool CaptureCursor { get; set; } = true;
    public bool HideFromCapture { get; set; } = true;
    public bool RecordAudio { get; set; } = true;
    public double BoostPercent { get; set; } = 100;
    public bool Limiter { get; set; } = true;
    public string Theme { get; set; } = "Dark";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenRecorder", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
