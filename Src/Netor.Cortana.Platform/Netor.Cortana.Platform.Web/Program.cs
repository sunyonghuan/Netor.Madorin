using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.FileProviders;
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
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/account/login";
        options.Cookie.Name = "Cortana.Platform.Web";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
    });
builder.Services.AddAuthorization();
builder.Services.AddControllersWithViews();
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

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
