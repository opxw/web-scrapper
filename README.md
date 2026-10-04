# Web Scrapper

Aplikasi WinForms .NET 10 untuk menyalin halaman HTML, CSS, dan aset HTTP ke folder lokal. Portable Windows x64, tanpa installer atau instalasi .NET terpisah. Tidak menjalankan JavaScript.

## Menjalankan

1. Ekstrak ZIP portable ke folder yang bisa ditulis dan jalankan `WebScrapper.exe`.
2. Isi URL HTTP/HTTPS dan folder hasil. Kedalaman 0 menyalin halaman awal beserta aset; kedalaman berikutnya mengikuti navigasi pada origin yang sama.
3. Pilih/edit User-Agent. Header kustom berformat `Nama: nilai`, satu baris per header. Isi cookie awal sebagai `name=value; name2=value2` bila dibutuhkan.
4. Atur jeda, variasi jeda, jumlah URL, kedalaman, ukuran, timeout, dan retry. Aset CDN dapat diaktifkan secara terpisah.
5. Klik **Mulai salin**. Daftar hasil menampilkan satu baris per URL dengan HTTP/status: antre, mengunduh, berhasil, sudah ada, gagal, dilewati, dihentikan. Query ditampilkan sebagai identitas hash agar nilai sensitif tidak muncul di daftar.
6. **Buka hasil** membuka HTML awal. **Lihat halaman di memori** menampilkan sumber asli HTML/CSS dari baris yang dipilih. **Berhenti** membatalkan request atau penantian retry.
7. **Auto-scroll** mengikuti baris hasil yang baru ditambahkan/diperbarui. Matikan untuk membaca baris sebelumnya tanpa perpindahan otomatis; dapat diubah saat scraping berjalan. Pilihan disimpan bersama project. Project lama memakai default aktif.

Grid hasil menggunakan font Segoe UI 8,25 pt dengan baris yang lebih ringkas.
Klik kanan pada sel grid lalu pilih **Copy text** untuk menyalin teks yang ditampilkan sel tersebut ke clipboard. Nilai query URL yang disamarkan tetap disalin dalam bentuk tersamar.

Host `pagead.syndication.com` otomatis dilewati sebelum request, termasuk target redirect dan scrap ulang. URL ditandai **Dilewati**, bukan gagal. Pencocokan host tidak membedakan huruf besar/kecil; aturan ini tidak berlaku untuk domain lain yang namanya hanya mirip.

Sejak 1.0.10 script `https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js` juga otomatis dilewati, termasuk seluruh variasi query, redirect dan retry project lama. Penanda grid `[q:...]` adalah hash query dan tidak perlu dimasukkan ke exclude. Aturan ini khusus host/path script tersebut; URL lain pada host itu tidak otomatis dikecualikan.

Tab **Exclude path** berisi satu path per baris, dengan nilai awal `/v8` dan `/v9`. `/v8` dan seluruh turunannya dilewati, sedangkan `/v80` tetap diproses. Pencocokan tidak membedakan huruf besar/kecil, membaca path URL yang sudah didekode, dan mengabaikan query. Berlaku pada semua host, target redirect, dan scrap ulang. Kosongkan untuk menonaktifkan exclude path. Daftar ini tersimpan di project; URL yang dilewati tetap tercatat dalam grid.

Opsi **Fast (paralel)** menjalankan 2–8 pekerja (default 4; mode awal nonaktif). Download berjalan bersamaan per kelompok, lalu hasil/file dan cache diselesaikan berurutan agar konsisten. Jeda request, robots Crawl-delay, batas byte dan cooldown Retry-After berlaku bersama; request yang sudah berjalan dapat selesai saat cooldown dimulai. Fast menambah koneksi dan file sementara, dan kelompok berikutnya menunggu pekerja paling lambat. Kecepatan tetap bergantung pada server dan pengaturan jeda.

Opsi **Ganti User-Agent otomatis sekali saat diminta verifikasi browser** tersedia pada tab User-Agent, aktif secara default dan tersimpan dalam project. Pemicu saat ini adalah HTTP 403 dengan header `cf-mitigated: challenge`. Aplikasi mengganti ke preset Firefox (atau Chrome bila awalnya Firefox), menunggu minimal 2 detik atau Retry-After yang lebih lama, lalu mencoba lagi. Pergantian maksimal satu kali untuk seluruh run; agent pengganti dipakai hingga run selesai, sedangkan run baru dimulai dari agent yang dipilih. Aturan robots dievaluasi kembali untuk agent pengganti. Respons 401/403 biasa tidak memicu pergantian. Penggantian header ini tidak menjalankan verifikasi JavaScript atau menyelesaikan CAPTCHA.

## Project, status, dan melanjutkan pekerjaan

### Auto cookie (1.0.9)

Tab **User-Agent, header & cookie** memiliki **Auto cookie sebelum mulai** (default aktif) dan **Buka browser / ambil cookie**. Saat Mulai/Retry belum memiliki sesi atau cookie manual, browser WebView2 dibuka. Selesaikan login/verifikasi secara manual, kemudian klik **Gunakan sesi ini**. Cookie diambil otomatis tanpa copy/paste, termasuk HttpOnly; batas domain/path/Secure/expiry dipertahankan dan crawler hanya mengirimnya ke origin awal. Jika baris URL pada origin yang sama dipilih, browser membuka URL tersebut. User-Agent mengikuti browser dan pergantian agent otomatis dinonaktifkan agar identitas sesi konsisten. Tombol browser dapat digunakan kembali untuk memperbarui sesi yang ditolak/kedaluwarsa. Cookie manual tetap tersedia dengan Auto cookie dimatikan.

Browser memakai profil InPrivate tersendiri, tidak membaca profil Chrome/Edge pribadi, dan tidak menyimpan password. Data sesi login tidak masuk ke project; folder data teknis WebView2 berada di LocalAppData/WebScrapper/Browser. Load project menghapus sesi impor dari aplikasi sehingga perlu mengambil sesi lagi. Hanya preferensi Auto cookie yang disimpan. WebView2 Runtime harus tersedia pada Windows; jika tidak tersedia aplikasi memberi pesan dan cookie manual tetap dapat digunakan. SDK disertakan dalam paket, runtime browser tidak dibundel. Fitur ini menjalankan browser untuk login; proses crawl masih HTTP statis. Cookie browser belum tentu cukup untuk proteksi yang mengikat sesi ke fingerprint browser. Pro: tanpa menyalin cookie sensitif; con: tambahan runtime dan interaksi verifikasi, tidak menjamin 403 hilang. SDK/runtime browser perlu diperbarui berkala.

API pengambilan cookie: [Microsoft WebView2 CookieManager](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2cookiemanager.getcookiesasync).

**Lanjutkan dari state/URL terakhir** aktif secara default. Jika project memiliki daftar URL, **Mulai salin** melanjutkan URL yang belum berhasil setelah posisi unduhan terakhir, kemudian kembali ke URL belum berhasil sebelumnya agar tidak tertinggal. Posisi mengacu pada URL terakhir yang selesai disimpan, bukan baris grid terpilih atau request terakhir yang gagal. Pada Fast, posisi mengikuti urutan penyimpanan hasil kelompok. File berhasil tetap diverifikasi dan dipakai kembali. Opsi dan posisi tersimpan dalam project; project lama tanpa posisi melanjutkan dari URL belum berhasil pertama. Nonaktifkan opsi untuk discovery dari URL awal memakai cache valid. Tombol **Scrap ulang belum berhasil** tetap membatasi proses pada URL belum berhasil. Save project diperlukan sebelum menutup aplikasi bila project belum pernah disimpan.

- **Save project** membuat file `.wsproject`, termasuk pengaturan, seluruh URL, kedalaman, hasil/status HTTP, URL akhir redirect, dan lokasi file relatif terhadap folder hasil. Dapat menyimpan konfigurasi sebelum run pertama.
- **Load project** memulihkan seluruh daftar URL tanpa crawl atau request HTTP, termasuk URL antre, gagal, dan dilewati. File/cache diperiksa secara lokal menggunakan panjang dan SHA-256. File valid menjadi **Sudah ada**; file/cache yang hilang atau berubah menjadi **Gagal**. Status sebelumnya saja tidak dianggap bukti keberhasilan. Lanjutkan melalui **Scrap ulang belum berhasil** setelah mengisi ulang autentikasi jika diperlukan.
- **Scrap ulang belum berhasil** melanjutkan URL yang gagal, dilewati, dibatalkan, atau masih antre; file valid tidak diunduh ulang. File hilang dapat diunduh ulang. Robots/HTTP denial tetap dapat menyebabkan kegagalan/dilewati.
- Saat aset/halaman yang dulu gagal akhirnya berhasil, HTML/CSS yang sudah tersimpan ikut ditulis ulang dari sumber asli sehingga tautannya mengarah ke hasil lokal yang baru.
- Setelah file project dipilih, project di-autosave saat run selesai/dihentikan. Untuk berhenti dan melanjutkan di lain waktu, gunakan **Berhenti**, kemudian **Save project**. Jangan menghentikan proses lewat Task Manager bila ingin seluruh antrean terbaru tersimpan.
- File project dan folder hasil/cache adalah satu kesatuan. Project menyimpan path absolut folder hasil, bukan menanamkan seluruh website ke JSON. Untuk berpindah komputer, salin keduanya dan sesuaikan `OutputDirectory` di project dengan lokasi baru sebelum load. Mengubah URL/folder di UI lalu Mulai/Save membuat project baru.
- Cookie dan semua header kustom, termasuk Authorization, tidak disimpan. Isi ulang setelah load. User-Agent disimpan.
- Konten asli tersedia di RAM maksimal 2 MB per dokumen (ukuran string UTF-16) dan 32 MB total. Dokumen lebih besar tetap tersedia di disk. Load mengisi RAM kembali dari cache valid sesuai batas tersebut.
- File project berisi URL lengkap, termasuk query agar retry tepat. HTML/CSS/cache adalah salinan konten situs dan dapat memuat data sensitif dari situs; simpan folder/project sesuai kebutuhan privasi Anda.

## Struktur file dan tautan lokal

Tombol **Fix URL** (1.0.12) memperbaiki tautan relatif pada HTML/CSS hasil project aktif secara lokal, tanpa membuka browser, cookie, crawl, atau download. Load project lalu klik Fix URL; pengaturan URL/folder yang belum disimpan di editor tidak mengubah target operasi ini. Sumber asli cache dipakai untuk rewrite, dan hanya file dengan hash valid dijadikan target lokal. File hilang ditandai gagal dan tautannya kembali ke URL online. File yang diubah pengguna tidak ditimpa. Hasil/cache diperbarui, project yang sudah memiliki lokasi file di-autosave, dan tombol Berhenti dapat membatalkan proses. Cache sumber harus tetap tersedia. Angka selesai adalah jumlah dokumen HTML/CSS yang diproses, termasuk yang isinya tidak berubah.

Perbaikan 1.0.11: pemetaan URL akhir redirect dipulihkan saat load/resume; referensi script dan aset tetap mengarah ke file lokal yang valid. Discovery mendukung `imagesrcset` pada preload, SVG `image/use` melalui `href`/`xlink:href`, serta rel stylesheet/preload/modulepreload/icon tanpa membedakan huruf besar/kecil. Endpoint dengan MIME JavaScript yang namanya `.php`/ekstensi lain disimpan berakhiran `.js`, tanpa mengubah isi script. Saat resume/retry, sumber HTML/CSS dari cache diperiksa lagi untuk menemukan aset statis yang sebelumnya belum tercatat, tanpa mengunduh ulang halaman yang valid. Batas depth/URL, exclude dan robots tetap berlaku. Script yang sebelumnya gagal ditautkan ke hasil lokal setelah retry berhasil. URL yang dibentuk saat JavaScript berjalan, import modul di dalam script, fetch/XHR dan service worker belum ditulis ulang; pengujian tautan statis bukan jaminan aplikasi web dinamis berjalan penuh melalui file lokal.

- Folder mengikuti host/port dan hierarki path situs. URL tanpa ekstensi yang mengembalikan HTML mendapat folder `index...html`.
- Nama file memakai suffix identitas URL; query memiliki suffix tersendiri. Ini mencegah tabrakan query, URL berbeda huruf besar/kecil pada Windows, serta `/a/` versus `/a/index.html`. Struktur folder dipertahankan, nama file tidak selalu identik dengan aslinya.
- Sebelum GET dan sebelum menulis, file tujuan diperiksa. File milik aplikasi dipakai kembali hanya bila metadata dan hash benar. File yang sudah ada tanpa metadata cocok atau yang diedit pengguna tidak ditimpa. Pindahkan/hapus sendiri file yang rusak/diubah sebelum retry jika memang ingin menggantinya.
- Penulisan download memakai `.part` sementara lalu rename atomik; file parsial tidak ditandai berhasil. Folder hasil dikunci selama run agar dua instance tidak menulis bersamaan.
- Metadata `.webscrapper-cache.json` menyimpan identitas hash, panjang, tipe/charset, lokasi lokal, dan hash konten. `.webscrapper-sources/` menyimpan HTML/CSS asli untuk discovery dan rewrite ulang yang konsisten. Jangan hapus cache bila ingin resume tanpa download.
- Penulisan ulang mencakup `href`, `src`, `poster`, `data`, `background`, `srcset`, inline style, `<style>`, CSS `url()` dan `@import`. `<base>` diselesaikan sebelum dihapus; fragmen dipertahankan. Path dihitung dari folder file sumber ke file tujuan dan di-escape per segmen.
- Hanya file yang benar-benar tersedia/valid ditautkan secara lokal. Target belum diunduh tetap menjadi URL online absolut. Query yang terunduh menjadi nama file unik, bukan query lokal.

## HTTP dan pacing

Mode normal menjalankan satu request pada satu waktu; Fast dapat menjalankan beberapa request bersamaan. Awal request memakai jeda minimum ditambah jitter 0–persentase yang dipilih, Referer berdasarkan halaman asal pada origin yang sama, cookie sesi dari Set-Cookie, serta dekompresi otomatis. Header dan cookie dibatasi ke origin awal (scheme/host/port), termasuk pada redirect. Redirect HTTPS ke HTTP ditolak. Sertifikat TLS tetap diverifikasi.

429/503/502/504 dan error koneksi tertentu memiliki retry terbatas dengan exponential backoff + jitter; `Retry-After` delta/date dihormati bila lebih lama. Setelah retry terakhir pada 429/503, request berikutnya tetap menunggu cooldown. Timeout berlaku untuk header dan pembacaan body; seluruh tunggu dapat dibatalkan. 401/403 biasa tidak di-retry otomatis; challenge eksplisit dapat memakai satu percobaan tambahan melalui opsi ganti agent. robots.txt dan Crawl-delay aktif secara default; robots yang tidak tersedia karena 401/403/5xx membuat origin ditolak untuk run tersebut, sedangkan 404/410 berarti tidak ada aturan. Request robots.txt tidak memicu pergantian agent.

User-Agent/header kustom membantu kompatibilitas, tetapi tidak menjamin tidak terdeteksi bot. HttpClient bukan browser: tidak mereplikasi fingerprint TLS, JavaScript, CAPTCHA, maupun login interaktif. Tidak ada rotasi IP atau penyelesaian CAPTCHA otomatis. Preset browser hanyalah string contoh yang dapat diedit.

## Keputusan dan batasan

- **Pro:** portable, UI async, penggunaan request terukur, session HTTP terisolasi, file lama dilindungi, status/retry persisten, path lokal dapat diverifikasi.
- **Con:** crawler HTTP statis; SPA, URL yang dibentuk JS, service worker, ES-module import, CSS escape/grammar kompleks, meta refresh, API runtime, dan CSP/browser file-origin restrictions tidak dijamin bekerja offline. CSS parsing saat ini menangani pola URL/import umum; bukan full CSSOM. Kesamaan visual seluruh situs tidak dijamin seperti render browser.
- **Efek jangka panjang:** AngleSharp/parser dan runtime self-contained harus dipublish ulang untuk pembaruan keamanan. Cache sumber menambah penggunaan disk; file valid dipakai sebagai snapshot dan tidak diperiksa kesegarannya ke server. Batas total adalah byte download run berjalan, tidak termasuk file cache lama atau hasil rewrite. Login interaktif/SPA membutuhkan fitur browser engine terpisah.
- HttpClient dipakai langsung karena target berupa HTTP/HTML umum beserta cookie/header server asal, bukan API envelope OPX.

## Build, test, publish

Versi 1.0.1 membedakan pesan 401, 403, dan header challenge browser. Pesan hanya mencatat apakah Cookie/Authorization dikirim, tanpa nilainya. 403 saja tidak membuktikan cookie salah. Arahkan mouse ke kolom Keterangan untuk membaca pesan lengkap.

Probe satu halaman dengan engine yang sama (tanpa cookie/header autentikasi; maksimal 1 URL):

```powershell
dotnet run --project tests/WebScrapper.Tests -c Release -- --probe https://biuan.com/BOELINES/ artifacts/probe-biuan
```

Probe lokal 4 Oktober 2026 berhasil HTTP 200 tanpa cookie pada halaman BOELINES. Ini hanya bukti akses halaman tersebut saat pengujian; bukan bukti seluruh situs/semua percobaan akan berhasil.

```powershell
dotnet build WebScrapper.slnx -c Release
dotnet run --project tests/WebScrapper.Tests -c Release
powershell -NoProfile -File scripts/publish-portable.ps1
```

Publish menghasilkan `artifacts/portable-win-x64/WebScrapper.exe` dan ZIP beserta SHA-256. Runtime .NET disertakan; native runtime dapat diekstrak oleh .NET ke cache per pengguna saat aplikasi pertama dijalankan. ARM64 dapat dipublish dengan `-Runtime win-arm64` (harus diuji pada perangkat ARM64).

Pengujian memakai server HTTP loopback: retry/cancel, cookie/auth scope, robots, struktur folder/relative URL, cache/hash, save/load, sinkronisasi file/status, dan recovery target gagal. Ini tidak membuktikan akses atau rendering situs eksternal tertentu.
