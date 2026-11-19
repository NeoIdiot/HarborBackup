# 目录规范

## 原则

采用单仓库、少量工程、清晰依赖方向。工程内部按职责归组，稳定后才根据实际复杂度拆分。文档、合成测试数据、开发工具和运行数据分开。

```text
src/
  HarborBackup.Core/
    Domain/              # 文件版本、动作、结果、不变量和纯决策
    Application/         # Backup、Pull、Preview、Verify、恢复等用例
    Ports/               # 文件观察、提交、状态、锁、时钟等小接口
  HarborBackup.Windows/
    FileSystem/          # 路径、扫描、哈希、暂存、替换、归档
    Persistence/         # JSON DTO、版本读取、原子写入、事务存储
    Integration/         # 设备身份、锁、共享宿主入口；属性维护待迁移
  HarborBackup.Cli/       # 参数、命令映射、输出、组合入口
  HarborBackup.Tray/      # ApplicationContext、调度、窗口、组合入口
tests/
  HarborBackup.Core.Tests/
  HarborBackup.Windows.Tests/
  HarborBackup.Compatibility.Tests/
  fixtures/
config/examples/
scripts/development/
scripts/deployment/
docs/decisions/
runtime/
```

四个产品及三个测试工程均已实现代码。Tray 包含 Lifecycle、Controllers、Presentation、Views/Pages、Views/Dialogs、Styling；Windows 的 Persistence/Configuration 存放共享配置草稿编辑器。拆分对应已经存在的职责，不增加空框架。

## 计划工程与依赖

| 工程 | 计划目标 | 项目引用 |
| --- | --- | --- |
| HarborBackup.Core | net10.0 | 不引用其他产品工程 |
| HarborBackup.Windows | net10.0-windows | Core |
| HarborBackup.Cli | net10.0-windows，Exe | Core、Windows |
| HarborBackup.Tray | net10.0-windows，WinExe + WinForms | Core、Windows |
| HarborBackup.Core.Tests | net10.0 | Core |
| HarborBackup.Windows.Tests | net10.0-windows + WinForms | Windows、Tray（含 STA 消息循环集成测试） |
| HarborBackup.Compatibility.Tests | net10.0-windows | 按测试边界引用 Core、Windows |

上表的项目文件和目标均已创建。最低 Windows 版本仍需用目标机器和实际 API 验证后确定。

禁止 Core 引用 Windows；禁止 CLI 引用 Tray 或反向引用。宿主在组合入口组装依赖，业务调用经过 Core 用例。

## 根目录文件

- README.md：产品说明、状态和导航。
- AGENTS.md：仓库开发约束。
- CONTRIBUTING.md：环境、工作流和验证要求。
- .editorconfig：编辑格式；.gitattributes：Git 换行约定；.gitignore：忽略产物和本机数据。
- HarborBackup.slnx、global.json、Directory.Build.props、NuGet.Config 已创建；开启 nullable、确定性编译和警告视为错误。
- 只有实际出现统一包管理需求时才增加 Directory.Packages.props，不预先堆积工具配置。

## 路径与部署

源码位置、程序安装位置、配置根和数据根不是同一个概念。新工作区不自动继承父目录的生产数据。

开发配置放在 `config/local/`；示例中的运行数据位于配置目录内的 `runtime/`（即 config/local/runtime），符合旧状态目录不得逃出配置根的规则，整个 config/local 被忽略。工作区根 runtime 仅为其他显式开发配置预留。生产迁移时显式映射旧配置、state、archived、conflicts 和 log 的真实位置。

程序内部路径相对明确的应用/配置根解析，不依赖当前工作目录；日志等旧字段保持其原有解析基准。移动程序后仍需修复外部登录启动项。

发布产物进入忽略的 `artifacts/`；不提交 bin/obj、生成的 EXE 或生产数据。原 PowerShell 脚本保留在父目录，不移动或复制为第二套活跃生产入口。
