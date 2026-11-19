# Integration

账户直连是唯一执行链，桌面和 CLI 共用 `BackupRuntime`，由它持有运行互斥并调用 `CloudBackupRuntime` 的预览、执行和恢复。

`CloudBackupSession` 保存单项目会话状态并调度文件操作；`.Observation` 负责目录、元数据、扫描和确认依据；`.Transfer` 负责保护、上传、拉取和提交；`.Previous` 只关闭并保留旧事务。文件拆分共享同一个会话，不增加服务或接口层。

`CloudMapping` 管理映射身份与恢复依据，`CloudRepository` 管理云端布局，`CloudProtectionStore` 管理历史和冲突材料。Graph 与授权缓存位于 OneDrive 工程，本目录还负责设备身份、进程互斥和登录启动。

集成测试使用合成云盘或独立测试目录，不修改真实计划任务或生产备份。当前协议见 [账户传输架构](../../../docs/architecture/onedrive-account-transport.md)。
