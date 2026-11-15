using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Boneiset.Server.Pages;

public sealed class LoginModel(Store store) : DashboardPage
{
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? RedirectToPage("/Index") : Page();
    public async Task<IActionResult> OnPostAsync()
    {
        var user = ModelState.IsValid ? await store.AuthenticateAsync(Username ?? "", Password ?? "") : null;
        Password = "";
        ModelState.Remove(nameof(Password));
        if (user is null)
        {
            ModelState.AddModelError("", "Tidak bisa masuk. Periksa kredensial atau tunggu 15 menit jika akun terkunci.");
            return Page();
        }
        await HttpContext.SignInAsync(user.Principal(), new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
