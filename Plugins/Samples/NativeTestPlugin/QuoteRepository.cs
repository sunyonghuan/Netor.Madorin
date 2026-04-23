namespace NativeTestPlugin;

/// <summary>
/// 编程名言仓库。
/// </summary>
public class QuoteRepository
{
    private static readonly string[] Quotes =
    [
        "代码是写给人看的，顺便能在机器上跑。—— Harold Abelson",
        "过早优化是万恶之源。—— Donald Knuth",
        "简单是可靠的先决条件。—— Edsger Dijkstra",
        "先让它能跑，再让它跑对，最后让它跑快。—— Kent Beck",
        "好的代码是它自己最好的文档。—— Steve McConnell",
        "任何傻瓜都能写出计算机能理解的代码。优秀的程序员写人能理解的代码。—— Martin Fowler",
        "调试的难度是写代码的两倍。所以如果你尽全力写代码，按定义你就不够聪明来调试它。—— Brian Kernighan",
        "Talk is cheap. Show me the code. —— Linus Torvalds"
    ];

    /// <summary>
    /// 获取一条随机编程名言。
    /// </summary>
    public string GetRandom() => Quotes[Random.Shared.Next(Quotes.Length)];
}
