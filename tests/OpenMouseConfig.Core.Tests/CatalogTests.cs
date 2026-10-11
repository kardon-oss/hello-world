using OpenMouseConfig.Core.Definitions;
using OpenMouseConfig.Core.Devices;
using OpenMouseConfig.Core.Hid;

namespace OpenMouseConfig.Core.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("omc-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Shipped_devices_json_loads_without_errors()
    {
        var catalog = DeviceCatalog.Load([Path.Combine(AppContext.BaseDirectory, "devices.json")]);

        Assert.DoesNotContain(catalog.Messages, m => m.Severity == CatalogSeverity.Error);
        var hts = Assert.Single(catalog.Devices, d => d.Id == "gwolves-hts-plus-pro-8k");

        Assert.False(hts.Verified); // placeholders must keep the app in dry-run
        Assert.Equal([125, 250, 500, 1000, 2000, 4000, 8000], hts.Capabilities.PollingRates);
        Assert.Equal(26000, hts.Capabilities.Dpi!.Max);
        Assert.Equal(4, hts.Capabilities.Dpi.Stages);
        Assert.Equal(20, hts.Capabilities.Debounce!.Max);
        Assert.Equal([1d, 2d], hts.Capabilities.LiftOffDistances);
        Assert.Equal(0x1234, hts.Match[0].VendorId);
        Assert.Equal(0xFF00, hts.Match[0].UsagePage);

        var features = DeviceFeatures.From(hts);
        Assert.True(features.PollingRate && features.Dpi && features.ActiveStage && features.Debounce
            && features.LiftOffDistance && features.MotionSync && features.Battery && features.ReadBack);
    }

    [Fact]
    public void Every_shipped_command_builds_with_default_settings()
    {
        var catalog = DeviceCatalog.Load([Path.Combine(AppContext.BaseDirectory, "devices.json")]);
        foreach (var device in catalog.Devices)
        {
            var settings = MouseSettings.CreateDefault(device);
            var vars = settings.ToVariables();
            vars["value"] = 1;
            vars["stage"] = 1;
            vars["dpi"] = settings.DpiStages.FirstOrDefault(400);

            foreach (var (name, command) in device.Commands)
            {
                // "value" means something different per command; use a valid one for mapped fields.
                if (name == CommandNames.SetPollingRate)
                {
                    vars["value"] = settings.PollingRate;
                }
                else if (name == CommandNames.SetLiftOffDistance)
                {
                    vars["value"] = settings.LiftOffDistance;
                }
                else
                {
                    vars["value"] = 1;
                }

                var report = Protocol.PayloadBuilder.Build(command, device.Transport.Checksum, device.Transport.ReportLength ?? 0, vars);
                Assert.True(report.Length > 0, $"{device.Id}.{name}");
            }
        }
    }

    [Fact]
    public void Hex_strings_comments_and_trailing_commas_are_accepted()
    {
        var file = Write("a.json", """
            // leading comment
            {
              "devices": [
                {
                  "id": "x", "name": "X",
                  "match": [ { "vendorId": "0x1A2B", "productId": 42, "usagePage": "0xFF00", "usageId": "1" }, ],
                  "transport": { "reportType": "Output", "responseType": "input" },
                  "commands": { "setDebounce": { "payload": "05 04", "fields": [ { "name": "value", "index": "0x02", "mask": "0x1F" } ] } },
                },
              ],
            }
            """);

        var catalog = DeviceCatalog.Load([file]);

        Assert.Equal(0, catalog.ErrorCount);
        var device = Assert.Single(catalog.Devices);
        Assert.Equal(0x1A2B, device.Match[0].VendorId);
        Assert.Equal(42, device.Match[0].ProductId);
        Assert.Equal(1, device.Match[0].UsageId);
        Assert.Equal(ReportKind.Output, device.Transport.ReportType);
        Assert.Equal(ResponseKind.Input, device.Transport.ResponseType);
        Assert.Equal(0x1F, device.Commands["SETDEBOUNCE"].Fields[0].Mask); // command lookup is case-insensitive
    }

    [Fact]
    public void Unknown_properties_warn_and_bad_devices_are_skipped_without_affecting_others()
    {
        var file = Write("a.json", """
            {
              "devices": [
                { "id": "good", "name": "Good", "match": [ { "vendorId": 1, "productId": 2, "usagePage": 65280 } ],
                  "commands": { "c": { "pyload": "05", "payload": "05", "_comment": "ignored" } } },
                { "id": "bad", "name": "Bad", "match": [ { "vendorId": 1, "productId": 3, "usagePage": 65280 } ],
                  "commands": { "c": { "payload": "05 ZZ" } } }
              ]
            }
            """);

        var catalog = DeviceCatalog.Load([file]);

        Assert.Equal("good", Assert.Single(catalog.Devices).Id);
        Assert.Contains(catalog.Messages, m => m.Severity == CatalogSeverity.Warning && m.Text.Contains("pyload"));
        Assert.DoesNotContain(catalog.Messages, m => m.Text.Contains("_comment"));
        Assert.Contains(catalog.Messages, m => m.Severity == CatalogSeverity.Error && m.Source.Contains("bad"));
    }

    [Fact]
    public void Invalid_json_is_reported_with_its_location()
    {
        var broken = Write("broken.json", "{ \"devices\": [ { \"id\": } ] }");
        var catalog = DeviceCatalog.Load([broken]);

        Assert.Empty(catalog.Devices);
        var error = Assert.Single(catalog.Messages);
        Assert.Contains("LineNumber", error.Text);
    }

    [Fact]
    public void Later_files_override_by_id_and_take_matching_priority()
    {
        var bundled = Write("bundled.json", """
            { "devices": [
              { "id": "m", "name": "Bundled", "match": [ { "vendorId": 1, "productId": 1, "usagePage": 65280 } ] },
              { "id": "other", "name": "Other", "match": [ { "vendorId": 1, "productId": 1, "usagePage": 65280 } ] }
            ] }
            """);
        var user = Write("user.json", """
            { "devices": [ { "id": "M", "name": "User", "match": [ { "vendorId": 1, "productId": 1, "usagePage": 65280 } ] } ] }
            """);

        var catalog = DeviceCatalog.Load([bundled, user]);

        Assert.Equal(["User", "Other"], catalog.Devices.Select(d => d.Name));
        Assert.Contains(catalog.Messages, m => m.Severity == CatalogSeverity.Info && m.Text.Contains("Overrides"));

        var hid = new HidDeviceInfo { Path = @"\\?\hid#vid_0001&pid_0001&mi_02#x", VendorId = 1, ProductId = 1, UsagePage = 0xFF00, Usage = 1 };
        var detected = Assert.Single(DeviceMatcher.Match(catalog.Devices, [hid]));
        Assert.Equal("User", detected.Definition.Name);
        Assert.Equal(2, hid.InterfaceNumber);
    }

    [Fact]
    public void Matcher_respects_usage_page_and_interface()
    {
        var definition = new DeviceDefinition
        {
            Id = "d",
            Match = [new DeviceMatch { VendorId = 1, ProductId = 2, UsagePage = 0xFF00, UsageId = 1, Interface = 2 }],
        };
        HidDeviceInfo Hid(ushort page, string mi) => new() { Path = $@"\\?\hid#vid_0001&pid_0002&{mi}&col01#1", VendorId = 1, ProductId = 2, UsagePage = page, Usage = 1 };

        var result = DeviceMatcher.Match([definition], [Hid(0x0001, "mi_00"), Hid(0xFF00, "mi_01"), Hid(0xFF00, "mi_02")]);

        Assert.Equal(Hid(0xFF00, "mi_02").Path, Assert.Single(result).Hid!.Path);
    }

    private const string ValidMatch = """ "match": [ { "vendorId": 1, "productId": 1, "usagePage": 65280 } ] """;

    [Theory]
    [InlineData(""" "match": [ null ] """, "match[0] is null")]
    [InlineData(ValidMatch + """, "onConnect": [ null ] """, "onConnect contains null")]
    [InlineData(ValidMatch + """, "commands": { "c": { "payload": "05", "fields": [ null ] } } """, "fields[0] is null")]
    [InlineData(ValidMatch + """, "commands": { "c": { "payload": "05", "response": { "fields": [ null ] } } } """, "response.fields[0] is null")]
    public void Null_list_entries_skip_only_that_device(string badBody, string expectedError)
    {
        var file = Write("nulls.json", $$"""
            { "devices": [
              { "id": "bad", "name": "Bad", {{badBody}} },
              { "id": "good", "name": "Good", "match": [ { "vendorId": 1, "productId": 2, "usagePage": 65280 } ] }
            ] }
            """);

        var catalog = DeviceCatalog.Load([file]);

        Assert.Equal("good", Assert.Single(catalog.Devices).Id);
        Assert.Contains(catalog.Messages, m => m.Severity == CatalogSeverity.Error && m.Source.Contains("bad") && m.Text.Contains(expectedError));
    }

    [Fact]
    public void Validator_rejects_definitions_that_would_misbehave_at_runtime()
    {
        var file = Write("rules.json", """
            { "devices": [
              { "id": "no-index", "name": "x", "match": [ { "vendorId": 1, "productId": 1, "usagePage": 65280 } ],
                "commands": { "c": { "payload": "05 01", "fields": [ { "name": "value" } ] } } },
              { "id": "reply-oob", "name": "x", "match": [ { "vendorId": 1, "productId": 2, "usagePage": 65280 } ],
                "transport": { "reportLength": 8 },
                "commands": { "c": { "payload": "05 10", "response": { "fields": [ { "name": "battery", "index": 9 } ] } } } },
              { "id": "zero-timeout", "name": "x", "match": [ { "vendorId": 1, "productId": 3, "usagePage": 65280 } ],
                "commands": { "c": { "payload": "05 10", "response": { "timeoutMs": 0 } } } },
              { "id": "checksum-overlap", "name": "x", "match": [ { "vendorId": 1, "productId": 4, "usagePage": 65280 } ],
                "transport": { "checksum": { "type": "sum8", "index": 3, "start": 0, "end": 5 } } }
            ] }
            """);

        var catalog = DeviceCatalog.Load([file]);

        Assert.Empty(catalog.Devices);
        Assert.Contains(catalog.Messages, m => m.Source.Contains("no-index") && m.Text.Contains("\"index\" is missing"));
        Assert.Contains(catalog.Messages, m => m.Source.Contains("reply-oob") && m.Text.Contains("exceeds transport.reportLength"));
        Assert.Contains(catalog.Messages, m => m.Source.Contains("zero-timeout") && m.Text.Contains("timeoutMs must be positive"));
        Assert.Contains(catalog.Messages, m => m.Source.Contains("checksum-overlap") && m.Text.Contains("inside its own range"));
        Assert.Contains(catalog.Messages, m => m.Source.Contains("checksum-overlap") && m.Text.Contains("Set reportLength"));
    }

    [Fact]
    public void Feature_replies_to_output_commands_are_not_bounds_checked_against_the_output_length()
    {
        var file = Write("mixed.json", """
            { "devices": [ { "id": "mixed", "name": "Mixed", "match": [ { "vendorId": 1, "productId": 1, "usagePage": 65280 } ],
              "transport": { "reportType": "output", "responseType": "feature", "reportLength": 17 },
              "commands": { "c": { "payload": "08 04", "response": { "fields": [ { "name": "battery", "index": 40 } ] } } } } ] }
            """);

        var catalog = DeviceCatalog.Load([file]);

        Assert.Equal(0, catalog.ErrorCount);
        Assert.Single(catalog.Devices);
    }

    [Fact]
    public void Masks_and_map_values_can_use_bit_31()
    {
        var file = Write("wide.json", """
            { "devices": [ { "id": "w", "name": "W", "match": [ { "vendorId": 1, "productId": 1, "usagePage": 65280 } ],
              "commands": { "c": { "payload": "05 00 00 00 00", "fields": [
                { "name": "value", "index": 1, "size": 4, "mask": "0xFF000000", "shift": 24 },
                { "name": "value", "index": 1, "size": 4, "mask": "0x00000001", "map": { "1": "0xFFFFFFFF" } } ] } } } ] }
            """);

        var catalog = DeviceCatalog.Load([file]);

        Assert.Equal(0, catalog.ErrorCount);
        var field = Assert.Single(catalog.Devices).Commands["c"].Fields[0];
        Assert.Equal(0xFF000000L, field.Mask);
        var report = new byte[5];
        Protocol.FieldCodec.Encode(report, field, 0xAB);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0xAB }, report);
    }

    [Fact]
    public void Bad_hex_numbers_are_reported_with_their_json_location()
    {
        var file = Write("badhex.json", """
            { "devices": [
              { "id": "x", "match": [ { "vendorId": "0xZZ", "productId": 1 } ] } ] }
            """);

        var error = Assert.Single(DeviceCatalog.Load([file]).Messages);

        Assert.Contains("0xZZ", error.Text);
        Assert.Contains("LineNumber: 1", error.Text);
        Assert.Contains("vendorId", error.Text);
    }

    private string Write(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }
}
