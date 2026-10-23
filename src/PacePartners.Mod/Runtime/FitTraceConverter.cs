using System;
using System.Collections.Generic;
using System.IO;
using PacePartners.Core;
using PacePartners.Mod.Infrastructure;

namespace PacePartners.Mod.Runtime
{
    internal static class FitTraceConverter
    {
        /// <summary>
        /// Preserves every valid FIT position, fills only missing elevation
        /// fields, and optionally reverses the dense trace. Recorded speed never
        /// participates in this conversion.
        /// </summary>
        public static List<RouteTracePoint> ToRouteTrace(
            DecodedFitRoute route,
            bool reversed)
        {
            if (route == null)
            {
                throw new ArgumentNullException(nameof(route));
            }

            if (route.Points.Count < 2)
            {
                throw new InvalidDataException(
                    "The FIT course has fewer than two valid positions.");
            }

            double[] chainage = HorizontalChainage(route);
            double?[] altitude = new double?[route.Points.Count];
            int validAltitudeCount = 0;
            for (int i = 0; i < route.Points.Count; i++)
            {
                altitude[i] = route.Points[i].AltitudeMeters;
                if (altitude[i].HasValue)
                {
                    validAltitudeCount++;
                }
            }

            if (validAltitudeCount == 0)
            {
                throw new InvalidDataException(
                    "The FIT course has no altitude data; a physical gradient profile cannot be built.");
            }

            FillMissingAltitudes(altitude, chainage);
            var result = new List<RouteTracePoint>(route.Points.Count);
            for (int i = 0; i < route.Points.Count; i++)
            {
                DecodedFitPoint point = route.Points[i];
                double physicalAltitude = altitude[i].Value;
                result.Add(new RouteTracePoint(
                    point.WorldX,
                    point.WorldY,
                    physicalAltitude / route.ElevationScale,
                    physicalAltitude));
            }

            if (reversed)
            {
                result.Reverse();
            }

            return result;
        }

        private static double[] HorizontalChainage(DecodedFitRoute route)
        {
            double[] chainage = new double[route.Points.Count];
            for (int i = 1; i < route.Points.Count; i++)
            {
                double dx = route.Points[i].WorldX - route.Points[i - 1].WorldX;
                double dy = route.Points[i].WorldY - route.Points[i - 1].WorldY;
                chainage[i] = chainage[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
            }

            return chainage;
        }

        private static void FillMissingAltitudes(double?[] altitude, double[] chainage)
        {
            int first = Array.FindIndex(altitude, value => value.HasValue);
            for (int i = 0; i < first; i++)
            {
                altitude[i] = altitude[first];
            }

            int previous = first;
            for (int i = first + 1; i < altitude.Length; i++)
            {
                if (!altitude[i].HasValue)
                {
                    continue;
                }

                int next = i;
                double span = chainage[next] - chainage[previous];
                for (int missing = previous + 1; missing < next; missing++)
                {
                    double fraction = span > 1e-9
                        ? (chainage[missing] - chainage[previous]) / span
                        : (double)(missing - previous) / (next - previous);
                    altitude[missing] =
                        altitude[previous].Value +
                        ((altitude[next].Value - altitude[previous].Value) * fraction);
                }

                previous = next;
            }

            for (int i = previous + 1; i < altitude.Length; i++)
            {
                altitude[i] = altitude[previous];
            }
        }
    }
}
