namespace Devlooped;

sealed class JqHaltException : Exception
{
    public int ExitCode { get; }

    public JqHaltException(int exitCode)
        : base("halt")
    {
        ExitCode = exitCode;
    }
}
