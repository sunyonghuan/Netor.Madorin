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

public sealed class ForgotPasswordViewModel
{
    [Required(ErrorMessage = "请输入账号或邮箱")]
    [Display(Name = "账号或邮箱")]
    public string Account { get; set; } = string.Empty;
}

public sealed class ResetPasswordViewModel
{
    [Required(ErrorMessage = "请输入账号或邮箱")]
    [Display(Name = "账号或邮箱")]
    public string Account { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入注册邮箱")]
    [EmailAddress(ErrorMessage = "邮箱格式不正确")]
    [Display(Name = "注册邮箱")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入新密码")]
    [DataType(DataType.Password)]
    [Display(Name = "新密码")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "请再次输入新密码")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "两次密码不一致")]
    [Display(Name = "确认新密码")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class ProfileViewModel
{
    public long No { get; set; }

    [Display(Name = "登录名")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入昵称")]
    [Display(Name = "昵称")]
    public string NickName { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入邮箱")]
    [EmailAddress(ErrorMessage = "邮箱格式不正确")]
    [Display(Name = "邮箱")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "手机号")]
    public string Phone { get; set; } = string.Empty;

    [DataType(DataType.Password)]
    [Display(Name = "新密码")]
    public string? NewPassword { get; set; }

    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword), ErrorMessage = "两次密码不一致")]
    [Display(Name = "确认新密码")]
    public string? ConfirmNewPassword { get; set; }
}

public sealed class AccountProfileViewModel
{
    [Display(Name = "登录账号")]
    public string LoginUserName { get; set; } = string.Empty;

    [Display(Name = "昵称")]
    public string? NickName { get; set; }

    [Required(ErrorMessage = "请输入邮箱")]
    [EmailAddress(ErrorMessage = "邮箱格式不正确")]
    [Display(Name = "邮箱")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "手机号")]
    public string? Phone { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "当前密码")]
    public string? CurrentPassword { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "新密码")]
    public string? NewPassword { get; set; }

    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword), ErrorMessage = "两次密码不一致")]
    [Display(Name = "确认新密码")]
    public string? ConfirmNewPassword { get; set; }
}
