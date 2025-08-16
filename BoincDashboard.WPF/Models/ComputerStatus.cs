using System;

namespace BoincDashboard.Models
{
    public class ComputerStatus
    {
        public string HostName { get; set; } = string.Empty;
        public DateTime LastSeen { get; set; }
        public bool IsOnline { get; set; }
    }
}
