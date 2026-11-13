# vPilot Tablet Dashboard

A lightweight, responsive web dashboard and companion mirror for [vPilot](https://vpilot.rosscarlson.dev/) (VATSIM pilot client). View controllers in range and monitor active COM1 and COM2 frequencies in real time — on any iPad, Android tablet, phone, or directly inside [Virtual EFB](https://flightsim.to/addon/118791/virtual-efb-electronic-flight-bag-for-android-pc-checklists-wind-calc-simbrief-navigraph).

<p align="center">
  <img src="screenshots/dashboard-preview.jpg" alt="vPilot Tablet Dashboard Preview" width="850">
</p>

---

## ✨ Features

- **Realistic COM1 & COM2 RX/TX Indicators**: Live indicators styled identically to the real vPilot client (`#00a2ff`). The TX light illuminates on your active transmit radio when you press your Push-To-Talk (PTT) key, and the RX light indicates incoming audio.
- **In-Cockpit & Virtual EFB Compatible**: Seamlessly integrates into [Virtual EFB](https://flightsim.to/addon/118791/virtual-efb-electronic-flight-bag-for-android-pc-checklists-wind-calc-simbrief-navigraph) or in-cockpit web browser tablets using `http://localhost:8085`.
- **Automatic Callsign Sync**: Automatically synchronizes your active callsign straight from vPilot once connected to the VATSIM network — no manual SimBrief Pilot ID or callsign entry required.
- **Independent Scrolling Panels**: The controller list and chat/message tabs scroll separately while keeping the top radio header and callsign fixed in view.
- **Silent Built-in Web Server (Zero Dependencies)**: When vPilot starts, the plugin serves the dashboard directly on port `8085` and WebSockets on port `9090` with zero external tools (no Python required), and closes cleanly when vPilot exits.
- **Zero Configuration IP Handling**: The dashboard automatically detects your PC's IP address dynamically using `window.location.hostname`. No configuration files or manual IP edits required.

---

## 📁 Repository Structure

```
vPilot-Tablet-Dashboard/
├── Install.bat                 # 1-Click Installer (unblocks DLLs & installs to vPilot)
├── screenshots/
│   └── dashboard-preview.jpg   # Tablet dashboard preview
├── Dashboard/
│   ├── index.html              # Responsive web dashboard
│   └── Launch_Dashboard.bat    # Optional standalone launcher (without vPilot)
├── Plugin/
│   ├── vPilotTabletMirror.dll  # Pre-compiled vPilot companion plugin (includes built-in UI)
│   ├── Fleck.dll               # WebSocket server dependency
│   └── Newtonsoft.Json.dll     # JSON serialization dependency
└── Source/
    ├── vPilotTabletMirror.sln  # Visual Studio solution
    └── vPilotTabletMirror/     # C# plugin source code
```

---

## 🚀 Quick Start & Installation

### Option A: 1-Click Install (Recommended)

1. Extract the entire downloaded ZIP archive to any folder (e.g. `Documents`).
2. Double-click **`Install.bat`** inside the extracted folder.
   - *(If Windows Defender SmartScreen appears, click **More info** → **Run anyway**)*.
   - `Install.bat` automatically **unblocks Windows security locks**, installs all plugin DLLs into `%LOCALAPPDATA%\vPilot\Plugins`, configures the dashboard path, and shows your exact connection URL.

---

### Option B: Manual Installation

> [!IMPORTANT]
> **Windows Security Block Notice:** Whenever you download `.dll` files from the internet (e.g. Flightsim.to), Windows blocks them with *"Mark of the Web"*. Before extracting or copying manually, **right-click the downloaded `.zip` (or each `.dll`) → Properties → check "Unblock" → Apply → OK**, otherwise vPilot will be blocked from loading the plugin!

1. Close vPilot if it is currently running.
2. Open Windows File Explorer and navigate to your vPilot plugins folder:
   ```
   %LOCALAPPDATA%\vPilot\Plugins
   ```
   *(Paste the path above into the File Explorer address bar and press **Enter** — it resolves to `C:\Users\<YourUsername>\AppData\Local\vPilot\Plugins`)*.
3. Copy all files from the `Plugin/` directory into this folder (make sure each DLL is **Unblocked**):
   - `vPilotTabletMirror.dll`
   - `Fleck.dll`
   - `Newtonsoft.Json.dll`
4. *(Optional)* If you keep the `Dashboard` folder outside your `Documents` folder, you can create a text file named `dashboard_path.txt` inside `%LOCALAPPDATA%\vPilot\Plugins\` containing the full path to your `Dashboard` directory. *(Note: Even without a `Dashboard` folder on disk, the plugin has the dashboard built in as a fallback!).*

---

### Step 2: Find Your PC's Local IP Address

To view the dashboard from your tablet or another device on your local Wi-Fi:

1. On your flight sim PC, open **Command Prompt** or **PowerShell** (Press `Win + R`, type `cmd`, and press **Enter**).
2. Type `ipconfig` and press **Enter**.
3. Look for your active network connection (Wi-Fi or Ethernet) and find the **IPv4 Address**:
   ```
   IPv4 Address. . . . . . . . . . . : 192.168.1.50
   ```
   *(Your address will typically begin with `192.168.x.x` or `10.0.x.x`)*.

> **Tip: Using a Fixed / Static IP Works Better**  
> Assigning a fixed (static) local IP address to your flight simulator PC (or configuring a DHCP reservation in your home router) is strongly recommended. That way, your PC's IP address will never change, and you can save a bookmark or shortcut on your tablet without ever having to look up the IP address again.

---

### Step 3: Connect Your Tablet or Virtual EFB

1. Start **vPilot**. The plugin will automatically initialize the WebSocket server (port `9090`) and launch the dashboard web server (port `8085`).
2. Open your preferred device or browser:
   - **Inside [Virtual EFB](https://flightsim.to/addon/118791/virtual-efb-electronic-flight-bag-for-android-pc-checklists-wind-calc-simbrief-navigraph) or Simulator PC Browser:**  
     Navigate to:
     ```
     http://localhost:8085
     ```
   - **On a Physical iPad, Tablet, or Phone (Same Wi-Fi):**  
     Navigate to:
     ```
     http://<YOUR-PC-IP>:8085
     ```
     *Example: `http://192.168.1.50:8085`*
3. The dashboard will load and automatically connect to your simulator session.

---

## 🔒 Firewall & Network Troubleshooting

If your dashboard says **"Connecting to vPilot mirror..."** or your tablet cannot load the page:

- **Stuck on "Connecting to vPilot mirror..."**:
  - Make sure **vPilot** is running (and wait 10–15 seconds for its initial simulator scan to finish).
  - If you installed manually without running `Install.bat`, Windows may have blocked the DLLs. Right-click each `.dll` inside `%LOCALAPPDATA%\vPilot\Plugins\` → **Properties** → check **Unblock** → **OK**, then restart vPilot.
- **Same Network**: Verify that your tablet is connected to the same local Wi-Fi or LAN as your flight simulator PC (not a guest Wi-Fi network with client isolation).
- **Windows Defender Firewall**:
  1. Open **Windows Security** → **Firewall & network protection**.
  2. Click **Allow an app through firewall**.
  3. Ensure **vPilot** has checkboxes enabled for **Private** networks.
  4. Alternatively, add an Inbound Rule in Windows Firewall allowing TCP ports `8085` and `9090`.

---

## 🛠️ Building from Source

For developers wishing to modify or compile the plugin:

1. Open `Source/vPilotTabletMirror.sln` in **Visual Studio 2022** (or build with MSBuild).
2. Ensure target framework is set to **.NET Framework 4.7.2**.
3. Select **Release** configuration and build the solution.
4. Compiled output will be generated in `Source/vPilotTabletMirror/bin/Release/`.

---

## 📄 License & Disclaimer

This project is an open-source, community-created companion tool and is not officially affiliated with or endorsed by VATSIM or the author of vPilot.
