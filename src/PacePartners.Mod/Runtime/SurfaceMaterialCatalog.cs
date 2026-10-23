using System.Collections.Generic;
using GTA;
using PacePartners.Core;

namespace PacePartners.Mod.Runtime
{
    /// <summary>
    /// Physical properties for GTA material hashes. The coefficients mirror the
    /// terrain categories used by GTBike V, while the raw hash remains in the
    /// profile cache so weather and tuning changes are always recomputable.
    /// </summary>
    internal static class SurfaceMaterialCatalog
    {
        public static SurfaceMaterialDefinition Default(double weatherGripMultiplier)
        {
            return Material(0, "Unknown", 0.008, 0.80, weatherGripMultiplier);
        }

        public static List<SurfaceMaterialDefinition> Create(double weatherGripMultiplier)
        {
            return new List<SurfaceMaterialDefinition>
            {
                Material(1187676648, "Concrete", 0.002, 0.90, weatherGripMultiplier),
                Material(359120722, "Concrete pothole", 0.006, 0.90, weatherGripMultiplier),
                Material(-1084640111, "Dusty concrete", 0.004, 0.80, weatherGripMultiplier),
                Material(282940568, "Tarmac", 0.004, 0.90, weatherGripMultiplier),
                Material(-1301352528, "Painted tarmac", 0.003, 0.85, weatherGripMultiplier),
                Material(1886546517, "Tarmac pothole", 0.006, 0.90, weatherGripMultiplier),
                Material(-840216541, "Rock", 0.010, 0.90, weatherGripMultiplier),
                Material(-124769592, "Mossy rock", 0.010, 0.70, weatherGripMultiplier),
                Material(765206029, "Stone", 0.010, 0.90, weatherGripMultiplier),
                Material(576169331, "Cobblestone", 0.008, 0.80, weatherGripMultiplier),
                Material(1907048430, "Paving slab", 0.006, 0.70, weatherGripMultiplier),
                Material(592446772, "Solid sandstone", 0.005, 0.90, weatherGripMultiplier),
                Material(1913209870, "Brittle sandstone", 0.008, 0.70, weatherGripMultiplier),
                Material(-1595148316, "Loose sand", 0.020, 0.40, weatherGripMultiplier),
                Material(510490462, "Compact sand", 0.012, 0.50, weatherGripMultiplier),
                Material(-1907520769, "Sand track", 0.009, 0.50, weatherGripMultiplier),
                Material(-786060715, "Ice", 0.001, 0.10, weatherGripMultiplier),
                Material(-1931024423, "Icy tarmac", 0.001, 0.20, weatherGripMultiplier),
                Material(951832588, "Small gravel", 0.010, 0.65, weatherGripMultiplier),
                Material(2128369009, "Large gravel", 0.012, 0.60, weatherGripMultiplier),
                Material(-356706482, "Deep gravel", 0.015, 0.45, weatherGripMultiplier),
                Material(1925605558, "Rail ballast", 0.020, 0.45, weatherGripMultiplier),
                Material(-1885547121, "Dirt track", 0.010, 0.50, weatherGripMultiplier),
                Material(-1942898710, "Hard mud", 0.012, 0.30, weatherGripMultiplier),
                Material(312396330, "Mud pothole", 0.014, 0.30, weatherGripMultiplier),
                Material(1635937914, "Soft mud", 0.020, 0.20, weatherGripMultiplier),
                Material(-700658213, "Soil", 0.010, 0.50, weatherGripMultiplier),
                Material(1144315879, "Hard clay", 0.008, 0.60, weatherGripMultiplier),
                Material(560985072, "Soft clay", 0.010, 0.40, weatherGripMultiplier),
                Material(-461750719, "Long grass", 0.012, 0.40, weatherGripMultiplier),
                Material(1333033863, "Grass", 0.010, 0.40, weatherGripMultiplier),
                Material(-1286696947, "Short grass", 0.009, 0.50, weatherGripMultiplier),
                Material(-754997699, "Metal manhole", 0.005, 0.90, weatherGripMultiplier),
                Material(2015599386, "Concrete pavement", 0.002, 0.90, weatherGripMultiplier),
                Material(-1147361576, "Brick pavement", 0.008, 0.90, weatherGripMultiplier),
                Material(-2088174996, "Stunt ramp", 0.001, 0.80, weatherGripMultiplier)
            };
        }

        public static double WeatherGripMultiplier(Weather weather)
        {
            switch (weather)
            {
                case Weather.Raining:
                case Weather.ThunderStorm:
                    return 0.70;
                case Weather.Snowing:
                    return 0.50;
                case Weather.Snowlight:
                case Weather.Christmas:
                    return 0.60;
                case Weather.Blizzard:
                    return 0.40;
                case Weather.Clearing:
                case Weather.Foggy:
                    return 0.90;
                default:
                    return 1.00;
            }
        }

        private static SurfaceMaterialDefinition Material(
            int hash,
            string name,
            double rollingResistance,
            double dryGrip,
            double weatherGripMultiplier)
        {
            return new SurfaceMaterialDefinition
            {
                MaterialHash = hash,
                Name = name,
                RollingResistanceCoefficient = rollingResistance,
                GripCoefficient = dryGrip * weatherGripMultiplier,
                RoughnessFactor = 1.0,
                SurfaceSpeedLimitMps = 0.0
            };
        }
    }
}
