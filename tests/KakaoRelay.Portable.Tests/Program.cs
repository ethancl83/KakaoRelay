if (args.Contains("app-server") || args.Contains("--input-format"))
{
    await ResidentCliChecks.Fixture(args); return 0;
}
if (args.Contains("--output-last-message"))
{
    Console.InputEncoding = System.Text.Encoding.UTF8;
    var input = await Console.In.ReadToEndAsync();
    if (input == "HANG") await Task.Delay(Timeout.Infinite);
    await File.WriteAllTextAsync(args[Array.IndexOf(args, "--output-last-message") + 1], input); return 0;
}
var root = Path.Combine(Path.GetTempPath(), "KakaoRelay.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var checks = 0;
try
{
    await AiChecks.RunAsync(root, (condition, name) => { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); });
    Console.WriteLine($"All {checks} portable checks passed."); return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
finally { Directory.Delete(root, true); }
