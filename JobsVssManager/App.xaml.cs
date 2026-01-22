using System.IO;
using System.Text.Json;
using System.Windows;
using JobsVssManager.Services;
using JobsVssManager.ViewModels;
using JobsVssManager.Views;

namespace JobsVssManager
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);  

            var options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                PropertyNameCaseInsensitive = true
            };

            var config = JsonSerializer.Deserialize<Config>(
                File.ReadAllText("appsettings.json"), options);

            // Use VssAdminProvider
            IVssProvider provider = new VssAdminProvider();
            
            var vm = new MainViewModel(
                provider, 
                config?.JobsRoot ?? "C:\\job", 
                config?.Volume ?? "C:\\");
            
            var window = new MainWindow { DataContext = vm };
            window.Show();
        }
    }

    public class Config
    {
        public string? JobsRoot { get; set; }
        public string? Volume { get; set; }
    }
}
