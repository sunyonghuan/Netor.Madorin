using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Platform.Admin.Models.Auth;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "请输入管理员账号")]
    [Display(Name = "管理员账号")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入登录密码")]
    [DataType(DataType.Password)]
    [Display(Name = "登录密码")]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "记住登录")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}
