# 组件构建器

## 作用

`component` 是 2.10+ 的统一组件构建机制，用于建立更一致的组件 API、实例管理和事件模型。

## 关键能力

- 创建组件
- 统一实例接口
- 全局设置
- 事件定义
- 获取实例
- 删除实例
- 扩展接口

## 典型能力

- `layui.component(options)`
- `component.removeInst(id)`
- `component.CONST`

## 使用建议

- 新组件优先按统一构建器规范设计，便于实例化、销毁和扩展。
- 组件应暴露清晰的 `render`、`reload`、`destroy` 类方法。

## AI 使用规则

- 设计复用型组件时，先考虑是否能用统一组件构建器组织实例。
- 如果业务需要多实例控制，应设计唯一 ID 和实例回收机制。

## 文档地址

- `https://layui.dev/docs/2/component/`
