namespace Tailed.Map
{
    /// <summary>Physics layers (created by TownSceneSetup in the project's TagManager).</summary>
    public static class Layers
    {
        public const int Ground = 6, Buildings = 7, Vehicles = 8, PlayerCar = 9;
        public const int BuildingsMask = 1 << Buildings;
    }
}
