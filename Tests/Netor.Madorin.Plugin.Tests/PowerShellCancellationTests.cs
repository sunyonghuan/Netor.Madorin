using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Madorin.Plugin.BuiltIn.PowerShell;

namespace Netor.Madorin.Plugin.Tests;

/// <summary>
/// 验证停止按钮优化后，PowerShellExecutor / ExecutionSession
/// 在取消令牌触发时能立即终止进程树，而非等待超时。
/// </summary>
[TestClass]
public sealed class PowerShellCancellationTests
{
    // ──── PowerShellExecutor（快速执行模式 sys_execute_powershell）────

    /// <summary>
    /// 正常命令应正常完成，不被误取消。
    /// </summary>
    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task Executor_NormalCommand_CompletesSuccessfully()
    {
        var executor = new PowerShellExecutor(NullLogger<PowerShellExecutor>.Instance);
        await using var _ = executor;

        var result = await executor.ExecuteAsync("Write-Output 'hello'", timeout: 10_000);

        Assert.IsTrue(result.Success, $"期望成功，实际输出：{result.FullOutput}");
        StringAssert.Contains(result.Output, "hello");
    }

    /// <summary>
    /// 取消令牌触发时，执行器应在 1.5 秒内抛出 OperationCanceledException，
    /// 不应等到 60 秒超时。
    /// </summary>
    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task Executor_CancelledMidExecution_ThrowsQuickly()
    {
        var executor = new PowerShellExecutor(NullLogger<PowerShellExecutor>.Instance);
        await using var _ = executor;

        using var cts = new CancellationTokenSource();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 启动 20 秒长命令，800ms 后取消
        var task = executor.ExecuteAsync("Start-Sleep 20", timeout: 60_000, ct: cts.Token);
        await Task.Delay(800);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => task);

        sw.Stop();

        // 从取消信号发出到任务结束，不应超过 1500ms
        Assert.IsLessThan(1500L + 800L, sw.ElapsedMilliseconds,
            $"取消后进程未在 1.5s 内终止，实际耗时 {sw.ElapsedMilliseconds}ms");
    }

    // ──── ExecutionSession（会话模式 sys_send_command）────

    /// <summary>
    /// 正常命令应正常输出，不被误取消。
    /// </summary>
    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task Session_NormalCommand_YieldsOutput()
    {
        await using var session = new ExecutionSession(
            NullLogger<SessionRegistry>.Instance, background: true);

        // 等会话就绪
        await WaitForSessionReadyAsync(session);

        var lines = new List<string>();
        await foreach (var line in session.ExecuteCommandAsync("Write-Output 'hi'", timeoutMs: 10_000))
        {
            lines.Add(line);
        }

        Assert.IsTrue(lines.Any(l => l.Contains("hi")),
            $"期望输出包含 'hi'，实际：{string.Join("|", lines)}");
        Assert.AreEqual(ExecutionSessionState.Ready, session.State,
            "命令完成后会话应回到 Ready 状态");
    }

    /// <summary>
    /// 取消令牌触发时，会话的 ExecuteCommandAsync 应在 1.5 秒内抛出
    /// OperationCanceledException，进程树已被终止，会话状态变为 Failed。
    /// </summary>
    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task Session_CancelledMidExecution_ThrowsQuickly()
    {
        await using var session = new ExecutionSession(
            NullLogger<SessionRegistry>.Instance, background: true);

        await WaitForSessionReadyAsync(session);

        using var cts = new CancellationTokenSource();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 启动 20 秒长命令
        var iterationTask = Task.Run(async () =>
        {
            await foreach (var _ in session.ExecuteCommandAsync("Start-Sleep 20",
                               timeoutMs: 60_000, ct: cts.Token))
            {
                // 消费输出
            }
        });

        // 等命令进入执行阶段再取消
        await Task.Delay(800);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => iterationTask);

        sw.Stop();

        Assert.IsLessThan(1500L + 800L, sw.ElapsedMilliseconds,
            $"取消后进程未在 1.5s 内终止，实际耗时 {sw.ElapsedMilliseconds}ms");

        Assert.AreEqual(ExecutionSessionState.Failed, session.State,
            "取消后会话进程已被杀死，状态应为 Failed");
    }

    // ──── 辅助 ────

    private static async Task WaitForSessionReadyAsync(
        ExecutionSession session,
        int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (session.State == ExecutionSessionState.Ready)
                return;
            await Task.Delay(50);
        }

        Assert.Fail($"会话未在 {timeoutMs}ms 内进入 Ready 状态，当前：{session.State}");
    }
}
