using System;
using GTA;
using GTA.Math;
using PacePartners.Core;

namespace PacePartners.Mod.Runtime
{
    internal sealed class LiveSurfaceSample
    {
        public RouteSurfaceObservation Observation { get; set; }

        public double HitWorldZ { get; set; }
    }

    /// <summary>
    /// Samples only streamed route geometry close to an active rider. A short
    /// ray centred on the FIT Z avoids accidentally classifying a bridge deck as
    /// the road below (or a tunnel as the terrain above it).
    /// </summary>
    internal sealed class LiveSurfaceSampler
    {
        private readonly double minimumSpacingMeters;
        private readonly int minimumIntervalMilliseconds;
        private int lastSampleTime;
        private double lastSampleDistance = double.NegativeInfinity;

        public LiveSurfaceSampler(
            double minimumSpacingMeters,
            int minimumIntervalMilliseconds)
        {
            this.minimumSpacingMeters = Math.Max(1.0, minimumSpacingMeters);
            this.minimumIntervalMilliseconds = Math.Max(50, minimumIntervalMilliseconds);
        }

        public bool TrySample(
            RouteProfile profile,
            double routeDistanceMeters,
            Entity ignoredEntity,
            bool ignoreRaycast,
            out LiveSurfaceSample sample)
        {
            sample = null;
            if (ignoreRaycast ||
                profile == null ||
                profile.Samples == null ||
                profile.Samples.Count == 0)
            {
                return false;
            }

            int now = Environment.TickCount;
            if (unchecked(now - lastSampleTime) < minimumIntervalMilliseconds ||
                Math.Abs(routeDistanceMeters - lastSampleDistance) < minimumSpacingMeters)
            {
                return false;
            }

            RouteProfileSample routeSample = InterpolateProfile(profile, routeDistanceMeters);
            Vector3 origin = new Vector3(
                (float)routeSample.X,
                (float)routeSample.Y,
                (float)(routeSample.Z + 1.75));
            Vector3 target = new Vector3(
                origin.X,
                origin.Y,
                (float)(routeSample.Z - 4.0));
            RaycastResult hit = World.Raycast(
                origin,
                target,
                IntersectFlags.Map,
                ignoredEntity);

            lastSampleTime = now;
            lastSampleDistance = routeSample.DistanceMeters;
            if (!hit.DidHit)
            {
                return false;
            }

            sample = new LiveSurfaceSample
            {
                Observation = new RouteSurfaceObservation(
                    routeSample.DistanceMeters,
                    (int)hit.MaterialHash),
                HitWorldZ = hit.HitPosition.Z
            };
            return true;
        }

        public void Reset()
        {
            lastSampleTime = 0;
            lastSampleDistance = double.NegativeInfinity;
        }

        private static RouteProfileSample InterpolateProfile(
            RouteProfile profile,
            double requestedDistance)
        {
            double distance = Math.Max(
                0.0,
                Math.Min(profile.TotalDistanceMeters, requestedDistance));
            int low = 0;
            int high = profile.Samples.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                double candidate = profile.Samples[middle].DistanceMeters;
                if (candidate < distance)
                {
                    low = middle + 1;
                }
                else if (candidate > distance)
                {
                    high = middle - 1;
                }
                else
                {
                    return profile.Samples[middle];
                }
            }

            if (low <= 0)
            {
                return profile.Samples[0];
            }

            if (low >= profile.Samples.Count)
            {
                return profile.Samples[profile.Samples.Count - 1];
            }

            RouteProfileSample before = profile.Samples[low - 1];
            RouteProfileSample after = profile.Samples[low];
            return distance - before.DistanceMeters <= after.DistanceMeters - distance
                ? before
                : after;
        }
    }
}
