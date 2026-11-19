# OneDrive HTTP 400 修复与验证（0.8.0-preview.6）

日期：2026-11-03。

## 复现与结论

使用当前授权进行隔离验证：目录读取成功；createUploadSession 请求体内显式指定
@microsoft.graph.conflictBehavior=fail 时，当前个人 OneDrive 后端返回 HTTP 400 / invalidRequest。
删除该字段虽然可以创建会话，但实测可能覆盖已有同名文件，If-None-Match: * 也未阻止覆盖。
因此没有采用“删除参数直接上传正式文件”的修复。

当前后端的会话上传及下载返回 my.microsoftpersonalcontent.com，原有地址允许列表缺少此精确域名。
原错误处理只报告 HTTP 状态，没有保留安全的结构化诊断字段。

## 实现

- 不超过 4 MiB 的文件（包括空文件、仓库标识、普通元信息）直接使用 content 上传接口，
  查询参数显式指定 conflictBehavior=fail。并发同名创建拒绝覆盖；不会改成 replace 重试。
- 较大文件通过上传会话写入随机的 .harbor-upload-<GUID>.tmp 暂存文件。
  创建会话仅指定暂存名称，不携带当前后端拒绝的冲突属性。
- 传输完成后使用 PATCH 改为正式名称，查询参数 conflictBehavior=fail，并携带暂存版本的 If-Match。
  目标名称已存在时返回 409，保留已有文件和暂存内容。
- 事务里的 TransferSession 新增可选暂存名称、已完成项目 ID 和 ETag。
  创建会话前记录暂存名称，发布前记录完整暂存文件，发布成功后清除会话。
  恢复验证名称、大小、类型和版本；旧式直接面向正式名称的会话不再恢复写入，使用新的独立暂存身份。
  暂存文件属于恢复证据，不能当普通缓存清理。
- 精确允许 HTTPS 默认端口的 my.microsoftpersonalcontent.com；不放开其任意子域。
  授权 Bearer 仅发送给 Graph；预授权内容端点仍不携带 Bearer。
- CloudRequestException 仅保存失败步骤、HTTP 状态、受限格式错误代码和 GUID 请求 ID。
  错误响应最多读取 32 KiB；不复制原始响应正文、账户路径、令牌或预授权 URL。
  非 JSON 错误仍显示状态和步骤。账户窗口的“查看详情”支持中英文。

## 自动化验证

./scripts/test/Test.ps1 -NoRestore -IsolatedProfile

Core 178、Windows 64、Desktop 30、Integration 15，共 287 项通过；构建零警告、零错误。
记录位于 artifacts/test-results/current，本轮 TRX 时间为 2026-11-03 16:01–16:03。

新增覆盖：小文件和空文件拒绝覆盖上传、已有文件不写入、并发冲突不降级 replace、
大文件暂存及条件发布、完成阶段中断恢复、发布冲突保留恢复信息、
内容域名不转发 Bearer、恶意相似域名/非 HTTPS/自定义端口拒绝、
结构化错误脱敏、非 JSON 错误、中英文详情。

## 真实个人 OneDrive 隔离验证

所有云端写入仅使用本轮生成的独立临时文件夹和合成内容，本机验证配置位于 artifacts。
未修改正式账户配置、正式仓库及备份文件。

- 原始会话参数 HTTP 400 可复现。
- 直接上传 /content?@microsoft.graph.conflictBehavior=fail 对同名目标返回 409，原内容不变。
- 暂存文件条件发布：新名称成功；已有名称及第二个并发发布返回 409，首次内容不变。
- 使用修复后的 CloudAccountSession 实际创建仓库、写入并读回 repository.json、保存本机验证配置、
  重新浏览仓库并校验身份成功。
- 实际 GraphDrive 上传并下载 0、257、4194321 字节，内容逐字节一致。
- 在 4194333 字节文件传输完成、发布之前注入中断；使用保存的完成阶段恢复，发布并下载核验成功。
- 尝试写入不同的同名元信息被拒绝，原项目 ETag 未改变。
- 所有临时云端目录清理返回 HTTP 204。正式账户配置读回与测试前一致。

这不是两台实体设备测试，也不代表正式安装升级及完整备份任务的全流程验收。
本轮不自动替换用户正在运行的安装版。

## 参考

- [Microsoft Graph 上传会话](https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0)
- [Microsoft Graph 文件内容上传](https://learn.microsoft.com/en-us/graph/api/driveitem-put-content?view=graph-rest-1.0)

文档中的默认冲突行为与本次个人账户后端实测不一致，因此以显式拒绝覆盖接口和隔离验证为发布依据。

## 发布物

安装包：artifacts/publish/HarborBackup-0.8.0-preview.6-20261103-160614/installers/HarborBackup-0.8.0-preview.6-Setup-x64.exe

安装包 53063261 字节（约 50.6 MiB），安装载荷 182056870 字节。
SHA-256：579600F068162A2DF7D1127701F813D844DB3E9F9680A79C99543DFB1A35202A。
完整私有 .NET 10.0.14 运行时、不裁剪、未签名。与 preview.5 相比，安装包增加 5583 字节，载荷增加 12288 字节。
发布载荷 CLI --help 启动成功。未自动安装或替换正在运行的正式版本；安装生命周期及两台实体设备验收尚未执行。
