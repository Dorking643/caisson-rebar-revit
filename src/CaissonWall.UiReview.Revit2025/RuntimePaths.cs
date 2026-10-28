using System.IO;

namespace CaissonWallUi;

/// <summary>Per-user writable paths, resolved at runtime on the recipient's computer.</summary>
public static class RuntimePaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaissonRebar", "R36.Share1");
    public static string WallDrafts => Path.Combine(Root, "drafts", "wall");
    public static string WallLogs => Path.Combine(Root, "logs", "wall");
    public static string SlabDrafts => Path.Combine(Root, "drafts", "slab");
    public static string SlabLogs => Path.Combine(Root, "logs", "slab");
}
