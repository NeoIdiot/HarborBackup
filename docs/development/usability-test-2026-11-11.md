# 用户视角测试报告（2026-11-11）

本报告记录对当前工作区（提交 `5571311`，版本 `0.9.0`，账户直连模式）按实际使用方式进行的测试。测试覆盖自动化回归、CLI 黑盒、桌面端真实启动与界面操作、自包含发布、隔离安装验收、产物清理和多语言文案核对。

第 1～11 节为 2026-11-11 的原始测试记录；第 12 节记录 2026-11-12 对 P2-1、P3-1～P3-6 的修复和复核。原始问题描述保留用于对照，当前状态以第 12 节为准。

结论：**未发现阻断性或数据损坏级缺陷**。产品主链路（安装、首次配置、新增/编辑项目、暂停恢复、错误提示、升级/卸载保留数据）可用。发现 1 项中等严重度的验证脚本兼容问题、1 项中等严重度的运行详情可诊断性问题，以及若干轻微体验和文案问题，见第 9 节。

未测试真实 OneDrive 账户授权和真实云端往返，也未在正式安装、生产数据或其他实体设备上操作。因此本报告**不代表安装版或真实账户验收通过**。

## 1. 测试环境

| 项目 | 值 |
| --- | --- |
| 操作系统 | Windows 11 Pro 10.0.26300 |
| 运行时 / SDK | .NET 10（`global.json` 固定基线） |
| Shell | Windows PowerShell 5.1.26100.9818、PowerShell 7.6.10 |
| 产品版本 | 0.9.0，`FileVersion` 0.9.0.0，`ProductVersion` 0.9.0+5571311… |
| 构建 | Release，自包含 `win-x64`，`PublishTrimmed=false` |
| 测试账号 | 合成配置（`test@example.invalid` / 合成 drive、root、repository），无 MSAL 令牌缓存 |

所有测试只写入仓库内的 `artifacts/`，安装验收写入 `artifacts/install-validation/`。未改系统计划任务、正式安装目录或真实 OneDrive 数据。

## 2. 结果总览

| 领域 | 结果 | 说明 |
| --- | --- | --- |
| 单元/集成测试 | 通过 | 366 项（Core 15、Windows 108、Desktop 87、Integration 156），干净串行运行全部通过 |
| 产物清理合成测试 | 通过 | 保留批数、最近成功保护、范围、链接、互斥均验证 |
| CLI 黑盒 | 通过 | 73 个用例，参数、退出码、配置校验、未授权路径符合设计 |
| 桌面端真实启动 | 通过 | 主窗口、设置、账户对话框、添加项目窗口均可正常渲染和操作 |
| 新增项目云端检查 | 通过（合成） | 接续、首次核对、取消不保存、已存在项目引导、离线编辑由测试覆盖 |
| 自包含发布 | 通过 | 发布清单、运行时、版本信息一致 |
| 隔离安装验收 | 通过（PowerShell 7） | 20 项检查全部通过，含升级/卸载数据哈希 |
| 隔离安装验收 | **失败（PowerShell 5.1）** | 脚本在第 48 行中止，见问题 P2-1；已于 2026-11-12 修复并复核通过，见第 12 节 |
| 多语言资源 | 基本通过 | 原始轮次中英各 459 项且键完全一致；1 项中文资源值为英文，见问题 P3-1。修复后为 462 项，见第 12 节 |
| 真实账户 / 真实离线 | 未测试 | 无授权账户；未断开本机网络，见第 10 节 |

## 3. 自动化回归

使用 `Test.ps1 -Configuration Release -IsolatedProfile`，并设置 `HARBORBACKUP_SCREENSHOT_DIR` 导出真实控件图片。

- Core 15 项、Windows 108 项、Desktop 87 项、Integration 156 项，全部通过。
- 首轮完整运行时 Integration 有 2 项失败：`AccountConfigurationDoesNotRequireInstalledOneDriveClient`、`UnboundAccountCannotFallBackToExistingSyncDirectory`，错误为 `Busy: another backup or preview holds the run lock.`
- 根因是同时运行的 CLI 黑盒用例占用了进程间共享的 `Local\HarborBackup.Run`。单独重跑 Integration 后 156/156 通过，确认不是产品缺陷。这反而验证了跨进程运行互斥有效，但也说明完整测试要求机器上没有其他 HarborBackup 操作在跑；失败提示对普通用户偏技术化。
- `Test-ArtifactCleanup.ps1` 在完整测试入口前执行并通过：保留批数、最近成功保护、不越界、链接跳过、并发互斥。
- 测试过程导出 89 个截图/辅助文件，可作为界面走查材料（`artifacts/usability-screenshots/`）。

## 4. CLI 黑盒测试

共 73 个用例，分为全新配置（40）和已绑定合成账户（33）。命令通过隔离的 `--config` / `--system-config` 指向 `artifacts/usability-cli/`，目录名包含中文和空格。

### 4.1 正常路径

- `--help` 和无参数：输出帮助，退出码 0。
- `initialize --fresh`：生成 `projects.json`、`backup-settings.json`，退出码 0。
- 重复 `initialize --fresh`：旧数据完整保留为同级 `data-retained-<id>`，退出码 0。
- 绑定合成账户后：`status --json`、`pause --json`、`resume --json` 完全离线可用，暂停状态正确持久化。
- `cloud-status --json`：返回账户配置和 `requiresAuthorization`。
- 未授权时执行 `preview` / `backup` / `pull` / `recover`：退出码 1，报告包含 `CloudPreviewFailed` / `CloudOperationFailed`，说明“需要授权”；未观察到业务文件变化。
- `devices` 未授权：退出码 1，仅输出错误，不产生半截 JSON。

### 4.2 参数与配置校验

以下均返回退出码 1 和明确错误，没有崩溃或堆栈泄漏：

- 未知命令/选项、重复标志、缺少选项值、`--direction` 用错命令、非法方向。
- `--project-id` 与 `--destination` 同时使用、非法项目 ID、`--selection` 缺目标、`--prepare-upload` 缺目标、`--automatic` 带清单。
- `--config` 与 `--system-config` 不在同一目录。
- 配置文件缺失、`folders` 缺失、`schemaVersion=1`、重复 JSON 键、重复项目 ID、目标嵌套重叠、目标与 `HarborBackup` 管理目录重叠、非法项目 ID、非法 `mappingVersion`、旧字段 `backupName`、目标路径含 `..`。
- `backup-settings` 间隔为 0、使用非 SHA-256 算法。
- 账户绑定缺少 `driveId`。
- 账户文件 v1 可被内存规范化为 v2，`cloud-status` 正常返回。

单个项目的排除规则非法（如目录规则含 `*`）不会阻断 `status`，而是记录为项目级配置错误，留待预览显示。这与“配置错误不阻止读取其他状态”的设计一致。

### 4.3 CLI 发现

- **JSON 命名风格不一致**：`preview`/`verify` 输出 PascalCase（`Projects`、`Plans`、`ExitCode`），`backup`/`pull`/`recover`/`status` 输出 camelCase（`projects`、`files`、`exitCode`）。同一套 CLI 的脚本消费方需要写两套字段名。
- **CLI 面向用户的消息基本为英文**（参数错误、配置错误、授权错误），而桌面端和文档以中文为主。作为技术工具可接受，但与产品整体语言不一致。
- `initialize --fresh` 的 JSON 把中文路径转义为 `全...`，合法但不易读。
- 未绑定账户时，`status`/`pause`/`resume` 也无法执行。账户直连模式下可以理解，但“暂停自动备份”这类本机安全动作在授权失效时同样被拒绝，值得确认是否符合预期。
- 开发构建不嵌入默认 Client ID（发布构建才会注入），因此开发版 `cloud-folders`/`cloud-disconnect` 会提示配置 Client ID；发布版无此问题。验证文档可提示这一点，避免误判。

## 5. 桌面端实际使用

使用全新自包含发布复制到 `artifacts/usability-live/`，通过 `initialize --fresh` 生成数据后真实启动 `HarborBackup.Desktop.exe`，使用 UI Automation 操作并截图（`live-01` ~ `live-05`）。

### 5.1 已确认可用

- 首次启动主窗口正常，导航、空状态、页脚状态一致。
- “设置”页正常：语言、账户连接、登录启动、备份间隔/延迟/时限、保存/重载/帮助。
- “账户与管理目录”对话框正常打开；未连接时显示“尚未连接 / 未加载 / 请选择仓库文件夹”，并提供创建或使用已有文件夹两种方式。
- “添加备份项目”窗口正常打开，项目 ID、本地源、云端目标、实际目标、排除规则、检查/预览/保存/取消/帮助均可见。
- 将源目录指向安装根目录内部时，保存被正确拒绝并提示 `Source overlaps internal data directories.`，未写入配置。
- 改为合法源目录后，因未连接账户，保存被拒绝并提示 `OneDrive account transport is not enabled.`，未写入配置。说明“新项目保存前必须完成云端检查”的约束生效。
- 账户对话框可通过“关闭窗口”正常关闭；测试结束时强制结束了隔离实例，托盘退出和“退出并停止本机自动备份”流程未在本次实机操作中验证。

### 5.2 体验观察

- **首次安装的概览文案**：数据目录已由安装器初始化，但没有账户、没有项目。概览仍显示“备份位置已就绪，请添加项目。”，而底部状态同时显示未验证。对首次用户，这可能让人误以为云端位置已经可用。建议区分“本机数据已就绪”和“账户/云端已就绪”。
- **添加项目窗口的初始焦点**：打开后焦点落在只读的 64 位项目 ID 上并全选，蓝色高亮看起来像可编辑输入框。建议初始焦点放在“本地源目录”。
- **账户对话框未连接状态**： “刷新记录”“上一级”“使用当前文件夹”“应用云端存储设置”仍为可点击状态，点击后才会提示未连接。建议在未连接时禁用云端浏览和应用按钮。
- **排除规则区域**：默认 780×740 窗口下，排除目录/文件的输入框底部被页脚遮住，需要滚动才能完整查看。功能无碍，但初次使用不够直观。
- **冲突对话框**：路径、两侧版本和确认按钮清晰；测试数据使用占位哈希，实际显示长度需在真实冲突中复核。

## 6. 发布与隔离安装

### 6.1 发布

执行 `Publish.ps1 -IsolatedProfile`：

- 发布目录 `artifacts/publish/HarborBackup-0.9.0-20261111-222005`。
- `payload/app` 为自包含 `win-x64`，未裁剪；`ProductVersion` 为 `0.9.0+5571311…`，与当前提交一致。
- 发布成功后自动清理了更早的发布批和 `test-results/network-targeted` 子目录，符合新保留策略。

### 6.2 安装包

执行 `Build-Installer.ps1 -ValidationBuild`：

- 验证安装包 37,197,846 字节。
- SHA-256：`4B2C5517064A32349E82E1BC8DD361928CFC42664284F9E590D910239464EE05`。
- 未签名（验证构建），不可作为正式分发包。

### 6.3 隔离安装验收

- **PowerShell 7.6.10**：`Test-Installer.ps1` 通过，20 项检查全部通过，`validation.json` 位于 `artifacts/install-validation/419b7a03b035438b83d683f96c1f3a7e/`。覆盖首次安装生成配置、打包 MSAL 离线检查、CLI/桌面共享配置、中文空格路径、未授权拒绝执行、升级/卸载保留全部数据哈希、旧格式重建、重复安装保留数据、读取失败中止安装等。
- **Windows PowerShell 5.1**：同一命令在脚本第 48 行中止，错误为 `NativeCommandError: Connect OneDrive and select a cloud repository in settings.`，只留下安装目录和 `install.log`，没有 `validation.json`。见 P2-1。
- 安装验收只使用仓库内独立目录，未升级正式安装，未操作生产数据。

## 7. 多语言与文案核对

- 原始轮次：`Strings.resx` 与 `Strings.en-US.resx` 各有 459 个键，键集合完全一致，无单边缺失。
- 英文资源中没有残留中文值。
- 中文资源中有 1 个键的值仍为英文：`Cloud target changed. Browse and select the folder again.`（新增于首次云端检查，见 P3-1）。
- 新项目未连接账户时的提示 `OneDrive account transport is not enabled.`、配置校验错误（如 `Source overlaps internal data directories.`、`Expected a nonempty JSON string.`）没有中文资源，中文界面直接显示英文。
- 新增的网络提示（离线、不可达、安全连接失败、服务不可用、等待网络）中英文资源齐全。
- 修复后：两个资源文件均为 462 个键且键集合一致；`Cloud target changed. Browse and select the folder again.`、`OneDrive account transport is not enabled.`、`Connect OneDrive and select a cloud repository in settings.`、`OneDrive authorization required. Reconnect in settings.` 均有中文值。`ConfigurationPresenter` 新增的保存前检查使用已有中文键 `Connect OneDrive and bind a cloud repository before saving projects.`。`Source overlaps internal data directories.` 等底层校验消息仍为英文，属于产品级配置诊断，未在本次翻译。

## 8. 产物清理

- `Test-ArtifactCleanup.ps1` 通过。
- `Clean-Artifacts.ps1 -WhatIf` 正确列出计划删除的旧批次，不实际删除。
- 发布成功后实际删除了更早的发布目录和 `test-results/network-targeted`；安装验收成功后删除了两个更早的验证批，保留最近尝试和最近成功。
- 清理只作用于 `publish`、`install-validation`、`test-results` 的批次子目录；未触及 `artifacts` 下的其他测试材料。
- 注意：`test-results` 下任何子目录都会被视作批次（只要不在保留范围内），不会因为缺少 `completed.json` 而跳过。若有人手工把需要长期保留的材料放在该目录，可能被下一次成功运行清理。

## 9. 问题清单

| ID | 严重度 | 领域 | 问题 | 证据 | 建议 | 状态（2026-11-12） |
| --- | --- | --- | --- | --- | --- | --- |
| P2-1 | 中 | 验证脚本 | `Test-Installer.ps1` 在 Windows PowerShell 5.1 下于第 48 行中止，无法完成安装验收；PowerShell 7.6 通过。脚本声明 `#Requires -Version 5.1`。 | `artifacts/usability-installer-test.log`；不完整目录 `install-validation/55a9…` 无 `validation.json`；已用 `$ErrorActionPreference='Stop'` + 原生命令 stderr 的最小脚本复现，5.1 返回 1、7.6 继续执行。该行早于本次改动 | 原生命令重定向 stderr 时避免受 `$ErrorActionPreference='Stop'` 终止，例如临时设为 `Continue`、使用 `$ErrorActionPreference` 局部作用域或改为捕获输出后判断退出码。 | 已修复，5.1/7.6 均通过 |
| P2-2 | 中 | 运行详情 | 运行详情窗口对网络诊断再次套用“友好提示”，用户无法在界面看到完整异常文本；而用户指南写明可查看完整错误文本。 | `DetailRow.DisplayDetails` 统一调用 `CloudAccountPresentation.Message`；`RunDetailsDialog` 使用该属性 | 仅对概览/普通列表使用友好提示；运行详情保留原始诊断，或同时显示“简短说明 + 技术详情”。 | 保留行为，按实际逻辑改文档 |
| P3-1 | 轻 | 多语言 | 中文资源键 `Cloud target changed. Browse and select the folder again.` 的值仍为英文；新项目未连接账户等错误也无中文。 | `Strings.resx:938`；实机保存截图 `live-05-save-without-account.png` | 补充中文资源；新项目保存前若账户未连接，可先给出中文引导而非直接显示底层异常。 | 已修复 |
| P3-2 | 轻 | CLI | `preview`/`verify` 与 `backup`/`status` 的 JSON 字段大小写风格不一致。 | 两组输出对比（PascalCase vs camelCase） | 统一序列化选项；若需兼容旧脚本，可在文档中明确两套格式。 | 已修复（代码级复核） |
| P3-3 | 轻 | 首次使用 | 无账户、无项目时概览显示“备份位置已就绪”，与实际状态不符。 | `live-01-overview.png`；`MainForm.cs` 仅根据配置文件是否存在判断 `NeedsSetup` | 区分本机数据、账户、项目三个就绪条件；账户未连接时显示“请先连接 OneDrive”。 | 已修复 |
| P3-4 | 轻 | 账户对话框 | 未连接时云端浏览和应用按钮仍可点击，点击后才提示错误。 | `live-03-account-dialog.png`；控件未按连接状态禁用 | 未连接时禁用“刷新记录/上一级/使用当前文件夹/应用云端存储设置”。 | 已修复 |
| P3-5 | 轻 | 添加项目 | 打开窗口时只读项目 ID 获得焦点并全选，视觉上像可编辑。 | `live-04-add-project.png`、`live-05-save-without-account.png` | 初始焦点放到“本地源目录”，项目 ID 可保留可选中复制。 | 已修复 |
| P3-6 | 轻 | 文档 | 指南称同一项目已在本机时会“引导编辑原项目”，实际是报错并让用户自行取消后查找。 | `ProjectEditorDialog` 抛出“此云端项目已加入本机列表，请编辑原项目。” | 文案改为“提示编辑原项目”，或提供直接跳转。 | 按实际行为改文档 |
| P4-1 | 提示 | CLI | `initialize --fresh` 输出把中文路径转义为 `\uXXXX`。 | C08 输出 | 可接受；如需人工阅读可在示例中说明。 | 保留 |
| P4-2 | 提示 | 测试环境 | 完整测试与 CLI 同时运行会因共享运行锁失败，提示 `Busy: another backup or preview holds the run lock.` | 首轮 Integration 2 项失败，单独重跑通过 | 验证文档注明“测试期间不要同时运行 CLI/桌面操作”；测试入口可在遇到 Busy 时给出更明确的说明。 | 保留 |

## 10. 未覆盖范围

以下项目本次无法在不影响真实环境的前提下验证：

- 真实 Microsoft 账户交互授权、真实 OneDrive 上传/下载、真实共享元数据和跨设备冲突。
- 物理断网、网卡禁用、代理/TLS 故障、休眠唤醒后的网络恢复。网络检查的离线分支仅由注入式单元测试覆盖。
- 正式签名的安装包、正式安装目录升级、Windows 计划任务和登录启动的真实行为。
- 生产数据、真实业务路径、大文件/超大数据集性能、UNC/超长路径。
- 屏幕阅读器、键盘完整遍历、高对比度、多显示器混合 DPI 的人工验收；截图和 DPI 测试只能作为参考。
- 两台实体设备之间的完整加入/取回流程。

## 11. 复现材料

| 内容 | 位置 |
| --- | --- |
| 完整测试日志 | `artifacts/usability-test-run.log` |
| 测试结果目录 | `artifacts/test-results/run-20261111-221507-…` |
| 界面截图 | `artifacts/usability-screenshots/`（89 个） |
| CLI 矩阵（全新配置） | `artifacts/usability-cli/矩阵结果.txt` |
| CLI 矩阵（已绑定账户） | `artifacts/usability-cli/已配置/矩阵结果.txt` |
| 实机界面截图 | `artifacts/usability-live/live-01…05` |
| 自包含发布 | `artifacts/publish/HarborBackup-0.9.0-20261111-222005` |
| 验证安装包 | 同上 `validation-installers/HarborBackup-0.9.0-Setup-x64.exe` |
| 安装验收（PowerShell 7） | `artifacts/install-validation/419b7a03…/validation.json` |
| 安装验收（PowerShell 5.1 中止） | `artifacts/install-validation/55a945e7…/install.log` |
| 产物清理预览 | `artifacts/usability-cleanup-whatif.log` |

## 12. 修复与复核（2026-11-12）

本节记录对第 9 节问题的处理。改动位于 `5571311` 之后的工作区，尚未提交。

### 12.1 改动清单

| 项 | 改动 | 主要文件 |
| --- | --- | --- |
| P2-1 | 新增 `Invoke-CliFailure`：重定向原生命令 stderr 前临时把 `$ErrorActionPreference` 设为 `Continue`，再按退出码判断。新增 `Get-DataHash`：通过 `\\?\` 扩展路径和 SHA-256 计算哈希，规避 5.1 的 260 字符限制。数据哈希与校验循环全部改用该函数。 | `scripts/test/Test-Installer.ps1` |
| P3-1 | 补齐 3 条中文资源；修正 `Cloud target changed. Browse and select the folder again.` 的中文值；保存新项目前先检查账户是否启用且已授权，未就绪时抛出带中文资源的错误。 | `Localization/Strings.resx`、`Strings.en-US.resx`、`Presentation/ConfigurationPresenter.cs` |
| P3-2 | `preview`/`verify` 的 `--json` 输出改用 `AtomicJson.Options`（`JsonSerializerDefaults.Web`），与 `backup`/`status` 等命令一致。 | `src/HarborBackup.Cli/Bootstrap/Program.cs` |
| P3-3 | 概览文案按账户状态区分：账户未启用或需重新授权时显示“请先连接 OneDrive 账户并选择云端仓库。”。 | `src/HarborBackup.Desktop/Shell/MainForm.cs` |
| P3-4 | 账户对话框新增 `UpdateCloudControls`：未连接时禁用云端浏览按钮；有已保存仓库时仍允许只修改本机保留策略并应用。 | `Features/Settings/CloudAccountDialog.cs` |
| P3-5 | 添加/编辑项目窗口显示后把焦点放到“本地源目录”输入框。 | `Features/Projects/ProjectEditorDialog.cs` |
| P3-6 | 中英指南改为“同一项目已加入本机时会提示编辑原项目，不再重复添加”。 | `docs/user-guide/onedrive-account.*.md` |
| P2-2 | 按实际行为修改文档：运行详情显示简短错误说明和日志路径，完整原始诊断保留在运行记录和日志中。 | `docs/user-guide/onedrive-account.*.md` |

### 12.2 自动化复核

- 完整回归：Core 15、Windows 108、Desktop 87、Integration 156，共 366 项全部通过，0 失败（`artifacts/fix-verification-test-run.log`，结果目录 `artifacts/test-results/run-20261112-093415-…`）。
- Release 构建 0 警告、0 错误。
- 账户对话框“有已保存仓库、需要重新授权时仍可只修改本机保留策略”的既有测试 `StateIsolationTests.SavedRepositoryRetentionRemainsEditableWithoutCloudListing` 在调整 `apply.Enabled` 规则后通过。
- 中英资源各 462 个键，键集合一致，新增中文值已核对。

### 12.3 安装验收复核

- 从修复后的代码重新发布：`artifacts/publish/HarborBackup-0.9.0-20261112-093828`（自包含 win-x64，未裁剪）。
- 重新构建隔离验证安装包：37,201,135 字节，SHA-256 `0107A46160BB672669B58A687D60DD0D5A487571E2F655F7A04B1021D8051990`，未签名。
- **Windows PowerShell 5.1.26100.9818：`PASS`，退出码 0。** 原第 48 行通过后，暴露出保留数据目录深层路径超过 260 字符、`Get-FileHash` 无法读取的问题；补 `Get-DataHash` 后完整通过。
- **PowerShell 7.6.10：`PASS`，退出码 0。**
- 证据：`artifacts/post-fix-installer-rerun.log`；7.6 的 `validation.json` 保存在 `artifacts/remediation-evidence/ps7-validation.json`。5.1 通过批次的目录随后被产物保留策略清理（最新成功批为 7.6 批次），通过记录以日志为准。

### 12.4 本轮未复核与遗留

- P3-2 只有代码级复核：无真实云端时 `preview`/`verify` 无法生成成功报告，离线只输出错误文本，未取得修复后的 camelCase 样例。
- P3-3、P3-5 未在修复后重新做实机界面走查；P3-4 的按钮状态由单元测试和构造逻辑覆盖，未再次截图。
- 修复后未重跑 CLI 73 项黑盒矩阵；CLI 改动只涉及序列化选项。
- Windows PowerShell 5.1 读取无 BOM 的 UTF-8 脚本时，会把测试安装目录名中的中文显示为乱码（如 `娴嬭瘯`）。功能检查使用变量路径，全部通过；这是脚本编码的显示问题，未在本轮修改。
- 真实 Microsoft 账户、真实网络故障、正式签名安装包、生产数据升级仍属于第 10 节的未覆盖范围，本次修复没有改变这一结论。

本报告为开发环境用户视角测试记录，不等同于正式安装版验收。真实账户、真实网络和正式签名安装包仍需单独验收后再对外声明。