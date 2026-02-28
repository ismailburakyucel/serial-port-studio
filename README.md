# Seri Port Monitor (Serial Port Studio)

Bu projeyi kendi elektronik ve yazılım çalışmalarımda seri port trafiğini daha rahat izleyebilmek için geliştirdim. Günlük işlerimde aktif olarak kullanıyorum ve oldukça işime yarıyor. Seri port ile çalışan diğer geliştiricilerin de işini kolaylaştırabileceğini düşünerek paylaştım.

Temel amaç, birden fazla portu aynı anda, karmaşadan uzak ve esnek bir şekilde takip edebilmektir.

---

## 🇹🇷 Özellikler

* **Otomatik Tanıma:** USB-Seri dönüştürücü takıldığında portu otomatik algılar ve izleme penceresini açar. Manuel "Port Yenile" yapmanıza gerek kalmaz.
* **Esnek Pencereler:** Her portun penceresini ana ekrandan ayırıp masaüstünde istediğiniz yere koyabilir (Detach), küçültebilir veya tam ekran yapabilirsiniz.
* **Kelime Vurgulama:** Önemli gördüğünüz kelimeler (Hata, Tamam, Sıcaklık vb.) için renk kuralları belirleyebilirsiniz. Bu kuralları dışa aktarıp sonra tekrar kullanabilirsiniz.
* **Log Yönetimi:** Alınan verileri zaman damgalarıyla birlikte dosyaya kaydeder. Dahili log görüntüleyicisi ile eski kayıtlar içinde arama (Ctrl+F) yapabilirsiniz.
* **Görünüm Profilleri:** Her pencere için farklı yazı tipi, boyutu ve renk teması seçip profil olarak kaydedebilirsiniz.
* **Akış Kontrolü:** Veri alımını tamamen durdurabilir (Pause) veya sadece ekran kaymasını dondurup (Freeze) gelen verilere geriye dönük bakabilirsiniz.

### 🛠 Kurulum ve Kullanım
1.  Bilgisayarınızda **.NET 6.0** veya üzeri bir sürümün yüklü olduğundan emin olun.
2.  `SeriPortMonitor.sln` dosyasını Visual Studio ile açın.
3.  Projeyi derleyin (Build) ve çalıştırın.

---

## 🇬🇧 Features

I developed this tool to simplify my own workflow when dealing with serial port communication. It has been very helpful in my projects, and I’m sharing it here so it might be useful for others working on similar tasks.

* **Auto-Detection:** Automatically detects new COM ports when a USB-Serial device is plugged in. No manual refresh required.
* **Flexible Windows:** Each port window can be detached from the main layout, moved anywhere on your desktop, or minimized.
* **Keyword Highlighting:** Define color rules for specific keywords (Error, OK, etc.). Rules can be exported and imported as JSON.
* **Log Management:** Saves incoming data to text files with timestamps. Features a built-in log viewer with search (Ctrl+F) support.
* **Custom Profiles:** Customize and save font types, sizes, and color schemes for each window as profiles.
* **Flow Control:** Pause the data stream entirely or just freeze auto-scroll to analyze previous lines.

### 🛠 Installation and Usage
1.  Ensure **.NET 6.0** or higher is installed on your machine.
2.  Open `SeriPortMonitor.sln` in Visual Studio.
3.  Build and Run.

---
*Bu proje açık kaynaklıdır ve geliştirilmeye müsaittir. İşinize yaraması dileğiyle.*
