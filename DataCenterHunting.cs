namespace SRankSentinel;

[Flags]
public enum NorthAmericanDataCenters
{
    None = 0, Aether = 1, Crystal = 2, Dynamis = 4, Primal = 8,
    All = Aether | Crystal | Dynamis | Primal,
}

internal static class DataCenterHunting
{
    internal static NorthAmericanDataCenters ForWorld(VisitWorld? world) =>
        world is { RegionId: 2 } ? world.LogicalDcId switch
        {
            4 => NorthAmericanDataCenters.Aether,
            8 => NorthAmericanDataCenters.Crystal,
            11 => NorthAmericanDataCenters.Dynamis,
            5 => NorthAmericanDataCenters.Primal,
            _ => NorthAmericanDataCenters.None,
        } : NorthAmericanDataCenters.None;

    // Region eligibility is checked separately. These four switches only narrow NA reports.
    internal static bool Allows(VisitWorld? world, NorthAmericanDataCenters enabled)
    {
        var center = ForWorld(world);
        return center == NorthAmericanDataCenters.None || (enabled & center) != 0;
    }
}
