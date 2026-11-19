using System.Reflection;
using Microsoft.Identity.Client;

namespace HarborBackup.OneDrive.Authentication;

// Public release identity only. Never embed a client secret.
public static class OneDriveApplication
{
    public static string DefaultClientId { get; } = typeof(OneDriveApplication).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(a => a.Key == "OneDriveClientId")?.Value?.Trim() ?? "";

    public static bool HasDefaultClientId => IsValid(DefaultClientId);
    public static bool IsValid(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty;
    public static string Resolve(string? customClientId = null) => Resolve(customClientId, DefaultClientId);

    // The bundled HarborBackup registration accepts personal Microsoft accounts only.
    // Custom applications retain the existing common authority.
    internal static AadAuthorityAudience AudienceFor(string clientId) => AudienceFor(clientId, DefaultClientId);
    internal static AadAuthorityAudience AudienceFor(string clientId, string defaultClientId) =>
        IsValid(defaultClientId) && Guid.TryParse(clientId, out var id) && id == Guid.Parse(defaultClientId)
            ? AadAuthorityAudience.PersonalMicrosoftAccount
            : AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount;

    internal static string Resolve(string? customClientId, string defaultClientId)
    {
        if (!string.IsNullOrWhiteSpace(customClientId))
        {
            if (!IsValid(customClientId)) throw new ArgumentException("Invalid custom OneDrive application Client ID.");
            return Guid.Parse(customClientId).ToString();
        }
        if (!IsValid(defaultClientId))
            throw new InvalidOperationException("This build has no HarborBackup OneDrive application ID. Contact the publisher or configure a custom application in Advanced settings.");
        return Guid.Parse(defaultClientId).ToString();
    }
}
