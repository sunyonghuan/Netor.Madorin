# Debugger 架构重构 : 100%

## Step 1 修改项目类型为类库 : 100%
- [√] 修改 .csproj OutputType 为 Library
- [√] 移除不必要的包引用（保留 Extensions 包）

## Step 2 重写 PluginDebugRunner.cs : 100%
- [√] 删除 Main 入口方法
- [√] 改为从 AppDomain.CurrentDomain.GetAssemblies() 扫描
- [√] 提供 DiscoverPlugins() / CreateHost() / RunInteractiveAsync() API

## Step 3 重写 DebugPluginHost.cs : 100%
- [√] 移除 Assembly 加载逻辑，程序集已在 AppDomain 中
- [√] 添加 configureServices 可选参数

## Step 4 重写 DebugPluginContext.cs : 100%
- [√] 简化代码，使用 LoggerFactory.Create 替代完全限定名

## Step 5 更新 PluginScanner.cs : 100%
- [√] 添加 TryScan 方法（不抛异常的扫描）
- [√] Scan 方法内部调用 TryScan

## Step 6 添加使用示例文档 : 100%
- [√] 删除空的 Debug.cs
- [√] README.md 说明正确的使用方式（待创建）

## Step 7 编译验证 : 100%
- [√] 运行 dotnet build 验证：0 错误，3 个 XML 注释警告

---

## 架构变更摘要

### 错误架构（之前）
```
PluginDebugRunner.exe (独立可执行程序)
  ├── 从磁盘扫描 DLL 路径
  ├── Assembly.LoadFrom 加载程序集
  └── 完全错误的设计！
```

### 正确架构（现在）
```
用户 Console.exe (F5 启动)
  ├── 引用 Netor.Cortana.Plugin.Native.Debugger (类库)
  ├── 引用 MyPlugin1 (类库)
  └── 所有程序集已在 AppDomain 中
      → AppDomain.CurrentDomain.GetAssemblies() 直接扫描
      → 无需 LoadFrom，无需路径！
```

### 核心 API
| 方法 | 说明 |
|------|------|
| `DiscoverPlugins()` | 扫描当前 AppDomain 发现所有插件 |
| `CreateHost()` | 创建调试宿主（自动发现唯一插件） |
| `CreateHost(Assembly)` | 创建调试宿主（指定程序集） |
| `RunInteractiveAsync(host)` | 进入交互式工具调用调试循环 |
