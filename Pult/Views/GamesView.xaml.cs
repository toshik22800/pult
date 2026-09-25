using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pult.Services;

namespace Pult.Views;

public partial class GamesView : UserControl
{
    public Action<string> Report { get; set; } = _ => { };

    private List<Game> _all = new();
    private List<Game> _shown = new();
    private string _query = "";
    private string _sort = "name"; // name | size
    private bool _asc = true;
    private bool _busy;
    private bool _ultraCols;

    public GamesView()
    {
        InitializeComponent();
        try { _ultraCols = AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Maximum; }
        catch { }
        SizeChanged += (_, e) =>
        {
            int cols = e.NewSize.Width > 1100 ? 4 : e.NewSize.Width > 700 ? 3 : 2;
            // Ультра: карточки крупнее — на одну колонку меньше.
            // Флаг кэширован: чтение файла на каждый пиксель ресайза не делаем.
            if (_ultraCols) cols = Math.Max(2, cols - 1);
            CardsGrid.Columns = cols;
        };
        ApplySimpleMode();
        Refresh(false);
    }

    public void Refresh(bool force)
    {
        if (_busy) return;
        _busy = true;
        try { _ultraCols = AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Maximum; }
        catch { }
        SetStatus("Сканирую игры…");
        Task.Run(() => GameService.GetGames(force, AppSettings.Load().ExtraGameDirs)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                _busy = false;
                try
                {
                    _all = t.IsFaulted || t.Result == null ? new List<Game>() : t.Result;
                    Render();
                    SetStatus(_all.Count == 0 ? "Игры не найдены." : $"Найдено игр: {_all.Count}.");
                }
                catch (Exception ex)
                {
                    SetStatus($"Ошибка сканирования: {ex.Message}");
                }
            });
        });
    }

    // Простой интерфейс: без технического — прячем размеры, папки,
    // сортировку по размеру, подпись источника, бары и хиро-чипы. Остаются иконка,
    // имя, размер одной строкой и дружелюбная «Случайная».
    private static bool IsSimple()
    {
        try { return AppSettings.Load().Interface == AppSettings.InterfaceMode.Simple; }
        catch { return false; }
    }

    private bool ApplySimpleMode()
    {
        bool simple = IsSimple();
        try
        {
            var hide = simple ? Visibility.Collapsed : Visibility.Visible;
            SizesBtn.Visibility = hide;
            SizesGap.Visibility = hide;
            AddFolderBtn.Visibility = hide;
            AddGap.Visibility = hide;
            SortSizeBtn.Visibility = hide;
            HeroRow.Visibility = hide;
            EmptyHint.Text = simple
                ? "Нажми «Обновить» — найду твои игры."
                : "Нажми «Обновить» или добавь папку с играми кнопкой «+Папка».";
            if (simple && _sort == "size") { _sort = "name"; _asc = true; }
        }
        catch { }
        return simple;
    }

    // Хиро-статистика: ряд чипов под шапкой. Дёшево — один проход по _all.
    private void BuildHero(long totalBytes)
    {
        try
        {
            var tile = (Brush)FindResource("B_Tile");
            var line = (Brush)FindResource("B_Border");
            var text = (Brush)FindResource("B_Text");
            var muted = (Brush)FindResource("B_Muted");

            HeroRow.Children.Clear();
            HeroRow.Children.Add(MakeChip(tile, line, text, muted, "Игр:", _all.Count.ToString()));
            HeroRow.Children.Add(MakeChip(tile, line, text, muted, "Всего:",
                totalBytes > 0 ? GameService.FormatGb(totalBytes) : "—"));

            // Топ-3 источника по количеству, если источников больше — «и ещё k».
            var groups = _all.GroupBy(g => g.Source)
                .Select(x => new { Source = x.Key, Count = x.Count() })
                .OrderByDescending(x => x.Count)
                .ToList();
            foreach (var grp in groups.Take(3))
                HeroRow.Children.Add(MakeChip(tile, line, text, muted, grp.Source + ":", grp.Count.ToString()));
            int rest = groups.Count - 3;
            if (rest > 0)
                HeroRow.Children.Add(MakeChip(tile, line, text, muted, "", $"и ещё {rest}"));
        }
        catch { }
    }

    // Чип хиро-статистики: квадратный, B_Tile + 1px рамка, лейбл muted, число жирное.
    private Border MakeChip(Brush tile, Brush line, Brush text, Brush muted, string label, string value)
    {
        var chip = new Border
        {
            Background = tile,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (label.Length > 0)
        {
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = muted,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        row.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = text,
            Margin = new Thickness(label.Length > 0 ? 8 : 0, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        chip.Child = row;
        return chip;
    }

    private void Render()
    {
        bool simple = ApplySimpleMode();
        string q = (_query ?? "").Trim().ToLowerInvariant();
        IEnumerable<Game> seq = _all;
        if (q.Length > 0)
            seq = seq.Where(g => g.Name.ToLowerInvariant().Contains(q));
        seq = _sort == "size"
            ? (_asc
                ? seq.OrderBy(g => g.SizeBytes).ThenBy(g => g.Name)
                : seq.OrderByDescending(g => g.SizeBytes).ThenBy(g => g.Name))
            : (_asc
                ? seq.OrderBy(g => g.Name)
                : seq.OrderByDescending(g => g.Name));
        var shown = seq.ToList();
        _shown = shown;

        CountText.Text = $"{shown.Count} / {_all.Count}";
        long totalBytes = _all.Sum(g => g.SizeBytes);
        if (totalBytes > 0)
            CountText.Text += $" • {GameService.FormatGb(totalBytes)}";
        SortNameBtn.Content = _sort == "name" ? (_asc ? "Имя ▲" : "Имя ▼") : "Имя";
        SortSizeBtn.Content = _sort == "size" ? (_asc ? "Размер ▲" : "Размер ▼") : "Размер";
        long maxBytes = shown.Count > 0 ? shown.Max(g => g.SizeBytes) : 0;
        EmptyState.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        CardsGrid.Children.Clear();
        var text = (Brush)FindResource("B_Text");
        var muted = (Brush)FindResource("B_Muted");
        var accent = (Brush)FindResource("B_Accent");
        var dim = (Brush)FindResource("B_Dim");
        // Хиро-чипы — только Расширенный (в Простом HeroRow скрыт).
        if (!simple) BuildHero(totalBytes);

        foreach (var g in shown)
        {
            var card = new Border
            {
                Style = (Style)FindResource("ActionCard"),
                Margin = new Thickness(8),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = g,
                ToolTip = $"Запустить: {g.Name}\n{g.Launch}",
            };
            var root = new Grid();
            // Вотермарка для игр без обложки — пустого места не остаётся.
            string glyph = g.Source == "Steam" ? "\uE7FC" : "\uE8F4";
            var mark = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 72,
                Foreground = dim,
                Opacity = 0.12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0),
                IsHitTestVisible = false,
            };
            root.Children.Add(mark);
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // Баннер Steam-арта с затемнением снизу (появляется при загрузке).
            var artGrid = new Grid { Height = 150, Visibility = Visibility.Collapsed };
            var artBanner = new Image
            {
                Stretch = Stretch.UniformToFill,
                StretchDirection = StretchDirection.Both,
            };
            RenderOptions.SetBitmapScalingMode(artBanner, BitmapScalingMode.HighQuality);
            artGrid.Children.Add(artBanner);
            var fadeBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.55),
                EndPoint = new Point(0, 1),
            };
            fadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
            fadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(115, 0, 0, 0), 1));
            fadeBrush.Freeze();
            artGrid.Children.Add(new Border
            {
                Background = fadeBrush,
                IsHitTestVisible = false,
            });
            // Тонкий акцентный блик сверху баннера (10% — лёгкий, без тяжести).
            Color topColor = accent is SolidColorBrush accBrush ? accBrush.Color
                : ThemeService.CurrentAccent();
            var topTint = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
            };
            topTint.GradientStops.Add(new GradientStop(topColor, 0));
            topTint.GradientStops.Add(new GradientStop(
                Color.FromArgb(0, topColor.R, topColor.G, topColor.B), 1));
            topTint.Freeze();
            artGrid.Children.Add(new Border
            {
                Background = topTint,
                Opacity = 0.10,
                Height = 44,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            });
            stack.Children.Add(artGrid);
            var body = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Квадратная иконка exe — только если нет баннера.
            var artIcon = new Image
            {
                Width = 40, Height = 40,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Visibility = Visibility.Collapsed,
            };
            RenderOptions.SetBitmapScalingMode(artIcon, BitmapScalingMode.HighQuality);
            Grid.SetColumn(artIcon, 0);
            head.Children.Add(artIcon);

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 26,
                Foreground = g.Source == "Steam" ? accent : dim,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            Grid.SetColumn(icon, 0);
            head.Children.Add(icon);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock
            {
                Text = g.Name,
                FontSize = 11,
                Foreground = text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = g.Name,
            };
            name.SetResourceReference(TextBlock.FontFamilyProperty, "F_Ui");
            texts.Children.Add(name);
            string sizeStr = g.SizeBytes > 0 ? GameService.FormatGb(g.SizeBytes) : "—";
            if (simple)
            {
                texts.Children.Add(new TextBlock
                {
                    Text = sizeStr,
                    FontSize = 11,
                    Foreground = muted,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
            else
            {
                // Строка-источник: квадратик 8×8 + название + размер, всё в одной строке.
                var srcRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 0),
                };
                srcRow.Children.Add(new Border
                {
                    Width = 8,
                    Height = 8,
                    Background = g.Source == "Steam" ? accent : dim,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                });
                srcRow.Children.Add(new TextBlock
                {
                    Text = g.Source,
                    FontSize = 11,
                    Foreground = muted,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                srcRow.Children.Add(new TextBlock
                {
                    Text = $" • {sizeStr}",
                    FontSize = 11,
                    Foreground = muted,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                texts.Children.Add(srcRow);
            }
            Grid.SetColumn(texts, 1);
            head.Children.Add(texts);
            body.Children.Add(head);

            // Бар доли от самой большой игры + подпись размера — только в Расширенном.
            if (!simple && maxBytes > 0 && g.SizeBytes > 0)
            {
                double frac = System.Math.Max(0.03, (double)g.SizeBytes / maxBytes);
                int pct = (int)System.Math.Round(g.SizeBytes * 100.0 / maxBytes);
                var barRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                barRow.ColumnDefinitions.Add(new ColumnDefinition
                    { Width = new GridLength(1, GridUnitType.Star) });
                barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var barGrid = new Grid
                {
                    Height = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                barGrid.ColumnDefinitions.Add(new ColumnDefinition
                    { Width = new GridLength(frac, GridUnitType.Star) });
                barGrid.ColumnDefinitions.Add(new ColumnDefinition
                    { Width = new GridLength(1 - frac, GridUnitType.Star) });
                var track = new Border
                {
                    // Ресурсная кисть вместо застывшего тёмного hex —
                    // на светлой теме трек-полоска тоже переключается.
                    Background = (Brush)FindResource("B_Soft"),
                };
                Grid.SetColumn(track, 0);
                Grid.SetColumnSpan(track, 2);
                barGrid.Children.Add(track);
                // Градиент заливки: акцент → его же осветлённый тинт
                // (раньше застывший перивинкл не переключался с темой).
                Color accColor = accent is SolidColorBrush acc ? acc.Color
                    : ThemeService.CurrentAccent();
                var fillBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5),
                };
                fillBrush.GradientStops.Add(new GradientStop(accColor, 0));
                fillBrush.GradientStops.Add(new GradientStop(ThemeService.Tint(accColor, 0.42), 1));
                fillBrush.Freeze();
                var fill = new Border { Background = fillBrush };
                Grid.SetColumn(fill, 0);
                barGrid.Children.Add(fill);
                Grid.SetColumn(barGrid, 0);
                barRow.Children.Add(barGrid);
                var barLabel = new TextBlock
                {
                    Text = $"{GameService.FormatGb(g.SizeBytes)} • {pct}%",
                    FontSize = 10,
                    Foreground = muted,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(barLabel, 1);
                barRow.Children.Add(barLabel);
                body.Children.Add(barRow);
            }

            stack.Children.Add(body);
            root.Children.Add(stack);
            card.Child = root;
            card.MouseLeftButtonUp += (s, _) =>
            {
                if (s is Border b && b.Tag is Game gm) RunGame(gm);
            };
            CardsGrid.Children.Add(card);
            // Арт — фоном: байты читаем в пуле, битмап морозим, ставим в UI.
            // Карточка могла уже пересоздаться — проверяем, что она ещё в сетке.
            Task.Run(() => GameArt.GetDisplayArt(g)).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (t.IsFaulted) return;
                        var (path, banner) = t.Result;
                        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                        if (!CardsGrid.Children.Contains(card)) return;
                        var bmp = new BitmapImage();
                        using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                        {
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = banner ? 560 : 96;
                            bmp.StreamSource = ms;
                            bmp.EndInit();
                        }
                        bmp.Freeze();
                        mark.Visibility = Visibility.Collapsed;
                        // С баннером контент прижат кверху, без — по центру карточки.
                        stack.VerticalAlignment = banner ? VerticalAlignment.Top : VerticalAlignment.Center;
                        if (banner)
                        {
                            artBanner.Source = bmp;
                            artGrid.Visibility = Visibility.Visible;
                            artIcon.Visibility = Visibility.Collapsed;
                            icon.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            artIcon.Source = bmp;
                            artIcon.Visibility = Visibility.Visible;
                            icon.Visibility = Visibility.Collapsed;
                        }
                    }
                    catch { }
                });
            });
        }
    }

    private void RunGame(Game g)
    {
        SetStatus($"Запускаю: {g.Name}…");
        Task.Run(() => GameService.Launch(g)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() => SetStatus(t.IsFaulted ? $"Ошибка запуска {g.Name}" : (t.Result ?? "Готово")));
        });
    }

    private void Random_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_shown.Count == 0)
            {
                SetStatus("Не из чего выбирать — список пуст.");
                return;
            }
            var g = _shown[Random.Shared.Next(_shown.Count)];
            var owner = Window.GetWindow(this);
            if (!Pult.ConfirmDialog.Ask(owner, $"Запустить «{g.Name}»?",
                "Случайный выбор. Отменить запуск уже не выйдет."))
                return;
            RunGame(g);
        }
        catch (Exception ex)
        {
            SetStatus($"Не вышло: {ex.Message}");
        }
    }

    private void SetStatus(string s)
    {
        StatusText.Text = s;
        try { Report(s); } catch { }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh(true);

    private void Sizes_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _all.Count == 0) return;
        _busy = true;
        SetStatus("Считаю размеры…");
        var snapshot = new List<Game>(_all);
        Task.Run(() => GameService.GetSizes(snapshot)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                _busy = false;
                try
                {
                    if (!t.IsFaulted && t.Result != null) _all = t.Result;
                    Render();
                    SetStatus("Размеры обновлены.");
                }
                catch (Exception ex)
                {
                    SetStatus($"Ошибка подсчёта: {ex.Message}");
                }
            });
        });
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка с играми" };
        if (dlg.ShowDialog() != true) return;
        string folder = dlg.FolderName;
        try
        {
            var s = AppSettings.Load();
            if (!s.ExtraGameDirs.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                s.ExtraGameDirs.Add(folder);
                s.Save();
            }
        }
        catch { }
        _busy = true;
        SetStatus("Сканирую папку…");
        Task.Run(() => GameService.ScanSingleFolder(folder)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                _busy = false;
                try
                {
                    var added = t.IsFaulted || t.Result == null ? new List<Game>() : t.Result;
                    if (added.Count > 0)
                    {
                        var seen = new HashSet<string>(_all.Select(g => GameService.Normalize(g.Name)));
                        foreach (var g in added)
                        {
                            string key = GameService.Normalize(g.Name);
                            if (key.Length == 0 || !seen.Add(key)) continue;
                            _all.Add(g);
                        }
                        _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                        GameService.SaveCache(_all);
                    }
                    Render();
                    SetStatus(added.Count == 0 ? "В папке игр не найдено." : $"Добавлено: {added.Count}.");
                }
                catch (Exception ex)
                {
                    SetStatus($"Ошибка: {ex.Message}");
                }
            });
        });
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text ?? "";
        Render();
    }

    private void SortName_Click(object sender, RoutedEventArgs e)
    {
        if (_sort == "name") _asc = !_asc;
        else { _sort = "name"; _asc = true; }
        Render();
    }

    private void SortSize_Click(object sender, RoutedEventArgs e)
    {
        if (_sort == "size") _asc = !_asc;
        else { _sort = "size"; _asc = false; }
        Render();
    }
}
