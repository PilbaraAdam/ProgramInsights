using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ProgramInsights.Services.Connections;
using Snowflake.Data.Client;

namespace ProgramInsights.Services.Auth;

/// <summary>A verified Preview account.</summary>
public sealed record PreviewIdentity(string Email, string Name, string? Org);

/// <summary>
/// Checks Preview email + password against PROGRAMINSIGHTS_APP.SECURITY.PREVIEW_USERS.
///
/// Same scheme as Benchmarks (app/domain/password_hash.py) so its issuing tools work
/// unchanged: PBKDF2-HMAC-SHA256, hex salt and hash, iteration count stored per row,
/// 32-byte derived key, constant-time compare.
///
/// Failures are deliberately indistinguishable — unknown email, wrong password,
/// deactivated account and a locked-out attempt all read the same to the caller — so
/// the form cannot be used to find out who has access. Unlike Benchmarks, repeated
/// failures lock the email and the client IP out for a while.
/// </summary>
public sealed class PreviewAccountService
{
    private readonly SnowflakeSessionOpener _opener;
    private readonly SnowflakeOptions _sf;
    private readonly PreviewOptions _preview;
    private readonly ILogger<PreviewAccountService> _log;
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntil)> _attempts = new();

    public PreviewAccountService(
        SnowflakeSessionOpener opener,
        IOptions<SnowflakeOptions> sf,
        IOptions<PreviewOptions> preview,
        ILogger<PreviewAccountService> log)
    {
        _opener = opener;
        _sf = sf.Value;
        _preview = preview.Value;
        _log = log;
    }

    public async Task<PreviewIdentity?> VerifyAsync(
        string? email, string? password, string? clientIp, CancellationToken ct = default)
    {
        email = email?.Trim().ToLowerInvariant();
        password = password?.Trim();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password) || email.Length > 320)
            return null;

        var emailKey = "email:" + email;
        var ipKey = "ip:" + (clientIp ?? "unknown");
        if (IsLockedOut(emailKey) || IsLockedOut(ipKey))
        {
            _log.LogWarning("Preview sign-in refused while locked out ({Email}, {Ip})", email, clientIp);
            return null;
        }

        var identity = await CheckAsync(email, password, ct);
        if (identity is null)
        {
            RecordFailure(emailKey);
            RecordFailure(ipKey);
            return null;
        }

        _attempts.TryRemove(emailKey, out _);
        return identity;
    }

    private async Task<PreviewIdentity?> CheckAsync(string email, string password, CancellationToken ct)
    {
        string? name, org, salt, hash;
        int iterations;
        try
        {
            await using var conn = await _opener.OpenAsync(
                PreviewSnowflakeTokenProvider.ForEmail(_preview, email), null, null, ct, _preview.Role);
            await using var cmd = new SnowflakeDbCommand(conn)
            {
                CommandText =
                    $"SELECT NAME, ORG, PASSWORD_SALT, PASSWORD_HASH, HASH_ITERATIONS " +
                    $"FROM {_sf.ControlDatabase}.{_sf.ControlSchema}.PREVIEW_USERS " +
                    "WHERE LOWER(TRIM(EMAIL)) = :Email AND IS_ACTIVE = TRUE " +
                    "QUALIFY ROW_NUMBER() OVER (ORDER BY UPDATED_AT DESC) = 1"
            };
            SnowflakeDb.AddParam(cmd, "Email", email);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            name = reader.IsDBNull(0) ? null : reader.GetString(0);
            org = reader.IsDBNull(1) ? null : reader.GetString(1);
            salt = reader.IsDBNull(2) ? null : reader.GetString(2);
            hash = reader.IsDBNull(3) ? null : reader.GetString(3);
            iterations = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
        }
        catch (SnowflakeDbException ex)
        {
            // Fails closed. Usually a missing schema USAGE grant (SQL/Control/09).
            _log.LogWarning(ex, "Preview account lookup failed");
            return null;
        }

        if (!VerifyPassword(password, salt, hash, iterations)) return null;

        return new PreviewIdentity(email, string.IsNullOrWhiteSpace(name) ? NameFromEmail(email) : name!, org);
    }

    public static bool VerifyPassword(string password, string? saltHex, string? hashHex, int iterations)
    {
        if (string.IsNullOrEmpty(saltHex) || string.IsNullOrEmpty(hashHex) || iterations <= 0) return false;
        try
        {
            var salt = Convert.FromHexString(saltHex);
            var expected = Convert.FromHexString(hashHex);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private bool IsLockedOut(string key) =>
        _attempts.TryGetValue(key, out var a) && a.LockedUntil > DateTimeOffset.UtcNow;

    private void RecordFailure(string key) =>
        _attempts.AddOrUpdate(key,
            _ => (1, DateTimeOffset.MinValue),
            (_, a) =>
            {
                // A lockout that has expired starts a fresh count.
                var failures = a.LockedUntil != DateTimeOffset.MinValue && a.LockedUntil <= DateTimeOffset.UtcNow
                    ? 1 : a.Failures + 1;
                var lockedUntil = failures >= _preview.MaxFailedAttempts
                    ? DateTimeOffset.UtcNow.AddMinutes(_preview.LockoutMinutes)
                    : DateTimeOffset.MinValue;
                return (failures, lockedUntil);
            });

    private static string NameFromEmail(string email)
    {
        var local = email.Split('@')[0].Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');
        return string.Join(' ', local.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
