namespace RCForb.Gui;

public sealed class StateItemViewModel(string key, string value) : ObservableObject
{
    public string Key { get; } = key;
    public string Value { get; } = value;
}
