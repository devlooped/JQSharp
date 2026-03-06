using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Devlooped;

public sealed class BuiltinFilter : JqFilter
{
    static readonly HashSet<string> builtinNames = new(StringComparer.Ordinal)
    {
        "empty",
        "type", "length", "utf8bytelength", "infinite", "nan",
        "isinfinite", "isnan", "isfinite", "isnormal",
        "arrays", "objects", "iterables", "booleans", "numbers",
        "normals", "finites", "strings", "nulls", "values", "scalars",
        "keys", "keys_unsorted", "reverse", "sort", "unique",
        "flatten", "add", "any", "all", "min", "max",
        "to_entries", "from_entries", "paths", "transpose", "combinations",
        "tonumber", "tostring", "tojson", "fromjson",
        "explode", "implode", "ascii_downcase", "ascii_upcase",
        "abs", "floor", "sqrt",
        "acos", "acosh", "asin", "asinh", "atan", "atanh", "cbrt", "ceil",
        "cos", "cosh", "erf", "erfc", "exp", "exp2", "expm1", "fabs",
        "log", "log10", "log2", "logb", "log1p",
        "nearbyint", "round", "sin", "sinh", "tan", "tanh", "trunc",
        "tgamma", "lgamma", "j0", "j1",
        "modf", "frexp",
        "recurse", "halt", "error", "env", "builtins",
        "first", "last",
        "not",
        "now", "todate", "todateiso8601", "fromdate", "fromdateiso8601", "gmtime", "localtime", "mktime",
    };

    readonly string name;

    public BuiltinFilter(string name)
    {
        this.name = name;
    }

    public static bool IsBuiltin(string name) => builtinNames.Contains(name);

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        return name switch
        {
            "empty" => EvaluateEmpty(),
            "type" => EvaluateType(input),
            "length" => EvaluateLength(input),
            "utf8bytelength" => EvaluateUtf8ByteLength(input),
            "infinite" => EvaluateInfinite(),
            "nan" => EvaluateNan(),
            "isinfinite" => EvaluateIsInfinite(input),
            "isnan" => EvaluateIsNan(input),
            "isfinite" => EvaluateIsFinite(input),
            "isnormal" => EvaluateIsNormal(input),
            "arrays" => EvaluateTypeSelector(input, JsonValueKind.Array),
            "objects" => EvaluateTypeSelector(input, JsonValueKind.Object),
            "iterables" => EvaluateIterables(input),
            "booleans" => EvaluateBooleans(input),
            "numbers" => EvaluateNumbers(input),
            "normals" => EvaluateNormals(input),
            "finites" => EvaluateFinites(input),
            "strings" => EvaluateTypeSelector(input, JsonValueKind.String),
            "nulls" => EvaluateTypeSelector(input, JsonValueKind.Null),
            "values" => EvaluateValues(input),
            "scalars" => EvaluateScalars(input),
            "keys" => EvaluateKeys(input, sorted: true),
            "keys_unsorted" => EvaluateKeys(input, sorted: false),
            "reverse" => EvaluateReverse(input),
            "sort" => EvaluateSort(input),
            "unique" => EvaluateUnique(input),
            "flatten" => EvaluateFlatten(input, int.MaxValue),
            "add" => EvaluateAdd(input),
            "any" => EvaluateAny(input),
            "all" => EvaluateAll(input),
            "min" => EvaluateMin(input),
            "max" => EvaluateMax(input),
            "to_entries" => EvaluateToEntries(input),
            "from_entries" => EvaluateFromEntries(input),
            "paths" => EvaluatePaths(input),
            "transpose" => EvaluateTranspose(input),
            "combinations" => EvaluateCombinations(input),
            "tonumber" => EvaluateToNumber(input),
            "tostring" => EvaluateToString(input),
            "tojson" => EvaluateToJson(input),
            "fromjson" => EvaluateFromJson(input),
            "explode" => EvaluateExplode(input),
            "implode" => EvaluateImplode(input),
            "ascii_downcase" => EvaluateAsciiDowncase(input),
            "ascii_upcase" => EvaluateAsciiUpcase(input),
            "abs" => EvaluateAbs(input),
            "floor" => EvaluateFloor(input),
            "sqrt" => EvaluateSqrt(input),
            "acos" => EvaluateMathUnary(input, Math.Acos),
            "acosh" => EvaluateMathUnary(input, Math.Acosh),
            "asin" => EvaluateMathUnary(input, Math.Asin),
            "asinh" => EvaluateMathUnary(input, Math.Asinh),
            "atan" => EvaluateMathUnary(input, Math.Atan),
            "atanh" => EvaluateMathUnary(input, Math.Atanh),
            "cbrt" => EvaluateMathUnary(input, Math.Cbrt),
            "ceil" => EvaluateMathUnary(input, Math.Ceiling),
            "cos" => EvaluateMathUnary(input, Math.Cos),
            "cosh" => EvaluateMathUnary(input, Math.Cosh),
            "erf" => EvaluateMathUnary(input, MathExtra.Erf),
            "erfc" => EvaluateMathUnary(input, MathExtra.Erfc),
            "exp" => EvaluateMathUnary(input, Math.Exp),
            "exp2" => EvaluateMathUnary(input, static x => Math.Pow(2, x)),
            "expm1" => EvaluateMathUnary(input, static x => Math.Exp(x) - 1),
            "fabs" => EvaluateMathUnary(input, Math.Abs),
            "log" => EvaluateMathUnary(input, Math.Log),
            "log10" => EvaluateMathUnary(input, Math.Log10),
            "log2" => EvaluateMathUnary(input, Math.Log2),
            "logb" => EvaluateLogb(input),
            "log1p" => EvaluateMathUnary(input, static x => Math.Log(1 + x)),
            "nearbyint" => EvaluateMathUnary(input, static x => Math.Round(x, MidpointRounding.ToEven)),
            "round" => EvaluateMathUnary(input, static x => Math.Round(x, MidpointRounding.AwayFromZero)),
            "sin" => EvaluateMathUnary(input, Math.Sin),
            "sinh" => EvaluateMathUnary(input, Math.Sinh),
            "tan" => EvaluateMathUnary(input, Math.Tan),
            "tanh" => EvaluateMathUnary(input, Math.Tanh),
            "trunc" => EvaluateMathUnary(input, Math.Truncate),
            "tgamma" => EvaluateMathUnary(input, MathExtra.TGamma),
            "lgamma" => EvaluateMathUnary(input, MathExtra.LGamma),
            "j0" => EvaluateMathUnary(input, MathExtra.BesselJ0),
            "j1" => EvaluateMathUnary(input, MathExtra.BesselJ1),
            "modf" => EvaluateModf(input),
            "frexp" => EvaluateFrexp(input),
            "recurse" => EvaluateRecurse(input),
            "halt" => throw new JqHaltException(0),
            "error" => throw new JqException(input),
            "env" => EvaluateEnv(),
            "builtins" => EvaluateBuiltins(),
            "first" => EvaluateFirst(input),
            "last" => EvaluateLast(input),
            "now" => EvaluateNow(),
            "todate" or "todateiso8601" => EvaluateToDateIso8601(input),
            "fromdate" or "fromdateiso8601" => EvaluateFromDateIso8601(input),
            "gmtime" => EvaluateGmtime(input),
            "localtime" => EvaluateLocaltime(input),
            "mktime" => EvaluateMktime(input),
            _ => throw new JqException($"Unknown builtin '{name}'."),
        };
    }

    // Generator
    static IEnumerable<JsonElement> EvaluateEmpty()
    {
        yield break;
    }

    // Type introspection
    static IEnumerable<JsonElement> EvaluateType(JsonElement input)
    {
        yield return CreateStringElement(GetTypeName(input));
    }

    static IEnumerable<JsonElement> EvaluateLength(JsonElement input)
    {
        switch (input.ValueKind)
        {
            case JsonValueKind.Null:
                yield return CreateNumberElement(0);
                break;
            case JsonValueKind.String:
                // jq counts Unicode codepoints
                var str = input.GetString() ?? string.Empty;
                var codepoints = 0;
                for (var i = 0; i < str.Length; i++)
                {
                    codepoints++;
                    if (char.IsHighSurrogate(str[i]) && i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
                        i++;
                }
                yield return CreateNumberElement(codepoints);
                break;
            case JsonValueKind.Array:
                yield return CreateNumberElement(input.GetArrayLength());
                break;
            case JsonValueKind.Object:
                var count = 0;
                foreach (var _ in input.EnumerateObject())
                    count++;
                yield return CreateNumberElement(count);
                break;
            case JsonValueKind.Number:
                // length of a number is its absolute value
                yield return CreateNumberElement(Math.Abs(input.GetDouble()));
                break;
            default:
                throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) has no length");
        }
    }

    static IEnumerable<JsonElement> EvaluateUtf8ByteLength(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) only strings have UTF-8 byte length");

        var str = input.GetString() ?? string.Empty;
        yield return CreateNumberElement(Encoding.UTF8.GetByteCount(str));
    }

    static IEnumerable<JsonElement> EvaluateInfinite()
    {
        // jq outputs 1.7976931348623157e+308 for infinite
        yield return CreateNumberElement(double.MaxValue);
    }

    static IEnumerable<JsonElement> EvaluateNan()
    {
        // JSON cannot represent NaN; jq outputs null
        yield return CreateNullElement();
    }

    static IEnumerable<JsonElement> EvaluateIsInfinite(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Number)
        {
            yield return CreateBooleanElement(false);
            yield break;
        }
        var val = input.GetDouble();
        yield return CreateBooleanElement(double.IsInfinity(val) || val == double.MaxValue || val == double.MinValue);
    }

    static IEnumerable<JsonElement> EvaluateIsNan(JsonElement input)
    {
        // Since we represent nan as null, check for null from nan context
        if (input.ValueKind == JsonValueKind.Null)
        {
            yield return CreateBooleanElement(true);
            yield break;
        }
        if (input.ValueKind == JsonValueKind.Number)
        {
            yield return CreateBooleanElement(double.IsNaN(input.GetDouble()));
            yield break;
        }
        yield return CreateBooleanElement(false);
    }

    static IEnumerable<JsonElement> EvaluateIsFinite(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Number)
        {
            // jq: nan | isfinite → true (nan is represented as null for us)
            // null | isfinite → true in jq (since nan | isfinite is true)
            if (input.ValueKind == JsonValueKind.Null)
            {
                yield return CreateBooleanElement(true);
                yield break;
            }
            yield return CreateBooleanElement(false);
            yield break;
        }
        var val = input.GetDouble();
        var isInf = double.IsInfinity(val) || val == double.MaxValue || val == double.MinValue;
        yield return CreateBooleanElement(!isInf);
    }

    static IEnumerable<JsonElement> EvaluateIsNormal(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Number)
        {
            yield return CreateBooleanElement(false);
            yield break;
        }
        var val = input.GetDouble();
        yield return CreateBooleanElement(double.IsNormal(val));
    }

    // Type selectors
    static IEnumerable<JsonElement> EvaluateTypeSelector(JsonElement input, JsonValueKind kind)
    {
        if (input.ValueKind == kind)
            yield return input;
    }

    static IEnumerable<JsonElement> EvaluateIterables(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            yield return input;
    }

    static IEnumerable<JsonElement> EvaluateBooleans(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.True or JsonValueKind.False)
            yield return input;
    }

    static IEnumerable<JsonElement> EvaluateNumbers(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Number)
            yield return input;
    }

    static IEnumerable<JsonElement> EvaluateNormals(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Number && double.IsNormal(input.GetDouble()))
            yield return input;
    }

    static IEnumerable<JsonElement> EvaluateFinites(JsonElement input)
    {
        // jq: finites passes through values that are finite (including null representing nan)
        if (input.ValueKind == JsonValueKind.Null)
        {
            yield return input;
            yield break;
        }
        if (input.ValueKind == JsonValueKind.Number)
        {
            var val = input.GetDouble();
            var isInf = double.IsInfinity(val) || val == double.MaxValue || val == double.MinValue;
            if (!isInf)
                yield return input;
        }
    }

    static IEnumerable<JsonElement> EvaluateValues(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Null)
            yield return input;
    }

    static IEnumerable<JsonElement> EvaluateScalars(JsonElement input)
    {
        if (input.ValueKind is not JsonValueKind.Array and not JsonValueKind.Object)
            yield return input;
    }

    // Object/Array builtins
    static IEnumerable<JsonElement> EvaluateKeys(JsonElement input, bool sorted)
    {
        if (input.ValueKind == JsonValueKind.Array)
        {
            var length = input.GetArrayLength();
            yield return CreateElement(writer =>
            {
                writer.WriteStartArray();
                for (var i = 0; i < length; i++)
                    writer.WriteNumberValue(i);
                writer.WriteEndArray();
            });
            yield break;
        }

        if (input.ValueKind == JsonValueKind.Object)
        {
            var keys = input.EnumerateObject().Select(p => p.Name);
            if (sorted)
                keys = keys.OrderBy(k => k, StringComparer.Ordinal);

            var keyList = keys.ToList();
            yield return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var key in keyList)
                    writer.WriteStringValue(key);
                writer.WriteEndArray();
            });
            yield break;
        }

        throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) has no keys");
    }

    static IEnumerable<JsonElement> EvaluateReverse(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be reversed");

        var items = input.EnumerateArray().ToArray();
        Array.Reverse(items);
        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var item in items)
                item.WriteTo(writer);
            writer.WriteEndArray();
        });
    }

    static IEnumerable<JsonElement> EvaluateSort(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be sorted");

        var items = input.EnumerateArray().ToArray();
        Array.Sort(items, CompareElements);
        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var item in items)
                item.WriteTo(writer);
            writer.WriteEndArray();
        });
    }

    static IEnumerable<JsonElement> EvaluateUnique(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var items = input.EnumerateArray().ToArray();
        Array.Sort(items, CompareElements);

        var unique = new List<JsonElement>();
        foreach (var item in items)
        {
            if (unique.Count == 0 || !StructurallyEqual(unique[^1], item))
                unique.Add(item);
        }

        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var item in unique)
                item.WriteTo(writer);
            writer.WriteEndArray();
        });
    }

    static IEnumerable<JsonElement> EvaluateFlatten(JsonElement input, int depth)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be flattened");

        var result = new List<JsonElement>();
        FlattenRecursive(input, depth, result);
        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var item in result)
                item.WriteTo(writer);
            writer.WriteEndArray();
        });
    }

    static void FlattenRecursive(JsonElement array, int depth, List<JsonElement> result)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (depth > 0 && item.ValueKind == JsonValueKind.Array)
                FlattenRecursive(item, depth - 1, result);
            else
                result.Add(item);
        }
    }

    static IEnumerable<JsonElement> EvaluateAdd(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var items = input.EnumerateArray().ToArray();
        if (items.Length == 0)
        {
            yield return CreateNullElement();
            yield break;
        }

        var accumulator = items[0];
        for (var i = 1; i < items.Length; i++)
            accumulator = AddValues(accumulator, items[i]);

        yield return accumulator;
    }

    static JsonElement AddValues(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Null)
            return right;
        if (right.ValueKind == JsonValueKind.Null)
            return left;

        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            return CreateNumberElement(left.GetDouble() + right.GetDouble());

        if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
            return CreateStringElement((left.GetString() ?? "") + (right.GetString() ?? ""));

        if (left.ValueKind == JsonValueKind.Array && right.ValueKind == JsonValueKind.Array)
        {
            return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var item in left.EnumerateArray())
                    item.WriteTo(writer);
                foreach (var item in right.EnumerateArray())
                    item.WriteTo(writer);
                writer.WriteEndArray();
            });
        }

        if (left.ValueKind == JsonValueKind.Object && right.ValueKind == JsonValueKind.Object)
        {
            return CreateElement(writer =>
            {
                writer.WriteStartObject();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                // right side wins on conflicts (later entries override)
                foreach (var prop in right.EnumerateObject())
                    seen.Add(prop.Name);

                foreach (var prop in left.EnumerateObject())
                {
                    if (!seen.Contains(prop.Name))
                    {
                        writer.WritePropertyName(prop.Name);
                        prop.Value.WriteTo(writer);
                    }
                }
                foreach (var prop in right.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            });
        }

        throw new JqException(
            $"{GetTypeName(left)} ({GetValueText(left)}) and {GetTypeName(right)} ({GetValueText(right)}) cannot be added");
    }

    static IEnumerable<JsonElement> EvaluateAny(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        foreach (var item in input.EnumerateArray())
        {
            if (IsTruthy(item))
            {
                yield return CreateBooleanElement(true);
                yield break;
            }
        }
        yield return CreateBooleanElement(false);
    }

    static IEnumerable<JsonElement> EvaluateAll(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        foreach (var item in input.EnumerateArray())
        {
            if (!IsTruthy(item))
            {
                yield return CreateBooleanElement(false);
                yield break;
            }
        }
        yield return CreateBooleanElement(true);
    }

    static IEnumerable<JsonElement> EvaluateMin(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var items = input.EnumerateArray().ToArray();
        if (items.Length == 0)
        {
            yield return CreateNullElement();
            yield break;
        }

        var min = items[0];
        for (var i = 1; i < items.Length; i++)
        {
            if (CompareElements(items[i], min) < 0)
                min = items[i];
        }
        yield return min;
    }

    static IEnumerable<JsonElement> EvaluateMax(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var items = input.EnumerateArray().ToArray();
        if (items.Length == 0)
        {
            yield return CreateNullElement();
            yield break;
        }

        var max = items[0];
        for (var i = 1; i < items.Length; i++)
        {
            if (CompareElements(items[i], max) > 0)
                max = items[i];
        }
        yield return max;
    }

    static IEnumerable<JsonElement> EvaluateToEntries(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an object");

        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var prop in input.EnumerateObject())
            {
                writer.WriteStartObject();
                writer.WriteString("key", prop.Name);
                writer.WritePropertyName("value");
                prop.Value.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
    }

    static IEnumerable<JsonElement> EvaluateFromEntries(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        yield return CreateElement(writer =>
        {
            writer.WriteStartObject();
            foreach (var entry in input.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    throw new JqException($"Cannot convert {GetTypeName(entry)} to entry");

                // Accept "key", "Key", "name", or "Name" as the key field
                string? key = null;
                if (entry.TryGetProperty("key", out var keyProp))
                    key = keyProp.ValueKind == JsonValueKind.String ? keyProp.GetString() : keyProp.GetRawText();
                else if (entry.TryGetProperty("Key", out keyProp))
                    key = keyProp.ValueKind == JsonValueKind.String ? keyProp.GetString() : keyProp.GetRawText();
                else if (entry.TryGetProperty("name", out keyProp))
                    key = keyProp.ValueKind == JsonValueKind.String ? keyProp.GetString() : keyProp.GetRawText();
                else if (entry.TryGetProperty("Name", out keyProp))
                    key = keyProp.ValueKind == JsonValueKind.String ? keyProp.GetString() : keyProp.GetRawText();

                if (key == null)
                    throw new JqException("Expected object with key/name field");

                writer.WritePropertyName(key);
                if (entry.TryGetProperty("value", out var valueProp))
                    valueProp.WriteTo(writer);
                else
                    writer.WriteNullValue();
            }
            writer.WriteEndObject();
        });
    }

    static IEnumerable<JsonElement> EvaluatePaths(JsonElement input)
    {
        foreach (var path in EnumeratePaths(input))
            yield return path;
    }

    static IEnumerable<JsonElement> EnumeratePaths(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in input.EnumerateArray())
            {
                var currentIndex = index;
                yield return CreateElement(writer =>
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(currentIndex);
                    writer.WriteEndArray();
                });

                foreach (var subPath in EnumeratePaths(item))
                {
                    var capturedIndex = currentIndex;
                    var capturedSubPath = subPath;
                    yield return CreateElement(writer =>
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(capturedIndex);
                        foreach (var component in capturedSubPath.EnumerateArray())
                            component.WriteTo(writer);
                        writer.WriteEndArray();
                    });
                }
                index++;
            }
        }
        else if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in input.EnumerateObject())
            {
                var propName = prop.Name;
                yield return CreateElement(writer =>
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(propName);
                    writer.WriteEndArray();
                });

                foreach (var subPath in EnumeratePaths(prop.Value))
                {
                    var capturedName = propName;
                    var capturedSubPath = subPath;
                    yield return CreateElement(writer =>
                    {
                        writer.WriteStartArray();
                        writer.WriteStringValue(capturedName);
                        foreach (var component in capturedSubPath.EnumerateArray())
                            component.WriteTo(writer);
                        writer.WriteEndArray();
                    });
                }
            }
        }
    }

    static IEnumerable<JsonElement> EvaluateTranspose(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be transposed");

        var rows = input.EnumerateArray().ToArray();
        if (rows.Length == 0)
        {
            yield return CreateElement(static writer => writer.WriteStartArray());
            yield break;
        }

        var maxLen = 0;
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Array)
                throw new JqException($"{GetTypeName(row)} ({GetValueText(row)}) is not an array");
            var len = row.GetArrayLength();
            if (len > maxLen) maxLen = len;
        }

        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            for (var col = 0; col < maxLen; col++)
            {
                writer.WriteStartArray();
                foreach (var row in rows)
                {
                    var arr = row.EnumerateArray().ToArray();
                    if (col < arr.Length)
                        arr[col].WriteTo(writer);
                    else
                        writer.WriteNullValue();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        });
    }

    static IEnumerable<JsonElement> EvaluateCombinations(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var arrays = new List<JsonElement[]>();
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array)
                throw new JqException($"Cannot iterate over {GetTypeName(item)} ({GetValueText(item)})");
            arrays.Add([.. item.EnumerateArray()]);
        }

        if (arrays.Count == 0)
        {
            yield return CreateElement(static writer =>
            {
                writer.WriteStartArray();
                writer.WriteEndArray();
            });
            yield break;
        }

        foreach (var combo in CartesianProduct(arrays, 0))
            yield return combo;
    }

    static IEnumerable<JsonElement> CartesianProduct(List<JsonElement[]> arrays, int index)
    {
        if (index == arrays.Count)
        {
            yield return CreateElement(static writer =>
            {
                writer.WriteStartArray();
                writer.WriteEndArray();
            });
            yield break;
        }

        foreach (var item in arrays[index])
        {
            foreach (var rest in CartesianProduct(arrays, index + 1))
            {
                var capturedItem = item;
                var capturedRest = rest;
                yield return CreateElement(writer =>
                {
                    writer.WriteStartArray();
                    capturedItem.WriteTo(writer);
                    foreach (var r in capturedRest.EnumerateArray())
                        r.WriteTo(writer);
                    writer.WriteEndArray();
                });
            }
        }
    }

    // Conversion builtins
    static IEnumerable<JsonElement> EvaluateToNumber(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Number)
        {
            yield return input;
            yield break;
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            var str = input.GetString() ?? string.Empty;
            if (double.TryParse(str, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var num))
            {
                yield return CreateNumberElement(num);
                yield break;
            }
            throw new JqException($"Invalid numeric literal \"{str}\"");
        }

        throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be converted to number");
    }

    static IEnumerable<JsonElement> EvaluateToString(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
        {
            yield return input;
            yield break;
        }

        // For all other types, serialize to JSON string representation
        yield return CreateStringElement(JsonSerializer.Serialize(input));
    }

    static IEnumerable<JsonElement> EvaluateToJson(JsonElement input)
    {
        yield return CreateStringElement(JsonSerializer.Serialize(input));
    }

    static IEnumerable<JsonElement> EvaluateFromJson(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be parsed as JSON");

        var str = input.GetString() ?? string.Empty;
        using var doc = JsonDocument.Parse(str);
        yield return doc.RootElement.Clone();
    }

    static IEnumerable<JsonElement> EvaluateExplode(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be exploded");

        var str = input.GetString() ?? string.Empty;
        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            for (var i = 0; i < str.Length; i++)
            {
                int codepoint;
                if (char.IsHighSurrogate(str[i]) && i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
                {
                    codepoint = char.ConvertToUtf32(str[i], str[i + 1]);
                    i++;
                }
                else
                {
                    codepoint = str[i];
                }
                writer.WriteNumberValue(codepoint);
            }
            writer.WriteEndArray();
        });
    }

    static IEnumerable<JsonElement> EvaluateImplode(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be imploded");

        var sb = new StringBuilder();
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number)
                throw new JqException($"Cannot implode non-numeric value");

            var codepoint = (int)item.GetDouble();
            sb.Append(char.ConvertFromUtf32(codepoint));
        }
        yield return CreateStringElement(sb.ToString());
    }

    static IEnumerable<JsonElement> EvaluateAsciiDowncase(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be ascii_downcased");

        var str = input.GetString() ?? string.Empty;
        yield return CreateStringElement(str.ToLowerInvariant());
    }

    static IEnumerable<JsonElement> EvaluateAsciiUpcase(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be ascii_upcased");

        var str = input.GetString() ?? string.Empty;
        yield return CreateStringElement(str.ToUpperInvariant());
    }

    // Math builtins
    static IEnumerable<JsonElement> EvaluateAbs(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Number)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be negated");

        yield return CreateNumberElement(Math.Abs(input.GetDouble()));
    }

    static IEnumerable<JsonElement> EvaluateFloor(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Number)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be floored");

        yield return CreateNumberElement(Math.Floor(input.GetDouble()));
    }

    static IEnumerable<JsonElement> EvaluateSqrt(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Number)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be sqrt'd");

        yield return CreateNumberElement(Math.Sqrt(input.GetDouble()));
    }

    // Phase 13: Math functions
    static IEnumerable<JsonElement> EvaluateMathUnary(JsonElement input, Func<double, double> fn)
    {
        RequireNumber(input);
        yield return CreateMathResult(fn(input.GetDouble()));
    }

    static IEnumerable<JsonElement> EvaluateLogb(JsonElement input)
    {
        RequireNumber(input);
        var x = input.GetDouble();
        if (x == 0)
            yield return CreateMathResult(double.NegativeInfinity);
        else
            yield return CreateMathResult(Math.ILogB(x));
    }

    static IEnumerable<JsonElement> EvaluateModf(JsonElement input)
    {
        RequireNumber(input);
        var x = input.GetDouble();
        var intPart = Math.Truncate(x);
        var fracPart = x - intPart;
        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            WriteNumber(writer, fracPart);
            WriteNumber(writer, intPart);
            writer.WriteEndArray();
        });
    }

    static IEnumerable<JsonElement> EvaluateFrexp(JsonElement input)
    {
        RequireNumber(input);
        var x = input.GetDouble();
        if (x == 0)
        {
            yield return CreateElement(static writer =>
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(0);
                writer.WriteNumberValue(0);
                writer.WriteEndArray();
            });
            yield break;
        }
        if (double.IsNaN(x) || double.IsInfinity(x))
        {
            yield return CreateElement(writer =>
            {
                writer.WriteStartArray();
                if (double.IsNaN(x))
                    writer.WriteNullValue();
                else
                    writer.WriteNumberValue(x > 0 ? double.MaxValue : -double.MaxValue);
                writer.WriteNumberValue(0);
                writer.WriteEndArray();
            });
            yield break;
        }
        var exponent = Math.ILogB(x) + 1;
        var significand = x * Math.Pow(2, -exponent);
        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            WriteNumber(writer, significand);
            WriteNumber(writer, exponent);
            writer.WriteEndArray();
        });
    }

    static void WriteNumber(Utf8JsonWriter writer, double value)
    {
        if (value >= long.MinValue && value <= long.MaxValue && Math.Floor(value) == value)
            writer.WriteNumberValue((long)value);
        else
            writer.WriteNumberValue(value);
    }

    // Other builtins
    static IEnumerable<JsonElement> EvaluateRecurse(JsonElement input)
    {
        return Traverse(input);
    }

    static IEnumerable<JsonElement> Traverse(JsonElement current)
    {
        yield return current;

        if (current.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in current.EnumerateArray())
            {
                foreach (var nested in Traverse(element))
                    yield return nested;
            }
        }
        else if (current.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in current.EnumerateObject())
            {
                foreach (var nested in Traverse(property.Value))
                    yield return nested;
            }
        }
    }

    static IEnumerable<JsonElement> EvaluateEnv()
    {
        var envVars = Environment.GetEnvironmentVariables();
        yield return CreateElement(writer =>
        {
            writer.WriteStartObject();
            foreach (System.Collections.DictionaryEntry entry in envVars)
            {
                writer.WriteString(entry.Key?.ToString() ?? "", entry.Value?.ToString() ?? "");
            }
            writer.WriteEndObject();
        });
    }

    static IEnumerable<JsonElement> EvaluateBuiltins()
    {
        var names = builtinNames
            .Select(static name => $"{name}/0")
            .Concat(ParameterizedFilter.KnownBuiltinArities)
            .Concat(FormatFilter.FormatNames.Select(static name => $"@{name}/0"))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var name in names)
                writer.WriteStringValue(name);
            writer.WriteEndArray();
        });
    }

    static IEnumerable<JsonElement> EvaluateFirst(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        if (input.GetArrayLength() == 0)
            yield break;

        yield return input.EnumerateArray().First();
    }

    static IEnumerable<JsonElement> EvaluateLast(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        if (input.GetArrayLength() == 0)
            yield break;

        yield return input.EnumerateArray().Last();
    }

    static IEnumerable<JsonElement> EvaluateNow()
    {
        yield return CreateNumberElement(ToUnixTimestamp(DateTimeOffset.UtcNow));
    }

    static IEnumerable<JsonElement> EvaluateToDateIso8601(JsonElement input)
    {
        RequireNumber(input);
        var dt = FromUnixTimestamp(input.GetDouble());
        yield return CreateStringElement(dt.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
    }

    static IEnumerable<JsonElement> EvaluateFromDateIso8601(JsonElement input)
    {
        RequireString(input);
        var str = input.GetString() ?? "";
        if (!DateTimeOffset.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            throw new JqException($"date \"{str}\" does not match format \"%Y-%m-%dT%H:%M:%SZ\"");
        yield return CreateNumberElement(ToUnixTimestamp(dt));
    }

    static IEnumerable<JsonElement> EvaluateGmtime(JsonElement input)
    {
        RequireNumber(input);
        var dt = FromUnixTimestamp(input.GetDouble());
        yield return CreateBrokenDownTime(dt);
    }

    static IEnumerable<JsonElement> EvaluateLocaltime(JsonElement input)
    {
        RequireNumber(input);
        var dt = FromUnixTimestamp(input.GetDouble()).ToLocalTime();
        yield return CreateBrokenDownTime(dt);
    }

    static IEnumerable<JsonElement> EvaluateMktime(JsonElement input)
    {
        var dt = ParseBrokenDownTime(input);
        yield return CreateNumberElement(ToUnixTimestamp(dt));
    }

}

