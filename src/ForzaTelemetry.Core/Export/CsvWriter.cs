using System.Globalization;
using System.Text;

namespace ForzaTelemetry.Core.Export;

/// <summary>
/// Writes a CSV file the way spreadsheets expect it: RFC 4180 quoting, UTF-8 with a byte order mark, numbers that do not depend on
/// the user's language.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these choices.</b>
/// <list type="bullet">
/// <item><description><b>Invariant numbers</b> (<c>3.456</c>, never <c>3,456</c>): a comma as the decimal separator would split the
/// value into two columns in any reader that does not share the writer's culture, and the files are meant to be shared.
/// A spreadsheet in a comma-decimal locale still converts a dot-decimal column correctly on import; the reverse is a corrupted
/// file.</description></item>
/// <item><description><b>UTF-8 with BOM</b>: Excel guesses the legacy code page for a BOM-less file and shows "CoupÃ©" for a car
/// nickname with an accent; the BOM is the one signal it honors. Every other reader ignores it.</description></item>
/// <item><description><b>RFC 4180 quoting</b>: CRLF row ends; a field with a comma, a double quote or a line break is quoted, and a
/// quote inside it is doubled.</description></item>
/// <item><description><b>Formula neutralising</b>: a text cell that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or CR would be
/// EXECUTED by a spreadsheet as a formula, and the car nicknames in these files are typed by the user (or by whoever sent them
/// a car file). Such text gets a leading apostrophe (the OWASP-recommended guard); numbers are written by the numeric overloads,
/// which never need it, so a negative number stays a number.</description></item>
/// </list>
/// </para>
/// <para>Not thread-safe. Write fields with <c>Field</c>, finish each row with <see cref="EndRow"/>; the writer puts the commas.</para>
/// </remarks>
public sealed class CsvWriter : IDisposable
{
    private const string RowEnd = "\r\n";

    private readonly TextWriter _writer;
    private bool _atRowStart = true;

    /// <summary>Wraps a writer. Prefer <see cref="Create"/> unless a test wants the text itself.</summary>
    public CsvWriter(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    /// <summary>The encoding of every file this writer produces: UTF-8, with the byte order mark Excel needs to read it as UTF-8.</summary>
    public static Encoding FileEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>A writer over <paramref name="stream"/> in the file format described in the class remarks.</summary>
    /// <param name="stream">The destination; written, flushed and (unless <paramref name="leaveOpen"/>) closed with the writer.</param>
    /// <param name="leaveOpen">Keep the stream open when the writer is disposed.</param>
    public static CsvWriter Create(Stream stream, bool leaveOpen = false) =>
        new(new StreamWriter(stream, FileEncoding, bufferSize: 64 * 1024, leaveOpen) { NewLine = RowEnd });

    /// <summary>Writes a whole row of text fields (typically the header).</summary>
    public CsvWriter Row(params ReadOnlySpan<string> fields)
    {
        foreach (var field in fields)
        {
            Field(field);
        }

        return EndRow();
    }

    /// <summary>A text field: quoted when it needs it, neutralised when a spreadsheet could take it for a formula. Null is an empty cell.</summary>
    public CsvWriter Field(string? value)
    {
        Separate();
        if (string.IsNullOrEmpty(value))
        {
            return this;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        if (value.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            _writer.Write(value);
            return this;
        }

        _writer.Write('"');
        _writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        _writer.Write('"');
        return this;
    }

    /// <summary>A whole number.</summary>
    public CsvWriter Field(long value)
    {
        Separate();
        _writer.Write(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    /// <summary>
    /// A real number in the invariant culture, formatted with <paramref name="format"/> (for example <c>"0.000"</c>). A null, NaN or
    /// infinite value is an empty cell: "no value" must not read as a number.
    /// </summary>
    public CsvWriter Field(double? value, string format)
    {
        Separate();
        if (value is { } number && double.IsFinite(number))
        {
            _writer.Write(number.ToString(format, CultureInfo.InvariantCulture));
        }

        return this;
    }

    /// <summary>An instant as ISO 8601 with its offset (<c>2026-10-18T12:34:56.0000000-04:00</c>): unambiguous in any time zone. Null is an empty cell.</summary>
    public CsvWriter Field(DateTimeOffset? value)
    {
        Separate();
        if (value is { } instant)
        {
            _writer.Write(instant.ToString("O", CultureInfo.InvariantCulture));
        }

        return this;
    }

    /// <summary>Ends the current row.</summary>
    public CsvWriter EndRow()
    {
        _writer.Write(RowEnd);
        _atRowStart = true;
        return this;
    }

    /// <summary>Writes buffered text to the destination.</summary>
    public void Flush() => _writer.Flush();

    /// <inheritdoc />
    public void Dispose() => _writer.Dispose();

    private void Separate()
    {
        if (!_atRowStart)
        {
            _writer.Write(',');
        }

        _atRowStart = false;
    }
}
