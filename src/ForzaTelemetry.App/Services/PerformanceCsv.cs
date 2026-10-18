using System.IO;
using ForzaTelemetry.Core.Export;

namespace ForzaTelemetry.App.Services;

/// <summary>Every car's stored bests as a CSV table: one row per car (model and performance index), in the same units whatever the display unit.</summary>
/// <remarks>
/// <para>
/// <b>Units are in the column names, and fixed.</b> The Performance page shows what the user's unit setting picks (km/h or mph),
/// but a file is shared and read later: a column called "top speed" with no unit is a guess. So the export always carries every value
/// the history holds, in the unit its name says (<c>zero_to_100_kmh_s</c>, <c>zero_to_60_mph_s</c>, <c>top_speed_kmh</c>,
/// <c>brake_100_to_0_kmh_m</c>...), independent of the display setting. A value the car never measured is an empty cell.
/// </para>
/// <para>
/// <c>car_name</c> is the name the app shows for the car (the player's nickname, else the real name from the bundled list, else "Car #N"); the game's id, <c>car_ordinal</c>, is the stable key.
/// <c>updated_at</c> is ISO 8601 with its offset.
/// </para>
/// </remarks>
public static class PerformanceCsv
{
    /// <summary>The header row.</summary>
    public static readonly string[] Columns =
    [
        "car_ordinal", "car_name", "performance_index", "class", "drivetrain",
        "zero_to_100_kmh_s", "zero_to_200_kmh_s", "hundred_to_200_kmh_s", "zero_to_60_mph_s", "sixty_to_130_mph_s",
        "quarter_mile_s", "quarter_mile_trap_kmh", "top_speed_kmh", "brake_100_to_0_kmh_m", "brake_60_to_0_mph_m",
        "updated_at"
    ];

    /// <summary>Writes the table (header and rows) to <paramref name="stream"/>, which stays open.</summary>
    /// <param name="stream">The destination.</param>
    /// <param name="records">The stored bests, in any order (rows are written by car id, then performance index).</param>
    /// <param name="carName">Looks up the display name of a car id (null or empty when unknown); optional. The store behind it is
    /// UI-thread-only, so a caller that writes on another thread passes a lookup over a snapshot.</param>
    public static void Write(Stream stream, IEnumerable<CarPerformanceRecord> records, Func<int, string?>? carName = null)
    {
        using var csv = CsvWriter.Create(stream, leaveOpen: true);
        csv.Row(Columns);

        foreach (var record in records.OrderBy(r => r.Car.CarOrdinal).ThenBy(r => r.Car.PerformanceIndex))
        {
            csv.Field(record.Car.CarOrdinal)
                .Field(carName?.Invoke(record.Car.CarOrdinal))
                .Field(record.Car.PerformanceIndex)
                .Field(CarInfoFormatter.ClassLabel(record.CarClass))
                .Field(CarInfoFormatter.DrivetrainLabel(record.DrivetrainType))
                .Field(record.ZeroTo100Seconds, "0.000")
                .Field(record.ZeroTo200Seconds, "0.000")
                .Field(record.HundredTo200Seconds, "0.000")
                .Field(record.ZeroTo60MphSeconds, "0.000")
                .Field(record.SixtyTo130MphSeconds, "0.000")
                .Field(record.QuarterMileSeconds, "0.000")
                .Field(record.QuarterMileTrapKmh, "0.0")
                .Field(record.TopSpeedKmh, "0.0")
                .Field(record.BrakeHundredMeters, "0.0")
                .Field(record.BrakeSixtyMphMeters, "0.0")
                .Field(record.UpdatedAt == DateTimeOffset.UnixEpoch ? null : record.UpdatedAt) // the epoch stands for "unknown"
                .EndRow();
        }
    }
}
