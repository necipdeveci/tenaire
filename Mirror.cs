using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tenaire;

/// <param name="MaxSize">En uzun kenar (piksel). 0: telefonun kendi cozunurlugu.</param>
sealed record Profile(string Key, string Title, int MaxSize, int MaxFps, int BitRateMbps, string Hint)
{
    public string Summary => $"{(MaxSize == 0 ? "Tam" : MaxSize)} · {MaxFps} fps";
}

static class Profiles
{
    public static readonly Profile[] All =
    {
        new("cool", "Serin", 1280, 30, 8,
            "En az ek ısınma. Uzun oyun seansları için."),
        new("balanced", "Dengeli", 1920, 60, 16,
            "Net ve akıcı kayıt, makul ek yük. Önerilen ayar."),
        new("quality", "Kalite", 0, 60, 30,
            "Telefonun tam çözünürlüğü. En çok ısınan seçenek."),
    };

    public static Profile Default => All[1];
    public static Profile Find(string? key) => All.FirstOrDefault(p => p.Key == key) ?? Default;
}

static class MirrorArguments
{
    /// <param name="recordPath">Null ise yalnizca yansitilir, kayit yapilmaz.</param>
    /// <param name="gameAudio">Yalnizca kayitla birlikte gecerlidir.</param>
    /// <summary>
    /// Duz "mic" telefon hoparlorundeki oyun sesini de alir, oyun iki kez duyulur ve dalgalanir.
    /// Gorusme modu telefonun yanki engelleyicisini devreye sokar.
    /// </summary>
    public static string PhoneMicrophoneSource(bool echoCancellation) =>
        echoCancellation ? "mic-voice-communication" : "mic";

    /// <summary>Mikrofon icin ayri, penceresiz motor sureci.</summary>
    public static IReadOnlyList<string> BuildMicrophone(string serial, string recordPath, string audioSource) => new[]
    {
        $"--serial={serial}",
        "--no-control",
        "--no-video",
        "--no-window",
        $"--audio-source={audioSource}",
        "--audio-codec=aac",
        // Konusma icin yeterli, oyun sesinin 128K'sina gerek yok.
        "--audio-bit-rate=96K",
        "--no-audio-playback",
        "--record-format=m4a",
        $"--record={recordPath}",
    };

    /// <summary>Yayin programinin yakalayacagi pencere adi.</summary>
    public const string CaptureWindowTitle = "Tenaire Yansıtma";

    /// <summary>Yansitma penceresi: ses ayri yoldan bilgisayarda calar, istenirse goruntu ve oyun sesi kaydedilir.</summary>
    /// <param name="recordPath">Null ise kayit yapilmaz.</param>
    public static IReadOnlyList<string> BuildWindow(string serial, Profile profile, int videoBufferMs, string? recordPath, bool recordGameAudio)
    {
        var args = new List<string>
        {
            $"--serial={serial}",
            "--no-control",
            "--video-codec=h264",
            $"--video-bit-rate={profile.BitRateMbps}M",
            $"--max-fps={profile.MaxFps}",
            $"--window-title={CaptureWindowTitle}",
        };
        if (profile.MaxSize > 0) args.Add($"--max-size={profile.MaxSize}");
        if (videoBufferMs > 0) args.Add($"--video-buffer={videoBufferMs}");

        if (recordPath is not null) args.Add($"--record={recordPath}");
        if (recordPath is not null && recordGameAudio)
        {
            args.Add("--audio-source=playback");
            args.Add("--audio-dup");
            args.Add("--audio-codec=aac");
            args.Add("--no-audio-playback");
        }
        else
        {
            args.Add("--no-audio");
        }
        return args;
    }

    /// <summary>Canli yayin: goruntu ve oyun sesi dosya yerine yayin hattina akar.</summary>
    public static IReadOnlyList<string> BuildLive(string serial, Profile profile, string pipePath, bool gameAudio)
    {
        var args = new List<string>
        {
            $"--serial={serial}",
            "--no-control",
            "--video-codec=h264",
            $"--video-bit-rate={profile.BitRateMbps}M",
            $"--max-fps={profile.MaxFps}",
            "--window-title=Tenaire",
            "--record-format=mkv",
            $"--record={pipePath}",
        };
        if (profile.MaxSize > 0) args.Add($"--max-size={profile.MaxSize}");

        if (gameAudio)
        {
            args.Add("--audio-source=playback");
            args.Add("--audio-dup");
            args.Add("--audio-codec=aac");
            args.Add("--audio-bit-rate=192K");
            args.Add("--no-audio-playback");
        }
        else
        {
            args.Add("--no-audio");
        }
        return args;
    }

    /// <summary>Ses testi: kodlama yapilmaz, ham ses dosya yerine uygulamaya akar.</summary>
    public static IReadOnlyList<string> BuildAudioProbe(string serial, string audioSource, string pipePath)
    {
        var args = new List<string>
        {
            $"--serial={serial}",
            "--no-control",
            "--no-video",
            "--no-window",
            $"--audio-source={audioSource}",
            "--audio-codec=raw",
            "--no-audio-playback",
            "--record-format=wav",
            $"--record={pipePath}",
        };
        if (audioSource == "playback") args.Add("--audio-dup");
        return args;
    }

    public static IReadOnlyList<string> Build(string serial, Profile profile, string? recordPath, bool gameAudio)
    {
        var args = new List<string>
        {
            $"--serial={serial}",
            // Oyun telefonda oynanir: bilgisayardan telefona hicbir komut gitmez.
            "--no-control",
            // USB'de bant genisligi bol: H.264 yuksek bit hiziyla hem en uyumlu hem en gecikmesiz secenek.
            "--video-codec=h264",
            $"--video-bit-rate={profile.BitRateMbps}M",
            $"--max-fps={profile.MaxFps}",
            "--window-title=Tenaire",
        };
        if (profile.MaxSize > 0) args.Add($"--max-size={profile.MaxSize}");

        if (recordPath is not null) args.Add($"--record={recordPath}");

        if (recordPath is not null && gameAudio)
        {
            // Ses telefonda calmaya devam eder, bilgisayarda yankilanmaz, yalnizca kayda yazilir.
            args.Add("--audio-source=playback");
            args.Add("--audio-dup");
            args.Add("--audio-codec=aac");
            args.Add("--no-audio-playback");
        }
        else
        {
            args.Add("--no-audio");
        }
        return args;
    }
}

/// <summary>Tek bir scrcpy surecini yonetir.</summary>
sealed class MirrorSession
{
    const int NoForcedExit = int.MinValue;

    // scrcpy 5.0.1 baslatma hatasini yazdiktan sonra bazen kapanmadan asili kaliyor.
    const string StartFailureMarker = "Server connection failed";
    static readonly TimeSpan StartFailureGrace = TimeSpan.FromSeconds(3);

    Process? _process;
    volatile string? _lastError;
    volatile int _forcedExitCode = NoForcedExit;

    /// <summary>Surec bitince arka plan is parcaciginda calisir: (cikis kodu, son hata satiri).</summary>
    public event Action<int, string?>? Exited;

    /// <summary>Ilk goruntu karesi cozuldugunde bir kez, arka planda calisir.</summary>
    public event Action? VideoStarted;

    // scrcpy ilk kareyi cozerken doku boyutunu bu kelimeyle yazar. "Recording started" satiri
    // telefondaki sunucu acilmadan geldigi icin baslama isareti olarak kullanilamiyor.
    const string VideoStartedMarker = "Texture";

    volatile bool _videoSignaled;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(IEnumerable<string> arguments)
    {
        _lastError = null;
        _forcedExitCode = NoForcedExit;
        _videoSignaled = false;
        var info = new ProcessStartInfo(Engine.ScrcpyPath)
        {
            WorkingDirectory = Engine.Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["ADB"] = Engine.AdbPath;
        foreach (var arg in arguments) info.ArgumentList.Add(arg);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Capture(process, e.Data);
        process.ErrorDataReceived += (_, e) => Capture(process, e.Data);
        process.Exited += (_, _) =>
        {
            var code = _forcedExitCode != NoForcedExit ? _forcedExitCode : process.ExitCode;
            process.Dispose();
            if (ReferenceEquals(_process, process)) _process = null;
            Exited?.Invoke(code, _lastError);
        };

        _process = process;
        try
        {
            process.Start();
        }
        catch
        {
            _process = null;
            process.Dispose();
            throw;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    void Capture(Process process, string? line)
    {
        if (line is null) return;
        if (!_videoSignaled && line.Contains(VideoStartedMarker, StringComparison.Ordinal))
        {
            _videoSignaled = true;
            VideoStarted?.Invoke();
        }

        const string prefix = "ERROR:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return;

        _lastError ??= line[prefix.Length..].Trim();
        if (line.Contains(StartFailureMarker, StringComparison.Ordinal))
            _ = Task.Delay(StartFailureGrace).ContinueWith(_ => ForceExit(process, 1), TaskScheduler.Default);
    }

    /// <summary>Asili kalan sureci sonlandirir ve verilen cikis koduyla bitmis sayar.</summary>
    public void ForceExit(int exitCode) => ForceExit(_process, exitCode);

    void ForceExit(Process? process, int exitCode)
    {
        if (process is null || !ReferenceEquals(process, _process)) return;
        try
        {
            _forcedExitCode = exitCode;
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Bu arada kendiliginden kapanmis.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    const uint CtrlC = 0;
    static readonly object ConsoleGate = new();

    /// <summary>
    /// Penceresiz surece Ctrl+C gonderip kapanmasini bekler. Uygulamanin kendisi sinyalden
    /// etkilenmesin diye surec bitene kadar sinyal yok sayilir.
    /// </summary>
    static bool InterruptAndWait(Process process, int timeoutMs)
    {
        lock (ConsoleGate)
        {
            if (!AttachConsole((uint)process.Id)) return false;
            try
            {
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                return GenerateConsoleCtrlEvent(CtrlC, 0) && process.WaitForExit(timeoutMs);
            }
            finally
            {
                FreeConsole();
                SetConsoleCtrlHandler(IntPtr.Zero, false);
            }
        }
    }

    /// <summary>
    /// Once temiz cikis ister (pencere varsa kapatarak, yoksa Ctrl+C ile), olmazsa sureci
    /// sonlandirir. Bekleme suresi uzun: MP4/M4A dosyasi ancak motor kendi kapanirsa
    /// oynatilabilir sekilde tamamlanir.
    /// </summary>
    public void Stop()
    {
        var process = _process;
        if (process is null) return;
        try
        {
            process.Refresh();
            const int gracefulMs = 10000;
            if (process.CloseMainWindow() ? process.WaitForExit(gracefulMs) : InterruptAndWait(process, gracefulMs)) return;
            process.Kill();
            process.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Bu arada kendiliginden kapanmis.
        }
    }
}
