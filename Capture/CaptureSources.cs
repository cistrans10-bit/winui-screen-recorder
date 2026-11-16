using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace ScreenRecorder.Capture;

internal sealed record MonitorInfo(IntPtr Handle, string Name, int Width, int Height, bool IsPrimary)
{
    public string Label => $"{Name}{(IsPrimary ? " (Primary)" : "")}  ·  {Width}×{Height}";
}

/// <summary>Enumerates monitors and creates GraphicsCaptureItems for them without a picker.</summary>
internal static unsafe class CaptureSources
{
    private static readonly Guid InteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid CaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var r = info.rcMonitor;
                const string prefix = @"\\.\DISPLAY";
                string device = info.szDevice ?? "";
                string name = device.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? "Display " + device[prefix.Length..]
                    : device;
                list.Add(new MonitorInfo(hMonitor, name, r.Right - r.Left, r.Bottom - r.Top, (info.dwFlags & 1) != 0));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Name).ToList();
    }

    public static GraphicsCaptureItem CreateForMonitor(IntPtr hMonitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
        try
        {
            Guid iid = InteropIid;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out var factory));
            try
            {
                Guid itemIid = CaptureItemIid;
                IntPtr item;
                // IGraphicsCaptureItemInterop: CreateForWindow = slot 3, CreateForMonitor = slot 4.
                var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(*(void***)factory + 4));
                Marshal.ThrowExceptionForHR(create(factory, hMonitor, &itemIid, &item));
                try
                {
                    return GraphicsCaptureItem.FromAbi(item);
                }
                finally
                {
                    Marshal.Release(item);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
}
