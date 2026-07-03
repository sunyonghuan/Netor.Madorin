# Architecture Checklist

1. Entry 层是否只做组合。
2. Tools 是否无业务逻辑和 I/O。
3. Application 是否承载用例。
4. Infrastructure 是否封装外部交互。
5. 自定义模型是否有 JsonContext 注册。
6. `[Plugin]` 是否完整声明了 Id、Name、Version、Description，以及需要暴露的 Capabilities。
7. 宿主权限申请是否使用 `[RequiredHostCapability]`，并区分必需能力与可选能力。
8. 设置界面字段是否使用 `[PluginSetting]`，敏感值是否标记 `Sensitive = true`。
9. 运行时代码是否统一通过 `PluginSettings` 读取宿主注入，而不是手工解析 init JSON。
10. 外部包是否已做版本查询和 AOT 探测。
11. 发布安装是否走安装技能和热更新工具。
