using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Boneiset.Server.Pages;

public sealed class UsersModel(Store store) : DashboardPage
{
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Role { get; set; } = "Viewer";
    public JsonElement Rows { get; private set; }
    public async Task OnGetAsync() => Rows = await store.UsersAsync(SafeOffset);
    public async Task<IActionResult> OnPostCreateAsync()
    {
        var error = ModelState.IsValid ? await store.CreateUserAsync(Username ?? "", Password ?? "", Role, Actor) : "Isian tidak valid.";
        Password = ""; ModelState.Remove(nameof(Password));
        if (error is not null) { ModelState.AddModelError("", error); await OnGetAsync(); return Page(); }
        Notice = "Akun dibuat. Berikan kata sandi melalui kanal privat; pengguna dapat menggantinya lewat halaman Akun.";
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostStatusAsync(Guid target, bool enabled, bool confirm)
    {
        if (!ModelState.IsValid || !confirm) { Notice = "Konfirmasi perubahan status akun diperlukan."; return RedirectToPage(); }
        var error = await store.SetUserEnabledAsync(target, enabled, Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), Actor);
        Notice = error ?? "Status akun diperbarui. Sesi lama tidak berlaku lagi.";
        return RedirectToPage();
    }
}
