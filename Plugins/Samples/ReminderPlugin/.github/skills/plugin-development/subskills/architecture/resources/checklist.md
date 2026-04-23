# Architecture Checklist

1. Entry 层是否只做组合。
2. Tools 是否无业务逻辑和 I/O。
3. Application 是否承载用例。
4. Infrastructure 是否封装外部交互。
5. 自定义模型是否有 JsonContext 注册。
6. 外部包是否已做版本查询和 AOT 探测。
7. 发布安装是否走安装技能和热更新工具。