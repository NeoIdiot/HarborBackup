# CLI 使用说明

CLI 与托盘使用相同用例、事务执行器和兼容运行锁。支持以下命令，不依赖旧 PowerShell 作为运行时。

默认安装包内含私有 Desktop 运行时。CLI 与主窗口共用安装版配置默认位置 `%LOCALAPPDATA%\HarborBackup\workspace`；无安装标记的开发目录使用 EXE 同目录。--config / --system-config 可明确覆盖，隔离验证始终显式指定；云根仍使用 --onedrive-root 或环境变量。依赖运行时的诊断发布现在同样要求 Desktop Runtime，以与 Tray 共享一致的框架程序集。

| 命令 | 行为 |
| --- | --- |
| preview | 只读预览，支持 --direction Backup 或 Pull |
| verify | 只读强制完整哈希，支持两个方向，不执行恢复 |
| backup | 恢复本机未完成事务，再按新观察备份；仅写目标 |
| pull | 恢复并取回，必须用 --destination 明确选择项目；仅写源 |
| recover | 仅检查、保护、补交状态和清理可确认的事务，不重放文件覆盖 |
| status | 输出本机暂停状态与最近运行摘要，不写入 |
| pause / resume | 持久化本机后续自动备份暂停状态，手动运行仍可用 |

恢复可能处理选中项目上次相反方向的事务，只修复状态或保护材料，不重放源/目标复制。运行目录须使用隔离配置，生产切换尚未执行。

## 构建

在仓库根执行：

```powershell
. .\scripts\development\Use-DotNet.ps1
& $HarborDotNet restore HarborBackup.slnx --configfile NuGet.Config --locked-mode
& $HarborDotNet build src/HarborBackup.Cli/HarborBackup.Cli.csproj --configuration Release --no-restore
& $HarborDotNet .\src\HarborBackup.Cli\bin\Release\net10.0-windows\HarborBackup.Cli.dll --help
```

## 隔离环境示例

下列路径仅为占位示例，必须替换为已准备的测试目录；源根和模拟 OneDrive 根需存在，配置内 source 指向测试源。配置根不能与目标交叠。

```powershell
& $HarborDotNet .\src\HarborBackup.Cli\bin\Release\net10.0-windows\HarborBackup.Cli.dll preview `
  --config "C:\HarborBackup-Demo\config\bakup-config.json" `
  --system-config "C:\HarborBackup-Demo\config\system-config.json" `
  --onedrive-root "C:\HarborBackup-Demo\cloud" `
  --direction Backup --full-check --json
```

--config 和 --system-config 必须提供绝对路径；不自动搜索父目录生产配置。--onedrive-root 可省略，此时读取本进程 OneDrive 环境变量。--direction Pull 只预览取回，并不改写源文件；--destination 按规范化 destination 精确选择一个项目。

在同一隔离配置上执行写入：

```powershell
$cli = '.\src\HarborBackup.Cli\bin\Release\net10.0-windows\HarborBackup.Cli.dll'
$common = @('--config', 'C:\HarborBackup-Demo\config\bakup-config.json',
  '--system-config', 'C:\HarborBackup-Demo\config\system-config.json',
  '--onedrive-root', 'C:\HarborBackup-Demo\cloud')
& $HarborDotNet $cli backup @common --full-check --json
& $HarborDotNet $cli pull @common --destination 'Projects/Demo' --json
& $HarborDotNet $cli recover @common --json
& $HarborDotNet $cli status @common
& $HarborDotNet $cli pause @common
& $HarborDotNet $cli resume @common
```

--direction 仅用于 preview/verify；backup --full-check 会复制，verify 不复制。status/pause/resume 始终输出 JSON，不接受 --destination 或 --full-check。Ctrl+C 在扫描或文件事务之间停止；已经开始的文件事务先安全完成。

默认按旧元数据缓存判断，--full-check 强制重新计算哈希。读取仅联机内容可能触发下载。输出的 CloudUploadStatus 始终是 Unverified。

## 结果和互斥

| 退出码 | 含义 |
| --- | --- |
| 0 | 本地处理完成，无待处理问题；预览不意味着动作已执行 |
| 1 | 参数、配置、状态或项目读取失败 |
| 2 | 有冲突/延期/待交接问题，或本机会话已有运行占用互斥 |
| 130 | 用户取消 |

预览 JSON 保留原 PascalCase 字段；新写入报告和控制命令使用 camelCase。写入报告包含 mode、startedAtUtc、completedAtUtc、projects、cancelled、exitCode；项目含 files、issues、metrics。无效命令和初始化失败输出标准错误文本，非 JSON 错误协议。

CLI 在同一主线程取得/释放旧运行 Mutex，等待后台异步 I/O 返回，避免 Mutex 跨线程释放。preview/verify 发现本设备事务 JSON 时报告 PendingTransactions，不执行恢复，不写缓存、状态、归档、日志或空间释放属性。写入命令先恢复；未知现场保留记录并跳过受影响文件，未知任务/损坏归属阻止项目写入。

写入状态沿用 files v1、status v2，日志为 YYYYMMDD-电脑名.txt 和独立明细。当前记录 freeSpacePending，但尚未请求 OneDrive 释放空间，也未执行归档/日志保留清理；这些配置暂不产生维护动作。copy.threads/retryCount/retryWaitSeconds 尚不用于同步事务，失败保留证据并在后续轮次重检。状态或日志失败返回失败结果，不撤回已经核验的文件复制。

## 可重复验收

```powershell
.\scripts\development\Test.ps1 -Configuration Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\development\Test-PreviewParity.ps1 -LegacyRoot ..
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\development\Test-WriteCycle.ps1 -LegacyRoot ..
```

对照脚本只读取旧函数和源码摘要，合成数据位于 artifacts/validation 下唯一目录，比较 Backup/Pull 文件决策和预览前后内容、目录及写入时间。它不执行旧主入口、不读取真实配置、不改动计划任务。
