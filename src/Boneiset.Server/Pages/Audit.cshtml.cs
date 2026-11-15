using System.Text.Json;

namespace Boneiset.Server.Pages;

public sealed class AuditModel(Store store) : DashboardPage
{
    public JsonElement Rows { get; private set; }
    public async Task OnGetAsync() => Rows = await store.AuditAsync(SafeOffset);
}
