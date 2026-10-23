using System;
using System.Drawing;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using PacePartners.Core;

namespace PacePartners.Mod.Runtime
{
    /// <summary>
    /// Turns a distance-domain physics plan into a persistent GTA bicycle task.
    /// Route progress is geometric, not "one FIT record per second", and driving
    /// tasks are only replaced when their steering target is consumed or lost.
    /// </summary>
    internal sealed class PacerAgent : IDisposable
    {
        private const int ControlVehicleTaskId = 169;

        private static readonly VehicleDrivingFlags DrivingFlags =
            VehicleDrivingFlags.AllowGoingWrongWay |
            VehicleDrivingFlags.UseShortCutLinks |
            VehicleDrivingFlags.SteerAroundStationaryVehicles |
            VehicleDrivingFlags.SteerAroundObjects |
            VehicleDrivingFlags.SteerAroundPeds |
            VehicleDrivingFlags.SwerveAroundAllVehicles;

        private RouteProfile profile;
        private PacerPlan plan;
        private RouteProgressTracker progressTracker;
        private string paceLabel;
        private readonly bool loop;
        private readonly float lateralOffset;
        private Vehicle vehicle;
        private Ped ped;
        private Blip blip;
        private TextElement overheadLabel;
        private double routeDistance;
        private double taskTargetDistance = double.NegativeInfinity;
        private double commandedSpeed;
        private double offRouteSeconds;
        private double elapsedSeconds;
        private double inactiveTaskSeconds;
        private int lastTaskIssueTime;
        private double loopReturnStallSeconds;
        private double lastLoopReturnDistance = double.MaxValue;
        private Vector3 lastTrackedPosition;
        private bool hasTrackedPosition;
        private bool returningToStart;
        private bool finished;

        public PacerAgent(
            RouteProfile profile,
            PacerPlan plan,
            string paceLabel,
            int index,
            bool loop,
            string bicycleModel,
            string riderModel,
            int opacity)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
            this.paceLabel = NormalizeLabel(paceLabel);
            this.loop = loop;
            lateralOffset = LaneOffset(index);
            progressTracker = new RouteProgressTracker(profile);
            progressTracker.Reset(0.0);
            Spawn(
                bicycleModel ?? "TRIBIKE3",
                riderModel ?? "a_m_y_roadcyc_01",
                opacity,
                index);
        }

        public string Name => plan.Name;

        public double RouteDistanceMeters => routeDistance;

        public double ResolvedWattsPerKilogram => plan.ResolvedWattsPerKilogram;

        public TargetTimeSolution TargetTimeSolution => plan.TargetTimeSolution;

        public bool IsFinished => finished;

        public Vehicle Vehicle => vehicle;

        public Ped Ped => ped;

        public void Update(double deltaSeconds)
        {
            if (!Exists(vehicle) || !Exists(ped))
            {
                return;
            }

            DrawOverheadLabel();
            if (finished)
            {
                return;
            }

            deltaSeconds = Math.Max(0.001, Math.Min(0.25, deltaSeconds));
            elapsedSeconds += deltaSeconds;
            if (!ped.IsInVehicle(vehicle))
            {
                ped.SetIntoVehicle(vehicle, VehicleSeat.Driver);
                taskTargetDistance = double.NegativeInfinity;
            }

            if (returningToStart)
            {
                UpdateLoopReturn(deltaSeconds);
                return;
            }

            Vector3 currentPosition = vehicle.Position;
            double actualMovement = hasTrackedPosition
                ? currentPosition.DistanceTo2D(lastTrackedPosition)
                : 0.0;
            lastTrackedPosition = currentPosition;
            hasTrackedPosition = true;
            double plausibleAdvance = Math.Max(
                0.01,
                Math.Min(
                    actualMovement + (0.5 * deltaSeconds),
                    (Math.Max(1.0, commandedSpeed) * deltaSeconds) + 1.0));
            RouteProgress progress = progressTracker.Update(
                currentPosition.X,
                currentPosition.Y,
                plausibleAdvance);
            routeDistance = progress.DistanceMeters;
            UpdateRecovery(progress, deltaSeconds);

            if (routeDistance >= profile.TotalDistanceMeters - 1.5)
            {
                if (loop)
                {
                    returningToStart = true;
                    loopReturnStallSeconds = 0.0;
                    lastLoopReturnDistance = double.MaxValue;
                    taskTargetDistance = double.NegativeInfinity;
                    UpdateLoopReturn(deltaSeconds);
                }
                else
                {
                    Finish();
                }

                return;
            }

            RouteProfileSample currentSection = SampleAtDistance(routeDistance);
            double cornerScanDistance = Math.Max(
                18.0,
                commandedSpeed * profile.Options.CornerLookAheadSeconds);
            RouteProfileSample technicalSection = MostTechnicalSample(
                routeDistance,
                Math.Min(
                    profile.TotalDistanceMeters,
                    routeDistance + cornerScanDistance));
            bool technical =
                technicalSection.Straightness < 0.97 ||
                Math.Abs(technicalSection.SignedCurvaturePerMeter) > 0.006;
            double desiredSpeed = SpeedAtDistance(routeDistance);
            if (plan.TargetTimeSolution != null && !technical)
            {
                double scheduleError =
                    elapsedSeconds - ElapsedAtDistance(routeDistance);
                double scheduleMultiplier = Math.Max(
                    0.95,
                    Math.Min(1.05, 1.0 + ((scheduleError / 60.0) * 0.03)));
                desiredSpeed = Math.Min(
                    currentSection.SafeSpeedMps,
                    desiredSpeed * scheduleMultiplier);
            }
            double maximumIncrease =
                profile.Options.MaximumAccelerationMps2 * deltaSeconds;
            double maximumDecrease =
                profile.Options.MaximumBrakingDecelerationMps2 * deltaSeconds;
            if (commandedSpeed <= 0.0)
            {
                commandedSpeed = desiredSpeed;
            }
            else if (desiredSpeed > commandedSpeed)
            {
                commandedSpeed = Math.Min(desiredSpeed, commandedSpeed + maximumIncrease);
            }
            else
            {
                commandedSpeed = Math.Max(desiredSpeed, commandedSpeed - maximumDecrease);
            }

            commandedSpeed = Math.Max(0.2, commandedSpeed);
            ApplyCruiseSpeed((float)commandedSpeed);

            double steeringLookAhead = Math.Max(
                10.0,
                Math.Min(35.0, 10.0 + (commandedSpeed * 1.8)));
            if (technical)
            {
                double curvature = Math.Abs(
                    technicalSection.SignedCurvaturePerMeter);
                double technicalLookAhead = curvature > 1e-6
                    ? 0.25 / curvature
                    : 10.0;
                steeringLookAhead = Math.Min(
                    steeringLookAhead,
                    Math.Max(5.0, Math.Min(10.0, technicalLookAhead)));
            }
            double requestedTargetDistance = Math.Min(
                profile.TotalDistanceMeters,
                routeDistance + steeringLookAhead);
            bool taskActive = Function.Call<bool>(
                Hash.GET_IS_TASK_ACTIVE,
                ped.Handle,
                ControlVehicleTaskId);
            inactiveTaskSeconds = taskActive
                ? 0.0
                : inactiveTaskSeconds + deltaSeconds;
            bool targetConsumed =
                routeDistance >= taskTargetDistance -
                Math.Max(2.0, steeringLookAhead * 0.20);
            int now = Environment.TickCount;
            bool canRetask = unchecked(now - lastTaskIssueTime) >= 500;
            if (canRetask &&
                (inactiveTaskSeconds >= 0.75 ||
                 double.IsNegativeInfinity(taskTargetDistance) ||
                 targetConsumed))
            {
                RouteProfileSample target = SampleAtDistance(requestedTargetDistance);
                Vector3 steeringTarget = OffsetPoint(target, lateralOffset);
                ped.Task.DriveTo(
                    vehicle,
                    steeringTarget,
                    (float)commandedSpeed,
                    technical
                        ? DrivingFlags
                        : DrivingFlags | VehicleDrivingFlags.ForceStraightLine,
                    3.0f);
                taskTargetDistance = requestedTargetDistance;
                lastTaskIssueTime = now;
                inactiveTaskSeconds = 0.0;
            }
        }

        public void ReplacePlan(
            RouteProfile updatedProfile,
            PacerPlan updatedPlan,
            string updatedPaceLabel)
        {
            profile = updatedProfile ??
                throw new ArgumentNullException(nameof(updatedProfile));
            plan = updatedPlan ?? throw new ArgumentNullException(nameof(updatedPlan));
            paceLabel = NormalizeLabel(updatedPaceLabel);
            progressTracker = new RouteProgressTracker(profile);
            progressTracker.Reset(Math.Min(routeDistance, profile.TotalDistanceMeters));
            routeDistance = Math.Min(routeDistance, profile.TotalDistanceMeters);
            if (Exists(blip))
            {
                blip.Name = "Pace partner: " + DisplayLabel();
            }

            if (overheadLabel != null)
            {
                overheadLabel.Caption = DisplayLabel();
            }
        }

        public void ReplaceAppearance(
            string bicycleModel,
            string riderModel,
            int opacity,
            int index)
        {
            if (finished)
            {
                return;
            }

            DeleteSpawnedEntities();
            taskTargetDistance = double.NegativeInfinity;
            inactiveTaskSeconds = 0.0;
            offRouteSeconds = 0.0;
            loopReturnStallSeconds = 0.0;
            lastLoopReturnDistance = double.MaxValue;
            hasTrackedPosition = false;
            Spawn(
                bicycleModel ?? "TRIBIKE3",
                riderModel ?? "a_m_y_roadcyc_01",
                opacity,
                index,
                routeDistance);
        }

        public void Dispose()
        {
            DeleteSpawnedEntities();
            finished = true;
        }

        private void DeleteSpawnedEntities()
        {
            if (Exists(blip))
            {
                blip.Delete();
            }

            if (Exists(ped))
            {
                ped.Delete();
            }

            if (Exists(vehicle))
            {
                vehicle.Delete();
            }

            blip = null;
            overheadLabel = null;
            ped = null;
            vehicle = null;
        }

        private void Spawn(
            string bicycleModel,
            string riderModel,
            int opacity,
            int index,
            double spawnDistanceMeters = 0.0)
        {
            Model bike = new Model(bicycleModel);
            Model rider = new Model(riderModel);
            if (!bike.IsInCdImage ||
                !bike.IsValid ||
                !bike.IsBicycle ||
                !bike.Request(2000))
            {
                throw new InvalidOperationException(
                    "Could not load bicycle model " + bicycleModel + ".");
            }

            if (!rider.IsInCdImage || !rider.IsValid || !rider.Request(2000))
            {
                bike.MarkAsNoLongerNeeded();
                throw new InvalidOperationException(
                    "Could not load rider model " + riderModel + ".");
            }

            RouteProfileSample start = SampleAtDistance(
                Math.Max(
                    0.0,
                    Math.Min(spawnDistanceMeters, profile.TotalDistanceMeters)));
            Vector3 position = OffsetPoint(start, lateralOffset);
            float heading = HeadingAt(start);
            vehicle = World.CreateVehicle(bike, position, heading);
            ped = World.CreatePed(rider, position, heading);
            bike.MarkAsNoLongerNeeded();
            rider.MarkAsNoLongerNeeded();
            if (!Exists(vehicle) || !Exists(ped))
            {
                Dispose();
                throw new InvalidOperationException("GTA could not spawn a pace partner.");
            }

            opacity = Math.Max(0, Math.Min(255, opacity));
            vehicle.IsInvincible = true;
            vehicle.Opacity = opacity;
            vehicle.Mods.CustomPrimaryColor = ColourFor(index);
            vehicle.MaxSpeed = (float)profile.Options.MaximumRouteSpeedMps;
            ped.IsInvincible = true;
            ped.Opacity = opacity;
            ped.BlockPermanentEvents = true;
            ped.MaxDrivingSpeed = (float)profile.Options.MaximumRouteSpeedMps;
            ped.DrivingAggressiveness = 0.5f;
            ped.SetIntoVehicle(vehicle, VehicleSeat.Driver);
            Function.Call(Hash.SET_PED_KEEP_TASK, ped.Handle, true);
            float initialSpeed = (float)Math.Max(
                0.5,
                SpeedAtDistance(spawnDistanceMeters));
            vehicle.ForwardSpeed = initialSpeed;
            commandedSpeed = initialSpeed;
            lastTrackedPosition = vehicle.Position;
            hasTrackedPosition = true;
            blip = vehicle.AddBlip();
            if (Exists(blip))
            {
                blip.Sprite = BlipSprite.Bicycle;
                blip.Color = BlipColor.WhiteNotPure;
                blip.Name = "Pace partner: " + DisplayLabel();
            }

            overheadLabel = new TextElement(
                DisplayLabel(),
                new PointF(0.0f, 0.0f),
                0.35f,
                Color.WhiteSmoke,
                GTA.UI.Font.ChaletLondon,
                Alignment.Center,
                false,
                true);
        }

        private static string NormalizeLabel(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private void UpdateRecovery(RouteProgress progress, double deltaSeconds)
        {
            if (progress.LateralOffsetMeters > 30.0)
            {
                offRouteSeconds += deltaSeconds;
            }
            else
            {
                offRouteSeconds = 0.0;
            }

            if (offRouteSeconds < 3.0)
            {
                return;
            }

            RouteProfileSample recovery = SampleAtDistance(routeDistance + 2.0);
            vehicle.PositionNoOffset = OffsetPoint(recovery, lateralOffset);
            vehicle.Heading = HeadingAt(recovery);
            vehicle.ForwardSpeed = (float)Math.Max(1.0, commandedSpeed);
            ped.SetIntoVehicle(vehicle, VehicleSeat.Driver);
            ped.Task.ClearAll();
            taskTargetDistance = double.NegativeInfinity;
            offRouteSeconds = 0.0;
        }

        private void UpdateLoopReturn(double deltaSeconds)
        {
            RouteProfileSample start = profile.Samples[0];
            Vector3 target = OffsetPoint(start, lateralOffset);
            double remaining = vehicle.Position.DistanceTo2D(target);
            if (remaining <= 7.0)
            {
                CompleteLoopReturn();
                return;
            }

            if (remaining < lastLoopReturnDistance - 0.25)
            {
                loopReturnStallSeconds = 0.0;
            }
            else
            {
                loopReturnStallSeconds += deltaSeconds;
            }

            lastLoopReturnDistance = remaining;
            if (loopReturnStallSeconds >= 5.0)
            {
                vehicle.PositionNoOffset = target;
                vehicle.Heading = HeadingAt(start);
                vehicle.ForwardSpeed = (float)Math.Max(1.0, commandedSpeed);
                ped.SetIntoVehicle(vehicle, VehicleSeat.Driver);
                ped.Task.ClearAll();
                CompleteLoopReturn();
                return;
            }

            ApplyCruiseSpeed((float)Math.Max(2.0, Math.Min(6.0, commandedSpeed)));
            bool taskActive = Function.Call<bool>(
                Hash.GET_IS_TASK_ACTIVE,
                ped.Handle,
                ControlVehicleTaskId);
            int now = Environment.TickCount;
            if ((!taskActive || double.IsNegativeInfinity(taskTargetDistance)) &&
                unchecked(now - lastTaskIssueTime) >= 500)
            {
                ped.Task.DriveTo(
                    vehicle,
                    target,
                    (float)Math.Max(2.0, Math.Min(6.0, commandedSpeed)),
                    DrivingFlags,
                    3.0f);
                taskTargetDistance = profile.TotalDistanceMeters + 1.0;
                lastTaskIssueTime = now;
            }
        }

        private void CompleteLoopReturn()
        {
            progressTracker.Reset();
            routeDistance = 0.0;
            elapsedSeconds = 0.0;
            returningToStart = false;
            taskTargetDistance = double.NegativeInfinity;
            loopReturnStallSeconds = 0.0;
            lastLoopReturnDistance = double.MaxValue;
        }

        private void Finish()
        {
            finished = true;
            ApplyCruiseSpeed(0.0f);
            ped.Task.ClearAll();
            if (Exists(blip))
            {
                blip.Name = "Pace partner (finished): " + DisplayLabel();
                blip.Color = BlipColor.Red;
            }

            if (overheadLabel != null)
            {
                overheadLabel.Caption = DisplayLabel() + " (finished)";
            }
        }

        private void ApplyCruiseSpeed(float speed)
        {
            Function.Call(Hash.SET_DRIVE_TASK_MAX_CRUISE_SPEED, ped.Handle, speed);
            Function.Call(Hash.SET_DRIVE_TASK_CRUISE_SPEED, ped.Handle, speed);
            ped.DrivingSpeed = speed;
        }

        private double SpeedAtDistance(double distance)
        {
            int index = FindUpperState(distance);
            if (index <= 0)
            {
                return plan.Simulation.States[0].SpeedMps;
            }

            if (index >= plan.Simulation.States.Count)
            {
                return plan.Simulation.States[plan.Simulation.States.Count - 1].SpeedMps;
            }

            PacerState before = plan.Simulation.States[index - 1];
            PacerState after = plan.Simulation.States[index];
            double width = after.DistanceMeters - before.DistanceMeters;
            double fraction = width > 0.0
                ? (distance - before.DistanceMeters) / width
                : 0.0;
            return before.SpeedMps + ((after.SpeedMps - before.SpeedMps) * fraction);
        }

        private double ElapsedAtDistance(double distance)
        {
            int index = FindUpperState(distance);
            if (index <= 0)
            {
                return plan.Simulation.States[0].ElapsedSeconds;
            }

            if (index >= plan.Simulation.States.Count)
            {
                return plan.Simulation.States[
                    plan.Simulation.States.Count - 1].ElapsedSeconds;
            }

            PacerState before = plan.Simulation.States[index - 1];
            PacerState after = plan.Simulation.States[index];
            double width = after.DistanceMeters - before.DistanceMeters;
            double fraction = width > 0.0
                ? (distance - before.DistanceMeters) / width
                : 0.0;
            return before.ElapsedSeconds +
                   ((after.ElapsedSeconds - before.ElapsedSeconds) * fraction);
        }

        private RouteProfileSample MostTechnicalSample(
            double startDistance,
            double endDistance)
        {
            int start = FindUpperProfileSample(startDistance);
            int end = FindUpperProfileSample(endDistance);
            RouteProfileSample best = profile.Samples[start];
            double bestScore = TechnicalScore(best);
            for (int i = start + 1; i <= end; i++)
            {
                RouteProfileSample candidate = profile.Samples[i];
                double score = TechnicalScore(candidate);
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best;
        }

        private RouteProfileSample SampleAtDistance(double distance)
        {
            return profile.Samples[FindUpperProfileSample(distance)];
        }

        private int FindUpperProfileSample(double distance)
        {
            int low = 0;
            int high = profile.Samples.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                if (profile.Samples[middle].DistanceMeters < distance)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return Math.Max(0, Math.Min(profile.Samples.Count - 1, low));
        }

        private int FindUpperState(double distance)
        {
            int low = 0;
            int high = plan.Simulation.States.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                if (plan.Simulation.States[middle].DistanceMeters < distance)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return low;
        }

        private static Vector3 OffsetPoint(RouteProfileSample sample, float offset)
        {
            double normalX = -Math.Sin(sample.HeadingRadians);
            double normalY = Math.Cos(sample.HeadingRadians);
            return new Vector3(
                (float)(sample.X + (normalX * offset)),
                (float)(sample.Y + (normalY * offset)),
                (float)(sample.Z + 0.25));
        }

        private static float HeadingAt(RouteProfileSample sample)
        {
            Vector2 direction = new Vector2(
                (float)Math.Cos(sample.HeadingRadians),
                (float)Math.Sin(sample.HeadingRadians));
            return direction.ToHeading();
        }

        private static float LaneOffset(int index)
        {
            if (index == 0)
            {
                return -1.2f;
            }

            int row = (index + 1) / 2;
            return (index % 2 == 0 ? -1.0f : 1.0f) * (1.2f + (row * 0.6f));
        }

        private static double TechnicalScore(RouteProfileSample sample)
        {
            return (1.0 - sample.Straightness) +
                   Math.Min(0.25, Math.Abs(sample.SignedCurvaturePerMeter) * 6.0);
        }

        private static Color ColourFor(int index)
        {
            Color[] colours =
            {
                Color.FromArgb(52, 152, 219),
                Color.FromArgb(46, 204, 113),
                Color.FromArgb(241, 196, 15),
                Color.FromArgb(231, 76, 60),
                Color.FromArgb(155, 89, 182),
                Color.FromArgb(26, 188, 156)
            };
            return colours[Math.Abs(index) % colours.Length];
        }

        private string DisplayLabel()
        {
            string pace = plan.RequestedCompletionTimeSeconds.HasValue
                ? string.Format(
                    "{0} | {1:0.0} W/kg",
                    plan.Name,
                    plan.ResolvedWattsPerKilogram)
                : plan.Name;

            return string.Format(
                "{0} | {1}",
                pace,
                paceLabel ?? EffortCategory(plan.ResolvedWattsPerKilogram));
        }

        private static string EffortCategory(double wattsPerKilogram)
        {
            if (wattsPerKilogram <= 1.5)
            {
                return "Beginner";
            }

            if (wattsPerKilogram <= 2.0)
            {
                return "Social";
            }

            if (wattsPerKilogram <= 2.5)
            {
                return "Intermediate";
            }

            if (wattsPerKilogram <= 3.0)
            {
                return "Advanced";
            }

            return "Expert";
        }

        private void DrawOverheadLabel()
        {
            if (overheadLabel == null ||
                !ped.IsOnScreen ||
                Game.Player == null ||
                !Exists(Game.Player.Character))
            {
                return;
            }

            float distance = ped.Position.DistanceTo(Game.Player.Character.Position);
            if (distance > 60.0f)
            {
                return;
            }

            Vector3 origin =
                ped.Bones[Bone.IKHead].Position +
                new Vector3(0.0f, 0.0f, 0.55f) +
                (ped.Velocity / Math.Max(1.0f, Game.FPS));
            overheadLabel.Scale = Math.Max(
                0.20f,
                Math.Min(0.38f, 0.42f - (distance * 0.004f)));
            Function.Call(
                Hash.SET_DRAW_ORIGIN,
                origin.X,
                origin.Y,
                origin.Z,
                0);
            try
            {
                overheadLabel.Draw();
            }
            finally
            {
                Function.Call(Hash.CLEAR_DRAW_ORIGIN);
            }
        }

        private static bool Exists(Entity entity)
        {
            return entity != null && entity.Exists();
        }

        private static bool Exists(Blip value)
        {
            return value != null && value.Exists();
        }
    }
}
