using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Netor.Cortana.Platform.Web.Controllers;

public abstract class WebControllerBase : Controller
{
    protected string? CurrentAccountId => User.FindFirstValue(ClaimTypes.NameIdentifier);
}