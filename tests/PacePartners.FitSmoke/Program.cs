using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PacePartners.Core;
using PacePartners.Mod.Infrastructure;
using PacePartners.Mod.Runtime;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string directory = args.Length > 0
                ? Path.GetFullPath(args[0])
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Rockstar Games",
                    "GTA V",
                    "ModSettings");
            Regex versionedName = new Regex(
                @"^\d+-.+\.fit$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            List<string> files = Directory
                .EnumerateFiles(directory, "*.fit", SearchOption.TopDirectoryOnly)
                .Where(path => versionedName.IsMatch(Path.GetFileName(path)))
                .OrderBy(ParseCourseId)
                .ToList();
            if (files.Count == 0)
            {
                throw new InvalidOperationException(
                    "No versioned GTBikeV FIT cache files were found in " + directory);
            }

            var reader = new FitCourseReader();
            var origin = new FitGeoOrigin(-19.10637, -169.871);
            int totalRecords = 0;
            int totalProfileSections = 0;
            int routeFiveRecords = 0;
            int hillyEightRecords = 0;
            double minimumGradient = double.MaxValue;
            double maximumGradient = double.MinValue;
            double minimumSafeSpeed = double.MaxValue;
            double maximumSafeSpeed = double.MinValue;
            string minimumGradientRoute = string.Empty;
            string maximumGradientRoute = string.Empty;
            double minimumGradientDistance = 0.0;
            double maximumGradientDistance = 0.0;

            foreach (string file in files)
            {
                DecodedFitRoute decoded = reader.Read(file, origin, 0.5);
                List<RouteTracePoint> trace =
                    FitTraceConverter.ToRouteTrace(decoded, false);
                RouteProfileInput input = new RouteProfileInput
                {
                    TracePoints = trace
                };
                RouteProfile profile = RouteProfileBuilder.Build(input);
                if (profile.Samples.Count < 2 ||
                    profile.TotalDistanceMeters <= 0.0 ||
                    profile.Samples.Any(sample =>
                        double.IsNaN(sample.Z) ||
                        double.IsInfinity(sample.Z)))
                {
                    throw new InvalidDataException(
                        Path.GetFileName(file) + " produced an invalid route profile.");
                }

                int courseId = ParseCourseId(file);
                if (courseId == 5)
                {
                    routeFiveRecords = decoded.Points.Count;
                }

                if (courseId == 40)
                {
                    hillyEightRecords = decoded.Points.Count;
                }

                totalRecords += decoded.Points.Count;
                totalProfileSections += profile.Samples.Count;
                double routeMinimumGradient =
                    profile.Samples.Min(sample => sample.Gradient);
                double routeMaximumGradient =
                    profile.Samples.Max(sample => sample.Gradient);
                if (routeMinimumGradient < minimumGradient)
                {
                    minimumGradient = routeMinimumGradient;
                    minimumGradientRoute = Path.GetFileName(file);
                    minimumGradientDistance = profile.Samples
                        .OrderBy(sample => sample.Gradient)
                        .First()
                        .DistanceMeters;
                }

                if (routeMaximumGradient > maximumGradient)
                {
                    maximumGradient = routeMaximumGradient;
                    maximumGradientRoute = Path.GetFileName(file);
                    maximumGradientDistance = profile.Samples
                        .OrderByDescending(sample => sample.Gradient)
                        .First()
                        .DistanceMeters;
                }
                minimumSafeSpeed = Math.Min(
                    minimumSafeSpeed,
                    profile.Samples.Min(sample => sample.SafeSpeedMps));
                maximumSafeSpeed = Math.Max(
                    maximumSafeSpeed,
                    profile.Samples.Max(sample => sample.SafeSpeedMps));
            }

            if (routeFiveRecords < 2)
            {
                throw new InvalidDataException(
                    "Course 5 did not retain its position records despite absent speed fields.");
            }

            if (hillyEightRecords < 2)
            {
                throw new InvalidDataException("Hilly 8 was not decoded.");
            }

            Console.WriteLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "PASS {0} cached routes, {1:N0} dense FIT records, {2:N0} calculated sections",
                    files.Count,
                    totalRecords,
                    totalProfileSections));
            Console.WriteLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Course 5 records={0:N0}; Hilly 8 records={1:N0}; gradient={2:P1}..{3:P1}; safe speed={4:0.00}..{5:0.00} m/s",
                    routeFiveRecords,
                    hillyEightRecords,
                    minimumGradient,
                    maximumGradient,
                    minimumSafeSpeed,
                    maximumSafeSpeed));
            Console.WriteLine(
                "Gradient extremes: " +
                minimumGradientRoute + " @" +
                minimumGradientDistance.ToString("0", CultureInfo.InvariantCulture) +
                "m / " + maximumGradientRoute + " @" +
                maximumGradientDistance.ToString("0", CultureInfo.InvariantCulture) +
                "m");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + exception);
            return 1;
        }
    }

    private static int ParseCourseId(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int separator = name.IndexOf('-');
        return int.Parse(
            separator < 0 ? name : name.Substring(0, separator),
            CultureInfo.InvariantCulture);
    }
}
