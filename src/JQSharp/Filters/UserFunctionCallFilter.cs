using System.Text.Json;

namespace Devlooped;

public sealed class UserFunctionCallFilter(UserFunctionDef funcDef, JqFilter[] args) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        var bodyEnv = env;
        for (var i = 0; i < funcDef.ParamNames.Length; i++)
            bodyEnv = bodyEnv.BindFilter(funcDef.ParamNames[i], new FilterClosure(args[i], env));

        return funcDef.Body!.Evaluate(input, bodyEnv);
    }
}
