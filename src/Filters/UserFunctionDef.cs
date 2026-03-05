using System.Text.Json;

namespace Devlooped;

public sealed class UserFunctionDef
{
    public string Name { get; }
    public string[] ParamNames { get; }
    public JqFilter? Body { get; set; }
    public int Arity => ParamNames.Length;

    public UserFunctionDef(string name, string[] paramNames)
    {
        Name = name;
        ParamNames = paramNames;
        Body = null;
    }
}
