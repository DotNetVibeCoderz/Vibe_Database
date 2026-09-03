using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Text;
using CuteDB.Browser.Services;
using Microsoft.SemanticKernel;

namespace CuteDB.Browser.Ai.Plugins;

/// <summary>
/// The small things a model cannot do reliably in its head: arithmetic, dates, and encodings.
/// </summary>
/// <remarks>
/// Every function here exists because a language model is bad at it and a computer is not. A model
/// asked to add a column of currency will often be close and occasionally be wrong, and "close" is
/// not a property you want in a figure someone is about to act on. Asked what today's date is, it
/// will confidently give you its training cut-off.
/// </remarks>
public sealed class ToolboxPlugin(ActivityLog log)
{
    /// <summary>Evaluates an arithmetic expression exactly.</summary>
    [KernelFunction("math_calculate")]
    [Description("Evaluates an arithmetic expression and returns the exact result. Use it for any arithmetic at all — totals, percentages, unit conversions, ratios — rather than working it out yourself. Supports + - * / % and parentheses.")]
    public string Calculate(
        [Description("The expression, such as (250000 + 125000) * 0.11")] string expression)
    {
        log.Info("jack", $"math_calculate({expression})");

        try
        {
            // DataTable.Compute is the one exact expression evaluator already in the framework.
            // It understands the four operators, %, and parentheses, which is the whole of what
            // this function claims to do.
            var value = new DataTable().Compute(expression, filter: null);

            return value is null or DBNull
                ? "That did not evaluate to anything."
                : Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture);
        }
        catch (Exception exception)
        {
            return $"Could not evaluate '{expression}': {exception.Message}";
        }
    }

    /// <summary>Sums, averages and describes a list of numbers.</summary>
    [KernelFunction("math_summarise")]
    [Description("Takes a comma-separated list of numbers and returns their count, sum, mean, minimum, maximum and median. Use it instead of adding a column of figures in your head.")]
    public string Summarise(
        [Description("Numbers separated by commas, such as 250000, 125000, 980000")] string numbers)
    {
        log.Info("jack", "math_summarise");

        var values = numbers
            .Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => decimal.TryParse(part, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                ? value
                : (decimal?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToList();

        if (values.Count == 0)
        {
            return "I could not read any numbers out of that.";
        }

        var median = values.Count % 2 == 1
            ? values[values.Count / 2]
            : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2;

        return $"""
            count  {values.Count:N0}
            sum    {values.Sum():N2}
            mean   {values.Average():N2}
            median {median:N2}
            min    {values[0]:N2}
            max    {values[^1]:N2}
            """;
    }

    /// <summary>Reports the current date and time.</summary>
    [KernelFunction("current_datetime")]
    [Description("Returns the current date and time, locally and in UTC, with the day of the week and the time zone. Call it whenever a question involves 'today', 'now', 'this month' or any relative date — you cannot know the date otherwise.")]
    public string Now()
    {
        log.Info("jack", "current_datetime");

        var local = DateTimeOffset.Now;

        return $"""
            Local: {local:dddd, dd MMMM yyyy HH:mm:ss} ({TimeZoneInfo.Local.DisplayName})
            UTC:   {local.UtcDateTime:yyyy-MM-dd HH:mm:ss}
            ISO:   {local:O}
            Today in CuteQL date literal form: '{local:yyyy-MM-dd}'
            """;
    }

    /// <summary>Adds an interval to a date, or measures the gap between two.</summary>
    [KernelFunction("date_maths")]
    [Description("Date arithmetic. With one date and an offset it returns the shifted date; with two dates it returns the interval between them. Use it rather than counting days yourself.")]
    public string DateMaths(
        [Description("The starting date, ISO format such as 2026-03-01. 'today' is accepted.")] string from,
        [Description("Either a second ISO date to measure to, or an offset such as '+30d', '-2m', '+1y'. Days, months and years.")] string toOrOffset)
    {
        log.Info("jack", $"date_maths({from}, {toOrOffset})");

        if (!TryReadDate(from, out var start))
        {
            return $"I could not read '{from}' as a date. Use an ISO date such as 2026-03-01.";
        }

        if (TryReadDate(toOrOffset, out var end))
        {
            var span = end - start;
            var months = ((end.Year - start.Year) * 12) + end.Month - start.Month;

            return $"""
                From {start:yyyy-MM-dd} to {end:yyyy-MM-dd}
                {span.TotalDays:N0} days ({span.TotalDays / 7:N1} weeks, about {months} months)
                """;
        }

        var text = toOrOffset.Trim();
        var sign = text.StartsWith('-') ? -1 : 1;
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());

        if (digits.Length == 0)
        {
            return $"I could not read '{toOrOffset}' as a date or an offset. Try '+30d' or '2026-06-30'.";
        }

        var amount = sign * int.Parse(digits, CultureInfo.InvariantCulture);
        var unit = char.ToLowerInvariant(text[^1]);

        var result = unit switch
        {
            'y' => start.AddYears(amount),
            'm' => start.AddMonths(amount),
            'w' => start.AddDays(amount * 7),
            _ => start.AddDays(amount),
        };

        return $"{start:yyyy-MM-dd} {(sign < 0 ? "-" : "+")} {Math.Abs(amount)}{unit} = {result:yyyy-MM-dd} ({result:dddd})";
    }

    /// <summary>Converts between text, base64 and hexadecimal.</summary>
    [KernelFunction("encode_text")]
    [Description("Converts text between plain, base64 and hex. Useful when a stored value is encoded and the person wants to see what it says, or the other way round.")]
    public string Encode(
        [Description("The text to convert.")] string text,
        [Description("One of: to_base64, from_base64, to_hex, from_hex.")] string mode)
    {
        log.Info("jack", $"encode_text({mode})");

        try
        {
            return mode.Trim().ToLowerInvariant() switch
            {
                "to_base64" => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
                "from_base64" => Encoding.UTF8.GetString(Convert.FromBase64String(text)),
                "to_hex" => Convert.ToHexString(Encoding.UTF8.GetBytes(text)),
                "from_hex" => Encoding.UTF8.GetString(Convert.FromHexString(text)),
                _ => "Mode must be one of: to_base64, from_base64, to_hex, from_hex.",
            };
        }
        catch (Exception exception)
        {
            return $"Could not convert that: {exception.Message}";
        }
    }

    private static bool TryReadDate(string text, out DateTime value)
    {
        text = text.Trim();

        if (string.Equals(text, "today", StringComparison.OrdinalIgnoreCase))
        {
            value = DateTime.Today;
            return true;
        }

        if (string.Equals(text, "now", StringComparison.OrdinalIgnoreCase))
        {
            value = DateTime.Now;
            return true;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
