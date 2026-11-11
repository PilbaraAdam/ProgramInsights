namespace ProgramInsights.Services.Tenancy;

public interface ITenantContext
{
    /// <summary>
    /// The current user's tenant resolution, stable for the lifetime of this circuit.
    ///
    /// Pinning it per-circuit matters: if the database name could change midway through
    /// a multi-statement save, half the writes would land in the wrong database.
    /// Changes are picked up on the next page load instead.
    /// </summary>
    Task<TenantResolution> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// The database holding the user's saved work, or throws
    /// <see cref="Auth.SnowflakeAccessDeniedException"/> if the user is not entitled.
    /// Used by the connection factory.
    /// </summary>
    Task<string> GetDatabaseAsync(CancellationToken ct = default);

    /// <summary>
    /// Work as the given membership role from now on (this and later circuits). The
    /// role must be one the resolver offered; anything else is refused, never obeyed.
    /// </summary>
    Task<TenantResolution> ChooseInstitutionAsync(string role, CancellationToken ct = default);

    /// <summary>Forces a re-read on the next call, for both this circuit and the shared cache.</summary>
    Task InvalidateAsync(CancellationToken ct = default);
}
