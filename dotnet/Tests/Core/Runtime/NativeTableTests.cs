using JQLite.Core.Builtins;

namespace JQLite.Tests.Core.Runtime;

public class NativeTableTests
{
    // A native of the catalogue without an implementation is a defect of the build: the table
    // refuses to load, and this test names it.
    [Fact]
    public void Every_native_of_the_catalogue_is_implemented()
        => Assert.Empty(Natives.All.Where(native => !NativeTable.IsImplemented(native)).Select(native => native.ToString()));
}
