using JQLite.Core.Binding;
using JQLite.Core.Runtime;
using JQLite.Core.Values;
using static JQLite.Core.Builtins.NativeTable;

namespace JQLite.Core.Builtins;

/// <summary>
/// <c>modulemeta/0</c> (§9.11), the native of the modules group: a port without the group (see
/// Binding/Modules.cs) leaves this file out and does not list it.
/// </summary>
static class ModuleNatives
{
    public static void Register() => Add("modulemeta", 0, Modulemeta);

    // The description of the module the input names, resolved as from the main program with the
    // source the program was bound with: what the loader recorded when the program loaded it.
    static bool Modulemeta(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        if (input.Kind != JqKind.String)
            return Fail(Errors.ModulemetaNotString, out result, out error);
        var path = input.AsString();
        if (interpreter.Program.Modules is not { } modules)
            return Fail(JqValue.FromString(BindingMessages.ModuleNotFound(path)), out result, out error);
        return modules.TryDescribe(path, out var description, out var failure)
            ? Ok(description, out result, out error)
            : Fail(JqValue.FromString(failure), out result, out error);
    }
}
