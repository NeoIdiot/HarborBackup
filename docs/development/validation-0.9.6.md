# 0.9.6 发布纪律、测试入口与运行诊断整改（2026-11-17）

## 变更

将此前 0.9.2–0.9.5 工作区按下载协议、备份与运行结果、桌面导航/诊断、托盘样式、发布流程/文档组织为可审查提交，不补造历史源码 tag，不推送。README 改为引用源码版本与逐版本验证记录，避免把旧版本标题当作当前版本。

正式发布要求 Git 工作区干净，manifest v4、安装 payload 的 release.json 和 package-report.json 绑定准确 commit/tree/SDK。发布前后及安装器构建前后重新核验，源码中途改变即失败。源码提交可追溯不等于安装器逐字节可复现。

完整测试在构建前和各工程开始前检查桌面进程、Running 标记及 CLI/备份运行锁。前置条件不满足时明确停止，不关闭正式程序。IsolatedProfile 不隔离生产锁，测试期间仍需避免启动应用。

运行摘要、历史/日志写入独立尝试；故障作为报告级 Diagnostics 返回。已核验传输与 CloudUploadStatus 保留，辅助存储异常返回待处理码 2，原有传输失败/取消仍分别为 1/130。基线、事务、就绪标记、保护材料继续严格处理。介质自身失效时，不能保证错误也能持久化；当前 UI/CLI 返回报告包含诊断。

## 开发验证

用户退出正式程序后，完整测试入口通过：Core 15、Windows 121、Desktop 105、Integration 190，共 431 项通过、0 失败、0 跳过；另有产物清理、测试环境探针、合成 Git 源码身份检查通过。报告目录：`artifacts/test-results/run-20261117-170817-c2d2287f29204c9385d3766d770e9405`。本轮功能测试发生在版本号由 0.9.5 调整为 0.9.6 之前。

新增用例覆盖摘要文件占用、历史路径失效、损坏摘要与历史同时失败、传输失败/取消优先级、无效 diagnostics JSON，以及中文/英文界面把辅助写入故障与已完成文件分开展示。

## 干净提交验证与发布

随后在干净提交 `8839db552dfe20fd4297670cd569c39793dc0faf` 上重跑完整入口，0.9.6 Release 构建 0 警告、0 错误；Core 15、Windows 121、Desktop 105、Integration 190，共 431 项全部通过，0 跳过。产物清理、测试前置检查和源码身份合成验证同时通过。

- 源码 tree：`706dfdeaf9186e12d7de40c687f817870365d967`。
- 本地发布 tag：`v0.9.6`，指向上述实际构建提交，没有补造历史 tag，没有推送。
- 完整测试记录：`artifacts/test-results/run-20261117-172125-0a77e3b3944f4bb7b45c3635074c02e1`；run-context.json 与 completed.json 均记录上述 commit/tree、clean=true、SDK `10.0.115`、Release 配置。
- 发布目录：`artifacts/publish/HarborBackup-0.9.6-20261117-172448`。
- 普通包：`installers/HarborBackup-0.9.6-Setup-x64.exe`，37,200,538 字节，SHA-256 `DCA4C6FFBB2122583187B8D1DAEF1A69E49611473440A013E5F7A950A875FF3D`。
- 隔离验证包：`validation-installers/HarborBackup-0.9.6-Setup-x64.exe`，37,200,344 字节，SHA-256 `99C67DA3F7FC1E684A6C01FE1F4806BD4404A091ED48616315F353107B1E0F12`。
- 五个产品程序集 FileVersion 为 `0.9.6.0`，ProductVersion 均带上述完整提交号；manifest、payload/release.json、两个 package-report.json 的源码身份一致。发布 CLI `--help` 启动通过。

实际验证了脏工作区发布在开始构建前被拒绝，以及干净提交发布、打包通过。固定运行时包摘要、发布布局、manifest 文件哈希、安装包摘要校验通过。运行时 10.0.14，自包含 win-x64、不裁剪，安装包未签名。

用户退出正式程序后，在正常用户环境执行仓库隔离目录的安装验收通过，报告：`artifacts/install-validation/ca17badeb9384eaca5fea7984fd4e273/validation.json`。20 项检查涵盖全新安装、CLI/桌面启动、自包含运行时、未授权拒绝、忙碌升级拒绝、旧 app/runtime 清理、升级/卸载数据哈希保留、重装、旧格式整体留存重建、重复安装不重置和不可读数据拒绝安装。未替换正式安装、修改真实计划任务或生产备份。

本验证记录作为构建后的独立文档提交，发布 tag 仍保持指向真正生成 payload 的源码提交。后续重建该版本应检出 `v0.9.6`。

## 剩余边界

后续逐步推进通用错误码和恢复状态机拆分；真实 OneDrive、多设备、跨屏 DPI 和性能验收仍单列，不在本次大范围重写。依赖使用固定锁文件与本地缓存，发布还原关闭在线漏洞查询，未执行在线漏洞审计。产物和报告受自动保留策略管理。
