using Microsoft.Identity.Client;
using HarborBackup.OneDrive.Transport;
using System.Security.Cryptography;

namespace HarborBackup.OneDrive.Authentication;

public interface IAccessTokens
{
    Task<string> GetAsync(CancellationToken token);
}

public sealed class AccountAuthorizationRequiredException(string message) : InvalidOperationException(message);

public sealed class AccountTokens : IAccessTokens
{
    private readonly IPublicClientApplication app;
    private readonly string? accountId;
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private static readonly string[] Scopes = ["Files.ReadWrite"];
    public AccountTokens(string clientId, string credentialDirectory, string? accountId = null)
    {
        if (!OneDriveApplication.IsValid(clientId)) throw new ArgumentException("Configure a Microsoft Entra public desktop Client ID.");
        this.accountId = accountId;
        Directory.CreateDirectory(credentialDirectory);
        var path = Path.Combine(credentialDirectory, clientId + ".bin");
        // Public desktop OAuth with PKCE; no password, client secret or embedded web runtime.
        app = PublicClientApplicationBuilder.Create(clientId).WithAuthority(AzureCloudInstance.AzurePublic, OneDriveApplication.AudienceFor(clientId))
            .WithRedirectUri("http://localhost").WithHttpClientFactory(DiagnosticAuthenticationClientFactory.Instance).Build();
        app.UserTokenCache.SetBeforeAccess(a =>
        {
            if (File.Exists(path)) a.TokenCache.DeserializeMsalV3(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
        });
        app.UserTokenCache.SetAfterAccess(a =>
        {
            if (!a.HasStateChanged) return;
            var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(pending, ProtectedData.Protect(a.TokenCache.SerializeMsalV3(), null, DataProtectionScope.CurrentUser));
            File.Move(pending, path, true);
        });
    }
    public async Task<(string Id, string Name)> ConnectAsync(CancellationToken token)
    {
        CloudNetworkAvailability.EnsureAvailable(token);
        try
        {
            var result = await app.AcquireTokenInteractive(Scopes).WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false).ExecuteAsync(token);
            return (result.Account.HomeAccountId.Identifier, result.Account.Username);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && CloudTransportException.Find(ex, token) is not null)
        { throw CloudTransportException.Find(ex, token)!; }
        catch (Exception ex) when (!token.IsCancellationRequested && CloudNetworkAvailability.FriendlyMessage(ex) == CloudNetworkAvailability.Offline)
        { throw new CloudOfflineException(); }
    }
    public async Task<string> GetAsync(CancellationToken token)
    {
        // File transfers share this provider. Serialize refresh/cache callbacks, not HTTP transfers.
        await tokenGate.WaitAsync(token);
        try { return await GetCoreAsync(token); }
        finally { tokenGate.Release(); }
    }
    private async Task<string> GetCoreAsync(CancellationToken token)
    {
        if (accountId is null) throw new InvalidOperationException("Connect a OneDrive account first.");
        var account = (await app.GetAccountsAsync()).SingleOrDefault(a => a.HomeAccountId.Identifier == accountId);
        if (account is null) throw new AccountAuthorizationRequiredException("OneDrive authorization required.");
        try { return (await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(token)).AccessToken; }
        catch (MsalUiRequiredException) { throw new AccountAuthorizationRequiredException("OneDrive authorization required. Reconnect in settings."); }
        catch (Exception ex) when (!token.IsCancellationRequested && CloudTransportException.Find(ex, token) is not null)
        { throw CloudTransportException.Find(ex, token)!; }
        catch (Exception ex) when (!token.IsCancellationRequested && CloudNetworkAvailability.FriendlyMessage(ex) == CloudNetworkAvailability.Offline)
        { throw new CloudOfflineException(); }
    }
    public async Task DisconnectAsync()
    {
        foreach (var account in await app.GetAccountsAsync())
            if (accountId is null || account.HomeAccountId.Identifier == accountId) await app.RemoveAsync(account);
    }
}
