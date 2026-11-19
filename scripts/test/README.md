# test

职责和命令见 ../../docs/development/verification.md。生成文件全部写入 artifacts。

## 真实 OneDrive 性能对照

`./scripts/test/Measure-OneDriveFlow.ps1 -Label <label>` 在新建随机云端根目录中运行正常备份、无变化、无关变化、预览和确认拉取。`-CacheOnly` 只测缓存场景；`-TransportCompare` 交错比较四路/八路小文件和 3.125 MiB/10 MiB 分块，各两轮。需要已授权的当前用户账户、退出托盘和 CLI，以及能解密 DPAPI 的 Windows 用户执行环境。

工具只复制加密凭据到仓库 artifacts 的独立目录，不保存正式账户配置；最后核验并删除自建云端测试根和凭据副本，检查正式配置及原缓存哈希不变。结果、匿名时序、源码哈希及本轮二进制位于 `artifacts/diagnostics/flow-<label>-<id>`。失败结果保留，不能按成功记录解释。此工具不属于默认离线回归，不替代安装验收；不要提交其产物或身份材料。
