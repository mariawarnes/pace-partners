using System;
using System.Collections.Generic;
using System.Linq;
using PacePartners.Core;

namespace PacePartners.Mod.Runtime
{
    internal sealed class PacerManager : IDisposable
    {
        private enum PaceTier
        {
            Beginner,
            Intermediate,
            Expert
        }

        private readonly List<PacerAgent> agents = new List<PacerAgent>();
        private readonly List<PaceTier> agentTiers = new List<PaceTier>();
        private IReadOnlyList<string> activePaceLabels;
        private string activePacingGender;
        private int activeOpacity;

        public IReadOnlyList<PacerAgent> Agents => agents;

        public bool IsActive => agents.Count > 0;

        public void Start(
            RouteProfile profile,
            IList<PacerPlan> plans,
            bool loop,
            IReadOnlyList<string> paceLabels,
            string pacingGender,
            int opacity)
        {
            DisposeAgents();
            if (plans == null)
            {
                return;
            }

            activePaceLabels = paceLabels;
            activePacingGender = pacingGender;
            activeOpacity = opacity;
            List<PacerPlan> activePlans =
                plans.Where(item => item != null).ToList();
            int index = 0;
            try
            {
                foreach (PacerPlan plan in activePlans)
                {
                    PaceTier tier = ClassifyPace(
                        plan.ResolvedWattsPerKilogram,
                        pacingGender);
                    agents.Add(new PacerAgent(
                        profile,
                        plan,
                        SelectPaceLabel(
                            paceLabels,
                            index,
                            tier,
                            pacingGender),
                        index,
                        loop,
                        SelectBicycleModel(tier),
                        SelectRiderModel(tier, pacingGender),
                        opacity));
                    agentTiers.Add(tier);
                    index++;
                }
            }
            catch
            {
                DisposeAgents();
                throw;
            }
        }

        public void Update(double deltaSeconds)
        {
            for (int i = 0; i < agents.Count; i++)
            {
                agents[i].Update(deltaSeconds);
            }
        }

        public void Replan(
            RouteProfile profile,
            IList<PacerPlan> plans)
        {
            if (plans == null || plans.Count != agents.Count)
            {
                throw new ArgumentException(
                    "A replacement plan is required for every active pacer.",
                    nameof(plans));
            }

            for (int i = 0; i < agents.Count; i++)
            {
                PacerPlan plan = plans[i];
                PaceTier tier = ClassifyPace(
                    plan.ResolvedWattsPerKilogram,
                    activePacingGender);
                agents[i].ReplacePlan(
                    profile,
                    plan,
                    SelectPaceLabel(
                        activePaceLabels,
                        i,
                        tier,
                        activePacingGender));
                if (tier != agentTiers[i])
                {
                    agents[i].ReplaceAppearance(
                        SelectBicycleModel(tier),
                        SelectRiderModel(tier, activePacingGender),
                        activeOpacity,
                        i);
                    agentTiers[i] = tier;
                }
            }
        }

        /// <summary>
        /// Pure CPU work. Call this off the GTA script thread; spawning and
        /// native task control remain in Start/Update.
        /// </summary>
        public static List<PacerPlan> BuildPlans(
            RouteProfile profile,
            IList<PacerDefinition> definitions,
            RiderPhysicsOptions physics)
        {
            var plans = new List<PacerPlan>();
            if (definitions == null)
            {
                return plans;
            }

            for (int i = 0; i < definitions.Count; i++)
            {
                PacerDefinition definition = definitions[i];
                if (definition == null)
                {
                    continue;
                }

                RiderPhysicsOptions individualPhysics = CopyPhysics(physics);
                individualPhysics.PowerVariationSeed += i * 7919;
                plans.Add(PacerPlanner.CreatePlan(
                    profile,
                    definition,
                    individualPhysics));
            }

            return plans;
        }

        public void Dispose()
        {
            DisposeAgents();
        }

        private void DisposeAgents()
        {
            for (int i = 0; i < agents.Count; i++)
            {
                agents[i].Dispose();
            }

            agents.Clear();
            agentTiers.Clear();
            activePaceLabels = null;
            activePacingGender = null;
            activeOpacity = 0;
        }

        private static RiderPhysicsOptions CopyPhysics(RiderPhysicsOptions source)
        {
            source = source ?? new RiderPhysicsOptions();
            return new RiderPhysicsOptions
            {
                RiderMassKg = source.RiderMassKg,
                BikeMassKg = source.BikeMassKg,
                DragAreaSquareMeters = source.DragAreaSquareMeters,
                AirDensityKgPerCubicMeter = source.AirDensityKgPerCubicMeter,
                DrivetrainEfficiency = source.DrivetrainEfficiency,
                GravityMps2 = source.GravityMps2,
                StartingSpeedMps = source.StartingSpeedMps,
                MinimumMovingSpeedMps = source.MinimumMovingSpeedMps,
                MaximumPropulsiveAccelerationMps2 =
                    source.MaximumPropulsiveAccelerationMps2,
                PowerVariationFraction = source.PowerVariationFraction,
                PrimaryVariationPeriodSeconds =
                    source.PrimaryVariationPeriodSeconds,
                SecondaryVariationPeriodSeconds =
                    source.SecondaryVariationPeriodSeconds,
                PowerVariationSeed = source.PowerVariationSeed
            };
        }

        private static string SelectPaceLabel(
            IReadOnlyList<string> paceLabels,
            int index,
            PaceTier tier,
            string pacingGender)
        {
            if (paceLabels != null &&
                index >= 0 &&
                index < paceLabels.Count &&
                !string.IsNullOrWhiteSpace(paceLabels[index]))
            {
                return paceLabels[index].Trim();
            }

            return tier + (IsMale(pacingGender) ? " Male" : " Female");
        }

        private static PaceTier ClassifyPace(
            double wattsPerKilogram,
            string pacingGender)
        {
            bool male = IsMale(pacingGender);
            double beginnerUpper = male ? 2.45 : 2.05;
            double intermediateUpper = male ? 3.55 : 3.15;
            return wattsPerKilogram <= beginnerUpper
                ? PaceTier.Beginner
                : wattsPerKilogram <= intermediateUpper
                    ? PaceTier.Intermediate
                    : PaceTier.Expert;
        }

        private static bool IsMale(string pacingGender)
        {
            return string.Equals(
                pacingGender,
                "Male",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string SelectBicycleModel(PaceTier tier)
        {
            switch (tier)
            {
                case PaceTier.Beginner:
                    return "CRUISER";
                case PaceTier.Intermediate:
                    return "SCORCHER";
                default:
                    return "TRIBIKE3";
            }
        }

        private static string SelectRiderModel(
            PaceTier tier,
            string pacingGender)
        {
            if (IsMale(pacingGender))
            {
                switch (tier)
                {
                    case PaceTier.Beginner:
                        return "a_m_y_hipster_01";
                    case PaceTier.Intermediate:
                        return "a_m_y_cyclist_01";
                    default:
                        return "a_m_y_roadcyc_01";
                }
            }

            switch (tier)
            {
                case PaceTier.Beginner:
                    return "a_f_y_hipster_01";
                case PaceTier.Intermediate:
                    return "a_f_y_fitness_01";
                default:
                    return "a_f_y_runner_01";
            }
        }
    }
}
