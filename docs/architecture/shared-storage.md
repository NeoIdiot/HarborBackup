# ADR-0009：共享保护存储与本机可用性

> 历史设计：0.7 同步目录模式。当前账户直连规则以 [OneDrive 传输设计](onedrive-account-transport.md) 为准。

日期：2026-11-02。版本：0.7.0。用户要求历史版本、冲突副本及元信息默认统一到 OneDrive/HarborBackup，并能分别选择两类内容在本机保留还是仅联机。

## 路径与配置

StorageLocations 是桌面和运行时唯一的默认路径解析器。storage-settings.json 新增 sharedRoot、archiveDirectory、conflictDirectory、archiveRetention、conflictRetention。三类目录默认是 sharedRoot 下的 archives、conflicts、metadata；sharedRoot 默认是本机 OneDrive/HarborBackup。null 表示默认，保留显式覆盖。已有 protectionDirectory 继续作为旧 archives/conflicts 父目录生效。

保留策略使用 KeepLocal/OnlineOnly 枚举，默认 KeepLocal，未知值拒绝。仅联机保护目录必须在配置的 OneDrive 内，其他外部目录只能本地保留。三目录不得重叠，与源、目标、应用和本机数据也不得重叠。旧布局未完成事务是明确例外：只有旧默认保护目录及缺少 protectionRoot 或指向旧默认位置的事务，才临时继续旧目录恢复；事务清除后新默认生效。新事务记录 protectionRoot，映射改变时拒绝猜测恢复。

目录选择立即持久化，不与备份设置草稿混用。Desktop StorageSnapshot 独立于项目/运行状态加载，因此项目尚未配置、项目损坏或运行状态读取失败不会隐藏有效存储路径。目录文本在 UI 线程只读内存，异步保存完成立即回显。保留队列损坏也不丢失已解析路径。

## 文件生命周期

ArchiveStore 在覆盖前完成副本复制与哈希校验，并把副本及其索引登记到本机 state/devices/设备ID/protection-retention.json。登记失败会阻止本次继续覆盖；已保留证据不删除。元信息发布登记自己拥有的共享 JSON。副本命名仍使用项目映射、相对路径、版本哈希及设备标记。

队列只控制登记文件，不递归对共享根目录设置属性。Index 项（元信息和副本 JSON）固定 KeepLocal，内容项读取最新本机偏好。已有副本通过显式操作扫描各级安全目录，验证索引中的设备、类别和摘要，只导入本设备副本。不删除其他设备记录或改变其他设备策略。

任何本机未完成事务（包括格式损坏文件）都会暂缓处理。事务目录不可读则失败，不能视为没有事务。目录更换后的旧队列项标为 PreviousLocation，不操作旧文件。

每次备份/恢复完成后和托盘每 60 次一秒 Tick 处理，获取相同运行互斥；不积压并行任务。单批最多 64 项且处理约四秒后停止；同步原生文件调用完成后才检查时间，因此不是硬实时四秒。按上次检查时间轮转，避免尾部饥饿。失败保留错误及待重试状态；暂停自动备份不影响独立的保留请求，退出停止重试。

## OneDrive 边界

CloudFileRetention 使用 Windows Cloud Files API 查询占位、InSync、固定及内容可用状态，不通过读取文件内容探测状态。普通文件没有占位注册时标为 WaitingForOneDrive，不假装云端成功。

通过 CfSetPinState 的 PINNED/UNPINNED 表达本地保留/仅联机意图，仅联机须 provider InSync。使用长路径句柄，不启动外部命令；真实测试发现 attrib 对 261 字符路径可返回 0 却未固定，因此改用官方句柄 API。绝不调用删除或直接强制脱水接口。Request 成功只代表请求已发出，下轮再查询实际状态。查询和设置期间通过 RtlSetThreadPlaceholderCompatibilityMode 暴露真实占位属性，并在 finally 恢复线程原状态，避免 Windows 兼容隐藏导致把占位文件误认为普通文件。

OnDemand 表示内容不是完整本地可用，不能证明本地物理分配为零。文件可能仍有部分缓存，恢复会触发下载。整体备份结果仍显示云端上传未验证；单文件 provider 状态不是另一台电脑已收到的证明。

队列采用原子 JSON 和并发签名校验。它是控制与诊断状态，不取代共同基线或文件事务。用户不得把它当缓存删除。版本自动清理和云端 API 上传不在本次范围内。

参考：[微软按需文件属性](https://learn.microsoft.com/en-us/sharepoint/files-on-demand-windows)、[Cloud Files 状态](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_placeholder_state)。
原生保留接口：[CfSetPinState](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfsetpinstate)、[占位兼容模式](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-rtlsetthreadplaceholdercompatibilitymode)。
