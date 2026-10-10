namespace OpenMouseConfig.Core.Definitions;

/// <summary>
/// Command names the UI knows about. A device supports a feature when devices.json defines the
/// matching capability AND the command. Any other command name is allowed too and can be used
/// from "onConnect" or "commitCommand".
/// </summary>
public static class CommandNames
{
    public const string SetPollingRate = "setPollingRate";
    public const string SetDpiStage = "setDpiStage";
    public const string SetActiveDpiStage = "setActiveDpiStage";
    public const string SetDebounce = "setDebounce";
    public const string SetLiftOffDistance = "setLiftOffDistance";
    public const string SetMotionSync = "setMotionSync";
    public const string GetBattery = "getBattery";
}

/// <summary>
/// Variable names usable in a field's "name".
/// <para>
/// Every command receives ALL current settings (so one packet can carry several of them, e.g. a
/// "write whole config" command) plus a few command-specific values (<see cref="Value"/>,
/// <see cref="Stage"/>, <see cref="Dpi"/>).
/// </para>
/// </summary>
public static class VariableNames
{
    /// <summary>The value being changed by the current command (Hz, DPI, ms, mm, 0/1).</summary>
    public const string Value = "value";

    /// <summary>1-based DPI stage number for setDpiStage (use "add": -1 if the protocol is 0-based).</summary>
    public const string Stage = "stage";

    /// <summary>DPI of the stage being changed by setDpiStage.</summary>
    public const string Dpi = "dpi";

    public const string PollingRate = "pollingRate";

    /// <summary>1-based active stage.</summary>
    public const string ActiveStage = "activeStage";

    public const string StageCount = "stageCount";
    public const string Debounce = "debounce";
    public const string LiftOffDistance = "liftOffDistance";

    /// <summary>0 = off, 1 = on.</summary>
    public const string MotionSync = "motionSync";

    /// <summary>Battery percentage (responses only).</summary>
    public const string Battery = "battery";

    /// <summary>0/1 charging flag (responses only).</summary>
    public const string Charging = "charging";

    /// <summary>"dpi1", "dpi2", ... — DPI of every stage, available to every command.</summary>
    public static string DpiOfStage(int stage) => $"dpi{stage}";
}
