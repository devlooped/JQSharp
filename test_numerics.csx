using System;
using System.Text.Json;

var tests = new[] { "42", "3.14", "1e100", "9999999999999999999", "0.1", "-5", "100000000000000000000000000000" };
foreach (var t in tests) {
    using var doc = JsonDocument.Parse(t);
    var el = doc.RootElement;
    Console.Write($"Input: {t,-35}");
    Console.Write($" i32={el.TryGetInt32(out var i32)}({i32})");
    Console.Write($" i64={el.TryGetInt64(out var i64)}({i64})");
    Console.Write($" dbl={el.TryGetDouble(out var d)}({d})");
    Console.Write($" dec={el.TryGetDecimal(out var dec)}({dec})");
    Console.WriteLine();
}
Console.WriteLine("\n--- Double precision ---");
Console.WriteLine($"0.1 + 0.2 = {0.1 + 0.2} (== 0.3? {0.1 + 0.2 == 0.3})");
Console.WriteLine($"C# -7 % 3 = {-7 % 3}");
Console.WriteLine($"C# 7 % -3 = {7 % -3}");
Console.WriteLine($"C# -7.5 % 2.5 = {-7.5 % 2.5}");
