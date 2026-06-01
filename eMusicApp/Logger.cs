using System;
using System.IO;

namespace eMusicApp
{
    public static class Logger
    {
        private static readonly string LogFilePath;
        private static readonly object _lock = new object();

        static Logger()
        {
            try
            {
                // Write to public external storage if possible for easy access, otherwise local app data
                var extDir = Android.App.Application.Context.GetExternalFilesDir(null);
                if (extDir != null)
                {
                    LogFilePath = Path.Combine(extDir.AbsolutePath, "emusic_log.txt");
                }
                else
                {
                    var cacheDir = Android.App.Application.Context.CacheDir;
                    LogFilePath = Path.Combine(cacheDir?.AbsolutePath ?? "", "emusic_log.txt");
                }
            }
            catch
            {
                LogFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "emusic_log.txt");
            }
        }

        public static void Log(string message, Exception? ex = null)
        {
            try
            {
                lock (_lock)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    string logLine = $"[{timestamp}] {message}";
                    
                    if (ex != null)
                    {
                        logLine += $"\nEXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                        if (ex.InnerException != null)
                        {
                            logLine += $"\nINNER: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
                        }
                    }

                    // Print to logcat as well
                    Console.WriteLine("EMUSIC_LOG: " + logLine);

                    File.AppendAllText(LogFilePath, logLine + "\n");
                }
            }
            catch (Exception)
            {
                // Ignore logging errors to prevent recursive crashes
            }
        }

        public static string GetLogPath() => LogFilePath;
        
        public static string GetLogContent()
        {
            try
            {
                if (File.Exists(LogFilePath))
                {
                    return File.ReadAllText(LogFilePath);
                }
                return "No log file found.";
            }
            catch (Exception ex)
            {
                return "Error reading log: " + ex.Message;
            }
        }

        public static void ClearLog()
        {
            try
            {
                if (File.Exists(LogFilePath))
                {
                    File.Delete(LogFilePath);
                }
            }
            catch { }
        }
    }
}
