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

            // Select provider based on configuration
            IVssProvider provider = config?.VssMode?.ToLower() switch
            {
                "gitlfs" => new GitLfsVssProvider(
                    snapshotsRootPath: config.GitLfsRepository ?? @"C:\job\AmitTest1 - Copy"),
                "vssadmin" => new VssAdminProvider(),
                _ => new VssAdminProvider()
            };
            
            var vm = new MainViewModel(
                provider, 
                config?.JobsRoot ?? "C:\\job", 
                config?.JobsRoot ?? "C:\\job");
            
            var window = new MainWindow { DataContext = vm };
            window.Show();
        }
    }

    public class Config
    {
        public string? JobsRoot { get; set; }
        public string? VssMode { get; set; }
        
        // Git LFS settings
        public string? GitLfsRepository { get; set; }
        public string? GitLfsRemote { get; set; }
        public bool GitLfsAutoPush { get; set; }
    }
}
