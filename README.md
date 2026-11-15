# Boneiset

Sistem inventaris perangkat perusahaan. Target domain: `https://boneiset.awancode.com`.

## Status implementasi

API ASP.NET Core 10, PostgreSQL, dashboard Razor Pages, dan akun individual sudah diimplementasikan.
Agent Windows, installer, dan pengambilan lokasi nyata BELUM diimplementasikan.
Belum ada perubahan DNS atau deployment ke domain produksi. Data pengujian sepenuhnya sintetis.

Dashboard menyediakan:

- Login Admin/Viewer; kata sandi di-hash, sesi maksimal 8 jam, proteksi CSRF, dan pembatasan percobaan masuk.
- Register aset, pencarian/cabang, detail hardware, penempatan, aplikasi, histori lokasi dan ekspor CSV.
- Pembuatan token pendaftaran, konfirmasi pencabutan agent, kebijakan aplikasi, pengguna, dan audit beridentitas individual.
- Pembuatan/penonaktifan akun, ganti kata sandi sendiri, dan pemulihan sandi melalui operator server.
- Tampilan responsif dengan waktu UTC, status data kosong/gagal, dan label lokasi historis. Tidak memanggil layanan peta eksternal.

Tampilan masih draf tanpa arahan merek yang disetujui. Keputusan tampilan tercatat di `DESIGN.md`.

API sudah mendukung:

- Kode pendaftaran satu kali, kedaluwarsa satu jam; kredensial berbeda untuk setiap perangkat.
- Inventaris hardware, identitas aset internal, pemegang, divisi, cabang, dan status aset.
- Laporan lokasi dengan waktu pengambilan, waktu penerimaan, sumber, akurasi, dan status izin.
- Kontrak pengiriman 900 detik / 15 menit dan inventaris harian, dikembalikan saat pendaftaran.
- Pengiriman ulang idempoten; laporan tertunda tidak menimpa inventaris terbaru.
- Inventaris software per lingkup, riwayat perubahan, pencarian, dan kebijakan nama aplikasi.
- Pemindaian parsial tidak dianggap sebagai penghapusan aplikasi. Lingkup pengguna yang tidak dipindai tetap dipertahankan.
- Penanda perangkat terlambat melapor, SN duplikat, dan aplikasi yang cocok dengan daftar peringatan.
- Ekspor CSV inventaris, log perubahan admin, pencabutan akses perangkat, dan retensi lokasi 30 hari.

Jadwal 15 menit baru merupakan kontrak API, bukan bukti agent sudah mengirim lokasi.
Tahap agent harus diuji pada Windows 10/11 nyata: izin lokasi, sesi pengguna, restart, sleep, antrean offline, dan geolokasi aktual.
Pemisahan yang direncanakan: layanan Windows untuk inventaris/pengiriman dan pendamping sesi pengguna untuk lokasi.
Jangan menganggap lokasi tetap tersedia saat perangkat mati, sleep, offline, atau izin lokasi ditolak.

## Menjalankan lokal

Prasyarat: .NET SDK 10 dan PostgreSQL 18. Gunakan database pengujian khusus, bukan database produksi.
Pengujian sesi pengembangan ini menggunakan SDK 10.0.404 dan PostgreSQL 18.1 lokal.
SDK dipasang lokal di `.tools/dotnet`, bukan mengganti instalasi .NET sistem.
Pada komputer ini, tambahkan `$env:PATH = "$PWD\.tools\dotnet;$env:PATH"` di terminal PowerShell sebelum perintah `dotnet`.

PowerShell:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5087'
$env:ConnectionStrings__Database = Read-Host 'Connection string database pengujian'
$env:AdminApiKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --project src/Boneiset.Server
```

Simpan kunci admin di pengelola kata sandi bila ingin menggunakannya pada terminal lain.
`GET /health` memeriksa koneksi database; `/` membuka dashboard dan mengarahkan pengguna anonim ke `/Login`.
HTTP hanya diperbolehkan pada lingkungan Development. Bind Development ke loopback saja.

Setelah konfigurasi lingkungan dan database siap, buat admin awal dalam terminal terpisah dengan konfigurasi sama:

```powershell
dotnet build src/Boneiset.Server -c Release
./tools/dashboard-account.ps1 -Username admin
```

Skrip meminta kata sandi dua kali tanpa menampilkannya (14-128 karakter). Tidak ada kata sandi bawaan.
Pembuatan akun awal ditolak jika sudah ada pengguna. Akun berikutnya dibuat melalui menu Pengguna oleh Admin.
Jika lupa kata sandi, operator yang memiliki akses server/database dapat menjalankan:

```powershell
./tools/dashboard-account.ps1 -Username admin -ResetPassword
```

Reset dicatat di audit dan membatalkan seluruh sesi lama; tidak mengaktifkan akun yang dinonaktifkan.
Kredensial dikirim melalui lingkungan proses anak, bukan argumen command line. Jangan mengaktifkan pencatatan lingkungan proses berisi rahasia.

Alternatif Docker lokal: gunakan PostgreSQL pengujian yang sudah berjalan di Docker dan network-nya.
Isi `.env` dengan koneksi database pengujian, bukan koneksi server utama. Nama proyek hanya memisahkan
container/key ring; nama proyek TIDAK mengisolasi database eksternal yang ditunjuk connection string.

```powershell
./tools/new-env.ps1
```

Setelah mengisi `.env` sesuai bagian produksi (gunakan `TRUSTED_PROXY=127.0.0.1` untuk uji lokal tanpa proxy):

```powershell
docker compose -p boneiset-dev -f compose.yaml -f compose.dev.yaml config --quiet
docker compose -p boneiset-dev -f compose.yaml -f compose.dev.yaml up -d --build server
```

Skrip hanya menghasilkan API key, bukan akun/password PostgreSQL atau sertifikat TLS.
API lokal tersedia pada `http://127.0.0.1:5087` jika port contoh tidak diubah.
Tidak ada container PostgreSQL atau reverse proxy baru yang dibuat.

## Menguji

```powershell
dotnet build src/Boneiset.Server -c Release
dotnet run --project checks/Boneiset.Checks
$env:BONEISET_TEST_ADMIN_KEY = $env:AdminApiKey
dotnet run --project checks/Boneiset.Checks -- http://127.0.0.1:5087
./checks/DeploymentChecks.ps1
```

Pemeriksaan deployment menguji pembuatan `.env` privat dan penolakan penimpaan rahasia.
Opsional: tambahkan `-ComposeExecutable /path/ke/docker-compose` untuk validasi tiga konfigurasi Compose
dan penolakan pengaturan kosong, memakai nilai sintetis tanpa Docker daemon atau koneksi database.

Opsional: set `BONEISET_TEST_DATABASE` ke connection string PostgreSQL loopback bernama `boneiset_test`
untuk pemeriksaan retensi, token kedaluwarsa, hash, dan integrasi dashboard (autentikasi, CSRF, peran, audit, dan pemulihan akun).
Pemeriksaan dashboard membuat akun sementara dan menghapus akun tersebut saat selesai; fixture aset tetap berlabel pengujian.
Uji batas login menghabiskan kuota IP pada akhir pengujian. Tunggu 60 detik atau restart server lokal sebelum mengulang.

Pemeriksaan HTTP sengaja hanya menerima alamat loopback. Pemeriksaan menambah perangkat dan aplikasi sintetis pada database pengujian.
Tidak memasang agent, tidak mengambil lokasi komputer ini, dan tidak mengirim data ke domain produksi.

## Administrasi API

```powershell
./tools/admin.ps1 -Action enrollment-token -ServerUrl http://127.0.0.1:5087
./tools/admin.ps1 -Action devices -ServerUrl http://127.0.0.1:5087
./tools/admin.ps1 -Action export -ServerUrl http://127.0.0.1:5087 -OutputPath assets.csv
```

Skrip meminta API key secara tersembunyi, atau membaca `BONEISET_ADMIN_KEY`.
Kode pendaftaran dan device key adalah rahasia; jangan taruh di URL, log, atau repo.
Admin API menggunakan satu kunci bootstrap untuk pilot, bukan pengganti login/RBAC multi-admin.
Identitas audit dari kunci API tetap `bootstrap-admin`; dashboard mencatat `user:<id>:<username>`.
Jangan menyimpan admin API key di JavaScript atau penyimpanan browser.

## Persiapan produksi

Konfigurasi Compose sudah divalidasi tanpa daemon memakai nilai sintetis (host Nginx, container Nginx, dan Development).
Container aplikasi, koneksi PostgreSQL utama, serta konfigurasi Nginx/TLS pada server produksi belum diuji.
Jangan meluncurkan ke seluruh perusahaan sebelum agent, penandatanganan installer, penguatan akses,
dan uji lapangan selesai. Tidak ada kredensial default.

Compose hanya menjalankan aplikasi Boneiset. PostgreSQL Docker dan Nginx memakai instalasi yang sudah ada.
Tidak membuat container database/proxy, tidak mengambil port 80/443, dan tidak mengelola volume PostgreSQL utama.
Panduan mengasumsikan PostgreSQL, Boneiset, dan Nginx berada pada satu host Docker/Linux.
Untuk PostgreSQL lintas host, gunakan koneksi privat dan verifikasi TLS database; jangan membuka port database ke internet.

### Database pada PostgreSQL yang sudah ada

Buat database dan role khusus Boneiset, bukan menaruh tabel di database aplikasi lain atau memakai akun superuser.
Melalui sesi `psql` sebagai administrator PostgreSQL yang sudah ada, jalankan sekali:

```sql
CREATE ROLE boneiset LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
\password boneiset
CREATE DATABASE boneiset OWNER boneiset;
```

`\password` meminta sandi tanpa memasukkannya ke riwayat perintah SQL. Jika role/database sudah ada, periksa kepemilikan
dan izin; jangan hapus atau buat ulang. Aplikasi membuat tabel saat startup di database yang ditunjuk.
PostgreSQL 18.1 sudah diuji lokal; versi PostgreSQL server utama belum diverifikasi.

Gunakan network Docker user-defined yang sudah terhubung ke PostgreSQL. `POSTGRES_NETWORK` adalah nama network
aktual, termasuk awalan proyek bila ada, bukan nama service Compose. Compose mengharuskan network tersebut sudah ada.
`Host` pada connection string adalah nama/alias PostgreSQL di network itu dan port internalnya, bukan `localhost`
atau port PostgreSQL yang dipublikasikan ke host. Jika PostgreSQL masih memakai network `bridge` bawaan,
siapkan network user-defined dan daftarkan secara persisten dalam konfigurasi PostgreSQL milik operator.

### Konfigurasi aplikasi

Jalankan `tools/new-env.ps1` dengan PowerShell 7. Skrip membuat `.env` berakses terbatas, menghasilkan API key,
dan menolak menimpa file yang sudah ada. Isi nilai kosong berikut sesuai infrastruktur; jangan kirim sandi di chat:

| Variabel | Isi |
| --- | --- |
| `DATABASE_CONNECTION_STRING` | Koneksi database/role khusus Boneiset pada PostgreSQL yang sudah ada. |
| `POSTGRES_NETWORK` | Nama network Docker PostgreSQL yang sudah ada. |
| `TRUSTED_PROXY` | Satu alamat IP sumber Nginx sebagaimana terlihat oleh container Boneiset. |
| `BONEISET_HTTP_PORT` | Port loopback host untuk Nginx di host, default `5087`. |
| `NGINX_NETWORK` | Diisi hanya jika Nginx berada di Docker: nama network proxy yang sudah ada. |
| `ADMIN_API_KEY` | Dihasilkan skrip; simpan di pengelola rahasia, bukan browser. |

Contoh format koneksi di `.env` (ganti seluruh placeholder):

```dotenv
DATABASE_CONNECTION_STRING='Host=NAMA_ALIAS_POSTGRES;Port=5432;Database=boneiset;Username=boneiset;Password=GANTI_DENGAN_SANDI_ROLE'
```

Kutip tunggal `.env` mencegah ekspansi `$` dalam password. Password yang memuat kutip atau pemisah connection string
tetap perlu escaping sesuai format Compose dan Npgsql; password acak panjang berbentuk heksadesimal menghindari ambiguitas ini.
Jangan menampilkan `.env` atau hasil `docker compose config` tanpa `--quiet` ke log karena mengandung rahasia.
Mengubah `.env` tidak otomatis mengganti password role PostgreSQL. Rotasikan keduanya secara terkoordinasi.

### Nginx berjalan di host

1. Pastikan PostgreSQL siap, DNS `boneiset.awancode.com` mengarah ke server, dan sertifikat TLS domain sudah tersedia melalui mekanisme Nginx yang digunakan. Jika ada AAAA, periksa IPv6 juga.
2. Isi `TRUSTED_PROXY` dengan IP sumber koneksi Nginx ke container. Pada Docker bridge Linux biasanya IP gateway network, bukan otomatis `127.0.0.1`. Periksa konfigurasi network dan verifikasi jalur permintaan.
3. Jalankan `docker compose config --quiet`, lalu `docker compose up -d --build server`. Jangan gunakan override Development pada produksi.
4. Gunakan `deploy/nginx.conf` sebagai virtual host domain, bukan mengganti konfigurasi global Nginx. Sesuaikan lokasi sertifikat dan `proxy_pass` jika port bukan `5087`. Jangan buat virtual host duplikat.
5. Jalankan `nginx -t`, lalu reload Nginx hanya jika pemeriksaan berhasil. Uji `https://boneiset.awancode.com/health` dan halaman login melalui HTTPS.
6. Buat akun awal melalui `./tools/dashboard-account.ps1 -Username admin -Docker` dari host dengan PowerShell 7. Periksa backup/restore dan hak akses di staging sebelum akses perusahaan.

Port aplikasi hanya dipublikasikan pada `127.0.0.1`, bukan seluruh antarmuka host. Pertahankan firewall pembatas port tersebut.
Server hanya memproses satu hop forwarded headers dari proxy tepercaya (serta loopback bawaan framework).
Contoh Nginx menimpa header IP/scheme dari klien, bukan mempercayai nilai kiriman klien.
Jika memakai CDN/load balancer sebelum Nginx, konfigurasi trust/IP asli perlu ditinjau terpisah; jangan mempercayai semua jaringan.
Respons `HTTPS required.` melalui Nginx menandakan jalur/header HTTPS atau `TRUSTED_PROXY` belum sesuai; jangan mengubah ke Development untuk mengakalinya.
Pembatasan permintaan bergantung pada IP ini; verifikasi bahwa IP klien tidak semuanya terbaca sebagai IP proxy.

### Nginx berjalan di Docker

Gunakan network proxy yang sudah ada dan terpisah dari network PostgreSQL; Nginx tidak perlu bergabung ke network database.
Isi `NGINX_NETWORK` dan set `TRUSTED_PROXY` ke IP container Nginx pada network proxy. Pastikan alamat ini stabil/reserved
dalam Compose Nginx milik operator; perubahan IP tanpa memperbarui trust akan membuat permintaan HTTPS ditolak.

```sh
docker compose -f compose.yaml -f compose.nginx.yaml config --quiet
docker compose -f compose.yaml -f compose.nginx.yaml up -d --build server
```

Gunakan Docker Compose 2.24.4 atau lebih baru untuk override ini. Override menghapus publikasi port host dan menambahkan
alias `boneiset-server` pada network proxy. Ubah `proxy_pass` pada virtual host menjadi `http://boneiset-server:8080`.
`127.0.0.1:5087` tidak menunjuk Boneiset dari dalam container Nginx. Pastikan sertifikat tersedia di dalam container Nginx.
Jalankan `nginx -t` dan reload di container Nginx yang sudah ada. Setelah container Boneiset dibuat ulang, reload Nginx
agar upstream DNS di-resolve ulang. Gunakan kedua file Compose secara konsisten saat pembaruan aplikasi.

Kedua pola memakai PostgreSQL yang sama tanpa mengubah container/data utama. Hanya database khusus Boneiset yang ditulisi.
Backup database Boneiset secara terpisah. Jangan menjalankan `docker compose down -v` pada produksi: volume key ring
akan hilang walaupun PostgreSQL eksternal tidak dikelola Compose ini.

Volume `dashboard_keys` menyimpan kunci proteksi cookie secara persisten; direktori image dimiliki pengguna aplikasi dengan mode 0700.
Pada Windows, kunci baru dilindungi DPAPI untuk identitas proses. Pada Linux, kunci di volume tidak otomatis terenkripsi;
batasi akses host/volume dan gunakan enkripsi disk atau konfigurasi sertifikat sebelum membuka akses perusahaan.
Untuk enkripsi key ring berbasis sertifikat, pasang PFX privat secara read-only di container dan set
`DataProtection__CertificatePath` serta `DataProtection__CertificatePassword` melalui pengelola rahasia.
Jangan commit PFX. Backup key ring dan sertifikat secara terproteksi; pertahankan sertifikat lama saat rotasi.
Kehilangan key ring membuat cookie/sesi lama tidak dapat dibaca. Penggantian sertifikat perlu diuji di staging.

Admin dapat mengubah semua aset dan pengguna; Viewer dapat melihat lokasi seluruh perangkat dan mengekspor inventaris.
Belum ada pembatasan per cabang, MFA, atau SSO. Batasi pilot ke jaringan privat/VPN sampai kebijakan akses perusahaan disetujui.

## Batas keamanan dan retensi

- Device key disimpan sebagai hash SHA-256; rahasia sumber berukuran 256 bit. Agent tetap harus melindungi kunci lokalnya.
- Lokasi yang dikirim agent bukan bukti anti-pemalsuan. Tidak ada jaminan anti-tamper atau pelacakan setelah reinstall.
- Waktu disimpan dan ditampilkan dalam UTC dengan label eksplisit.
- Lokasi lebih tua dari retensi tidak ditampilkan dan dihapus setiap jam; batas retensi dapat diatur 1..30 hari.
- Metadata laporan disimpan 31 hari untuk idempotensi. Log audit, riwayat software, serta inventaris memiliki kebijakan penyimpanan terpisah yang belum diotomatisasi.
- Retensi database tidak menghapus backup otomatis; tentukan masa simpan backup, akses lokasi, dan pemberitahuan karyawan sebelum produksi.
- Ekspor dibatasi 10.000 aset per permintaan; lebih besar menghasilkan error, bukan file terpotong. Gunakan filter atau API berhalaman.
- Pembatasan permintaan awal: 240 laporan/API admin per IP per menit, 30 pendaftaran per IP per menit. Evaluasi untuk kantor besar di balik NAT.
- Dashboard: 180 permintaan/IP/menit; login memiliki batas tambahan 20 permintaan/IP/menit. Lima sandi salah mengunci akun 15 menit.
- Skema idempoten dibuat saat startup dengan advisory lock; tahap dashboard menambah tabel `dashboard_users` tanpa mengubah data aset.
  Sebelum migrasi destruktif, tambahkan migrasi berversi dan uji backup/restore.

Kontrak API rinci: `docs/API.md`.
