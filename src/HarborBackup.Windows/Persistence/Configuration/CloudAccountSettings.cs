using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Windows.Persistence.Configuration;

public sealed record CloudAccountSettings(int SchemaVersion = 2, bool Enabled = false, string ClientId = "",
    string? AccountId = null, string? AccountName = null, string? DriveId = null, string? RootId = null,
    string RootName = "HarborBackup", string? RepositoryId = null,
    LocalRetention ArchiveRetention = LocalRetention.OnlineOnly, LocalRetention ConflictRetention = LocalRetention.OnlineOnly, CloudFolders? Folders = null, bool RequiresAuthorization = false);

public sealed class CloudAccountStore(DataPaths data)
{
    private string FilePath => Path.Combine(data.Config, "onedrive-account.json");
    public bool HasAccountConfiguration => File.Exists(FilePath);
    public CloudAccountSettings Read()
    {
        var text = AtomicJson.ReadText(FilePath);
        // Normalize v1 in memory only. Persist the v2 layout on an explicit settings save.
        if (text is not null)
        {
            using var validated = JsonInput.Parse(text);
            var document = System.Text.Json.Nodes.JsonNode.Parse(text, new() { PropertyNameCaseInsensitive = true })!.AsObject();
            if (document["schemaVersion"]?.GetValue<int>() == 1)
            {
                if (document["folders"] is System.Text.Json.Nodes.JsonObject folders) folders.Remove("backups");
                document["schemaVersion"] = 2;
                text = document.ToJsonString();
            }
        }
        var settings = text is null ? new() : JsonSerializer.Deserialize<CloudAccountSettings>(text, AtomicJson.Options) ??
            throw new FormatException("Invalid OneDrive settings.");
        if (settings.SchemaVersion != 2 || !Enum.IsDefined(settings.ArchiveRetention) || !Enum.IsDefined(settings.ConflictRetention))
            throw new FormatException("Unsupported OneDrive settings.");
        if (settings.Enabled && (!Guid.TryParse(settings.ClientId, out _) || string.IsNullOrWhiteSpace(settings.AccountId) ||
            string.IsNullOrWhiteSpace(settings.DriveId) || string.IsNullOrWhiteSpace(settings.RootId) ||
            !Guid.TryParseExact(settings.RepositoryId, "N", out _))) throw new FormatException("OneDrive account binding is incomplete.");
        (settings.Folders ?? new CloudFolders()).Validate();
        return settings;
    }
    public void Save(CloudAccountSettings value, CloudAccountSettings expected)
    {
        using var guard = new RunLock();
        if (Read() != expected) throw new IOException("OneDrive settings changed. Reload before saving.");
        if (value.SchemaVersion != 2) throw new FormatException("Unsupported OneDrive settings.");
        (value.Folders ?? new CloudFolders()).Validate();
        if (expected.Enabled && value.RootId == expected.RootId && (value.Folders ?? new CloudFolders()) != (expected.Folders ?? new CloudFolders())) throw new IOException("Existing repository layout is shared and immutable. Bind a new repository to change it.");
        if (value.AccountId != expected.AccountId || value.DriveId != expected.DriveId ||
            value.RepositoryId != expected.RepositoryId || value.RootId != expected.RootId || value.Enabled != expected.Enabled ||
            !string.Equals(value.ClientId, expected.ClientId, StringComparison.OrdinalIgnoreCase))
            EnsureNoPending(data);
        AtomicJson.Write(FilePath, value, AtomicJson.Signature(AtomicJson.ReadText(FilePath)));
    }
    public static void EnsureNoPending(DataPaths data)
    {
        SafePath.Validate(data.State, data.Root);
        if (!Directory.Exists(data.State)) return;
        foreach (var directory in Directory.EnumerateDirectories(data.State, "*", SearchOption.AllDirectories))
        {
            SafePath.Validate(directory, data.State);
            if ((Path.GetFileName(directory) is "transactions" or "cloud-transactions") &&
                Directory.EnumerateFileSystemEntries(directory).Any())
                throw new IOException("Recover pending transactions before changing the account or cloud location.");
        }
    }
    public AccountTokens Tokens(CloudAccountSettings settings)
    {
        var path = Path.Combine(data.Root, "credentials");
        SafePath.Validate(path, data.Root);
        return new(settings.ClientId, path, settings.AccountId);
    }
    public GraphTransferOptions TransferOptions()
    {
        var text = AtomicJson.ReadText(data.BackupSettings);
        if (text is null) return new();
        var transfer = BackupSettingsReader.Parse(text);
        return new(UploadBytesPerSecond: transfer.UploadKiBPerSecond * 1024L,
            DownloadBytesPerSecond: transfer.DownloadKiBPerSecond * 1024L);
    }
    public GraphDrive Open(CloudAccountSettings? settings = null, GraphTransferOptions? options = null)
    {
        settings ??= Read();
        if (!settings.Enabled) throw new InvalidOperationException("OneDrive account transport is not enabled.");
        return new(Tokens(settings), settings.DriveId!, options: options ?? TransferOptions());
    }
}
