# 账户模式配置示例

data/config 中使用 projects.json、backup-settings.json、desktop-settings.json、onedrive-account.json。
首次使用通过桌面账户窗口完成授权和云端仓库绑定；示例身份不能用于真实连接。
不再读取 storage-settings.json 或本机 OneDrive 根目录。不删除已有配置或业务数据。

projects.json 的每个 folders 项可设置布尔字段 `automaticBidirectional`，省略或 `false` 表示关闭自动双向备份，继续现有自动上传；`true` 表示该项目允许自动上传本地单侧修改、下载云端单侧修改。设置仅对本机生效，各设备分别开启；使用支持此字段的客户端。策略切换保留映射版本、共同基线和就绪状态，新项目或实际目录切换后仍需先完成一次手动上传或拉取。双方修改、无基线且内容不同或缺少可信元数据的文件等待明确选择；不传播删除，单侧缺失的文件可能从另一侧补回。桌面定时器沿用全局周期，并与 CLI `backup --automatic` 共用项目策略、暂停和单轮预算。

projects.json 的可选字段 builtInExclusions 保存全局内置规则 ID，缺省或空数组表示不启用。可选值为 word-locks-v1、excel-locks-v1、powerpoint-locks-v1、windows-thumbnails-v1、windows-folder-settings-v1、macos-folder-info-v1。例如 `["word-locks-v1"]` 排除任意层级的 `~$*.docx`、`~$*.doc`。excludedDirectories / excludedFiles 仍保存手动规则，项目规则继续叠加。内置规则 ID 及其表达式固定；未知 ID 或非法字段会拒绝加载，不静默忽略。启用内置规则的配置须使用支持该字段的客户端。
