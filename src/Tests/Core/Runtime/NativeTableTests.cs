using Devlooped.Core.Builtins;

namespace Devlooped.Tests.Core.Runtime;

public class NativeTableTests
{
    // The natives of the catalogue without an implementation yet: step 9 (modules) of the jqlite
    // plan empties this list.
    static readonly string[] NotImplemented =
    [
    ];

    [Fact]
    public void Every_native_but_the_listed_ones_is_implemented()
        => Assert.Equal(
            NotImplemented.Order(StringComparer.Ordinal),
            Natives.All.Where(native => !NativeTable.IsImplemented(native)).Select(native => native.ToString()).Order(StringComparer.Ordinal));
}