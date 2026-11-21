using System.Diagnostics;
using System.Text.Json;

namespace BlenderMinor;

/// <summary>
/// --selftest: runs ALL the tools (with and without material) in an ISOLATED headless Blender
/// (--background --factory-startup, no --online-mode: it does not touch the user's window or the 9876 bridge).
/// Use it after updating Blender to find out which template broke. Exit code 0 = all good.
/// </summary>
public static class SelfTest
{
    private static readonly string[] Cases =
    [
        "{}",
        """{"color":"blue","specular":0.7,"roughness":0.3,"x":1,"y":2,"z":3,"name":"SelfTest"}""",
    ];

    // Tools that work on objects act on a cube created first, named SelfTestCube (size 2, centered at the origin).
    // Setup = extra cubes created before the cases (e.g. a cutter). MissingJson = a call naming an object that does not
    // exist, which must answer with a clear "ERROR: object not found" and never a traceback (null = not applicable).
    private sealed record OpPlan((string Tool, string Json)[] Setup, string[] Cases, string? MissingJson);

    private static readonly Dictionary<string, OpPlan> OpPlans = new()
    {
        ["create_box"] = new([], ["""{"name":"SelfTestBox","width":4,"depth":2,"height":1,"x":10,"color":"blue"}"""], null),
        ["set_dimensions"] = new([], ["""{"name":"SelfTestCube","x":3,"z":1.5}"""], """{"name":"DoesNotExist","x":1}"""),
        ["set_color"] = new([], ["""{"name":"SelfTestCube","color":"blue"}""", """{"name":"SelfTestCube","color":"roxa","roughness":0.2}"""], """{"name":"DoesNotExist","color":"red"}"""),
        ["move_object"] = new([], ["""{"name":"SelfTestCube","x":5,"y":1}""", """{"name":"SelfTestCube","z":2,"relative":true}"""], """{"name":"DoesNotExist","x":1}"""),
        ["bevel_object"] = new([], ["""{"name":"SelfTestCube","width":0.1,"segments":2}""", """{"name":"SelfTestCube","width":0.05}"""], """{"name":"DoesNotExist","width":0.1}"""),
        ["array_object"] = new([("create_box", """{"name":"SelfTestPiece","width":5,"depth":5,"height":2}""")],
            ["""{"name":"SelfTestPiece","count_x":3,"count_y":2,"gap":1}""", """{"name":"SelfTestCube","count_x":2,"apply":false}"""], """{"name":"DoesNotExist","count_x":2}"""),
        ["boolean_op"] = new([("create_cube", """{"name":"SelfTestCutter","size":1,"x":1}""")],
            ["""{"target":"SelfTestCube","cutter":"SelfTestCutter","operation":"DIFFERENCE"}"""], """{"target":"DoesNotExist","cutter":"SelfTestCube"}"""),
        ["drill_hole"] = new([],
            ["""{"name":"SelfTestCube","diameter":0.5,"x":0.3,"y":0.2}""",
             """{"name":"SelfTestCube","diameter":0.3,"x":-0.5,"y":-0.5,"z":1,"depth":0.4}""",
             """{"name":"SelfTestCube","diameter":0.3,"x":0.5,"y":-0.5,"counterbore_diameter":0.6,"counterbore_depth":0.3}""",
             """{"name":"SelfTestCube","diameter":0.2,"x":-0.5,"y":0.5,"countersink_diameter":0.5}"""],
            """{"name":"DoesNotExist","diameter":1,"x":0,"y":0}"""),
        ["screw_hole"] = new([("create_box", """{"name":"SelfTestPlate","width":40,"depth":40,"height":10}""")],
            ["""{"name":"SelfTestPlate","size":"M3","x":0,"y":0}""",
             """{"name":"SelfTestPlate","size":"M3","kind":"counterbore","x":10,"y":0}""",
             """{"name":"SelfTestPlate","size":"M3","kind":"countersunk","x":-10,"y":0}""",
             """{"name":"SelfTestPlate","size":"M3","kind":"insert","x":0,"y":10,"z":5}"""],
            """{"name":"DoesNotExist","size":"M3","x":0,"y":0}"""),
        ["check_mesh"] = new([], ["""{"name":"SelfTestCube"}"""], """{"name":"DoesNotExist"}"""),
        ["repair_mesh"] = new([], ["""{"name":"SelfTestCube"}"""], """{"name":"DoesNotExist"}"""),
        ["delete_object"] = new([("create_cube", """{"name":"SelfTestTrash"}""")], ["""{"name":"SelfTestTrash"}"""], """{"name":"DoesNotExist"}"""),
        ["export_stl"] = new([], ["""{"name":"SelfTestCube","filename":"selftest.stl"}"""], """{"name":"DoesNotExist"}"""),
        ["set_units"] = new([], ["""{"unit":"mm"}""", """{"unit":"m"}"""], null),
    };

    /// <summary>Builds a script for either a shape tool or an object tool.</summary>
    private static (string? Script, string? Error) BuildAny(string tool, JsonElement args)
    {
        if (ShapeScripts.Find(tool) is { } shape) return ShapeScripts.Build(shape, args);
        return OpScripts.Find(tool) is { } op ? OpScripts.Build(op, args) : (null, $"Unknown tool '{tool}'.");
    }

    public static async Task<int> RunAsync()
    {
        string blender = Environment.GetEnvironmentVariable("BLENDER_BIN") ?? "/opt/blender/blender-5.3.0-linux-x64/blender";
        if (!File.Exists(blender))
        {
            Console.Error.WriteLine($"Blender not found at {blender} (set BLENDER_BIN).");
            return 2;
        }
        Console.WriteLine($"Blender: {blender}");
        var (_, version, _) = await RunAsync(blender, ["--version"], 60);
        Console.WriteLine("Version: " + version.Split('\n').FirstOrDefault(l => l.StartsWith("Blender"))?.Trim());
        Console.WriteLine($"Templates folder: {Templates.Dir} ({(Directory.Exists(Templates.Dir) ? "exists" : "does not exist: only the embedded texts apply")})");
        Console.WriteLine();

        var tmp = Directory.CreateTempSubdirectory("blender-minor-selftest-");
        Environment.SetEnvironmentVariable("BLENDER_MINOR_EXPORT_DIR", tmp.FullName);   // export_stl writes here, never to the real folder
        int failed = 0;
        try
        {
            foreach (var shape in ShapeScripts.Shapes)
            {
                var src = ShapeScripts.Sources(shape);
                string origin = $"base={Short(src["base"])}, material={Short(src["material"])}, footer={Short(src["footer"])}";
                var script = new System.Text.StringBuilder();
                string? buildError = null;
                foreach (var c in Cases)
                {
                    using var doc = JsonDocument.Parse(c);
                    var (s, err) = ShapeScripts.Build(shape, doc.RootElement);
                    if (err is not null) { buildError = err; break; }
                    script.Append(s);
                }
                if (buildError is not null) { failed++; Console.WriteLine($"FAILED  {shape.Tool}: {buildError}"); continue; }

                string file = Path.Combine(tmp.FullName, shape.Tool + ".py");
                await File.WriteAllTextAsync(file, script.ToString());
                var (code, output, timedOut) = await RunAsync(blender, ["-b", "--factory-startup", "--python-exit-code", "1", "--python", file], 120);

                int oks = output.Split('\n').Count(l => l.StartsWith("OK "));
                bool ok = !timedOut && code == 0 && oks == Cases.Length && !output.Contains("Traceback");
                var warnings = output.Split('\n').Where(l => l.Contains("Warning")).Select(l => l[(l.IndexOf("Warning", StringComparison.Ordinal))..].Trim()).Distinct().ToList();

                Console.WriteLine($"{(ok ? "OK     " : "FAILED ")} {shape.Tool}  [{origin}]");
                foreach (var w in warnings) Console.WriteLine($"   Blender warning: {w}");
                if (!ok)
                {
                    failed++;
                    if (timedOut) Console.WriteLine("   timed out");
                    foreach (var l in output.Split('\n').Where(l => l.Contains("Error") || l.Contains("Traceback") || l.TrimStart().StartsWith("File ")).Take(8))
                        Console.WriteLine("   " + l.Trim());
                }
            }

            // list_objects and the editing tools, run against a freshly created cube in the same headless Blender.
            string prelude;
            using (var d0 = JsonDocument.Parse("""{"name":"SelfTestCube"}"""))
            {
                var (s0, e0) = ShapeScripts.Build(ShapeScripts.Find("create_cube")!, d0.RootElement);
                if (e0 is not null) { Console.WriteLine($"FAILED  setup: {e0}"); return 1; }
                prelude = s0 + "\n";
            }

            async Task<bool> RunCaseAsync(string tool, string script, int expectedOk, string? mustContain)
            {
                string file = Path.Combine(tmp.FullName, tool + ".py");
                await File.WriteAllTextAsync(file, script);
                var (code, output, timedOut) = await RunAsync(blender, ["-b", "--factory-startup", "--python-exit-code", "1", "--python", file], 120);
                int oks = output.Split('\n').Count(l => l.StartsWith("OK "));
                bool ok = !timedOut && code == 0 && oks >= expectedOk && !output.Contains("Traceback")
                          && (mustContain is null || output.Contains(mustContain));
                Console.WriteLine($"{(ok ? "OK     " : "FAILED ")} {tool}");
                if (!ok)
                {
                    failed++;
                    if (timedOut) Console.WriteLine("   timed out");
                    foreach (var l in output.Split('\n').Where(l => l.Contains("Error") || l.Contains("ERROR") || l.Contains("Traceback") || l.TrimStart().StartsWith("File ")).Take(8))
                        Console.WriteLine("   " + l.Trim());
                }
                return ok;
            }

            await RunCaseAsync("list_objects", prelude + Rpc.ListScript, 1, "SelfTestCube MESH");

            foreach (var op in OpScripts.Ops)
            {
                var plan = OpPlans[op.Tool];
                var script = new System.Text.StringBuilder(prelude);
                int expectedOk = 1;
                string? opError = null;
                foreach (var (stool, sj) in plan.Setup)
                {
                    using var sd = JsonDocument.Parse(sj);
                    var (ss, se) = BuildAny(stool, sd.RootElement);
                    if (se is not null) { opError = se; break; }
                    script.Append(ss).Append('\n');
                    expectedOk++;
                }
                foreach (var c in plan.Cases)
                {
                    using var doc = JsonDocument.Parse(c);
                    var (s, err) = OpScripts.Build(op, doc.RootElement);
                    if (err is not null) { opError ??= err; break; }
                    script.Append(s).Append('\n');
                    expectedOk++;
                }
                // an object that does not exist must get a clear error message, not a traceback
                if (plan.MissingJson is not null)
                {
                    using var missing = JsonDocument.Parse(plan.MissingJson);
                    var (s, err) = OpScripts.Build(op, missing.RootElement);
                    if (err is not null) opError ??= err; else script.Append(s).Append('\n');
                }
                if (opError is not null) { failed++; Console.WriteLine($"FAILED  {op.Tool}: {opError}"); continue; }
                await RunCaseAsync(op.Tool, script.ToString(), expectedOk, plan.MissingJson is null ? null : "ERROR: object not found: DoesNotExist");
            }
        }
        finally { try { tmp.Delete(true); } catch (IOException) { } }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "All good." : $"{failed} tool(s) with problems: fix the template (a file in the templates folder) and run again.");
        return failed == 0 ? 0 : 1;
    }

    private static string Short(string source) => source.StartsWith("file") ? source : "embedded";

    private static async Task<(int Code, string Output, bool TimedOut)> RunAsync(string exe, string[] args, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a); // no shell: nothing interprets the text
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        bool timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { timedOut = true; try { p.Kill(true); } catch (InvalidOperationException) { } }
        return (timedOut ? -1 : p.ExitCode, await outTask + "\n" + await errTask, timedOut);
    }
}
