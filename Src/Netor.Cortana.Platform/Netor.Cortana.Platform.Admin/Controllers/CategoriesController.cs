using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models.Categories;
using Netor.Cortana.Platform.Admin.Models.Shared;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using System.Text.Json;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class CategoriesController(PlatformDbContext dbContext) : Controller
{
    public async Task<IActionResult> Index(string? keyword, bool? visible, CancellationToken cancellationToken)
    {
        var query = dbContext.Categories
            .AsNoTracking()
            .Include(x => x.Assets)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x => x.Name.Contains(normalizedKeyword) || x.Slug.Contains(normalizedKeyword));
        }

        if (visible is not null)
        {
            query = query.Where(x => x.IsVisible == visible);
        }

        var items = await query
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new CategoryListItem(
                x.ID,
                x.Name,
                x.Slug,
                x.Description,
                x.SortOrder,
                x.IsVisible,
                x.Assets.Count))
            .ToListAsync(cancellationToken);

        var model = new CategoryIndexViewModel
        {
            Items = items,
            Keyword = keyword,
            Visible = visible,
            TotalCount = await dbContext.Categories.CountAsync(cancellationToken),
            VisibleCount = await dbContext.Categories.CountAsync(x => x.IsVisible, cancellationToken),
            HiddenCount = await dbContext.Categories.CountAsync(x => !x.IsVisible, cancellationToken)
        };

        return View(model);
    }

    public async Task<IActionResult> TableData(
        string? keyword,
        string? slug,
        bool? visible,
        int? sortMin,
        int? sortMax,
        int? assetMin,
        int? assetMax,
        int? publishedAssetMin,
        int? publishedAssetMax,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.Categories
            .AsNoTracking()
            .Include(x => x.Assets)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Name.Contains(normalizedKeyword) ||
                x.Slug.Contains(normalizedKeyword) ||
                (x.Description != null && x.Description.Contains(normalizedKeyword)));
        }

        if (!string.IsNullOrWhiteSpace(slug))
        {
            var normalizedSlug = slug.Trim();
            query = query.Where(x => x.Slug.Contains(normalizedSlug));
        }

        if (visible is not null)
        {
            query = query.Where(x => x.IsVisible == visible);
        }

        if (sortMin is not null)
        {
            query = query.Where(x => x.SortOrder >= sortMin);
        }

        if (sortMax is not null)
        {
            query = query.Where(x => x.SortOrder <= sortMax);
        }

        if (assetMin is not null)
        {
            query = query.Where(x => x.Assets.Count >= assetMin);
        }

        if (assetMax is not null)
        {
            query = query.Where(x => x.Assets.Count <= assetMax);
        }

        if (publishedAssetMin is not null)
        {
            query = query.Where(x => x.Assets.Count(a => a.Status == AssetStatus.Published) >= publishedAssetMin);
        }

        if (publishedAssetMax is not null)
        {
            query = query.Where(x => x.Assets.Count(a => a.Status == AssetStatus.Published) <= publishedAssetMax);
        }

        var count = await query.CountAsync(cancellationToken);
        var categories = await ApplyCategorySorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = categories.Select(x => new CategoryTableItem(
                x.ID,
                x.Name,
                x.Slug,
                string.IsNullOrWhiteSpace(x.Description) ? "-" : x.Description,
                x.SortOrder,
                x.IsVisible,
                x.IsVisible ? "显示" : "隐藏",
                x.IsVisible ? "admin-badge-success" : "admin-badge-muted",
                x.Assets.Count,
                x.Assets.Count(a => a.Status == AssetStatus.Published),
                x.TimeStamp))
            .ToList();

        return Json(new LayuiTableResult<CategoryTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    public IActionResult Create()
    {
        return View(new CategoryEditViewModel { SortOrder = 50, IsVisible = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CategoryEditViewModel model, CancellationToken cancellationToken)
    {
        await ValidateSlugAsync(model, null, cancellationToken);

        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "categoryTable"));
            }

            return View(model);
        }

        dbContext.Categories.Add(new Category
        {
            Name = model.Name.Trim(),
            Slug = model.Slug.Trim(),
            Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim(),
            SortOrder = model.SortOrder,
            IsVisible = model.IsVisible
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        const string successMessage = "分类已创建。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "categoryTable", true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage, "categoryTable");
        }

        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(string id, CancellationToken cancellationToken)
    {
        var category = await dbContext.Categories.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (category is null)
        {
            return NotFound();
        }

        var model = new CategoryEditViewModel
        {
            Id = category.ID,
            Name = category.Name,
            Slug = category.Slug,
            Description = category.Description,
            SortOrder = category.SortOrder,
            IsVisible = category.IsVisible
        };

        return View(model);
    }

    public async Task<IActionResult> Details(string id, CancellationToken cancellationToken)
    {
        var category = await dbContext.Categories
            .AsNoTracking()
            .Include(x => x.Assets)
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);

        if (category is null)
        {
            return NotFound();
        }

        var model = new CategoryDetailViewModel
        {
            Id = category.ID,
            Name = category.Name,
            Slug = category.Slug,
            Description = string.IsNullOrWhiteSpace(category.Description) ? "-" : category.Description,
            SortOrder = category.SortOrder,
            IsVisible = category.IsVisible,
            AssetCount = category.Assets.Count,
            PublishedAssetCount = category.Assets.Count(x => x.Status == AssetStatus.Published),
            Assets = category.Assets
                .OrderByDescending(x => x.TimeStamp)
                .Take(20)
                .Select(x => new CategoryAssetItem(
                    x.ID,
                    x.Name,
                    x.Slug,
                    GetAssetTypeName(x.Type),
                    GetAssetStatusName(x.Status),
                    x.DownloadCount))
                .ToList()
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string id, CategoryEditViewModel model, CancellationToken cancellationToken)
    {
        if (model.Id != id)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson("分类参数不匹配。", "categoryTable"));
            }

            return BadRequest();
        }

        await ValidateSlugAsync(model, id, cancellationToken);

        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "categoryTable"));
            }

            return View(model);
        }

        var category = await dbContext.Categories.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (category is null)
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("分类不存在或已被删除。", "categoryTable"));
            }

            return NotFound();
        }

        category.Name = model.Name.Trim();
        category.Slug = model.Slug.Trim();
        category.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        category.SortOrder = model.SortOrder;
        category.IsVisible = model.IsVisible;

        await dbContext.SaveChangesAsync(cancellationToken);
        const string successMessage = "分类已保存。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "categoryTable", true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage, "categoryTable");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleVisible(string id, CancellationToken cancellationToken)
    {
        var category = await dbContext.Categories.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (category is null)
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("分类不存在或已被删除。", "categoryTable"));
            }

            return NotFound();
        }

        category.IsVisible = !category.IsVisible;
        await dbContext.SaveChangesAsync(cancellationToken);
        var message = category.IsVisible ? "分类已显示。" : "分类已隐藏。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(message, "categoryTable", true));
        }

        TempData["AdminToast"] = message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Batch(string[] ids, string operation, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            const string emptyMessage = "请先选择要操作的分类。";
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(emptyMessage, "categoryTable"));
            }

            TempData["AdminToast"] = emptyMessage;
            return RedirectToAction(nameof(Index));
        }

        var categories = await dbContext.Categories
            .Include(x => x.Assets)
            .Where(x => ids.Contains(x.ID))
            .ToListAsync(cancellationToken);

        if (operation == "delete")
        {
            var deletable = categories.Where(x => x.Assets.Count == 0).ToList();
            dbContext.Categories.RemoveRange(deletable);
            await dbContext.SaveChangesAsync(cancellationToken);
            var message = deletable.Count == categories.Count
                ? $"已批量删除 {deletable.Count} 个分类。"
                : $"已删除 {deletable.Count} 个空分类，存在资源的分类已跳过。";
            if (IsAjaxRequest())
            {
                return Json(DrawerJson(message, "categoryTable", true));
            }

            TempData["AdminToast"] = message;
            return RedirectToAction(nameof(Index));
        }

        foreach (var category in categories)
        {
            if (operation == "show")
            {
                category.IsVisible = true;
            }
            else if (operation == "hide")
            {
                category.IsVisible = false;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var successMessage = $"已批量处理 {categories.Count} 个分类。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "categoryTable", true));
        }

        TempData["AdminToast"] = successMessage;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var category = await dbContext.Categories
            .Include(x => x.Assets)
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);

        if (category is null)
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("分类不存在或已被删除。", "categoryTable"));
            }

            return NotFound();
        }

        if (category.Assets.Count > 0)
        {
            var assetUrl = Url.Action("Index", "Assets", new { categoryId = category.ID }) ?? "/Assets";
            var message = $"分类“{category.Name}”下还有 {category.Assets.Count} 个资源，暂时不能删除。请先到资源管理中调整这些资源的分类绑定后再删除。";
            if (IsAjaxRequest())
            {
                return BadRequest(new
                {
                    success = false,
                    message,
                    tableId = "categoryTable",
                    assetUrl
                });
            }

            TempData["AdminToast"] = message;
            return RedirectToAction(nameof(Index));
        }

        dbContext.Categories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken);
        const string successMessage = "分类已删除。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "categoryTable", true));
        }

        TempData["AdminToast"] = successMessage;
        return RedirectToAction(nameof(Index));
    }

    private static IQueryable<Category> ApplyCategorySorting(IQueryable<Category> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "name" => isAscending ? query.OrderBy(x => x.Name) : query.OrderByDescending(x => x.Name),
            "slug" => isAscending ? query.OrderBy(x => x.Slug) : query.OrderByDescending(x => x.Slug),
            "sortOrder" => isAscending ? query.OrderBy(x => x.SortOrder) : query.OrderByDescending(x => x.SortOrder),
            "isVisible" => isAscending ? query.OrderBy(x => x.IsVisible) : query.OrderByDescending(x => x.IsVisible),
            "assetCount" => isAscending ? query.OrderBy(x => x.Assets.Count) : query.OrderByDescending(x => x.Assets.Count),
            "publishedAssetCount" => isAscending ? query.OrderBy(x => x.Assets.Count(a => a.Status == AssetStatus.Published)) : query.OrderByDescending(x => x.Assets.Count(a => a.Status == AssetStatus.Published)),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
        };
    }

    private static string GetAssetTypeName(AssetType type) => type switch
    {
        AssetType.Plugin => "插件",
        AssetType.Skill => "技能",
        AssetType.Agent => "智能体",
        AssetType.Solution => "解决方案",
        _ => type.ToString()
    };

    private static string GetAssetStatusName(AssetStatus status) => status switch
    {
        AssetStatus.Draft => "草稿",
        AssetStatus.Published => "已发布",
        AssetStatus.Hidden => "隐藏",
        AssetStatus.Offline => "下架",
        _ => status.ToString()
    };

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
            + "location.href = document.referrer || \"/Categories\";"
            + "}"
            + "</script></body></html>";

        return Content(html, "text/html");
    }

    private async Task ValidateSlugAsync(CategoryEditViewModel model, string? currentId, CancellationToken cancellationToken)
    {
        model.Name = model.Name.Trim();
        model.Slug = model.Slug.Trim();
        model.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();

        var exists = await dbContext.Categories
            .AnyAsync(x => x.Slug == model.Slug && x.ID != currentId, cancellationToken);

        if (exists)
        {
            ModelState.AddModelError(nameof(CategoryEditViewModel.Slug), "分类标识已存在");
        }
    }
}
