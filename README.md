# Pace Partners for GTBike V

Pace Partners adds effort-based virtual riders to the course currently selected
in GTBike V. It builds its own speed plan from the cached dense FIT trace:
gradient, bends, surface grip, rolling resistance, acceleration and braking all
affect the rider. Recorded FIT speed is deliberately ignored.

`PacingGender` selects a real-world FTP-style group containing Beginner,
Intermediate and Expert riders. Female uses 1.5, 2.6 and 3.7 W/kg; male uses
1.9, 3.0 and 4.1 W/kg. You can also request riders by target
course-completion time.

## What it does

- Reads GTBike V's live `CurrentCourse` selection and resolves its exact
  version in `courses.json`.
- Loads
  `Documents\Rockstar Games\GTA V\ModSettings\{course-id}-{version}.fit`.
- Keeps every valid FIT position record, including zero-speed records and
  routes with no speed field.
- Uses FIT altitude for physical gradient and FIT-derived GTA Z for
  bridges, tunnels and elevated roads.
- Resamples the dense geometry at 2 m by default, calculates a 25 m smoothed
  gradient, bend curvature and straightness, then applies forward acceleration
  and backward braking envelopes.
- Samples GTA material hashes with downward raycasts while the route is active.
  Raw material hashes stay recomputable; a derived profile is invalidated when
  weather-dependent grip changes.
- Simulates independent rider power. A constant-power rider therefore slows on
  climbs and rough ground, accelerates on descents and straights, and brakes
  before technical corners.
- Applies a small deterministic power drift over long periods instead of
  changing speed randomly each second.
- Shows an overhead pace label on nearby riders and names their map blips.
  Labels include W/kg and the configured pacing classification; for example,
  `1.5 W/kg | Beginner Female`. The selected pacing gender also controls the
  spawned ped's gender. Completion-time pacers also include their target time.
- Drives each ped continuously along route distance. It does not advance one
  FIT record per second, drop stopped records, discard FIT elevation, or reset
  the driving task on every update.

GTBike V's course JSON is used only to resolve course identity and version. Its
sparse navigation anchors are never used as bend geometry.

## Requirements

- Grand Theft Auto V with a compatible Script Hook V setup
- ScriptHookVDotNet 3
- GTBike V, including its `Scripts\Fit.dll` and `Scripts\Newtonsoft.Json.dll`
- .NET Framework 4.8 for building

This is a single-player mod. Follow Rockstar's and each dependency's terms, and
do not use game modifications in modes where they are prohibited.

## Install

1. Install and run GTBike V once so
   `Documents\Rockstar Games\GTA V\ModSettings\courses.json` and the cached FIT
   routes exist.
2. Copy `PacePartners.dll` and `PacePartners.Core.dll` into the GTA V
   `Scripts` directory.
3. Copy [examples/PacePartners.ini](examples/PacePartners.ini) to
   `Scripts\PacePartners.ini` and edit it if desired.
4. Start GTA V, select a course in GTBike V, and start the course. With the
   default `AutoStart=true`, the pace partners load automatically.

Controls:

- `F10` -- spawn or remove pace partners for the selected course.
- `F11` -- reload `PacePartners.ini` and the selected route's overrides.

The script deliberately fails closed if GTBike V is not loaded or no current
course can be identified. Messages appear in-game; details are written to
`Scripts\PacePartners.log`.

## Configuration

The full default configuration is in
[examples/PacePartners.ini](examples/PacePartners.ini).

Set `PacingGender` to `Female` or `Male`. This automatically creates the three
matching riders and labels:

```ini
[PACING]
PacingGender=Female
TargetCompletionTimes=00:45:00,60
```

| Selected group | Beginner | Intermediate | Expert |
| --- | ---: | ---: | ---: |
| Female | 1.5 W/kg | 2.6 W/kg | 3.7 W/kg |
| Male | 1.9 W/kg | 3.0 W/kg | 4.1 W/kg |

These are representative FTP-style anchors derived from TrainerRoad's athlete
benchmarks: female untrained below 1.6, trained around 2.6 and elite above 3.6
W/kg; male untrained below 2.0, trained around 3.0 and elite above 4.0 W/kg.
British Cycling describes FTP as sustainable power for roughly 30–60 minutes
and threshold W/kg as a strong cycling-performance indicator. See
[TrainerRoad's FTP comparison](https://www.trainerroad.com/blog/is-my-ftp-too-low/)
and [British Cycling's power guide](https://www.britishcycling.org.uk/knowledge/training/get-started/article/izn20140820-Training-Understanding-Intensity-3--Power-0).

`TargetCompletionTimes` creates optional additional riders by solving for the
effort needed to finish near each time. Times may be comma-separated
`hh:mm:ss` values or numeric minutes. Target-time riders receive the nearest
Beginner, Intermediate or Expert label from the selected group.

W/kg uses GTBike V's `[user] UserWeightKg` to calculate rider power, so every
pacer represents the selected effort at the user's configured weight. Total
moving mass also includes `BikeMassKg`. The script monitors `GTBikeVConfig.ini`;
when GTBike V saves a different user weight, active pacing plans are
automatically recalculated. `PowerVariationFraction=0.018` is a maximum 1.8%
slow power variation, combined from the two configured periods.

`PacingGender` also selects matching female or male avatars. Clothing and
bicycle become progressively more performance-oriented:

| Pace level | Rider style | Bicycle |
| --- | --- | --- |
| Beginner | Casual street clothing | Cruiser |
| Intermediate | Fitness/cycling clothing | Scorcher |
| Expert | Running/road-cycling race kit | TriBike3 |

Female presets use GTA's hipster, fitness and runner peds; male presets use
hipster, cyclist and road-cyclist peds. Target-time riders use the appearance
for their calculated Beginner, Intermediate or Expert tier.

GTBike V stores course altitude using its elevation scale. The default
`ElevationScale=0.5` converts FIT altitude back to GTA world Z while retaining
the unscaled FIT altitude for gradient. Only change this if the matching GTBike
V course cache was produced with another scale.

## Profiles, live surfaces and overrides

Calculated profiles are direction-specific and live under:

```text
Documents\Rockstar Games\GTA V\ModSettings\PacePartners\
|-- Profiles\{id}-{version}-{forward|reverse}.profile.json
`-- Overrides\{id}-{version}-{forward|reverse}.json
```

Profiles are generated data. They are reused only when the schema, algorithm,
FIT SHA-256, profile options, material/weather mapping and override SHA-256
still match. It is safe to delete a profile; it will be rebuilt.

An empty override file is created when a course direction is first loaded.
Copy fields from
[examples/40-26.0-forward.json](examples/40-26.0-forward.json) as needed, keep
distances in metres from the route start, then press `F11`.

Override fields are optional:

| Field | Meaning |
| --- | --- |
| `StartDistanceMeters`, `EndDistanceMeters` | Inclusive route-distance range |
| `IgnoreSurfaceRaycast` | Ignore cached/live material data in the range |
| `MaterialHash` | Force a GTA material hash |
| `RollingResistanceCoefficient` | Force rolling resistance |
| `GripCoefficient` | Force dry grip |
| `RoughnessFactor` | Force the surface roughness multiplier |
| `SurfaceSpeedLimitMps` | Add a surface speed cap in m/s |
| `MaximumSafeSpeedMps` | Hard final corner/section cap in m/s |
| `Gradient` | Force grade as a fraction (`0.05` is 5%) |
| `CurvatureMultiplier` | Scale calculated bend curvature |
| `ZOffsetMeters` | Shift GTA target Z without altering physical gradient |

Typical uses are excluding a tunnel ceiling from raycasts, selecting a bridge
deck material, nudging a vertically stacked route, or lowering the speed of a
known problematic corner. Specific physical fields in an override take
precedence over the material mapping.

## Build and test

Point the build at a GTA V installation containing the required DLLs. Either
copy `Directory.Build.props.example` to `Directory.Build.props`, set `GTAV_DIR`,
or pass the property directly:

```powershell
dotnet build .\PacePartners.sln -c Release `
  -p:GtaVDirectory="D:\SteamLibrary\steamapps\common\Grand Theft Auto V"
```

Build output is written to `artifacts\Release`. Run the dependency-free core
test executable with:

```powershell
dotnet run --project .\tests\PacePartners.Core.Tests\PacePartners.Core.Tests.csproj `
  -c Release
```

The game integration must still be smoke-tested in the same GTA V, GTBike V and
ScriptHookVDotNet versions that will be used in play.

To profile every locally cached FIT route without launching GTA:

```powershell
dotnet run --project .\tests\PacePartners.FitSmoke\PacePartners.FitSmoke.csproj `
  -p:GtaVDirectory="D:\SteamLibrary\steamapps\common\Grand Theft Auto V" -- `
  "$HOME\Documents\Rockstar Games\GTA V\ModSettings"
```

## Architecture

`PacePartners.Core` is the deterministic route-profiler, material model,
constant-power simulator and target-time solver. `PacePartners.Mod` owns the
GTBike V reflection boundary, FIT/catalog readers, profile persistence, GTA
raycasts and spawned rider control. Expensive FIT/profile work runs off the game
thread; GTA entities and raycasts stay on the script tick.

ActivityGhosts was used as the design scaffold for FIT-to-GTA coordinate
conversion and spawned-rider control. This implementation replaces its
time/index replay model with a distance-domain physical plan and persistent
vehicle task. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## License

Copyright (C) 2026 Pace Partners contributors.

Licensed under the GNU General Public License, version 3 only
(`GPL-3.0-only`). See [LICENSE](LICENSE). This project is not affiliated with or
endorsed by Rockstar Games or GTBike V.
