using HarborBackup.OneDrive.Authentication;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class OneDriveApplicationTests
{
    private const string Bundled = "0b51e308-977c-424d-a50d-6c559f51c579";
    private const string Custom = "646690bf-467f-4f83-a3ef-e3a7a4e886b0";

    [TestMethod]
    public void DefaultAndCustomIdentitiesAreExplicit()
    {
        Assert.AreEqual(Bundled, OneDriveApplication.Resolve(null, Bundled));
        Assert.AreEqual(Bundled, OneDriveApplication.Resolve("  ", Bundled));
        Assert.AreEqual(Custom, OneDriveApplication.Resolve(" " + Custom.ToUpperInvariant() + " ", Bundled));
        Assert.AreEqual(Custom, OneDriveApplication.Resolve(Custom, ""));
    }

    [TestMethod]
    public void BundledPersonalApplicationUsesConsumersAndCustomKeepsCommon()
    {
        Assert.AreEqual(Microsoft.Identity.Client.AadAuthorityAudience.PersonalMicrosoftAccount,
            OneDriveApplication.AudienceFor(Bundled.ToUpperInvariant(), Bundled));
        Assert.AreEqual(Microsoft.Identity.Client.AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount,
            OneDriveApplication.AudienceFor(Custom, Bundled));
        Assert.AreEqual(Microsoft.Identity.Client.AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount,
            OneDriveApplication.AudienceFor(Custom, ""));
    }

    [TestMethod]
    public void MissingOrInvalidIdentityCannotSilentlyUseAnotherApplication()
    {
        Assert.Throws<InvalidOperationException>(() => OneDriveApplication.Resolve(null, ""));
        Assert.Throws<InvalidOperationException>(() => OneDriveApplication.Resolve(null, Guid.Empty.ToString()));
        Assert.Throws<ArgumentException>(() => OneDriveApplication.Resolve("not-an-id", Bundled));
        Assert.Throws<ArgumentException>(() => OneDriveApplication.Resolve(Guid.Empty.ToString(), Bundled));
    }

    [TestMethod]
    public async Task SwitchingApplicationWithPendingTransactionIsRejectedBeforeAuthentication()
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var store = new CloudAccountStore(paths.Data);
        store.Save(new(ClientId: Custom, AccountId: "synthetic-account"), store.Read());
        var pending = Path.Combine(paths.Data.State, "devices", "test-device", "cloud-transactions");
        Directory.CreateDirectory(pending);
        File.WriteAllText(Path.Combine(pending, "pending.json"), "{}");
        await Assert.ThrowsAsync<IOException>(() => CloudConnection.ConnectAsync(paths.Data, Bundled, CancellationToken.None));
        Assert.AreEqual(Custom, store.Read().ClientId);
        Assert.IsFalse(Directory.Exists(Path.Combine(paths.Data.Root, "credentials")));
    }
}
