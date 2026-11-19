# 0.9.0-preview.6 仓库格式 v2

日期：2026-11-04（Asia/Shanghai）。

## 改动
- 仓库标识、账户配置升级至 schemaVersion 2。CloudFolders 移除 Backups，设置窗口移除隐藏旧字段，目录解析器不再接受 backups 区域。
- v1 本机账户配置读取时仅在内存迁移；显式保存写入 v2。v2 拒绝旧字段/未知目录字段，仓库必须声明三个管理目录。
- 新建仓库使用 v2；已有仓库通过应用存储设置或 cloud-upgrade 显式升级，普通备份/预览只校验。
- 迁移保留 ID/布局，条件移动原标识为 .harborbackup-repository-v1.json，随后创建并核验新标识，再保存账户。
- 请求响应丢失或本地保存失败可重试；目录/ID 不一致、未知格式、迁移备份碰撞、未完成事务阻止升级。
- 不移动/删除业务文件或旧 backups 内容，不修改共同基线和恢复事务格式。多设备需结束旧操作并更新客户端。

## 验证
- 新建 v2 标识与账户落盘。
- 旧标识和账户升级，自定义布局与文件保持不变。
- 旧标识移动后丢失响应：重试恢复。
- 云端升级后本地配置保存失败：再次应用成功。
- 版本/目录/ID/备份碰撞/未完成事务异常均无云端改写。
- v2 旧 backups 字段、缺失目录、非法管理区域被拒绝。
- 同一升级重复应用不改写标识。
- 回归共 327 项：Core 178、Windows 66、Desktop 39、Integration 44；构建零警告、零错误。
- Desktop 的 LanguageSwitchPreservesDraftAndFilesAndRelayoutsSettings 首次在 popup.Visible 断言失败，单独重跑通过，未修改该测试或生产菜单逻辑；真实桌面焦点对弹出菜单的影响仍是测试限制。
- 新增仓库格式/迁移用例 11 项全部通过。TRX：artifacts/test-results/current（17:24 与 17:31）；失败 UI 用例重跑通过见该次工具日志。
- git diff --check 通过；发布目录私有运行时执行 CLI --help 成功，包含 cloud-upgrade。

## 发布
- artifacts/publish/HarborBackup-0.9.0-preview.6-20261104-173254/installers/HarborBackup-0.9.0-preview.6-Setup-x64.exe。
- 安装包 53,078,152 bytes，payload 182,136,517 bytes；完整私有运行时、未裁剪、未签名。
- SHA-256：5ED52941FB0843DE79AE5618DE93894957EB65264EF2BFEEB64B4A1A547726E2。
- 所有自动测试使用假传输和隔离目录；未升级生产仓库或覆盖安装。真实 Graph 升级及多台实体设备联动尚未验收。
