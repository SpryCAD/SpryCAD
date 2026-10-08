namespace SpryCad.Kernel;

/// <summary>
/// The version of what the kernel builds: the solids and display meshes a part's features give. Saved with every
/// display cache (assemblies keep one per part revision and configuration), so a cache made by older code is known
/// to be stale and rebuilt, while one made by this code is used as it is, instead of rebuilding the part.
/// <para>
/// Bump <see cref="Current"/> with any change that makes the same part come out differently: booleans, blends,
/// threads, tessellation tolerances, face or edge naming, mesh layout. Not for speedups that give the same result.
/// </para>
/// </summary>
public static class GeometryVersion
{
    public const int Current = 1;
}
