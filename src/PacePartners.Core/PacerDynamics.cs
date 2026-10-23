using System;
using System.Collections.Generic;

namespace PacePartners.Core
{
    /// <summary>
    /// Independent pacer effort. Exactly one of WattsPerKilogram and
    /// DesiredCompletionTimeSeconds must be supplied.
    /// </summary>
    public sealed class PacerDefinition
    {
        public string Name { get; set; }

        public double? WattsPerKilogram { get; set; }

        public double? DesiredCompletionTimeSeconds { get; set; }

        public double MinimumSolverWattsPerKilogram { get; set; }

        public double MaximumSolverWattsPerKilogram { get; set; }

        public PacerDefinition()
        {
            Name = string.Empty;
            MinimumSolverWattsPerKilogram = 0.5;
            MaximumSolverWattsPerKilogram = 8.0;
        }

        public static PacerDefinition ForEffort(string name, double wattsPerKilogram)
        {
            return new PacerDefinition
            {
                Name = name ?? string.Empty,
                WattsPerKilogram = wattsPerKilogram
            };
        }

        public static PacerDefinition ForCompletionTime(string name, double seconds)
        {
            return new PacerDefinition
            {
                Name = name ?? string.Empty,
                DesiredCompletionTimeSeconds = seconds
            };
        }
    }

    public sealed class RiderPhysicsOptions
    {
        public double RiderMassKg { get; set; }

        public double BikeMassKg { get; set; }

        public double DragAreaSquareMeters { get; set; }

        public double AirDensityKgPerCubicMeter { get; set; }

        public double DrivetrainEfficiency { get; set; }

        public double GravityMps2 { get; set; }

        public double StartingSpeedMps { get; set; }

        public double MinimumMovingSpeedMps { get; set; }

        public double MaximumPropulsiveAccelerationMps2 { get; set; }

        public double PowerVariationFraction { get; set; }

        public double PrimaryVariationPeriodSeconds { get; set; }

        public double SecondaryVariationPeriodSeconds { get; set; }

        public int PowerVariationSeed { get; set; }

        public RiderPhysicsOptions()
        {
            RiderMassKg = 75.0;
            BikeMassKg = 10.0;
            DragAreaSquareMeters = 0.32;
            AirDensityKgPerCubicMeter = 1.225;
            DrivetrainEfficiency = 0.97;
            GravityMps2 = 9.80665;
            StartingSpeedMps = 3.0;
            MinimumMovingSpeedMps = 0.5;
            MaximumPropulsiveAccelerationMps2 = 1.2;
            PowerVariationFraction = 0.018;
            PrimaryVariationPeriodSeconds = 53.0;
            SecondaryVariationPeriodSeconds = 89.0;
            PowerVariationSeed = 1;
        }
    }

    public sealed class PacerState
    {
        public double DistanceMeters { get; set; }

        public double ElapsedSeconds { get; set; }

        public double SpeedMps { get; set; }

        public double PowerWatts { get; set; }

        public double PowerMultiplier { get; set; }

        public double AccelerationMps2 { get; set; }

        public bool IsBrakingForLimit { get; set; }
    }

    public sealed class PacerSimulationResult
    {
        public double WattsPerKilogram { get; set; }

        public double NominalPowerWatts { get; set; }

        public double CompletionTimeSeconds { get; set; }

        public List<PacerState> States { get; set; }

        public PacerSimulationResult()
        {
            States = new List<PacerState>();
        }
    }

    public sealed class TargetTimeSolution
    {
        public double WattsPerKilogram { get; set; }

        public double RequestedCompletionTimeSeconds { get; set; }

        public double AchievedCompletionTimeSeconds { get; set; }

        public int Iterations { get; set; }

        public bool IsClampedToSolverRange { get; set; }

        public TargetTimeReachability Reachability { get; set; }

        public bool IsReachable
        {
            get { return Reachability == TargetTimeReachability.Reachable; }
        }
    }

    public enum TargetTimeReachability
    {
        Reachable = 0,
        SlowerThanMinimumEffort = 1,
        FasterThanMaximumEffort = 2
    }

    public sealed class PacerPlan
    {
        public string Name { get; set; }

        public double ResolvedWattsPerKilogram { get; set; }

        public double? RequestedCompletionTimeSeconds { get; set; }

        public TargetTimeSolution TargetTimeSolution { get; set; }

        public PacerSimulationResult Simulation { get; set; }

        public PacerPlan()
        {
            Name = string.Empty;
            Simulation = new PacerSimulationResult();
        }
    }

    /// <summary>
    /// Smooth, deterministic low-frequency effort movement. There is no random
    /// per-tick speed noise: the multiplier is continuous in elapsed time.
    /// </summary>
    public static class DeterministicPowerVariation
    {
        public static double GetMultiplier(
            double elapsedSeconds,
            double maximumFraction,
            double primaryPeriodSeconds,
            double secondaryPeriodSeconds,
            int seed)
        {
            if (!IsFinite(elapsedSeconds) || elapsedSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            }

            if (!IsFinite(maximumFraction) ||
                maximumFraction < 0.0 ||
                maximumFraction >= 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumFraction));
            }

            if (!IsFinite(primaryPeriodSeconds) || primaryPeriodSeconds <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(primaryPeriodSeconds));
            }

            if (!IsFinite(secondaryPeriodSeconds) || secondaryPeriodSeconds <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(secondaryPeriodSeconds));
            }

            if (maximumFraction == 0.0)
            {
                return 1.0;
            }

            double phaseA = Phase(seed, 0x9e3779b9u);
            double phaseB = Phase(seed, 0x85ebca6bu);
            double primary = Math.Sin(
                ((2.0 * Math.PI * elapsedSeconds) / primaryPeriodSeconds) + phaseA);
            double secondary = 0.35 * Math.Sin(
                ((2.0 * Math.PI * elapsedSeconds) / secondaryPeriodSeconds) + phaseB);
            double normalized = (primary + secondary) / 1.35;
            return 1.0 + (maximumFraction * normalized);
        }

        private static double Phase(int seed, uint salt)
        {
            uint value = unchecked((uint)seed) ^ salt;
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            double unit = value / ((double)uint.MaxValue + 1.0);
            return unit * 2.0 * Math.PI;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>
    /// Distance-domain constant-power bicycle dynamics. Original FIT speed is
    /// intentionally absent. Gravity, rolling resistance, aerodynamic drag,
    /// surface roughness, and the derived safe-speed envelope determine motion.
    /// </summary>
    public static class PacerSimulator
    {
        public static PacerSimulationResult Simulate(
            RouteProfile profile,
            double wattsPerKilogram)
        {
            return Simulate(profile, wattsPerKilogram, new RiderPhysicsOptions());
        }

        public static PacerSimulationResult Simulate(
            RouteProfile profile,
            double wattsPerKilogram,
            RiderPhysicsOptions options)
        {
            ValidateProfile(profile);
            ValidateOptions(options);
            if (!IsFinite(wattsPerKilogram) || wattsPerKilogram <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(wattsPerKilogram));
            }

            double riderPower = wattsPerKilogram * options.RiderMassKg;
            List<PacerState> states = new List<PacerState>(profile.Samples.Count);
            double speed = Math.Min(
                options.StartingSpeedMps,
                profile.Samples[0].SafeSpeedMps);
            speed = Math.Max(0.1, speed);
            double elapsed = 0.0;
            double initialMultiplier = GetPowerMultiplier(elapsed, options);
            states.Add(new PacerState
            {
                DistanceMeters = profile.Samples[0].DistanceMeters,
                ElapsedSeconds = elapsed,
                SpeedMps = speed,
                PowerWatts = riderPower * initialMultiplier,
                PowerMultiplier = initialMultiplier,
                AccelerationMps2 = 0.0,
                IsBrakingForLimit = false
            });

            double totalMass = options.RiderMassKg + options.BikeMassKg;
            for (int i = 0; i < profile.Samples.Count - 1; i++)
            {
                RouteProfileSample current = profile.Samples[i];
                RouteProfileSample next = profile.Samples[i + 1];
                double distance = next.DistanceMeters - current.DistanceMeters;
                double multiplier = GetPowerMultiplier(elapsed, options);
                double crankPower = riderPower * multiplier;
                double wheelPower = crankPower * options.DrivetrainEfficiency;
                double gradient = (current.Gradient + next.Gradient) * 0.5;
                double rollingResistance =
                    (current.RollingResistanceCoefficient +
                     next.RollingResistanceCoefficient) * 0.5;
                double roughness =
                    (current.RoughnessFactor + next.RoughnessFactor) * 0.5;
                double slopeScale = Math.Sqrt(1.0 + (gradient * gradient));
                double slopeSin = gradient / slopeScale;
                double slopeCos = 1.0 / slopeScale;
                double rollingForce =
                    totalMass *
                    options.GravityMps2 *
                    slopeCos *
                    rollingResistance *
                    roughness;
                double gravityForce =
                    totalMass * options.GravityMps2 * slopeSin;
                double aerodynamicForce =
                    0.5 *
                    options.AirDensityKgPerCubicMeter *
                    options.DragAreaSquareMeters *
                    speed *
                    speed;
                double effectiveForceSpeed = Math.Max(
                    speed,
                    options.MinimumMovingSpeedMps);
                double propulsiveForce = wheelPower / effectiveForceSpeed;
                double unconstrainedAcceleration =
                    (propulsiveForce -
                     rollingForce -
                     gravityForce -
                     aerodynamicForce) / totalMass;
                double propulsiveAcceleration = Math.Min(
                    options.MaximumPropulsiveAccelerationMps2,
                    unconstrainedAcceleration);
                double squaredSpeed =
                    (speed * speed) + (2.0 * propulsiveAcceleration * distance);
                double unconstrainedNextSpeed = Math.Sqrt(Math.Max(
                    options.MinimumMovingSpeedMps *
                    options.MinimumMovingSpeedMps,
                    squaredSpeed));
                double nextSpeed = Math.Min(
                    unconstrainedNextSpeed,
                    next.SafeSpeedMps);
                nextSpeed = Math.Max(0.1, nextSpeed);
                double averageSpeed = Math.Max(0.1, (speed + nextSpeed) * 0.5);
                double sectionTime = distance / averageSpeed;
                double actualAcceleration =
                    ((nextSpeed * nextSpeed) - (speed * speed)) /
                    (2.0 * distance);
                bool braking = unconstrainedNextSpeed > next.SafeSpeedMps + 1e-7;

                elapsed += sectionTime;
                speed = nextSpeed;
                double stateMultiplier = GetPowerMultiplier(elapsed, options);
                states.Add(new PacerState
                {
                    DistanceMeters = next.DistanceMeters,
                    ElapsedSeconds = elapsed,
                    SpeedMps = speed,
                    PowerWatts = riderPower * stateMultiplier,
                    PowerMultiplier = stateMultiplier,
                    AccelerationMps2 = actualAcceleration,
                    IsBrakingForLimit = braking
                });
            }

            return new PacerSimulationResult
            {
                WattsPerKilogram = wattsPerKilogram,
                NominalPowerWatts = riderPower,
                CompletionTimeSeconds = elapsed,
                States = states
            };
        }

        private static double GetPowerMultiplier(
            double elapsedSeconds,
            RiderPhysicsOptions options)
        {
            return DeterministicPowerVariation.GetMultiplier(
                elapsedSeconds,
                options.PowerVariationFraction,
                options.PrimaryVariationPeriodSeconds,
                options.SecondaryVariationPeriodSeconds,
                options.PowerVariationSeed);
        }

        private static void ValidateProfile(RouteProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (profile.Samples == null || profile.Samples.Count < 2)
            {
                throw new ArgumentException(
                    "A route profile needs at least two samples.",
                    nameof(profile));
            }

            for (int i = 0; i < profile.Samples.Count; i++)
            {
                RouteProfileSample sample = profile.Samples[i];
                if (sample == null)
                {
                    throw new ArgumentException(
                        "Route profile sample " + i + " is null.",
                        nameof(profile));
                }

                if (!IsFinite(sample.DistanceMeters) ||
                    !IsFinite(sample.Gradient) ||
                    !IsFinite(sample.RollingResistanceCoefficient) ||
                    !IsFinite(sample.RoughnessFactor) ||
                    !IsFinite(sample.SafeSpeedMps) ||
                    sample.RollingResistanceCoefficient <= 0.0 ||
                    sample.RoughnessFactor <= 0.0 ||
                    sample.SafeSpeedMps <= 0.0)
                {
                    throw new ArgumentException(
                        "Route profile sample " + i + " has invalid physical values.",
                        nameof(profile));
                }

                if (i > 0 &&
                    sample.DistanceMeters <= profile.Samples[i - 1].DistanceMeters)
                {
                    throw new ArgumentException(
                        "Route profile distances must be strictly increasing.",
                        nameof(profile));
                }
            }
        }

        private static void ValidateOptions(RiderPhysicsOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            RequirePositive(options.RiderMassKg, nameof(options.RiderMassKg));
            RequirePositive(options.BikeMassKg, nameof(options.BikeMassKg));
            RequirePositive(options.DragAreaSquareMeters, nameof(options.DragAreaSquareMeters));
            RequirePositive(options.AirDensityKgPerCubicMeter, nameof(options.AirDensityKgPerCubicMeter));
            RequirePositive(options.DrivetrainEfficiency, nameof(options.DrivetrainEfficiency));
            if (options.DrivetrainEfficiency > 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.DrivetrainEfficiency));
            }

            RequirePositive(options.GravityMps2, nameof(options.GravityMps2));
            RequirePositive(options.StartingSpeedMps, nameof(options.StartingSpeedMps));
            RequirePositive(options.MinimumMovingSpeedMps, nameof(options.MinimumMovingSpeedMps));
            RequirePositive(
                options.MaximumPropulsiveAccelerationMps2,
                nameof(options.MaximumPropulsiveAccelerationMps2));
            if (!IsFinite(options.PowerVariationFraction) ||
                options.PowerVariationFraction < 0.0 ||
                options.PowerVariationFraction >= 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.PowerVariationFraction));
            }

            RequirePositive(
                options.PrimaryVariationPeriodSeconds,
                nameof(options.PrimaryVariationPeriodSeconds));
            RequirePositive(
                options.SecondaryVariationPeriodSeconds,
                nameof(options.SecondaryVariationPeriodSeconds));
        }

        private static void RequirePositive(double value, string name)
        {
            if (!IsFinite(value) || value <= 0.0)
            {
                throw new ArgumentOutOfRangeException(name);
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    public static class TargetCompletionTimeSolver
    {
        public static TargetTimeSolution Solve(
            RouteProfile profile,
            double desiredCompletionTimeSeconds,
            RiderPhysicsOptions options,
            double minimumWattsPerKilogram,
            double maximumWattsPerKilogram)
        {
            return Solve(
                profile,
                desiredCompletionTimeSeconds,
                options,
                minimumWattsPerKilogram,
                maximumWattsPerKilogram,
                0.25,
                40);
        }

        public static TargetTimeSolution Solve(
            RouteProfile profile,
            double desiredCompletionTimeSeconds,
            RiderPhysicsOptions options,
            double minimumWattsPerKilogram,
            double maximumWattsPerKilogram,
            double timeToleranceSeconds,
            int maximumIterations)
        {
            if (!IsFinite(desiredCompletionTimeSeconds) ||
                desiredCompletionTimeSeconds <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(desiredCompletionTimeSeconds));
            }

            if (!IsFinite(minimumWattsPerKilogram) ||
                minimumWattsPerKilogram <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumWattsPerKilogram));
            }

            if (!IsFinite(maximumWattsPerKilogram) ||
                maximumWattsPerKilogram <= minimumWattsPerKilogram)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumWattsPerKilogram));
            }

            if (!IsFinite(timeToleranceSeconds) || timeToleranceSeconds <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(timeToleranceSeconds));
            }

            if (maximumIterations <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumIterations));
            }

            PacerSimulationResult slow =
                PacerSimulator.Simulate(profile, minimumWattsPerKilogram, options);
            if (desiredCompletionTimeSeconds >= slow.CompletionTimeSeconds)
            {
                return CreateSolution(
                    minimumWattsPerKilogram,
                    desiredCompletionTimeSeconds,
                    slow.CompletionTimeSeconds,
                    0,
                    TargetTimeReachability.SlowerThanMinimumEffort);
            }

            PacerSimulationResult fast =
                PacerSimulator.Simulate(profile, maximumWattsPerKilogram, options);
            if (desiredCompletionTimeSeconds <= fast.CompletionTimeSeconds)
            {
                return CreateSolution(
                    maximumWattsPerKilogram,
                    desiredCompletionTimeSeconds,
                    fast.CompletionTimeSeconds,
                    0,
                    TargetTimeReachability.FasterThanMaximumEffort);
            }

            double low = minimumWattsPerKilogram;
            double high = maximumWattsPerKilogram;
            PacerSimulationResult best = null;
            double bestEffort = low;
            int iterations = 0;

            for (int i = 0; i < maximumIterations; i++)
            {
                iterations = i + 1;
                double effort = (low + high) * 0.5;
                PacerSimulationResult candidate =
                    PacerSimulator.Simulate(profile, effort, options);
                best = candidate;
                bestEffort = effort;
                double error =
                    candidate.CompletionTimeSeconds - desiredCompletionTimeSeconds;
                if (Math.Abs(error) <= timeToleranceSeconds)
                {
                    break;
                }

                if (error > 0.0)
                {
                    low = effort;
                }
                else
                {
                    high = effort;
                }
            }

            return CreateSolution(
                bestEffort,
                desiredCompletionTimeSeconds,
                best.CompletionTimeSeconds,
                iterations,
                TargetTimeReachability.Reachable);
        }

        private static TargetTimeSolution CreateSolution(
            double effort,
            double requestedTime,
            double achievedTime,
            int iterations,
            TargetTimeReachability reachability)
        {
            return new TargetTimeSolution
            {
                WattsPerKilogram = effort,
                RequestedCompletionTimeSeconds = requestedTime,
                AchievedCompletionTimeSeconds = achievedTime,
                Iterations = iterations,
                IsClampedToSolverRange =
                    reachability != TargetTimeReachability.Reachable,
                Reachability = reachability
            };
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    public static class PacerPlanner
    {
        public static PacerPlan CreatePlan(
            RouteProfile profile,
            PacerDefinition definition,
            RiderPhysicsOptions physicsOptions)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            bool hasEffort = definition.WattsPerKilogram.HasValue;
            bool hasTime = definition.DesiredCompletionTimeSeconds.HasValue;
            if (hasEffort == hasTime)
            {
                throw new ArgumentException(
                    "Specify exactly one of watts/kg or desired completion time.",
                    nameof(definition));
            }

            double resolvedEffort;
            TargetTimeSolution solution = null;
            if (hasEffort)
            {
                resolvedEffort = definition.WattsPerKilogram.Value;
            }
            else
            {
                solution = TargetCompletionTimeSolver.Solve(
                    profile,
                    definition.DesiredCompletionTimeSeconds.Value,
                    physicsOptions,
                    definition.MinimumSolverWattsPerKilogram,
                    definition.MaximumSolverWattsPerKilogram);
                resolvedEffort = solution.WattsPerKilogram;
            }

            PacerSimulationResult simulation =
                PacerSimulator.Simulate(profile, resolvedEffort, physicsOptions);
            return new PacerPlan
            {
                Name = definition.Name ?? string.Empty,
                ResolvedWattsPerKilogram = resolvedEffort,
                RequestedCompletionTimeSeconds =
                    definition.DesiredCompletionTimeSeconds,
                TargetTimeSolution = solution,
                Simulation = simulation
            };
        }
    }
}
