using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.FileProviders;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Netor.Cortana.Platform.Admin.Mcp;
using Netor.Cortana.Platform.Admin.Mcp.Tools;
using Netor.Cortana.Platform.Admin.Operations;
using Netor.Cortana.Platform.Admin.Services;
using Netor.Cortana.Platform.Entitys;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Services;
using Netor.Cortana.Platform.Services.Docs;

var builder = WebApplication.CreateBuilder(args);
var httpPort = builder.Configuration.GetValue<int?>("Server:HttpPort");
if (httpPort is > 0)
{
    builder.WebHost.UseUrls($"http://*:{httpPort}");
}

// Add services to the container.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.LogoutPath = "/Auth/Logout";
        options.AccessDeniedPath = "/Auth/Login";
        options.Cookie.Name = "Cortana.Platform.Admin";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    })
    .AddScheme<AuthenticationSchemeOptions, AdminMcpAuthHandler>(AdminMcpAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy(AdminMcpAuthHandler.SchemeName, policy => policy
        .AddAuthenticationSchemes(AdminMcpAuthHandler.SchemeName)
        .RequireAuthenticatedUser());
});
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("AdminMcpPerToken", httpContext =>
    {
        var tokenId = httpContext.User.FindFirst("TokenID")?.Value ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(tokenId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("Mcp:RateLimit:PermitPerMinute", 60),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = builder.Configuration.GetValue("Mcp:RateLimit:QueueLimit", 0)
        });
    });
});
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "cortana-admin", Version = "1.0.0" };
    })
    .WithHttpTransport()
    .WithTools<AccountsTool>()
    .WithTools<AssetsTool>()
    .WithTools<DashboardTool>()
    .WithTools<DocsTool>();
builder.Services.AddScoped<AdminBrandingService>();
builder.Services.AddScoped<AdminShellMetricsService>();
builder.Services.AddScoped<AdminMcpTokenService>();
builder.Services.AddScoped<AdminAuditService>();
builder.Services.AddScoped<AdminMcpContext>();
builder.Services.AddScoped<AdminMcpIdempotencyService>();
builder.Services.AddScoped<AccountOperations>();
builder.Services.AddScoped<AssetOperations>();
builder.Services.AddScoped<DashboardOperations>();
builder.Services.AddScoped<DocOperations>();
builder.Services.AddPlatformDbContext(builder.Configuration);
builder.Services.AddPlatformServices(builder.Configuration);

var app = builder.Build();

await app.InitializeAsync();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
var docsMediaPathService = app.Services.GetRequiredService<DocsMediaPathService>();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(docsMediaPathService.EnsureRootPath(app.Environment.ContentRootPath)),
    RequestPath = docsMediaPathService.RequestPath
});
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapGet("/mcp/health", () => Results.Ok(new AdminMcpHealthResponse("ok", "1.0.0")))
    .AllowAnonymous();

if (builder.Configuration.GetValue("Mcp:Enabled", false))
{
    app.MapMcp("/mcp")
        .RequireAuthorization(AdminMcpAuthHandler.SchemeName)
        .RequireRateLimiting("AdminMcpPerToken");
}

app.Run();

public sealed record AdminMcpHealthResponse(string Status, string Version);
