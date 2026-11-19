using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Integration;

public sealed partial class CloudBackupSession
{
    // v2 uploaded to per-operation protection folders before publishing. Never reinterpret
    // its Staged/ArchiveFolder fields as the current direct-upload journal. Close it only
    // after verifying a complete live version, retaining its original bytes and protection.
    private async Task ClosePreviousJournalAsync(string path, CloudJournal j, CancellationToken token)
    {
        var text = AtomicJson.ReadText(path)!;
        using var document = JsonDocument.Parse(text);
        var json = document.RootElement;
        T? Field<T>(string key) => json.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.Deserialize<T>(AtomicJson.Options) : default;
        var staged = Field<RemoteItem>("staged");
        var archiveFolder = Field<string>("archiveFolder");
        if (j.Binding != Binding || !Guid.TryParseExact(j.Id, "N", out _) || Path.GetFileName(path) != j.Id + ".json" ||
            PathRules.Relative(j.Path) != j.Path || !Enum.IsDefined(j.Direction) || !Enum.IsDefined(j.Protection) ||
            j.Phase is not ("Prepared" or "Staged" or "Archived" or "Published") ||
            j.Local is { } local && (!FileVersion.IsHash(local.Sha256) || local.Length < 0) ||
            j.Remote is { } remote && (remote.Project != CloudRepository.ProjectKey(project) ||
                remote.Path != j.Path || remote.ItemId != j.Expected?.Id || remote.ETag != j.Expected?.ETag ||
                remote.Version.Length != j.Expected?.Size || remote.Version.Length < 0 || !FileVersion.IsHash(remote.Version.Sha256)) ||
            j.Expected is { } expected && (expected.Folder || expected.Name != Path.GetFileName(j.Path)) ||
            j.Uploaded is not null || j.ArchiveParent is not null)
            throw new IOException("旧操作的设备、映射、路径或阶段不一致，恢复材料已保留。");
        var parent = await ParentAsync(j.Path, false, token);
        if (parent?.Id != j.Parent) throw new IOException("旧操作的云端父目录已改变，恢复材料已保留。");
        var name = Path.GetFileName(j.Path);
        var live = await drive.FindAsync(j.Parent, name, token);
        var area = j.Direction == SyncDirection.Pull && j.Publish ? "archives" : "conflicts";
        var stageFolder = await Repository.FolderAsync(area + "/" + CloudRepository.ProjectKey(project) + "/" + j.Id + "/content", false, token);
        var stagedContent = stageFolder is null ? null : await drive.FindAsync(stageFolder.Id, name, token);
        if (staged is not null)
        {
            var candidate = stagedContent?.Id == staged.Id ? stagedContent
                : j.Direction == SyncDirection.Backup && j.Publish && live?.Id == staged.Id ? live : null;
            if (candidate is null || staged.Name != name || staged.Size != j.Local?.Length || j.Local is null)
                throw new IOException("旧操作的暂存内容或位置已改变，恢复材料已保留。");
            await Repository.VerifyAsync(candidate, j.Local, j.QuickXor, false, token);
        }
        // A lost upload response may leave protected bytes without a Staged field. Keep them.
        if (stagedContent is not null && staged is null)
        {
            if (j.Local is null) throw new IOException("旧暂存文件缺少内容摘要，恢复材料已保留。");
            await Repository.VerifyAsync(stagedContent, j.Local, j.QuickXor, false, token);
        }
        var archive = await Repository.FolderAsync("archives/" + CloudRepository.ProjectKey(project) + "/" + j.Id, false, token);
        if (archiveFolder is not null && archive?.Id != archiveFolder)
            throw new IOException("旧操作的历史目录已改变，恢复材料已保留。");
        var content = archive is null ? null : await drive.FindAsync(archive.Id, "content", token);
        var archived = content is null ? null : await drive.FindAsync(content.Id, name, token);
        var alreadyRestored = j.Direction == SyncDirection.Backup && archive is not null && archived is null && live is not null && live.Id == j.Expected?.Id;
        if (alreadyRestored)
        {
            if (j.Remote is null || live is null) throw new IOException("旧操作缺少原文件摘要，恢复材料已保留。");
            await Repository.VerifyAsync(live, j.Remote.Version, j.Expected!.QuickXorHash, false, token);
        }
        else if (j.Direction == SyncDirection.Backup && (j.Archived is not null || archived is not null))
        {
            if (archived is null || archived.Id != j.Expected?.Id || j.Archived is not null && archived.Id != j.Archived.Id || j.Remote is null)
                throw new IOException("旧操作的云端原文件保护记录不一致，恢复材料已保留。");
            await Repository.VerifyAsync(archived, j.Remote.Version, j.Expected!.QuickXorHash, false, token);
        }
        if (j.Direction == SyncDirection.Backup && j.Publish && staged is not null && live?.Id == staged.Id)
        {
            // Publication succeeded. The next explicit operation can establish a baseline;
            // this reconciliation never infers a common baseline or rewrites old metadata.
        }
        else if (j.Direction == SyncDirection.Backup && j.Publish && live is null && j.Expected is not null)
        {
            if (archived is null || j.Remote is null) throw new IOException("云端原文件缺失且找不到已核验的保护副本，恢复材料已保留。");
            var restored = await drive.MoveAsync(archived, j.Parent, name, null, token);
            await Repository.VerifyAsync(restored, j.Remote.Version, j.Expected.QuickXorHash, false, token);
        }
        else if (!alreadyRestored) RequireExpected(j.Expected, live);

        token.ThrowIfCancellationRequested();
        var retained = Path.Combine(StateRoot, "retained-transactions", j.Id + ".json");
        SafePath.Validate(retained, config.Preview.StateDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
        if (AtomicJson.ReadText(path) != text) throw new IOException("恢复记录被外部修改，原记录已保留。");
        File.Move(path, retained); // No overwrite, no cleanup of old archives, stages or local rollback files.
    }
}
