using System;
using System.Diagnostics;
using System.IO;

namespace ArcGisProAppYolo.Tools
{
    internal static class Logger
    {
        private static readonly string LogFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcGisProAppYolo", "logs");
        private static readonly string LogFile = Path.Combine(LogFolder, "yolo_addin.log");

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}" + Environment.NewLine;
                File.AppendAllText(LogFile, line);
                Debug.WriteLine(line);
            }
            catch
            {
                // swallow - logging must not throw
            }
        }

        public static string GetLogPath() => LogFile;
    }
}
