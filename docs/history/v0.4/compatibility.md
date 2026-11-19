# PowerShell 兼容清单

## 基线来源

初始化于 2026-10-27，参考父目录的以下源码和文档：

- scripts/Backup-Conflict.ps1：纯决策。
- scripts/Backup-SyncState.ps1：设备身份、任务身份、缓存与 JSON。
- scripts/Backup-FileStore.ps1：归档与事务。
- scripts/Backup-Hash.ps1、Backup-Inventory.ps1、Backup-Paths.ps1：哈希、边界、排除与路径。
- scripts/Backup-ArchiveRetention.ps1：归档清理与保护。
- scripts/Backup-Projects.ps1：互斥、入口、日志、退出码。
- docs/sync-conflict-design-zh.md、docs/development-validation-zh.md：现有规范与限制。

已在 tests/fixtures/legacy-source-manifest.json 记录 11 个旧源码摘要，并由旧纯函数生成 162 个决策样例与身份向量。Test-PreviewParity.ps1 在独立临时目录核对新旧预览，运行前校验旧源码摘要。完整兼容尚未完成，尤其不能将结构样例视为恢复/写入兼容证明。

## 身份与配置

| 编号 | 必须兼容的行为 | 验证方法 |
| --- | --- | --- |
| COMP-01 | 名称 HarborBackup 不改变旧协议前缀与文件名 | 固定输入摘要、路径和名称断言 |
| COMP-02 | 设备 ID 为 UTF-8 SHA-256 小写十六进制；输入 `AutoBackup/v1|MachineGuid|SID|Salt`；Salt 来自进程环境变量 AUTOBACKUP_DEVICE_IDENTITY | 合成 MachineGuid/SID/空与非空 Salt 向量 |
| COMP-03 | 任务 ID 输入为解析后 Source.ToLowerInvariant() + `|` + Destination.ToLowerInvariant() | 中文、大小写、分隔符和桌面重定向样例 |
| COMP-04 | 指纹为排除目录按原顺序以 `|` 连接 + `||` + 排除文件同样连接 + `||` + Target.ToLowerInvariant() 的摘要 | 不擅自排序、改分隔符或改变大小写处理 |
| COMP-05 | 保留 bakup-config.json 拼写、source/destination 和全局/项目排除规则 | 旧合法/非法配置差异测试 |
| COMP-06 | destination 相对 OneDrive 根，拒绝别名重复及父子目标重叠 | 路径矩阵 |
| COMP-07 | 源支持明确绝对路径、环境变量或实际桌面下的单个文件夹名，拒绝含糊路径和 OneDrive 内源 | 模拟用户环境和真实临时目录 |

身份输入指规范化后的旧解析结果，不能以新解析器产生的近似字符串代替。排除目录不支持通配符；文件通配符仅在末级文件名生效；全局与项目排除取并集，保留旧去重和顺序语义。

## 文件行为

| 编号 | 行为 |
| --- | --- |
| COMP-08 | 同内容不复制；共同基线仅在满足实际验证条件时推进 |
| COMP-09 | 不同内容、源较新：Backup 保护目标旧版后写目标；Pull 返回待上传 |
| COMP-10 | 不同内容、目标较新：Pull 保护本地旧版后取回；Backup 返回待取回 |
| COMP-11 | 同时间不同内容保存双方、不覆盖；首次差异和双方修改的分类沿用共同基线逻辑 |
| COMP-12 | 缺失文件不触发反向复制或删除；Pull 源根必须预先存在 |
| COMP-13 | 普通更新写 archived；初次差异/双方修改等冲突副本写 conflicts；相同内容归档去重 |
| COMP-14 | 普通归档当前最多 1 版、年龄超过 30 天清理；保护未解决冲突和恢复引用；conflicts 不参与普通清理 |
| COMP-15 | 缓存用大小和 UTC 修改时间；每文件按 hashedAtUtc 判断七天核查；复制路径强制核验 |
| COMP-16 | 当前实现对比当前 UTC 超前五分钟的修改时间延期；缓存命中不等于本次核验 |
| COMP-17 | 识别允许的云占位 reparse point，拒绝符号链接、目录联接和未知类型；不能一刀切排除占位文件 |

Backup 发现目标较新时，是否保存本地冲突版本仍由首次差异/双方修改的分类决定，不应每轮重复归档未变的普通旧版。

## 磁盘协议与入口

| 编号 | 行为 |
| --- | --- |
| COMP-18 | 读取现有 schemaVersion=1 的 files.json 与事务；保留 sourceObservation、targetObservation、baselineSource、baselineTarget、lastCommon 等语义 |
| COMP-19 | 只写本设备状态；未知版本/损坏记录阻止相关写入；外部状态变化检查继续保留 |
| COMP-20 | 临时名称和旧事务 AutoBackup 标识保持恢复兼容，不因改名失联 |
| COMP-21 | 旧互斥为 `Local\OneDriveDesktopProjectBackup`，只覆盖本机会话；兼容期使用相同保护，不虚构跨会话/跨设备能力 |
| COMP-22 | 现有调度配置为 30 分钟、启动延迟 2 分钟；不在迁移中暗改为 10 分钟 |
| COMP-23 | 每日摘要 `YYYYMMDD-电脑名.txt`，保留独立明细；现有退出码 0 完成、1 失败、2 存在待处理/警告 |

CLI 新增 busy、参数无效等结果时，先制定退出码映射，不抢占旧含义。status.json 和旧 log/status.json 的读取兼容性需要阶段 0 样例检查，不在此假设可直接互换。

## 明确的变化管理

新功能包括托盘、control.json、本机暂停和登录启动管理。任何跨会话锁、归档所有权、清理延迟、数据结构升级、调度周期或失败策略变化都记录为独立变更。

旧实现存在缺陷时，以不变量和预期结果为准修复并记录差异；兼容不意味着复制已证实的缺陷。新工作区不使用真实状态作为测试夹具。

当前只读增量的明确差异：重复 JSON 属性按大小写不敏感拒绝；未来 hashedAtUtc 不复用缓存；任务摘要与 source/destination 映射不符时拒绝读取；预览发现任何本设备未完成事务时保守地阻止项目预览，而旧预览未执行此拦截。目录动作现在显式出现在预览中，旧文件 Plans 不含目录，因此对照时分别处理。仅支持 sync.enabled=true，旧 Robocopy 分支未迁移。普通运行和完整核查均在拟复制/归档前刷新两端观察。

2026-10-28 写入增量：files v1/status v2 新输出已经旧读取器验证；普通更新归档、初次冲突保护、明确 Pull 和本机控制已接入。v1 事务可保守恢复，新事务使用 v2（设备绑定与实际被替换版本保护），旧程序不能直接接管未完成 v2 记录。恢复不重放过期复制。见 [事务 ADR](decisions/0004-transaction-v2.md)。

尚未迁移 COMP-14 的保留清理、空间释放与重试、日志保留清理；配置保留/验证但不执行这些维护动作。freeSpacePending 只记录待释放事实，不表示已请求或已上传。copy 的并行度/立即重试参数当前不用于同步事务，后续轮次先恢复再判断。日志保持旧每日文件名，明细内容使用新 JSON 报告，旧日志文本解析器尚未验收。不能据此宣称完整替代旧程序。

主窗口增量新增配置写入：保留原 JSON 中未编辑且能够通过现有校验的字段，不扩展旧身份算法。允许 folders=[] 表示未配置/全部移除，C# 不调度空项目；不承诺旧入口接受空列表。手动启动改为打开主窗口，--background 及新登录启动命令保持托盘启动。界面偏好位于本机 LocalAppData；文件缓存、状态和事务协议不变。
