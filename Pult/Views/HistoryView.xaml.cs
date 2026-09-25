using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pult.Services;

namespace Pult.Views;

public partial class HistoryView : UserControl
{
    public Action<string> Report = _ => { };

    public HistoryView()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        try { Render(ActionJournal.LoadAll()); }
        catch (Exception ex)
        {
            try { Report("История не прочиталась: " + ex.Message); } catch { }
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Render(System.Collections.Generic.List<ActionJournal.JournalEntry> entries)
    {
        EntriesPanel.Children.Clear();
        var muted = (Brush)FindResource("B_Muted");
        var text = (Brush)FindResource("B_Text");
        var accent = (Brush)FindResource("B_Accent");
        var border = (Brush)FindResource("B_Border");

        if (entries.Count == 0)
        {
            EntriesPanel.Children.Add(new TextBlock
            {
                Text = "Пока пусто. Измени твик или приватность в Системе — запись появится здесь.",
                FontSize = 13, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var e in entries.OrderByDescending(x => x.Time).Take(100))
        {
            var card = new Border
            {
                Background = (Brush)FindResource("B_Panel"),
                BorderBrush = border, BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(0), Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var box = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = KindName(e.Kind), FontSize = 11, Foreground = accent,
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            head.Children.Add(new TextBlock
            {
                Text = e.Time.ToString("dd.MM HH:mm"), FontSize = 11, Foreground = muted,
                VerticalAlignment = VerticalAlignment.Center,
            });
            box.Children.Add(head);
            var title = new TextBlock
            {
                // 🆕 0.14.2: заголовок записи идёт через F_Ui — общий шрифт
                // интерфейса (0.14.0 держал тут пиксель безусловно).
                Text = e.Title,
                FontSize = 13, Foreground = text,
                Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            };
            title.SetResourceReference(TextBlock.FontFamilyProperty, "F_Ui");
            box.Children.Add(title);
            if (!string.IsNullOrWhiteSpace(e.Detail))
                box.Children.Add(new TextBlock
                {
                    Text = e.Detail, FontSize = 12, Foreground = muted,
                    Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
            Grid.SetColumn(box, 0);
            grid.Children.Add(box);

            if (e.CanUndo)
            {
                var undo = new Button
                {
                    Content = "Отменить", FontSize = 12, Padding = new Thickness(12, 4, 12, 4),
                    Margin = new Thickness(12, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = e, VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Вернуть состояние «было».",
                };
                undo.Click += Undo_Click;
                Grid.SetColumn(undo, 1);
                grid.Children.Add(undo);
            }
            else
            {
                var pill = new TextBlock
                {
                    Text = "без отката", FontSize = 11, Foreground = muted,
                    Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(pill, 1);
                grid.Children.Add(pill);
            }

            card.Child = grid;
            EntriesPanel.Children.Add(card);
        }
    }

    private static string KindName(string kind) => kind switch
    {
        "tweak" => "ТВИК",
        "privacy" => "ПРИВАТНОСТЬ",
        "power" => "ПИТАНИЕ",
        "startup" => "АВТОЗАГРУЗКА",
        _ => "ИНФО",
    };

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not ActionJournal.JournalEntry en) return;
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, $"Откатить «{en.Title}»?", en.Detail ?? "")) return;
        b.IsEnabled = false;
        try { Report($"Откатываю: {en.Title}…"); } catch { }
        Task.Run(() => ActionJournal.Undo(en)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    string res = t.IsFaulted ? $"Ошибка: {t.Exception?.GetBaseException().Message}" : t.Result;
                    try { Report(res); } catch { }
                    Reload();
                }
                catch { }
            });
        });
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (!ConfirmDialog.Ask(owner, "Создать точку восстановления?",
            "Checkpoint-Computer. Занимает до 3 минут, нужна 1 точка в сутки.")) return;
        RestoreBtn.IsEnabled = false;
        RestoreStatus.Visibility = Visibility.Visible;
        RestoreStatus.Text = "Создаю точку восстановления… (до 3 мин)";
        try { Report("Создаю точку восстановления…"); } catch { }
        Task.Run(() => RestorePointService.Create()).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    string res = t.IsFaulted ? $"Ошибка: {t.Exception?.GetBaseException().Message}" : t.Result;
                    RestoreStatus.Text = res;
                    try { Report(res); } catch { }
                    AppLog.Info("Точка восстановления: " + res);
                }
                catch { }
                finally { RestoreBtn.IsEnabled = true; }
            });
        });
    }
}
