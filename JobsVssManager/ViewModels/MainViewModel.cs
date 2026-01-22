using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
        private JobViewModel? _selectedJob;
        private SnapshotModel? _selectedSnapshot;
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

        public SnapshotModel? SelectedSnapshot
        {
            get => _selectedSnapshot;
            set
            {
                _selectedSnapshot = value;
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
        public ICommand DeleteSnapshotCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand RedoCommand { get; }

        public MainViewModel(IVssProvider vssProvider, string jobsRoot, string volume)
        {
            _jobsRoot = jobsRoot;
            Directory.CreateDirectory(_jobsRoot);

            _snapshotService = new VssSnapshotService(vssProvider, volume);

            foreach (var dir in Directory.GetDirectories(_jobsRoot))
                Jobs.Add(new JobViewModel(new JobModel { Name = Path.GetFileName(dir), Path = dir }));

            _ = LoadSnapshotsAsync();

            CreateSnapshotCommand = new RelayCommand(async _ => await CreateSnapshotAsync(), _ => !IsBusy);
            DeleteSnapshotCommand = new RelayCommand(async _ => await DeleteSnapshotAsync(), _ => Snapshots.Count > 0 && !IsBusy);
            RestoreCommand = new RelayCommand(async _ => await RestoreAsync(), _ => SelectedJob != null && SelectedSnapshot != null && !IsBusy);
            RedoCommand = new RelayCommand(async _ => await RedoAsync(), _ => SelectedJob != null && Snapshots.Count > 0 && !IsBusy);
        }

        private async Task LoadSnapshotsAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                IsBusy = true;
                StatusMessage = "Loading snapshots...";

                var snapshots = await _snapshotService.ListSnapshotsAsync();

                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);

                // Marshal back to UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Snapshots.Clear();
                    foreach (var snapshot in snapshots.OrderByDescending(s => s.CreatedAt))
                    {
                        Snapshots.Add(snapshot);
                    }
                });

                StatusMessage = $"Loaded {Snapshots.Count} snapshot(s) ({duration})";
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
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
            var stopwatch = Stopwatch.StartNew();
            try
            {
                IsBusy = true;
                StatusMessage = "Creating snapshot...";

                var snap = await _snapshotService.CreateSnapshotAsync($"Snapshot {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);

                // Marshal back to UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Snapshots.Insert(0, snap);
                    MessageBox.Show(
                        $"Snapshot created successfully!\n\nID: {snap.Id}\nDuration: {duration}",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                StatusMessage = $"Snapshot created successfully ({duration})";
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
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

        private async Task DeleteSnapshotAsync()
        {
            if (Snapshots.Count == 0)
                return;

            var result = await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    $"Are you sure you want to delete all {Snapshots.Count} snapshot(s)?",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning));

            if (result == MessageBoxResult.No)
                return;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                IsBusy = true;
                StatusMessage = $"Deleting {Snapshots.Count} snapshot(s)...";

                var snapshotsToDelete = Snapshots.ToList();

                foreach (var snapshot in snapshotsToDelete)
                {
                    try
                    {
                        await _snapshotService.DeleteSnapshotAsync(snapshot.Id);
                    }
                    catch (Exception ex)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            MessageBox.Show($"Failed to delete snapshot {snapshot.Id}:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                    }
                }

                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Snapshots.Clear();
                    MessageBox.Show(
                        $"All snapshots deleted successfully.\n\nDuration: {duration}",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                StatusMessage = $"Snapshots deleted successfully ({duration})";
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Failed to delete snapshots:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
                StatusMessage = "Failed to delete snapshots";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RestoreAsync()
        {
            if (SelectedJob == null || SelectedSnapshot == null)
                return;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                IsBusy = true;
                StatusMessage = $"Restoring from snapshot: {SelectedSnapshot.Description}...";

                await _snapshotService.RestoreFolderAsync(SelectedSnapshot.Id, SelectedJob.Path);

                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        $"Restored from snapshot:\n{SelectedSnapshot.Description}\n\nDuration: {duration}",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                StatusMessage = $"Restore completed successfully ({duration})";
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
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

        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes >= 1)
                return $"{duration.Minutes}m {duration.Seconds}s";
            else if (duration.TotalSeconds >= 1)
                return $"{duration.Seconds}.{duration.Milliseconds:D3}s";
            else
                return $"{duration.Milliseconds}ms";
        }
    }
}
