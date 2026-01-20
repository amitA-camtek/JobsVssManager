using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using JobsVssManager.Models;
using JobsVssManager.Services;

namespace JobsVssManager.ViewModels
{
    public class MainViewModel : BaseViewModel
    {
        private readonly VssSnapshotService _snapshotService;
        private readonly JobsWatcherService _jobsWatcher;
        private readonly string _jobsRoot;

        public ObservableCollection<JobViewModel> Jobs { get; } = new();
        public ObservableCollection<SnapshotModel> Snapshots { get; } = new();

        private JobViewModel _selectedJob;
        public JobViewModel SelectedJob
        {
            get => _selectedJob;
            set { _selectedJob = value; OnPropertyChanged(); }
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

            LoadSnapshots();

            CreateSnapshotCommand = new RelayCommand(_ => CreateSnapshot());
            UndoCommand = new RelayCommand(_ => Undo(), _ => SelectedJob != null && Snapshots.Count > 0);
            RedoCommand = new RelayCommand(_ => Redo(), _ => SelectedJob != null && Snapshots.Count > 0);
        }

        private void OnJobFolderCreated(string fullPath)
        {
            //Jobs.Add(new JobViewModel(new JobModel { Name = Path.GetFileName(fullPath), Path = fullPath }));
        }

        private void LoadSnapshots()
        {
            Snapshots.Clear();
            foreach (var snap in _snapshotService.ListSnapshots())
                Snapshots.Add(snap);
        }

        private void CreateSnapshot()
        {
            try
            {
                var snap = _snapshotService.CreateSnapshot($"Snapshot {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Snapshots.Insert(0, snap);
                System.Windows.MessageBox.Show($"Snapshot created successfully!\nID: {snap.Id}", "Success");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to create snapshot:\n{ex.Message}", "Error");
            }
        }

        private void Undo()
        {
            if (SelectedJob == null || Snapshots.Count == 0)
                return;

            try
            {
                var snapshot = Snapshots.First();
                _snapshotService.RestoreFolder(snapshot.Id, SelectedJob.Path);
                System.Windows.MessageBox.Show($"Restored from snapshot:\n{snapshot.Description}", "Success");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to restore:\n{ex.Message}", "Error");
            }
        }

        private void Redo()
        {
            // For redo, you'd need to track which snapshot to restore to
            // This could restore from a newer snapshot if available
            if (SelectedJob == null || Snapshots.Count < 2)
                return;

            var snapshot = Snapshots[1]; // Example: use second snapshot
            _snapshotService.RestoreFolder(snapshot.Id, SelectedJob.Path);
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly System.Action<object> _execute;
        private readonly System.Predicate<object> _canExecute;

        public RelayCommand(System.Action<object> execute, System.Predicate<object> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);
        public void Execute(object parameter) => _execute(parameter);
        public event System.EventHandler CanExecuteChanged
        {
            add => System.Windows.Input.CommandManager.RequerySuggested += value;
            remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
        }
    }
}
