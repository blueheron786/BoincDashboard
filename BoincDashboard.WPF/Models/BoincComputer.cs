using System;

namespace BoincDashboard.Models
{
    public class BoincComputer
    {
        public string HostName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int ActiveTasks { get; set; }
        public int TotalTasks { get; set; }
        public string Platform { get; set; } = string.Empty;
        public string BoincVersion { get; set; } = string.Empty;
        public DateTime LastContact { get; set; }
    }
}
