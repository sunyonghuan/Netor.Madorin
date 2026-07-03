using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Netor.Cortana.Platform.Services.Orders;
using Netor.Cortana.Platform.Web.Models.Orders;

namespace Netor.Cortana.Platform.Web.Controllers;

[Authorize]
[Route("orders")]
public sealed class OrdersController(OrderService orderService) : WebControllerBase
{
    [HttpGet("confirm")]
    public async Task<IActionResult> Confirm(string assetId, string pricingPlanId, CancellationToken cancellationToken)
    {
        var data = await orderService.GetPlanInfoAsync(assetId, pricingPlanId, cancellationToken);
        if (data is null)
        {
            return NotFound();
        }

        return View(new OrderConfirmViewModel
        {
            AssetId = assetId,
            PricingPlanId = pricingPlanId,
            AssetName = data.AssetName,
            PricingPlanName = data.PricingPlanName,
            Price = data.Price,
            Currency = data.Currency
        });
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string assetId, string pricingPlanId, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId;
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Challenge();
        }

        var result = await orderService.CreateAsync(accountId, assetId, pricingPlanId, cancellationToken);
        if (result.Status == CreateOrderStatus.NotFound)
        {
            return NotFound();
        }

        if (result.Status == CreateOrderStatus.AccountDisabled)
        {
            TempData["WebToast"] = "账号已被禁用或冻结，暂时不能订阅资源。";
            return RedirectToAction("Assets", "Market");
        }

        if (result.Status == CreateOrderStatus.FreeSubscriptionCreated)
        {
            TempData["WebToast"] = "免费资源已加入订阅，可直接下载。";
            return RedirectToAction("Download", "Downloads", new { assetId });
        }

        TempData["WebToast"] = "订单已创建，请完成模拟支付。";
        return RedirectToAction(nameof(Pay), new { id = result.OrderId });
    }

    [HttpGet("pay/{id}")]
    public async Task<IActionResult> Pay(string id, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId;
        var order = string.IsNullOrWhiteSpace(accountId)
            ? null
            : await orderService.GetPaymentInfoAsync(accountId, id, cancellationToken);

        if (order is null)
        {
            return NotFound();
        }

        return View(new OrderPayViewModel { OrderId = id, OrderNo = order.OrderNo, Title = order.Title, Amount = order.Amount });
    }

    [HttpPost("pay/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PayPost(string id, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId;
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Challenge();
        }

        var result = await orderService.PayAsync(accountId, id, cancellationToken);
        if (!result.Found)
        {
            return NotFound();
        }

        if (result.IsAccountDisabled)
        {
            TempData["WebToast"] = "账号已被禁用或冻结，暂时不能完成支付。";
            return RedirectToAction(nameof(Pay), new { id });
        }

        TempData["WebToast"] = "模拟支付成功，订阅已生效。";
        return RedirectToAction(nameof(Result), new { id });
    }

    [HttpGet("result/{id}")]
    public async Task<IActionResult> Result(string id, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId;
        var order = string.IsNullOrWhiteSpace(accountId)
            ? null
            : await orderService.GetPaymentInfoAsync(accountId, id, cancellationToken);

        if (order is null)
        {
            return NotFound();
        }

        return View(new OrderResultViewModel { OrderId = id, Title = order.Title, IsPaid = order.IsPaid });
    }
}
