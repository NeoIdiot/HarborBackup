# 恢复、长期元数据与后台运行优化（2026-11-19）

本轮为源码开发与隔离验证，未发布安装包、替换正式安装或操作真实账户业务目录。五个产品工程及普通 OneDrive 目录契约保持；新增 CrashRecovery 仅为测试探针。

## 实施内容

1. 独立恢复：Desktop 和 CLI 共用 CloudRestoreService，支持当前文件、上一份归档、路径筛选、独立输出、正文 SHA-256 与下载前后版本核验。缺少可信证据不会下载或算作通过。restore-test 支持字节预算、分组和预算内轮换，并保留文件及 JSON 报告。输出拒绝活动/退休源、app/data/保护目录重叠、链接及覆盖。不会更新基线或就绪标记。
2. 元数据检查点：按稳定分桶保存 256 个目录摘要与当前文件证据，账户/云盘/仓库绑定和内容校验完整；变化桶读取原始记录。损坏检查点、过滤放开、历史版本重新引入会回退必要原始读取。只读观察不发布云端检查点，完整执行后可每增加至少 128 条证据发布一次。原始证据、历史、冲突和事务不删除。
3. 项目健康：用 CoverageComplete 区分完整操作与部分选择，显示当前映射最近完整成功、最近恢复核验范围/文件数、已知积压和首次时间。规则变化接续，目录变化隔离，24 小时提醒去重。未开始或未选择的工作不伪造精确数量。
4. 真实中断：在 Prepared、Archived、Uploaded、Metadata 边界强制结束自建子进程，新进程从持久合成云盘和本地事务恢复。上传不确定时，旧内容可保留在冲突材料中；测试核对保护内容、当前文件、基线和源文件，不把普通归档路径当成唯一合法保护位置。
5. 速率控制：可选 transfer.uploadKiBPerSecond / downloadKiBPerSecond，0 不限；同一 drive 会话所有并发内容传输共享方向预算，分块等待可取消，保持重试和协议范围。与自动备份总量/时间预算分开。
6. 冲突与说明：展示大小、修改时间、版本时间、设备和摘要；另存查看重新验证确认值与内容。恢复结果逐文件显示易读说明，完整 JSON 留在输出目录。补齐中英文资源和默认八文件并发说明。截图发现英文参数换行后，已将预算/分组与操作按钮分行，并补最小宽度断言。

## 开发验证

Windows / SDK 10.0.115 / Release，源码基点 `8c7e3d4e64769a09c35d52fd3ff5850fb6c378cc`，tree `73a2d80c0cd3794161aded51ab0dcbdb2602c00a`。工作区含本轮修改，clean=false；上述提交号不代表完整修改快照。未创建发行标签。

最终入口：`./scripts/test/Test.ps1 -Configuration Release -IsolatedProfile -NoRestore`。

| 测试工程 | 通过 | 失败 / 跳过 |
| --- | ---: | ---: |
| Core | 24 | 0 / 0 |
| Windows | 168 | 0 / 0 |
| Desktop | 141 | 0 / 0 |
| Integration | 273 | 0 / 0 |
| 合计 | **606** | **0 / 0** |

构建零警告、零错误。最终证据为 `artifacts/test-results/run-20261119-105504-954cb0924c8d4eabaee76e53d4129cee/completed.json` 及四份 TRX。该轮还通过产物清理、测试前置条件、源码身份合成检查及四个进程中断用例，后者证据为同目录 crash-recovery.json 和独立用例目录。测试与正式程序互斥；开始发现托盘运行后停止，用户退出后才继续，没有自动关闭正式程序。

专项测试映射：CloudRestoreTests（7 项）、MetadataCheckpointTests（6 项）、ProjectCoverageTests、ProjectHealthTests、ProjectHealthUiTests、BandwidthLimitTests、CloudConflictExportTests、RestoreUiTests、RestoreReportPresentationTests。涵盖预算饥饿、旧筛选失效、版本变化、损坏、取消、不覆盖、映射隔离、共享限速、分块范围及双语布局。最终 UI 专项 7 项通过并检查中英实际控件位图，证据为 `artifacts/diagnostics/optimization-ui`。CLI 的帮助、非法范围和非法分组值也实际启动检查通过，未加载真实账户执行传输。

## 百万记录基准

设置 `HARBORBACKUP_LARGE_METADATA_BENCHMARK=1`，单独运行 MetadataCheckpointTests.LongHistoryIndexScalesWithCatalogAndKeepsOnlyCurrentBodies，通过：

| 项目 | 结果 |
| --- | ---: |
| 当前文件 / 修改轮次 | 10,000 / 100 |
| 合成历史目录记录 | 1,000,000 |
| 检查点中的当前正文证据 | 10,000 |
| 序列化检查点大小 | 12,365,877 字节（约 11.8 MiB） |
| 新增一条目录记录后需重读的变化桶 | 4,160 条 |
| 目录构造、摘要、验证、序列化等累计 | 4,280 ms |
| 测量时 GC.GetTotalMemory(false) | 371,188,904 字节 |

原始输出保存在 `artifacts/diagnostics/optimization-million-records` 下该轮 TRX 的 StdOut 中。这是内存合成目录算法基准；没有枚举真实百万文件云盘或下载百万份 JSON。GC 数字是调用时的托管内存估算，不是进程峰值或托盘常驻占用。冷/热业务读取与回退另由较小的实际运行用例验证，不外推生产吞吐量。

## 边界

- 未做本轮真实 OneDrive 授权传输、实体双机、长期真实限流、实际断电或安装/升级验收。UI 位图与控件测试不等于跨显示器 DPI、真实辅助功能或完整人工交互验收；路径测试不等于恶意链接替换竞争验收。
- 检查点减少历史 JSON 正文读取，目录身份枚举仍随历史数量增长；独立恢复候选目前仍读取原始文件/保护证据，未以目录算法基准宣称恢复端到端耗时收益。
- 普通历史仍只保上一份，未解决冲突和恢复材料除外；没有新增云端证据清理或迁移器。
- 新探针不增加第三方包，依赖恢复复用锁定缓存。在线 NuGet 审计源不可达时，探针恢复命令仅对该次恢复关闭审计；本轮不声称进行了新的在线依赖漏洞审计。正式发布约束保持。
- 产物由仓库保留策略管理，记录中的本地路径以后可能清理；不提交产物、日志、身份或业务路径。
