# 自包含安装包完整开发方案

## 目标与范围

默认交付 HarborBackup-<版本>-Setup-x64.exe，双击安装、开始菜单启动、Windows 应用列表卸载。保留 .NET 10 与 WinForms；自包含目录发布，PublishTrimmed=false、PublishSingleFile=false，不采用 Native AOT。用户不运行构建脚本，不另行安装 .NET。程序和用户数据分离，不改动生产计划任务。

第一版提供 x64。ARM64、自动更新服务、MSIX、MSI、配置迁移向导和安装中断后的事务式版本回滚不属于本增量；继续使用已有显式路径和迁移检查接入旧配置。系统版本能通过安装器检查不等于所有版本已经实机验收。

## 工程边界

- Core 的备份决策与事务协议保持不变。
- Windows/Integration/ApplicationPaths 统一已安装与便携默认路径；显式参数保持优先，不搜索父目录。
- Windows/Integration/InstallationGuard 与 Inno 共用安装协调协议；CLI 与 Tray 持有进程存活标记。
- packaging/windows 保存固定工具和运行时版本、安装脚本、安装模式标记。
- Publish.ps1 固定 RID/运行时，独立发布两个入口，校验同名依赖一致后合并，剔除符号和开发资料，生成逐文件 SHA-256 清单。
- Build-Installer.ps1 核验全部清单和额外文件，调用固定 Inno 编译器，输出体积/摘要/签名状态报告。构建脚本仅供开发者使用。

## 安装、升级与卸载

安装采用当前用户权限，固定默认目录 LocalAppData/Programs/HarborBackup；用户数据为 LocalAppData/HarborBackup/workspace。安装器不写配置、状态或业务数据。开始菜单默认创建，桌面快捷方式可选，登录启动由应用设置管理。初次无配置不运行备份。

升级、卸载先获取短时 Startup 互斥；发现 Running 标记时提示从托盘安全退出，CLI 也必须结束。成功后持有 Maintenance 标记至文件操作完成；新进程在同一 Startup 互斥下检查标记，避免检查与替换之间的新启动竞争。标记使用 Global 命名空间和大写 Windows 用户名，同一账户不同会话也受保护；不同域同名用户会保守互相阻止，不绕过访问拒绝。仅持有句柄，不留下磁盘锁文件，进程异常退出由内核释放。互斥协议不是跨设备分布式锁，也不代替旧备份锁。

使用 CloseApplications=no、RestartApplications=no，不强杀应用，不排队重启替换文件。同版本可重装，较旧版本拒绝覆盖。安装期间文件占用/权限/空间错误交给安装器报告，保留数据；不声称提供断电级程序回滚。旧开发版无协议，发布说明要求先退出。

卸载仅删除安装器拥有的文件；不递归清理程序目录中的额外用户文件，不删除工作区。仅当 HKCU Run 的命令指向本安装 EXE 时删除该项，保留其他位置实例的注册。私有运行时随程序卸载，不卸载系统共享运行时。

## 轻量化与供应链

不手删框架 DLL、不强制启用裁剪；两个入口共享一份一致的运行时文件，LZMA2 压缩安装包。用户包不含源码、测试夹具、PDB 和完整开发文档。初轮保留框架语言资源，测量后再决定语言精简，避免未经验证的 UI 回退。

发布版本、RID、运行时与 Inno 版本由 release.json 明确固定。获取工具只从官方版本地址并验证数字签名；工具不随应用分发。NuGet 依赖锁定。私有运行时必须随安全补丁重新构建、测试、发布。正式公开发行应给应用和安装器签名；证书由发行者提供，不创建伪可信证书或关闭系统检查。开发包明确记录未签名。

## 实施顺序与验收

1. 路径管理和进程安装协调：单元测试验证默认路径、参数覆盖、无隐式写入、安装时拒绝启动、最后进程退出才释放标记。
2. 自包含发行：在独立目录发布，验证 coreclr/hostfxr/WinForms 依赖、清单、两个入口启动及许可文件；保持备份兼容回归。
3. 安装器：编译正式和隔离验收变体。变体只跳过真实注册与快捷方式，保留文件安装、卸载、版本检查和互斥代码；安装到 artifacts 的唯一目录。
4. 生命周期验收：安装、同版本重装、运行中阻止升级/卸载、额外文件保留、卸载后业务数据不变；需要干净 VM 补验无系统运行时、真实快捷方式/应用列表、跨会话、降级和异常电源场景。
5. 性能测量：记录 Setup.exe 压缩字节数、发布有效载荷逻辑字节数、实际安装文件逻辑字节数（含卸载器，不称 NTFS 分配空间）；独立合成空项目配置，隐藏托盘预热后采样私有内存、工作集和 CPU，不做强制 GC。将采样时长、环境、场景和原始样本一起保存。

本轮先报告基线，不把安装大小当内存、不把空项目场景当大规模备份表现。进一步性能优化只在实测发现瓶颈后进行。可靠备份优先于最低包体积。

## 依据

- [.NET 发布模式](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
- [WinForms 裁剪限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)
- [Inno 安装权限](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)
- [Inno 应用关闭设置](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm)

实际交付与验收证据补充到 development-validation.md，未执行项保持明确。
