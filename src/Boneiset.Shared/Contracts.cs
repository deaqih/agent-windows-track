using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Boneiset.Shared;

public static class Protocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan InventoryInterval = TimeSpan.FromDays(1);
    public const string ProductionUrl = "https://boneiset.awancode.com";
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string? Validate(DeviceReport report, DateTimeOffset now)
    {
        if (report.ReportId == Guid.Empty || report.CapturedAt > now.AddMinutes(5) || report.CapturedAt < now.AddDays(-30))
            return "Invalid report ID or capture time.";
        var location = report.Location;
        if (location is null || !new[] { "available", "unavailable", "permission_denied", "no_user_session", "error" }.Contains(location.Status))
            return "Invalid location status.";
        if (location.Status == "available")
        {
            if (location.Latitude is not double latitude || !double.IsFinite(latitude) || latitude is < -90 or > 90 ||
                location.Longitude is not double longitude || !double.IsFinite(longitude) || longitude is < -180 or > 180 ||
                location.AccuracyMeters is not double accuracy || !double.IsFinite(accuracy) || accuracy is < 0 or > 20000000 ||
                location.MeasuredAt is not DateTimeOffset measured || measured > report.CapturedAt.AddMinutes(5) || measured > now.AddMinutes(5) ||
                measured < report.CapturedAt.AddDays(-30) || string.IsNullOrWhiteSpace(location.Source) || location.Source.Length > 40)
                return "Invalid location measurement.";
        }
        else if (location.Latitude is not null || location.Longitude is not null || location.MeasuredAt is not null || location.AccuracyMeters is not null || location.Source is not null)
            return "Unavailable location cannot contain coordinates.";

        if (report.Inventory is not { } inventory) return null;
        if (new[] { inventory.Serial, inventory.Manufacturer, inventory.Model, inventory.HardwareUuid, inventory.Cpu, inventory.OsVersion, inventory.AgentVersion }
            .Any(value => value is null || value.Length > 500) || inventory.RamBytes < 0 || inventory.DiskBytes < 0 ||
            inventory.SoftwareScopes is null || inventory.SoftwareScopes.Count > 128)
            return "Invalid inventory.";
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var scope in inventory.SoftwareScopes)
        {
            if (scope is null || string.IsNullOrWhiteSpace(scope.Scope) || scope.Scope.Length > 180 || !scopes.Add(scope.Scope) || scope.Applications is null)
                return "Invalid or duplicate software scope.";
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var app in scope.Applications)
            {
                if (app is null || string.IsNullOrWhiteSpace(app.Key) || app.Key.Length > 500 || !keys.Add(app.Key) ||
                    string.IsNullOrWhiteSpace(app.Name) || app.Name.Length > 500 || app.Version is null || app.Version.Length > 200 ||
                    app.Publisher is null || app.Publisher.Length > 500 || ++count > 20000)
                    return "Invalid or excessive software inventory.";
            }
        }
        return null;
    }

}

public sealed record EnrollmentRequest(string Token, string MachineName);
public sealed record EnrollmentResponse(Guid DeviceId, string DeviceKey, int ReportIntervalSeconds = 900, int InventoryIntervalSeconds = 86400);
public sealed record DeviceReport(Guid ReportId, DateTimeOffset CapturedAt, LocationSample Location, InventorySnapshot? Inventory);
public sealed record LocationSample(string Status, double? Latitude = null, double? Longitude = null,
    double? AccuracyMeters = null, DateTimeOffset? MeasuredAt = null, string? Source = null);
public sealed record InventorySnapshot(string Serial, string Manufacturer, string Model, string HardwareUuid, string Cpu,
    long RamBytes, long DiskBytes, string OsVersion, string AgentVersion, List<SoftwareScope> SoftwareScopes);
public sealed record SoftwareScope(string Scope, bool Complete, List<SoftwareApp> Applications);
public sealed record SoftwareApp(string Key, string Name, string Version, string Publisher);
