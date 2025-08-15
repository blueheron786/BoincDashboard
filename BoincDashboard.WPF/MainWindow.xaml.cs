using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;

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
            
            // Load tasks after the window is fully loaded to avoid deadlock
            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadAllTasks();
        }

        private void InitializeHosts()
        {
            // Load hosts from hosts.json file
            try
            {
                var hostsJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hosts.json");
                if (File.Exists(hostsJsonPath))
                {
                    var jsonContent = File.ReadAllText(hostsJsonPath);
                    _hosts = JsonConvert.DeserializeObject<List<BoincHost>>(jsonContent) ?? new List<BoincHost>();
                    Console.WriteLine($"Loaded {_hosts.Count} hosts from hosts.json");
                    
                    foreach (var host in _hosts)
                    {
                        Console.WriteLine($"Host: {host.Name} at {host.Address}");
                    }
                }
                else
                {
                    Console.WriteLine($"hosts.json file not found at: {hostsJsonPath}");
                    _hosts = new List<BoincHost>();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading hosts from JSON: {ex.Message}");
                MessageBox.Show($"Error loading hosts configuration: {ex.Message}", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                _hosts = new List<BoincHost>();
            }
        }

        private void SetupRefreshTimer()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1),
            };
            _refreshTimer.Tick += async (s, e) => await LoadAllTasks();
            _refreshTimer.Start();
        }

        private async Task LoadAllTasks()
        {
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();
            var allTasks = new List<BoincTask>();
            var errorMessages = new List<string>();

            foreach (var host in _hosts)
            {
                try
                {
                    var hostTasks = await GetTasksFromHost(host);
                    allTasks.AddRange(hostTasks);
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Error connecting to {host.Name} ({host.Address}): {ex.Message}";
                    errorMessages.Add(errorMsg);
                    Console.WriteLine(errorMsg);
                    Console.WriteLine($"Full exception: {ex}");
                }
            }

            // Update UI on main thread
            Dispatcher.Invoke(() =>
            {
                TasksDataGrid.ItemsSource = allTasks;
                // Default sort by ElapsedTime descending
                var collectionView = System.Windows.Data.CollectionViewSource.GetDefaultView(TasksDataGrid.ItemsSource);
                if (collectionView != null)
                {
                    collectionView.SortDescriptions.Clear();
                    collectionView.SortDescriptions.Add(new System.ComponentModel.SortDescription("ElapsedTime", System.ComponentModel.ListSortDirection.Descending));
                }
                var statusText = $"Last Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                if (errorMessages.Count > 0)
                {
                    statusText += $" | Errors: {errorMessages.Count} host(s) unreachable";
                }
                if (allTasks.Count == 0 && errorMessages.Count > 0)
                {
                    // Show detailed errors if no tasks found
                    MessageBox.Show(
                        string.Join("\n", errorMessages),
                        "Connection Errors",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                LastUpdatedLabel.Text = statusText;
            });
            stopwatch.Stop();
            Console.WriteLine($"Total refresh time: {stopwatch.ElapsedMilliseconds} ms");
        }

        private async Task<List<BoincTask>> GetTasksFromHost(BoincHost host)
        {
            var tasks = new List<BoincTask>();

            try
            {
                using var client = new TcpClient();
                
                // Set a timeout for connection
                client.ReceiveTimeout = 10000; // 10 seconds
                client.SendTimeout = 10000;

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
                var projects = await SendRpcCommand(stream, "<boinc_gui_rpc_request><get_project_status/></boinc_gui_rpc_request>");
                var projectMap = ParseProjects(projects);

                // Get active tasks
                var tasksXml = await SendRpcCommand(stream, "<boinc_gui_rpc_request><get_results/></boinc_gui_rpc_request>");
                
                // Debug: Log the raw XML to see what we're getting
                Console.WriteLine($"Raw tasks XML from {host.Name}:");
                Console.WriteLine(tasksXml);
                Console.WriteLine("--- End Raw XML ---");
                
                // Also get current CC status to see actively running tasks
                var ccStatusXml = await SendRpcCommand(stream, "<boinc_gui_rpc_request><get_cc_status/></boinc_gui_rpc_request>");
                Console.WriteLine($"CC Status XML from {host.Name}:");
                Console.WriteLine(ccStatusXml);
                Console.WriteLine("--- End CC Status XML ---");
                
                var taskElements = XDocument.Parse(tasksXml)
                    .Descendants("result");

                foreach (var taskElement in taskElements)
                {
                    var projectUrl = taskElement.Element("project_url")?.Value ?? "";
                    var projectName = projectMap.ContainsKey(projectUrl) ? projectMap[projectUrl] : projectUrl;

                    var activeTask = taskElement.Element("active_task");
                    var stateValue = taskElement.Element("state")?.Value ?? "";
                    var schedulerState = taskElement.Element("scheduler_state")?.Value ?? "";
                    var activeTaskState = activeTask?.Element("active_task_state")?.Value ?? "";
                    
                    // Determine the actual task state based on multiple factors
                    var state = GetTaskState(stateValue, schedulerState, activeTask != null, activeTaskState);
                    
                    // Calculate progress more accurately
                    double progressPercent = 0;
                    if (stateValue == "5") // Complete
                    {
                        progressPercent = 100;
                    }
                    else
                    {
                        // Try active_task first, then result element
                        var fractionDone = activeTask?.Element("fraction_done")?.Value ?? 
                                         taskElement.Element("fraction_done")?.Value ?? "0";
                        progressPercent = double.Parse(fractionDone) * 100;
                    }
                    
                    // Debug: Print task details
                    Console.WriteLine($"Task: {taskElement.Element("name")?.Value}, State: {stateValue}, Scheduler: {schedulerState}, Active: {activeTask != null}, ActiveTaskState: {activeTaskState}, Final State: {state}, Progress: {progressPercent:F1}%");
                    
                    var task = new BoincTask
                    {
                        HostName = host.Name,
                        ProjectName = projectName,
                        TaskName = taskElement.Element("name")?.Value ?? "",
                        State = state,
                        ProgressPercent = progressPercent,
                        ElapsedTime = TimeSpan.FromSeconds(double.Parse(activeTask?.Element("elapsed_time")?.Value ?? 
                                                                      taskElement.Element("elapsed_time")?.Value ?? "0")),
                        RemainingTime = TimeSpan.FromSeconds(double.Parse(activeTask?.Element("estimated_cpu_time_remaining")?.Value ?? 
                                                                        taskElement.Element("estimated_cpu_time_remaining")?.Value ?? "0")),
                        Deadline = UnixTimeStampToDateTime(double.Parse(taskElement.Element("report_deadline")?.Value ?? "0"))
                    };

                    tasks.Add(task);
                }
            }
            catch (Exception ex)
            {
                // Re-throw with more context
                throw new Exception($"Failed to connect to BOINC host '{host.Name}' at {host.Address}:{host.Port}. Error: {ex.Message}", ex);
            }

            return tasks;
        }

        private async Task AuthenticateAsync(NetworkStream stream, string password)
        {
            try
            {
                Console.WriteLine("Starting BOINC authentication...");
                
                // Give BOINC a moment to prepare after connection
                await Task.Delay(100);
                
                // Send auth1 command to get nonce
                var auth1Command = "<boinc_gui_rpc_request><auth1/></boinc_gui_rpc_request>";
                Console.WriteLine($"Sending auth1: {auth1Command}");
                var auth1Bytes = Encoding.UTF8.GetBytes(auth1Command + "\x03");
                await stream.WriteAsync(auth1Bytes);
                await stream.FlushAsync(); // Ensure data is sent immediately
                
                // Read nonce response with timeout
                var nonceResponse = await ReadResponseWithTimeout(stream, 5000); // 5 second timeout
                Console.WriteLine($"Received nonce response: '{nonceResponse}'");
                
                if (string.IsNullOrEmpty(nonceResponse))
                {
                    throw new Exception("Received empty response from BOINC server. Check if GUI RPC is enabled and remote access is allowed.");
                }
                
                // Parse nonce from response
                var nonceStart = nonceResponse.IndexOf("<nonce>");
                var nonceEnd = nonceResponse.IndexOf("</nonce>");
                
                if (nonceStart == -1 || nonceEnd == -1)
                {
                    // Check if this is an error response
                    if (nonceResponse.Contains("unauthorized") || nonceResponse.Contains("error"))
                    {
                        throw new Exception($"BOINC server returned error: {nonceResponse}");
                    }
                    throw new Exception($"Failed to parse nonce from response: '{nonceResponse}'. Make sure this host is in remote_hosts.cfg");
                }
                
                var nonce = nonceResponse.Substring(nonceStart + 7, nonceEnd - nonceStart - 7);
                Console.WriteLine($"Extracted nonce: {nonce}");
                
                // Calculate MD5 hash of nonce + password
                using var md5 = MD5.Create();
                var hashInput = nonce + password;
                var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
                var hash = Convert.ToHexString(hashBytes).ToLower();
                
                // Send auth2 command with hash
                var auth2Command = $"<boinc_gui_rpc_request><auth2><nonce_hash>{hash}</nonce_hash></auth2></boinc_gui_rpc_request>";
                Console.WriteLine($"Sending auth2: {auth2Command}");
                var auth2Bytes = Encoding.UTF8.GetBytes(auth2Command + "\x03");
                await stream.WriteAsync(auth2Bytes);
                await stream.FlushAsync(); // Ensure data is sent immediately
                
                // Read auth response
                var authResponse = await ReadResponseWithTimeout(stream, 5000);
                Console.WriteLine($"Received auth response: '{authResponse}'");
                
                // Check if authentication was successful
                if (!authResponse.Contains("<authorized/>"))
                {
                    throw new Exception($"Authentication failed. Response: '{authResponse}'. Check GUI RPC password.");
                }
                
                Console.WriteLine("Authentication successful!");
            }
            catch (Exception ex)
            {
                throw new Exception($"Authentication failed: {ex.Message}", ex);
            }
        }

        private async Task<string> SendRpcCommand(NetworkStream stream, string command)
        {
            // BOINC GUI RPC protocol requires commands to end with \x03
            var requestBytes = Encoding.UTF8.GetBytes(command + "\x03");
            await stream.WriteAsync(requestBytes);
            await stream.FlushAsync(); // Ensure data is sent immediately
            return await ReadResponse(stream);
        }

        private async Task<string> ReadResponseWithTimeout(NetworkStream stream, int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                return await ReadResponse(stream, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new Exception($"Response timeout after {timeoutMs}ms - BOINC may not be responding");
            }
        }

        private async Task<string> ReadResponse(NetworkStream stream, CancellationToken cancellationToken = default)
        {
            var responseBuilder = new StringBuilder();
            var buffer = new byte[4096]; // Larger buffer for better performance
            
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0) 
                {
                    // Connection closed unexpectedly
                    throw new Exception("Connection closed while reading response");
                }
                
                var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                responseBuilder.Append(chunk);
                
                // Check if we have the complete response (ends with \x03)
                var currentResponse = responseBuilder.ToString();
                if (currentResponse.Contains('\x03'))
                {
                    // Make sure we have a complete XML structure
                    var cleanResponse = currentResponse.Replace("\x03", "");
                    
                    // Debug: Log the response to see what we're getting
                    Console.WriteLine($"BOINC Response: {cleanResponse}");
                    
                    // Basic validation - responses should contain XML tags
                    if (cleanResponse.Contains("<") && cleanResponse.Contains(">"))
                    {
                        return cleanResponse;
                    }
                    else if (string.IsNullOrWhiteSpace(cleanResponse))
                    {
                        throw new Exception("Received empty response from BOINC");
                    }
                    else
                    {
                        // Sometimes BOINC sends non-XML responses for errors
                        return cleanResponse;
                    }
                }
                
                // Safety check to prevent infinite loops with very large responses
                if (responseBuilder.Length > 1024 * 1024) // 1MB limit
                {
                    throw new Exception("Response too large - possible infinite loop");
                }
            }
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

        private string GetTaskState(string stateValue, string schedulerState = "", bool hasActiveTask = false, string activeTaskState = "")
        {
            // First check if it's suspended (scheduler_state 0 means suspended)
            if (schedulerState == "0")
            {
                return "Suspended";
            }
            
            // Check basic state
            var baseState = stateValue switch
            {
                "1" => "Downloading",
                "2" => "Ready to Run",
                "3" => "Computing", 
                "4" => "Uploading",
                "5" => "Complete",
                "6" => "Aborted",
                _ => "Unknown"
            };
            
            // If state is 2 (Ready to Run) but has active_task with active_task_state 1, it's actually "Running"
            if (baseState == "Ready to Run" && hasActiveTask && activeTaskState == "1")
            {
                return "Running";
            }
            
            // If state is 2 (Ready to Run) but has active_task with active_task_state 0, it's "Suspended"
            if (baseState == "Ready to Run" && hasActiveTask && activeTaskState == "0")
            {
                return "Suspended";
            }
            
            // If state is "Computing" and has active_task, it's "Running"
            if (baseState == "Computing" && hasActiveTask)
            {
                return "Running";
            }
            
            return baseState;
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
            // TODO: Implement host filtering (ComboBox temporarily removed)
        }

        private void StatusFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // TODO: Implement status filtering (ComboBox temporarily removed)
        }
    }
}