using System;

namespace JobsVssManager.Models
{
    public class SnapshotModel
    {
        public string Id { get; set; }          // VSS snapshot ID or shadow copy ID
        public DateTime CreatedAt { get; set; }
        public string Volume { get; set; }      // e.g. "D:"
        public string Description { get; set; }
    }
}
