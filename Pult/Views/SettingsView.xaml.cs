using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pult.Services;

namespace Pult.Views;

public partial class SettingsView : UserControl
{
    private AppSettings _s = AppSettings.Load();

    public SettingsView()
    {
        InitializeComponent();
        Reload();
        // 🆕 0.14.1 (фидбек: «блики статичные»): оживляем полосы-разделители
        // (11 штук): дыхание + бегущий свет со стаггером. Хук в ctor — в
        // headless-рендере стенда Loaded не стреляет, анимации должны
        // навешиваться и там (старт за краем → пиксели рендера не меняются).
        try { SheenFx.AnimateBars(this); } catch { }
        try
        {
            string ver = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "—";
            AppVersionText.Text = $"Пульт, версия {ver}";
            DataPathText.Text = $"Данные: {DataDir()}";
        }
        catch { }
    }

    public void Reload()
    {
        _s = AppSettings.Load();
        ProviderBox.SelectedIndex = _s.Llm.Type == "ollama" ? 0 : 1;
        BaseUrlBox.Text = _s.Llm.BaseUrl;
        ApiKeyBox.Text = _s.Llm.ApiKey;
        ModelBox.Text = _s.Llm.Model;
        ExpBox.Checked -= ExpBox_Checked;
        ExpBox.IsChecked = _s.ExperimentalEnabled;
        ExpBox.Checked += ExpBox_Checked;
        ExpConfirm.Visibility = Visibility.Collapsed;
        AutostartBox.Checked -= Autostart_Changed;
        AutostartBox.IsChecked = IsAutostartOn();
        AutostartBox.Checked += Autostart_Changed;
        RefreshModeCards();
        RefreshThemeCards();
        // 🆕 0.14.0: пилюли текущих шрифта и масштаба.
        try { RefreshFontChips(); RefreshScaleChips(); } catch { }
        RefreshExpCard();
        AutoOrgBox.Checked -= AutoOrg_Changed;
        AutoOrgBox.IsChecked = _s.AutoOrganizeEnabled;
        AutoOrgBox.Checked += AutoOrg_Changed;
        IntervalBox.SelectedIndex = _s.AutoOrganizeIntervalMin switch
        {
            15 => 0, 30 => 1, 120 => 3, 240 => 4, _ => 2,
        };
        DirsList.ItemsSource = null;
        DirsList.ItemsSource = _s.ExtraGameDirs;
        OrgDirsList.ItemsSource = null;
        OrgDirsList.ItemsSource = _s.AutoOrganizeDirs;
        StrictPrivacyBox.Checked -= StrictPrivacy_Changed;
        StrictPrivacyBox.Unchecked -= StrictPrivacy_Changed;
        StrictPrivacyBox.IsChecked = _s.StrictPrivacyMode;
        StrictPrivacyBox.Checked += StrictPrivacy_Changed;
        StrictPrivacyBox.Unchecked += StrictPrivacy_Changed;
        // Простой интерфейс: без технических полей.
        try
        {
            var tech = _s.Interface == AppSettings.InterfaceMode.Simple
                ? Visibility.Collapsed : Visibility.Visible;
            BaseUrlLabel.Visibility = tech;
            BaseUrlBox.Visibility = tech;
            ApiKeyLabel.Visibility = tech;
            ApiKeyBox.Visibility = tech;
        }
        catch { }
        // Чат без флага выключен — прячем и его настройки (карточка модели).
        try { LlmCard.Visibility = AppSettings.ExpOn() ? Visibility.Visible : Visibility.Collapsed; }
        catch { }
        RefreshStatusPill();
    }

    // Проскроллить к блоку «Вид» (зовёт Простой дом).
    public void ScrollToAppearance()
    {
        try { AppearanceCard.BringIntoView(); } catch { }
    }

    private void RefreshStatusPill()
    {
        try
        {
            string model = string.IsNullOrWhiteSpace(_s.Llm.Model) ? "—" : _s.Llm.Model.Trim();
            if (model.Length > 28) model = model[..28] + "…";
            string exp = _s.ExperimentalEnabled ? "ВКЛ" : "ВЫКЛ";
            StatusPillText.Text = $"МОДЕЛЬ: {model} • ЭКСП: {exp}";
        }
        catch { }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static bool IsAutostartOn()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Pult") != null;
        }
        catch { return false; }
    }

    private static string ExeCmd()
    {
        string exe = Environment.ProcessPath ?? "";
        return $"\"{exe}\"";
    }

    private static string DataDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pult");

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (AutostartBox.IsChecked == true) k.SetValue("Pult", ExeCmd());
            else k.DeleteValue("Pult", false);
        }
        catch { }
    }

    private void RefreshModeCards()
    {
        try
        {
            int fx = (int)_s.Effects;
            StyleModeCard(FxAutoBtn, FxAutoPill, fx == 0);
            StyleModeCard(FxMinBtn, FxMinPill, fx == 1);
            StyleModeCard(FxNormBtn, FxNormPill, fx == 2);
            StyleModeCard(FxMaxBtn, FxMaxPill, fx == 3);
            try
            {
                // 🆕 0.14.2: пороги Auto названы явно (баг-репорт 0.14.1 —
                // «задокументировать критерии»). Формулировка согласована с
                // AppSettings.DecideEffects и со спекой стенда `auto`.
                FxAutoDesc.Text = "Сейчас: " + (AppSettings.DetectEffects() switch
                {
                    AppSettings.EffectsMode.Minimum => "минимум",
                    AppSettings.EffectsMode.Maximum => "максимум",
                    _ => "обычные",
                }) + ". Пороги: слабое железо/запрет анимаций — минимум, батарея — обычные, иначе максимум";
            }
            catch { }
            int ui = (int)_s.Interface;
            StyleModeCard(UiSimpleBtn, UiSimplePill, ui == 0);
            StyleModeCard(UiAdvBtn, UiAdvPill, ui == 1);
            // 🆕 0.14.3: полосы перекрашиваются при каждой пересборке режимов
            // (смена Эффектов меняет и вид полос: Минимум — прозрачные).
            PaintStrips();
        }
        catch { }
    }

    private void StyleModeCard(Button b, TextBlock pill, bool selected)
    {
        b.BorderBrush = (Brush)FindResource(selected ? "B_Accent" : "B_Border");
        b.Background = (Brush)FindResource(selected ? "B_Soft" : "B_Tile");
        pill.Text = selected ? "●" : "○";
        pill.Foreground = (Brush)FindResource(selected ? "B_Accent" : "B_Muted");
    }

    // 🆕 0.14.3 (фидбек: «тонкие полоски — декор для галочки»): в обычном
    // режиме полосы карточек — тихие структурные разделители темы, а у
    // карточки LLM полоса несёт состояние подключения (зел/красн). В
    // Минимуме — снова B_Bar (прозрачен): fxswitch ищет полосу именно с
    // кистью B_Bar, а settingsTall_min проверяет их угасание.
    private Brush? _llmState; // null — проверки ещё не было

    private static readonly Brush OkStrip = Frozen(0x3F, 0xB9, 0x50);
    private static readonly Brush ErrStrip = Frozen(0xF8, 0x51, 0x49);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var s = new SolidColorBrush(Color.FromRgb(r, g, b));
        s.Freeze();
        return s;
    }

    private void PaintStrips()
    {
        try
        {
            bool min = AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Minimum;
            foreach (var strip in StripWalk())
            {
                if (min) strip.SetResourceReference(Border.BackgroundProperty, "B_Bar");
                else if (strip == LlmStrip && _llmState != null)
                    strip.Background = _llmState;
                else strip.SetResourceReference(Border.BackgroundProperty, "B_Border");
            }
        }
        catch { }
    }

    // Все полосы-разделители (Height=2, Width=200) — как их ищет SheenFx.
    private List<Border> StripWalk()
    {
        var list = new List<Border>();
        void Walk(DependencyObject d)
        {
            if (d is null) return;
            if (d is Border b && b.Height == 2 && !double.IsNaN(b.Width)
                && Math.Abs(b.Width - 200) < 0.5 && !list.Contains(b)) list.Add(b);
            int n = 0;
            try { n = VisualTreeHelper.GetChildrenCount(d); } catch { }
            for (int k = 0; k < n; k++) Walk(VisualTreeHelper.GetChild(d, k));
            if (d is FrameworkElement fe)
                foreach (var c in LogicalTreeHelper.GetChildren(fe))
                    if (c is DependencyObject cd) Walk(cd);
        }
        Walk(this);
        return list;
    }

    // Пресеты и свои цвета темы: применяются сразу, без перезапуска.
    private void RefreshThemeCards()
    {
        try
        {
            _s = AppSettings.Load();
            AccentBox.Text = _s.AccentColor;
            BgBox.Text = _s.BackgroundColor;
            PanelBox.Text = _s.PanelColor;
            var presets = ThemeService.Presets;
            var btns = new[] { Theme0Btn, Theme1Btn, Theme2Btn, Theme3Btn, Theme4Btn };
            var sw = new[] { Theme0Swatch, Theme1Swatch, Theme2Swatch, Theme3Swatch, Theme4Swatch };
            var pills = new[] { Theme0Pill, Theme1Pill, Theme2Pill, Theme3Pill, Theme4Pill };
            for (int i = 0; i < btns.Length && i < presets.Length; i++)
            {
                sw[i].Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(presets[i].Accent));
                bool sel = AppSettings.NormHex(_s.AccentColor, "#7C6CF0")
                               == AppSettings.NormHex(presets[i].Accent, "#7C6CF0")
                           && AppSettings.NormHex(_s.BackgroundColor, "#0A0E1A")
                               == AppSettings.NormHex(presets[i].Bg, "#0A0E1A")
                           && AppSettings.NormHex(_s.PanelColor, "#0E1424")
                               == AppSettings.NormHex(presets[i].Panel, "#0E1424");
                btns[i].BorderBrush = (Brush)FindResource(sel ? "B_Accent" : "B_Border");
                btns[i].Background = (Brush)FindResource(sel ? "B_Soft" : "B_Tile");
                pills[i].Foreground = (Brush)FindResource(sel ? "B_Text" : "B_Muted");
            }
        }
        catch { }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        if (tag.Length != 3 || tag[0] != 't' || tag[1] != 'h'
            || tag[2] < '0' || tag[2] > '4') return;
        try
        {
            var p = ThemeService.Presets[tag[2] - '0'];
            ThemeService.ApplyPreset(p);
            RefreshThemeCards();
            ThemeStatus.Text = $"Готово: пресет «{p.Name}» применён.";
        }
        catch (Exception ex) { ThemeStatus.Text = "Ошибка: " + ex.Message; }
    }

    private void ThemeApply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool ok = AppSettings.NormHex(AccentBox.Text, "") != ""
                       && AppSettings.NormHex(BgBox.Text, "") != ""
                       && AppSettings.NormHex(PanelBox.Text, "") != "";
            if (!ok)
            {
                ThemeStatus.Text = "Цвет — в формате #RRGGBB, например #7C6CF0.";
                return;
            }
            ThemeService.ApplyCustom(AccentBox.Text, BgBox.Text, PanelBox.Text);
            RefreshThemeCards();
            ThemeStatus.Text = "Готово: свои цвета применены.";
        }
        catch (Exception ex) { ThemeStatus.Text = "Ошибка: " + ex.Message; }
    }

    private void FxCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        if (tag.Length != 3 || tag[0] != 'f' || tag[1] != 'x'
            || tag[2] < '0' || tag[2] > '3') return;
        try
        {
            _s.Effects = (AppSettings.EffectsMode)(tag[2] - '0');
            _s.Save();
            // 🆕 0.14.0: плоские токены Минимума живут в ресурсах палитры —
            // перекрасить сразу при смене режима эффектов.
            ThemeService.Apply();
            RefreshModeCards();
            try
            {
                if (Window.GetWindow(this) is MainWindow mw) mw.ApplyAppearance();
            }
            catch { }
            AppLog.Info("Эффекты: " + _s.Effects);
        }
        catch { }
    }

    private void UiCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        if (tag.Length != 3 || tag[0] != 'u' || tag[1] != 'i'
            || (tag[2] != '0' && tag[2] != '1')) return;
        try
        {
            _s.Interface = (AppSettings.InterfaceMode)(tag[2] - '0');
            // Уход в Простой гасит флаг: залипшее «включено» без видимой
            // галочки и управления — нельзя.
            if (_s.Interface == AppSettings.InterfaceMode.Simple && _s.ExperimentalEnabled)
            {
                _s.ExperimentalEnabled = false;
                ExpBox.Checked -= ExpBox_Checked;
                ExpBox.Unchecked -= ExpBox_Unchecked;
                ExpBox.IsChecked = false;
                ExpBox.Checked += ExpBox_Checked;
                ExpBox.Unchecked += ExpBox_Unchecked;
                ExpConfirm.Visibility = Visibility.Collapsed;
            }
            _s.Save();
            RefreshModeCards();
            RefreshExpCard();
            Reload();
            try
            {
                if (Window.GetWindow(this) is MainWindow mw) mw.ApplyAppearance();
            }
            catch { }
            AppLog.Info("Интерфейс: " + _s.Interface);
        }
        catch { }
    }

    // 🆕 0.14.0: шрифт интерфейса (Вид → Шрифт). Ресурс F_Ui меняется
    // живьём через DynamicResource — перерисовка вся, без перезапуска.
    private void Font_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        if (tag.Length != 5 || !tag.StartsWith("font", StringComparison.Ordinal)) return;
        if (tag[4] < '0' || tag[4] > '3') return;
        try
        {
            // 🆕 0.14.1: тег fontN — это ИНДЕКС чипа, а не ключ шрифта.
            // 0.14.0 писал в FontKey "0".."3": ApplyFont таких ключей не
            // знал и всегда ставил Segoe UI — шрифт «не менялся вообще
            // никак», а RefreshFontChips сравнивал с ключами и не зажигал
            // пилюлю. Ключ берём из каталога Fonts (индексы совпадают с
            // порядком чипов font0..font3: retro, segoe, bahnschrift,
            // trebuchet).
            _s.FontKey = Appearance.Fonts[tag[4] - '0'].Key;
            _s.Save();
            Appearance.ApplyFont(_s.FontKey);
            RefreshFontChips();
            FontStatus.Text = "Готово: шрифт применён.";
            AppLog.Info("Шрифт: " + _s.FontKey);
        }
        catch (Exception ex) { FontStatus.Text = "Ошибка: " + ex.Message; }
    }

    // 🆕 0.14.0: масштаб интерфейса (Вид → Масштаб): тег scNNN = проценты.
    // Зажат Appearance.SetScale в 0.75..1.75 — «осторожно» по контракту.
    private void Scale_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        if (tag.Length < 4 || !tag.StartsWith("sc", StringComparison.Ordinal)) return;
        if (!int.TryParse(tag[2..], out var percent)) return;
        try
        {
            _s.UiScale = percent / 100.0;
            _s.Save();
            Appearance.SetScale(_s.UiScale);
            RefreshScaleChips();
            ScaleStatus.Text = "Готово: масштаб " + percent + "%.";
            AppLog.Info("Масштаб: " + percent + "%");
        }
        catch (Exception ex) { ScaleStatus.Text = "Ошибка: " + ex.Message; }
    }

    private void RefreshFontChips()
    {
        try
        {
            var k = string.IsNullOrWhiteSpace(_s.FontKey) ? "segoe" : _s.FontKey;
            Font0Pill.Text = k == "retro" ? "●" : "○";
            Font1Pill.Text = k == "segoe" ? "●" : "○";
            Font2Pill.Text = k == "bahnschrift" ? "●" : "○";
            Font3Pill.Text = k == "trebuchet" ? "●" : "○";
        }
        catch { }
    }

    private void RefreshScaleChips()
    {
        try
        {
            var pct = (int)Math.Round((_s.UiScale <= 0 ? 1 : _s.UiScale) * 100);
            Sc75Pill.Text = pct == 75 ? "●" : "○";
            Sc80Pill.Text = pct == 80 ? "●" : "○";
            Sc85Pill.Text = pct == 85 ? "●" : "○";
            Sc90Pill.Text = pct == 90 ? "●" : "○";
            Sc100Pill.Text = pct == 100 ? "●" : "○";
            Sc110Pill.Text = pct == 110 ? "●" : "○";
            Sc125Pill.Text = pct == 125 ? "●" : "○";
            Sc150Pill.Text = pct == 150 ? "●" : "○";
        }
        catch { }
    }

    private void StrictPrivacy_Changed(object sender, RoutedEventArgs e)
    {
        try
        {
            _s.StrictPrivacyMode = StrictPrivacyBox.IsChecked == true;
            _s.Save();
            if (_s.StrictPrivacyMode)
            {
                // Сразу стираем прошлое, не ждём выхода.
                try { System.IO.File.Delete(ChatView.HistoryPath()); } catch { }
            }
            AppLog.Info("Строгая приватность: " + (_s.StrictPrivacyMode ? "вкл" : "выкл"));
        }
        catch { }
    }

    private void ModelReset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ProviderBox.SelectedIndex = 0;
            BaseUrlBox.Text = "http://127.0.0.1:11434/v1";
            ApiKeyBox.Text = "ollama";
            ModelBox.Text = "qwen2.5:7b";
            _s.Llm.Type = "ollama";
            _s.Llm.BaseUrl = BaseUrlBox.Text.Trim();
            _s.Llm.ApiKey = ApiKeyBox.Text.Trim();
            _s.Llm.Model = ModelBox.Text.Trim();
            _s.Save();
            RefreshStatusPill();
            LlmStatus.Text = "Сброшено к Ollama.";
            _llmState = null; PaintStrips(); // 🆕 0.14.3: статус сброшен
        }
        catch { }
    }

    private void HistoryOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Window.GetWindow(this) is MainWindow mw) mw.GoTo("history");
        }
        catch { }
    }

    private void AutoOrg_Changed(object sender, RoutedEventArgs e)
    {
        _s.AutoOrganizeEnabled = AutoOrgBox.IsChecked == true;
        _s.Save();
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalBox.SelectedIndex < 0) return;
        _s.AutoOrganizeIntervalMin = IntervalBox.SelectedIndex switch
        {
            0 => 15, 1 => 30, 3 => 120, 4 => 240, _ => 60,
        };
        _s.Save();
    }

    private void ProviderBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedIndex == 0)
        {
            BaseUrlBox.Text = "http://127.0.0.1:11434/v1";
            if (ApiKeyBox.Text == "") ApiKeyBox.Text = "ollama";
        }
        else
        {
            if (BaseUrlBox.Text.Contains("127.0.0.1")) BaseUrlBox.Text = "https://api.openai.com/v1";
            if (ApiKeyBox.Text == "ollama") ApiKeyBox.Text = "";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _s.Llm.Type = ProviderBox.SelectedIndex == 0 ? "ollama" : "openai_compatible";
        _s.Llm.BaseUrl = BaseUrlBox.Text.Trim();
        _s.Llm.ApiKey = ApiKeyBox.Text.Trim();
        _s.Llm.Model = ModelBox.Text.Trim();
        _s.Save();
        RefreshStatusPill();
    }

    private async void TestLlm_Click(object sender, RoutedEventArgs e)
    {
        TestLlmBtn.IsEnabled = false;
        LlmStatus.Text = "Проверка…";
        _llmState = null; PaintStrips(); // 🆕 0.14.3: на время проверки — тихо
        try
        {
            var tmp = new AppSettings
            {
                Llm = new LlmSettings
                {
                    Type = ProviderBox.SelectedIndex == 0 ? "ollama" : "openai_compatible",
                    BaseUrl = BaseUrlBox.Text.Trim(),
                    ApiKey = ApiKeyBox.Text.Trim(),
                    Model = ModelBox.Text.Trim(),
                },
            };
            using var client = new LlmClient(tmp);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (ok, msg) = await client.TestConnectionAsync(cts.Token);
            string m = (msg ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
            if (m.Length > 200) m = m[..200] + "…";
            LlmStatus.Text = ok ? $"ОК: {m}" : $"Ошибка: {m}";
            _llmState = ok ? OkStrip : ErrStrip; PaintStrips(); // 🆕 0.14.3
        }
        catch (Exception ex)
        {
            string m = (ex.Message ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
            if (m.Length > 200) m = m[..200] + "…";
            LlmStatus.Text = $"Ошибка: {m}";
            _llmState = ErrStrip; PaintStrips(); // 🆕 0.14.3
        }
        finally
        {
            TestLlmBtn.IsEnabled = true;
        }
    }

    // Экспериментальное — только в расширенном интерфейсе.
    private void RefreshExpCard()
    {
        try
        {
            bool adv = _s.Interface == AppSettings.InterfaceMode.Advanced;
            ExpBox.Visibility = adv ? Visibility.Visible : Visibility.Collapsed;
            ExpBox.IsEnabled = adv;
            ExpHint.Text = adv
                ? "Удаление файлов, очистка Temp, завершение процессов, чат с ИИ. Могут глючить."
                : "Нужно больше? Включи «Расширенный» интерфейс выше — там фишки, опасные кнопки и чат с ИИ.";
            if (!adv) ExpConfirm.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void ExpBox_Checked(object sender, RoutedEventArgs e)
    {
        ExpConfirm.Visibility = Visibility.Visible;
    }

    private void ExpBox_Unchecked(object sender, RoutedEventArgs e)
    {
        _s.ExperimentalEnabled = false;
        _s.Save();
        ExpConfirm.Visibility = Visibility.Collapsed;
        RefreshStatusPill();
        Reload();
        try
        {
            if (Window.GetWindow(this) is MainWindow mw) mw.ApplyAppearance();
        }
        catch { }
    }

    private void ExpText_Changed(object sender, TextChangedEventArgs e)
    {
        ExpOk.IsEnabled = ExpText.Text.Trim().ToUpperInvariant() == "ВКЛЮЧИТЬ";
    }

    private void ExpOk_Click(object sender, RoutedEventArgs e)
    {
        _s.ExperimentalEnabled = true;
        _s.Save();
        ExpConfirm.Visibility = Visibility.Collapsed;
        ExpText.Text = "";
        ExpBox.Checked -= ExpBox_Checked;
        ExpBox.IsChecked = true;
        ExpBox.Checked += ExpBox_Checked;
        RefreshStatusPill();
        Reload();
        try
        {
            if (Window.GetWindow(this) is MainWindow mw) mw.ApplyAppearance();
        }
        catch { }
    }

    private void AddDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка с играми" };
        if (dlg.ShowDialog() != true) return;
        if (!_s.ExtraGameDirs.Contains(dlg.FolderName))
        {
            _s.ExtraGameDirs.Add(dlg.FolderName);
            _s.Save();
            DirsList.ItemsSource = null;
            DirsList.ItemsSource = _s.ExtraGameDirs;
        }
    }

    private void RemoveDir_Click(object sender, RoutedEventArgs e)
    {
        if (DirsList.SelectedItem is string d)
        {
            _s.ExtraGameDirs.Remove(d);
            _s.Save();
            DirsList.ItemsSource = null;
            DirsList.ItemsSource = _s.ExtraGameDirs;
        }
    }

    private void OrgAddDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка для автопорядка" };
        if (dlg.ShowDialog() != true) return;
        if (!_s.AutoOrganizeDirs.Contains(dlg.FolderName))
        {
            _s.AutoOrganizeDirs.Add(dlg.FolderName);
            _s.Save();
            OrgDirsList.ItemsSource = null;
            OrgDirsList.ItemsSource = _s.AutoOrganizeDirs;
        }
    }

    private void OrgRemoveDir_Click(object sender, RoutedEventArgs e)
    {
        if (OrgDirsList.SelectedItem is string d)
        {
            _s.AutoOrganizeDirs.Remove(d);
            _s.Save();
            OrgDirsList.ItemsSource = null;
            OrgDirsList.ItemsSource = _s.AutoOrganizeDirs;
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string log = AppLog.FilePath;
            if (!File.Exists(log)) File.WriteAllText(log, "");
            Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{log}\"", UseShellExecute = false });
        }
        catch (Exception ex)
        {
            BackupStatus.Text = $"Не вышло: {ex.Message}";
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = DataDir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }

    private void Patches_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var owner = Window.GetWindow(this);
            new PatchesDialog { Owner = owner }.ShowDialog();
        }
        catch { }
    }

    private void DiagCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BackupStatus.Text = AppLog.CopyDiagnostics();
        }
        catch (Exception ex)
        {
            BackupStatus.Text = $"Не вышло: {ex.Message}";
        }
    }

    private void SettingsExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _s.Save();
            string src = Path.Combine(DataDir(), "settings.json");
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт настроек",
                FileName = "pult-settings.json",
                Filter = "JSON (*.json)|*.json",
            };
            if (dlg.ShowDialog() != true) return;
            File.Copy(src, dlg.FileName, true);
            AppLog.Info("Настройки экспортированы: " + dlg.FileName);
            BackupStatus.Text = "Экспортировано: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            AppLog.Error("Экспорт настроек: " + ex.Message);
            BackupStatus.Text = $"Не вышло: {ex.Message}";
        }
    }

    private void SettingsImport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Импорт настроек",
                Filter = "JSON (*.json)|*.json",
            };
            if (dlg.ShowDialog() != true) return;
            AppSettings? imp;
            try
            {
                imp = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(dlg.FileName));
            }
            catch (Exception ex)
            {
                BackupStatus.Text = $"Битый файл: {ex.Message}";
                return;
            }
            if (imp == null)
            {
                BackupStatus.Text = "Битый файл: пусто.";
                return;
            }
            imp.Llm ??= new LlmSettings();
            imp.ExtraGameDirs ??= new List<string>();
            imp.AutoOrganizeDirs ??= new List<string>();
            imp.Save();
            _s = AppSettings.Load();
            Reload();
            AppLog.Info("Настройки импортированы: " + dlg.FileName);
            BackupStatus.Text = "Импортировано, настройки применены.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Импорт настроек: " + ex.Message);
            BackupStatus.Text = $"Не вышло: {ex.Message}";
        }
    }
}
