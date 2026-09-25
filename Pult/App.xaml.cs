using System.Configuration;
using System.Data;
using System.Threading.Tasks;
using System.Windows;
using Pult.Services;
using WpfApp = System.Windows.Application;

namespace Pult;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : WpfApp
{
    private static bool _crashBoxShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            try { CrashLog.Write("Dispatcher", args.Exception); } catch { }
            args.Handled = true; // не даём окну молча закрыться на ресайзе
            if (_crashBoxShown) return; // окно — один раз за сессию, дальше только лог
            _crashBoxShown = true;
            try { System.Windows.MessageBox.Show("Ошибка UI, но окно не закрываю:\n" + args.Exception.Message, "Пульт"); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { CrashLog.Write("AppDomain", args.ExceptionObject as Exception); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try { CrashLog.Write("Task", args.Exception); } catch { }
            args.SetObserved();
        };
        // Тема из настроек — до первых окон, чтобы не мигало дефолтным фиолетом.
        try { ThemeService.Apply(); } catch { }
        // 🆕 0.14.0: шрифт и масштаб — тоже до первых окон (гейт, мастер).
        try { Appearance.Init(AppSettings.Load()); } catch { }
        // Гейт ДО главного окна: без прав дальше не пускаем вообще.
        if (!AdminGate.IsAdmin())
        {
            bool restart = false;
            try { restart = new AdminGate().ShowDialog() == true; }
            catch { }
            // При restart новый процесс уже запущен из гейта.
            Shutdown();
            return;
        }
        // Мастер первого запуска: только если файла настроек ещё нет.
        // Старым пользователям с существующим файлом не показываем.
        if (!AppSettings.Exists())
        {
            try { new WelcomeWizard().ShowDialog(); }
            catch { }
        }
        // Строгий режим: при выходе стираем историю чата и лог.
        Exit += (_, _) =>
        {
            try
            {
                if (!AppSettings.Load().StrictPrivacyMode) return;
                try { System.IO.File.Delete(Views.ChatView.HistoryPath()); } catch { }
                try { System.IO.File.Delete(AppLog.FilePath); } catch { }
            }
            catch { }
        };
        base.OnStartup(e);
    }
}

