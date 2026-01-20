using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JobsVssManager.Models;

namespace JobsVssManager.Services
{
    public class VssAdminProvider : IVssProvider
    {
        private readonly Dictionary<string, string> _snapshotDevices = new();

        public VssAdminProvider()
        {
            // Check if running as administrator
            if (!IsAdministrator())
            {
                throw new UnauthorizedAccessException(
                    "This application requires Administrator privileges to use VSS.\n" +
                    "Please restart Visual Studio or the application as Administrator.");
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
                
                return new SnapshotModel
                {
                    Id = shadowId,
                    Volume = volume,
                    CreatedAt = creationTime,
                    Description = description
                };
            });
        }

        public async Task<IEnumerable<SnapshotModel>> ListSnapshotsAsync(string volume)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var output = RunAsync("list shadows").Result;
                    
                    // Check if no snapshots exist
                    if (string.IsNullOrWhiteSpace(output) || 
                        output.Contains("No items found") ||
                        !output.Contains("Shadow Copy ID"))
                    {
                        return Array.Empty<SnapshotModel>();
                    }

                    var snapshots = new List<SnapshotModel>();

                    // Parse output for each shadow copy
                    var shadowBlocks = Regex.Split(output, @"(?=Shadow Copy ID:)");
                    
                    foreach (var block in shadowBlocks)
                    {
                        var idMatch = Regex.Match(block, @"Shadow Copy ID:\s*(\{[^\}]+\})");
                        var volumeMatch = Regex.Match(block, @"Original Volume:\s*([^\r\n]+)");
                        var timeMatch = Regex.Match(block, @"Creation Time:\s*([^\r\n]+)");
                        var deviceMatch = Regex.Match(block, @"Shadow Copy Volume:\s*([^\r\n]+)");

                        //if (idMatch.Success && volumeMatch.Success)
                        {
                            var origVolume = volumeMatch.Groups[1].Value.Trim();
                            //if (origVolume.StartsWith(volume, StringComparison.OrdinalIgnoreCase))
                            {
                                var id = idMatch.Groups[1].Value;
                                
                                if (deviceMatch.Success)
                                {
                                    lock (_snapshotDevices)
                                    {
                                        _snapshotDevices[id] = deviceMatch.Groups[1].Value.Trim();
                                    }
                                }

                                snapshots.Add(new SnapshotModel
                                {
                                    Id = id,
                                    Volume = origVolume,
                                    CreatedAt = timeMatch.Success ? DateTime.Parse(timeMatch.Groups[1].Value) : DateTime.Now,
                                    Description = "CamTek VSS Snapshot"
                                });
                            }
                        }
                    }

                    return (IEnumerable<SnapshotModel>)snapshots;
                }
                catch (Exception ex)
                {
                    // Return empty list if listing fails (e.g., no snapshots exist)
                    if (ex.Message.Contains("No items found") || ex.Message.Contains("No shadow copies"))
                    {
                        return Array.Empty<SnapshotModel>();
                    }
                    throw;
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
