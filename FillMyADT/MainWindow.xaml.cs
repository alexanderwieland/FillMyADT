using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using FillMyADT.Services;
using FillMyADT.Services.EventSources;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.Windows;

namespace FillMyADT
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            // Initialize logging first
            LoggingService.InitializeLogging();
            Log.Information("Application starting");

            try
            {
                // Build services BEFORE InitializeComponent
                var serviceProvider = BuildServices();
                Resources.Add("services", serviceProvider);

                // Now initialize the window with services available
                InitializeComponent();

                Log.Information("Application initialized successfully");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application failed to initialize");
                MessageBox.Show($"Application failed to start: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private static IServiceProvider BuildServices()
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddWpfBlazorWebView();
#if DEBUG
            serviceCollection.AddBlazorWebViewDeveloperTools();
#endif

            serviceCollection.AddSingleton<ConfigurationService>();
            serviceCollection.AddSingleton<EventSourceRegistry>();
            serviceCollection.AddSingleton<EventAggregatorService>();
            serviceCollection.AddSingleton(sp => new EventToTimeSpanConverter(
                sp.GetRequiredService<EventSourceRegistry>(), sp.GetRequiredService<ConfigurationService>()));
            serviceCollection.AddSingleton<ClipboardFormatterService>();
            serviceCollection.AddSingleton<AdtXmlExportService>();

            return serviceCollection.BuildServiceProvider();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            LoggingService.CloseLogging();
            Application.Current.Shutdown();
        }
    }
}