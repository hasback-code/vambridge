using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using SimpleJSON;
using MVR.FileManagementSecure;

public class VAMBridgePlugin : MVRScript
{
    public const string VERSION = "1.0.0"; 
    
    private const string PLUGIN_DATA_DIR = "Saves/PluginData/VAMBridgePlugin";
    private const string REQUEST_PATH = PLUGIN_DATA_DIR + "/request.txt";
    private const string RESPONSE_PATH = PLUGIN_DATA_DIR + "/response.txt";
    
    private JSONStorableString _status;
    private JSONStorableBool _enableDebug;
    private JSONStorableBool _autoScanOnLoad;
    private JSONStorableBool _showPlayModeBtn; 
    private JSONStorableString _errorText;

    private List<string> _missingPackages = new List<string>();
    private Dictionary<string, string> _installedPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> _metaCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    
    private string _destDir = "AddonPackages"; 
    private bool _cancelScan = false;
    private bool _cancelResolve = false;
    private bool _isScanning = false;
    private bool _isFirstBoot = true; 
    private UIDynamicButton _cancelButton;

    // UX STATE MACHINE: 0=Normal, 1=Resolving, 2=Partial, 3=Failed, 4=Success
    private int _btnState = 0; 
    private int _lastMissingCount = 0;

    public override void Init()
    {
        _errorText = new JSONStorableString("Errors", "");
        RegisterString(_errorText);

        try
        {
            CreateHeader($"VAM Bridge Plugin v{VERSION}", 30);
            
            _status = new JSONStorableString("Status", "Ready.");
            RegisterString(_status);
            var statusField = CreateTextField(_status, true);
            if (statusField != null) statusField.height = 80;

            RegisterAction(new JSONStorableAction("Scan Dependencies", () => {
                StopAllCoroutines();
                _btnState = 0;
                StartCoroutine(ScanDependencies());
            }));
            
            RegisterAction(new JSONStorableAction("Resolve Missing", () => StartCoroutine(ResolveMissing())));

            var scanBtn = CreateButton("Scan Dependencies");
            if (scanBtn != null) scanBtn.button.onClick.AddListener(() => {
                StopAllCoroutines();
                _btnState = 0;
                StartCoroutine(ScanDependencies());
            });

            var resolveBtn = CreateButton("Resolve Missing");
            if (resolveBtn != null) resolveBtn.button.onClick.AddListener(() => StartCoroutine(ResolveMissing()));

            _enableDebug = new JSONStorableBool("Enable Debug", false) { isStorable = true };
            RegisterBool(_enableDebug);
            CreateToggle(_enableDebug);

            _autoScanOnLoad = new JSONStorableBool("Auto Scan on Load", true) { isStorable = true };
            RegisterBool(_autoScanOnLoad);
            CreateToggle(_autoScanOnLoad);

            _showPlayModeBtn = new JSONStorableBool("Show Play Mode Button", true) { isStorable = true };
            RegisterBool(_showPlayModeBtn);
            CreateToggle(_showPlayModeBtn);

            var errorFieldUI = CreateTextField(_errorText, true);
            if (errorFieldUI != null) errorFieldUI.height = 150;
            
            _showPlayModeBtn.setCallbackFunction = delegate(bool val) {
                if (val && !_isFirstBoot) {
                    _btnState = 0;
                    StopAllCoroutines();
                    StartCoroutine(ScanDependencies());
                }
            };

            _autoScanOnLoad.setCallbackFunction = delegate(bool val) {
                SuperController.singleton.onSceneLoadedHandlers -= OnSceneLoaded;
                if (val) SuperController.singleton.onSceneLoadedHandlers += OnSceneLoaded;
            };

            if (_autoScanOnLoad.val) {
                SuperController.singleton.onSceneLoadedHandlers += OnSceneLoaded;
            }

            BridgeLogger.Init(_enableDebug);
            BridgeLogger.Info($"=== VAM Bridge Plugin v{VERSION} INITIALIZED ===");
        }
        catch (Exception e)
        {
            SuperController.LogError("[VAM Bridge] Init error: " + e.Message);
            if (_errorText != null) _errorText.val = "Init error: " + e.Message;
        }
    }

    private void AbortOperations(string message)
    {
        _cancelScan = true;
        _cancelResolve = true;
        _isScanning = false;
        _btnState = 0;
        if (_status != null) _status.val = message;
        if (_cancelButton != null) _cancelButton.button.interactable = false;
    }

    void OnGUI()
    {
        if (SuperController.singleton == null || SuperController.singleton.isLoading) return;
        if (_showPlayModeBtn == null || !_showPlayModeBtn.val) return;
        if (_missingPackages == null) return;

        if (_missingPackages.Count > 0 || _btnState == 4)
        {
            Color defaultColor = GUI.backgroundColor;
            string btnText = $"Resolve ({_missingPackages.Count}) Missing Packages";
            
            if (_btnState == 1) {
                GUI.backgroundColor = Color.cyan;
                btnText = "Resolving... Please wait.";
            } else if (_btnState == 2) {
                GUI.backgroundColor = Color.yellow;
                btnText = $"Partial! ({_missingPackages.Count}) still missing. Retry?";
            } else if (_btnState == 3) {
                GUI.backgroundColor = new Color(1f, 0.4f, 0.4f); 
                btnText = $"Failed! ({_missingPackages.Count}) missing. Retry?";
            } else if (_btnState == 4) {
                GUI.backgroundColor = Color.green;
                btnText = "Resolved! Click to dismiss.\n(Reload preset manually)";
            }

            if (GUI.Button(new Rect(20, 20, 240, 45), btnText))
            {
                if (_btnState == 4)
                {
                    _btnState = 0;
                    _missingPackages.Clear(); 
                }
                else if (_btnState != 1)
                {
                    StartCoroutine(ResolveMissing());
                }
            }
            GUI.backgroundColor = defaultColor; 
        }
    }

    private void OnSceneLoaded() 
    { 
        if (_isFirstBoot || Time.realtimeSinceStartup < 15f) 
        {
            _isFirstBoot = false;
            BridgeLogger.Info("Boot sequence detected. Skipping auto-scan.");
            return;
        }

        StopAllCoroutines(); 
        _btnState = 0;
        StartCoroutine(DelayedScan()); 
    }

    private IEnumerator DelayedScan()
    {
        while (SuperController.singleton != null && SuperController.singleton.isLoading)
        {
            yield return null;
        }

        if (Time.realtimeSinceStartup < 15f) 
        {
            BridgeLogger.Info("VAM is booting. Skipping auto-scan to protect native file system.");
            yield break;
        }

        yield return new WaitForSeconds(1.5f); 
        StartCoroutine(ScanDependencies());
    }

    private void GetAllVarFiles(string dir, List<string> results)
    {
        try
        {
            if (!FileManagerSecure.DirectoryExists(dir)) return;
            
            string[] files = FileManagerSecure.GetFiles(dir);
            foreach (string file in files)
            {
                if (file.EndsWith(".var", StringComparison.OrdinalIgnoreCase)) results.Add(file);
            }

            string[] subDirs = FileManagerSecure.GetDirectories(dir);
            foreach (string subDir in subDirs) GetAllVarFiles(subDir, results);
        }
        catch { }
    }

    private void RefreshPackageCache()
    {
        _installedPackages.Clear();
        try
        {
            List<string> varFiles = new List<string>();
            GetAllVarFiles(_destDir, varFiles);

            foreach (var file in varFiles)
            {
                int slashIndex = file.LastIndexOf('/');
                if (slashIndex == -1) slashIndex = file.LastIndexOf('\\');
                string fileName = file.Substring(slashIndex + 1);

                string packageId = fileName.EndsWith(".var", StringComparison.OrdinalIgnoreCase) 
                                   ? fileName.Substring(0, fileName.Length - 4) 
                                   : fileName;

                if (!_installedPackages.ContainsKey(packageId)) _installedPackages[packageId] = file;

                var parts = packageId.Split('.');
                int currentVer; 
                
                if (parts.Length >= 3 && int.TryParse(parts.Last(), out currentVer))
                {
                    string latestKey = string.Join(".", parts.Take(parts.Length - 1).ToArray()) + ".latest"; 
                    
                    if (!_installedPackages.ContainsKey(latestKey))
                    {
                        _installedPackages[latestKey] = file;
                    }
                    else
                    {
                        string existingName = _installedPackages[latestKey];
                        int exSlashIndex = existingName.LastIndexOf('/');
                        if (exSlashIndex == -1) exSlashIndex = existingName.LastIndexOf('\\');
                        string exFileName = existingName.Substring(exSlashIndex + 1);
                        string cleanExisting = exFileName.EndsWith(".var", StringComparison.OrdinalIgnoreCase)
                                               ? exFileName.Substring(0, exFileName.Length - 4)
                                               : exFileName;
                        
                        int existingVer; 
                        if (int.TryParse(cleanExisting.Split('.').Last(), out existingVer) && currentVer > existingVer)
                        {
                            _installedPackages[latestKey] = file;
                        }
                    }
                }
            }
            BridgeLogger.Debug($"Cache Built: {_installedPackages.Count} unique packages found.");
        }
        catch (Exception e)
        {
            BridgeLogger.Warn("Failed to build package cache: " + e.Message);
        }
    }

    private IEnumerator ScanDependencies()
    {
        if (_isScanning) yield break;
        _isScanning = true;
        _btnState = 0;

        BridgeLogger.Debug($"--- STARTING SCAN (v{VERSION}) ---");
        
        _cancelScan = false;
        if (_cancelButton != null) _cancelButton.button.interactable = true;
        _status.val = "Building package cache...";
        yield return null;

        RefreshPackageCache();

        _status.val = "Scanning scene...";
        _missingPackages.Clear();
        _metaCache.Clear();

        var sceneJson = SuperController.singleton.GetSaveJSON();
        var packageRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Uses unrestricted JSON scanning to inherently catch all dependencies, including parent vars
        FindPackageRefs(sceneJson, packageRefs);

        BridgeLogger.Debug($"Scene JSON contains {packageRefs.Count} direct package references.");

        var toProcess = new Queue<string>(packageRefs);
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (toProcess.Count > 0 && !_cancelScan)
        {
            if (SuperController.singleton.isLoading) 
            {
                AbortOperations("Scan aborted due to scene load.");
                yield break;
            }

            float startTime = Time.realtimeSinceStartup;
            
            while((Time.realtimeSinceStartup - startTime) < 0.01f && toProcess.Count > 0)
            {
                var refId = toProcess.Dequeue();
                if (processed.Contains(refId)) continue;
                processed.Add(refId);

                if (!_installedPackages.ContainsKey(refId))
                {
                    if (!_missingPackages.Contains(refId)) 
                    {
                        _missingPackages.Add(refId);
                        BridgeLogger.Debug($"MARKED MISSING: {refId}");
                    }
                }
                else
                {
                    var subDeps = GetSubDependencies(refId);
                    foreach (var sub in subDeps)
                    {
                        if (!processed.Contains(sub)) toProcess.Enqueue(sub);
                    }
                }
            }
            yield return null;
        }

        if (_cancelScan)
            _status.val = "Scan cancelled.";
        else if (!_missingPackages.Any())
            _status.val = "No missing dependencies found.";
        else
            _status.val = $"Found {_missingPackages.Count} missing packages. Click Resolve.";

        BridgeLogger.Debug($"--- SCAN COMPLETE. Total Missing: {_missingPackages.Count} ---");

        if (_cancelButton != null) _cancelButton.button.interactable = false;
        _isScanning = false;
    }

    private List<string> GetSubDependencies(string packageId)
    {
        if (_metaCache.ContainsKey(packageId)) return _metaCache[packageId];

        var subDeps = new List<string>();
        string targetId = packageId;
        
        if (_installedPackages.ContainsKey(packageId))
        {
            string filePath = _installedPackages[packageId];
            int slashIndex = filePath.LastIndexOf('/');
            if (slashIndex == -1) slashIndex = filePath.LastIndexOf('\\');
            string fileName = filePath.Substring(slashIndex + 1);
            
            if (fileName.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
            {
                targetId = fileName.Substring(0, fileName.Length - 4);
            }
        }
        
        string virtualPath = $"{targetId}:/meta.json";

        try
        {
            if (FileManagerSecure.FileExists(virtualPath))
            {
                string jsonContent = FileManagerSecure.ReadAllText(virtualPath);
                if (!string.IsNullOrEmpty(jsonContent))
                {
                    var metaJson = JSON.Parse(jsonContent);
                    var depsNode = metaJson["dependencies"] as JSONClass;
                    if (depsNode != null)
                    {
                        foreach (string key in depsNode.Keys) subDeps.Add(key);
                    }
                }
            }
        }
        catch { }

        _metaCache[packageId] = subDeps;
        return subDeps;
    }

    private void FindPackageRefs(JSONNode node, HashSet<string> refs)
    {
        if (node == null) return;

        if (node is JSONClass)
        {
            JSONClass classNode = node as JSONClass;
            foreach (string key in classNode.Keys)
            {
                var val = classNode[key];
                if (val is JSONClass || val is JSONArray) FindPackageRefs(val, refs);
                else CheckAndAddRef(val.Value, refs); 
            }
        }
        else if (node is JSONArray)
        {
            JSONArray arrayNode = node as JSONArray;
            foreach (JSONNode child in arrayNode)
            {
                if (child is JSONClass || child is JSONArray) FindPackageRefs(child, refs);
                else CheckAndAddRef(child.Value, refs);
            }
        }
        else CheckAndAddRef(node.Value, refs);
    }

    private void CheckAndAddRef(string strVal, HashSet<string> refs)
    {
        if (string.IsNullOrEmpty(strVal)) return;
        if (strVal.Contains(":/"))
        {
            var parts = strVal.Split(':');
            var pkgId = parts[0];
            
            if (pkgId.Contains(".") && (pkgId.EndsWith(".latest", StringComparison.OrdinalIgnoreCase) || char.IsDigit(pkgId[pkgId.Length - 1])))
            {
                refs.Add(pkgId);
            }
        }
    }

    private IEnumerator ResolveMissing()
    {
        _cancelResolve = false;

        if (!_missingPackages.Any())
        {
            _status.val = "No packages to resolve. Scan first.";
            yield break;
        }

        _btnState = 1; 
        _lastMissingCount = _missingPackages.Count;

        if (FileManagerSecure.FileExists(RESPONSE_PATH)) 
        {
            try { FileManagerSecure.DeleteFile(RESPONSE_PATH); } catch { }
        }

        _status.val = "Exporting request to Bridge App...";
        
        try {
            if (!FileManagerSecure.DirectoryExists(PLUGIN_DATA_DIR)) FileManagerSecure.CreateDirectory(PLUGIN_DATA_DIR);
            
            var exportLines = new List<string> { $"VERSION:{VERSION}" };
            exportLines.AddRange(_missingPackages);
            string packageString = string.Join("\n", exportLines.ToArray());
            FileManagerSecure.WriteAllText(REQUEST_PATH, packageString);
            BridgeLogger.Info($"Exported {exportLines.Count - 1} packages to request.txt.");
        } catch (Exception e) {
            _errorText.val = "Error writing request: " + e.Message;
            BridgeLogger.Warn("Failed to export request: " + e.Message);
            _btnState = 3; 
            yield break;
        }

        _status.val = "Bridge is working... (Do not close VAM)";
        float timeout = 25f; 
        float timer = 0f;

        while (!FileManagerSecure.FileExists(RESPONSE_PATH))
        {
            if (SuperController.singleton.isLoading || _cancelResolve) 
            {
                AbortOperations("Operation Cancelled.");
                yield break;
            }

            if (timer > timeout) {
                _status.val = "Error: Bridge timed out. Is the Windows App running?";
                _errorText.val = "Please launch the VamDependencyBridge console application.";
                BridgeLogger.Warn("Timed out waiting for daemon response.txt.");
                _btnState = 3; 
                yield break;
            }
            timer += Time.deltaTime;
            yield return null;
        }

        _status.val = "Reading results...";
        string finalMessage = "Error reading response.";
        int retries = 5;
        bool readSuccess = false;
        
        while (retries > 0 && !readSuccess) 
        {
            if (SuperController.singleton.isLoading) 
            {
                AbortOperations("Scene load detected during read.");
                yield break;
            }

            try {
                finalMessage = FileManagerSecure.ReadAllText(RESPONSE_PATH);
                readSuccess = true; 
            } catch { retries--; }
            if (!readSuccess) yield return new WaitForSeconds(0.2f);
        }

        try { FileManagerSecure.DeleteFile(RESPONSE_PATH); } catch { }
        
        _status.val = finalMessage;

        if (finalMessage.Contains("Success") || finalMessage.Contains("Copied")) {
            _status.val += "\nRescanning packages in VAM...";
            yield return new WaitForSeconds(0.5f);
            
            if (SuperController.singleton.isLoading) 
            {
                AbortOperations("Scene load detected. Rescan aborted.");
                yield break;
            }

            try { SuperController.singleton.RescanPackages(); } catch { }

            RefreshPackageCache(); 
            
            int daemonErrors = _lastMissingCount; 
            try 
            {
                string searchStr = "Errors/Missing:";
                int idx = finalMessage.IndexOf(searchStr);
                if (idx != -1) 
                {
                    int startIdx = idx + searchStr.Length;
                    int endIdx = finalMessage.IndexOf('.', startIdx);
                    if (endIdx == -1) endIdx = finalMessage.Length;
                    
                    string numStr = finalMessage.Substring(startIdx, endIdx - startIdx).Trim();
                    int.TryParse(numStr, out daemonErrors);
                }
            } 
            catch { }

            if (daemonErrors == 0) {
                _btnState = 4;
                _status.val = finalMessage + "\n\nRescan Complete!\n(Click the preset/scene in your menu again to apply the copied files).";
            } else if (daemonErrors < _lastMissingCount) {
                while (_missingPackages.Count > daemonErrors && _missingPackages.Count > 0) {
                    _missingPackages.RemoveAt(0); 
                }
                _btnState = 2;
                _status.val = finalMessage + $"\n\nRescan Complete. {daemonErrors} still missing.";
            } else {
                _btnState = 3;
            }
        }
        else
        {
            _btnState = 3;
        }
    }

    private void CreateHeader(string text, float height)
    {
        var tf = CreateTextField(new JSONStorableString("header", $"<b>{text}</b>"), true);
        if (tf != null) {
            tf.height = height;
            tf.backgroundColor = Color.clear;
        }
    }

    public void OnDestroy()
    {
        StopAllCoroutines(); 
        SuperController.singleton.onSceneLoadedHandlers -= OnSceneLoaded;
    }
}

public static class BridgeLogger
{
    private static JSONStorableBool _enableDebug;

    public static void Init(JSONStorableBool debugFlag)
    {
        _enableDebug = debugFlag;
    }

    public static void Info(string msg) {
        SuperController.LogMessage($"[VAM Bridge] {msg}");
    }

    public static void Debug(string msg) {
        if (_enableDebug != null && _enableDebug.val) {
            SuperController.LogMessage($"[VAM Bridge] {msg}");
        }
    }

    public static void Warn(string msg) {
        SuperController.LogError($"[VAM Bridge] {msg}");
    }
}