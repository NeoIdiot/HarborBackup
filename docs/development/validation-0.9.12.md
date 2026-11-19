# 0.9.12 网络请求优化与安装包验证（2026-11-18）

本版减少项目注册、目录解析及不可变元数据创建的重复请求；清单缓存按可证明的变更范围失效，JSON 文件元数据版本检查使用只读批请求。默认八路文件处理、10 MiB 上传分块，遇 429 降低派发并发。覆盖保护、条件写入、内容核验、恢复记录及共同基线提交顺序保留。逐批真实网络测量见 [网络优化记录](network-optimization-2026-11-18.md)。

## 源码与完整回归

安装包实际构建源码为干净提交 `e9a76ea6c03834fc568f6306f7b56d522d8968a4`，tree `6994ff6b96a630c464c8c14e2ce2eb9dda8fa734`。SDK 10.0.115，Release，自包含 win-x64，运行时 10.0.14，不裁剪、不单文件、不 ReadyToRun。

升版并提交后重新运行完整测试，Core 15、Windows 130、Desktop 112、Integration 206，合计 **463 项全部通过**，构建零警告、零错误。证据为 `artifacts/test-results/run-20261118-135047-2f3ab2d0080740218d565fbb9533ac1d/completed.json` 与同目录四份 TRX，开始/结束源码身份一致、clean=true。

## 发布与安装验证

发布目录：`artifacts/publish/HarborBackup-0.9.12-20261118-135414`。manifest v4、payload/release.json 和 package-report.json 的版本及源码身份一致。发布时核验固定运行时包 SHA-512，Desktop/CLI 依赖按相同哈希合并，共 290 个 payload 文件、125,553,185 字节。

从同一 payload 构建无卸载注册、无开始菜单的隔离验证安装器，并在当前 Windows 用户环境运行 Test-Installer.ps1，通过全部检查。证据：`artifacts/install-validation/8ed4c25adb1e45ebafdf8dd588dfe973/validation.json`。

覆盖首次安装自动配置、MSAL 离线加载、CLI 与桌面共用配置、无系统 dotnet 路径启动、中文空格路径及无关工作目录、旧目录传输不可用、未授权拒绝修改业务文件、暂停/恢复、coreclr 从 app 加载、忙碌升级被拒绝、旧程序/runtime 清理、升级/卸载全部 data 哈希保留、重装读取保留数据、旧格式自动重建且原数据全字节保留、重复安装不重置当前数据、不可读数据拒绝安装且不强制重置。

最初一次沙箱执行因无法解析 Windows localappdata shell 目录而停止，未记作通过；改为当前用户环境后完成上述隔离验证。没有覆盖用户正式安装或修改真实计划任务。安装验证使用合成账户，没有在安装目录执行真实账户备份；真实 Graph 往返另见网络测量记录。

## 交付

正式安装包：`artifacts/publish/HarborBackup-0.9.12-20261118-135414/installers/HarborBackup-0.9.12-Setup-x64.exe`。

- 大小：37,224,226 字节（约 35.5 MiB）。
- SHA-256：`F7A795C72BE0F907BBB0B790D5A238A9446EAD606ECE823759EDB63ADA7D6D6E`。
- 未签名；正式包与验证包使用相同经过核验的 payload，正式包本身没有覆盖安装到用户生产目录。
- 本地发布 tag 指向上述实际构建源码；本记录作为后续文档提交，不改写构建来源。

不将这些结果解释为实体双机、长期限流、全部 DPI/辅助功能或生产数据端到端验收。产物由仓库保留策略管理，路径可能随后续成功构建清理。
