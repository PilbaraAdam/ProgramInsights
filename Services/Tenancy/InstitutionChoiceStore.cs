using System.Collections.Concurrent;

namespace ProgramInsights.Services.Tenancy;

/// <summary>
/// Remembers which membership role a user picked, across circuits (page reloads,
/// second tabs). Singleton and in-memory: an app restart forgets it, which costs the
/// user one more pick — the same restart already drops the in-memory MSAL cache and
/// sends them back through sign-in.
///
/// This is only a preference. The resolver re-checks it against the roles the user's
/// Snowflake session can actually assume on every resolution, so a stale or forged
/// entry resolves to nothing rather than to someone else's institution.
/// </summary>
public sealed class InstitutionChoiceStore
{
    private readonly ConcurrentDictionary<string, string> _choices = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string principal) =>
        _choices.TryGetValue(principal, out var role) ? role : null;

    public void Set(string principal, string role) => _choices[principal] = role;

    public void Clear(string principal) => _choices.TryRemove(principal, out _);
}
