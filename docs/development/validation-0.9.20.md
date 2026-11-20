# 0.9.20 网络恢复、目录浏览与项目身份验证（2026-11-20）

本版包含云端目录浏览缓存与有限预取、按项目 ID 关联业务操作，以及网络中断后的原任务安全续行。恢复状态区分自动等待、版本确认、需要修复和主动停止；续行核验原范围、版本回执、映射及预算，不取得手动采用当前本地版本的权限。

## 源码与回归

发布文件、安装包及完整回归均来自干净提交 `f6cd5158e818cdc5b3ded84a2c173712d8cd3751`，tree 为 `ce92bff9584ecbf0bad7a9e64522543669b298af`，`clean=true`。产品及项目依赖锁同步为 0.9.20，第三方依赖版本未变。本记录在打包完成后另行提交，不改变二进制源码身份；未推送远端或发布 GitHub Release。

SDK 10.0.115 / Release，构建零警告、零错误。完整入口 `./scripts/test/Test.ps1 -Configuration Release -IsolatedProfile -NoRestore` 通过 Core 39、Windows 200、Desktop 213、Integration 385，共 **837 项测试**，失败和跳过均为 0。产物维护、源码身份、测试前置条件、发布布局和安装包导出规则的合成检查，以及 Prepared、Archived、Uploaded、Metadata 四阶段进程中断恢复探针全部通过。

证据为 `artifacts/test-results/run-20261120-121849-88ccdd2869674f66bf1334ba2abd4d0a` 内的 `completed.json`、四份 `*.trx` 和 `crash-recovery.json`，源码身份与安装包一致。此前的[目录改名与项目 ID 验证](validation-project-identity-2026-11-20.md)和[网络恢复开发验证](validation-network-recovery-2026-11-20.md)保留各自原有源码与实测范围。

## 安装包与隔离安装

`Publish.ps1 -IsolatedProfile` 使用锁定恢复并校验运行时包 SHA-512。发布为不裁剪的自包含 win-x64，运行时固定为 10.0.14，Inno Setup 固定为 7.1.0；Desktop/CLI 共用运行时与依赖。发布清单为 291 个文件、125,942,724 字节，打包时校验全部文件哈希和许可证。

- 正式安装包：`artifacts/publish/HarborBackup-0.9.20-20261120-122611/installers/HarborBackup-0.9.20-Setup-x64.exe`。
- 固定交付入口：`artifacts/installers/HarborBackup-0.9.20-Setup-x64.exe`，附原始 `package-report.json`、`SHA256SUMS.txt` 和 `latest.json`。
- 大小：37,321,197 字节；未签名。
- SHA-256：`E809D65B401B519D4E6D1E8CB91F498A487ADA028B00869BC7985BABFB5F6C8F`。

先编译独立验证包并执行 `Test-Installer.ps1`，通过 **20 项隔离安装检查**，再从同一份发布文件编译正式包。验证覆盖首次安装、MSAL 离线依赖、无系统 dotnet 的 CLI、桌面从 app 加载内置运行时、账户状态与暂停/恢复、未授权拒绝执行、旧目录模式不回退、忙碌时拒绝升级、升级/卸载保留 data 全文件哈希、重装、旧格式留存后重建、重复安装和配置不可读时中止。

隔离安装证据为 `artifacts/install-validation/b90931bb4a5b4d2f8552c39c8562d9e4/validation.json`。验证包与正式包的报告均绑定上述源码、SDK、运行时及同一发布清单；正式导出副本与原包哈希一致。

## 验证边界

隔离验证只安装到仓库 artifacts，不注册正式卸载项、快捷方式或通知协议，不替换本机正式安装。它不代表生产安装升级或真实业务验收。真实 OneDrive 授权、真实断网恢复、大文件吞吐、实体多设备、长期限流、通知点击、跨显示器 DPI 和完整辅助功能未在本轮执行；业务传输与恢复由合成云盘及协议测试覆盖，进程中断探针不等同于断电。

安装包、配置、身份、日志和构建产物未加入提交；本地产物路径受仓库保留策略管理。
