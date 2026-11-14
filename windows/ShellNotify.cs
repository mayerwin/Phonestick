using System;
using System.Runtime.InteropServices;

namespace Phonestick
{
    /// <summary>
    /// Tells Explorer a drive came or went. Without it, "This PC" can keep showing the old list
    /// until something makes it look again (typing the letter in the address bar, say).
    /// </summary>
    internal static class ShellNotify
    {
        private const int SHCNE_DRIVEADD = 0x00000100, SHCNE_DRIVEREMOVED = 0x00000080;
        private const uint SHCNF_PATHW = 0x0005, SHCNF_FLUSHNOWAIT = 0x3000;

        public static void DriveAdded(string drive) => Notify(SHCNE_DRIVEADD, drive);
        public static void DriveRemoved(string drive) => Notify(SHCNE_DRIVEREMOVED, drive);

        private static void Notify(int ev, string drive)
        {
            if (string.IsNullOrEmpty(drive)) return;
            var path = Marshal.StringToHGlobalUni(drive.TrimEnd('\\') + "\\");
            try { SHChangeNotify(ev, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, path, IntPtr.Zero); }
            catch (Exception ex) { Log.Write("shell notify: " + ex.Message); }
            finally { Marshal.FreeHGlobal(path); }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
