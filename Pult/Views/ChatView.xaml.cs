using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pult.Services;

namespace Pult.Views;

public partial class ChatView : UserControl
{
    public Action<string> Report { get; set; } = _ => { };

    private sealed class HistItem
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private readonly List<HistItem> _history = new();
    private bool _busy;
    private CancellationTokenSource? _cts;
    private readonly System.Windows.Threading.DispatcherTimer _tipTimer =
        new() { Interval = TimeSpan.FromSeconds(7) };
    private int _tipIndex;

    // ==== 0.14.4 Chat 3: документ-ответ, сноски, пустой экран ====
    private sealed class DocMsg
    {
        public bool User;             // true — цитата пользователя
        public string Text = "";
        public List<string> Tools = new(); // сноски (tool-события ответа)
        public string Time = "";      // HH:mm — футер документа
    }
    private readonly List<DocMsg> _docs = new();        // построенное (ререндер)
    private readonly List<string> _pendingNotes = new(); // tool-события ответа
    private bool _inputInEmpty;  // поле ввода сейчас в пустом экране
    private bool? _wide;         // ширина поля со сносками (>=1100 — правое)

    private static readonly string[] Tips =
    {
        "Совет: Ctrl+Alt+3 — место на дисках без чата.",
        "Совет: попроси «запомни, что…» — не забуду.",
        "Совет: «найди в интернете…» — почитаю и перескажу.",
        "Совет: удаление и килл процессов — только с флагом в Настройках.",
        "Совет: Shift+Enter — перенос строки.",
        "Шутка: бэкап, которого нет, — тоже бэкап. Экспорт — в Настройках.",
        "Шутка: мой любимый диск — пустой. Спроси «сколько места на дисках?»",
    };

    private DispatcherTimer? _typingTimer;
    private Border? _typingBubble;
    private bool _typingOn;

    // Режим интерфейса кэшируем при создании вьюхи:
    // Простой — скругления 10, Расширенный — ретро-квадрат 0.
    private readonly bool _simple;
    private readonly double _corner;

    private static string HistFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pult", "history.json");

    internal static string HistoryPath() => HistFile;

    internal static bool IsStrict()
    {
        try { return AppSettings.Load().StrictPrivacyMode; }
        catch { return false; }
    }

    // Простой интерфейс — дружелюбные скругления, Расширенный — квадратный ретро
    private static bool IsSimple()
    {
        try { return AppSettings.Load().Interface == AppSettings.InterfaceMode.Simple; }
        catch { return false; }
    }

    public ChatView()
    {
        _simple = IsSimple();
        _corner = _simple ? 10 : 0;
        InitializeComponent();
        ApplyCorners();
        LoadHistory();
        FillEmptyStatus();
        _tipTimer.Tick += (_, _) => RotateTip();
        RotateTip();
        _tipTimer.Start();
        // Chat 3: пустой экран — поле ввода по центру; история — уже внизу.
        _wide = WideNotes();
        SetEmpty(EmptyState.Visibility == Visibility.Visible);
        SizeChanged += (_, _) => CheckWide();
    }

    // Углы карточек и плиток из XAML — по режиму интерфейса (0 / 10)
    private void ApplyCorners()
    {
        var r = new CornerRadius(_corner);
        SetCorners(LogoTile, r);
        SetCorners(InputWrap, r);
        SetCorners(StatusPill, r);
        SetCorners(HintChip, r);
    }

    private static void SetCorners(DependencyObject root, CornerRadius r)
    {
        if (root is Border b) b.CornerRadius = r;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d) SetCorners(d, r);
    }

    private void FillEmptyStatus()
    {
        try
        {
            var s = AppSettings.Load();
            string st = $"модель: {s.Llm.Model} • инструментов: {LlmClient.ToolCount()}";
            EmptyStatusText.Text = st;
            StatusPill.ToolTip = st;
        }
        catch { EmptyStatusText.Text = ""; }
    }

    private void RotateTip()
    {
        try
        {
            if (EmptyState.Visibility != Visibility.Visible) return;
            EmptyTipText.Text = Tips[_tipIndex % Tips.Length];
            _tipIndex++;
        }
        catch { }
    }

    // ---- История ----

    private void LoadHistory()
    {
        try
        {
            if (IsStrict()) return; // строго: старое не показываем
            if (File.Exists(HistFile))
            {
                var all = JsonSerializer.Deserialize<List<HistItem>>(File.ReadAllText(HistFile));
                if (all != null)
                {
                    foreach (var h in all.TakeLast(200))
                    {
                        if (h.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(h.Content))
                            _history.Add(h);
                    }
                }
            }
        }
        catch { }
        var recent = _history.TakeLast(20).ToList();
        if (recent.Count > 0)
        {
            SetEmpty(false);
            foreach (var h in recent)
            {
                if (h.Role == "user") AddUserBubble(h.Content, false);
                else AddAiBubble(h.Content, false);
            }
            ScrollToEnd();
        }
    }

    private void SaveHistory()
    {
        try
        {
            if (IsStrict()) return; // строго: историю не пишем вообще
            while (_history.Count > 200) _history.RemoveAt(0);
            Directory.CreateDirectory(Path.GetDirectoryName(HistFile)!);
            File.WriteAllText(HistFile, JsonSerializer.Serialize(_history,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ---- Пузыри ----

    // Маркдаун-лайт: **жирный**, `код`, заголовки #, списки - / 1,
    // блоки кода в ```…``` (Consolas, тёмный фон, полоска-акцент слева).
    private static readonly Regex MdInline = new(@"\*\*(.+?)\*\*|`(.+?)`");
    private static readonly Regex MdNumbered = new(@"^\d+[.)]\s+");
    private static readonly Regex MdFence = new(@"^\s*```");

    // Обычные строки markdown в один текстовый блок
    private static TextBlock MakeText(IReadOnlyList<string> lines, Brush fg, Brush accent,
        Brush codeBg, double size = 14, double lh = 18)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            LineHeight = lh,
            Foreground = fg,
            Margin = new Thickness(0),
        };
        for (int li = 0; li < lines.Count; li++)
        {
            string line = lines[li];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("## "))
            {
                tb.Inlines.Add(new Run(trimmed[3..].Trim())
                    { FontWeight = FontWeights.Bold, FontSize = size + 7, Foreground = fg });
            }
            else if (trimmed.StartsWith("# "))
            {
                tb.Inlines.Add(new Run(trimmed[2..].Trim())
                    { FontWeight = FontWeights.Bold, FontSize = size + 4, Foreground = fg });
            }
            else
            {
                string rest = line;
                var num = MdNumbered.Match(trimmed);
                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("• "))
                {
                    tb.Inlines.Add(new Run("• ") { Foreground = accent });
                    rest = trimmed[2..];
                }
                else if (num.Success)
                {
                    tb.Inlines.Add(new Run(num.Value) { Foreground = accent });
                    rest = trimmed[num.Length..];
                }
                int pos = 0;
                foreach (Match m in MdInline.Matches(rest))
                {
                    if (m.Index > pos)
                        tb.Inlines.Add(new Run(rest.Substring(pos, m.Index - pos)) { Foreground = fg });
                    if (m.Groups[1].Success)
                        tb.Inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeights.Bold, Foreground = fg });
                    else
                        tb.Inlines.Add(new Run(m.Groups[2].Value)
                        {
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = Math.Max(11, size - 3),
                            Foreground = accent,
                            Background = codeBg,
                        });
                    pos = m.Index + m.Length;
                }
                if (pos < rest.Length)
                    tb.Inlines.Add(new Run(rest[pos..]) { Foreground = fg });
                if (rest.Length == 0)
                    tb.Inlines.Add(new Run("") { Foreground = fg });
            }
            if (li < lines.Count - 1) tb.Inlines.Add(new LineBreak());
        }
        return tb;
    }

    // Блок кода: тёмный фон, Consolas, 1px акцентная полоска слева,
    // подпись языка сверху (из строки ```язык) — Chat 3.
    private static UIElement MakeCode(IReadOnlyList<string> code, Brush fg, Brush accent,
        Brush codeBg, double radius, string lang = "", double size = 12)
    {
        var tb = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = size,
            Foreground = fg,
            TextWrapping = TextWrapping.Wrap,
        };
        for (int i = 0; i < code.Count; i++)
        {
            if (i > 0) tb.Inlines.Add(new LineBreak());
            tb.Inlines.Add(new Run(code[i]));
        }
        if (code.Count == 0) tb.Inlines.Add(new Run(""));
        var inset = new Border
        {
            Background = codeBg,
            BorderBrush = accent,
            BorderThickness = new Thickness(1, 0, 0, 0),
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 4, 0, 4),
            Child = tb,
        };
        if (lang.Length == 0) return inset;
        var box = new StackPanel { Orientation = Orientation.Vertical };
        box.Children.Add(new TextBlock
        {
            Text = lang,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = accent,
            Margin = new Thickness(0, 0, 0, 4),
        });
        box.Children.Add(inset);
        return box;
    }

    private static void RenderMarkdown(Panel host, string text, Brush fg, Brush accent,
        Brush codeBg, double radius, double size = 14, double lh = 18)
    {
        try
        {
            host.Children.Clear();
            string[] lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            var chunk = new List<string>();
            var code = new List<string>();
            bool inCode = false;
            string lang = "";

            void FlushChunk()
            {
                if (chunk.Count == 0) return;
                host.Children.Add(MakeText(chunk, fg, accent, codeBg, size, lh));
                chunk.Clear();
            }
            void FlushCode()
            {
                if (code.Count == 0) return;
                host.Children.Add(MakeCode(code, fg, accent, codeBg, radius, lang,
                    size >= 16 ? 13 : 12));
                code.Clear();
                lang = "";
            }

            foreach (string line in lines)
            {
                if (MdFence.IsMatch(line))
                {
                    FlushChunk();
                    bool closing = inCode;
                    inCode = !inCode;
                    if (closing) FlushCode();
                    else lang = line.Trim().Trim('`').Trim(); // ```язык
                    continue;
                }
                if (inCode) code.Add(line);
                else chunk.Add(line);
            }
            FlushChunk();
            FlushCode(); // блок закрыли или нет — покажем, что есть
            if (host.Children.Count == 0)
                host.Children.Add(MakeText(new[] { "" }, fg, accent, codeBg, size, lh));
        }
        catch
        {
            try
            {
                host.Children.Clear();
                host.Children.Add(new TextBlock
                {
                    Text = text ?? "",
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = lh,
                    Foreground = fg,
                });
            }
            catch { }
        }
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string t)
        {
            try { Clipboard.SetText(t); Report("Скопировано в буфер."); }
            catch (Exception ex) { try { Report("Не вышло: " + ex.Message); } catch { } }
        }
    }

    // Максимальная ширина пузыря в процентах от ширины списка сообщений
    private double BubbleMax(double ratio)
    {
        double w = MessagesPanel.ActualWidth;
        if (double.IsNaN(w) || w < 100) w = 880; // до первого layout — запасная ширина
        return w * ratio;
    }

    // Цитата пользователя Chat 3: справа, без пузыря — 2px акцент-линия слева,
    // текст 14px, подпись «ты» убрана (скетч «Страница и поля»).
    private void AddUserBubble(string text, bool scroll = true, bool record = true)
    {
        bool wasBottom = IsUserNearBottom();
        if (record)
            _docs.Add(new DocMsg { User = true, Text = text, Time = DateTime.Now.ToString("HH:mm") });
        var host = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(60, 0, 0, 16),
        };
        host.Children.Add(new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = (Brush)FindResource("B_Accent"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(_corner),
            Padding = new Thickness(13, 5, 11, 5),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = Math.Min(BubbleMax(0.5), 680),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 14,
                LineHeight = 22,
                Foreground = (Brush)FindResource("B_Text"),
                TextWrapping = TextWrapping.Wrap,
            },
        });
        MessagesPanel.Children.Add(host);
        try { Services.EffectsHelper.FadeIn(host); } catch { }
        if (scroll) ScrollAfterAdd(wasBottom);
    }

    // Ответ-документ Chat 3: мера ~680px, кегль 17/1.65, сегменты — абзацы и
    // блоки кода. Сноски (tool-события) — правое поле 260px, порядковая
    // эвристика: сноска k напротив сегмента k, лишние — под последним; окно
    // <1100 — сноска под своим абзацем. Футер: время + «копировать».
    private void AddAiBubble(string text, bool scroll = true,
        IReadOnlyList<string>? notes = null, string? ts = null, bool record = true)
    {
        text = text ?? "";
        bool wasBottom = IsUserNearBottom();
        var list = notes != null ? new List<string>(notes) : new List<string>();
        var blocks = SplitBlocks(text);
        bool wide = WideNotes();
        string stamp = ts ?? DateTime.Now.ToString("HH:mm");
        if (record)
            _docs.Add(new DocMsg
            {
                User = false,
                Text = text,
                Tools = new List<string>(list),
                Time = stamp,
            });

        var host = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 18),
        };
        int noteIdx = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(680) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cell = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, i + 1 < blocks.Count ? 10 : 0),
            };
            var body = new StackPanel();
            RenderMarkdown(body, blocks[i],
                (Brush)FindResource("B_Text"),
                (Brush)FindResource("B_Accent"),
                (Brush)FindResource("B_Soft"),
                _corner, 17, 28);
            cell.Children.Add(body);

            // Порядковая эвристика: k-я сноска напротив k-го сегмента,
            // лишние — все под последним сегментом.
            var rowNotes = new List<string>();
            if (noteIdx < list.Count && i == noteIdx)
            {
                rowNotes.Add(list[noteIdx]);
                noteIdx++;
            }
            if (i == blocks.Count - 1)
                while (noteIdx < list.Count)
                {
                    rowNotes.Add(list[noteIdx]);
                    noteIdx++;
                }
            if (rowNotes.Count > 0)
            {
                if (wide)
                {
                    var side = new StackPanel { Margin = new Thickness(18, 2, 0, 0) };
                    foreach (var n in rowNotes) side.Children.Add(MakeNote(n));
                    Grid.SetColumn(side, 1);
                    row.Children.Add(side);
                }
                else
                {
                    foreach (var n in rowNotes)
                    {
                        var one = MakeNote(n);
                        one.Margin = new Thickness(0, 5, 0, 0);
                        cell.Children.Add(one);
                    }
                }
            }
            // Футер документа — под последним сегментом.
            if (i == blocks.Count - 1)
                cell.Children.Add(MakeDocFoot(stamp, text));

            Grid.SetColumn(cell, 0);
            row.Children.Add(cell);
            host.Children.Add(row);
        }
        if (blocks.Count == 0)
            host.Children.Add(MakeDocFoot(stamp, text));
        MessagesPanel.Children.Add(host);
        try { Services.EffectsHelper.FadeIn(host); } catch { }
        if (scroll) ScrollAfterAdd(wasBottom);
    }

    // Tool-события текущего ответа копятся и уходят СНОСКАМИ к документу
    // (Chat 3): пузыри-таблетки в ленте больше не рисуем.
    private void AddToolNote(string msg)
    {
        try
        {
            string s = (msg ?? "").Trim();
            if (s.Length > 0 && _pendingNotes.Count < 8) _pendingNotes.Add(s);
        }
        catch { }
    }

    // ---- Typing-индикатор ----

    private void ShowTyping()
    {
        if (_typingBubble != null) return;
        bool wasBottom = IsUserNearBottom();
        // Chat 3: индикатор — просто точки в ленте документа, без рамки.
        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(2, 14, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        border.Child = new TextBlock
        {
            Text = "● ● ●",
            FontSize = 13,
            Foreground = (Brush)FindResource("B_Dim"),
        };
        _typingBubble = border;
        MessagesPanel.Children.Add(border);
        try { Services.EffectsHelper.FadeIn(border); } catch { }
        ScrollAfterAdd(wasBottom);

        _typingOn = true;
        _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _typingTimer.Tick += TypingTimer_Tick;
        _typingTimer.Start();
    }

    private void HideTyping()
    {
        try
        {
            if (_typingTimer != null)
            {
                _typingTimer.Stop();
                _typingTimer.Tick -= TypingTimer_Tick;
                _typingTimer = null;
            }
        }
        catch { }
        try
        {
            if (_typingBubble != null)
            {
                MessagesPanel.Children.Remove(_typingBubble);
                _typingBubble = null;
            }
        }
        catch { }
        _typingOn = false;
    }

    private void TypingTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _typingOn = !_typingOn;
            if (_typingBubble != null)
                _typingBubble.Opacity = _typingOn ? 1.0 : 0.3;
        }
        catch { }
    }

    // ---- Автоскролл ----

    private bool IsUserNearBottom()
    {
        try
        {
            if (ChatScroll == null) return true;
            return ChatScroll.VerticalOffset + ChatScroll.ViewportHeight >= ChatScroll.ExtentHeight - 40;
        }
        catch { return true; }
    }

    private void ScrollAfterAdd(bool wasBottom)
    {
        try
        {
            if (wasBottom) ScrollToEnd();
            else if (ScrollDownButton != null) ScrollDownButton.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private void ScrollToEnd()
    {
        try
        {
            ChatScroll.ScrollToEnd();
            if (ScrollDownButton != null) ScrollDownButton.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void ChatScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        try
        {
            if (ScrollDownButton == null) return;
            ScrollDownButton.Visibility = IsUserNearBottom() ? Visibility.Collapsed : Visibility.Visible;
        }
        catch { }
    }

    private void ScrollDownButton_Click(object sender, RoutedEventArgs e)
    {
        ScrollToEnd();
    }

    // ---- Отправка ----

    private void SendText(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || _busy) return;
        _busy = true;
        _cts = new CancellationTokenSource();
        UpdateSendButton();

        SetEmpty(false);
        AddUserBubble(text);
        _history.Add(new HistItem { Role = "user", Content = text });
        SaveHistory();
        InputBox.Clear();
        _pendingNotes.Clear();
        SetStatus("Думаю…");
        ShowTyping();

        var cts = _cts;
        var past = _history
            .Where(h => h.Role is "user" or "assistant")
            .TakeLast(20)
            .Select(h => new ChatMessage(h.Role, h.Content))
            .ToList();
        Task.Run(async () =>
        {
            string result;
            try
            {
                var settings = AppSettings.Load();
                using var client = new LlmClient(settings);
                result = await client.RunConversationAsync(
                    text,
                (kind, msg) =>
                {
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (kind == "tool") AddToolNote(msg);
                        });
                    }
                    catch { }
                },
                (name, args) =>
                {
                    if (!LlmClient.NeedsConfirm(name)) return true;
                    try
                    {
                        return Dispatcher.Invoke(() =>
                        {
                            string summary = args.Count == 0
                                ? "без параметров"
                                : string.Join("\n", args.Select(kv => $"{kv.Key}: {kv.Value}"));
                            var owner = Window.GetWindow(this);
                            return ConfirmDialog.Ask(owner, $"Выполнить «{name}»?", summary);
                        });
                    }
                    catch { return false; }
                },
                cts.Token, past);
            }
            catch (Exception ex)
            {
                result = $"Ошибка: {ex.Message}";
            }
            Dispatcher.Invoke(() =>
            {
                try
                {
                    HideTyping();
                    AddAiBubble(result, true, _pendingNotes);
                    _pendingNotes.Clear();
                    _history.Add(new HistItem { Role = "assistant", Content = result });
                    SaveHistory();
                }
                finally
                {
                    _busy = false;
                    _cts = null;
                    UpdateSendButton();
                    SetStatus("Готов");
                }
            });
        }, cts.Token);
    }

    private void UpdateSendButton()
    {
        if (_busy)
        {
            SendLabel.Text = "Стоп";
            SendLabel.Visibility = Visibility.Visible;
            SendIcon.Text = "\uE71A";
        }
        else
        {
            SendLabel.Text = "Отправить";
            SendLabel.Visibility = Visibility.Collapsed;
            SendIcon.Text = "\uE724";
        }
    }

    private void SetStatus(string s)
    {
        try { Report(s); } catch { }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            try { _cts?.Cancel(); } catch { }
            return;
        }
        SendText(InputBox.Text);
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Оставлен для совместимости; реально работает PreviewKeyDown ниже.
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            if (_busy) return;
            SendText(InputBox.Text);
        }
    }

    // Ссылки пустого экрана: «Настрой» — навигация, остальные — готовые запросы.
    private void QuickLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string s || s.Length == 0) return;
            if (s == "settings")
            {
                if (Window.GetWindow(this) is MainWindow mw) mw.GoTo("settings");
                return;
            }
            SendText(s);
        }
        catch { }
    }

    // ---- Chat 3: поле ввода в пустом экране, сноски, документы ----

    // Пустой экран: поле ввода живёт по центру «Что делаем?», после первой
    // отправки (или при загруженной истории) возвращается на нижнюю строку.
    private void SetEmpty(bool empty)
    {
        try
        {
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (empty == _inputInEmpty) return;
            _inputInEmpty = empty;
            if (InputBar.Parent is Panel oldp) oldp.Children.Remove(InputBar);
            if (empty)
            {
                Grid.SetRow(InputBar, 0);
                InputBar.Width = 620;
                InputBar.HorizontalAlignment = HorizontalAlignment.Center;
                InputBar.Margin = new Thickness(0, 0, 0, 0);
                EmptySlot.Children.Add(InputBar);
            }
            else
            {
                Grid.SetRow(InputBar, 2);
                InputBar.Width = double.NaN;
                InputBar.HorizontalAlignment = HorizontalAlignment.Stretch;
                InputBar.Margin = new Thickness(0, 14, 0, 0);
                RootGrid.Children.Add(InputBar);
            }
        }
        catch { }
    }

    // Правое поле сносок — только на окнах >=1100; уже — под своим абзацем.
    private bool WideNotes()
    {
        double w = ActualWidth;
        if (double.IsNaN(w) || w < 100) return true;
        return w >= 1100;
    }

    private void CheckWide()
    {
        try
        {
            bool wide = WideNotes();
            if (_wide == wide) return;
            _wide = wide;
            RebuildDocs();
        }
        catch { }
    }

    // Перерисовка ленты из _docs при смене ширины (сноски переезжают).
    private void RebuildDocs()
    {
        try
        {
            bool wasBottom = IsUserNearBottom();
            var typing = _typingBubble;
            if (typing != null) MessagesPanel.Children.Remove(typing);
            MessagesPanel.Children.Clear();
            foreach (var d in _docs)
            {
                if (d.User) AddUserBubble(d.Text, false, record: false);
                else AddAiBubble(d.Text, false, d.Tools, d.Time, record: false);
            }
            if (_docs.Count == 0)
            {
                MessagesPanel.Children.Add(EmptyState);
                SetEmpty(true);
            }
            else SetEmpty(false);
            if (typing != null) MessagesPanel.Children.Add(typing);
            ScrollAfterAdd(wasBottom);
        }
        catch { }
    }

    // Разбивка ответа на сегменты: абзацы (пустая строка) и блоки кода
    // (```); каждый сегмент — строка документа, к ней привязывается сноска.
    private static List<string> SplitBlocks(string text)
    {
        var blocks = new List<string>();
        try
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            var cur = new List<string>();
            bool inCode = false;
            foreach (var line in lines)
            {
                if (MdFence.IsMatch(line))
                {
                    if (!inCode)
                    {
                        if (cur.Count > 0) blocks.Add(string.Join("\n", cur));
                        cur.Clear();
                        inCode = true;
                        cur.Add(line);
                    }
                    else
                    {
                        cur.Add(line);
                        blocks.Add(string.Join("\n", cur));
                        cur.Clear();
                        inCode = false;
                    }
                    continue;
                }
                if (inCode)
                {
                    cur.Add(line);
                    continue;
                }
                if (line.Trim().Length == 0)
                {
                    if (cur.Count > 0)
                    {
                        blocks.Add(string.Join("\n", cur));
                        cur.Clear();
                    }
                }
                else cur.Add(line);
            }
            if (cur.Count > 0) blocks.Add(string.Join("\n", cur));
        }
        catch { }
        return blocks;
    }

    // Сноска: чип справа от сегмента (или под ним — узкое окно), «↑» акцентом.
    private Border MakeNote(string note)
    {
        var tb = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("B_Dim"),
        };
        tb.Inlines.Add(new Run("\u2191 ")
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("B_Accent"),
        });
        tb.Inlines.Add(new Run(note));
        return new Border
        {
            Background = (Brush)FindResource("B_Tile"),
            BorderBrush = (Brush)FindResource("B_Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_corner),
            Padding = new Thickness(8, 5, 8, 5),
            MaxWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = tb,
        };
    }

    // Футер документа: время ответа + ссылка «копировать».
    private StackPanel MakeDocFoot(string stamp, string text)
    {
        var foot = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 9, 0, 0),
        };
        foot.Children.Add(new TextBlock
        {
            Text = stamp,
            FontSize = 12,
            Foreground = (Brush)FindResource("B_Muted"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        foot.Children.Add(new TextBlock
        {
            Text = "·",
            FontSize = 12,
            Margin = new Thickness(7, 0, 7, 0),
            Foreground = (Brush)FindResource("B_Muted"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var copy = new Button
        {
            Content = "копировать",
            FontSize = 12,
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            Tag = text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("B_Muted"),
            ToolTip = "Скопировать ответ в буфер обмена.",
            VerticalAlignment = VerticalAlignment.Center,
        };
        copy.Click += CopyText_Click;
        foot.Children.Add(copy);
        return foot;
    }

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, "Очистить чат?", "Удалить все сообщения и историю.")) return;
        HideTyping();
        MessagesPanel.Children.Clear();
        MessagesPanel.Children.Add(EmptyState);
        _history.Clear();
        _docs.Clear();
        SetEmpty(true);
        SaveHistory();
        SetStatus("Готов");
        if (ScrollDownButton != null) ScrollDownButton.Visibility = Visibility.Collapsed;
    }
}
