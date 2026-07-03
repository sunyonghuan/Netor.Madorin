---
title: Native 通道 C ABI 契约（非 C# 原生 DLL）
version: 1
---

# Native 通道 C ABI 契约

本文档面向**非 C# 开发者**：只要你能产出导出 C 风格函数的原生 DLL，就能接入 Native 通道。

已知可行语言：C、C++、Rust（`#[no_mangle] extern "C"`）、Go（`//export` + `-buildmode=c-shared`）、Zig。

> Cortana 的宿主子进程 `Cortana.NativeHost.exe` 会用 `LoadLibrary` / `dlopen` 加载你的 DLL，逐个解析导出符号并调用。

## 1. 必须导出的 5 个 C 函数

```c
// 必需
const char* cortana_plugin_get_info(void);
const char* cortana_plugin_invoke(const char* toolName, const char* argsJson);
void        cortana_plugin_free(void* ptr);

// 可选（未导出时宿主视为 no-op）
int         cortana_plugin_init(const char* configJson);
void        cortana_plugin_destroy(void);
```

- 符号名**必须严格**为 `cortana_plugin_*`，不能带 C++ name mangling。C++ 要用 `extern "C"`。
- 调用约定：平台默认（Windows x64 是 Microsoft x64，Linux 是 SysV）。**不要**用 `__stdcall` / `WINAPI`。
- 所有字符串参数/返回值：**UTF-8 编码，NUL 结尾**。

## 2. 语义与生命周期

调用顺序：`get_info` → `init`（可选）→ 多次 `invoke` → `destroy`（可选）→ DLL 卸载。

### 2.1 `cortana_plugin_get_info`

返回插件元数据 JSON，结构见 [process-protocol.md §3.1](./process-protocol.md#31-get_info)（完全相同）。

- 返回指针的所有权归**宿主**，使用完毕后宿主会调用 `cortana_plugin_free(ptr)`。
- 插件需用可通过 `cortana_plugin_free` 释放的分配器分配内存（见 §3）。
- 返回 `NULL` 表示失败。

### 2.2 `cortana_plugin_init(configJson)`

参数：UTF-8 JSON 字符串，字段同 [process-protocol.md §3.2](./process-protocol.md#32-init)：`dataDirectory` / `workspaceDirectory` / `pluginDirectory` / `wsPort`。

- **传入指针由宿主拥有**，插件只能在本次调用内读取，不得持有。
- 返回 `int`：**非零**表示成功，`0` 表示失败。
- 未导出时宿主跳过 init 阶段，直接进入 invoke。

### 2.3 `cortana_plugin_invoke(toolName, argsJson)`

- 两个传入参数均为宿主拥有的 UTF-8 字符串，调用期有效，不得持有。
- 返回 UTF-8 JSON 字符串或纯文本指针，宿主调用完后用 `cortana_plugin_free` 释放。
- 返回 `NULL` 表示错误（宿主会响应 `{success:false}`）。

### 2.4 `cortana_plugin_free(ptr)`

释放由 `get_info` / `invoke` 返回的指针。宿主**只**会调用此函数释放插件返回的内存。

插件可以实现为 `free(ptr)`、`delete[] ptr`、Rust 的 `Box::from_raw` 等，只要与分配方式匹配。

### 2.5 `cortana_plugin_destroy`

无参无返回。进程退出前被调用。释放全局状态。

## 3. 内存所有权规则

| 方向 | 谁分配 | 谁释放 |
|---|---|---|
| 宿主 → 插件（`toolName`/`args`/`configJson`） | 宿主 | 宿主（调用返回后自行释放；插件**不可**持有） |
| 插件 → 宿主（`get_info`/`invoke` 返回） | 插件 | 宿主会调 `cortana_plugin_free` |

**关键**：插件返回的指针释放方式必须与分配方式匹配，自行在 `cortana_plugin_free` 内处理。

## 4. plugin.json

```json
{
  "id": "my_plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "runtime": "native",
  "libraryName": "MyPlugin.dll"
}
```

- `runtime` 必须为 `"native"`
- `libraryName` 是相对插件目录的 DLL 文件名（Windows `.dll`、Linux `.so`、macOS `.dylib`）
- 其他字段（description/tags/instructions/tools）可留空；宿主调 `get_info` 取真实元数据

## 5. 线程与错误

- 宿主**单线程**顺序调用导出函数，无需插件内部加锁
- 不要抛异常越过 C ABI 边界（C++ 要 try/catch 全部吞掉；Rust 要 `catch_unwind`）
- 标准错误 (`stderr`) 允许输出日志；**不要**往 `stdout` 写（那是协议通道）

## 6. 最小 C 示例

```c
#include <string.h>
#include <stdlib.h>

#define EXPORT __declspec(dllexport)  // Windows；Linux 用 __attribute__((visibility("default")))

static const char* INFO_JSON =
    "{\"id\":\"demo\",\"name\":\"Demo\",\"version\":\"1.0.0\","
    "\"tools\":[{\"name\":\"demo_echo\",\"description\":\"echo\","
    "\"parameters\":[{\"name\":\"text\",\"type\":\"string\",\"required\":true}]}]}";

EXPORT const char* cortana_plugin_get_info(void) {
    char* buf = (char*)malloc(strlen(INFO_JSON) + 1);
    strcpy(buf, INFO_JSON);
    return buf;
}

EXPORT const char* cortana_plugin_invoke(const char* toolName, const char* argsJson) {
    // 极简：直接把 args 原样回显
    size_t n = strlen(argsJson);
    char* out = (char*)malloc(n + 1);
    memcpy(out, argsJson, n + 1);
    return out;
}

EXPORT void cortana_plugin_free(void* ptr) {
    free(ptr);
}
```

编译：`cl /LD demo.c /Fe:MyPlugin.dll`（MSVC）或 `gcc -shared -fPIC -o MyPlugin.dll demo.c`（MinGW）。

## 7. 语言特定要点

### Rust

```rust
use std::ffi::{CStr, CString};
use std::os::raw::c_char;

#[no_mangle]
pub extern "C" fn cortana_plugin_get_info() -> *mut c_char {
    CString::new(r#"{"id":"demo",...}"#).unwrap().into_raw()
}

#[no_mangle]
pub extern "C" fn cortana_plugin_free(ptr: *mut c_char) {
    if !ptr.is_null() { unsafe { let _ = CString::from_raw(ptr); } }
}
```

Cargo.toml：`[lib] crate-type = ["cdylib"]`

### Go

Go 可以产出 C 共享库，但需要 `cgo`：

```go
package main
/*
#include <stdlib.h>
*/
import "C"
import "unsafe"

//export cortana_plugin_get_info
func cortana_plugin_get_info() *C.char {
    return C.CString(`{"id":"demo",...}`)
}

//export cortana_plugin_free
func cortana_plugin_free(ptr unsafe.Pointer) { C.free(ptr) }

func main() {}
```

编译：`go build -buildmode=c-shared -o MyPlugin.dll`

### Python / Node.js / 其他解释型语言

**不适用 Native 通道**（无法直接产出原生 DLL）。改走 [Process 通道](./process-protocol.md)。

## 8. 为什么 C# 必须 AOT？

.NET 的 IL DLL 依赖 CLR 运行时初始化，不能被 `LoadLibrary` 直接当原生库加载。AOT 发布后才产出真正的原生代码 + `UnmanagedCallersOnly` 导出表，符合本契约。

因此 C# 插件直接用 [csharp-native.md](./csharp-native.md) 描述的 `[Plugin]` / `[Tool]` 高层 API，Generator 会自动生成符合本文档契约的导出函数——你**不需要**手写 C ABI。
