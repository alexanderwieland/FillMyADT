using System.Runtime.InteropServices;
using System.Windows;

namespace FillMyADT
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        protected override void OnStartup(StartupEventArgs e)
        {
            AllocConsole();
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            Environment.Exit(e.ApplicationExitCode);
        }
    }

}
