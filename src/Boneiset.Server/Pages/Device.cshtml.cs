using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Boneiset.Server.Pages;

public sealed class DeviceModel(Store store, IConfiguration configuration) : DashboardPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "overview";
    [BindProperty] public AssetUpdate Asset { get; set; } = new("", "", "", "", "In use");
    [BindProperty] public bool ConfirmRevoke { get; set; }
    public JsonElement Device { get; private set; }
    public JsonElement Rows { get; private set; }
    public JsonElement Inventory => Device.GetProperty("inventory");
    public int Retention => configuration.GetValue("LocationRetentionDays", 30);
    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return NotFound();
        Asset = new(Device.Text("asset_tag"), Device.Text("owner_name"), Device.Text("department"), Device.Text("branch"), Device.Text("lifecycle"));
        return Page();
    }
    private async Task<bool> LoadAsync()
    {
        if (Tab is not ("overview" or "locations" or "software" or "events" or "history")) Tab = "overview";
        if (Tab == "history" && !IsAdmin) Tab = "overview";
        var devices = await store.DeviceAsync(Id);
        if (devices.GetArrayLength() == 0) return false;
        Device = devices[0];
        Rows = Tab switch
        {
            "locations" => await store.LocationsAsync(Id, SafeOffset, Retention),
            "software" => await store.ApplicationsAsync("", Id, SafeOffset),
            "events" => await store.SoftwareEventsAsync(Id, SafeOffset),
            "history" => await store.DeviceAuditAsync(Id, SafeOffset),
            _ => default
        };
        return true;
    }
    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!IsAdmin) return Forbid();
        Tab = "overview";
        if (Asset is not null) Asset = Asset with { OwnerName = Asset.OwnerName ?? "", Department = Asset.Department ?? "", Branch = Asset.Branch ?? "" };
        if (!ModelState.IsValid || Asset is null || !Asset.IsValid()) ModelState.AddModelError("", "Periksa tag aset, status, dan panjang isian (maksimal 200 karakter).");
        else
        {
            try
            {
                if (!await store.UpdateAssetAsync(Id, Asset, Actor)) return NotFound();
                Notice = "Data aset diperbarui dan tercatat dalam audit.";
                return RedirectToPage(new { Id });
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            { ModelState.AddModelError("", "Tag aset sudah digunakan perangkat lain."); }
        }
        if (!await LoadAsync()) return NotFound();
        return Page();
    }
    public async Task<IActionResult> OnPostRevokeAsync()
    {
        if (!IsAdmin) return Forbid();
        if (!ConfirmRevoke) { Notice = "Centang konfirmasi sebelum mencabut akses."; return RedirectToPage(new { Id }); }
        if (!await store.RevokeAsync(Id, Actor)) { Notice = "Perangkat tidak ditemukan atau akses sudah dicabut."; return RedirectToPage(new { Id }); }
        Notice = "Akses agent dicabut. Laporan berikutnya akan ditolak; data lama tetap tersimpan.";
        return RedirectToPage(new { Id });
    }
}
