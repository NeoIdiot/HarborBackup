# 发布体积与依赖核查（2026-11-04）

后续 SDK 自包含方案已按用户指示实现并生成 preview.7，见 [发布与安装验证](validation-0.9-preview7.md)。下文保留最初核查与实验记录。

## 当前发行包

以 0.9.0-preview.6 的 release-manifest.json、package-report.json 和实际文件为依据。MiB = 1,048,576 字节。安装占用指发行文件逻辑大小，不含用户 data、卸载程序、文件系统簇开销。

| 项目 | 字节 | MiB |
| --- | ---: | ---: |
| 安装程序 | 53,078,152 | 50.62 |
| 完整发行文件 | 182,136,517 | 173.70 |
| runtime | 179,658,519 | 171.34 |
| app（两个入口及依赖，共 14 个文件） | 2,297,117 | 2.19 |
| docs | 11,882 | 0.01 |
| licenses | 168,119 | 0.16 |

运行时占发行文件约 98.6%。发布脚本已校验同名应用依赖哈希后合并；两个入口并未分别携带一份完整运行时。安装器使用 lzma2/ultra64 与 SolidCompression=yes。发行包未包含 SDK、测试框架、PDB、源码或 NuGet 缓存。

## 依赖与冗余处理

- Microsoft.Identity.Client 4.92.0：AccountTokens 使用 MSAL 的系统浏览器交互登录和静默授权，必须保留。发布文件约 1.22 MiB。
- Microsoft.IdentityModel.Abstractions 8.14.0：MSAL 的传递依赖，发布文件 19,504 字节，保留。
- System.Security.Cryptography.ProtectedData 10.0.0：Windows 当前用户 DPAPI 保护令牌缓存，必须保留项目包引用。当前发布 app 中没有重复 DLL，由 Windows Desktop 共享框架提供实现。
- 移除 OneDrive 工程未使用的 Core 项目引用，并同步受影响的锁文件。Core 仍为其他工程必需，不能从发行包删除。
- 移除 FileDecision.RequiresFreshContent：全仓库无调用方。
- 未发现完全无引用的顶层源代码类型；这只是静态引用筛查，不等于证明所有方法/分支均可达。
- ConflictDecision / CoordinatedDecision 仍被 CloudRevisions 调用，不按旧名字视为废代码。CanAdvanceBaseline 仅在旧规则测试中使用，列为后续规则整理候选，本次不改变基线语义。
- 仓库 v1→v2 升级、旧事务拒绝处理是保护已有数据的有效逻辑，不作为无用兼容代码删除。
- CLI 保留用于恢复、诊断和自动化验证，删去仅能节约少量应用文件，不会消除托盘所需的运行时。

本次两项源代码清理主要改善依赖边界；没有据此声称安装包明显变小。

## 标准自包含发布的隔离实验

当前正式方案是 framework-dependent 应用 + 原样携带完整私有共享运行时。这会将 WindowsDesktop 中的 WPF 文件也一起打包。

本地 SDK 的 WindowsDesktop RuntimeList.xml 标记 WindowsForms / WPF profile。使用 SDK 的标准发布选项：

    dotnet publish <入口工程> -c Release -r win-x64 --self-contained true
      -p:RuntimeFrameworkVersion=10.0.14
      -p:PublishTrimmed=false -p:PublishSingleFile=false
      -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false

两个入口各发布到隔离目录，对相同相对路径逐一验证哈希一致后统计去重并集。未手工删除框架 DLL，未启用 IL 裁剪，未移除框架语言资源。实验输出位于忽略目录 artifacts/diagnostics/package-size。

| 可比范围：应用与运行时，不含文档/许可 | 字节 | MiB |
| --- | ---: | ---: |
| 当前 app + runtime | 181,955,636 | 173.53 |
| SDK 自包含发布的两个入口去重并集 | 125,127,691 | 119.33 |
| 差值 | 56,827,945 | 54.20 |

约减少 31.23%，实验并集 280 个文件，含 System.Windows.Forms.dll，不含 PresentationFramework.dll。实验 CLI 的 --help 可启动。

这是发布方式的候选优化，**尚未转换正式发布脚本，也未生成新正式安装包**。50.62 MiB 是现有安装程序体积，不能把上述未压缩降幅直接套用于安装程序压缩体积。

正式采用前须解决：
1. 现有 app/runtime 分离约定和启动器布局；标准自包含输出默认将应用与运行时放在同一目录，不能直接当作现有私有共享运行时布局。
2. Desktop/CLI 共用同一套依赖，验证文件冲突、实际加载路径以及不依赖系统运行时的启动。
3. 保持 data/config、state、凭据等数据生命周期不变；验证升级、回退和卸载。
4. 验证真实 WinForms 窗口、托盘、双语、系统浏览器登录、恢复和取消。
5. 用正式安装器同参数测量压缩体积、安装文件占用和托盘内存，再决定切换。

## 验证与范围

- Release 构建：0 警告、0 错误。
- Core 178 / Windows 66 / Desktop 39 / Integration 44：合计 327 项通过。
- 锁定还原通过；依赖版本未变更。
- 未安装实验产物，未修改生产配置、云端文件或既有安装版。
- 这些验证不代表自包含实验版完成桌面/安装验收。
