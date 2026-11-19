# 0.9.19 三档自动备份模式与紧凑列表验证（2026-11-19）

本版将项目自动模式改为“仅上传 / 双向备份 / 仅下载”，默认仅上传；桌面与托盘“立即运行”按项目策略执行。显式上传、下载保持各自方向，切换模式保留映射、共同基线和初始化状态。仅下载不改写云端业务文件，管理元数据与覆盖前保护材料仍按原规则保存；不传播删除，双方修改等待明确选择。

项目列表移除重复的实际目标列，保护状态使用短文字和固定行高，完整成功、恢复核验和积压证据保留在悬停提示及项目状态窗口。编辑窗口保留相对 OneDrive 根目录的云端目录。中英文界面、架构、示例和用户指南同步更新。

## 源码与回归

发布文件和完整回归来自干净提交 `87b9d15ca2d6b2c65ae31aba374add2613bd0f32`，tree `09894771664d4aa5c6704a1817c0fd29d02062e0`。版本与项目依赖锁为 0.9.19，第三方依赖版本未变。本记录在安装包完成后另行提交，不改变二进制的源码身份；未推送远端或发布 GitHub Release。

SDK 10.0.115，Release 构建零警告、零错误。完整入口 `./scripts/test/Test.ps1 -Configuration Release -IsolatedProfile -NoRestore` 通过 Core 31、Windows 186、Desktop 154、Integration 295，共 **666 项测试**，失败和跳过均为 0。产物维护、源码身份、测试前置条件、发布布局及安装包导出规则的合成检查，以及 Prepared、Archived、Uploaded、Metadata 四阶段进程中断恢复探针全部通过。

回归证据为 `artifacts/test-results/run-20261119-163623-89e248e2bc8c4bb4941588c88b37a9ca` 内的 `completed.json`、四份 TRX 和 `crash-recovery.json`，记录 `clean=true`，与发布文件和安装包的源码身份一致。此前的界面与开发验证另见[专项记录](validation-project-modes-2026-11-19.md)，该记录保留原有源码及验证范围。

## 安装包与隔离安装

发布入口 `Publish.ps1 -IsolatedProfile` 使用锁定恢复，校验运行时包 SHA-512；运行时固定为 10.0.14，Inno Setup 固定为 7.1.0。发布为不裁剪的自包含 win-x64，Desktop/CLI 共用运行时和依赖。291 个发布文件共 125,808,708 字节，清单中的每项大小和 SHA-256、项目及第三方许可证均已核对。

- 正式安装包：`artifacts/publish/HarborBackup-0.9.19-20261119-164105/installers/HarborBackup-0.9.19-Setup-x64.exe`。
- 固定交付入口：`artifacts/installers/HarborBackup-0.9.19-Setup-x64.exe`，附原始 `package-report.json`、`SHA256SUMS.txt` 和 `latest.json`。
- 大小：37,295,156 字节；未签名。
- SHA-256：`9C5E3FBB37C4C8C2AF83BC63DDEAA9E5FEFD8200C08650077C76819FB90F55B7`。
- 验证包与正式包使用同一份发布文件，两份报告均绑定上述干净源码、SDK 和运行时。

执行 `Build-Installer.ps1 -ValidationBuild` 后运行 `Test-Installer.ps1`，通过 **20 项隔离安装检查**：首次安装、MSAL 离线依赖加载、无系统 dotnet 的 CLI、桌面从 app 加载自包含运行时、账户状态与暂停/恢复、未授权拒绝执行、旧目录模式不回退、忙碌时拒绝升级、升级/卸载保留 data 全文件哈希、重装、旧格式留存后重建、重复安装及配置不可读时中止。证据为 `artifacts/install-validation/bb2ae30ce78b4f55be5d98f1aa4d53d5/validation.json`。

受限执行环境的首次安装尝试因 Windows `localappdata` 目录解析失败而退出，未计为通过；之后在正常用户执行环境完成上述隔离安装检查。验证目录仍位于仓库 artifacts，不注册正式卸载项、快捷方式或通知协议，不替换正式安装。

## 验证边界

真实 OneDrive 授权和传输、实体多设备、长期限流、通知点击、跨显示器 DPI 与完整辅助功能未在本轮执行。传输策略、保护、预算、取消和恢复由合成云盘与协议测试覆盖；隔离安装检查不代表真实业务升级验收。产物、配置、身份和日志不提交，产物路径受仓库保留策略管理。
