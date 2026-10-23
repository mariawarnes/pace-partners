using System;
using System.Collections.Generic;
using PacePartners.Core;
using PacePartners.Mod.Runtime;

namespace PacePartners.Core.Tests
{
    internal static class Program
    {
        private static int passed;

        private static int Main()
        {
            Action[] tests =
            {
                DistanceInterpolationAndWindowedProjection,
                LocalProgressCannotJumpToNearbyReturnLeg,
                SmoothedGradientUsesPhysicalFitAltitude,
                ImplausibleGradientIsClampedUnlessOverridden,
                DenseStraightAndArcCurvature,
                SurfaceInfluenceAndManualOverrides,
                BrakingAndAccelerationEnvelopes,
                ConstantPowerRespondsToGradeAndSurface,
                PowerVariationIsSlowAndDeterministic,
                TargetCompletionTimeEffortSolver
            };

            try
            {
                for (int i = 0; i < tests.Length; i++)
                {
                    tests[i]();
                    passed++;
                    Console.WriteLine("PASS " + tests[i].Method.Name);
                }

                Console.WriteLine("All " + passed + " deterministic tests passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "FAIL after " + passed + " passed tests: " + exception);
                return 1;
            }
        }

        private static void DistanceInterpolationAndWindowedProjection()
        {
            List<RouteTracePoint> points = new List<RouteTracePoint>
            {
                Point(-10.0, 0.0, 0.0),
                Point(10.0, 0.0, 2.0),
                Point(10.0, 10.0, 3.0),
                Point(0.0, 0.0, 4.0),
                Point(0.0, 10.0, 5.0)
            };
            RouteGeometry geometry = new RouteGeometry(points);
            RoutePosition midpoint = geometry.Interpolate(10.0);
            Near(0.0, midpoint.X, 1e-9, "distance interpolation X");
            Near(1.0, midpoint.AltitudeMeters, 1e-9, "altitude interpolation");

            RouteProjection early = geometry.ProjectNear(0.0, 0.0, 10.0, 3.0);
            RouteProjection late = geometry.ProjectNear(0.0, 0.0, 34.0, 4.0);
            True(early.DistanceMeters < 15.0, "early crossing branch selected");
            True(late.DistanceMeters > 30.0, "late crossing branch selected");
        }

        private static void SmoothedGradientUsesPhysicalFitAltitude()
        {
            RouteProfileInput input = LineInput(200.0, 1.0, 0.05, Road());
            for (int i = 0; i < input.TracePoints.Count; i++)
            {
                // GTA world Z deliberately disagrees with FIT altitude.
                input.TracePoints[i].Z = 500.0 - (input.TracePoints[i].X * 0.3);
            }

            RouteProfile profile = RouteProfileBuilder.Build(
                input,
                Options(1.0, 30.0));
            RouteProfileSample middle = At(profile, 100.0);
            Near(0.05, middle.Gradient, 1e-10, "25 m smoothed FIT grade");
            Near(5.0, middle.AltitudeMeters, 1e-9, "physical FIT altitude");
            Near(470.0, middle.Z, 1e-9, "GTA Z remains independent");
        }

        private static void LocalProgressCannotJumpToNearbyReturnLeg()
        {
            RouteProfileInput input = new RouteProfileInput
            {
                DefaultSurface = Road()
            };
            for (int x = 0; x <= 50; x++)
            {
                input.TracePoints.Add(Point(x, 0.0, 0.0));
            }

            input.TracePoints.Add(Point(50.0, 1.0, 0.0));
            for (int x = 49; x >= 0; x--)
            {
                input.TracePoints.Add(Point(x, 1.0, 0.0));
            }

            RouteProfile profile = RouteProfileBuilder.Build(
                input,
                Options(1.0, 20.0));
            var tracker = new RouteProgressTracker(profile);
            tracker.Reset(0.0);
            RouteProgress progress = tracker.Update(2.0, 0.9, 4.0);
            True(
                progress.DistanceMeters <= 4.0 + 1e-9,
                "nearby return leg cannot jump progress");
            tracker.Reset(0.0);
            for (int frame = 0; frame < 120; frame++)
            {
                progress = tracker.Update(2.0, 0.9, 0.01);
            }

            True(
                progress.DistanceMeters < 2.0,
                "stationary crossing cannot ratchet through return leg");
        }

        private static void DenseStraightAndArcCurvature()
        {
            RouteProfile line = RouteProfileBuilder.Build(
                LineInput(150.0, 0.5, 0.0, Road()),
                Options(1.0, 30.0));
            Near(
                0.0,
                At(line, 75.0).SignedCurvaturePerMeter,
                1e-10,
                "straight curvature");
            Near(1.0, At(line, 75.0).Straightness, 1e-10, "straight straightness");

            const double radius = 50.0;
            RouteProfileInput arcInput = new RouteProfileInput
            {
                DefaultSurface = Road()
            };
            const int pointCount = 401;
            for (int i = 0; i < pointCount; i++)
            {
                double angle = (Math.PI * 0.5 * i) / (pointCount - 1);
                arcInput.TracePoints.Add(new RouteTracePoint(
                    radius * Math.Cos(angle),
                    radius * Math.Sin(angle),
                    0.0,
                    0.0));
            }

            RouteProfile arc = RouteProfileBuilder.Build(
                arcInput,
                Options(1.0, 30.0));
            RouteProfileSample arcMiddle = At(arc, arc.TotalDistanceMeters * 0.5);
            Near(1.0 / radius, arcMiddle.SignedCurvaturePerMeter, 0.0002, "arc curvature");
            True(arcMiddle.Straightness < 0.997, "arc is less straight than line");
            True(arcMiddle.Straightness > 0.97, "arc straightness remains normalized");
        }

        private static void ImplausibleGradientIsClampedUnlessOverridden()
        {
            RouteProfileInput input = LineInput(100.0, 1.0, 2.0, Road());
            input.Overrides.Add(new RouteProfileOverride
            {
                Id = "known-ramp",
                StartDistanceMeters = 70.0,
                EndDistanceMeters = 80.0,
                Gradient = 0.60
            });
            RouteProfileOptions options = Options(1.0, 30.0);
            options.MaximumAbsoluteGradient = 0.45;
            RouteProfile profile = RouteProfileBuilder.Build(input, options);

            Near(0.45, At(profile, 40.0).Gradient, 1e-12, "calculated gradient clamp");
            Near(0.60, At(profile, 75.0).Gradient, 1e-12, "manual gradient precedence");
        }

        private static void SurfaceInfluenceAndManualOverrides()
        {
            SurfaceMaterialDefinition gravel = new SurfaceMaterialDefinition
            {
                MaterialHash = 22,
                Name = "Gravel",
                RollingResistanceCoefficient = 0.025,
                GripCoefficient = 0.55,
                RoughnessFactor = 1.35,
                SurfaceSpeedLimitMps = 12.0
            };
            RouteProfileInput input = LineInput(120.0, 1.0, 0.0, Road());
            input.MaterialDefinitions.Add(gravel);
            input.SurfaceObservations.Add(new RouteSurfaceObservation(60.0, 22));
            input.Overrides.Add(new RouteProfileOverride
            {
                Id = "bridge",
                StartDistanceMeters = 80.0,
                EndDistanceMeters = 90.0,
                IgnoreSurfaceRaycast = true,
                MaterialHash = 22,
                RollingResistanceCoefficient = 0.03,
                MaximumSafeSpeedMps = 8.0,
                ZOffsetMeters = 2.5
            });
            RouteProfileOptions options = Options(1.0, 30.0);
            options.SurfaceObservationInfluenceRadiusMeters = 10.0;

            RouteProfile profile = RouteProfileBuilder.Build(input, options);
            Equal(0, At(profile, 20.0).MaterialHash, "unsampled route falls back");
            Equal(22, At(profile, 60.0).MaterialHash, "nearby raycast applies");
            Equal(0, At(profile, 72.0).MaterialHash, "raycast influence is bounded");
            RouteProfileSample bridge = At(profile, 85.0);
            Equal(22, bridge.MaterialHash, "manual bridge material");
            Near(0.03, bridge.RollingResistanceCoefficient, 1e-12, "manual rolling resistance");
            Near(2.5, bridge.Z, 1e-12, "manual GTA Z offset");
            True(bridge.RawCornerSpeedMps <= 8.0, "manual problem-corner cap");
        }

        private static void BrakingAndAccelerationEnvelopes()
        {
            RouteProfileInput input = new RouteProfileInput
            {
                DefaultSurface = Road()
            };
            for (int x = 0; x <= 100; x++)
            {
                input.TracePoints.Add(Point(x, 0.0, 0.0));
            }

            for (int y = 1; y <= 100; y++)
            {
                input.TracePoints.Add(Point(100.0, y, 0.0));
            }

            RouteProfileOptions options = Options(1.0, 20.0);
            options.StartSpeedMps = 20.0;
            options.MaximumBrakingDecelerationMps2 = 2.5;
            options.MaximumAccelerationMps2 = 0.7;
            options.CornerLookAheadSeconds = 6.0;
            RouteProfile profile = RouteProfileBuilder.Build(input, options);

            int corner = IndexOfMinimumRawSpeed(profile);
            True(corner > 10, "corner has braking approach");
            RouteProfileSample approach = profile.Samples[corner - 8];
            True(
                approach.BrakingEnvelopeSpeedMps < approach.RawCornerSpeedMps,
                "backward braking envelope anticipates corner");
            True(
                profile.Samples[corner - 1].SafeSpeedMps >
                profile.Samples[corner].SafeSpeedMps,
                "speed falls into corner");
            True(
                profile.Samples[corner + 5].SafeSpeedMps >
                profile.Samples[corner].SafeSpeedMps,
                "forward envelope accelerates out");

            for (int i = 0; i < profile.Samples.Count - 1; i++)
            {
                double distance =
                    profile.Samples[i + 1].DistanceMeters -
                    profile.Samples[i].DistanceMeters;
                double requiredDeceleration =
                    ((profile.Samples[i].BrakingEnvelopeSpeedMps *
                      profile.Samples[i].BrakingEnvelopeSpeedMps) -
                     (profile.Samples[i + 1].BrakingEnvelopeSpeedMps *
                      profile.Samples[i + 1].BrakingEnvelopeSpeedMps)) /
                    (2.0 * distance);
                True(
                    requiredDeceleration <=
                    options.MaximumBrakingDecelerationMps2 + 1e-8,
                    "braking envelope has no look-ahead discontinuity");
            }

            double exitDistance =
                profile.Samples[corner + 5].DistanceMeters -
                profile.Samples[corner].DistanceMeters;
            double exitAcceleration =
                ((profile.Samples[corner + 5].SafeSpeedMps *
                  profile.Samples[corner + 5].SafeSpeedMps) -
                 (profile.Samples[corner].SafeSpeedMps *
                  profile.Samples[corner].SafeSpeedMps)) /
                (2.0 * exitDistance);
            True(exitAcceleration <= 0.7 + 1e-8, "acceleration envelope obeyed");
        }

        private static void ConstantPowerRespondsToGradeAndSurface()
        {
            RouteProfileOptions profileOptions = Options(2.0, 35.0);
            RouteProfile flat = RouteProfileBuilder.Build(
                LineInput(1500.0, 2.0, 0.0, Road()),
                profileOptions);
            RouteProfile uphill = RouteProfileBuilder.Build(
                LineInput(1500.0, 2.0, 0.05, Road()),
                profileOptions);
            RouteProfile downhill = RouteProfileBuilder.Build(
                LineInput(1500.0, 2.0, -0.05, Road()),
                profileOptions);
            SurfaceMaterialDefinition gravel = new SurfaceMaterialDefinition
            {
                MaterialHash = 9,
                Name = "Gravel",
                RollingResistanceCoefficient = 0.025,
                GripCoefficient = 0.65,
                RoughnessFactor = 1.25,
                SurfaceSpeedLimitMps = 0.0
            };
            RouteProfile gravelProfile = RouteProfileBuilder.Build(
                LineInput(1500.0, 2.0, 0.0, gravel),
                profileOptions);
            RiderPhysicsOptions physics = PhysicsWithoutVariation();

            PacerSimulationResult flatRide =
                PacerSimulator.Simulate(flat, 2.5, physics);
            PacerSimulationResult uphillRide =
                PacerSimulator.Simulate(uphill, 2.5, physics);
            PacerSimulationResult downhillRide =
                PacerSimulator.Simulate(downhill, 2.5, physics);
            PacerSimulationResult gravelRide =
                PacerSimulator.Simulate(gravelProfile, 2.5, physics);

            True(
                uphillRide.CompletionTimeSeconds > flatRide.CompletionTimeSeconds,
                "constant power slows uphill");
            True(
                downhillRide.CompletionTimeSeconds < flatRide.CompletionTimeSeconds,
                "constant power accelerates downhill");
            True(
                gravelRide.CompletionTimeSeconds > flatRide.CompletionTimeSeconds,
                "constant power slows on gravel");
        }

        private static void PowerVariationIsSlowAndDeterministic()
        {
            double minimum = double.MaxValue;
            double maximum = double.MinValue;
            double previous = 0.0;
            for (int second = 0; second <= 300; second++)
            {
                double value = DeterministicPowerVariation.GetMultiplier(
                    second,
                    0.02,
                    53.0,
                    89.0,
                    12345);
                double repeated = DeterministicPowerVariation.GetMultiplier(
                    second,
                    0.02,
                    53.0,
                    89.0,
                    12345);
                Near(value, repeated, 0.0, "variation determinism");
                True(value >= 0.98 && value <= 1.02, "variation stays small");
                if (second > 0)
                {
                    True(Math.Abs(value - previous) < 0.004, "variation changes slowly");
                }

                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                previous = value;
            }

            True(maximum - minimum > 0.025, "variation moves over long periods");
        }

        private static void TargetCompletionTimeEffortSolver()
        {
            RouteProfile profile = RouteProfileBuilder.Build(
                LineInput(1200.0, 2.0, 0.01, Road()),
                Options(2.0, 35.0));
            RiderPhysicsOptions physics = PhysicsWithoutVariation();
            PacerSimulationResult reference =
                PacerSimulator.Simulate(profile, 2.5, physics);
            TargetTimeSolution solution = TargetCompletionTimeSolver.Solve(
                profile,
                reference.CompletionTimeSeconds,
                physics,
                0.5,
                6.0,
                0.05,
                50);
            True(solution.IsReachable, "reference target is reachable");
            Near(2.5, solution.WattsPerKilogram, 0.02, "target-time watts/kg");
            Near(
                reference.CompletionTimeSeconds,
                solution.AchievedCompletionTimeSeconds,
                0.05,
                "target completion time");

            PacerSimulationResult slow =
                PacerSimulator.Simulate(profile, 0.5, physics);
            PacerSimulationResult fast =
                PacerSimulator.Simulate(profile, 6.0, physics);
            TargetTimeSolution tooSlow = TargetCompletionTimeSolver.Solve(
                profile,
                slow.CompletionTimeSeconds + 60.0,
                physics,
                0.5,
                6.0);
            TargetTimeSolution tooFast = TargetCompletionTimeSolver.Solve(
                profile,
                Math.Max(1.0, fast.CompletionTimeSeconds - 60.0),
                physics,
                0.5,
                6.0);
            Equal(
                TargetTimeReachability.SlowerThanMinimumEffort,
                tooSlow.Reachability,
                "slow unreachable target report");
            Equal(
                TargetTimeReachability.FasterThanMaximumEffort,
                tooFast.Reachability,
                "fast unreachable target report");
        }

        private static RouteProfileInput LineInput(
            double length,
            double pointSpacing,
            double gradient,
            SurfaceMaterialDefinition defaultSurface)
        {
            RouteProfileInput input = new RouteProfileInput
            {
                DefaultSurface = defaultSurface
            };
            int count = (int)Math.Floor(length / pointSpacing);
            for (int i = 0; i <= count; i++)
            {
                double x = i * pointSpacing;
                input.TracePoints.Add(new RouteTracePoint(
                    x,
                    0.0,
                    gradient * x,
                    gradient * x));
            }

            if (input.TracePoints[input.TracePoints.Count - 1].X < length)
            {
                input.TracePoints.Add(new RouteTracePoint(
                    length,
                    0.0,
                    gradient * length,
                    gradient * length));
            }

            return input;
        }

        private static SurfaceMaterialDefinition Road()
        {
            return new SurfaceMaterialDefinition
            {
                MaterialHash = 0,
                Name = "Road",
                RollingResistanceCoefficient = 0.0045,
                GripCoefficient = 0.9,
                RoughnessFactor = 1.0,
                SurfaceSpeedLimitMps = 0.0
            };
        }

        private static RouteProfileOptions Options(double spacing, double maximumSpeed)
        {
            return new RouteProfileOptions
            {
                SampleSpacingMeters = spacing,
                GradientWindowMeters = 25.0,
                MaximumAbsoluteGradient = 0.45,
                CurvatureWindowMeters = 12.0,
                StraightnessWindowMeters = 25.0,
                MaximumRouteSpeedMps = maximumSpeed,
                CornerGripUtilization = 0.6,
                MinimumCurvatureForLimit = 0.0005,
                CornerLookAheadSeconds = 6.0,
                MaximumBrakingDecelerationMps2 = 2.5,
                MaximumAccelerationMps2 = 0.8,
                StartSpeedMps = 3.0,
                GravityMps2 = 9.80665,
                SurfaceObservationInfluenceRadiusMeters = 15.0
            };
        }

        private static RiderPhysicsOptions PhysicsWithoutVariation()
        {
            return new RiderPhysicsOptions
            {
                PowerVariationFraction = 0.0
            };
        }

        private static RouteTracePoint Point(double x, double y, double altitude)
        {
            return new RouteTracePoint(x, y, altitude, altitude);
        }

        private static RouteProfileSample At(RouteProfile profile, double distance)
        {
            int best = 0;
            double bestDifference = double.MaxValue;
            for (int i = 0; i < profile.Samples.Count; i++)
            {
                double difference =
                    Math.Abs(profile.Samples[i].DistanceMeters - distance);
                if (difference < bestDifference)
                {
                    bestDifference = difference;
                    best = i;
                }
            }

            return profile.Samples[best];
        }

        private static int IndexOfMinimumRawSpeed(RouteProfile profile)
        {
            int best = 0;
            for (int i = 1; i < profile.Samples.Count; i++)
            {
                if (profile.Samples[i].RawCornerSpeedMps <
                    profile.Samples[best].RawCornerSpeedMps)
                {
                    best = i;
                }
            }

            return best;
        }

        private static void Near(
            double expected,
            double actual,
            double tolerance,
            string message)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new InvalidOperationException(
                    message + ": expected " + expected + ", actual " + actual);
            }
        }

        private static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    message + ": expected " + expected + ", actual " + actual);
            }
        }
    }
}
