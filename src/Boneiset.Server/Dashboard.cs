using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boneiset.Server;

public abstract class DashboardPage : PageModel
{
    [TempData] public string? Notice { get; set; }
    public string Actor => DashboardSecurity.Actor(User);
    public bool IsAdmin => User.IsInRole("Admin");
    [BindProperty(SupportsGet = true)] public int Offset { get; set; }
    public int SafeOffset => Math.Clamp(Offset, 0, 1000000);
    public static string Filter(string? input) => (input ?? "").Trim()[..Math.Min((input ?? "").Trim().Length, 200)];
}

public static class Display
{
    public static string Text(this JsonElement row, string field) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(field, out var value)
        && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
    public static bool Flag(this JsonElement row, string field) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.True;
    public static string Time(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.UtcDateTime.ToString("dd MMM yyyy HH:mm 'UTC'", CultureInfo.GetCultureInfo("id-ID")) : "Belum diterima";
    public static string Location(string value) => value switch
    {
        "available" => "Koordinat tersedia", "permission_denied" => "Izin lokasi ditolak", "no_user_session" => "Tidak ada sesi pengguna",
        "error" => "Gagal membaca lokasi", _ => "Lokasi tidak tersedia"
    };
    public static string Lifecycle(string value) => value switch
    {
        "Stock" => "Stok", "In use" => "Digunakan", "Loan" => "Dipinjamkan", "Repair" => "Perbaikan", "Lost" => "Hilang", "Retired" => "Pensiun", _ => value
    };
    public static string Event(string value) => value switch
    {
        "first-detected" => "Pertama terdeteksi", "detected-again" => "Terdeteksi kembali", "version-changed" => "Versi berubah",
        "no-longer-detected" => "Tidak terdeteksi lagi", _ => value
    };
    public static string Size(string value) => long.TryParse(value, out var bytes) && bytes > 0
        ? $"{bytes / 1073741824d:0.#} GiB" : "Belum tersedia";
    public static byte[] AssetCsv(JsonElement rows)
    {
        var columns = new[] { "id", "asset_tag", "machine_name", "serial", "owner_name", "department", "branch", "lifecycle", "last_received", "last_captured" };
        var csv = new StringBuilder(string.Join(',', columns) + "\r\n");
        foreach (var row in rows.EnumerateArray()) csv.AppendLine(string.Join(',', columns.Select(column => CsvCell(row.Text(column)))));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }
    private static string CsvCell(string value)
    {
        var trimmed = value.TrimStart();
        if ((trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) || value.Any(character => character is '\t' or '\r' or '\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
