using System;

namespace JobsVssManager.Models
{
    public class SnapshotModel
    {
        public string Id { get; set; }          // VSS snapshot ID or shadow copy ID
        public DateTime CreatedAt { get; set; }
        public string Volume { get; set; }      // e.g. "D:"
        public string Description { get; set; }
        public DateTime ExpiresAt { get; set; } // Expiration date/time
        
        public bool IsExpired => DateTime.Now > ExpiresAt;
    }
}
