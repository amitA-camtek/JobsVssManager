using System;
using System.IO;

namespace JobsVssManager.Services
{
    public class JobsWatcherService : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Action<string> _onJobFolderCreated;

        public JobsWatcherService(string jobsRoot, Action<string> onJobFolderCreated)
        {
            _onJobFolderCreated = onJobFolderCreated;

            _watcher = new FileSystemWatcher(jobsRoot)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName
            };

            _watcher.Created += OnCreated;
            _watcher.EnableRaisingEvents = true;
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            if (Directory.Exists(e.FullPath))
                _onJobFolderCreated?.Invoke(e.FullPath);
        }

        public void Dispose() => _watcher.Dispose();
    }
}
