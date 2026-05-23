# Native Layout

```text
PluginName/
├── Startup.cs
├── PluginJsonContext.cs
├── Tools/
├── Application/
├── Contracts/
├── Infrastructure/
└── Debug/
	├── PluginName.Debug.csproj
	└── Program.cs
```

调试项目只用于开发态 REPL 调试，引用插件项目和 `Netor.Cortana.Plugin.Native.Debugger`。不要把 `Debug/` 目录内容放入发布包。

发布最小产物：

```text
plugin-name/
├── plugin.json
└── PluginName.dll
```