# 开发和验证

先阅读 [当前账户传输规则](../architecture/onedrive-account-transport.md) 和 [安装与源码布局](../architecture/storage-layout.md)。版本实测记录见 [验证记录索引](validation-index.md)，仅用于审计。

当前版本的构建、打包及验证限制见 [0.9.17 发布记录](validation-0.9.17.md)。

Windows / .NET 10 SDK，global.json 固定 SDK 基线；使用 scripts/build/Use-DotNet.ps1。NuGet 缓存、工具和报告位于 artifacts，禁止提交。

发布、打包、完整测试和隔离安装验收成功后自动清理旧产物。`scripts/build/artifact-retention.json` 的 `keepCount` 默认是 2：每类保留最近两批，另保留最近一批成功结果、最后包含正常安装包的发布批次及当前操作使用的输入/输出。`PreservePaths` 可以指向批次内的文件或子目录，清理会保护所属批次。失败时不触发清理，旧失败批次可由下次成功运行清理。发布、测试、打包、安装验收、安装包导出和手动清理在同一仓库互斥。

清理只处理 `artifacts/publish/HarborBackup-*`、`artifacts/install-validation/<32位ID>` 和 `artifacts/test-results` 的批次子目录；不清理依赖、工具、诊断、截图、其他测试材料或正式安装数据。链接及含链接的目录跳过。历史验证文档中的本地产物路径可能随清理失效，文档和已记录的摘要不受影响。

正常 `Build-Installer.ps1` 成功后自动调用 `Export-Installer.ps1`，将校验后的安装包放到固定入口 `artifacts/installers`；`-ValidationBuild` 不更新该入口。已有包首次导出可运行 `./scripts/release/Export-Installer.ps1`，省略参数时选择 `artifacts/publish` 中最近的正常 `installers/package-report.json`，也可用 `-PublishDirectory <发布目录>` 指定批次。用 `Invoke-Item ./artifacts/installers` 快速打开。

固定入口包含带版本名的 exe、原始 `package-report.json`、`SHA256SUMS.txt`、`latest.json` 和 README。导出只复制并校验，不重建或改写原报告的 source，原 publish 树继续作为证据保留。latest 表示最近正常构建的选择结果（显式指定时为所选批次），不保证是最高版本、仓库 HEAD 的构建或已通过安装验收；来源和实测范围以报告及对应验证记录为准。

立即清理可执行 `./scripts/build/Clean-Artifacts.ps1`；加 `-WhatIf` 仅列出计划，加 `-KeepCount 1` 可临时每类只保留最近一批，前述成功、正常安装包和使用中的额外保护批次仍保留。清理边界、保留策略和并发保护的合成测试为 `./scripts/test/Test-ArtifactCleanup.ps1`，完整测试入口也会先执行它。测试报告现在按每轮独立目录保存，不再持续堆入 `test-results/current`。

## 命令

```powershell
./scripts/test/Test.ps1 -Configuration Release -IsolatedProfile
./scripts/release/Publish.ps1 -IsolatedProfile
./scripts/release/Build-Installer.ps1 -PublishDirectory <发布目录> -ValidationBuild
./scripts/test/Test-Installer.ps1 -Installer <验证安装包>
./scripts/release/Build-Installer.ps1 -PublishDirectory <发布目录>
```

四个测试工程为 Core、Windows、Desktop、Integration。串行运行工程，因为真实运行互斥测试必须使用同一个生产锁名；单个工程保留其受控并行设置。

完整入口还会构建 `scripts/test/CrashRecovery` 合成探针，在 Prepared、Archived、Uploaded、Metadata 四个边界等待父进程强制终止，再由新进程读取持久合成云盘和本地事务恢复。只终止本轮创建的探针 PID，不关闭正式应用；证据写入该轮 `crash-recovery.json` 和各独立用例目录。该测试不等于断电、真实 Graph 故障或实体双机验收。单独执行入口为 `scripts/test/Test-CrashRecovery.ps1`，仍须先退出正式程序并避免与其他构建/测试并行。

优化专项测试包括 CloudRestoreTests（独立输出/正文/版本/预算轮转）、MetadataCheckpointTests（冷/热读取与长期目录）、ProjectCoverageTests/ProjectHealthTests/ProjectHealthUiTests（覆盖范围、映射、新鲜度和去重）、BandwidthLimitTests（共享速率/协议/取消）、CloudConflictExportTests/RestoreUiTests/RestoreReportPresentationTests（另存、双语控件及结果说明）。百万条目录基准可在隔离环境设置 `HARBORBACKUP_LARGE_METADATA_BENCHMARK=1` 后仅运行 `MetadataCheckpointTests.LongHistoryIndexScalesWithCatalogAndKeepsOnlyCurrentBodies`；普通回归使用较小规模，测量结果不设机器相关的耗时通过阈值。

运行完整测试前，从托盘退出本机 HarborBackup，并等待 CLI 备份/预览结束。Test.ps1 在构建前及各测试工程启动前检查桌面进程、Running 标记及运行锁，前置条件不满足时明确停止，不把它计作业务断言失败，也不自动关闭正式程序。`-IsolatedProfile` 仅隔离部分目录，不隔离生产互斥量；测试期间不要重新启动应用，启动时仍可能产生竞争。隔离锁与进程探针的合成验证入口为 `scripts/test/Test-TestEnvironment.ps1`，只使用随机名称和合成进程结果。

正式发布要求所有所需源码、资源及文档已提交，且没有未提交或未跟踪文件。Publish.ps1 在开始和结束核验 commit/tree/干净状态，manifest v4 与 payload/release.json 记录源提交、tree、SDK；构建器只接受与当前源码身份一致的 payload，package-report.json 也记录该身份。源文件在构建中改变会使发布失败。合成 Git 仓库验证为 `scripts/test/Test-SourceIdentity.ps1`，不会改用户 Git 身份或全局配置。开发修改可正常 build/test，不能以脏工作区直接正式打包。

完整测试的 run-context.json 和成功时的 completed.json 记录 commit/tree/clean、SDK 和构建配置。开始/结束的提交、tree 或干净状态不同则不标记该轮完成。开发测试可在脏工作区运行，但明确记录 clean=false，该提交号不代表完整的脏工作区源码快照；发行验证应使用干净提交。

先提交产品源码与发布配置，再发布和打包，之后可另提交验证记录。发布 tag 指向真正生成 payload 的源码提交；不要给无法精确还原的旧工作区补造历史 tag。此约束保证源码可追溯，不承诺含编译时间信息的安装器逐字节可复现。状态摘要/历史/日志失败会作为报告级 Diagnostics 返回，传输结果及 CloudUploadStatus 保留；CLI 返回待处理码 2，已有传输失败仍为 1、取消仍为 130。历史写入仍会在摘要失败后尝试，介质自身失效时不能保证错误也能落盘。共同基线、事务、就绪标记和保护材料仍严格处理，绝不按辅助写入降级。

Core 决策测试覆盖共同基线的单方/双方修改、无基线、三种时钟顺序、双向缺失与目录、不可信观察和无效基线。旧 PowerShell 时间规则夹具已移除，不再作为当前行为契约。

StorageLayoutTests 覆盖清理不丢事务和保护副本、清理互斥、稳定项目 ID。Integration 工程使用合成云盘验证账户传输、并发冲突、归档和恢复。旧目录模式专属测试随实现移除；配置、语言、界面性能、路径和 Graph 协议测试保留。

安装验证使用无卸载注册、无开始菜单快捷方式的隔离构建，目录位于 artifacts/install-validation。实际安装并执行 CLI、桌面，检查 coreclr 从 app 加载、CLI 在无系统 dotnet 路径下启动；验证账户配置、暂停/恢复、未授权拒绝执行和旧模式不回退，检查升级/卸载前后的 data 全文件哈希。实际云端复制由合成集成测试覆盖，真实授权验收单列。包含中文空格路径及不同工作目录。不会改用户正式安装或生产备份。

UI 可设置 HARBORBACKUP_SCREENSHOT_DIR 导出实际控件图片。DrawToBitmap 不等同于跨屏或真实辅助功能系统验收。Measure-InstalledTray.ps1 使用实际安装版进程采样，不用测试宿主冒充托盘内存。

导航性能回归使用合成配置和云端目录读取器。主窗口显示后逐页预创建原生控件，并异步预读设置草稿；设置行直接测量布局，避免多层 AutoSize 表格在切换可见性时重复递归测量。页脚始终预留设置按钮栏高度，避免切换页签时内容区反复缩放。SettingsNavigationPerformance 等待预创建完成，在中英文下逐一记录五个页签首次及重复切换的同步阻塞和总耗时，同时检查内容区高度不变；设置截图目录时输出 navigation-performance.json 和各语言单独记录，同步阻塞上限为 150 ms。该指标衡量准备完成后的切换，不代表冷启动耗时或真实安装版验收。SettingsLoadingTests 覆盖预读合并、加载期间禁用编辑、草稿保留和关闭期间读取完成；语言/紧凑布局回归同时检查语言选项和行内操作完整显示。
