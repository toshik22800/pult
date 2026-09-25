using System.Windows;
using System.Windows.Input;

namespace Pult;

// Тёмный ретро-диалог вместо белого MessageBox.
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string question, string details = "")
    {
        InitializeComponent();
        // 🆕 0.14.0: масштаб UI — контент диалога живёт в ScaleTransform.
        Services.Appearance.ApplyScale(this);
        Loaded += (_, _) => Services.DialogFx.Enter(this);
        ApplySimpleLook();
        QuestionLabel.Text = question;
        if (!string.IsNullOrWhiteSpace(details))
        {
            DetailsLabel.Text = details;
            DetailsBox.Visibility = Visibility.Visible;
        }
    }

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

    public static bool Ask(Window? owner, string question, string details = "")
    {
        var dlg = new ConfirmDialog(question, details);
        if (owner != null) dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }

    private void Titlebar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Yes_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void No_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
