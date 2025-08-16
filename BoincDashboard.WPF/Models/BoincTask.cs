using System;

namespace BoincDashboard.Models
{
    public class BoincTask
    {
        public string HostName { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string TaskName { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public double ProgressPercent { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public TimeSpan RemainingTime { get; set; }
        public DateTime Deadline { get; set; }
        
        // Formatted time properties that include days when > 24 hours
        public string ElapsedTimeFormatted => FormatTimeSpan(ElapsedTime);
        public string RemainingTimeFormatted => FormatTimeSpan(RemainingTime);
        
        private static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalDays >= 1)
            {
                return $"{(int)ts.TotalDays}d {ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            }
            return $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        }
        
        // Computed property to show appropriate time info based on task state
        public string TimeInfo => State == "Complete" 
            ? $"Runtime: {ElapsedTimeFormatted}"
            : RemainingTime.TotalSeconds > 0 
                ? $"Remaining: {RemainingTimeFormatted}" 
                : $"Elapsed: {ElapsedTimeFormatted}";
    }
}
