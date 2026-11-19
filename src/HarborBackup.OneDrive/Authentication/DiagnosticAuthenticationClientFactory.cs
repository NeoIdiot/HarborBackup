using HarborBackup.OneDrive.Transport;
using Microsoft.Identity.Client;

namespace HarborBackup.OneDrive.Authentication;

// MSAL owns retry behavior. Reuse one client; keep the default proxy and certificate validation.
internal sealed class DiagnosticAuthenticationClientFactory : IMsalHttpClientFactory
{
    public static DiagnosticAuthenticationClientFactory Instance { get; } = new();
    private readonly HttpClient client = new(new NetworkDiagnosticHandler(new HttpClientHandler(), authentication: true,
        networkAvailable: System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable));
    public HttpClient GetHttpClient() => client;
}
