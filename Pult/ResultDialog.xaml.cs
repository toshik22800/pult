using System.Windows;
using System.Windows.Input;

namespace Pult;

public partial class ResultDialog : Window
{
    private readonly Func<string>? _action;

    public ResultDialog(string code, string title, string status, string text,
                        string? actionLabel = null, Func<string>? action = null)
    {
        InitializeComponent();
        // 🆕 0.14.0: масштаб UI — контент диалога живёт в ScaleTransform.
        Services.Appearance.ApplyScale(this);
        Loaded += (_, _) => Services.DialogFx.Enter(this);
        HeaderGlyph.Text = code;
        HeaderLabel.Text = title;
        TitleLabel.Text = $"  {title}";
        StatusLabel.Text = status;
        BodyBox.Text = text;
        _action = action;
        if (actionLabel != null && action != null)
        {
            ActionButton.Content = actionLabel;
            ActionButton.Visibility = Visibility.Visible;
        }
        ApplySimpleLook();
        KeyDown += (_, e) => { try { if (e.Key == Key.Escape) Close(); } catch { } };
    }

    public static void ShowResult(Window owner, string code, string title, string text,
                                  string? actionLabel = null, Func<string>? action = null)
    {
        string status = text.StartsWith("Ошибка") ? "●  Ошибка" : "●  Готово";
        new ResultDialog(code, title, status, text, actionLabel, action) { Owner = owner }.ShowDialog();
    }

    private void Titlebar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(BodyBox.Text); } catch { }
        Close();
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_action == null) return;
        ActionButton.IsEnabled = false;
        ActionButton.Content = "Выполняю…";
        Task.Run(_action).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                string res = t.IsFaulted ? $"Ошибка: {t.Exception?.GetBaseException().Message}" : t.Result;
                BodyBox.Text = res;
                StatusLabel.Text = res.StartsWith("Ошибка") ? "●  Ошибка" : "●  Готово";
                ActionButton.Visibility = Visibility.Collapsed;
            });
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Простой интерфейс: мягкие углы + просторнее. Расширенный — как было.
    private void ApplySimpleLook()
    {
        try
        {
            if (Services.AppSettings.Load().Interface
                != Services.AppSettings.InterfaceMode.Simple) return;
            BodyBorder.CornerRadius = new CornerRadius(Services.DesignTokens.CornerSimple);
            var p = BodyBorder.Padding;
            BodyBorder.Padding = new Thickness(p.Left * 1.2, p.Top * 1.2, p.Right * 1.2, p.Bottom * 1.2);
        }
        catch { }
    }
}
