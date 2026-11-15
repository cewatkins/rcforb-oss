namespace RCForb.Gui;

public sealed class ChatLineViewModel(string sender, string text, string timestamp) : ObservableObject
{
    public string Sender { get; } = sender;
    public string Text { get; } = text;
    public string Timestamp { get; } = timestamp;
    public string Display => $"{Sender}: {Text}";
}
