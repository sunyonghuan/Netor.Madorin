# Server Record Schema

## 固定目录

- 工作区/Servers/{IP}/server.md
- 工作区/Servers/{IP}/id_rsa

## 必填字段

- IP
- 端口号
- 名称
- 用户名
- 登录方式

## 约束

- 字段名固定使用中文标签和全角冒号。
- server.md 信息完整时，不再重复询问同一字段。
- 登录方式只允许填写 密钥 或 密码初始化。