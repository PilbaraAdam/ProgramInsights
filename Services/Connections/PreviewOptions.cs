namespace ProgramInsights.Services.Connections;

/// <summary>
/// Bound from the "Preview" section. One codebase, two deployments: with
/// <see cref="Enabled"/> set, this deployment is the Private Preview — email and
/// password sign-in instead of Entra, and every query runs as one key-pair service
/// account (SQL/Control/09). Never put the private key in appsettings; supply it from
/// Key Vault / App Service configuration or user-secrets.
/// </summary>
public sealed class PreviewOptions
{
    public bool Enabled { get; set; }

    /// <summary>Snowflake service user, e.g. PROGRAMINSIGHTS_PREVIEW_SVC.</summary>
    public string ServiceUser { get; set; } = "";

    /// <summary>Role the service user connects with, e.g. PROGRAMINSIGHTS_PREVIEW.</summary>
    public string Role { get; set; } = "PROGRAMINSIGHTS_PREVIEW";

    /// <summary>PEM private key content. Takes precedence over <see cref="PrivateKeyPath"/>.</summary>
    public string? PrivateKey { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>Failed sign-ins allowed per email (and per IP) before a temporary lockout.</summary>
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;

    public void Validate()
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(ServiceUser))
            throw new InvalidOperationException("Preview:ServiceUser is required when Preview:Enabled is true.");
        if (string.IsNullOrWhiteSpace(PrivateKey) && string.IsNullOrWhiteSpace(PrivateKeyPath))
            throw new InvalidOperationException("Preview:PrivateKey or Preview:PrivateKeyPath is required when Preview:Enabled is true.");
    }
}
