# 0.7.0 共享保护目录与本机保留验证

日期：2026-11-02。设计见 [共享保护存储](../architecture/shared-storage.md)。

## 功能与修复

- 修复设置字段依赖完整备份配置导致“已保存仍显示未设置”。存储快照独立读取；没有项目、项目配置损坏时仍显示有效路径，队列读取错误也不隐藏路径。
- 默认共享根目录为本机 OneDrive/HarborBackup，archives、conflicts、metadata 分别存放历史版本、冲突副本和元信息。共享根及各子目录均可单独设置/恢复默认。
- 历史版本和冲突副本分别设置 KeepLocal/OnlineOnly，本机偏好不会覆盖其他电脑。元信息和副本索引请求本地保留。
- 保护副本覆盖前登记到本机持久化目录；有事务、事务目录不可确认或尚未同步时不请求释放。处理失败保留错误和重试记录，目录变更不删除旧文件。
- 对旧本地默认保护目录的中断事务，恢复前临时保持原位置；完成后才使用新默认。
- 设置提供保留状态详情及“应用到已有副本”；只发现本设备的受管理副本，JSON 业务副本不会误当索引。UI 原有布局、字体和语言切换继续保留。

## 自动化验证

四个工程共 296 项通过：Core 181、Windows 89、Desktop 20、Integration 6。新增覆盖路径默认/自定义、无项目时的即时回显与重载、两种语言、策略独立性、普通文件不能冒充云文件、事务阻止释放、目录异常、失败后重启重试、旧目录保留、覆盖前持久化、旧事务恢复及长路径。

最终 Windows 套件包含真实原生 API 对普通文件和大于 260 字符路径的查询。UI 图片在 artifacts/ui-storage；检查了中文与英文的路径显示。DrawToBitmap 不是跨显示器 DPI 验收。

## 真实 OneDrive 验证

使用当前用户 OneDrive 下新建的独立合成测试目录，不使用正式备份项目。最终通过发布版 CLI 驱动：

1. 生成目标版本并修改源，产生历史副本和索引。
2. 元信息、历史副本及索引全部观察为 KeptLocal。
3. 历史副本改为 OnlineOnly，先观察 ReleaseRequested，后观察 OnDemand。
4. 改回 KeepLocal，先观察 DownloadRequested，后观察 KeptLocal。
5. 重新下载后的历史内容 SHA-256 与原始合成内容相同。

报告：artifacts/cloud-validation/a7305d458bc649f3a48552b8bdf3e183/corrected-validation.json。该报告 passed=true，testDirectoryRemoved=true；四个合成云文件及测试目录已清理，报告保留。本次是真实单机云端往返，不代表已验证两台物理电脑并发或断网恢复。

探索测试发现并修复两项模拟测试未暴露的问题：

- Windows 兼容模式对应用隐藏云占位属性。查询/设置时临时使用 RtlSetThreadPlaceholderCompatibilityMode(2)，finally 恢复线程原状态。
- 原生路径需要支持长路径，attrib 在 261 字符元信息路径上可能未应用固定且返回成功。最终改用 CfSetPinState 句柄 API，并验证同一长路径元信息能够固定。

初始探索报告明确标为未通过，不能当作最终验收。最终 API 只改变用户固定意图，不直接删除或强制脱水；单文件同步客户端状态不代表所有设备已收到。OnDemand 不代表本地磁盘分配精确为零。

## 安装与体积

最终发布：artifacts/publish/HarborBackup-0.7.0-20261102-091752。
正式包：installers/HarborBackup-0.7.0-Setup-x64.exe。
实际隔离安装报告：artifacts/install-validation/f0303d5dd73c424297483a829128bfc6/validation.json。

安装后的 CLI 产生共享 archives，持久化保留队列，普通测试目录正确报告 WaitingForOneDrive；备份、覆盖归档、恢复、元信息、私有运行时加载、运行时阻止升级、升级/卸载保留 data 及重装读取均通过。

验收期间通过软件安全退出入口暂时退出原托盘，完成后重启原安装版；正式安装未被覆盖，计划任务未修改。两份本轮中间发布载荷已清理，清单留在 artifacts/release-audit。

| 项目 | 字节 | MiB |
| --- | ---: | ---: |
| 正式安装包 | 52726010 | 50.28 |
| 发布载荷 | 180595209 | 172.23 |
| 实际安装后程序，含卸载文件、不含测试 data | 185314528 | 176.73 |

MiB 为 1024 × 1024 字节，按文件长度之和统计，不是 NTFS 分配空间。私有运行时保持完整，不裁剪。安装版启动约四秒工作集 62279680 字节，仅单次启动采样，不作为长期空闲或峰值指标。

SHA-256：8AD3CB2983656E37C9B076E7E0B2EDB3501488680E1E6C2EB1D7C7A2A0E67AC8。
