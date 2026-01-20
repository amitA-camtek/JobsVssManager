using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JobsVssManager.Models;

namespace JobsVssManager.Services
{
    public class VssAdminProvider : IVssProvider
    {
        private readonly Dictionary<string, string> _snapshotDevices = new();
        private readonly string _metadataFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JobsVssManager",
            "snapshots.json");

        public VssAdminProvider()
        {
            // Check if running as administrator
            if (!IsAdministrator())
            {
                throw new UnauthorizedAccessException(
                    "This application requires Administrator privileges to use VSS.\n" +
                    "Please restart Visual Studio or the application as Administrator.");
            }
            
            // Ensure metadata directory exists
            var metadataDir = Path.GetDirectoryName(_metadataFile);
            if (!string.IsNullOrEmpty(metadataDir))
            {
                Directory.CreateDirectory(metadataDir);
            }
        }

        private bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private async Task<string> RunAsync(string args)
        {
            return await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "vssadmin",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null)
                    throw new Exception("Failed to start vssadmin process.");
                    
                var output = p.StandardOutput.ReadToEnd();
                var error = p.StandardError.ReadToEnd();
                p.WaitForExit();
                
                if (p.ExitCode != 0)
                {
                    var errorMsg = !string.IsNullOrWhiteSpace(error) ? error : output;
                    throw new Exception(
                        $"vssadmin failed with exit code {p.ExitCode}.\n" +
                        $"Command: vssadmin {args}\n" +
                        $"Output: {errorMsg}\n\n" +
                        "Make sure the application is running as Administrator.");
                }
                
                return output;
            });
        }

        public async Task<SnapshotModel> CreateSnapshotAsync(string volume, string description)
        {
            return await Task.Run(() =>
            {
                // Use WMI to create shadow copy
                using var shadowClass = new ManagementClass("Win32_ShadowCopy");
                using var inParams = shadowClass.GetMethodParameters("Create");
                
                // Ensure volume format is correct (e.g., "C:\")
                if (!volume.EndsWith("\\"))
                    volume += "\\";
                
                inParams["Volume"] = volume;
                inParams["Context"] = "ClientAccessible";
                
                using var outParams = shadowClass.InvokeMethod("Create", inParams, null);
                
                if (outParams == null || (uint)outParams["ReturnValue"] != 0)
                {
                    throw new Exception($"Failed to create shadow copy. Return code: {outParams?["ReturnValue"]}");
                }
                
                var shadowId = (string)outParams["ShadowID"];
                
                // Query the newly created shadow to get device path
                using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_ShadowCopy WHERE ID='{shadowId}'");
                using var results = searcher.Get();
                
                string? devicePath = null;
                DateTime creationTime = DateTime.Now;
                
                foreach (ManagementObject obj in results)
                {
                    devicePath = obj["DeviceObject"]?.ToString();
                    var installDate = obj["InstallDate"]?.ToString();
                    if (!string.IsNullOrEmpty(installDate))
                    {
                        creationTime = ManagementDateTimeConverter.ToDateTime(installDate);
                    }
                    break;
                }
                
                if (!string.IsNullOrEmpty(devicePath))
                {
                    lock (_snapshotDevices)
                    {
                        _snapshotDevices[shadowId] = devicePath;
                    }
                }
                
                var snapshot = new SnapshotModel
                {
                    Id = shadowId,
                    Volume = volume,
                    CreatedAt = creationTime,
                    Description = description
                };
                
                // Save description to metadata file
                SaveSnapshotMetadata(shadowId, description);
                
                return snapshot;
            });
        }

        public async Task<IEnumerable<SnapshotModel>> ListSnapshotsAsync(string volume)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Ensure volume format is correct (e.g., "C:\")
                    if (!volume.EndsWith("\\"))
                        volume += "\\";

                    var snapshots = new List<SnapshotModel>();
                    var metadata = LoadSnapshotMetadata();

                    // Use WMI to query shadow copies
                    using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ShadowCopy");
                    using var results = searcher.Get();

                    foreach (ManagementObject obj in results)
                    {
                        var shadowId = obj["ID"]?.ToString();
                        var origVolume = obj["VolumeName"]?.ToString();
                        var devicePath = obj["DeviceObject"]?.ToString();
                        var installDate = obj["InstallDate"]?.ToString();

                        if (string.IsNullOrEmpty(shadowId))
                            continue;

                        // Parse creation time from WMI format
                        DateTime creationTime = DateTime.Now;
                        if (!string.IsNullOrEmpty(installDate))
                        {
                            try
                            {
                                creationTime = ManagementDateTimeConverter.ToDateTime(installDate);
                            }
                            catch
                            {
                                // Use current time if parsing fails
                            }
                        }

                        // Cache device path
                        if (!string.IsNullOrEmpty(devicePath))
                        {
                            lock (_snapshotDevices)
                            {
                                _snapshotDevices[shadowId] = devicePath;
                            }
                        }

                        // Get description from metadata, or use default
                        var description = metadata.TryGetValue(shadowId, out var desc) 
                            ? desc 
                            : "CamTek VSS Snapshot";

                        snapshots.Add(new SnapshotModel
                        {
                            Id = shadowId,
                            Volume = origVolume ?? "",
                            CreatedAt = creationTime,
                            Description = description
                        });
                    }

                    return (IEnumerable<SnapshotModel>)snapshots;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to list snapshots: {ex.Message}");
                    return Array.Empty<SnapshotModel>();
                }
            });
        }

        public async Task DeleteSnapshotAsync(string snapshotId)
        {
            await RunAsync($"delete shadows /shadow={snapshotId} /quiet");
            lock (_snapshotDevices)
            {
                _snapshotDevices.Remove(snapshotId);
            }
            
            // Remove metadata
            RemoveSnapshotMetadata(snapshotId);
        }

        private void SaveSnapshotMetadata(string snapshotId, string description)
        {
            try
            {
                var metadata = LoadSnapshotMetadata();
                metadata[snapshotId] = description;
                
                var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_metadataFile, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save snapshot metadata: {ex.Message}");
            }
        }

        private Dictionary<string, string> LoadSnapshotMetadata()
        {
            try
            {
                if (File.Exists(_metadataFile))
                {
                    var json = File.ReadAllText(_metadataFile);
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(json) 
                           ?? new Dictionary<string, string>();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load snapshot metadata: {ex.Message}");
            }
            
            return new Dictionary<string, string>();
        }

        private void RemoveSnapshotMetadata(string snapshotId)
        {
            try
            {
                var metadata = LoadSnapshotMetadata();
                if (metadata.Remove(snapshotId))
                {
                    var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_metadataFile, json);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to remove snapshot metadata: {ex.Message}");
            }
        }

        public async Task<string> GetSnapshotPathAsync(string snapshotId, string volume)
        {
            return await Task.Run(() =>
            {
                // Return cached device path
                lock (_snapshotDevices)
                {
                    if (_snapshotDevices.TryGetValue(snapshotId, out var devicePath))
                    {
                        return devicePath.TrimEnd('\\') + "\\";
                    }
                }

                // Query if not cached
                var snapshots = ListSnapshotsAsync(volume).Result;
                var snapshot = snapshots.FirstOrDefault(s => s.Id == snapshotId);
                
                lock (_snapshotDevices)
                {
                    if (snapshot != null && _snapshotDevices.TryGetValue(snapshotId, out var devicePath))
                    {
                        return devicePath.TrimEnd('\\') + "\\";
                    }
                }

                throw new Exception($"Snapshot device path not found for ID: {snapshotId}");
            });
        }
    }
}
