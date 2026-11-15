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

        ThemeButton.Click += (_, _) => ViewModel.ToggleTheme();
        ConnectButton.Click += async (_, _) => await ViewModel.ConnectManualAsync();
        DisconnectButton.Click += async (_, _) => await ViewModel.DisconnectAsync();
        RefreshButton.Click += async (_, _) => await ViewModel.RefreshDirectoryAsync();
        JoinButton.Click += async (_, _) => await ViewModel.ConnectRemoteAsync();
        RemoteListBox.DoubleTapped += async (_, _) => await ViewModel.ConnectRemoteAsync();

        // Channel control and live session options.
        SetFrequencyAButton.Click += async (_, _) => await ViewModel.SetFrequencyAAsync();
        SetFrequencyBButton.Click += async (_, _) => await ViewModel.SetFrequencyBAsync();
        SetBandButton.Click += async (_, _) => await ViewModel.SetBandAsync();
        SetModeButton.Click += async (_, _) => await ViewModel.SetModeAsync();
        SetFilterButton.Click += async (_, _) => await ViewModel.SetFilterAsync();
        ReLoginButton.Click += async (_, _) => await ViewModel.ReLoginAsync();
        RevertGuestButton.Click += async (_, _) => await ViewModel.RevertToGuestAsync();

        ViewModel.ChatLines.CollectionChanged += OnChatLinesChanged;
        ViewModel.Notices.CollectionChanged += OnNoticesChanged;

        // Chat input Enter key handling
        if (ChatInputBox is not null)
        {
            ChatInputBox.KeyDown += OnChatInputKeyDown;
        }
    }

    private void OnChatLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScrollToLast(ChatListBox);

    private void OnNoticesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScrollToLast(NoticesListBox);

    private void OnSendChatClick(object? sender, RoutedEventArgs e)
        => _ = ViewModel.SendChatAsync();

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
