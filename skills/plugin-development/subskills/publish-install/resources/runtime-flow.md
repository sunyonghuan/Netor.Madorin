# Runtime Flow

1. 生成 zip。
2. 查询已加载插件目录名。
3. 卸载目标插件。
4. 调用 `install-plugin-package.ps1`，由它转发到 `skills/skill-plugin-installation/scripts/install-package.ps1` 覆盖安装。
5. 重载插件。
6. 验证插件列表。
