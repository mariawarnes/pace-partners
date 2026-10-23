using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using GTA;
using PacePartners.Core;
using PacePartners.Mod.Diagnostics;
using PacePartners.Mod.Infrastructure;
using PacePartners.Mod.Persistence;
using PacePartners.Mod.Runtime;

namespace PacePartners.Mod
{
    public sealed class PacePartnersScript : Script
    {
        private readonly string scriptsDirectory;
        private readonly string modSettingsDirectory;
        private readonly string coursesJsonPath;
        private readonly SelectedCourseProbe courseProbe = new SelectedCourseProbe();
        private readonly CourseCatalogReader catalogReader = new CourseCatalogReader();
        private readonly FitCourseReader fitReader = new FitCourseReader();
        private readonly PacePartnersSettingsLoader settingsLoader =
            new PacePartnersSettingsLoader();
        private readonly PacerManager pacers = new PacerManager();

        private PacePartnersSettings settings;
        private RouteProfileStore profileStore;
        private LiveSurfaceSampler surfaceSampler;
        private SelectedCourseSelection currentSelection;
        private PreparedCourse activeCourse;
        private Task<PreparedCourse> courseLoadTask;
        private Task<ProfileRebuildResult> profileRebuildTask;
        private Task cacheSaveTask;
        private readonly Dictionary<string, RouteProfileCacheDocument>
            pendingCacheSaves =
                new Dictionary<string, RouteProfileCacheDocument>(
                    StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> pendingCacheSaveOrder =
            new Queue<string>();
        private int generation;
        private int courseLoadGeneration;
        private int profileRebuildGeneration;
        private int lastCourseProbeTime;
        private int lastProfileRebuildTime;
        private int lastCacheSaveTime;
        private int lastWeatherCheckTime;
        private int lastUserWeightCheckTime;
        private DateTime lastGtBikeConfigWriteUtc;
        private int surfaceLookAheadIndex;
        private bool spawnRequested;
        private bool profileDirty;
        private bool cacheDirty;
        private SelectedCourseProbeStatus? lastProbeStatus;

        public PacePartnersScript()
        {
            string gameDirectory = AppDomain.CurrentDomain.BaseDirectory;
            scriptsDirectory = Path.Combine(gameDirectory, "Scripts");
            string gtaDocumentsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Rockstar Games",
                "GTA V");
            modSettingsDirectory = Path.Combine(gtaDocumentsDirectory, "ModSettings");
            coursesJsonPath = Path.Combine(modSettingsDirectory, "courses.json");

            ModLog.Initialize(scriptsDirectory);
            try
            {
                ReloadSettings(false);
                profileStore = new RouteProfileStore(modSettingsDirectory);
                spawnRequested = settings.Enabled && settings.AutoStart;
                surfaceSampler = CreateSurfaceSampler();
                Tick += OnTick;
                KeyDown += OnKeyDown;
                Aborted += OnAborted;
                ModLog.Info("Pace Partners script initialized.");
            }
            catch (Exception exception)
            {
                ModLog.Error("Pace Partners could not initialize.", exception);
                ModLog.Notify("initialization failed; see PacePartners.log.");
            }
        }

        private void OnTick(object sender, EventArgs eventArgs)
        {
            try
            {
                CompleteCourseLoad();
                CompleteProfileRebuild();
                CompleteCacheSave();
                CheckUserWeight();

                if (pacers.IsActive)
                {
                    pacers.Update(Game.LastFrameTime);
                    SampleLiveSurface();
                    CheckWeather();
                    StartProfileRebuildIfNeeded();
                    SaveCacheIfNeeded();
                }

                int now = Environment.TickCount;
                if (unchecked(now - lastCourseProbeTime) >= 1000)
                {
                    lastCourseProbeTime = now;
                    PollSelectedCourse();
                }
            }
            catch (Exception exception)
            {
                ModLog.Error("Unhandled pace-partner tick error.", exception);
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs eventArgs)
        {
            try
            {
                if (eventArgs.KeyCode == settings.ToggleKey)
                {
                    TogglePacers();
                }
                else if (eventArgs.KeyCode == settings.ReloadKey)
                {
                    ReloadEverything();
                }
            }
            catch (Exception exception)
            {
                ModLog.Error("Pace-partner key action failed.", exception);
                ModLog.Notify("action failed; see PacePartners.log.");
            }
        }

        private void PollSelectedCourse()
        {
            SelectedCourseProbeResult result = courseProbe.Probe();
            if (!result.IsSelected)
            {
                if (lastProbeStatus != result.Status)
                {
                    ModLog.Info(result.Error);
                    lastProbeStatus = result.Status;
                }

                if (currentSelection != null)
                {
                    generation++;
                    SaveActiveCourseAsync();
                    pacers.Dispose();
                    activeCourse = null;
                    currentSelection = null;
                    profileDirty = false;
                    cacheDirty = false;
                    ModLog.Notify(
                        result.Status == SelectedCourseProbeStatus.NoCourseSelected
                            ? "no GTBikeV course is selected."
                            : "GTBikeV course state became unavailable; pacers removed.");
                }

                return;
            }

            lastProbeStatus = SelectedCourseProbeStatus.Selected;
            if (SameSelection(currentSelection, result.Selection))
            {
                return;
            }

            currentSelection = result.Selection;
            generation++;
            SaveActiveCourseAsync();
            pacers.Dispose();
            activeCourse = null;
            profileDirty = false;
            cacheDirty = false;
            surfaceSampler.Reset();
            ModLog.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Selected course {0} ({1}), reverse={2}, loop={3}.",
                currentSelection.BaseId,
                currentSelection.Name,
                currentSelection.Reverse,
                currentSelection.Loop));
            if (spawnRequested && settings.Enabled)
            {
                BeginCourseLoad(currentSelection);
            }
        }

        private void BeginCourseLoad(SelectedCourseSelection selection)
        {
            int requestedGeneration = generation;
            Weather capturedWeather = World.Weather;
            double weatherGrip = SurfaceMaterialCatalog.WeatherGripMultiplier(
                capturedWeather);
            PacePartnersSettings capturedSettings = settings;
            courseLoadGeneration = requestedGeneration;
            courseLoadTask = Task.Run(
                () => PrepareCourse(
                    selection,
                    capturedSettings,
                    weatherGrip,
                    capturedWeather));
            ModLog.Notify("loading the dense FIT route for " + selection.Name + "...");
        }

        private PreparedCourse PrepareCourse(
            SelectedCourseSelection selection,
            PacePartnersSettings capturedSettings,
            double weatherGrip,
            Weather weather)
        {
            CourseCatalog catalog = catalogReader.Read(coursesJsonPath);
            CourseCatalogEntry entry = catalog.Find(selection.BaseId);
            if (entry == null)
            {
                throw new KeyNotFoundException(
                    "Selected course id " + selection.BaseId +
                    " is not present in courses.json.");
            }

            string fitPath = catalog.ResolveFitPath(selection.BaseId);
            string fitFingerprint = RouteProfileStore.FingerprintFile(fitPath);
            string overrideFingerprint;
            ManualRouteOverrides manualOverrides = profileStore.LoadOverrides(
                selection.BaseId,
                entry.Version,
                selection.Reverse,
                out overrideFingerprint);
            RouteProfileOptions options = CreateProfileOptions(capturedSettings);
            List<SurfaceMaterialDefinition> materials =
                SurfaceMaterialCatalog.Create(weatherGrip);
            SurfaceMaterialDefinition defaultSurface =
                SurfaceMaterialCatalog.Default(weatherGrip);
            RouteProfileCacheDocument cached = profileStore.TryLoad(
                selection.BaseId,
                entry.Version,
                selection.Reverse);

            RouteProfileCacheDocument document;
            if (profileStore.IsReusable(
                    cached,
                    selection.BaseId,
                    entry.Version,
                    selection.Reverse,
                    fitFingerprint,
                    overrideFingerprint,
                    options,
                    materials,
                    defaultSurface))
            {
                document = cached;
                document.Input.MaterialDefinitions = materials;
                document.Input.DefaultSurface = defaultSurface;
                document.Input.Overrides = manualOverrides.Sections;
            }
            else
            {
                DecodedFitRoute decoded = fitReader.Read(
                    fitPath,
                    capturedSettings.InitialGpsOrigin,
                    capturedSettings.ElevationScale);
                List<RouteTracePoint> trace = FitTraceConverter.ToRouteTrace(
                    decoded,
                    selection.Reverse);
                List<RouteSurfaceObservation> observations =
                    CanReuseObservations(
                        cached,
                        selection,
                        entry.Version,
                        fitFingerprint)
                        ? new List<RouteSurfaceObservation>(
                            cached.Input.SurfaceObservations)
                        : new List<RouteSurfaceObservation>();
                RouteProfileInput input = new RouteProfileInput
                {
                    TracePoints = trace,
                    SurfaceObservations = observations,
                    MaterialDefinitions = materials,
                    Overrides = manualOverrides.Sections,
                    DefaultSurface = defaultSurface
                };
                document = new RouteProfileCacheDocument
                {
                    CourseId = selection.BaseId,
                    CourseVersion = entry.Version,
                    Reversed = selection.Reverse,
                    FitSha256 = fitFingerprint,
                    OverridesSha256 = overrideFingerprint,
                    Input = input,
                    Options = options,
                    Profile = RouteProfileBuilder.Build(input, options)
                };
            }

            List<PacerDefinition> definitions =
                CreatePacerDefinitions(capturedSettings);
            RiderPhysicsOptions physics =
                CreatePhysicsOptions(capturedSettings);
            return new PreparedCourse
            {
                Selection = selection,
                CatalogEntry = entry,
                FitPath = fitPath,
                Document = document,
                Definitions = definitions,
                Physics = physics,
                Plans = PacerManager.BuildPlans(
                    document.Profile,
                    definitions,
                    physics),
                Weather = weather
            };
        }

        private void CompleteCourseLoad()
        {
            Task<PreparedCourse> task = courseLoadTask;
            if (task == null || !task.IsCompleted)
            {
                return;
            }

            courseLoadTask = null;
            if (task.IsFaulted)
            {
                Exception error = task.Exception?.Flatten().InnerExceptions.FirstOrDefault() ??
                                  new InvalidOperationException("Unknown route loading failure.");
                ModLog.Error("Course profile loading failed.", error);
                ModLog.Notify("course load failed; see PacePartners.log.");
                return;
            }

            if (task.IsCanceled ||
                courseLoadGeneration != generation ||
                !spawnRequested)
            {
                return;
            }

            activeCourse = task.Result;
            cacheDirty = true;
            surfaceSampler = CreateSurfaceSampler();
            pacers.Start(
                activeCourse.Document.Profile,
                activeCourse.Plans,
                activeCourse.Selection.Loop,
                settings.PaceLabels,
                settings.PacingGender.ToString(),
                settings.Opacity);
            foreach (PacerAgent agent in pacers.Agents)
            {
                TargetTimeSolution solution = agent.TargetTimeSolution;
                if (solution != null && !solution.IsReachable)
                {
                    ModLog.Info(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} is outside the configured 0.5-8.0 W/kg solver range; " +
                        "using {1:0.00} W/kg for {2}.",
                        FormatDuration(TimeSpan.FromSeconds(
                            solution.RequestedCompletionTimeSeconds)),
                        solution.WattsPerKilogram,
                        solution.Reachability));
                }
            }
            lastProfileRebuildTime = Environment.TickCount;
            lastCacheSaveTime = unchecked(Environment.TickCount - 10000);
            SaveCacheIfNeeded();
            ModLog.Notify(string.Format(
                CultureInfo.InvariantCulture,
                "{0} pace partners loaded on {1} ({2:0.0} km).",
                activeCourse.Definitions.Count,
                activeCourse.Selection.Name,
                activeCourse.Document.Profile.TotalDistanceMeters / 1000.0));
        }

        private void SampleLiveSurface()
        {
            if (activeCourse == null || pacers.Agents.Count == 0)
            {
                return;
            }

            PacerAgent agent = pacers.Agents.FirstOrDefault(item => !item.IsFinished);
            if (agent == null)
            {
                return;
            }

            double lookAhead = (surfaceLookAheadIndex++ % 5) * 10.0;
            double sampleDistance = Math.Min(
                activeCourse.Document.Profile.TotalDistanceMeters,
                agent.RouteDistanceMeters + lookAhead);
            bool ignoreRaycast = IsRaycastIgnored(
                activeCourse.Document.Input.Overrides,
                sampleDistance);
            LiveSurfaceSample sample;
            if (!surfaceSampler.TrySample(
                    activeCourse.Document.Profile,
                    sampleDistance,
                    agent.Vehicle,
                    ignoreRaycast,
                    out sample))
            {
                return;
            }

            if (RouteProfileStore.UpsertSurfaceObservation(
                    activeCourse.Document.Input.SurfaceObservations,
                    sample.Observation,
                    settings.SurfaceSampleSpacingMeters * 0.55))
            {
                profileDirty = true;
                cacheDirty = true;
            }
        }

        private void StartProfileRebuildIfNeeded()
        {
            int now = Environment.TickCount;
            if (!profileDirty ||
                profileRebuildTask != null ||
                unchecked(now - lastProfileRebuildTime) < 2000 ||
                activeCourse == null)
            {
                return;
            }

            RouteProfileInput snapshot = CopyInput(activeCourse.Document.Input);
            RouteProfileOptions options = activeCourse.Document.Options;
            List<PacerDefinition> definitions =
                new List<PacerDefinition>(activeCourse.Definitions);
            RiderPhysicsOptions physics = activeCourse.Physics;
            profileRebuildGeneration = generation;
            profileDirty = false;
            lastProfileRebuildTime = now;
            profileRebuildTask = Task.Run(() =>
            {
                RouteProfile profile = RouteProfileBuilder.Build(snapshot, options);
                return new ProfileRebuildResult
                {
                    Profile = profile,
                    Plans = PacerManager.BuildPlans(
                        profile,
                        definitions,
                        physics)
                };
            });
        }

        private void CompleteProfileRebuild()
        {
            Task<ProfileRebuildResult> task = profileRebuildTask;
            if (task == null || !task.IsCompleted)
            {
                return;
            }

            profileRebuildTask = null;
            if (task.IsFaulted)
            {
                ModLog.Error(
                    "Live surface profile recalculation failed.",
                    task.Exception?.Flatten());
                return;
            }

            if (task.IsCanceled ||
                profileRebuildGeneration != generation ||
                activeCourse == null)
            {
                return;
            }

            activeCourse.Document.Profile = task.Result.Profile;
            activeCourse.Plans = task.Result.Plans;
            pacers.Replan(
                activeCourse.Document.Profile,
                activeCourse.Plans);
            cacheDirty = true;
        }

        private void CheckWeather()
        {
            int now = Environment.TickCount;
            if (activeCourse == null ||
                unchecked(now - lastWeatherCheckTime) < 5000)
            {
                return;
            }

            lastWeatherCheckTime = now;
            Weather current = World.Weather;
            if (current == activeCourse.Weather)
            {
                return;
            }

            activeCourse.Weather = current;
            double multiplier = SurfaceMaterialCatalog.WeatherGripMultiplier(current);
            activeCourse.Document.Input.MaterialDefinitions =
                SurfaceMaterialCatalog.Create(multiplier);
            activeCourse.Document.Input.DefaultSurface =
                SurfaceMaterialCatalog.Default(multiplier);
            profileDirty = true;
            cacheDirty = true;
        }

        private void SaveCacheIfNeeded()
        {
            int now = Environment.TickCount;
            if (!cacheDirty ||
                cacheSaveTask != null ||
                unchecked(now - lastCacheSaveTime) < 10000)
            {
                return;
            }

            lastCacheSaveTime = now;
            SaveActiveCourseAsync();
        }

        private void SaveActiveCourseAsync()
        {
            if (activeCourse == null)
            {
                return;
            }

            RouteProfileCacheDocument snapshot =
                CopyDocument(activeCourse.Document);
            cacheDirty = false;
            if (cacheSaveTask != null)
            {
                QueueCacheSave(snapshot);
                return;
            }

            StartCacheSave(snapshot);
        }

        private void CompleteCacheSave()
        {
            Task task = cacheSaveTask;
            if (task == null || !task.IsCompleted)
            {
                return;
            }

            cacheSaveTask = null;
            if (task.IsFaulted)
            {
                cacheDirty = true;
                ModLog.Error(
                    "Route profile cache save failed.",
                    task.Exception?.Flatten());
            }

            StartNextPendingCacheSave();
        }

        private void StartCacheSave(RouteProfileCacheDocument snapshot)
        {
            cacheSaveTask = Task.Run(() => profileStore.Save(snapshot));
        }

        private void QueueCacheSave(RouteProfileCacheDocument snapshot)
        {
            string path = profileStore.GetProfilePath(
                snapshot.CourseId,
                snapshot.CourseVersion,
                snapshot.Reversed);
            if (!pendingCacheSaves.ContainsKey(path))
            {
                pendingCacheSaveOrder.Enqueue(path);
            }

            pendingCacheSaves[path] = snapshot;
        }

        private void StartNextPendingCacheSave()
        {
            while (pendingCacheSaveOrder.Count > 0)
            {
                string path = pendingCacheSaveOrder.Dequeue();
                RouteProfileCacheDocument snapshot;
                if (!pendingCacheSaves.TryGetValue(path, out snapshot))
                {
                    continue;
                }

                pendingCacheSaves.Remove(path);
                StartCacheSave(snapshot);
                return;
            }
        }

        private void TogglePacers()
        {
            if (!settings.Enabled)
            {
                ModLog.Notify("disabled in PacePartners.ini.");
                return;
            }

            spawnRequested = !spawnRequested;
            generation++;
            if (!spawnRequested)
            {
                SaveActiveCourseAsync();
                pacers.Dispose();
                ModLog.Notify("pace partners removed.");
                return;
            }

            if (currentSelection == null)
            {
                ModLog.Notify("select a GTBikeV course first.");
                return;
            }

            if (activeCourse != null &&
                SameSelection(activeCourse.Selection, currentSelection))
            {
                pacers.Start(
                    activeCourse.Document.Profile,
                    activeCourse.Plans,
                    activeCourse.Selection.Loop,
                    settings.PaceLabels,
                    settings.PacingGender.ToString(),
                    settings.Opacity);
            }
            else
            {
                BeginCourseLoad(currentSelection);
            }
        }

        private void ReloadEverything()
        {
            SaveActiveCourseSynchronously();
            generation++;
            pacers.Dispose();
            activeCourse = null;
            courseLoadTask = null;
            profileRebuildTask = null;
            ReloadSettings(true);
            surfaceSampler = CreateSurfaceSampler();
            spawnRequested = settings.Enabled && settings.AutoStart;
            if (spawnRequested && currentSelection != null)
            {
                BeginCourseLoad(currentSelection);
            }
        }

        private void ReloadSettings(bool notify)
        {
            settings = settingsLoader.Load(scriptsDirectory, modSettingsDirectory);
            lastGtBikeConfigWriteUtc = File.GetLastWriteTimeUtc(
                settings.GtBikeVConfigPath);
            if (notify)
            {
                ModLog.Notify("settings and route overrides reloaded.");
            }
        }

        private void CheckUserWeight()
        {
            int now = Environment.TickCount;
            if (unchecked(now - lastUserWeightCheckTime) < 1000)
            {
                return;
            }

            lastUserWeightCheckTime = now;
            DateTime writeTime = File.GetLastWriteTimeUtc(
                settings.GtBikeVConfigPath);
            if (writeTime == lastGtBikeConfigWriteUtc)
            {
                return;
            }

            PacePartnersSettings refreshed;
            try
            {
                refreshed = settingsLoader.Load(
                    scriptsDirectory,
                    modSettingsDirectory);
            }
            catch (Exception exception)
            {
                ModLog.Error(
                    "Could not refresh GTBikeV user weight; keeping the current pacing plans.",
                    exception);
                return;
            }

            lastGtBikeConfigWriteUtc = writeTime;
            if (Math.Abs(
                    refreshed.RiderMassKilograms -
                    settings.RiderMassKilograms) < 0.001)
            {
                return;
            }

            ReloadForUserWeight(refreshed);
        }

        private void ReloadForUserWeight(PacePartnersSettings refreshed)
        {
            double previousWeight = settings.RiderMassKilograms;
            bool shouldRemainEnabled = spawnRequested;
            SaveActiveCourseSynchronously();
            generation++;
            pacers.Dispose();
            activeCourse = null;
            courseLoadTask = null;
            profileRebuildTask = null;
            profileDirty = false;
            cacheDirty = false;
            settings = refreshed;
            surfaceSampler = CreateSurfaceSampler();
            spawnRequested = shouldRemainEnabled && settings.Enabled;
            if (spawnRequested && currentSelection != null)
            {
                BeginCourseLoad(currentSelection);
            }

            ModLog.Notify(string.Format(
                CultureInfo.InvariantCulture,
                "GTBikeV user weight changed from {0:0.0} to {1:0.0} kg; pacing plans recalculated.",
                previousWeight,
                settings.RiderMassKilograms));
        }

        private LiveSurfaceSampler CreateSurfaceSampler()
        {
            return new LiveSurfaceSampler(
                settings.SurfaceSampleSpacingMeters,
                settings.SurfaceSampleIntervalMilliseconds);
        }

        private void SaveActiveCourseSynchronously()
        {
            if (activeCourse == null)
            {
                return;
            }

            try
            {
                if (cacheSaveTask != null)
                {
                    try
                    {
                        if (!cacheSaveTask.Wait(5000))
                        {
                            QueueCacheSave(CopyDocument(activeCourse.Document));
                            return;
                        }
                    }
                    catch (AggregateException exception)
                    {
                        ModLog.Error(
                            "Previous asynchronous cache save failed.",
                            exception.Flatten());
                    }

                    cacheSaveTask = null;
                }

                FlushPendingCacheSavesSynchronously();
                profileStore.Save(CopyDocument(activeCourse.Document));
                cacheDirty = false;
            }
            catch (Exception exception)
            {
                ModLog.Error("Final route profile cache save failed.", exception);
            }
        }

        private void FlushPendingCacheSavesSynchronously()
        {
            while (pendingCacheSaveOrder.Count > 0)
            {
                string path = pendingCacheSaveOrder.Dequeue();
                RouteProfileCacheDocument snapshot;
                if (!pendingCacheSaves.TryGetValue(path, out snapshot))
                {
                    continue;
                }

                pendingCacheSaves.Remove(path);
                profileStore.Save(snapshot);
            }
        }

        private void OnAborted(object sender, EventArgs eventArgs)
        {
            Tick -= OnTick;
            KeyDown -= OnKeyDown;
            Aborted -= OnAborted;
            SaveActiveCourseSynchronously();
            pacers.Dispose();
        }

        private static RouteProfileOptions CreateProfileOptions(
            PacePartnersSettings source)
        {
            return new RouteProfileOptions
            {
                SampleSpacingMeters = source.SampleSpacingMeters,
                GradientWindowMeters = source.GradientWindowMeters,
                CurvatureWindowMeters = source.CurvatureWindowMeters,
                StraightnessWindowMeters = source.StraightnessWindowMeters,
                CornerLookAheadSeconds = source.CornerLookAheadSeconds,
                MaximumBrakingDecelerationMps2 =
                    source.MaximumBrakingMetersPerSecondSquared,
                MaximumAccelerationMps2 =
                    source.MaximumAccelerationMetersPerSecondSquared,
                SurfaceObservationInfluenceRadiusMeters = Math.Max(
                    10.0,
                    source.SurfaceSampleSpacingMeters * 1.5)
            };
        }

        private static RiderPhysicsOptions CreatePhysicsOptions(
            PacePartnersSettings source)
        {
            return new RiderPhysicsOptions
            {
                RiderMassKg = source.RiderMassKilograms,
                BikeMassKg = source.BikeMassKilograms,
                DragAreaSquareMeters = source.DragAreaSquareMeters,
                PowerVariationFraction = source.PowerVariationFraction,
                PrimaryVariationPeriodSeconds =
                    source.PrimaryVariationPeriodSeconds,
                SecondaryVariationPeriodSeconds =
                    source.SecondaryVariationPeriodSeconds
            };
        }

        private static List<PacerDefinition> CreatePacerDefinitions(
            PacePartnersSettings source)
        {
            var result = new List<PacerDefinition>();
            foreach (double effort in source.EffortsWattsPerKilogram)
            {
                result.Add(PacerDefinition.ForEffort(
                    effort.ToString("0.0", CultureInfo.InvariantCulture) + " W/kg",
                    effort));
            }

            foreach (TimeSpan time in source.TargetCompletionTimes)
            {
                result.Add(PacerDefinition.ForCompletionTime(
                    "Target " + FormatDuration(time),
                    time.TotalSeconds));
            }

            return result;
        }

        private static string FormatDuration(TimeSpan time)
        {
            return time.TotalHours >= 1.0
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1:00}:{2:00}",
                    (int)time.TotalHours,
                    time.Minutes,
                    time.Seconds)
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1:00}",
                    (int)time.TotalMinutes,
                    time.Seconds);
        }

        private static bool CanReuseObservations(
            RouteProfileCacheDocument cached,
            SelectedCourseSelection selection,
            string version,
            string fitFingerprint)
        {
            return cached != null &&
                   cached.CourseId == selection.BaseId &&
                   cached.Reversed == selection.Reverse &&
                   string.Equals(cached.CourseVersion, version, StringComparison.Ordinal) &&
                   string.Equals(
                       cached.FitSha256,
                       fitFingerprint,
                       StringComparison.OrdinalIgnoreCase) &&
                   cached.Input != null &&
                   cached.Input.SurfaceObservations != null;
        }

        private static RouteProfileInput CopyInput(RouteProfileInput source)
        {
            return new RouteProfileInput
            {
                TracePoints = new List<RouteTracePoint>(source.TracePoints),
                SurfaceObservations =
                    new List<RouteSurfaceObservation>(source.SurfaceObservations),
                MaterialDefinitions =
                    new List<SurfaceMaterialDefinition>(source.MaterialDefinitions),
                Overrides = new List<RouteProfileOverride>(source.Overrides),
                DefaultSurface = source.DefaultSurface
            };
        }

        private static RouteProfileCacheDocument CopyDocument(
            RouteProfileCacheDocument source)
        {
            return new RouteProfileCacheDocument
            {
                SchemaVersion = source.SchemaVersion,
                CourseId = source.CourseId,
                CourseVersion = source.CourseVersion,
                Reversed = source.Reversed,
                FitSha256 = source.FitSha256,
                OverridesSha256 = source.OverridesSha256,
                OptionsSha256 = source.OptionsSha256,
                MaterialsSha256 = source.MaterialsSha256,
                CalculatedUtc = source.CalculatedUtc,
                Input = CopyInput(source.Input),
                Options = source.Options,
                Profile = source.Profile
            };
        }

        private static bool IsRaycastIgnored(
            IEnumerable<RouteProfileOverride> overrides,
            double distance)
        {
            PropertyInfo property = typeof(RouteProfileOverride).GetProperty(
                "IgnoreSurfaceRaycast",
                BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
            {
                return false;
            }

            foreach (RouteProfileOverride section in overrides)
            {
                if (section == null ||
                    distance < section.StartDistanceMeters ||
                    distance > section.EndDistanceMeters)
                {
                    continue;
                }

                object value = property.GetValue(section, null);
                if (value is bool boolean && boolean)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameSelection(
            SelectedCourseSelection left,
            SelectedCourseSelection right)
        {
            return left != null &&
                   right != null &&
                   left.EffectiveId == right.EffectiveId &&
                   left.Reverse == right.Reverse &&
                   left.Loop == right.Loop;
        }

        private sealed class PreparedCourse
        {
            public SelectedCourseSelection Selection { get; set; }

            public CourseCatalogEntry CatalogEntry { get; set; }

            public string FitPath { get; set; }

            public RouteProfileCacheDocument Document { get; set; }

            public List<PacerDefinition> Definitions { get; set; }

            public RiderPhysicsOptions Physics { get; set; }

            public List<PacerPlan> Plans { get; set; }

            public Weather Weather { get; set; }
        }

        private sealed class ProfileRebuildResult
        {
            public RouteProfile Profile { get; set; }

            public List<PacerPlan> Plans { get; set; }
        }
    }
}
