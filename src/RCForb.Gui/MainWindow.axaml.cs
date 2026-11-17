using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RCForb.Gui;

public partial class MainWindow : Window
{
    private bool _closing;

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        RemoteListBox.DoubleTapped += async (_, _) => await ViewModel.ConnectRemoteAsync();

        ViewModel.ChatLines.CollectionChanged += OnChatLinesChanged;
        ViewModel.Notices.CollectionChanged += OnNoticesChanged;

        // Chat input Enter key handling
        if (ChatInputBox is not null)
        {
            ChatInputBox.KeyDown += OnChatInputKeyDown;
        }
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.ConnectManualAsync();

    private void OnDisconnectClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.DisconnectAsync();

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.RefreshDirectoryAsync();

    private void OnJoinClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.ConnectRemoteAsync();

    private void OnThemeClick(object? sender, RoutedEventArgs e)
        => ViewModel.ToggleTheme();

    private void OnClassicClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClassicMode = !ViewModel.ClassicMode;
        ApplyClassicMode(ViewModel.ClassicMode);
    }

    private void ApplyClassicMode(bool on)
    {
        // Walk the visual tree and toggle the "classic" CSS class on the containers
        // that have .classic styles in App.axaml (chassis panels, radio buttons,
        // lamps, meters, text boxes, progress bars, list boxes).
        var root = this.Content as Avalonia.Controls.Control ?? this;
        WalkAndToggle(root, on);
    }

    private static void WalkAndToggle(Avalonia.Controls.Control? control, bool on)
    {
        if (control is null) return;

        var typeName = control.GetType().Name;
        var isChassis = control is Avalonia.Controls.Border b
                        && b.Classes.Contains("chassis");
        var isRadioButton = control is Avalonia.Controls.Button btn
                            && btn.Classes.Contains("radio");
        var isLamp = control is Avalonia.Controls.Border lb
                     && lb.Classes.Contains("lamp");
        var isMeterBezel = control is Avalonia.Controls.Border mb
                           && mb.Classes.Contains("meter-bezel");
        var isTextInput = control is Avalonia.Controls.TextBox
                          || control is Avalonia.Controls.ComboBox
                          || control is Avalonia.Controls.NumericUpDown;
        var isProgress = control is Avalonia.Controls.ProgressBar;
        var isList = control is Avalonia.Controls.ListBox;

        if (isChassis || isRadioButton || isLamp || isMeterBezel || isTextInput
            || isProgress || isList)
        {
            if (on)
                control.Classes.Add("classic");
            else
                control.Classes.Remove("classic");
        }

        // Recurse into children (Avalonia controls expose logical children).
        if (control is Avalonia.Controls.Panel panel)
        {
            foreach (var child in panel.Children)
                if (child is Avalonia.Controls.Control c) WalkAndToggle(c, on);
        }
        else if (control is Avalonia.Controls.Decorator dec && dec.Child is Avalonia.Controls.Control cc)
        {
            WalkAndToggle(cc, on);
        }
        else if (control is Avalonia.Controls.ContentControl cc2 && cc2.Content is Avalonia.Controls.Control c3)
        {
            WalkAndToggle(c3, on);
        }
    }

    private void OnRecordToggleClick(object? sender, RoutedEventArgs e)
        => ViewModel.RecordEnabled = !ViewModel.RecordEnabled;

    private void OnTxClick(object? sender, RoutedEventArgs e)
    {
        // Transmit requires an FCC license and remote-owner authorization; this receive-only
        // build has no TX path. The button is present (per the RCForb manual) but does nothing.
    }

    private void OnReLoginClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.ReLoginAsync();

    private void OnRevertGuestClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.RevertToGuestAsync();

    private void OnSetFrequencyAClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.SetFrequencyAAsync();

    private void OnSetFrequencyBClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.SetFrequencyBAsync();

    private void OnSetBandClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.SetBandAsync();

    private void OnSetModeClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.SetModeAsync();

    private void OnSetFilterClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.SetFilterAsync();

    private void OnSendChatClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.SendChatAsync();

    private void OnRequestMembershipClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.RequestMembershipAsync();

    private void OnChatLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScrollToLast(ChatListBox);

    private void OnNoticesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScrollToLast(NoticesListBox);

    private void OnChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = ViewModel.SendChatAsync();
        }
    }

    private static void ScrollToLast(ListBox listBox)
    {
        if (listBox.Items.Count > 0 && listBox.Items[^1] is { } last)
        {
            listBox.ScrollIntoView(last);
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_closing)
        {
            base.OnClosing(e);
            return;
        }

        _closing = true;
        e.Cancel = true;
        await ViewModel.ShutdownAsync();
        Close();
    }
}
