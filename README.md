# Jobs VSS Manager

A Windows application for managing Volume Shadow Copy Service (VSS) snapshots for job folders.

## Features

- Create VSS snapshots for volume backup
- Restore job folders from selected snapshots
- Delete existing snapshots
- Monitor job folders with automatic detection
- Real-time status updates with operation duration tracking

## Requirements

- Windows OS with VSS support
- .NET 6.0 or higher
- Administrator privileges (required for VSS operations)

## VSS Managing

### Snapshot Descriptions

**Important:** Windows VSS (Volume Shadow Copy Service) does not natively store custom descriptions with snapshots. To work around this limitation, the application stores snapshot descriptions in a separate metadata file located at:

%AppData%\JobsVssManager\snapshots.json

**How it works:**

1. **Creating Snapshots:** When you create a snapshot with a description (e.g., "Snapshot 2026-01-20 14:30:00"), the description is saved to the metadata file along with the snapshot ID.

2. **Loading Snapshots:** When the application loads, it reads all VSS snapshots from Windows and retrieves their descriptions from the metadata file. If no metadata exists for a snapshot (e.g., snapshots created externally), a default description is used.

3. **Deleting Snapshots:** When a snapshot is deleted, its metadata entry is also removed from the JSON file.

4. **Persistence:** Snapshot descriptions persist across application restarts and are automatically synchronized with VSS snapshot lifecycle.

**Note:** The creation time for each snapshot is accurately retrieved from VSS and is unique for each snapshot. Only the custom description requires separate storage.

### Administrator Privileges

VSS operations require administrator privileges. Make sure to run the application as Administrator, otherwise you'll receive an error when the application starts.

## Usage

1. Launch the application as Administrator
2. Select a job folder from the left panel
3. Create a snapshot using the "Create Snapshot" button
4. To restore: Select a job folder, select a snapshot, and click "Restore"
5. Monitor operation progress and duration in the status bar

## Configuration

Edit `appsettings.json` to configure:

- **JobsRoot:** Root directory for job folders
- **VssMode:** VSS provider mode (VssAdmin recommended)