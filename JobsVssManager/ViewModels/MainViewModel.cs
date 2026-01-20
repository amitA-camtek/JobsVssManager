using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using JobsVssManager.Models;
using JobsVssManager.Services;
using JobsVssManager.Utilities; // Add this using if RelayCommand is in Utilities namespace

namespace JobsVssManager.ViewModels
{
    public class MainViewModel : BaseViewModel
    {
        private readonly string _jobsRoot;
        private readonly VssSnapshotService _snapshotService;
        private readonly JobsWatcherService _jobsWatcher;
        private JobViewModel? _selectedJob;
        private bool _isBusy;
        private string _statusMessage = "";

        public ObservableCollection<JobViewModel> Jobs { get; } = new();
        public ObservableCollection<SnapshotModel> Snapshots { get; } = new();

        public JobViewModel? SelectedJob
        {
            get => _selectedJob;
            set
            {
                _selectedJob = value;
                OnPropertyChanged();
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }

        public bool IsNotBusy => !IsBusy;

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }

        public ICommand CreateSnapshotCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }

        public MainViewModel(IVssProvider vssProvider, string jobsRoot, string volume)
        {
            _jobsRoot = jobsRoot;
            Directory.CreateDirectory(_jobsRoot);

            _snapshotService = new VssSnapshotService(vssProvider, volume);
            _jobsWatcher = new JobsWatcherService(_jobsRoot, OnJobFolderCreated);

            foreach (var dir in Directory.GetDirectories(_jobsRoot))
                Jobs.Add(new JobViewModel(new JobModel { Name = Path.GetFileName(dir), Path = dir }));

            _ = LoadSnapshotsAsync();

            CreateSnapshotCommand = new RelayCommand(async _ => await CreateSnapshotAsync(), _ => !IsBusy);
            UndoCommand = new RelayCommand(async _ => await UndoAsync(), _ => SelectedJob != null && Snapshots.Count > 0 && !IsBusy);
            RedoCommand = new RelayCommand(async _ => await RedoAsync(), _ => SelectedJob != null && Snapshots.Count > 0 && !IsBusy);
        }

        private async Task LoadSnapshotsAsync()
        {
            try
            {
                IsBusy = true;
                StatusMessage = "Loading snapshots...";
                
                var snapshots = await _snapshotService.ListSnapshotsAsync();
                
                // Marshal back to UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Snapshots.Clear();
                    foreach (var snapshot in snapshots.OrderByDescending(s => s.CreatedAt))
                    {
                        Snapshots.Add(snapshot);
                    }
                });
                
                StatusMessage = $"Loaded {Snapshots.Count} snapshot(s)";
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Failed to load snapshots:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
                StatusMessage = "Failed to load snapshots";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OnJobFolderCreated(string fullPath)
        {
            // Marshal to UI thread for ObservableCollection
            Application.Current.Dispatcher.Invoke(() =>
            {
                Jobs.Add(new JobViewModel(new JobModel
                {
                    Name = Path.GetFileName(fullPath),
                    Path = fullPath
                }));
            });
        }

        private async Task CreateSnapshotAsync()
        {
            try
            {
                IsBusy = true;
                StatusMessage = "Creating snapshot...";
                
                var snap = await _snapshotService.CreateSnapshotAsync($"Snapshot {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                
                // Marshal back to UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Snapshots.Insert(0, snap);
                    MessageBox.Show($"Snapshot created successfully!\nID: {snap.Id}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                });
                
                StatusMessage = "Snapshot created successfully";
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Failed to create snapshot:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
                StatusMessage = "Failed to create snapshot";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task UndoAsync()
        {
            if (SelectedJob == null || Snapshots.Count == 0)
                return;

            try
            {
                IsBusy = true;
                var snapshot = Snapshots.First();
                StatusMessage = $"Restoring from snapshot: {snapshot.Description}...";
                
                await _snapshotService.RestoreFolderAsync(snapshot.Id, SelectedJob.Path);
                
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Restored from snapshot:\n{snapshot.Description}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                });
                
                StatusMessage = "Restore completed successfully";
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Failed to restore:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
                StatusMessage = "Restore failed";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RedoAsync()
        {
            // Placeholder for redo functionality
            await Task.Run(() =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show("Redo functionality not yet implemented.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            });
        }
    }
}
