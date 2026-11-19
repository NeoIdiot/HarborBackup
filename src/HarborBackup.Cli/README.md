# HarborBackup.Cli

模块职责和依赖边界见 ../../docs/architecture/storage-layout.md。

当前项目配置为 schemaVersion 2，id 为固定的 64 位十六进制项目 ID。配置、共享文件证据、云端目录登记、保护索引和运行状态均按项目 ID 管理。跨设备加入沿用 ID；改名不改变 ID。`--project-id <id>` 用于按 ID 选择；`--destination` 仅为可读路径选择器。

安装器自动检查并重建不支持的数据格式，无须手动初始化。当前格式和重复安装保留数据。旧 data 整体留存为同级 data-retained-*，不迁移旧项目；检查和重建不访问云端，不删除源文件和云端正文。`initialize --fresh` 仅保留为明确要求重置时的维修工具。

项目目录切换只保存配置。新映射需先完成一次手动上传或拉取，才继续自动备份；自动调用使用 `backup --automatic`，未准备好的项目会跳过。普通 `backup` / `pull` 不隐含强制覆盖差异。

`backup --automatic` 按本机每个项目的 `automaticBidirectional` 策略执行：省略或 false 仅自动上传，true 按共同基线逐文件选择上传或下载。与桌面定时器共用执行链、暂停和单轮预算；双方修改、无基线且内容不同、缺少可信元数据和涉及未完成事务的文件保留待处理。比较不下载正文，确定下载计划后才按 Pull 事务执行；不传播删除，单侧缺失可能被另一侧补回。切换策略不重置基线或激活未初始化映射，手动 backup / pull 继续使用明确的单一方向。

需要明确选择使用哪一端的版本时，先输出操作预览，再确认其中的选择清单：

```powershell
HarborBackup.Cli.exe preview --direction Pull --destination "Projects/Demo" --json > "C:\Review\pull-preview.json"
HarborBackup.Cli.exe pull --destination "Projects/Demo" --selection "C:\Review\pull-preview.json"

HarborBackup.Cli.exe preview --destination "Projects/Demo" --prepare-upload --json > "C:\Review\upload-preview.json"
HarborBackup.Cli.exe backup --destination "Projects/Demo" --selection "C:\Review\upload-preview.json"
```

示例中的 `C:\Review` 是用户自行准备的报告目录。`--selection` 指向上一步保存的完整 JSON 报告的绝对路径；核对计划后，可从 `PullSelection.Files` 或 `UploadSelection.Files` 移除不执行的条目，不修改确认值。执行重新检查文件和映射；预览后有变化则跳过。上传先保护云端旧文件，拉取先保护本地旧文件；不传播删除。未选择的待处理差异会使新映射继续等待手动操作。

`recover --destination "Projects/Demo"` 按未完成事务的原映射恢复，包括已切换或移除的旧目录。恢复不会将旧事务改指向新目录，也不会激活新映射的自动备份。
