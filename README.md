# VAM Dependency Bridge

VAM Dependency Bridge is a two-part automated system designed to instantly scan Virt-A-Mate (VAM) scenes, detect missing dependencies, and automatically copy those missing `.var` files from your master repository directly into your VAM `AddonPackages` folder.

## ⚖️ License
This project is licensed under **Creative Commons Attribution-ShareAlike 4.0 (CC BY-SA 4.0)**.
* **Attribution:** You must give appropriate credit and provide a link to the original work.
* **ShareAlike:** If you remix, transform, or build upon the material, you must distribute your contributions under the same license as the original.

---

## 🛠️ Installation & Setup

### Part 1: Install 'Everything' (The Search Engine)
The Daemon uses the 'Everything' search engine to locate files across massive hard drives in milliseconds.
1. Download and install **[Everything](https://www.voidtools.com/downloads/)**.
2. Download the **Everything Command-line Interface (es.exe)** from the same page.
3. Extract `es.exe` and place it in the same folder as Everything (usually `C:\Program Files\Everything`).

### Part 2: Install the VAM Bridge
1. Download the latest `VamDependencyBridge.exe` from the **[Releases](https://github.com/hasback-code/vambridge/releases/tag/1.0.0)** page.
2. Download `hasback.VAMBridgePlugin.x.var` and place it in your VAM `AddonPackages` folder.
3. Run `VamDependencyBridge.exe`. Right-click the tray icon -> **Settings**. Set your VAM, Everything, and Repository paths.
   * *Note: The Daemon automatically clears its `error.txt` and `bridge_log.txt` on every fresh start to keep your logs relevant.*

### Part 3: Using the Bridge
1. Add the plugin as a **Session Plugin** in VAM. 
2. A button will appear in the top-left when missing dependencies are detected.
3. **Workflow Tip:** If you move new VARs into your `AddonPackages` while VAM is running, use the **Scan Dependencies** button in the plugin's Custom UI to manually refresh the cache.

---

## ⚡ Optimizing 'Everything' for a VAM Repo
To ensure the Bridge is lightweight and only finds `.var` files in your repository, configure Everything as follows:

1. Open the **Everything** desktop app. 
2. Go to **Tools** -> **Options**.
3. Under **Indexes** -> **NTFS**:
   * Select your system drives (e.g., `C:`) and **Uncheck** "Include in database". 
4. Under **Indexes** -> **Folders**:
   * Click **Add...** and select only your master VAM Repository folder or Network Drive.
5. Under **Exclude** -> **Include only files**, add `*.var`. This makes Everything configured to focus on package files. You can verify this by typing `ext:var` in the Everything search bar to ensure only VAM packages appear.
6. Under **Indexes** -> **Exclude**:
   * Check "Exclude hidden files and folders" and "Exclude system files and folders". 

Click **Apply**. Everything is now a dedicated, ultra-lightweight VAM indexer.

---
### VAM Launch from Tray ###
🚀 Quick Launch Feature

The VAM Dependency Bridge tray icon doubles as a convenient launcher for Virt-A-Mate, allowing you to start the game directly from your system tray.

Launch Options:
1. Desktop Mode: Launches VAM in non-VR mode by applying the -vrmode None argument.
2. VR Mode (Default): Launches VAM using your standard VR configuration (no additional arguments).
3. OpenVR Mode: Forces VAM to launch specifically using the OpenVR runtime with the -vrmode OpenVR argument.  

How It Works:
The daemon uses the VAM Root Directory defined in your settings to locate your VaM.exe file. When a mode is selected, it uses the Windows ProcessStartInfo class to execute the file with the specific Unity VR flags while automatically setting the correct working directory to ensure all internal VAM dependencies load correctly.

---
### 🖥️ Command-Line Options (Debug Mode)
For advanced troubleshooting, the Windows Daemon supports command-line flags to enable detailed logging.

* **Enable Debug Mode:** Launch the daemon with `-d` or `--debug`. This creates a `bridge_log.txt` in the executable folder with step-by-step processing details.
  * *Example:* `VamDependencyBridge.exe -d`
* **Normal Mode:** Launching the executable without any flags (default) will only log critical errors to `error.txt`.


You can create a Windows shortcut to the `.exe`, right-click it, select **Properties**, and add ` -d` to the end of the **Target** field to always launch in debug mode.

---

## Support
If this tool saves you hours of manual file management, consider supporting the development!
☕ **[Support the Creator on Ko-fi](https://ko-fi.com/grepstar)**

---
*Disclaimer: This tool is not affiliated with MeshedVR.*
