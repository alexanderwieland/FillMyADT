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

            // Load full application configuration
            AppConfiguration appConfig;
            try
            {
                appConfig = configService.LoadAllConfigsAsync().GetAwaiter().GetResult() ?? new AppConfiguration();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load application config, using defaults");
                appConfig = new AppConfiguration();
            }

            // Load individual event source configurations with fallback to defaults
            WindowsEventSourceConfig windowsConfig;
            GitEventSourceConfig gitConfig;
            OutlookEventSourceConfig outlookConfig;
            EdgeEventSourceConfig edgeConfig;

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

            try
            {
                edgeConfig = configService.LoadConfigAsync<EdgeEventSourceConfig>().GetAwaiter().GetResult()
                    ?? new EdgeEventSourceConfig();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load Edge event source config, using defaults");
                edgeConfig = new EdgeEventSourceConfig();
            }

            Log.Information("Configuration loaded from {ConfigPath}", configService.ConfigDirectory);

            // Register event sources with configurations
            serviceCollection.AddSingleton<IEventSource>(sp => new WindowsEventSource(windowsConfig));
            serviceCollection.AddSingleton<IEventSource>(sp => new GitEventSource(gitConfig));
            serviceCollection.AddSingleton<IEventSource>(sp => new OutlookEventSource(outlookConfig, appConfig));
            serviceCollection.AddSingleton<IEventSource>(sp => new EdgeEventSource(edgeConfig));

            // Register aggregator service
            serviceCollection.AddSingleton<EventAggregatorService>();

            // Register converter service with app config for location handling
            serviceCollection.AddSingleton(sp =>
            {
                var gitSource = sp.GetServices<IEventSource>().OfType<GitEventSource>().FirstOrDefault();
                return new EventToTimeSpanConverter(gitSource, appConfig);
            });

            // Register clipboard formatter
            serviceCollection.AddSingleton<ClipboardFormatterService>();

            return serviceCollection.BuildServiceProvider();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            LoggingService.CloseLogging();
        }
    }
}