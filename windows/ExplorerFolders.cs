using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Phonestick
{
    /// <summary>The folders shown in open File Explorer windows, through the Shell's own list of them.</summary>
    internal static class ExplorerFolders
    {
        private static readonly Type ShellType = Type.GetTypeFromProgID("Shell.Application");

        /// <summary>Local paths ("P:\DCIM\Camera") of the open Explorer windows; empty if the Shell cannot be asked.</summary>
        public static List<string> Open()
        {
            var list = new List<string>();
            if (ShellType == null) return list;
            // The Shell's window list is an apartment-threaded COM object: asked from an STA thread.
            var t = new Thread(() =>
            {
                object shell = null, windows = null;
                try
                {
                    shell = Activator.CreateInstance(ShellType);
                    windows = Call(shell, "Windows", BindingFlags.InvokeMethod);
                    int count = (int)Call(windows, "Count", BindingFlags.GetProperty);
                    for (int i = 0; i < count; i++)
                    {
                        object w = null;
                        try
                        {
                            w = Call(windows, "Item", BindingFlags.InvokeMethod, i);
                            if (w == null) continue;
                            var url = Call(w, "LocationURL", BindingFlags.GetProperty) as string;
                            if (string.IsNullOrEmpty(url) || !url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) continue;
                            var path = new Uri(url).LocalPath;
                            lock (list) list.Add(path);
                        }
                        catch { }
                        finally { Release(w); }
                    }
                }
                catch { }
                finally { Release(windows); Release(shell); }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(3000);
            lock (list) return new List<string>(list);
        }

        private static object Call(object o, string name, BindingFlags how, params object[] args) =>
            o.GetType().InvokeMember(name, how, null, o, args.Length == 0 ? null : args);

        private static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
        }
    }
}
