using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Tenaire;

/// <summary>48 kHz, 16 bit, stereo ham sesi Windows ses cikisina veren oynatici.</summary>
sealed class WaveOutPlayer : IDisposable
{
    const int BytesPerSecond = 48000 * 4;
    const int BufferBytes = BytesPerSecond / 50; // 20 ms
    const int BufferCount = 3;
    const int RingBytes = BytesPerSecond;

    // Ses yaklasik 20 ms'lik parcalarla gelir, duzensizlikleri ortmek icin kisa bir tampon yeter.
    const int PrimeBytes = BytesPerSecond * 60 / 1000;
    const int MaxBacklogBytes = BytesPerSecond * 200 / 1000;
    const int TrimToBytes = BytesPerSecond * 100 / 1000;

    const uint WaveMapper = 0xFFFFFFFF;
    const uint CallbackEvent = 0x00050000;
    const uint WhdrDone = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr handle);

    static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));

    readonly byte[] _ring = new byte[RingBytes];
    readonly object _gate = new();
    readonly AutoResetEvent _bufferDone = new(false);
    readonly IntPtr[] _headers = new IntPtr[BufferCount];
    readonly IntPtr _handle;
    readonly Thread _thread;
    int _readAt, _count;
    bool _priming = true;
    volatile bool _stopping;

    public WaveOutPlayer()
    {
        var format = new WaveFormat
        {
            FormatTag = 1, Channels = 2, SamplesPerSec = 48000, AvgBytesPerSec = BytesPerSecond,
            BlockAlign = 4, BitsPerSample = 16,
        };
        var result = waveOutOpen(out _handle, WaveMapper, ref format, _bufferDone.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent);
        if (result != 0) throw new InvalidOperationException($"Ses çıkışı açılamadı (kod {result}).");

        for (var i = 0; i < BufferCount; i++)
        {
            var header = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { Data = Marshal.AllocHGlobal(BufferBytes), BufferLength = BufferBytes }, header, false);
            waveOutPrepareHeader(_handle, header, HeaderSize);
            _headers[i] = header;
        }

        _thread = new Thread(PlayLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Tenaire ses" };
        _thread.Start();
    }

    /// <summary>Gelen sesi kuyruga ekler. Gecikme buyurse en eski kisim atilir.</summary>
    public void Enqueue(ReadOnlySpan<byte> samples)
    {
        lock (_gate)
        {
            foreach (var value in samples)
            {
                if (_count == RingBytes)
                {
                    _readAt = (_readAt + 1) % RingBytes;
                    _count--;
                }
                _ring[(_readAt + _count) % RingBytes] = value;
                _count++;
            }

            if (_count > MaxBacklogBytes)
            {
                var drop = (_count - TrimToBytes) & ~3;
                _readAt = (_readAt + drop) % RingBytes;
                _count -= drop;
            }
        }
    }

    void Fill(IntPtr target)
    {
        lock (_gate)
        {
            if (_priming && _count >= PrimeBytes) _priming = false;
            if (!_priming && _count < BufferBytes) _priming = true;

            if (_priming)
            {
                Marshal.Copy(new byte[BufferBytes], 0, target, BufferBytes);
                return;
            }

            var first = Math.Min(BufferBytes, RingBytes - _readAt);
            Marshal.Copy(_ring, _readAt, target, first);
            if (first < BufferBytes) Marshal.Copy(_ring, 0, target + first, BufferBytes - first);
            _readAt = (_readAt + BufferBytes) % RingBytes;
            _count -= BufferBytes;
        }
    }

    void Submit(IntPtr header)
    {
        Fill(Marshal.ReadIntPtr(header));
        waveOutWrite(_handle, header, HeaderSize);
    }

    void PlayLoop()
    {
        foreach (var header in _headers) Submit(header);

        while (!_stopping)
        {
            _bufferDone.WaitOne(50);
            foreach (var header in _headers)
            {
                if (_stopping) return;
                if (((uint)Marshal.ReadInt32(header, FlagsOffset) & WhdrDone) != 0) Submit(header);
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(1000);
        waveOutReset(_handle);
        foreach (var header in _headers)
        {
            var data = Marshal.ReadIntPtr(header);
            waveOutUnprepareHeader(_handle, header, HeaderSize);
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(header);
        }
        waveOutClose(_handle);
        _bufferDone.Dispose();
    }
}

/// <summary>Bellekteki ham sesi ffmpeg uzerinden M4A dosyasina yazar.</summary>
sealed class PcmFileWriter
{
    readonly NamedPipeServerStream _pipe;
    readonly FfmpegProcess _encoder = new();
    volatile bool _connected;

    public PcmFileWriter(string path)
    {
        var name = $"tenaire_pcm_{Environment.ProcessId}_{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 20, 1 << 20);
        _ = WaitForEncoderAsync();
        _encoder.Start(new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", $@"\\.\pipe\{name}",
            "-c:a", "aac", "-b:a", "96k", "-y", path,
        }, captureOutput: false);
    }

    async Task WaitForEncoderAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync().ConfigureAwait(false);
            _connected = true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Kayit baslamadan durduruldu.
        }
    }

    /// <summary>Dosyanin basina verilen sure kadar sessizlik ekler.</summary>
    public void WriteSilence(TimeSpan duration)
    {
        var remaining = (int)(duration.TotalSeconds * 48000) * 4;
        var block = new byte[8192];
        while (remaining > 0)
        {
            var count = Math.Min(remaining, block.Length);
            Write(block.AsSpan(0, count));
            remaining -= count;
        }
    }

    public void Write(ReadOnlySpan<byte> samples)
    {
        if (!_connected) return;
        try
        {
            _pipe.Write(samples);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _connected = false;
        }
    }

    /// <summary>Boru kapaninca ffmpeg dosyayi tamamlar.</summary>
    public void Stop()
    {
        _connected = false;
        _pipe.Dispose();
        _encoder.Stop();
    }
}

/// <summary>Oyun sesini ve mikrofonu bilgisayarda calar, istenirse mikrofonu dosyaya da yazar.</summary>
sealed class AudioPassthrough
{
    static readonly TimeSpan ServerStartLimit = TimeSpan.FromSeconds(8);

    // Kayit modunda telefon mikrofonunun goruntuden sonra acilma suresi.
    static readonly TimeSpan PhoneMicrophoneStartDelay = TimeSpan.FromMilliseconds(900);

    readonly string _serial;
    readonly string? _pcMicrophoneId;
    readonly string _phoneMicrophoneSource;
    readonly PhoneLevelProbe? _game;
    readonly WaveOutPlayer? _gamePlayer, _microphonePlayer;
    readonly PcmFileWriter? _microphoneWriter;
    double _gameGain, _microphoneGain;

    public LevelProbe? Game => _game;
    public LevelProbe? Microphone { get; }

    /// <summary>Telefon mikrofonu tum denemelere ragmen acilamazsa calisir.</summary>
    public event Action<string?>? MicrophoneFailed;

    /// <param name="pcMicrophoneId">Null ise telefonun mikrofonu kullanilir.</param>
    /// <param name="microphoneRecordPath">Null degilse mikrofon ham haliyle bu dosyaya da yazilir.</param>
    public AudioPassthrough(string serial, bool game, bool microphone, string? pcMicrophoneId, string phoneMicrophoneSource,
        double gameDb, double microphoneDb, string? microphoneRecordPath)
    {
        _serial = serial;
        _pcMicrophoneId = pcMicrophoneId;
        _phoneMicrophoneSource = phoneMicrophoneSource;
        SetGains(gameDb, microphoneDb);

        if (game)
        {
            _game = new PhoneLevelProbe("playgame");
            _gamePlayer = new WaveOutPlayer();
            _game.SampleSink = samples => Forward(samples, _gamePlayer, Volatile.Read(ref _gameGain));
        }

        if (microphone)
        {
            Microphone = pcMicrophoneId is null ? new PhoneLevelProbe("playmic") : new PcLevelProbe();
            if (Microphone is PhoneLevelProbe phoneProbe) phoneProbe.Failed += error => MicrophoneFailed?.Invoke(error);
            _microphonePlayer = new WaveOutPlayer();
            if (microphoneRecordPath is not null) _microphoneWriter = new PcmFileWriter(microphoneRecordPath);
            var settles = pcMicrophoneId is null;
            var createdAt = Stopwatch.GetTimestamp();
            long firstSampleAt = 0;
            Microphone.SampleSink = samples =>
            {
                if (firstSampleAt == 0)
                {
                    firstSampleAt = Stopwatch.GetTimestamp();

                    // Bu modda telefon mikrofonu oyun sesinden sonra acildigi icin normal kayittan
                    // daha gec baslar. Fark sessizlikle kapatilir, boylece ayni gecikme ayari gecerli olur.
                    var extra = Stopwatch.GetElapsedTime(createdAt) - PhoneMicrophoneStartDelay;
                    if (settles && extra > TimeSpan.Zero) _microphoneWriter?.WriteSilence(extra);
                }

                // Kayit, seviye uygulanmadan yazilir, seviyeler birlestirirken uygulanir.
                _microphoneWriter?.Write(MemoryMarshal.AsBytes(samples));
                var settling = settles && Stopwatch.GetElapsedTime(firstSampleAt).TotalSeconds < Gain.MicrophoneSettleSeconds;
                Forward(samples, _microphonePlayer, settling ? 0 : Volatile.Read(ref _microphoneGain));
            };
        }
    }

    static void Forward(Span<short> samples, WaveOutPlayer player, double gain)
    {
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)Math.Clamp((int)(samples[i] * gain), short.MinValue, short.MaxValue);
        player.Enqueue(MemoryMarshal.AsBytes(samples));
    }

    public void SetGains(double gameDb, double microphoneDb)
    {
        Volatile.Write(ref _gameGain, Gain.ToLinear(gameDb));
        Volatile.Write(ref _microphoneGain, Gain.ToLinear(microphoneDb));
    }

    public async Task StartAsync()
    {
        _game?.Start(_serial, "playback");
        switch (Microphone)
        {
            case PcLevelProbe pc:
                pc.Start(_pcMicrophoneId!);
                break;
            case PhoneLevelProbe phone:
                // Iki motor ayni anda acilirsa telefondaki sunucu dosyasi cakisir.
                if (_game is not null) await _game.WaitForDataAsync(ServerStartLimit);
                phone.Start(_serial, _phoneMicrophoneSource);
                break;
        }
    }

    public async Task StopAsync()
    {
        var stops = new List<Task>();
        if (_game is not null) stops.Add(_game.StopAsync());
        if (Microphone is not null) stops.Add(Microphone.StopAsync());
        await Task.WhenAll(stops).ConfigureAwait(false);
        if (_microphoneWriter is { } writer) await Task.Run(writer.Stop).ConfigureAwait(false);
        _gamePlayer?.Dispose();
        _microphonePlayer?.Dispose();
    }
}
