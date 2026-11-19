# 公开发布检查表

本表用于首次公开仓库及后续发行，不替代 [开发验证流程](verification.md)。当前产品代码较小，精简优先处理入口、隐私和产物边界；历史审计资料保留。

## 本轮已整理

- 项目采用根目录 [MIT LICENSE](../../LICENSE)，第三方声明继续保留在 packaging/licenses。后续 Publish/Build-Installer 会检查发行物中的项目许可证与源码一致。
- 拆分中英文 README，补齐从零开发、隐私、安全报告和支持入口；移除已不适用的 runtime 说明、空 Application 占位和三个重复的工程跳转 README。
- 准备 Windows 合成测试 CI、固定提交的 Actions、Dependabot、Issue 与 PR 模板；未连接远端，未声称 GitHub 托管 CI 已通过。
- 将当前 10 份早期验证记录中 13 处 TRX 用户/主机前缀改为通配表达，保留目录、时间和指标。通配表达用于公开说明，不表示证据文件已重命名。
- 按现有保留脚本保留一批及最近成功结果，清理旧安装/测试批次。依赖、工具、诊断及最新 0.9.17 安装包保留；state、保护副本和未解决材料不作为普通缓存清理。

## 决定公开哪段历史

当前跟踪内容已去除上述本机标识，但旧提交仍保留它们。历史及本地残留 blob 扫描未发现令牌、私钥或安装产物；这不是对所有秘密格式的绝对保证。公开 OneDrive Client ID 是应用标识，第三方许可证中的邮箱、合成测试值不等同于凭据。

维护者已选择在当前仓库截断历史，只保留脱敏内容的一个新根提交；本地不保留旧 Git 数据库、旧标签或内部引用的备份。原有源码和文档中的历史验证记录保留，但其中的旧提交号不再能从当前仓库检出。

开发工具可能产生内部 `refs/codex/*`，它们不属于产品发行历史。上传时明确选择产品分支和后续实际发行的标签；避免 `--mirror`。不要直接压缩整个工作目录或 `.git`：被忽略的缓存、数据和本地材料不因 .gitignore 而从普通 ZIP 中消失。

公开仓库的首次安装包应从新的公开源码重新构建并使用相应发行标签，不给新根提交补造旧二进制的历史来源。原 0.9.17 安装包及其提交号属于截断前的发行记录；旧标签不带入当前仓库。

## GitHub 上仍需操作

1. 确定仓库所有者、名称、描述及主题，核对许可证版权归属。当前文档没有臆造仓库地址。
2. 启用 Private vulnerability reporting，再确认 SECURITY 中的私密入口可用。启用依赖图、Dependabot 告警及可用的秘密扫描/推送保护。
3. 推送选定内容，在托管 Windows runner 上实际跑一轮 CI；成功后再把检查设为分支保护要求。当前只准备了工作流。
4. 为默认分支设置审查/必需检查规则；依赖更新不自动合并。按实际维护能力决定是否增加 CODEOWNERS、行为准则或维护期限，避免虚构维护人或响应承诺。
5. 准备使用合成项目的主窗口/恢复流程截图，并记录图标/第三方素材的来源与许可。不要从 artifacts 直接复制可能包含真实路径的旧截图。
6. 通过 Releases 分发安装包、SHA-256、许可证及版本说明；将签名状态、真实账户/实体双机/安装验收范围写清。二进制不放入源码 Git 历史。

## 仍值得推进

- Windows 安装包签名和可信更新机制。
- 首次连接、首次备份、独立恢复的短演示与外部用户测试。
- 首轮 GitHub CI 的实际 Windows 环境适配及复现记录。
- 真实账户、多设备及长期故障环境的专用验收；当前合成回归不能替代这些结果。
- 若维护者希望自动发布，再设计人工批准、准确源码身份、校验和/构建证明与最小权限的发布工作流；当前 CI 不自动发布。

GitHub 功能与许可说明以官方文档为准：[仓库许可证](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)、[私密漏洞报告](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository)、[移除敏感历史](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository)。
