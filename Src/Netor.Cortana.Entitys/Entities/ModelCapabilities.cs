using System;

namespace Netor.Cortana.Entitys;

/// <summary>
/// 模型输入能力位标志。描述模型可接受的输入内容类型。
/// </summary>
[Flags]
public enum InputCapabilities
{
    None = 0,
    Text = 1,
    Image = 1 << 1,
    Audio = 1 << 2,
    Video = 1 << 3,
    File = 1 << 4,
}

/// <summary>
/// 模型输出能力位标志。描述模型可产生的输出内容类型。
/// </summary>
[Flags]
public enum OutputCapabilities
{
    None = 0,
    Text = 1,
    Image = 1 << 1,
    Audio = 1 << 2,
    Video = 1 << 3,
}

/// <summary>
/// 模型交互能力位标志。描述模型支持的交互特性。
/// </summary>
[Flags]
public enum InteractionCapabilities
{
    None = 0,
    FunctionCall = 1,
    Streaming = 1 << 1,
    SystemPrompt = 1 << 2,
    JsonMode = 1 << 3,
}
