# 0.6.1 默认元信息目录与安装体积验证

日期：2026-11-01。

## 路径与手动设置

默认共享路径调整为 `<OneDrive 根目录>/HarborBackup/metadata`，不再包含 AutoBackup 层级。各设备从自己的 OneDrive 根目录解析，不硬编码用户名。

设置中的“共享元信息目录 → 选择目录…”可指定其他 OneDrive 子目录，“使用默认位置”清除自定义值。保存自定义路径后升级不会覆盖它。修改位置用于后续发布，不删除原目录记录；各设备须选择同一共享位置。

新增回归用例验证默认路径、自定义路径持久化、重新读取和恢复默认；原有重叠目录保护继续有效。中英文帮助与架构文档同步更新。

## 验证结果

Release 构建零警告、零错误。286 项测试全部通过：Core 181、Windows 80、Desktop 19、Integration 6。

发布目录：`artifacts/publish/HarborBackup-0.6.1-20261101-130510`。
隔离安装验证：`artifacts/install-validation/abc177f382b2449ab5e6a88a1b051c73/validation.json`。

实际安装后检查了新默认路径上的元信息，devices CLI 摘要与文件一致；备份、覆盖归档、恢复、桌面私有运行时加载、升级保护以及卸载重装保留数据均通过。不替换正式安装。真实跨电脑 OneDrive 网络验收边界仍见 [0.6.0 验证记录](validation-0.6.md)。

## 体积实测

单位为 MiB（1024 × 1024 字节）。安装后数字为隔离安装文件长度之和，包含卸载程序，排除测试生成的 data；不是 NTFS 分配簇的“占用空间”。未来业务数据、日志、缓存和保护副本另计。

| 项目 | 字节 | MiB |
| --- | ---: | ---: |
| 正式安装包 | 52720742 | 50.28 |
| 解压载荷 | 180558819 | 172.19 |
| 实际安装后程序总计，含卸载程序 | 185278138 | 176.70 |
| runtime | 179658519 | 171.34 |
| app | 805709 | 0.77 |
| docs + licenses | 93844 | 0.09 |
| 卸载程序及登记文件 | 4719319 | 4.50 |

根目录另有 747 字节 release.json。完整机器可读结果见发布目录 `size-analysis.json`。正式包 SHA-256：`75D96B09BCE0B32190EB9F4BA6B9B6BBBA0B87CF60B66BEDAB917FFB634A8C1A`。

## 体积增长来源

| 版本 | 安装包 MiB | 解压载荷 MiB |
| --- | ---: | ---: |
| 0.4.4 | 35.04 | 117.91 |
| 0.5.0 | 50.26 | 172.13 |
| 0.5.1 | 50.26 | 172.14 |
| 0.6.0 | 50.27 | 172.19 |
| 0.6.1 | 50.28 | 172.19 |

数据取自保留的各版 installers/package-report.json。0.4.4 使用 .NET SDK 自包含发布，manifest 中 selfContained=true、trimmed=false。0.5 的 app/runtime 分离改成应用依赖相对目录内的私有运行时，并解压完整 .NET Runtime 与 Windows Desktop Runtime 压缩包。两种发行方式都让用户无需预装运行时；不是启用了裁剪与否的差别。

对比 0.4.4/app 的文件名与当前 runtime，当前多出 211 个文件，合计 54677871 字节（52.14 MiB）；该统计是按文件名判断新增项，未包含同名文件大小变化。大文件包括 PresentationFramework.dll、PresentationCore.dll、D3DCompiler_47_cor3.dll、wpfgfx_cor3.dll 等 WPF/图形组件。目录分离后的完整 Desktop Runtime 包含这些组件，尽管当前主窗口使用 WinForms。

主要增长发生于 0.4.4 → 0.5，而非本次元信息功能或路径修改。当前 runtime 文件保持与 0.6.0 完全相同；本次继续遵循完整私有运行时、不裁剪的发布约束，没有手工删除运行时 DLL。
