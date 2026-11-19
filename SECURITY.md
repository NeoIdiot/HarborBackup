# 安全报告 / Security reporting

请通过 GitHub 私密漏洞报告提交疑似漏洞，不要在公开 issue 或 PR 中提供可利用的细节。在本仓库的 **Security → Advisories → Report a vulnerability** 提交报告；该入口只有在维护者启用 Private vulnerability reporting 后才会出现。[GitHub 启用说明](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository)。

如果没有该按钮，可以使用“优化建议 / Feature request”模板，只请求启用私密报告，不要附漏洞细节，等待维护者提供私密渠道。本文件不表示该设置已经启用，也没有指定其他未确认的联络方式。

报告请包含受影响版本或提交、影响范围、必要的前置条件，以及使用合成数据的最小复现。不要提交访问/刷新令牌、授权缓存、预授权 URL、真实账户标识、业务路径或原始备份文件。若意外公开凭据，请先通过相应账户或服务撤销凭据，并清理公开内容。

安全修复以当前发行版和主开发分支为重点；旧版本可能需要升级。项目没有承诺响应或修复时限。真实 OneDrive 授权、安装/升级验收的范围以对应版本验证记录为准。

Use GitHub private vulnerability reporting for suspected vulnerabilities, rather than posting exploitable details in public issues or pull requests. Open **Security → Advisories → Report a vulnerability** in this repository. A maintainer must enable Private vulnerability reporting before this button appears; see the [GitHub instructions](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository).

If the button is absent, use the feature-request form only to request private reporting, without including vulnerability details, then wait for a private channel. This document does not claim that the feature has already been enabled or provide an unconfirmed alternative contact.

Include the affected version or commit, impact, prerequisites, and a minimal reproduction using synthetic data. Exclude tokens, credential caches, preauthenticated URLs, real account identifiers, business paths, and original backup files. Revoke credentials through the relevant service if they were accidentally exposed, and remove the public material.

Security fixes focus on the current release and main development branch; older versions may require upgrading. No response or remediation deadline is promised. Real-account and installation/upgrade validation remain limited to the evidence recorded for each release.
