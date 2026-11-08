using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Tenaire;

sealed record CaptureDevice(string Name, string Id);

/// <summary>Standart girisine 'q' yazilarak temiz kapatilabilen ffmpeg sureci.</summary>
sealed class FfmpegProcess
{
    Process? _process;
    volatile string? _firstError;

    /// <summary>Surec bitince arka planda calisir: (cikis kodu, ilk hata satiri).</summary>
    public event Action<int, string?>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Yalnizca <c>captureOutput</c> ile baslatildiginda dolu olur.</summary>
    public Stream? Output { get; private set; }

    public void Start(IEnumerable<string> arguments, bool captureOutput)
    {
        _firstError = null;
        var info = new ProcessStartInfo(Engine.FfmpegPath)
        {
            WorkingDirectory = Path.GetDirectoryName(Engine.FfmpegPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            // Canli seviye komutlarinin onayi da hata kanalindan gelir, hata sayilmaz.
            if (!string.IsNullOrWhiteSpace(e.Data) && !e.Data.StartsWith("Command reply", StringComparison.Ordinal))
                _firstError ??= e.Data; // Ilk satir asil nedendir, sonrakiler onun sonuclari.
        };
        process.Exited += (_, _) =>
        {
            var code = process.ExitCode;
            if (ReferenceEquals(_process, process)) _process = null;
            Exited?.Invoke(code, _firstError);
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
        process.BeginErrorReadLine();
        Output = captureOutput ? process.StandardOutput.BaseStream : null;
    }

    /// <summary>Calisan ffmpeg'e bir klavye komutu satiri gonderir.</summary>
    public void SendLine(string text)
    {
        try
        {
            var process = _process;
            if (process is null) return;
            process.StandardInput.Write(text + "\n");
            process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // ffmpeg bu arada kapanmis.
        }
    }

    /// <summary>ffmpeg'e dosyayi tamamlayip kapanmasini soyler, olmazsa sonlandirir.</summary>
    public void Stop()
    {
        var process = _process;
        if (process is null) return;
        try
        {
            process.StandardInput.Write('q');
            process.StandardInput.Flush();
            if (process.WaitForExit(10000)) return;
            process.Kill();
            process.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
                                       or System.ComponentModel.Win32Exception)
        {
            // Bu arada kendiliginden kapanmis.
        }
    }
}

static class PcMicrophone
{
    static string[] InputArguments(string deviceId) => new[]
    {
        "-hide_banner", "-loglevel", "error",
        "-f", "dshow", "-audio_buffer_size", "20", "-i", $"audio={deviceId}",
    };

    public static string[] RecordArguments(string deviceId, string path) =>
        InputArguments(deviceId).Concat(new[] { "-c:a", "aac", "-b:a", "128k", "-y", path }).ToArray();

    public static string[] ProbeArguments(string deviceId) =>
        InputArguments(deviceId).Concat(new[] { "-f", "s16le", "-ac", "2", "-ar", "48000", "pipe:1" }).ToArray();

    public static async Task<IReadOnlyList<CaptureDevice>> ListAsync()
    {
        var info = new ProcessStartInfo(Engine.FfmpegPath)
        {
            WorkingDirectory = Path.GetDirectoryName(Engine.FfmpegPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-list_devices", "true", "-f", "dshow", "-i", "dummy" })
            info.ArgumentList.Add(argument);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            process.OutputDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            var report = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return ParseDevices(report);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { /* kapanmis */ }
            return Array.Empty<CaptureDevice>();
        }
    }

    public static IReadOnlyList<CaptureDevice> ParseDevices(string report)
    {
        var devices = new List<CaptureDevice>();
        var lines = report.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var device = Regex.Match(lines[i], "\"(.+)\"\\s+\\(audio\\)");
            if (!device.Success) continue;

            var name = device.Groups[1].Value;
            var alternative = i + 1 < lines.Length ? Regex.Match(lines[i + 1], "Alternative name \"(.+)\"") : Match.Empty;
            devices.Add(new CaptureDevice(name, alternative.Success ? alternative.Groups[1].Value : name));
        }
        return devices;
    }
}
