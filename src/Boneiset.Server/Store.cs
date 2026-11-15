using System.Text.Json;
using Boneiset.Shared;
using Npgsql;

namespace Boneiset.Server;

public sealed partial class Store(NpgsqlDataSource database)
{
    public async Task InitializeAsync()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, "SELECT pg_advisory_xact_lock(7141501)");
        await using var stream = typeof(Store).Assembly.GetManifestResourceStream("Boneiset.Server.schema.sql")!;
        using var reader = new StreamReader(stream);
        await Execute(connection, await reader.ReadToEndAsync());
        await transaction.CommitAsync();
    }

    public async Task<string> IssueTokenAsync(string actor)
    {
        var token = Protocol.NewSecret();
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, "INSERT INTO enrollment_tokens(hash,expires_at,created_by) VALUES (@hash,now()+interval '1 hour',@actor)",
            ("hash", Protocol.Hash(token)), ("actor", actor));
        await Audit(connection, actor, "enrollment-token-created", null, "Single-use; valid for one hour.");
        await transaction.CommitAsync();
        return token;
    }

    public async Task<EnrollmentResponse?> EnrollAsync(EnrollmentRequest request)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var changed = await Execute(connection, "UPDATE enrollment_tokens SET used_at=now() WHERE hash=@hash AND used_at IS NULL AND expires_at>now()",
            ("hash", Protocol.Hash(request.Token)));
        if (changed == 0) return null;
        var deviceId = Guid.NewGuid();
        var key = Protocol.NewSecret();
        await Execute(connection, "INSERT INTO devices(id,machine_name,key_hash,asset_tag) VALUES(@id,@name,@hash,@tag)",
            ("id", deviceId), ("name", request.MachineName.Trim()), ("hash", Protocol.Hash(key)), ("tag", $"BN-{deviceId:N}"));
        await Audit(connection, "enrollment", "device-enrolled", deviceId, request.MachineName.Trim());
        await transaction.CommitAsync();
        return new(deviceId, key, (int)Protocol.ReportInterval.TotalSeconds, (int)Protocol.InventoryInterval.TotalSeconds);
    }

    public async Task<bool?> AcceptAsync(Guid deviceId, string deviceKey, DeviceReport report, int retentionDays)
    {
        report = report with { CapturedAt = report.CapturedAt.ToUniversalTime(),
            Location = report.Location with { MeasuredAt = report.Location.MeasuredAt?.ToUniversalTime() } };
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var auth = Command(connection, "SELECT id FROM devices WHERE id=@id AND key_hash=@hash AND NOT revoked FOR UPDATE",
            ("id", deviceId), ("hash", Protocol.Hash(deviceKey))))
        {
            if (await auth.ExecuteScalarAsync() is null) return null;
        }
        var inserted = await Execute(connection, "INSERT INTO reports(device_id,report_id,captured_at) VALUES(@id,@report,@time) ON CONFLICT DO NOTHING",
            ("id", deviceId), ("report", report.ReportId), ("time", report.CapturedAt));
        if (inserted == 0) { await transaction.CommitAsync(); return false; }
        await Execute(connection, """
            UPDATE devices SET last_received=now(),
              location_status=CASE WHEN last_captured IS NULL OR last_captured<=@time THEN @status ELSE location_status END,
              last_captured=GREATEST(last_captured,@time) WHERE id=@id
            """, ("id", deviceId), ("time", report.CapturedAt), ("status", report.Location.Status));
        var location = report.Location;
        if (location.Status == "available" && location.MeasuredAt >= DateTimeOffset.UtcNow.AddDays(-retentionDays))
            await Execute(connection, """
                INSERT INTO locations(device_id,report_id,measured_at,latitude,longitude,accuracy,source)
                VALUES(@id,@report,@time,@lat,@lon,@accuracy,@source)
                """, ("id", deviceId), ("report", report.ReportId), ("time", location.MeasuredAt!.Value),
                ("lat", location.Latitude!.Value), ("lon", location.Longitude!.Value), ("accuracy", location.AccuracyMeters!.Value), ("source", location.Source!));
        if (report.Inventory is { } inventory)
        {
            await Execute(connection, """
                UPDATE devices SET inventory=@inventory::jsonb,inventory_at=@time
                WHERE id=@id AND (inventory_at IS NULL OR inventory_at<=@time)
                """, ("id", deviceId), ("time", report.CapturedAt),
                ("inventory", JsonSerializer.Serialize(inventory with { SoftwareScopes = [] }, Protocol.Json)));
            foreach (var scope in inventory.SoftwareScopes)
                await SaveSoftware(connection, deviceId, report.CapturedAt, scope);
        }
        await transaction.CommitAsync();
        return true;
    }

    private static async Task SaveSoftware(NpgsqlConnection connection, Guid deviceId, DateTimeOffset capturedAt, SoftwareScope scope)
    {
        var changed = await Execute(connection, """
            INSERT INTO software_scopes(device_id,scope,captured_at,complete) VALUES(@id,@scope,@time,@complete)
            ON CONFLICT(device_id,scope) DO UPDATE SET captured_at=@time,complete=@complete
            WHERE software_scopes.captured_at<@time
            """, ("id", deviceId), ("scope", scope.Scope), ("time", capturedAt), ("complete", scope.Complete));
        if (changed == 0) return;
        await Execute(connection, """
            WITH incoming AS MATERIALIZED (
              SELECT * FROM jsonb_to_recordset(@apps::jsonb) AS app(key text,name text,version text,publisher text)
            ), events AS (
              INSERT INTO software_events(device_id,observed_at,scope,name,kind,detail)
              SELECT @id,@time,@scope,incoming.name,
                CASE WHEN previous.app_key IS NULL THEN 'first-detected' WHEN NOT previous.present THEN 'detected-again' ELSE 'version-changed' END,
                CASE WHEN previous.present THEN previous.version || ' => ' || incoming.version ELSE incoming.version END
              FROM incoming LEFT JOIN software previous ON previous.device_id=@id AND previous.scope=@scope AND previous.app_key=incoming.key
              WHERE previous.app_key IS NULL OR NOT previous.present OR previous.version<>incoming.version
            )
            INSERT INTO software(device_id,scope,app_key,name,version,publisher,first_seen,last_seen)
              SELECT @id,@scope,key,name,version,publisher,@time,@time FROM incoming
            ON CONFLICT(device_id,scope,app_key) DO UPDATE SET
              name=EXCLUDED.name,version=EXCLUDED.version,publisher=EXCLUDED.publisher,last_seen=EXCLUDED.last_seen,present=true
            """, ("id", deviceId), ("scope", scope.Scope), ("time", capturedAt),
            ("apps", JsonSerializer.Serialize(scope.Applications, Protocol.Json)));
        if (scope.Complete)
            await Execute(connection, """
                WITH removed AS (
                  UPDATE software SET present=false WHERE device_id=@id AND scope=@scope AND present AND last_seen<@time RETURNING name
                ) INSERT INTO software_events(device_id,observed_at,scope,name,kind,detail)
                  SELECT @id,@time,@scope,name,'no-longer-detected','Missing from a complete scope scan; not an exact uninstall timestamp.' FROM removed
                """, ("id", deviceId), ("scope", scope.Scope), ("time", capturedAt));
    }

    internal static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }

    internal static async Task<int> Execute(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    internal static Task<int> Audit(NpgsqlConnection connection, string actor, string action, Guid? device, string detail) =>
        Execute(connection, "INSERT INTO audit(actor,action,device_id,detail) VALUES(@actor,@action,@device::uuid,@detail)",
            ("actor", actor), ("action", action), ("device", device?.ToString() ?? (object)DBNull.Value), ("detail", detail));
}
