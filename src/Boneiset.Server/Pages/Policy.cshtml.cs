using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Boneiset.Server.Pages;

public sealed class PolicyModel(Store store) : DashboardPage
{
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public bool Confirm { get; set; }
    public JsonElement Rows { get; private set; }
    public async Task OnGetAsync() => Rows = await store.PoliciesAsync();
    public Task<IActionResult> OnPostAddAsync() => SaveAsync(false);
    public Task<IActionResult> OnPostRemoveAsync() => SaveAsync(true);
    private async Task<IActionResult> SaveAsync(bool remove)
    {
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(Name) || Name.Length > 500 || (remove && !Confirm))
        {
            ModelState.AddModelError("", "Isi nama aplikasi (maksimal 500 karakter); penghapusan memerlukan konfirmasi.");
            await OnGetAsync(); return Page();
        }
        await store.SetPolicyAsync(Name.Trim().ToLowerInvariant(), remove, Actor);
        Notice = remove ? "Penanda aplikasi dihapus." : "Aplikasi ditandai untuk ditinjau.";
        return RedirectToPage();
    }
}
