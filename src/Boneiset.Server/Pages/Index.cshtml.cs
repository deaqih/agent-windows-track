using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Boneiset.Server.Pages;

public sealed class IndexModel(Store store, IConfiguration configuration) : DashboardPage
{
    [BindProperty(SupportsGet = true)] public string? Query { get; set; }
    [BindProperty(SupportsGet = true)] public string? Branch { get; set; }
    public JsonElement Rows { get; private set; }
    public int OfflineMinutes => configuration.GetValue("OfflineAfterMinutes", 60);
    public async Task OnGetAsync() => Rows = await store.DevicesAsync(Filter(Query), Filter(Branch), SafeOffset, OfflineMinutes, configuration.GetValue("LocationRetentionDays", 30));
    public async Task<IActionResult> OnPostExportAsync()
    {
        var rows = await store.ExportDevicesAsync(Filter(Query), Filter(Branch));
        if (rows.GetArrayLength() > 10000)
        {
            ModelState.AddModelError("", "Ekspor melebihi 10.000 aset. Persempit pencarian atau cabang.");
            await OnGetAsync();
            return Page();
        }
        return File(Display.AssetCsv(rows), "text/csv; charset=utf-8", "boneiset-assets.csv");
    }
}
