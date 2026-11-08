using System.Diagnostics;
using System.Globalization;

namespace Tenaire;

sealed record Device(string Serial, string State, string Model)
{
    public bool IsReady => State == "device";
}

sealed record Battery(double TemperatureC, int Level);

static class Engine
{
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "engine");
    public static string ScrcpyPath { get; } = Path.Combine(Directory, "scrcpy.exe");
    public static string AdbPath { get; } = Path.Combine(Directory, "adb.exe");

    // scrcpy'nin kendi kutuphaneleriyle cakismasin diye ayri klasorde durur.
    public static string FfmpegPath { get; } = Path.Combine(Directory, "ffmpeg", "ffmpeg.exe");
    public static bool IsAvailable => File.Exists(ScrcpyPath) && File.Exists(AdbPath);
}

static class Adb
{
    /// <returns>Komut basarisiz olursa veya zaman asimina ugrarsa null.</returns>
    static async Task<string?> RunAsync(int timeoutMs, params string[] args)
    {
        var info = new ProcessStartInfo(Engine.AdbPath)
        {
            WorkingDirectory = Engine.Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var cts = new CancellationTokenSource(timeoutMs);
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            try { if (!process.HasExited) process.Kill(); } catch { /* zaten kapanmis */ }
            return null;
        }
    }

    /// <summary>adb sunucusu soguk baslarken birkac saniye surebildigi icin sure uzun tutuldu.</summary>
    public static async Task<IReadOnlyList<Device>?> GetDevicesAsync()
    {
        var output = await RunAsync(15000, "devices", "-l");
        return output is null ? null : ParseDevices(output);
    }

    public static IReadOnlyList<Device> ParseDevices(string output)
    {
        var devices = new List<Device>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith("List of devices", StringComparison.Ordinal) || rawLine.StartsWith('*')) continue;
            var parts = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var model = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?[6..].Replace('_', ' ');
            devices.Add(new Device(parts[0], parts[1], model ?? "Android cihaz"));
        }
        return devices;
    }

    public static async Task<Battery?> GetBatteryAsync(string serial)
    {
        var output = await RunAsync(5000, "-s", serial, "shell", "dumpsys", "battery");
        return output is null ? null : ParseBattery(output);
    }

    public static Battery? ParseBattery(string output)
    {
        int? temperature = null, level = null;
        foreach (var rawLine in output.Split('\n'))
        {
            var separator = rawLine.IndexOf(':');
            if (separator < 0) continue;
            var key = rawLine[..separator].Trim();
            if (key != "temperature" && key != "level") continue;
            if (!int.TryParse(rawLine[(separator + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;
            if (key == "temperature") temperature = value; else level = value;
        }
        // Android sicakligi derecenin onda biri olarak bildirir.
        return temperature is int t && level is int l ? new Battery(t / 10.0, l) : null;
    }

    public static Task StopServerAsync() => RunAsync(3000, "kill-server");
}
