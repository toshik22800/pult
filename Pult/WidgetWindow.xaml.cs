using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Pult.Services;

namespace Pult;

// Виджет на рабочий стол: часы + CPU/RAM. Таскается мышью, живёт отдельно.
public partial class WidgetWindow : Window
{
    private readonly SystemMonitor _mon = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _tick;
    private bool _placed;

    public WidgetWindow()
    {
        InitializeComponent();
        // 🆕 0.14.0: масштаб UI — контент виджета живёт в ScaleTransform.
        Services.Appearance.ApplyScale(this);
        try { Topmost = AppSettings.Load().WidgetTopmost; } catch { }
        _placed = TryRestorePosition();
        if (!_placed)
            PlaceTopRight();
        Loaded += (_, _) =>
        {
            try
            {
                // Высоту знаем только после layout — дефолт едет в правый нижний угол.
                if (!_placed)
                {
                    var wa = WorkArea();
                    if (ActualHeight > 0)
                    {
                        Left = wa.Right - Width - 24;
                        Top = wa.Bottom - ActualHeight - 24;
                    }
                }
            }
            catch { }
        };
        _timer.Tick += (_, _) => Tick();
        Tick();
        _timer.Start();
        Closed += (_, _) =>
        {
            try { _timer.Stop(); } catch { }
            try
            {
                var s = AppSettings.Load();
                s.WidgetLeft = Left;
                s.WidgetTop = Top;
                s.WidgetVisible = false;
                s.Save();
            }
            catch { }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            // Виджет — не отдельная программа: прячем из Alt+Tab
            // (из панели задач уже убран через ShowInTaskbar=False).
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, -20);
            SetWindowLong(hwnd, -20, ex | 0x80);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    // Рабочая область (без панели задач) — только SystemParameters,
    // как везде в проекте. Новых зависимостей не тянем.
    private static Rect WorkArea()
    {
        try { return SystemParameters.WorkArea; }
        catch { return new Rect(0, 0, 1024, 768); }
    }

    // Сохранённая позиция: только если точка внутри WorkArea,
    // иначе монитор отключили — откат на дефолт.
    private bool TryRestorePosition()
    {
        try
        {
            var s = AppSettings.Load();
            double x = s.WidgetLeft, y = s.WidgetTop;
            if (double.IsNaN(x) || double.IsNaN(y)) return false;
            var wa = WorkArea();
            if (x < wa.Left - 200 || y < wa.Top || x > wa.Right || y > wa.Bottom)
                return false;
            Left = x;
            Top = y;
            return true;
        }
        catch { return false; }
    }

    private void PlaceTopRight()
    {
        try
        {
            var wa = WorkArea();
            Left = wa.Right - Width - 24;
            Top = wa.Top + 48;
        }
        catch { }
    }

    private void Tick()
    {
        try
        {
            _tick++;
            ClockText.Text = DateTime.Now.ToString("HH:mm");
            DateText.Text = DateTime.Now.ToString("dddd, d MMMM",
                new System.Globalization.CultureInfo("ru-RU"));
            if (_tick % 2 == 0)
            {
                double cpu = 0, mem = 0, disk = 0;
                try { cpu = _mon.CpuPercent(); } catch { }
                try { mem = _mon.Memory().UsedPct; } catch { }
                try
                {
                    double max = 0;
                    foreach (var d in SystemMonitor.Disks())
                    {
                        try { if (d.IsReady && d.UsedPct > max) max = d.UsedPct; }
                        catch { }
                    }
                    disk = max;
                }
                catch { }
                CpuRing.Value = cpu;
                CpuPct.Text = $"{cpu:F0}%";
                RamRing.Value = mem;
                RamPct.Text = $"{mem:F0}%";
                DiskRing.Value = disk;
                DiskPct.Text = $"{disk:F0}%";
            }
        }
        catch { }
    }

    private void Root_Drag(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.ChangedButton == MouseButton.Left
                && !(e.OriginalSource is System.Windows.Controls.Button))
                DragMove();
        }
        catch { }
    }

    private void Root_Location(object? sender, EventArgs e)
    {
        // Позицию пишем при закрытии, не на каждый пиксель.
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { }
    }
}
