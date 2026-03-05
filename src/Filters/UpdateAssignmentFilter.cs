using System.Text.Json;

namespace Devlooped;

internal sealed class UpdateAssignmentFilter(JqFilter pathExpr, JqFilter updateExpr) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        var paths = PathResolver.GetPaths(pathExpr, input, env).ToList();
        var result = input;
        var deletePaths = new List<JsonElement[]>();

        foreach (var path in paths)
        {
            if (!PathResolver.TryGetPathValue(result, path, out var currentValue))
                currentValue = CreateNullElement();

            using var enumerator = updateExpr.Evaluate(currentValue, env).GetEnumerator();
            if (enumerator.MoveNext())
                result = PathResolver.SetPathValue(result, path, enumerator.Current);
            else
                deletePaths.Add(path);
        }

        if (deletePaths.Count > 0)
        {
            deletePaths.Sort(static (a, b) =>
            {
                if (a.Length != b.Length)
                    return b.Length.CompareTo(a.Length);

                for (var i = 0; i < a.Length; i++)
                {
                    if (a[i].ValueKind == JsonValueKind.Number && b[i].ValueKind == JsonValueKind.Number)
                    {
                        var cmp = b[i].GetDouble().CompareTo(a[i].GetDouble());
                        if (cmp != 0)
                            return cmp;
                    }
                }

                return 0;
            });

            foreach (var path in deletePaths)
                result = PathResolver.DeletePathValue(result, path);
        }

        yield return result;
    }
}

