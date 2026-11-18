using System.Text.Json;

namespace JQLite;

/// <summary>An error of a jq expression: an uncaught runtime error, or (as <see cref="JqParseException"/>) a syntax or binding error.</summary>
public class JqException : Exception
{
    /// <summary>Creates an exception with a message and no error value.</summary>
    public JqException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception for a jq error value: <paramref name="message"/> describes it.</summary>
    public JqException(string message, JsonElement value)
        : base(message)
        => Value = value;

    /// <summary>
    /// The error value of an uncaught runtime error (what <c>error(v)</c> raised, or the message
    /// string of a builtin's error), or <see langword="null"/>. The <see cref="Exception.Message"/> is
    /// the value itself when it is a string, else its JSON text followed by <c>(not a string)</c>.
    /// </summary>
    public JsonElement? Value { get; }
}

/// <summary>A syntax or binding error, thrown by <see cref="Jq.Parse(string, JqOptions?)"/> before any evaluation.</summary>
public sealed class JqParseException : JqException
{
    internal JqParseException(string message, int line, int column)
        : base(message)
        => (Line, Column) = (line, column);

    /// <summary>The 1-based line of the error, in the expression or in the module the message names.</summary>
    public int Line { get; }

    /// <summary>The 1-based column of the error, counting code points.</summary>
    public int Column { get; }
}

/// <summary>
/// <c>halt_error</c> ended the evaluation: <see cref="JqException.Value"/> is the value it was given
/// and <see cref="ExitCode"/> its code. (<c>halt</c> ends an evaluation normally, without an exception.)
/// </summary>
public sealed class JqHaltException : JqException
{
    internal JqHaltException(string message, JsonElement value, int exitCode)
        : base(message, value)
        => ExitCode = exitCode;

    /// <summary>The exit code given to <c>halt_error</c>.</summary>
    public int ExitCode { get; }
}
