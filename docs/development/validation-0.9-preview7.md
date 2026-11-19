# 0.9.0-preview.7：SDK 自包含 WinForms 发布

日期：2026-11-04。采用用户确认的标准自包含方案；本次不更改备份和账户加载行为。

## 发布布局

- app：Desktop、CLI、SDK 按 WinForms profile 选择的运行时与应用依赖。
- data：配置、令牌、状态、历史、日志、缓存和本机保护副本，不纳入安装文件。
- docs / licenses / release.json：使用文档、授权声明和发行信息。
- 不再生成独立 runtime 目录；升级在原维护互斥保护下移除旧 app/runtime 并写入新 app，保留 data。
- 保留 WinForms；PublishTrimmed=false、PublishSingleFile=false、PublishReadyToRun=false。
- Desktop/CLI 分别发布，同路径文件逐项比较 SHA-256，完全一致才去重合并。
- 固定运行时 10.0.14；两个运行时 NuGet 包由 release.json 固定 SHA-512，发布前校验。包内许可证和第三方声明一起发行。
- 发行 manifest 使用 schemaVersion=3、deployment=self-contained，与云端 repository.json 的版本无关。

## 体积

| 发行文件逻辑大小 | preview.6 | preview.7 | 变化 |
| --- | ---: | ---: | ---: |
| 正式安装程序 | 53,078,152 B / 50.62 MiB | 37,143,384 B / 35.42 MiB | -30.02% |
| 解压后发行文件 | 182,136,517 B / 173.70 MiB | 125,300,509 B / 119.50 MiB | -31.21% |

不包含用户 data、卸载器和文件系统分配开销。两个版本使用相同 LZMA2/ultra64 solid 压缩参数，不通过移除双语界面或手工删除 DLL 缩包。

发行目录：artifacts/publish/HarborBackup-0.9.0-preview.7-20261104-185003。
正式安装程序：installers/HarborBackup-0.9.0-preview.7-Setup-x64.exe。
SHA-256：E55C036D496FD86AE9C48BEA7091C51DE5DCB3887CC1DB27368CC7B11059F1EC。
安装包仍未签名。

## 验证

- 本次 publish 两个入口成功，输出共用同一套运行时文件。
- Test-PublishLayout.ps1：6 项通过，覆盖有效布局、缺失运行时、依赖外部框架、错误版本、WPF 混入、重复 runtime 目录。
- Test-Installer.ps1：实际安装到独立中文/空格路径成功，使用同一 payload 的 ValidationBuild 安装包，不写正式卸载登记、快捷方式或登录启动。
- CLI 在 PATH 移除系统 dotnet、DOTNET_ROOT/DOTNET_ROOT_X64 指向不存在目录时仍可启动。
- 实际托盘进程加载 app/coreclr.dll；没有依赖旧 runtime 路径。
- 打包后的 MSAL 离线调用通过；没有使用真实账户或传输业务文件。
- 未授权备份拒绝执行，源文件与旧配置保持不变。
- 托盘运行时安装升级被阻止；通过 --shutdown 安全退出。
- 重装/升级/卸载保留测试 data 中每个文件的 SHA-256；旧程序文件及模拟旧 runtime 目录被清理。
- 完整记录：artifacts/install-validation/ebbb7fd2b45a4890bf38bf769c9e0570/validation.json。
- 前一轮冗余清理的 327 项测试（Core 178 / Windows 66 / Desktop 39 / Integration 44）已通过。本次只调整发布、安装、验证脚本、文档和项目注释，没有再次改变业务实现。

## 验证边界

隔离升级用新包重装并预置旧 runtime 文件验证清理，不等于对真实生产安装执行升级。正式安装包已生成，尚未覆盖用户当前安装。账户真实登录、双设备交接与长期托盘内存未在本次重复验收；安装验证中的单点 working set 不用作稳定空闲内存报告。
