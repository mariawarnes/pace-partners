using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using PacePartners.Core;

namespace PacePartners.Mod.Persistence
{
    internal sealed class RouteProfileCacheDocument
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public int CourseId { get; set; }

        public string CourseVersion { get; set; } = string.Empty;

        public bool Reversed { get; set; }

        public string FitSha256 { get; set; } = string.Empty;

        public string OverridesSha256 { get; set; } = string.Empty;

        public string OptionsSha256 { get; set; } = string.Empty;

        public string MaterialsSha256 { get; set; } = string.Empty;

        public DateTime CalculatedUtc { get; set; }

        public RouteProfileInput Input { get; set; } = new RouteProfileInput();

        public RouteProfileOptions Options { get; set; } = new RouteProfileOptions();

        public RouteProfile Profile { get; set; } = new RouteProfile();
    }

    internal sealed class ManualRouteOverrides
    {
        public List<RouteProfileOverride> Sections { get; set; } =
            new List<RouteProfileOverride>();
    }

    /// <summary>
    /// Owns the recomputable route profile cache and the deliberately separate,
    /// user-edited override files.
    /// </summary>
    internal sealed class RouteProfileStore
    {
        private static readonly JsonSerializerSettings JsonSettings =
            new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                Culture = CultureInfo.InvariantCulture
            };
        private static readonly object FileGatesLock = new object();
        private static readonly Dictionary<string, object> FileGates =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private readonly string profilesDirectory;
        private readonly string overridesDirectory;

        public RouteProfileStore(string modSettingsDirectory)
        {
            string root = Path.Combine(modSettingsDirectory, "PacePartners");
            profilesDirectory = Path.Combine(root, "Profiles");
            overridesDirectory = Path.Combine(root, "Overrides");
            Directory.CreateDirectory(profilesDirectory);
            Directory.CreateDirectory(overridesDirectory);
        }

        public string GetProfilePath(int courseId, string version, bool reversed)
        {
            return Path.Combine(
                profilesDirectory,
                FileStem(courseId, version, reversed) + ".profile.json");
        }

        public string GetOverridePath(int courseId, string version, bool reversed)
        {
            return Path.Combine(
                overridesDirectory,
                FileStem(courseId, version, reversed) + ".json");
        }

        public RouteProfileCacheDocument TryLoad(
            int courseId,
            string version,
            bool reversed)
        {
            string path = GetProfilePath(courseId, version, reversed);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using (FileStream stream = OpenSharedRead(path))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    return JsonConvert.DeserializeObject<RouteProfileCacheDocument>(
                        reader.ReadToEnd(),
                        JsonSettings);
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public bool IsReusable(
            RouteProfileCacheDocument document,
            int courseId,
            string version,
            bool reversed,
            string fitSha256,
            string overridesSha256,
            RouteProfileOptions options,
            IList<SurfaceMaterialDefinition> materialDefinitions,
            SurfaceMaterialDefinition defaultSurface)
        {
            return document != null &&
                   document.SchemaVersion == RouteProfileCacheDocument.CurrentSchemaVersion &&
                   document.CourseId == courseId &&
                   string.Equals(document.CourseVersion, version, StringComparison.Ordinal) &&
                   document.Reversed == reversed &&
                   document.Input != null &&
                   document.Input.TracePoints != null &&
                   document.Input.SurfaceObservations != null &&
                   document.Input.MaterialDefinitions != null &&
                   document.Input.Overrides != null &&
                   document.Options != null &&
                   string.Equals(document.FitSha256, fitSha256, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       document.OverridesSha256,
                       overridesSha256,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       document.OptionsSha256,
                       FingerprintJson(options),
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       document.MaterialsSha256,
                       FingerprintMaterials(materialDefinitions, defaultSurface),
                       StringComparison.OrdinalIgnoreCase) &&
                   document.Profile != null &&
                   string.Equals(
                       document.Profile.AlgorithmVersion,
                       RouteProfileBuilder.CurrentAlgorithmVersion,
                       StringComparison.Ordinal) &&
                   document.Profile.Samples != null &&
                   document.Profile.Samples.Count > 1;
        }

        public void Save(RouteProfileCacheDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            document.SchemaVersion = RouteProfileCacheDocument.CurrentSchemaVersion;
            document.CalculatedUtc = DateTime.UtcNow;
            document.OptionsSha256 = FingerprintJson(document.Options);
            document.MaterialsSha256 = FingerprintMaterials(
                document.Input.MaterialDefinitions,
                document.Input.DefaultSurface);
            string path = GetProfilePath(
                document.CourseId,
                document.CourseVersion,
                document.Reversed);
            WriteJsonAtomically(path, document);
        }

        public ManualRouteOverrides LoadOverrides(
            int courseId,
            string version,
            bool reversed,
            out string fingerprint)
        {
            string path = GetOverridePath(courseId, version, reversed);
            if (!File.Exists(path))
            {
                EnsureOverrideTemplate(path, courseId, version, reversed);
                fingerprint = FingerprintFile(path);
                return new ManualRouteOverrides();
            }

            byte[] bytes;
            try
            {
                bytes = ReadAllBytesShared(path);
                ManualRouteOverrides result =
                    JsonConvert.DeserializeObject<ManualRouteOverrides>(
                        Encoding.UTF8.GetString(bytes),
                        JsonSettings) ??
                    new ManualRouteOverrides();
                result.Sections = (result.Sections ??
                    new List<RouteProfileOverride>())
                    .Where(section => section != null)
                    .ToList();
                fingerprint = FingerprintBytes(bytes);
                return result;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "The route override file is not valid JSON: " + path,
                    exception);
            }
        }

        public static string FingerprintFile(string path)
        {
            using (FileStream stream = OpenSharedRead(path))
            using (SHA256 hash = SHA256.Create())
            {
                return ToHex(hash.ComputeHash(stream));
            }
        }

        public static bool UpsertSurfaceObservation(
            IList<RouteSurfaceObservation> observations,
            RouteSurfaceObservation observation,
            double replacementRadiusMeters)
        {
            if (observations == null)
            {
                throw new ArgumentNullException(nameof(observations));
            }

            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            int replacementIndex = -1;
            double nearest = double.MaxValue;
            for (int i = 0; i < observations.Count; i++)
            {
                double difference = Math.Abs(
                    observations[i].DistanceMeters - observation.DistanceMeters);
                if (difference < nearest)
                {
                    nearest = difference;
                    replacementIndex = i;
                }
            }

            if (replacementIndex >= 0 && nearest <= replacementRadiusMeters)
            {
                if (observations[replacementIndex].MaterialHash == observation.MaterialHash)
                {
                    return false;
                }

                observations[replacementIndex] = observation;
            }
            else
            {
                observations.Add(observation);
            }

            List<RouteSurfaceObservation> ordered = observations
                .OrderBy(item => item.DistanceMeters)
                .ToList();
            observations.Clear();
            foreach (RouteSurfaceObservation item in ordered)
            {
                observations.Add(item);
            }

            return true;
        }

        private static string FileStem(int courseId, string version, bool reversed)
        {
            string safeVersion = string.Concat(
                (version ?? string.Empty).Select(
                    character => Path.GetInvalidFileNameChars().Contains(character)
                        ? '_'
                        : character));
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1}-{2}",
                courseId,
                safeVersion,
                reversed ? "reverse" : "forward");
        }

        private static string FingerprintJson(object value)
        {
            return FingerprintBytes(
                Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, JsonSettings)));
        }

        private static string FingerprintMaterials(
            IList<SurfaceMaterialDefinition> definitions,
            SurfaceMaterialDefinition defaultSurface)
        {
            var value = new
            {
                Default = defaultSurface,
                Definitions = definitions ?? new List<SurfaceMaterialDefinition>()
            };
            return FingerprintJson(value);
        }

        private static string FingerprintBytes(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return ToHex(hash.ComputeHash(bytes));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            using (FileStream stream = OpenSharedRead(path))
            using (MemoryStream output = new MemoryStream())
            {
                stream.CopyTo(output);
                return output.ToArray();
            }
        }

        private static FileStream OpenSharedRead(string path)
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }

        private static void WriteJsonAtomically(string path, object value)
        {
            string json = JsonConvert.SerializeObject(value, JsonSettings);
            object fileGate = GetFileGate(path);
            lock (fileGate)
            {
                string temporaryPath =
                    path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(
                        temporaryPath,
                        json,
                        new UTF8Encoding(false));
                    if (File.Exists(path))
                    {
                        File.Replace(temporaryPath, path, null);
                    }
                    else
                    {
                        File.Move(temporaryPath, path);
                    }
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
        }

        private static object GetFileGate(string path)
        {
            string fullPath = Path.GetFullPath(path);
            lock (FileGatesLock)
            {
                object gate;
                if (!FileGates.TryGetValue(fullPath, out gate))
                {
                    gate = new object();
                    FileGates.Add(fullPath, gate);
                }

                return gate;
            }
        }

        private static void EnsureOverrideTemplate(
            string path,
            int courseId,
            string version,
            bool reversed)
        {
            if (File.Exists(path))
            {
                return;
            }

            var template = new
            {
                CourseId = courseId,
                CourseVersion = version,
                Direction = reversed ? "reverse" : "forward",
                Sections = Array.Empty<RouteProfileOverride>()
            };
            WriteJsonAtomically(path, template);
        }
    }
}
