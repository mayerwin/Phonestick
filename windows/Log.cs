using System;
using System.IO;

namespace Phonestick
{
    /// <summary>A small text log in Phonestick.config\phonestick.log, for troubleshooting.</summary>
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Config.Dir);
                    var path = Path.Combine(Config.Dir, "phonestick.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
