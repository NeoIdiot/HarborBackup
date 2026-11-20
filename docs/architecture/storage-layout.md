# ADR-0007：单安装根目录与应用、运行时、数据分离

当前云端目录及状态规则以 [0.9 账户传输架构](onedrive-account-transport.md) 为准；本页安装目录与生命周期仍适用，旧同步目录说明仅作历史记录。

状态：0.9.0 正式版；账户传输与数据规则以链接的当前架构为准。

## 安装目录

`%LOCALAPPDATA%/Programs/HarborBackup/{app,data,docs,licenses}`，根目录包含 release.json 与卸载记录。当前用户安装，无须管理员权限；本布局不支持直接安装到普通用户不可写的 Program Files。

app 为 Desktop/CLI 及其共享依赖、SDK 标准自包含发布所需的运行时。两个入口均使用 WinForms 框架 profile；由 SDK 选择运行时文件，不手工删除框架 DLL、不进行 IL 裁剪。PublishTrimmed、PublishSingleFile、PublishReadyToRun 均为 false。发布前验证固定版本运行时 NuGet 包 SHA-512；同名发布文件哈希必须一致，合并后只保留一份。runtimeconfig 必须声明固定版本的 includedFrameworks，禁止引用外部共享框架。安装包不依赖系统 .NET，不注册 PATH，runtime 的维护更新随应用发布。

data/config 分为 projects.json、backup-settings.json、desktop-settings.json、storage-settings.json；全部声明 schemaVersion。备份/CLI 共用 storage-settings，不从 UI 偏好或 OneDrive 环境变量隐式推断根目录。显式命令行根目录仅覆盖本次调用。默认数据路径由 app 的父目录计算，和当前工作目录无关。开发输出不在 app 目录时，数据保存在当前输出目录的 data，测试必须注入独立路径。

## 数据职责

- state/devices/<设备ID>/baselines.json：共同基线，不包含扫描缓存。
- last-run.json：最近结果；scheduler.json：暂停状态；transactions：未完成文件事务。
- 账户直连的 `state/devices/<设备ID>/cloud/<映射摘要>` 还保存网络续行任务：`resume-task.json`、`resume-tasks/<任务ID>`、`active-tasks` 和 `retained-tasks`。原授权、范围、完成回执和预算属于恢复依据，不是可清理缓存；格式与核验规则见[账户传输设计](onedrive-account-transport.md#network-interrupted-task-continuation)。
- last-run 运行摘要、history/logs 写入失败作为报告级诊断返回，已核验文件传输和共同基线不回滚；摘要失败后仍尝试历史写入。基线、事务、就绪标记和保护材料不属于此降级路径，仍严格核验并保留恢复证据。
- cache/hashes/<设备ID>/<项目摘要>.json：可重建观察；损坏或不可读时重新哈希，不能清空基线。
- history：结构化运行报告；logs：日汇总诊断。默认保留 90/30 天，可通过 backup-settings 的 history/logging.retentionDays 调整。
- protection/archives、protection/conflicts：有价值的历史内容和冲突副本。可在设置指定外部保护目录，仅影响后续副本；原目录内容保留。未完成事务时禁止更改存储设置。
- temp：普通临时输出，清理保留最近 24 小时文件。事务暂存必须在业务目标同卷，不属于此目录。

缓存清理由 RunLock 与写入操作互斥，仅枚举 cache/temp，校验路径与链接，不遍历 state/protection。归档版本保留自动清理仍不在本次目录重构范围，未解决冲突不自动清理。设备 ID 使用 HarborBackup/v1 命名域；每个项目有固定的 64 位十六进制项目 ID（projects.json schemaVersion 2），显示名称和排除规则变化不改变映射身份。实际目录映射变更隔离旧基线，事务绑定原映射；排除规则只刷新扫描缓存，基线接续规则见当前账户传输架构。

## 安装生命周期

安装器在复制程序后、解除维护互斥和启动应用前，自动检查数据格式。首次安装生成空配置；旧格式或缺失/损坏的必要配置整体留存后重新生成；当前格式不改写。检查不访问项目源目录或云端。文件占用等读取失败中止安装，禁止把读取失败当作空配置处理。历史验证见 [自动安装重建验收](../development/validation-0.9-preview21.md)。

安装器只登记 app/docs/licenses/release.json，不登记 data。升级在维护互斥保护下重建 app 并移除旧 runtime 目录，保留 data；卸载保留全部 data。没有旧格式迁移器；0.4 安装请先正常卸载，再安装 0.5，避免平铺的旧 DLL 留在根目录。当前安装器不提供断电级原子回滚；失败不能宣称升级成功，重新运行安装包修复程序，保留数据。

## 源码边界

Core 的 Domain 保存纯模型和共同基线决策，Features/Scheduling 保存调度与运行协调；不包含平台副作用或没有用例的接口层。
Windows/FileSystem 承担文件副作用，Persistence/{Configuration,State,History,Cache} 分离持久化职责，Storage 统一路径与可丢弃数据管理，Integration 管理平台身份、互斥和登录启动。
OneDrive/Transport 负责 Graph 协议，Authentication 负责授权及当前 Windows 用户保护的令牌缓存。Windows/Integration 的 BackupRuntime 是桌面与 CLI 共用入口，CloudBackupRuntime 调度项目，CloudBackupSession 按观察与传输拆分源码文件，共享同一会话和事务流程。

Desktop/Features/{Overview,Projects,History,Issues,Settings} 按页面组织；Shell 保留框架与共享状态绑定，Controls、Styling、Localization、Bootstrap 各司其职。CLI 解析命令并调用同一用例。
Core 不引用任何平台或宿主；Windows 使用 Core 和 OneDrive；Desktop/CLI 在组合入口组装 Windows 实现。保持 Core、Windows、OneDrive、Desktop、Cli 五个产品工程。

## 明确限制

文件系统链接替换的恶意竞争、真实 OneDrive 云占位、多设备交接、跨显示器 DPI 和真实登录启动仍需专用环境。目录隔离不等于权限沙箱。未签名安装包仍受目标电脑的应用控制策略约束。
## 0.6 共享元信息扩展

必要逐文件元信息通过 OneDrive 独立子目录共享；本机恢复事务、共同基线和待发布记录仍在 data/state。详见 [共享元信息设计](shared-metadata.md)。

## 0.7 共享保护存储扩展

新保护副本默认进入 OneDrive/HarborBackup/archives 和 conflicts，旧 data/protection 保留。保留策略、统一路径及事务兼容见 [共享存储设计](shared-storage.md)。
