using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Text.Json;

namespace VamDependencyBridge
{
    // --- CONFIGURATION CLASS ---
    public class BridgeConfig
    {
        public string VamPath { get; set; } = @"N:\VAM";
        public string EverythingPath { get; set; } = @"C:\Program Files\Everything";
        public string RepoPath { get; set; } = @"Z:\";
    }

    static class Program
    {
        public const string VERSION = "1.0.0";
        public const string VERSION_PREFIX_MATCH = "1.0.";
        static bool IsDebugMode = false;

        // Dynamic Path Properties
        static BridgeConfig Config = new BridgeConfig();
        static string ConfigFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge_config.json");
        static string ErrorLogPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.txt");
        static string DebugLogPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge_log.txt");

        static string EsPath => Path.Combine(Config.EverythingPath, "es.exe");
        static string EverythingDaemonPath => Path.Combine(Config.EverythingPath, "Everything.exe");
        static string PluginDataDir => Path.Combine(Config.VamPath, "Saves", "PluginData", "VAMBridgePlugin");
        static string RequestPath => Path.Combine(PluginDataDir, "request.txt");
        static string ResponsePath => Path.Combine(PluginDataDir, "response.txt");
        static string DestPath => Path.Combine(Config.VamPath, "AddonPackages");

        static NotifyIcon? trayIcon;
        static Form? hiddenForm; 
        static readonly object _processLock = new object();
        static bool _isProcessing = false;
        
        static CancellationTokenSource? _pollingCts;

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--debug") || args.Contains("-d"))
            {
                IsDebugMode = true;
            }

            // THE FIX: Unconditionally wipe old logs on a fresh start
            try { if (File.Exists(ErrorLogPath)) File.Delete(ErrorLogPath); } catch { }
            try { if (File.Exists(DebugLogPath)) File.Delete(DebugLogPath); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            hiddenForm = new Form();
            _ = hiddenForm.Handle; 

            LoadConfig();
            InitializeSystemTray();

            if (!Directory.Exists(Config.VamPath) || !Directory.Exists(Config.EverythingPath))
            {
                ShowTooltip("Action Required", "Default paths are invalid. Please configure your directories.", ToolTipIcon.Warning);
                ShowSettingsForm();
            }
            else
            {
                ApplyConfigAndStart();
            }

            Application.Run(); 
        }

        // --- CORE SYSTEM LOGIC ---

        static void ApplyConfigAndStart()
        {
            try
            {
                EnsureEverythingIsRunning();
                if (!Directory.Exists(PluginDataDir)) Directory.CreateDirectory(PluginDataDir);

                if (_pollingCts != null)
                {
                    _pollingCts.Cancel();
                    _pollingCts.Dispose();
                }
                _pollingCts = new CancellationTokenSource();
                Task.Run(() => PollForRequests(_pollingCts.Token));

                LogDebug("=== VAM Dependency Bridge Started ===");
                LogDebug($"Monitoring: {PluginDataDir}");
                ShowTooltip("Bridge Active", "Monitoring for missing dependency requests.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                LogError("Failed to start bridge monitoring.", ex);
                ShowTooltip("Bridge Error", "Failed to start. Check the error log.", ToolTipIcon.Error);
            }
        }

        static void EnsureEverythingIsRunning()
        {
            if (!File.Exists(EverythingDaemonPath))
            {
                LogError($"Everything.exe not found at: {EverythingDaemonPath}");
                return;
            }

            Process[] pname = Process.GetProcessesByName("Everything");
            if (pname.Length == 0)
            {
                LogDebug("[!] Everything service not running. Starting it...");
                try {
                    Process.Start(EverythingDaemonPath);
                    Thread.Sleep(1000); 
                } catch (Exception ex) {
                    LogError("Failed to start Everything.exe automatically.", ex);
                }
            }
        }

        static async Task PollForRequests(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (File.Exists(RequestPath))
                {
                    lock (_processLock)
                    {
                        if (_isProcessing) continue;
                        _isProcessing = true;
                    }

                    try
                    {
                        LogDebug("Detected request.txt! Processing...");
                        await ProcessRequestAsync();
                    }
                    catch (Exception ex)
                    {
                        LogError("Critical error during file event processing.", ex);
                        ShowTooltip("Bridge Error", "An error occurred. Check error.txt.", ToolTipIcon.Error);
                    }
                    finally
                    {
                        lock (_processLock) { _isProcessing = false; }
                    }
                }
                
                await Task.Delay(1000, token); 
            }
        }

        static async Task ProcessRequestAsync()
        {
            string[] lines = Array.Empty<string>();
            bool fileRead = false;
            int retries = 5;

            while (retries > 0 && !fileRead)
            {
                try
                {
                    lines = File.ReadAllLines(RequestPath);
                    fileRead = true;
                }
                catch (IOException)
                {
                    retries--;
                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    LogError("Error reading request file.", ex);
                    return;
                }
            }

            if (!fileRead || lines.Length == 0) return;

            try { File.Delete(RequestPath); } catch { }

            if (!lines[0].StartsWith("VERSION:" + VERSION_PREFIX_MATCH))
            {
                string err = $"Version mismatch! Bridge is {VERSION}, Plugin is {lines[0]}.";
                await WriteResponseSafeAsync(err);
                ShowTooltip("Version Mismatch", err, ToolTipIcon.Error);
                return;
            }

            EnsureEverythingIsRunning();

            int packageCount = lines.Length - 1;
            LogDebug($"[+] New Request Detected! ({packageCount} packages)");
            ShowTooltip("Resolving Packages...", $"Searching {Config.RepoPath} for {packageCount} missing files...", ToolTipIcon.Info);

            int copied = 0;
            int errors = 0;

            for (int i = 1; i < lines.Length; i++)
            {
                string pkg = lines[i].Trim();
                if (string.IsNullOrEmpty(pkg)) continue;

                string? sourceFile = SearchPackage(pkg);

                if (string.IsNullOrEmpty(sourceFile) || !File.Exists(sourceFile))
                {
                    LogDebug($"   [X] FAILED: Could not locate '{pkg}'");
                    LogError($"Failed to resolve package: {pkg}");
                    errors++;
                    continue;
                }

                try
                {
                    string fileName = Path.GetFileName(sourceFile);
                    string destFile = Path.Combine(DestPath, fileName);
                    
                    if (!File.Exists(destFile))
                    {
                        File.Copy(sourceFile, destFile, true);
                        LogDebug($"   [SUCCESS] Copied: {fileName}");
                    }
                    copied++;
                }
                catch (Exception ex)
                {
                    LogError($"Error copying file {sourceFile} to AddonPackages.", ex);
                    errors++;
                }
            }

            string resultMsg = $"Success: Copied/Found {copied}. Errors/Missing: {errors}.";
            LogDebug($"[=] JOB DONE. {resultMsg}\n");
            await WriteResponseSafeAsync(resultMsg);

            ShowTooltip("Resolution Complete", resultMsg, errors > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        static async Task WriteResponseSafeAsync(string message)
        {
            int retries = 5;
            bool fileWritten = false;
            while (retries > 0 && !fileWritten)
            {
                try
                {
                    File.WriteAllText(ResponsePath, message);
                    fileWritten = true;
                }
                catch (IOException)
                {
                    retries--;
                    await Task.Delay(500);
                }
            }
        }

        static string? SearchPackage(string packageId)
        {
            try
            {
                if (!File.Exists(EsPath)) throw new Exception($"es.exe not found at {EsPath}");

                bool isLatest = packageId.EndsWith(".latest", StringComparison.OrdinalIgnoreCase);
                string baseName = isLatest ? packageId.Substring(0, packageId.Length - 7) : packageId;

                string esArgs = isLatest 
                    ? $"\"{Config.RepoPath}\" \"{baseName}\" ext:var" 
                    : $"\"{Config.RepoPath}\" \"{packageId}.var\"";

                using (var proc = new Process {
                    StartInfo = new ProcessStartInfo {
                        FileName = EsPath,
                        Arguments = esArgs,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                })
                {                
                    proc.Start();
                    string output = proc.StandardOutput.ReadToEnd();
                    
                    if (!proc.WaitForExit(10000)) 
                    {
                        proc.Kill();
                        LogError($"es.exe search timed out for package: {packageId}");
                        return null;
                    }

                    var results = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (results.Length == 0) return null;

                    if (isLatest)
                    {
                        var validFiles = new List<Tuple<string, int>>();
                        foreach (var path in results)
                        {
                            string name = Path.GetFileNameWithoutExtension(path);
                            var parts = name.Split('.');
                            
                            if (parts.Length >= 3 && int.TryParse(parts.Last(), out int v))
                            {
                                string fileBase = string.Join(".", parts.Take(parts.Length - 1));
                                if (fileBase.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                                {
                                    validFiles.Add(new Tuple<string, int>(path, v));
                                }
                            }
                        }

                        if (validFiles.Count > 0)
                        {
                            return validFiles.OrderByDescending(x => x.Item2).First().Item1;
                        }
                        return null;
                    }
                    
                    return results[0]; 
                } 
            } 
            catch (Exception ex)
            {
                LogError($"Search Exception for {packageId}", ex);
                return null;
            }
        }

        // --- CONFIG & UI SETTINGS ---

        static void LoadConfig()
        {
            try {
                if (File.Exists(ConfigFilePath)) {
                    string json = File.ReadAllText(ConfigFilePath);
                    Config = JsonSerializer.Deserialize<BridgeConfig>(json) ?? new BridgeConfig();
                }
            } catch (Exception ex) {
                LogError("Failed to load config.json. Using defaults.", ex);
            }
        }

        static void SaveConfig()
        {
            try {
                string json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFilePath, json);
            } catch (Exception ex) {
                LogError("Failed to save config.json.", ex);
            }
        }

        static void ShowSettingsForm()
        {
            using (Form form = new Form {
                Text = "VAM Bridge Settings",
                Size = new Size(520, 260),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            })
            {
                int y = 20;
                TextBox txtVam = AddPathRow(form, "VAM Root Directory:", Config.VamPath, ref y);
                TextBox txtEverything = AddPathRow(form, "Everything Install Folder:", Config.EverythingPath, ref y);
                TextBox txtRepo = AddPathRow(form, "Repository Path:", Config.RepoPath, ref y);

                Button btnSave = new Button { Text = "Save && Restart", Left = 300, Top = y + 10, Width = 100 };
                Button btnCancel = new Button { Text = "Cancel", Left = 410, Top = y + 10, Width = 80 };

                btnSave.Click += delegate {
                    if (!Directory.Exists(txtVam.Text) || !Directory.Exists(txtEverything.Text) || !Directory.Exists(txtRepo.Text)) {
                        MessageBox.Show("One or more directories do not exist! Please select valid folders.", "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    Config.VamPath = txtVam.Text;
                    Config.EverythingPath = txtEverything.Text;
                    Config.RepoPath = txtRepo.Text;
                    SaveConfig();
                    
                    ApplyConfigAndStart();
                    form.Close();
                };

                btnCancel.Click += delegate { form.Close(); };

                form.Controls.Add(btnSave);
                form.Controls.Add(btnCancel);
                
                form.ShowDialog();
            } 
        }

        static TextBox AddPathRow(Form parent, string labelText, string currentValue, ref int y)
        {
            Label lbl = new Label { Text = labelText, Left = 15, Top = y + 3, AutoSize = true };
            TextBox txt = new TextBox { Text = currentValue, Left = 180, Top = y, Width = 230 };
            Button btn = new Button { Text = "Browse", Left = 415, Top = y - 1, Width = 75 };

            btn.Click += delegate {
                using (var fbd = new FolderBrowserDialog { SelectedPath = txt.Text }) {
                    if (fbd.ShowDialog() == DialogResult.OK) txt.Text = fbd.SelectedPath;
                }
            };

            parent.Controls.Add(lbl);
            parent.Controls.Add(txt);
            parent.Controls.Add(btn);
            y += 40;
            return txt;
        }

        // --- SYSTEM TRAY & LOGGING ---

        static void InitializeSystemTray()
        {
            trayIcon = new NotifyIcon()
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
                Visible = true,
                Text = $"VAM Bridge [{VERSION}]"
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add($"VAM Bridge v{VERSION}").Enabled = false;
            contextMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem launchMenu = new ToolStripMenuItem("Launch Virt-A-Mate");
            launchMenu.DropDownItems.Add("Desktop Mode", null, (s, e) => LaunchVam("-vrmode None"));
            launchMenu.DropDownItems.Add("VR Mode (Default)", null, (s, e) => LaunchVam(""));
            launchMenu.DropDownItems.Add("OpenVR Mode", null, (s, e) => LaunchVam("-vrmode OpenVR"));
            
            contextMenu.Items.Add(launchMenu);
            contextMenu.Items.Add(new ToolStripSeparator());
            
            contextMenu.Items.Add("Settings...", null, (s, e) => ShowSettingsForm());
            contextMenu.Items.Add("Open AddonPackages", null, (s, e) => {
                if (Directory.Exists(DestPath)) Process.Start("explorer.exe", DestPath);
            });
            
            contextMenu.Items.Add(new ToolStripSeparator());

            contextMenu.Items.Add("Download 'Everything' App...", null, (s, e) => {
                Process.Start(new ProcessStartInfo("https://www.voidtools.com/downloads/") { UseShellExecute = true });
            });

            contextMenu.Items.Add("Support the Creator (Ko-fi) <3", null, (s, e) => {
                Process.Start(new ProcessStartInfo("https://ko-fi.com/grepstar") { UseShellExecute = true });
            });

            contextMenu.Items.Add("About", null, (s, e) => {
                string aboutMessage = 
                    "The VAM Dependency Bridge is a background daemon that monitors your Virt-A-Mate environment for missing package requests. It seamlessly searches your repository and transfers the necessary files to your AddonPackages folder.\n\n" +
                    "Made by Hasback.\n\n" +
                    "Note: Both 'Everything' and the 'Everything CLI' (es.exe) must be installed in the same path for this to work.\n\n" +
                    "Meant to work with the hasback.VAMBridgePlugin.x.var";
                
                MessageBox.Show(aboutMessage, $"About VAM Bridge v{VERSION}", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });

            contextMenu.Items.Add(new ToolStripSeparator());
            
            contextMenu.Items.Add("View Error Log", null, (s, e) => {
                if (File.Exists(ErrorLogPath)) Process.Start("notepad.exe", ErrorLogPath);
                else MessageBox.Show("No errors have been logged yet!", "All Good", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });

            if (IsDebugMode) {
                contextMenu.Items.Add("View Debug Log", null, (s, e) => {
                    if (File.Exists(DebugLogPath)) Process.Start("notepad.exe", DebugLogPath);
                });
            }

            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("Exit", null, (s, e) => { 
                _pollingCts?.Cancel();
                if (trayIcon != null) trayIcon.Visible = false; 
                Application.Exit(); 
            });

            trayIcon.ContextMenuStrip = contextMenu;
        }

        static void LaunchVam(string arguments)
        {
            string vamExe = Path.Combine(Config.VamPath, "VaM.exe");

            if (!File.Exists(vamExe))
            {
                ShowTooltip("File Not Found", "VaM.exe is missing from your configured VAM directory.", ToolTipIcon.Error);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = vamExe,
                    Arguments = arguments,
                    WorkingDirectory = Config.VamPath, 
                    UseShellExecute = false
                };
                Process.Start(psi);
                LogDebug($"Launched VaM: {vamExe} {arguments}");
            }
            catch (Exception ex)
            {
                LogError("Failed to launch VaM.", ex);
                ShowTooltip("Launch Failed", "Could not start Virt-A-Mate. Check error log.", ToolTipIcon.Error);
            }
        }

        static void LogDebug(string message)
        {
            if (!IsDebugMode) return;
            try { 
                File.AppendAllText(DebugLogPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n"); 
            } catch { }
        }

        static void LogError(string message, Exception? ex = null)
        {
            try {
                string errorTxt = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {message}\n";
                if (ex != null) errorTxt += $"   Exception: {ex.Message}\n";
                File.AppendAllText(ErrorLogPath, errorTxt);
            } catch { }
        }

        static void ShowTooltip(string title, string message, ToolTipIcon icon)
        {
            if (trayIcon == null || hiddenForm == null) return;

            try 
            {
                hiddenForm.BeginInvoke(new Action(() => {
                    trayIcon.BalloonTipTitle = title;
                    trayIcon.BalloonTipText = message;
                    trayIcon.BalloonTipIcon = icon;
                    trayIcon.ShowBalloonTip(3000);
                }));
            } 
            catch (Exception ex) 
            {
                LogError("Failed to show tooltip", ex);
            }
        }
    }
}