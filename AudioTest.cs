using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Tenaire;

delegate void SampleHandler(Span<short> samples);

/// <summary>Ham ses akisindan anlik tepe seviyesini cikarir.</summary>
abstract class LevelProbe
{
    /// <summary>Doluysa her ses parcasi seviye olculdukten sonra buraya da verilir.</summary>
    public SampleHandler? SampleSink { get; set; }

    readonly TaskCompletionSource _firstData = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _peak;

    /// <summary>Ses gelene kadar (veya sure dolana kadar) bekler.</summary>
    public Task WaitForDataAsync(TimeSpan timeout) => Task.WhenAny(_firstData.Task, Task.Delay(timeout));

    /// <summary>Son cagridan beri gorulen en yuksek seviye (0..1).</summary>
    public double TakePeak() => Interlocked.Exchange(ref _peak, 0) / 32768.0;

    protected bool HasData => _firstData.Task.IsCompleted;

    public abstract Task StopAsync();

    protected async Task PumpAsync(Stream stream, int bytesToSkip, CancellationToken token)
    {
        try
        {
            var buffer = new byte[4096];
            var carry = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(carry), token).ConfigureAwait(false);
                if (read == 0) break;

                var count = carry + read;
                var start = Math.Min(bytesToSkip, count);
                bytesToSkip -= start;
                var end = start + ((count - start) & ~1);
                if (end > start)
                {
                    var samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(start, end - start));
                    RecordPeak(samples);
                    SampleSink?.Invoke(samples);
                    _firstData.TrySetResult();
                }

                carry = count - end;
                if (carry > 0) Buffer.BlockCopy(buffer, end, buffer, 0, carry);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Test durduruldu veya kaynak kapandi.
        }
    }

    void RecordPeak(ReadOnlySpan<short> samples)
    {
        var peak = 0;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs((int)sample);
            if (magnitude > peak) peak = magnitude;
        }

        int current;
        do
        {
            current = Volatile.Read(ref _peak);
            if (peak <= current) return;
        } while (Interlocked.CompareExchange(ref _peak, peak, current) != current);
    }
}

/// <summary>Telefondaki bir ses kaynagini dosyaya yazmadan dinler.</summary>
sealed class PhoneLevelProbe : LevelProbe
{
    // WAV basligini ornek sanip cubugu ziplatmamak icin ilk ~20 ms atlanir.
    const int HeaderBytes = 4096;

    // Telefonda birden cok motor pes pese acilinca sunucu dosyasi cakisabiliyor.
    const int MaxStartAttempts = 4;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1.5);

    readonly MirrorSession _session = new();
    readonly CancellationTokenSource _stop = new();
    readonly NamedPipeServerStream _pipe;
    readonly string _pipePath;
    IReadOnlyList<string>? _arguments;
    Task _reader = Task.CompletedTask;
    int _attempts;
    volatile bool _stopping;

    /// <summary>Tum denemelere ragmen ses alinamazsa, son hatayla calisir.</summary>
    public event Action<string?>? Failed;

    public PhoneLevelProbe(string label)
    {
        var name = $"tenaire_{label}_{Environment.ProcessId}";
        _pipePath = $@"\\.\pipe\{name}";
        _pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _session.Exited += OnSessionExited;
    }

    public void Start(string serial, string audioSource)
    {
        _reader = ReadAsync(_stop.Token);
        _arguments = MirrorArguments.BuildAudioProbe(serial, audioSource, _pipePath);
        _session.Start(_arguments);
    }

    void OnSessionExited(int exitCode, string? error)
    {
        if (_stopping || HasData) return;

        if (++_attempts >= MaxStartAttempts)
        {
            Failed?.Invoke(error);
            return;
        }

        _ = Task.Delay(RetryDelay).ContinueWith(_ =>
        {
            if (_stopping) return;
            try
            {
                _session.Start(_arguments!);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Failed?.Invoke(ex.Message);
            }
        }, TaskScheduler.Default);
    }

    async Task ReadAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                return;
            }

            await PumpAsync(_pipe, HeaderBytes, token).ConfigureAwait(false);
            if (HasData) return;

            // Hic ses gelmeden kopan motor icin boru yeni denemeye hazirlanir.
            try
            {
                _pipe.Disconnect();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    public override async Task StopAsync()
    {
        _stopping = true;
        await Task.Run(_session.Stop).ConfigureAwait(false);
        _stop.Cancel();
        await _reader.ConfigureAwait(false);
        _pipe.Dispose();
        _stop.Dispose();
    }
}

/// <summary>Bilgisayardaki bir mikrofonu dosyaya yazmadan dinler.</summary>
sealed class PcLevelProbe : LevelProbe
{
    readonly FfmpegProcess _capture = new();
    readonly CancellationTokenSource _stop = new();
    Task _reader = Task.CompletedTask;

    public void Start(string deviceId)
    {
        _capture.Start(PcMicrophone.ProbeArguments(deviceId), captureOutput: true);
        _reader = PumpAsync(_capture.Output!, 0, _stop.Token);
    }

    public override async Task StopAsync()
    {
        await Task.Run(_capture.Stop).ConfigureAwait(false);
        _stop.Cancel();
        await _reader.ConfigureAwait(false);
        _stop.Dispose();
    }
}

/// <summary>Oyun sesi ve secili mikrofonu birlikte dinleyen test oturumu.</summary>
sealed class AudioTest
{
    static readonly TimeSpan ServerStartLimit = TimeSpan.FromSeconds(8);

    readonly string _serial;
    readonly string? _pcMicrophoneId;
    readonly string _phoneMicrophoneSource;
    readonly PhoneLevelProbe _game = new("game");

    public LevelProbe Game => _game;
    public LevelProbe Microphone { get; }

    /// <param name="pcMicrophoneId">Null ise telefonun mikrofonu dinlenir.</param>
    public AudioTest(string serial, string? pcMicrophoneId, string phoneMicrophoneSource)
    {
        _serial = serial;
        _pcMicrophoneId = pcMicrophoneId;
        _phoneMicrophoneSource = phoneMicrophoneSource;
        Microphone = pcMicrophoneId is null ? new PhoneLevelProbe("mic") : new PcLevelProbe();
    }

    public async Task StartAsync()
    {
        _game.Start(_serial, "playback");
        switch (Microphone)
        {
            case PcLevelProbe pc:
                pc.Start(_pcMicrophoneId!);
                break;
            case PhoneLevelProbe phone:
                // Iki motor ayni anda acilirsa telefondaki sunucu dosyasi cakisir, ilki ses verince ikincisi acilir.
                await _game.WaitForDataAsync(ServerStartLimit);
                phone.Start(_serial, _phoneMicrophoneSource);
                break;
        }
    }

    public Task StopAsync() => Task.WhenAll(Game.StopAsync(), Microphone.StopAsync());
}
