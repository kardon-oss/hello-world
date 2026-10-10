using OpenMouseConfig.Core.Definitions;
using OpenMouseConfig.Core.Protocol;

namespace OpenMouseConfig.Core.Tests;

public class HexFormatTests
{
    [Theory]
    [InlineData("05 0A 00 FF")]
    [InlineData("050A00FF")]
    [InlineData("0x05, 0x0A, 0x00, 0xFF")]
    [InlineData("05:0a:00:ff")]
    [InlineData("[0x5, 0xa, 0x0, 0xff]")]
    public void Parse_accepts_common_notations(string text) =>
        Assert.Equal(new byte[] { 0x05, 0x0A, 0x00, 0xFF }, HexFormat.Parse(text));

    [Theory]
    [InlineData("05 0G")]
    [InlineData("050")]
    [InlineData("05 ??")]
    public void Parse_rejects_invalid_input(string text) =>
        Assert.Throws<FormatException>(() => HexFormat.Parse(text));

    [Fact]
    public void FormatCompact_collapses_trailing_padding()
    {
        var report = new byte[64];
        report[0] = 0x05;
        report[1] = 0x01;
        Assert.Equal("05 01 … (+62 × 00)", HexFormat.FormatCompact(report));
        Assert.Equal("05 01 00", HexFormat.FormatCompact(new byte[] { 5, 1, 0 }));
    }

    [Theory]
    [InlineData("0x1A2B", 0x1A2B)]
    [InlineData("26000", 26000)]
    [InlineData("-1", -1)]
    [InlineData("0xFF_00", 0xFF00)]
    public void TryParseInteger_handles_decimal_and_hex(string text, long expected)
    {
        Assert.True(HexFormat.TryParseInteger(text, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void BytePattern_supports_wildcards()
    {
        var pattern = BytePattern.Parse("05 ?? 01");
        Assert.True(pattern.Matches(new byte[] { 0x05, 0x99, 0x01, 0x42 }));
        Assert.False(pattern.Matches(new byte[] { 0x05, 0x99, 0x02 }));
        Assert.False(pattern.Matches(new byte[] { 0x05 }));
        Assert.True(BytePattern.Parse(null).Matches(new byte[] { 1 }));
    }
}

public class FieldCodecTests
{
    [Fact]
    public void Encodes_little_and_big_endian_with_scaling()
    {
        var report = new byte[8];
        FieldCodec.Encode(report, new FieldDefinition { Name = "dpi", Index = 1, Size = 2, Divide = 50 }, 26000); // 520 = 0x0208
        FieldCodec.Encode(report, new FieldDefinition { Name = "dpi", Index = 3, Size = 2, Divide = 50, Endian = Endianness.Big }, 26000);
        Assert.Equal(new byte[] { 0, 0x08, 0x02, 0x02, 0x08, 0, 0, 0 }, report);
    }

    [Fact]
    public void Add_converts_one_based_to_zero_based_and_back()
    {
        var field = new FieldDefinition { Name = "stage", Index = 0, Add = -1 };
        var report = new byte[1];
        FieldCodec.Encode(report, field, 3);
        Assert.Equal(2, report[0]);
        Assert.Equal(3, FieldCodec.Decode(report, field));
    }

    [Fact]
    public void Map_translates_values_and_rejects_unknown_ones()
    {
        var field = new FieldDefinition
        {
            Name = "value",
            Index = 0,
            Map = new() { ["1000"] = 0x01, ["8000"] = 0x40, ["on"] = 0x07 },
        };
        var report = new byte[1];

        FieldCodec.Encode(report, field, 8000);
        Assert.Equal(0x40, report[0]);
        Assert.Equal(8000, FieldCodec.Decode(report, field));

        FieldCodec.Encode(report, field, 1); // "on"
        Assert.Equal(0x07, report[0]);

        Assert.Throws<PayloadException>(() => FieldCodec.Encode(report, field, 250));
        Assert.Null(FieldCodec.Decode(new byte[] { 0x99 }, field));
    }

    [Fact]
    public void Mask_and_shift_only_touch_their_bits()
    {
        var report = new byte[] { 0b1111_0001 };
        var flag = new FieldDefinition { Name = "motionSync", Index = 0, Mask = 0b0000_1000, Shift = 3 };

        FieldCodec.Encode(report, flag, 1);
        Assert.Equal(0b1111_1001, report[0]);
        FieldCodec.Encode(report, flag, 0);
        Assert.Equal(0b1111_0001, report[0]);
        Assert.Equal(0, FieldCodec.Decode(report, flag));
    }

    [Fact]
    public void Out_of_range_values_throw_instead_of_truncating()
    {
        var report = new byte[2];
        Assert.Throws<PayloadException>(() => FieldCodec.Encode(report, new FieldDefinition { Name = "dpi", Index = 0, Divide = 50 }, 26000));
        Assert.Throws<PayloadException>(() => FieldCodec.Encode(report, new FieldDefinition { Name = "x", Index = 1, Size = 2 }, 1));
        Assert.Throws<PayloadException>(() => FieldCodec.Encode(report, new FieldDefinition { Name = "x", Index = 0 }, -1));
    }
}

public class ChecksumAndPayloadTests
{
    [Fact]
    public void Checksums_cover_the_configured_range()
    {
        Assert.Equal(0x0F, Checksum.Compute(new byte[] { 0x05, 0x0A }, ChecksumType.Sum8, 0));
        Assert.Equal(0x0F, Checksum.Compute(new byte[] { 0x05, 0x0A }, ChecksumType.Xor8, 0));
        Assert.Equal(0x46, Checksum.Compute(new byte[] { 0x05, 0x0A }, ChecksumType.SumComplement8, 0x55));
        Assert.Equal(0xF1, Checksum.Compute(new byte[] { 0x05, 0x0A }, ChecksumType.SumComplement8, 0));
    }

    [Fact]
    public void Build_pads_injects_then_checksums()
    {
        var command = new CommandDefinition
        {
            Payload = "05 02",
            Fields =
            [
                new FieldDefinition { Name = "stage", Index = 2, Add = -1 },
                new FieldDefinition { Name = "dpi", Index = 3, Size = 2, Divide = 50 },
            ],
        };
        var checksum = new ChecksumDefinition { Type = ChecksumType.Sum8, Index = -1, Start = 1 };
        var vars = new Dictionary<string, double> { ["stage"] = 2, ["dpi"] = 1600 };

        var report = PayloadBuilder.Build(command, checksum, 8, vars);

        // 05 | 02 01 20 00 00 00 | sum(02+01+20) = 0x23
        Assert.Equal(new byte[] { 0x05, 0x02, 0x01, 0x20, 0x00, 0x00, 0x00, 0x23 }, report);
    }

    [Fact]
    public void Build_refuses_to_guess_the_checksum_position_without_a_report_length()
    {
        var command = new CommandDefinition { Payload = "05 01 02" };
        var checksum = new ChecksumDefinition { Type = ChecksumType.Sum8, Index = -1 };

        var ex = Assert.Throws<PayloadException>(() => PayloadBuilder.Build(command, checksum, 0, new Dictionary<string, double>()));

        Assert.Contains("reportLength", ex.Message);
        Assert.Equal(3, PayloadBuilder.Build(command, new ChecksumDefinition { Type = ChecksumType.None }, 0, new Dictionary<string, double>()).Length);
    }

    [Fact]
    public void Field_without_an_index_never_overwrites_the_report_id()
    {
        var report = new byte[] { 0x05, 0x00 };
        Assert.Throws<PayloadException>(() => FieldCodec.Encode(report, new FieldDefinition { Name = "value" }, 1));
        Assert.Equal(0x05, report[0]);
    }

    [Fact]
    public void Build_explains_missing_variables_and_oversized_payloads()
    {
        var command = new CommandDefinition { Payload = "05 01 02 03", Fields = [new FieldDefinition { Name = "nope", Index = 1 }] };

        var missing = Assert.Throws<PayloadException>(() => PayloadBuilder.Build(command, null, 8, new Dictionary<string, double>()));
        Assert.Contains("nope", missing.Message);

        var tooLong = Assert.Throws<PayloadException>(() => PayloadBuilder.Build(command, null, 2, new Dictionary<string, double> { ["nope"] = 1 }));
        Assert.Contains("report ID", tooLong.Message);
    }
}
