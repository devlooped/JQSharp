using System.Collections.Immutable;
using System.Text;
using Devlooped.Core.Binding;
using Devlooped.Core.Runtime;
using Devlooped.Core.Values;
using static Devlooped.Core.Builtins.NativeTable;
using Path = Devlooped.Core.Runtime.Path;

namespace Devlooped.Core.Builtins;

/// <summary>§9.2 Core and §9.3 Types: the natives of the evaluation model itself.</summary>
static class CoreNatives
{
    // builtins/0: every non-internal builtin, natives and prelude, sorted by code point.
    static readonly Lazy<JqValue> builtins = new(ListBuiltins);

    public static void Register()
    {
        Add("empty", 0, static (_, _, _, _, _, _) => null);
        Add("error", 0, static (_, input, _, out result, out error) => Fail(input, out result, out error));
        Add("type", 0, static (_, input, _, out result, out error) => Ok(JqValue.FromString(input.TypeName), out result, out error));
        Add("length", 0, Length);
        Add("utf8bytelength", 0, Utf8ByteLength);
        Add("builtins", 0, static (_, _, _, out result, out error) => Ok(builtins.Value, out result, out error));
        Add("halt", 0, static (_, _, _, _, _, _) => Signal.Halt(null, 0));
        Add("halt_error", 1, HaltError);
        Add("last", 1, Last);
        Add("infinite", 0, static (_, _, _, out result, out error) => Ok(JqValue.FromNumber(double.PositiveInfinity), out result, out error));
        Add("nan", 0, static (_, _, _, out result, out error) => Ok(JqValue.FromNumber(double.NaN), out result, out error));
        Add("isinfinite", 0, static (_, input, _, out result, out error) => Ok(JqValue.FromBool(input.Kind == JqKind.Number && double.IsInfinity(input.AsNumber())), out result, out error));
        Add("isnan", 0, static (_, input, _, out result, out error) => Ok(JqValue.FromBool(input.Kind == JqKind.Number && double.IsNaN(input.AsNumber())), out result, out error));
        Add("isnormal", 0, static (_, input, _, out result, out error) => Ok(JqValue.FromBool(input.Kind == JqKind.Number && double.IsNormal(input.AsNumber())), out result, out error));
    }

    static bool Length(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        switch (input.Kind)
        {
            case JqKind.Null:
                return Ok(JqValue.FromNumber(0), out result, out error);
            case JqKind.Number:
                return Ok(JqValue.FromNumber(Math.Abs(input.AsNumber())), out result, out error);
            case JqKind.String:
                return Ok(JqValue.FromNumber(CodePoints.Length(input.AsString())), out result, out error);
            case JqKind.Array:
                return Ok(JqValue.FromNumber(input.AsArray().Count), out result, out error);
            case JqKind.Object:
                return Ok(JqValue.FromNumber(input.AsObject().Count), out result, out error);
            default:
                return Fail(Errors.HasNoLength(input), out result, out error);
        }
    }

    static bool Utf8ByteLength(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
        => input.Kind == JqKind.String
            ? Ok(JqValue.FromNumber(Encoding.UTF8.GetByteCount(input.AsString())), out result, out error)
            : Fail(Errors.OnlyStringsHaveUtf8Length(input), out result, out error);

    // halt_error(c): halts with the input and trunc(c) for the first output c.
    static Signal? HaltError(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
    {
        var first = Signal.Break(interpreter.NewLabel());
        var code = JqValue.Null;
        var signal = interpreter.Eval(arguments[0], env, input, null, (value, _) =>
        {
            code = value;
            return first;
        });
        if (!ReferenceEquals(signal, first))
            return signal;
        if (code.Kind != JqKind.Number)
            return Interpreter.Fail(Errors.HaltErrorNumberRequired(input));
        var number = Math.Truncate(code.AsNumber());
        return Signal.Halt(input, double.IsNaN(number) ? 0 : (int)Math.Clamp(number, int.MinValue, int.MaxValue));
    }

    // last(f): the last output of f, with its path. *loop*
    static Signal? Last(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
    {
        var has = false;
        var last = JqValue.Null;
        Path? lastAt = null;
        var signal = interpreter.Eval(arguments[0], env, input, path, (value, at) =>
        {
            has = true;
            last = value;
            lastAt = at;
            return null;
        });
        return signal ?? (has ? Interpreter.Output(emit, last, lastAt) : null);
    }

    static JqValue ListBuiltins()
    {
        var names = new List<string>();
        foreach (var native in Natives.All)
        {
            if (!native.IsInternal)
                names.Add(native.ToString());
        }

        foreach (var function in Prelude.Instance.Definitions)
        {
            if (!function.Name.StartsWith('_'))
                names.Add(function.ToString());
        }

        names.Sort(CodePoints.Compare);
        var items = new JqValue[names.Count];
        for (var i = 0; i < items.Length; i++)
            items[i] = JqValue.FromString(names[i]);
        return JqValue.FromArray(JqArray.FromOwned(items));
    }
}
