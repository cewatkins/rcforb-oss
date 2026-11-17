namespace RCForb.Gui;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public int BufferMs { get; set; } = 300;
    public bool WaveformVisible { get; set; } = true;
    public bool NeedleVisible { get; set; } = true;
    public bool ListenOnly { get; set; } = false;
    public bool ShowChatTimestamps { get; set; } = true;
    public string RecordPath { get; set; } = "";
    public string ManualHost { get; set; } = "";
    public string ManualControlPort { get; set; } = "4525";
    public string ManualAudioPort { get; set; } = "4524";
    public string? SelectedAudioDevice { get; set; }
    public int VolumePercent { get; set; } = 100;
}
