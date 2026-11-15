using System.Text.Json;
using Boneiset.Shared;

namespace Boneiset.Server;

public sealed partial class Store
{
    public async Task<JsonElement> DevicesAsync(string query, string branch, int offset, int offlineMinutes, int retentionDays) => await Query("""
        SELECT device.id,device.machine_name,device.asset_tag,device.owner_name,device.department,device.branch,device.lifecycle,
          device.revoked,device.enrolled_at,device.last_received,device.last_captured,device.location_status,device.inventory_at,
          device.inventory->>'serial' AS serial,device.inventory->>'manufacturer' AS manufacturer,device.inventory->>'model' AS model,
          device.last_received IS NULL OR device.last_received < now()-make_interval(mins=>@offline)
            OR device.last_captured < now()-make_interval(mins=>@offline) AS overdue,
          latest.latitude,latest.longitude,latest.accuracy,latest.source,latest.measured_at,
          EXISTS(SELECT 1 FROM devices other WHERE other.id<>device.id AND other.inventory->>'serial'=device.inventory->>'serial'
            AND COALESCE(device.inventory->>'serial','')<>'') AS duplicate_serial,
          EXISTS(SELECT 1 FROM software app JOIN software_policy policy ON lower(app.name)=lower(policy.name)
            WHERE app.device_id=device.id AND app.present) AS software_warning
        FROM devices device
        LEFT JOIN LATERAL (SELECT * FROM locations WHERE device_id=device.id AND measured_at>now()-make_interval(days=>@retention)
          ORDER BY measured_at DESC LIMIT 1) latest ON true
        WHERE (@query='' OR device.machine_name ILIKE @pattern OR device.asset_tag ILIKE @pattern
          OR device.owner_name ILIKE @pattern OR device.inventory->>'serial' ILIKE @pattern)
          AND (@branch='' OR device.branch=@branch)
        ORDER BY device.enrolled_at DESC,device.id LIMIT 200 OFFSET @offset
        """, ("query", query), ("pattern", $"%{query}%"), ("branch", branch), ("offset", offset), ("offline", offlineMinutes), ("retention", retentionDays));

    public Task<JsonElement> DeviceAsync(Guid id) => Query("""
        SELECT id,machine_name,asset_tag,owner_name,department,branch,lifecycle,revoked,enrolled_at,
          last_received,last_captured,location_status,inventory,inventory_at FROM devices WHERE id=@id
        """, ("id", id));

    public Task<JsonElement> ExportDevicesAsync(string query, string branch) => Query("""
        SELECT id,asset_tag,machine_name,inventory->>'serial' AS serial,owner_name,department,branch,lifecycle,last_received,last_captured
        FROM devices WHERE (@query='' OR machine_name ILIKE @pattern OR asset_tag ILIKE @pattern
          OR owner_name ILIKE @pattern OR inventory->>'serial' ILIKE @pattern)
          AND (@branch='' OR branch=@branch) ORDER BY asset_tag,id LIMIT 10001
        """, ("query", query), ("pattern", $"%{query}%"), ("branch", branch));

    public Task<JsonElement> LocationsAsync(Guid id, int offset, int retentionDays) => Query("""
        SELECT location.report_id,location.measured_at,location.latitude,location.longitude,location.accuracy,location.source,
          report.captured_at,report.received_at FROM locations location
        JOIN reports report USING(device_id,report_id)
        WHERE location.device_id=@id AND location.measured_at>now()-make_interval(days=>@retention)
        ORDER BY location.measured_at DESC,location.report_id LIMIT 200 OFFSET @offset
        """, ("id", id), ("offset", offset), ("retention", retentionDays));

    public Task<JsonElement> ApplicationsAsync(string query, Guid? id, int offset) => Query("""
        SELECT app.device_id,device.machine_name,device.asset_tag,app.scope,app.name,app.version,app.publisher,
          app.first_seen,app.last_seen,scan.complete AS scan_complete,scan.captured_at AS scope_scanned_at,
          policy.name IS NOT NULL AS policy_warning
        FROM software app JOIN devices device ON device.id=app.device_id
        JOIN software_scopes scan ON scan.device_id=app.device_id AND scan.scope=app.scope
        LEFT JOIN software_policy policy ON lower(policy.name)=lower(app.name)
        WHERE app.present AND (@id::uuid IS NULL OR app.device_id=@id::uuid)
          AND (@query='' OR app.name ILIKE @pattern)
        ORDER BY app.name,app.device_id,app.scope,app.app_key LIMIT 200 OFFSET @offset
        """, ("id", id?.ToString() ?? (object)DBNull.Value), ("query", query), ("pattern", $"%{query}%"), ("offset", offset));

    public Task<JsonElement> SoftwareEventsAsync(Guid id, int offset) => Query("""
        SELECT observed_at,scope,name,kind,detail FROM software_events WHERE device_id=@id
        ORDER BY observed_at DESC,id DESC LIMIT 200 OFFSET @offset
        """, ("id", id), ("offset", offset));

    public Task<JsonElement> AuditAsync(int offset) => Query("SELECT * FROM audit ORDER BY id DESC LIMIT 200 OFFSET @offset", ("offset", offset));
    public Task<JsonElement> DeviceAuditAsync(Guid id, int offset) => Query(
        "SELECT at,actor,action,detail FROM audit WHERE device_id=@id ORDER BY id DESC LIMIT 200 OFFSET @offset", ("id", id), ("offset", offset));
    public Task<JsonElement> PoliciesAsync() => Query("SELECT name,created_by,created_at FROM software_policy ORDER BY name");

    public async Task<bool> UpdateAssetAsync(Guid id, AssetUpdate update, string actor)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        string? previous;
        await using (var command = Command(connection, """
            SELECT jsonb_build_object('assetTag',asset_tag,'ownerName',owner_name,'department',department,'branch',branch,'lifecycle',lifecycle)::text
            FROM devices WHERE id=@id FOR UPDATE
            """, ("id", id))) previous = (string?)await command.ExecuteScalarAsync();
        if (previous is null) return false;
        await Execute(connection, """
            UPDATE devices SET asset_tag=@tag,owner_name=@owner,department=@department,branch=@branch,lifecycle=@lifecycle WHERE id=@id
            """, ("id", id), ("tag", update.AssetTag.Trim()), ("owner", update.OwnerName.Trim()),
            ("department", update.Department.Trim()), ("branch", update.Branch.Trim()), ("lifecycle", update.Lifecycle));
        await Audit(connection, actor, "asset-updated", id, $"Before: {previous}; After: {JsonSerializer.Serialize(update, Protocol.Json)}");
        await transaction.CommitAsync();
        return true;
    }

    public async Task<bool> RevokeAsync(Guid id, string actor)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var changed = await Execute(connection, "UPDATE devices SET revoked=true WHERE id=@id AND NOT revoked", ("id", id));
        if (changed != 0) await Audit(connection, actor, "device-revoked", id, "Further agent reports rejected.");
        await transaction.CommitAsync();
        return changed != 0;
    }

    public async Task SetPolicyAsync(string name, bool remove, string actor)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (remove) await Execute(connection, "DELETE FROM software_policy WHERE name=@name", ("name", name));
        else await Execute(connection, "INSERT INTO software_policy(name,created_by) VALUES(@name,@actor) ON CONFLICT DO NOTHING", ("name", name), ("actor", actor));
        await Audit(connection, actor, remove ? "software-policy-removed" : "software-policy-added", null, name);
        await transaction.CommitAsync();
    }

    public async Task CleanupAsync(int retentionDays)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, "DELETE FROM locations WHERE measured_at<now()-make_interval(days=>@days)", ("days", retentionDays));
        await Execute(connection, "DELETE FROM reports WHERE captured_at<now()-interval '31 days'");
        await Execute(connection, "DELETE FROM enrollment_tokens WHERE expires_at<now()-interval '1 day'");
        await transaction.CommitAsync();
    }

    public async Task<bool> IsReadyAsync()
    {
        await using var command = database.CreateCommand("SELECT 1");
        return await command.ExecuteScalarAsync() is 1;
    }

    private async Task<JsonElement> Query(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = Command(connection, $"SELECT COALESCE(jsonb_agg(to_jsonb(result)), '[]'::jsonb)::text FROM ({sql}) result", parameters);
        using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        return document.RootElement.Clone();
    }
}

public sealed record AssetUpdate(string AssetTag, string OwnerName, string Department, string Branch, string Lifecycle)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(AssetTag) &&
        new[] { AssetTag, OwnerName, Department, Branch }.All(value => value is not null && value.Length <= 200) &&
        new[] { "Stock", "In use", "Loan", "Repair", "Lost", "Retired" }.Contains(Lifecycle);
}
