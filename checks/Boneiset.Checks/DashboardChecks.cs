using System.Net;
using System.Text.RegularExpressions;
using Boneiset.Server;
using Boneiset.Shared;
using Npgsql;

internal static class DashboardChecks
{
    public static async Task RunAsync(Uri baseUri, NpgsqlDataSource database, Action<bool, string> check)
    {
        var store = new Store(database);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var adminName = "test-admin-" + suffix;
        var viewerName = "test-viewer-" + suffix;
        var password = Protocol.NewSecret();
        var nextPassword = Protocol.NewSecret();
        using var anonymous = Browser(baseUri);
        using var admin = Browser(baseUri);
        using var viewer = Browser(baseUri);
        check(await store.CreateUserAsync(adminName, password, "Admin", "dashboard-checks") is null, "dashboard admin created");
        check(await store.CreateUserAsync(viewerName, password, "Viewer", "dashboard-checks") is null, "dashboard viewer created");
        try
        {
            check(await store.CreateUserAsync(adminName.ToUpperInvariant(), password, "Admin", "dashboard-checks") is not null, "usernames unique ignoring case");
            check(await store.CreateUserAsync("bad", "short", "Admin", "dashboard-checks") is not null, "weak password rejected");
            check(await store.CreateUserAsync("bad user", password, "Admin", "dashboard-checks") is not null, "invalid username rejected");
            check(await store.CreateUserAsync("test-invalid-role", password, "Owner", "dashboard-checks") is not null, "invalid role rejected");
            check(await store.CreateUserAsync("test-second-bootstrap", password, "Admin", "dashboard-checks", true) is not null, "bootstrap closed after first account");
            await using (var command = database.CreateCommand("SELECT password_hash FROM dashboard_users WHERE username=@name"))
            {
                command.Parameters.AddWithValue("name", adminName);
                var storedHash = (string)(await command.ExecuteScalarAsync())!;
                check(storedHash != password && storedHash.Length > 60, "password stored as salted hash");
            }
            var redirect = await anonymous.GetAsync("/");
            check(redirect.StatusCode == HttpStatusCode.Redirect && new Uri(baseUri, redirect.Headers.Location!).AbsolutePath == "/Login", "anonymous dashboard access redirected");
            check((await anonymous.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Username"] = adminName, ["Password"] = password }))).StatusCode == HttpStatusCode.BadRequest, "login requires antiforgery token");
            await Login(admin, adminName, password, "https://example.invalid/");
            await Login(viewer, viewerName, password);
            var index = await admin.GetAsync("/");
            var html = await index.Content.ReadAsStringAsync();
            check(index.StatusCode == HttpStatusCode.OK && html.Contains("Register aset"), "authenticated register renders");
            check(index.Headers.GetValues("Content-Security-Policy").Single().Contains("frame-ancestors 'none'"), "dashboard CSP forbids framing");
            check(index.Headers.CacheControl?.NoStore == true, "dashboard not cacheable");
            check((await admin.GetAsync("/api/v1/admin/devices")).StatusCode == HttpStatusCode.Unauthorized, "browser session does not unlock bearer admin API");
            foreach (var page in new[] { "/Enrollment", "/Users", "/Audit", "/Policy" })
            {
                check((await admin.GetAsync(page)).StatusCode == HttpStatusCode.OK, "admin can open " + page);
                var denied = await viewer.GetAsync(page);
                check(denied.StatusCode == HttpStatusCode.Redirect && denied.Headers.Location!.OriginalString.Contains("code=403"), "viewer denied " + page);
            }
            check((await admin.PostAsync("/Enrollment", new FormUrlEncodedContent(new Dictionary<string, string> { ["Authorized"] = "true" }))).StatusCode == HttpStatusCode.BadRequest, "write requires antiforgery token");
            var tokenResponse = await Form(admin, "/Enrollment", new() { ["Authorized"] = "true" });
            check(tokenResponse.StatusCode == HttpStatusCode.OK && (await tokenResponse.Content.ReadAsStringAsync()).Contains("Token berhasil dibuat"), "enrollment token page works");
            var deniedWrite = await Form(viewer, "/Enrollment", new() { ["Authorized"] = "true" }, "/");
            check(deniedWrite.StatusCode == HttpStatusCode.Redirect, "viewer cannot issue enrollment tokens");
            var csv = await Form(viewer, "/?handler=Export", new());
            check(csv.StatusCode == HttpStatusCode.OK && csv.Content.Headers.ContentType!.MediaType == "text/csv", "viewer may export inventory");
            var enrollment = (await store.EnrollAsync(new(await store.IssueTokenAsync("dashboard-checks"), "TEST-UI-<script>alert(1)</script>")))!;
            var route = "/devices/" + enrollment.DeviceId;
            html = await admin.GetStringAsync(route);
            check(html.Contains("&lt;script&gt;") && !html.Contains("<script>alert(1)</script>"), "device values HTML encoded");
            var values = new Dictionary<string, string> { ["Asset.AssetTag"] = "UI-" + suffix, ["Asset.OwnerName"] = "", ["Asset.Department"] = "", ["Asset.Branch"] = "", ["Asset.Lifecycle"] = "Stock" };
            check((await Form(viewer, route + "?handler=Save", values, route)).StatusCode == HttpStatusCode.Redirect, "viewer cannot edit asset");
            check((await Form(admin, route + "?handler=Save", values, route)).StatusCode == HttpStatusCode.Redirect, "admin can save blank optional assignment fields");
            check((await store.DeviceAsync(enrollment.DeviceId))[0].Text("asset_tag") == "UI-" + suffix, "asset update persisted");
            foreach (var tab in new[] { "overview", "locations", "software", "events", "history" })
                check((await admin.GetAsync(route + "?tab=" + tab)).StatusCode == HttpStatusCode.OK, "device tab renders " + tab);
            check((await admin.GetAsync("/devices/" + Guid.NewGuid())).StatusCode == HttpStatusCode.NotFound, "missing device returns 404");
            var policyName = "TEST-POLICY-" + suffix;
            check((await Form(admin, "/Policy?handler=Add", new() { ["Name"] = policyName }, "/Policy")).StatusCode == HttpStatusCode.Redirect, "admin can add software warning");
            check((await admin.GetStringAsync("/Policy")).Contains(policyName.ToLowerInvariant()), "policy visible");
            await Form(admin, "/Policy?handler=Remove", new() { ["Name"] = policyName, ["Confirm"] = "true" }, "/Policy");
            check(!(await admin.GetStringAsync("/Policy")).Contains(policyName.ToLowerInvariant()), "policy removal works");
            await Form(admin, route + "?handler=Revoke", new(), route);
            check(!(await store.DeviceAsync(enrollment.DeviceId))[0].Flag("revoked"), "revoke requires confirmation");
            await Form(admin, route + "?handler=Revoke", new() { ["ConfirmRevoke"] = "true" }, route);
            check((await store.DeviceAsync(enrollment.DeviceId))[0].Flag("revoked"), "confirmed device revoke persisted");
            var adminUser = (await store.AuthenticateAsync(adminName, password))!;
            var viewerUser = (await store.AuthenticateAsync(viewerName, password))!;
            check(await store.SetUserEnabledAsync(adminUser.Id, false, adminUser.Id, adminUser.Actor) is not null, "cannot disable self");
            check(await store.SetUserEnabledAsync(viewerUser.Id, false, adminUser.Id, adminUser.Actor) is null, "admin disables viewer");
            check((await viewer.GetAsync("/")).StatusCode == HttpStatusCode.Redirect, "disabled user session rejected on next request");
            await store.SetUserEnabledAsync(viewerUser.Id, true, adminUser.Id, adminUser.Actor);
            check(!await store.ValidSessionAsync(viewerUser.Id, viewerUser.Stamp), "reenabling does not resurrect old sessions");
            for (var attempt = 0; attempt < 5; attempt++) await store.AuthenticateAsync(viewerName, "incorrect-password");
            check(await store.AuthenticateAsync(viewerName, password) is null, "five failed logins lock account even with correct password");
            await using (var command = database.CreateCommand("UPDATE dashboard_users SET locked_until=now()-interval '1 second' WHERE username=@name"))
            { command.Parameters.AddWithValue("name", viewerName); await command.ExecuteNonQueryAsync(); }
            check(await store.AuthenticateAsync(viewerName, password) is not null, "lockout expires");
            await store.SetUserEnabledAsync(viewerUser.Id, false, adminUser.Id, adminUser.Actor);
            check(await store.ResetPasswordLocallyAsync(viewerName, nextPassword), "operator can reset forgotten password");
            check(await store.AuthenticateAsync(viewerName, nextPassword) is null, "password reset does not enable disabled account");
            await store.SetUserEnabledAsync(viewerUser.Id, true, adminUser.Id, adminUser.Actor);
            check(await store.AuthenticateAsync(viewerName, password) is null && await store.AuthenticateAsync(viewerName, nextPassword) is not null, "reset password replaces previous credential");
            var passwordResult = await Form(admin, "/Account", new() { ["CurrentPassword"] = password, ["NewPassword"] = nextPassword, ["ConfirmPassword"] = nextPassword });
            check(passwordResult.StatusCode == HttpStatusCode.Redirect, "password change redirects to login");
            check(!await store.ValidSessionAsync(adminUser.Id, adminUser.Stamp), "password change invalidates previous sessions");
            check(await store.AuthenticateAsync(adminName, password) is null && await store.AuthenticateAsync(adminName, nextPassword) is not null, "only new password accepted");
            await Login(admin, adminName, nextPassword);
            var logout = await Form(admin, "/Account?handler=Logout", new(), "/Account");
            check(logout.StatusCode == HttpStatusCode.Redirect && (await admin.GetAsync("/")).StatusCode == HttpStatusCode.Redirect, "logout closes session");
            await using (var command = database.CreateCommand("SELECT count(*) FROM audit WHERE actor=@actor AND action='asset-updated'"))
            { command.Parameters.AddWithValue("actor", adminUser.Actor); check((long)(await command.ExecuteScalarAsync())! > 0, "audit records individual human actor"); }
            var throttled = false;
            for (var attempt = 0; attempt < 22; attempt++)
            {
                using var response = await anonymous.GetAsync("/Login");
                if (response.StatusCode == HttpStatusCode.TooManyRequests) { throttled = true; break; }
            }
            check(throttled, "login IP rate limit enforced");
        }
        finally
        {
            await using var command = database.CreateCommand("DELETE FROM dashboard_users WHERE username=@admin OR username=@viewer");
            command.Parameters.AddWithValue("admin", adminName); command.Parameters.AddWithValue("viewer", viewerName);
            await command.ExecuteNonQueryAsync();
        }

        async Task Login(HttpClient browser, string username, string secret, string returnUrl = "/")
        {
            var result = await Form(browser, "/Login", new() { ["Username"] = username, ["Password"] = secret, ["ReturnUrl"] = returnUrl });
            check(result.StatusCode == HttpStatusCode.Redirect && result.Headers.Location!.OriginalString == "/", "login succeeds with safe local redirect");
            var cookie = result.Headers.GetValues("Set-Cookie").First(value => value.StartsWith("Boneiset.Session=", StringComparison.Ordinal));
            check(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase), "session cookie HttpOnly and SameSite Strict");
        }
    }

    private static HttpClient Browser(Uri baseUri) => new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) { BaseAddress = baseUri };

    private static async Task<HttpResponseMessage> Form(HttpClient browser, string path, Dictionary<string, string> values, string? source = null)
    {
        var html = await browser.GetStringAsync(source ?? path.Split('?')[0]);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        if (token.Length == 0) throw new Exception("Antiforgery form missing at " + (source ?? path));
        values["__RequestVerificationToken"] = WebUtility.HtmlDecode(token);
        return await browser.PostAsync(path, new FormUrlEncodedContent(values));
    }
}
