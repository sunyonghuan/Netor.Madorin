using System.Security.Cryptography;
using System.Text;

namespace Netor.Cortana.Entitys.Extensions;

/// <summary>
/// 通用扩展方法集合，供所有模块共享。
/// </summary>
public static class CommonExtensions
{
    /// <summary>
    /// 将字符串截断到指定的最大长度。
    /// </summary>
    public static string Truncate(this string str, int maxLength)
    {
        return str.Length <= maxLength ? str : str[..maxLength];
    }

    /// <summary>
    /// 计算字符串的 MD5 哈希值并返回 32 位小写十六进制字符串。
    /// </summary>
    public static string Md5Encrypt(this string str)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(str));
        return Convert.ToHexStringLower(hash);
    }
}
