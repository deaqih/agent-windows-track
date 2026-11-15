using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Boneiset.Server.Pages;

public sealed class AccountModel(Store store) : DashboardPage
{
    [BindProperty] public string CurrentPassword { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid || !DashboardSecurity.ValidPassword(NewPassword) || NewPassword != ConfirmPassword)
            ModelState.AddModelError("", "Kata sandi baru harus 14-128 karakter dan konfirmasi harus sama.");
        else
        {
            var verified = await store.AuthenticateAsync(User.Identity!.Name!, CurrentPassword ?? "");
            if (verified is not null && verified.Id.ToString() == User.FindFirstValue(ClaimTypes.NameIdentifier)
                && verified.Stamp.ToString() == User.FindFirstValue("stamp") && await store.ChangePasswordAsync(verified, NewPassword))
            {
                await HttpContext.SignOutAsync();
                Notice = "Kata sandi diganti. Semua sesi lama berakhir; masuk kembali dengan kata sandi baru.";
                return RedirectToPage("/Login");
            }
            ModelState.AddModelError("", "Kata sandi lama tidak valid, akun terkunci, atau sesi telah berubah.");
        }
        CurrentPassword = NewPassword = ConfirmPassword = "";
        foreach (var field in new[] { nameof(CurrentPassword), nameof(NewPassword), nameof(ConfirmPassword) }) ModelState.Remove(field);
        return Page();
    }
    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync();
        return RedirectToPage("/Login");
    }
}
