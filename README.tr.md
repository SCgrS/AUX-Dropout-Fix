# AUX Dropout Fix

[English](README.md)

Bilgisayarın sesi kısıkken taşınabilir hoparlörlerin kablo girişinde kesilmesini önleyen küçük bir **Windows arka plan aracı**.

Bazı hoparlörler 3,5 mm girişteki sinyal zayıflayınca kendini susturur. Bilgisayarın sesi kısıkken şarkıların ve videoların sessiz yerleri kesilir, siz de bilgisayarı %100'de tutup sesi hoparlörden ayarlamak zorunda kalırsınız. AUX Dropout Fix kablo çıkışına sürekli, duyulmayan 20 Hz'lik bir sinyal gönderir. Hoparlör girişin sessiz olduğunu hiç düşünmez.

**JBL Charge 4** için yapıldı ve onda sınandı, çalıştığı kesin. Kablo girişini aynı biçimde susturan başka hoparlörlerde de işe yarayabilir.

## Kurulum

Gereksinim: Windows 10 ya da 11. Başka bir şey gerekmez. Program Windows'un kendi .NET Framework 4'üyle çalışır.

Henüz hazır bir indirme dosyası yok. Program, kaynak kodundan Windows'un kendi C# derleyicisiyle derlenir.

1. Bu sayfada **Code › Download ZIP**'i seçin ve zip dosyasını bir klasöre çıkarın.
2. `Build.cmd` dosyasına çift tıklayın. `bin\AUXDropoutFix.exe` oluşur.
3. `bin\AUXDropoutFix.exe` dosyasına çift tıklayın. Program kendini kullanıcınız için kurar (yönetici izni gerekmez), Windows açılışına ve **Ayarlar › Uygulamalar › Yüklü uygulamalar** listesine yazar, arka planda çalışmaya başlar. Kurulduğunu bir iletiyle bildirir.
4. Windows SmartScreen uyarırsa **Daha fazla bilgi** › **Yine de çalıştır**'ı seçin. Uyarı dosyaların imzalı olmamasından çıkar.

## Öne çıkanlar

- **Duyulmaz:** Sinyal 20 Hz'dir ve çıkışta -40 dB düzeyindedir.
- **Windows sesinden bağımsız:** Windows sesi kısılınca sinyal aynı oranda güçlendirilir, hoparlöre giden düzeyi değişmez.
- **Yalnızca ses çalarken:** O çıkışta başka bir program ses çalmıyorsa sinyal 15 saniye sonra yavaşça kapanır, program ses akışını da durdurur. Hoparlör boştayken kendini susturabilir ve kapanabilir, bilgisayar da uykuya geçebilir.
- **Penceresi yok:** Arka planda sessizce çalışır, Windows açılınca kendiliğinden başlar.

## Ne yapar

### Sinyal

Sinyal "High Definition Audio Device" kartının ilk etkin hoparlör ya da hat çıkışına (kulaklık ve HDMI değil), genellikle hoparlör girişine gider. Varsayılan çıkış başka bir aygıt olsa da (örneğin Bluetooth kulaklık) oraya gitmeye devam eder, ama o çıkışta ses çalan bir program yoksa kapalı kalır.

Windows sesi yaklaşık %7'nin altına inince sinyal daha fazla güçlendirilemez ve zayıflamaya başlar. JBL Charge 4'te %5'te kesilme olmadı. Daha aşağısında hoparlör yeniden susabilir.

Başka bir program yüksek sesle çalarken sinyal geri çekilir, ikisinin toplamı tam ölçeği aşıp cızırdamasın diye. Hoparlör yüksek sesli yerlerde zaten susmaz.

### Ayarlar

İsterseniz `%LOCALAPPDATA%\AUXDropoutFix\settings.txt` dosyasına `ad=değer` satırları yazabilirsiniz. Program başlarken okunur. Aralığın dışındaki bir değer kullanılmaz, günlüğe yazılır.

| Ad | Varsayılan | Aralık | Anlamı |
| --- | --- | --- | --- |
| `output` | ("High Definition Audio Device"ın ilk hoparlör çıkışı) | | Çıkışın Windows'taki adı, örneğin `Hoparlör (High Definition Audio Device)` |
| `frequency` | `20` | 10 ile 30 | Sinyalin frekansı (Hz) |
| `targetdb` | `-40` | -60 ile -30 | Sinyalin çıkıştaki düzeyi (dB) |
| `maxdb` | `-3` | -60 ile -3 | Windows sesi kısıkken sinyalin güçlendirilebileceği üst sınır (dB) |
| `holdseconds` | `15` | 0 ile 3600 | Ses bittikten sonra sinyalin açık kalacağı süre (saniye) |

### Dikkat

Hoparlör aynı bilgisayara hem ses kablosuyla hem de şarj için USB ile bağlıysa vızıltı oluşabilir. Hoparlör artık kendini susturmadığı için bu vızıltı duyulur. Hoparlörü bilgisayardan değil, prizdeki bir şarj aletinden şarj edin.

### Nasıl çalışır

Program Windows'un ses arayüzüyle (WASAPI, paylaşımlı kip, olay güdümlü, 20 ms tampon) çıkışa kendi ses akışını açar. Yarım saniyede bir o çıkıştaki ses oturumlarına bakar. Başka bir programın oturumu yalnızca gerçekten ses çıkarırken çalıyor sayılır, sessiz bir akışı açık tutan program sinyali açmaz. Ses başlayınca akış başlatılır, sinyal yavaşça kapandıktan sonra durdurulur. Akış çalışırken program her 10 ms'de Windows ses düzeyini ve öbür programların tepe düzeyini okur, sinyalin genliğini buna göre ayarlar. Windows'un görüşmeler sırasında öbür sesleri kısma özelliğinin dışında kalır. 32 bit süreç olarak çalışır, bu daha az bellek ister. `tools\sessions.ps1` o anda hangi programların ses çaldığını gösterir.

## Güncelleme

Program kendini güncellemez. Yeni sürümü yukarıdaki gibi indirip yeniden derleyin, sonra yeni `bin\AUXDropoutFix.exe` dosyasına çift tıklayın. Çalışan kopya kapatılıp yenisiyle değiştirilir.

## Veri gizliliği

Program internete bağlanmaz, hiçbir veri göndermez, kullanım verisi toplamaz. Ayarlarını `%LOCALAPPDATA%\AUXDropoutFix\settings.txt` dosyasından okur, yalnızca `%LOCALAPPDATA%\AUXDropoutFix\log.txt` dosyasına başlama, kapanma, sinyalin açılıp kapanması ve hata satırları yazar. Günlük dosyası 200 KB'ı geçince adı `log.old.txt` olur ve yeni bir dosya başlar.

Program **Ayarlar › Uygulamalar › Yüklü uygulamalar**'dan kaldırılınca kapatılır, Windows açılışından silinir, program klasörü ile ayar ve günlük klasörü de silinir.

## Üçüncü taraf bileşenler

Yok. Program yalnızca Windows'un kendi bileşenlerini kullanır.

## Otomatik denetimler

[![CodeQL Advanced](https://github.com/SCgrS/AUX-Dropout-Fix/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/SCgrS/AUX-Dropout-Fix/actions/workflows/codeql.yml)\
[![Dependency Review](https://github.com/SCgrS/AUX-Dropout-Fix/actions/workflows/dependency-review.yml/badge.svg?branch=main)](https://github.com/SCgrS/AUX-Dropout-Fix/actions/workflows/dependency-review.yml)

## Teşekkür

Bu hoparlörlerin kablo girişini devrede, sinyalin gücüne bakarak susturduğunu Hackaday'deki [Reverse-engineering JBL flip 4](https://hackaday.io/project/162337-reverse-engineering-jbl-flip-4) projesi ortaya koydu. Sürekli alçak frekans sinyali fikri de oradan ve [iFixit'teki tartışmadan](https://www.ifixit.com/Answers/View/405479/Why+playing+via+AUX+cut's+on+quiet+passages+in+the+songs-movies) geliyor.

---

Soru ve geri bildirimler için: [x.com/CgrShn](https://x.com/CgrShn)

Lisans: AGPL-3.0. Ayrıntılar için [LICENSE](LICENSE) dosyasına bakınız.
