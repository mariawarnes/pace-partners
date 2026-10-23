using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PacePartners.Mod.Infrastructure
{
    public enum PacingGender
    {
        Female,
        Male
    }

    public sealed class PacePartnersSettings
    {
        internal PacePartnersSettings(
            string pacePartnersIniPath,
            string gtBikeVConfigPath,
            bool enabled,
            bool autoStart,
            Keys toggleKey,
            Keys reloadKey,
            FitGeoOrigin initialGpsOrigin,
            double elevationScale,
            PacingGender pacingGender,
            IList<double> effortsWattsPerKilogram,
            IList<TimeSpan> targetCompletionTimes,
            IList<string> paceLabels,
            double riderMassKilograms,
            double bikeMassKilograms,
            double dragAreaSquareMeters,
            double powerVariationFraction,
            double primaryVariationPeriodSeconds,
            double secondaryVariationPeriodSeconds,
            int opacity,
            double sampleSpacingMeters,
            double gradientWindowMeters,
            double curvatureWindowMeters,
            double straightnessWindowMeters,
            double cornerLookAheadSeconds,
            double maximumBrakingMetersPerSecondSquared,
            double maximumAccelerationMetersPerSecondSquared,
            double surfaceSampleSpacingMeters,
            int surfaceSampleIntervalMilliseconds)
        {
            PacePartnersIniPath = pacePartnersIniPath;
            GtBikeVConfigPath = gtBikeVConfigPath;
            Enabled = enabled;
            AutoStart = autoStart;
            ToggleKey = toggleKey;
            ReloadKey = reloadKey;
            InitialGpsOrigin = initialGpsOrigin;
            ElevationScale = elevationScale;
            PacingGender = pacingGender;
            EffortsWattsPerKilogram =
                new ReadOnlyCollection<double>(effortsWattsPerKilogram);
            TargetCompletionTimes =
                new ReadOnlyCollection<TimeSpan>(targetCompletionTimes);
            PaceLabels = new ReadOnlyCollection<string>(paceLabels);
            RiderMassKilograms = riderMassKilograms;
            BikeMassKilograms = bikeMassKilograms;
            DragAreaSquareMeters = dragAreaSquareMeters;
            PowerVariationFraction = powerVariationFraction;
            PrimaryVariationPeriodSeconds = primaryVariationPeriodSeconds;
            SecondaryVariationPeriodSeconds = secondaryVariationPeriodSeconds;
            Opacity = opacity;
            SampleSpacingMeters = sampleSpacingMeters;
            GradientWindowMeters = gradientWindowMeters;
            CurvatureWindowMeters = curvatureWindowMeters;
            StraightnessWindowMeters = straightnessWindowMeters;
            CornerLookAheadSeconds = cornerLookAheadSeconds;
            MaximumBrakingMetersPerSecondSquared = maximumBrakingMetersPerSecondSquared;
            MaximumAccelerationMetersPerSecondSquared = maximumAccelerationMetersPerSecondSquared;
            SurfaceSampleSpacingMeters = surfaceSampleSpacingMeters;
            SurfaceSampleIntervalMilliseconds = surfaceSampleIntervalMilliseconds;
        }

        public string PacePartnersIniPath { get; }
        public string GtBikeVConfigPath { get; }
        public bool Enabled { get; }
        public bool AutoStart { get; }
        public Keys ToggleKey { get; }
        public Keys ReloadKey { get; }
        public FitGeoOrigin InitialGpsOrigin { get; }
        public double ElevationScale { get; }
        public PacingGender PacingGender { get; }
        public IReadOnlyList<double> EffortsWattsPerKilogram { get; }
        public IReadOnlyList<double> EffortsWPerKg => EffortsWattsPerKilogram;
        public IReadOnlyList<TimeSpan> TargetCompletionTimes { get; }
        public IReadOnlyList<string> PaceLabels { get; }
        public double RiderMassKilograms { get; }
        public double RiderMassKg => RiderMassKilograms;
        public double BikeMassKilograms { get; }
        public double BikeMassKg => BikeMassKilograms;
        public double DragAreaSquareMeters { get; }
        public double PowerVariationFraction { get; }
        public double PrimaryVariationPeriodSeconds { get; }
        public double SecondaryVariationPeriodSeconds { get; }
        public int Opacity { get; }
        public double SampleSpacingMeters { get; }
        public double GradientWindowMeters { get; }
        public double CurvatureWindowMeters { get; }
        public double StraightnessWindowMeters { get; }
        public double CornerLookAheadSeconds { get; }
        public double MaximumBrakingMetersPerSecondSquared { get; }
        public double MaxBrakingMps2 => MaximumBrakingMetersPerSecondSquared;
        public double MaximumAccelerationMetersPerSecondSquared { get; }
        public double MaxAccelerationMps2 => MaximumAccelerationMetersPerSecondSquared;
        public double SurfaceSampleSpacingMeters { get; }
        public int SurfaceSampleIntervalMilliseconds { get; }
        public int SurfaceSampleIntervalMs => SurfaceSampleIntervalMilliseconds;
    }

    public sealed class PacePartnersSettingsLoader
    {
        public const double DefaultInitialLatitude = -19.10637;
        public const double DefaultInitialLongitude = -169.871;
        public const double DefaultElevationScale = 0.5;

        // Representative points inside TrainerRoad's untrained, trained and
        // elite FTP W/kg bands, exposed as Beginner, Intermediate and Expert.
        private static readonly double[] FemaleEfforts = { 1.5, 2.6, 3.7 };
        private static readonly double[] MaleEfforts = { 1.9, 3.0, 4.1 };
        private static readonly string[] FemalePaceLabels =
        {
            "Beginner Female",
            "Intermediate Female",
            "Expert Female"
        };
        private static readonly string[] MalePaceLabels =
        {
            "Beginner Male",
            "Intermediate Male",
            "Expert Male"
        };

        public PacePartnersSettings Load(string scriptsDirectory, string modSettingsDirectory)
        {
            if (string.IsNullOrWhiteSpace(scriptsDirectory))
                throw new ArgumentException("The GTA Scripts directory is required.", nameof(scriptsDirectory));
            if (string.IsNullOrWhiteSpace(modSettingsDirectory))
                throw new ArgumentException("The GTBikeV ModSettings directory is required.", nameof(modSettingsDirectory));

            string pacePath = Path.GetFullPath(Path.Combine(scriptsDirectory, "PacePartners.ini"));
            string gtBikePath = Path.GetFullPath(Path.Combine(modSettingsDirectory, "GTBikeVConfig.ini"));
            IniData pace = IniData.ReadIfExists(pacePath);
            IniData gtBike = IniData.ReadIfExists(gtBikePath);

            bool enabled = ParseOptionalBoolean(
                pace.Get("MAIN", "Enabled"),
                true,
                "PacePartners.ini [MAIN] Enabled");
            bool autoStart = ParseOptionalBoolean(
                pace.Get("MAIN", "AutoStart"),
                true,
                "PacePartners.ini [MAIN] AutoStart");
            Keys toggleKey = ParseOptionalKey(
                pace.Get("MAIN", "ToggleKey"),
                Keys.F10,
                "PacePartners.ini [MAIN] ToggleKey");
            Keys reloadKey = ParseOptionalKey(
                pace.Get("MAIN", "ReloadKey"),
                Keys.F11,
                "PacePartners.ini [MAIN] ReloadKey");

            double latitude = ParseOptionalDouble(
                gtBike.Get("MAIN", "InitialGPSPointLat"),
                DefaultInitialLatitude,
                "GTBikeVConfig.ini [MAIN] InitialGPSPointLat");
            double longitude = ParseOptionalDouble(
                gtBike.Get("MAIN", "InitialGPSPointLong"),
                DefaultInitialLongitude,
                "GTBikeVConfig.ini [MAIN] InitialGPSPointLong");

            PacingGender pacingGender = ParsePacingGender(
                pace.Get("PACING", "PacingGender"));
            IList<double> efforts = pacingGender == PacingGender.Female
                ? FemaleEfforts.ToList()
                : MaleEfforts.ToList();
            IList<string> paceLabels = pacingGender == PacingGender.Female
                ? FemalePaceLabels.ToList()
                : MalePaceLabels.ToList();

            string targetText =
                pace.Get("PACING", "TargetCompletionTimes") ??
                pace.Get("PACING", "TargetTimes") ??
                pace.Get("MAIN", "TargetCompletionTimes");
            IList<TimeSpan> targets = ParseTargetTimes(targetText);

            string elevationText =
                pace.Get("ROUTE", "ElevationScale") ??
                pace.Get("MAIN", "ElevationScale");
            double elevationScale = ParseOptionalDouble(
                elevationText,
                DefaultElevationScale,
                "PacePartners.ini ElevationScale");
            if (elevationScale <= 0.0)
                throw new FormatException("PacePartners.ini ElevationScale must be greater than zero.");

            double riderMass = ParseOptionalDouble(
                gtBike.Get("USER", "UserWeightKg"),
                0.0,
                "GTBikeVConfig.ini [USER] UserWeightKg");
            if (riderMass <= 0.0)
                throw new FormatException(
                    "GTBikeVConfig.ini [USER] UserWeightKg must be greater than zero.");
            double bikeMass = ReadPositiveDouble(
                pace,
                "PACING",
                "BikeMassKg",
                6.9);
            double dragArea = ReadPositiveDouble(
                pace,
                "PACING",
                "DragAreaSquareMeters",
                0.32);
            double powerVariation = ParseOptionalDouble(
                pace.Get("PACING", "PowerVariationFraction"),
                0.018,
                "PacePartners.ini [PACING] PowerVariationFraction");
            if (powerVariation < 0.0 || powerVariation >= 1.0)
                throw new FormatException(
                    "PacePartners.ini [PACING] PowerVariationFraction must be at least zero and less than one.");
            double primaryVariationPeriod = ReadPositiveDouble(
                pace,
                "PACING",
                "PrimaryVariationPeriodSeconds",
                53.0);
            double secondaryVariationPeriod = ReadPositiveDouble(
                pace,
                "PACING",
                "SecondaryVariationPeriodSeconds",
                89.0);

            int opacity = ParseOptionalInt32(
                pace.Get("SPAWN", "Opacity"),
                180,
                "PacePartners.ini [SPAWN] Opacity");
            if (opacity < 0 || opacity > 255)
                throw new FormatException(
                    "PacePartners.ini [SPAWN] Opacity must be between 0 and 255.");

            double sampleSpacing = ReadPositiveDouble(
                pace,
                "ROUTE",
                "SampleSpacingMeters",
                2.0);
            double gradientWindow = ReadPositiveDouble(
                pace,
                "ROUTE",
                "GradientWindowMeters",
                25.0);
            double curvatureWindow = ReadPositiveDouble(
                pace,
                "ROUTE",
                "CurvatureWindowMeters",
                12.0);
            double straightnessWindow = ReadPositiveDouble(
                pace,
                "ROUTE",
                "StraightnessWindowMeters",
                25.0);
            double cornerLookAhead = ReadPositiveDouble(
                pace,
                "ROUTE",
                "CornerLookAheadSeconds",
                6.0);
            double maximumBraking = ReadPositiveDouble(
                pace,
                "ROUTE",
                "MaxBrakingMps2",
                2.5);
            double maximumAcceleration = ReadPositiveDouble(
                pace,
                "ROUTE",
                "MaxAccelerationMps2",
                0.8);
            double surfaceSampleSpacing = ReadPositiveDouble(
                pace,
                "ROUTE",
                "SurfaceSampleSpacingMeters",
                8.0);
            int surfaceSampleInterval = ParseOptionalInt32(
                pace.Get("ROUTE", "SurfaceSampleIntervalMs"),
                150,
                "PacePartners.ini [ROUTE] SurfaceSampleIntervalMs");
            if (surfaceSampleInterval <= 0)
                throw new FormatException(
                    "PacePartners.ini [ROUTE] SurfaceSampleIntervalMs must be greater than zero.");

            return new PacePartnersSettings(
                pacePath,
                gtBikePath,
                enabled,
                autoStart,
                toggleKey,
                reloadKey,
                new FitGeoOrigin(latitude, longitude),
                elevationScale,
                pacingGender,
                efforts,
                targets,
                paceLabels,
                riderMass,
                bikeMass,
                dragArea,
                powerVariation,
                primaryVariationPeriod,
                secondaryVariationPeriod,
                opacity,
                sampleSpacing,
                gradientWindow,
                curvatureWindow,
                straightnessWindow,
                cornerLookAhead,
                maximumBraking,
                maximumAcceleration,
                surfaceSampleSpacing,
                surfaceSampleInterval);
        }

        private static double ReadPositiveDouble(
            IniData data,
            string section,
            string key,
            double fallback)
        {
            string description = $"PacePartners.ini [{section}] {key}";
            double value = ParseOptionalDouble(data.Get(section, key), fallback, description);
            if (value <= 0.0)
                throw new FormatException(description + " must be greater than zero.");
            return value;
        }

        private static string ReadNonEmptyString(
            IniData data,
            string section,
            string key,
            string fallback)
        {
            string value = data.Get(section, key);
            if (value == null)
                return fallback;

            value = value.Trim();
            if (value.Length == 0)
                throw new FormatException($"PacePartners.ini [{section}] {key} cannot be empty.");
            return value;
        }

        private static PacingGender ParsePacingGender(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return PacingGender.Female;

            string value = text.Trim();
            if (value.Equals("Female", StringComparison.OrdinalIgnoreCase))
                return PacingGender.Female;
            if (value.Equals("Male", StringComparison.OrdinalIgnoreCase))
                return PacingGender.Male;

            throw new FormatException(
                "PacePartners.ini [PACING] PacingGender must be Female or Male.");
        }

        private static IList<TimeSpan> ParseTargetTimes(string text)
        {
            var values = new List<TimeSpan>();
            if (string.IsNullOrWhiteSpace(text))
                return values;

            foreach (string token in SplitList(text))
            {
                TimeSpan value;
                if (token.IndexOf(':') >= 0)
                {
                    if (!TimeSpan.TryParse(token, CultureInfo.InvariantCulture, out value))
                        throw new FormatException(
                            $"Invalid target completion time '{token}' in PacePartners.ini.");
                }
                else
                {
                    if (!double.TryParse(
                            token,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double minutes))
                        throw new FormatException(
                            $"Invalid target completion time '{token}' in PacePartners.ini.");
                    value = TimeSpan.FromMinutes(minutes);
                }

                if (value <= TimeSpan.Zero)
                    throw new FormatException(
                        $"Target completion time '{token}' must be positive.");
                if (!values.Contains(value))
                    values.Add(value);
            }

            return values;
        }

        private static IEnumerable<string> SplitList(string text)
        {
            return text
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0);
        }

        private static double ParseOptionalDouble(string text, double fallback, string description)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            if (!double.TryParse(
                    text.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value) ||
                double.IsNaN(value) ||
                double.IsInfinity(value))
                throw new FormatException($"{description} is not a finite invariant-culture number.");

            return value;
        }

        private static bool ParseOptionalBoolean(string text, bool fallback, string description)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            if (!bool.TryParse(text.Trim(), out bool value))
                throw new FormatException(description + " must be true or false.");
            return value;
        }

        private static Keys ParseOptionalKey(string text, Keys fallback, string description)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            if (!Enum.TryParse(text.Trim(), true, out Keys value))
                throw new FormatException(description + " is not a System.Windows.Forms.Keys value.");
            return value;
        }

        private static int ParseOptionalInt32(string text, int fallback, string description)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            if (!int.TryParse(
                    text.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int value))
                throw new FormatException(description + " is not an integer.");
            return value;
        }

        private sealed class IniData
        {
            private readonly Dictionary<string, string> _values =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public static IniData ReadIfExists(string path)
            {
                var result = new IniData();
                if (!File.Exists(path))
                    return result;

                string section = string.Empty;
                using (var stream = new FileStream(
                           path,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.Length == 0 ||
                            trimmed.StartsWith(";", StringComparison.Ordinal) ||
                            trimmed.StartsWith("#", StringComparison.Ordinal))
                            continue;

                        if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                            trimmed.EndsWith("]", StringComparison.Ordinal))
                        {
                            section = trimmed.Substring(1, trimmed.Length - 2).Trim();
                            continue;
                        }

                        int equals = trimmed.IndexOf('=');
                        if (equals <= 0)
                            continue;

                        string key = trimmed.Substring(0, equals).Trim();
                        string value = StripInlineComment(trimmed.Substring(equals + 1).Trim());
                        result._values[ComposeKey(section, key)] = value;
                    }
                }

                return result;
            }

            public string Get(string section, string key)
            {
                _values.TryGetValue(ComposeKey(section, key), out string value);
                return value;
            }

            private static string ComposeKey(string section, string key)
            {
                return (section ?? string.Empty).Trim() + "\0" + (key ?? string.Empty).Trim();
            }

            private static string StripInlineComment(string value)
            {
                int semicolon = value.IndexOf(';');
                int hash = value.IndexOf('#');
                int index = semicolon < 0
                    ? hash
                    : hash < 0
                        ? semicolon
                        : Math.Min(semicolon, hash);
                return index < 0 ? value.Trim() : value.Substring(0, index).Trim();
            }
        }
    }
}
