# 账户模式配置示例

data/config 中使用 projects.json、backup-settings.json、desktop-settings.json、onedrive-account.json。
首次使用通过桌面账户窗口完成授权和云端仓库绑定；示例身份不能用于真实连接。
不再读取 storage-settings.json 或本机 OneDrive 根目录。不删除已有配置或业务数据。

projects.json 的每个 folders 项可设置 `automaticBackupMode`：

| 值 | 界面名称 | 业务文件方向 |
| --- | --- | --- |
| `upload`（缺省） | 仅上传 | 本地 → OneDrive |
| `bidirectional` | 双向备份 | 按共同基线选择上传或下载 |
| `download` | 仅下载 | OneDrive → 本地 |

设置仅对本机生效，各设备分别选择；桌面定时器、“立即运行”和 CLI `backup --automatic` 共用项目策略、暂停和单轮预算。仅下载仍可上传管理元数据及覆盖前保护材料，不上传本地业务文件到项目云端目录。旧 `automaticBidirectional` 布尔字段继续读取：`true` 对应双向，`false` 或省略对应仅上传；存在新字段时优先采用新字段，非法模式值拒绝加载。桌面保存使用新字段并移除旧布尔字段。应使用支持三种模式的客户端。

策略切换保留映射版本、共同基线和就绪状态，新项目或实际目录切换后仍需先完成一次手动上传或拉取。双方修改、无基线且内容不同或缺少可信元数据的文件等待明确选择；不传播删除，单侧缺失的文件可按所选方向从另一侧补回。显式手动 `backup` / `pull` 继续分别执行上传 / 下载。

`projects.example.json` 保持空项目模板。下面是可加入 `folders` 的合成项目示例；`destination` 始终相对授权 OneDrive 根目录：

```json
{
  "id": "1111111111111111111111111111111111111111111111111111111111111111",
  "source": "C:\\BackupDemo\\Example",
  "destination": "Projects/Example",
  "automaticBackupMode": "download",
  "excludedDirectories": [],
  "excludedFiles": []
}
```

上例将业务文件下载到本机示例目录；首次仍需完成一次明确的手动操作，配置保存本身不传输文件。

projects.json 的可选字段 builtInExclusions 保存全局内置规则 ID，缺省或空数组表示不启用。可选值为 word-locks-v1、excel-locks-v1、powerpoint-locks-v1、windows-thumbnails-v1、windows-folder-settings-v1、macos-folder-info-v1。例如 `["word-locks-v1"]` 排除任意层级的 `~$*.docx`、`~$*.doc`。excludedDirectories / excludedFiles 仍保存手动规则，项目规则继续叠加。内置规则 ID 及其表达式固定；未知 ID 或非法字段会拒绝加载，不静默忽略。启用内置规则的配置须使用支持该字段的客户端。

每个 folders 项还可设置 `backupPaused`（布尔值，默认 false），暂停本机该项目的自动备份及自动重试；手动操作仍可使用。
