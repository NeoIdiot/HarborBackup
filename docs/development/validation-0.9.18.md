# 0.9.18 项目自动双向备份与 ESC 关闭验证（2026-11-19）

本版增加项目级自动双向备份开关，默认关闭；关闭时继续自动上传，开启后按共同基线逐文件选择上传或下载。双方修改和无基线差异保留冲突并等待明确选择，不传播删除。自动调用共用暂停、预算、互斥和事务保护；策略切换不重置映射、共同基线或首次操作就绪状态。

主窗口 ESC 与点击右上角关闭按钮一致：关闭主窗口，保留托盘和后台任务。模态弹窗优先处理 ESC。项目编辑、列表和运行详情同步提供中英文说明及传输方向。

## 源码与回归

正式发布和完整回归使用干净提交 `94c206802cd300e3ad8736c94ad3f7f61726be1e`，tree `db1f9eb9d27572a53075f897e2464564762c63e8`。版本配置与项目依赖锁同步为 0.9.18，第三方依赖版本未变。本记录在打包后另行提交，不改变安装包的源码身份；未推送远端或发布 GitHub Release。

SDK 10.0.115，Release 构建零警告、零错误。完整入口 `Test.ps1 -Configuration Release -IsolatedProfile -NoRestore` 通过 Core 31、Windows 175、Desktop 149、Integration 286，共 **641 项测试**，并通过产物清理、源码身份、测试前置条件及 Prepared、Archived、Uploaded、Metadata 四阶段进程中断恢复。

新增回归覆盖默认关闭、混合项目隔离、双向单侧修改与缺失、无基线和双方修改保护、未知云端内容拒绝下载、新鲜本地哈希复核、共享预算与持久暂停、并发传输取消后排空及事务保留、策略切换保持恢复证据，以及 ESC 保留托盘、弹窗优先和双语界面。

证据为 `artifacts/test-results/run-20261119-154838-a8f9d597b93a4a1cb4230e544349b2a8/completed.json`、同目录四份 TRX 和 crash-recovery.json，记录 clean=true，与发布文件及安装包的源码身份一致。

## 安装与交付

运行时固定为 10.0.14，发布前锁定恢复并校验两份运行时包 SHA-512。使用 Inno Setup 7.1.0，生成不裁剪的自包含 win-x64 发布文件；Desktop 与 CLI 共用运行时和依赖。

正式包：`artifacts/publish/HarborBackup-0.9.18-20261119-155330/installers/HarborBackup-0.9.18-Setup-x64.exe`。

- 固定交付入口：`artifacts/installers/HarborBackup-0.9.18-Setup-x64.exe`，附原始 package-report.json、SHA256SUMS.txt 和 latest.json。
- 大小：37,279,763 字节；未签名。
- SHA-256：`7F3024B2F35A383ED742FBD5CB9F6DFBF57EA89240AC4B3C9E1A70EC6CF32624`。
- 发布文件：291 个，共 125,796,907 字节。
- 验证包与正式包来自同一份 payload，两份 package-report.json 记录相同的源码、SDK 和运行时。

隔离安装验证通过 **20 项检查**，包含首次安装、打包后 MSAL 离线依赖加载、无系统 dotnet 路径的 CLI、桌面从 app 加载自包含 coreclr、暂停/恢复、未授权操作拒绝执行、旧目录模式不回退、忙碌时拒绝升级、升级/卸载前后 data 全文件哈希保持、重装、旧格式完整留存后重建，以及配置不可读时中止安装。证据为 `artifacts/install-validation/8c0428eb99c044f6ac73ea37ed31c7ee/validation.json`。

## 验证边界

隔离安装位于仓库 artifacts，不注册正式快捷方式、卸载项或通知协议，不替换正式安装。合成数据和源码界面检查不等同于真实账户或正式业务升级验收。

未执行本版真实 OneDrive 授权和传输、实体双机、长期限流、通知中心点击、跨显示器 DPI 或完整辅助功能验收。传输决策、保护、核验和恢复由合成云盘及协议测试覆盖。

产物路径受仓库保留策略管理；后续清理不改变本页摘要。产物、身份和日志不提交。
