using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using FillMyADT.Services;
using FillMyADT.Services.EventSources;
using Serilog;

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

            // Register configuration service
            var configService = new ConfigurationService();
            serviceCollection.AddSingleton(configService);

            // Initialize default configuration if needed (synchronous for startup)
            try
            {
                configService.InitializeDefaultConfigAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to initialize default configuration");
            }

            // Load configurations with fallback to defaults
            WindowsEventSourceConfig windowsConfig;
            GitEventSourceConfig gitConfig;
            OutlookEventSourceConfig outlookConfig;

            try
            {
                windowsConfig = configService.LoadConfigAsync<WindowsEventSourceConfig>().GetAwaiter().GetResult()
                    ?? new WindowsEventSourceConfig();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load Windows event source config, using defaults");
                windowsConfig = new WindowsEventSourceConfig();
            }

            try
            {
                gitConfig = configService.LoadConfigAsync<GitEventSourceConfig>().GetAwaiter().GetResult()
                    ?? new GitEventSourceConfig();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load Git event source config, using defaults");
                gitConfig = new GitEventSourceConfig();
            }

            try
            {
                outlookConfig = configService.LoadConfigAsync<OutlookEventSourceConfig>().GetAwaiter().GetResult()
                    ?? new OutlookEventSourceConfig();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load Outlook event source config, using defaults");
                outlookConfig = new OutlookEventSourceConfig();
            }

            Log.Information("Configuration loaded from {ConfigPath}", configService.ConfigDirectory);

            // Register event sources with configurations
            serviceCollection.AddSingleton<IEventSource>(sp => new WindowsEventSource(windowsConfig));
            serviceCollection.AddSingleton<IEventSource>(sp => new GitEventSource(gitConfig));
            serviceCollection.AddSingleton<IEventSource>(sp => new OutlookEventSource(outlookConfig));

            // Register aggregator service
            serviceCollection.AddSingleton<EventAggregatorService>();

            // Register converter service
            serviceCollection.AddSingleton<EventToTimeSpanConverter>();

            return serviceCollection.BuildServiceProvider();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            LoggingService.CloseLogging();
        }
    }
}