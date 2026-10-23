using System;
using System.Collections.Generic;

namespace PacePartners.Core
{
    /// <summary>
    /// Converts dense FIT geometry and raw surface observations into a regularly
    /// spaced physical route profile. This class is deterministic and has no GTA
    /// or file-system dependencies.
    /// </summary>
    public static class RouteProfileBuilder
    {
        public const string CurrentAlgorithmVersion = "route-profile-v1";

        public static RouteProfile Build(RouteProfileInput input)
        {
            return Build(input, new RouteProfileOptions());
        }

        public static RouteProfile Build(RouteProfileInput input, RouteProfileOptions options)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            ValidateOptions(options);
            RouteGeometry geometry = new RouteGeometry(input.TracePoints);
            SurfaceResolver surfaceResolver = new SurfaceResolver(
                input,
                geometry.TotalDistanceMeters,
                options.SurfaceObservationInfluenceRadiusMeters);
            List<RouteProfileOverride> overrides = ValidateAndCopyOverrides(input.Overrides, geometry.TotalDistanceMeters);
            List<RouteProfileSample> samples = CreateSamples(geometry, surfaceResolver, overrides, options);

            ApplyBrakingEnvelope(samples, options);
            ApplyAccelerationEnvelope(samples, options);

            return new RouteProfile
            {
                AlgorithmVersion = CurrentAlgorithmVersion,
                TotalDistanceMeters = geometry.TotalDistanceMeters,
                Options = CopyOptions(options),
                Samples = samples
            };
        }

        private static List<RouteProfileSample> CreateSamples(
            RouteGeometry geometry,
            SurfaceResolver surfaceResolver,
            IList<RouteProfileOverride> overrides,
            RouteProfileOptions options)
        {
            double totalDistance = geometry.TotalDistanceMeters;
            int regularSampleCount = (int)Math.Floor(totalDistance / options.SampleSpacingMeters);
            List<RouteProfileSample> samples = new List<RouteProfileSample>(regularSampleCount + 2);

            for (int i = 0; i <= regularSampleCount; i++)
            {
                double distance = i * options.SampleSpacingMeters;
                if (distance > totalDistance)
                {
                    distance = totalDistance;
                }

                samples.Add(CreateSample(geometry, surfaceResolver, overrides, options, distance));
            }

            if (samples[samples.Count - 1].DistanceMeters < totalDistance - 1e-7)
            {
                samples.Add(CreateSample(geometry, surfaceResolver, overrides, options, totalDistance));
            }

            return samples;
        }

        private static RouteProfileSample CreateSample(
            RouteGeometry geometry,
            SurfaceResolver surfaceResolver,
            IList<RouteProfileOverride> overrides,
            RouteProfileOptions options,
            double distance)
        {
            RoutePosition position = geometry.Interpolate(distance);
            ResolvedSurface surface = surfaceResolver.Resolve(distance);
            double gradient = CalculateSmoothedGradient(
                geometry,
                distance,
                options.GradientWindowMeters);
            gradient = Clamp(
                gradient,
                -options.MaximumAbsoluteGradient,
                options.MaximumAbsoluteGradient);
            double curvature = CalculateSignedCurvature(
                geometry,
                distance,
                options.CurvatureWindowMeters);
            double straightness = CalculateStraightness(
                geometry,
                distance,
                options.StraightnessWindowMeters);
            double? manualMaximumSpeed = null;
            double z = position.Z;

            for (int i = 0; i < overrides.Count; i++)
            {
                RouteProfileOverride item = overrides[i];
                if (!ContainsDistance(item, distance))
                {
                    continue;
                }

                if (item.IgnoreSurfaceRaycast == true)
                {
                    surface = surfaceResolver.ResolveDefault();
                }

                if (item.MaterialHash.HasValue)
                {
                    surface = surfaceResolver.ResolveMaterial(item.MaterialHash.Value);
                }

                if (item.RollingResistanceCoefficient.HasValue)
                {
                    surface.RollingResistanceCoefficient = item.RollingResistanceCoefficient.Value;
                }

                if (item.GripCoefficient.HasValue)
                {
                    surface.GripCoefficient = item.GripCoefficient.Value;
                }

                if (item.RoughnessFactor.HasValue)
                {
                    surface.RoughnessFactor = item.RoughnessFactor.Value;
                }

                if (item.SurfaceSpeedLimitMps.HasValue)
                {
                    surface.SurfaceSpeedLimitMps = item.SurfaceSpeedLimitMps.Value;
                }

                if (item.Gradient.HasValue)
                {
                    gradient = item.Gradient.Value;
                }

                if (item.CurvatureMultiplier.HasValue)
                {
                    curvature *= item.CurvatureMultiplier.Value;
                }

                if (item.MaximumSafeSpeedMps.HasValue)
                {
                    manualMaximumSpeed = manualMaximumSpeed.HasValue
                        ? Math.Min(manualMaximumSpeed.Value, item.MaximumSafeSpeedMps.Value)
                        : item.MaximumSafeSpeedMps.Value;
                }

                if (item.ZOffsetMeters.HasValue)
                {
                    z += item.ZOffsetMeters.Value;
                }
            }

            ValidateResolvedSurface(surface, distance);
            double rawCornerSpeed = CalculateRawCornerSpeed(
                curvature,
                surface,
                manualMaximumSpeed,
                options);

            return new RouteProfileSample
            {
                DistanceMeters = distance,
                X = position.X,
                Y = position.Y,
                Z = z,
                AltitudeMeters = position.AltitudeMeters,
                HeadingRadians = geometry.HeadingRadians(distance, 2.0),
                Gradient = gradient,
                SignedCurvaturePerMeter = curvature,
                Straightness = straightness,
                MaterialHash = surface.MaterialHash,
                RollingResistanceCoefficient = surface.RollingResistanceCoefficient,
                GripCoefficient = surface.GripCoefficient,
                RoughnessFactor = surface.RoughnessFactor,
                SurfaceSpeedLimitMps = surface.SurfaceSpeedLimitMps,
                RawCornerSpeedMps = rawCornerSpeed,
                BrakingEnvelopeSpeedMps = rawCornerSpeed,
                AccelerationEnvelopeSpeedMps = rawCornerSpeed,
                SafeSpeedMps = rawCornerSpeed
            };
        }

        private static double CalculateSmoothedGradient(
            RouteGeometry geometry,
            double distance,
            double requestedWindowMeters)
        {
            double window = Math.Min(requestedWindowMeters, geometry.TotalDistanceMeters);
            double left;
            double right;
            GetWindowBounds(geometry.TotalDistanceMeters, distance, window, out left, out right);
            double width = right - left;
            if (width <= 1e-8)
            {
                return 0.0;
            }

            // Eleven evenly spaced FIT-altitude samples give a symmetric
            // least-squares slope while suppressing point-to-point altitude noise.
            const int sampleCount = 11;
            double meanDistance = (left + right) * 0.5;
            double altitudeSum = 0.0;
            double[] altitudes = new double[sampleCount];
            double[] offsets = new double[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                double fraction = (double)i / (sampleCount - 1);
                double sampleDistance = left + (width * fraction);
                offsets[i] = sampleDistance - meanDistance;
                altitudes[i] = geometry.Interpolate(sampleDistance).AltitudeMeters;
                altitudeSum += altitudes[i];
            }

            double meanAltitude = altitudeSum / sampleCount;
            double numerator = 0.0;
            double denominator = 0.0;
            for (int i = 0; i < sampleCount; i++)
            {
                numerator += offsets[i] * (altitudes[i] - meanAltitude);
                denominator += offsets[i] * offsets[i];
            }

            return denominator > 0.0 ? numerator / denominator : 0.0;
        }

        private static double CalculateSignedCurvature(
            RouteGeometry geometry,
            double distance,
            double requestedWindowMeters)
        {
            double left;
            double right;
            GetWindowBounds(
                geometry.TotalDistanceMeters,
                distance,
                Math.Min(requestedWindowMeters, geometry.TotalDistanceMeters),
                out left,
                out right);
            if (right - left <= 1e-6)
            {
                return 0.0;
            }

            double middle = (left + right) * 0.5;
            RoutePosition a = geometry.Interpolate(left);
            RoutePosition b = geometry.Interpolate(middle);
            RoutePosition c = geometry.Interpolate(right);
            double ab = Distance(a.X, a.Y, b.X, b.Y);
            double bc = Distance(b.X, b.Y, c.X, c.Y);
            double ca = Distance(c.X, c.Y, a.X, a.Y);
            double divisor = ab * bc * ca;
            if (divisor <= 1e-10)
            {
                return 0.0;
            }

            double twiceArea =
                ((b.X - a.X) * (c.Y - a.Y)) -
                ((b.Y - a.Y) * (c.X - a.X));
            return (2.0 * twiceArea) / divisor;
        }

        private static double CalculateStraightness(
            RouteGeometry geometry,
            double distance,
            double requestedWindowMeters)
        {
            double left;
            double right;
            GetWindowBounds(
                geometry.TotalDistanceMeters,
                distance,
                Math.Min(requestedWindowMeters, geometry.TotalDistanceMeters),
                out left,
                out right);
            double pathLength = right - left;
            if (pathLength <= 1e-8)
            {
                return 1.0;
            }

            RoutePosition a = geometry.Interpolate(left);
            RoutePosition b = geometry.Interpolate(right);
            double chord = Distance(a.X, a.Y, b.X, b.Y);
            return Clamp(chord / pathLength, 0.0, 1.0);
        }

        private static double CalculateRawCornerSpeed(
            double signedCurvature,
            ResolvedSurface surface,
            double? manualMaximumSpeed,
            RouteProfileOptions options)
        {
            double speed = options.MaximumRouteSpeedMps;
            double curvature = Math.Abs(signedCurvature);
            if (curvature >= options.MinimumCurvatureForLimit)
            {
                double roughnessPenalty = Math.Max(1.0, surface.RoughnessFactor);
                double usableGrip = surface.GripCoefficient / roughnessPenalty;
                double lateralAcceleration =
                    usableGrip *
                    options.GravityMps2 *
                    options.CornerGripUtilization;
                speed = Math.Min(speed, Math.Sqrt(lateralAcceleration / curvature));
            }

            if (surface.SurfaceSpeedLimitMps > 0.0)
            {
                speed = Math.Min(speed, surface.SurfaceSpeedLimitMps);
            }

            if (manualMaximumSpeed.HasValue)
            {
                speed = Math.Min(speed, manualMaximumSpeed.Value);
            }

            return Math.Max(0.1, speed);
        }

        private static void ApplyBrakingEnvelope(
            IList<RouteProfileSample> samples,
            RouteProfileOptions options)
        {
            int last = samples.Count - 1;
            samples[last].BrakingEnvelopeSpeedMps = samples[last].RawCornerSpeedMps;

            for (int i = last - 1; i >= 0; i--)
            {
                RouteProfileSample current = samples[i];
                RouteProfileSample next = samples[i + 1];
                double distance = next.DistanceMeters - current.DistanceMeters;
                double grip = Math.Min(current.GripCoefficient, next.GripCoefficient);
                double roughness = Math.Max(current.RoughnessFactor, next.RoughnessFactor);
                double gripFactor = Clamp(grip / Math.Max(1.0, roughness), 0.20, 1.0);
                double deceleration =
                    options.MaximumBrakingDecelerationMps2 * gripFactor;
                double candidate = Math.Sqrt(
                    (next.BrakingEnvelopeSpeedMps * next.BrakingEnvelopeSpeedMps) +
                    (2.0 * deceleration * distance));
                // Never truncate the physical braking distance at the nominal
                // look-ahead horizon. A severe bend may require more than six
                // seconds of braking; clipping there creates an impossible
                // one-sample speed drop. CornerLookAheadSeconds remains the
                // minimum runtime anticipation horizon, while this backward
                // pass guarantees a continuous, achievable envelope.
                current.BrakingEnvelopeSpeedMps = Math.Min(
                    current.RawCornerSpeedMps,
                    candidate);
            }
        }

        private static void ApplyAccelerationEnvelope(
            IList<RouteProfileSample> samples,
            RouteProfileOptions options)
        {
            RouteProfileSample first = samples[0];
            first.AccelerationEnvelopeSpeedMps = Math.Min(
                options.StartSpeedMps,
                first.BrakingEnvelopeSpeedMps);
            first.SafeSpeedMps = first.AccelerationEnvelopeSpeedMps;

            for (int i = 1; i < samples.Count; i++)
            {
                RouteProfileSample previous = samples[i - 1];
                RouteProfileSample current = samples[i];
                double distance = current.DistanceMeters - previous.DistanceMeters;
                double gripFactor = Clamp(
                    current.GripCoefficient / Math.Max(1.0, current.RoughnessFactor),
                    0.20,
                    1.0);
                double acceleration = options.MaximumAccelerationMps2 * gripFactor;
                double candidate = Math.Sqrt(
                    (previous.SafeSpeedMps * previous.SafeSpeedMps) +
                    (2.0 * acceleration * distance));
                current.AccelerationEnvelopeSpeedMps = candidate;
                current.SafeSpeedMps = Math.Min(
                    current.BrakingEnvelopeSpeedMps,
                    candidate);
            }
        }

        private static bool ContainsDistance(RouteProfileOverride item, double distance)
        {
            return distance >= item.StartDistanceMeters - 1e-8 &&
                   distance <= item.EndDistanceMeters + 1e-8;
        }

        private static List<RouteProfileOverride> ValidateAndCopyOverrides(
            IList<RouteProfileOverride> overrides,
            double totalDistance)
        {
            List<RouteProfileOverride> result = new List<RouteProfileOverride>();
            if (overrides == null)
            {
                return result;
            }

            for (int i = 0; i < overrides.Count; i++)
            {
                RouteProfileOverride item = overrides[i];
                if (item == null)
                {
                    throw new ArgumentException("Route override " + i + " is null.", nameof(overrides));
                }

                if (!IsFinite(item.StartDistanceMeters) ||
                    !IsFinite(item.EndDistanceMeters) ||
                    item.StartDistanceMeters < 0.0 ||
                    item.EndDistanceMeters < item.StartDistanceMeters ||
                    item.EndDistanceMeters > totalDistance + 1e-6)
                {
                    throw new ArgumentException("Route override " + i + " has an invalid distance range.", nameof(overrides));
                }

                ValidateOptionalPositive(item.RollingResistanceCoefficient, "rolling resistance", i);
                ValidateOptionalPositive(item.GripCoefficient, "grip", i);
                ValidateOptionalPositive(item.RoughnessFactor, "roughness", i);
                ValidateOptionalNonNegative(item.SurfaceSpeedLimitMps, "surface speed limit", i);
                ValidateOptionalPositive(item.MaximumSafeSpeedMps, "maximum safe speed", i);
                ValidateOptionalNonNegative(item.CurvatureMultiplier, "curvature multiplier", i);
                if (item.Gradient.HasValue && !IsFinite(item.Gradient.Value))
                {
                    throw new ArgumentException("Route override " + i + " has an invalid gradient.", nameof(overrides));
                }

                if (item.ZOffsetMeters.HasValue && !IsFinite(item.ZOffsetMeters.Value))
                {
                    throw new ArgumentException("Route override " + i + " has an invalid Z offset.", nameof(overrides));
                }

                result.Add(item);
            }

            return result;
        }

        private static void ValidateOptions(RouteProfileOptions options)
        {
            RequirePositive(options.SampleSpacingMeters, nameof(options.SampleSpacingMeters));
            RequirePositive(options.GradientWindowMeters, nameof(options.GradientWindowMeters));
            RequirePositive(
                options.MaximumAbsoluteGradient,
                nameof(options.MaximumAbsoluteGradient));
            RequirePositive(options.CurvatureWindowMeters, nameof(options.CurvatureWindowMeters));
            RequirePositive(options.StraightnessWindowMeters, nameof(options.StraightnessWindowMeters));
            RequirePositive(options.MaximumRouteSpeedMps, nameof(options.MaximumRouteSpeedMps));
            RequirePositive(options.CornerGripUtilization, nameof(options.CornerGripUtilization));
            RequireNonNegative(options.MinimumCurvatureForLimit, nameof(options.MinimumCurvatureForLimit));
            RequirePositive(options.CornerLookAheadSeconds, nameof(options.CornerLookAheadSeconds));
            RequirePositive(options.MaximumBrakingDecelerationMps2, nameof(options.MaximumBrakingDecelerationMps2));
            RequirePositive(options.MaximumAccelerationMps2, nameof(options.MaximumAccelerationMps2));
            RequirePositive(options.StartSpeedMps, nameof(options.StartSpeedMps));
            RequirePositive(options.GravityMps2, nameof(options.GravityMps2));
            RequirePositive(
                options.SurfaceObservationInfluenceRadiusMeters,
                nameof(options.SurfaceObservationInfluenceRadiusMeters));
        }

        private static RouteProfileOptions CopyOptions(RouteProfileOptions options)
        {
            return new RouteProfileOptions
            {
                SampleSpacingMeters = options.SampleSpacingMeters,
                GradientWindowMeters = options.GradientWindowMeters,
                MaximumAbsoluteGradient = options.MaximumAbsoluteGradient,
                CurvatureWindowMeters = options.CurvatureWindowMeters,
                StraightnessWindowMeters = options.StraightnessWindowMeters,
                MaximumRouteSpeedMps = options.MaximumRouteSpeedMps,
                CornerGripUtilization = options.CornerGripUtilization,
                MinimumCurvatureForLimit = options.MinimumCurvatureForLimit,
                CornerLookAheadSeconds = options.CornerLookAheadSeconds,
                MaximumBrakingDecelerationMps2 = options.MaximumBrakingDecelerationMps2,
                MaximumAccelerationMps2 = options.MaximumAccelerationMps2,
                StartSpeedMps = options.StartSpeedMps,
                GravityMps2 = options.GravityMps2,
                SurfaceObservationInfluenceRadiusMeters =
                    options.SurfaceObservationInfluenceRadiusMeters
            };
        }

        private static void GetWindowBounds(
            double totalDistance,
            double center,
            double window,
            out double left,
            out double right)
        {
            double half = window * 0.5;
            left = center - half;
            right = center + half;

            if (left < 0.0)
            {
                right = Math.Min(totalDistance, right - left);
                left = 0.0;
            }

            if (right > totalDistance)
            {
                left = Math.Max(0.0, left - (right - totalDistance));
                right = totalDistance;
            }
        }

        private static double Distance(double ax, double ay, double bx, double by)
        {
            double dx = bx - ax;
            double dy = by - ay;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static void ValidateResolvedSurface(ResolvedSurface surface, double distance)
        {
            if (!IsFinite(surface.RollingResistanceCoefficient) ||
                surface.RollingResistanceCoefficient <= 0.0 ||
                !IsFinite(surface.GripCoefficient) ||
                surface.GripCoefficient <= 0.0 ||
                !IsFinite(surface.RoughnessFactor) ||
                surface.RoughnessFactor <= 0.0 ||
                !IsFinite(surface.SurfaceSpeedLimitMps) ||
                surface.SurfaceSpeedLimitMps < 0.0)
            {
                throw new InvalidOperationException(
                    "The resolved surface at " + distance + " m has invalid physical properties.");
            }
        }

        private static void ValidateOptionalPositive(double? value, string name, int index)
        {
            if (value.HasValue && (!IsFinite(value.Value) || value.Value <= 0.0))
            {
                throw new ArgumentException(
                    "Route override " + index + " has invalid " + name + ".");
            }
        }

        private static void ValidateOptionalNonNegative(double? value, string name, int index)
        {
            if (value.HasValue && (!IsFinite(value.Value) || value.Value < 0.0))
            {
                throw new ArgumentException(
                    "Route override " + index + " has invalid " + name + ".");
            }
        }

        private static void RequirePositive(double value, string name)
        {
            if (!IsFinite(value) || value <= 0.0)
            {
                throw new ArgumentOutOfRangeException(name);
            }
        }

        private static void RequireNonNegative(double value, string name)
        {
            if (!IsFinite(value) || value < 0.0)
            {
                throw new ArgumentOutOfRangeException(name);
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return value < minimum ? minimum : (value > maximum ? maximum : value);
        }

        private sealed class ResolvedSurface
        {
            public int MaterialHash;
            public double RollingResistanceCoefficient;
            public double GripCoefficient;
            public double RoughnessFactor;
            public double SurfaceSpeedLimitMps;

            public ResolvedSurface Copy()
            {
                return new ResolvedSurface
                {
                    MaterialHash = MaterialHash,
                    RollingResistanceCoefficient = RollingResistanceCoefficient,
                    GripCoefficient = GripCoefficient,
                    RoughnessFactor = RoughnessFactor,
                    SurfaceSpeedLimitMps = SurfaceSpeedLimitMps
                };
            }
        }

        private sealed class SurfaceResolver
        {
            private readonly Dictionary<int, ResolvedSurface> definitions;
            private readonly List<RouteSurfaceObservation> observations;
            private readonly ResolvedSurface fallback;
            private readonly double influenceRadiusMeters;

            public SurfaceResolver(
                RouteProfileInput input,
                double totalDistance,
                double influenceRadiusMeters)
            {
                SurfaceMaterialDefinition defaultSurface =
                    input.DefaultSurface ?? SurfaceMaterialDefinition.Asphalt(0);
                fallback = FromDefinition(defaultSurface);
                this.influenceRadiusMeters = influenceRadiusMeters;
                ValidateResolvedSurface(fallback, 0.0);
                definitions = new Dictionary<int, ResolvedSurface>();

                if (input.MaterialDefinitions != null)
                {
                    for (int i = 0; i < input.MaterialDefinitions.Count; i++)
                    {
                        SurfaceMaterialDefinition definition = input.MaterialDefinitions[i];
                        if (definition == null)
                        {
                            throw new ArgumentException(
                                "Surface material definition " + i + " is null.",
                                nameof(input));
                        }

                        ResolvedSurface resolved = FromDefinition(definition);
                        ValidateResolvedSurface(resolved, 0.0);
                        definitions[definition.MaterialHash] = resolved;
                    }
                }

                observations = new List<RouteSurfaceObservation>();
                if (input.SurfaceObservations != null)
                {
                    for (int i = 0; i < input.SurfaceObservations.Count; i++)
                    {
                        RouteSurfaceObservation observation = input.SurfaceObservations[i];
                        ValidateObservation(observation, i, totalDistance);
                        observations.Add(observation);
                    }
                }

                observations.Sort(CompareObservations);
            }

            public ResolvedSurface Resolve(double distance)
            {
                if (observations.Count == 0)
                {
                    return fallback.Copy();
                }

                int index = FindNearestObservationIndex(distance);
                RouteSurfaceObservation observation = observations[index];
                if (Math.Abs(observation.DistanceMeters - distance) >
                    influenceRadiusMeters)
                {
                    return fallback.Copy();
                }

                ResolvedSurface surface = ResolveMaterial(observation.MaterialHash);
                if (observation.RollingResistanceCoefficient.HasValue)
                {
                    surface.RollingResistanceCoefficient =
                        observation.RollingResistanceCoefficient.Value;
                }

                if (observation.GripCoefficient.HasValue)
                {
                    surface.GripCoefficient = observation.GripCoefficient.Value;
                }

                if (observation.RoughnessFactor.HasValue)
                {
                    surface.RoughnessFactor = observation.RoughnessFactor.Value;
                }

                if (observation.SurfaceSpeedLimitMps.HasValue)
                {
                    surface.SurfaceSpeedLimitMps = observation.SurfaceSpeedLimitMps.Value;
                }

                return surface;
            }

            public ResolvedSurface ResolveDefault()
            {
                return fallback.Copy();
            }

            public ResolvedSurface ResolveMaterial(int materialHash)
            {
                ResolvedSurface definition;
                if (definitions.TryGetValue(materialHash, out definition))
                {
                    return definition.Copy();
                }

                ResolvedSurface result = fallback.Copy();
                result.MaterialHash = materialHash;
                return result;
            }

            private int FindNearestObservationIndex(double distance)
            {
                int low = 0;
                int high = observations.Count - 1;
                while (low <= high)
                {
                    int middle = low + ((high - low) / 2);
                    double candidate = observations[middle].DistanceMeters;
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
                        return middle;
                    }
                }

                if (low <= 0)
                {
                    return 0;
                }

                if (low >= observations.Count)
                {
                    return observations.Count - 1;
                }

                double lowerDifference =
                    distance - observations[low - 1].DistanceMeters;
                double upperDifference =
                    observations[low].DistanceMeters - distance;
                return lowerDifference <= upperDifference ? low - 1 : low;
            }

            private static int CompareObservations(
                RouteSurfaceObservation a,
                RouteSurfaceObservation b)
            {
                return a.DistanceMeters.CompareTo(b.DistanceMeters);
            }

            private static ResolvedSurface FromDefinition(
                SurfaceMaterialDefinition definition)
            {
                return new ResolvedSurface
                {
                    MaterialHash = definition.MaterialHash,
                    RollingResistanceCoefficient =
                        definition.RollingResistanceCoefficient,
                    GripCoefficient = definition.GripCoefficient,
                    RoughnessFactor = definition.RoughnessFactor,
                    SurfaceSpeedLimitMps = definition.SurfaceSpeedLimitMps
                };
            }

            private static void ValidateObservation(
                RouteSurfaceObservation observation,
                int index,
                double totalDistance)
            {
                if (observation == null)
                {
                    throw new ArgumentException(
                        "Surface observation " + index + " is null.");
                }

                if (!IsFinite(observation.DistanceMeters) ||
                    observation.DistanceMeters < 0.0 ||
                    observation.DistanceMeters > totalDistance + 1e-6)
                {
                    throw new ArgumentException(
                        "Surface observation " + index + " has an invalid distance.");
                }

                ValidateObservationValue(
                    observation.RollingResistanceCoefficient,
                    false,
                    "rolling resistance",
                    index);
                ValidateObservationValue(
                    observation.GripCoefficient,
                    false,
                    "grip",
                    index);
                ValidateObservationValue(
                    observation.RoughnessFactor,
                    false,
                    "roughness",
                    index);
                ValidateObservationValue(
                    observation.SurfaceSpeedLimitMps,
                    true,
                    "surface speed limit",
                    index);
            }

            private static void ValidateObservationValue(
                double? value,
                bool allowZero,
                string name,
                int index)
            {
                if (!value.HasValue)
                {
                    return;
                }

                bool rangeInvalid = allowZero ? value.Value < 0.0 : value.Value <= 0.0;
                if (!IsFinite(value.Value) || rangeInvalid)
                {
                    throw new ArgumentException(
                        "Surface observation " + index + " has invalid " + name + ".");
                }
            }
        }
    }
}
