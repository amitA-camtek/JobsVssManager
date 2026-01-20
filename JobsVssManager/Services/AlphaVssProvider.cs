    using System;
using System.Collections.Generic;
using JobsVssManager.Models;

namespace JobsVssManager.Services
{
    public class AlphaVssProvider : IVssProvider
    {
        public SnapshotModel CreateSnapshot(string volume, string description)
        {
            throw new NotImplementedException(
                "AlphaVssProvider requires AlphaVSS library which is not available on NuGet.\n" +
                "Please use VssAdminProvider instead by setting VssMode to 'VssAdmin' in appsettings.json");
        }

        public IEnumerable<SnapshotModel> ListSnapshots(string volume)
        {
            throw new NotImplementedException("Use VssAdminProvider instead.");
        }

        public void DeleteSnapshot(string snapshotId)
        {
            throw new NotImplementedException("Use VssAdminProvider instead.");
        }

        public string GetSnapshotPath(string snapshotId, string volume)
        {
            throw new NotImplementedException("Use VssAdminProvider instead.");
        }
    }
}
