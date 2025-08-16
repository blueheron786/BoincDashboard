using System;
using System.Globalization;
using System.Windows.Data;

namespace BoincDashboard.Converters
{
    public class LastContactConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime dateTime)
            {
                if (dateTime == DateTime.MinValue)
                {
                    return "Never";
                }

                var timeDiff = DateTime.Now - dateTime;
                
                if (timeDiff.TotalMinutes < 1)
                {
                    return "Just now";
                }
                else if (timeDiff.TotalMinutes < 60)
                {
                    return $"{(int)timeDiff.TotalMinutes}m ago";
                }
                else if (timeDiff.TotalHours < 24)
                {
                    return $"{(int)timeDiff.TotalHours}h ago";
                }
                else if (timeDiff.TotalDays < 7)
                {
                    return $"{(int)timeDiff.TotalDays}d ago";
                }
                else
                {
                    return dateTime.ToString("MMM dd");
                }
            }
            return "Unknown";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
