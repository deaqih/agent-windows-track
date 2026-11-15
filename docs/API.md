# API v1

Base produksi: `https://boneiset.awancode.com`. Seluruh kredensial dikirim melalui HTTPS, bukan parameter URL.
Admin: `Authorization: Bearer <AdminApiKey>`. Device: `Authorization: Bearer <DeviceKey>`.
API tetap hanya menerima Bearer key; cookie dashboard tidak memberi akses API admin. CORS lintas origin tidak diaktifkan.
Dashboard menggunakan cookie HttpOnly, SameSite Strict, Secure pada produksi, serta antiforgery untuk semua formulir POST.
Halaman Admin: pendaftaran, kebijakan, pengguna dan audit. Halaman Viewer/Admin: aset, aplikasi, lokasi, ekspor dan akun sendiri.

## Pendaftaran

`POST /api/v1/admin/enrollment-tokens` menghasilkan `{ token, expiresInMinutes: 60 }`.
Token hanya boleh digunakan sekali. Token tidak sama dengan kunci admin atau kunci perangkat.

`POST /api/v1/enroll`:

```json
{ "token": "<single-use-token>", "machineName": "LAPTOP-IT-01" }
```

Balasan berisi `deviceId`, `deviceKey`, `reportIntervalSeconds: 900`, dan `inventoryIntervalSeconds: 86400`.
Server hanya menyimpan hash device key. Kunci mentah hanya dikembalikan pada balasan pendaftaran.
Jika balasan pendaftaran hilang, admin harus memeriksa perangkat yang terbentuk, mencabutnya jika perlu, lalu membuat token baru.

## Laporan perangkat

`POST /api/v1/devices/{deviceId}/reports`:

```json
{
  "reportId": "c68f1239-b0a8-472f-a184-580100410e84",
  "capturedAt": "2026-11-15T08:00:00Z",
  "location": {
    "status": "available",
    "latitude": -6.2,
    "longitude": 106.8,
    "accuracyMeters": 50,
    "measuredAt": "2026-11-15T07:59:45Z",
    "source": "WiFi"
  },
  "inventory": null
}
```

Koordinat contoh sintetis. Ganti ID dan waktu ketika menguji.
Jika tidak ada lokasi, kirim hanya `location: { "status": "permission_denied" }` atau
`unavailable`, `no_user_session`, `error`. Jangan sertakan koordinat lama di laporan kegagalan.

Balasan sukses: `{ "accepted": true, "duplicate": false }`; retry ID yang sama menghasilkan `duplicate: true` tanpa menambah data.
ID harus dipertahankan ketika mengirim ulang. Laporan lebih tua dari 30 hari atau lebih dari 5 menit di masa depan ditolak.
Batas body 8 MiB. Agen perlu retry dengan jeda untuk jaringan gagal, 429, dan 5xx; jangan mengulang tanpa batas untuk 400/401.
401 setelah pendaftaran dapat berarti kunci salah atau akses dicabut; butuh tindakan IT.

Inventaris harian memakai struktur berikut pada properti `inventory`:

```json
{
  "serial": "EXAMPLE-SN",
  "manufacturer": "Example",
  "model": "Example model",
  "hardwareUuid": "example-hardware-uuid",
  "cpu": "Example CPU",
  "ramBytes": 16000000000,
  "diskBytes": 512000000000,
  "osVersion": "Windows 11",
  "agentVersion": "0.1.0",
  "softwareScopes": [
    {
      "scope": "registry:machine64",
      "complete": true,
      "applications": [
        { "key": "stable-uninstall-key", "name": "Example App", "version": "1.0", "publisher": "Example" }
      ]
    }
  ]
}
```

Pisahkan scope mesin 32/64-bit, masing-masing SID pengguna, dan aplikasi Store. Gunakan key stabil lintas versi,
bukan nama tampilan atau versi aplikasi. Jangan mengirim `complete: true` jika pembacaan scope gagal atau terpotong.
Scope yang tidak dipindai harus dihilangkan atau ditandai parsial. `complete: false` masih dapat menambah/memperbarui item yang terbaca,
tetapi tidak menghapus item yang tidak ditemukan. Aplikasi portable tidak dijamin terinventarisasi.

## API admin

| Method | Path setelah `/api/v1/admin` | Fungsi |
|---|---|---|
| GET | `/devices?query=&branch=&offset=0` | Daftar aset, status terlambat, lokasi terakhir, peringatan |
| GET | `/devices/{id}` | Detail hardware dan penempatan |
| PUT | `/devices/{id}/asset` | Ubah metadata aset |
| GET | `/devices/{id}/locations?offset=0` | Lokasi dalam masa retensi |
| GET | `/applications?query=&deviceId=&offset=0` | Aplikasi yang masih terdeteksi |
| GET | `/devices/{id}/software-events?offset=0` | Riwayat observasi software |
| POST | `/devices/{id}/revoke` | Tolak laporan perangkat selanjutnya |
| GET | `/software-policy` | Daftar nama aplikasi yang ditandai |
| PUT | `/software-policy` | `{ "name": "Example App", "remove": false }` |
| GET | `/audit?offset=0` | Log tindakan admin |
| GET | `/export/assets.csv?query=&branch=` | Ekspor inventaris, bukan koordinat |

API daftar berhalaman maksimum 200 baris. Tambahkan offset 200 sampai halaman kosong; gunakan saat dataset stabil untuk ekspor konsisten.
Kebijakan software memakai kesamaan nama persis tanpa membedakan huruf besar/kecil, bukan pemblokiran instalasi atau analisis malware.
Status terlambat memperhitungkan waktu penerimaan DAN waktu pengambilan; laporan lama yang baru diunggah tidak mengubah perangkat menjadi aktif.

Body metadata aset:

```json
{ "assetTag": "IT-0001", "ownerName": "Employee", "department": "IT", "branch": "Main office", "lifecycle": "In use" }
```

Status: `Stock`, `In use`, `Loan`, `Repair`, `Lost`, `Retired`. Asset tag unik; SN tidak dijadikan identitas utama.
Semua edit inventaris manual adalah terpisah dari hasil pengambilan agent. Tidak ada API remote shell atau remote wipe.
