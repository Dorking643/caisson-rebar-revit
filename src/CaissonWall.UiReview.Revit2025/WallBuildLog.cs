using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace CaissonWallUi;

/// <summary>One local diagnostic record per build attempt, including the exact submitted draft.</summary>
public static class WallBuildLog
{
    public static string DirectoryPath => RuntimePaths.WallLogs;
    public static string? Save(ReviewDocument design,JsonObject result)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            string path=Path.Combine(DirectoryPath,$"build_{DateTime.Now:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}.json");
            result["log_path"]=path;
            var record=new JsonObject{["local_time"]=DateTimeOffset.Now.ToString("O"),["utc"]=DateTime.UtcNow.ToString("O"),
                ["assembly"]=Assembly.GetExecutingAssembly().GetName().Name,["design"]=JsonNode.Parse(design.ToJson()),["result"]=result.DeepClone()};
            File.WriteAllText(path,record.ToJsonString(ReviewDocument.JsonOptions),new UTF8Encoding(false));return path;
        }
        catch(Exception ex){result["log_write_error"]=ex.Message;return null;}
    }

    /// <summary>Managed diagnostics only; safe in modeless WinForms callbacks (no Revit API calls).</summary>
    public static string? SaveDiagnostic(string stage,Exception error,JsonObject? context=null)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            string path=Path.Combine(DirectoryPath,$"ui_error_{DateTime.Now:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}.json");
            var record=new JsonObject
            {
                ["event"]="ui_error",["stage"]=stage,["local_time"]=DateTimeOffset.Now.ToString("O"),
                ["utc"]=DateTime.UtcNow.ToString("O"),["assembly"]=Assembly.GetExecutingAssembly().GetName().Name,
                ["thread_id"]=Environment.CurrentManagedThreadId,["message"]=error.Message,["error"]=error.ToString(),
                ["context"]=context?.DeepClone()
            };
            File.WriteAllText(path,record.ToJsonString(ReviewDocument.JsonOptions),new UTF8Encoding(false));return path;
        }
        catch(Exception ex){System.Diagnostics.Trace.WriteLine($"Wall UI diagnostic failed: {ex}");return null;}
    }
}
