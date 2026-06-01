using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
namespace kuraiaepiai.Source;
public class KuraiaepiaiReporter {
    public async Task<object> GenerateReport(string projectPath, string swaggerJsonContent) {
        var config = JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText(Path.Combine(projectPath, "kuraiaepiai.config.json"), Encoding.UTF8)) ?? new ProjectConfig();
        var allFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")).ToList();
        var dataFolder = Path.Combine(projectPath, "Data");
        var dataFiles = Directory.Exists(dataFolder) ? Directory.GetFiles(dataFolder, "*.cs").Select(File.ReadAllText).ToList() : new List<string>();
        var codeMap = new List<object>();
        foreach (var file in allFiles.Where(f => f.EndsWith("Controller.cs"))) {
            var content = File.ReadAllText(file);
            string ctrl = Path.GetFileNameWithoutExtension(file);
            var matches = Regex.Matches(content, @"\[Http(Get|Post|Put|Delete|Patch)[^\]]*\]");
            foreach (Match m in matches) {
                var mm = Regex.Match(content.Substring(m.Index, Math.Min(400, content.Length - m.Index)), @"public\s+(?:async\s+)?(?:Task<|ActionResult<)?[\w\.<>\[\]\s]+\s+(\w+)\s*\(");
                if (mm.Success) {
                    string mName = mm.Groups[1].Value;
                    int bStart = content.IndexOf('{', m.Index);
                    string body = bStart != -1 ? content.Substring(bStart, Math.Min(1200, content.Length - bStart)) : "";
                    var dataCalls = Regex.Matches(body, @"\.(\w+)\s*\(").Select(mc => mc.Groups[1].Value).ToList();
                    var tables = new List<string>();
                    var sqlTypes = new List<string>();
                    var keywords = new[] { "SELECT","UPDATE","INSERT","DELETE" };
                    foreach(var dc in dataFiles) {
                        foreach(var dm in dataCalls) {
                            if (dc.Contains(" " + dm + "(")) {
                                foreach(var k in keywords) if(dc.ToUpper().Contains(k)) sqlTypes.Add(k);
                                var sm = Regex.Matches(dc, @"(?i)(?:FROM|JOIN|UPDATE|INTO)\s+([\[\]\w\d\._]+)");
                                tables.AddRange(sm.Select(t => t.Groups[1].Value.Trim('[', ']', ' ', '"')).Where(t => !keywords.Contains(t.ToUpper()) && t.ToUpper() != "VALUES"));
                            }
                        }
                    }
                    codeMap.Add(new { Controller = ctrl, MethodName = mName, Verb = m.Groups[1].Value.ToUpper(), SqlType = sqlTypes.Distinct().ToList(), TargetTables = tables.Distinct().ToList() });
                }
            }
        }
        return new { 
            ownership = new { config.BusinessOwner, config.BusinessDept, config.ITOwner, config.ITDept, config.SystemName, config.APIName, TotalLinesOfCode = allFiles.Sum(f => File.ReadAllLines(f).Length), TotalFiles = allFiles.Count },
            packages = Assembly.GetEntryAssembly()?.GetReferencedAssemblies().Select(a => new { a.Name, Version = a.Version?.ToString() }),
            codeMap, swagger = JsonSerializer.Deserialize<object>(swaggerJsonContent) 
        };
    }
}
public class ProjectConfig { public string BusinessOwner { get; set; } = ""; public string BusinessDept { get; set; } = ""; public string ITOwner { get; set; } = ""; public string ITDept { get; set; } = ""; public string SystemName { get; set; } = ""; public string APIName { get; set; } = ""; }