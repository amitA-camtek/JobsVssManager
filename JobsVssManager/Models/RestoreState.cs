namespace JobsVssManager.Models
{
    public class RestoreState
    {
        public string? SnapshotId { get; set; }
        public string? TargetPath { get; set; }
        public DateTime StartedAt { get; set; }
        public string Status { get; set; } = "InProgress"; // InProgress, Completed, Failed
        public string? SnapshotDescription { get; set; }
    }
}