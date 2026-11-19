# HarborBackup 工程约束

- 用户确认 0.5 使用全新配置，不需要旧 PowerShell/0.4 数据兼容与迁移。当前规范见 docs/architecture/storage-layout.md。
- 账户直连分支新增 OneDrive 适配工程，详见 docs/architecture/onedrive-account-transport.md。产品为 Core、Windows、OneDrive、Desktop、Cli 五个工程。Core 不引用平台/宿主；本地文件业务副作用集中在 Windows，Graph 与受当前 Windows 用户保护的授权缓存位于 OneDrive，桌面和 CLI 共用用例。
- 安装根目录包含 app/data/docs/licenses。app 为 SDK 标准自包含 WinForms 发布，Desktop/CLI 共用所需运行时与依赖，PublishTrimmed=false；不再单独复制完整 Desktop Runtime。升级清理旧 runtime，保留 data。应用不写 app。本机数据放 data，共享元信息、历史版本和冲突副本通过授权账户管理，见 docs/architecture/onedrive-account-transport.md。
- Backup 不写源，Pull 不写目标，不传播删除。覆盖前保护不同内容，核验成功后才提交共同基线。
- 账户直连是唯一执行链，禁止回退本地同步目录模式；共同基线用于三方比较，双方修改保护并等待明确选择，0.9 直接备份到每项目指定的普通云端目录；管理目录仅保存元数据、历史和冲突材料。比较不下载云端正文；普通历史只留上一份，未解决冲突和恢复材料除外。旧数字版本内容保留，旧版本文档仅供历史参考。
- state 是恢复依据，cache 是可重建优化。清理不得触及状态、归档或未解决冲突。恢复校验设备、任务、映射、路径和格式版本。
- 仅在本仓库和独立临时目录开发验证。不得顺便修改父目录脚本、真实计划任务或生产数据。
- 遵循 CONTRIBUTING.md 与 docs/development/verification.md。不得把开发测试通过宣称为安装版验收通过；如有未测项如实说明。
- 不提交本机配置、身份、真实业务路径、日志、密钥、构建产物。
- 用户明确指示优先于本文件；日常代码、文档和隔离测试不要求额外确认。
