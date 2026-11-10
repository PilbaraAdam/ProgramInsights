using ProgramInsights.Services.Auth;

namespace ProgramInsights.Services.Tenancy;

public interface ITenantResolver
{
    /// <summary>
    /// Resolves a credential to its client database, caching the result across circuits.
    ///
    /// The credential is passed in rather than injected: the implementation is a
    /// singleton (so the cache is shared by every circuit) while the token provider is
    /// scoped, and injecting the latter into the former would be a captive dependency.
    /// </summary>
    Task<TenantResolution> ResolveAsync(SnowflakeCredential cred, CancellationToken ct = default);

    /// <summary>Drops the cached resolution for a principal, so the next call re-reads Snowflake.</summary>
    void Invalidate(string principal);
}
