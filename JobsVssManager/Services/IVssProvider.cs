using System.Collections.Generic;
using System.Threading.Tasks;
using JobsVssManager.Models;

namespace JobsVssManager.Services
{
    public interface IVssProvider
    {
        Task<SnapshotModel> CreateSnapshotAsync(string volume, string description);
        Task<IEnumerable<SnapshotModel>> ListSnapshotsAsync(string volume);
        Task DeleteSnapshotAsync(string snapshotId);
        Task<string> GetSnapshotPathAsync(string snapshotId, string volume);
    }
}
