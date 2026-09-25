using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pult.Services;

namespace Pult.Views;

// Домашний экран Простого интерфейса: пульс + проверка + 4 большие кнопки.
// Цепочки: «нашёл → Сделать? → готово». Всё — через ConfirmDialog и ActionJournal.
public partial class SimpleHomeView : UserControl
{
    public Action<string> Report = _ => { };
    public Action GoChat = () => { };
    public Action GoHistory = () => { };
    public Action GoAppearance = () => { };

    private bool _busy;

    public SimpleHomeView()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        _beatTimer.Tick += (_, _) => BeatTick();
        _beatTimer.Start();
    }

    public void Reload()
    {
        try { ApplyPulseGlow(); } catch { }
        try { PlayEnter(); } catch { }
        // Число дышит масштабом, статус и линия — прозрачностью, синхронно.
        // (Заменяет общий PulseAnimate на этом числе, чтобы не было двух ритмов.)
        try { EffectsHelper.PulseBeat(PulseScore, PulseStatus, PulseSpark); } catch { }
        // Чат без флага выключен — ссылку помощника прячем вместе с чатом.
        // 🆕 0.14.3: чат и история живут ссылками под сеткой, поэтому дом
        // всегда 4 плитки-цепочки (спека PROJECT), Корзина — правая внизу.
        // История видна всегда (как и пункт «Что я сделал» в меню).
        try
        {
            bool exp = AppSettings.ExpOn();
            CardChat.Visibility = exp ? Visibility.Visible : Visibility.Collapsed;
            CardHistory.Visibility = Visibility.Visible;
            CardRecycle.Margin = new Thickness(10, 10, 0, 0);
        }
        catch { }
        try { UpdateRecycleSub(); } catch { }
        try { RenderPulse(); }
        catch (Exception ex)
        {
            try { Report("Не вышло проверить: " + ex.Message); } catch { }
        }
    }

    // Появление карточек по общему уровню эффектов (как вкладки в MainWindow).
    private void PlayEnter()
    {
        try { EffectsHelper.FadeIn(PulseCard); } catch { }
        try { EffectsHelper.FadeIn(SmartCard); } catch { }
        try { EffectsHelper.FadeIn(CardJunk); } catch { }
        try { EffectsHelper.FadeIn(CardSpace); } catch { }
        try { EffectsHelper.FadeIn(CardDupes); } catch { }
        try { EffectsHelper.FadeIn(CardChat); } catch { }
        try { EffectsHelper.FadeIn(CardHistory); } catch { }
        try { EffectsHelper.FadeIn(CardRecycle); } catch { }
    }

    // Уровни: Максимум — живое свечение, Обычные — мягкая статичная тень,
    // Минимум — ничего. Эффект на рамке сзади, текст чёткий.
    // Один экземпляр эффекта на один элемент.
    private System.Windows.Media.Effects.DropShadowEffect? _pulseGlow;
    private System.Windows.Media.Effects.DropShadowEffect? _pulseGlowStatic;
    private System.Windows.Media.Color _pulseGlowAccent;

    private void ApplyPulseGlow()
    {
        try
        {
            var mode = AppSettings.EffectsMode.Normal;
            try { mode = AppSettings.EffectiveEffects(); } catch { }
            bool ultra = mode == AppSettings.EffectsMode.Maximum;
            var accent = ThemeService.CurrentAccent();
            // Тема могла смениться: свечение старого акцента не подходит — пересобираем.
            if (_pulseGlowAccent != accent)
            {
                _pulseGlow = null;
                _pulseGlowStatic = null;
                _pulseGlowAccent = accent;
            }
            if (ultra && _pulseGlow == null)
            {
                _pulseGlow = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = accent,
                    BlurRadius = 26, ShadowDepth = 0, Opacity = 0.55,
                };
            }
            if (!ultra && mode == AppSettings.EffectsMode.Normal && _pulseGlowStatic == null)
            {
                var e = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = accent,
                    BlurRadius = 18, ShadowDepth = 0, Opacity = 0.30,
                };
                e.Freeze();
                _pulseGlowStatic = e;
            }
            PulseGlowFrame.Effect = ultra ? _pulseGlow
                : mode == AppSettings.EffectsMode.Normal ? _pulseGlowStatic : null;
        }
        catch { }
    }

    private static string FirstLine(string s)
    {
        try
        {
            s = (s ?? "").Trim();
            int i = s.IndexOf('\n');
            return (i < 0 ? s : s[..i]).Trim();
        }
        catch { return ""; }
    }

    // ---- Пульс ----

    private PulseSnapshot? _pulse;

    private void RenderPulse()
    {
        PulseScore.Text = "…";
        PulseStatus.Text = "Считаю…";
        PulseSub.Text = "";
        PulseSpark.Points.Clear();
        PulseService.ComputeAsync().ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (t.IsFaulted || t.Result == null)
                    {
                        PulseScore.Text = "—";
                        PulseStatus.Text = "Не вышло проверить.";
                        return;
                    }
                    _pulse = t.Result;
                    var s = _pulse;
                    try { PulseService.AppendToHistory(s); } catch { }
                    PulseScore.Text = $"{s.Score}";
                    var (label, color) = s.Score >= 80
                        ? ("Отлично", Color.FromRgb(0x3F, 0xB9, 0x50))
                        : s.Score >= 50
                            ? ("Неплохо", Color.FromRgb(0xD2, 0x99, 0x22))
                            : ("Требует внимания", Color.FromRgb(0xF8, 0x51, 0x49));
                    PulseStatus.Text = label;
                    PulseStatus.Foreground = new SolidColorBrush(color);
                    // 🆕 0.14.3: светофор — и число тоже цветом состояния
                    // (спека: «Простой дом = светофор»), не только статус.
                    PulseScore.Foreground = new SolidColorBrush(color);
                    var parts = new[] {
                        ("диски", s.DiskScore), ("мусор", s.JunkScore),
                        ("обновления", s.UpdatesScore), ("ошибки", s.ErrorsScore),
                        ("приватность", s.PrivacyScore) };
                    var worst = parts.OrderBy(p => p.Item2).First();
                    PulseSub.Text = $"Слабее всего: {worst.Item1} ({worst.Item2}). Клик — разбор.";
                    DrawBeat();
                }
                catch { }
            });
        });
    }

    // ---- Кардиограмма: 12 разных ритмов вместо истории ----

    private int _beatIndex;
    private readonly System.Windows.Threading.DispatcherTimer _beatTimer =
        new() { Interval = TimeSpan.FromMilliseconds(2800) };

    // Параметры удара: P/Q/R/S/T — высоты зубцов, Alt — каждый второй удар
    // (бигеминия), Wobble — плавание изолинии, Noise — тремор
    // (детерминированный, без дрожания картинки).
    private sealed record BeatSpec(double P, double Q, double R, double S, double T,
        double Alt, double Wobble, double Noise);

    private static readonly List<double[]> PulseBeats = BuildBeats();

    private static double Gauss(double x, double c, double w)
    {
        try
        {
            double d = (x - c) / w;
            return Math.Exp(-d * d / 2);
        }
        catch { return 0; }
    }

    private static double[] BuildBeat(BeatSpec s, int n = 150)
    {
        var v = new double[n];
        for (int i = 0; i < n; i++)
        {
            double ph = (i / (double)n * 3.0) % 1.0; // 3 удара на полосу
            double amp = ((i / (n / 3)) % 2 == 0) ? 1.0 : s.Alt;
            double y = 0.5
                + amp * (s.P * Gauss(ph, 0.12, 0.05)
                    - s.Q * Gauss(ph, 0.28, 0.025)
                    + s.R * Gauss(ph, 0.33, 0.03)
                    - s.S * Gauss(ph, 0.38, 0.03)
                    + s.T * Gauss(ph, 0.60, 0.07))
                + s.Wobble * Math.Sin(i * 0.3);
            if (s.Noise > 0)
                y += (((i * 37) % 11) / 11.0 - 0.5) * s.Noise;
            v[i] = Math.Clamp(y, 0, 1);
        }
        return v;
    }

    private static List<double[]> BuildBeats()
    {
        var specs = new List<BeatSpec>
        {
            new(0.10, 0.08, 0.45, 0.25, 0.20, 1.00, 0.00, 0.00), // спокойный синус
            new(0.08, 0.06, 0.55, 0.15, 0.22, 1.00, 0.00, 0.00), // высокий R
            new(0.10, 0.12, 0.47, 0.42, 0.18, 1.00, 0.00, 0.00), // глубокий S
            new(0.10, 0.08, 0.45, 0.25, -0.22, 1.00, 0.00, 0.00), // инверсия T
            new(0.05, 0.03, 0.35, 0.10, 0.10, 1.00, 0.00, 0.00), // тихий
            new(0.07, 0.05, 0.35, 0.12, 0.12, 0.90, 0.00, 0.00), // частый мелкий
            new(0.10, 0.10, 0.55, 0.40, 0.35, 0.55, 0.00, 0.00), // экстрасистола
            new(0.10, 0.08, 0.45, 0.25, 0.20, 1.00, 0.08, 0.00), // плавающая изолиния
            new(0.10, 0.08, 0.45, 0.25, 0.20, 0.45, 0.00, 0.00), // бигеминия
            new(0.09, 0.07, 0.44, 0.22, 0.34, 1.00, 0.00, 0.00), // остроконечный T
            new(0.10, 0.20, 0.42, 0.25, 0.20, 1.00, 0.00, 0.00), // заметный Q
            new(0.10, 0.08, 0.45, 0.25, 0.20, 1.00, 0.00, 0.06), // тремор
        };
        var res = new List<double[]>(specs.Count);
        foreach (var s in specs) res.Add(BuildBeat(s));
        return res;
    }

    private void DrawBeat()
    {
        try
        {
            PulseSpark.Points.Clear();
            var v = PulseBeats[_beatIndex % PulseBeats.Count];
            const double w = 120, h = 36, pad = 3;
            for (int i = 0; i < v.Length; i++)
            {
                double x = i * (w / (v.Length - 1));
                double y = pad + (1 - Math.Clamp(v[i], 0, 1)) * (h - pad * 2);
                PulseSpark.Points.Add(new Point(x, y));
            }
        }
        catch { }
    }

    // Ротация ритмов — только в Максимуме и только на видимой Главной.
    private void BeatTick()
    {
        try
        {
            if (!IsVisible) return;
            bool ultra = false;
            try { ultra = AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Maximum; }
            catch { }
            if (!ultra) return;
            _beatIndex = (_beatIndex + 1) % PulseBeats.Count;
            DrawBeat();
        }
        catch { }
    }

    private void Pulse_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try { Report("Разбираю пульс…"); } catch { }
        PulseService.ComputeDetailedAsync().ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    _busy = false;
                    var owner = Window.GetWindow(this);
                    if (t.IsFaulted || t.Result == null)
                    {
                        try { Report("Не вышло разобрать."); } catch { }
                        return;
                    }
                    var d = t.Result;
                    _pulse = d.Snapshot;
                    PulseScore.Text = $"{d.Snapshot.Score}";
                    DrawBeat();
                    var lines = new List<string>
                    {
                        $"Пульс: {d.Snapshot.Score} из 100.",
                        DiskLine(d), JunkLine(d), UpdatesLine(d), ErrorsLine(d), PrivacyLine(d),
                    };
                    bool diskBad = d.DiskUsedPct >= 80 && d.DiskName.Length > 0;
                    if (diskBad)
                        ResultDialog.ShowResult(owner, "\uE9F5", "Пульс", string.Join("\n", lines),
                            "Освободить место", () =>
                            {
                                string r = DiskCleanupService.CleanTemp(24);
                                ActionJournal.AddInfo("Чистка из пульса", FirstLine(r));
                                return r;
                            });
                    else
                        ResultDialog.ShowResult(owner, "\uE9F5", "Пульс", string.Join("\n", lines));
                    try { Report("Готов"); } catch { }
                    Reload();
                }
                catch { _busy = false; }
            });
        });
    }

    private static string DiskLine(PulseDetails d) =>
        d.DiskName.Length == 0 ? "• Диски: не прочитались."
        : d.DiskUsedPct >= 90 ? $"• Диски — {d.Snapshot.DiskScore}: диск {d.DiskName} заполнен на {d.DiskUsedPct:F0}%!"
        : d.DiskUsedPct >= 80 ? $"• Диски — {d.Snapshot.DiskScore}: диск {d.DiskName} заполнен на {d.DiskUsedPct:F0}%."
        : $"• Диски — {d.Snapshot.DiskScore}: места хватает.";

    private static string JunkLine(PulseDetails d) =>
        d.TempBytes <= 0 ? $"• Мусор — {d.Snapshot.JunkScore}: чисто."
        : $"• Мусор — {d.Snapshot.JunkScore}: ~{DiskInfo.Gb(d.TempBytes)} ({d.TempCount} файлов).";

    private static string UpdatesLine(PulseDetails d) =>
        d.UpdateCount <= 0 ? $"• Обновления — {d.Snapshot.UpdatesScore}: программы свежие."
        : $"• Обновления — {d.Snapshot.UpdatesScore}: {d.UpdateCount} программ ждут.";

    private static string ErrorsLine(PulseDetails d) =>
        d.ErrorCount <= 0 ? $"• Ошибки — {d.Snapshot.ErrorsScore}: журналы чистые."
        : $"• Ошибки — {d.Snapshot.ErrorsScore}: {d.ErrorCount} свежих в журналах.";

    private static string PrivacyLine(PulseDetails d) =>
        d.PrivTotal <= 0 ? $"• Приватность — {d.Snapshot.PrivacyScore}: не прочиталась."
        : $"• Приватность — {d.Snapshot.PrivacyScore}: закрыто {d.PrivClosed} из {d.PrivTotal}.";

    private void Chat_Click(object sender, MouseButtonEventArgs e)
    {
        try { GoChat(); } catch { }
    }

    private void History_Click(object sender, MouseButtonEventArgs e)
    {
        try { GoHistory(); } catch { }
    }

    private void GoAdvanced_Click(object sender, MouseButtonEventArgs e)
    {
        try { GoAppearance(); } catch { }
    }

    // ---- Умная проверка: диск C, мусор, обновления → «Исправить всё» ----

    private void Smart_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try { Report("Проверяю всё…"); } catch { }
        Task.Run(() =>
        {
            double cFreePct = -1;
            try
            {
                var c = new DriveInfo("C");
                if (c.IsReady && c.TotalSize > 0)
                    cFreePct = 100.0 * c.AvailableFreeSpace / c.TotalSize;
            }
            catch { }
            var (tCount, tBytes) = DiskCleanupService.MeasureTemp(24);
            var (rCount, rBytes) = DiskCleanupService.RecycleBinInfo();
            // winget иногда висит — ждём не дольше 30с.
            int upCount = -1;
            try
            {
                if (WingetService.Available())
                {
                    var wt = Task.Run(() => WingetService.ListUpdates());
                    upCount = wt.Wait(TimeSpan.FromSeconds(30)) ? (wt.Result?.Count ?? 0) : -1;
                }
                else upCount = -2;
            }
            catch { }
            return (cFreePct, tCount, tBytes, rCount, rBytes, upCount);
        }).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    _busy = false;
                    var owner = Window.GetWindow(this);
                    if (t.IsFaulted)
                    {
                        try { Report("Не вышло проверить."); } catch { }
                        return;
                    }
                    var (cFreePct, tCount, tBytes, rCount, rBytes, upCount) = t.Result;
                    var lines = new List<string> { "Проверил компьютер." };
                    bool diskBad = cFreePct >= 0 && cFreePct < 15;
                    if (cFreePct >= 0)
                        lines.Add(diskBad
                            ? $"• Диск C: свободно {cFreePct:F0}% — маловато."
                            : $"• Диск C: свободно {cFreePct:F0}% — нормально.");
                    bool junkBig = tBytes > 10L * 1024 * 1024;
                    bool recBig = rBytes > 10L * 1024 * 1024;
                    if (junkBig)
                        lines.Add($"• Мусора: ~{DiskInfo.Gb(tBytes)} ({tCount} файлов).");
                    else if (tCount > 0)
                        lines.Add("• Мусора почти нет — мелочи.");
                    else
                        lines.Add("• Мусора нет.");
                    if (recBig)
                        lines.Add($"• В корзине: ~{DiskCleanupService.FormatSize(rBytes)} ({rCount} {DiskCleanupService.Plural(rCount, "файл", "файла", "файлов")}) — могу вынести.");
                    if (upCount > 0)
                        lines.Add($"• Обновлений программ: {upCount}.");
                    else if (upCount == 0)
                        lines.Add("• Программы свежие.");
                    bool hasWork = diskBad || junkBig || recBig || upCount > 0;
                    if (upCount < 0)
                        lines.Add("• Обновления проверить не вышло.");
                    string text = string.Join("\n", lines);
                    if (!hasWork)
                    {
                        ResultDialog.ShowResult(owner, "\uE9F5", "Проверка", text + "\nИсправлять нечего — всё хорошо!");
                        return;
                    }
                    ResultDialog.ShowResult(owner, "\uE9F5", "Проверка", text,
                        "Исправить всё", () =>
                        {
                            var done = new List<string>();
                            try
                            {
                                string c = DiskCleanupService.CleanTemp(24);
                                done.Add("Мусор: " + FirstLine(c));
                            }
                            catch (Exception ex) { done.Add("Мусор: не вышло (" + ex.Message + ")"); }
                            if (recBig)
                            {
                                try
                                {
                                    string rb = DiskCleanupService.EmptyRecycleBin();
                                    done.Add("Корзина: " + FirstLine(rb));
                                }
                                catch (Exception ex) { done.Add("Корзина: не вышло (" + ex.Message + ")"); }
                            }
                            bool exp = false;
                            try { exp = AppSettings.ExpOn(); } catch { }
                            if (exp && upCount > 0)
                            {
                                try
                                {
                                    string u = WingetService.UpgradeAll();
                                    done.Add("Обновления: " + FirstLine(u));
                                }
                                catch (Exception ex) { done.Add("Обновления: не вышло (" + ex.Message + ")"); }
                            }
                            else if (upCount > 0)
                                done.Add("Обновления пропустил: нужен Расширенный интерфейс и флаг «Экспериментальное».");
                            string res = "Сделал:\n" + string.Join("\n", done);
                            ActionJournal.AddInfo("Умная проверка", FirstLine(res));
                            return res;
                        });
                    try { Report("Готов"); } catch { }
                }
                catch { _busy = false; }
            });
        });
    }

    // ---- Корзина: замер → «вынести?» → готово ----

    private void UpdateRecycleSub()
    {
        try
        {
            var (count, bytes) = DiskCleanupService.RecycleBinInfo();
            UpdateRecycleSub(count, bytes);
        }
        catch { }
    }

    private void UpdateRecycleSub(int count, long bytes)
    {
        try
        {
            RecycleSub.Text = count <= 0 || bytes <= 0
                ? "Пусто — выносить нечего"
                : $"~{DiskCleanupService.FormatSize(bytes)} ({count} {DiskCleanupService.Plural(count, "файл", "файла", "файлов")}) — нажми, вынесу";
        }
        catch { }
    }

    private void Recycle_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try { Report("Смотрю корзину…"); } catch { }
        Task.Run(() => DiskCleanupService.RecycleBinInfo()).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    _busy = false;
                    var owner = Window.GetWindow(this);
                    if (t.IsFaulted)
                    {
                        try { Report("Не вышло посмотреть."); } catch { }
                        return;
                    }
                    var (count, bytes) = t.Result;
                    UpdateRecycleSub(count, bytes);
                    if (count <= 0 || bytes <= 0)
                    {
                        ResultDialog.ShowResult(owner, "\uE74D", "Корзина",
                            "Корзина пуста — выносить нечего. Так держать!");
                        return;
                    }
                    ResultDialog.ShowResult(owner, "\uE74D", "Корзина",
                        $"В корзине: ~{DiskCleanupService.FormatSize(bytes)} ({count} {DiskCleanupService.Plural(count, "файл", "файла", "файлов")}).\nВынесу без возврата — достать уже не выйдет.",
                        "Очистить", () =>
                        {
                            string r = DiskCleanupService.EmptyRecycleBin();
                            ActionJournal.AddInfo("Очистка корзины", FirstLine(r));
                            return r;
                        });
                    try { Report("Готов"); } catch { }
                    Reload();
                }
                catch { _busy = false; }
            });
        });
    }

    // ---- Цепочки ----

    private void Junk_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try { Report("Смотрю временные файлы…"); } catch { }
        Task.Run(() => DiskCleanupService.PreviewTemp(24)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    _busy = false;
                    var owner = Window.GetWindow(this);
                    if (t.IsFaulted)
                    {
                        try { Report("Не вышло посмотреть."); } catch { }
                        return;
                    }
                    string preview = t.Result ?? "";
                    if (preview.StartsWith("Ошибка"))
                    {
                        ResultDialog.ShowResult(owner, "\uE74D", "Уборка мусора", preview);
                        return;
                    }
                    if (preview.Contains("не найдено") || preview.Contains("нечего"))
                    {
                        ResultDialog.ShowResult(owner, "\uE74D", "Уборка мусора",
                            "Мусора нет — всё чисто. Так держать!");
                        return;
                    }
                    ResultDialog.ShowResult(owner, "\uE74D", "Уборка мусора", preview,
                        "Очистить", () =>
                        {
                            string r = DiskCleanupService.CleanTemp(24);
                            ActionJournal.AddInfo("Уборка мусора", FirstLine(r));
                            return r;
                        });
                    try { Report("Готов"); } catch { }
                }
                catch { _busy = false; }
            });
        });
    }

    private static long FreeOf(string name)
    {
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady && d.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                        return d.AvailableFreeSpace;
                }
                catch { }
            }
        }
        catch { }
        return -1;
    }

    private void Space_Click(object sender, MouseButtonEventArgs e) => SpaceChain();

    private void SpaceChain()
    {
        if (_busy) return;
        _busy = true;
        try { Report("Смотрю диски…"); } catch { }
        Task.Run(() => SystemMonitor.DiskReport()).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    _busy = false;
                    var owner = Window.GetWindow(this);
                    if (t.IsFaulted)
                    {
                        try { Report("Не вышло посмотреть диски."); } catch { }
                        return;
                    }
                    ResultDialog.ShowResult(owner, "\uE74E", "Место на дисках", t.Result ?? "",
                        "Освободить", () =>
                        {
                            long before = FreeOf("C");
                            string r = DiskCleanupService.CleanTemp(24);
                            long after = FreeOf("C");
                            string freed = before >= 0 && after >= before
                                ? $" На диске C стало свободнее на ~{DiskInfo.Gb(after - before)}."
                                : "";
                            string done = FirstLine(r) + freed;
                            ActionJournal.AddInfo("Освобождение места", done);
                            return r + freed;
                        });
                    try { Report("Готов"); } catch { }
                    Reload();
                }
                catch { _busy = false; }
            });
        });
    }

    private void Dupes_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        string dir = "";
        try { dir = FileService.Downloads(); } catch { }
        try { Report("Ищу одинаковые файлы…"); } catch { }
        Task.Run(() => DiskCleanupService.FindDuplicates(dir)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    _busy = false;
                    var owner = Window.GetWindow(this);
                    if (t.IsFaulted)
                    {
                        try { Report("Не вышло поискать."); } catch { }
                        return;
                    }
                    string found = t.Result ?? "";
                    if (found.StartsWith("Ошибка") || !found.Contains("лишних копий:"))
                    {
                        ResultDialog.ShowResult(owner, "\uE8C8", "Повторы", found);
                        return;
                    }
                    int dupes = 0;
                    string size = "";
                    try
                    {
                        var m1 = Regex.Match(found, @"лишних копий:\s*(\d+)");
                        if (m1.Success) dupes = int.Parse(m1.Groups[1].Value);
                        var m2 = Regex.Match(found, @"освободит:\s*~([^\.\n]+)");
                        if (m2.Success) size = m2.Groups[1].Value.Trim();
                    }
                    catch { }
                    ResultDialog.ShowResult(owner, "\uE8C8", "Повторы", found);
                    if (dupes <= 0) return;
                    string q = size.Length > 0
                        ? $"Убрать {dupes} лишних копий (~{size})?"
                        : $"Убрать {dupes} лишних копий?";
                    if (!ConfirmDialog.Ask(owner, q, "Оригиналы не трогаю — только копии."))
                        return;
                    _busy = true;
                    try { Report("Убираю копии…"); } catch { }
                    Task.Run(() => DiskCleanupService.DeleteDuplicates(dir)).ContinueWith(t2 =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                _busy = false;
                                string done = t2.IsFaulted
                                    ? $"Ошибка: {t2.Exception?.GetBaseException().Message}"
                                    : (t2.Result ?? "");
                                ResultDialog.ShowResult(owner, "\uE8C8", "Повторы", done);
                                ActionJournal.AddInfo("Уборка повторов", FirstLine(done));
                                try { Report("Готов"); } catch { }
                            }
                            catch { _busy = false; }
                        });
                    });
                }
                catch { _busy = false; }
            });
        });
    }
}
