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
using JobsVssManager.Utilities;

namespace JobsVssManager.ViewModels
{
    public class MainViewModel : BaseViewModel
    {
        private readonly string _jobsRoot;
        private readonly VssSnapshotService _snapshotService;
        private readonly RestoreStateManager _restoreStateManager;
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
        public ICommand SaveSnapshotCommand { get; }
        public ICommand RestoreCommand { get; }

        public MainViewModel(IVssProvider vssProvider, string jobsRoot, string volume, int snapshotExpirationHours = 24)
        {
            _jobsRoot = jobsRoot;
            Directory.CreateDirectory(_jobsRoot);

            _snapshotService = new VssSnapshotService(vssProvider, volume, snapshotExpirationHours);
            _restoreStateManager = new RestoreStateManager();

            foreach (var dir in Directory.GetDirectories(_jobsRoot))
                Jobs.Add(new JobViewModel(new JobModel { Name = Path.GetFileName(dir), Path = dir }));

            _ = LoadSnapshotsAsync();

            CreateSnapshotCommand = new RelayCommand(async _ => await CreateSnapshotAsync(), _ => !IsBusy);
            SaveSnapshotCommand = new RelayCommand(async _ => await DeleteSnapshotAsync(), _ => Snapshots.Count > 0 && !IsBusy);
            RestoreCommand = new RelayCommand(async _ => await RestoreAsync(), _ => SelectedJob != null && SelectedSnapshot != null && !IsBusy);
        }

        public async Task CheckPendingRestoreAsync()
        {
            var pendingRestore = _restoreStateManager.GetPendingRestore();
            if (pendingRestore == null)
                return;

            var result = await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    $"A restore operation was interrupted:\n\n" +
                    $"Snapshot: {pendingRestore.SnapshotDescription ?? pendingRestore.SnapshotId}\n" +
                    $"Target: {pendingRestore.TargetPath}\n" +
                    $"Started: {pendingRestore.StartedAt:g}\n\n" +
                    $"Do you want to retry the restore?",
                    "Incomplete Restore Detected",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning));

            if (result == MessageBoxResult.Yes)
            {
                await ResumeRestoreAsync(pendingRestore);
            }
            else
            {
                _restoreStateManager.MarkFailed();
                StatusMessage = "Pending restore cancelled by user";
            }
        }

        private async Task ResumeRestoreAsync(RestoreState restoreState)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                IsBusy = true;
                StatusMessage = $"Resuming restore: {restoreState.SnapshotDescription ?? restoreState.SnapshotId}...";

                await _snapshotService.RestoreFolderAsync(restoreState.SnapshotId!, restoreState.TargetPath!);

                _restoreStateManager.MarkCompleted();

                // Delete the snapshot after successful restore
                StatusMessage = "Deleting snapshot after restore...";
                await _snapshotService.DeleteSnapshotAsync(restoreState.SnapshotId!);

                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // Remove from UI if it exists
                    var snapshotToRemove = Snapshots.FirstOrDefault(s => s.Id == restoreState.SnapshotId);
                    if (snapshotToRemove != null)
                    {
                        Snapshots.Remove(snapshotToRemove);
                    }
                    
                    MessageBox.Show(
                        $"Restore completed successfully!\n\n" +
                        $"Snapshot: {restoreState.SnapshotDescription ?? restoreState.SnapshotId}\n" +
                        $"Target: {restoreState.TargetPath}\n" +
                        $"Duration: {duration}\n\n" +
                        $"Snapshot has been deleted.",
                        "Restore Complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                StatusMessage = $"Restore completed and snapshot deleted ({duration})";
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _restoreStateManager.MarkFailed();
                
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        $"Failed to resume restore:\n{ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                });
                StatusMessage = "Restore failed";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadSnapshotsAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                IsBusy = true;
                StatusMessage = "Loading snapshots...";

                var snapshots = await _snapshotService.ListSnapshotsAsync();
                var snapshotsList = snapshots.OrderByDescending(s => s.CreatedAt).ToList();

                // Find expired snapshots
                var expiredSnapshots = snapshotsList.Where(s => s.IsExpired).ToList();

                // Delete expired snapshots
                if (expiredSnapshots.Any())
                {
                    StatusMessage = $"Deleting {expiredSnapshots.Count} expired snapshot(s)...";
                    
                    foreach (var expiredSnapshot in expiredSnapshots)
                    {
                        try
                        {
                            await _snapshotService.DeleteSnapshotAsync(expiredSnapshot.Id);
                            snapshotsList.Remove(expiredSnapshot);
                        }
                        catch (Exception ex)
                        {
                            // Log but continue with other snapshots
                            Debug.WriteLine($"Failed to delete expired snapshot {expiredSnapshot.Id}: {ex.Message}");
                        }
                    }
                }

                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);

                // Marshal back to UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Snapshots.Clear();
                    foreach (var snapshot in snapshotsList)
                    {
                        Snapshots.Add(snapshot);
                    }
                });

                var message = $"Loaded {Snapshots.Count} snapshot(s) ({duration})";
                if (expiredSnapshots.Any())
                {
                    message += $" - Deleted {expiredSnapshots.Count} expired";
                }
                StatusMessage = message;
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
                        $"Snapshot created successfully!\n\n" +
                        $"ID: {snap.Id}\n" +
                        $"Expires: {snap.ExpiresAt:g}\n" +
                        $"Duration: {duration}",
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
            var snapshotToDelete = SelectedSnapshot; // Store reference before clearing selection
            
            try
            {
                IsBusy = true;
                StatusMessage = $"Restoring from snapshot: {SelectedSnapshot.Description}...";

                // Save restore state before starting
                _restoreStateManager.SaveRestoreState(
                    SelectedSnapshot.Id, 
                    SelectedJob.Path, 
                    SelectedSnapshot.Description);

                await _snapshotService.RestoreFolderAsync(SelectedSnapshot.Id, SelectedJob.Path);

                // Mark as completed
                _restoreStateManager.MarkCompleted();

                // Delete the snapshot after successful restore
                StatusMessage = "Deleting snapshot after restore...";
                await _snapshotService.DeleteSnapshotAsync(snapshotToDelete.Id);

                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // Remove from UI
                    Snapshots.Remove(snapshotToDelete);
                    
                    MessageBox.Show(
                        $"Restored from snapshot:\n{snapshotToDelete.Description}\n\n" +
                        $"Duration: {duration}\n\n" +
                        $"Snapshot has been deleted.",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                StatusMessage = $"Restore completed and snapshot deleted ({duration})";
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _restoreStateManager.MarkFailed();
                
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
