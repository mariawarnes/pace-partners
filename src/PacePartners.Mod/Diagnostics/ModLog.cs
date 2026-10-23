using System;
using System.Globalization;
using System.IO;
using System.Text;
using GTA.UI;

namespace PacePartners.Mod.Diagnostics
{
    internal static class ModLog
    {
        private static readonly object Gate = new object();
        private static string path;

        public static void Initialize(string scriptsDirectory)
        {
            path = Path.Combine(scriptsDirectory, "PacePartners.log");
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        public static void Notify(string message)
        {
            Info(message);
            Notification.PostTicker(
                "~b~Pace Partners~s~: " + message,
                false,
                false);
        }

        private static void Write(string level, string message, Exception exception)
        {
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:O} [{1}] {2}{3}{4}",
                DateTime.UtcNow,
                level,
                message ?? string.Empty,
                exception == null ? string.Empty : Environment.NewLine,
                exception == null ? string.Empty : exception.ToString());
            lock (Gate)
            {
                try
                {
                    if (!string.IsNullOrEmpty(path))
                    {
                        File.AppendAllText(
                            path,
                            line + Environment.NewLine,
                            new UTF8Encoding(false));
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
