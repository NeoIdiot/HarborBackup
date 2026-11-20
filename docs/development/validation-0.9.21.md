# 0.9.21 单项目暂停与操作文案验证（2026-11-20）

本版在项目页提供“暂停项目／恢复项目”，只暂停本机选中项目的后续自动备份与自动重试，重启后保留。手动操作仍可用，恢复后仍遵守全局暂停。项目列表显示“已暂停”；“加入云端”“取回项目…”“刷新云端”等操作使用较短文案，中英文同步更新。

## 源码和开发验证

功能与发布配置提交为 `d4adc63f9d13186a692bde2ec7e0f906c4a0bb4a`，tree 为 `08705a18eb52ea4076d85cbc3b2a87af87e1bf1d`。版本和项目依赖锁由 0.9.20 同步到 0.9.21，第三方依赖未变。发布及两种安装包均使用该干净提交（clean=true）；本验证记录另行提交。

版本号调整前，完整入口 `./scripts/test/Test.ps1 -Configuration Release -IsolatedProfile -NoRestore` 通过 Core 39、Windows 204、Desktop 213、Integration 386，共 **842 项测试**，失败和跳过均为 0。构建零警告、零错误；产物维护、源码身份、测试前置条件、发布布局及导出合成检查，以及 Prepared、Archived、Uploaded、Metadata 四阶段进程中断恢复均通过。

该次开发回归使用未提交的功能工作区，记录中的基准提交为 `b5c9b153de29d733b83c72ccce39af580f0fa55a`、clean=false；它不是最终干净提交的全量测试。证据目录为 `artifacts/test-results/run-20261120-124646-a25a9e55f1e048dabbc9806ae42463f7`。中英文项目列表定向 2 项再次通过，截图核对按钮完整显示、英文窄窗口自动换行、暂停与恢复状态；报告位于 `artifacts/test-results/project-pause-visual`，图片位于 `artifacts/screenshots/project-pause`。版本调整后执行了锁定恢复、自包含发布和下述隔离安装检查，未重复整套开发测试。

## 发布和隔离安装

`Publish.ps1 -IsolatedProfile`、`Build-Installer.ps1 -ValidationBuild`、`Test-Installer.ps1` 和正式 `Build-Installer.ps1` 依次通过。SDK 为 10.0.115，运行时固定 10.0.14，Inno Setup 7.1.0；自包含 win-x64，不裁剪，Desktop/CLI 共用运行时。发布清单含 291 个文件、125,948,207 字节。

- 发布目录：`artifacts/publish/HarborBackup-0.9.21-20261120-130139`。
- 正式交付入口：`artifacts/installers/HarborBackup-0.9.21-Setup-x64.exe`。
- 正式包大小：37,329,147 字节；未签名。
- SHA-256：`FF524EAD6C4CA1736D22285BF68B5A66B7C9B1C2832A186C1E433337CD40F806`。

独立验证包通过 **20 项隔离安装检查**，证据为 `artifacts/install-validation/d2f1d343a0ba45beaabcea5587ce5dcf/validation.json`。检查覆盖首次安装、离线授权依赖、无系统 dotnet 的 CLI、桌面加载 app 内运行时、全局暂停／恢复、无授权时拒绝执行、旧目录模式不回退、忙碌时拒绝升级、升级／卸载保留 data 全文件哈希及格式初始化留存。正式导出副本、发布目录原包和报告的 SHA-256 一致。

## 验证边界

隔离安装仅使用仓库 artifacts 内的合成数据，未改本机正式安装和真实业务数据。单项目暂停的交互及执行行为由源码 UI 和合成传输测试覆盖，未另作安装版该功能的人工验收。未执行真实 OneDrive 授权、真实传输、实体多机、跨显示器 DPI 或完整辅助功能验收。进程中断探针不等同于断电。未推送远端或发布 GitHub Release。构建产物未加入提交，本地产物受保留策略管理。
