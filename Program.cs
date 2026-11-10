using ProgramInsights.Components;
using ProgramInsights.Extensions;
using ProgramInsights.Services;

var builder = WebApplication.CreateBuilder(args);

// Entra ID SSO + Snowflake token acquisition.
builder.Services.AddProgramInsightsAuth(builder.Configuration);

// Per-user Snowflake connections and role-to-client-database routing.
builder.Services.AddSnowflakeTenancy(builder.Configuration);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<ScenarioService>();
builder.Services.AddScoped<ProgramService>();
builder.Services.AddScoped<ProgramCalculationService>();
builder.Services.AddScoped<ExcelExportService>();
builder.Services.AddScoped<DataMappingService>();
builder.Services.AddScoped<CortexService>();
builder.Services.AddScoped<BenchmarkService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
    // Behind a reverse proxy (App Service), without this OIDC builds http:// redirect
    // URIs and Entra rejects them as a redirect-URI mismatch.
    app.UseForwardedHeaders();
}

app.UseHttpsRedirection();
app.UseStaticFiles();  // serves wwwroot files including libman-restored assets

// Order matters: authentication resolves who you are, authorization decides what
// you may reach, and antiforgery must come after both so its tokens are bound to
// the authenticated user.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

// /MicrosoftIdentity/Account/{SignIn,SignOut,Challenge} from Microsoft.Identity.Web.UI.
app.MapControllers();

// RequireAuthorization here (rather than as a global fallback policy) means an
// unauthenticated request for a page gets a real HTTP 302 to Entra before any
// Blazor circuit starts. A global fallback would also catch MapStaticAssets and
// redirect the CSS.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();
