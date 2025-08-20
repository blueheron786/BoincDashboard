using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using System.Globalization;
using System.Windows.Data;
using BoincDashboard.Models;
using BoincDashboard.Infrastructure;
using BoincDashboard.Converters;
using System.Windows.Forms; // For NotifyIcon
using System.Drawing; // For Icon

namespace BoincDashboard
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _refreshTimer = new();
        private List<BoincHost> _hosts = new();
        
        // Connection pooling for keeping authenticated sessions alive
        private Dictionary<string, BoincConnection> _activeConnections = new();
        private readonly object _connectionLock = new object();
        
        // Sort state preservation
        private bool _isFirstLoad = true;
        
        // Filter state
        private List<BoincTask> _allTasks = new();
        private List<BoincComputer> _allComputers = new();
        private string _selectedHost = "All Hosts";
        private string _selectedStatus = "All Status";

        // Track successful connections (hosts that responded, even with 0 tasks)
        private HashSet<string> _successfulConnections = new();

        // Computer status persistence
        private Dictionary<string, ComputerStatus> _computerStatusHistory = new();
        private DispatcherTimer _statusSaveTimer = new();
        private readonly string _statusFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BoincDashboard", "computer_status.json");

        // System tray functionality
        private NotifyIcon? _notifyIcon;
        private bool _isExiting = false;

        public MainWindow()
        {
            InitializeComponent();
            InitializeSystemTray();
            InitializeHosts();
            LoadComputerStatusHistory();
            SetupStatusSaveTimer();
            // Don't start refresh timer yet - wait until after first load
            SetupRefreshTimer(startImmediately: false);
            
            // Load tasks after the window is fully loaded to avoid deadlock
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
            this.StateChanged += MainWindow_StateChanged;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                Hide();
                _notifyIcon?.ShowBalloonTip(2000, "BOINC Dashboard", "Application was minimized to tray. Right-click the tray icon to exit.", ToolTipIcon.Info);
                return;
            }
            
            _statusSaveTimer?.Stop();
            SaveComputerStatusHistory();
            _notifyIcon?.Dispose();
        }

        private void LoadComputerStatusHistory()
        {
            try
            {
                if (File.Exists(_statusFilePath))
                {
                    var json = File.ReadAllText(_statusFilePath);
                    var statusList = JsonConvert.DeserializeObject<List<ComputerStatus>>(json) ?? new List<ComputerStatus>();
                    _computerStatusHistory = statusList.ToDictionary(s => s.HostName, s => s);
                    Console.WriteLine($"Loaded status history for {_computerStatusHistory.Count} computers");
                }
                else
                {
                    Console.WriteLine("No existing status file found, starting fresh");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading computer status history: {ex.Message}");
                _computerStatusHistory = new Dictionary<string, ComputerStatus>();
            }
        }

        private void SaveComputerStatusHistory()
        {
            try
            {
                var directory = Path.GetDirectoryName(_statusFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var statusList = _computerStatusHistory.Values.ToList();
                var json = JsonConvert.SerializeObject(statusList, Formatting.Indented);
                File.WriteAllText(_statusFilePath, json);
                Console.WriteLine($"Saved status history for {statusList.Count} computers");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving computer status history: {ex.Message}");
            }
        }

        private void SetupStatusSaveTimer()
        {
            _statusSaveTimer.Interval = TimeSpan.FromMinutes(1); // Save every minute
            _statusSaveTimer.Tick += (sender, e) => SaveComputerStatusHistory();
            _statusSaveTimer.Start();
        }

        #region System Tray Functionality

        private void InitializeSystemTray()
        {
            _notifyIcon = new NotifyIcon();
            
            // Create icon from embedded resource or use default
            try
            {
                // Try to load from output directory (this should work since we confirmed the file exists)
                var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "favicon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
                }
                else
                {
                    // Fallback to application icon
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                // Fallback to a simple system icon
                _notifyIcon.Icon = SystemIcons.Information;
            }
            
            _notifyIcon.Text = "BOINC Dashboard";
            _notifyIcon.Visible = true;
            
            // Double-click to show window
            _notifyIcon.DoubleClick += (sender, e) => ShowWindow();
            
            // Right-click context menu
            var contextMenu = new ContextMenuStrip();
            
            var showMenuItem = new ToolStripMenuItem("Show", null, (sender, e) => ShowWindow());
            var exitMenuItem = new ToolStripMenuItem("Exit", null, (sender, e) => ExitApplication());
            
            contextMenu.Items.Add(showMenuItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(exitMenuItem);
            
            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                _notifyIcon!.ShowBalloonTip(2000, "BOINC Dashboard", "Application was minimized to tray", ToolTipIcon.Info);
            }
        }

        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _isExiting = true;
            _notifyIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        #endregion

        private void UpdateComputerStatus(string hostName, bool isOnline)
        {
            if (!_computerStatusHistory.ContainsKey(hostName))
            {
                _computerStatusHistory[hostName] = new ComputerStatus { HostName = hostName };
            }

            var status = _computerStatusHistory[hostName];
            status.IsOnline = isOnline;
            if (isOnline)
            {
                status.LastSeen = DateTime.Now;
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeFilters();
            await LoadAllTasks();
            LoadAllComputers();
            
            // Start refresh timer only after initial load completes
            _refreshTimer.Start();
            Console.WriteLine("Initial load complete - refresh timer started");
        }

        private void InitializeFilters()
        {
            // Initialize host filter
            HostFilterComboBox.Items.Add("All Hosts");
            foreach (var host in _hosts)
            {
                HostFilterComboBox.Items.Add(host.Name);
            }
            HostFilterComboBox.SelectedIndex = 0;

            // Initialize status filter
            StatusFilterComboBox.Items.Add("All Status");
            StatusFilterComboBox.Items.Add("Running");
            StatusFilterComboBox.Items.Add("Complete");
            StatusFilterComboBox.Items.Add("Paused");
            StatusFilterComboBox.Items.Add("Error");
            StatusFilterComboBox.SelectedIndex = 0;
        }

        private void HostFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HostFilterComboBox.SelectedItem != null)
            {
                _selectedHost = HostFilterComboBox.SelectedItem.ToString() ?? "All Hosts";
                ApplyFilters();
            }
        }

        private void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StatusFilterComboBox.SelectedItem != null)
            {
                _selectedStatus = StatusFilterComboBox.SelectedItem.ToString() ?? "All Status";
                ApplyFilters();

            }
        }

        private void ApplyFilters()
        {
            if (_allTasks == null || _allTasks.Count == 0) return;

            var filteredTasks = _allTasks.AsEnumerable();

            // Apply host filter
            if (_selectedHost != "All Hosts")
            {
                filteredTasks = filteredTasks.Where(t => t.HostName == _selectedHost);
            }

            // Apply status filter
            if (_selectedStatus != "All Status")
            {
                filteredTasks = filteredTasks.Where(t => t.State == _selectedStatus);
            }

            // Update DataGrid with filtered results
            var filteredList = filteredTasks.ToList();
            TasksDataGrid.ItemsSource = filteredList;

            // Update task count
            var runningTasksCount = filteredList.Count(t => t.State == "Running");
            var totalTasksCount = _allTasks.Count;
            
            if (_selectedHost == "All Hosts" && _selectedStatus == "All Status")
            {
                TotalTasksLabel.Text = $"Total Tasks: {totalTasksCount} ({runningTasksCount} running)";
            }
            else
            {
                TotalTasksLabel.Text = $"Filtered Tasks: {filteredList.Count} of {totalTasksCount} ({runningTasksCount} running)";
            }
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
                System.Windows.MessageBox.Show($"Error loading hosts configuration: {ex.Message}", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                _hosts = new List<BoincHost>();
            }
        }

        private void SetupRefreshTimer(bool startImmediately = true)
        {
            _refreshTimer = new DispatcherTimer
            {
                /////////////// takes around 0.5s to update
                // For some slow machines, 1s is too fast.
                // Keep it slow so we get consistent results.
                // Will be auto-adjusted based on runtime
                Interval = TimeSpan.FromSeconds(5),
            };
            _refreshTimer.Tick += async (s, e) => 
            {
                await LoadAllTasks();
                LoadAllComputers();
            };
            
            if (startImmediately)
            {
                _refreshTimer.Start();
            }
        }

        private async Task LoadAllTasks()
        {
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();
            var allTasks = new List<BoincTask>();
            var errorMessages = new List<string>();

            // Show loading indicator
            Dispatcher.Invoke(() =>
            {
                LastUpdatedLabel.Text = "Connecting to all hosts...";
            });

            // Clean up stale connections before starting
            CleanupStaleConnections();

            // Create tasks for all hosts to run in parallel
            var hostTasks = _hosts.Select(async host =>
            {
                try
                {
                    var tasks = await GetTasksFromHost(host);
                    return new { Host = host, Tasks = tasks, Error = (string?)null };
                }
                catch (TimeoutException)
                {
                    var errorMsg = $"[TIMEOUT] {host.Name} ({host.Address}) not accessible - connection timeout. Skipping retries.";
                    Console.WriteLine(errorMsg);
                    return new { Host = host, Tasks = new List<BoincTask>(), Error = (string?)errorMsg };
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Error connecting to {host.Name} ({host.Address}): {ex.Message}";
                    
                    // Specific handling for other connection timeouts - don't retry
                    if (ex.Message.Contains("connection attempt failed") || 
                        ex.Message.Contains("did not properly respond after a period of time") ||
                        ex.Message.Contains("failed to respond"))
                    {
                        Console.WriteLine($"[TIMEOUT] {host.Name} ({host.Address}) not accessible - connection timeout. Skipping retries.");
                    }
                    else
                    {
                        Console.WriteLine($"[ERROR] {errorMsg}");
                        Console.WriteLine($"Full exception: {ex}");
                    }
                    return new { Host = host, Tasks = new List<BoincTask>(), Error = (string?)errorMsg };
                }
            }).ToArray();

            // Wait for all hosts to respond
            var results = await Task.WhenAll(hostTasks);

            // Process results and track successful connections
            _successfulConnections.Clear();
            foreach (var result in results)
            {
                if (result.Error != null)
                {
                    errorMessages.Add(result.Error);
                }
                else
                {
                    // Host connected successfully (even if it has 0 tasks)
                    _successfulConnections.Add(result.Host.Name);
                    allTasks.AddRange(result.Tasks);
                }
            }

            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            Console.WriteLine($"Total refresh time: {elapsedMs} ms");

            // Auto-adjust timer interval based on runtime
            AdjustTimerInterval(elapsedMs);

            // Update UI on main thread
            Dispatcher.Invoke(() =>
            {
                // Save current DataGrid column sort state
                var currentSortColumn = TasksDataGrid.Columns.FirstOrDefault(c => c.SortDirection.HasValue);
                var currentSortDirection = currentSortColumn?.SortDirection;
                
                // Store all tasks for filtering
                _allTasks = allTasks;
                
                // Apply current filters
                ApplyFilters();
                
                // Restore sort state by updating DataGrid columns directly
                if (_isFirstLoad)
                {
                    // Set default sort on first load - find Elapsed column and set it to descending
                    var elapsedColumn = TasksDataGrid.Columns.FirstOrDefault(c => c.SortMemberPath == "ElapsedTime");
                    if (elapsedColumn != null)
                    {
                        elapsedColumn.SortDirection = System.ComponentModel.ListSortDirection.Descending;
                        var collectionView = System.Windows.Data.CollectionViewSource.GetDefaultView(TasksDataGrid.ItemsSource);
                        collectionView?.SortDescriptions.Clear();
                        collectionView?.SortDescriptions.Add(new System.ComponentModel.SortDescription("ElapsedTime", System.ComponentModel.ListSortDirection.Descending));
                    }
                    _isFirstLoad = false;
                }
                else if (currentSortColumn != null && currentSortDirection.HasValue)
                {
                    // Clear all column sort directions first
                    foreach (var column in TasksDataGrid.Columns)
                    {
                        column.SortDirection = null;
                    }
                    
                    // Restore the previously sorted column
                    currentSortColumn.SortDirection = currentSortDirection;
                    
                    // Apply the sort to the collection view
                    var collectionView = System.Windows.Data.CollectionViewSource.GetDefaultView(TasksDataGrid.ItemsSource);
                    collectionView?.SortDescriptions.Clear();
                    collectionView?.SortDescriptions.Add(new System.ComponentModel.SortDescription(currentSortColumn.SortMemberPath, currentSortDirection.Value));
                }
                
                // Update counters
                var activeHostsCount = _successfulConnections.Count;
                var totalTasksCount = allTasks.Count;
                var runningTasksCount = allTasks.Count(t => t.State == "Running");
                
                TotalTasksLabel.Text = $"Total Tasks: {totalTasksCount} ({runningTasksCount} running)";
                ActiveHostsLabel.Text = $"Active Hosts: {activeHostsCount}/{_hosts.Count}";
                
                var statusText = $"Last Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({elapsedMs}ms)";
                
                // Update unavailable hosts in dedicated bottom label
                if (errorMessages.Count > 0)
                {
                    var unavailableHosts = errorMessages.Select(err => 
                    {
                        // Extract host name from error message
                        var match = Regex.Match(err, @"Error connecting to ([^(]+)");
                        return match.Success ? match.Groups[1].Value.Trim() : "Unknown";
                    }).ToList();
                    
                    UnavailableHostsLabel.Text = $"Unavailable hosts: {string.Join(", ", unavailableHosts)}";
                }
                else
                {
                    UnavailableHostsLabel.Text = "";
                }
                if (allTasks.Count == 0 && errorMessages.Count > 0)
                {
                    // Show detailed errors if no tasks found
                    System.Windows.MessageBox.Show(
                        string.Join("\n", errorMessages),
                        "Connection Errors",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                LastUpdatedLabel.Text = statusText;
            });
        }

        private void LoadAllComputers()
        {
            try
            {
                Console.WriteLine("LoadAllComputers: Creating computer list from existing task data...");
                var allComputers = new List<BoincComputer>();

                // Add all successfully connected hosts (even those with 0 tasks)
                foreach (var hostName in _successfulConnections)
                {
                    var hostTasks = _allTasks.Where(t => t.HostName == hostName).ToList();
                    var computer = new BoincComputer
                    {
                        HostName = hostName,
                        Status = "Connected", // Successfully connected to BOINC
                        ActiveTasks = hostTasks.Count(t => t.State == "Running"),
                        TotalTasks = hostTasks.Count,
                        Platform = "Active Host", // We know it's active if we connected successfully
                        BoincVersion = "Connected",
                        LastContact = DateTime.Now
                    };
                    allComputers.Add(computer);
                    
                    // Update status history
                    UpdateComputerStatus(hostName, true);
                    
                    Console.WriteLine($"Added computer from successful connection: {computer.HostName} ({computer.ActiveTasks}/{computer.TotalTasks} tasks)");
                }

                // Add configured hosts that we couldn't connect to (offline hosts)
                foreach (var configuredHost in _hosts)
                {
                    if (!_successfulConnections.Contains(configuredHost.Name))
                    {
                        // Get last seen time from history
                        var lastSeen = DateTime.MinValue;
                        if (_computerStatusHistory.ContainsKey(configuredHost.Name))
                        {
                            lastSeen = _computerStatusHistory[configuredHost.Name].LastSeen;
                        }

                        var offlineComputer = new BoincComputer
                        {
                            HostName = configuredHost.Name,
                            Status = "Offline",
                            ActiveTasks = 0,
                            TotalTasks = 0,
                            Platform = "Unknown",
                            BoincVersion = "Unknown",
                            LastContact = lastSeen
                        };
                        allComputers.Add(offlineComputer);
                        
                        // Update status history
                        UpdateComputerStatus(configuredHost.Name, false);
                        
                        Console.WriteLine($"Added offline computer: {offlineComputer.HostName} (last seen: {(lastSeen == DateTime.MinValue ? "Never" : lastSeen.ToString("yyyy-MM-dd HH:mm:ss"))})");
                    }
                }

                var activeHostsCount = _successfulConnections.Count;

                // Sort computers with online first, then offline, then by hostname
                allComputers = allComputers
                    .OrderBy(c => c.Status == "Offline" ? 1 : 0)
                    .ThenBy(c => c.HostName)
                    .ToList();

                // Update UI on the main thread
                Console.WriteLine($"LoadAllComputers: Updating UI with {allComputers.Count} computers ({activeHostsCount} online)");
                
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        _allComputers = allComputers;
                        ComputersDataGrid.ItemsSource = null; // Clear first
                        ComputersDataGrid.ItemsSource = allComputers; // Then set new data
                        ComputerCountLabel.Text = $"Total Computers: {allComputers.Count} ({activeHostsCount} online)";
                        
                        // Update the new status labels
                        var offlineHostsCount = allComputers.Count - activeHostsCount;
                        OnlineCountLabel.Text = $"{activeHostsCount} Online";
                        OfflineCountLabel.Text = $"{offlineHostsCount} Offline";
                        
                        Console.WriteLine("LoadAllComputers: UI updated successfully");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error updating computers UI: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LoadAllComputers: Fatal error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        private async Task<BoincComputer?> GetComputerInfo(BoincHost host, List<BoincTask> hostTasks)
        {
            try
            {
                var connection = await GetOrCreateConnection(host);
                
                // Get host info and version
                var hostInfoXml = await SendRpcCommand(connection.Stream!, "<boinc_gui_rpc_request><get_host_info/></boinc_gui_rpc_request>");
                var versionXml = await SendRpcCommand(connection.Stream!, "<boinc_gui_rpc_request><exchange_versions/></boinc_gui_rpc_request>");
                
                var hostDoc = XDocument.Parse(hostInfoXml);
                var versionDoc = XDocument.Parse(versionXml);
                
                var computer = new BoincComputer
                {
                    HostName = host.Name,
                    Status = "Online",
                    ActiveTasks = hostTasks.Count(t => t.State == "Running"),
                    TotalTasks = hostTasks.Count,
                    Platform = GetPlatformString(hostDoc),
                    BoincVersion = versionDoc.Descendants("version").FirstOrDefault()?.Value ?? "Unknown",
                    LastContact = DateTime.Now
                };

                return computer;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting computer info from {host.Name}: {ex.Message}");
                return null;
            }
        }

        private string GetPlatformString(XDocument hostDoc)
        {
            try
            {
                var osName = hostDoc.Descendants("os_name").FirstOrDefault()?.Value ?? "";
                var osVersion = hostDoc.Descendants("os_version").FirstOrDefault()?.Value ?? "";
                var architecture = hostDoc.Descendants("p_name").FirstOrDefault()?.Value ?? "";
                
                return $"{osName} {osVersion} ({architecture})".Trim();
            }
            catch
            {
                return "Unknown";
            }
        }

        private async Task<List<BoincTask>> GetTasksFromHost(BoincHost host)
        {
            var tasks = new List<BoincTask>();

            try
            {
                var connection = await GetOrCreateConnection(host);
                
                // Get project list first to map URLs to names
                var projects = await SendRpcCommand(connection.Stream!, "<boinc_gui_rpc_request><get_project_status/></boinc_gui_rpc_request>");
                var projectMap = ParseProjects(projects);

                // Get active tasks
                var tasksXml = await SendRpcCommand(connection.Stream!, "<boinc_gui_rpc_request><get_results/></boinc_gui_rpc_request>");
                
                // Also get current CC status to see actively running tasks
                
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
                    
                    // Calculate elapsed time and remaining time
                    TimeSpan elapsedTime;
                    TimeSpan remainingTime;
                    
                    if (state == "Complete")
                    {
                        // For completed tasks, show the total runtime
                        var totalRuntimeSeconds = double.Parse(taskElement.Element("final_elapsed_time")?.Value ?? 
                                                              taskElement.Element("elapsed_time")?.Value ?? "0");
                        elapsedTime = TimeSpan.FromSeconds(totalRuntimeSeconds);
                        remainingTime = TimeSpan.Zero; // No remaining time for completed tasks
                    }
                    else
                    {
                        // For active tasks, show elapsed and remaining time
                        var elapsedSeconds = double.Parse(activeTask?.Element("elapsed_time")?.Value ?? 
                                                         taskElement.Element("elapsed_time")?.Value ?? "0");
                        var remainingSeconds = double.Parse(activeTask?.Element("estimated_cpu_time_remaining")?.Value ?? 
                                                           taskElement.Element("estimated_cpu_time_remaining")?.Value ?? "0");
                        
                        elapsedTime = TimeSpan.FromSeconds(elapsedSeconds);
                        
                        // If task is >= 10% complete, calculate remaining time based on progress instead of BOINC estimate
                        if (progressPercent >= 10.0 && progressPercent < 100.0 && elapsedSeconds > 0)
                        {
                            // Calculate: remaining = (100/percent_complete * elapsed_time) - elapsed_time
                            var totalEstimatedSeconds = (100.0 / progressPercent) * elapsedSeconds;
                            var calculatedRemainingSeconds = totalEstimatedSeconds - elapsedSeconds;
                            remainingTime = TimeSpan.FromSeconds(Math.Max(0, calculatedRemainingSeconds));
                        }
                        else
                        {
                            // Use BOINC's estimate for tasks < 10% complete or when no elapsed time
                            remainingTime = TimeSpan.FromSeconds(remainingSeconds);
                        }
                    }
                    
                    var task = new BoincTask
                    {
                        HostName = host.Name,
                        ProjectName = projectName,
                        TaskName = taskElement.Element("name")?.Value ?? "",
                        State = state,
                        ProgressPercent = progressPercent,
                        ElapsedTime = elapsedTime,
                        RemainingTime = remainingTime,
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

        private async Task<BoincConnection> GetOrCreateConnection(BoincHost host)
        {
            var connectionKey = $"{host.Address}:{host.Port}";
            
            lock (_connectionLock)
            {
                // Check if we have a valid existing connection
                if (_activeConnections.TryGetValue(connectionKey, out var existingConnection))
                {
                    if (existingConnection.IsConnected)
                    {
                        existingConnection.LastUsed = DateTime.Now;
                        return existingConnection;
                    }
                    else
                    {
                        // Connection is stale, remove it
                        Console.WriteLine($"Removing stale connection to {host.Name}");
                        existingConnection.Dispose();
                        _activeConnections.Remove(connectionKey);
                    }
                }
            }

            // Create new connection
            Console.WriteLine($"Creating new connection to {host.Name}");
            var newConnection = await CreateAndAuthenticateConnection(host);
            
            lock (_connectionLock)
            {
                _activeConnections[connectionKey] = newConnection;
            }
            
            return newConnection;
        }

        private async Task<BoincConnection> CreateAndAuthenticateConnection(BoincHost host)
        {
            var client = new TcpClient();
            
            try
            {
                // Set timeouts
                client.ReceiveTimeout = 5000; // 5 seconds for faster timeout detection
                client.SendTimeout = 5000;

                // Create cancellation token for connection timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(9));
                
                // Connect to host with timeout
                if (host.Address.Contains(':'))
                {
                    await client.ConnectAsync(System.Net.IPAddress.Parse(host.Address), host.Port, cts.Token);
                }
                else
                {
                    await client.ConnectAsync(host.Address, host.Port, cts.Token);
                }

                var stream = client.GetStream();
                
                // Authenticate with timeout
                await AuthenticateAsync(stream, host.Password);
                
                Console.WriteLine($"Successfully authenticated to {host.Name}");
                
                return new BoincConnection
                {
                    TcpClient = client,
                    Stream = stream,
                    LastUsed = DateTime.Now
                };
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                throw new TimeoutException($"Connection to {host.Name} ({host.Address}) timed out after 5 seconds");
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private void CleanupStaleConnections()
        {
            lock (_connectionLock)
            {
                var staleConnections = _activeConnections
                    .Where(kvp => !kvp.Value.IsConnected || DateTime.Now - kvp.Value.LastUsed > TimeSpan.FromMinutes(5))
                    .ToList();

                foreach (var (key, connection) in staleConnections)
                {
                    Console.WriteLine($"Cleaning up stale connection: {key}");
                    connection.Dispose();
                    _activeConnections.Remove(key);
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Cleanup all connections when window closes
            lock (_connectionLock)
            {
                foreach (var connection in _activeConnections.Values)
                {
                    connection.Dispose();
                }
                _activeConnections.Clear();
            }
            base.OnClosed(e);
        }

        private async Task AuthenticateAsync(NetworkStream stream, string password)
        {
            try
            {
                // Give BOINC a moment to prepare after connection
                await Task.Delay(100);
                
                // Send auth1 command to get nonce
                var auth1Command = "<boinc_gui_rpc_request><auth1/></boinc_gui_rpc_request>";
                var auth1Bytes = Encoding.UTF8.GetBytes(auth1Command + "\x03");
                await stream.WriteAsync(auth1Bytes);
                await stream.FlushAsync(); // Ensure data is sent immediately
                
                // Read nonce response with timeout
                var nonceResponse = await ReadResponseWithTimeout(stream, 5000); // 5 second timeout
                
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
                
                // Calculate MD5 hash of nonce + password
                using var md5 = MD5.Create();
                var hashInput = nonce + password;
                var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
                var hash = Convert.ToHexString(hashBytes).ToLower();
                
                // Send auth2 command with hash
                var auth2Command = $"<boinc_gui_rpc_request><auth2><nonce_hash>{hash}</nonce_hash></auth2></boinc_gui_rpc_request>";
                var auth2Bytes = Encoding.UTF8.GetBytes(auth2Command + "\x03");
                await stream.WriteAsync(auth2Bytes);
                await stream.FlushAsync(); // Ensure data is sent immediately
                
                // Read auth response
                var authResponse = await ReadResponseWithTimeout(stream, 5000);
                
                // Check if authentication was successful
                if (!authResponse.Contains("<authorized/>"))
                {
                    throw new Exception($"Authentication failed. Response: '{authResponse}'. Check GUI RPC password.");
                }
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
            
            return baseState;
        }

        private DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)unixTimeStamp).DateTime;
        }

        private void AdjustTimerInterval(long elapsedMs)
        {
            // Calculate optimal interval based on runtime
            var currentIntervalMs = _refreshTimer.Interval.TotalMilliseconds;
            // Minimum interval: 5 seconds, Maximum interval: 60 seconds
            
            var minIntervalMs = Math.Max(5000, currentIntervalMs + (elapsedMs > currentIntervalMs ? 1000 : 0));
            var maxIntervalMs = 60000;
            var optimalIntervalMs = Math.Min(minIntervalMs, maxIntervalMs);
            
            var newInterval = TimeSpan.FromMilliseconds(optimalIntervalMs);
            
            // Only update if the change is significant (>= 1 second difference)
            if (Math.Abs((newInterval - _refreshTimer.Interval).TotalMilliseconds) >= 1000)
            {
                _refreshTimer.Stop();
                _refreshTimer.Interval = newInterval;
                _refreshTimer.Start();
                
                Console.WriteLine($"Timer interval adjusted from {_refreshTimer.Interval.TotalSeconds:F1}s to {newInterval.TotalSeconds:F1}s (runtime: {elapsedMs}ms)");
            }
            
            // Always update the auto-refresh button text (even if interval didn't change)
            UpdateAutoRefreshButtonText();
        }

        private void UpdateAutoRefreshButtonText()
        {
            // Round up the interval to the nearest second (e.g., 5.01 -> 6 seconds)
            var intervalSeconds = Math.Ceiling(_refreshTimer.Interval.TotalSeconds);
            
            Dispatcher.Invoke(() =>
            {
                AutoRefreshToggle.Content = $"?? Auto-Refresh (every {intervalSeconds}s)";
            });
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadAllTasks();
            LoadAllComputers();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement settings dialog
            System.Windows.MessageBox.Show("Settings dialog not yet implemented.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
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
