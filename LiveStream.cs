using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace Tenaire;

/// <param name="Url">Anahtar dahil tam yayin adresi.</param>
/// <param name="Key">Hata mesajlarindan gizlenmesi icin ayrica tutulur.</param>
/// <param name="PcMicrophoneId">Null ise mikrofon telefondan gelir.</param>
/// <param name="LocalPath">Null degilse yayin bilgisayara da yazilir.</param>
sealed record LiveSettings(
    string Url, string Key, int Width, int Height, int Fps, int BitRateKbps,
    bool GameAudio, double GameDb,
    bool Microphone, string? PcMicrophoneId, double MicrophoneDb, int MicrophoneDelayMs,
    string? LocalPath);

/// <summary>
/// scrcpy ve ffmpeg isimli boruya yalnizca istemci olarak baglanabildigi icin
/// aradaki veriyi uygulama tasir.
/// </summary>
sealed class PipeRelay
{
    const int BufferBytes = 1 << 20;

    readonly NamedPipeServerStream _input, _output;
    readonly CancellationTokenSource _stop = new();
    Task _pump = Task.CompletedTask;

    public string InputPath { get; }
    public string OutputPath { get; }

    public PipeRelay(string label)
    {
        var name = $"tenaire_{label}_{Environment.ProcessId}_{Guid.NewGuid():N}";
        InputPath = $@"\\.\pipe\{name}_in";
        OutputPath = $@"\\.\pipe\{name}_out";
        _input = new NamedPipeServerStream($"{name}_in", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufferBytes, BufferBytes);
        _output = new NamedPipeServerStream($"{name}_out", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufferBytes, BufferBytes);
    }

    public void Start() => _pump = PumpAsync(_stop.Token);

    async Task PumpAsync(CancellationToken token)
    {
        try
        {
            await Task.WhenAll(_input.WaitForConnectionAsync(token), _output.WaitForConnectionAsync(token)).ConfigureAwait(false);
            await _input.CopyToAsync(_output, 1 << 16, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Yayin durduruldu veya karsi taraf kapandi.
        }
        finally
        {
            // Girdi bitince ffmpeg'in dosya sonunu gorebilmesi icin cikis kapatilir.
            _output.Dispose();
        }
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        await _pump.ConfigureAwait(false);
        _input.Dispose();
        _output.Dispose();
        _stop.Dispose();
    }
}

/// <summary>Telefondan gelen goruntu ve sesleri sabit formatli canli yayina cevirir.</summary>
sealed class LiveStream
{
    const int AudioBitRateKbps = 160;

    readonly LiveSettings _settings;
    readonly FfmpegProcess _ffmpeg = new();
    readonly PipeRelay _video = new("video");
    readonly PipeRelay? _phoneMicrophone;
    Task _progressReader = Task.CompletedTask;

    /// <summary>Surec bitince arka planda calisir: (cikis kodu, anahtari gizlenmis hata).</summary>
    public event Action<int, string?>? Exited;

    /// <summary>Yaklasik saniyede bir, yayinlanan sureyle calisir.</summary>
    public event Action<TimeSpan>? Progress;

    public string VideoInputPath => _video.InputPath;

    /// <summary>Mikrofon telefondan gelecekse scrcpy'nin yazacagi boru, degilse null.</summary>
    public string? MicrophoneInputPath => _phoneMicrophone?.InputPath;

    public LiveStream(LiveSettings settings)
    {
        _settings = settings;
        if (settings.Microphone && settings.PcMicrophoneId is null) _phoneMicrophone = new PipeRelay("mic");
    }

    public void Start()
    {
        _video.Start();
        _phoneMicrophone?.Start();
        _ffmpeg.Exited += (code, error) => Exited?.Invoke(code, Hide(error));
        _ffmpeg.Start(BuildArguments(_settings, _video.OutputPath, _phoneMicrophone?.OutputPath), captureOutput: true);
        _progressReader = ReadProgressAsync(_ffmpeg.Output!);
    }

    string? Hide(string? text) =>
        text is null || _settings.Key.Length == 0 ? text : text.Replace(_settings.Key, "***", StringComparison.Ordinal);

    static string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    public static string BuildFilter(LiveSettings settings)
    {
        var video = $"[0:v:0]scale={settings.Width}:{settings.Height}:force_original_aspect_ratio=decrease," +
                    $"pad={settings.Width}:{settings.Height}:(ow-iw)/2:(oh-ih)/2,fps={settings.Fps},format=yuv420p[v]";

        const string limiter = "alimiter=limit=0.97:level=false";
        var hasMicrophone = settings.Microphone;
        var microphoneInput = hasMicrophone ? 1 : -1;
        var game = $"[0:a:0]aresample=async=1000,volume@game={Gain.ToFilterValue(settings.GameDb)}";
        var settle = settings.PcMicrophoneId is null ? Gain.MicrophoneSettleFilter + "," : "";
        var microphone = $"[{microphoneInput}:a:0]{settle}aresample=48000:async=1000,aformat=channel_layouts=stereo," +
                         $"adelay=delays={settings.MicrophoneDelayMs}:all=1,volume@mic={Gain.ToFilterValue(settings.MicrophoneDb)}";

        string audio;
        if (settings.GameAudio && hasMicrophone)
            audio = $"{game}[g];{microphone}[m];[g][m]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,{limiter}[a]";
        else if (settings.GameAudio)
            audio = $"{game},{limiter}[a]";
        else if (hasMicrophone)
            audio = $"{microphone},{limiter}[a]";
        else
            audio = "anullsrc=r=48000:cl=stereo[a]"; // Yayin servisleri ses izi bekler.

        return $"{video};{audio}";
    }

    public static IReadOnlyList<string> BuildArguments(LiveSettings settings, string videoPipe, string? microphonePipe)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats", "-stats_period", "1", "-progress", "pipe:1",
            // Kisa analiz: varsayilan 5 saniyelik bekleme yayinin gec baslamasina neden oluyor.
            "-thread_queue_size", "4096", "-probesize", "1M", "-analyzeduration", "1000000",
            "-f", "matroska", "-i", videoPipe,
        };

        if (settings.Microphone)
        {
            arguments.AddRange(new[] { "-thread_queue_size", "4096" });
            if (settings.PcMicrophoneId is { } deviceId)
                arguments.AddRange(new[] { "-f", "dshow", "-audio_buffer_size", "20", "-i", $"audio={deviceId}" });
            else
                arguments.AddRange(new[] { "-f", "wav", "-i", microphonePipe! });
        }

        var keyframeInterval = (settings.Fps * 2).ToString(CultureInfo.InvariantCulture);
        var bitRate = $"{settings.BitRateKbps}k";
        arguments.AddRange(new[]
        {
            "-filter_complex", BuildFilter(settings),
            "-map", "[v]", "-map", "[a]",
            "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "cbr",
            "-b:v", bitRate, "-maxrate", bitRate, "-bufsize", $"{settings.BitRateKbps * 2}k",
            "-g", keyframeInterval, "-keyint_min", keyframeInterval, "-profile:v", "high", "-bf", "2",
            "-c:a", "aac", "-b:a", $"{AudioBitRateKbps}k", "-ar", "48000", "-ac", "2",
            "-flags", "+global_header",
            "-f", "tee", BuildTeeTargets(settings),
        });
        return arguments;
    }

    static string BuildTeeTargets(LiveSettings settings)
    {
        var targets = $"[f=flv:flvflags=no_duration_filesize]{settings.Url}";
        if (settings.LocalPath is { } path)
        {
            // Parcali MP4: yayin aniden kesilse bile dosya acilabilir kalir.
            // Ters bolu tee icinde kacis karakteri oldugu icin duz boluye cevrilir.
            targets += $"|[f=mp4:movflags=+frag_keyframe+empty_moov+default_base_moof]{path.Replace('\\', '/')}";
        }
        return targets;
    }

    async Task ReadProgressAsync(Stream output)
    {
        try
        {
            using var reader = new StreamReader(output, Encoding.UTF8);
            var elapsed = TimeSpan.Zero;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
                    && microseconds > 0)
                {
                    elapsed = TimeSpan.FromMicroseconds(microseconds);
                }
                else if (line.StartsWith("progress=", StringComparison.Ordinal) && elapsed > TimeSpan.Zero)
                {
                    Progress?.Invoke(elapsed);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // ffmpeg kapandi.
        }
    }

    /// <summary>Yayin sirasinda seviyeyi kesinti olmadan degistirir.</summary>
    public void SetLevels(double gameDb, double microphoneDb)
    {
        if (_settings.GameAudio) _ffmpeg.SendLine($"cvolume@game -1 volume {Gain.ToFilterValue(gameDb)}");
        if (_settings.Microphone) _ffmpeg.SendLine($"cvolume@mic -1 volume {Gain.ToFilterValue(microphoneDb)}");
    }

    /// <summary>Yayini kapatir ve varsa yerel dosyayi tamamlar.</summary>
    public async Task StopAsync()
    {
        await Task.Run(_ffmpeg.Stop).ConfigureAwait(false);
        await _progressReader.ConfigureAwait(false);
        await _video.StopAsync().ConfigureAwait(false);
        if (_phoneMicrophone is not null) await _phoneMicrophone.StopAsync().ConfigureAwait(false);
    }
}
