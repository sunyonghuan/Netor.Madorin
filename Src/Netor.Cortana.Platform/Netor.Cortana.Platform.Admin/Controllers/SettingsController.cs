using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models.Settings;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Database.Abstractions;
using Netor.Database.Abstractions.Enums;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class SettingsController(PlatformDbContext dbContext) : Controller
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
            return View("Edit", model);
        }

        var key = model.Key.Trim();
        var keyExists = await dbContext.SystemSettings.AnyAsync(x => x.Key == key, cancellationToken);
        if (keyExists)
        {
            ModelState.AddModelError(nameof(model.Key), "设置键已存在");
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

        TempData["AdminToast"] = "系统设置已创建。";
        return RedirectToAction(nameof(Index), new { group = model.Group });
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
            return View(model);
        }

        if (string.IsNullOrWhiteSpace(model.Id))
        {
            return NotFound();
        }

        var setting = await dbContext.SystemSettings.FirstOrDefaultAsync(x => x.ID == model.Id, cancellationToken);
        if (setting is null)
        {
            return NotFound();
        }

        var key = model.Key.Trim();
        var keyExists = await dbContext.SystemSettings.AnyAsync(x => x.ID != model.Id && x.Key == key, cancellationToken);
        if (keyExists)
        {
            ModelState.AddModelError(nameof(model.Key), "设置键已存在");
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

        TempData["AdminToast"] = "系统设置已保存。";
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
            "platform.storage.root" => "Data",
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
