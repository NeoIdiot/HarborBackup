# 账户模式配置示例

data/config 中使用 projects.json、backup-settings.json、desktop-settings.json、onedrive-account.json。
首次使用通过桌面账户窗口完成授权和云端仓库绑定；示例身份不能用于真实连接。
不再读取 storage-settings.json 或本机 OneDrive 根目录。不删除已有配置或业务数据。

projects.json 的可选字段 builtInExclusions 保存全局内置规则 ID，缺省或空数组表示不启用。可选值为 word-locks-v1、excel-locks-v1、powerpoint-locks-v1、windows-thumbnails-v1、windows-folder-settings-v1、macos-folder-info-v1。例如 `["word-locks-v1"]` 排除任意层级的 `~$*.docx`、`~$*.doc`。excludedDirectories / excludedFiles 仍保存手动规则，项目规则继续叠加。内置规则 ID 及其表达式固定；未知 ID 或非法字段会拒绝加载，不静默忽略。启用内置规则的配置须使用支持该字段的客户端。
