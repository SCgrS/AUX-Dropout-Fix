# AUX Dropout Fix

### [⬇ Windows için indir (AUXDropoutFix.exe)](https://github.com/SCgrS/AUX-Dropout-Fix/releases/latest/download/AUXDropoutFix.exe)

[English](README.md)

Bilgisayarın sesi kısıkken taşınabilir hoparlörlerin kablo girişinde kesilmesini önleyen küçük bir **Windows arka plan aracı**.

Bazı hoparlörler 3,5 mm girişteki sinyal zayıflayınca kendini susturur. Bilgisayarın sesi kısıkken şarkıların ve videoların sessiz yerleri kesilir, siz de bilgisayarı %100'de tutup sesi hoparlörden ayarlamak zorunda kalırsınız. AUX Dropout Fix kablo çıkışına sürekli, duyulmayan 20 Hz'lik bir sinyal gönderir. Hoparlör girişin sessiz olduğunu hiç düşünmez.

**JBL Charge 4** için yapıldı ve onda sınandı, çalıştığı kesin. Kablo girişini aynı biçimde susturan başka hoparlörlerde de işe yarayabilir.

## Kurulum

Gereksinim: Windows 10 ya da 11. Başka bir şey gerekmez, Windows'un kendi .NET Framework 4'ü yeter.

1. Yukarıdaki bağlantıdan `AUXDropoutFix.exe` dosyasını indirin.
2. Dosyaya çift tıklayın. Program kendini kullanıcınız için kurar (yönetici izni gerekmez), Windows açılışına yazar ve arka planda çalışmaya başlar.
3. Windows SmartScreen uyarırsa "Daha fazla bilgi" › "Yine de çalıştır"ı seçin. Uyarı programın imzalı olmamasından çıkar.

## Öne çıkanlar

- **Duyulmaz:** Sinyal 20 Hz'dir ve çıkışta -40 dB düzeyindedir.
- **Windows sesinden bağımsız:** Windows sesi kısılınca sinyal aynı oranda güçlendirilir, hoparlöre giden düzeyi değişmez.
- **Yalnızca ses çalarken:** O çıkışta başka bir program ses çalmıyorsa sinyal 15 saniye sonra yavaşça kapanır. Hoparlör boştayken kendini susturabilir ve kapanabilir.
- **Penceresi yok:** Arka planda sessizce çalışır, Windows açılınca kendiliğinden başlar.

## Ne yapar

### Sinyal

Sinyal "High Definition Audio Device" kartının kulaklık olmayan ilk etkin çıkışına, genellikle hoparlör girişine gider. Varsayılan çıkış başka bir aygıt olsa da (örneğin Bluetooth kulaklık) oraya gitmeye devam eder, ama o çıkışta ses çalan bir program yoksa kapalı kalır.

Windows sesi yaklaşık %7'nin altına inince sinyal daha fazla güçlendirilemez ve zayıflamaya başlar. JBL Charge 4'te %5'te kesilme olmadı. Daha aşağısında hoparlör yeniden susabilir.

### Ayarlar

İsterseniz `%LOCALAPPDATA%\AUXDropoutFix\settings.txt` dosyasına `ad=değer` satırları yazabilirsiniz. Program başlarken okunur.

| Ad | Varsayılan | Anlamı |
| --- | --- | --- |
| `output` | ("High Definition Audio Device"ın ilk çıkışı) | Çıkışın Windows'taki adı, örneğin `Hoparlör (High Definition Audio Device)` |
| `frequency` | `20` | Sinyalin frekansı (Hz) |
| `targetdb` | `-40` | Sinyalin çıkıştaki düzeyi (dB) |
| `maxdb` | `-3` | Windows sesi kısıkken sinyalin güçlendirilebileceği üst sınır (dB) |
| `holdseconds` | `15` | Ses bittikten sonra sinyalin açık kalacağı süre (saniye) |

### Dikkat

Hoparlör aynı bilgisayara hem ses kablosuyla hem de şarj için USB ile bağlıysa vızıltı oluşabilir. Hoparlör artık kendini susturmadığı için bu vızıltı duyulur. Hoparlörü bilgisayardan değil, prizdeki bir şarj aletinden şarj edin.

### Nasıl çalışır

Program Windows'un ses arayüzüyle (WASAPI, paylaşımlı kip, olay güdümlü, 20 ms tampon) çıkışa kendi ses akışını açar. Her 10 ms'de Windows ses düzeyini okur ve sinyalin genliğini buna göre ayarlar. Ayrı bir iş parçacığı yarım saniyede bir o çıkıştaki ses oturumlarına bakar, başka bir programın oturumu etkinse sinyali açar. `tools\sessions.ps1` o anda hangi programların ses çaldığını gösterir.

## Güncelleme

Program kendini güncellemez. Yeni sürümü indirip çift tıklayın, çalışan kopya kapatılıp yenisiyle değiştirilir.

## Kaldırma

Ayarlar › Uygulamalar › Yüklü uygulamalar'da AUX Dropout Fix'i bulup Kaldır'ı seçin. Program kapatılır, Windows açılışından silinir, program klasörü ile ayar ve günlük klasörü silinir.

## Verileriniz

Program internete bağlanmaz ve hiçbir veri göndermez. Yalnızca `%LOCALAPPDATA%\AUXDropoutFix\log.txt` dosyasına başlama, kapanma ve hata satırları yazar. Dosya 200 KB'ı geçince silinip yeniden başlar.

## Üçüncü taraf bileşenler

Yok. Program yalnızca Windows'un kendi bileşenlerini kullanır.

## Teşekkür

Bu hoparlörlerin kablo girişini devrede, sinyalin gücüne bakarak susturduğunu Hackaday'deki [Reverse-engineering JBL flip 4](https://hackaday.io/project/162337-reverse-engineering-jbl-flip-4) projesi ortaya koydu. Sürekli alçak frekans sinyali fikri de oradan ve [iFixit'teki tartışmadan](https://www.ifixit.com/Answers/View/405479/Why+playing+via+AUX+cut's+on+quiet+passages+in+the+songs-movies) geliyor.

---

Soru ve geri bildirimler için: [x.com/CgrShn](https://x.com/CgrShn)

Lisans: AGPL-3.0. Ayrıntılar için [LICENSE](LICENSE) dosyasına bakınız.
