using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Security.Cryptography;

namespace BoincDashboard
{
    public class BoincHost
    {
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public int Port { get; set; } = 31416;
    }

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
    }

    public partial class MainWindow : Window
    {
        private DispatcherTimer _refreshTimer = new();
        private List<BoincHost> _hosts = new();

        public MainWindow()
        {
            InitializeComponent();
            InitializeHosts();
            SetupRefreshTimer();
            _ = LoadAllTasks();
        }

        private void InitializeHosts()
        {
            // Configure your BOINC hosts here
            _hosts = new List<BoincHost>
            {
                new BoincHost 
                { 
                    Name = "bismillah", 
                    Address = "127.0.0.1", 
                    Password = "93a2b7718e61db20a693dec7eb48e075" 
                },
                // new BoincHost 
                // { 
                //     Name = "Laptop", 
                //     Address = "192.168.1.101", 
                //     Password = "your_gui_rpc_password_here" 
                // },
                // // Add IPv6 hosts if needed
                // new BoincHost 
                // { 
                //     Name = "Server", 
                //     Address = "2001:db8::1", 
                //     Password = "your_gui_rpc_password_here" 
                // }
            };
        }

        private void SetupRefreshTimer()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(5) // Refresh every 5 minutes
            };
            _refreshTimer.Tick += async (s, e) => await LoadAllTasks();
            _refreshTimer.Start();
        }

        private async Task LoadAllTasks()
        {
            var allTasks = new List<BoincTask>();

            foreach (var host in _hosts)
            {
                try
                {
                    var hostTasks = await GetTasksFromHost(host);
                    allTasks.AddRange(hostTasks);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error connecting to {host.Name}: {ex.Message}");
                    // Add error indicator to UI if needed
                }
            }

            // Update UI on main thread
            Dispatcher.Invoke(() =>
            {
                TasksDataGrid.ItemsSource = allTasks;
                LastUpdatedLabel.Text = $"Last Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            });
        }

        private async Task<List<BoincTask>> GetTasksFromHost(BoincHost host)
        {
            var tasks = new List<BoincTask>();

            using var client = new TcpClient();
            
            // Handle both IPv4 and IPv6
            if (host.Address.Contains(':'))
            {
                await client.ConnectAsync(System.Net.IPAddress.Parse(host.Address), host.Port);
            }
            else
            {
                await client.ConnectAsync(host.Address, host.Port);
            }

            using var stream = client.GetStream();

            // Authenticate
            await AuthenticateAsync(stream, host.Password);

            // Get project list first to map URLs to names
            var projects = await SendRpcCommand(stream, "<get_project_status/>");
            var projectMap = ParseProjects(projects);

            // Get active tasks
            var tasksXml = await SendRpcCommand(stream, "<get_results/>");
            var taskElements = XDocument.Parse(tasksXml)
                .Descendants("result")
                .Where(r => r.Element("active_task") != null);

            foreach (var taskElement in taskElements)
            {
                var projectUrl = taskElement.Element("project_url")?.Value ?? "";
                var projectName = projectMap.ContainsKey(projectUrl) ? projectMap[projectUrl] : projectUrl;

                var activeTask = taskElement.Element("active_task");
                var state = GetTaskState(taskElement.Element("state")?.Value ?? "");
                
                var task = new BoincTask
                {
                    HostName = host.Name,
                    ProjectName = projectName,
                    TaskName = taskElement.Element("name")?.Value ?? "",
                    State = state,
                    ProgressPercent = double.Parse(activeTask?.Element("fraction_done")?.Value ?? "0") * 100,
                    ElapsedTime = TimeSpan.FromSeconds(double.Parse(activeTask?.Element("elapsed_time")?.Value ?? "0")),
                    RemainingTime = TimeSpan.FromSeconds(double.Parse(activeTask?.Element("estimated_cpu_time_remaining")?.Value ?? "0")),
                    Deadline = UnixTimeStampToDateTime(double.Parse(taskElement.Element("report_deadline")?.Value ?? "0"))
                };

                tasks.Add(task);
            }

            return tasks;
        }

        private async Task AuthenticateAsync(NetworkStream stream, string password)
        {
            // Get nonce
            await SendRpcCommand(stream, "<auth1/>");
            var nonceResponse = await ReadResponse(stream);
            var nonceDoc = XDocument.Parse(nonceResponse);
            var nonce = nonceDoc.Descendants("nonce").FirstOrDefault()?.Value;

            if (string.IsNullOrEmpty(nonce))
                throw new Exception("Failed to get authentication nonce");

            // Calculate hash
            var hasher = MD5.Create();
            var hashBytes = hasher.ComputeHash(Encoding.UTF8.GetBytes(nonce + password));
            var hash = Convert.ToHexString(hashBytes).ToLower();

            // Send auth2
            var auth2Command = $"<auth2><nonce_hash>{hash}</nonce_hash></auth2>";
            await SendRpcCommand(stream, auth2Command);
        }

        private async Task<string> SendRpcCommand(NetworkStream stream, string command)
        {
            var requestBytes = Encoding.UTF8.GetBytes(command);
            await stream.WriteAsync(requestBytes);
            return await ReadResponse(stream);
        }

        private async Task<string> ReadResponse(NetworkStream stream)
        {
            var buffer = new byte[65536];
            var bytesRead = await stream.ReadAsync(buffer);
            return Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }

        private Dictionary<string, string> ParseProjects(string projectsXml)
        {
            var projectMap = new Dictionary<string, string>();
            var doc = XDocument.Parse(projectsXml);
            
            foreach (var project in doc.Descendants("project"))
            {
                var url = project.Element("master_url")?.Value;
                var name = project.Element("project_name")?.Value;
                if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(name))
                {
                    projectMap[url] = name;
                }
            }
            
            return projectMap;
        }

        private string GetTaskState(string stateValue)
        {
            return stateValue switch
            {
                "1" => "Downloading",
                "2" => "Ready to Run",
                "3" => "Computing",
                "4" => "Uploading",
                "5" => "Complete",
                "6" => "Aborted",
                _ => "Unknown"
            };
        }

        private DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)unixTimeStamp).DateTime;
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadAllTasks();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement settings dialog
            MessageBox.Show("Settings dialog not yet implemented.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AutoRefreshToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle)
            {
                if (toggle.IsChecked == true)
                {
                    _refreshTimer.Start();
                }
                else
                {
                    _refreshTimer.Stop();
                }
            }
        }

        private void HostFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // TODO: Implement host filtering
        }

        private void StatusFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // TODO: Implement status filtering
        }
    }
}