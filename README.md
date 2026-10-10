# OpenMouseConfig

A native Windows configuration utility for gaming mice: a desktop alternative to WebHID
configurators like [mouse.xyz](https://mouse.xyz). The first target is the **G-Wolves HTS Plus Pro 8K**.

You can add more mice **without recompiling**. Everything device-specific (VID/PID, HID usage
page, report type and every byte of every command) is in [`devices.json`](devices.json).

> [!WARNING]
> **The shipped G-Wolves definition is a placeholder.** Its VID/PID and payloads are invented and
> marked `"verified": false`. While a definition is unverified, the app stays in **dry-run** mode:
> it builds every packet and shows it in the developer console, but sends nothing to the mouse.
> Fill in the real values from your own capture (see [How to add a mouse](#how-to-add-or-fix-a-mouse))
> before you turn dry-run off.

---

## Contents

- [Features](#features)
- [Repository layout](#repository-layout)
- [Architecture](#architecture)
- [Project setup guide](#project-setup-guide)
- [Building the installer](#building-the-installer)
- [Using the app](#using-the-app)
- [How to add (or fix) a mouse](#how-to-add-or-fix-a-mouse)
- [devices.json reference](#devicesjson-reference)
- [Troubleshooting](#troubleshooting)

---

## Features

| Area | What you get |
|---|---|
| Device selector | Lists connected mice that match an entry in `devices.json`. It rescans on USB plug/unplug, and F5 rescans manually. |
| Status | Connection indicator and a battery percentage with a charging flag (polled every `batteryPollSeconds`). |
| Polling rate | 125 / 250 / 500 / 1000 / 2000 / 4000 / 8000 Hz. The list comes from `capabilities.pollingRates`. |
| DPI | 4 stages, each with a slider and a text box (up to 26 000 DPI, snapped to the device's step). A badge selects the active stage. |
| Debounce | Slider from 0 to 20 ms. |
| Lift-off distance | Segmented 1 mm / 2 mm control. Any list of distances works, e.g. `[0.7, 1, 2]`. |
| Motion Sync | On/off switch. |
| Read-back | Reads the mouse's current settings on connect, if the definition provides read commands. |
| Developer console | Live TX/RX hex log, dry-run switch, offline mode, a HID interface lister and a raw packet sender for replaying captures. |
| Extensibility | Bundled `devices.json` plus your own `%APPDATA%\OpenMouseConfig\devices\*.json`, which override bundled entries by `id`. |

The HID layer calls the native Windows HID API directly (`hid.dll`, `cfgmgr32.dll`) through
P/Invoke. Chromium's WebHID uses the same API, so the app sees the same devices and reports as
mouse.xyz.

## Repository layout

```
├─ devices.json                         ← all device definitions (copied next to the .exe)
├─ OpenMouseConfig.sln
├─ src/
│  ├─ OpenMouseConfig.Core/             ← UI-free library (net10.0), unit-tested
│  │  ├─ Definitions/                   devices.json model, loader/merger, validator
│  │  ├─ Protocol/                      hex parsing, field codec, checksums, PayloadBuilder
│  │  ├─ Hid/                           IHidBackend / IHidDevice abstraction
│  │  │  └─ Windows/                    native implementation (P/Invoke)
│  │  └─ Devices/                       MouseController, MouseSettings, DeviceMatcher
│  └─ OpenMouseConfig/                  ← WPF app (net10.0-windows), MVVM
│     ├─ ViewModels/                    MainViewModel, DpiStageViewModel, DeviceOptionViewModel
│     ├─ Services/                      paths, settings store, UI log, dialogs, debouncer
│     ├─ Themes/Dark.xaml               all control templates and colours
│     └─ MainWindow.xaml                the dashboard
├─ tests/OpenMouseConfig.Core.Tests/    xUnit tests for payloads, JSON loading and the controller
└─ installer/
   ├─ OpenMouseConfig.iss               Inno Setup script
   └─ build-installer.ps1               publish + compile installer in one step
```

## Architecture

```
 View (XAML)            ViewModel                    Core (no UI)                               Windows
┌────────────┐ binding ┌───────────────┐ typed call ┌──────────────────┐ bytes ┌───────────────┐ P/Invoke ┌─────────┐
│ MainWindow │◀──────▶│ MainViewModel │───────────▶│ MouseController  │──────▶│ IHidDevice    │────────▶│ hid.dll │
│  sliders,  │         │  debounce,    │            │  ├ PayloadBuilder│       │ (HidDevice)   │         └─────────┘
│  toggles…  │         │  dry-run      │            │  └ response match│       └───────────────┘
└────────────┘         └───────────────┘            └────────▲─────────┘
                                                             │ templates, byte indices, transforms
                                                    ┌────────┴─────────┐
                                                    │   devices.json   │
                                                    └──────────────────┘
```

1. **View → ViewModel.** For example, the user drags DPI stage 3 to 1 600. The binding updates
   `DpiStageViewModel.Dpi`, which is snapped to the device's step. The `MainViewModel` debounces
   the change for 250 ms.
2. **ViewModel → Core.** `MouseController.SetDpiStageAsync(settings, stage: 3)` looks up the
   `setDpiStage` command in the definition.
3. **Core.** `PayloadBuilder` parses the hex template and zero-pads it to the report length
   Windows reports for the device. It writes each field (`stage` → byte 2 with `add: -1`,
   `dpi` → bytes 3-4 with `divide: 50`, little endian) and then applies the checksum.
4. **HID.** The report goes out as a feature report (`HidD_SetFeature`) or an output report
   (overlapped `WriteFile`). If the command declares a response, the controller reads feature or
   input reports until one matches `expect`, then decodes it with the same field rules in reverse.

The Core library has no WPF dependency and targets plain `net10.0`, so its tests run anywhere.
Swapping in another HID backend (HidSharp, hidapi or a mock) means implementing two interfaces.

## Project setup guide

### Prerequisites

| Tool | Version | Notes |
|---|---|---|
| Windows | 10 (1809+) or 11, x64 | Needed to run the app. Building also works on Linux/macOS through `EnableWindowsTargeting`. |
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0 | `dotnet --version` should print `10.x` |
| IDE (optional) | Visual Studio 2026, Rider, or VS Code with C# Dev Kit | Any IDE that supports .NET 10 / WPF |
| [Inno Setup](https://jrsoftware.org/isdl.php) | 6.3+ | Only needed for building the installer (`winget install JRSoftware.InnoSetup`) |
| [Wireshark](https://www.wireshark.org/) + USBPcap | latest | Only needed for capturing packets of new mice |

### Build, test and run

```powershell
git clone <this repo>
cd <repo>
dotnet restore
dotnet test                                   # payload engine, JSON loader, controller
dotnet run --project src/OpenMouseConfig      # starts the app
```

### NuGet packages

| Project | Package | Version | Purpose |
|---|---|---|---|
| OpenMouseConfig | `CommunityToolkit.Mvvm` | 8.4.0 | `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` source generators |
| OpenMouseConfig.Core.Tests | `xunit` | 2.9.3 | test framework |
| OpenMouseConfig.Core.Tests | `xunit.runner.visualstudio` | 3.1.4 | test runner |
| OpenMouseConfig.Core.Tests | `Microsoft.NET.Test.Sdk` | 17.14.1 | `dotnet test` host |

There is no HID package: the native API is called directly (`Core/Hid/Windows/NativeMethods.cs`).
JSON uses the built-in `System.Text.Json`.

### Re-creating the skeleton from scratch (for reference)

```powershell
dotnet new sln -n OpenMouseConfig --format sln
dotnet new classlib -n OpenMouseConfig.Core        -o src/OpenMouseConfig.Core        -f net10.0
dotnet new wpf      -n OpenMouseConfig             -o src/OpenMouseConfig             -f net10.0
dotnet new xunit    -n OpenMouseConfig.Core.Tests  -o tests/OpenMouseConfig.Core.Tests -f net10.0
dotnet sln add src/OpenMouseConfig.Core src/OpenMouseConfig tests/OpenMouseConfig.Core.Tests
dotnet add src/OpenMouseConfig reference src/OpenMouseConfig.Core
dotnet add tests/OpenMouseConfig.Core.Tests reference src/OpenMouseConfig.Core
dotnet add src/OpenMouseConfig package CommunityToolkit.Mvvm --version 8.4.0
```

Then set `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the Core project (for overlapped I/O).
Also link `devices.json` into the app project with `CopyToOutputDirectory`, as
`src/OpenMouseConfig/OpenMouseConfig.csproj` does.

## Building the installer

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 0.1.0
```

The script:

1. runs the unit tests (skip them with `-SkipTests`)
2. publishes a self-contained `win-x64` build to `artifacts\publish\win-x64`, so end users don't need .NET installed
3. compiles `installer\OpenMouseConfig.iss` with Inno Setup into
   `artifacts\installer\OpenMouseConfig-Setup-0.1.0-x64.exe`

About the installer:

- It installs per user by default, under `%LOCALAPPDATA%\Programs`, with no admin prompt. A
  dialog offers "install for all users".
- The bundled `devices.json` is replaced on upgrade. User definitions in
  `%APPDATA%\OpenMouseConfig\devices\` are never touched, and uninstall keeps them too.
- To build without the script:
  `dotnet publish src/OpenMouseConfig -c Release -r win-x64 --self-contained -o artifacts/publish/win-x64`,
  then `ISCC.exe /DAppVersion=0.1.0 installer\OpenMouseConfig.iss`.

## Using the app

- **Header:** the device dropdown (only mice matched by a definition appear), a rescan button,
  a DRY RUN badge, the connection state and the battery level.
- **DPI card:** one row per stage. Drag the slider, or type a value and press Enter. Click a
  numbered badge to make that stage active.
- **Performance card:** polling rate, debounce, lift-off distance and Motion Sync. Each change
  is sent immediately (sliders wait until you stop dragging). If the definition has a
  `commitCommand`, it is sent after every change.
- **Device card:** the USB identity, the matched HID collection, the report sizes and the
  definition file. **Read from mouse** re-runs the `onConnect` reads. **Write all settings**
  pushes every value shown.
- **Developer console** (bottom of the window):
  - **Dry run:** log packets instead of sending them. This is forced on for unverified
    definitions, and turning it off for them asks for confirmation.
  - **Show offline definitions:** lists every definition even without the mouse attached, so
    you can test the UI and check payloads.
  - **List HID interfaces:** prints every HID collection with VID, PID, usage page, usage,
    interface and report lengths.
  - **Raw sender:** type hex (byte 0 = report ID), choose Feature or Output, then send. With
    **Read reply** on, the device's answer is logged too. Use it to replay captured packets
    byte for byte.
- **Status bar:** a summary of the loaded definitions, **Open definitions folder** and
  **Reload definitions** (no restart needed).

Definition files are loaded in this order (later files win when two use the same `id`):

1. `<install dir>\devices.json` (bundled)
2. `%APPDATA%\OpenMouseConfig\devices\*.json`, in alphabetical order (yours)

## How to add (or fix) a mouse

The workflow: find the USB identity, record what the official configurator sends, work out which
bytes change, describe that in JSON, then check it in dry-run before sending real packets.

### Step 1: Find VID, PID and the config collection

In OpenMouseConfig, open the **Developer console** and click **List HID interfaces**. A mouse
usually shows several lines with the same VID/PID:

```
VID 0x1234 PID 0x5678 UsagePage 0x0001 Usage 0x0002 MI 00 | In 9 Out 0 Feat 0  | ...   ← mouse input (owned by Windows)
VID 0x1234 PID 0x5678 UsagePage 0x000C Usage 0x0001 MI 01 | In 3 Out 0 Feat 0  | ...   ← consumer keys
VID 0x1234 PID 0x5678 UsagePage 0xFF00 Usage 0x0001 MI 02 | In 0 Out 0 Feat 65 | ...   ← vendor config ✔
```

You want the line with a **vendor-defined usage page** (`0xFF00` to `0xFFFF`) and a non-zero
`Feat` or `Out` length. Those four numbers (VID, PID, usage page, usage) go into `match`.

Wireless mice usually show a **different PID for the receiver and for the cable**. Add one
`match` entry for each.

Other places to find the IDs: Device Manager → the device → Details → *Hardware Ids*
(`HID\VID_1234&PID_5678&MI_02&Col01`), or the `filters` that the configurator website passes to
`navigator.hid.requestDevice()` (search its JavaScript for `vendorId`).

### Step 2a: Capture packets from the browser (easiest)

WebHID configurators run in Chrome or Edge, so you can log every report straight from the page.
Open mouse.xyz, press **F12** → **Console**, paste the snippet below **before** you click
*Connect* (reload the page first if it has already connected), and then change one setting at a
time:

```js
// Logs every WebHID report as a ready-to-paste devices.json payload (byte 0 = report ID).
(() => {
  const hex = (v) => [...new Uint8Array(v.buffer ?? v, v.byteOffset ?? 0, v.byteLength)]
    .map((b) => b.toString(16).padStart(2, '0').toUpperCase()).join(' ');
  const id = (n) => n.toString(16).padStart(2, '0').toUpperCase();

  for (const name of ['sendFeatureReport', 'sendReport']) {
    const original = HIDDevice.prototype[name];
    HIDDevice.prototype[name] = function (reportId, data) {
      console.log(`%c${name}`, 'color:#a78bfa', `"payload": "${id(reportId)} ${hex(data)}"`);
      return original.call(this, reportId, data);
    };
  }

  const receive = HIDDevice.prototype.receiveFeatureReport;
  HIDDevice.prototype.receiveFeatureReport = async function (reportId) {
    const view = await receive.call(this, reportId);
    console.log('%creceiveFeatureReport', 'color:#38bdf8', `id ${id(reportId)} →`, hex(view));
    return view;
  };

  const open = HIDDevice.prototype.open;
  HIDDevice.prototype.open = async function () {
    await open.call(this);
    console.log('opened', this.productName,
      `VID 0x${this.vendorId.toString(16)} PID 0x${this.productId.toString(16)}`,
      this.collections.map((c) => `usagePage 0x${c.usagePage.toString(16)} usage 0x${c.usage.toString(16)}`));
    this.addEventListener('inputreport', (e) =>
      console.log('%cinputreport', 'color:#38bdf8', `"${id(e.reportId)} ${hex(e.data)}"`));
  };
})();
```

The function name tells you the transport. `sendFeatureReport` means `"reportType": "feature"`,
and `sendReport` means `"output"`. If replies arrive via `inputreport`, use
`"responseType": "input"`. If the page calls `receiveFeatureReport`, use `"feature"`.

WebHID passes the report ID separately from the data. The snippet already puts it in front, so
its output matches the `payload` format (`"05 01 40 00 …"`). For `receiveFeatureReport`, check
whether the logged bytes already start with the report ID before you count byte indices.

### Step 2b: Capture with Wireshark and USBPcap (works with any software)

Use this when there is no website, or when you want to see exactly what goes over the wire.

1. Install **Wireshark** and tick **USBPcap** in its installer, then reboot.
2. Start Wireshark and pick the **USBPcap*N*** interface for the root hub your mouse (or its
   receiver) is on. If you're not sure, capture on each one in turn while you unplug and replug
   the mouse.
3. Find the device address. Filter on `usb.idVendor == 0x1234`, replug the mouse, and look at
   the *GET DESCRIPTOR* response. Its source column reads something like `1.7.0`, which means
   bus 1, device 7.
4. Show only configuration traffic for that device:

   ```
   usb.bus_id == 1 && usb.device_address == 7 && (usbhid.setup.bRequest == 0x09 || usbhid.setup.bRequest == 0x01 || (usb.transfer_type == 0x01 && usb.endpoint_address.direction == 0))
   ```

   | Packet | Meaning | devices.json |
   |---|---|---|
   | `SET_REPORT` (bRequest `0x09`), wValue `0x03xx` | feature report, ID `xx` | `"reportType": "feature"` |
   | `SET_REPORT` (bRequest `0x09`), wValue `0x02xx` | output report over the control pipe | `"reportType": "output"` |
   | `URB_INTERRUPT out` | output report over the interrupt endpoint | `"reportType": "output"` |
   | `GET_REPORT` (bRequest `0x01`), wValue `0x03xx` | feature read; the reply is in the completion packet | `"responseType": "feature"` |
   | `URB_INTERRUPT in` right after a command | input report reply | `"responseType": "input"` |

5. Change **one** setting in the vendor tool and find the new packet. The bytes are under
   **Data Fragment** (`usb.data_fragment`) for control transfers, and **HID Data** or
   **Leftover Capture Data** (`usbhid.data` / `usb.capdata`) for interrupt transfers.
   Right-click the field → *Copy* → *…as Hex Stream* and paste it straight into `payload`.

**Report ID rule:** if the device uses report IDs (wValue low byte ≠ 0), the data already starts
with the ID. If it doesn't (`0x00`), add `00` in front of what Wireshark shows, because the
payload's byte 0 is always the report ID.

**Avoid noise:** wireless dongles send movement reports constantly. Don't touch the mouse
during a capture; use a second mouse to click through the configurator.

### Step 3: Work out what the bytes mean

Change one value at a time and compare the packets:

| Action in vendor tool | Captured payload (trailing zeros trimmed) |
|---|---|
| Polling 1000 Hz | `05 01 01` |
| Polling 2000 Hz | `05 01 10` |
| Polling 8000 Hz | `05 01 40` |
| DPI stage 1 = 400 | `05 02 00 08 00 08 00` |
| DPI stage 1 = 1600 | `05 02 00 20 00 20 00` |
| DPI stage 3 = 1600 | `05 02 02 20 00 20 00` |

From this table:

- Byte 0 is the report ID (`05`) and byte 1 is a command ID.
- Polling rate is byte 2. The values follow no formula, so it needs a `map`:
  `{ "1000": "0x01", "2000": "0x10", "8000": "0x40" }`.
- DPI 400 → `0x08` and 1600 → `0x20`, so raw = DPI ÷ 50. It is a 16-bit little-endian value at
  bytes 3-4 (X) and again at 5-6 (Y): `"size": 2, "divide": 50`.
- The stage number is byte 2 and counts from 0, while the app counts stages from 1:
  `"name": "stage", "add": -1`.
- If the last byte changes with every packet even though nothing else explains it, it is
  probably a checksum (see [`checksum`](#checksum)).

### Step 4: Write the definition

Put your file in `%APPDATA%\OpenMouseConfig\devices\my-mouse.json`. **Open definitions folder**
takes you there. Use the same format as `devices.json`, start with `"verified": false`, then
click **Reload definitions**. Load problems appear in the console with the exact JSON path, for
example `commands.setDpiStage.fields[1]: index 70 + size 2 exceeds transport.reportLength 64`.

### Step 5: Check in dry-run, then go live

1. With dry-run on, change each setting in OpenMouseConfig and compare the `TX` lines in the
   console with your capture. They must match byte for byte (zero padding is shown as `… (+N × 00)`).
2. Use the **raw sender** to replay one captured packet and confirm the mouse reacts. Turn on
   *Read reply* to see what it answers.
3. Turn dry-run off (you'll be asked to confirm), then try each control.
4. Once everything works, set `"verified": true`. The banner disappears and dry-run is no longer
   forced on.

To share a definition, send the JSON file. Other users drop it into their own `devices` folder,
or you can open a PR that adds it to the bundled `devices.json`.

## devices.json reference

The file is JSON with a few relaxations: `//` comments and trailing commas are allowed, and
integers may be numbers or hex strings (`"0x1A2B"`). Unknown keys produce a warning, so typos
don't fail silently. Keys starting with `_` or `$` (such as `"_comment"`) are ignored.

```jsonc
{
  "schemaVersion": 1,
  "devices": [ { /* device */ }, { /* device */ } ]
}
```

### Device

| Key | Type | Description |
|---|---|---|
| `id` | string | Unique, stable key. A user file with the same `id` replaces the bundled entry. |
| `name`, `manufacturer` | string | Shown in the UI. |
| `verified` | bool | `false` forces dry-run and shows a warning banner. |
| `notes` | string | Shown in the Device card. |
| `match` | array | USB identities, see [match](#match). |
| `transport` | object | How reports are sent and read, see [transport](#transport). |
| `capabilities` | object | Which controls exist and their ranges, see [capabilities](#capabilities). |
| `defaults` | object | Initial values: `pollingRate`, `dpiStages`, `activeStage` (1-based), `debounce`, `liftOffDistance`, `motionSync`. |
| `commands` | object | Named packet templates, see [commands](#commands). |
| `onConnect` | string[] | Commands run after connecting, usually reads. |
| `commitCommand` | string | Command sent after every successful change, e.g. save to onboard memory. |
| `batteryPollSeconds` | int | How often `getBattery` runs (default 60, minimum 5). |

### match

| Key | Description |
|---|---|
| `vendorId`, `productId` | Required. 16-bit values. |
| `usagePage`, `usageId` | The vendor config collection. **Strongly recommended**: without them, any collection of the device can match. |
| `interface` | USB interface number (the `MI_xx` part of the device path). Optional tie-breaker for composite devices. |
| `label` | Second line in the dropdown, e.g. "8K Wireless Receiver". |

### transport

| Key | Default | Description |
|---|---|---|
| `reportType` | `"feature"` | How commands are sent: `"feature"` (`HidD_SetFeature`, WebHID `sendFeatureReport`) or `"output"` (`WriteFile`, WebHID `sendReport`). |
| `responseType` | `"feature"` | How replies are read: `"feature"` (`HidD_GetFeature`) or `"input"` (next matching input report). |
| `reportLength` | — | Report size **including** the report-ID byte. Only used offline; a connected device's real size (HIDP_CAPS) always wins. |
| `responseTimeoutMs` | 1000 | How long to wait for a matching reply. |
| `responseDelayMs` | 0 | Pause between sending a command and its GET_FEATURE. |
| `responseRetries` | 3 | Extra GET_FEATURE attempts while the reply doesn't match `expect`. |
| `interCommandDelayMs` | 10 | Pause after every command. |
| `checksum` | none | See [checksum](#checksum). |

### capabilities

| Key | Example | Enables |
|---|---|---|
| `pollingRates` | `[125, 250, 500, 1000, 2000, 4000, 8000]` | Polling dropdown (needs `setPollingRate`) |
| `dpi` | `{ "min": 50, "max": 26000, "step": 50, "stages": 4 }` | DPI rows (needs `setDpiStage`; the active-stage badge needs `setActiveDpiStage`) |
| `debounce` | `{ "min": 0, "max": 20, "step": 1 }` | Debounce slider (needs `setDebounce`) |
| `liftOffDistances` | `[1, 2]` | LOD selector (needs `setLiftOffDistance`) |
| `motionSync` | `true` | Motion Sync switch (needs `setMotionSync`) |
| `battery` | `true` | Battery indicator (needs `getBattery` with a response) |

### commands

Well-known names that the UI calls:

| Command | Sent when | Extra variables |
|---|---|---|
| `setPollingRate` | polling rate changes | `value` = Hz |
| `setDpiStage` | a stage's DPI changes | `value` = `dpi` = DPI, `stage` = 1-based stage |
| `setActiveDpiStage` | a stage badge is clicked | `value` = `stage` = active stage, `dpi` = its DPI |
| `setDebounce` | debounce changes | `value` = ms |
| `setLiftOffDistance` | LOD changes | `value` = mm |
| `setMotionSync` | Motion Sync toggles | `value` = 0 / 1 |
| `getBattery` | on connect and every `batteryPollSeconds` | — |

Any other name works too and can be used from `onConnect` or `commitCommand`.

| Key | Description |
|---|---|
| `payload` | Hex template. **Byte 0 = report ID.** Zero-padded to the report length automatically. Accepts `"05 01 00"`, `"050100"` or `"0x05, 0x01"`. |
| `fields` | Values to write into the template, see [fields](#fields). |
| `reportType` | Overrides `transport.reportType` for this command. |
| `response` | Reply handling, see [response](#response). |
| `delayAfterMs` | Overrides `transport.interCommandDelayMs`. |
| `forEachStage` | When listed in `onConnect`, runs once per DPI stage with `stage` = 1..N; a decoded `dpi` is stored as `dpi1`…`dpiN`. |

#### Variables

Every command can read **all current settings**. A protocol that sends one "full config" packet
can therefore use a single command.

| Variable | Meaning |
|---|---|
| `pollingRate` | Hz |
| `dpi1` … `dpiN` | DPI of each stage |
| `activeStage` | 1-based |
| `stageCount` | number of stages |
| `debounce` | ms |
| `liftOffDistance` | mm |
| `motionSync` | 0 / 1 |
| `value`, `stage`, `dpi` | command-specific (see the table above) |
| `battery`, `charging` | response-only: percentage and a 0/1 charging flag |

### fields

```
raw = map[value]                                        if "map" is given
raw = round(value × multiply ÷ divide + add)            otherwise
```

The raw integer is written as `size` bytes at `index`. When a `mask` is given, only those bits
change (after `raw << shift`). Responses are decoded with the exact inverse.

| Key | Default | Description |
|---|---|---|
| `name` | — | Variable to write, or the name to store a decoded value under. |
| `index` | — | Byte offset (0 = report ID). |
| `size` | 1 | 1–4 bytes. |
| `endian` | `"little"` | `"little"` or `"big"` for multi-byte values. |
| `multiply`, `divide`, `add` | 1, 1, 0 | Linear transform, e.g. DPI/50 → `"divide": 50`; 0-based stages → `"add": -1`. |
| `map` | — | Lookup table: `{ "1000": "0x01", "8000": "0x40" }`. Booleans can use `"on"`/`"off"` keys. |
| `mask`, `shift` | all bits, 0 | Bit fields, e.g. bit 3 of a flags byte → `"mask": "0x08", "shift": 3`. |

Values that don't fit are rejected with an error rather than silently truncated.

### response

| Key | Description |
|---|---|
| `type` | `"feature"` or `"input"`; overrides `transport.responseType`. |
| `reportId` | Report ID for GET_FEATURE. Defaults to the command's byte 0. |
| `expect` | Hex prefix the reply must start with, with `??` wildcards, e.g. `"05 90 ??"`. Other reports are skipped. |
| `fields` | Values to decode (same keys as above). Names matching settings (`pollingRate`, `dpi2`, `motionSync`, …) update the UI; `battery` and `charging` update the header. |
| `timeoutMs` | Overrides `transport.responseTimeoutMs`. |

### checksum

Computed over the final, padded report after all fields have been written.

| Key | Default | Description |
|---|---|---|
| `type` | `"none"` | `"sum8"` = (initial + Σ) & 0xFF · `"xor8"` = initial ⊕ every byte · `"sumComplement8"` = (initial − Σ) & 0xFF |
| `index` | -1 | Where the checksum byte goes; negative counts from the end. |
| `start` | 0 | First byte included (use 1 to skip the report ID). |
| `end` | `index − 1` | Last byte included (inclusive); negative counts from the end. |
| `initial` | 0 | Seed value. `"sumComplement8"` with `"initial": "0x55"` gives the common `0x55 − Σ` scheme. |

### A second example: output reports, input replies and a checksum

```jsonc
{
  "id": "example-output-mouse",
  "name": "Example Output-Report Mouse",
  "verified": false,
  "match": [ { "label": "Wired", "vendorId": "0xABCD", "productId": "0x0001", "usagePage": "0xFF01", "usageId": "0x0001" } ],
  "transport": {
    "reportType": "output",            // WriteFile / sendReport
    "responseType": "input",           // replies arrive as input reports
    "reportLength": 17,
    "checksum": { "type": "sumComplement8", "initial": "0x55", "index": 16, "start": 0, "end": 15 }
  },
  "capabilities": {
    "pollingRates": [1000, 2000, 4000],
    "dpi": { "min": 100, "max": 16000, "step": 100, "stages": 4 },
    "battery": true
  },
  "commands": {
    "setPollingRate": {
      "payload": "08 07 00 00 00 01",
      "fields": [ { "name": "value", "index": 6, "map": { "1000": 1, "2000": 2, "4000": 3 } } ]
    },
    "setDpiStage": {
      "payload": "08 07 00 00 0C 04",
      "fields": [
        { "name": "stage", "index": 3, "multiply": 4, "add": -4 },                 // stage n lives at offset (n−1)×4
        { "name": "dpi",   "index": 6, "size": 2, "endian": "big", "divide": 100 }
      ]
    },
    "getBattery": {
      "payload": "08 04",
      "response": { "expect": "08 04", "timeoutMs": 500, "fields": [ { "name": "battery", "index": 6 }, { "name": "charging", "index": 7 } ] }
    }
  }
}
```

## Troubleshooting

| Symptom | Things to check |
|---|---|
| Mouse not in the dropdown | Look in the console for definition errors. Click **List HID interfaces** and compare VID, PID and usage page with your `match`. The receiver and the cable often have different PIDs. |
| "Could not open HID device" | Close the vendor software, which may hold the device exclusively. Browser tabs using WebHID don't. |
| "…has no Feature reports" | `usagePage`/`usageId` select the wrong collection, or the device uses output reports (`"reportType": "output"`). |
| "No matching response" | Check `expect`, `responseType`, `responseDelayMs` and `responseRetries`. Compare the RX lines in the console with your capture. |
| Settings lost after replugging | The device probably needs a "save to flash" packet. Capture it and set `commitCommand`. |
| Controls greyed out | The capability is declared but the command is missing (the console warns about this when loading). |
| Everything says "dry-run" | The definition is `"verified": false`, or you selected an offline definition. |

---

**Disclaimer:** This is an independent project, not affiliated with or endorsed by G-Wolves or
mouse.xyz. Sending incorrect packets can misconfigure a device. Use the dry-run workflow above
and proceed at your own risk.
