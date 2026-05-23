# 大文件排除说明

由于 GitHub 对单个文件有 100MB 的大小限制，以下文件已被添加到 `.gitignore` 中排除提交：

## 排除的文件列表

### 1. ONNX 模型文件 (共 10 个文件，约 1.5GB)

| 文件路径 | 大小 |
|---------|------|
| `Src/sherpa_models/TTS/sherpa-onnx-zipvoice-zh-en-emilia/fm_decoder.onnx` | 455.27 MB |
| `Src/sherpa_models/STT/encoder.int8.onnx` | 157.80 MB |
| `Src/sherpa_models/TTS/sherpa-onnx-zipvoice-distill-int8-zh-en-emilia/decoder.int8.onnx` | 118.88 MB |
| `Src/sherpa_models/TTS/sherpa-onnx-zipvoice-zh-en-emilia/fm_decoder_int8.onnx` | 118.85 MB |
| `Src/sherpa_models/TTS/kokoro-int8-multi-lang-v1_1/model.int8.onnx` | 109.00 MB |
| `Src/sherpa_models/TTS/TTS-Kokoro/model.int8.onnx` | 109.00 MB |

### 2. 模型压缩包 (1 个文件，140MB)

| 文件路径 | 大小 |
|---------|------|
| `Src/sherpa_models/TTS/kokoro-int8-multi-lang-v1_1.tar.bz2` | 140.22 MB |

### 3. 编译生成的大型文件 (4 个文件，约 600MB)

| 文件路径 | 大小 |
|---------|------|
| `Src/Netor.Cortana.UI/bin/Release/net10.0/win-x64/native/Cortana.pdb` | 157.11 MB |
| `Src/Netor.Cortana.UI/obj/Release/net10.0/win-x64/native/Cortana.obj` | 146.78 MB |
| `Src/Netor.Cortana.UI/obj/Release/net10.0/win-x64/native/Cortana.Avalonia.obj` | 139.65 MB |

---

## 如何获取这些文件？

### 方案 1：使用 Git LFS (推荐)
如果需要版本控制这些大文件，建议使用 Git LFS：

```bash
# 安装 Git LFS
git lfs install

# 追踪大文件类型
git lfs track "*.onnx"
git lfs track "*.tar.bz2"

# 提交 .gitattributes
git add .gitattributes
git commit -m "Add Git LFS tracking"
```

### 方案 2：外部存储
将模型文件上传到：
- **云存储**：阿里云 OSS、腾讯云 COS、AWS S3
- **模型仓库**：Hugging Face、ModelScope
- **网盘**：百度网盘、OneDrive

在 README 中提供下载链接和放置路径说明。

### 方案 3：本地构建
如果这些文件可以通过脚本生成或下载，提供自动化脚本：

```bash
# 示例：下载模型脚本
./scripts/download_models.sh
```

---

## .gitignore 规则

已添加以下规则到 `.gitignore`：

```gitignore
# ONNX 模型文件
*.onnx
**/sherpa_models/**/*.onnx

# 模型压缩包
*.tar.bz2
**/sherpa_models/**/*.tar.bz2

# 编译生成的大型对象文件和符号文件
*.obj
*.pdb

# bin 和 obj 目录
**/sherpa_models/
**/obj/**/native/
```

---

## 注意事项

1. **编译文件**：`bin/` 和 `obj/` 目录下的文件本就应该被排除，这些是编译生成的临时文件
2. **模型文件**：当前 UI 项目从 `Src/sherpa_models/{KWS,STT,TTS}` 复制模型到输出目录，运行时通过 `UserDataDirectory/sherpa_models/{KWS,STT,TTS}` 加载
3. **首次克隆**：新克隆的仓库需要手动下载模型文件到 `Src/sherpa_models/` 对应子目录，发布产物中也需要保留输出目录下的 `sherpa_models/`

---

**生成时间**：2025-05-XX  
**扫描工具**：PowerShell Get-ChildItem  
**文件总数**：15 个  
**总大小**：约 2.1 GB
