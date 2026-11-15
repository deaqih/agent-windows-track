using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Boneiset.Shared;
using Boneiset.Server;
using Npgsql;

var passed = 0;
var now = DateTimeOffset.UtcNow;
var location = new LocationSample("available", -6.2, 106.8, 50, now, "WiFi");
var report = new DeviceReport(Guid.NewGuid(), now, location, null);
Check(Protocol.ReportInterval == TimeSpan.FromMinutes(15), "15-minute reporting contract");
Check(Protocol.Validate(report, now) is null, "valid location");
Check(Protocol.Validate(report with { Location = new("permission_denied") }, now) is null, "permission denial is reportable");
Check(Protocol.Validate(report with { Location = location with { Latitude = 91 } }, now) is not null, "invalid latitude rejected");
Check(Protocol.Validate(report with { Location = location with { Longitude = -181 } }, now) is not null, "invalid longitude rejected");
Check(Protocol.Validate(report with { Location = location with { AccuracyMeters = double.NaN } }, now) is not null, "NaN rejected");
Check(Protocol.Validate(report with { Location = location with { AccuracyMeters = -1 } }, now) is not null, "negative accuracy rejected");
Check(Protocol.Validate(report with { Location = location with { MeasuredAt = null } }, now) is not null, "measurement time required");
Check(Protocol.Validate(report with { Location = location with { Status = "unavailable" } }, now) is not null, "unavailable cannot carry fake coordinates");
Check(Protocol.Validate(report with { CapturedAt = now.AddDays(-31) }, now) is not null, "expired capture rejected");
Check(Protocol.Validate(report with { CapturedAt = now.AddMinutes(6) }, now) is not null, "future capture rejected");
Check(Protocol.Validate(report with { ReportId = Guid.Empty }, now) is not null, "empty report ID rejected");
Check(Protocol.NewSecret().Length == 64 && Protocol.NewSecret() != Protocol.NewSecret(), "random device secrets");
var machine = new SoftwareScope("registry:machine64", true, [new("app-1", "Test Browser", "1.0", "Test Publisher")]);
var userScope = new SoftwareScope("registry:user:S-1-5-21-test", true, [new("user-app", "User Tool", "1.0", "Test")]);
var inventory = new InventorySnapshot("TEST-SN", "Test", "Model", "TEST-UUID", "Test CPU", 8000000000, 256000000000, "Windows test", "0.1.0", [machine, userScope]);
Check(Protocol.Validate(report with { Inventory = inventory }, now) is null, "software scopes valid");
Check(Protocol.Validate(report with { Inventory = inventory with { SoftwareScopes = [machine, machine] } }, now) is not null, "duplicate scope rejected");
Check(Protocol.Validate(report with { Inventory = inventory with { SoftwareScopes = [machine with { Applications = [machine.Applications[0], machine.Applications[0]] }] } }, now) is not null, "duplicate application key rejected");
Check(Protocol.Validate(report with { CapturedAt = now.AddMinutes(4), Location = location with { MeasuredAt = now.AddMinutes(8) } }, now) is not null,
    "clock skew cannot compound into a future location");

if (args.Length == 0)
{
    Console.WriteLine($"PASS: {passed} protocol checks. Pass a loopback server URL to also run API integration checks.");
    return;
}
var baseUri = new Uri(args[0]);
if (!baseUri.IsLoopback) throw new InvalidOperationException("Integration checks create test records and only run against loopback servers.");
var adminKey = Environment.GetEnvironmentVariable("BONEISET_TEST_ADMIN_KEY")
    ?? throw new InvalidOperationException("Set BONEISET_TEST_ADMIN_KEY.");
using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(60) };
await Expect(HttpMethod.Get, "/health", null, null, HttpStatusCode.OK);
await Expect(HttpMethod.Get, "/api/v1/admin/devices", null, null, HttpStatusCode.Unauthorized);
await Expect(HttpMethod.Get, "/api/v1/admin/devices", null, Protocol.NewSecret(), HttpStatusCode.Unauthorized);
await Expect(HttpMethod.Post, "/api/v1/enroll", new EnrollmentRequest("bad", "TEST"), null, HttpStatusCode.BadRequest);
await Expect(HttpMethod.Post, "/api/v1/enroll", new EnrollmentRequest(Protocol.NewSecret(), "TEST"), null, HttpStatusCode.Unauthorized);
var token = (await Expect(HttpMethod.Post, "/api/v1/admin/enrollment-tokens", null, adminKey, HttpStatusCode.OK)).GetProperty("token").GetString()!;
var registration = await Expect(HttpMethod.Post, "/api/v1/enroll", new EnrollmentRequest(token, "CHECK-" + Guid.NewGuid().ToString("N")[..8]), null, HttpStatusCode.OK);
var device = registration.Deserialize<EnrollmentResponse>(Protocol.Json)!;
Check(device.ReportIntervalSeconds == 900 && device.InventoryIntervalSeconds == 86400, "enrollment returns 15-minute and daily schedules");
var route = $"/api/v1/devices/{device.DeviceId}/reports";
var adminRoute = $"/api/v1/admin/devices/{device.DeviceId}";
await Expect(HttpMethod.Post, "/api/v1/enroll", new EnrollmentRequest(token, "REPLAY"), null, HttpStatusCode.Unauthorized);
await Expect(HttpMethod.Post, route, report, Protocol.NewSecret(), HttpStatusCode.Unauthorized);
await Expect(HttpMethod.Post, route, report with { Location = location with { Latitude = 100 } }, device.DeviceKey, HttpStatusCode.BadRequest);
await Expect(HttpMethod.Post, route, report with { CapturedAt = now.AddDays(-31) }, device.DeviceKey, HttpStatusCode.BadRequest);
report = report with { Inventory = inventory };
await Expect(HttpMethod.Post, route, report, device.DeviceKey, HttpStatusCode.OK);
var duplicate = await Expect(HttpMethod.Post, route, report, device.DeviceKey, HttpStatusCode.OK);
Check(duplicate.GetProperty("duplicate").GetBoolean(), "duplicate report acknowledged idempotently");
var locations = await Expect(HttpMethod.Get, adminRoute + "/locations", null, adminKey, HttpStatusCode.OK);
Check(locations.GetArrayLength() == 1, "retry does not duplicate location");
var apps = await Apps();
Check(apps.GetArrayLength() == 2, "machine and user software recorded");
var old = report with { ReportId = Guid.NewGuid(), CapturedAt = now.AddMinutes(-10), Location = location with { MeasuredAt = now.AddMinutes(-10) },
    Inventory = inventory with { Serial = "OLD-SERIAL", SoftwareScopes = [machine with { Applications = [] }] } };
await Expect(HttpMethod.Post, route, old, device.DeviceKey, HttpStatusCode.OK);
var details = await Expect(HttpMethod.Get, adminRoute, null, adminKey, HttpStatusCode.OK);
Check(details.GetProperty("inventory").GetProperty("serial").GetString() == "TEST-SN", "late report cannot replace newer hardware");
Check((await Apps()).GetArrayLength() == 2, "late report cannot remove current software");
var partial = report with { ReportId = Guid.NewGuid(), CapturedAt = now.AddSeconds(1), Location = new("permission_denied"),
    Inventory = inventory with { SoftwareScopes = [machine with { Complete = false, Applications = [] }] } };
await Expect(HttpMethod.Post, route, partial, device.DeviceKey, HttpStatusCode.OK);
Check((await Apps()).GetArrayLength() == 2, "partial scan cannot claim uninstall");
details = await Expect(HttpMethod.Get, adminRoute, null, adminKey, HttpStatusCode.OK);
Check(details.GetProperty("location_status").GetString() == "permission_denied", "permission denial visible without fabricated location");
var complete = partial with { ReportId = Guid.NewGuid(), CapturedAt = now.AddSeconds(2),
    Inventory = inventory with { SoftwareScopes = [machine with { Applications = [] }] } };
await Expect(HttpMethod.Post, route, complete, device.DeviceKey, HttpStatusCode.OK);
apps = await Apps();
Check(apps.GetArrayLength() == 1 && apps[0].GetProperty("name").GetString() == "User Tool", "complete machine scan preserves unscanned user scope");
var version2 = inventory with { SoftwareScopes = [machine with { Applications = [machine.Applications[0] with { Version = "2.0" }] }] };
await Expect(HttpMethod.Post, route, report with { ReportId = Guid.NewGuid(), CapturedAt = now.AddSeconds(3), Inventory = version2 }, device.DeviceKey, HttpStatusCode.OK);
await Expect(HttpMethod.Post, route, report with { ReportId = Guid.NewGuid(), CapturedAt = now.AddSeconds(4), Inventory = version2 with {
    SoftwareScopes = [machine with { Applications = [machine.Applications[0] with { Version = "3.0" }] }] } }, device.DeviceKey, HttpStatusCode.OK);
var events = await Expect(HttpMethod.Get, adminRoute + "/software-events", null, adminKey, HttpStatusCode.OK);
Check(events.EnumerateArray().Any(item => item.GetProperty("kind").GetString() == "version-changed"), "version changes tracked");
Check(events.EnumerateArray().Any(item => item.GetProperty("kind").GetString() == "no-longer-detected"), "removal observation tracked");
await Expect(HttpMethod.Put, "/api/v1/admin/software-policy", new { name = "Test Browser" }, adminKey, HttpStatusCode.NoContent);
apps = await Apps();
Check(apps.EnumerateArray().Any(item => item.GetProperty("policy_warning").GetBoolean()), "policy match flagged");
await Expect(HttpMethod.Put, adminRoute + "/asset", new { assetTag = "CHECK-" + device.DeviceId, ownerName = "Test Employee", department = "IT", branch = "Test branch", lifecycle = "In use" }, adminKey, HttpStatusCode.NoContent);
await Expect(HttpMethod.Put, adminRoute + "/asset", new { assetTag = "", ownerName = "", department = "", branch = "", lifecycle = "INVALID" }, adminKey, HttpStatusCode.BadRequest);
details = await Expect(HttpMethod.Get, adminRoute, null, adminKey, HttpStatusCode.OK);
Check(details.GetProperty("owner_name").GetString() == "Test Employee" && !details.TryGetProperty("key_hash", out _), "asset assignment saved; credential hash never exposed");
await Expect(HttpMethod.Put, adminRoute + "/asset", new { assetTag = "CHECK-" + device.DeviceId, ownerName = " =2+2", department = "IT", branch = "CSV check", lifecycle = "In use" }, adminKey, HttpStatusCode.NoContent);
await Expect(HttpMethod.Post, route, report with { ReportId = Guid.NewGuid(), CapturedAt = now.AddSeconds(5),
    Inventory = inventory with { Serial = " =3+3" } }, device.DeviceKey, HttpStatusCode.OK);
using (var csvRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/admin/export/assets.csv?query=CHECK-{device.DeviceId}"))
{
    csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
    using var csvResponse = await client.SendAsync(csvRequest);
    var csv = await csvResponse.Content.ReadAsStringAsync();
    Check(csvResponse.IsSuccessStatusCode && csv.Contains("\"'=2+2\"", StringComparison.Ordinal) && csv.Contains("\"' =3+3\"", StringComparison.Ordinal),
        "CSV protects formula injection with and without leading whitespace");
    Check(!csv.Contains(device.DeviceKey, StringComparison.Ordinal), "CSV never exposes device key");
}
await Expect(HttpMethod.Post, route, report with { ReportId = Guid.NewGuid(), CapturedAt = now.AddSeconds(6).ToOffset(TimeSpan.FromHours(7)),
    Location = location with { MeasuredAt = now.ToOffset(TimeSpan.FromHours(7)) } }, device.DeviceKey, HttpStatusCode.OK);
Check(true, "non-UTC input timestamps normalized before persistence");
Check((await Expect(HttpMethod.Get, $"/api/v1/admin/applications?deviceId={device.DeviceId}&offset=200", null, adminKey, HttpStatusCode.OK)).GetArrayLength() == 0,
    "application pagination has an empty final page");
await Expect(HttpMethod.Post, adminRoute + "/revoke", null, adminKey, HttpStatusCode.NoContent);
await Expect(HttpMethod.Post, route, report with { ReportId = Guid.NewGuid() }, device.DeviceKey, HttpStatusCode.Unauthorized);
await Expect(HttpMethod.Get, "/api/v1/admin/audit", null, adminKey, HttpStatusCode.OK);
var concurrentToken = (await Expect(HttpMethod.Post, "/api/v1/admin/enrollment-tokens", null, adminKey, HttpStatusCode.OK)).GetProperty("token").GetString()!;
var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
{
    using var response = await client.PostAsJsonAsync("/api/v1/enroll", new EnrollmentRequest(concurrentToken, $"CONCURRENT-{index}"));
    return response.StatusCode;
}));
Check(concurrent.Count(status => status == HttpStatusCode.OK) == 1 && concurrent.Count(status => status == HttpStatusCode.Unauthorized) == 3, "single-use token atomic under concurrency");

var databaseConnection = Environment.GetEnvironmentVariable("BONEISET_TEST_DATABASE");
if (!string.IsNullOrEmpty(databaseConnection))
{
    var settings = new NpgsqlConnectionStringBuilder(databaseConnection);
    if (settings.Database != "boneiset_test" || settings.Host is not ("127.0.0.1" or "localhost"))
        throw new InvalidOperationException("Retention fixtures require loopback database named boneiset_test.");
    await using var database = NpgsqlDataSource.Create(databaseConnection);
    var store = new Store(database);
    var expiredReport = Guid.NewGuid();
    await using (var command = database.CreateCommand("""
        INSERT INTO reports(device_id,report_id,captured_at) VALUES(@device,@report,now()-interval '32 days');
        INSERT INTO locations(device_id,report_id,measured_at,latitude,longitude,accuracy,source)
          VALUES(@device,@report,now()-interval '32 days',0,0,10,'Synthetic');
        """))
    {
        command.Parameters.AddWithValue("device", device.DeviceId);
        command.Parameters.AddWithValue("report", expiredReport);
        await command.ExecuteNonQueryAsync();
    }
    locations = await Expect(HttpMethod.Get, adminRoute + "/locations", null, adminKey, HttpStatusCode.OK);
    Check(locations.EnumerateArray().All(item => item.GetProperty("report_id").GetGuid() != expiredReport), "expired location hidden before scheduled cleanup");
    await store.CleanupAsync(30);
    await using (var command = database.CreateCommand("SELECT count(*) FROM reports WHERE device_id=@device AND report_id=@report"))
    {
        command.Parameters.AddWithValue("device", device.DeviceId);
        command.Parameters.AddWithValue("report", expiredReport);
        Check((long)(await command.ExecuteScalarAsync())! == 0, "retention deletes expired reports and linked locations");
    }
    await using (var command = database.CreateCommand("SELECT key_hash FROM devices WHERE id=@device"))
    {
        command.Parameters.AddWithValue("device", device.DeviceId);
        Check((string)(await command.ExecuteScalarAsync())! == Protocol.Hash(device.DeviceKey), "database stores only device key hash");
    }
    var expiredToken = await store.IssueTokenAsync("checks");
    await using (var command = database.CreateCommand("UPDATE enrollment_tokens SET expires_at=now()-interval '1 second' WHERE hash=@hash"))
    {
        command.Parameters.AddWithValue("hash", Protocol.Hash(expiredToken));
        await command.ExecuteNonQueryAsync();
    }
    await Expect(HttpMethod.Post, "/api/v1/enroll", new EnrollmentRequest(expiredToken, "EXPIRED-TOKEN"), null, HttpStatusCode.Unauthorized);
    await DashboardChecks.RunAsync(baseUri, database, Check);
}
Console.WriteLine($"PASS: {passed} protocol and HTTP integration checks.");

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}
async Task<JsonElement> Expect(HttpMethod method, string path, object? body, string? key, HttpStatusCode expected)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null) request.Content = JsonContent.Create(body, options: Protocol.Json);
    if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    using var response = await client.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    if (response.StatusCode != expected) throw new Exception($"{method} {path}: expected {expected}, got {response.StatusCode}: {text}");
    Check(true, $"{method} {path}: {(int)expected}");
    using var parsed = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "null" : text);
    return parsed.RootElement.Clone();
}
Task<JsonElement> Apps() => Expect(HttpMethod.Get, $"/api/v1/admin/applications?deviceId={device.DeviceId}", null, adminKey, HttpStatusCode.OK);
