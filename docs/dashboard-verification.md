# Verifikasi dashboard

Tanggal: 15 November 2026. Seluruh akun, aset, aplikasi, dan koordinat untuk pengujian bersifat sintetis.
Tidak ada agent dipasang atau data komputer perusahaan dikumpulkan. Tidak ada deployment produksi.

## Build dan pemeriksaan otomatis

- .NET SDK 10.0.404, PostgreSQL 18.1 lokal; restore dengan lockfile.
- Release build: 0 warning, 0 error.
- 141 assertion protokol/HTTP/database lulus, termasuk 79 pemeriksaan API sebelumnya.
- Pengujian dashboard mencakup login, antiforgery, Admin/Viewer, penolakan cookie pada API Bearer,
  encoding HTML, pendaftaran, edit aset, kebijakan, revoke, export, audit individual, lockout,
  reset sandi, pembatalan sesi setelah disable/ganti sandi, dan pembatasan IP login.
- Pemindaian dependensi NuGet tidak melaporkan kerentanan dari sumber yang dikonfigurasi saat pengujian.
- `tools/dashboard-account.ps1` lulus pemeriksaan sintaks PowerShell.

Perintah yang dapat diulang ada di README bagian Menguji; implementasi pemeriksaan terdapat di
`checks/Boneiset.Checks/DashboardChecks.cs`. Gunakan database loopback `boneiset_test`, bukan produksi.

## Browser dan tata letak

40 assertion browser menggunakan Edge headless terisolasi lulus, tanpa JavaScript page error.

- Login, tautan lewati navigasi, menu, tab detail perangkat, perubahan penempatan, serta audit expand/collapse.
- Cari/reset aset dan aplikasi, kondisi kosong, tambah/hapus penanda aplikasi, dan unduhan CSV.
- Buat pengguna sintetis, nonaktifkan/aktifkan, akses Viewer, ganti sandi gagal/berhasil, dan logout.
- Pembuatan token dan pencabutan agent dengan konfirmasi pada fixture sintetis.
- Register diuji pada lebar 360, 390, 768, dan 1440 px. Tidak ada overflow horizontal dokumen;
  tabel lebar memiliki daerah gulir sendiri yang dapat difokuskan dengan keyboard.
- Delapan halaman dashboard diuji pada lebar 390 px: satu h1, bahasa Indonesia, dan semua input terlihat memiliki label.
- Form login ponsel ditempatkan sebelum penjelasan produk; tombol Masuk terlihat tanpa menggulir pada 390 x 844 px.
- Kontras lima pasangan teks utama dihitung: 5.39:1 hingga 12.69:1. Fokus keyboard terlihat.
- Database pengujian dihentikan sementara: halaman menampilkan pesan generik HTTP 500 tanpa detail internal,
  `/health` menghasilkan 503. Dashboard pulih setelah database dinyalakan kembali.

Screenshot dan hasil browser tersimpan lokal di `.local/`; tidak dimasukkan ke source/deployment.
Halaman penuh dengan lebih dari 200 baris belum diuji melalui klik pager browser pada sesi ini.

## Pemeriksaan desain

- Draf tanpa arahan merek; ENERGY 1 / RHYTHM 1 / MOTION 1 sesuai DESIGN.md.
- Palet hijau/kertas dengan aksen peringatan berteks; register aset menjadi fokus, bukan kartu metrik dekoratif.
- Tidak ada gambar/logo hasil generasi, statistik produksi palsu, font eksternal, layanan peta, atau tautan fitur mati.
- Kontrol yang tampil pada fixture diuji; formulir memiliki umpan balik, status pengiriman, validasi, dan konfirmasi tindakan berisiko.
- Responsif, fokus, label, kontras dan reduced-motion ditinjau. Ini bukan sertifikasi aksesibilitas menyeluruh.

## Batas rilis

Agent/installer dan geolokasi Windows nyata belum dibuat. Interval 15 menit masih kontrak server.
Docker, TLS publik, rotasi sertifikat data protection, pembatasan akses per cabang, MFA/SSO,
serta uji beban/penetrasi belum diverifikasi pada produksi. Domain tujuan tetap `boneiset.awancode.com`.
