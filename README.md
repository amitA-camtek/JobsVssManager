# JobsVssManager

A Windows desktop application for managing Volume Shadow Copy Service (VSS) snapshots of job folders with real-time monitoring and one-click restoration capabilities.

## Overview

JobsVssManager is a WPF application built on .NET 6 that provides a user-friendly interface for creating, managing, and restoring VSS snapshots of designated job folders. It's designed for scenarios where you need quick backup and recovery of folder contents, such as development environments, testing scenarios, or data protection workflows.

## Features

### Core Functionality
- **Real-time Folder Monitoring** - Automatically detects new job folders as they're created
- **VSS Snapshot Creation** - Creates point-in-time Volume Shadow Copy snapshots with timestamps
- **Snapshot Management** - List, view, and delete existing snapshots
- **One-Click Restoration** - Restore folders to previous snapshot states (Undo/Redo operations)
- **Administrator Mode Required** - Ensures proper VSS permissions for all operations

### Technical Features
- **Multiple VSS Provider Implementations**:
  - **VssAdminProvider** (WMI) - Uses `Win32_ShadowCopy` WMI class (Recommended, default)
  - **NativeVssProvider** (COM) - Direct Windows VSS COM API via P/Invoke
  - **AlphaVssProvider** - Placeholder for legacy AlphaVSS library integration
- **MVVM Architecture** - Clean separation of concerns with ViewModels, Models, and Views
- **File System Watcher** - Background monitoring of job directories
- **Configuration-based** - Customizable through `appsettings.json`

## Requirements

- **OS**: Windows 10/11 or Windows Server 2016+
- **Framework**: .NET 6.0 Runtime (Windows Desktop)
- **Privileges**: Administrator rights (required for VSS operations)
- **Services**: Volume Shadow Copy Service must be enabled and running

## Installation

1. Clone the repository:
  git clone https://github.com/amitA-camtek/JobsVssManager.git

2. Open the solution in Visual Studio 2022 or later

3. Restore NuGet packages:
- `System.Management` (for WMI-based VSS provider)

4. Build the solution (Release or Debug configuration)

5. **Run as Administrator** - Right-click the executable and select "Run as administrator"

## Configuration

Edit `appsettings.json` in the application directory:
{ "JobsRoot": "C:\job\S7 test-x20-machine", "VssMode": "VssAdmin" }

### Configuration Options

| Setting | Description | Options |
|---------|-------------|---------|
| `JobsRoot` | Root directory containing job folders to monitor | Any valid Windows path |
| `VssMode` | VSS provider implementation to use | `VssAdmin` (WMI), `NativeVss` (COM), `AlphaVss` |

### Recommended Settings
- **VssMode**: `VssAdmin` - Most reliable, uses Windows Management Instrumentation
- **JobsRoot**: Set to your job/project root directory

## Usage

### Creating a Snapshot
1. Launch the application as Administrator
2. Click the **"Create Snapshot"** button
3. A new snapshot is created with a timestamp (e.g., "Snapshot 2026-01-20 14:30:45")
4. Snapshot appears in the snapshots list

### Restoring from Snapshot (Undo)
1. Select a job folder from the left panel
2. Select a snapshot from the snapshots list
3. Click **"Undo"** to restore the folder to the selected snapshot state
4. Confirm the operation

### Deleting Snapshots
- Snapshots can be managed through Windows' `vssadmin` command:
vssadmin list shadows vssadmin delete shadows /shadow={shadow-id}

## Architecture

### Project Structure
JobsVssManager/ 
                                    ├── Models/ 
                                    │   
                                    ├── JobModel.cs          
# Job folder representation         │   
                                    └── SnapshotModel.cs     
# VSS snapshot representation       ├── ViewModels/ │   
                                    ├── BaseViewModel.cs     
# INotifyPropertyChanged base class │   
                                    ├── MainViewModel.cs     
                # Main window logic │   └── JobViewModel.cs      # Job folder view model ├── Views/ │   └── MainWindow.xaml      # Main application window ├── Services/ │   ├── IVssProvider.cs      # VSS provider interface │   ├── VssAdminProvider.cs  # WMI-based VSS (Recommended) │   ├── NativeVssProvider.cs # COM-based VSS │   ├── VssSnapshotService.cs# Snapshot operations wrapper │   └── JobsWatcherService.cs# File system monitoring └── App.xaml.cs              # Application entry point

### Design Patterns
- **MVVM (Model-View-ViewModel)** - UI separation
- **Strategy Pattern** - Pluggable VSS provider implementations
- **Observer Pattern** - File system monitoring
- **Dependency Injection** - Service initialization in App.xaml.cs

## VSS Provider Comparison

| Provider | Technology | Pros | Cons | Status |
|----------|-----------|------|------|--------|
| VssAdminProvider | WMI (`Win32_ShadowCopy`) | Reliable, simple, no COM issues | Slightly slower | ✅ Recommended |
| NativeVssProvider | COM API (P/Invoke) | Full VSS control, best performance | Complex, COM interop issues | ⚠️ Experimental |
| AlphaVssProvider | AlphaVSS library | Feature-rich | Deprecated, not available on NuGet | ❌ Placeholder only |

## Troubleshooting

### "Application requires Administrator privileges"
- Right-click the executable → "Run as administrator"
- Or set the manifest to require administrator elevation

### "Volume Shadow Copy Service is not running"
1. Press Win+R, type `services.msc`
2. Find "Volume Shadow Copy"
3. Set to "Automatic" or "Manual" and start the service

### "Failed to create snapshot"
- Ensure sufficient disk space (at least 100MB free on target volume)
- Check that VSS providers are installed: `vssadmin list providers`
- Verify volume supports VSS: `vssadmin list volumes`

### Snapshots not accessible
- VSS shadow copy device paths don't work with standard `Directory.Exists()`
- The application handles this internally by attempting direct access

## Known Limitations

- Only works on NTFS volumes with VSS support
- Requires Administrator privileges for all VSS operations
- Shadow copies are non-persistent by default (may expire)
- COM-based `NativeVssProvider` has platform-specific COM interface issues

## Contributing

Contributions are welcome! Please:
1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add some amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

This project is proprietary software. All rights reserved.

## Acknowledgments

- Built with Windows Volume Shadow Copy Service API
- Uses Windows Management Instrumentation (WMI) for VSS operations
- Inspired by the need for quick backup/restore in development workflows

## Support

For issues, questions, or feature requests, please open an issue on the GitHub repository:
https://github.com/amitA-camtek/JobsVssManager/issues

## Version History

### v1.0.0 (Initial Release)
- WPF application with MVVM architecture
- VssAdminProvider (WMI-based) implementation
- Real-time folder monitoring
- Snapshot creation and restoration
- Basic error handling and logging

---

**Note**: This application modifies system shadow copies. Always test in a non-production environment first and ensure you have proper backups before performing restore operations.

