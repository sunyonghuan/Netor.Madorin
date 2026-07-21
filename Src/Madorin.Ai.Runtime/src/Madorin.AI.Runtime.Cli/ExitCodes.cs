namespace Madorin.AI.Runtime.Cli;

public static class ExitCodes
{
    public const int Success = 0;
    public const int GeneralError = 1;
    public const int InvalidArguments = 2;
    public const int AuthenticationFailed = 3;
    public const int WorkspaceError = 4;
    public const int ConnectionFailed = 5;
    public const int UserInterrupted = 130;
}
