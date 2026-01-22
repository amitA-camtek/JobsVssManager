using System;
using System.IO;
using System.Text.Json;
using JobsVssManager.Models;

namespace JobsVssManager.Services
{
    public class RestoreStateManager
    {
        private readonly string _stateFilePath;

        public RestoreStateManager()
        {
            const string stateDirectory = @"C:\temp";
            Directory.CreateDirectory(stateDirectory);
            _stateFilePath = Path.Combine(stateDirectory, "vss_restore_state.json");
        }

        public void SaveRestoreState(string snapshotId, string targetPath, string? snapshotDescription = null)
        {
            var state = new RestoreState
            {
                SnapshotId = snapshotId,
                TargetPath = targetPath,
                StartedAt = DateTime.Now,
                Status = "InProgress",
                SnapshotDescription = snapshotDescription
            };
            
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFilePath, json);
        }

        public RestoreState? GetPendingRestore()
        {
            if (!File.Exists(_stateFilePath))
                return null;

            try
            {
                var json = File.ReadAllText(_stateFilePath);
                var state = JsonSerializer.Deserialize<RestoreState>(json);
                return state?.Status == "InProgress" ? state : null;
            }
            catch
            {
                return null;
            }
        }

        public void MarkCompleted()
        {
            if (File.Exists(_stateFilePath))
                File.Delete(_stateFilePath);
        }

        public void MarkFailed()
        {
            if (File.Exists(_stateFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_stateFilePath);
                    var state = JsonSerializer.Deserialize<RestoreState>(json);
                    if (state != null)
                    {
                        state.Status = "Failed";
                        var updatedJson = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(_stateFilePath, updatedJson);
                    }
                }
                catch
                {
                    // Ignore errors when marking failed
                }
            }
        }
    }
}