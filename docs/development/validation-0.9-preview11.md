# 0.9.0-preview.11：网络失败诊断

日期：2026-11-04。此前云端优化和多设备项目列表已提交为 7437bcc，本轮仅补充诊断及相关测试。

## 诊断内容

- Graph 请求失败记录服务域名、操作、Send/Headers 或 ResponseBody 阶段、本次尝试编号、本次请求耗时、代理策略和安全的内层异常链。
- 异常链只保留已知异常类型、HttpRequestError、SocketError/native code、HRESULT。能够区分 DNS、TLS、连接重置等类别，不从一条 SSL 错误推断网络封锁或证书问题。
- 非成功 HTTP 响应继续保留状态码、结构化错误代码、请求 ID，新增域名、尝试编号和耗时。
- MSAL 认证通过官方 IMsalHttpClientFactory 接入同一诊断处理器，复用 HttpClient，保留 SDK 重试和认证错误处理。SDK 包装后的网络异常解包为安全摘要；认证尝试编号 0 表示由 SDK 管理，不能当成没有发送请求。系统浏览器网页自身的错误不在应用 HTTP 诊断范围内。
- 业务失败通过既有报告进入 data/history 和界面详情；data/logs 仍为日汇总索引。账户操作的错误显示在账户窗口“查看详情”。本轮不新增成功请求跟踪或常驻抓包日志。
- 中英文账户错误详情展示诊断字段。phase 和异常类型/错误码保留技术标识，便于排查。

## 保密与语义

不保存原始异常对象、原始异常消息、响应正文、请求路径或查询参数、Authorization、代理地址与密码。只记录域名，不输出预授权 uploadUrl。即使上层调用异常 ToString()，也不会包含原始异常中的敏感消息。

用户取消保留 OperationCanceledException 语义；请求超时按网络失败报告。现有只读重试和写入恢复规则保持不变，不自动重放结果不确定的写入。读取网络响应与写入本地目标分开，避免将本地磁盘错误标成远端连接故障。

proxyPolicy=SystemDefault 仅表示未覆盖 .NET 默认代理策略，不证明连接绕过系统代理、TUN 或透明转发。未增加代理选择设置；未关闭 TLS 验证或固定 TLS 版本，也未修改生产网络环境。

耗时从该次 HTTP 处理器开始计时，响应正文错误包含此前等待响应头的时间；不包含排队退避和先前尝试总耗时。MSAL 的认证网络故障使用实际认证请求域名，不冒充 Graph 故障。

## 使用

升级后正常使用；再次失败时，在运行记录中选中该次运行并查看详情，或打开对应 data/history JSON 中的 issues/message、files/error。账户/目录操作失败时查看账户窗口错误详情。

例如：host=graph.microsoft.com、phase=Send/Headers、causes=HttpRequestException/SecureConnectionError -> AuthenticationException/TLS -> SocketException/ConnectionReset，可定位为该接口连接阶段的 TLS/连接重置问题。若 host 是上传服务域名且 operation=Upload file content，则应进一步比较上传端点链路。错误分类是诊断线索，不等同于网络问题已修复。

## 验证

全量 375 项通过：Core 178、Windows 85、Desktop 54、Integration 58；构建零警告、零错误。新增 8 项网络测试和 1 项中英文错误展示测试，覆盖 TLS 内层错误链、DNS、上传/认证域名、只读重试次数、写入不重放、用户取消、超时、响应读取失败、目标磁盘错误、安全脱敏及 MSAL 包装解包。

## 发布

安装包：artifacts/publish/HarborBackup-0.9.0-preview.11-20261104-224814/installers/HarborBackup-0.9.0-preview.11-Setup-x64.exe。

安装包 37,163,323 字节（35.44 MiB），发行文件 125,358,878 字节（119.55 MiB，不含用户数据和卸载器）。SHA-256：90A14958156BEAA5883F39D8FAADE645987F66DAE0B89FC6E5F4F014C39F79B7。SDK 自包含发布、文件清单哈希和发布布局检查通过，无新增依赖。

## 验证边界

使用模拟 HTTP、合成异常、临时目录测试，不读取令牌、不上传生产文件。不为验证故障修改代理或关闭证书验证。当前正式托盘仍运行，本轮只生成安装包，不覆盖生产安装；真实直连上传故障待升级后的诊断确认。

MSAL 扩展依据：[官方 HttpClient 工厂说明](https://learn.microsoft.com/en-us/entra/msal/dotnet/advanced/httpclient)。
