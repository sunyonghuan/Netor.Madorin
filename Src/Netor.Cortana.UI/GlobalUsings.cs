#region Microsoft.Extensions

global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;

#endregion

#region Microsoft.Agents.AI

// 不导入 Workflows 避免 Run 类型冲突
global using Microsoft.Agents.AI;

#endregion

#region Microsoft.Extensions.AI

global using Microsoft.Extensions.AI;

#endregion

#region Avalonia

global using Avalonia;
global using Avalonia.Controls;
global using Avalonia.Markup.Xaml;

#endregion

#region Project Namespaces

global using Netor.Cortana.UI;
global using Netor.Cortana.AI;
global using Netor.Cortana.AI.Providers;
global using Netor.Cortana.Entitys;
global using Netor.Cortana.Entitys.Services;
global using Netor.Cortana.Voice;

#endregion

#region EventHub

// 不导入顶层 Netor.EventHub 避免 EventArgs 冲突
global using Netor.EventHub.Services;

global using IPublisher = Netor.EventHub.IPublisher;
global using ISubscriber = Netor.EventHub.ISubscriber;

#endregion

#region Plugin

global using Netor.Cortana.Plugin.Mcp;

#endregion

#region System

global using System.Text.Json;

#endregion