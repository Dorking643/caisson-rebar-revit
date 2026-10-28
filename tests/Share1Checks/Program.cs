using CaissonWallUi;
using System.Text.Json;

var results = new List<object>();
int failed = 0;
void Check(string name, bool pass) { results.Add(new { name, pass }); if (!pass) failed++; }
var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
Check("runtime_under_current_user_local_data", RuntimePaths.Root.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
var folders = new[] { RuntimePaths.WallDrafts, RuntimePaths.WallLogs, RuntimePaths.SlabDrafts, RuntimePaths.SlabLogs };
Check("drafts_and_logs_are_separate", folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4);
Check("all_paths_absolute_and_scoped", folders.All(p => Path.IsPathFullyQualified(p) && p.StartsWith(RuntimePaths.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
Check("blank_host_has_no_project_defaults", SlabDefaultData.TryCreate(new HostContext()) == null);
Check("verified_matching_geometry_has_no_project_defaults", SlabDefaultData.TryCreate(new HostContext { IsVerified=true, OuterRadiusMm=2400, HeightMm=400, InnerRadiusMm=0, HostUniqueId="sample-host" }) == null);
Check("null_host_has_no_project_defaults", SlabDefaultData.TryCreate(null!) == null);
Check("no_embedded_project_default_resource", !typeof(SlabDefaultData).Assembly.GetManifestResourceNames().Any(n => n.Contains("SlabDefault", StringComparison.OrdinalIgnoreCase)));
string json = JsonSerializer.Serialize(new { status=failed == 0 ? "PASS" : "FAIL", passed=results.Count-failed, failed, checks=results }, new JsonSerializerOptions { WriteIndented=true });
Console.WriteLine(json);
if (args.Length > 0) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!); File.WriteAllText(args[0], json); }
return failed == 0 ? 0 : 1;
