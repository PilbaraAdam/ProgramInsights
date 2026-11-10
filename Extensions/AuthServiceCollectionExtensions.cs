using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using ProgramInsights.Services.Auth;
using ProgramInsights.Services.Connections;
using ProgramInsights.Services.Tenancy;

namespace ProgramInsights.Extensions;

/// <summary>
/// Entra ID sign-in plus token acquisition for Snowflake.
///
/// The access token this obtains is passed straight to Snowflake as an OAuth
/// bearer token, so each user connects to Snowflake as their own Snowflake user
/// with their own role — which is what makes per-client database isolation real
/// rather than advisory. Snowflake's External OAuth security integration must
/// trust this same Entra app registration.
/// </summary>
public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddProgramInsightsAuth(
        this IServiceCollection services, IConfiguration config)
    {
        // The scope Snowflake's External OAuth integration expects. "session:role-any"
        // is the Snowflake convention for "let the token's user assume any role they
        // have been granted" — it requires EXTERNAL_OAUTH_ANY_ROLE_MODE = 'ENABLE'
        // on the integration.
        var snowflakeScope = config["Snowflake:Scope"]
            ?? throw new InvalidOperationException(
                "Snowflake:Scope is not configured. Expected api://<ClientId>/session:role-any");

        services
            .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(config.GetSection("AzureAd"))
            // Do NOT add openid/profile/offline_access here — MSAL.NET adds the
            // reserved scopes itself and rejects them as input. offline_access is
            // what gives us silent refresh, so the effective session length is the
            // cookie lifetime rather than the ~60-90 min access token lifetime.
            .EnableTokenAcquisitionToCallDownstreamApi(new[] { snowflakeScope })
            // In-memory is a deliberate tradeoff for this round: an app restart or
            // deploy discards the MSAL cache while auth cookies stay valid, so
            // active users get bounced to re-login. Swap for AddDistributedTokenCaches()
            // backed by SQL Server or Redis when that becomes painful.
            .AddInMemoryTokenCaches();

        services.AddAuthorization();

        // Required for <AuthorizeView> / <AuthorizeRouteView> to see the user in
        // non-interactive components such as MainLayout.
        services.AddCascadingAuthenticationState();

        // Safety net for incremental consent / conditional access challenges.
        services.AddMicrosoftIdentityConsentHandler();

        // Provides /MicrosoftIdentity/Account/{SignIn,SignOut,Challenge}. Without
        // the matching app.MapControllers() every login and logout link 404s.
        services.AddControllersWithViews().AddMicrosoftIdentityUI();

        return services;
    }

    /// <summary>
    /// Per-user Snowflake connections: token acquisition, role-to-database routing, and
    /// the connection factory the data services depend on.
    /// </summary>
    public static IServiceCollection AddSnowflakeTenancy(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<SnowflakeOptions>()
            .Bind(config.GetSection("Snowflake"))
            .ValidateDataAnnotations()
            // Fail at boot on a missing Account/Warehouse/Scope rather than on the
            // first query, when the cause is far less obvious.
            .ValidateOnStart();

        // Shared by the tenant resolver's cache.
        services.AddMemoryCache();

        // Stateless and tenant-agnostic, so a singleton. Being tenant-agnostic is what
        // lets the resolver (which decides the tenant) and the factory (which needs it)
        // both use this without a dependency cycle.
        services.AddSingleton<SnowflakeSessionOpener>();

        // Singleton: the resolution cache is shared across circuits, so a refresh or a
        // second tab costs no extra Snowflake round trips. It takes the credential as a
        // parameter instead of injecting the scoped token provider, which would be a
        // captive dependency.
        services.AddSingleton<ITenantResolver, TenantResolver>();

        // Scoped == per circuit.
        services.AddScoped<ISnowflakeTokenProvider, EntraSnowflakeTokenProvider>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ISnowflakeConnectionFactory, SnowflakeConnectionFactory>();

        return services;
    }
}
