using System;
using System.Collections.Generic;

namespace PacePartners.Core
{
    public sealed class RoutePosition
    {
        public double DistanceMeters { get; internal set; }

        public double X { get; internal set; }

        public double Y { get; internal set; }

        public double Z { get; internal set; }

        public double AltitudeMeters { get; internal set; }

        public int SegmentIndex { get; internal set; }

        public double SegmentFraction { get; internal set; }
    }

    public sealed class RouteProjection
    {
        public double DistanceMeters { get; internal set; }

        public double X { get; internal set; }

        public double Y { get; internal set; }

        public double LateralOffsetMeters { get; internal set; }

        public double SignedLateralOffsetMeters { get; internal set; }

        public int SegmentIndex { get; internal set; }

        public double SegmentFraction { get; internal set; }
    }

    /// <summary>
    /// Distance parameterisation, interpolation, and XY projection for a dense
    /// FIT trace. Route distance is horizontal path distance; FIT altitude is
    /// carried independently for physically meaningful gradient.
    /// </summary>
    public sealed class RouteGeometry
    {
        private const double MinimumSegmentLength = 1e-6;

        private readonly List<RouteTracePoint> points;
        private readonly double[] distances;

        public RouteGeometry(IList<RouteTracePoint> tracePoints)
        {
            if (tracePoints == null)
            {
                throw new ArgumentNullException(nameof(tracePoints));
            }

            if (tracePoints.Count < 2)
            {
                throw new ArgumentException("A route trace needs at least two points.", nameof(tracePoints));
            }

            points = new List<RouteTracePoint>(tracePoints.Count);
            RouteTracePoint first = CopyAndValidate(tracePoints[0], 0);
            points.Add(first);

            for (int i = 1; i < tracePoints.Count; i++)
            {
                RouteTracePoint candidate = CopyAndValidate(tracePoints[i], i);
                RouteTracePoint previous = points[points.Count - 1];
                double dx = candidate.X - previous.X;
                double dy = candidate.Y - previous.Y;
                if ((dx * dx) + (dy * dy) >= MinimumSegmentLength * MinimumSegmentLength)
                {
                    points.Add(candidate);
                }
            }

            if (points.Count < 2)
            {
                throw new ArgumentException("The route trace needs at least two distinct XY positions.", nameof(tracePoints));
            }

            distances = new double[points.Count];
            for (int i = 1; i < points.Count; i++)
            {
                double dx = points[i].X - points[i - 1].X;
                double dy = points[i].Y - points[i - 1].Y;
                distances[i] = distances[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
            }
        }

        public int PointCount
        {
            get { return points.Count; }
        }

        public double TotalDistanceMeters
        {
            get { return distances[distances.Length - 1]; }
        }

        public double GetPointDistance(int pointIndex)
        {
            if (pointIndex < 0 || pointIndex >= distances.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(pointIndex));
            }

            return distances[pointIndex];
        }

        public RoutePosition Interpolate(double distanceMeters)
        {
            double distance = Clamp(distanceMeters, 0.0, TotalDistanceMeters);
            int upper = FindUpperPointIndex(distance);
            if (upper == 0)
            {
                return PositionFromPoint(0, distance);
            }

            if (upper >= points.Count)
            {
                return PositionFromPoint(points.Count - 1, distance);
            }

            int lower = upper - 1;
            double segmentLength = distances[upper] - distances[lower];
            double fraction = segmentLength > 0.0
                ? (distance - distances[lower]) / segmentLength
                : 0.0;
            RouteTracePoint a = points[lower];
            RouteTracePoint b = points[upper];

            return new RoutePosition
            {
                DistanceMeters = distance,
                X = Lerp(a.X, b.X, fraction),
                Y = Lerp(a.Y, b.Y, fraction),
                Z = Lerp(a.Z, b.Z, fraction),
                AltitudeMeters = Lerp(a.AltitudeMeters, b.AltitudeMeters, fraction),
                SegmentIndex = lower,
                SegmentFraction = fraction
            };
        }

        public RouteProjection Project(double x, double y)
        {
            return Project(x, y, 0.0, TotalDistanceMeters);
        }

        /// <summary>
        /// Projects onto only the requested route-distance window. Supplying a
        /// window around the rider's previous progress prevents branch jumps at
        /// figure-eights and spatially overlapping roads.
        /// </summary>
        public RouteProjection Project(
            double x,
            double y,
            double minimumDistanceMeters,
            double maximumDistanceMeters)
        {
            if (!IsFinite(x) || !IsFinite(y))
            {
                throw new ArgumentOutOfRangeException(nameof(x));
            }

            if (!IsFinite(minimumDistanceMeters) ||
                !IsFinite(maximumDistanceMeters) ||
                minimumDistanceMeters < 0.0 ||
                maximumDistanceMeters < minimumDistanceMeters ||
                maximumDistanceMeters > TotalDistanceMeters)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumDistanceMeters));
            }

            double bestSquaredDistance = double.MaxValue;
            RouteProjection best = null;

            for (int i = 0; i < points.Count - 1; i++)
            {
                if (distances[i + 1] < minimumDistanceMeters ||
                    distances[i] > maximumDistanceMeters)
                {
                    continue;
                }

                RouteTracePoint a = points[i];
                RouteTracePoint b = points[i + 1];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double lengthSquared = (dx * dx) + (dy * dy);
                double fraction = (((x - a.X) * dx) + ((y - a.Y) * dy)) / lengthSquared;
                double segmentLength = distances[i + 1] - distances[i];
                double minimumFraction = Math.Max(
                    0.0,
                    (minimumDistanceMeters - distances[i]) / segmentLength);
                double maximumFraction = Math.Min(
                    1.0,
                    (maximumDistanceMeters - distances[i]) / segmentLength);
                fraction = Clamp(fraction, minimumFraction, maximumFraction);

                double projectedX = a.X + (dx * fraction);
                double projectedY = a.Y + (dy * fraction);
                double offsetX = x - projectedX;
                double offsetY = y - projectedY;
                double squaredDistance = (offsetX * offsetX) + (offsetY * offsetY);

                if (squaredDistance < bestSquaredDistance)
                {
                    double cross = (dx * (y - a.Y)) - (dy * (x - a.X));
                    double sign = cross < 0.0 ? -1.0 : 1.0;
                    double lateral = Math.Sqrt(squaredDistance);
                    bestSquaredDistance = squaredDistance;
                    best = new RouteProjection
                    {
                        DistanceMeters = distances[i] + (segmentLength * fraction),
                        X = projectedX,
                        Y = projectedY,
                        LateralOffsetMeters = lateral,
                        SignedLateralOffsetMeters = sign * lateral,
                        SegmentIndex = i,
                        SegmentFraction = fraction
                    };
                }
            }

            return best;
        }

        /// <summary>Convenience wrapper for a symmetric progress window.</summary>
        public RouteProjection ProjectNear(
            double x,
            double y,
            double expectedDistanceMeters,
            double searchRadiusMeters)
        {
            if (!IsFinite(expectedDistanceMeters) ||
                !IsFinite(searchRadiusMeters) ||
                searchRadiusMeters <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(searchRadiusMeters));
            }

            double center = Clamp(expectedDistanceMeters, 0.0, TotalDistanceMeters);
            return Project(
                x,
                y,
                Math.Max(0.0, center - searchRadiusMeters),
                Math.Min(TotalDistanceMeters, center + searchRadiusMeters));
        }

        public double HeadingRadians(double distanceMeters, double baselineMeters)
        {
            if (!IsFinite(baselineMeters) || baselineMeters <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(baselineMeters));
            }

            double half = baselineMeters * 0.5;
            double left = Math.Max(0.0, distanceMeters - half);
            double right = Math.Min(TotalDistanceMeters, distanceMeters + half);
            if (right - left < MinimumSegmentLength)
            {
                return 0.0;
            }

            RoutePosition a = Interpolate(left);
            RoutePosition b = Interpolate(right);
            return Math.Atan2(b.Y - a.Y, b.X - a.X);
        }

        private RoutePosition PositionFromPoint(int index, double distance)
        {
            RouteTracePoint point = points[index];
            int segmentIndex = index >= points.Count - 1 ? points.Count - 2 : index;
            double fraction = index >= points.Count - 1 ? 1.0 : 0.0;
            return new RoutePosition
            {
                DistanceMeters = distance,
                X = point.X,
                Y = point.Y,
                Z = point.Z,
                AltitudeMeters = point.AltitudeMeters,
                SegmentIndex = segmentIndex,
                SegmentFraction = fraction
            };
        }

        private int FindUpperPointIndex(double distance)
        {
            int index = Array.BinarySearch(distances, distance);
            if (index >= 0)
            {
                return index;
            }

            return ~index;
        }

        private static RouteTracePoint CopyAndValidate(RouteTracePoint point, int index)
        {
            if (point == null)
            {
                throw new ArgumentException("Trace point " + index + " is null.", nameof(point));
            }

            if (!IsFinite(point.X) ||
                !IsFinite(point.Y) ||
                !IsFinite(point.Z) ||
                !IsFinite(point.AltitudeMeters))
            {
                throw new ArgumentException("Trace point " + index + " contains a non-finite coordinate.", nameof(point));
            }

            return new RouteTracePoint(point.X, point.Y, point.Z, point.AltitudeMeters);
        }

        private static double Lerp(double a, double b, double fraction)
        {
            return a + ((b - a) * fraction);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return value < minimum ? minimum : (value > maximum ? maximum : value);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
