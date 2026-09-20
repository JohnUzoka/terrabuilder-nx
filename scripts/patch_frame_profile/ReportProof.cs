using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Program;
internal static class ReportProof
{
    internal static object Verify(string text)
    {
        var token = new Regex("([a-z_][a-z_0-9]*)=(\"(?:\\\\.|[^\"\\\\])*\"|[^ ]+)", RegexOptions.CultureInvariant);
        var rows = new List<Dictionary<string, string>>();
        long activeInterval = -1, frequency = 0; int blocks = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Require(line.StartsWith("NX_PROFILE "), "unparseable log prefix");
            int begin = line.IndexOf(' ', 11); Require(begin > 0, "missing row attributes");
            var row = new Dictionary<string, string> { ["row"] = line[11..begin] };
            int at = begin + 1;
            foreach (Match match in token.Matches(line, at))
            {
                Require(match.Index == at, "unparsed report bytes");
                string key = match.Groups[1].Value, value = match.Groups[2].Value;
                row.Add(key, value.StartsWith('"') ? JsonSerializer.Deserialize<string>(value)! : value);
                at = match.Index + match.Length + 1;
            }
            Require(at == line.Length + 1, "unparsed report suffix");
            long interval = long.Parse(row["interval"], CultureInfo.InvariantCulture);
            if (row["row"] == "BEGIN") { Require(activeInterval == -1, "nested report block"); activeInterval = interval; frequency = long.Parse(row["frequency"], CultureInfo.InvariantCulture); }
            Require(interval == activeInterval, "report interval mismatch");
            if (row["row"] == "END") { activeInterval = -1; blocks++; }
            if (row["row"] == "METRIC")
            {
                long used = long.Parse(row["used"], CultureInfo.InvariantCulture), invalid = long.Parse(row["invalid_negative"], CultureInfo.InvariantCulture), total = long.Parse(row["valid_raw_total"], CultureInfo.InvariantCulture), frames = long.Parse(row["frames"], CultureInfo.InvariantCulture);
                long valid = used - invalid;
                Require(valid >= 0 && frames > 0, "invalid metric denominator");
                if (valid == 0) Require(row["mean_per_frame"] == "null" && row["mean_per_valid_used_frame"] == "null" && row["max"] == "null", "fabricated empty mean");
                else {
                    double scale = row["unit"] == "ms" ? 1000.0 / frequency : 1;
                    double mean = double.Parse(row["mean_per_frame"], CultureInfo.InvariantCulture), usedMean = double.Parse(row["mean_per_valid_used_frame"], CultureInfo.InvariantCulture);
                    Require(Math.Abs(mean - (double)total / frames * scale) < 1e-6 && Math.Abs(usedMean - (double)total / valid * scale) < 1e-6, "unit or mean denominator differs");
                }
            }
            rows.Add(row);
        }
        Require(activeInterval == -1 && blocks > 0, "incomplete report block");
        Require(rows.Any(r => r.GetValueOrDefault("label") == "timer\"\\\n☃"), "JSON label did not roundtrip");
        return new { blocks, rows = rows.Count, metricRows = rows.Count(r => r["row"] == "METRIC"), parsedLabelsAndInvariantMeans = true };
    }
}
