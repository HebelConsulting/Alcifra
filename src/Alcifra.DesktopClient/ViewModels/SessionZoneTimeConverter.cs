using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Alcifra.ApiClient;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// Shows an instant bound straight from an API row on the session's clock, as the general date/short time ("g") —
/// what a view needs when it binds a <see cref="DateTimeOffset"/> it has no row view-model for (#1911).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The reminder dialog and the My Work list bound <c>RemindAt</c> with
/// <c>StringFormat='{}{0:g}'</c>, which prints the instant in whatever offset it arrived with: on Postgres the
/// server answers in UTC, so a reminder set for 09:00 in Zurich read "07:00" — while the web, converting to
/// <c>SessionTimeZone.Current</c>, read 09:00 (ADR 0801/0812's rule: the viewer's clock, never the payload's).
/// </para>
/// <para>
/// The zone is a constructor argument read at each conversion, so a test can pin it without touching the
/// process-wide session zone; <see cref="Instance"/> reads the session's.
/// </para>
/// </remarks>
public sealed class SessionZoneTimeConverter(Func<TimeZoneInfo> zone) : IValueConverter
{
    /// <summary>On the session's zone — the one every other instant in the desktop is shown in.</summary>
    public static readonly SessionZoneTimeConverter Instance = new(() => SessionTimeZone.Current);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset instant => instant.InZone(zone()).DateTime.ToString("g", culture),
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
