using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PacePartners.Mod.Infrastructure
{
    public sealed class CourseCatalogEntry
    {
        public CourseCatalogEntry(ushort id, string name, string version, bool loop, bool reverse)
        {
            Id = id;
            Name = name ?? string.Empty;
            Version = version ?? throw new ArgumentNullException(nameof(version));
            Loop = loop;
            Reverse = reverse;
        }

        public ushort Id { get; }
        public string Name { get; }
        public string Version { get; }
        public bool Loop { get; }
        public bool Reverse { get; }
    }

    public sealed class CourseCatalog
    {
        private readonly IReadOnlyList<CourseCatalogEntry> _courses;

        internal CourseCatalog(string sourcePath, string listVersion, IList<CourseCatalogEntry> courses)
        {
            SourcePath = sourcePath;
            ListVersion = listVersion;
            _courses = new ReadOnlyCollection<CourseCatalogEntry>(courses);
        }

        public string SourcePath { get; }
        public string ListVersion { get; }
        public IReadOnlyList<CourseCatalogEntry> Courses => _courses;

        public CourseCatalogEntry Find(ushort baseCourseId)
        {
            return _courses.FirstOrDefault(course => course.Id == baseCourseId);
        }

        /// <summary>
        /// Resolves only the exact GTBikeV cache name: {base-id}-{version}.fit.
        /// There is deliberately no newest-file or wildcard fallback.
        /// </summary>
        public string ResolveFitPath(ushort baseCourseId)
        {
            CourseCatalogEntry course = Find(baseCourseId);
            if (course == null)
                throw new KeyNotFoundException(
                    $"Course id {baseCourseId} is not present in '{SourcePath}'.");

            string directory = Path.GetDirectoryName(SourcePath);
            string fileName = baseCourseId.ToString(CultureInfo.InvariantCulture) +
                              "-" + course.Version + ".fit";
            string path = Path.Combine(directory ?? string.Empty, fileName);

            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"The exact cached FIT file for course {baseCourseId} version '{course.Version}' was not found.",
                    path);

            return path;
        }
    }

    public sealed class CourseCatalogReader
    {
        private readonly int _maximumAttempts;
        private readonly int _initialRetryDelayMilliseconds;

        public CourseCatalogReader(int maximumAttempts = 4, int initialRetryDelayMilliseconds = 25)
        {
            if (maximumAttempts < 1 || maximumAttempts > 10)
                throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
            if (initialRetryDelayMilliseconds < 0 || initialRetryDelayMilliseconds > 1000)
                throw new ArgumentOutOfRangeException(nameof(initialRetryDelayMilliseconds));

            _maximumAttempts = maximumAttempts;
            _initialRetryDelayMilliseconds = initialRetryDelayMilliseconds;
        }

        public CourseCatalog Read(string coursesJsonPath)
        {
            if (string.IsNullOrWhiteSpace(coursesJsonPath))
                throw new ArgumentException("A courses.json path is required.", nameof(coursesJsonPath));

            string fullPath = Path.GetFullPath(coursesJsonPath);
            Exception lastTransientError = null;

            for (int attempt = 0; attempt < _maximumAttempts; attempt++)
            {
                try
                {
                    return ReadOnce(fullPath);
                }
                catch (Exception ex) when (IsTransient(ex) && attempt + 1 < _maximumAttempts)
                {
                    lastTransientError = ex;
                    int delay = checked(_initialRetryDelayMilliseconds * (1 << attempt));
                    if (delay > 0)
                        Thread.Sleep(delay);
                }
            }

            throw new IOException(
                $"Could not obtain a stable read of '{fullPath}' after {_maximumAttempts} attempts.",
                lastTransientError);
        }

        private static CourseCatalog ReadOnce(string fullPath)
        {
            JObject root;
            using (var stream = new FileStream(
                       fullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var text = new StreamReader(stream))
            using (var json = new JsonTextReader(text))
            {
                root = JObject.Load(json);
            }

            string listVersion = PreserveTokenText(root["ListVersion"]);
            JArray list = root["CourseList"] as JArray;
            if (list == null)
                throw new JsonSerializationException("courses.json does not contain a CourseList array.");

            var courses = new List<CourseCatalogEntry>(list.Count);
            var ids = new HashSet<ushort>();
            foreach (JObject item in list.OfType<JObject>())
            {
                ushort id = RequireUInt16(item, "Id");
                string name = item.Value<string>("Name") ?? string.Empty;
                string version = PreserveTokenText(item["Version"]);
                ValidateVersion(version, id);

                if (!ids.Add(id))
                    throw new JsonSerializationException($"courses.json contains duplicate course id {id}.");

                courses.Add(new CourseCatalogEntry(
                    id,
                    name,
                    version,
                    item.Value<bool?>("Loop") ?? false,
                    item.Value<bool?>("Reverse") ?? false));
            }

            return new CourseCatalog(fullPath, listVersion, courses);
        }

        private static ushort RequireUInt16(JObject item, string propertyName)
        {
            JToken token = item[propertyName];
            if (token == null)
                throw new JsonSerializationException($"Course entry is missing {propertyName}.");

            try
            {
                return token.Value<ushort>();
            }
            catch (Exception ex)
            {
                throw new JsonSerializationException(
                    $"Course entry has an invalid {propertyName}: {token}.", ex);
            }
        }

        private static string PreserveTokenText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            return token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString(Formatting.None);
        }

        private static void ValidateVersion(string version, ushort id)
        {
            if (string.IsNullOrWhiteSpace(version))
                throw new JsonSerializationException($"Course {id} has no Version.");

            if (version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                version.Contains("/") ||
                version.Contains("\\"))
                throw new JsonSerializationException(
                    $"Course {id} has an unsafe cache Version '{version}'.");
        }

        private static bool IsTransient(Exception ex)
        {
            return ex is IOException ||
                   ex is UnauthorizedAccessException ||
                   ex is JsonReaderException ||
                   ex is JsonSerializationException;
        }
    }
}
