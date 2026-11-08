using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Tenaire;

public partial class MainWindow : Window
{
    sealed record MicrophoneOption(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    sealed record LivePlatform(string Id, string Label, string? ServerUrl)
    {
        public override string ToString() => Label;
    }

    const string CustomPlatformId = "custom";
    static readonly LivePlatform[] LivePlatforms =
    {
        new("youtube", "YouTube", "rtmps://a.rtmps.youtube.com/live2"),
        new("twitch", "Twitch", "rtmps://ingest.global-contribute.live-video.net/app"),
        new(CustomPlatformId, "Özel sunucu", null),
    };

    const string PhoneMicrophoneId = "phone";
    static readonly MicrophoneOption PhoneMicrophone = new(PhoneMicrophoneId, "Telefon (takılıysa kulaklık mikrofonu)");

    static readonly string RecordingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Tenaire");

    static readonly TimeSpan LiveConfirmedAfter = TimeSpan.FromSeconds(1);
    const int WindowVideoBufferMs = 100;

    const int MaxRetries = 5;
    static readonly TimeSpan StableRun = TimeSpan.FromSeconds(15);

    // Telefona gereksiz yuk binmesin diye ses testi kendiliginden kapanir.
    static readonly TimeSpan AudioTestLimit = TimeSpan.FromSeconds(60);

    const int WmDeviceChange = 0x0219;
    const int UsbEventScanTicks = 3;
    const int SafetyScanTicks = 10;
    static readonly TimeSpan IdleBatteryInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan RecordingBatteryInterval = TimeSpan.FromSeconds(30);

    const string NoSignalText = "— dB";
    const string MergeLabel = "Sesleri Videoyla Birleştir";

    readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _batteryTimer = new() { Interval = IdleBatteryInterval };
    readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    readonly MirrorSession _session = new();
    readonly MirrorSession _phoneMicrophone = new();
    readonly FfmpegProcess _pcMicrophone = new();
    readonly AppSettings _settings = AppSettings.Load();

    readonly Brush _good, _warn, _hot, _idle, _accent, _text, _muted;

    IReadOnlyList<Device> _devices = Array.Empty<Device>();
    AudioTest? _audioTest;
    AudioPassthrough? _passthrough;
    Task? _passthroughStopping;
    bool _sessionIsWindow;
    LiveStream? _live;
    Task? _liveStopping;
    string? _liveError;
    bool _liveReachedServer, _sessionWasLive;
    TimeSpan _liveElapsed;
    CancellationTokenSource? _mergeCancellation;
    string? _selectedSerial;
    string? _notice, _recordingPath, _microphonePath;
    bool _wantMirror, _refreshingDevices, _refreshingBattery, _refreshingMicrophones, _stoppingAudioTest, _closed;
    bool _delayIsForPhone = true;
    int _retries, _missedDeviceChecks, _usbScanTicks, _ticksSinceScan;
    long _startedAt, _audioTestStartedAt;
    double? _lastTemperature, _temperatureAtStart;

    Device? Selected =>
        _devices.FirstOrDefault(d => d.Serial == _selectedSerial)
        ?? _devices.FirstOrDefault(d => d.IsReady)
        ?? _devices.FirstOrDefault();

    bool IsMirroring => _wantMirror || _session.IsRunning;
    bool IsMerging => _mergeCancellation is not null;
    bool IsRecordOn => _record.IsChecked == true;
    bool IsLiveMode => _modeLive.IsChecked == true;
    bool IsWindowMode => _modeWindow.IsChecked == true;
    string PhoneMicrophoneSource => MirrorArguments.PhoneMicrophoneSource(_echoCancellation.IsChecked == true);

    /// <summary>Bilgisayar mikrofonu seciliyse kimligi, telefon seciliyse null.</summary>
    string? SelectedPcMicrophoneId =>
        _microphoneSource.SelectedItem is MicrophoneOption option && option.Id != PhoneMicrophoneId ? option.Id : null;

    public MainWindow()
    {
        InitializeComponent();

        _good = (Brush)FindResource("Mint");
        _hot = (Brush)FindResource("Red");
        _text = (Brush)FindResource("Text");
        _muted = (Brush)FindResource("Muted");
        _warn = new SolidColorBrush(Color.FromRgb(0xF5, 0xB8, 0x3D));
        _idle = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        _accent = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xF6));

        BuildProfileButtons();

        _record.IsChecked = _settings.Record;
        _gameAudio.IsChecked = _settings.GameAudio;
        _microphone.IsChecked = _settings.Microphone;

        foreach (var platform in LivePlatforms) _livePlatform.Items.Add(platform);
        _livePlatform.SelectedItem = LivePlatforms.FirstOrDefault(p => p.Id == _settings.LivePlatform) ?? LivePlatforms[0];
        _liveBitrate.Value = _settings.LiveBitRateKbps;
        _liveUrl.Text = _settings.LiveCustomUrl;
        _liveKey.Password = _settings.LiveKey;
        _liveGameAudio.IsChecked = _settings.LiveGameAudio;
        _liveMicrophone.IsChecked = _settings.LiveMicrophone;
        _liveRecord.IsChecked = _settings.LiveRecord;
        _windowGameAudio.IsChecked = _settings.WindowGameAudio;
        _windowMicrophone.IsChecked = _settings.WindowMicrophone;
        _windowRecord.IsChecked = _settings.WindowRecord;
        _echoCancellation.IsChecked = _settings.EchoCancellation;
        (_settings.Mode switch { "live" => _modeLive, "window" => _modeWindow, _ => _modeRecord }).IsChecked = true;

        InitializeLevel(_gameDb, _gameSlider, _settings.GameDb);
        InitializeLevel(_microphoneDb, _microphoneSlider, _settings.MicrophoneDb);
        _gameDb.ValueChanged += PushLiveLevels;
        _microphoneDb.ValueChanged += PushLiveLevels;

        _delay.Value = Math.Clamp(_settings.MicrophoneDelayMs, 0, 5000);
        _gameReadout.Text = _microphoneReadout.Text = NoSignalText;
        _mergeText.Text = MergeLabel;
        _temperature.Text = "— °C";
        _temperature.Foreground = _muted;
        _battery.Text = "Pil sıcaklığı";

        _microphoneSource.Items.Add(PhoneMicrophone);
        _microphoneSource.SelectedItem = PhoneMicrophone;

        _session.Exited += (code, error) => PostToUi(() => OnSessionExited(code, error));
        // Iki motor ayni anda baslarsa telefondaki sunucu dosyasini birbirinin ustune yazar,
        // bu yuzden mikrofon ilk goruntu karesinden sonra acilir.
        _session.VideoStarted += () => PostToUi(StartMicrophone);
        _phoneMicrophone.Exited += (code, error) => PostToUi(() => OnMicrophoneExited(code, error));
        _pcMicrophone.Exited += (code, error) => PostToUi(() => OnMicrophoneExited(code, error));
        _deviceTimer.Tick += async (_, _) => await OnDeviceTickAsync();
        _batteryTimer.Tick += async (_, _) => await RefreshBatteryAsync();
        _meterTimer.Tick += (_, _) => OnMeterTick();

        UpdateHint();
        UpdateView();
    }

    void PostToUi(Action action)
    {
        if (!_closed) Dispatcher.BeginInvoke(action);
    }

    void BuildProfileButtons()
    {
        var selected = Profiles.Find(_settings.ProfileKey).Key;
        foreach (var profile in Profiles.All)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = profile.Title, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center });
            content.Children.Add(new TextBlock { Text = profile.Summary, FontSize = 12, Opacity = 0.75, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });

            var button = new RadioButton
            {
                Style = (Style)FindResource("Segment"),
                GroupName = "profile",
                Content = content,
                Tag = profile,
                IsChecked = profile.Key == selected,
            };
            button.Checked += (_, _) => UpdateHint();
            _profileHost.Children.Add(button);
        }
    }

    static void InitializeLevel(NumberBox box, Slider slider, double db)
    {
        box.Minimum = Gain.MinDb;
        box.Maximum = Gain.MaxDb;
        box.Value = (decimal)Math.Clamp(db, Gain.MinDb, Gain.MaxDb);
        slider.Minimum = Gain.MinDb;
        slider.Maximum = Gain.MaxDb;
        slider.Value = (double)box.Value;

        // Yalnizca fareyle surukleme 0,5 dB'ye oturur, kutuya yazilan 0,1 dB'lik deger korunur.
        slider.ValueChanged += (_, e) => box.Value = (decimal)e.NewValue;
        box.ValueChanged += () => slider.Value = (double)box.Value;
        slider.MouseDoubleClick += (_, _) => box.Value = 0;
        slider.PreviewMouseWheel += (_, e) =>
        {
            box.Value += Math.Sign(e.Delta) * 0.5M;
            e.Handled = true;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowHook);
    }

    IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // USB degisikliklerinde Windows haber verir, boyle boslugu surekli taramaya gerek kalmaz.
        if (msg == WmDeviceChange)
        {
            _usbScanTicks = UsbEventScanTicks;
            _ = RefreshMicrophonesAsync();
        }
        return IntPtr.Zero;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _deviceTimer.Start();
        _batteryTimer.Start();
        await RefreshMicrophonesAsync();
        await RefreshDevicesAsync();
        await RefreshBatteryAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closed = true;
        _deviceTimer.Stop();
        _batteryTimer.Stop();
        _meterTimer.Stop();
        _wantMirror = false;
        SaveSettings();

        _mergeCancellation?.Cancel();
        var test = _audioTest;
        _audioTest = null;
        if (test is not null) Task.Run(test.StopAsync).Wait(TimeSpan.FromSeconds(15));

        var live = _live;
        _live = null;
        if (live is not null && _liveStopping is null) Task.Run(live.StopAsync).Wait(TimeSpan.FromSeconds(15));

        var passthrough = _passthrough;
        _passthrough = null;
        if (passthrough is not null && _passthroughStopping is null) Task.Run(passthrough.StopAsync).Wait(TimeSpan.FromSeconds(15));

        _session.Stop();
        StopMicrophones();
        // Uygulama kapanmadan bitmesi beklenir, yoksa adb arka planda acik kalabiliyor.
        if (Engine.IsAvailable) Task.Run(Adb.StopServerAsync).Wait(TimeSpan.FromSeconds(3));
        base.OnClosing(e);
    }

    void OnMinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    void OnRecordToggled(object sender, RoutedEventArgs e)
    {
        UpdateHint();
        UpdateView();
    }

    void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _recordPanel.Visibility = _modeRecord.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _livePanel.Visibility = IsLiveMode ? Visibility.Visible : Visibility.Collapsed;
        _windowPanel.Visibility = IsWindowMode ? Visibility.Visible : Visibility.Collapsed;
        _notice = null;
        UpdateHint();
        UpdateView();
    }

    void OnLivePlatformChanged(object sender, SelectionChangedEventArgs e)
    {
        var isCustom = (_livePlatform.SelectedItem as LivePlatform)?.Id == CustomPlatformId;
        _liveUrlPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    void PushLiveLevels()
    {
        var gameDb = (double)_gameDb.Value;
        var microphoneDb = (double)_microphoneDb.Value;
        _live?.SetLevels(gameDb, microphoneDb);
        _passthrough?.SetGains(gameDb, microphoneDb);
    }

    void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(RecordingsFolder);
        Process.Start(new ProcessStartInfo(RecordingsFolder) { UseShellExecute = true })?.Dispose();
    }

    void OnDeviceListChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_deviceList.SelectedItem is not string serial || serial == _selectedSerial) return;
        _selectedSerial = serial;
        UpdateView();
        _ = RefreshBatteryAsync();
    }

    void OnMicrophoneDropDownOpened(object? sender, EventArgs e) => _ = RefreshMicrophonesAsync();

    void OnMicrophoneSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_microphoneSource.SelectedItem is not MicrophoneOption option) return;
        if (IsLoaded) UpdateView();

        // Telefon ve bilgisayar mikrofonunun gecikmesi farkli oldugu icin ayri hatirlanir.
        var isPhone = option.Id == PhoneMicrophoneId;
        if (isPhone == _delayIsForPhone) return;

        StoreDelay();
        _delayIsForPhone = isPhone;
        _delay.Value = isPhone ? _settings.MicrophoneDelayMs : _settings.PcMicrophoneDelayMs;
    }

    void StoreDelay()
    {
        if (_delayIsForPhone) _settings.MicrophoneDelayMs = (int)_delay.Value;
        else _settings.PcMicrophoneDelayMs = (int)_delay.Value;
    }

    async Task RefreshMicrophonesAsync()
    {
        if (_refreshingMicrophones || !Merger.IsAvailable) return;
        _refreshingMicrophones = true;
        try
        {
            var devices = await PcMicrophone.ListAsync();
            if (_closed || IsMirroring || _audioTest is not null) return;

            var options = new[] { PhoneMicrophone }
                .Concat(devices.Select(d => new MicrophoneOption(d.Id, d.Name)))
                .ToArray();

            var current = _microphoneSource.Items.Cast<MicrophoneOption>().Select(o => o.Id);
            var unchanged = current.SequenceEqual(options.Select(o => o.Id));
            if (unchanged && _microphoneSource.SelectedItem is MicrophoneOption active
                          && (active.Id != PhoneMicrophoneId || _settings.MicrophoneSource == PhoneMicrophoneId)) return;

            var wanted = (_microphoneSource.SelectedItem as MicrophoneOption)?.Id is { } id && id != PhoneMicrophoneId
                ? id
                : _settings.MicrophoneSource;

            if (!unchanged)
            {
                _microphoneSource.Items.Clear();
                foreach (var option in options) _microphoneSource.Items.Add(option);
            }
            _microphoneSource.SelectedItem = options.FirstOrDefault(o => o.Id == wanted) ?? PhoneMicrophone;
        }
        finally
        {
            _refreshingMicrophones = false;
        }
    }

    void SaveSettings()
    {
        _settings.ProfileKey = CurrentProfile.Key;
        _settings.Record = IsRecordOn;
        _settings.GameAudio = _gameAudio.IsChecked == true;
        _settings.Microphone = _microphone.IsChecked == true;
        _settings.GameDb = (double)_gameDb.Value;
        _settings.MicrophoneDb = (double)_microphoneDb.Value;
        StoreDelay();
        if (_microphoneSource.SelectedItem is MicrophoneOption option) _settings.MicrophoneSource = option.Id;
        _settings.Mode = IsLiveMode ? "live" : IsWindowMode ? "window" : "record";
        _settings.WindowGameAudio = _windowGameAudio.IsChecked == true;
        _settings.WindowMicrophone = _windowMicrophone.IsChecked == true;
        _settings.WindowRecord = _windowRecord.IsChecked == true;
        _settings.EchoCancellation = _echoCancellation.IsChecked == true;
        if (_livePlatform.SelectedItem is LivePlatform platform) _settings.LivePlatform = platform.Id;
        _settings.LiveCustomUrl = _liveUrl.Text.Trim();
        _settings.LiveBitRateKbps = (int)_liveBitrate.Value;
        _settings.LiveGameAudio = _liveGameAudio.IsChecked == true;
        _settings.LiveMicrophone = _liveMicrophone.IsChecked == true;
        _settings.LiveRecord = _liveRecord.IsChecked == true;
        _settings.LiveKey = _liveKey.Password.Trim();
        _settings.Save();
    }

    Profile CurrentProfile =>
        _profileHost.Children.OfType<RadioButton>().First(b => b.IsChecked == true).Tag as Profile ?? Profiles.Default;

    void UpdateHint()
    {
        var profile = CurrentProfile;
        if (IsWindowMode)
        {
            _hint.Text = profile.Hint;
            return;
        }

        if (IsLiveMode)
        {
            var (_, height, fps) = LiveFormat(profile);
            _hint.Text = $"{profile.Hint} Yayın {height}p {fps} fps olarak gider, grafik kartı kodlar.";
            return;
        }

        var gigabytesPerHour = profile.BitRateMbps * 3600 / 8 / 1000.0;
        _hint.Text = IsRecordOn
            ? $"{profile.Hint} Kayıt yaklaşık {FormatDecimal(gigabytesPerHour)} GB/saat yer kaplar."
            : profile.Hint;
    }

    /// <summary>Yayin servisleri 16:9 bekledigi icin dikey goruntu ortalanip yanlari doldurulur.</summary>
    static (int Width, int Height, int Fps) LiveFormat(Profile profile) =>
        profile.MaxFps <= 30 ? (1280, 720, 30) : (1920, 1080, 60);

    LiveSettings BuildLiveSettings()
    {
        var (width, height, fps) = LiveFormat(CurrentProfile);
        var platform = (LivePlatform)_livePlatform.SelectedItem;
        var server = (platform.ServerUrl ?? _liveUrl.Text).Trim().TrimEnd('/');
        var key = _liveKey.Password.Trim();

        _recordingPath = null;
        if (_liveRecord.IsChecked == true)
        {
            Directory.CreateDirectory(RecordingsFolder);
            _recordingPath = Path.Combine(RecordingsFolder, $"Tenaire_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_yayin.mp4");
        }

        return new LiveSettings(
            $"{server}/{key}", key, width, height, fps, (int)_liveBitrate.Value,
            _liveGameAudio.IsChecked == true, (double)_gameDb.Value,
            _liveMicrophone.IsChecked == true, SelectedPcMicrophoneId, (double)_microphoneDb.Value, (int)_delay.Value,
            _recordingPath);
    }

    /// <summary>Eksik bilgi varsa yayini baslatmadan aciklama dondurur.</summary>
    string? ValidateLive()
    {
        if (_liveKey.Password.Trim().Length == 0) return "Yayın anahtarını girin.";
        if ((_livePlatform.SelectedItem as LivePlatform)?.ServerUrl is not null) return null;

        var url = _liveUrl.Text.Trim();
        return url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase)
            ? null
            : "Sunucu adresi rtmp:// veya rtmps:// ile başlamalı.";
    }

    Task StopLiveAsync()
    {
        if (_live is not { } live) return Task.CompletedTask;
        return _liveStopping ??= FinishAsync();

        async Task FinishAsync()
        {
            try
            {
                await live.StopAsync();
            }
            finally
            {
                if (ReferenceEquals(_live, live)) _live = null;
                _liveStopping = null;
            }
        }
    }

    void OnLiveProgress(LiveStream live, TimeSpan elapsed)
    {
        if (!ReferenceEquals(live, _live)) return;

        // Baglanti kurulamasa da ffmpeg kisa bir ilerleme bildirebiliyor.
        if (elapsed >= LiveConfirmedAfter) _liveReachedServer = true;
        _liveElapsed = elapsed;
        if (_notice is null && _session.IsRunning) UpdateView();
    }

    static string DescribeLiveError(string? error) =>
        error?.Contains("nvenc", StringComparison.OrdinalIgnoreCase) == true
            ? "Grafik kartının video kodlayıcısı açılamadı."
            : "Yayın sunucusuna bağlanılamadı. İnternet bağlantısını, platformu ve yayın anahtarını kontrol edin.";

    void OnLiveExited(LiveStream live, int exitCode, string? error)
    {
        if (!ReferenceEquals(live, _live) || !_wantMirror) return;
        _liveError = DescribeLiveError(error);

        // Hic baglanamadiysa sorun buyuk ihtimalle adres veya anahtardir, tekrar denemek bosuna.
        if (!_liveReachedServer)
        {
            _wantMirror = false;
            _notice = $"Yayın başlatılamadı. {_liveError}";
        }
        _session.ForceExit(2);
        UpdateView();
    }

    // Tamamlanamayan kayit yalnizca birkac baytlik baslik birakir.
    static bool HasRecordedData(string? path) =>
        path is not null && File.Exists(path) && new FileInfo(path).Length > 1024;

    string RecordingNotice()
    {
        if (_sessionWasLive)
            return HasRecordedData(_recordingPath) ? $"Yayın bitti. Kayıt: {Path.GetFileName(_recordingPath)}" : "Yayın bitti.";
        if (!HasRecordedData(_recordingPath)) return "Yansıtma kapatıldı.";
        var microphone = HasRecordedData(_microphonePath) ? " Mikrofon ayrı dosyada." : "";
        return $"Kayıt tamamlandı: {Path.GetFileName(_recordingPath)}.{microphone}";
    }

    async Task OnDeviceTickAsync()
    {
        _ticksSinceScan++;
        var mustPoll = _wantMirror || Selected is { IsReady: false };
        if (_usbScanTicks > 0) _usbScanTicks--;
        else if (!mustPoll && _ticksSinceScan < SafetyScanTicks) return;

        _ticksSinceScan = 0;
        await RefreshDevicesAsync();
    }

    async Task RefreshDevicesAsync()
    {
        if (_refreshingDevices || _session.IsRunning || !Engine.IsAvailable) return;
        _refreshingDevices = true;
        try
        {
            var devices = await Adb.GetDevicesAsync() ?? Array.Empty<Device>();
            if (_closed || _session.IsRunning) return;

            var hadReady = Selected?.IsReady == true;
            _devices = devices;
            UpdateDeviceList();
            UpdateView();

            if (Selected?.IsReady == true)
            {
                if (_wantMirror) BeginMirror();
                else if (!hadReady) _ = RefreshBatteryAsync();
            }
        }
        finally
        {
            _refreshingDevices = false;
        }
    }

    void UpdateDeviceList()
    {
        var serials = _devices.Select(d => d.Serial).ToArray();
        if (!_deviceList.Items.Cast<string>().SequenceEqual(serials))
        {
            _deviceList.Items.Clear();
            foreach (var serial in serials) _deviceList.Items.Add(serial);
        }

        var selected = Selected;
        if (selected is not null)
        {
            _selectedSerial = selected.Serial;
            _deviceList.SelectedItem = selected.Serial;
        }

        _deviceList.Visibility = serials.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task RefreshBatteryAsync()
    {
        if (_refreshingBattery) return;
        var device = Selected;
        if (device is not { IsReady: true })
        {
            ShowBattery(null);
            return;
        }

        _refreshingBattery = true;
        try
        {
            var battery = await Adb.GetBatteryAsync(device.Serial);
            if (_closed) return;
            ShowBattery(battery);
            await RecoverIfDeviceLostAsync(device.Serial, battery is not null);
        }
        finally
        {
            _refreshingBattery = false;
        }
    }

    /// <summary>
    /// Telefon gittigi halde motor kapanmazsa oturumu kopmus sayar ve yeniden baglanma
    /// akisina sokar. Yalnizca pil okumasi basarisizsa ek adb cagrisi yapar.
    /// </summary>
    async Task RecoverIfDeviceLostAsync(string serial, bool batteryRead)
    {
        if (batteryRead || !_session.IsRunning)
        {
            _missedDeviceChecks = 0;
            return;
        }

        var devices = await Adb.GetDevicesAsync();
        var lost = devices is not null && !devices.Any(d => d.Serial == serial && d.IsReady);
        _missedDeviceChecks = lost ? _missedDeviceChecks + 1 : 0;
        if (_missedDeviceChecks >= 2)
        {
            _missedDeviceChecks = 0;
            _session.ForceExit(2);
        }
    }

    static string FormatDecimal(double value, string format = "0.0") =>
        value.ToString(format, CultureInfo.InvariantCulture).Replace('.', ',');

    void ShowBattery(Battery? battery)
    {
        _lastTemperature = battery?.TemperatureC;
        if (battery is null)
        {
            _temperature.Text = "— °C";
            _temperature.Foreground = _muted;
            _battery.Text = "Pil sıcaklığı";
            _delta.Text = "";
            return;
        }

        var temperature = battery.TemperatureC;
        _temperature.Text = $"{FormatDecimal(temperature)} °C";
        _temperature.Foreground = temperature >= 42 ? _hot : temperature >= 38 ? _warn : _good;
        _battery.Text = $"Pil sıcaklığı · %{battery.Level}";
        _delta.Text = _temperatureAtStart is double start && IsMirroring
            ? $"Başlangıçtan beri {FormatDecimal(temperature - start, "+0.0;-0.0;0.0")} °C"
            : temperature >= 42 ? "Yüksek, Serin önerilir" : "";
    }

    void ShowReadout(TextBlock readout, LevelMeter meter, double db)
    {
        string text;
        Brush color;
        if (Gain.IsMuted(db))
        {
            text = "Kapalı";
            color = _muted;
        }
        else if (meter.HeldDb is double held)
        {
            text = $"{FormatDecimal(held)} dB";
            color = held > 0 ? _hot : _text;
        }
        else
        {
            text = NoSignalText;
            color = _muted;
        }

        if (readout.Text != text) readout.Text = text;
        if (!ReferenceEquals(readout.Foreground, color)) readout.Foreground = color;
    }

    async void OnTestClicked(object sender, RoutedEventArgs e)
    {
        if (_audioTest is not null)
        {
            await StopAudioTestAsync();
            return;
        }
        if (IsMirroring || Selected is not { IsReady: true } device) return;

        var test = new AudioTest(device.Serial, SelectedPcMicrophoneId, PhoneMicrophoneSource);
        _audioTest = test;
        _audioTestStartedAt = Stopwatch.GetTimestamp();
        _notice = "Ses testi açık. Oyunda ses çıkarın ve mikrofona konuşun.";
        _meterTimer.Start();
        UpdateView();

        try
        {
            await test.StartAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            _notice = $"Ses testi başlatılamadı: {ex.Message}";
            await StopAudioTestAsync();
        }
    }

    void OnMeterTick()
    {
        var test = _audioTest;
        var passthrough = _passthrough;
        if (test is null && passthrough is null) return;

        if (test is not null && Stopwatch.GetElapsedTime(_audioTestStartedAt) >= AudioTestLimit)
        {
            _notice = "Ses testi süre dolduğu için kapatıldı.";
            _ = StopAudioTestAsync();
            return;
        }

        var game = test?.Game ?? passthrough?.Game;
        var microphone = test?.Microphone ?? passthrough?.Microphone;
        var gameDb = (double)_gameDb.Value;
        var microphoneDb = (double)_microphoneDb.Value;
        _gameLevel.Update((game?.TakePeak() ?? 0) * Gain.ToLinear(gameDb));
        _microphoneLevel.Update((microphone?.TakePeak() ?? 0) * Gain.ToLinear(microphoneDb));
        ShowReadout(_gameReadout, _gameLevel, gameDb);
        ShowReadout(_microphoneReadout, _microphoneLevel, microphoneDb);
    }

    void ResetMeters()
    {
        _meterTimer.Stop();
        _gameLevel.Reset();
        _microphoneLevel.Reset();
        _gameReadout.Text = _microphoneReadout.Text = NoSignalText;
        _gameReadout.Foreground = _microphoneReadout.Foreground = _muted;
    }

    async Task StartPassthroughAsync(string serial)
    {
        if (_passthrough is not null) return;
        try
        {
            var passthrough = new AudioPassthrough(serial,
                _windowGameAudio.IsChecked == true, _windowMicrophone.IsChecked == true, SelectedPcMicrophoneId, PhoneMicrophoneSource,
                (double)_gameDb.Value, (double)_microphoneDb.Value, _microphonePath);
            passthrough.MicrophoneFailed += error => PostToUi(() =>
            {
                _notice = $"Mikrofon açılamadı ({error ?? "bilinmeyen hata"}). Görüntü sürüyor.";
                UpdateView();
            });
            _passthrough = passthrough;
            _meterTimer.Start();
            await passthrough.StartAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            _notice = $"Ses başlatılamadı, görüntü sürüyor: {ex.Message}";
            UpdateView();
        }
    }

    Task StopPassthroughAsync()
    {
        if (_passthrough is not { } passthrough) return Task.CompletedTask;
        return _passthroughStopping ??= FinishAsync();

        async Task FinishAsync()
        {
            ResetMeters();
            try
            {
                await passthrough.StopAsync();
            }
            finally
            {
                if (ReferenceEquals(_passthrough, passthrough)) _passthrough = null;
                _passthroughStopping = null;
            }
        }
    }

    async Task StopAudioTestAsync()
    {
        var test = _audioTest;
        if (test is null) return;

        _audioTest = null;
        _stoppingAudioTest = true;
        ResetMeters();
        UpdateView();

        await test.StopAsync();

        _stoppingAudioTest = false;
        if (_notice?.StartsWith("Ses testi açık", StringComparison.Ordinal) == true) _notice = null;
        UpdateView();
    }

    void StopMicrophones()
    {
        _phoneMicrophone.Stop();
        _pcMicrophone.Stop();
    }

    async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (IsMirroring)
        {
            _wantMirror = false;
            _start.IsEnabled = false;
            await StopLiveAsync();
            await StopPassthroughAsync();
            await Task.Run(() =>
            {
                _session.Stop();
                StopMicrophones();
            });
            _notice = RecordingNotice();
            UpdateView();
            return;
        }

        if (IsLiveMode && ValidateLive() is { } problem)
        {
            _notice = problem;
            UpdateView();
            return;
        }

        await StopAudioTestAsync();
        SaveSettings();

        _liveError = null;
        _liveReachedServer = false;

        _wantMirror = true;
        _retries = 0;
        _temperatureAtStart = _lastTemperature;
        BeginMirror();
    }

    void BeginMirror()
    {
        // Onceki yayin hatti kapanmadan yenisi acilmaz.
        if (_live is not null || Selected is not { IsReady: true } device) return;
        try
        {
            _notice = null;
            _startedAt = Stopwatch.GetTimestamp();
            _recordingPath = null;
            _microphonePath = null;
            _sessionWasLive = IsLiveMode;
            _sessionIsWindow = IsWindowMode;

            if (IsWindowMode)
            {
                if (_windowRecord.IsChecked == true)
                {
                    Directory.CreateDirectory(RecordingsFolder);
                    _recordingPath = Path.Combine(RecordingsFolder, $"Tenaire_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
                    if (_windowMicrophone.IsChecked == true) _microphonePath = Merger.MicrophonePathFor(_recordingPath);
                }

                // Ses yolu goruntu gecikmesi kadar geciktigi icin goruntu de hizalanir.
                _session.Start(MirrorArguments.BuildWindow(device.Serial, CurrentProfile, WindowVideoBufferMs,
                    _recordingPath, _windowGameAudio.IsChecked == true));
                UpdateView();
                return;
            }

            if (IsLiveMode)
            {
                _liveElapsed = TimeSpan.Zero;
                var live = new LiveStream(BuildLiveSettings());
                live.Exited += (code, error) => PostToUi(() => OnLiveExited(live, code, error));
                live.Progress += elapsed => PostToUi(() => OnLiveProgress(live, elapsed));
                _live = live;
                live.Start();
                _session.Start(MirrorArguments.BuildLive(device.Serial, CurrentProfile, live.VideoInputPath, _liveGameAudio.IsChecked == true));
                UpdateView();
                return;
            }

            if (IsRecordOn)
            {
                // Yeniden baglanmada eski kaydin ustune yazilmaz, her oturum ayri dosyadir.
                Directory.CreateDirectory(RecordingsFolder);
                var name = $"Tenaire_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
                _recordingPath = Path.Combine(RecordingsFolder, $"{name}.mp4");
                if (_microphone.IsChecked == true) _microphonePath = Merger.MicrophonePathFor(_recordingPath);
            }
            _session.Start(MirrorArguments.Build(device.Serial, CurrentProfile, _recordingPath, _gameAudio.IsChecked == true));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            _wantMirror = false;
            _notice = $"Yansıtma motoru açılamadı: {ex.Message}";
            _ = StopLiveAsync();
        }
        UpdateView();
    }

    void StartMicrophone()
    {
        if (!_session.IsRunning || _phoneMicrophone.IsRunning || _pcMicrophone.IsRunning) return;
        if (Selected is not { IsReady: true } device) return;

        if (_sessionIsWindow)
        {
            _ = StartPassthroughAsync(device.Serial);
            return;
        }

        if (_live is null && _microphonePath is null) return;
        try
        {
            if (_live is not null)
            {
                // Bilgisayar mikrofonunu yayin hatti kendisi okur.
                if (_live.MicrophoneInputPath is { } pipe)
                    _phoneMicrophone.Start(MirrorArguments.BuildAudioProbe(device.Serial, PhoneMicrophoneSource, pipe));
                return;
            }

            if (SelectedPcMicrophoneId is { } deviceId)
                _pcMicrophone.Start(PcMicrophone.RecordArguments(deviceId, _microphonePath!), captureOutput: false);
            else
                _phoneMicrophone.Start(MirrorArguments.BuildMicrophone(device.Serial, _microphonePath!, PhoneMicrophoneSource));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _notice = $"Mikrofon kaydı başlatılamadı: {ex.Message}";
            UpdateView();
        }
    }

    void OnMicrophoneExited(int exitCode, string? error)
    {
        // Mikrofon durdu diye goruntu kaydi kesilmez, yalnizca kullanici bilgilendirilir.
        if (!_wantMirror || !_session.IsRunning) return;
        var running = _live is not null ? "Yayın" : "Görüntü kaydı";
        _notice = $"Mikrofon durdu ({error ?? $"çıkış kodu {exitCode}"}). {running} sürüyor.";
        UpdateView();
    }

    async void OnSessionExited(int exitCode, string? error)
    {
        // Bildirim mikrofon dosyasinin durumunu yazacagi icin once kapanmasi beklenir.
        await Task.Run(StopMicrophones);
        await StopLiveAsync();
        await StopPassthroughAsync();
        if (_closed) return;

        if (!_wantMirror)
        {
            UpdateView();
            return;
        }

        if (exitCode == 0)
        {
            _wantMirror = false;
            _notice = RecordingNotice();
        }
        else
        {
            if (Stopwatch.GetElapsedTime(_startedAt) >= StableRun) _retries = 0;

            // Kod 2: calisirken baglanti koptu. Sonraki cihaz taramasi yeniden baslatir ve
            // tarama araligi denemeler arasinda dogal bir bekleme saglar.
            if ((exitCode == 2 || _retries > 0) && _retries < MaxRetries)
            {
                _retries++;
                _notice = _liveError is null
                    ? $"Bağlantı koptu, yeniden bağlanılıyor ({_retries}/{MaxRetries}). Kabloyu kontrol edin."
                    : $"Yayın koptu, yeniden bağlanılıyor ({_retries}/{MaxRetries}). {_liveError}";
                _liveError = null;
            }
            else
            {
                _wantMirror = false;
                _notice = $"{(_sessionWasLive ? "Yayın" : "Yansıtma")} başlatılamadı: {_liveError ?? error ?? $"çıkış kodu {exitCode}"}";
            }
        }
        UpdateView();
    }

    static string? NewestMergeableVideoName()
    {
        if (!Directory.Exists(RecordingsFolder)) return null;
        return new DirectoryInfo(RecordingsFolder).EnumerateFiles("*.mp4")
            .Where(f => HasRecordedData(Merger.MicrophonePathFor(f.FullName)))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.Name;
    }

    async void OnMergeClicked(object sender, RoutedEventArgs e)
    {
        if (IsMerging || IsMirroring) return;

        Directory.CreateDirectory(RecordingsFolder);
        var dialog = new OpenFileDialog
        {
            Title = "Birleştirilecek videoyu seçin",
            Filter = "Video (*.mp4)|*.mp4",
            InitialDirectory = RecordingsFolder,
            FileName = NewestMergeableVideoName() ?? "",
        };
        if (dialog.ShowDialog(this) != true) return;
        var videoPath = dialog.FileName;

        if (!HasRecordedData(Merger.MicrophonePathFor(videoPath)))
        {
            _notice = $"Bu videonun kullanılabilir mikrofon kaydı yok: {Path.GetFileName(videoPath)}";
            UpdateView();
            return;
        }

        SaveSettings();
        var cancellation = new CancellationTokenSource();
        _mergeCancellation = cancellation;
        _notice = null;
        UpdateView();

        try
        {
            var levels = new MergeLevels((double)_gameDb.Value, (double)_microphoneDb.Value, (int)_delay.Value);
            var progress = new Progress<int>(percent => _mergeText.Text = $"Birleştiriliyor %{percent}");
            var result = await Merger.RunAsync(videoPath, levels, progress, cancellation.Token);
            _notice = result.Success
                ? $"Birleştirildi: {Path.GetFileName(result.Detail)}"
                : $"Birleştirilemedi: {result.Detail}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            _notice = $"Birleştirilemedi: {ex.Message}";
        }
        finally
        {
            _mergeCancellation = null;
            cancellation.Dispose();
        }

        if (_closed) return;
        _mergeText.Text = MergeLabel;
        UpdateView();
    }

    void UpdateView()
    {
        var device = Selected;
        var mirroring = IsMirroring;
        var running = _session.IsRunning;
        var testing = _audioTest is not null;

        if (!Engine.IsAvailable)
        {
            SetDevice(_hot, "Motor bulunamadı", $"engine klasöründe scrcpy.exe ve adb.exe olmalı: {Engine.Directory}");
        }
        else if (device is null)
        {
            SetDevice(_idle, "Telefon bağlı değil",
                "USB kablosunu takın ve telefonda Geliştirici seçeneklerinden USB hata ayıklamayı açın.");
        }
        else if (device.State == "unauthorized")
        {
            SetDevice(_warn, "Onay bekleniyor", "Telefonda çıkan \"USB hata ayıklamaya izin ver\" penceresini onaylayın.");
        }
        else if (!device.IsReady)
        {
            SetDevice(_warn, "Bağlantı kuruluyor", $"Telefon durumu: {device.State}");
        }
        else if (running)
        {
            if (_sessionIsWindow)
            {
                var action = _recordingPath is null ? "Yansıtılıyor" : "Yansıtılıyor ve kaydediliyor";
                SetDevice(_recordingPath is null ? _accent : _hot, device.Model,
                    $"{action}. Yayın programında \"{MirrorArguments.CaptureWindowTitle}\" penceresini seçin.");
            }
            else if (_sessionWasLive)
            {
                SetDevice(_hot, device.Model, _liveElapsed >= LiveConfirmedAfter
                    ? $"Yayında · {_liveElapsed:hh\\:mm\\:ss}. Telefonda oynamaya devam edebilirsiniz."
                    : "Yayın başlatılıyor, sunucuya bağlanılıyor.");
            }
            else
            {
                var recording = _recordingPath is not null;
                SetDevice(recording ? _hot : _accent, device.Model,
                    $"{(recording ? "Kaydediliyor" : "Yansıtılıyor")}: {CurrentProfile.Title} profili. Telefonda oynamaya devam edebilirsiniz.");
            }
        }
        else
        {
            SetDevice(_good, device.Model, "Hazır.");
        }
        if (_notice is not null) _status.Text = _notice;

        _startText.Text = mirroring ? "Durdur"
            : IsLiveMode ? "Yayına Başla"
            : IsWindowMode || !IsRecordOn ? "Yansıtmayı Başlat"
            : "Kaydı Başlat";
        _tip.Text = IsLiveMode ? "Önce oyunu açın, sonra yayını başlatın."
            : IsWindowMode ? "Önce oyunu açın, sonra yansıtmayı başlatın."
            : "Önce oyunu açın, sonra kaydı başlatın.";
        _start.Style = (Style)FindResource(mirroring ? "ActionButton" : "RecordButton");
        _startDot.Visibility = mirroring ? Visibility.Collapsed : Visibility.Visible;
        _startSquare.Visibility = mirroring ? Visibility.Visible : Visibility.Collapsed;
        _start.IsEnabled = Engine.IsAvailable && !_stoppingAudioTest && !IsMerging && (mirroring || device?.IsReady == true);

        _test.Content = testing ? "Testi Durdur" : "Sesi Test Et";
        _test.IsEnabled = Engine.IsAvailable && !mirroring && !_stoppingAudioTest && (testing || device?.IsReady == true);

        _merge.IsEnabled = Merger.IsAvailable && !mirroring && !IsMerging && !testing;

        _profileHost.IsEnabled = !mirroring;
        _record.IsEnabled = !mirroring;
        _gameAudio.IsEnabled = !mirroring && IsRecordOn;
        _microphone.IsEnabled = !mirroring && IsRecordOn;
        _deviceList.IsEnabled = !mirroring;
        _microphoneSource.IsEnabled = !mirroring && !testing;
        _echoCancellation.IsEnabled = !mirroring && !testing && SelectedPcMicrophoneId is null;
        _modeHost.IsEnabled = !mirroring;
        _livePanel.IsEnabled = !mirroring;
        _windowPanel.IsEnabled = !mirroring;

        var batteryInterval = mirroring ? RecordingBatteryInterval : IdleBatteryInterval;
        if (_batteryTimer.Interval != batteryInterval) _batteryTimer.Interval = batteryInterval;
    }

    void SetDevice(Brush dot, string name, string status)
    {
        _dot.Fill = dot;
        _device.Text = name;
        _status.Text = status;
    }
}
