using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Phonestick
{
    /// <summary>
    /// A Windows job object that holds the helper processes this app starts (sshfs, which owns the
    /// drive letter, and scrcpy). It is set to kill everything in it when its last handle closes, and
    /// only this process holds that handle. So when Phonestick ends in any way (Quit, a crash, Task
    /// Manager, a power-off of the process), Windows itself ends sshfs, and with it the drive. No
    /// code of ours has to run for that, which is the point.
    /// </summary>
    internal static class ChildJob
    {
        private static readonly IntPtr Handle = Create();

        private static IntPtr Create()
        {
            try
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new InvalidOperationException("CreateJobObject failed: " + Marshal.GetLastWin32Error());
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                        throw new InvalidOperationException("SetInformationJobObject failed: " + Marshal.GetLastWin32Error());
                }
                finally { Marshal.FreeHGlobal(ptr); }
                return job; // never closed: Windows closes it when this process ends, which is what kills the children
            }
            catch (Exception ex)
            {
                Log.Write("job object: " + ex.Message);
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Puts a just-started process in the job. Processes it starts later join the job too.
        /// Returns false if Windows refused; the start-up cleanup (Mount.KillStale) then remains
        /// the fallback.
        /// </summary>
        public static bool Add(Process p)
        {
            if (Handle == IntPtr.Zero) return false;
            try
            {
                if (AssignProcessToJobObject(Handle, p.Handle)) return true;
                Log.Write("could not tie process " + p.Id + " to " + AppInfo.Name + ": " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { Log.Write("job assign: " + ex.Message); }
            return false;
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}
