using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JobsVssManager.Models;

namespace JobsVssManager.Services
{
    public class VssSnapshotService
    {
        private readonly IVssProvider _provider;
        private readonly string _volume;

        public VssSnapshotService(IVssProvider provider, string volume)
        {
            _provider = provider;
            _volume = volume;
        }

        public Task<SnapshotModel> CreateSnapshotAsync(string description)
        {
            return _provider.CreateSnapshotAsync(_volume, description);
        }

        public Task<IEnumerable<SnapshotModel>> ListSnapshotsAsync()
        {
            return _provider.ListSnapshotsAsync(_volume);
        }

        public Task DeleteSnapshotAsync(string id)
        {
            return _provider.DeleteSnapshotAsync(id);
        }

        public Task<string> GetSnapshotPathAsync(string id)
        {
            return _provider.GetSnapshotPathAsync(id, _volume);
        }

        // Get specific folder path within a snapshot
        public async Task<string> GetSnapshotFolderPathAsync(string snapshotId, string originalFolderPath)
        {
            var snapshotRoot = await _provider.GetSnapshotPathAsync(snapshotId, _volume);
            var volumeRoot = Path.GetPathRoot(originalFolderPath);
            
            // Get relative path (remove volume root like "D:\")
            var relativePath = originalFolderPath?.Substring(volumeRoot?.Length ?? 0) ?? "";
            
            // Ensure snapshot root ends with backslash
            if (!snapshotRoot.EndsWith("\\"))
                snapshotRoot += "\\";
            
            // Combine without using Path.Combine (it doesn't work well with VSS UNC paths)
            return snapshotRoot + relativePath;
        }
            
        // Restore specific folder from snapshot
        public async Task RestoreFolderAsync(string snapshotId, string targetFolderPath)
        {
            var snapshotFolderPath = await GetSnapshotFolderPathAsync(snapshotId, targetFolderPath);

            // Run file operations on a background thread
            await Task.Run(() =>
            {
                // VSS shadow copy paths don't work reliably with Directory.Exists()
                // Try to access it directly and catch exceptions instead
                try
                {
                    // Attempt to enumerate to verify path accessibility
                    _ = Directory.GetFiles(snapshotFolderPath);
                    
                    if (!Directory.Exists(targetFolderPath))
                    {
                        // Target doesn't exist, create it and copy everything from snapshot
                        Directory.CreateDirectory(targetFolderPath);
                    }

                    // Smart sync: restore to exact snapshot state
                    SmartSyncDirectory(snapshotFolderPath, targetFolderPath);
                }
                catch (DirectoryNotFoundException)
                {
                    var snapshotRoot = _provider.GetSnapshotPathAsync(snapshotId, _volume).Result;
                    throw new DirectoryNotFoundException(
                        $"Folder not found in snapshot: {snapshotFolderPath}\n\n" +
                        $"Target folder: {targetFolderPath}\n" +
                        $"Snapshot root: {snapshotRoot}\n\n" +
                        "Make sure the snapshot was created successfully and hasn't expired.");
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new UnauthorizedAccessException(
                        $"Cannot access snapshot path. Make sure the application is running as Administrator.\n\n" +
                        $"Path: {snapshotFolderPath}\n" +
                        $"Error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Smart directory synchronization that:
        /// 1. Copies deleted files from snapshot
        /// 2. Overrides modified files
        /// 3. Deletes new files that don't exist in snapshot
        /// </summary>
        private void SmartSyncDirectory(string snapshotDir, string targetDir)
        {
            // Ensure target directory exists
            Directory.CreateDirectory(targetDir);

            // Get all files in snapshot (source of truth)
            var snapshotFiles = Directory.GetFiles(snapshotDir)
                .Select(f => Path.GetFileName(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Get all files in target (current state)
            var targetFiles = Directory.GetFiles(targetDir)
                .Select(f => Path.GetFileName(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 1. Copy/Override files from snapshot (deleted or modified files)
            foreach (var fileName in snapshotFiles)
            {
                var snapshotFile = Path.Combine(snapshotDir, fileName);
                var targetFile = Path.Combine(targetDir, fileName);

                bool shouldCopy = false;

                if (!File.Exists(targetFile))
                {
                    // File was deleted - restore it
                    shouldCopy = true;
                }
                else
                {
                    // File exists - check if modified
                    if (IsFileModified(snapshotFile, targetFile))
                    {
                        shouldCopy = true;
                    }
                }

                if (shouldCopy)
                {
                    File.Copy(snapshotFile, targetFile, overwrite: true);
                }
            }

            // 2. Delete new files that don't exist in snapshot
            foreach (var fileName in targetFiles)
            {
                if (!snapshotFiles.Contains(fileName))
                {
                    var targetFile = Path.Combine(targetDir, fileName);
                    try
                    {
                        File.Delete(targetFile);
                    }
                    catch
                    {
                        // Ignore if file cannot be deleted (in use, etc.)
                    }
                }
            }

            // 3. Process subdirectories
            var snapshotDirs = Directory.GetDirectories(snapshotDir)
                .Select(d => Path.GetFileName(d))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var targetDirs = Directory.GetDirectories(targetDir)
                .Select(d => Path.GetFileName(d))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Recursively sync subdirectories that exist in snapshot
            foreach (var dirName in snapshotDirs)
            {
                var snapshotSubDir = Path.Combine(snapshotDir, dirName);
                var targetSubDir = Path.Combine(targetDir, dirName);
                SmartSyncDirectory(snapshotSubDir, targetSubDir);
            }

            // Delete directories that don't exist in snapshot
            foreach (var dirName in targetDirs)
            {
                if (!snapshotDirs.Contains(dirName))
                {
                    var targetSubDir = Path.Combine(targetDir, dirName);
                    try
                    {
                        Directory.Delete(targetSubDir, recursive: true);
                    }
                    catch
                    {
                        // Ignore if directory cannot be deleted (in use, etc.)
                    }
                }
            }
        }

        /// <summary>
        /// Check if file has been modified by comparing size and last write time
        /// </summary>
        private bool IsFileModified(string snapshotFile, string targetFile)
        {
            try
            {
                var snapshotInfo = new FileInfo(snapshotFile);
                var targetInfo = new FileInfo(targetFile);

                // Compare file size (fast check)
                if (snapshotInfo.Length != targetInfo.Length)
                    return true;

                // Compare last write time (with 2 second tolerance for filesystem precision)
                var timeDiff = Math.Abs((snapshotInfo.LastWriteTime - targetInfo.LastWriteTime).TotalSeconds);
                if (timeDiff > 2)
                    return true;

                return false;
            }
            catch
            {
                // If we can't compare, assume modified
                return true;
            }
        }
    }
}
