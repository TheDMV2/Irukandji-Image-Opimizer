using System;
using System.IO;
using System.Threading.Tasks;

namespace Irukandji.ImageOptimizer.Logging
{
    public static class PluginLogger
    {
        private static readonly object LockObj = new object();
        private static string LogDirectory;

        public static void Initialize(string configPath)
        {
            try
            {
                LogDirectory = Path.Combine(configPath, "ImageOptimizerLogs");
                Directory.CreateDirectory(LogDirectory);
                
                // Keep server boot speeds native and decoupled from Disk IO checks
                Task.Run(() => CleanOldLogs());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Irukandji] Failed to initialize logger: {ex.Message}");
            }
        }

        public static void LogError(string message, Exception ex = null)
        {
            string formattedMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] {message}";
            if (ex != null)
            {
                formattedMsg += $"\nException: {ex.Message}\nStack: {ex.StackTrace}";
            }

            Console.WriteLine($"[Irukandji] {formattedMsg}");

            try
            {
                lock (LockObj)
                {
                    string filePath = Path.Combine(LogDirectory, $"log_{DateTime.Now:yyyyMMdd}.txt");
                    File.AppendAllText(filePath, formattedMsg + Environment.NewLine);
                }
            }
            catch
            {
                // Soft recovery if host system locks active directories or runs out of storage
            }
        }

        public static void LogInfo(string message)
        {
            Console.WriteLine($"[Irukandji] [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] {message}");
        }

        private static void CleanOldLogs()
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return;

                var files = Directory.GetFiles(LogDirectory, "log_*.txt");
                var expirationDate = DateTime.Now.AddDays(-30);

                foreach (var file in files) {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime < expirationDate) {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Irukandji] Failed to clean up expired logs: {ex.Message}");
            }
        }
    }
}