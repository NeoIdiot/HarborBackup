# 0.9.0-preview.5 设置文案

日期：2026-11-04（Asia/Shanghai）。

- 中英文“历史版本本机保留”标签移除数量括注，同时作用于设置页和账户窗口。
- 仅修改显示文案，普通历史保留上一份的策略及帮助说明不变。
- 核查确认：CloudRepository.ReadJsonAsync / JsonAsync 按 Drive ID、Item ID、ETag 在 data/cache/cloud 保存元信息缓存；本机基线、事务等保存在 data/state，共享文件版本元信息保存在云端管理目录 metadata。读取时先获取云端版本标识，缓存不能替代最新云端状态核查。
- 本机缓存不是完整离线镜像，可删除后重建；恢复所需 state 不属于缓存。
- 本次不修改业务配置、数据布局、OneDrive 按需下载属性或备份逻辑。

验证：发布构建、资源编译、发布目录私有运行时 CLI --help 和 git diff --check 均通过。此低风险文案修改不新增实现镜像测试。未执行正式覆盖安装。

产物：artifacts/publish/HarborBackup-0.9.0-preview.5-20261104-162015/installers/HarborBackup-0.9.0-preview.5-Setup-x64.exe。
安装包 53,068,152 bytes；payload 182,129,299 bytes；SHA-256：5B875D6ECE35CA7AE1476074746A40DF9CF20DFFD0ECADFD8B1B78740A0F5D8F。完整私有运行时，未裁剪、未签名。
