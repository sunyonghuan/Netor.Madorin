namespace Madorin.AI.Runtime.Cli.Commands;

internal interface IReplInterruptSource
{
    public IDisposable Subscribe(Action interruptHandler);
}
