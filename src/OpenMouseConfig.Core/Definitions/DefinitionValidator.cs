using OpenMouseConfig.Core.Protocol;

namespace OpenMouseConfig.Core.Definitions;

/// <summary>
/// Sanity-checks a freshly deserialized <see cref="DeviceDefinition"/> and normalises it
/// (null collections → empty, case-insensitive command lookup).
/// Errors make the loader skip the device; warnings are shown but the device still loads.
/// The goal is to turn "my mouse doesn't work" into a precise message such as
/// "commands.setDpiStage.fields[1]: index 70 + size 2 exceeds reportLength 64".
/// </summary>
public static class DefinitionValidator
{
    public static void Validate(DeviceDefinition d, List<string> errors, List<string> warnings)
    {
        Normalize(d);
        WarnUnknown(d, "device", warnings);

        if (d.Id.Length == 0)
        {
            errors.Add("Missing \"id\".");
        }

        if (string.IsNullOrWhiteSpace(d.Name))
        {
            warnings.Add("Missing \"name\"; the id is shown instead.");
            d.Name = d.Id;
        }

        ValidateMatches(d, errors, warnings);
        ValidateTransport(d, errors, warnings);
        ValidateCapabilities(d, errors, warnings);

        try
        {
            d.Commands = new Dictionary<string, CommandDefinition>(d.Commands, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            errors.Add("Two commands have names that differ only in letter case.");
        }

        foreach (var (name, command) in d.Commands)
        {
            if (command is null)
            {
                errors.Add($"commands.{name} is null.");
                continue;
            }

            ValidateCommand(d, $"commands.{name}", command, errors, warnings);
        }

        foreach (var name in d.OnConnect)
        {
            if (name is null)
            {
                errors.Add("onConnect contains null.");
            }
            else if (!d.HasCommand(name))
            {
                errors.Add($"onConnect references unknown command \"{name}\".");
            }
        }

        if (d.CommitCommand is { Length: > 0 } commit && !d.HasCommand(commit))
        {
            errors.Add($"commitCommand references unknown command \"{commit}\".");
        }

        WarnMissingCommand(d, d.Capabilities.PollingRates.Count > 0, CommandNames.SetPollingRate, "polling rate", warnings);
        WarnMissingCommand(d, d.Capabilities.Dpi is not null, CommandNames.SetDpiStage, "DPI", warnings);
        WarnMissingCommand(d, d.Capabilities.Debounce is not null, CommandNames.SetDebounce, "debounce", warnings);
        WarnMissingCommand(d, d.Capabilities.LiftOffDistances.Count > 0, CommandNames.SetLiftOffDistance, "lift-off distance", warnings);
        WarnMissingCommand(d, d.Capabilities.MotionSync, CommandNames.SetMotionSync, "motion sync", warnings);
        WarnMissingCommand(d, d.Capabilities.Battery, CommandNames.GetBattery, "battery", warnings);

        if (d.BatteryPollSeconds < 5)
        {
            warnings.Add("batteryPollSeconds below 5 is clamped to 5.");
            d.BatteryPollSeconds = 5;
        }
    }

    // JSON "null" (or missing objects) would otherwise surface as NullReferenceExceptions later.
    private static void Normalize(DeviceDefinition d)
    {
        d.Id = d.Id?.Trim() ?? "";
        d.Match ??= [];
        d.Transport ??= new TransportDefinition();
        d.Capabilities ??= new CapabilityDefinition();
        d.Capabilities.PollingRates ??= [];
        d.Capabilities.LiftOffDistances ??= [];
        d.Defaults ??= new DefaultSettingsDefinition();
        d.Commands ??= new Dictionary<string, CommandDefinition>(StringComparer.OrdinalIgnoreCase);
        d.OnConnect ??= [];

        foreach (var command in d.Commands.Values)
        {
            if (command is null)
            {
                continue;
            }

            command.Fields ??= [];
            if (command.Response is not null)
            {
                command.Response.Fields ??= [];
            }
        }
    }

    private static void ValidateMatches(DeviceDefinition d, List<string> errors, List<string> warnings)
    {
        if (d.Match.Count == 0)
        {
            errors.Add("\"match\" needs at least one { vendorId, productId } entry.");
        }

        for (int i = 0; i < d.Match.Count; i++)
        {
            var m = d.Match[i];
            var where = $"match[{i}]";
            if (m is null)
            {
                errors.Add($"{where} is null.");
                continue;
            }

            WarnUnknown(m, where, warnings);

            if (m.VendorId is < 0 or > 0xFFFF)
            {
                errors.Add($"{where}.vendorId 0x{m.VendorId:X} is not a 16-bit value.");
            }

            if (m.ProductId is < 0 or > 0xFFFF)
            {
                errors.Add($"{where}.productId 0x{m.ProductId:X} is not a 16-bit value.");
            }

            if (m.VendorId == 0 || m.ProductId == 0)
            {
                warnings.Add($"{where}: vendorId/productId is 0 — still a placeholder?");
            }

            if (m.UsagePage is null)
            {
                warnings.Add(
                    $"{where}: no usagePage. Mice expose several HID collections; without usagePage/usageId the first one found is used, which is often the wrong one.");
            }
        }
    }

    private static void ValidateTransport(DeviceDefinition d, List<string> errors, List<string> warnings)
    {
        var t = d.Transport;
        WarnUnknown(t, "transport", warnings);

        if (t.ReportLength is < 1 or > 4096)
        {
            errors.Add($"transport.reportLength {t.ReportLength} is out of range (1-4096).");
        }

        if (t.ResponseTimeoutMs < 1)
        {
            errors.Add("transport.responseTimeoutMs must be positive.");
        }

        if (t.ResponseRetries < 0 || t.ResponseDelayMs < 0 || t.InterCommandDelayMs < 0)
        {
            errors.Add("transport delays/retries cannot be negative.");
        }

        if (t.Checksum is { } c)
        {
            WarnUnknown(c, "transport.checksum", warnings);
            if (c.Type != ChecksumType.None)
            {
                // Without a reportLength only the length-independent rules (start <= end, checksum
                // byte outside its own range) can be checked; a nominal length makes bounds pass.
                int? checkLength = t.ReportLength
                    ?? (c.Index >= 0 && c.Start >= 0 && c.End is null or >= 0 ? Math.Max(c.Index, c.End ?? 0) + 1 : null);
                if (checkLength is { } len)
                {
                    try
                    {
                        Checksum.ResolveRange(c, len);
                    }
                    catch (PayloadException ex)
                    {
                        errors.Add($"transport.checksum: {ex.Message}");
                    }
                }

                if (t.ReportLength is null)
                {
                    warnings.Add("transport.checksum is set but transport.reportLength is not: offline dry-run can't place the checksum. Set reportLength.");
                }
            }
        }
    }

    private static void ValidateCapabilities(DeviceDefinition d, List<string> errors, List<string> warnings)
    {
        var caps = d.Capabilities;
        WarnUnknown(caps, "capabilities", warnings);
        WarnUnknown(d.Defaults, "defaults", warnings);

        if (caps.PollingRates.Any(r => r <= 0))
        {
            errors.Add("capabilities.pollingRates must be positive numbers (Hz).");
        }

        if (caps.Dpi is { } dpi)
        {
            WarnUnknown(dpi, "capabilities.dpi", warnings);
            if (dpi.Min <= 0 || dpi.Max < dpi.Min || dpi.Step <= 0)
            {
                errors.Add($"capabilities.dpi: invalid range min {dpi.Min}, max {dpi.Max}, step {dpi.Step}.");
            }

            if (dpi.Stages is < 1 or > 8)
            {
                errors.Add($"capabilities.dpi.stages {dpi.Stages} must be between 1 and 8.");
            }
        }

        if (caps.Debounce is { } deb)
        {
            WarnUnknown(deb, "capabilities.debounce", warnings);
            if (deb.Min < 0 || deb.Max < deb.Min || deb.Step <= 0)
            {
                errors.Add($"capabilities.debounce: invalid range min {deb.Min}, max {deb.Max}, step {deb.Step}.");
            }
        }

        if (caps.LiftOffDistances.Any(l => l <= 0))
        {
            errors.Add("capabilities.liftOffDistances must be positive (millimetres).");
        }
    }

    private static void ValidateCommand(DeviceDefinition d, string where, CommandDefinition command, List<string> errors, List<string> warnings)
    {
        WarnUnknown(command, where, warnings);

        byte[] template;
        try
        {
            template = command.GetTemplate();
        }
        catch (FormatException ex)
        {
            errors.Add($"{where}.payload: {ex.Message}");
            return;
        }

        if (template.Length == 0)
        {
            errors.Add($"{where}.payload is empty. It must at least contain the report ID byte.");
            return;
        }

        var reportLength = d.Transport.ReportLength;
        if (reportLength is { } len && template.Length > len)
        {
            errors.Add($"{where}.payload is {template.Length} bytes but transport.reportLength is {len}.");
        }

        for (int i = 0; i < command.Fields.Count; i++)
        {
            var field = command.Fields[i];
            if (field is null)
            {
                errors.Add($"{where}.fields[{i}] is null.");
                continue;
            }

            ValidateField($"{where}.fields[{i}]", field, reportLength, errors, warnings);
            if (reportLength is null && field.Index >= 0 && field.Index + field.Size > template.Length)
            {
                warnings.Add($"{where}.fields[{i}] lies past the end of the payload; set transport.reportLength so offline dry-run can build this command.");
            }
        }

        if (command.Response is { } response)
        {
            WarnUnknown(response, $"{where}.response", warnings);
            try
            {
                response.GetPattern();
            }
            catch (FormatException ex)
            {
                errors.Add($"{where}.response.expect: {ex.Message}");
            }

            if (response.ReportId is < 0 or > 255)
            {
                errors.Add($"{where}.response.reportId must be a byte.");
            }

            if (response.TimeoutMs is < 1)
            {
                errors.Add($"{where}.response.timeoutMs must be positive.");
            }

            // transport.reportLength describes the report commands are SENT with. A feature reply has that
            // size only when commands are feature reports too (output and feature reports can differ).
            bool sendsFeature = (command.ReportType ?? d.Transport.ReportType) == ReportKind.Feature;
            bool readsFeature = (response.Type ?? d.Transport.ResponseType) == ResponseKind.Feature;
            var responseLength = sendsFeature && readsFeature ? reportLength : null;
            for (int i = 0; i < response.Fields.Count; i++)
            {
                if (response.Fields[i] is null)
                {
                    errors.Add($"{where}.response.fields[{i}] is null.");
                    continue;
                }

                ValidateField($"{where}.response.fields[{i}]", response.Fields[i], responseLength, errors, warnings);
            }
        }
    }

    private static void ValidateField(string where, FieldDefinition f, int? reportLength, List<string> errors, List<string> warnings)
    {
        WarnUnknown(f, where, warnings);

        if (string.IsNullOrWhiteSpace(f.Name))
        {
            errors.Add($"{where}: missing \"name\" (which value goes here, e.g. \"value\" or \"dpi\").");
        }

        if (f.Index < 0)
        {
            errors.Add($"{where}: \"index\" is missing or negative (byte 0 is the report ID).");
        }

        if (f.Size is < 1 or > 4)
        {
            errors.Add($"{where}.size must be 1-4 bytes.");
        }
        else if (reportLength is { } len && f.Index + f.Size > len)
        {
            errors.Add($"{where}: index {f.Index} + size {f.Size} exceeds transport.reportLength {len}.");
        }

        if (f.Divide == 0 || f.Multiply == 0)
        {
            errors.Add($"{where}: multiply/divide cannot be 0.");
        }

        if (f.Shift is < 0 or > 31)
        {
            errors.Add($"{where}.shift must be 0-31.");
        }

        if (f.Mask is { } mask && f.Size is >= 1 and <= 4)
        {
            long max = FieldCodec.MaxRaw(f.Size);
            if (mask <= 0 || mask > max)
            {
                errors.Add($"{where}.mask 0x{mask:X} doesn't fit in {f.Size} byte(s).");
            }
        }

        try
        {
            f.GetMapEntries();
        }
        catch (FormatException ex)
        {
            errors.Add($"{where}.map: {ex.Message}");
        }
    }

    private static void WarnMissingCommand(DeviceDefinition d, bool capable, string command, string feature, List<string> warnings)
    {
        if (capable && !d.HasCommand(command))
        {
            warnings.Add($"Capability \"{feature}\" is declared but command \"{command}\" is missing; the control will be disabled.");
        }
    }

    private static void WarnUnknown(DefinitionNode? node, string where, List<string> warnings)
    {
        if (node?.UnknownProperties is null)
        {
            return;
        }

        foreach (var key in node.UnknownProperties.Keys)
        {
            // "$schema", "_comment", "_todo"... are deliberately ignored.
            if (key.StartsWith('$') || key.StartsWith('_'))
            {
                continue;
            }

            warnings.Add($"{where}: unknown property \"{key}\" (typo?) — ignored.");
        }
    }
}
