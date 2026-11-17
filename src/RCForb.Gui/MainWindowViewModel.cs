using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using RCForb.Audio;
using RCForb.Client.Audio;
using RCForb.Client.Directory;
using RCForb.Client.Session;
using RCForb.Client.State;

namespace RCForb.Gui;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly DispatcherTimer _uiTimer;
    private readonly ObservableCollection<RemoteRecordViewModel> _remotes = new();
    private readonly ObservableCollection<ChatLineViewModel> _chatLines = new();
    private readonly ObservableCollection<string> _connectedUsers = new();
    private readonly ObservableCollection<NoticeViewModel> _notices = new();
    private readonly ObservableCollection<StateItemViewModel> _unknownKeys = new();
    private readonly ObservableCollection<string> _audioDevices = new();
    private readonly ObservableCollection<string> _modeOptions = new();
    private readonly ObservableCollection<string> _filterOptions = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private GuestSession? _session;
    private AuthenticatedSession? _authSession;
    private bool _radioClosed;
    private bool _radioPoweredOff;
    private SoundFlowAudioSink? _player;
    private WavFileWriter? _recorder;
    private IGuestAudioSink? _sink;
    private DateTime? _connectedAt;
    private int _lastLinesApplied;
    private DateTime? _recordingStartedAt;
    private string _authUser = "";
    private string _authPassword = "";
    private string _authStateText = "";
    private string _authMessageText = "";
    private bool _useAuth = false;
    private string _chatInput = "";

    private readonly SettingsService _settingsService;
    private AppSettings _settings = new();
    private WaveformTapSink? _waveformSink;
    private short[]? _waveformSamples;
    private double _needleValue;
    private double _needleSmoothed = -40.0;
    private string _needleValueText = "";
    private bool _waveformVisible = true;
    private bool _needleVisible = true;
    private bool _isDarkTheme = true;

    private GuestTotals _totals = new();
    private int _autoDisconnectSeconds;

    private string _status = "idle";
    private string _targetName = "";
    private TimeSpan _elapsed;
    private bool _isConnected;
    private bool _reconnectEnabled;
    private bool _recordEnabled;
    private string _recordPath = "";
    private bool _playbackEnabled = true;
    private int _bufferMs = 300;
    private string? _selectedAudioDevice;
    private string _manualHost = "";
    private string _manualControlPort = "4525";
    private string _manualAudioPort = "4524";
    private RemoteRecordViewModel? _selectedRemote;
    private string _directoryFilter = "";

    private string _frequencyDisplay = "—";
    private string _frequencyBDisplay = "";
    private bool _hasFrequencyB;
    private string _modeDisplay = "";
    private string _band = "";
    private string _protocolVersion = "";
    private string _orbId = "";
    private decimal _frequencyAValue = 14_250_000m;
    private decimal _frequencyBValue = 7_185_000m;
    private string _bandText = "";
    private string _modeText = "";
    private string _filterText = "";
    private string _restrictionBanner = "";
    private string _radioDisplay = "";
    private string _sMeterText = "";
    private double _sMeterPercent;
    private double _sMeterValue;
    private string _sMeterReadout = "";
    private bool _stateReady;

    private double _audioLevelPercent;
    private double _playoutDepthMs;
    private long _underruns;
    private long _audioPackets;
    private double _decodedSeconds;
    private long _decodeErrors;
    private long _controlLines;
    private string _audioError = "";

    public enum TuningState { Off, Locked, Ok }

    private TuningState _tuningState;
    private string _tuningStatusText = "";
    private IBrush? _tuningStatusBrush;

    /// <summary>Assembly version, shown in the window title so the user can tell
    /// which build they are running.</summary>
    private static readonly Lazy<string> _version = new(() =>
    {
        var name = typeof(MainWindowViewModel).Assembly.GetName();
        return name.Version is { } v ? v.ToString() : "0.0.0";
    });

    public string Version => _version.Value;

    public string WindowTitle => $"rcforb-gui — listen only v{Version}";

    public string TuningStatusText
    {
        get => _tuningStatusText;
        private set => SetProperty(ref _tuningStatusText, value);
    }

    public IBrush? TuningStatusBrush
    {
        get => _tuningStatusBrush;
        private set => SetProperty(ref _tuningStatusBrush, value);
    }

    /// <summary>True when the server reports the radio as powered off (the PWROFF sentinel).</summary>
    public bool RadioPoweredOff
    {
        get => _radioPoweredOff;
        private set => SetProperty(ref _radioPoweredOff, value);
    }

    /// <summary>Human-readable power state for a UI badge.</summary>
    public string PowerStateText => RadioPoweredOff ? "POWER OFF" : "POWER ON";

    /// <summary>Badge colour: red when off, green when on.</summary>
    public IBrush? PowerStateBrush => RadioPoweredOff
        ? ThemeBrush("ErrorBrush")
        : ThemeBrush("SuccessBrush");

    public MainWindowViewModel()
    {
        _settingsService = new SettingsService();
        _settings = _settingsService.Load();

        _bufferMs = _settings.BufferMs;
        _recordPath = _settings.RecordPath;
        _manualHost = _settings.ManualHost;
        _manualControlPort = _settings.ManualControlPort;
        _manualAudioPort = _settings.ManualAudioPort;
        _waveformVisible = _settings.WaveformVisible;
        _needleVisible = _settings.NeedleVisible;
        _listenOnly = _settings.ListenOnly;
        _showChatTimestamps = _settings.ShowChatTimestamps;
        _isDarkTheme = !string.Equals(_settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, OnUiTimerTick);
        _uiTimer.Start();

        RefreshAudioDevices();
        if (!string.IsNullOrWhiteSpace(_settings.SelectedAudioDevice))
        {
            SelectedAudioDevice = _audioDevices.FirstOrDefault(d => d == _settings.SelectedAudioDevice) ?? SelectedAudioDevice;
        }

        _ = RefreshDirectoryAsync();
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string TargetName
    {
        get => _targetName;
        private set => SetProperty(ref _targetName, value);
    }

    public TimeSpan Elapsed
    {
        get => _elapsed;
        private set => SetProperty(ref _elapsed, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(IsNotConnected));
                OnPropertyChanged(nameof(CanConnectManual));
                OnPropertyChanged(nameof(CanConnectRemote));
            }
        }
    }

    public bool IsNotConnected => !IsConnected;

    public bool ReconnectEnabled
    {
        get => _reconnectEnabled;
        set => SetProperty(ref _reconnectEnabled, value);
    }

    public bool RecordEnabled
    {
        get => _recordEnabled;
        set => SetProperty(ref _recordEnabled, value);
    }

    public string RecordPath
    {
        get => _recordPath;
        set => SetProperty(ref _recordPath, value);
    }

    public bool PlaybackEnabled
    {
        get => _playbackEnabled;
        set => SetProperty(ref _playbackEnabled, value);
    }

    public int BufferMs
    {
        get => _bufferMs;
        set => SetProperty(ref _bufferMs, value);
    }

    public ObservableCollection<string> AudioDevices => _audioDevices;

    public string? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set => SetProperty(ref _selectedAudioDevice, value);
    }

    public string ManualHost
    {
        get => _manualHost;
        set
        {
            if (SetProperty(ref _manualHost, value))
            {
                OnPropertyChanged(nameof(CanConnectManual));
            }
        }
    }

    public string ManualControlPort
    {
        get => _manualControlPort;
        set => SetProperty(ref _manualControlPort, value);
    }

    public string ManualAudioPort
    {
        get => _manualAudioPort;
        set => SetProperty(ref _manualAudioPort, value);
    }

    public ObservableCollection<RemoteRecordViewModel> Remotes => _remotes;

    public RemoteRecordViewModel? SelectedRemote
    {
        get => _selectedRemote;
        set
        {
            if (SetProperty(ref _selectedRemote, value))
            {
                OnPropertyChanged(nameof(CanConnectRemote));
            }
        }
    }

    public string DirectoryFilter
    {
        get => _directoryFilter;
        set
        {
            if (SetProperty(ref _directoryFilter, value))
            {
                ApplyDirectoryFilter();
            }
        }
    }

    private List<RemoteRecordViewModel> _allRemotes = new();

    public ObservableCollection<ChatLineViewModel> ChatLines => _chatLines;
    public ObservableCollection<string> ConnectedUsers => _connectedUsers;
    public ObservableCollection<NoticeViewModel> Notices => _notices;
    public ObservableCollection<StateItemViewModel> UnknownKeys => _unknownKeys;

    public string FrequencyDisplay
    {
        get => _frequencyDisplay;
        private set => SetProperty(ref _frequencyDisplay, value);
    }

    public string FrequencyBDisplay
    {
        get => _frequencyBDisplay;
        private set => SetProperty(ref _frequencyBDisplay, value);
    }

    public decimal FrequencyAValue
    {
        get => _frequencyAValue;
        set => SetProperty(ref _frequencyAValue, value);
    }

    public decimal FrequencyBValue
    {
        get => _frequencyBValue;
        set => SetProperty(ref _frequencyBValue, value);
    }

    public bool HasFrequencyB
    {
        get => _hasFrequencyB;
        private set => SetProperty(ref _hasFrequencyB, value);
    }

    public string ModeDisplay
    {
        get => _modeDisplay;
        private set => SetProperty(ref _modeDisplay, value);
    }

    public string Band
    {
        get => _band;
        private set => SetProperty(ref _band, value);
    }

    /// <summary>Protocol version the server advertised (e.g. "rcs").</summary>
    public string ProtocolVersion
    {
        get => _protocolVersion;
        private set => SetProperty(ref _protocolVersion, value);
    }

    /// <summary>Orb ID the server advertised.</summary>
    public string OrbId
    {
        get => _orbId;
        private set => SetProperty(ref _orbId, value);
    }

    public string BandText
    {
        get => _bandText;
        set => SetProperty(ref _bandText, value);
    }

    public string ModeText
    {
        get => _modeText;
        set => SetProperty(ref _modeText, value);
    }

    public string FilterText
    {
        get => _filterText;
        set => SetProperty(ref _filterText, value);
    }

    public ObservableCollection<string> ModeOptions => _modeOptions;

    public ObservableCollection<string> FilterOptions => _filterOptions;

    public bool HasModeOptions => _modeOptions.Count > 0;

    public bool HasFilterOptions => _filterOptions.Count > 0;

    public string RadioDisplay
    {
        get => _radioDisplay;
        private set => SetProperty(ref _radioDisplay, value);
    }

    /// <summary>True when a WAV recording is actively in progress.</summary>
    public bool IsRecording => RecordEnabled && IsConnected && _recorder is not null;

    /// <summary>How long the current recording has been running.</summary>
    public TimeSpan RecordingDuration =>
        _recordingStartedAt is { } started ? DateTime.UtcNow - started : TimeSpan.Zero;

    /// <summary>On-screen recording status, e.g. "REC 00:01:23".</summary>
    public string RecordingStatus => IsRecording
        ? $"REC {RecordingDuration:hh\\:mm\\:ss}"
        : "";

    public string SMeterText
    {
        get => _sMeterText;
        private set => SetProperty(ref _sMeterText, value);
    }

    public double SMeterPercent
    {
        get => _sMeterPercent;
        private set => SetProperty(ref _sMeterPercent, value);
    }

    /// <summary>Normalised 0..1 signal level driving the analog S-meter needle.</summary>
    public double SMeterValue
    {
        get => _sMeterValue;
        private set => SetProperty(ref _sMeterValue, value);
    }

    /// <summary>Calibrated readout, e.g. "S9 + 11 dB", from the server's smeter label.</summary>
    public string SMeterReadout
    {
        get => _sMeterReadout;
        private set => SetProperty(ref _sMeterReadout, value);
    }

    public bool StateReady
    {
        get => _stateReady;
        private set => SetProperty(ref _stateReady, value);
    }

    public double AudioLevelPercent
    {
        get => _audioLevelPercent;
        private set => SetProperty(ref _audioLevelPercent, value);
    }

    public double PlayoutDepthMs
    {
        get => _playoutDepthMs;
        private set => SetProperty(ref _playoutDepthMs, value);
    }

    public long Underruns
    {
        get => _underruns;
        private set => SetProperty(ref _underruns, value);
    }

    public long AudioPackets
    {
        get => _audioPackets;
        private set => SetProperty(ref _audioPackets, value);
    }

    public double DecodedSeconds
    {
        get => _decodedSeconds;
        private set => SetProperty(ref _decodedSeconds, value);
    }

    public long DecodeErrors
    {
        get => _decodeErrors;
        private set => SetProperty(ref _decodeErrors, value);
    }

    public long ControlLines
    {
        get => _controlLines;
        private set => SetProperty(ref _controlLines, value);
    }

    public string AudioError
    {
        get => _audioError;
        private set => SetProperty(ref _audioError, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        private set
        {
            if (SetProperty(ref _isDarkTheme, value))
            {
                OnPropertyChanged(nameof(ThemeToggleLabel));
            }
        }
    }

    public string ThemeToggleLabel => IsDarkTheme ? "☀ Light" : "🌙 Dark";

    public bool WaveformVisible
    {
        get => _waveformVisible;
        set
        {
            if (SetProperty(ref _waveformVisible, value))
            {
                _settings.WaveformVisible = value;
                SaveSettings();
            }
        }
    }

    public bool NeedleVisible
    {
        get => _needleVisible;
        set
        {
            if (SetProperty(ref _needleVisible, value))
            {
                _settings.NeedleVisible = value;
                SaveSettings();
            }
        }
    }

    public short[]? WaveformSamples
    {
        get => _waveformSamples;
        private set => SetProperty(ref _waveformSamples, value);
    }

    public double NeedleValue
    {
        get => _needleValue;
        private set => SetProperty(ref _needleValue, value);
    }

    public string NeedleValueText
    {
        get => _needleValueText;
        private set => SetProperty(ref _needleValueText, value);
    }

    public bool CanConnectManual => IsNotConnected && !string.IsNullOrWhiteSpace(ManualHost);

    public bool CanConnectRemote => IsNotConnected && SelectedRemote is not null;

    public string AuthUser
    {
        get => _authUser;
        set => SetProperty(ref _authUser, value);
    }

    public string AuthPassword
    {
        get => _authPassword;
        set => SetProperty(ref _authPassword, value);
    }

    public bool UseAuth
    {
        get => _useAuth;
        set => SetProperty(ref _useAuth, value);
    }

    private bool _listenOnly;
    public bool ListenOnly
    {
        get => _listenOnly;
        set
        {
            if (SetProperty(ref _listenOnly, value))
            {
                _settings.ListenOnly = value;
                SaveSettings();
                OnPropertyChanged(nameof(ShowChannelControls));
                UpdateTuningState();
            }
        }
    }

    private bool _showChatTimestamps = true;
    public bool ShowChatTimestamps
    {
        get => _showChatTimestamps;
        set
        {
            if (SetProperty(ref _showChatTimestamps, value))
            {
                _settings.ShowChatTimestamps = value;
                SaveSettings();
            }
        }
    }

    public bool ShowChannelControls => !ListenOnly && _authSession is not null;

    public string ChatInput
    {
        get => _chatInput;
        set
        {
            if (SetProperty(ref _chatInput, value))
            {
                OnPropertyChanged(nameof(CanSendChat));
            }
        }
    }

    public bool CanSendChat => _authSession is not null && _authSession.AuthState == AuthState.Authorised && !string.IsNullOrWhiteSpace(_chatInput);

    public bool IsAuthenticated => _authSession is not null && _authSession.AuthState == AuthState.Authorised;

    public string AuthStateText
    {
        get => _authStateText;
        private set => SetProperty(ref _authStateText, value);
    }

    public string AuthMessageText
    {
        get => _authMessageText;
        private set => SetProperty(ref _authMessageText, value);
    }

    public string RestrictionBanner
    {
        get => _restrictionBanner;
        private set
        {
            if (SetProperty(ref _restrictionBanner, value))
            {
                OnPropertyChanged(nameof(HasRestriction));
            }
        }
    }

    public bool HasRestriction => !string.IsNullOrEmpty(RestrictionBanner);

    public bool CanControlChannels => _authSession is not null
        && _authSession.AuthState != AuthState.Refused
        && !_radioClosed
        && !_radioPoweredOff;

    public bool ShowChannelControlsHint => _authSession is null;

    public bool CanSetFrequencyB => HasFrequencyB && CanControlChannels;

    public bool CanReLogin => _authSession is not null;

    public bool CanRevertToGuest => _authSession is not null && _authSession.AuthState != AuthState.Pending;

    public void HandleStartupArgs(string[] args)
    {
        string? host = null;
        int? seconds = null;
        string? user = null;
        string? password = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--host" or "-host")
            {
                host = i + 1 < args.Length ? args[++i] : null;
            }
            else if (arg is "--seconds" or "-seconds")
            {
                if (i + 1 < args.Length && int.TryParse(args[++i], out var s) && s > 0)
                {
                    seconds = s;
                }
            }
            else if (arg is "--reconnect" or "-reconnect")
            {
                ReconnectEnabled = true;
            }
            else if (arg is "--record" or "-record")
            {
                RecordEnabled = true;
                RecordPath = i + 1 < args.Length ? args[++i] : "";
            }
            else if (arg is "--buffer" or "-buffer")
            {
                if (i + 1 < args.Length && int.TryParse(args[++i], out var b) && b is >= 100 and <= 1000)
                {
                    BufferMs = b;
                }
            }
            else if (arg is "--listen-only" or "-listen-only")
            {
                ListenOnly = true;
            }
            else if (arg is "--audio-device" or "-audio-device")
            {
                var name = i + 1 < args.Length ? args[++i] : null;
                if (name is not null)
                {
                    RefreshAudioDevices();
                    SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Contains(name, StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (arg is "--user" or "-user")
            {
                user = i + 1 < args.Length ? args[++i] : null;
            }
            else if (arg is "--password" or "-password")
            {
                password = i + 1 < args.Length ? args[++i] : null;
            }
        }

        if (seconds is > 0)
        {
            _autoDisconnectSeconds = seconds.Value;
        }

        if (!string.IsNullOrWhiteSpace(host))
        {
            try
            {
                var target = ParseHostArgument(host);
                if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password))
                {
                    AuthUser = user;
                    AuthPassword = password;
                    UseAuth = true;
                }
                _ = ConnectTargetAsync(target);
            }
            catch (Exception ex)
            {
                AddNotice("error", $"Invalid --host: {ex.Message}");
            }
        }
    }

    public void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        var theme = IsDarkTheme ? "Dark" : "Light";
        _settings.Theme = theme;
        ThemeService.Apply(theme);
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.BufferMs = BufferMs;
        _settings.RecordPath = RecordPath;
        _settings.ManualHost = ManualHost;
        _settings.ManualControlPort = ManualControlPort;
        _settings.ManualAudioPort = ManualAudioPort;
        _settings.SelectedAudioDevice = SelectedAudioDevice;
        _settings.WaveformVisible = WaveformVisible;
        _settings.NeedleVisible = NeedleVisible;
        _settings.ListenOnly = ListenOnly;
        _settings.ShowChatTimestamps = ShowChatTimestamps;
        _settings.Theme = IsDarkTheme ? "Dark" : "Light";
        _settingsService.Save(_settings);
    }

    private static GuestTarget ParseHostArgument(string argument)
    {
        var slash = argument.IndexOf('/');
        var endpoint = slash < 0 ? argument : argument[..slash];
        var audio = GuestTarget.DefaultAudioPort;
        if (slash >= 0 && !int.TryParse(argument[(slash + 1)..], out audio))
        {
            throw new ArgumentException($"'{argument}' has no audio port after '/'");
        }

        var colon = endpoint.LastIndexOf(':');
        var host = colon < 0 ? endpoint : endpoint[..colon];
        var control = GuestTarget.DefaultControlPort;
        if (colon >= 0 && !int.TryParse(endpoint[(colon + 1)..], out control))
        {
            throw new ArgumentException($"'{endpoint}' has no control port after ':'");
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("--host needs a host name or address");
        }

        return new GuestTarget(host, control, audio);
    }

    private static string ComputeMd5Hex(string plaintext)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(plaintext));
        var sb = new StringBuilder(32);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    public async Task RefreshDirectoryAsync()
    {
        try
        {
            var directory = await RemoteDirectory.FetchAsync();
            _allRemotes = directory.Entries
                .Select(r => new RemoteRecordViewModel(r))
                .ToList();
            ApplyDirectoryFilter();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            AddNotice("error", $"Could not read the remote list: {ex.Message}");
        }
    }

    private void ApplyDirectoryFilter()
    {
        var filter = DirectoryFilter.Trim();
        _remotes.Clear();
        foreach (var remote in _allRemotes)
        {
            if (string.IsNullOrEmpty(filter) ||
                remote.Record.ServerName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                remote.Record.RadioName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                remote.Record.Country.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                _remotes.Add(remote);
            }
        }
    }

    public void RefreshAudioDevices()
    {
        var selected = SelectedAudioDevice;
        _audioDevices.Clear();
        foreach (var device in SoundFlowAudioSink.ListDevices())
        {
            _audioDevices.Add(device);
        }

        SelectedAudioDevice = _audioDevices.Count > 0
            ? (selected is not null && _audioDevices.Contains(selected) ? selected : _audioDevices[0])
            : null;
    }

    public async Task ConnectRemoteAsync()
    {
        if (SelectedRemote is not { } remote)
        {
            return;
        }

        if (!remote.IsOnline)
        {
            AddNotice("error", $"{remote.Record.ServerName} is listed as offline.");
            return;
        }

        if (!remote.IsV07)
        {
            AddNotice("error", $"{remote.Record.ServerName} advertises no audio port; only the dual-TCP v0.7 path is supported.");
            return;
        }

        var target = new GuestTarget(remote.Record.Domain, remote.Record.ControlPort, remote.Record.AudioPort);
        await ConnectTargetAsync(target);
    }

    public async Task ConnectManualAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualHost))
        {
            return;
        }

        if (!int.TryParse(ManualControlPort, out var controlPort) || controlPort is <= 0 or > 65535)
        {
            AddNotice("error", "Control port must be 1..65535.");
            return;
        }

        if (!int.TryParse(ManualAudioPort, out var audioPort) || audioPort is <= 0 or > 65535)
        {
            AddNotice("error", "Audio port must be 1..65535.");
            return;
        }

        var target = new GuestTarget(ManualHost.Trim(), controlPort, audioPort);
        await ConnectTargetAsync(target);
    }

    public async Task ConnectTargetAsync(GuestTarget target)
    {
        if (IsConnected)
        {
            return;
        }

        await DisconnectAsync();

        if (!BuildSinks())
        {
            return;
        }

        ResetState();
        // Start every connection from a cleared auth posture: an authorised session that was
        // disconnected must not look authenticated, and a guest connection must not inherit a
        // stale "Authorised" pill from a previous authenticated session.
        SetProperty(ref _authStateText, "");
        SetProperty(ref _authMessageText, "");
        Status = "connecting";
        TargetName = UseAuth ? $"{AuthUser}@{target.Display}" : target.Display;
        IsConnected = true;
        UpdateTuningState();
        _connectedAt = DateTime.UtcNow;
        _totals = new GuestTotals();
        _cts = new CancellationTokenSource();

        if (_autoDisconnectSeconds > 0)
        {
            _ = AutoDisconnectAsync(_cts.Token);
        }

        if (UseAuth && !string.IsNullOrWhiteSpace(AuthUser) && !string.IsNullOrWhiteSpace(AuthPassword))
        {
            var authTarget = new AuthenticatedTarget(
                target.Host,
                target.ControlPort,
                target.AudioPort,
                AuthUser,
                ComputeMd5Hex(AuthPassword));
            _runTask = Task.Run(() => RunLoopAuthenticatedAsync(authTarget, _cts.Token), CancellationToken.None);
        }
        else
        {
            _runTask = Task.Run(() => RunLoopAsync(target, _cts.Token), CancellationToken.None);
        }
    }

    private async Task AutoDisconnectAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_autoDisconnectSeconds), token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(async () => await DisconnectAsync());
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnected && _runTask is null)
        {
            return;
        }

        _cts?.Cancel();

        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _runTask = null;
            }
        }

        _cts?.Dispose();
        _cts = null;
        _session = null;
        _authSession = null;
        // Clear the authenticated posture so the "Authorised" pill does not linger after
        // disconnect, and a subsequent guest connection does not appear authenticated.
        SetProperty(ref _authStateText, "");
        SetProperty(ref _authMessageText, "");
        IsConnected = false;
        Status = "idle";
        UpdateTuningState();
        _chatLines.Clear();
        _notices.Clear();
        _connectedUsers.Clear();
        Elapsed = TimeSpan.Zero;
        _connectedAt = null;
        DisposeSinks();
        OnPropertyChanged(nameof(CanConnectManual));
        OnPropertyChanged(nameof(CanConnectRemote));
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(AuthStateText));
        OnPropertyChanged(nameof(AuthMessageText));
        OnPropertyChanged(nameof(CanSendChat));
        OnPropertyChanged(nameof(CanControlChannels));
        OnPropertyChanged(nameof(ShowChannelControlsHint));
        OnPropertyChanged(nameof(CanSetFrequencyB));
        OnPropertyChanged(nameof(CanReLogin));
        OnPropertyChanged(nameof(CanRevertToGuest));
        OnPropertyChanged(nameof(HasFrequencyB));
        OnPropertyChanged(nameof(ShowChannelControls));
    }

    public async Task ShutdownAsync()
    {
        await DisconnectAsync();
        _uiTimer.Stop();
        DisposeSinks();
        SaveSettings();
    }

    private bool BuildSinks()
    {
        DisposeSinks();
        AudioError = "";

        if (RecordEnabled)
        {
            var path = RecordPath.Trim();
            if (string.IsNullOrEmpty(path))
            {
                AudioError = "Recording is on but no file path is set.";
                AddNotice("error", AudioError);
                return false;
            }

            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _recorder = new WavFileWriter(path);
                _recordingStartedAt = DateTime.UtcNow;
                AddNotice("info", $"Recording to {path}");
            }
            catch (Exception ex)
            {
                AudioError = $"Cannot start recording: {ex.Message}";
                AddNotice("error", AudioError);
                return false;
            }
        }

        if (PlaybackEnabled)
        {
            var deviceName = CleanDeviceName(SelectedAudioDevice);
            _player = SoundFlowAudioSink.TryStart(
                deviceName,
                out var playbackError,
                targetLatency: TimeSpan.FromMilliseconds(BufferMs));

            if (_player is null)
            {
                AudioError = $"Playback unavailable: {playbackError}";
                AddNotice("error", AudioError);
            }
            else
            {
                AddNotice("info", $"Playback on {_player.DeviceName} ({_player.SampleRate} Hz, {BufferMs} ms buffer)");
            }
        }

        _waveformSink = new WaveformTapSink();

        var sinks = new List<IGuestAudioSink>
        {
            _waveformSink,
        };
        if (_recorder is not null)
        {
            sinks.Add(_recorder);
        }
        if (_player is not null)
        {
            sinks.Add(_player);
        }

        _sink = sinks.Count == 1 ? sinks[0] : new CompositeAudioSink(sinks.ToArray());

        return true;
    }

    private static string? CleanDeviceName(string? displayName)
    {
        if (displayName is null)
        {
            return null;
        }

        const string suffix = " (default)";
        return displayName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? displayName[..^suffix.Length]
            : displayName;
    }

    private void DisposeSinks()
    {
        if (_sink is IDisposable sinkDisposable)
        {
            sinkDisposable.Dispose();
        }

        _sink = null;
        _recorder = null;
        _recordingStartedAt = null;
        _player = null;
        _waveformSink?.Dispose();
        _waveformSink = null;
    }

    private void UpdateTuningState()
    {
        TuningState level;
        string text;
        if (_authSession is null)
        {
            level = TuningState.Locked;
            text = "TUNING LOCKED — not authenticated";
        }
        else if (ListenOnly)
        {
            level = TuningState.Locked;
            text = "TUNING LOCKED — listen only";
        }
        else if (_authSession.AuthState != AuthState.Authorised)
        {
            level = TuningState.Locked;
            text = "TUNING LOCKED — not authorised";
        }
        else if (_radioClosed)
        {
            level = TuningState.Locked;
            text = "TUNING LOCKED — radio closed by remote";
        }
        else if (_radioPoweredOff)
        {
            level = TuningState.Locked;
            text = "TUNING LOCKED — radio powered off";
        }
        else if (CanControlChannels)
        {
            level = TuningState.Ok;
            text = "TUNING — ON";
        }
        else
        {
            level = TuningState.Locked;
            text = "TUNING LOCKED";
        }

        _tuningState = level;
        TuningStatusText = text;
        TuningStatusBrush = level switch
        {
            TuningState.Ok => ThemeBrush("SuccessBrush"),
            _ => ThemeBrush("ErrorBrush"),
        } ?? ThemeBrush("TextSecondaryBrush") ?? new SolidColorBrush(Colors.Gray);
    }

    private static IBrush? ThemeBrush(string key)
    {
        try
        {
            var resources = Avalonia.Application.Current?.Resources;
            if (resources is null)
            {
                return null;
            }
            return resources.TryGetResource(key, null, out var value)
                ? value as IBrush
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void PlayTone(int hertz, int milliseconds)
    {
#pragma warning disable CA1416 // Console.Beep is Windows-only; silence is acceptable elsewhere.
        try { Console.Beep(hertz, milliseconds); }
#pragma warning restore CA1416
        catch { /* Beep is unavailable on some hosts; silence is acceptable. */ }
    }

    private void PlayAck() => PlayTone(900, 80);
    private void PlayReject() => PlayTone(260, 220);

    private void ResetState()
    {
        _chatLines.Clear();
        _notices.Clear();
        _unknownKeys.Clear();
        _lastLinesApplied = 0;
        FrequencyDisplay = "—";
        FrequencyBDisplay = "";
        HasFrequencyB = false;
        ModeDisplay = "";
        Band = "";
        ProtocolVersion = "";
        OrbId = "";
        RadioDisplay = "";
        SMeterText = "";
        SMeterPercent = 0;
        SMeterValue = 0;
        SMeterReadout = "";
        StateReady = false;
        AudioLevelPercent = 0;
        PlayoutDepthMs = 0;
        Underruns = 0;
        AudioPackets = 0;
        DecodedSeconds = 0;
        DecodeErrors = 0;
        ControlLines = 0;
        WaveformSamples = null;
        NeedleValue = -40.0;
        NeedleValueText = "";
        _needleSmoothed = -40.0;

        _modeOptions.Clear();
        _filterOptions.Clear();
        _connectedUsers.Clear();
        _radioClosed = false;
        _radioPoweredOff = false;
        AuthStateText = "Pending";
        AuthMessageText = "";
        RestrictionBanner = "";
        FrequencyAValue = 14_250_000m;
        FrequencyBValue = 7_185_000m;
        BandText = "";
        ModeText = "";
        FilterText = "";
        OnPropertyChanged(nameof(HasModeOptions));
        OnPropertyChanged(nameof(HasFilterOptions));
    }

    private async Task RunLoopAsync(GuestTarget target, CancellationToken cancellationToken)
    {
        var attempt = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                attempt++;
                if (attempt > 1)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Status = "connecting";
                        AddNotice("info", $"Reconnect attempt {attempt}");
                    });
                }

                _player?.Buffer.Reset();

                var session = new GuestSession(new GuestSessionOptions
                {
                    Target = target,
                    AudioSink = _sink,
                });

                WireSession(session);
                _lastLinesApplied = -1;
                _session = session;

                var report = await session.RunAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                _totals.Add(report);

                if (report.Cancelled || report.Failure is null || !ReconnectEnabled)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (report.Failure is not null)
                        {
                            Status = "link lost";
                            AddNotice("error", $"Link ended: {report.Failure}");
                        }
                        else if (!report.Cancelled)
                        {
                            Status = "idle";
                        }
                    });
                    break;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    Status = "link lost";
                    AddNotice("warning", $"Link ended: {report.Failure}; retrying in 1 s");
                });

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                _session = null;
                IsConnected = false;
                if (Status is not "connecting" and not "link lost")
                {
                    Status = "idle";
                }

                _connectedAt = null;
                OnPropertyChanged(nameof(CanConnectManual));
                OnPropertyChanged(nameof(CanConnectRemote));
            });
        }
    }

    private async Task RunLoopAuthenticatedAsync(AuthenticatedTarget target, CancellationToken cancellationToken)
    {
        var attempt = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                attempt++;
                if (attempt > 1)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Status = "connecting";
                        AddNotice("info", $"Reconnect attempt {attempt}");
                    });
                }

                _player?.Buffer.Reset();

                var session = new AuthenticatedSession(new AuthenticatedSessionOptions
                {
                    Target = target,
                    AudioSink = _sink,
                });

                WireSession(session);
                _lastLinesApplied = -1;
                _authSession = session;

                Dispatcher.UIThread.Post(() =>
                {
                    AuthStateText = "Pending";
                    AuthMessageText = "";
                    _radioClosed = false;

                    OnPropertyChanged(nameof(CanControlChannels));
                    OnPropertyChanged(nameof(ShowChannelControlsHint));
                    OnPropertyChanged(nameof(CanSetFrequencyB));
                    OnPropertyChanged(nameof(CanReLogin));
                    OnPropertyChanged(nameof(CanRevertToGuest));
                    OnPropertyChanged(nameof(HasFrequencyB));
                });

                var report = await session.RunAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                _totals.Add(report);

                Dispatcher.UIThread.Post(() =>
                {
                    OnPropertyChanged(nameof(IsAuthenticated));
                    OnPropertyChanged(nameof(CanSendChat));
                });

                if (report.Cancelled || report.Failure is null || !ReconnectEnabled)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (report.Failure is not null)
                        {
                            Status = "link lost";
                            AddNotice("error", $"Link ended: {report.Failure}");
                        }
                        else if (!report.Cancelled)
                        {
                            Status = "idle";
                        }
                    });
                    break;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    Status = "link lost";
                    AddNotice("warning", $"Link ended: {report.Failure}; retrying in 1 s");
                });

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                _authSession = null;
                IsConnected = false;
                if (Status is not "connecting" and not "link lost")
                {
                    Status = "idle";
                }

                _connectedAt = null;
                OnPropertyChanged(nameof(CanConnectManual));
                OnPropertyChanged(nameof(CanConnectRemote));
                OnPropertyChanged(nameof(IsAuthenticated));
                OnPropertyChanged(nameof(CanSendChat));
                OnPropertyChanged(nameof(CanControlChannels));
                OnPropertyChanged(nameof(ShowChannelControlsHint));
                OnPropertyChanged(nameof(CanSetFrequencyB));
                OnPropertyChanged(nameof(CanReLogin));
                OnPropertyChanged(nameof(CanRevertToGuest));
                OnPropertyChanged(nameof(HasFrequencyB));
            });
        }
    }

    private void WireSession(GuestSession session)
    {
        session.Ready += state =>
            Dispatcher.UIThread.Post(() => OnReady(state));

        session.ChatReceived += chat =>
            Dispatcher.UIThread.Post(() => OnChat(chat));

        session.Notice += (kind, text) =>
            Dispatcher.UIThread.Post(() => OnNotice(kind, text));
    }

    private void WireSession(AuthenticatedSession session)
    {
        session.Ready += state =>
            Dispatcher.UIThread.Post(() => OnReady(state));

        session.ChatReceived += chat =>
            Dispatcher.UIThread.Post(() => OnChat(chat));

        session.Notice += (kind, text) =>
            Dispatcher.UIThread.Post(() => OnNotice(kind, text));

        session.AuthStateChanged += (state, message) =>
            Dispatcher.UIThread.Post(() =>
            {
                var msg = message is null ? "" : $": {message}";
                AddNotice("auth", $"{state}{msg}");

                AuthStateText = state switch
                {
                    AuthState.Pending => "Pending",
                    AuthState.Authenticating => "Authenticating…",
                    AuthState.Authorised => "Authorised",
                    AuthState.Refused => "Refused",
                    AuthState.Guest => "Guest",
                    _ => state.ToString(),
                };
                AuthMessageText = message ?? "";

                OnPropertyChanged(nameof(IsAuthenticated));
                OnPropertyChanged(nameof(CanSendChat));
                OnPropertyChanged(nameof(CanRevertToGuest));
                OnPropertyChanged(nameof(CanReLogin));
                OnPropertyChanged(nameof(CanControlChannels));
                OnPropertyChanged(nameof(CanSetFrequencyB));
                OnPropertyChanged(nameof(ShowChannelControls));
                UpdateTuningState();
            });
    }

    public async Task SendChatAsync()
    {
        if (_authSession is null || _authSession.AuthState != AuthState.Authorised || string.IsNullOrWhiteSpace(_chatInput))
        {
            return;
        }

        var text = _chatInput;
        ChatInput = "";
        OnPropertyChanged(nameof(CanSendChat));

        var result = await _authSession.SendChatAsync(text);
        if (!result.Sent)
        {
            AddNotice("error", $"Chat send failed: {result.Error}");
        }
    }

    public async Task SetFrequencyAAsync()
    {
        if (_authSession is null || !CanControlChannels)
        {
            PlayReject();
            AddNotice("error", "Tuning is locked — authenticate and connect first.");
            return;
        }

        if (FrequencyAValue < 1m || FrequencyAValue > 10_000_000_000m)
        {
            PlayReject();
            AddNotice("error", "Frequency must be within 1 Hz – 10 GHz (SPEC-STATE-016).");
            return;
        }

        var result = await _authSession.SetFrequencyAsync((long)FrequencyAValue);
        if (result.Sent)
        {
            PlayAck();
            AddNotice("info", $"✓ VFO A set to {RadioStateStore.FormatHz(FrequencyAValue.ToString("0"))}");
        }
        else
        {
            PlayReject();
            AddNotice("error", $"Set VFO A failed: {result.Error}");
        }
    }

    public async Task SetFrequencyBAsync()
    {
        if (_authSession is null || !CanSetFrequencyB)
        {
            PlayReject();
            AddNotice("error", "VFO B tuning is locked — authenticate and connect first.");
            return;
        }

        if (FrequencyBValue < 1m || FrequencyBValue > 10_000_000_000m)
        {
            PlayReject();
            AddNotice("error", "Frequency must be within 1 Hz – 10 GHz (SPEC-STATE-016).");
            return;
        }

        var result = await _authSession.SetFrequencyBAsync((long)FrequencyBValue);
        if (result.Sent)
        {
            PlayAck();
            AddNotice("info", $"✓ VFO B set to {RadioStateStore.FormatHz(FrequencyBValue.ToString("0"))}");
        }
        else
        {
            PlayReject();
            AddNotice("error", $"Set VFO B failed: {result.Error}");
        }
    }

    public async Task SetBandAsync()
    {
        if (_authSession is null || !CanControlChannels)
        {
            PlayReject();
            AddNotice("error", "Tuning is locked — authenticate and connect first.");
            return;
        }

        var result = await _authSession.SetBandAsync(BandText.Trim());
        if (result.Sent)
        {
            PlayAck();
            AddNotice("info", $"✓ Band set to {BandText.Trim()}");
        }
        else
        {
            PlayReject();
            AddNotice("error", $"Set band failed: {result.Error}");
        }
    }

    public async Task SetModeAsync()
    {
        if (_authSession is null || !CanControlChannels)
        {
            PlayReject();
            AddNotice("error", "Tuning is locked — authenticate and connect first.");
            return;
        }

        var result = await _authSession.SetModeAsync(ModeText);
        if (result.Sent)
        {
            PlayAck();
            AddNotice("info", $"✓ Mode set to {ModeText}");
        }
        else
        {
            PlayReject();
            AddNotice("error", $"Set mode failed: {result.Error}");
        }
    }

    public async Task SetFilterAsync()
    {
        if (_authSession is null || !CanControlChannels)
        {
            PlayReject();
            AddNotice("error", "Tuning is locked — authenticate and connect first.");
            return;
        }

        var result = await _authSession.SetFilterAsync(FilterText);
        if (result.Sent)
        {
            PlayAck();
            AddNotice("info", $"✓ Filter set to {FilterText}");
        }
        else
        {
            PlayReject();
            AddNotice("error", $"Set filter failed: {result.Error}");
        }
    }

    public async Task ReLoginAsync()
    {
        if (_authSession is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(AuthUser))
        {
            AddNotice("error", "User name is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(AuthPassword))
        {
            AddNotice("error", "Password is required.");
            return;
        }

        var md5 = ComputeMd5Hex(AuthPassword);
        var result = await _authSession.ReloginAsync(AuthUser, md5);
        if (!result.Sent)
        {
            AddNotice("error", $"Re-login failed: {result.Error}");
        }
    }

    public async Task RevertToGuestAsync()
    {
        if (_authSession is not null)
        {
            _authSession.RevertToGuest();
        }
    }

    private void OnReady(RadioStateStore state)
    {
        StateReady = true;
        Status = "live";
        var snapshot = state.Snapshot();
        UpdateStateDisplay(snapshot);

        // Seed the user-editable tuning controls from the confirmed radio state so
        // they open showing the radio's actual settings. The main display stays
        // server-driven; these are the pending values the operator turns and sets.
        if (long.TryParse(snapshot.FrequencyText, out var f)) FrequencyAValue = f;
        if (long.TryParse(snapshot.FrequencyBText, out var fb)) FrequencyBValue = fb;
        BandText = snapshot.Band ?? "";
        ModeText = snapshot.Dropdowns.GetValueOrDefault("Mode", "");
        FilterText = snapshot.Dropdowns.GetValueOrDefault("Filter", "");

        UpdateTuningState();
        AddNotice("info", "State snapshot latched");
    }

    private void UpdateStateDisplay(RadioStateSnapshot state)
    {
        FrequencyDisplay = RadioStateStore.FormatHz(state.FrequencyText);
        HasFrequencyB = !string.IsNullOrEmpty(state.FrequencyBText);
        FrequencyBDisplay = HasFrequencyB ? $"VFO B: {RadioStateStore.FormatHz(state.FrequencyBText)}" : "";

        var mode = state.Dropdowns.GetValueOrDefault("Mode", "");
        var filter = state.Dropdowns.GetValueOrDefault("Filter", "");
        ModeDisplay = string.IsNullOrEmpty(filter) ? mode : $"{mode} • {filter}";
        Band = state.Band ?? "";
        ProtocolVersion = state.ProtocolVersion ?? "";
        OrbId = state.OrbId ?? "";

        // The server sends the PWROFF sentinel as FrequencyText when the radio is powered
        // off. Power state is receive-only: a guest cannot power the radio on.
        var wasOff = _radioPoweredOff;
        var poweredOff = string.Equals(state.FrequencyText, "PWROFF", StringComparison.OrdinalIgnoreCase);
        if (SetProperty(ref _radioPoweredOff, poweredOff))
        {
            OnPropertyChanged(nameof(PowerStateText));
            OnPropertyChanged(nameof(PowerStateBrush));
            OnPropertyChanged(nameof(CanControlChannels));
            OnPropertyChanged(nameof(CanSetFrequencyB));
            UpdateTuningState();
            if (poweredOff && !wasOff)
            {
                AddNotice("warning", "Radio is powered off (PWROFF). No audio or tuning until the remote operator powers it on.");
            }
            else if (!poweredOff && wasOff)
            {
                AddNotice("info", "Radio is now powered on.");
            }
        }

        if (state.Lists.TryGetValue("Mode", out var modeList))
        {
            _modeOptions.Clear();
            foreach (var entry in modeList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _modeOptions.Add(entry);
            }
        }
        else
        {
            _modeOptions.Clear();
        }
        OnPropertyChanged(nameof(HasModeOptions));

        if (state.Lists.TryGetValue("Filter", out var filterList))
        {
            _filterOptions.Clear();
            foreach (var entry in filterList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _filterOptions.Add(entry);
            }
        }
        else
        {
            _filterOptions.Clear();
        }
        OnPropertyChanged(nameof(HasFilterOptions));

        // Sync connected-users roster (dedup by uid).
        _connectedUsers.Clear();
        var seenUids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in state.Roster)
        {
            if (seenUids.Add(entry.Uid))
            {
                _connectedUsers.Add(entry.User);
            }
        }

        RadioDisplay = string.IsNullOrEmpty(state.Driver)
            ? state.RadioName ?? ""
            : $"{state.RadioName} / {state.Driver}";

        UpdateSMeter(state.SMeter);

        _unknownKeys.Clear();
        foreach (var item in state.Extension.Where(i => !IsModeledDeclarationKey(i.Key)))
        {
            _unknownKeys.Add(new StateItemViewModel(item.Key, item.Value));
        }

        _lastLinesApplied = state.LinesApplied;
    }

    private static bool IsModeledDeclarationKey(string key)
    {
        return key.Equals("id", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("version", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("buttons", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("dropdowns", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("lists", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("sliders", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("messages", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("statuses", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("ranges", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSMeter(string raw)
    {
        SMeterText = raw;
        SMeterPercent = 0;
        SMeterValue = 0;
        SMeterReadout = "";
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        var parts = raw.Split(',', 3);
        if (parts.Length >= 3 &&
            int.TryParse(parts[1], out var value) &&
            int.TryParse(parts[2], out var max) &&
            max > 0)
        {
            SMeterPercent = Math.Clamp((double)value / max * 100.0, 0, 100);
            SMeterValue = Math.Clamp((double)value / max, 0.0, 1.0);
            var label = parts[0].Trim();
            SMeterReadout = string.IsNullOrWhiteSpace(label)
                ? "S9"
                : (label.Contains("+", StringComparison.Ordinal) && !label.Contains("dB", StringComparison.OrdinalIgnoreCase)
                    ? label + " dB"
                    : label);
        }
        else if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
        {
            SMeterReadout = parts[0].Trim();
        }
    }

    private void OnChat(ChatMessage chat)
    {
        _chatLines.Add(new ChatLineViewModel(chat.Sender, chat.Text, chat.Timestamp));
        TrimCollection(_chatLines, 200);
    }

    private void OnNotice(string kind, string text)
    {
        if (kind is "sync")
        {
            Status = "unverified";
        }

        if (kind is "restriction")
        {
            RestrictionBanner = text;
        }

        // A remote that closed the radio (policy/limit) no longer accepts writes.
        if (kind is "warning" && text.StartsWith("The remote closed", StringComparison.Ordinal))
        {
            if (!_radioClosed)
            {
                _radioClosed = true;
                OnPropertyChanged(nameof(CanControlChannels));
                OnPropertyChanged(nameof(CanSetFrequencyB));
                UpdateTuningState();
            }
        }

        AddNotice(kind, text);
    }

    private void AddNotice(string kind, string text)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        _notices.Add(new NoticeViewModel(kind, text, timestamp));
        TrimCollection(_notices, 200);
    }

    private static void TrimCollection<T>(ObservableCollection<T> collection, int limit)
    {
        while (collection.Count > limit)
        {
            collection.RemoveAt(0);
        }
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        // A link loss freezes the clock at its last live value; snapping back to zero reads
        // as "nothing ever happened", next to a status that says the link just ended.
        if (_connectedAt.HasValue)
        {
            Elapsed = DateTime.UtcNow - _connectedAt.Value;
        }

        GuestStats? stats = null;
        RadioStateStore? stateStore = null;

        if (_session is not null)
        {
            stats = _session.Stats;
            stateStore = _session.State;
        }
        else if (_authSession is not null)
        {
            stats = _authSession.Stats;
            stateStore = _authSession.State;
        }

        if (stats is not null)
        {
            AudioPackets = _totals.AudioPackets + stats.AudioPackets;
            DecodedSeconds = (_totals.DecodedSamples + stats.DecodedSamples) / (double)SpeexPcmDecoder.SampleRate;
            DecodeErrors = _totals.DecodeErrors + stats.DecodeErrors;
            ControlLines = _totals.ControlLines + stats.ControlLines;
            AudioLevelPercent = Math.Clamp(stats.LastRms * 400.0, 0, 100);

            // Read a copy: the session applies lines on its own pump thread, and the live
            // bag dictionaries throw if enumerated while it writes them.
            var snapshot = stateStore!.Snapshot();
            if (snapshot.IsReady && snapshot.LinesApplied != _lastLinesApplied)
            {
                UpdateStateDisplay(snapshot);
            }
        }
        else
        {
            AudioPackets = _totals.AudioPackets;
            DecodedSeconds = _totals.DecodedSamples / (double)SpeexPcmDecoder.SampleRate;
            DecodeErrors = _totals.DecodeErrors;
            ControlLines = _totals.ControlLines;
            AudioLevelPercent = 0;
        }

        if (_player is not null)
        {
            PlayoutDepthMs = _player.Buffer.DepthSeconds * 1000.0;
            Underruns = _player.Buffer.Underruns;
        }
        else
        {
            PlayoutDepthMs = 0;
            Underruns = 0;
        }

        if (IsRecording)
        {
            OnPropertyChanged(nameof(RecordingDuration));
            OnPropertyChanged(nameof(RecordingStatus));
        }

        // Waveform scope: pull the most recent decoded samples from the tap.
        if (_waveformSink is not null && WaveformVisible)
        {
            var buffer = new short[WaveformTapSink.Capacity];
            var count = _waveformSink.ReadRecent(buffer, buffer.Length);
            if (count > 0)
            {
                Array.Resize(ref buffer, count);
                WaveformSamples = buffer;
            }
        }

        // VU needle: map the RMS level (0..100) onto a -40..+3 dB scale with ballistics.
        var level = AudioLevelPercent;
        var db = level <= 0.01 ? -40.0 : Math.Clamp(20.0 * Math.Log10(level / 100.0) + 3.0, -40.0, 3.0);
        var delta = db - _needleSmoothed;
        _needleSmoothed += delta * (delta > 0 ? 0.55 : 0.18);
        _needleSmoothed = Math.Clamp(_needleSmoothed, -40.0, 3.0);
        NeedleValue = _needleSmoothed;
        NeedleValueText = _needleSmoothed <= -39.5 ? "-40 dB" : $"{_needleSmoothed:0.0} dB";
    }

    private sealed class GuestTotals
    {
        public long ControlLines { get; private set; }
        public long AudioPackets { get; private set; }
        public long DecodedSamples { get; private set; }
        public long DecodeErrors { get; private set; }

        public void Add(GuestSessionReport report)
        {
            ControlLines = Math.Max(ControlLines, report.ControlLines);
            AudioPackets += report.AudioPackets;
            DecodedSamples += report.DecodedSamples;
            DecodeErrors += report.DecodeErrors;
        }

        public void Add(AuthenticatedSessionReport report)
        {
            ControlLines = Math.Max(ControlLines, report.ControlLines);
            AudioPackets += report.AudioPackets;
            DecodedSamples += report.DecodedSamples;
            DecodeErrors += report.DecodeErrors;
        }
    }
}
