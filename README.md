# Tenaire

Android telefonun ekranını USB kablosuyla bilgisayara yansıtan Windows uygulaması. Telefonda oyun oynarken görüntüyü bilgisayarda kaydetmek veya yayınlamak için yapıldı. Kodlama telefonun donanım kodlayıcısında yapıldığı için telefona bindirdiği yük düşüktür.

## Modlar

- **Kayıt:** Görüntüyü ve oyun sesini MP4 olarak, mikrofonu ayrı dosya olarak kaydeder. İkisi sonradan tek videoda birleştirilebilir.
- **Canlı Yayın:** RTMP veya RTMPS ile doğrudan yayın yapar, isteğe bağlı olarak bilgisayara da kaydeder.
- **Pencere:** Yayın programının yakalaması için yalnızca bir pencere açar, oyun sesi ve mikrofonu bilgisayardan çalar. İsteğe bağlı olarak aynı anda kayıt alır.

Mikrofon telefondan (kablolu kulaklık dahil) veya bilgisayardaki bir giriş aygıtından seçilebilir. Ses seviyeleri dB olarak ayarlanır.

## Gereksinimler

- Windows 10 veya üzeri
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- USB hata ayıklaması açık bir Android telefon

## Motorların kurulumu

Üçüncü taraf motorlar boyutları nedeniyle depoda yoktur. Derlemeden önce proje klasöründe `engine` klasörünü oluşturun:

1. [scrcpy 5.0.1](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1) Windows 64 bit paketinin içeriğini doğrudan `engine` klasörüne çıkarın. `engine\scrcpy.exe` ve `engine\adb.exe` oluşmalıdır.
2. [BtbN FFmpeg Builds](https://github.com/BtbN/FFmpeg-Builds/releases) sayfasından `n9.0` sürümünün `win64-lgpl-shared` paketini indirin. Paketin `bin` klasöründeki dosyaları ve `LICENSE.txt` dosyasını `engine\ffmpeg` klasörüne koyun. `engine\ffmpeg\ffmpeg.exe` oluşmalıdır.

FFmpeg'in ayrı klasörde durması gerekir, çünkü scrcpy aynı adlı ama farklı derlemelerde DLL dosyaları kullanır.

## Derleme

```bash
dotnet publish Tenaire.csproj -c Release -o dist
```

Çalıştırılacak dosya `dist\Tenaire.exe` olur. Dosyayı tek başına taşımayın, `dist` klasörünün tamamı gereklidir.

## Kullanılan bileşenler

- [scrcpy](https://github.com/Genymobile/scrcpy) (Apache 2.0)
- [FFmpeg](https://ffmpeg.org) (LGPL)

## Lisans

Tenaire'in kaynak kodu [MIT lisansı](LICENSE) ile dağıtılır. scrcpy ve FFmpeg kendi lisanslarına tabidir.
