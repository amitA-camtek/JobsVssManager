using System;
using System.Collections.Generic;
using System.IO;
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

        public SnapshotModel CreateSnapshot(string description)
        {
            return _provider.CreateSnapshot(_volume, description);
        }

        public IEnumerable<SnapshotModel> ListSnapshots()
        {
            return _provider.ListSnapshots(_volume);
        }

        public void DeleteSnapshot(string id)
        {
            _provider.DeleteSnapshot(id);
        }

        public string GetSnapshotPath(string id)
        {
            return _provider.GetSnapshotPath(id, _volume);
        }

        // Get specific folder path within a snapshot
        public string GetSnapshotFolderPath(string snapshotId, string originalFolderPath)
        {
            var snapshotRoot = _provider.GetSnapshotPath(snapshotId, _volume);
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
        public void RestoreFolder(string snapshotId, string targetFolderPath)
        {
            var snapshotFolderPath = GetSnapshotFolderPath(snapshotId, targetFolderPath);

            // VSS shadow copy paths don't work reliably with Directory.Exists()
            // Try to access it directly and catch exceptions instead
            try
            {
                // Attempt to enumerate to verify path accessibility
                _ = Directory.GetFiles(snapshotFolderPath);
                
                if (!Directory.Exists(targetFolderPath))
                {
                    throw new DirectoryNotFoundException($"Target folder doesn't exist: {targetFolderPath}");
                }

                // Delete existing folder
                Directory.Delete(targetFolderPath, recursive: true);

                // Copy from snapshot
                CopyDirectory(snapshotFolderPath, targetFolderPath);
            }
            catch (DirectoryNotFoundException)
            {
                var snapshotRoot = _provider.GetSnapshotPath(snapshotId, _volume);
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
        }

        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }
    }
}
