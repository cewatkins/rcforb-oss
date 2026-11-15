namespace RCForb.Gui;

public sealed class NoticeViewModel(string kind, string text, string timestamp) : ObservableObject
{
    public string Kind { get; } = kind;
    public string Text { get; } = text;
    public string Timestamp { get; } = timestamp;
    public string Display => $"[{Kind}] {Text}";
}
