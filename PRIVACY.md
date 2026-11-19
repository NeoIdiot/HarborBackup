# 隐私与数据位置 / Privacy and data locations

HarborBackup 直接连接用户授权的 Microsoft OneDrive 账户。应用未集成独立的分析、遥测或崩溃上报服务，也没有 HarborBackup 中转服务器。Microsoft 登录和 Graph 服务会处理授权、请求及必要的客户端信息，其服务政策由 Microsoft 提供。

## 授权与本机数据

- 使用系统浏览器、MSAL 公共桌面 OAuth 与 PKCE，请求 Graph `Files.ReadWrite` 权限；应用不收集 Microsoft 密码、不嵌入客户端密钥。
- Public Client ID 是公开应用标识，不是密钥。自行发行 Fork 时应注册自己的 Microsoft Entra 桌面应用并配置 Client ID；不要复制维护者或其他用户的授权缓存。
- 授权缓存位于安装数据根的 `data/credentials`，使用 Windows DPAPI CurrentUser 保护。它绑定当前 Windows 用户环境，不作为可移植备份；这不表示其他本机配置或全部文件都已加密。
- `data/config` 保存项目目录、账户与仓库绑定和偏好；`data/state` 保存共同基线、恢复记录和核验状态。运行报告、诊断及保护副本也可能包含文件名和本机绝对路径。

## OneDrive 中的数据

- 项目目标使用普通文件、原文件名和目录。HarborBackup 没有提供端到端内容加密；能够访问对应 OneDrive 内容的人也可能访问这些文件。
- 管理目录保存文件摘要、大小、时间、相对路径、项目/设备标识、云端项版本、运行观察、上一份归档及冲突/恢复材料。
- 日常比较和备份核验不下载云端业务正文；明确取回、独立恢复/演练以及本机保留保护副本会下载所选内容。
- 断开账户用于移除授权，不会删除项目、云端备份、历史或恢复材料；卸载也保留本机 data。不要为清理隐私而直接删除仍需恢复的 state 或保护材料。

## 公开问题与贡献

不要把真实配置、整个 data、令牌缓存、日志包或含真实路径的截图提交到 GitHub。先用合成数据复现并脱敏路径、账户、设备、授权头和预授权下载地址。安全问题请按 [SECURITY.md](SECURITY.md) 私下报告；普通问题见 [SUPPORT.md](SUPPORT.md)。

## English

HarborBackup connects directly to the Microsoft OneDrive account you authorize. It has no HarborBackup relay server or separate analytics/crash-reporting service. Microsoft authentication and Graph process the requests and client information needed for those services.

Authentication uses the system browser and MSAL public-client OAuth with PKCE and the Graph `Files.ReadWrite` scope. A public Client ID is an application identifier, not a secret. Fork distributors should register their own desktop application. Never share or redistribute authorization caches.

Token caches under `data/credentials` are protected by Windows DPAPI for the current user. Configuration, state, reports, paths and other local files are not thereby encrypted. OneDrive business files retain ordinary names and contents; HarborBackup does not provide end-to-end content encryption. Management metadata includes relative paths, hashes, sizes, times, project/device identifiers and cloud versions, together with archives and conflict/recovery materials.

Normal comparison does not download business content. Explicit pulls, restores, restore tests and local protection retention do download requested content. Disconnecting does not erase backups or recovery evidence; uninstalling retains data. Share synthetic, sanitized reproductions rather than configuration, token caches, logs or screenshots containing private information.
