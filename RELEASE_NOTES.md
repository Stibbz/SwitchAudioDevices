# AudioPilot — Release Notes

---

## v2.0 — 2026-05-20

### Highlights

- **Rebranded to AudioPilot** — formerly *SwitchAudioDevices*
- **AirPods & modern Bluetooth devices now work reliably** — two separate fixes landed that together resolve silent connection failures on BLE-hybrid and Apple devices
- **Device list updates in real time** — no more stale list after a Bluetooth device connects or disconnects

---

### What's new since v1.0

#### Bluetooth reliability
- **Fix: AirPods and BLE-hybrid devices failing to connect** (`3925477`, `9d60599`)  
  Classic `BluetoothSetServiceState` always returns error 87 on Apple devices — the connection now relies entirely on the WinRT RFCOMM path for those devices. An ACL socket is held open during A2DP negotiation to prevent the link dropping after ~3 s.

#### Device list
- **Reactive WASAPI updates** (`c790001`)  
  The device list now reacts to `IMMNotificationClient` endpoint state change events and reloads automatically. Previously the list could go stale after a Bluetooth device connected or a USB headset was plugged in.
- **Guard against transient COM exceptions** (`586d763`)  
  Endpoint enumeration no longer crashes on transient `COMException`s that Windows occasionally fires during rapid device state changes.

#### UI & icons
- **Updated tray and default-speaker icons** (`74f7865`)  
  Both now use the E772 volume glyph from Segoe MDL2 Assets, matching the Windows 11 system icon style.

#### Housekeeping
- Renamed project, namespace, settings folder, log file, and startup shortcut from `SwitchAudioDevices` → `AudioPilot` (`58f26d3`)

---

## v1.0 — 2026-03-25

Initial release.

### Features at launch

| Area | Detail |
|---|---|
| **Tray popup** | Left-click tray icon to open device picker; auto-hides on focus loss |
| **Device switching** | One-click default audio device switching via undocumented `IPolicyConfig` COM interface |
| **Bluetooth connect & switch** | Connects a paired but disconnected BT device, then sets it as default |
| **Global hotkeys** | Configurable next/prev device hotkeys; cycles through enabled devices only |
| **Hotkey BT support** | Hotkey cycle attempts a silent BT connection before switching |
| **Double-press skip** | Second hotkey press within 3 s on a failed BT device skips past it |
| **Position indicator** | Hotkey switch shows `[2/4]` style position in the Windows notification |
| **Windows notifications** | Balloon tip on hotkey cycle and BT connection failure |
| **Volume control** | Per-device volume slider; changes propagate to WASAPI immediately |
| **Test sound** | Button on the active device card to play a test tone |
| **Device icons** | Smart icons based on endpoint name (headphones, speakers, HDMI, etc.) |
| **Settings panel** | Toggle device visibility; configure hotkeys; launch-at-startup toggle |
| **Async navigation** | Settings panel loads asynchronously with a 5 s BT cache to eliminate lag |
| **Status bar** | Transient status messages in the footer (error state shown in red) |
| **Colour scheme** | Dark grey base (`#292929`), orange accent (`#FF9068`), semantic status colours |
| **Startup shortcut** | Optional Windows startup shortcut managed from settings |
