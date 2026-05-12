using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Platform.Web.Models.Account;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "请输入账号")]
    [Display(Name = "账号")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入密码")]
    [DataType(DataType.Password)]
    [Display(Name = "密码")]
    public string Password { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}

public sealed class RegisterViewModel
{
    [Required(ErrorMessage = "请输入登录名")]
    [Display(Name = "登录名")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入邮箱")]
    [EmailAddress(ErrorMessage = "邮箱格式不正确")]
    [Display(Name = "邮箱")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "手机号")]
    public string? Phone { get; set; }

    [Required(ErrorMessage = "请输入密码")]
    [DataType(DataType.Password)]
    [Display(Name = "密码")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "请再次输入密码")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "两次密码不一致")]
    [Display(Name = "确认密码")]
    public string ConfirmPassword { get; set; } = string.Empty;
}