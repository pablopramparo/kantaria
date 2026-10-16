// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Kantor.App;

/// <summary>El monitor donde está una ventana, en píxeles físicos del escritorio virtual.</summary>
internal readonly record struct MonitorDescription(
    IntPtr Handle, string DeviceName, bool Primary, int X, int Y, int Width, int Height, double DpiScale);

/// <summary>
/// Límites del monitor donde está una ventana. Con la aplicación declarada <b>PerMonitorV2</b> (manifiesto) las
/// coordenadas de Win32 son píxeles físicos reales; las funciones <see cref="BoundsOf"/> y <see cref="WorkAreaOf"/>
/// los convierten a unidades independientes del dispositivo para WPF y <see cref="Describe"/> los deja en píxeles
/// físicos (lo que se publica al control remoto). Una ventana WPF sin borde y maximizada cubre solo el área de
/// trabajo (deja visible la barra de tareas); para una pantalla completa real hay que dimensionarla al monitor.
/// </summary>
internal static class NativeMonitor
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorInfoPrimary = 1;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private static bool TryInfo(Window window, out IntPtr handle, out MonitorInfoEx info)
    {
        info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = string.Empty };
        handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
    }

    private static Rect ToDips(Window window, Rect32 r)
    {
        var source = PresentationSource.FromVisual(window);
        Matrix fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(fromDevice.Transform(new Point(r.Left, r.Top)), fromDevice.Transform(new Point(r.Right, r.Bottom)));
    }

    /// <summary>Rectángulo completo del monitor de la ventana (unidades de WPF), o <c>null</c> si no se puede consultar.</summary>
    public static Rect? BoundsOf(Window window) =>
        TryInfo(window, out _, out var info) ? ToDips(window, info.Monitor) : null;

    /// <summary>Área de trabajo (sin barra de tareas) del monitor de la ventana (unidades de WPF).</summary>
    public static Rect? WorkAreaOf(Window window) =>
        TryInfo(window, out _, out var info) ? ToDips(window, info.Work) : null;

    /// <summary>El monitor de la ventana en píxeles físicos, con su nombre GDI y su escala de DPI.</summary>
    public static MonitorDescription? Describe(Window window)
    {
        if (!TryInfo(window, out var hwnd, out var info))
        {
            return null;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var dpi = GetDpiForWindow(hwnd);
        return new MonitorDescription(monitor, info.Device, (info.Flags & MonitorInfoPrimary) != 0,
            info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left,
            info.Monitor.Bottom - info.Monitor.Top, dpi == 0 ? 1.0 : dpi / 96.0);
    }

    /// <summary>
    /// Ajusta la ventana al monitor completo en píxeles físicos (sin pasar por unidades de WPF, que dependen del
    /// DPI del monitor de origen). Devuelve <c>false</c> si ya estaba así o no se pudo.
    /// </summary>
    public static bool FitToMonitor(Window window)
    {
        if (!TryInfo(window, out var hwnd, out var info))
        {
            return false;
        }

        var target = info.Monitor;
        if (GetWindowRect(hwnd, out var current)
            && current.Left == target.Left && current.Top == target.Top
            && current.Right == target.Right && current.Bottom == target.Bottom)
        {
            return false;
        }

        // sin tocar el orden en Z: "siempre arriba" lo decide la propiedad Topmost de la ventana
        return SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top, target.Right - target.Left,
            target.Bottom - target.Top, SwpNoActivate | SwpFrameChanged | SwpNoZOrder);
    }
}
