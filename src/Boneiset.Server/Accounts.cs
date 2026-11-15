using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Boneiset.Server;

public sealed record DashboardUser(Guid Id, string Username, string Role, Guid Stamp)
{
    public string Actor => $"user:{Id}:{Username}";
    public ClaimsPrincipal Principal() => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, Id.ToString()), new Claim(ClaimTypes.Name, Username),
        new Claim(ClaimTypes.Role, Role), new Claim("stamp", Stamp.ToString())
    ], CookieAuthenticationDefaults.AuthenticationScheme));
}

public static class DashboardSecurity
{
    public static bool ValidUsername(string? username) => username is { Length: >= 3 and <= 100 }
        && username.All(character => char.IsAsciiLetterOrDigit(character) || "@._-".Contains(character));
    public static bool ValidPassword(string? password) => password is { Length: >= 14 and <= 128 } && !string.IsNullOrWhiteSpace(password);
    public static string Actor(ClaimsPrincipal principal) => $"user:{principal.FindFirstValue(ClaimTypes.NameIdentifier)}:{principal.Identity!.Name}";

    public static async Task ValidateCookie(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal!;
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            || !Guid.TryParse(principal.FindFirstValue("stamp"), out var stamp)
            || !await context.HttpContext.RequestServices.GetRequiredService<Store>().ValidSessionAsync(id, stamp))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
        }
    }
}

public sealed partial class Store
{
    private static readonly PasswordHasher<string> Passwords = new(Options.Create(new PasswordHasherOptions { IterationCount = 220000 }));
    private static readonly string DummyHash = Passwords.HashPassword("missing", Guid.NewGuid().ToString());

    public async Task<string?> CreateUserAsync(string username, string password, string role, string actor, bool firstOnly = false)
    {
        username = username.Trim().ToLowerInvariant();
        if (!DashboardSecurity.ValidUsername(username) || !DashboardSecurity.ValidPassword(password) || role is not ("Admin" or "Viewer"))
            return "Nama pengguna 3-100 karakter; kata sandi 14-128 karakter. Peran harus Admin atau Viewer.";
        var hash = Passwords.HashPassword(username, password);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, "SELECT pg_advisory_xact_lock(7141502)");
        if (firstOnly)
        {
            await using var count = Command(connection, "SELECT EXISTS(SELECT 1 FROM dashboard_users)");
            if ((bool)(await count.ExecuteScalarAsync())!) return "Akun awal sudah dibuat. Gunakan halaman Pengguna.";
        }
        var changed = await Execute(connection, """
            INSERT INTO dashboard_users(id,username,password_hash,role,session_stamp) VALUES(@id,@name,@hash,@role,@stamp)
            ON CONFLICT(username) DO NOTHING
            """, ("id", Guid.NewGuid()), ("name", username), ("hash", hash), ("role", role), ("stamp", Guid.NewGuid()));
        if (changed == 0) return "Nama pengguna sudah digunakan.";
        await Audit(connection, actor, "user-created", null, $"{username}; role={role}");
        await transaction.CommitAsync();
        return null;
    }

    public async Task<DashboardUser?> AuthenticateAsync(string username, string password)
    {
        username = username.Trim().ToLowerInvariant();
        if (!DashboardSecurity.ValidUsername(username) || password.Length > 128) return null;
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = Command(connection, """
            SELECT id,password_hash,role,session_stamp,enabled,locked_until>now() AS locked
            FROM dashboard_users WHERE username=@name FOR UPDATE
            """, ("name", username));
        Guid id = Guid.Empty, stamp = Guid.Empty;
        var hash = DummyHash;
        var role = "Viewer";
        bool enabled = false, locked = false;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                id = reader.GetGuid(0); hash = reader.GetString(1); role = reader.GetString(2); stamp = reader.GetGuid(3);
                enabled = reader.GetBoolean(4); locked = !reader.IsDBNull(5) && reader.GetBoolean(5);
            }
        }
        var result = Passwords.VerifyHashedPassword(username, hash, password);
        if (id == Guid.Empty || !enabled || locked) return null;
        if (result == PasswordVerificationResult.Failed)
        {
            await Execute(connection, """
                UPDATE dashboard_users SET
                  locked_until=CASE WHEN failed_attempts>=4 THEN now()+interval '15 minutes' ELSE NULL END,
                  failed_attempts=CASE WHEN failed_attempts>=4 THEN 0 ELSE failed_attempts+1 END WHERE id=@id
                """, ("id", id));
            await Audit(connection, $"user:{id}:{username}", "login-failed", null, "Invalid credentials.");
            await transaction.CommitAsync();
            return null;
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded) hash = Passwords.HashPassword(username, password);
        await Execute(connection, "UPDATE dashboard_users SET failed_attempts=0,locked_until=NULL,last_login=now(),password_hash=@hash WHERE id=@id",
            ("id", id), ("hash", hash));
        var user = new DashboardUser(id, username, role, stamp);
        await Audit(connection, user.Actor, "login", null, "Dashboard session opened.");
        await transaction.CommitAsync();
        return user;
    }

    public async Task<bool> ValidSessionAsync(Guid id, Guid stamp)
    {
        await using var command = database.CreateCommand("SELECT EXISTS(SELECT 1 FROM dashboard_users WHERE id=@id AND session_stamp=@stamp AND enabled)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("stamp", stamp);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    public Task<System.Text.Json.JsonElement> UsersAsync(int offset) => Query("""
        SELECT id,username,role,enabled,created_at,last_login FROM dashboard_users ORDER BY username LIMIT 200 OFFSET @offset
        """, ("offset", offset));

    public async Task<string?> SetUserEnabledAsync(Guid id, bool enabled, Guid actorId, string actor)
    {
        if (id == actorId) return "Status akun sendiri tidak boleh diubah.";
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, "SELECT pg_advisory_xact_lock(7141502)");
        if (!enabled)
        {
            await using var lastAdmin = Command(connection, """
                SELECT EXISTS(SELECT 1 FROM dashboard_users WHERE id=@id AND role='Admin' AND enabled)
                  AND (SELECT count(*) FROM dashboard_users WHERE role='Admin' AND enabled)<=1
                """, ("id", id));
            if ((bool)(await lastAdmin.ExecuteScalarAsync())!) return "Admin terakhir tidak boleh dinonaktifkan.";
        }
        var changed = await Execute(connection, "UPDATE dashboard_users SET enabled=@enabled,session_stamp=@stamp WHERE id=@id AND enabled<>@enabled",
            ("id", id), ("enabled", enabled), ("stamp", Guid.NewGuid()));
        if (changed == 0) return "Akun tidak ditemukan atau status sudah sama.";
        await Audit(connection, actor, "user-status-changed", null, $"{id}; enabled={enabled}");
        await transaction.CommitAsync();
        return null;
    }

    public async Task<bool> ChangePasswordAsync(DashboardUser user, string password)
    {
        if (!DashboardSecurity.ValidPassword(password)) return false;
        var hash = Passwords.HashPassword(user.Username, password);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var changed = await Execute(connection, """
            UPDATE dashboard_users SET password_hash=@hash,session_stamp=@next WHERE id=@id AND session_stamp=@stamp AND enabled
            """, ("hash", hash), ("next", Guid.NewGuid()), ("id", user.Id), ("stamp", user.Stamp));
        if (changed == 0) return false;
        await Audit(connection, user.Actor, "password-changed", null, "All sessions invalidated.");
        await transaction.CommitAsync();
        return true;
    }

    public async Task<bool> ResetPasswordLocallyAsync(string username, string password)
    {
        username = username.Trim().ToLowerInvariant();
        if (!DashboardSecurity.ValidUsername(username) || !DashboardSecurity.ValidPassword(password)) return false;
        var hash = Passwords.HashPassword(username, password);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var changed = await Execute(connection, """
            UPDATE dashboard_users SET password_hash=@hash,session_stamp=@stamp,failed_attempts=0,locked_until=NULL WHERE username=@name
            """, ("hash", hash), ("stamp", Guid.NewGuid()), ("name", username));
        if (changed == 0) return false;
        await Audit(connection, "local-recovery", "password-reset", null, $"{username}; all sessions invalidated; enabled state unchanged.");
        await transaction.CommitAsync();
        return true;
    }
}
