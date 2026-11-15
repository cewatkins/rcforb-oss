using System.Text.Json;
using RCForb.Gui;

namespace RCForb.Gui;

public sealed class SettingsService
{
    public SettingsService() : this(DefaultPath) { }

    public SettingsService(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rcforb-gui", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!System.IO.File.Exists(Path))
            {
                return new AppSettings();
            }

            var json = System.IO.File.ReadAllText(Path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var settings = JsonSerializer.Deserialize<AppSettings>(json, options);
            if (settings is null)
            {
                return new AppSettings();
            }

            // Defensive defaults: merge with defaults so missing/out-of-range values fall back.
            var merged = new AppSettings
            {
                Theme = settings.Theme ?? "Dark",
                BufferMs = settings.BufferMs,
                WaveformVisible = settings.WaveformVisible,
                NeedleVisible = settings.NeedleVisible,
                RecordPath = settings.RecordPath ?? "",
                ManualHost = settings.ManualHost ?? "",
                ManualControlPort = settings.ManualControlPort ?? "4525",
                ManualAudioPort = settings.ManualAudioPort ?? "4524",
                SelectedAudioDevice = settings.SelectedAudioDevice,
            };

            return merged;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);

            var tempPath = Path + ".tmp";
            System.IO.File.WriteAllText(tempPath, json);
            System.IO.File.Move(tempPath, Path);
        }
        catch
        {
            // Swallow any IO/JSON failure so loading a bad settings file never crashes the app.
        }
    }
}
