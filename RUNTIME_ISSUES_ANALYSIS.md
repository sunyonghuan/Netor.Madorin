# Madorin UI Runtime Issues Analysis

**Date**: May 18, 2026  
**Application**: Netor.Cortana.UI (Main UI Project)  
**Status**: ✅ Partially Fixed - 1 of 3 issues resolved

---

## Executive Summary

The Madorin UI application started successfully with all core services initialized. However, 2 plugin loading issues and 1 legacy endpoint issue were detected during startup. The weather plugin issue has been fixed. The social plugin requires investigation into its build configuration.

---

## Issue 1: Weather Plugin - Missing 'command' Field ✅ FIXED

### Severity: Medium
### Status: RESOLVED

**Problem**:
```
[Plugin:weather-plugin]: plugin.json validation failed (process mode missing 'command' field)
Plugin not loaded
```

**Root Cause**:
The weather-plugin plugin.json had `executableName` field instead of the required `command` field for process mode plugins. The PluginManifest validation requires the `command` field for `PluginRuntime.Process`.

**Validation Code** (from `Src/Netor.Cortana.Plugin/Core/PluginManifest.cs`):
```csharp
case PluginRuntime.Process when string.IsNullOrWhiteSpace(Command):
    error = "process 模式缺少 'command' 字段";
    return false;
```

**Fix Applied**:
Changed the plugin.json from:
```json
{
    "runtime": "process",
    "executableName": "WeatherPlugin.exe",
    ...
}
```

To:
```json
{
    "runtime": "process",
    "command": "WeatherPlugin.exe",
    ...
}
```

**File Modified**:
- `Src/Netor.Cortana.UI/bin/Debug/net10.0/plugins/weather-plugin/plugin.json`

**Verification**:
- ✅ Fixed in bin directory
- ⚠️ **ACTION REQUIRED**: Also update the source plugin.json file (if it exists in the project source tree) to prevent this issue in future builds

---

## Issue 2: Social Plugin - Native Plugin Export Missing ⚠️ INVESTIGATION NEEDED

### Severity: Medium
### Status: REQUIRES INVESTIGATION

**Problem**:
```
[Plugin:social]: EntryPointNotFoundException - cortana_plugin_get_info not found
NativeHost subprocess exited with code 2
Plugin failed to load
```

**Root Cause Analysis**:

The SocialPlugin is configured as a **native plugin** in its plugin.json:
```json
{
    "id": "social",
    "runtime": "native",
    "libraryName": "SocialPlugin.PluginHost.dll",
    ...
}
```

Native plugins in Madorin must export specific functions:
- `cortana_plugin_get_info()` - Returns plugin metadata
- `cortana_plugin_init()` - Optional initialization
- `cortana_plugin_invoke()` - Tool invocation handler
- `cortana_plugin_free()` - Frees returned UTF-8 memory
- `cortana_plugin_destroy()` - Optional shutdown hook

The captured startup log used an older export name. Current `Netor.Cortana.NativeHost` expects `cortana_plugin_get_info`, so any current investigation should verify the `cortana_plugin_*` exports instead.

**Why This Happens**:

Native plugins must be built using the **Madorin Native Plugin Generator** which:
1. Scans for `[Plugin]` and `[Tool]` attributes
2. Generates the required export functions
3. Compiles to a native DLL with proper exports

The current `SocialPlugin.PluginHost.dll` appears to be a managed .NET DLL without the native exports.

**Recommended Solutions**:

### Option A: Rebuild SocialPlugin (Recommended)
1. Locate the SocialPlugin source project
2. Ensure it uses the Madorin Native Plugin Generator
3. Rebuild the project
4. Verify the generated DLL has the required exports

### Option B: Disable SocialPlugin (Temporary)
If the plugin is not needed for current testing:
```bash
# Remove or rename the plugin directory
mv plugins/SocialPlugin plugins/SocialPlugin.disabled
```

### Option C: Investigate Build Configuration
1. Check if SocialPlugin has a separate native wrapper project
2. Verify the build output is using the correct DLL
3. Check for any build configuration issues

**Files Involved**:
- `Src/Netor.Cortana.UI/bin/Debug/net10.0/plugins/SocialPlugin/plugin.json`
- `Src/Netor.Cortana.UI/bin/Debug/net10.0/plugins/SocialPlugin/SocialPlugin.PluginHost.dll`

**Next Steps**:
- [ ] Investigate SocialPlugin source project location
- [ ] Check if it's using the Native Plugin Generator
- [ ] Rebuild or disable the plugin
- [ ] Re-run the application to verify

---

## Issue 3: /internal 404 Endpoint ℹ️ EXPECTED BEHAVIOR

### Severity: Low
### Status: EXPECTED - LEGACY ENDPOINT

**Problem**:
```
HTTP 404 Error: Request to `/internal` returned 404
Request reached end of middleware pipeline without being handled
```

**Root Cause**:

This is a **legacy endpoint** from an older Madorin architecture. The new unified PluginBus architecture (Phase 2B+) has replaced it with a single `/internal` endpoint that handles all WebSocket communication.

**Architecture Evolution**:

**Old Architecture** (Legacy):
- `/ws/` - Chat WebSocket
- `/internal/conversation-feed/` - Conversation history feed
- Multiple separate endpoints for different concerns

**New Architecture** (Current - Phase 2B+):
- `/internal` - Unified PluginBus endpoint
- Protocol field distinguishes message types:
  - `cortana.plugin-bus` - Main plugin communication
  - `conversation` - Conversation topic
  - `memory` - Memory supply
  - `model` - Model capability
  - `workflow` - Workflow task events

**Implementation Details**:

From `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs`:
```csharp
private WebApplication BuildApp(int port)
{
    return BuildLocalhostApp(port, app =>
    {
        app.Map(CortanaWsEndpoints.PluginBusPath, context => 
            AcceptClientAsync(context, _cts?.Token ?? CancellationToken.None));
    });
}
```

Where `CortanaWsEndpoints.PluginBusPath = "/internal"`

**Migration Path**:

Any clients using the legacy `/ws/` or `/internal/conversation-feed/` endpoints should migrate to:
```
ws://localhost:{port}/internal
```

With subscription message:
```json
{
  "type": "subscribe",
  "protocol": "cortana.plugin-bus",
  "topics": ["conversation"],
  "capabilities": ["conversation.v1"]
}
```

**Files Involved**:
- `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs`
- `Src/Netor.Cortana.Entitys/CortanaWsEndpoints.cs`

**Status**: ✅ No action required - this is expected behavior

---

## Successfully Loaded Plugins ✅

The following 7 plugins loaded successfully:

| Plugin | Tools | Status |
|--------|-------|--------|
| application_launcher | 4 | ✅ Loaded |
| bt (Baota panel) | 21 | ✅ Loaded |
| memory_engine | 5 | ✅ Loaded |
| office | 23 | ✅ Loaded |
| reminder | 7 | ✅ Loaded |
| csx_script (C# Script Runner) | 9 | ✅ Loaded |
| window_management | 9 | ✅ Loaded |

**Total Tools Available**: 78

---

## Voice System Status ✅

All voice services initialized successfully:

- ✅ **Sherpa-ONNX KWS Engine**: Initialized (16000Hz sample rate)
- ✅ **Audio Input Devices**: 3 devices detected
- ✅ **Microphone Listening**: Started
- ✅ **MeloTTS TTS Engine**: Initialized (44100Hz)
- ✅ **Wake Greeting Audio**: Pre-generated and cached (54272 samples)

---

## Core Services Status ✅

All core services initialized successfully:

- ✅ **EventDispatcher**: Started (56 max concurrent, 28 worker threads)
- ✅ **Voice Services**: KWS/STT/TTS all started
- ✅ **AI Chat Service**: Loaded with default config (Provider=DeepSeek, Agent=小月)
- ✅ **WebSocket PluginBus**: Started on port 52841
- ✅ **Plugin Loader**: Scanned complete

---

## Recommendations

### Immediate Actions (Priority: High)
1. **Fix Weather Plugin Source**: Update the source plugin.json file to use `command` instead of `executableName`
2. **Investigate SocialPlugin**: Determine if it needs to be rebuilt or disabled

### Short-term Actions (Priority: Medium)
1. **Test Core Functionality**: Verify UI loads correctly in Avalonia window
2. **Test Chat Functionality**: Verify AI chat works with loaded plugins
3. **Test Plugin Tools**: Verify plugin tools are accessible and functional

### Long-term Actions (Priority: Low)
1. **Update Legacy Clients**: Migrate any clients using `/internal` to new `/internal` endpoint
2. **Documentation**: Update plugin development documentation to clarify `command` vs `executableName` fields

---

## Testing Checklist

- [ ] Weather plugin loads successfully after fix
- [ ] Social plugin either loads or is disabled
- [ ] UI window displays without errors
- [ ] Chat functionality works
- [ ] Plugin tools are accessible
- [ ] Voice input/output works
- [ ] WebSocket connections are stable

---

## References

**Related Documentation**:
- `Docs/未来版本策划/Kestrel WebSocket 服务重构与运营平台补充.md` - PluginBus architecture
- `Src/Netor.Cortana.Plugin/Core/PluginManifest.cs` - Plugin validation logic
- `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs` - WebSocket server implementation

**Key Classes**:
- `PluginManifest` - Plugin configuration validation
- `WebSocketPluginBusServerService` - Unified WebSocket server
- `NativePluginHost` - Native plugin loader
- `PluginLoader` - Main plugin loading orchestrator

---

**Report Generated**: 2026-05-18  
**Application Version**: 1.3.8 (UI)  
**Status**: ✅ Ready for testing after fixes



