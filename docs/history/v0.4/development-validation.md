# 开发验证记录

## 2026-10-28：0.4.4 设置页切换性能与对称淡边缘（当前）

复现用户报告的设置页延迟：首次进入总耗时 4202 ms，其中同步阻塞 4102 ms；三次已有草稿的切换约 3449、3571、3488 ms。测量位于 `artifacts/ui-navigation-before/navigation-performance.json`，使用独立合成配置、真实 WinForms 消息循环，说明重复切换的卡顿并非配置文件读取造成。

输入框外围 AutoSize 的原生尺寸递归测量与页面 VisibleChanged 引发的重复布局是主要原因。关闭外围 AutoSize、缓存字体/DPI 相关高度、稳定子控件锚点后，重复切换降至 647～711 ms；合并切页期间的容器布局后，最终重复切换为 222、271、227 ms，首次总耗时 412 ms。最终结果在 `artifacts/ui-navigation-final/navigation-performance.json`，不能代表任意机器或云端磁盘延迟。

- 17 项桌面/语言专项全部通过，包括新增首次/重复导航耗时回归，以及上下左右边线和淡外缘对应像素颜色相等的检查；旧的滚动、焦点、草稿、双语、配置保存和托盘生命周期检查继续通过。
- 卡片、输入框与普通按钮的描边在可绘制范围内对称放置；输入边线适当减淡，四周增加同样的约一物理像素淡外缘，没有方向偏移。未重新开启托盘系统投影。
- 已检查中英文设置顶部/底部与最小窗口渲染。图像和测试记录分别位于 `artifacts/ui-navigation-final`、`artifacts/test-results/ui-navigation-final`；实际 Device DPI 为 96，图像仍为控件 DrawToBitmap，不宣称真实桌面、高 DPI、跨屏或无障碍验收完成。
- Release 测试构建、自包含发布通过，未新增依赖。此次未改动备份执行器，没有重复运行全套文件事务测试。

已安全退出旧版并升级安装，安装器退出码 0，版本登记 0.4.4，282 个发布文件的安装后 SHA-256 全部匹配。新版主窗口已打开，文件版本 0.4.4.0、标题 `HarborBackup / 概览`、窗口句柄非零。保留现有配置，没有修改真实备份数据、登录启动、旧计划任务或安全策略。

安装包：`artifacts/publish/HarborBackup-0.4.4-20261028-193833-97169a2b/installers/HarborBackup-0.4.4-Setup-x64.exe`，36,744,023 字节；SHA-256 `D32DF70D5B81C6204333DE46B3CDA904B7E9A3AA699288E1BBA8ACDCE426E841`。发布内容 123,641,357 字节，实际安装含卸载器 128,556,464 字节，安装和启动报告保存在该发布目录。

## 2026-10-28：0.4.3 输入框、边线层次与根目录同行（基线）

设置的语言、数值、根目录以及运行记录的筛选、结果和日期控件使用 4 像素小圆角输入框；外层卡片为 6 像素圆角，边线与输入框采用两档对比度，避免大框和小框边界过浅。原生文本、下拉、数字和日历控件仍负责输入与键盘交互，外围仅做轻量绘制。根目录标题、只读路径和选择按钮同排。托盘菜单关闭系统 DropShadow，不另外绘制阴影。

- Release 测试构建、自包含发布通过，没有新增包依赖。15 项桌面/语言专项全部通过；本轮未改动备份执行器，没有重复运行全套事务测试。
- 既有中英文往返、配置保存、托盘生命周期和定时刷新稳定性检查通过；新增根目录路径与标题的纵向中心对齐、托盘禁用投影断言。常规与最小窗口均检查设置控件边界和草稿保留。
- 检查 `artifacts/ui-input-frames` 的中英文设置与运行记录渲染，修正原生下拉框首选高度与实际高度不同导致的残留内边线。测试报告位于 `artifacts/test-results/ui-input-frames`，当前实际 Device DPI 为 96。
- 图像为 WinForms 控件 DrawToBitmap 渲染；不将其表述为真实桌面投影、高 DPI、跨屏或屏幕阅读器已验收。

已通过应用安全退出入口关闭旧版，升级安装退出码 0，卸载登记版本 0.4.3。282 个发布文件安装后逐一通过 SHA-256 校验；安装版主窗口正常打开，文件版本 0.4.3.0、标题 `HarborBackup / 概览`、窗口句柄非零。

安装包：`artifacts/publish/HarborBackup-0.4.3-20261028-192110-dc64d278/installers/HarborBackup-0.4.3-Setup-x64.exe`，36,755,336 字节；SHA-256 `8A9219BAAD68A1B9C546AC9211014506901DC7BEEC592CB2BEF39603DDCA948E`。发布内容 123,638,797 字节，实际安装含卸载器 128,489,919 字节。安装和启动报告位于该发布目录。未修改真实配置、登录启动、旧计划任务或系统安全策略。

## 2026-10-28：0.4.2 托盘菜单、底栏操作与刷新稳定性（基线）

托盘菜单改为圆角白底，移除灰色图标边栏，保留暂停勾选区域。设置的保存与重新加载由主窗口底栏承载，位于窗口右下角，与左侧状态同排；动作与草稿仍由 SettingsPage 管理。

定位到原来每五秒配置重读先推送 loading=true，再推送 false；界面按 Busy 显示/隐藏进度区，自动高度底栏反复变化，使设置区域跳动。另有每秒无条件通知及相同项目行重复绑定。修复保留内部读取/写入并发保护，只在读取完成后通知，进度区仅对应实际可取消操作；相同项目行复用，未变化文字不重复赋值。详见 [语言与帮助设计](ui-language-and-help.md)。

- Release 构建 0 警告、0 错误；完整回归 261 项通过：Core 176、Windows 79、Compatibility 6。
- 设置窗口回归在改动未保存数值后连续执行 10 次调度滴答，再等待真实定时器 5.5 秒。断言设置区域没有尺寸变化、保存按钮不反复启停、滚动位置和输入焦点保留、值仍为 47、草稿仍未保存、相同项目集合不重新绑定。
- 新增控制器回归确认后台读取不发布短暂忙碌状态，同时仍能发现外部修改的项目目标与损坏 JSON，避免以停用刷新掩盖问题。
- 检查中英文常规/最小窗口、设置页顶部/底部、菜单圆角区域及勾选栏保留。控件渲染位于 `artifacts/ui-tray-stability`，记录的实际 Device DPI 为 96；不宣称完成高 DPI、跨屏或系统高对比度的实际桌面验收。
- 测试报告位于 `artifacts/test-results/ui-tray-stability-full`。渲染来自真实 WinForms 控件 DrawToBitmap，原生桌面自动化限制仍沿用此前记录。

本轮没有新增 UI 包或改变文件事务规则。安装结果：通过应用安全退出入口关闭旧版后升级成功，退出码 0，卸载登记版本 0.4.2。282 个安装文件逐一通过 SHA-256 校验，安装版 CLI 合成备份、归档、取回、排除、核查、暂停/恢复和旧状态读取验证通过。已打开安装版主窗口，文件版本 0.4.2.0、标题 `HarborBackup / 概览`、窗口句柄非零。

安装包为 `artifacts/publish/HarborBackup-0.4.2-20261028-170914-37a7610e/installers/HarborBackup-0.4.2-Setup-x64.exe`，36,749,936 字节；SHA-256 `F98F12B14FBABE0F12899E18FA9846996ABD309E407F771E07937E2C3956B710`。发布内容 123,637,261 字节，实际安装含卸载器 128,424,398 字节，安装与启动报告保存在该发布目录。保留现有用户配置，没有修改登录启动、旧计划任务或系统安全策略。

## 2026-10-28：0.4.1 设置卡片紧凑布局（基线）

根据实际界面反馈，消除设置单行卡片底部的多余空白。设置行右侧不再套用嵌套换行 FlowLayoutPanel，改为按控件首选宽度测量的单行表格；登录启动状态与启用/停用按钮同排，OneDrive 路径去掉内嵌矩形边框，沿用卡片统一边框。无消息时隐藏底部反馈行，主窗口状态区按可见内容收缩。帮助改为设置滚动内容最后的独立按钮，中文“帮助”、英文“Help”。

- Release 测试构建与自包含发布通过。桌面/语言专项 14 项通过；随后收缩底部状态区并移除旧的混合语言资源键后，再次通过 14 项专项。未改动备份执行器，本轮没有重复运行全套文件事务测试。
- 扩展既有双语布局回归：常规/最小窗口检查卡片高度上限，防止再次预留已删除说明的空间；检查行内控件边界、帮助按钮语言以及帮助在所有设置项之后。原有语言切换保留草稿、偏好持久化与不改写备份配置检查仍通过。
- 开始阶段在 168 DPI 渲染并检查卡片布局；最终 96 DPI 渲染检查中英文顶部/底部、最小窗口和保存反馈。后续默认环境及用户会话补验实际返回 96 DPI，因此不把目录名中的 168 视为最终高 DPI 验收证据。渲染为实际控件 DrawToBitmap，不是安装版桌面截图，跨屏 DPI 与无障碍仍未补验。
- 图像在 `artifacts/ui-settings-compact-*`，最终测试记录在 `artifacts/test-results/ui-settings-compact-final`、`ui-settings-compact-96`；以 `render-context.txt` 中记录的 Device DPI 为准。
- 通过应用安全退出入口关闭旧版，升级安装成功，版本登记 0.4.1；282 个安装文件 SHA-256 全部匹配。安装版主窗口正常打开，版本 0.4.1.0，标题 `HarborBackup / 概览`、窗口句柄非零。
- 安装包位于 `artifacts/publish/HarborBackup-0.4.1-20261028-164406-05e86c0c/installers/HarborBackup-0.4.1-Setup-x64.exe`，36,753,025 字节；SHA-256 `FE28146499CF721D984EE400152BCF9F0C018A8996BC0D0BB34EE523A43177BC`。发布内容 123,632,919 字节，实际安装含卸载器 128,356,071 字节。安装与启动报告保存在该发布目录。

保留自包含、不裁剪的 WinForms 发行方式，未增加包依赖。安装未导入或改写真实备份配置，未修改登录启动、计划任务和安全策略。

## 2026-10-28：0.4.0 双语、集中帮助与布局修复（基线）

完成中英文即时切换、本机语言持久化、设置帮助窗口和设置行内容自适应布局，移除设置小字说明及重复副标题。说明见 [语言与帮助设计](ui-language-and-help.md)。保留 WinForms、自包含运行时和不裁剪发布方式，无新增包依赖。

- Release 构建 0 警告、0 错误；完整回归 260 项通过：Core 176、Windows 78、Compatibility 6。之后将项目编辑器的长目标路径改为可复制的只读输入框，补跑 96 DPI 桌面/语言专项 14 项及最终 168 DPI 双语布局专项 1 项，均通过。
- 新增资源键与格式参数一致性、旧偏好兼容、语言选择持久化测试。同一窗口中的双语往返测试保留数值 47 与未保存标记，验证备份配置、系统配置内容未被语言切换改写。
- 在 96 DPI 和 168 DPI 下检查双语页面、设置顶部/底部、最小窗口、帮助、项目编辑和预览的实际控件渲染。修复固定顶部高度造成的按钮裁切、编辑器长路径的行内裁切。渲染位于 `artifacts/ui-language-96` 和 `artifacts/ui-language-dpi`，测试记录位于 `artifacts/test-results/ui-language-*`。
- 本轮为 DrawToBitmap 控件渲染验收，不是实际桌面抓屏。上一轮记录的原生桌面自动化服务不可用限制仍适用；跨屏动态 DPI、屏幕阅读器和系统高对比度未补验。
- 发布包：`artifacts/publish/HarborBackup-0.4.0-20261028-161506-194d0e57/installers/HarborBackup-0.4.0-Setup-x64.exe`，SHA-256 `D55FB0B84381F9A33735131574B1470C11753657126E57EF4497502C44C98F6F`。
- 已通过应用自身安全退出命令关闭 0.3.0，升级安装退出码 0，卸载登记版本 0.4.0。安装后 282 个发布文件 SHA-256 全部匹配，包含 `en-US/HarborBackup.Tray.resources.dll`；安装版 CLI 合成写入、归档、取回、排除、核查、暂停/恢复和旧状态读取验证通过。
- 安装包 36,747,336 字节（35.04 MiB），发布内容 123,632,862 字节（117.91 MiB），实际安装含卸载器 128,292,029 字节（122.35 MiB）。相对 0.3.0 安装包增加约 10.64 KiB；本轮未重新测量托盘内存，下文基线测量仍仅代表 0.3.0。
- 实际安装版用独立英文偏好启动并通过单实例入口激活，标题为 `HarborBackup / Overview`，文件版本 0.4.0.0，窗口句柄非零，未创建测试备份配置。安全关闭该实例后打开正常安装版，标题为 `HarborBackup / 概览`、窗口句柄非零。报告为发布目录的 `installed-verification.json` 与 `window-validation.json`。

测试只使用合成数据。升级保留用户数据；本轮没有导入生产配置、修改真实备份或更改登录启动和旧计划任务。包仍未签名，没有调整系统安全策略。

## 2026-10-28：0.3.0 界面重设计（基线）

根据用户反馈重新查阅 PowerToys、Files、Kopia 的官方资料并核对前两者的界面截图，完成原生 WinForms 导航、首次使用页、状态卡片、设置分组、项目编辑、列表空状态与品牌图标。设计与参考链接见 [界面重设计](ui-redesign.md)。本次未新增 UI 框架或第三方包，也未修改文件事务规则。

- Release 构建通过，0 警告、0 错误。最终完整回归为 257 项：Core 176、Windows 75、Compatibility 6；另一次 96 DPI 桌面专项回归 11 项通过。
- 新增缺失配置与损坏配置两种桌面测试，确认欢迎引导不会掩盖损坏 JSON，浏览设置不创建默认配置、不触发备份。
- 在默认 PerMonitorV2 和 96 DPI 下输出并检查五个页面、项目编辑、首次使用和错误状态的渲染图，包含最小窗口。高 DPI 检查中发现并修复按钮首选尺寸重复放大、设置 UserControl 未缩放导致标题裁切、表格行高未随 DPI 调整的问题。
- 图像位于忽略目录 `artifacts/ui-redesign` 与 `artifacts/ui-redesign-dpi`。这些是实际控件的 DrawToBitmap 渲染图，不是安装版桌面截图。
- 原生桌面自动化服务报 `Computer Use native pipe is unavailable ... os error 2`，重试和重新初始化后仍不可用。因此未完成真实屏幕点击、跨显示器 DPI 切换、屏幕阅读器与高对比度系统主题的人工等效验收，不将其写为通过。
- 发布自包含、不裁剪的 0.3.0 默认安装包，沿用 Desktop Runtime 10.0.14；包位于 `artifacts/publish/HarborBackup-0.3.0-20261028-152056-b0f9d15e/installers/HarborBackup-0.3.0-Setup-x64.exe`。
- 从本机既有 0.2.0 升级成功，安装器退出码 0，卸载登记版本 0.3.0。安装后的 281 个发布文件逐一通过 SHA-256 校验；安装版 CLI 的独立合成写入、归档、取回、排除、核查、暂停/恢复及旧状态读取验证通过。
- 安装包 36,736,439 字节（35.03 MiB），发布内容 123,591,339 字节（117.87 MiB），安装后含卸载器 128,186,389 字节（122.25 MiB）。与上一版安装包相比增加约 20.4 KiB。
- 包仍未签名；SHA-256 为 `3881B46A7BB5B1656A160734DFD06B5A1FBD4132063E47D434A22314E0DEB193`。本轮沿用用户之前自行调整后的运行环境，没有修改安全策略。
- 0.3.0 安装版隐藏托盘空闲复测：空合成项目、独立云根与偏好，预热 20 秒、采样 120.28 秒共 24 点、无强制 GC。私有内存均值 15.39 MiB、峰值 18.55 MiB；工作集均值 57.24 MiB、峰值 60.23 MiB；CPU 单逻辑核口径 0.572%，12 逻辑核整机归一口径 0.0476%。确认使用安装目录内的 coreclr.dll。报告为 `artifacts/measurements/a873ac9fe02a40ed803c4673591a7a6c/measurement.json`；这是隐藏托盘场景，不代表打开主窗口或备份峰值。

测量后已启动当前用户安装目录中的 0.3.0 主窗口，进程与非零窗口句柄核验通过，窗口标题为“HarborBackup · 泊岸备份”（`window-validation.json`）。未通过桌面自动化进行视觉抓屏的限制仍如上所述。

测试与升级未导入生产配置、启用登录启动或重新启用旧计划任务。结束前确认旧任务仍为 Disabled，安装版两份生产配置均未创建。用户数据与父目录备份实现不变。

## 2026-10-28：安装运行补验与旧任务停用（0.2.0 基线）

用户明确要求关闭旧 PowerShell 同步脚本，并告知已自行关闭 SAC。只读检查 VerifiedAndReputablePolicyState=0；本轮没有修改安全策略。按任务动作核对 OneDrive-Project-Backup 指向本工作区的 Backup-Projects.ps1，先导出定义，再 Disable-ScheduledTask。确认 Enabled=false、State=Disabled，旧备份进程为 0，未强杀任何进程。任务定义和安装日志位于忽略的 artifacts/migration/stop-legacy-20261028-123604。

重新运行隔离安装完整验证成功，报告为 artifacts/install-validation/9e9efeff0b064a498dd3c08ddeecc410/validation.json，applicationExecutionTested=true。覆盖安装后 CLI 启动、重装、运行标记阻止升级/卸载、维护期间拒绝 CLI、卸载保留额外文件。真实安装托盘运行期间另外尝试安装，安装器退出码为 7，没有写入目标程序，验证真实进程与安装保护配合正常。

随后使用既有 SHA-256 已核验的默认 0.2.0 安装包完成当前用户安装，退出码 0。Windows 应用卸载注册存在，开始菜单 HarborBackup.lnk 存在。安装后的 CLI --help 成功，并通过 Test-WriteCycle.ps1 -PublishedCli 的合成备份、归档、取回、排除、核查、暂停/恢复和旧状态读取兼容验证。安装后主窗口已正常打开，登录启动未登记。

使用实际安装 EXE 进行隐藏托盘测量：空合成项目、独立云根与偏好文件、无备份任务、无强制 GC；预热 20 秒后采样 120.26 秒，共 24 点。确认 coreclr.dll 来自应用安装目录。报告位于 artifacts/measurements/1cc9b4c2fbfa4d28b6ef74dd224831ac/measurement.json。测量期间执行过一次预期被拒绝的隔离安装器，用于真实运行保护验证；下列 CPU 为目标托盘进程，不是系统总 CPU。

| 指标 | 实测结果 |
| --- | --- |
| 默认安装包 | 35.01 MiB，包文件未变 |
| 实际用户安装占用 | 128,076,540 字节 / 122.14 MiB，含卸载器的逻辑文件长度之和 |
| 私有内存均值 / 最大值 | 15.41 / 18.61 MiB |
| 工作集均值 / 最大值 | 56.92 / 60.03 MiB |
| 平均 CPU，按单逻辑核计 | 0.624% |
| 平均 CPU，按 12 逻辑处理器归一化 | 0.052% |

测试托盘已通过 --shutdown 安全退出，随后打开正常主窗口供用户配置。默认项目/系统配置尚不存在，未导入父目录真实配置、未启用新自动备份、未修改源文件或目标。旧任务停用与新程序安装已经完成，但不代表生产备份切换完成。代码未改动，之前 255 项测试结果保持；本轮补充的是安装后执行与实际进程资源测量。

仍未验收：SAC 开启时的受信任签名发行、无系统运行时的干净 VM、真实备份规模下的资源峰值、不同会话/用户、降级、断电或磁盘耗尽安装异常。现有空项目基线不代表有大量文件或历史记录时的资源需求。安装包仍未签名，不能将本机成功推广为所有安全策略下都能启动。

## 历史：2026-10-28 自包含安装包首次验证

完整设计见 [安装包开发方案](installer-plan.md)。新增安装/便携路径解析、安装维护与进程生命周期互斥、当前用户 Inno 安装/卸载、固定版本的自包含发布和清单校验；托盘增加安全退出请求及隔离偏好路径。未改变备份协议或生产入口。

环境：Windows x64 10.0.26300、SDK 10.0.114、私有运行时 10.0.14、Inno Setup 7.1.0。官方编译器安装包 SHA-256 为 0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F，签名 Valid，发布者 Pyrsys B.V.；以便携方式提取到 artifacts/tools，没有安装为系统工具。

Release 构建 0 警告、0 错误；锁定恢复成功；**255 项测试通过：Core 176、Windows 73、Compatibility 6，0 失败、0 跳过**。新增路径隔离、维护拒绝启动、进程标记生命周期和安全退出通信测试。再次通过旧版双向预览各 8 个决策对照以及 CLI 写入/归档/取回/恢复和旧 files/status 读取。回归执行的是开发输出，不等同于安装后启动通过。

产物：artifacts/publish/HarborBackup-0.2.0-20261028-112000-18d0a79d/installers/HarborBackup-0.2.0-Setup-x64.exe。Setup 的 SHA-256 为 36DBDBA18ABB4923D6DE348BBD7D7D0CC703D4B824DBF6CA64009153AD5D9C5C。默认发行安装包与应用均未签名，用户已确认没有可用签名证书或其他测试电脑。

| 指标 | 实测值 | 口径 |
| --- | --- | --- |
| 压缩安装包 | 36,715,595 字节 / 35.01 MiB | 默认安装包，包含私有运行时 |
| 应用有效载荷 | 123,545,259 字节 / 117.82 MiB | app 目录逻辑文件长度之和 |
| 隔离安装后 | 128,128,746 字节 / 122.19 MiB | 含卸载器；非 NTFS 分配空间，验收变体与默认包卸载代码略有差别 |
| 托盘空闲内存 / CPU | 未测得 | 应用控制在采样前阻止加载，不填 0 或使用开发宿主数据替代 |

隔离安装验证使用相同 .iss 的 ValidationBuild，仅关闭真实应用注册、快捷方式和启动项清理。实际完成安装、同版本重装、持有 Running 标记时阻止升级/卸载、卸载保留额外用户文件及重装。报告在 artifacts/install-validation/a6acb6a83ab243debb7a0be0242de95d/validation.json。它明确记录 applicationExecutionTested=false，不能称为完整最终用户验收。

首次沙箱执行因 Windows Shell Folder 查询失败退出；在正常用户目录查询环境下安装成功。随后安装后 CLI 加载 HarborBackup.Windows.dll、托盘加载 HarborBackup.Tray.dll 被 Windows 应用控制拒绝，错误 0x800711C7，CodeIntegrity 3077/3033。托盘内存采样尝试使用空项目、独立云根和偏好路径，20 秒预热前已退出，退出码 -532462766，采样数为 0。夹具位于 artifacts/measurements/0be6dda2aeaa45a7ad725fe1f71add49。没有修改系统策略、信任设置或应用文件来绕过限制。

未完成：受信任签名后的安装版实际运行和内存/CPU基线、无系统 .NET 的干净 VM、真实开始菜单与应用列表注册、跨会话/不同用户、降级实机测试、断电/磁盘不足安装失败场景。Measure-InstalledTray.ps1 已提供可复现采样与失败记录；在允许该构建的验收环境或可信签名可用后补测。旧备份维护和生产切换限制继续保留。

## 历史：2026-10-28 主窗口与配置编辑增量

基于 feat/transactions-and-tray 工作树继续开发，未额外提交或修改旧入口。现在手动启动显示主窗口，--background/登录启动进入托盘。五个页面、项目/排除编辑、设置保存、草稿预览、统一运行状态、结构化进度、停止当前操作、本机偏好和实例激活已实现。

当前 Release 构建 0 警告、0 错误；**252 项测试全部通过，0 失败、0 跳过**：Core 176、Windows 70、Compatibility 6。新增验证包括：

- 配置往返保留未知字段、环境变量表达式和未选中文档；外部变化拒绝覆盖。
- 本机可见其他设备的未完成事务阻止映射、排除和移除操作；空项目不删除业务数据。
- 缺少配置可初始化，草稿加载/预览不写状态或目标，运行锁拒绝并发保存。
- 取消当前操作后仍可启动下一轮；日志损坏与正常记录分开展示；当前用户命名管道接受显示窗口请求。
- STA 界面测试同时覆盖隐藏和显示启动；实际操作输入框和按钮新增项目、修改周期并保存；关闭隐藏、再次激活保持同一窗口；退出释放图标。

界面测试导出概览、项目、运行记录、待处理问题、设置和项目编辑图片，位于忽略的 artifacts/ui-validation，已检查主要页面布局及固定保存反馈区。设置 HARBORBACKUP_SCREENSHOT_DIR 为隔离输出目录可重新生成。测试偏好文件也注入临时目录，不写真实 LocalAppData、登录注册或计划任务。

本次再次通过 Test-PreviewParity.ps1（双向各 8 个决策一致）和 Test-WriteCycle.ps1（备份、归档、取回、排除、只读核查、暂停/恢复以及旧 files/status 读取兼容）。

最新目录发布位于 artifacts/publish/HarborBackup-20261028-102928-669a62b1，包含主窗口版本、文档和示例。发布后 CLI.exe --help 正常，Tray.exe 的 PE subsystem 为 2（Windows GUI）。Git 差异空白、生成产物忽略、JSON 语法和 Markdown 相对链接检查通过。

配置了 PerMonitorV2、DPI 布局及高对比度配色；实际多显示器、125%/150%/200% 缩放、屏幕阅读器和真实登录启动仍未完成专用验收。截图由 WinForms 控件渲染生成，不代表所有 Windows 主题的原生标题栏外观。维护能力、生产配置预览、设备交接和切换/回退仍按下方限制保留。

## 历史：2026-10-28 事务、恢复、托盘和调度

用户要求的之前代码已提交为 **f661634**（feat: implement read-only preview and legacy compatibility tests）。本次开发分支为 feat/transactions-and-tray，下面描述本分支工作树的验证结果。

环境为 Windows x64（10.0.26300）、.NET SDK 10.0.114、NETCore/Desktop Runtime 10.0.14。global.json 基线仍为 10.0.100，补丁按 latestPatch 解析。新增 Tray 后共四个产品工程、三个测试工程，MSTest 4.0.1，依赖有 lock 文件。

Release 全解决方案构建通过，0 警告、0 错误。全套 **236 项测试通过，0 失败、0 跳过**：Core 175、Windows 55、Compatibility 6。包括九个事务提交检查点中断后各重复恢复两次、替换窗口第三版保护、状态外部修改、归档失败/半写、唯一暂存保护、同时间冲突去重、文件占用、旧 v1 恢复、未知映射拦截、事务安全取消、本机暂停、状态历史保留、日志不得写入业务根、单调时钟和托盘 STA 消息循环。

托盘集成测试实际创建 NotifyIcon，确认初始没有 Form；备份完成、打开/关闭状态窗口后消息循环继续；正常退出隐藏图标。没有注册真实登录启动、切换任务、注销或终止用户会话。单任务协调器测试验证忙碌/暂停自动触发不排队和停止时等待安全边界；运行锁覆盖兼容 Mutex 竞争。真实多进程长期运行和性能仍需专用验收。

独立端到端脚本通过：

- Test-PreviewParity.ps1：与冻结旧代码对照，Backup/Pull 各 8 个文件决策相同；目录、内容和 UTC 写入时间不变。
- Test-WriteCycle.ps1：CLI 首次备份、普通更新归档、目标较新待取回、指定项目 Pull、排除、只读完整核查、暂停/恢复、事务恢复；旧 PowerShell New-BackupSyncContext / Read-BackupState 成功读取新写出的 files v1 / status v2。

目录发布脚本已运行通过，产物位于 artifacts/publish/HarborBackup-20261028-095348-6e29eb09。发布后的 CLI.exe --help 正常；Tray.exe 的 PE subsystem 实测为 2（Windows GUI），不会自动分配控制台。该次目录发布为 708,901 字节，包含 PDB、文档和示例，不包含 .NET Desktop Runtime；不能当作自包含包体积或内存测量。Git 差异空白、仓库 Markdown 相对链接和 JSON 语法检查通过。

复现命令：

```powershell
. .\scripts\development\Use-DotNet.ps1
& $HarborDotNet restore HarborBackup.slnx --configfile NuGet.Config --locked-mode
& $HarborDotNet build HarborBackup.slnx -c Release --no-restore
& $HarborDotNet test HarborBackup.slnx -c Release --no-restore --no-build --logger trx --results-directory artifacts/test-results
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\development\Test-PreviewParity.ps1 -LegacyRoot ..
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\development\Test-WriteCycle.ps1 -LegacyRoot ..
```

NuGet 审计曾因沙箱网络不可达报告 NU1900；允许访问官方源后重新 restore 成功，未关闭漏洞审计。测试报告在忽略的 artifacts/test-results。所有写入测试使用唯一临时目录或 artifacts/validation，清理验证目录边界。父目录仅用作只读兼容源码参考，不读真实业务文件、不改配置/状态/归档或计划任务。

未完成：归档/日志清理、OneDrive 空间释放与重试；真实磁盘耗尽、强制杀进程/关机/断电；仅联机文件与路径竞争、跨设备交接；登录启动实际登记；真实配置预览、迁移/回退及内存/性能测量。注入 IOException 不能替代真实设备故障验收。写入 metrics 当前描述扫描及决策重检，不包含所有事务内部哈希，不能当作总 I/O 性能数据。v2 未完成事务不能由旧程序接管，见 [事务 ADR](decisions/0004-transaction-v2.md)。

## 历史：首个只读增量

日期：2026-10-27。基线提交：142ed9d；开发分支：feat/read-only-preview。此记录只描述 Core、Windows 读取适配器和 CLI 预览，不宣称完成写入迁移。

## 环境与实现

- Windows x64，.NET SDK 10.0.100，Microsoft.NETCore.App / Microsoft.WindowsDesktop.App 10.0.0。
- 三个产品工程：Core、Windows、CLI；三个 MSTest 4.0.1 测试工程；依赖通过 packages.lock.json 固定。
- 旧源码摘要来自父目录 11 个脚本；合成决策向量 162 个，身份向量和 files/status/transaction/archive 样例均不包含真实设备或业务数据。
- 支持配置验证、排除规则、路径边界、云 reparse tag 判别、只读扫描、SHA-256、元数据缓存、旧 v1 基线读取、双向预览、JSON/文本结果、兼容运行互斥。
- 拟复制或保护内容前重新观察两端；任何未完成本机事务导致保守阻止预览，既不修复也不删除。

## 已执行检查

最终 Release 解决方案构建通过，0 警告、0 错误；CLI --help 通过。对照脚本在隔离目录中完成 Backup、Pull 各 8 个文件决策比较，文件内容、目录和 UTC 写入时间均未改变。

最新代码在同一次 Release 测试运行中通过 **205 项测试，0 失败、0 跳过**：Core 172、Windows 27、Compatibility 6。Core 包含 162 个旧决策向量及用例/不变量测试；Windows 覆盖配置边界、排除、缓存、文件占用、读取中消失的文件不得被判为正常缺失、只读快照、待恢复事务及互斥；Compatibility 覆盖身份、BOM、未知版本、基线及路径损坏。

实际命令为 `dotnet build HarborBackup.slnx --no-restore --configuration Release` 和 `dotnet test HarborBackup.slnx --configuration Release --no-restore --no-build --logger trx --results-directory artifacts/test-results`。三个 TRX 报告在忽略的 artifacts/test-results 下，不提交含本机信息的报告。

## 已观察到的环境问题

早期 Debug 及部分 Release 测试进程报告 `0x800711C7`（Application Control policy blocked this file），Code Integrity/Operational 中可观察到 3077 事件；沙箱外执行也曾出现。阻止发生在程序集加载，不能据此判断业务断言失败，也不能当作测试通过。最终当前 Release 产物已完成一次全套通过，无法仅凭该结果确定此前拦截的消失原因或保证其他产物不会再触发。

没有关闭应用控制、添加策略例外或修改系统签名策略。若环境再次拦截，记录实际失败并在允许本地开发程序集的环境验证，不能绕过策略或省略测试。诊断含义参考 [微软 App Control 排查文档](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/operations/appcontrol-debugging-and-troubleshooting)。

## 重现命令

```powershell
.\scripts\development\Test.ps1 -Configuration Release
# 沙箱无法读取用户 NuGet 配置时，仅隔离当前进程配置：
.\scripts\development\Test.ps1 -NoRestore -IsolatedProfile -Configuration Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\development\Test-PreviewParity.ps1 -LegacyRoot ..
```

## 当时尚未验收（现状以上方 2026-10-28 记录为准）

- 专用环境的 OneDrive 仅联机内容、真实链接/联接竞争和多设备交接。
- 真实生产配置预览、运行内存/体积/性能报告。
- 状态写入、归档与复制、事务恢复及故障注入、清理与空间释放。
- 托盘、定时调度、登录启动、发布迁移和回退。

当时未注册计划任务、未读取生产业务文件或写父目录配置/缓存/归档，CLI 尚无写入命令。现在已实现写入，生产切换仍未开始。
