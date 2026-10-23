using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Dynastream.Fit;

namespace PacePartners.Mod.Infrastructure
{
    public sealed class FitGeoOrigin
    {
        public FitGeoOrigin(double latitudeDegrees, double longitudeDegrees)
        {
            if (double.IsNaN(latitudeDegrees) || double.IsInfinity(latitudeDegrees) ||
                latitudeDegrees < -90.0 || latitudeDegrees > 90.0)
                throw new ArgumentOutOfRangeException(nameof(latitudeDegrees));
            if (double.IsNaN(longitudeDegrees) || double.IsInfinity(longitudeDegrees) ||
                longitudeDegrees < -180.0 || longitudeDegrees > 180.0)
                throw new ArgumentOutOfRangeException(nameof(longitudeDegrees));

            LatitudeDegrees = latitudeDegrees;
            LongitudeDegrees = longitudeDegrees;
        }

        public double LatitudeDegrees { get; }
        public double LongitudeDegrees { get; }
    }

    public sealed class DecodedFitPoint
    {
        internal DecodedFitPoint(
            int sourceIndex,
            double latitudeDegrees,
            double longitudeDegrees,
            double worldX,
            double worldY,
            double? altitudeMeters,
            double? worldZ,
            double? distanceMeters,
            System.DateTime? timestampUtc)
        {
            SourceIndex = sourceIndex;
            LatitudeDegrees = latitudeDegrees;
            LongitudeDegrees = longitudeDegrees;
            WorldX = worldX;
            WorldY = worldY;
            AltitudeMeters = altitudeMeters;
            WorldZ = worldZ;
            DistanceMeters = distanceMeters;
            TimestampUtc = timestampUtc;
        }

        public int SourceIndex { get; }
        public double LatitudeDegrees { get; }
        public double LongitudeDegrees { get; }
        public double WorldX { get; }
        public double WorldY { get; }
        public double? AltitudeMeters { get; }
        public double? WorldZ { get; }
        public double? DistanceMeters { get; }
        public System.DateTime? TimestampUtc { get; }
    }

    public sealed class DecodedFitRoute
    {
        internal DecodedFitRoute(
            string sourcePath,
            FitGeoOrigin origin,
            double elevationScale,
            IList<DecodedFitPoint> points)
        {
            SourcePath = sourcePath;
            Origin = origin;
            ElevationScale = elevationScale;
            Points = new ReadOnlyCollection<DecodedFitPoint>(points);
        }

        public string SourcePath { get; }
        public FitGeoOrigin Origin { get; }
        public double ElevationScale { get; }
        public IReadOnlyList<DecodedFitPoint> Points { get; }

        public bool HasMissingAltitude
        {
            get
            {
                for (int index = 0; index < Points.Count; index++)
                    if (!Points[index].AltitudeMeters.HasValue)
                        return true;
                return false;
            }
        }

        /// <summary>
        /// Returns a new route with null altitude gaps linearly interpolated by
        /// cumulative horizontal route distance. Leading/trailing gaps use the
        /// nearest known altitude. If the FIT has no altitude at all, the route
        /// remains nullable so callers cannot mistake invented height for data.
        /// </summary>
        public DecodedFitRoute WithInterpolatedAltitudes()
        {
            int firstKnown = -1;
            for (int index = 0; index < Points.Count; index++)
            {
                if (Points[index].AltitudeMeters.HasValue)
                {
                    firstKnown = index;
                    break;
                }
            }

            if (firstKnown < 0 || !HasMissingAltitude)
                return this;

            var cumulative = new double[Points.Count];
            for (int index = 1; index < Points.Count; index++)
            {
                double dx = Points[index].WorldX - Points[index - 1].WorldX;
                double dy = Points[index].WorldY - Points[index - 1].WorldY;
                cumulative[index] = cumulative[index - 1] + Math.Sqrt(dx * dx + dy * dy);
            }

            var altitudes = new double[Points.Count];
            double firstAltitude = Points[firstKnown].AltitudeMeters.Value;
            for (int index = 0; index <= firstKnown; index++)
                altitudes[index] = firstAltitude;

            int previousKnown = firstKnown;
            for (int index = firstKnown + 1; index < Points.Count; index++)
            {
                if (!Points[index].AltitudeMeters.HasValue)
                    continue;

                double startAltitude = Points[previousKnown].AltitudeMeters.Value;
                double endAltitude = Points[index].AltitudeMeters.Value;
                double span = cumulative[index] - cumulative[previousKnown];
                for (int gap = previousKnown + 1; gap < index; gap++)
                {
                    double fraction = span > 0.0
                        ? (cumulative[gap] - cumulative[previousKnown]) / span
                        : (double)(gap - previousKnown) / (index - previousKnown);
                    altitudes[gap] = startAltitude +
                                     (endAltitude - startAltitude) * fraction;
                }

                altitudes[index] = endAltitude;
                previousKnown = index;
            }

            double lastAltitude = altitudes[previousKnown];
            for (int index = previousKnown + 1; index < Points.Count; index++)
                altitudes[index] = lastAltitude;

            var interpolated = new List<DecodedFitPoint>(Points.Count);
            for (int index = 0; index < Points.Count; index++)
            {
                DecodedFitPoint point = Points[index];
                double altitude = altitudes[index];
                interpolated.Add(new DecodedFitPoint(
                    point.SourceIndex,
                    point.LatitudeDegrees,
                    point.LongitudeDegrees,
                    point.WorldX,
                    point.WorldY,
                    altitude,
                    altitude / ElevationScale,
                    point.DistanceMeters,
                    point.TimestampUtc));
            }

            return new DecodedFitRoute(SourcePath, Origin, ElevationScale, interpolated);
        }
    }

    /// <summary>
    /// Decodes dense FIT positions. Recorded FIT speed is intentionally never
    /// read: pace is derived later from rider effort and the calculated route.
    /// </summary>
    public sealed class FitCourseReader
    {
        private const double SemicirclesPerHalfTurn = 2147483648.0;
        private const double EarthRadiusMeters = 6378137.0;

        public DecodedFitRoute Read(string fitPath, FitGeoOrigin origin, double elevationScale)
        {
            if (string.IsNullOrWhiteSpace(fitPath))
                throw new ArgumentException("A FIT path is required.", nameof(fitPath));
            if (origin == null)
                throw new ArgumentNullException(nameof(origin));
            if (double.IsNaN(elevationScale) ||
                double.IsInfinity(elevationScale) ||
                elevationScale <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(elevationScale));

            string fullPath = Path.GetFullPath(fitPath);
            var points = new List<DecodedFitPoint>();
            int sourceIndex = 0;

            var decoder = new Decode();
            var broadcaster = new MesgBroadcaster();
            broadcaster.RecordMesgEvent += (sender, args) =>
            {
                var record = new RecordMesg(args.mesg);
                int currentSourceIndex = sourceIndex++;

                int? latitudeSemicircles = record.GetPositionLat();
                int? longitudeSemicircles = record.GetPositionLong();
                if (!latitudeSemicircles.HasValue || !longitudeSemicircles.HasValue)
                    return;

                double latitude = SemicirclesToDegrees(latitudeSemicircles.Value);
                double longitude = SemicirclesToDegrees(longitudeSemicircles.Value);
                ProjectActivityGhostsCoordinates(
                    latitude,
                    longitude,
                    origin,
                    out double worldX,
                    out double worldY);

                double? altitude = ReadAltitude(record);
                double? worldZ = altitude.HasValue
                    ? (double?)(altitude.Value / elevationScale)
                    : null;

                points.Add(new DecodedFitPoint(
                    currentSourceIndex,
                    latitude,
                    longitude,
                    worldX,
                    worldY,
                    altitude,
                    worldZ,
                    ReadDistance(record),
                    ReadTimestamp(record)));
            };

            using (var stream = new FileStream(
                       fullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                if (!decoder.IsFIT(stream))
                    throw new InvalidDataException($"'{fullPath}' is not a FIT file.");

                stream.Position = 0;
                if (!decoder.CheckIntegrity(stream))
                    throw new InvalidDataException($"'{fullPath}' failed FIT integrity validation.");

                stream.Position = 0;
                decoder.MesgEvent += broadcaster.OnMesg;
                decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
                decoder.Read(stream);
            }

            if (points.Count == 0)
                throw new InvalidDataException($"'{fullPath}' contains no records with valid latitude/longitude.");

            return new DecodedFitRoute(fullPath, origin, elevationScale, points);
        }

        public static double SemicirclesToDegrees(int semicircles)
        {
            return semicircles * (180.0 / SemicirclesPerHalfTurn);
        }

        private static double? ReadAltitude(RecordMesg record)
        {
            float? enhanced = record.GetEnhancedAltitude();
            if (enhanced.HasValue && IsFinite(enhanced.Value))
                return enhanced.Value;

            float? altitude = record.GetAltitude();
            return altitude.HasValue && IsFinite(altitude.Value)
                ? (double?)altitude.Value
                : null;
        }

        private static double? ReadDistance(RecordMesg record)
        {
            float? distance = record.GetDistance();
            return distance.HasValue && IsFinite(distance.Value)
                ? (double?)distance.Value
                : null;
        }

        private static System.DateTime? ReadTimestamp(RecordMesg record)
        {
            Dynastream.Fit.DateTime timestamp = record.GetTimestamp();
            if (timestamp == null)
                return null;

            System.DateTime value = timestamp.GetDateTime();
            return value.Kind == System.DateTimeKind.Utc
                ? value
                : System.DateTime.SpecifyKind(value, System.DateTimeKind.Utc);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// The same GPS-to-world projection used by ActivityGhosts: great-circle
        /// distance to GTBikeV's configured origin plus its bearing rotation.
        /// </summary>
        private static void ProjectActivityGhostsCoordinates(
            double latitudeDegrees,
            double longitudeDegrees,
            FitGeoOrigin origin,
            out double worldX,
            out double worldY)
        {
            double fromLatitude = DegreesToRadians(latitudeDegrees);
            double fromLongitude = DegreesToRadians(longitudeDegrees);
            double originLatitude = DegreesToRadians(origin.LatitudeDegrees);
            double originLongitude = DegreesToRadians(origin.LongitudeDegrees);

            double deltaLatitude = originLatitude - fromLatitude;
            double deltaLongitude = originLongitude - fromLongitude;
            double sinLatitude = Math.Sin(deltaLatitude / 2.0);
            double sinLongitude = Math.Sin(deltaLongitude / 2.0);
            double a = sinLatitude * sinLatitude +
                       Math.Cos(fromLatitude) * Math.Cos(originLatitude) *
                       sinLongitude * sinLongitude;
            a = Math.Max(0.0, Math.Min(1.0, a));
            double distance = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a)) *
                              EarthRadiusMeters;

            double y = Math.Sin(deltaLongitude) * Math.Cos(originLatitude);
            double x = Math.Cos(fromLatitude) * Math.Sin(originLatitude) -
                       Math.Sin(fromLatitude) * Math.Cos(originLatitude) *
                       Math.Cos(deltaLongitude);
            double bearing = -(Math.Atan2(y, x) + Math.PI / 2.0);

            worldX = distance * Math.Cos(bearing);
            worldY = distance * Math.Sin(bearing);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * (Math.PI / 180.0);
        }
    }
}
