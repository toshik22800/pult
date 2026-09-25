using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Pult.Services;

namespace Pult.Views;

public partial class SystemView : UserControl
{
    public Action<string> Report = _ => { };

    private readonly SystemMonitor _mon = new();
    private readonly DispatcherTimer _autoTimer;
    private bool _detailsOpen;

    // ==== 0.14.4 Sys B: приборная стена (бенто-плитки вкладки «Обзор») ====
    // Ключ плитки -> её дерево; разметка пересобирается только при смене
    // состава/режимов (сигнатура), значения и цвета — на каждом тике
    // (BentoPaint, вызывается из KpiHeroPaint).
    private sealed class BentoTile
    {
        public string Key = "";
        public Border Root = null!;
        public Border Strip = null!;   // полоса-статус 2px по верхнему краю
        public TextBlock Value = null!;
        public TextBlock? Sub;         // подпись рядом/справа от числа
        public TextBlock? Phrase;      // фраза-причина (только у hero)
        public Grid? Ruler;            // paint-грид BuildRuler (Tag=BarState)
        public Grid? Ruler2;           // вторая рулетка (память) у «Нагрузки»
    }
    private readonly Dictionary<string, BentoTile> _bento = new();
    private string _bentoSig = "";
    private string? _drawerKey;          // открытый ящик деталей
    private string _topProcLine = "";    // причина-фраза плитки «Нагрузка»

    private static readonly Brush WarnBrush;
    private static readonly Brush OkBrush;
    private static readonly Brush ErrBrush;

    static SystemView()
    {
        // Жёсткие цвета только семантические (ok/warn/error): их смысл — «норма /
        // внимание / критично» — универсален и не зависит от темы.
        // Треки/подложки берутся из ресурсов (B_Soft), цвета метрик — из ThemeService.
        var warn = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
        warn.Freeze();
        WarnBrush = warn;
        var ok = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        ok.Freeze();
        OkBrush = ok;
        var err = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
        err.Freeze();
        ErrBrush = err;
    }

    private static readonly HashSet<string> ProtectedProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "csrss", "wininit", "services", "lsass", "winlogon",
        "smss", "svchost", "dwm", "explorer", "registry", "memory compression",
    };

    private sealed record ProcRow(string Name, int Pid, double Mb, double Cpu = 0);

    private string _procMode = "mem"; // mem | cpu

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name) return;
        var panel = FindName(name) as StackPanel;
        if (panel == null) return;
        bool show = panel.Visibility != Visibility.Visible;
        panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        b.Content = show ? "—" : "+";
        // 🆕 0.14.3: превью журнала живёт, пока список свёрнут.
        try
        {
            if (name == "EventsPanel" && EventsPreview != null)
                EventsPreview.Visibility = show
                    ? Visibility.Collapsed : Visibility.Visible;
        }
        catch { }
    }

    // 🆕 0.14.3: чип «Ошибки» в строке критичного — журнал на вкладке
    // «Обзор», просто раскрываем его и подводим к себе.
    private void GoErrors_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _tab = "overview";
            RefreshTabs();
            if (EventsPanel != null) EventsPanel.Visibility = Visibility.Visible;
            if (EventsToggle != null) EventsToggle.Content = "—";
            if (EventsPreview != null) EventsPreview.Visibility = Visibility.Collapsed;
            // На «Обзоре» журнал открывается ящиком деталей под стеной плиток.
            OpenBentoDrawer("events");
        }
        catch { }
    }

    private void ProcMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string m && (m == "mem" || m == "cpu"))
        {
            _procMode = m;
            Refresh();
        }
    }

    public SystemView()
    {
        InitializeComponent();
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoTimer.Tick += (_, _) => RefreshFull(false);
        IsVisibleChanged += OnIsVisibleChanged;
        ApplySpeed();
        Loaded += (_, _) =>
        {
            try
            {
                var win = Window.GetWindow(this);
                if (win != null) win.StateChanged += (_, _) =>
                {
                    try
                    {
                        if (win.WindowState == WindowState.Minimized) _autoTimer.Stop();
                        else if (IsVisible && !_autoTimer.IsEnabled) { ApplySpeed(); _autoTimer.Start(); }
                    }
                    catch { }
                };
            }
            catch { }
            try { ApplySimpleMode(); } catch { }
            try { RefreshTabs(); } catch { }
            // Дорисовка героя — только когда разметка уже построена;
            // при каждом показе экрана (view кэшируется) это даёт «въезд» заново.
            try { ApplyFx(); } catch { }
            RebalanceSoon();
        };
        if (IsVisible) _autoTimer.Start();
        Refresh();
    }

    // Скорость опроса: минимум — раз в 15с, максимум — раз в 2с, иначе 5с.
    public void ApplySpeed()
    {
        try
        {
            int secs = AppSettings.EffectiveEffects() switch
            {
                AppSettings.EffectsMode.Minimum => 15,
                AppSettings.EffectsMode.Maximum => 2,
                _ => 5,
            };
            _autoTimer.Interval = TimeSpan.FromSeconds(secs);
            // Режим эффектов мог смениться вместе со скоростью — пересобрать glow/блики.
            try { ApplyFx(); } catch { }
        }
        catch { }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        try
        {
            var win = Window.GetWindow(this);
            bool minimized = win?.WindowState == WindowState.Minimized;
            if (IsVisible && !minimized) { ApplySpeed(); _autoTimer.Start(); }
            else _autoTimer.Stop();
        }
        catch { }
    }

    private bool _inLayout;
    private int _currentMode = -1; // 3 | 2 | 1
    private bool _layoutQueued;
    private string _tab = "overview";

    // Вкладки Системы: порядок внутри — как в XAML.
    private List<UIElement> TabCards(string tab) => tab switch
    {
        "perf" => new() { ProcsCard, StartupCard, ServicesCard },
        "storage" => new() { FoldersCard, SysFoldersCard, ProgramsCard, JunkCard },
        "privacy" => new() { PrivacyCard, TweaksCard },
        "hw" => new() { HwCard, SensorsCard, DriversCard, MonitorsCard, AudioCard },
        "net" => new() { AdaptersCard, WifiCard, TcpCard },
        _ => new() { LoadCard, PowerCard, EventsCard, UpdatesCard },
    };

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string t) return;
        _tab = t;
        RefreshTabs();
        ApplyLayout(true, animate: true);
    }

    private void RefreshTabs()
    {
        try
        {
            var accent = (Brush)FindResource("B_Accent");
            var border = (Brush)FindResource("B_Border");
            var soft = (Brush)FindResource("B_Soft");
            var tile = (Brush)FindResource("B_Tile");
            var text = (Brush)FindResource("B_Text");
            var muted = (Brush)FindResource("B_Muted");
            foreach (var ch in TabBar.Children)
            {
                if (ch is not Button b || b.Tag is not string t) continue;
                bool sel = t == _tab;
                b.BorderBrush = sel ? accent : border;
                b.Background = sel ? soft : tile;
                b.Foreground = sel ? text : muted;
                b.BorderThickness = sel ? new Thickness(2, 2, 2, 4) : new Thickness(2);
            }
            ApplyTabChrome();
        }
        catch { }
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 200) return;
        int mode = e.NewSize.Width >= 1750 ? 3 : e.NewSize.Width >= 1100 ? 2 : 1;
        if (mode == _currentMode) return; // тот же режим — двигать 21 карточку не надо, иначе виснет на ресайзе
        if (_layoutQueued) return;
        _layoutQueued = true;
        try { Pult.Services.CrashLog.Trace($"SizeChanged w={e.NewSize.Width:F0} mode {mode} cur {_currentMode}"); } catch { }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _layoutQueued = false;
            try
            {
                if (_tab == "overview") { _currentMode = mode; BentoLayout(); }
                else ApplyLayout(false);
            }
            catch (Exception ex) { try { Report("Ошибка раскладки: " + ex.Message); } catch { } }
        }), DispatcherPriority.Loaded);
    }

    private List<UIElement>? _cards;

    private void EnsureCards()
    {
        if (_cards != null) return;
        _cards = new List<UIElement>();
        foreach (UIElement c in LeftColumn.Children) _cards.Add(c);
        foreach (UIElement c in RightColumn.Children) _cards.Add(c);
    }

    private void ApplyLayout(bool force = true, bool animate = false)
    {
        if (_inLayout) return;
        try
        {
            _inLayout = true;
            EnsureCards();
            // На «Обзоре» колонки не раскладываем: живёт приборная стена, а
            // карточки могут лежать в ящике деталей — ApplyLayout вернул бы
            // раскрытую карточку обратно в колонку «сама собой».
            if (_tab == "overview") return;
            var all = _cards!;
            // Раскладываем только плашки активной вкладки; скрытые пропускаем.
            // Простой режим прячет сложное, пустые блоки — сами себя (батарея, Wi-Fi).
            var inTab = TabCards(_tab);
            var cards = all.Select((c, i) => (c, i))
                .Where(t => inTab.Contains(t.c)
                    && (t.c is not FrameworkElement fe || fe.Visibility == Visibility.Visible))
                .ToList();
            if (cards.Count == 0) return;
            double w = ActualWidth;
            if (double.IsNaN(w) || w < 200) return;
            if (LayoutGrid == null || LeftColumn == null || RightColumn == null || MidColumn == null) return;
            int mode = w >= 1750 ? 3 : w >= 1100 ? 2 : 1;
            if (!force && mode == _currentMode) return;
            // Высоты считаем ДО Clear — после отцепления ActualHeight уже 0.
            double[] heights;
            try { heights = cards.Select(t => GetCardHeight(t.c, t.i)).ToArray(); }
            catch { heights = cards.Select(t => t.i < CardWeights.Length ? CardWeights[t.i] : 250).ToArray(); }
            // Чистое вычисление: куда какая карточка, без трогания visuals.
            var target = new List<UIElement>[3];
            if (w >= 1750)
            {
                // Три колонки — делим НЕ по штукам, а по высоте, порядок сохраняем.
                var (a, b) = BestSplit3(heights);
                target[0] = cards.Take(a).Select(t => t.c).ToList();
                target[1] = cards.Skip(a).Take(b - a).Select(t => t.c).ToList();
                target[2] = cards.Skip(b).Select(t => t.c).ToList();
            }
            else if (w >= 1100)
            {
                int split = BestSplit2(heights);
                target[0] = cards.Take(split).Select(t => t.c).ToList();
                target[1] = cards.Skip(split).Select(t => t.c).ToList();
                target[2] = new List<UIElement>();
            }
            else
            {
                target[0] = cards.Select(t => t.c).ToList();
                target[1] = new List<UIElement>();
                target[2] = new List<UIElement>();
            }
            // Раскладка не изменилась — выходим не трогая: главное лекарство от дёргания.
            if (mode == _currentMode
                && SameOrder(LeftColumn, target[0])
                && SameOrder(MidColumn, target[1])
                && SameOrder(RightColumn, target[2])) return;
            _currentMode = mode;
            LayoutGrid.ColumnDefinitions.Clear();
            LayoutGrid.RowDefinitions.Clear();
            try
            {
        if (w >= 1750)
        {
            // Три колонки — делим НЕ по штукам, а по высоте, порядок сохраняем.
            for (int i = 0; i < 3; i++)
                LayoutGrid.ColumnDefinitions.Add(new ColumnDefinition
                    { Width = new GridLength(1, GridUnitType.Star) });
            LayoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cols = new[] { LeftColumn, MidColumn, RightColumn };
            foreach (var p in cols) { p.Children.Clear(); p.Visibility = Visibility.Visible; }
            foreach (var c in target[0]) LeftColumn.Children.Add(c);
            foreach (var c in target[1]) MidColumn.Children.Add(c);
            foreach (var c in target[2]) RightColumn.Children.Add(c);
            Grid.SetColumn(LeftColumn, 0); Grid.SetRow(LeftColumn, 0);
            Grid.SetColumn(MidColumn, 1); Grid.SetRow(MidColumn, 0);
            Grid.SetColumn(RightColumn, 2); Grid.SetRow(RightColumn, 0);
            LeftColumn.Margin = new Thickness(0, 0, 6, 0);
            MidColumn.Margin = new Thickness(6, 0, 6, 0);
            RightColumn.Margin = new Thickness(6, 0, 0, 0);
        }
        else if (w >= 1100)
        {
            // Bento-раскладка: левая колонка чуть шире — в ней живёт «Загрузка»
            // с секциями, правая узкая — список карточек.
            LayoutGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1.6, GridUnitType.Star) });
            LayoutGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            LayoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            MidColumn.Children.Clear();
            MidColumn.Visibility = Visibility.Collapsed;
            LeftColumn.Children.Clear();
            RightColumn.Children.Clear();
            foreach (var c in target[0]) LeftColumn.Children.Add(c);
            foreach (var c in target[1]) RightColumn.Children.Add(c);
            Grid.SetColumn(LeftColumn, 0); Grid.SetRow(LeftColumn, 0);
            Grid.SetColumn(RightColumn, 1); Grid.SetRow(RightColumn, 0);
            LeftColumn.Visibility = Visibility.Visible;
            RightColumn.Visibility = Visibility.Visible;
            LeftColumn.Margin = new Thickness(0, 0, 6, 0);
            RightColumn.Margin = new Thickness(6, 0, 0, 0);
        }
        else
        {
            LayoutGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            LayoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            MidColumn.Children.Clear();
            RightColumn.Children.Clear();
            LeftColumn.Children.Clear();
            MidColumn.Visibility = Visibility.Collapsed;
            RightColumn.Visibility = Visibility.Collapsed;
            LeftColumn.Visibility = Visibility.Visible;
            foreach (var c in target[0])
            {
                Grid.SetColumn(LeftColumn, 0);
                Grid.SetRow(LeftColumn, 0);
                LeftColumn.Children.Add(c);
            }
            LeftColumn.Margin = new Thickness(0);
        }
            }
            catch (Exception ex)
            {
                // Самый безопасный фолбэк — всё в одну колонку, окно живёт.
                try
                {
                    Pult.Services.CrashLog.Write("ApplyLayout", ex);
                    MidColumn.Children.Clear();
                    RightColumn.Children.Clear();
                    LeftColumn.Children.Clear();
                    MidColumn.Visibility = Visibility.Collapsed;
                    RightColumn.Visibility = Visibility.Collapsed;
                    LeftColumn.Visibility = Visibility.Visible;
                    foreach (var t in cards) LeftColumn.Children.Add(t.c);
                    _currentMode = 1;
                }
                catch { }
            }
            // Анимация — только по явной просьбе (переключение вкладки),
            // таймерные перебалансировки применяются молча.
            if (animate)
            {
                try
                {
                    foreach (var t in cards)
                        if (t.c is UIElement el) EffectsHelper.FadeIn(el);
                }
                catch { }
            }
        }
        finally { _inLayout = false; }
    }

    // Порядок детей уже как надо — переставлять нечего.
    private static bool SameOrder(StackPanel panel, List<UIElement> want)
    {
        try
        {
            if (panel.Children.Count != want.Count) return false;
            for (int i = 0; i < want.Count; i++)
                if (!ReferenceEquals(panel.Children[i], want[i])) return false;
            return true;
        }
        catch { return false; }
    }

    // Веса-заглушки пока карточки ещё не измерены (ActualHeight=0).
    // Порядок = исходный порядок в XAML: Загрузка, Драйверы, Службы, Сеть,
    // Мониторы, Приватность, Мусор, Питание, Железо, Процессы, Автозагрузка,
    // Папки, Программы, Датчики, Wi-Fi, Подключения, Звук, Папки системы,
    // Обновления, Ошибки, Твики.
    private static readonly double[] CardWeights =
        { 650, 120, 250, 800, 100, 500, 150, 180, 250, 450, 250, 300, 450, 250, 100, 40, 120, 250, 350, 40, 550 };

    private static double GetCardHeight(UIElement c, int index)
    {
        if (c is FrameworkElement fe && fe.ActualHeight > 20)
            return fe.ActualHeight + 12;
        if (index >= 0 && index < CardWeights.Length)
            return CardWeights[index];
        return 250;
    }

    private static int BestSplit2(double[] h)
    {
        double total = h.Sum();
        double acc = 0;
        int best = h.Length / 2;
        double bestDiff = double.MaxValue;
        for (int i = 1; i < h.Length; i++)
        {
            acc += h[i - 1];
            double diff = Math.Abs(total - 2 * acc);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        return best;
    }

    private static (int a, int b) BestSplit3(double[] h)
    {
        int n = h.Length;
        double[] pref = new double[n + 1];
        for (int i = 0; i < n; i++) pref[i + 1] = pref[i] + h[i];
        double total = pref[n];
        int ba = n / 3, bb = 2 * n / 3;
        double best = double.MaxValue;
        for (int a = 1; a < n - 1; a++)
            for (int b = a + 1; b < n; b++)
            {
                double h1 = pref[a], h2 = pref[b] - pref[a], h3 = total - pref[b];
                double diff = Math.Max(h1, Math.Max(h2, h3)) - Math.Min(h1, Math.Min(h2, h3));
                if (diff < best) { best = diff; ba = a; bb = b; }
            }
        return (ba, bb);
    }

    // Простой режим: оставляет понятное
    // (загрузка, железо, папки, программы, обновления, твики),
    // прячет техническое и пугающее.
    public void ApplySimpleMode()
    {
        try
        {
            bool simple = true;
            try { simple = AppSettings.Load().Interface == AppSettings.InterfaceMode.Simple; } catch { }
            var hide = simple ? Visibility.Collapsed : Visibility.Visible;
            SensorsCard.Visibility = hide;
            TcpCard.Visibility = hide;
            SysFoldersCard.Visibility = hide;
            EventsCard.Visibility = hide;
            JunkCard.Visibility = hide;
            ServicesCard.Visibility = hide;
            AdaptersCard.Visibility = hide;
            MonitorsCard.Visibility = hide;
            ProcsCard.Visibility = hide;
            ApplyLayout(true);
            // Простой режим прячет «События» — состав стены мог измениться.
            if (_tab == "overview") BentoLayout();
        }
        catch { }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var owner = Window.GetWindow(this);
            ResultDialog.ShowResult(owner, "\uE946", "Что здесь что — простыми словами",
                "Загрузка — насколько заняты процессор, память и диски. Если всё красное — компу тяжело.\n\n"
                + "Драйверы — программы для железа. Если «с проблемами нет» — всё хорошо.\n\n"
                + "Службы — фоновые задачи Windows. «Стоят» — значит должны работать, но не работают.\n\n"
                + "Сеть — интернет-провод и его скорость.\n\n"
                + "Мониторы — подключённые экраны.\n\n"
                + "Приватность — что Windows отдаёт Microsoft. «Закрыто» — хорошо.\n\n"
                + "Мусор — встроенные приложения Windows, которые можно удалить.\n\n"
                + "Питание — режим: сбалансированный (обычно) или максимальная скорость.\n\n"
                + "Железо — процессор, видеокарта, память. Кнопка «Подробнее» — детали.\n\n"
                + "Процессы — кто сейчас ест память и процессор. Завершать — только с флагом.\n\n"
                + "Автозагрузка — что запускается вместе с Windows и тормозит старт.\n\n"
                + "Папки — где лежат твои файлы и сколько весят.\n\n"
                + "Программы — что установлено и сколько занимает.\n\n"
                + "Wi-Fi — сети рядом (если Wi-Fi есть и включён).\n\n"
                + "Звук — наушники и колонки.\n\n"
                + "Обновления — свежие ли Windows и программы.\n\n"
                + "Твики — полезные переключатели Windows в один клик.\n\n"
                + "Всё опасное спрашивает подтверждения. Любое изменение можно откатить во вкладке «История».");
        }
        catch { }
    }

    // После подгрузки данных высоты меняются — перебалансировать на следующем тике.
    private void RebalanceSoon()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { ApplyLayout(true); } catch { }
        }), DispatcherPriority.Loaded);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        _detailsOpen = !_detailsOpen;
        DetailsPanel.Visibility = _detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = _detailsOpen ? "Скрыть" : "Подробнее";
    }

    private (string GpuVersion, string GpuDate) _lastGpu = ("", "");
    private (string LastSearch, string LastInstall, bool RebootNeeded) _lastWu = ("", "", false);

    private DateTime _lastFull = DateTime.MinValue;

    public void Refresh() => RefreshFull(true);

    // Тихий тик таймера: только живые метры in-place (бары — через кэш BarState,
    // тексты — присвоением). Без перестроения панелей и колонок.
    // Полный перестрой — по кнопке и не чаще раза в минуту (см. _lastFull).
    private void RefreshFast()
    {
        Task.Run(() =>
        {
            double cpu = 0, memPct = 0;
            long memUsed = 0, memTotal = 0;
            try { cpu = _mon.CpuPercent(); } catch { }
            try
            {
                var m = _mon.Memory();
                memPct = m.UsedPct; memUsed = m.UsedBytes; memTotal = m.TotalBytes;
            }
            catch { }
            var net = SystemMonitor.NetworkSpeed();
            return (cpu, memPct, memUsed, memTotal, net);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var r = t.Result;
                    SetBar(CpuBarGrid, r.cpu, "cpu");
                    CpuPctText.Text = $"{r.cpu:F0}%";
                    CpuPctText.Foreground = PctSemi(r.cpu);
                    SetRamBar(RamBarGrid, r.memPct);
                    RamPctText.Text = $"{r.memPct:F0}%";
                    RamPctText.Foreground = PctSemi(r.memPct);
                    RamSubText.Text = r.memTotal > 0
                        ? $"Занято {DiskInfo.Gb(r.memUsed)} из {DiskInfo.Gb(r.memTotal)}" : "";
                    NetworkLiveText.Text = r.net.Iface.Length == 0
                        ? "—"
                        : $"{r.net.Iface}: ↓ {FmtSpeed(r.net.DownKbps)}  ↑ {FmtSpeed(r.net.UpKbps)}";
                    // Герои и спарклайн живут на том же тике — данные те же, лишних опросов нет.
                    PushNetSample(r.net);
                    RenderNetSpark();
                    UpdateHeroCpu(r.cpu);
                    UpdateHeroRam(r.memPct, r.memUsed, r.memTotal);
                    UpdateHeroNet(r.net);
                    TickFresh();
                    if ((DateTime.Now - _lastFull).TotalSeconds >= 60) RefreshFull(true);
                }
                catch { }
            });
        });
    }

    // full=false — тихий тик (только метры). Полный перестрой — по кнопке
    // и сам не чаще раза в минуту (см. RefreshFast).
    public void RefreshFull(bool full)
    {
        if (!full)
        {
            RefreshFast();
            return;
        }
        _lastFull = DateTime.Now;
        string mode = _procMode;
        Task.Run(() =>
        {
            var info = HardwareService.GetInfo();
            double cpu = _mon.CpuPercent();
            var (memPct, memUsed, memTotal) = _mon.Memory();
            var disks = SystemMonitor.Disks();
            var procs = TopProcs(mode);
            var net = SystemMonitor.NetworkSpeed();
            var startup = SystemMonitor.StartupEntries();
            var gpuDrv = SystemMonitor.GpuDriver();
            var programs = SystemMonitor.InstalledPrograms();
            var services = SystemMonitor.ServicesInfo();
            var battDetail = SystemMonitor.BatteryDetail();
            var pagefile = SystemMonitor.PageFile();
            var wininfo = SystemMonitor.WindowsInfo();
            var adapters = SystemMonitor.Adapters();
            var folders = SystemMonitor.HomeFolders();
            var monitors = SystemMonitor.Monitors();
            var wifi = ExtraInfoService.WifiNetworks();
            var tcp = ExtraInfoService.TcpConnections();
            var audio = ExtraInfoService.AudioDevices();
            var wu = ExtraInfoService.WindowsUpdate();
            var events = ExtraInfoService.EventErrors();
            var powerSchemes = PrivacyService.PowerSchemes();
            bool exp = AppSettings.ExpOn();
            return (info, cpu, memPct, memUsed, memTotal, disks, procs, net, startup,
                gpuDrv, programs, services, battDetail, pagefile, wininfo,
                adapters, folders, monitors, wifi, tcp, audio,
                wu, events, powerSchemes, exp, mode);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Dispatcher.Invoke(() => Report("Не удалось обновить данные системы."));
                return;
            }
            var r = t.Result;
            Dispatcher.Invoke(() =>
            {
                try
                {
                // Один проход отрисовки вместо мельтешения: глушим промежуточные layout.
                using (Dispatcher.DisableProcessing())
                {
                SetBar(CpuBarGrid, r.cpu, "cpu");
                CpuPctText.Text = $"{r.cpu:F0}%";
                CpuPctText.Foreground = PctSemi(r.cpu);
                SetRamBar(RamBarGrid, r.memPct);
                RamPctText.Text = $"{r.memPct:F0}%";
                RamPctText.Foreground = PctSemi(r.memPct);
                RamSubText.Text = r.memTotal > 0
                    ? $"Занято {DiskInfo.Gb(r.memUsed)} из {DiskInfo.Gb(r.memTotal)}"
                        + (r.pagefile.TotalBytes > 0
                            ? $" • подкачка {DiskInfo.Gb(r.pagefile.UsedBytes)} из {DiskInfo.Gb(r.pagefile.TotalBytes)}"
                            : "")
                    : "";
                if (r.battDetail.Length > 0) BatteryText.Text = r.battDetail;
                NetworkLiveText.Text = r.net.Iface.Length == 0
                    ? "—"
                    : $"{r.net.Iface}: ↓ {FmtSpeed(r.net.DownKbps)}  ↑ {FmtSpeed(r.net.UpKbps)}";
                BuildDisks(r.disks);
                BuildHardware(r.info, r.disks, r.wininfo);
                BuildProcs(r.procs, r.exp, r.mode);
                BuildStartup(r.startup);
                _lastGpu = r.gpuDrv;
                _lastWu = r.wu;
                BuildDrivers(r.gpuDrv, new List<string>());
                DriversPanel.Children.Add(new TextBlock
                {
                    Text = "Проверяю устройства…",
                    FontSize = 12, Foreground = (Brush)FindResource("B_Muted"),
                    Margin = new Thickness(0, 4, 0, 0),
                });
                BuildPrograms(r.programs);
                BuildServices(r.services);
                BuildAdapters(r.adapters, r.net);
                BuildFolders(r.folders);
                BuildMonitors(r.monitors);
                BuildWifi(r.wifi);
                BuildTcp(r.tcp);
                BuildAudio(r.audio);
                BuildUpdates(r.wu, new List<WingetService.WingetUpdate>(),
                    WingetService.Available(), known: false); // 🆕 0.14.3: список ещё не пришёл
                UpdatesPanel.Children.Add(new TextBlock
                {
                    Text = "Загружаю список…",
                    FontSize = 12, Foreground = (Brush)FindResource("B_Muted"),
                    Margin = new Thickness(0, 6, 0, 0),
                });
                BuildEvents(r.events);
                BuildTweaks();
                BuildPrivacy();
                BuildPower(r.powerSchemes);
                BuildJunk(null);
                // Батареи нет (десктоп) — прячем блок целиком.
                try
                {
                    bool hasBatt = BatteryText.Text.Length > 0 && BatteryText.Text != "—";
                    var bv = hasBatt ? Visibility.Visible : Visibility.Collapsed;
                    BatteryHeader.Visibility = bv;
                    BatteryText.Visibility = bv;
                }
                catch { }
                // Один баннер вместо повторов про флаг в каждой плашке.
                try { ExpBanner.Visibility = r.exp ? Visibility.Collapsed : Visibility.Visible; }
                catch { }
                // Герои получают свежие значения полного снимка; штамп «обновлено» — сюда же,
                // чтобы не плодить отдельные таймеры.
                StampFresh();
                UpdateHeroCpu(r.cpu);
                UpdateHeroRam(r.memPct, r.memUsed, r.memTotal);
                UpdateHeroDisk(r.disks);
                UpdateHeroNet(r.net);
                PushNetSample(r.net);
                RenderNetSpark();
                TickFresh();
                RebalanceSoon();
                }
                }
                catch (Exception ex)
                {
                    try { Report("Ошибка отрисовки: " + ex.Message); } catch { }
                }
            });
        });
        if (full)
        {
            // Медленное — отдельными потоками, каждый дорисовывает свою карточку.
            // Все Invoke в try/catch: гонка _lastGpu/_lastWu больше не роняет окно.
            // Сторож: если проверка висит дольше 30с — текст ошибки вместо вечной загрузки.
            bool drvDone = false, wuDone = false, junkDone = false;
            Task.Run(() => SystemMonitor.ProblemDevices()).ContinueWith(t =>
            {
                var problems = t.IsFaulted || t.Result == null
                    ? new List<string>() : t.Result;
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        drvDone = true;
                        BuildDrivers(_lastGpu, problems);
                    }
                    catch (Exception ex) { try { Report("Ошибка драйверов: " + ex.Message); } catch { } }
                });
            });
            Task.Run(() => SensorsService.Read()).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    try { if (!t.IsFaulted && t.Result != null) BuildSensors(t.Result); }
                    catch { }
                });
            });
            Task.Run(() => ExtraInfoService.BigSystemFolders()).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    try { if (!t.IsFaulted && t.Result != null) BuildSysFolders(t.Result); }
                    catch { }
                });
            });
            Task.Run(() => WingetService.Available() ? WingetService.ListUpdates()
                : new List<WingetService.WingetUpdate>()).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        wuDone = true;
                        if (!t.IsFaulted && t.Result != null)
                            BuildUpdates(_lastWu, t.Result, WingetService.Available());
                        else
                            BuildUpdates(_lastWu, new List<WingetService.WingetUpdate>(),
                                WingetService.Available(), known: false); // 🆕 0.14.3: winget завис
                    }
                    catch (Exception ex) { try { Report("Ошибка обновлений: " + ex.Message); } catch { } }
                });
            });
            Task.Run(() => AppxService.ListApps()).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        junkDone = true;
                        if (!t.IsFaulted && t.Result != null) BuildJunk(t.Result);
                        else
                        {
                            JunkTitle.Text = "Мусор";
                            JunkPanel.Children.Clear();
                            JunkPanel.Children.Add(new TextBlock
                            {
                                Text = "Список приложений не прочитался. Нажми «Обновить».",
                                FontSize = 12, Foreground = (Brush)FindResource("B_Muted"),
                                TextWrapping = TextWrapping.Wrap,
                            });
                        }
                    }
                    catch { }
                });
            });
            Task.Delay(30000).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var muted = (Brush)FindResource("B_Muted");
                        if (!drvDone)
                        {
                            DriversTitle.Text = "Драйверы";
                            DriversPanel.Children.Add(new TextBlock
                            {
                                Text = "Проверка зависла (30 с): pnputil не ответил. Нажми «Обновить».",
                                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 4, 0, 0),
                            });
                        }
                        if (!wuDone)
                        {
                            UpdatesTitle.Text = "Обновления";
                            UpdatesPanel.Children.Add(new TextBlock
                            {
                                Text = "Список обновлений программ завис (30 с): winget не ответил.",
                                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 6, 0, 0),
                            });
                        }
                        if (!junkDone)
                        {
                            JunkTitle.Text = "Мусор";
                            JunkPanel.Children.Clear();
                            JunkPanel.Children.Add(new TextBlock
                            {
                                Text = "Проверка зависла (30 с): список приложений не прочитался. Нажми «Обновить».",
                                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                            });
                        }
                    }
                    catch { }
                });
            });
        }
    }

    private static string FmtSpeed(double kbps)
    {
        if (kbps >= 1024) return $"{kbps / 1024:F1} МБ/с";
        if (kbps >= 1) return $"{kbps:F0} КБ/с";
        return "0 КБ/с";
    }

    // --- Рулетки-бары: цвет ТОЛЬКО из ThemeService.MetricPair(key) ---
    // Локальных палитр больше нет: смена темы/акцента должна перекрашивать
    // и бары, и рулетки героя, а дублировать пары в коде — источник расхождений.
    // Ключи: cpu, ram, disk, net, wifi, folder, accent, hot.
    private static (Color From, Color To) Metric(string key) => ThemeService.MetricPair(key);

    // Состояние одного бара — живёт в Tag грида вместо старого списка сегментов.
    private sealed class BarState
    {
        public Border Fill = null!;
        public ScaleTransform Scale = null!;
        public Border? Tail;          // приглушённый хвост шкалы (dimTail), спереди Fill его перекрывает
        public string Key = "accent"; // ключ MetricPair — нужен для перекраски из ApplyPalette
        public bool DimTail;
        public bool HotZones = true;  // был ли у бара «горячий» сектор — плоские бары остаются плоскими
        public double LastPct;
        public DropShadowEffect? Glow;
    }

    private void SetBar(Grid g, double pct, string key) => PaintLeds(g, pct, key);

    // RAM-бар: заливка + приглушённый хвост — «занято из сколько» читается с одного взгляда.
    private void SetRamBar(Grid g, double pct) => PaintLeds(g, pct, "ram", dimTail: true);

    // Филл создаётся один раз (Border + ScaleTransform) и лежит в Tag грида —
    // дальше только меняем градиент и масштаб. Гриды дисков/папок пересоздаются
    // целиком, там Tag просто пересобирается заново.
    private void PaintLeds(Grid g, double pct, string key,
        bool dimTail = false, bool hotZones = true)
    {
        pct = Math.Clamp(pct, 0, 100);

        // Режим эффектов читаем один раз; ошибка — обычный режим.
        AppSettings.EffectsMode mode;
        try { mode = AppSettings.EffectiveEffects(); }
        catch { mode = AppSettings.EffectsMode.Normal; }

        // Почти полный бар — тёплая предупреждающая пара (кроме баров без зон).
        string useKey = hotZones && pct >= 90 ? "hot" : key;
        var pair = Metric(useKey);

        // Старый Tag (список LED-сегментов) отбрасываем и строим филл заново.
        if (g.Tag is not BarState st)
        {
            g.ColumnDefinitions.Clear();
            g.Children.Clear();
            var fill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new ScaleTransform(0, 1),
            };
            g.Children.Add(fill);
            st = new BarState
            {
                Fill = fill,
                Scale = (ScaleTransform)fill.RenderTransform,
            };
            g.Tag = st;
        }

        // Горизонтальный градиент на всю ширину бара (в Minimum — плоская
        // сплошная заливка парой метрики), заполнение — масштабом по X.
        Brush brush;
        if (mode == AppSettings.EffectsMode.Minimum)
        {
            var flat = new SolidColorBrush(pair.From);
            flat.Freeze();
            brush = flat;
        }
        else
        {
            var grad = new LinearGradientBrush(pair.From, pair.To, new Point(0, 0.5), new Point(1, 0.5));
            grad.Freeze();
            brush = grad;
        }
        st.Fill.Background = brush;
        st.Key = key;
        st.DimTail = dimTail;
        st.HotZones = hotZones;

        // Приглушённый хвост шкалы (RAM): создаём один раз, перекрашиваем тем же градиентом.
        if (dimTail && st.Tail == null)
        {
            var tail = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Opacity = 0.22,
                Background = brush,
            };
            g.Children.Insert(0, tail);
            st.Tail = tail;
        }
        else if (st.Tail != null)
        {
            st.Tail.Visibility = dimTail ? Visibility.Visible : Visibility.Collapsed;
            st.Tail.Background = brush;
        }

        // Свечение только в Maximum — статичное, без пульсаций (эффект у каждого бара свой).
        if (mode == AppSettings.EffectsMode.Maximum)
        {
            st.Glow = new DropShadowEffect
            {
                Color = pair.To,
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.55,
            };
            st.Fill.Effect = st.Glow;
        }
        else
        {
            st.Fill.Effect = null;
            st.Glow = null;
        }

        // Мгновенное присвоение: никаких EaseOut-доехживаний и накопленных
        // анимаций — шкала просто встаёт в новое значение на этом тике.
        st.Scale.ScaleX = pct / 100.0;
        st.LastPct = pct;
    }

    // ================= Дизайнерские блоки строк (единый стиль всех вкладок) =================

    // Динамический FontFamily из ресурса: переключатель шрифта в настройках
    // меняет F_Ui — экран переоборудуется без пересборки разметки.
    // Ресурса нет — шрифт остаётся унаследованным от предка.
    private static void UiFont(TextBlock tb) =>
        tb.SetResourceReference(TextBlock.FontFamilyProperty, "F_Ui");

    // Плитка-глиф 28×28: квадрат B_Tile с рамкой 1px, глиф окрашен парой метрики.
    private Border GlyphTile(string glyph, string key)
    {
        var pair = ThemeService.MetricPair(key);
        var fg = new SolidColorBrush(ThemeService.Tint(pair.To, 0.06));
        fg.Freeze();
        var g = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = fg,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var tile = new Border
        {
            Width = 28,
            Height = 28,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = g,
        };
        tile.SetResourceReference(Border.BackgroundProperty, "B_Tile");
        tile.SetResourceReference(Border.BorderBrushProperty, "B_Border");
        return tile;
    }

    // Точечный индикатор-статус слева от строки: квадрат 4px семантического цвета.
    private Border StatusDot(Brush color) => new()
    {
        Width = 4,
        Height = 4,
        Background = color,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    // Прямоугольный чип-статус (никаких скруглений): фон B_Tile, рамка 1px,
    // текст F_Ui 11 семантического цвета (ok/warn/err) или приглушённый.
    private Border StatusChip(string text, Brush fg)
    {
        var tb = new TextBlock { Text = text, FontSize = 11, Foreground = fg };
        UiFont(tb);
        var chip = new Border
        {
            Child = tb,
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        chip.SetResourceReference(Border.BackgroundProperty, "B_Tile");
        chip.SetResourceReference(Border.BorderBrushProperty, "B_Border");
        return chip;
    }

    // Строка-плитка вкладок: [глиф-квадрат или точка] | [заголовок F_Ui 13 +
    // подпись F_Ui 11 B_Muted] | [чипы и кнопка справа], опционально —
    // рулетка-измеритель под строкой. Карточка ProcCardStyle: квадрат, ховер.
    private Border TileRow(UIElement lead, string title, string? sub,
        IEnumerable<UIElement>? right, UIElement? ruler = null,
        Brush? subBrush = null, bool wrap = false)
    {
        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (wrap) titleTb.TextWrapping = TextWrapping.Wrap;
        titleTb.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
        UiFont(titleTb);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(titleTb);
        if (sub != null && sub.Length > 0)
        {
            var subTb = new TextBlock
            {
                Text = sub,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            };
            if (subBrush != null) subTb.Foreground = subBrush;
            else subTb.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
            UiFont(subTb);
            stack.Children.Add(subTb);
        }

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(stack, 1);
        content.Children.Add(stack);
        bool hasLead = lead != null;
        if (hasLead)
        {
            Grid.SetColumn(lead, 0);
            content.Children.Add(lead);
            stack.Margin = new Thickness(10, 0, 0, 0);
        }
        if (right != null)
        {
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            foreach (var p in right) rightPanel.Children.Add(p);
            Grid.SetColumn(rightPanel, 2);
            content.Children.Add(rightPanel);
        }

        UIElement body = content;
        if (ruler != null)
        {
            var rows = new Grid();
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(content, 0);
            rows.Children.Add(content);
            Grid.SetRow(ruler, 1);
            // Margin живёт на FrameworkElement — рулетка всегда Border.
            if (ruler is FrameworkElement rfe)
                rfe.Margin = new Thickness(hasLead ? 38 : 0, 6, 0, 0);
            rows.Children.Add(ruler);
            body = rows;
        }

        var card = new Border { Child = body };
        try { card.Style = (Style)FindResource("ProcCardStyle"); } catch { }
        return card;
    }

    // Две колонки label / value с волосяным разделителем (параметры сети,
    // датчики, сведения): подпись F_Ui 11 muted, значение 12 справа, перенос есть.
    private Grid KvRow(string label, string value, Brush? valueBrush = null)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock
        {
            Text = label,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260,
        };
        l.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
        UiFont(l);
        Grid.SetColumn(l, 0);
        g.Children.Add(l);
        var hair = new Border
        {
            Height = 1,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        hair.SetResourceReference(Border.BackgroundProperty, "B_Border");
        Grid.SetColumn(hair, 1);
        g.Children.Add(hair);
        var v = new TextBlock
        {
            Text = string.IsNullOrEmpty(value) ? "—" : value,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 0, 0),
            TextAlignment = TextAlignment.Right,
        };
        if (valueBrush != null) v.Foreground = valueBrush;
        else v.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
        Grid.SetColumn(v, 2);
        g.Children.Add(v);
        return g;
    }

    // Ruler-компонент (общий для файла): трек B_Tile 5px, заливка PaintLeds —
    // градиент пары ble→hi по ширине доли (в Minimum — плоская сплошная),
    // засечки — пять волосяных B_BorderH по 10/30/50/70/90%, процент справа
    // F_Ui 11. Полностью статичен: обновление = присвоение, анимаций нет.
    private Grid BuildRuler(double pct, string key, bool hotZones = true, bool showPct = true)
    {
        var paint = new Grid();
        var host = new Grid();
        host.Children.Add(paint);
        var ticks = new Grid();
        for (int i = 0; i < 5; i++)
            ticks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 5; i++)
        {
            var t = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center };
            t.SetResourceReference(Border.BackgroundProperty, "B_BorderH");
            Grid.SetColumn(t, i);
            ticks.Children.Add(t);
        }
        host.Children.Add(ticks);
        var track = new Border { Height = 5, ClipToBounds = true, Child = host };
        track.SetResourceReference(Border.BackgroundProperty, "B_Tile");
        PaintLeds(paint, pct, key, hotZones: hotZones);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(track, 0);
        row.Children.Add(track);
        if (showPct)
        {
            var pctTb = new TextBlock
            {
                Text = $"{Math.Round(pct):F0}%",
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            pctTb.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
            UiFont(pctTb);
            Grid.SetColumn(pctTb, 1);
            row.Children.Add(pctTb);
        }
        return row;
    }

    // ================= Герои (KPI-плитки), свежесть, спарклайн =================

    // Данные для подписей героев; копятся из тех же проходов, что и старые карточки —
    // отдельных опросов нет.
    private string _cpuModel = "";
    private string _cpuDetail = "";
    private string _cpuTempText = "";
    private double _cpuTempVal;               // 🆕 0.14.3: число для чипа и цвета
    private int _eventsCount;                 // 🆕 0.14.3: свежие ошибки (чип/полоса)
    private int _updatesPending = -1;         // 🆕 0.14.3: -1 = ещё не проверено
    private bool _updatesReboot;              // 🆕 0.14.3: Windows Update просит ребут
    private double _netLinkMbps;                       // максимум скорости линка (для % героя «Сеть»)
    private readonly double[] _netHist = new double[20]; // кольцевой буфер спарклайна, % от линка
    private int _netHistNext;
    private DateTime _freshAt = DateTime.MinValue;      // когда был полный снимок (шапка «обновлено»)

    // Тексты процессора: строка в карточке «Загрузка» и подпись под значением героя.
    private void RefreshCpuTexts()
    {
        try
        {
            if (CpuInfoLine != null)
                CpuInfoLine.Text = _cpuModel.Length == 0 ? "—" : _cpuModel;
            if (KpiCpuSub == null) return;
            // Подпись героя — частота (фрагмент cpu_detail после «/») + температура.
            string freq = "";
            int i = _cpuDetail.IndexOf('/');
            if (i >= 0 && i + 1 < _cpuDetail.Length) freq = _cpuDetail[(i + 1)..].Trim();
            KpiCpuSub.Inlines.Clear();
            if (freq.Length > 0) KpiCpuSub.Inlines.Add(new Run(freq));
            // 🆕 0.14.3: температура отдельным цветом состояния — 89°C больше
            // не тонет белым в подписи (фидбек: «критичная температура должна
            // визуально выделяться»).
            if (_cpuTempText.Length > 0)
            {
                if (freq.Length > 0) KpiCpuSub.Inlines.Add(new Run(" • "));
                KpiCpuSub.Inlines.Add(new Run(_cpuTempText)
                {
                    Foreground = TempSemi(_cpuTempVal),
                });
            }
            if (KpiCpuSub.Inlines.Count == 0) KpiCpuSub.Inlines.Add(new Run("—"));
        }
        catch { }
    }

    // Штамп свежести ставит только полное обновление; тик лишь пересчитывает подпись.
    private void StampFresh() => _freshAt = DateTime.Now;

    private void TickFresh()
    {
        try
        {
            if (FreshText == null) return;
            if (_freshAt == DateTime.MinValue) { FreshText.Text = "обновлено…"; return; }
            var age = DateTime.Now - _freshAt;
            FreshText.Text = age.TotalSeconds < 45 ? "только что"
                : age.TotalMinutes < 60 ? $"обновлено {age.Minutes} мин назад"
                : $"обновлено {age.Hours} ч назад";
        }
        catch { }
    }

    // ================= Герой KPI (плитки приборной панели, без колец) =================
    // Значения героев копятся из тех же проходов, что и карточки, — отдельных
    // опросов нет. KpiHeroPaint() мгновенно раскладывает их по плиткам: текст,
    // цвет зоны здоровья, заливка рулетки (пара метрики) и статичный accent-блик.
    // Ни одной анимации: обновление = присвоение.
    private double _hCpu, _hRam, _hDisk, _hNet;
    private string _hRamSub = "—", _hDiskSub = "—", _hNetSub = "—";

    // Здоровье диска: своя шкала (<80 норма, <95 внимание, дальше критично).
    private static Brush DiskSemi(double p) => p < 80 ? Semi("ok") : p < 95 ? Semi("warn") : Semi("err");

    private void UpdateHeroCpu(double cpu)
    {
        _hCpu = cpu;
        KpiHeroPaint();
    }

    private void UpdateHeroRam(double memPct, long used, long total)
    {
        _hRam = memPct;
        _hRamSub = total > 0 ? $"{GbS(used)} / {GbS(total)} ГБ" : "—";
        KpiHeroPaint();
    }

    // Диск: процент — максимум занятости (худший диск — тот, о котором и сообщаем),
    // подпись — первый готовый диск, на нём же лежит система у большинства.
    private void UpdateHeroDisk(List<DiskInfo> disks)
    {
        try
        {
            var ready = disks.Where(d => d.IsReady).ToList();
            if (ready.Count == 0)
            {
                _hDisk = 0;
                _hDiskSub = "нет носителя";
            }
            else
            {
                _hDisk = ready.Max(d => d.UsedPct);
                var first = ready[0];
                _hDiskSub = $"{first.Name}: {DiskInfo.Gb(first.FreeBytes)} свободно";
            }
        }
        catch { }
        KpiHeroPaint();
    }

    private void UpdateHeroNet((string Iface, double DownKbps, double UpKbps) net)
    {
        try
        {
            double mbps = Math.Max(0, net.DownKbps) / 1000.0;
            // % от скорости линка, если она известна; иначе — сырое значение 0..100.
            _hNet = _netLinkMbps > 0
                ? Math.Clamp(mbps * 100.0 / _netLinkMbps, 0, 100)
                : Math.Min(100, mbps);
            double up = Math.Max(0, net.UpKbps) / 1000.0;
            _hNetSub = $"↓ {mbps:F1} Мбит/с  ↑ {up:F1} Мбит/с";
        }
        catch { }
        KpiHeroPaint();
    }

    // Мгновенная перерисовка плиток героя из текущих метрик: значения и цвета
    // зон здоровья, заливки рулеток ( cpu/ram/disk/net ) и accent-блики.
    private void KpiHeroPaint()
    {
        try
        {
            if (KpiCpuPct != null) { KpiCpuPct.Text = $"{_hCpu:F0}%"; KpiCpuPct.Foreground = PctSemi(_hCpu); }
            if (KpiRamPct != null) { KpiRamPct.Text = $"{_hRam:F0}%"; KpiRamPct.Foreground = PctSemi(_hRam); }
            if (KpiDiskPct != null) { KpiDiskPct.Text = $"{_hDisk:F0}%"; KpiDiskPct.Foreground = DiskSemi(_hDisk); }
            if (KpiNetPct != null)
            {
                // Сеть — всегда пара net, без «горячих» зон.
                KpiNetPct.Text = $"{_hNet:F0}%";
                var np = ThemeService.MetricPair("net");
                var nb = new SolidColorBrush(np.From);
                nb.Freeze();
                KpiNetPct.Foreground = nb;
            }
            if (KpiRamSub != null) KpiRamSub.Text = _hRamSub;
            if (KpiDiskSub != null) KpiDiskSub.Text = _hDiskSub;
            if (KpiNetSub != null) KpiNetSub.Text = _hNetSub;
            // Рулетки-измерители: доля шириной по X из пары метрики, мгновенно.
            if (KpiCpuBar != null) PaintLeds(KpiCpuBar, _hCpu, "cpu");
            if (KpiRamBar != null) PaintLeds(KpiRamBar, _hRam, "ram", dimTail: true);
            if (KpiDiskBar != null) PaintLeds(KpiDiskBar, _hDisk, "disk");
            if (KpiNetBar != null) PaintLeds(KpiNetBar, _hNet, "net", hotZones: false);
            // 🆕 0.14.3: блики-статусы плиток — цвет = состояние зоны
            // (раньше блик всегда был акцентом «для галочки»); у сети
            // состояния нет — тихий цвет рамки темы.
            PaintBlip(KpiCpuBlip, PctSemi(_hCpu)); PaintBlip(KpiRamBlip, PctSemi(_hRam));
            PaintBlip(KpiDiskBlip, DiskSemi(_hDisk)); PaintBlip(KpiNetBlip, null);
            // 🆕 0.14.3: строка «первые две секунды» и полосы-статусы секций.
            PaintChips();
            PaintSectionStrips();
            BentoPaint();
        }
        catch { }
    }

    // 🆕 0.14.3: чипы «температура / место на диске / ошибки» — цвет точки
    // и подписи = состояние; без данных чип температуры/диска прячется.
    private void PaintChips()
    {
        try
        {
            if (ChipTemp != null)
            {
                bool on = _cpuTempText.Length > 0 && _cpuTempVal > 0;
                ChipTemp.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on)
                {
                    var b = TempSemi(_cpuTempVal);
                    ChipTempText.Text = "Процессор " + _cpuTempText;
                    ChipTempText.Foreground = b;
                    ChipTempDot.Fill = b;
                }
            }
            if (ChipDisk != null)
            {
                bool on = _hDiskSub.Length > 0 && _hDiskSub != "—"
                    && _hDiskSub != "нет носителя";
                ChipDisk.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on)
                {
                    var b = DiskSemi(_hDisk);
                    ChipDiskText.Text = _hDiskSub;
                    ChipDiskText.Foreground = b;
                    ChipDiskDot.Fill = b;
                }
            }
            if (ChipErr != null)
            {
                var b = _eventsCount > 0 ? ErrBrush : OkBrush;
                ChipErrText.Text = _eventsCount > 0 ? $"Ошибки: {_eventsCount}" : "Ошибок нет";
                ChipErrText.Foreground = b;
                ChipErrDot.Fill = b;
            }
        }
        catch { }
    }

    // 🆕 0.14.3: полосы-статусы секций Обзора: зелёная — ок, жёлтая —
    // внимание, красная — критично, нейтральная — данных ещё нет.
    private void PaintSectionStrips()
    {
        try
        {
            // Загрузка — худшая зона процессор/память/диск.
            SetStrip(StripLoad, Worst(PctSemi(_hCpu), PctSemi(_hRam), DiskSemi(_hDisk)));
            // Питание — строка батареи (настольный ПК — нейтраль).
            SetStrip(StripPower, PowerState());
            // Обновления — ждут пакеты/ребут; -1 = ещё не проверено.
            SetStrip(StripUpdates, _updatesReboot ? ErrBrush
                : _updatesPending < 0 ? null
                : _updatesPending > 0 ? WarnBrush : OkBrush);
            // Ошибки — свежие записи журналов.
            SetStrip(StripEvents, _eventsCount > 0 ? ErrBrush : OkBrush);
        }
        catch { }
    }

    private static void SetStrip(Border? strip, Brush? status)
    {
        if (strip == null) return;
        if (status != null) strip.Background = status;
        else strip.SetResourceReference(Border.BackgroundProperty, "B_Border");
    }

    private static Brush Worst(params Brush[] bs)
    {
        var best = bs[0];
        foreach (var b in bs) if (SemiRank(b) > SemiRank(best)) best = b;
        return best;
    }

    private static int SemiRank(Brush b) => b is SolidColorBrush sc
        ? sc.Color == (ErrBrush as SolidColorBrush)?.Color ? 2
            : sc.Color == (WarnBrush as SolidColorBrush)?.Color ? 1 : 0
        : 0;

    // Состояние питания из строки батареи: «15% (от батареи…) [низкий заряд]».
    private Brush? PowerState()
    {
        try
        {
            string t = BatteryText?.Text ?? "";
            if (t.Length == 0 || t == "—" || t.Contains("Нет батареи")) return null;
            if (t.Contains("низкий заря")) return ErrBrush;
            if (!int.TryParse(t.Split('%')[0].Trim(), out int pct)) return null;
            if (t.Contains("от батареи"))
                return pct <= 15 ? ErrBrush : pct <= 40 ? WarnBrush : OkBrush;
            return pct <= 15 ? WarnBrush : OkBrush;
        }
        catch { return null; }
    }

    // ============ 0.14.4 Sys B: приборная стена (бенто-плитки «Обзора») ============
    // Правила (скетч одобрен): аномалии первыми (err -> warn) идут hero-плиткой
    // (спан 2, число 56, рулетки, фраза-причина), здоровые — компактом (спан 1,
    // число 26); ноль аномалий — спокойная композиция 2/1/1 + 2/2 (число 44);
    // ряд = 4 единицы, последний дотягивается звёздами; узкое окно (<1100) —
    // плитка на ряд. «Питание» без батареи и «События» в Простом режиме
    // исчезают. Пороги — только существующие (PctSemi/DiskSemi/TempSemi).

    private void BentoLayout() => BentoPaint();

    // Состав стены: (ключ, статус-ранг, спан, hero). Считается на каждом тике;
    // по сигнатуре «ключ+режим» решаем, пересобирать ли разметку.
    private List<(string Key, int Rank, int Span, bool Hero)> BentoPlan()
    {
        var order = new[] { "load", "disk", "power", "updates", "events" };
        var items = new List<(string Key, int Rank)>();
        foreach (var k in order)
        {
            switch (k)
            {
                case "load":
                    items.Add((k, SemiRank(Worst(PctSemi(_hCpu), PctSemi(_hRam)))));
                    break;
                case "disk":
                    items.Add((k, Math.Max(SemiRank(DiskSemi(_hDisk)),
                        _cpuTempVal > 0 ? SemiRank(TempSemi(_cpuTempVal)) : 0)));
                    break;
                case "power":
                    // Нет данных о батарее (настольный ПК) — плитки нет.
                    if (PowerState() is Brush ps) items.Add((k, SemiRank(ps)));
                    break;
                case "updates":
                    items.Add((k, _updatesReboot ? 2 : _updatesPending > 0 ? 1 : 0));
                    break;
                case "events":
                    if (EventsCard == null || EventsCard.Visibility == Visibility.Visible)
                        items.Add((k, _eventsCount > 0 ? 2 : 0));
                    break;
            }
        }
        bool calm = items.All(t => t.Rank == 0);
        if (calm)
        {
            var spans = new Dictionary<string, int>
            {
                ["load"] = 2, ["disk"] = 1, ["power"] = 1, ["updates"] = 2, ["events"] = 2,
            };
            return items.Select(t => (t.Key, 0, spans[t.Key], true)).ToList();
        }
        return items
            .OrderByDescending(t => t.Rank)
            .ThenBy(t => Array.IndexOf(order, t.Key))
            .Select(t => (t.Key, t.Rank, t.Rank > 0 ? 2 : 1, t.Rank > 0))
            .ToList();
    }

    // Пересборка рядов (только при смене сигнатуры) + покраска значений.
    private void BentoPaint()
    {
        try
        {
            if (BentoRows == null || BentoPanel == null) return;
            if (BentoPanel.Visibility != Visibility.Visible) return;
            var plan = BentoPlan();
            string sig = string.Join("|", plan.Select(t => $"{t.Key}{(t.Hero ? 'H' : 'C')}"));
            if (sig != _bentoSig || _bento.Count != plan.Count) BuildBento(plan, sig);
            PaintBentoValues();
            if (BentoNetText != null) BentoNetText.Text = _hNetSub.Length > 0 ? _hNetSub : "—";
            if (BentoNetBar != null) PaintLeds(BentoNetBar, _hNet, "net", hotZones: false);
        }
        catch { }
    }

    // Упаковка: ряд = BentoRowUnits единицы (узкое окно — единица), последний
    // ряд растягивается сам: колонки — звёзды по спанам.
    private void BuildBento(List<(string Key, int Rank, int Span, bool Hero)> plan, string sig)
    {
        _bentoSig = sig;
        _bento.Clear();
        BentoRows.Children.Clear();
        double w = ActualWidth;
        if (double.IsNaN(w) || w < 200) w = 1600;      // до первого layout — широким
        int limit = w >= 1100 ? 4 : 1;                  // узкое окно — плитка на ряд
        var row = new List<(string Key, int Span, bool Hero)>();
        int acc = 0;

        void Flush()
        {
            if (row.Count == 0) return;
            var g = new Grid();
            for (int i = 0; i < row.Count; i++)
                g.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(row[i].Span, GridUnitType.Star),
                });
            for (int i = 0; i < row.Count; i++)
            {
                int rank = plan.First(p => p.Key == row[i].Key).Rank;
                var tile = BuildBentoTile(row[i].Key, rank, row[i].Hero);
                _bento[row[i].Key] = tile;
                Grid.SetColumn(tile.Root, i);
                g.Children.Add(tile.Root);
            }
            BentoRows.Children.Add(g);
            row.Clear();
            acc = 0;
        }

        foreach (var t in plan)
        {
            if (acc > 0 && acc + t.Span > limit) Flush();
            row.Add((t.Key, t.Span, t.Hero));
            acc += t.Span;
        }
        Flush();

        // Раскрытый ящик пережил пересборку — подсветку плитки возвращаем.
        if (_drawerKey != null && _bento.TryGetValue(_drawerKey, out var op))
            op.Root.SetValue(Border.BorderBrushProperty, (Brush)FindResource("B_Accent"));
    }

    // Одна плитка: полоса-статус 2px во всю ширину, лейбл + шеврон, число
    // (+ подпись), рулетки метрик, фраза-причина (hero). Клик — ящик деталей.
    private BentoTile BuildBentoTile(string key, int rank, bool hero)
    {
        var tile = new BentoTile { Key = key };
        string label = key switch
        {
            "load" => "НАГРУЗКА",
            "disk" => "ДИСК И ТЕМПЕРАТУРА",
            "power" => "ПИТАНИЕ",
            "updates" => "ОБНОВЛЕНИЯ",
            _ => "СОБЫТИЯ",
        };
        var root = new Border
        {
            Style = (Style)FindResource("SysCard"),
            Padding = hero ? new Thickness(18, 14, 18, 14) : new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 8, 8),
            Tag = key,
            Cursor = Cursors.Hand,
        };
        root.MouseLeftButtonUp += Tile_Click;
        tile.Root = root;
        var pad = root.Padding;

        var grid = new Grid();
        for (int i = 0; i < 5; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Child = grid;

        // Полоса-статус во всю ширину плитки (full-bleed, как accent-блик героя).
        var strip = new Border
        {
            Height = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(-pad.Left, -pad.Top, -pad.Right, 0),
        };
        strip.SetResourceReference(Border.BackgroundProperty, "B_Border");
        Grid.SetRow(strip, 0);
        grid.Children.Add(strip);
        tile.Strip = strip;

        // Лейбл + шеврон «к деталям».
        var head = new Grid { Margin = new Thickness(0, hero ? 6 : 5, 0, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        lbl.SetResourceReference(TextBlock.FontFamilyProperty, "F_Ui");
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
        Grid.SetColumn(lbl, 0);
        head.Children.Add(lbl);
        var chev = new TextBlock
        {
            Text = "→",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chev.SetResourceReference(TextBlock.FontFamilyProperty, "F_Ui");
        chev.SetResourceReference(TextBlock.ForegroundProperty, "B_Accent");
        Grid.SetColumn(chev, 1);
        head.Children.Add(chev);
        Grid.SetRow(head, 1);
        grid.Children.Add(head);

        // Число + подпись (hero — перенос слева, компакт — справа с эллипсисом).
        var valRow = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        valRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        valRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var val = new TextBlock
        {
            Text = "–",
            FontSize = !hero ? 26 : rank > 0 ? 56 : 44,
            VerticalAlignment = VerticalAlignment.Center,
        };
        val.SetResourceReference(TextBlock.FontFamilyProperty, "F_Ui");
        val.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
        Grid.SetColumn(val, 0);
        valRow.Children.Add(val);
        tile.Value = val;
        var sub = new TextBlock
        {
            Text = "",
            FontSize = hero ? 14 : 12,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = hero ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = hero ? TextAlignment.Left : TextAlignment.Right,
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
        Grid.SetColumn(sub, 1);
        valRow.Children.Add(sub);
        tile.Sub = sub;
        Grid.SetRow(valRow, 2);
        grid.Children.Add(valRow);

        // Рулетки метрик (нагрузка: cpu+память; диск: диск) — цвет из MetricPair.
        var rulers = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 6, 0, 0) };
        if (key == "load")
        {
            var (r1, p1) = BentoRuler(_hCpu, "cpu");
            r1.Margin = new Thickness(0, 0, 0, 4);
            rulers.Children.Add(r1);
            tile.Ruler = p1;
            var (r2, p2) = BentoRuler(_hRam, "ram");
            rulers.Children.Add(r2);
            tile.Ruler2 = p2;
        }
        else if (key == "disk")
        {
            var (r1, p1) = BentoRuler(_hDisk, "disk");
            rulers.Children.Add(r1);
            tile.Ruler = p1;
        }
        if (rulers.Children.Count > 0)
        {
            Grid.SetRow(rulers, 3);
            grid.Children.Add(rulers);
        }

        // Фраза-причина — только у hero (в компакте её заменяет подпись).
        if (hero)
        {
            var phrase = new TextBlock
            {
                Text = "",
                FontSize = 15,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            phrase.SetResourceReference(TextBlock.FontFamilyProperty, "F_Ui");
            phrase.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
            Grid.SetRow(phrase, 4);
            grid.Children.Add(phrase);
            tile.Phrase = phrase;
        }
        return tile;
    }

    // Рулетка плитки = BuildRuler (тот же билдер, что у карточек), но с
    // возвратом paint-грида: обновлять заливку на тике без пересоздания.
    private (Grid Row, Grid Paint) BentoRuler(double pct, string key, bool hotZones = true)
    {
        var row = BuildRuler(pct, key, hotZones: hotZones, showPct: false);
        Grid paint = null!;
        // BuildRuler: row -> track(Border, col0) -> host(Grid) -> paint(0), ticks(1)
        if (row.Children.Count > 0 && row.Children[0] is Border track
            && track.Child is Grid host && host.Children.Count > 0
            && host.Children[0] is Grid p)
            paint = p;
        return (row, paint);
    }

    // Значения/цвета/фразы/полосы — на каждом тике, без пересборки разметки.
    private void PaintBentoValues()
    {
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");

        if (_bento.TryGetValue("load", out var tt))
        {
            tt.Value.Text = $"{_hCpu:F0}%";
            tt.Value.Foreground = PctSemi(_hCpu);
            if (tt.Sub != null) tt.Sub.Text = $"память {_hRam:F0}%";
            if (tt.Ruler != null) PaintLeds(tt.Ruler, _hCpu, "cpu");
            if (tt.Ruler2 != null) PaintLeds(tt.Ruler2, _hRam, "ram", dimTail: true);
            var st = Worst(PctSemi(_hCpu), PctSemi(_hRam));
            SetStrip(tt.Strip, st);
            if (tt.Phrase != null)
            {
                int rank = SemiRank(st);
                bool cpuWorse = SemiRank(PctSemi(_hCpu)) >= SemiRank(PctSemi(_hRam));
                string ph;
                if (rank > 0)
                    ph = (cpuWorse ? $"процессор {_hCpu:F0}%" : $"память {_hRam:F0}%")
                        + (rank == 2 ? " — критично" : " — внимание");
                else
                    ph = _topProcLine.Length > 0 ? _topProcLine : "процессор и память в норме";
                tt.Phrase.Text = ph;
                tt.Phrase.Foreground = rank > 0 ? st : muted;
            }
        }

        if (_bento.TryGetValue("disk", out var td))
        {
            td.Value.Text = $"{_hDisk:F0}%";
            td.Value.Foreground = DiskSemi(_hDisk);
            if (td.Sub != null) td.Sub.Text = _hDiskSub.Length > 0 ? _hDiskSub : "—";
            if (td.Ruler != null) PaintLeds(td.Ruler, _hDisk, "disk");
            var st = _cpuTempVal > 0
                ? Worst(DiskSemi(_hDisk), TempSemi(_cpuTempVal))
                : DiskSemi(_hDisk);
            SetStrip(td.Strip, st);
            if (td.Phrase != null)
            {
                int rank = SemiRank(st);
                bool tempHot = _cpuTempVal > 0
                    && SemiRank(TempSemi(_cpuTempVal)) > 0
                    && SemiRank(TempSemi(_cpuTempVal)) >= SemiRank(DiskSemi(_hDisk));
                string ph;
                if (rank == 0)
                    ph = _cpuTempText.Length > 0 ? $"температура {_cpuTempText} — в норме"
                        : "место на диске в норме";
                else if (tempHot)
                    ph = $"процессор {_cpuTempText} — {(rank == 2 ? "перегрев" : "тепло")}";
                else
                    ph = _hDiskSub.Length > 0 ? $"мало места: {_hDiskSub}" : "мало места на диске";
                td.Phrase.Text = ph;
                td.Phrase.Foreground = rank > 0 ? st : muted;
            }
        }

        if (_bento.TryGetValue("power", out var tp))
        {
            string bt = BatteryText?.Text ?? "";
            string head = bt.Contains('%') ? bt.Split('%')[0].Trim() : "";
            tp.Value.Text = head.Length > 0 ? head + "%" : "–";
            string pmode = bt.Contains("заряжается") ? "заряжается от сети"
                : bt.Contains("от батареи") ? "от батареи" : "—";
            if (tp.Sub != null) tp.Sub.Text = pmode;
            var st = PowerState();
            SetStrip(tp.Strip, st);
            tp.Value.Foreground = st ?? text;
            if (tp.Phrase != null)
            {
                int rank = st != null ? SemiRank(st) : 0;
                string ph;
                if (bt.Contains("низкий заря")) ph = "низкий заряд — пора заряжать";
                else
                {
                    int a = bt.IndexOf('('), b = bt.IndexOf(')');
                    ph = a >= 0 && b > a ? bt.Substring(a + 1, b - a - 1) : "батарея в норме";
                }
                tp.Phrase.Text = ph;
                tp.Phrase.Foreground = rank > 0 ? st! : muted;
            }
        }

        if (_bento.TryGetValue("updates", out var tu))
        {
            tu.Value.Text = _updatesPending < 0 ? "–" : $"{_updatesPending}";
            var st = _updatesReboot ? ErrBrush
                : _updatesPending < 0 ? null
                : _updatesPending > 0 ? WarnBrush : OkBrush;
            SetStrip(tu.Strip, st);
            tu.Value.Foreground = st ?? text;
            if (tu.Sub != null)
                tu.Sub.Text = _updatesPending < 0 ? "не проверено"
                    : _updatesPending > 0 ? "ждут установки" : "";
            if (tu.Phrase != null)
            {
                int rank = st != null ? SemiRank(st) : 0;
                string ph = _updatesReboot ? "Windows просит перезагрузку"
                    : _updatesPending < 0 ? "проверка ещё не выполнялась"
                    : _updatesPending > 0 ? $"ждут установки: {_updatesPending}"
                    : "система свежая";
                tu.Phrase.Text = ph;
                tu.Phrase.Foreground = rank > 0 ? st! : muted;
            }
        }

        if (_bento.TryGetValue("events", out var te))
        {
            te.Value.Text = _eventsCount > 0 ? $"{_eventsCount}" : "0";
            var st = _eventsCount > 0 ? ErrBrush : OkBrush;
            SetStrip(te.Strip, st);
            te.Value.Foreground = st;
            if (te.Sub != null) te.Sub.Text = _eventsCount > 0 ? "свежих ошибок" : "";
            if (te.Phrase != null)
            {
                int rank = SemiRank(st);
                string ph = rank > 0
                    ? (EventsPreview != null && EventsPreview.Text.Length > 0
                        ? EventsPreview.Text : $"свежих ошибок: {_eventsCount}")
                    : "журнал чист";
                te.Phrase.Text = ph;
                te.Phrase.Foreground = rank > 0 ? st : muted;
            }
        }
    }

    // Хром вкладки: на «Обзоре» — стена плиток вместо героя/чипов/колонок
    // (герой и чипы остаются на остальных вкладках — контракт скетча Sys B).
    private void ApplyTabChrome()
    {
        try
        {
            bool over = _tab == "overview";
            if (HeroRow != null) HeroRow.Visibility = over ? Visibility.Collapsed : Visibility.Visible;
            if (ChipsRow != null) ChipsRow.Visibility = over ? Visibility.Collapsed : Visibility.Visible;
            if (BentoPanel != null) BentoPanel.Visibility = over ? Visibility.Visible : Visibility.Collapsed;
            if (LayoutGrid != null) LayoutGrid.Visibility = over ? Visibility.Collapsed : Visibility.Visible;
            if (over) BentoPaint();
            else CloseBentoDrawer();
        }
        catch { }
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string key) return;
            if (key == _drawerKey) CloseBentoDrawer();
            else OpenBentoDrawer(key);
        }
        catch { }
    }

    // Ящик деталей: тело существующей карточки (BuildX не трогаем — они пишут
    // в свои панели, где бы карточка ни лежала). Повторный клик закрывает.
    private void OpenBentoDrawer(string key)
    {
        try
        {
            UIElement? card = key switch
            {
                "load" or "disk" => (UIElement)LoadCard,
                "power" => (UIElement)PowerCard,
                "updates" => (UIElement)UpdatesCard,
                "events" => (UIElement)EventsCard,
                _ => null,
            };
            if (card == null || BentoDrawerHost == null) return;
            CloseBentoDrawer();
            _drawerKey = key;
            if (BentoDrawerTitle != null)
                BentoDrawerTitle.Text = key switch
                {
                    "load" or "disk" => "Загрузка — подробности",
                    "power" => "Питание — подробности",
                    "updates" => "Обновления — подробности",
                    _ => "События — подробности",
                };
            // WPF не даёт одному элементу двух родителей: сначала отцепляем
            // (колонка/предыдущий ящик), потом кладём в ящик.
            if (card is FrameworkElement fe && fe.Parent is Panel pp) pp.Children.Remove(fe);
            card.Visibility = Visibility.Visible;
            BentoDrawerHost.Children.Add(card);
            if (BentoDrawer != null) BentoDrawer.Visibility = Visibility.Visible;
            if (_bento.TryGetValue(key, out var t))
                t.Root.SetValue(Border.BorderBrushProperty, (Brush)FindResource("B_Accent"));
            BentoDrawer?.BringIntoView();
        }
        catch { }
    }

    private void CloseBentoDrawer()
    {
        try
        {
            if (BentoDrawerHost == null) return;
            BentoDrawerHost.Children.Clear();   // карточка остаётся без родителя
            if (BentoDrawer != null) BentoDrawer.Visibility = Visibility.Collapsed;
            if (_drawerKey != null && _bento.TryGetValue(_drawerKey, out var t))
                t.Root.ClearValue(Border.BorderBrushProperty); // стиль + ховер обратно
            _drawerKey = null;
        }
        catch { }
    }


    // Блик: градиент «цвет состояния → прозрачный», статичный — не двигается
    // никогда. 🆕 0.14.3: вместо всегда-акцентного блика — цвет состояния
    // зоны (null = у зоны нет состояния → тихий цвет рамки темы).
    private void PaintBlip(Border blip, Brush? status)
    {
        if (blip == null) return;
        try
        {
            Color c;
            if (status is SolidColorBrush sc) c = sc.Color;
            else if (TryFindResource("B_Border") is SolidColorBrush bb) c = bb.Color;
            else c = ThemeService.CurrentAccent();
            var g = new LinearGradientBrush(c, Color.FromArgb(0, c.R, c.G, c.B),
                new Point(0, 0.5), new Point(1, 0.5));
            g.Freeze();
            blip.Background = g;
        }
        catch { }
    }

    // Один ГБ-строку в духе DiskInfo.Gb, но без слова — для склейки «X / Y ГБ».
    private static string GbS(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("F1");

    // Кольцевой буфер спарклайна: кладём % от линка, чтобы шкала была честной.
    private void PushNetSample((string Iface, double DownKbps, double UpKbps) net)
    {
        try
        {
            double mbps = Math.Max(0, net.DownKbps) / 1000.0;
            double pct = _netLinkMbps > 0
                ? Math.Clamp(mbps * 100.0 / _netLinkMbps, 0, 100)
                : Math.Min(100, mbps);
            _netHist[_netHistNext] = pct;
            _netHistNext = (_netHistNext + 1) % _netHist.Length;
        }
        catch { }
    }

    // Спарклайн: 20 столбиков шириной по звёздам; свежие ярче, старые глушатся.
    // Перестраивается целиком — 20 Border'ов это дёшево, зато нет состояния на стороне XAML.
    private void RenderNetSpark()
    {
        try
        {
            if (NetSparkGrid == null) return;
            NetSparkGrid.ColumnDefinitions.Clear();
            NetSparkGrid.Children.Clear();
            for (int i = 0; i < _netHist.Length; i++)
                NetSparkGrid.ColumnDefinitions.Add(new ColumnDefinition
                    { Width = new GridLength(1, GridUnitType.Star) });
            var pair = ThemeService.MetricPair("net");
            int n = _netHist.Length;
            for (int i = 0; i < n; i++)
            {
                // Индекс в буфере: самый старый отрезок — первым, свежий — последним.
                int idx = (_netHistNext + i) % n;
                double v = Math.Clamp(_netHist[idx], 0, 100);
                double age = i / (double)(n - 1); // 0 — старый, 1 — свежий
                var cell = new Grid { Margin = new Thickness(1.5, 0, 1.5, 0) };
                cell.Children.Add(new Border
                {
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Height = Math.Max(2, 34 * 0.30), // невысокий «пол» — пустая история не выглядит пустой
                    Opacity = 0.30 + 0.35 * age,
                    Background = new SolidColorBrush(pair.From),
                });
                if (v > 0.5)
                {
                    var g = new LinearGradientBrush(pair.From, pair.To,
                        new Point(0, 1), new Point(0, 0));
                    g.Freeze();
                    cell.Children.Add(new Border
                    {
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Height = Math.Max(3, 34 * v / 100.0),
                        Opacity = 0.35 + 0.60 * age,
                        Background = g,
                    });
                }
                Grid.SetColumn(cell, i);
                NetSparkGrid.Children.Add(cell);
            }
        }
        catch { }
    }

    // Семантика по числу: ok/warn/error — единственные фиксированные цвета.
    private static Brush Semi(string kind) => kind switch
    {
        "ok" => OkBrush,
        "err" => ErrBrush,
        _ => WarnBrush,
    };

    // Температура: до 50 — норма, до 65 — тепло, дальше — горячо.
    private static Brush TempSemi(double t) => t <= 0 ? Semi("warn") : t < 50 ? Semi("ok") : t < 65 ? Semi("warn") : Semi("err");

    // Загрузка: до 70 — норма, до 90 — внимание, дальше — критично.
    private static Brush PctSemi(double p) => p < 70 ? Semi("ok") : p < 90 ? Semi("warn") : Semi("err");

    // ================= Перекраска по новой палитре =================

    // Обходит дерево и перекрашивает всё, что помнит свой ключ:
    // рулетки-бары (Grid с Tag=BarState).
    private void RepaintTree(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        RepaintTree(root, seen);
    }

    // Визуальный обход + логический: свёрнутая вкладка визуального дерева ещё
    // не отдаёт, а перекрасить её после смены темы всё равно надо. Узлы, встретившиеся
    // в обоих деревьях, обрабатываются один раз (seen), поэтому проход линейный.
    private void RepaintTree(DependencyObject? root, HashSet<DependencyObject> seen)
    {
        if (root == null || !seen.Add(root)) return;
        if (root is Grid g && g.Tag is BarState st)
            PaintLeds(g, st.LastPct, st.Key, st.DimTail, st.HotZones);
        int n = 0;
        try { n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); }
        catch { }
        for (int i = 0; i < n; i++)
        {
            try { RepaintTree(System.Windows.Media.VisualTreeHelper.GetChild(root, i), seen); }
            catch { }
        }
        if (root is FrameworkElement fe)
        {
            try
            {
                foreach (object child in LogicalTreeHelper.GetChildren(fe))
                    if (child is DependencyObject d) RepaintTree(d, seen);
            }
            catch { }
        }
    }

    // Главная перекраска: вызывается координатором после смены темы/акцента.
    // Событие ThemeService.Changed не подписываем — view кэшируется в MainWindow,
    // подписка накапливалась бы при каждом показе.
    public void ApplyPalette()
    {
        try { KpiHeroPaint(); } catch { }
        try { RepaintTree(LayoutGrid); } catch { }
        try { RepaintTree(BentoPanel); } catch { }
        try { RenderNetSpark(); } catch { }
    }

    // Эффекты по режиму: Maximum — только статичное свечение рулеток (DropShadow
    // создаёт PaintLeds), Normal — тихая статика, Minimum — плоские заливки без
    // градиентов и теней. Герой и дерево перекрашиваются мгновенно, анимаций нет.
    private void ApplyFx()
    {
        try { KpiHeroPaint(); } catch { }
        try { RepaintTree(LayoutGrid); } catch { }
        try { RepaintTree(BentoPanel); } catch { }
    }

    // Топ-3 процесса с мини-барами в карточке «Загрузка»:
    // в cpu-режиме — абсолютная загрузка, в mem-режиме — доля от самого тяжёлого.
    private void BuildTopMini(List<ProcRow> procs, string mode)
    {
        try
        {
            if (TopProcsPanel == null) return;
            TopProcsPanel.Children.Clear();
            _topProcLine = "";
            if (procs == null || procs.Count == 0) return;
            // Причина-фраза плитки «Нагрузка» — самый тяжёлый процесс тика.
            var top0 = procs[0];
            _topProcLine = mode == "cpu"
                ? (top0.Cpu >= 1 ? $"{top0.Name} держит {top0.Cpu:F0}%" : "")
                : (top0.Mb >= 256 ? $"{top0.Name} ест {top0.Mb:F0} МБ" : "");
            double topMb = Math.Max(1, procs.Max(p => p.Mb));
            var text = (Brush)FindResource("B_Text");
            var muted = (Brush)FindResource("B_Muted");
            foreach (var p in procs.Take(3))
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBlock
                {
                    Text = p.Name, FontSize = 11, Foreground = muted,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(name, 0);
                row.Children.Add(name);
                var val = new TextBlock
                {
                    Text = mode == "cpu" ? $"{p.Cpu:F1}%" : $"{p.Mb:F0} МБ",
                    FontSize = 11, Foreground = text,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(val, 1);
                row.Children.Add(val);
                TopProcsPanel.Children.Add(row);

                double pct = mode == "cpu"
                    ? Math.Clamp(p.Cpu, 0, 100)
                    : Math.Clamp(p.Mb * 100.0 / topMb, 0, 100);
                var ruler = BuildRuler(pct, "cpu", showPct: false);
                ruler.Margin = new Thickness(0, 3, 0, 0);
                TopProcsPanel.Children.Add(ruler);
            }
        }
        catch { }
    }

    // «Подпись: значение» — две колонки с волосяным разделителем (KvRow),
    // длинные значения переносятся. Секции железа/драйверов/дисков/системы.
    // 🆕 0.14.3: tip — техдеталь (метка тома, ФС) уходит в тултип, в строке
    // остаются только читаемые числа (фидбек про «Диск F: (Windows 11) [NTFS]»).
    private UIElement InfoLine(string label, string value, string tip = "")
    {
        var g = KvRow(label, value);
        if (tip.Length > 0) g.ToolTip = tip;
        return g;
    }

    private void BuildDisks(List<DiskInfo> disks)
    {
        DisksPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var ready = disks.Where(d => d.IsReady).ToList();
        if (ready.Count > 0)
        {
            long free = ready.Sum(d => d.FreeBytes);
            long total = ready.Sum(d => d.TotalBytes);
            var summary = new TextBlock
            {
                Text = $"Всего свободно {DiskInfo.Gb(free)} из {DiskInfo.Gb(total)}",
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8),
            };
            summary.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
            UiFont(summary);
            DisksPanel.Children.Add(summary);
        }
        foreach (var d in disks)
        {
            if (!d.IsReady)
            {
                DisksPanel.Children.Add(new TextBlock
                {
                    Text = $"Диск {d.Name}: нет носителя",
                    FontSize = 13,
                    Foreground = muted,
                    Margin = new Thickness(0, 2, 0, 8),
                });
                continue;
            }
            string title = $"Диск {d.Name}";
            // 🆕 0.14.3: метка тома и ФС — в тултип: в строке только цифры.
            string tip = d.Label.Length > 0 ? d.Label : "";
            if (d.Format.Length > 0)
                tip = tip.Length > 0 ? $"{tip} · {d.Format}" : d.Format;
            DisksPanel.Children.Add(InfoLine(title,
                $"{d.UsedPct:F0}% занято, свободно {DiskInfo.Gb(d.FreeBytes)} из {DiskInfo.Gb(d.TotalBytes)}",
                tip));
            // Рулетка-измеритель занятости диска (процент уже в строке выше).
            var ruler = BuildRuler(d.UsedPct, "disk", showPct: false);
            ruler.Margin = new Thickness(0, 4, 0, 8);
            DisksPanel.Children.Add(ruler);
        }
    }

    private void BuildHardware(Dictionary<string, string> info, List<DiskInfo> disks,
        (string InstallDate, string SecureBoot, string BootTime) wininfo)
    {
        string Get(string k) => info.TryGetValue(k, out var v) ? v : "";

        // Модель и детали процессора копим — из них собираются подписи героя и карточки.
        _cpuModel = Get("cpu");
        _cpuDetail = Get("cpu_detail");
        RefreshCpuTexts();

        MainHwPanel.Children.Clear();
        MainHwPanel.Children.Add(InfoLine("ОС", Get("os")));
        MainHwPanel.Children.Add(InfoLine("Процессор", Get("cpu")));
        MainHwPanel.Children.Add(InfoLine("Видеокарта", Get("gpu")));
        MainHwPanel.Children.Add(InfoLine("RAM всего", Get("ram_total")));

        BatteryText.Text = Get("battery") == "" ? "—" : Get("battery");

        CpuDetailText.Text = Get("cpu_detail") == "" ? "—" : Get("cpu_detail");
        GpuDetailText.Text = Get("gpu") == "" ? "—" : Get("gpu");
        NetworkText.Text = Get("network") == "" ? "—" : Get("network");

        DrivesDetailPanel.Children.Clear();
        if (disks.Count == 0)
        {
            DrivesSection.Visibility = Visibility.Collapsed;
        }
        else
        {
            DrivesSection.Visibility = Visibility.Visible;
            foreach (var d in disks)
                DrivesDetailPanel.Children.Add(InfoLine($"Диск {d.Name}",
                    $"свободно {DiskInfo.Gb(d.FreeBytes)} из {DiskInfo.Gb(d.TotalBytes)}"));
        }

        SysDetailPanel.Children.Clear();
        SysDetailPanel.Children.Add(InfoLine("Плата", Get("board")));
        SysDetailPanel.Children.Add(InfoLine("BIOS", Get("bios")));
        SysDetailPanel.Children.Add(InfoLine("Аптайм", Get("uptime")));
        SysDetailPanel.Children.Add(InfoLine("Пользователь", Get("user")));
        if (wininfo.InstallDate.Length > 0)
            SysDetailPanel.Children.Add(InfoLine("Windows установлена", wininfo.InstallDate));
        if (wininfo.SecureBoot.Length > 0)
            SysDetailPanel.Children.Add(InfoLine("SecureBoot", wininfo.SecureBoot));
        if (wininfo.BootTime.Length > 0)
            SysDetailPanel.Children.Add(InfoLine("Перезагрузка", wininfo.BootTime + " назад"));
        try
        {
            SysDetailPanel.Children.Add(InfoLine("Экраны",
                $"{SystemParameters.PrimaryScreenWidth:F0}×{SystemParameters.PrimaryScreenHeight:F0}"));
        }
        catch { }
    }

    private void BuildDrivers((string GpuVersion, string GpuDate) gpu,
        List<string> problems)
    {
        DriversPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        problems ??= new List<string>();
        string ver = gpu.GpuVersion ?? "";
        string date = gpu.GpuDate ?? "";
        string gpuLine = ver.Length > 0 ? ver : "—";
        if (date.Length > 0) gpuLine += $" ({date})";
        DriversPanel.Children.Add(InfoLine("Видео-драйвер", gpuLine));
        DriversTitle.Text = problems.Count == 0 ? "Драйверы ✓" : $"Драйверы ({problems.Count}!)";
        if (problems.Count == 0)
        {
            DriversPanel.Children.Add(new TextBlock
            {
                Text = "Устройств с проблемами нет.",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            return;
        }
        DriversPanel.Children.Add(new TextBlock
        {
            Text = "Требуют внимания:",
            FontSize = 12, Foreground = text, Margin = new Thickness(0, 6, 0, 2),
        });
        foreach (string p in problems)
            DriversPanel.Children.Add(new TextBlock
            {
                Text = "• " + p, FontSize = 12, Foreground = muted,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2),
            });
    }

    private void BuildPrograms((int Total, List<SystemMonitor.ProgramInfo> Top) programs)
    {
        ProgramsPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        ProgramsTitle.Text = programs.Total == 0 ? "Программы" : $"Программы ({programs.Total})";
        if (programs.Top.Count == 0)
        {
            ProgramsPanel.Children.Add(new TextBlock
            {
                Text = "Не удалось прочитать список.",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var p in programs.Top)
        {
            string size = p.SizeKb > 0
                ? (p.SizeKb >= 1048576
                    ? $"{p.SizeKb / 1048576.0:F1} ГБ"
                    : $"{p.SizeKb / 1024.0:F0} МБ")
                : "—";
            var sizeTb = new TextBlock
            {
                Text = size,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            sizeTb.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
            UiFont(sizeTb);
            var right = new List<UIElement> { sizeTb };
            if (AppSettings.ExpOn() && p.Uninstall.Length > 0)
            {
                var del = new Button
                {
                    Content = "Удалить", FontSize = 11, Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(8, 1, 0, 1), Cursor = Cursors.Hand,
                    Tag = p, ToolTip = $"Удалить {p.Name} штатным деинсталлятором.",
                };
                del.Click += ProgUninstall_Click;
                right.Add(del);
            }
            // Строка-плитка: глиф, название F_Ui, версия мелко, размер справа.
            var row = TileRow(GlyphTile("\uEA86", "accent"), p.Name,
                p.Version.Length > 0 ? p.Version : null, right);
            row.ToolTip = p.Name;
            ProgramsPanel.Children.Add(row);
        }
    }

    private void BuildServices((int Running, int Total, List<(string Name, string Display)> AutoStopped) svc)
    {
        ServicesPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        bool exp = AppSettings.ExpOn();
        ServicesTitle.Text = svc.Total == 0 ? "Службы"
            : svc.AutoStopped.Count > 0
                ? $"Службы ({svc.Running}/{svc.Total} • {svc.AutoStopped.Count} стоят!)"
                : $"Службы ({svc.Running}/{svc.Total})";
        ServicesPanel.Children.Add(new TextBlock
        {
            Text = svc.Total == 0 ? "Не удалось прочитать." : $"Запущено: {svc.Running} из {svc.Total}.",
            FontSize = 12, Foreground = muted, Margin = new Thickness(0, 0, 0, 4),
        });
        if (svc.AutoStopped.Count > 0)
        {
            ServicesPanel.Children.Add(new TextBlock
            {
                Text = "На авто, но стоят:",
                FontSize = 12, Foreground = text, Margin = new Thickness(0, 4, 0, 2),
            });
            foreach (var (name, display) in svc.AutoStopped)
            {
                var right = new List<UIElement>
                {
                    // Авто-служба стоит — это внимание: жёлтый чип.
                    StatusChip("СТОИТ", WarnBrush),
                };
                if (exp)
                {
                    var go = new Button
                    {
                        Content = "Старт", FontSize = 11, Padding = new Thickness(10, 2, 10, 2),
                        Margin = new Thickness(8, 1, 0, 1), Cursor = Cursors.Hand,
                        Tag = name, ToolTip = $"Запустить службу {name}.",
                    };
                    go.Click += SvcStart_Click;
                    right.Add(go);
                }
                // Строка-плитка: глиф-настройка, название F_Ui, чип статуса.
                ServicesPanel.Children.Add(TileRow(GlyphTile("\uE713", "accent"),
                    display, "на авто, но не работает", right));
            }
        }
    }

    private void SvcStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name) return;
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, $"Запустить службу {name}?")) return;
        SetStatusLocal($"Запускаю {name}…");
        Task.Run(() => SystemMonitor.ServiceControl(name, "start")).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                SetStatusLocal(t.IsFaulted ? "Ошибка запуска." : t.Result);
                Refresh();
            });
        });
    }

    private void SetStatusLocal(string s)
    {
        try { Report(s); } catch { }
    }

    private void BuildAdapters(List<SystemMonitor.AdapterInfo> adapters,
        (string Iface, double DownKbps, double UpKbps) net)
    {
        AdaptersPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        AdaptersTitle.Text = adapters.Count == 0 ? "Сеть" : $"Сеть ({adapters.Count})";
        AdaptersPanel.Children.Add(new TextBlock
        {
            Text = net.Iface.Length == 0
                ? "Сейчас трафика нет."
                : $"Сейчас: ↓ {FmtSpeed(net.DownKbps)}  ↑ {FmtSpeed(net.UpKbps)} ({net.Iface})",
            FontSize = 12, Foreground = text, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        if (adapters.Count == 0)
        {
            AdaptersPanel.Children.Add(new TextBlock
            {
                Text = "Активных адаптеров нет.",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        long maxSpeed = Math.Max(1, adapters.Max(a => a.SpeedMbps));
        // Ссылка на максимум линка нужна герою «Сеть» для процентов того же ряда.
        _netLinkMbps = maxSpeed;
        foreach (var a in adapters)
        {
            // Скорость линка — это гигаБИТЫ, не гигабайты.
            string speed = a.SpeedMbps >= 1000 ? $"{a.SpeedMbps / 1000.0:F1} Гбит/с"
                : a.SpeedMbps > 0 ? $"{a.SpeedMbps} Мбит/с" : "—";
            string sub = a.Kind;
            if (a.Ip.Length > 0) sub += $" • {a.Ip}";
            if (a.Mac.Length > 0) sub += $" • {a.Mac}";
            double share = a.SpeedMbps <= 0 ? 0 : Math.Clamp(a.SpeedMbps * 100.0 / maxSpeed, 3, 100);
            // Строка-плитка: глиф-квадрат, чип скорости, рулетка доли линка.
            // Красная зона тут врала бы — линк не «перегружен», он просто быстрее.
            var row = TileRow(GlyphTile("\uE945", "net"), a.Name, sub,
                new UIElement[] { StatusChip(speed, muted) },
                BuildRuler(share, "net", hotZones: false));
            AdaptersPanel.Children.Add(row);
        }
    }

    private void BuildFolders(List<(string Name, string Path, long Size, bool Complete)> folders)
    {
        FoldersPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        long max = 1;
        foreach (var f in folders) max = Math.Max(max, f.Size);
        foreach (var (name, path, size, complete) in folders)
        {
            var open = new Button
            {
                Content = "Открыть", FontSize = 11, Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand,
                Tag = path, ToolTip = "Открыть папку в проводнике.",
            };
            open.Click += FolderOpen_Click;
            var sizeTb = new TextBlock
            {
                Text = size > 0 ? DiskInfo.Gb(size) : "—",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            sizeTb.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
            UiFont(sizeTb);
            // Строка-плитка: название F_Ui, размер ГБ справа, рулетка
            // занятости (доля от самой большой папки) и кнопка «Открыть».
            var row = TileRow(GlyphTile("\uED43", "folder"), name + (complete ? "" : " …"), path,
                new UIElement[] { sizeTb, open },
                BuildRuler(Math.Clamp(size * 100.0 / max, size > 0 ? 3 : 0, 100), "folder"));
            FoldersPanel.Children.Add(row);
        }
    }

    private void BuildMonitors(List<SystemMonitor.MonitorInfo> monitors)
    {
        MonitorsPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        MonitorsTitle.Text = monitors.Count == 0 ? "Мониторы" : $"Мониторы ({monitors.Count})";
        if (monitors.Count == 0)
        {
            MonitorsPanel.Children.Add(new TextBlock
            {
                Text = "Не удалось прочитать.",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        int i = 1;
        foreach (var m in monitors)
        {
            var parts = m.Primary
                ? new UIElement[] { StatusChip("ГЛАВНЫЙ", OkBrush) }
                : null;
            // Строка-плитка с глифом: название F_Ui, разрешение в подписи.
            MonitorsPanel.Children.Add(TileRow(GlyphTile("\uE922", "accent"),
                $"Экран {i}", $"{m.W}×{m.H}", parts));
            i++;
        }
    }

    private void BuildSensors(SensorsSnapshot s)
    {
        SensorsPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        bool any = s.CpuTemp != null || s.GpuTemp != null
            || s.DriveTemps.Count > 0 || s.Fans.Count > 0;
        SensorsTitle.Text = "Датчики";
        if (!any)
        {
            SensorsPanel.Children.Add(new TextBlock
            {
                Text = "Датчиков нет (запусти от администратора — нужен драйвер).",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        void Row(string label, string value, Brush? brush = null) =>
            SensorsPanel.Children.Add(KvRow(label, value, brush));
        if (s.CpuTemp != null)
            Row("CPU", s.CpuTemp > 0 ? $"{s.CpuTemp:F0}°C" : "—",
                s.CpuTemp > 0 ? TempSemi(s.CpuTemp.Value) : null);
        if (s.CpuLoad != null) Row("CPU нагрузка", $"{s.CpuLoad:F0}%", PctSemi(s.CpuLoad.Value));
        if (s.GpuTemp != null)
            Row("GPU", s.GpuTemp > 0 ? $"{s.GpuTemp:F0}°C" : "—",
                s.GpuTemp > 0 ? TempSemi(s.GpuTemp.Value) : null);
        if (s.GpuLoad != null) Row("GPU нагрузка", $"{s.GpuLoad:F0}%", PctSemi(s.GpuLoad.Value));
        int di = 1;
        foreach (var (name, temp) in s.DriveTemps)
            Row($"Диск {di++} · {name}", temp > 0 ? $"{temp:F0}°C" : "—",
                temp > 0 ? TempSemi(temp) : null);
        foreach (var (name, rpm) in s.Fans) Row("Вентилятор " + name, $"{rpm:F0} об/м");

        // Температура процессора — в подпись героя (если датчик отдал ненулевое).
        _cpuTempText = s.CpuTemp != null && s.CpuTemp > 0 ? $"{s.CpuTemp:F0}°C" : "";
        _cpuTempVal = s.CpuTemp != null && s.CpuTemp > 0 ? s.CpuTemp.Value : 0; // 🆕 0.14.3
        RefreshCpuTexts();

        // Чип у секции «Диски»: максимум по датчикам дисков — честно, без привязки
        // датчиков к буквам (LibreHardwareMonitor не гарантирует порядок).
        try
        {
            if (DisksTempChip != null && s.DriveTemps.Count > 0)
            {
                float maxT = 0;
                foreach (var (_, t) in s.DriveTemps) if (t > maxT) maxT = t;
                if (maxT > 0)
                {
                    DisksTempChip.Text = $"{maxT:F0}°C";
                    DisksTempChip.Foreground = TempSemi(maxT);
                }
            }
        }
        catch { }
    }

    private void BuildWifi(List<ExtraInfoService.WifiNet> nets)
    {
        WifiPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        var accent = (Brush)FindResource("B_Accent");
        WifiTitle.Text = nets.Count == 0 ? "Wi-Fi" : $"Wi-Fi ({nets.Count})";
        // Адаптера нет (десктоп без Wi-Fi) — прячем блок целиком.
        try { WifiCard.Visibility = nets.Count == 0 ? Visibility.Collapsed : Visibility.Visible; }
        catch { }
        if (nets.Count == 0)
        {
            WifiPanel.Children.Add(new TextBlock
            {
                Text = "Сетей не видно (нет Wi-Fi или отключён).",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var n in nets.Take(12))
        {
            var parts = new List<UIElement>();
            if (n.Connected) parts.Add(StatusChip("ПОДКЛЮЧЕНА", OkBrush));
            parts.Add(StatusChip($"{n.SignalPct}%", muted));
            // Строка-плитка: глиф, SSID, чипы (подключение/сигнал) и рулетка
            // сигнала; зашифровка — в подписи (Auth) с подсказкой.
            var row = TileRow(GlyphTile("\uE895", "wifi"), n.Ssid,
                n.Auth.Length > 0 ? n.Auth : "сеть", parts,
                BuildRuler(n.SignalPct, "wifi", showPct: false));
            WifiPanel.Children.Add(row);
        }
    }

    private void BuildTcp(List<ExtraInfoService.TcpRow> rows)
    {
        TcpPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        TcpTitle.Text = rows.Count == 0 ? "Подключения" : $"Подключения ({rows.Count})";
        if (rows.Count == 0)
        {
            TcpPanel.Children.Add(new TextBlock
            {
                Text = "Активных подключений нет.",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var r in rows.Take(20))
        {
            // label/value с волосяным разделителем: адреса слева, состояние справа.
            var g = KvRow($"{r.Local} → {r.Remote}", $"{r.State} • {r.Process}");
            g.Margin = new Thickness(0, 4, 0, 4);
            TcpPanel.Children.Add(g);
        }
    }

    private void BuildAudio(List<string> devs)
    {
        AudioPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        AudioTitle.Text = devs.Count == 0 ? "Звук" : $"Звук ({devs.Count})";
        if (devs.Count == 0)
        {
            AudioPanel.Children.Add(new TextBlock
            {
                Text = "Устройств не найдено.",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (string d in devs.Take(10))
        {
            // Строка-плитка с глифом: имя устройства F_Ui, без лишнего.
            AudioPanel.Children.Add(TileRow(GlyphTile("\uE995", "accent"), d, null, null));
        }
    }

    private void BuildSysFolders(List<(string Name, string Path, long Size, bool Complete)> folders)
    {
        SysFoldersPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        long max = 1;
        foreach (var f in folders) max = Math.Max(max, f.Size);
        foreach (var (name, _, size, complete) in folders)
        {
            long sz = size;
            var sizeTb = new TextBlock
            {
                Text = size > 0 ? DiskInfo.Gb(size) : "—",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            sizeTb.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
            UiFont(sizeTb);
            // Та же строка-плитка, что и в «Папках», но с процентами на рулетке.
            var row = TileRow(GlyphTile("\uED44", "folder"), name + (complete ? "" : " …"), null,
                new UIElement[] { sizeTb },
                BuildRuler(Math.Clamp(sz * 100.0 / max, sz > 0 ? 3 : 0, 100), "folder"));
            SysFoldersPanel.Children.Add(row);
        }
        // Глубокая очистка: WinSxS (DISM) + кэш обновлений. Медленно — за флагом.
        try
        {
            SysFoldersPanel.Children.Add(new TextBlock
            {
                Text = "WinSxS и кэш обновлений чистятся отдельно — долго, но освобождают много.",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 6),
            });
            if (AppSettings.ExpOn())
            {
                var deep = new Button
                {
                    Content = "Глубокая очистка…", FontSize = 12,
                    Padding = new Thickness(12, 4, 12, 4), Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = "Анализ WinSxS (1–2 мин), потом очистка (до 30 мин) + кэш обновлений.",
                };
                deep.Click += DeepClean_Click;
                SysFoldersPanel.Children.Add(deep);
            }
        }
        catch { }
    }

    private bool _deepBusy;

    private void DeepClean_Click(object sender, RoutedEventArgs e)
    {
        if (_deepBusy) return;
        if (!AdminGate.IsAdmin())
        {
            var owner0 = Window.GetWindow(this);
            ResultDialog.ShowResult(owner0, "\uE8F4", "Глубокая очистка",
                "Нужен запуск от администратора: DISM и службы обновлений без прав не дадут.");
            return;
        }
        _deepBusy = true;
        SetStatusLocal("Считаю WinSxS и кэш обновлений… (1–2 мин)");
        Task.Run(() =>
        {
            string analysis = DiskCleanupService.DismAnalyze();
            var (ucCount, ucBytes) = DiskCleanupService.UpdateCacheInfo();
            return (analysis, ucCount, ucBytes);
        }).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    _deepBusy = false;
                    var owner = Window.GetWindow(this);
                    if (t.IsFaulted)
                    {
                        SetStatusLocal("Не вышло посчитать.");
                        return;
                    }
                    var (analysis, ucCount, ucBytes) = t.Result;
                    bool ucWork = ucCount > 0 && ucBytes > 0;
                    string ucLine = !ucWork ? "• Кэш обновлений: пуст."
                        : $"• Кэш обновлений: ~{DiskCleanupService.FormatSize(ucBytes)} ({ucCount} {DiskCleanupService.Plural(ucCount, "файл", "файла", "файлов")}).";
                    string text = "Глубокая очистка — что нашёл:\n" + analysis + "\n" + ucLine;
                    SetStatusLocal("Готов");
                    bool canClean = !analysis.StartsWith("Ошибка");
                    bool dismWork = !analysis.Contains("Чистить нечего");
                    if (!canClean || (!ucWork && !dismWork))
                    {
                        ResultDialog.ShowResult(owner, "\uE8F4", "Глубокая очистка", text);
                        return;
                    }
                    ResultDialog.ShowResult(owner, "\uE8F4", "Глубокая очистка", text,
                        "Очистить", () =>
                        {
                            string d = DiskCleanupService.DismCleanup();
                            string u = ucWork ? DiskCleanupService.UpdateCacheClean()
                                : "Кэш обновлений: трогать нечего.";
                            string res = d + "\n" + u;
                            try { ActionJournal.AddInfo("Глубокая очистка", res.Split('\n')[0].Trim()); }
                            catch { }
                            return res;
                        });
                    Refresh();
                }
                catch { _deepBusy = false; }
            });
        });
    }

    private void BuildUpdates((string LastSearch, string LastInstall, bool RebootNeeded) wu,
        List<WingetService.WingetUpdate> updates, bool wingetOk, bool known = true)
    {
        // 🆕 0.14.3: состояние полосы секции — ребут красный, ждут пакеты
        // жёлтый, всё свежее зелёный; known=false (список ещё не пришёл или
        // winget завис) — нейтральная, «не проверено».
        _updatesReboot = wu.RebootNeeded;
        _updatesPending = known && wingetOk ? updates.Count : -1;
        SetStrip(StripUpdates, _updatesReboot ? ErrBrush
            : _updatesPending < 0 ? null
            : _updatesPending > 0 ? WarnBrush : OkBrush);
        UpdatesPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        var accent = (Brush)FindResource("B_Accent");
        int n = 0;
        void Line(string label, string? value, bool warn = false)
        {
            // label/value с волосяным разделителем — как в сетевых параметрах.
            UpdatesPanel.Children.Add(KvRow(label,
                !string.IsNullOrEmpty(value) ? value : "—", warn ? accent : null));
            n++;
        }
        Line("Проверка обновлений", wu.LastSearch);
        Line("Установка", wu.LastInstall);
        Line("Перезагрузка", wu.RebootNeeded ? "НУЖНА!" : "не нужна", wu.RebootNeeded);
        // Часы активности + P2P-доставка — всё тут, никуда лезть не надо.
        {
            var hours = PrivacyService.ActiveHours();
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock
            {
                Text = "Не беспокоить:", FontSize = 12, Foreground = muted,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 0);
            row.Children.Add(lbl);
            _hoursStartBox = new System.Windows.Controls.TextBox
            {
                Text = hours.Start.ToString(), FontSize = 12, Width = 36, Height = 28,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(_hoursStartBox, 1);
            row.Children.Add(_hoursStartBox);
            var dash = new TextBlock
            {
                Text = "–", FontSize = 12, Foreground = muted,
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dash, 2);
            row.Children.Add(dash);
            _hoursEndBox = new System.Windows.Controls.TextBox
            {
                Text = hours.End.ToString(), FontSize = 12, Width = 36, Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(_hoursEndBox, 3);
            row.Children.Add(_hoursEndBox);
            var ok = new Button
            {
                Content = "OK", FontSize = 11, Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                ToolTip = "Поставить часы тишины обновлений (не беспокоить).",
            };
            ok.Click += HoursApply_Click;
            Grid.SetColumn(ok, 4);
            row.Children.Add(ok);
            UpdatesPanel.Children.Add(row);
        }
        {
            string del = PrivacyService.DeliveryState();
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock
            {
                Text = "Раздача обновлений другим ПК: "
                    + (del == "na" ? "—" : del == "off" ? "выкл" : "вкл"),
                FontSize = 12, Foreground = text,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 0);
            row.Children.Add(lbl);
            if (del != "na" && AppSettings.ExpOn())
            {
                bool enable = del != "off";
                var btn = new Button
                {
                    Content = enable ? "Выключить" : "Включить",
                    FontSize = 11, Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                    Tag = !enable,
                    ToolTip = "Раздача скачанных обновлений другим ПК по сети. Выкл = меньше нагрузки на диск и сеть.",
                };
                btn.Click += Delivery_Click;
                Grid.SetColumn(btn, 1);
                row.Children.Add(btn);
            }
            UpdatesPanel.Children.Add(row);
        }
        if (!wingetOk)
        {
            UpdatesPanel.Children.Add(new TextBlock
            {
                Text = "winget не найден.",
                FontSize = 12, Foreground = muted, Margin = new Thickness(0, 6, 0, 0),
            });
            return;
        }
        UpdatesPanel.Children.Add(new TextBlock
        {
            Text = updates.Count == 0 ? "Обновлений программ нет." : $"Обновления программ ({updates.Count}):",
            FontSize = 12, Foreground = text, Margin = new Thickness(0, 8, 0, 2),
        });
        foreach (var u in updates.Take(12))
        {
            // label/value с волосяным разделителем: пакет слева, версии справа.
            UpdatesPanel.Children.Add(KvRow(u.Name, $"{u.Version} → {u.Available}"));
        }
        UpdatesTitle.Text = "Обновления";
    }

    private void WuScan_Click(object sender, RoutedEventArgs e)
    {
        SetStatusLocal("Запускаю проверку обновлений…");
        Task.Run(ExtraInfoService.StartUpdateScan).ContinueWith(t =>
        {
            Dispatcher.Invoke(() => SetStatusLocal(t.IsFaulted ? "Ошибка." : t.Result));
        });
    }

    private void HoursApply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_hoursStartBox?.Text, out int s)
            || !int.TryParse(_hoursEndBox?.Text, out int en))
        {
            SetStatusLocal("Часы — числами от 0 до 23.");
            return;
        }
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, $"Не беспокоить {s}:00–{en}:00?")) return;
        SetStatusLocal("Ставлю часы…");
        Task.Run(() => PrivacyService.SetActiveHours(s, en)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                SetStatusLocal(t.IsFaulted ? "Ошибка." : t.Result);
                Refresh();
            });
        });
    }

    private void Delivery_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not bool enable) return;
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, enable
            ? "Включить раздачу обновлений?"
            : "Выключить раздачу обновлений?")) return;
        SetStatusLocal("Меняю доставку…");
        Task.Run(() => PrivacyService.SetDelivery(enable)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                SetStatusLocal(t.IsFaulted ? "Ошибка." : t.Result);
                Refresh();
            });
        });
    }

    private void WingetInstall_Click(object sender, RoutedEventArgs e)
    {
        string q = (WingetBox.Text ?? "").Trim();
        if (q.Length == 0) { SetStatusLocal("Введи название или ID пакета."); return; }
        if (!ExpGate("Установка программ — экспериментальное. Включи в Настройках.")) return;
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, $"Установить {q}?",
            "Установка через winget. Может запросить права.")) return;
        SetStatusLocal($"Устанавливаю {q}… (долго)");
        Task.Run(() => WingetService.Install(q)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() => SetStatusLocal(t.IsFaulted ? "Ошибка." : Shorten(t.Result)));
        });
    }

    private void WingetUpgrade_Click(object sender, RoutedEventArgs e)
    {
        if (!ExpGate("Обновление всех программ — экспериментальное. Включи в Настройках.")) return;
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, "Обновить ВСЕ программы?",
            "Через winget. Долго, могут быть перезапуски приложений.")) return;
        SetStatusLocal("Обновляю всё… (очень долго)");
        Task.Run(WingetService.UpgradeAll).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                SetStatusLocal(t.IsFaulted ? "Ошибка." : Shorten(t.Result));
                Refresh();
            });
        });
    }

    private static string Shorten(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length > 300) s = "…" + s[^300..];
        return s.Length == 0 ? "Готово." : s;
    }

    private bool ExpGate(string msg)
    {
        bool on = AppSettings.ExpOn();
        if (!on) SetStatusLocal(msg);
        return on;
    }

    private void BuildEvents(List<(DateTime Time, string Source, string Message)> events)
    {
        EventsPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        _eventsCount = events.Count; // 🆕 0.14.3: чип/полоса/превью считаются отсюда
        EventsTitle.Text = events.Count == 0 ? "Ошибки" : $"Ошибки ({events.Count})";
        // 🆕 0.14.3: превью свёрнутого журнала — по первой записи сразу видно,
        // критично ли, не раскрывая список (фидбек: «свёрнут без превью»).
        if (EventsPreview != null)
        {
            if (events.Count == 0)
            {
                EventsPreview.Text = "Свежих ошибок нет. Хорошо.";
                EventsPreview.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
            }
            else
            {
                string m = (events[0].Message ?? "").Replace("\n", " ").Trim();
                if (m.Length > 120) m = m[..120] + "…";
                string more = events.Count > 1 ? $" • ещё {events.Count - 1}" : "";
                EventsPreview.Text = $"Последняя: {m}{more}";
                EventsPreview.Foreground = ErrBrush;
            }
            EventsPreview.Visibility = EventsPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }
        PaintSectionStrips(); // 🆕 0.14.3
        PaintChips();
        if (events.Count == 0)
        {
            EventsPanel.Children.Add(new TextBlock
            {
                Text = "Свежих ошибок в журналах нет. Хорошо.",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var (time, source, message) in events.Take(12))
        {
            // Строка-плитка: точка-статус ошибки, текст переносится,
            // источник и время — в подписи.
            EventsPanel.Children.Add(TileRow(StatusDot(ErrBrush), message,
                $"{time:dd.MM HH:mm} • {source}", null, null, null, wrap: true));
        }
    }

    private void BuildTweaks()
    {
        TweaksPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        var accent = (Brush)FindResource("B_Accent");
        bool exp = AppSettings.ExpOn();
        TweaksTitle.Text = "Твики";
        // «Классическое меню ПКМ» — только Win11, на Win10 меню и так старое.
        bool isWin11 = false;
        try { isWin11 = Environment.OSVersion.Version.Build >= 22000; }
        catch { }
        foreach (var tw in TweakService.All)
        {
            if ((tw.Id == "classicmenu" || tw.Id == "taskalign") && !isWin11) continue;
            string state;
            try { state = TweakService.GetState(tw.Id); }
            catch { state = "na"; }
            var right = new List<UIElement>
            {
                StatusChip(state == "on" ? "ВКЛ" : state == "off" ? "ВЫКЛ" : "—",
                    state == "on" ? OkBrush : state == "off" ? WarnBrush : muted),
            };
            if (exp && state != "na")
            {
                bool turnOn = state != "on";
                var btn = new Button
                {
                    Content = turnOn ? "Включить" : "Выключить",
                    FontSize = 11, Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                    Tag = (tw.Id, turnOn),
                    ToolTip = $"{tw.Name}: {tw.Desc}",
                };
                btn.Click += Tweak_Click;
                right.Add(btn);
            }
            // Точка-статус слева, чип ВКЛ/ВЫКЛ цветом справа, кнопка рядом.
            TweaksPanel.Children.Add(TileRow(
                StatusDot(state == "on" ? OkBrush : state == "off" ? WarnBrush : muted),
                tw.Name + (tw.NeedsAdmin ? " (админ)" : ""), tw.Desc, right));
        }
    }

    private void Tweak_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not (string id, bool on)) return;
        string twName = id;
        try
        {
            var found = TweakService.All.FirstOrDefault(t => t.Id == id);
            if (found != null) twName = found.Name;
        }
        catch { }
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, $"Твик: {(on ? "включить" : "выключить")}?",
            "Меняет настройки Windows. На свой риск.")) return;
        SetStatusLocal("Применяю твик…");
        Task.Run(() =>
        {
            string bs;
            try { bs = TweakService.GetState(id); } catch { bs = "na"; }
            string rs = TweakService.Apply(id, on);
            return (before: bs, res: rs);
        }).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    string res = t.IsFaulted ? "Ошибка." : t.Result.res;
                    SetStatusLocal(res);
                    if (!t.IsFaulted && res.StartsWith("Готово"))
                    {
                        string bs = t.Result.before;
                        if (bs != "on" && bs != "off") bs = on ? "off" : "on";
                        ActionJournal.AddTweak(twName, id, bs == "on", on);
                    }
                    Refresh();
                }
                catch { Refresh(); }
            });
        });
    }

    private System.Windows.Controls.TextBox? _hoursStartBox;
    private System.Windows.Controls.TextBox? _hoursEndBox;

    private void BuildPrivacy()
    {
        PrivacyPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        var accent = (Brush)FindResource("B_Accent");
        bool exp = AppSettings.ExpOn();
        int closed = 0, total = 0;
        var rows = new List<(PrivacyService.PrivacyTweak Tw, string State)>();
        foreach (var tw in PrivacyService.All)
        {
            string st;
            try { st = PrivacyService.GetState(tw.Id); }
            catch { st = "na"; }
            total++;
            // Для cam/mic "on" = доступ разрешён (не приватно); для остальных on = закрыто.
            bool priv = tw.Id is "cam" or "mic" ? st != "on" : st == "on";
            if (priv) closed++;
            rows.Add((tw, st));
        }
        PrivacyTitle.Text = $"Приватность ({closed}/{total})";
        foreach (var (tw, st) in rows)
        {
            bool priv = tw.Id is "cam" or "mic" ? st != "on" : st == "on";
            var right = new List<UIElement>
            {
                // Прямоугольный чип: ЗАКРЫТО — зелёный, ОТКРЫТО — жёлтый.
                StatusChip(st == "na" ? "—" : priv ? "ЗАКРЫТО" : "ОТКРЫТО",
                    st == "na" ? muted : priv ? OkBrush : WarnBrush),
            };
            if (exp && st != "na")
            {
                // Кнопка переключает в закрытое состояние (или открывает для cam/mic).
                bool targetClosed = !priv;
                var btn = new Button
                {
                    Content = targetClosed ? "Закрыть" : "Открыть",
                    FontSize = 11, Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                    Tag = (tw.Id, targetClosed, tw.Id is "cam" or "mic"),
                };
                btn.Click += Privacy_Click;
                right.Add(btn);
            }
            // Точка-статус слева, иерархия «название / описание», чип + кнопка справа.
            PrivacyPanel.Children.Add(TileRow(
                StatusDot(st == "na" ? muted : priv ? OkBrush : WarnBrush),
                tw.Name, tw.Desc, right));
        }
    }

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not (string id, bool targetClosed, bool isAccess)) return;
        var owner = Window.GetWindow(this);
        string what = isAccess
            ? (targetClosed ? "закрыть доступ" : "разрешить доступ")
            : (targetClosed ? "закрыть слежку" : "открыть слежку");
        if (!ConfirmDialog.Ask(owner, $"{what}?",
            "Меняет настройки Windows.")) return;
        // Для cam/mic: targetClosed=true → Apply(id, false) (закрыть доступ).
        // Для остальных: targetClosed=true → Apply(id, true).
        bool applyOn = isAccess ? !targetClosed : targetClosed;
        string pvName = id;
        try
        {
            var found = PrivacyService.All.FirstOrDefault(t => t.Id == id);
            if (found != null) pvName = found.Name;
        }
        catch { }
        SetStatusLocal("Применяю…");
        Task.Run(() =>
        {
            string prev;
            try { prev = PrivacyService.GetState(id); } catch { prev = "na"; }
            string rs = PrivacyService.Apply(id, applyOn);
            return (prev, rs);
        }).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    string res = t.IsFaulted ? "Ошибка." : t.Result.rs;
                    SetStatusLocal(res);
                    if (!t.IsFaulted && res.StartsWith("Готово"))
                    {
                        string pv = t.Result.prev;
                        // Ключа не было (na) — честно без отката: писать значение
                        // там, где его не было, значит выдумывать состояние.
                        if (pv != "on" && pv != "off")
                            ActionJournal.AddInfo($"Приватность: {pvName}",
                                "Включено (прошлого значения не было — откат вручную).");
                        else
                            ActionJournal.AddPrivacy(pvName, id, pv == "on", applyOn);
                    }
                    Refresh();
                }
                catch { Refresh(); }
            });
        });
    }

    private void BuildJunk(List<AppxService.AppxApp>? apps)
    {
        JunkPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        bool exp = AppSettings.ExpOn();
        if (apps == null)
        {
            JunkTitle.Text = "Мусор";
            JunkPanel.Children.Add(new TextBlock
            {
                Text = "Сканирую встроенные приложения…",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        JunkTitle.Text = apps.Count == 0 ? "Мусор" : $"Мусор ({apps.Count})";
        if (apps.Count == 0)
        {
            JunkPanel.Children.Add(new TextBlock
            {
                Text = "Встроенного мусора не нашёл. Чисто.",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var a in apps.Take(25))
        {
            string nl = a.Name.ToLowerInvariant();
            bool dangerous = nl.Contains("xbox") || nl.Contains("microsoft.store")
                || nl.Contains("cortana") || nl.Contains("microsoftedge")
                || nl == "microsoft.edge" || nl.Contains("bing")
                || nl.Contains("zune") || nl.Contains("windowsfeedback")
                || nl.Contains("calculator") || nl.Contains("photos")
                || nl.Contains("paint")
                || nl.Contains("notepad") || nl.Contains("terminal")
                || nl.Contains("alarms") || nl.Contains("camera") || nl.Contains("maps");
            var right = new List<UIElement>();
            if (dangerous)
            {
                right.Add(StatusChip("СИСТЕМНОЕ", muted));
            }
            else if (exp)
            {
                var del = new Button
                {
                    Content = "Удалить", FontSize = 11, Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(8, 1, 0, 1), Cursor = Cursors.Hand,
                    Tag = a,
                    ToolTip = $"Удалить встроенное приложение {a.Name}. Вернуть можно через Store.",
                };
                del.Click += JunkRemove_Click;
                right.Add(del);
            }
            // Строка-плитка: глиф-папка, имя F_Ui, чип «системное»/кнопка.
            var row = TileRow(GlyphTile("\uE8F4", "folder"), PrettyAppxName(a.Name),
                null, right.Count > 0 ? right : null);
            row.ToolTip = a.Name;
            JunkPanel.Children.Add(row);
        }
        if (apps.Count > 25)
            JunkPanel.Children.Add(new TextBlock
            {
                Text = $"…и ещё {apps.Count - 25}",
                FontSize = 12, Foreground = muted, Margin = new Thickness(0, 4, 0, 0),
            });
    }

    // "Microsoft.BingWeather" → "Bing Weather", "A025C540.YandexMusic" → "Yandex Music",
    // "1527c705-…" → "Пакет 1527c705". Только для показа, удаляем по FullName.
    private static string PrettyAppxName(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return "Без названия";
            string s = name.Trim();
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
            // Похоже на GUID/ID без слов — показываем коротко.
            bool idLike = s.Length >= 8 && s.All(ch =>
                (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F') || ch == '-');
            if (idLike) return "Пакет " + s[..8];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '_' || c == '-') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && char.IsLower(s[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            string pretty = sb.ToString().Trim();
            return pretty.Length == 0 ? name : pretty;
        }
        catch { return name; }
    }

    private void JunkRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not AppxService.AppxApp a) return;
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, $"Удалить {a.Name}?",
            "Встроенное приложение Windows. Откатить можно через Store.")) return;
        SetStatusLocal($"Удаляю {a.Name}…");
        Task.Run(() => AppxService.Remove(a.FullName)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                SetStatusLocal(t.IsFaulted ? "Ошибка." : t.Result);
                Refresh();
            });
        });
    }

    private void BuildPower(List<PrivacyService.PowerScheme> schemes)
    {
        PowerPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        var accent = (Brush)FindResource("B_Accent");
        bool exp = AppSettings.ExpOn();
        if (schemes.Count == 0)
        {
            PowerPanel.Children.Add(new TextBlock
            {
                Text = "Не удалось прочитать схемы.",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var s in schemes)
        {
            var right = new List<UIElement>();
            if (s.Active) right.Add(StatusChip("АКТИВНА", OkBrush));
            else if (exp)
            {
                var go = new Button
                {
                    Content = "Включить", FontSize = 11, Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                    Tag = s.Guid, ToolTip = $"Включить схему питания: {s.Name}.",
                };
                go.Click += PowerScheme_Click;
                right.Add(go);
            }
            // Точка-статус: активная схема — зелёная, остальные — приглушены.
            PowerPanel.Children.Add(TileRow(StatusDot(s.Active ? OkBrush : muted),
                s.Name, null, right.Count > 0 ? right : null));
        }
    }

    private void PowerScheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string guid) return;
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, "Сменить схему питания?")) return;
        SetStatusLocal("Переключаю схему…");
        Task.Run(() =>
        {
            string prevGuid = "", prevName = "";
            try
            {
                var cur = PrivacyService.PowerSchemes().FirstOrDefault(s => s.Active);
                if (cur != null) { prevGuid = cur.Guid; prevName = cur.Name; }
            }
            catch { }
            string rs = PrivacyService.SetPowerScheme(guid);
            string nowName = "";
            try
            {
                var now = PrivacyService.PowerSchemes().FirstOrDefault(s => s.Guid == guid);
                if (now != null) nowName = now.Name;
            }
            catch { }
            return (prevGuid, prevName, nowName, rs);
        }).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    string res = t.IsFaulted ? "Ошибка." : t.Result.rs;
                    SetStatusLocal(res);
                    if (!t.IsFaulted && res.StartsWith("Готово"))
                        ActionJournal.AddPower(t.Result.prevName, t.Result.prevGuid,
                            t.Result.nowName.Length > 0 ? t.Result.nowName : guid);
                    Refresh();
                }
                catch { Refresh(); }
            });
        });
    }

    private void BuildStartup(List<SystemMonitor.StartupEntry> entries)
    {
        StartupPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        bool exp = AppSettings.ExpOn();
        StartupTitle.Text = entries.Count == 0 ? "Автозагрузка" : $"Автозагрузка ({entries.Count})";
        if (entries.Count == 0)
        {
            StartupPanel.Children.Add(new TextBlock
            {
                Text = "Пусто — ничего не запускается вместе с системой.",
                FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var e in entries.Take(20))
        {
            bool locked = StartupProtected(e.Name);
            string? advice = StartupAdvice(e.Name);
            // Чип источника: реестр (HKCU/HKLM) или папка — как в Играх.
            bool registry = (e.Location ?? "").Contains("HK", StringComparison.OrdinalIgnoreCase);
            var right = new List<UIElement>
            {
                StatusChip(registry ? "РЕЕСТР" : "ПАПКА", muted),
                StatusChip(locked ? "не трогать" : SimpleLoc(e.Location ?? ""),
                    locked ? WarnBrush : muted),
            };
            if (exp)
            {
                var off = new Button
                {
                    Content = "Выкл", FontSize = 11, Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(8, 1, 0, 1), Cursor = Cursors.Hand,
                    Tag = e, IsEnabled = !locked,
                    ToolTip = locked
                        ? "Системное — кнопка заблокирована."
                        : $"Убрать {e.Name} из автозагрузки. Вернуть можно (бэкап).",
                };
                off.Click += StartupOff_Click;
                right.Add(off);
            }
            // Строка-плитка: название F_Ui, совет-подпись жёлтым, чипы справа.
            StartupPanel.Children.Add(TileRow(GlyphTile("\uF0E3", "accent"),
                e.Name, advice, right, null, advice != null ? WarnBrush : null));
        }
        if (entries.Count > 20)
            StartupPanel.Children.Add(new TextBlock
            {
                Text = $"…и ещё {entries.Count - 20}",
                FontSize = 12, Foreground = muted, Margin = new Thickness(0, 4, 0, 0),
            });
    }

    // Простой интерфейс: вместо «реестр HKCU» — человеческое слово.
    private static string SimpleLoc(string loc)
    {
        try
        {
            if (AppSettings.Load().Interface == AppSettings.InterfaceMode.Simple)
                return "автозапуск";
        }
        catch { }
        return loc;
    }

    // Белый список: драйверы и защита — кнопку блокируем, а не прячем.
    private static bool StartupProtected(string name)
    {
        try
        {
            string nl = (name ?? "").ToLowerInvariant();
            return nl.Contains("securityhealth") || nl.Contains("defender")
                || nl.Contains("securitycenter") || nl.Contains("nvidia")
                || nl.Contains("radeon") || nl.Contains("adrenalin")
                || nl.Contains("realtek") || nl.Contains("synaptics")
                || nl.Contains("intel");
        }
        catch { return false; }
    }

    // Серый список: тормозят старт, выключать безопасно.
    private static string? StartupAdvice(string name)
    {
        try
        {
            string nl = (name ?? "").ToLowerInvariant();
            string[] slow =
            {
                "skype", "discord", "spotify", "onedrive", "utorrent", "bittorrent",
                "epicgameslauncher", "steam", "teams", "origin", "goggalaxy",
                "ituneshelper", "skydrive",
            };
            foreach (string s in slow)
                if (nl.Contains(s)) return "Тормозит старт — можно выключить";
        }
        catch { }
        return null;
    }

    private void StartupOff_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not SystemMonitor.StartupEntry en) return;
        if (StartupProtected(en.Name))
        {
            SetStatusLocal("Системное не трогаю — драйвер или защита Windows.");
            return;
        }
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, $"Выключить автозапуск?",
            $"{en.Name}\n({en.Location})")) return;
        SetStatusLocal($"Выключаю {en.Name}…");
        Task.Run(() => SystemMonitor.DisableStartup(en)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    string res = t.IsFaulted ? "Ошибка." : t.Result;
                    SetStatusLocal(res);
                    if (!t.IsFaulted && res.StartsWith("Выключено:"))
                        ActionJournal.AddStartup(en.Name, en.Location);
                    Refresh();
                }
                catch { Refresh(); }
            });
        });
    }

    private static void OpenExternal(string file, string args = "")
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); }
        catch { }
    }

    private void DrvScan_Click(object sender, RoutedEventArgs e)
    {
        SetStatusLocal("Сканирую устройства (pnputil)…");
        Task.Run(() => SystemMonitor.RunCmd("pnputil.exe", "/scan-devices", 60000)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                SetStatusLocal("Сканирование устройств запущено.");
                Refresh();
            });
        });
    }

    private void DrvMgr_Click(object sender, RoutedEventArgs e) => OpenExternal("devmgmt.msc");

    private void DrvUpdate_Click(object sender, RoutedEventArgs e) =>
        OpenExternal("ms-settings:windowsupdate");

    private void NetConn_Click(object sender, RoutedEventArgs e) => OpenExternal("ncpa.cpl");

    private void Display_Click(object sender, RoutedEventArgs e) => OpenExternal("desk.cpl");

    private void Msinfo_Click(object sender, RoutedEventArgs e) => OpenExternal("msinfo32.exe");

    private void Ping_Click(object sender, RoutedEventArgs e)
    {
        SetStatusLocal("Пингую 8.8.8.8…");
        Task.Run(() =>
        {
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = ping.Send("8.8.8.8", 2000);
                return reply.Status == System.Net.NetworkInformation.IPStatus.Success
                    ? $"8.8.8.8: {reply.RoundtripTime} мс."
                    : $"Нет ответа: {reply.Status}.";
            }
            catch (Exception ex) { return $"Нет сети: {ex.Message}"; }
        }).ContinueWith(t =>
        {
            Dispatcher.Invoke(() => SetStatusLocal(t.IsFaulted ? "Ошибка пинга." : t.Result));
        });
    }

    private void FolderOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string path && path.Length > 0)
            OpenExternal("explorer.exe", $"\"{path}\"");
    }

    private void ProgUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not SystemMonitor.ProgramInfo p) return;
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, $"Удалить {p.Name}?",
            "Запустится штатный деинсталлятор.")) return;
        SetStatusLocal($"Удаляю {p.Name}…");
        Task.Run(() => SystemMonitor.UninstallProgram(p)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                SetStatusLocal(t.IsFaulted ? "Ошибка." : t.Result);
                Refresh();
            });
        });
    }

    private static List<ProcRow> TopProcs(string mode)
    {
        var rows = new List<ProcRow>();
        try
        {
            if (mode == "cpu")
            {
                var first = new Dictionary<int, (string Name, double Ms)>();
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        first[p.Id] = (p.ProcessName,
                            p.TotalProcessorTime.TotalMilliseconds);
                        p.Dispose();
                    }
                    catch { }
                }
                System.Threading.Thread.Sleep(400);
                int cores = Math.Max(1, Environment.ProcessorCount);
                var scored = new List<ProcRow>();
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (!first.TryGetValue(p.Id, out var prev))
                        {
                            p.Dispose();
                            continue;
                        }
                        double delta = p.TotalProcessorTime.TotalMilliseconds - prev.Ms;
                        p.Dispose();
                        if (delta < 0) continue;
                        double cpu = delta / 400.0 / cores * 100.0;
                        scored.Add(new ProcRow(prev.Name, p.Id, 0, cpu));
                    }
                    catch { }
                }
                return scored.OrderByDescending(r => r.Cpu).Take(12).ToList();
            }
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    rows.Add(new ProcRow(p.ProcessName, p.Id,
                        p.WorkingSet64 / 1024.0 / 1024));
                    p.Dispose();
                }
                catch { /* чужой процесс — пропускаем */ }
            }
        }
        catch { }
        return rows.OrderByDescending(r => r.Mb).Take(12).ToList();
    }

    private void BuildProcs(List<ProcRow> procs, bool exp, string mode)
    {
        ProcsPanel.Children.Clear();
        ProcsHint.Text = exp
            ? "Завершение процессов включено (экспериментальное)."
            : "Список только для чтения. Завершение — в «Экспериментальном» (Настройки).";
        ProcMemBtn.Opacity = mode == "mem" ? 1 : 0.55;
        ProcCpuBtn.Opacity = mode == "cpu" ? 1 : 0.55;
        // Мини-топ живёт в карточке «Загрузка» — строится из того же списка.
        BuildTopMini(procs, mode);
        var dim = (Brush)FindResource("B_Muted");
        double topMb = Math.Max(1, procs.Count > 0 ? procs.Max(p => p.Mb) : 1);
        double topCpu = Math.Max(1, procs.Count > 0 ? procs.Max(p => p.Cpu) : 1);
        foreach (var p in procs)
        {
            var right = new List<UIElement>
            {
                StatusChip($"PID {p.Pid}", dim),
                StatusChip(mode == "cpu" ? $"{p.Cpu:F1}%" : $"{p.Mb:F0} МБ",
                    mode == "cpu" ? PctSemi(p.Cpu) : dim),
            };
            if (exp)
            {
                var kill = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Margin = new Thickness(8, 0, 0, 0),
                    ToolTip = $"Завершить {p.Name} (PID {p.Pid}). Несохранённые данные пропадут!",
                    Tag = p,
                };
                kill.Content = new TextBlock
                {
                    Text = "✕",
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                kill.Click += Kill_Click;
                right.Add(kill);
            }
            // Рулетка — доля от самого тяжёлого (статическая), шкала cpu/ram.
            double share = mode == "cpu"
                ? Math.Clamp(p.Cpu * 100.0 / topCpu, 0, 100)
                : Math.Clamp(p.Mb * 100.0 / topMb, 0, 100);
            string key = mode == "cpu" ? "cpu" : "ram";
            string sub = mode == "cpu" ? $"{p.Mb:F0} МБ" : $"{p.Cpu:F1}% CPU";
            var row = TileRow(GlyphTile("\uE950", key), p.Name, sub, right,
                BuildRuler(share, key));
            ProcsPanel.Children.Add(row);
        }
    }

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProcRow p) return;
        if (ProtectedProcs.Contains(p.Name) || p.Pid <= 4)
        {
            Report($"Процесс «{p.Name}» системный, завершать нельзя.");
            return;
        }
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, $"Завершить {p.Name}?",
            $"Процесс: {p.Name}  PID {p.Pid}")) return;
        try
        {
            using var proc = Process.GetProcessById(p.Pid);
            proc.Kill();
            Report($"Процесс «{p.Name}» завершён.");
        }
        catch (Exception ex)
        {
            Report($"Не удалось завершить «{p.Name}»: {ex.Message}");
        }
        Refresh();
    }
}
