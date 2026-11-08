using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tenaire;

sealed class AppSettings
{
    public string ProfileKey { get; set; } = Profiles.Default.Key;
    public bool Record { get; set; } = true;
    public bool GameAudio { get; set; } = true;
    public bool Microphone { get; set; } = true;
    public double GameDb { get; set; }
    public double MicrophoneDb { get; set; }
    public int MicrophoneDelayMs { get; set; } = 900;
    public int PcMicrophoneDelayMs { get; set; } = 300;

    /// <summary>"record", "live" veya "window".</summary>
    public string Mode { get; set; } = "record";
    public bool WindowGameAudio { get; set; } = true;
    public bool WindowMicrophone { get; set; } = true;
    public bool WindowRecord { get; set; }
    public bool EchoCancellation { get; set; } = true;
    public string LivePlatform { get; set; } = "youtube";
    public string LiveCustomUrl { get; set; } = "";
    public int LiveBitRateKbps { get; set; } = 6000;
    public bool LiveGameAudio { get; set; } = true;
    public bool LiveMicrophone { get; set; } = true;
    public bool LiveRecord { get; set; }

    /// <summary>Bellekte acik durur, dosyaya yalnizca Windows hesabina bagli sifreli yazilir.</summary>
    public string LiveKey { get; set; } = "";

    /// <summary>"phone" veya bilgisayardaki mikrofonun kimligi.</summary>
    public string MicrophoneSource { get; set; } = "phone";

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tenaire", "settings.txt");

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;
                switch (parts[0])
                {
                    case "profile": settings.ProfileKey = parts[1]; break;
                    case "record": settings.Record = parts[1] == "1"; break;
                    case "gameAudio": settings.GameAudio = parts[1] == "1"; break;
                    case "microphone": settings.Microphone = parts[1] == "1"; break;
                    case "gameDb": settings.GameDb = ParseOr(parts[1], settings.GameDb); break;
                    case "microphoneDb": settings.MicrophoneDb = ParseOr(parts[1], settings.MicrophoneDb); break;
                    case "microphoneDelayMs": settings.MicrophoneDelayMs = ParseOr(parts[1], settings.MicrophoneDelayMs); break;
                    case "pcMicrophoneDelayMs": settings.PcMicrophoneDelayMs = ParseOr(parts[1], settings.PcMicrophoneDelayMs); break;
                    case "microphoneSource": settings.MicrophoneSource = parts[1]; break;
                    case "liveMode": if (parts[1] == "1") settings.Mode = "live"; break; // eski ayar dosyalari
                    case "mode": settings.Mode = parts[1]; break;
                    case "windowGameAudio": settings.WindowGameAudio = parts[1] == "1"; break;
                    case "windowMicrophone": settings.WindowMicrophone = parts[1] == "1"; break;
                    case "windowRecord": settings.WindowRecord = parts[1] == "1"; break;
                    case "echoCancellation": settings.EchoCancellation = parts[1] == "1"; break;
                    case "livePlatform": settings.LivePlatform = parts[1]; break;
                    case "liveCustomUrl": settings.LiveCustomUrl = parts[1]; break;
                    case "liveBitRateKbps": settings.LiveBitRateKbps = ParseOr(parts[1], settings.LiveBitRateKbps); break;
                    case "liveGameAudio": settings.LiveGameAudio = parts[1] == "1"; break;
                    case "liveMicrophone": settings.LiveMicrophone = parts[1] == "1"; break;
                    case "liveRecord": settings.LiveRecord = parts[1] == "1"; break;
                    case "liveKey": settings.LiveKey = Unprotect(parts[1]); break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ilk calistirma veya okunamayan dosya: varsayilanlarla devam.
        }
        return settings;
    }

    static string Protect(string secret) => secret.Length == 0
        ? ""
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    static string Unprotect(string stored)
    {
        if (stored.Length == 0) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Baska bir hesaptan veya bozuk: kullanici anahtari yeniden girer.
            return "";
        }
    }

    static int ParseOr(string text, int fallback) => int.TryParse(text, out var value) ? value : fallback;

    static double ParseOr(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, new[]
            {
                $"profile={ProfileKey}",
                $"record={(Record ? 1 : 0)}",
                $"gameAudio={(GameAudio ? 1 : 0)}",
                $"microphone={(Microphone ? 1 : 0)}",
                $"gameDb={GameDb.ToString(CultureInfo.InvariantCulture)}",
                $"microphoneDb={MicrophoneDb.ToString(CultureInfo.InvariantCulture)}",
                $"microphoneDelayMs={MicrophoneDelayMs}",
                $"pcMicrophoneDelayMs={PcMicrophoneDelayMs}",
                $"microphoneSource={MicrophoneSource}",
                $"mode={Mode}",
                $"windowGameAudio={(WindowGameAudio ? 1 : 0)}",
                $"windowMicrophone={(WindowMicrophone ? 1 : 0)}",
                $"windowRecord={(WindowRecord ? 1 : 0)}",
                $"echoCancellation={(EchoCancellation ? 1 : 0)}",
                $"livePlatform={LivePlatform}",
                $"liveCustomUrl={LiveCustomUrl}",
                $"liveBitRateKbps={LiveBitRateKbps}",
                $"liveGameAudio={(LiveGameAudio ? 1 : 0)}",
                $"liveMicrophone={(LiveMicrophone ? 1 : 0)}",
                $"liveRecord={(LiveRecord ? 1 : 0)}",
                $"liveKey={Protect(LiveKey)}",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ayar kaydedilemedi diye yansitma engellenmemeli.
        }
    }
}
