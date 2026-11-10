namespace ProgramInsights.Services.Tenancy;

public interface ITenantContext
{
    /// <summary>
    /// The current user's tenant resolution, stable for the lifetime of this circuit.
    ///
    /// Pinning it per-circuit matters: if the database name could change midway through
    /// a multi-statement save, half the writes would land in the wrong client's
    /// database. Changes are picked up on the next page load instead.
    /// </summary>
    Task<TenantResolution> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// The resolved client database, or throws <see cref="Auth.SnowflakeAccessDeniedException"/>
    /// if the user is not provisioned. Used by the connection factory.
    /// </summary>
    Task<string> GetDatabaseAsync(CancellationToken ct = default);

    /// <summary>Forces a re-read on the next call, for both this circuit and the shared cache.</summary>
    Task InvalidateAsync(CancellationToken ct = default);
}
