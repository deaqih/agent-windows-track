using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Boneiset.Server.Pages;

public sealed class ApplicationsModel(Store store) : DashboardPage
{
    [BindProperty(SupportsGet = true)] public string? Query { get; set; }
    public JsonElement Rows { get; private set; }
    public async Task OnGetAsync() => Rows = await store.ApplicationsAsync(Filter(Query), null, SafeOffset);
}
