using Microsoft.AspNetCore.Mvc;

namespace Boneiset.Server.Pages;

public sealed class EnrollmentModel(Store store) : DashboardPage
{
    [BindProperty] public bool Authorized { get; set; }
    public string? Token { get; private set; }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!Authorized) { ModelState.AddModelError("", "Konfirmasikan kewenangan pendaftaran perangkat."); return Page(); }
        Token = await store.IssueTokenAsync(Actor);
        return Page();
    }
}
