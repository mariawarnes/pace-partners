using System;
using PacePartners.Core;

namespace PacePartners.Mod.Runtime
{
    internal sealed class RouteProgress
    {
        public double DistanceMeters { get; set; }

        public double LateralOffsetMeters { get; set; }

        public int SegmentIndex { get; set; }
    }

    /// <summary>
    /// Projects onto a local, forward-biased route window. A global nearest
    /// point alone can jump onto the wrong arm of a figure eight.
    /// </summary>
    internal sealed class RouteProgressTracker
    {
        private readonly RouteProfile profile;
        private int segmentIndex;
        private double distanceMeters;
        private bool initialized;

        public RouteProgressTracker(RouteProfile profile)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            if (profile.Samples == null || profile.Samples.Count < 2)
            {
                throw new ArgumentException("The route profile has no geometry.", nameof(profile));
            }
        }

        public RouteProgress Update(
            double x,
            double y,
            double maximumAdvanceMeters)
        {
            maximumAdvanceMeters = Math.Max(0.01, maximumAdvanceMeters);
            int start;
            int end;
            if (!initialized)
            {
                start = 0;
                end = profile.Samples.Count - 2;
            }
            else
            {
                double spacing = Math.Max(0.25, profile.Options.SampleSpacingMeters);
                int behind = Math.Max(3, (int)Math.Ceiling(6.0 / spacing));
                int ahead = Math.Max(
                    5,
                    (int)Math.Ceiling((maximumAdvanceMeters + 3.0) / spacing));
                start = Math.Max(0, segmentIndex - behind);
                end = Math.Min(profile.Samples.Count - 2, segmentIndex + ahead);
            }

            ProjectionCandidate candidate = FindNearest(x, y, start, end);
            if (candidate == null && initialized)
            {
                candidate = FindNearest(x, y, 0, profile.Samples.Count - 2);
            }

            double projectedDistance = candidate.DistanceMeters;
            if (initialized)
            {
                // Tolerate a little steering wobble without ever travelling
                // backwards through a crossing or parallel road section.
                projectedDistance = Math.Min(
                    distanceMeters + maximumAdvanceMeters,
                    Math.Max(distanceMeters - 0.75, projectedDistance));
            }

            distanceMeters = Math.Max(distanceMeters, projectedDistance);
            segmentIndex = FindSegmentForDistance(distanceMeters, candidate.SegmentIndex);
            initialized = true;
            return new RouteProgress
            {
                DistanceMeters = distanceMeters,
                LateralOffsetMeters = Math.Sqrt(candidate.SquaredOffset),
                SegmentIndex = segmentIndex
            };
        }

        public void Reset(double distance = 0.0)
        {
            distanceMeters = Math.Max(
                0.0,
                Math.Min(profile.TotalDistanceMeters, distance));
            segmentIndex = FindSegmentForDistance(distanceMeters, 0);
            initialized = true;
        }

        private ProjectionCandidate FindNearest(
            double x,
            double y,
            int start,
            int end)
        {
            ProjectionCandidate best = null;
            for (int i = start; i <= end; i++)
            {
                RouteProfileSample a = profile.Samples[i];
                RouteProfileSample b = profile.Samples[i + 1];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double lengthSquared = (dx * dx) + (dy * dy);
                if (lengthSquared <= 1e-10)
                {
                    continue;
                }

                double fraction =
                    (((x - a.X) * dx) + ((y - a.Y) * dy)) / lengthSquared;
                fraction = Math.Max(0.0, Math.Min(1.0, fraction));
                double projectedX = a.X + (dx * fraction);
                double projectedY = a.Y + (dy * fraction);
                double offsetX = x - projectedX;
                double offsetY = y - projectedY;
                double squaredOffset = (offsetX * offsetX) + (offsetY * offsetY);
                if (best == null || squaredOffset < best.SquaredOffset)
                {
                    best = new ProjectionCandidate
                    {
                        DistanceMeters = a.DistanceMeters +
                            ((b.DistanceMeters - a.DistanceMeters) * fraction),
                        SquaredOffset = squaredOffset,
                        SegmentIndex = i
                    };
                }
            }

            return best;
        }

        private int FindSegmentForDistance(double distance, int hint)
        {
            int index = Math.Max(0, Math.Min(profile.Samples.Count - 2, hint));
            while (index > 0 &&
                   profile.Samples[index].DistanceMeters > distance)
            {
                index--;
            }

            while (index < profile.Samples.Count - 2 &&
                   profile.Samples[index + 1].DistanceMeters < distance)
            {
                index++;
            }

            return index;
        }

        private sealed class ProjectionCandidate
        {
            public double DistanceMeters { get; set; }

            public double SquaredOffset { get; set; }

            public int SegmentIndex { get; set; }
        }
    }
}
