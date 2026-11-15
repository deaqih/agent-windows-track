using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Boneiset.Server.Pages;

[IgnoreAntiforgeryToken]
public sealed class ErrorModel : DashboardPage
{
    public int Code { get; private set; }
    public IActionResult OnGet(int code = 500)
    {
        Code = code is 400 or 401 or 403 or 404 or 429 or 503 ? code : 500;
        Response.StatusCode = Code;
        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (feature?.Path.StartsWith("/api/", StringComparison.Ordinal) == true)
            return new ObjectResult(new { error = "Server could not complete the request." }) { StatusCode = Code };
        return Page();
    }
    public IActionResult OnPost(int code = 500) => OnGet(code);
}
