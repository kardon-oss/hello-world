using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMouseConfig.Core.Hid;
using OpenMouseConfig.Core.Protocol;

namespace OpenMouseConfig.Core.Definitions;

// =====================================================================================
//  C# mirror of the devices.json format.
//
//  Every public property maps 1:1 to a camelCase JSON key (property names are matched
//  case-insensitively). Integers may be written as JSON numbers or as strings in decimal
//  or hex ("0x1A2B") — see FlexibleIntConverter. // comments and trailing commas are allowed.
//
//  README.md ("devices.json reference") documents the same structure for end users.
// =====================================================================================

/// <summary>Captures unknown JSON keys so typos (e.g. "pyload") can be reported instead of silently ignored.</summary>
public abstract class DefinitionNode
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownProperties { get; set; }
}

/// <summary>Root object of a devices.json file.</summary>
public sealed class DefinitionFile : DefinitionNode
{
    /// <summary>Format version, currently 1. Reserved for future migrations.</summary>
    public int SchemaVersion { get; set; } = 1;

    public List<DeviceDefinition> Devices { get; set; } = [];
}

/// <summary>One supported mouse (one physical product, possibly reachable via several VID/PIDs).</summary>
public sealed class DeviceDefinition : DefinitionNode
{
    /// <summary>Stable unique key, e.g. "gwolves-hts-plus-pro-8k". A user file with the same id overrides the bundled one.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name shown in the device selector.</summary>
    public string Name { get; set; } = "";

    public string? Manufacturer { get; set; }

    /// <summary>
    /// False while the payloads are placeholders / unconfirmed. The app forces dry-run mode for
    /// unverified devices and asks for confirmation before sending anything to them.
    /// </summary>
    public bool Verified { get; set; }

    /// <summary>Free text shown in the device info card (firmware notes, where the packets came from, ...).</summary>
    public string? Notes { get; set; }

    /// <summary>USB identities this mouse can appear as (wired, 8K dongle, 4K dongle, ...).</summary>
    public List<DeviceMatch> Match { get; set; } = [];

    public TransportDefinition Transport { get; set; } = new();

    public CapabilityDefinition Capabilities { get; set; } = new();

    /// <summary>Values the UI starts with before anything is read back from the mouse.</summary>
    public DefaultSettingsDefinition Defaults { get; set; } = new();

    /// <summary>
    /// Named packet templates. Well-known names (see <see cref="CommandNames"/>) are bound to UI
    /// controls; any other name can be referenced from <see cref="OnConnect"/> or <see cref="CommitCommand"/>.
    /// </summary>
    public Dictionary<string, CommandDefinition> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Commands executed right after connecting, typically reads such as "getSettings" and "getBattery".</summary>
    public List<string> OnConnect { get; set; } = [];

    /// <summary>Optional command sent after every successful setting change (e.g. "save to onboard memory").</summary>
    public string? CommitCommand { get; set; }

    /// <summary>How often "getBattery" is polled while connected.</summary>
    public int BatteryPollSeconds { get; set; } = 60;

    /// <summary>File this definition was loaded from (set by the loader, not read from JSON).</summary>
    [JsonIgnore]
    public string SourceFile { get; internal set; } = "";

    public bool HasCommand(string name) => TryGetCommand(name, out _);

    /// <summary>
    /// Variable names a command writes into its packet, plus those of <see cref="CommitCommand"/>
    /// (which follows every write). Used to know which settings a successful write actually applied.
    /// </summary>
    public IReadOnlyList<string> VariablesWrittenBy(string commandName)
    {
        var names = new List<string>();
        if (TryGetCommand(commandName, out var command))
        {
            names.AddRange(command.Fields.Select(f => f.Name));
        }

        if (CommitCommand is { Length: > 0 } commit && TryGetCommand(commit, out var commitCommand))
        {
            names.AddRange(commitCommand.Fields.Select(f => f.Name));
        }

        return names;
    }

    public bool TryGetCommand(string name, [NotNullWhen(true)] out CommandDefinition? command)
    {
        if (Commands.TryGetValue(name, out command))
        {
            return true;
        }

        // Fallback in case the dictionary was replaced with a case-sensitive one.
        foreach (var (key, value) in Commands)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                command = value;
                return true;
            }
        }

        command = null;
        return false;
    }

    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>
/// One VID/PID (+ optional HID usage) the device can be found under.
/// On Windows every HID top-level collection is a separate device interface, so
/// <see cref="UsagePage"/>/<see cref="UsageId"/> select the vendor-specific config collection —
/// exactly like the <c>filters</c> passed to WebHID's <c>navigator.hid.requestDevice()</c>.
/// </summary>
public sealed class DeviceMatch : DefinitionNode
{
    /// <summary>Shown under the device name, e.g. "8K Wireless Receiver" or "Wired".</summary>
    public string? Label { get; set; }

    public int VendorId { get; set; }

    public int ProductId { get; set; }

    /// <summary>HID usage page of the config collection, usually vendor defined (0xFF00-0xFFFF).</summary>
    public int? UsagePage { get; set; }

    /// <summary>HID usage id inside <see cref="UsagePage"/>.</summary>
    public int? UsageId { get; set; }

    /// <summary>USB interface number (the <c>&amp;MI_xx</c> part of the Windows device path), for composite devices.</summary>
    public int? Interface { get; set; }

    public bool Matches(HidDeviceInfo info) =>
        info.VendorId == VendorId
        && info.ProductId == ProductId
        && (UsagePage is null || info.UsagePage == UsagePage)
        && (UsageId is null || info.Usage == UsageId)
        && (Interface is null || info.InterfaceNumber == Interface);

    public override string ToString()
    {
        var text = $"VID 0x{VendorId:X4} PID 0x{ProductId:X4}";
        if (UsagePage is not null)
        {
            text += $" · Usage 0x{UsagePage:X4}:0x{UsageId ?? 0:X4}";
        }

        return text;
    }
}

/// <summary>How reports travel over USB for this device.</summary>
public sealed class TransportDefinition : DefinitionNode
{
    /// <summary>
    /// Report type used to SEND commands: "feature" (HidD_SetFeature / WebHID sendFeatureReport,
    /// shows up in Wireshark as a SET_REPORT control transfer) or "output" (WriteFile / WebHID
    /// sendReport, usually an interrupt OUT transfer). Commands can override it.
    /// </summary>
    public ReportKind ReportType { get; set; } = ReportKind.Feature;

    /// <summary>
    /// How replies are READ for commands that declare a "response": "feature" (HidD_GetFeature /
    /// receiveFeatureReport, GET_REPORT in Wireshark) or "input" (ReadFile / WebHID inputreport event).
    /// </summary>
    public ResponseKind ResponseType { get; set; } = ResponseKind.Feature;

    /// <summary>
    /// Total report size in bytes INCLUDING the report-ID byte. Only used as a fallback when the
    /// real size can't be taken from the device (dry-run without a connected mouse). When a device
    /// is connected the size reported by Windows (HIDP_CAPS) always wins.
    /// </summary>
    public int? ReportLength { get; set; }

    /// <summary>How long to wait for a matching response.</summary>
    public int ResponseTimeoutMs { get; set; } = 1000;

    /// <summary>Delay between sending a command and reading its feature-report response.</summary>
    public int ResponseDelayMs { get; set; } = 0;

    /// <summary>Extra GET_FEATURE attempts when the first answer doesn't match "expect".</summary>
    public int ResponseRetries { get; set; } = 3;

    /// <summary>Pause after every command; some MCUs drop packets that arrive back-to-back.</summary>
    public int InterCommandDelayMs { get; set; } = 10;

    /// <summary>Optional checksum appended to every outgoing command.</summary>
    public ChecksumDefinition? Checksum { get; set; }
}

/// <summary>What the mouse supports. Drives which UI controls are shown/enabled and their ranges.</summary>
public sealed class CapabilityDefinition : DefinitionNode
{
    /// <summary>Selectable polling rates in Hz, e.g. [125, 250, 500, 1000, 2000, 4000, 8000].</summary>
    public List<int> PollingRates { get; set; } = [];

    public DpiCapability? Dpi { get; set; }

    /// <summary>Button debounce range in milliseconds.</summary>
    public RangeCapability? Debounce { get; set; }

    /// <summary>Selectable lift-off distances in millimetres, e.g. [1, 2] or [0.7, 1, 2].</summary>
    public List<double> LiftOffDistances { get; set; } = [];

    public bool MotionSync { get; set; }

    public bool Battery { get; set; }
}

public sealed class DpiCapability : DefinitionNode
{
    public int Min { get; set; } = 50;

    public int Max { get; set; } = 26000;

    public int Step { get; set; } = 50;

    /// <summary>Number of DPI stages (cycle slots on the DPI button).</summary>
    public int Stages { get; set; } = 4;

    public int Snap(int dpi)
    {
        var step = Math.Max(1, Step);
        var snapped = Min + (int)Math.Round((dpi - Min) / (double)step, MidpointRounding.AwayFromZero) * step;
        return Math.Clamp(snapped, Min, Max);
    }
}

public sealed class RangeCapability : DefinitionNode
{
    public int Min { get; set; }

    public int Max { get; set; } = 20;

    public int Step { get; set; } = 1;

    public int Clamp(int value) => Math.Clamp(value, Min, Math.Max(Min, Max));
}

/// <summary>Initial UI values (before a read-back from the mouse, if any, replaces them).</summary>
public sealed class DefaultSettingsDefinition : DefinitionNode
{
    public int? PollingRate { get; set; }

    public List<int>? DpiStages { get; set; }

    /// <summary>1-based.</summary>
    public int ActiveStage { get; set; } = 1;

    public int? Debounce { get; set; }

    public double? LiftOffDistance { get; set; }

    public bool MotionSync { get; set; }
}

/// <summary>A packet template plus the rules for injecting UI values into it.</summary>
public sealed class CommandDefinition : DefinitionNode
{
    private byte[]? _template;
    private string? _templateSource;

    public string? Description { get; set; }

    /// <summary>
    /// The report as hex, BYTE 0 = REPORT ID (exactly how Wireshark shows the data stage of a
    /// SET_REPORT). Bytes that carry a setting can be left as 00 — <see cref="Fields"/> overwrites them.
    /// Shorter than the device's report length? It is zero-padded automatically.
    /// </summary>
    public string Payload { get; set; } = "";

    /// <summary>Where (byte index) and how (size, endianness, scaling, lookup table) values go into the payload.</summary>
    public List<FieldDefinition> Fields { get; set; } = [];

    /// <summary>Overrides <see cref="TransportDefinition.ReportType"/> for this command.</summary>
    public ReportKind? ReportType { get; set; }

    /// <summary>If set, a reply is read after sending and decoded into named values.</summary>
    public ResponseDefinition? Response { get; set; }

    /// <summary>Overrides <see cref="TransportDefinition.InterCommandDelayMs"/> after this command.</summary>
    public int? DelayAfterMs { get; set; }

    /// <summary>
    /// When listed in "onConnect": run once per DPI stage with variable <c>stage</c> = 1..N.
    /// A decoded <c>dpi</c> value is stored as <c>dpi1</c>, <c>dpi2</c>, ... automatically.
    /// </summary>
    public bool ForEachStage { get; set; }

    /// <summary>Parsed <see cref="Payload"/> (cached).</summary>
    public byte[] GetTemplate()
    {
        if (_template is null || !ReferenceEquals(_templateSource, Payload))
        {
            _template = HexFormat.Parse(Payload);
            _templateSource = Payload;
        }

        return _template;
    }
}

/// <summary>
/// Places one value into (or reads one value out of) a report.
/// <para>
/// Encoding:  <c>raw = map[value]</c> if a map is given, otherwise <c>raw = round(value × multiply ÷ divide + add)</c>.
/// The raw integer is then written as <see cref="Size"/> bytes at <see cref="Index"/>, optionally
/// shifted into a bit field described by <see cref="Mask"/>/<see cref="Shift"/>.
/// Decoding (responses) applies the exact inverse.
/// </para>
/// </summary>
public sealed class FieldDefinition : DefinitionNode
{
    private IReadOnlyList<MapEntry>? _mapEntries;
    private Dictionary<string, long>? _mapSource;

    /// <summary>
    /// Variable name. For commands: which value to write (see README "Variables"), e.g. "value", "dpi", "stage".
    /// For responses: the name the decoded value is stored under, e.g. "battery", "pollingRate", "dpi2".
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>Byte offset in the report (0 = report ID). Required: -1 means "not set" and fails validation.</summary>
    public int Index { get; set; } = -1;

    /// <summary>Number of bytes (1-4).</summary>
    public int Size { get; set; } = 1;

    public Endianness Endian { get; set; } = Endianness.Little;

    /// <summary>Bit mask (applied to the <see cref="Size"/>-byte integer) for packed flag bytes.</summary>
    public long? Mask { get; set; }

    /// <summary>Left shift applied to the raw value before masking, e.g. shift 4 + mask 0xF0 for the high nibble.</summary>
    public int Shift { get; set; }

    public double Multiply { get; set; } = 1;

    public double Divide { get; set; } = 1;

    public double Add { get; set; }

    /// <summary>
    /// Lookup table UI value → raw byte, e.g. { "125": "0x08", "1000": "0x01" } or { "off": 0, "on": 1 }.
    /// Takes precedence over multiply/divide/add.
    /// </summary>
    public Dictionary<string, long>? Map { get; set; }

    internal IReadOnlyList<MapEntry> GetMapEntries()
    {
        if (_mapEntries is null || !ReferenceEquals(_mapSource, Map))
        {
            _mapEntries = MapEntry.ParseAll(Map);
            _mapSource = Map;
        }

        return _mapEntries;
    }
}

/// <summary>One parsed entry of <see cref="FieldDefinition.Map"/>.</summary>
internal readonly record struct MapEntry(double Value, long Raw)
{
    public static IReadOnlyList<MapEntry> ParseAll(Dictionary<string, long>? map)
    {
        if (map is null || map.Count == 0)
        {
            return [];
        }

        var list = new List<MapEntry>(map.Count);
        foreach (var (key, raw) in map)
        {
            list.Add(new MapEntry(ParseKey(key), raw));
        }

        return list;
    }

    private static double ParseKey(string key)
    {
        switch (key.Trim().ToLowerInvariant())
        {
            case "true" or "on" or "yes" or "enabled":
                return 1;
            case "false" or "off" or "no" or "disabled":
                return 0;
        }

        if (double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        if (HexFormat.TryParseInteger(key, out var l))
        {
            return l;
        }

        throw new FormatException($"Map key '{key}' is not a number (or on/off/true/false).");
    }
}

/// <summary>Describes the reply to a command and how to decode it.</summary>
public sealed class ResponseDefinition : DefinitionNode
{
    private BytePattern? _pattern;
    private string? _patternSource;

    /// <summary>Overrides <see cref="TransportDefinition.ResponseType"/>.</summary>
    public ResponseKind? Type { get; set; }

    /// <summary>Report ID to request with GET_FEATURE. Defaults to byte 0 of the command's payload.</summary>
    public int? ReportId { get; set; }

    /// <summary>Prefix the reply must start with, with ?? wildcards, e.g. "05 90 ??". Non-matching reports are skipped.</summary>
    public string? Expect { get; set; }

    /// <summary>Values to decode from the reply.</summary>
    public List<FieldDefinition> Fields { get; set; } = [];

    public int? TimeoutMs { get; set; }

    public BytePattern GetPattern()
    {
        if (_pattern is null || !ReferenceEquals(_patternSource, Expect))
        {
            _pattern = BytePattern.Parse(Expect);
            _patternSource = Expect;
        }

        return _pattern;
    }
}

/// <summary>Checksum byte computed over part of every outgoing report (after all fields are injected).</summary>
public sealed class ChecksumDefinition : DefinitionNode
{
    public ChecksumType Type { get; set; } = ChecksumType.None;

    /// <summary>Where the checksum byte goes. Negative counts from the end: -1 = last byte of the report.</summary>
    public int Index { get; set; } = -1;

    /// <summary>First byte included in the calculation (0 = report ID; use 1 to skip it).</summary>
    public int Start { get; set; }

    /// <summary>Last byte included (inclusive). Negative counts from the end. Default: the byte before <see cref="Index"/>.</summary>
    public int? End { get; set; }

    /// <summary>Seed value. For "sumComplement8" with initial 0x55 you get the common (0x55 − Σ) scheme.</summary>
    public int Initial { get; set; }
}

public enum ReportKind
{
    Feature,
    Output,
}

public enum ResponseKind
{
    Feature,
    Input,
}

public enum Endianness
{
    Little,
    Big,
}

public enum ChecksumType
{
    /// <summary>No checksum.</summary>
    None,

    /// <summary>(initial + Σ bytes) &amp; 0xFF.</summary>
    Sum8,

    /// <summary>initial XOR every byte.</summary>
    Xor8,

    /// <summary>(initial − Σ bytes) &amp; 0xFF — two's complement when initial = 0.</summary>
    SumComplement8,
}
