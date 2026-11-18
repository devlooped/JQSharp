using Devlooped.Core.Builtins;

namespace Devlooped.Tests.Core.Runtime;

public class NativeTableTests
{
    // The natives of the catalogue without an implementation yet: steps 7 (collections, strings,
    // formats) and 8 (regex, dates, math) of the jqlite plan, and 9 (modules) empty this list.
    static readonly string[] NotImplemented =
    [
        "contains/1",
        "test/2", "match/2",
        "now/0", "gmtime/0", "localtime/0", "mktime/0", "strftime/1", "strflocaltime/1", "strptime/1",
        "floor/0", "ceil/0", "trunc/0", "round/0", "nearbyint/0", "fabs/0", "sqrt/0", "fmod/2", "remainder/2", "fma/3",
        "fmin/2", "fmax/2", "ldexp/2", "scalbln/2", "logb/0", "frexp/0", "modf/0",
        "acos/0", "asin/0", "atan/0", "acosh/0", "asinh/0", "atanh/0", "cbrt/0", "cos/0", "cosh/0", "exp/0", "exp2/0",
        "expm1/0", "log/0", "log10/0", "log1p/0", "log2/0", "sin/0", "sinh/0", "tan/0", "tanh/0", "atan2/2", "pow/2", "hypot/2",
        "erf/0", "erfc/0", "tgamma/0", "lgamma/0", "j0/0", "j1/0",
        "modulemeta/0",
    ];

    [Fact]
    public void Every_native_but_the_listed_ones_is_implemented()
        => Assert.Equal(
            NotImplemented.Order(StringComparer.Ordinal),
            Natives.All.Where(native => !NativeTable.IsImplemented(native)).Select(native => native.ToString()).Order(StringComparer.Ordinal));
}
