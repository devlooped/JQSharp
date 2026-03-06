namespace Devlooped;

sealed class JqBreakException(string label) : Exception("break")
{
    public string Label { get; } = label;
}
