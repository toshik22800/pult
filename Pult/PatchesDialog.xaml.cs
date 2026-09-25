using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Pult;

// Патчи карточками-таймлайном (язык пузырей ИИ: полоса + пишно),
// данные — из Services.ReleaseNotes.
public partial class PatchesDialog : Window
{
    public PatchesDialog()
    {
        InitializeComponent();
        // 🆕 0.14.0: масштаб UI — контент диалога живёт в ScaleTransform.
        Services.Appearance.ApplyScale(this);
        Loaded += (_, _) => Services.DialogFx.Enter(this);
        TitleLabel.Text = "  Патчи";
        var notes = Services.ReleaseNotes.All;
        StatusLabel.Text = $"{notes.Length} {Services.DiskCleanupService.Plural(notes.Length, "версия", "версии", "версий")}";
        BuildCards();
    }

    private void BuildCards()
    {
        try
        {
            var text = (Brush)FindResource("B_Text");
            var dim = (Brush)FindResource("B_Dim");
            var accent = (Brush)FindResource("B_Accent");
            var panel = (Brush)FindResource("B_Panel");
            var border = (Brush)FindResource("B_Border");
            var hdr = (FontFamily)FindResource("F_Ui");
            foreach (var n in Services.ReleaseNotes.All)
            {
                var outer = new Border
                {
                    Background = panel,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(0),
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 0, 10),
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var stripe = new Rectangle
                {
                    Fill = accent, Width = 3,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetColumn(stripe, 0);
                grid.Children.Add(stripe);
                var content = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock
                {
                    Text = n.Version, FontFamily = hdr, FontSize = 11,
                    Foreground = text, VerticalAlignment = VerticalAlignment.Center,
                });
                if (Services.ReleaseNotes.IsCurrent(n.Version))
                {
                    head.Children.Add(new Border
                    {
                        BorderBrush = accent, BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 2, 8, 2),
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = "ТЕКУЩАЯ", FontFamily = hdr, FontSize = 8,
                            Foreground = accent, VerticalAlignment = VerticalAlignment.Center,
                        },
                    });
                }
                content.Children.Add(head);
                foreach (string item in n.Items)
                {
                    var tb = new TextBlock
                    {
                        FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 5, 0, 0),
                    };
                    tb.Inlines.Add(new Run("• ") { Foreground = accent });
                    tb.Inlines.Add(new Run(item) { Foreground = dim });
                    content.Children.Add(tb);
                }
                Grid.SetColumn(content, 1);
                grid.Children.Add(content);
                outer.Child = grid;
                CardsHost.Children.Add(outer);
            }
        }
        catch { }
    }

    private void Titlebar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Services.ReleaseNotes.Format()); } catch { }
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
