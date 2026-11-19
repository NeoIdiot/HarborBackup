# GitHub 维护说明

`workflows/ci.yml` 在 GitHub 托管的 `windows-2025` 上运行完整 `Test.ps1 -Configuration Release -IsolatedProfile`。这是**无真实云端依赖的合成测试**：SDK 安装和 NuGet 还原仍需要网络，测试使用临时数据及合成传输，不配置用户令牌、真实 OneDrive、安装器或发布步骤。开发测试成功不代表真实授权或安装/升级验收通过。

SDK `10.0.115` 安装在 runner 临时目录，避免预装 SDK 影响 `global.json` 的 `latestPatch` 选择。更新 SDK 时同步 workflow 的安装版本和选择断言，并运行完整验证。CI 没有依赖本机 Inno 或包缓存；不缓存或上传用户数据。

工作流只有 `contents: read`，检出后不保留 Git 凭据，没有 `pull_request_target`、发布权限或自动合并。Fork PR 在无项目密钥的测试作业中运行；首次贡献的运行是否需要批准，由仓库维护者的 GitHub Actions 设置决定。上传白名单仅含合成 TRX、运行上下文、完成标记与崩溃恢复摘要，保留七天，不上传临时目录或授权材料。

第三方 Actions 固定完整提交 SHA。当前固定值来自官方发行提交：[checkout v6.0.3](https://github.com/actions/checkout/commit/df4cb1c069e1874edd31b4311f1884172cec0e10)、[setup-dotnet v5.4.0](https://github.com/actions/setup-dotnet/commit/26b0ec14cb23fa6904739307f278c14f94c95bf1)、[upload-artifact v6.0.0](https://github.com/actions/upload-artifact/commit/b7c566a772e6b6bfb58ed0dc250532a479d7789f)。依赖更新后复核完整 SHA 与官方发行的对应关系，保留版本注释；参见 [GitHub 工作流安全说明](https://docs.github.com/en/actions/reference/security/secure-use)。

`dependabot.yml` 每月检查 GitHub Actions 与 NuGet 版本更新，把 minor/patch 更新分组并限制同时打开的版本更新 PR；major 更新单独审阅。NuGet 直接匹配项目目录，也覆盖不在主 solution 中的测试辅助工程。安全告警和安全更新另由 GitHub 仓库设置控制，未在本地启用。配置不会自动合并，也不替代维护者审查包来源、锁文件、运行时包摘要和验证结果。[Dependabot 配置规范](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference)。

公开仓库前，维护者需要在 GitHub 设置中启用 Private vulnerability reporting，并确认 `Security → Advisories` 显示私密报告入口。[SECURITY.md](../SECURITY.md) 已说明该前提，没有宣称远端设置已完成。Issue 表单不配置虚构联系人或仓库 URL。
