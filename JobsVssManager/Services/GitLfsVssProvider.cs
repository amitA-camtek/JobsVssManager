using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using JobsVssManager.Models;

namespace JobsVssManager.Services
{
    public class GitLfsVssProvider : IVssProvider
    {
        private readonly string _snapshotsRootPath;
        private readonly string _metadataFile;

        public GitLfsVssProvider(string snapshotsRootPath)
        {
            _snapshotsRootPath = snapshotsRootPath;
            _metadataFile = Path.Combine(_snapshotsRootPath, "snapshots.json");
            
            Directory.CreateDirectory(_snapshotsRootPath);
        }

        public async Task<SnapshotModel> CreateSnapshotAsync(string volume, string description)
        {
            var snapshotId = Guid.NewGuid().ToString();
            var timestamp = DateTime.Now;
            var snapshotPath = Path.Combine(_snapshotsRootPath, snapshotId);

            try
            {
                Directory.CreateDirectory(snapshotPath);

                // Initialize Git repository for this snapshot
                InitializeGitRepository(snapshotPath);

                // Copy volume contents
                await Task.Run(() => CopyDirectory(volume.TrimEnd('\\'), snapshotPath));

                // Commit to Git
                RunGitCommand(snapshotPath, "add .");
                var commitMsg = $"Snapshot: {description}\nVolume: {volume}\nCreated: {timestamp:yyyy-MM-dd HH:mm:ss}";
                RunGitCommand(snapshotPath, $"commit -m \"{EscapeGitMessage(commitMsg)}\"");

                var snapshot = new SnapshotModel
                {
                    Id = snapshotId,
                    Volume = volume,
                    CreatedAt = timestamp,
                    Description = description
                };

                await SaveMetadataAsync(snapshot);
                return snapshot;
            }
            catch (Exception ex)
            {
                // Cleanup on failure
                if (Directory.Exists(snapshotPath))
                    Directory.Delete(snapshotPath, recursive: true);

                throw new Exception($"Failed to create snapshot: {ex.Message}", ex);
            }
        }

        public async Task<IEnumerable<SnapshotModel>> ListSnapshotsAsync(string volume)
        {
            var snapshots = await LoadMetadataAsync();

            // Filter by volume
            if (!string.IsNullOrEmpty(volume))
            {
                var normalizedVolume = volume.TrimEnd('\\');
                snapshots = snapshots
                    .Where(s => s.Volume?.TrimEnd('\\').Equals(normalizedVolume, StringComparison.OrdinalIgnoreCase) ?? false)
                    .ToList();
            }

            return snapshots.OrderByDescending(s => s.CreatedAt);
        }

        public async Task DeleteSnapshotAsync(string snapshotId)
        {
            var snapshotPath = Path.Combine(_snapshotsRootPath, snapshotId);

            if (!Directory.Exists(snapshotPath))
                throw new DirectoryNotFoundException($"Snapshot {snapshotId} not found");

            // Delete entire directory including Git repository
            Directory.Delete(snapshotPath, recursive: true);

            await RemoveMetadataAsync(snapshotId);
        }

        public async Task<string> GetSnapshotPathAsync(string snapshotId, string volume)
        {
            var snapshotPath = Path.Combine(_snapshotsRootPath, snapshotId);

            if (!Directory.Exists(snapshotPath))
                throw new DirectoryNotFoundException($"Snapshot {snapshotId} not found at {snapshotPath}");

            return await Task.FromResult(snapshotPath);
        }

        /// <summary>
        /// Delete snapshot Git repository after restore completes
        /// Call this after successfully restoring a snapshot
        /// </summary>
        public async Task DeleteSnapshotAfterRestoreAsync(string snapshotId)
        {
            await DeleteSnapshotAsync(snapshotId);
        }

        private void InitializeGitRepository(string repositoryPath)
        {
            // Initialize Git
            RunGitCommand(repositoryPath, "init");
            RunGitCommand(repositoryPath, "lfs install");
            
            // Configure Git user identity for this repository
            RunGitCommand(repositoryPath, "config user.name \"JobsVssManager\"");
            RunGitCommand(repositoryPath, "config user.email \"jobsvss@automated.local\"");
            
            // Create .gitattributes for LFS tracking
            var gitAttributesPath = Path.Combine(repositoryPath, ".gitattributes");
            File.WriteAllText(gitAttributesPath, 
                "*.zip filter=lfs diff=lfs merge=lfs -text\n" +
                "*.tar filter=lfs diff=lfs merge=lfs -text\n" +
                "*.gz filter=lfs diff=lfs merge=lfs -text\n" +
                "*.bin filter=lfs diff=lfs merge=lfs -text\n" +
                "*.dll filter=lfs diff=lfs merge=lfs -text\n" +
                "*.exe filter=lfs diff=lfs merge=lfs -text\n" +
                "*.pdb filter=lfs diff=lfs merge=lfs -text\n" +
                "*.log filter=lfs diff=lfs merge=lfs -text\n");
            
            // Initial commit for .gitattributes
            RunGitCommand(repositoryPath, "add .gitattributes");
            RunGitCommand(repositoryPath, "commit -m \"Initialize Git LFS\"");
            
            // Rename to main branch
            try { RunGitCommand(repositoryPath, "branch -M main"); } catch { }
        }

        private void CopyDirectory(string sourceDir, string destDir)
        {
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

            Directory.CreateDirectory(destDir);

            // Copy files
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                try
                {
                    var fileName = Path.GetFileName(file);
                    
                    // Skip .git directory and metadata file
                    if (fileName == ".git" || fileName == "snapshots.json" || fileName == ".gitattributes")
                        continue;
                    
                    var destFile = Path.Combine(destDir, fileName);
                    File.Copy(file, destFile, overwrite: true);
                }
                catch { /* Skip inaccessible files */ }
            }

            // Copy subdirectories recursively
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                try
                {
                    var dirName = Path.GetFileName(dir);
                    
                    // Skip .git directory
                    if (dirName == ".git")
                        continue;
                    
                    var destSubDir = Path.Combine(destDir, dirName);
                    CopyDirectory(dir, destSubDir);
                }
                catch { /* Skip inaccessible directories */ }
            }
        }

        private async Task SaveMetadataAsync(SnapshotModel snapshot)
        {
            var snapshots = (await LoadMetadataAsync()).ToList();
            snapshots.RemoveAll(s => s.Id == snapshot.Id);
            snapshots.Add(snapshot);

            var json = JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_metadataFile, json);
        }

        private async Task RemoveMetadataAsync(string snapshotId)
        {
            var snapshots = (await LoadMetadataAsync()).ToList();
            snapshots.RemoveAll(s => s.Id == snapshotId);

            var json = JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_metadataFile, json);
        }

        private async Task<List<SnapshotModel>> LoadMetadataAsync()
        {
            if (!File.Exists(_metadataFile))
                return new List<SnapshotModel>();

            try
            {
                var json = await File.ReadAllTextAsync(_metadataFile);
                return JsonSerializer.Deserialize<List<SnapshotModel>>(json) ?? new List<SnapshotModel>();
            }
            catch
            {
                return new List<SnapshotModel>();
            }
        }

        private string EscapeGitMessage(string message)
        {
            return message.Replace("\"", "\\\"").Replace("\n", " ");
        }

        private string RunGitCommand(string workingDirectory, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new Exception("Failed to start git process");

            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new Exception($"Git command failed: git {arguments}\n{error}");

            return output.ToString();
        }
    }
}