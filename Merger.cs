using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Tenaire;

sealed record MergeLevels(double GameDb, double MicrophoneDb, int MicrophoneDelayMs);

/// <summary>Ses kazanci dB olarak tutulur, en alt deger kaynagi tamamen kapatir.</summary>
static class Gain
{
    public const int MinDb = -40;
    public const int MaxDb = 20;

    /// <summary>Telefonun yanki engelleyicisi acilirken mikrofonda kisa, guclu bir patlama olusuyor.</summary>
    public const double MicrophoneSettleSeconds = 2.5;

    public static string MicrophoneSettleFilter =>
        "volume=0:enable='lt(t," + MicrophoneSettleSeconds.ToString("0.0", CultureInfo.InvariantCulture) + ")'";

    public static bool IsMuted(double db) => db <= MinDb;

    public static double ToLinear(double db) => IsMuted(db) ? 0 : Math.Pow(10, db / 20);

    public static string ToFilterValue(double db) =>
        IsMuted(db) ? "0" : db.ToString("0.0", CultureInfo.InvariantCulture) + "dB";
}

/// <param name="Detail">Basarida cikti dosyasinin yolu, hatada aciklama.</param>
sealed record MergeResult(bool Success, string Detail);

/// <summary>Mikrofon kaydini videoya karistirir. Goruntu yeniden kodlanmaz, kalite kaybi olmaz.</summary>
static class Merger
{
    const string MicrophoneSuffix = "_mikrofon.m4a";

    public static bool IsAvailable => File.Exists(Engine.FfmpegPath);

    public static string MicrophonePathFor(string videoPath) => Path.Combine(
        Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath) + MicrophoneSuffix);

    /// <summary>Var olan bir dosyanin ustune hic yazilmaz.</summary>
    static string NextOutputPath(string videoPath)
    {
        var stem = Path.Combine(Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath));
        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? $"{stem}_birlesik.mp4" : $"{stem}_birlesik_{i}.mp4";
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static string BuildFilter(bool hasGameAudio, MergeLevels levels)
    {
        // Sinirlayici, seviyeler yukseltilince sesin patlamasini onler.
        const string limiter = "alimiter=limit=0.97:level=false";
        var microphone = $"[1:a:0]{Gain.MicrophoneSettleFilter},adelay=delays={levels.MicrophoneDelayMs}:all=1,volume={Gain.ToFilterValue(levels.MicrophoneDb)}";
        return hasGameAudio
            ? $"[0:a:0]volume={Gain.ToFilterValue(levels.GameDb)}[game];{microphone}[mic];" +
              $"[game][mic]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,{limiter}[out]"
            : $"{microphone},{limiter}[out]";
    }

    static ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(Engine.FfmpegPath)
        {
            WorkingDirectory = Path.GetDirectoryName(Engine.FfmpegPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    static async Task<(TimeSpan Duration, bool HasAudio)> ProbeAsync(string videoPath)
    {
        using var process = Process.Start(CreateStartInfo(new[] { "-hide_banner", "-nostdin", "-i", videoPath }))!;
        process.OutputDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        var report = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var match = Regex.Match(report, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        var duration = match.Success
            ? TimeSpan.FromHours(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
              + TimeSpan.FromMinutes(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
              + TimeSpan.FromSeconds(double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture))
            : TimeSpan.Zero;
        return (duration, Regex.IsMatch(report, @"Stream #0:\d+.*Audio:"));
    }

    public static async Task<MergeResult> RunAsync(
        string videoPath, MergeLevels levels, IProgress<int>? progress, CancellationToken token)
    {
        var (duration, hasGameAudio) = await ProbeAsync(videoPath);
        var output = NextOutputPath(videoPath);
        var partial = Path.ChangeExtension(output, ".part.mp4");

        var info = CreateStartInfo(new[]
        {
            "-hide_banner", "-nostdin", "-y", "-nostats", "-progress", "pipe:1",
            "-i", videoPath,
            "-i", MicrophonePathFor(videoPath),
            "-filter_complex", BuildFilter(hasGameAudio, levels),
            "-map", "0:v:0", "-map", "[out]",
            "-c:v", "copy",
            "-c:a", "aac", "-b:a", "192k",
            "-shortest",
            "-movflags", "+faststart",
            partial,
        });

        var errors = new Queue<string>();
        using var process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errors)
            {
                errors.Enqueue(e.Data);
                if (errors.Count > 2) errors.Dequeue();
            }
        };

        process.Start();
        // Oyun veya baska isler aksamasin diye birlestirme dusuk oncelikte calisir.
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (InvalidOperationException) { /* cok kisa surdu ve kapandi */ }
        process.BeginErrorReadLine();

        using var cancellation = token.Register(() =>
        {
            try { process.Kill(); }
            catch (InvalidOperationException) { /* zaten kapanmis */ }
        });

        const string progressKey = "out_time_us=";
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith(progressKey, StringComparison.Ordinal) || duration <= TimeSpan.Zero) continue;
            if (!long.TryParse(line.AsSpan(progressKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)) continue;
            progress?.Report(Math.Clamp((int)(microseconds / 10.0 / duration.TotalMilliseconds / 100.0 * 100), 0, 99));
        }
        await process.WaitForExitAsync();

        if (process.ExitCode == 0 && !token.IsCancellationRequested)
        {
            File.Move(partial, output);
            return new MergeResult(true, output);
        }

        TryDelete(partial);
        string detail;
        lock (errors) detail = string.Join(" ", errors);
        return new MergeResult(false, token.IsCancellationRequested ? "İptal edildi." : detail);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* sonraki denemede ustune yazilir */ }
    }
}
