using System.Globalization;
using System.Text;
using ForzaTelemetry.Core.Export;

namespace ForzaTelemetry.Core.Tests;

public class CsvWriterTests
{
    private static string Write(Action<CsvWriter> build)
    {
        var text = new StringWriter();
        var csv = new CsvWriter(text);
        build(csv);
        csv.Flush();
        return text.ToString();
    }

    [Fact]
    public void Fields_are_separated_by_commas_and_rows_end_with_CRLF()
    {
        var text = Write(csv =>
        {
            csv.Row("lap", "time_s");
            csv.Field(1).Field(62.5, "0.000").EndRow();
        });

        Assert.Equal("lap,time_s\r\n1,62.500\r\n", text);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("two\r\nlines", "\"two\r\nlines\"")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("  padded  ", "  padded  ")]
    [InlineData("Coupé ñ 速度", "Coupé ñ 速度")]
    public void Text_is_quoted_only_when_it_needs_to_be_and_quotes_are_doubled(string? value, string expected)
    {
        Assert.Equal(expected + "\r\n", Write(csv => csv.Field(value).EndRow()));
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+SUM(A1)", "'+SUM(A1)")]
    [InlineData("-2", "'-2")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tindented", "'\tindented")]
    public void Text_a_spreadsheet_would_run_as_a_formula_is_neutralised(string value, string expected)
    {
        Assert.Equal(expected + "\r\n", Write(csv => csv.Field(value).EndRow()));
    }

    [Fact]
    public void A_neutralised_field_that_also_needs_quotes_gets_both()
    {
        Assert.Equal("\"'=a,b\"\r\n", Write(csv => csv.Field("=a,b").EndRow()));
    }

    [Fact]
    public void Numbers_are_not_neutralised_so_a_negative_stays_a_number()
    {
        Assert.Equal("-1.500,-3\r\n", Write(csv => csv.Field(-1.5, "0.000").Field(-3).EndRow()));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("es-ES")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void Numbers_use_the_invariant_culture_whatever_the_language(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            Assert.Equal("1234.500,0.25\r\n", Write(csv => csv.Field(1234.5, "0.000").Field(0.25, "0.##").EndRow()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_missing_or_non_finite_number_is_an_empty_cell(double value)
    {
        Assert.Equal(",\r\n", Write(csv => csv.Field(value, "0.0").Field((double?)null, "0.0").EndRow()));
    }

    [Fact]
    public void An_instant_is_written_as_ISO_8601_with_its_offset()
    {
        var instant = new DateTimeOffset(2026, 10, 18, 12, 34, 56, TimeSpan.FromHours(-4));

        Assert.Equal("2026-10-18T12:34:56.0000000-04:00,\r\n", Write(csv => csv.Field(instant).Field((DateTimeOffset?)null).EndRow()));
    }

    [Fact]
    public void An_empty_row_is_just_the_line_end()
    {
        Assert.Equal("\r\n", Write(csv => csv.EndRow()));
    }

    [Fact]
    public void The_file_is_UTF8_with_a_byte_order_mark_so_Excel_reads_accents()
    {
        var stream = new MemoryStream();
        using (var csv = CsvWriter.Create(stream, leaveOpen: true))
        {
            csv.Row("name").Field("Coupé").EndRow();
        }

        var bytes = stream.ToArray();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("name\r\nCoupé\r\n", new UTF8Encoding(false).GetString(bytes[3..]));
    }

    [Fact]
    public void Leave_open_keeps_the_stream_usable_after_the_writer_is_disposed()
    {
        var stream = new MemoryStream();

        CsvWriter.Create(stream, leaveOpen: true).Dispose();

        Assert.True(stream.CanWrite);
    }
}
