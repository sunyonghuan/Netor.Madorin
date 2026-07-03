using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models.Settings;
using Netor.Cortana.Platform.Admin.Models.Shared;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Services.Packages;
using Netor.Database.Abstractions;
using Netor.Database.Abstractions.Enums;
using System.Text.Json;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class SettingsController(PlatformDbContext dbContext, PackageStorageService packageStorageService) : Controller
{
    public async Task<IActionResult> Index(string? keyword, string? group, CancellationToken cancellationToken = default)
    {
        await dbContext.EnsureSeededAsync(cancellationToken);

        var query = dbContext.SystemSettings.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Key.Contains(normalizedKeyword) ||
                x.Name.Contains(normalizedKeyword) ||
                x.Display.Contains(normalizedKeyword) ||
                x.Value.Contains(normalizedKeyword));
        }

        if (!string.IsNullOrWhiteSpace(group))
        {
            query = query.Where(x => x.Group == group);
        }

        var allSettings = await dbContext.SystemSettings.AsNoTracking().ToListAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.Group)
            .ThenBy(x => x.Name)
            .ThenBy(x => x.Key)
            .Select(x => new SettingListItem(
                x.ID,
                x.Key,
                x.Name,
                x.Display,
                x.Value,
                x.Type,
                x.IsProtection,
                x.Group ?? string.Empty))
            .ToListAsync(cancellationToken);

        var model = new SettingsIndexViewModel
        {
            Items = items,
            Groups = allSettings
                .GroupBy(x => string.IsNullOrWhiteSpace(x.Group) ? "未分组" : x.Group!)
                .OrderBy(x => x.Key)
                .Select(x => new SettingGroupSummary(
                    x.Key,
                    x.Count(),
                    x.Count(i => i.Type == NetorDataType.Boolean),
                    x.Count(i => i.IsProtection)))
                .ToList(),
            Keyword = keyword,
            Group = group,
            TotalCount = allSettings.Count,
            BooleanCount = allSettings.Count(x => x.Type == NetorDataType.Boolean),
            ProtectedCount = allSettings.Count(x => x.IsProtection),
            EditableCount = allSettings.Count(x => !x.IsProtection)
        };

        return View(model);
    }

    public async Task<IActionResult> TableData(
        string? keyword,
        string? keyPrefix,
        string? group,
        NetorDataType? type,
        bool? isProtection,
        bool? isEmpty,
        string? displayKeyword,
        string? nameKeyword,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        await dbContext.EnsureSeededAsync(cancellationToken);
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.SystemSettings.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Key.Contains(normalizedKeyword) ||
                x.Name.Contains(normalizedKeyword) ||
                x.Display.Contains(normalizedKeyword) ||
                x.Value.Contains(normalizedKeyword));
        }

        if (!string.IsNullOrWhiteSpace(keyPrefix))
        {
            var normalizedPrefix = keyPrefix.Trim();
            query = query.Where(x => x.Key.StartsWith(normalizedPrefix));
        }

        if (!string.IsNullOrWhiteSpace(group))
        {
            query = query.Where(x => x.Group == group);
        }

        if (type is not null)
        {
            query = query.Where(x => x.Type == type);
        }

        if (isProtection is not null)
        {
            query = query.Where(x => x.IsProtection == isProtection);
        }

        if (isEmpty is true)
        {
            query = query.Where(x => x.Value == string.Empty);
        }
        else if (isEmpty is false)
        {
            query = query.Where(x => x.Value != string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(displayKeyword))
        {
            var normalizedDisplay = displayKeyword.Trim();
            query = query.Where(x => x.Display.Contains(normalizedDisplay));
        }

        if (!string.IsNullOrWhiteSpace(nameKeyword))
        {
            var normalizedName = nameKeyword.Trim();
            query = query.Where(x => x.Name.Contains(normalizedName));
        }

        var count = await query.CountAsync(cancellationToken);
        var settings = await ApplySettingSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = settings
            .Select(x => new SettingTableItem(
                x.ID,
                x.Key,
                x.Name,
                x.Display,
                x.Value,
                MaskValue(x.Key, x.Value),
                x.Type,
                GetTypeName(x.Type),
                x.IsProtection,
                x.IsProtection ? "保护" : "可编辑",
                x.Group ?? string.Empty))
            .ToList();

        return Json(new LayuiTableResult<SettingTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    public IActionResult Create()
    {
        return View("Edit", new SettingsEditViewModel
        {
            IsCreate = true,
            Type = NetorDataType.Text,
            Group = "平台设置"
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SettingsEditViewModel model, CancellationToken cancellationToken)
    {
        model.IsCreate = true;
        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "settingTable"));
            }

            return View("Edit", model);
        }

        var key = model.Key.Trim();
        var keyExists = await dbContext.SystemSettings.AnyAsync(x => x.Key == key, cancellationToken);
        if (keyExists)
        {
            ModelState.AddModelError(nameof(model.Key), "设置键已存在");
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "settingTable"));
            }

            return View("Edit", model);
        }

        dbContext.SystemSettings.Add(new SystemSetting
        {
            Key = key,
            Name = model.Name.Trim(),
            Display = model.Display?.Trim() ?? string.Empty,
            Value = NormalizeValue(model.Value, model.Type),
            Type = model.Type,
            Group = string.IsNullOrWhiteSpace(model.Group) ? null : model.Group.Trim(),
            IsProtection = model.IsProtection
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        const string successMessage = "系统设置已创建。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "settingTable", true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage, "settingTable");
        }

        return RedirectToAction(nameof(Index), new { group = model.Group });
    }

    public async Task<IActionResult> Details(string id, CancellationToken cancellationToken)
    {
        var setting = await dbContext.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (setting is null)
        {
            return NotFound();
        }

        return View(new SettingsDetailViewModel
        {
            Id = setting.ID,
            Key = setting.Key,
            Name = setting.Name,
            Display = string.IsNullOrWhiteSpace(setting.Display) ? "-" : setting.Display,
            Value = MaskValue(setting.Key, setting.Value),
            Type = setting.Type,
            TypeName = GetTypeName(setting.Type),
            Group = setting.Group ?? "未分组",
            IsProtection = setting.IsProtection
        });
    }

    public async Task<IActionResult> Edit(string id, CancellationToken cancellationToken)
    {
        var setting = await dbContext.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (setting is null)
        {
            return NotFound();
        }

        return View(new SettingsEditViewModel
        {
            Id = setting.ID,
            Key = setting.Key,
            Name = setting.Name,
            Display = setting.Display,
            Value = setting.Value,
            Type = setting.Type,
            Group = setting.Group ?? string.Empty,
            IsProtection = setting.IsProtection
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(SettingsEditViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "settingTable"));
            }

            return View(model);
        }

        if (string.IsNullOrWhiteSpace(model.Id))
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("设置不存在或已被删除。", "settingTable"));
            }

            return NotFound();
        }

        var setting = await dbContext.SystemSettings.FirstOrDefaultAsync(x => x.ID == model.Id, cancellationToken);
        if (setting is null)
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("设置不存在或已被删除。", "settingTable"));
            }

            return NotFound();
        }

        var key = model.Key.Trim();
        var keyExists = await dbContext.SystemSettings.AnyAsync(x => x.ID != model.Id && x.Key == key, cancellationToken);
        if (keyExists)
        {
            ModelState.AddModelError(nameof(model.Key), "设置键已存在");
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "settingTable"));
            }

            return View(model);
        }

        setting.Key = key;
        setting.Name = model.Name.Trim();
        setting.Display = model.Display?.Trim() ?? string.Empty;
        setting.Value = NormalizeValue(model.Value, model.Type);
        setting.Type = model.Type;
        setting.Group = string.IsNullOrWhiteSpace(model.Group) ? null : model.Group.Trim();
        setting.IsProtection = model.IsProtection;
        await dbContext.SaveChangesAsync(cancellationToken);

        const string successMessage = "系统设置已保存。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "settingTable", true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage, "settingTable");
        }

        return RedirectToAction(nameof(Index), new { group = setting.Group });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SettingsBatchUpdateViewModel model, CancellationToken cancellationToken)
    {
        var ids = model.Items.Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var settings = await dbContext.SystemSettings.Where(x => ids.Contains(x.ID)).ToListAsync(cancellationToken);
        var values = model.Items.ToDictionary(x => x.Id, x => x.Value ?? string.Empty);

        foreach (var setting in settings)
        {
            if (setting.IsProtection)
            {
                continue;
            }

            if (values.TryGetValue(setting.ID, out var value))
            {
                setting.Value = NormalizeValue(value, setting.Type);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminToast"] = "系统设置已批量保存。";
        return RedirectToAction(nameof(Index), new { keyword = model.Keyword, group = model.Group });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestPackageStorage(CancellationToken cancellationToken)
    {
        var result = await packageStorageService.TestCurrentProviderAsync(cancellationToken);
        if (result.Passed)
        {
            TempData["AdminToast"] = $"{result.ProviderName} 检测通过：{result.Message}";
        }
        else
        {
            TempData["SettingError"] = $"{result.ProviderName} 检测失败：{result.Message}";
        }

        return RedirectToAction(nameof(Index), new { group = "对象存储" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset(string id, CancellationToken cancellationToken)
    {
        var setting = await dbContext.SystemSettings.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (setting is null)
        {
            return NotFound();
        }

        setting.Value = GetDefaultValue(setting.Key, setting.Type);
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["AdminToast"] = $"{setting.Name} 已恢复推荐默认值。";
        return RedirectToAction(nameof(Index), new { group = setting.Group });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var setting = await dbContext.SystemSettings.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (setting is null)
        {
            return NotFound();
        }

        if (setting.IsProtection)
        {
            TempData["SettingError"] = "保护设置不能删除。";
            return RedirectToAction(nameof(Index), new { group = setting.Group });
        }

        var group = setting.Group;
        dbContext.SystemSettings.Remove(setting);
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["AdminToast"] = "系统设置已删除。";
        return RedirectToAction(nameof(Index), new { group });
    }

    private static IQueryable<SystemSetting> ApplySettingSorting(IQueryable<SystemSetting> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "key" => isAscending ? query.OrderBy(x => x.Key) : query.OrderByDescending(x => x.Key),
            "name" => isAscending ? query.OrderBy(x => x.Name) : query.OrderByDescending(x => x.Name),
            "type" => isAscending ? query.OrderBy(x => x.Type) : query.OrderByDescending(x => x.Type),
            "isProtection" => isAscending ? query.OrderBy(x => x.IsProtection) : query.OrderByDescending(x => x.IsProtection),
            "group" => isAscending ? query.OrderBy(x => x.Group) : query.OrderByDescending(x => x.Group),
            _ => query.OrderBy(x => x.Group).ThenBy(x => x.Name).ThenBy(x => x.Key)
        };
    }

    private static string GetTypeName(NetorDataType type)
    {
        return type switch
        {
            NetorDataType.Text => "文本",
            NetorDataType.Boolean => "开关",
            _ => type.ToString()
        };
    }

    private static string MaskValue(string key, string value)
    {
        return key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("key", StringComparison.OrdinalIgnoreCase)
            ? "******"
            : value;
    }

    private bool IsDrawerRequest()
    {
        return string.Equals(Request.Query["mode"].ToString(), "drawer", StringComparison.OrdinalIgnoreCase)
            || (Request.HasFormContentType && string.Equals(Request.Form["mode"].ToString(), "drawer", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsAjaxRequest()
    {
        return string.Equals(Request.Headers.XRequestedWith.ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
            || Request.Headers.Accept.Any(x => x?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
    }

    private string GetModelStateMessage()
    {
        return ModelState.Values
            .SelectMany(x => x.Errors)
            .Select(x => x.ErrorMessage)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            ?? "提交失败，请检查表单内容。";
    }

    private static object DrawerJson(string message, string tableId, bool success = false)
    {
        return new
        {
            success,
            message,
            tableId
        };
    }

    private ContentResult DrawerSuccess(string message, string tableId)
    {
        var encodedMessage = JsonSerializer.Serialize(message);
        var encodedTableId = JsonSerializer.Serialize(tableId);
        var html = "<!DOCTYPE html><html lang=\"zh-CN\"><body><script>"
            + "if (parent && parent.layui) {"
            + $"if (parent.layui.table) parent.layui.table.reload({encodedTableId});"
            + $"if (parent.layui.layer) parent.layui.layer.msg({encodedMessage});"
            + "}"
            + "if (parent && parent.layer && window.name) {"
            + "parent.layer.close(parent.layer.getFrameIndex(window.name));"
            + "} else {"
            + "location.href = document.referrer || \"/Settings\";"
            + "}"
            + "</script></body></html>";

        return Content(html, "text/html");
    }

    private static string NormalizeValue(string? value, NetorDataType type)
    {
        value ??= string.Empty;
        return type switch
        {
            NetorDataType.Boolean => bool.TryParse(value, out var boolean) && boolean ? "true" : "false",
            _ => value.Trim()
        };
    }

    private static string GetDefaultValue(string key, NetorDataType type)
    {
        return key switch
        {
            "platform.name" => "Madorin",
            "platform.site.domain" => "aimdl.cn",
            "platform.storage.root" => "Data",
            "package.storage.provider" => "Local",
            "package.storage.s3.endpoint" => string.Empty,
            "package.storage.s3.bucket" => string.Empty,
            "package.storage.s3.accessKeyId" => string.Empty,
            "package.storage.s3.accessKeySecret" => string.Empty,
            "package.storage.s3.region" => "us-east-1",
            "package.storage.s3.forcePathStyle" => "true",
            "package.storage.publicBaseUrl" => string.Empty,
            "platform.download.anonymous" => "false",
            "finance.currency.unit" => "CNY",
            "security.register.enabled" => "true",
            "security.login.captcha" => "false",
            "resource.review.required" => "true",
            "resource.featured.limit" => "8",
            "subscription.default.duration" => "365",
            "download.daily.limit" => "50",
            "notification.email.enabled" => "false",
            "notification.webhook.url" => string.Empty,
            "system.maintenance.enabled" => "false",
            "system.maintenance.message" => "系统维护中，请稍后再试。",
            _ => type == NetorDataType.Boolean ? "false" : string.Empty
        };
    }
}
