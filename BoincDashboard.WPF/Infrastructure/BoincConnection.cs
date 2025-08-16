using System;
using System.Net.Sockets;

namespace BoincDashboard.Infrastructure
{
    public class BoincConnection : IDisposable
    {
        public TcpClient? TcpClient { get; set; }
        public NetworkStream? Stream { get; set; }
        public DateTime LastUsed { get; set; }
        public bool IsConnected => TcpClient?.Connected == true && Stream?.CanRead == true && Stream?.CanWrite == true;

        public void Dispose()
        {
            Stream?.Dispose();
            TcpClient?.Dispose();
        }
    }
}
