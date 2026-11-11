using System.Security.Cryptography;
using System.Text;

namespace ProgramInsights.Services.Tenancy;

/// <summary>
/// Which cross-database datasets the user's Snowflake role can actually reach.
///
/// These are all optional enrichments — a client whose role lacks them must still be
/// able to model programs. Probing once at login lets the UI say "your role has no
/// Cortex access" instead of silently showing an empty panel, which is what the old
/// empty catch blocks did.
/// </summary>
public sealed record SnowflakeCapabilities(
    bool Benchmarks,
    bool HelpAllocations,
    bool Cortex)
{
    public static readonly SnowflakeCapabilities None = new(false, false, false);
}

/// <summary>How the signed-in user is entitled to Program Insights.</summary>
public enum AccessTier
{
    /// <summary>A Pilbara Insights client: APP_USERS + ROLE_DATABASE_MAP, own client database.</summary>
    Client,

    /// <summary>
    /// A licensed institution: holds a mapped &lt;X&gt;_PROGRAMINSIGHTS_MEMBER role.
    /// Saves to the shared WORKSPACE schema; model values come from the institution's
    /// model database when one has been loaded.
    /// </summary>
    Member,

    /// <summary>
    /// The Private Preview deployment: email + password, one service account,
    /// saves to the PREVIEW schema, no cost model.
    /// </summary>
    Preview
}

/// <summary>A membership role the user may sign in as, with its institution.</summary>
public sealed record InstitutionChoice(string Role, string Model, string Institution);

/// <summary>
/// The outcome of resolving a signed-in user: their tier, where their saved work
/// lives, which role to connect with, and where (if anywhere) their cost model is.
/// </summary>
public sealed record TenantResolution(
    bool Ok,
    string? Reason,
    AccessTier Tier,
    string? Role,
    string? DatabaseName,   // database holding the user's saved work (scenarios, programs...)
    string? SchemaName,     // schema holding the user's saved work
    string? ClientName,
    Guid TenantId,
    SnowflakeCapabilities Capabilities)
{
    /// <summary>
    /// Role passed as <c>role=</c> on every connection. Null keeps the user's
    /// DEFAULT_ROLE, which is how clients have always connected.
    /// </summary>
    public string? ConnectRole { get; init; }

    /// <summary>
    /// Written to OWNER_KEY on every row the user saves (institution MODEL key for
    /// members, email for Preview). Null for clients, whose tables have no such column.
    /// </summary>
    public string? OwnerKey { get; init; }

    /// <summary>Institution key shared with Benchmarks (members only).</summary>
    public string? ModelKey { get; init; }

    /// <summary>
    /// The institution's loaded cost model, for members. Null = no model in Snowflake
    /// yet, so Model values are unavailable. Clients pick their source in the mapping
    /// wizard instead; Preview never has one.
    /// </summary>
    public string? ModelSourceDatabase { get; init; }
    public string? ModelSourceSchema { get; init; }

    /// <summary>
    /// Every institution this user may work as. When <see cref="NeedsChoice"/> is set
    /// the user must pick one first; otherwise it lets them switch later.
    /// </summary>
    public IReadOnlyList<InstitutionChoice> Memberships { get; init; } = Array.Empty<InstitutionChoice>();

    /// <summary>The user holds several membership roles and has not said which to use.</summary>
    public bool NeedsChoice { get; init; }

    /// <summary>True when saved rows carry OWNER_KEY (members and Preview).</summary>
    public bool UsesOwnerKey => Tier != AccessTier.Client && !string.IsNullOrEmpty(OwnerKey);

    public static TenantResolution Denied(string reason, string? role = null) =>
        new(false, reason, AccessTier.Client, role, null, null, null, Guid.Empty, SnowflakeCapabilities.None);

    public static TenantResolution ChooseInstitution(IReadOnlyList<InstitutionChoice> choices) =>
        new(false, "Choose which institution to work as.", AccessTier.Member, null, null, null, null,
            Guid.Empty, SnowflakeCapabilities.None)
        {
            Memberships = choices,
            NeedsChoice = true
        };

    public static TenantResolution Client(
        string role, string databaseName, string schemaName, string clientName, Guid tenantId,
        SnowflakeCapabilities capabilities) =>
        new(true, null, AccessTier.Client, role, databaseName, schemaName, clientName, tenantId, capabilities);

    /// <summary>
    /// A stable tenant id for an owner with no ROLE_DATABASE_MAP row. Derived, not
    /// stored, so the same institution or Preview email always gets the same id and the
    /// existing <c>MODEL.TENANT</c> filters isolate them with no extra lookup.
    /// </summary>
    public static Guid DeriveTenantId(AccessTier tier, string ownerKey)
    {
        // Name-based (UUID v5 layout) over a fixed namespace prefix. SHA-1 here is an
        // id derivation, not a security control.
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(
            $"programinsights:{tier}:{ownerKey.Trim().ToLowerInvariant()}"));
        var g = new byte[16];
        Array.Copy(bytes, g, 16);
        g[6] = (byte)((g[6] & 0x0F) | 0x50);
        g[8] = (byte)((g[8] & 0x3F) | 0x80);
        return new Guid(g);
    }
}
