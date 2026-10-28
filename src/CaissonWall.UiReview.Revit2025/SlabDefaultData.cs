namespace CaissonWallUi;

/// <summary>
/// Share1 opens unreinforced example hosts with generic type and shape presets.
/// Original project-specific slab defaults are deliberately not distributed or auto-loaded.
/// Existing models with their own stored designs remain handled by SlabSession.
/// </summary>
public static class SlabDefaultData
{
    public static SlabDocument? TryCreate(HostContext host) => null;
}