using System.Collections.Generic;
using JobsVssManager.Models;

namespace JobsVssManager.Services
{
    public interface IVssProvider
    {
        SnapshotModel CreateSnapshot(string volume, string description);
        IEnumerable<SnapshotModel> ListSnapshots(string volume);
        void DeleteSnapshot(string snapshotId);
        string GetSnapshotPath(string snapshotId, string volume);
    }
}
