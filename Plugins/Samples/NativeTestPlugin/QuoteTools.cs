using Netor.Cortana.Plugin;

namespace NativeTestPlugin;

/// <summary>
/// 名言工具，用于测试无参数工具调用。
/// </summary>
[Tool]
public class QuoteTools
{
    private readonly QuoteRepository _repository;

    public QuoteTools(QuoteRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// 返回一条随机编程名言。
    /// </summary>
    [Tool(Name = "sys_random_quote", Description = "返回一条随机编程名言，用于测试无参数工具调用。")]
    public string RandomQuote()
    {
        return _repository.GetRandom();
    }
}
