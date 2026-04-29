using Netor.Cortana.Plugin.Native;

namespace NativeTestPlugin;

/// <summary>
/// 数学工具，用于测试参数解析功能。
/// </summary>
[Tool]
public class MathTools
{
    /// <summary>
    /// 计算两个数字的和。
    /// </summary>
    [Tool(Name = "math_add", Description = "计算两个数字的和，用于测试参数解析功能。")]
    public string MathAdd(
        [Parameter(Description = "第一个加数")] double a,
        [Parameter(Description = "第二个加数")] double b)
    {
        var sum = a + b;
        return $"{a} + {b} = {sum}";
    }

    /// <summary>
    /// 返回前 15 个质数。
    /// </summary>
    [Tool(Name = "get_first15_primes", Description = "返回前 15 个质数列表。")]
    public int[] GetFirst15Primes()
    {
        var primes = new List<int>();
        var candidate = 2;

        while (primes.Count < 15)
        {
            if (IsPrime(candidate))
                primes.Add(candidate);

            candidate++;
        }

        return primes.ToArray();

        static bool IsPrime(int n)
        {
            if (n < 2) return false;
            for (var i = 2; i * i <= n; i++)
            {
                if (n % i == 0) return false;
            }
            return true;
        }
    }
}