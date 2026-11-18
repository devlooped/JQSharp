using System.Text;
using System.Text.Json;
using Devlooped.Core.Values;

namespace Devlooped.Json;

/// <summary>
/// Converts between <see cref="JsonElement"/> and the core's <see cref="JqValue"/>: the only place
/// with conversion logic on System.Text.Json. No serializer and no reflection, so the library
/// stays trimmable and AOT-compatible.
/// </summary>
static class JsonElementAdapter
{
    // Outputs are read back by the reader below; the writer of the core writes up to 10,001 levels (spec §5.1).
    static readonly JsonReaderOptions readerOptions = new() { MaxDepth = 10_002 };

    /// <summary>
    /// The value of a JSON element: numbers as doubles, strings with lone surrogates replaced by
    /// U+FFFD (spec §4.1), objects in their key order, a repeated key replacing the earlier value at
    /// its position (spec §5.3). Containers are walked with an explicit stack: any depth works.
    /// </summary>
    public static JqValue ToValue(JsonElement element)
    {
        if (element.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            return Scalar(element);

        var stack = new List<Container> { new(element) };
        while (true)
        {
            var top = stack[^1];
            if (!top.TryNext(out var next))
            {
                var done = top.ToValue();
                stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0)
                    return done;
                stack[^1].Add(done);
            }
            else if (next.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                stack.Add(new Container(next));
            }
            else
            {
                top.Add(Scalar(next));
            }
        }
    }

    /// <summary>
    /// A JSON element whose raw text is the canonical text of <paramref name="value"/> (spec §5.1:
    /// NaN is <c>null</c>, ±Infinity is ±1.7976931348623157e+308), detached from any pooled document,
    /// so it stays valid after the evaluation.
    /// </summary>
    public static JsonElement ToElement(JqValue value)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(ValueText.ToJson(value)), readerOptions);
        return JsonElement.ParseValue(ref reader);
    }

    static JqValue Scalar(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return JqValue.FromNumber(element.TryGetDouble(out var number) ? number : Parse(element).AsNumber());
            case JsonValueKind.String:
                return Parse(element);
            case JsonValueKind.True:
                return JqValue.True;
            case JsonValueKind.False:
                return JqValue.False;
            default:
                return JqValue.Null;
        }
    }

    // A scalar read from its raw text by the core's reader (§5.3): strings with escaped lone
    // surrogates, which System.Text.Json refuses to unescape, and numbers out of double range.
    static JqValue Parse(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && TryGetString(element, out var text))
            return JqValue.FromString(text);
        ValueText.TryParse(element.GetRawText(), out var value, out _);
        return value;
    }

    static bool TryGetString(JsonElement element, out string text)
    {
        text = "";
        try
        {
            text = element.GetString()!;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // An array or object being converted: its enumerator and the values converted so far.
    sealed class Container
    {
        readonly JsonElement element;
        readonly bool isObject;
        // A key System.Text.Json refuses to unescape (a lone surrogate): the core's reader reads the object instead.
        bool unreadable;
        JsonElement.ArrayEnumerator items;
        JsonElement.ObjectEnumerator fields;
        readonly JqValue[]? array;
        readonly JqObjectBuilder? obj;
        int count;
        string key = "";

        public Container(JsonElement element)
        {
            this.element = element;
            isObject = element.ValueKind == JsonValueKind.Object;
            if (isObject)
            {
                fields = element.EnumerateObject();
                obj = new JqObjectBuilder();
            }
            else
            {
                items = element.EnumerateArray();
                array = new JqValue[element.GetArrayLength()];
            }
        }

        public bool TryNext(out JsonElement next)
        {
            if (isObject)
            {
                var more = !unreadable && fields.MoveNext();
                next = more ? fields.Current.Value : default;
                if (more && !TryGetName(fields.Current, out key))
                {
                    unreadable = true;
                    return false;
                }

                return more;
            }

            var any = items.MoveNext();
            next = any ? items.Current : default;
            return any;
        }

        public void Add(JqValue value)
        {
            if (obj is not null)
                obj.Set(key, value);
            else
                array![count++] = value;
        }

        public JqValue ToValue()
        {
            if (unreadable)
            {
                ValueText.TryParse(element.GetRawText(), out var value, out _);
                return value;
            }

            return obj is not null ? JqValue.FromObject(obj.ToObject()) : JqValue.FromArray(JqArray.FromOwned(array!));
        }

        static bool TryGetName(JsonProperty property, out string name)
        {
            name = "";
            try
            {
                name = CodePoints.Sanitize(property.Name);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}