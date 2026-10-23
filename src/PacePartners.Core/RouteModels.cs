using System;
using System.Collections.Generic;

namespace PacePartners.Core
{
    /// <summary>
    /// A dense point read from a FIT route. X/Y/Z are GTA world coordinates.
    /// AltitudeMeters is the physical FIT altitude and is deliberately separate
    /// from GTA Z so grade calculation never depends on world placement.
    /// </summary>
    public sealed class RouteTracePoint
    {
        public double X { get; set; }

        public double Y { get; set; }

        public double Z { get; set; }

        public double AltitudeMeters { get; set; }

        public RouteTracePoint()
        {
        }

        public RouteTracePoint(double x, double y, double z, double altitudeMeters)
        {
            X = x;
            Y = y;
            Z = z;
            AltitudeMeters = altitudeMeters;
        }
    }

    /// <summary>
    /// An unprocessed result from a downward GTA material raycast. Optional
    /// physical values let an integration persist information that was known at
    /// sampling time while retaining the material hash as the primary source.
    /// </summary>
    public sealed class RouteSurfaceObservation
    {
        public double DistanceMeters { get; set; }

        public int MaterialHash { get; set; }

        public double? RollingResistanceCoefficient { get; set; }

        public double? GripCoefficient { get; set; }

        public double? RoughnessFactor { get; set; }

        public double? SurfaceSpeedLimitMps { get; set; }

        public RouteSurfaceObservation()
        {
        }

        public RouteSurfaceObservation(double distanceMeters, int materialHash)
        {
            DistanceMeters = distanceMeters;
            MaterialHash = materialHash;
        }
    }

    /// <summary>
    /// Recomputable physical mapping for a GTA material hash.
    /// SurfaceSpeedLimitMps <= 0 means that the material has no explicit cap.
    /// </summary>
    public sealed class SurfaceMaterialDefinition
    {
        public int MaterialHash { get; set; }

        public string Name { get; set; }

        public double RollingResistanceCoefficient { get; set; }

        public double GripCoefficient { get; set; }

        public double RoughnessFactor { get; set; }

        public double SurfaceSpeedLimitMps { get; set; }

        public SurfaceMaterialDefinition()
        {
            Name = string.Empty;
            RollingResistanceCoefficient = 0.0045;
            GripCoefficient = 0.85;
            RoughnessFactor = 1.0;
        }

        public static SurfaceMaterialDefinition Asphalt(int materialHash)
        {
            return new SurfaceMaterialDefinition
            {
                MaterialHash = materialHash,
                Name = "Asphalt",
                RollingResistanceCoefficient = 0.0045,
                GripCoefficient = 0.85,
                RoughnessFactor = 1.0,
                SurfaceSpeedLimitMps = 0.0
            };
        }
    }

    /// <summary>
    /// Distance-range correction for tunnels, bridges, raycast misses, or a
    /// known problematic corner. Null values leave the calculated value intact.
    /// </summary>
    public sealed class RouteProfileOverride
    {
        public string Id { get; set; }

        public string Reason { get; set; }

        public double StartDistanceMeters { get; set; }

        public double EndDistanceMeters { get; set; }

        public int? MaterialHash { get; set; }

        public double? RollingResistanceCoefficient { get; set; }

        public double? GripCoefficient { get; set; }

        public double? RoughnessFactor { get; set; }

        public double? SurfaceSpeedLimitMps { get; set; }

        public double? MaximumSafeSpeedMps { get; set; }

        public double? Gradient { get; set; }

        public double? CurvatureMultiplier { get; set; }

        /// <summary>Adds to GTA world Z without changing physical FIT altitude.</summary>
        public double? ZOffsetMeters { get; set; }

        /// <summary>
        /// When true, discard raycast data in this range and use the configured
        /// default surface before applying any explicit properties below.
        /// </summary>
        public bool? IgnoreSurfaceRaycast { get; set; }

        public RouteProfileOverride()
        {
            Id = string.Empty;
            Reason = string.Empty;
        }
    }

    /// <summary>
    /// Inputs retained in a cache. Surface observations remain raw; changing a
    /// material mapping or override and rebuilding produces fresh derived limits.
    /// </summary>
    public sealed class RouteProfileInput
    {
        public List<RouteTracePoint> TracePoints { get; set; }

        public List<RouteSurfaceObservation> SurfaceObservations { get; set; }

        public List<SurfaceMaterialDefinition> MaterialDefinitions { get; set; }

        public List<RouteProfileOverride> Overrides { get; set; }

        public SurfaceMaterialDefinition DefaultSurface { get; set; }

        public RouteProfileInput()
        {
            TracePoints = new List<RouteTracePoint>();
            SurfaceObservations = new List<RouteSurfaceObservation>();
            MaterialDefinitions = new List<SurfaceMaterialDefinition>();
            Overrides = new List<RouteProfileOverride>();
            DefaultSurface = SurfaceMaterialDefinition.Asphalt(0);
        }
    }

    public sealed class RouteProfileOptions
    {
        public double SampleSpacingMeters { get; set; }

        public double GradientWindowMeters { get; set; }

        /// <summary>
        /// Clamp applied to calculated FIT gradient after smoothing. Manual
        /// Gradient overrides remain authoritative and are applied afterwards.
        /// </summary>
        public double MaximumAbsoluteGradient { get; set; }

        public double CurvatureWindowMeters { get; set; }

        public double StraightnessWindowMeters { get; set; }

        public double MaximumRouteSpeedMps { get; set; }

        public double CornerGripUtilization { get; set; }

        public double MinimumCurvatureForLimit { get; set; }

        public double CornerLookAheadSeconds { get; set; }

        public double MaximumBrakingDecelerationMps2 { get; set; }

        public double MaximumAccelerationMps2 { get; set; }

        public double StartSpeedMps { get; set; }

        public double GravityMps2 { get; set; }

        /// <summary>
        /// Maximum distance at which a raw raycast observation may affect a
        /// sample. Unsampled gaps fall back to DefaultSurface.
        /// </summary>
        public double SurfaceObservationInfluenceRadiusMeters { get; set; }

        public RouteProfileOptions()
        {
            SampleSpacingMeters = 2.0;
            GradientWindowMeters = 25.0;
            MaximumAbsoluteGradient = 0.45;
            CurvatureWindowMeters = 12.0;
            StraightnessWindowMeters = 25.0;
            MaximumRouteSpeedMps = 25.0;
            CornerGripUtilization = 0.60;
            MinimumCurvatureForLimit = 0.0005;
            CornerLookAheadSeconds = 6.0;
            MaximumBrakingDecelerationMps2 = 2.5;
            MaximumAccelerationMps2 = 0.8;
            StartSpeedMps = 3.0;
            GravityMps2 = 9.80665;
            SurfaceObservationInfluenceRadiusMeters = 15.0;
        }
    }

    /// <summary>
    /// A derived, regularly spaced section sample. All speeds are metres/second,
    /// gradient is rise/run (0.05 = 5%), and curvature is reciprocal metres.
    /// </summary>
    public sealed class RouteProfileSample
    {
        public double DistanceMeters { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Z { get; set; }

        public double AltitudeMeters { get; set; }

        public double HeadingRadians { get; set; }

        public double Gradient { get; set; }

        public double SignedCurvaturePerMeter { get; set; }

        public double Straightness { get; set; }

        public int MaterialHash { get; set; }

        public double RollingResistanceCoefficient { get; set; }

        public double GripCoefficient { get; set; }

        public double RoughnessFactor { get; set; }

        public double SurfaceSpeedLimitMps { get; set; }

        public double RawCornerSpeedMps { get; set; }

        public double BrakingEnvelopeSpeedMps { get; set; }

        public double AccelerationEnvelopeSpeedMps { get; set; }

        public double SafeSpeedMps { get; set; }
    }

    public sealed class RouteProfile
    {
        public string AlgorithmVersion { get; set; }

        public double TotalDistanceMeters { get; set; }

        public RouteProfileOptions Options { get; set; }

        public List<RouteProfileSample> Samples { get; set; }

        public RouteProfile()
        {
            AlgorithmVersion = string.Empty;
            Options = new RouteProfileOptions();
            Samples = new List<RouteProfileSample>();
        }
    }

    /// <summary>
    /// JSON-friendly cache payload named externally as {course-id}-{version}.
    /// Raw inputs and derived output coexist so the output can be invalidated and
    /// rebuilt when algorithms or overrides change.
    /// </summary>
    public sealed class CachedRouteProfile
    {
        public string CourseId { get; set; }

        public string CourseVersion { get; set; }

        public string SourceFitFingerprint { get; set; }

        public string CalculatedUtc { get; set; }

        public RouteProfileInput Input { get; set; }

        public RouteProfileOptions Options { get; set; }

        public RouteProfile Profile { get; set; }

        public CachedRouteProfile()
        {
            CourseId = string.Empty;
            CourseVersion = string.Empty;
            SourceFitFingerprint = string.Empty;
            CalculatedUtc = string.Empty;
            Input = new RouteProfileInput();
            Options = new RouteProfileOptions();
            Profile = new RouteProfile();
        }

        public RouteProfile Recalculate()
        {
            Profile = RouteProfileBuilder.Build(Input, Options);
            return Profile;
        }
    }
}
