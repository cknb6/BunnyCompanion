using System.Text;
using System.Text.Json;

namespace BunnyCompanion.Services;

/// <summary>
/// 天气播报纯逻辑：解析 wttr j1 JSON、高温/降水警示。无网络依赖，可单测。
/// </summary>
public static class WeatherReport
{
    public static string FormatWeatherBroadcast(string wttrJson, string? preferredPlace = null)
    {
        using var doc = JsonDocument.Parse(wttrJson);
        var root = doc.RootElement;
        var cur = root.GetProperty("current_condition")[0];
        var area = root.GetProperty("nearest_area")[0];
        var areaName = area.GetProperty("areaName")[0].GetProperty("value").GetString();
        var region = area.GetProperty("region")[0].GetProperty("value").GetString();
        var country = area.GetProperty("country")[0].GetProperty("value").GetString();
        string desc;
        try
        {
            desc = cur.GetProperty("lang_zh")[0].GetProperty("value").GetString()
                   ?? cur.GetProperty("weatherDesc")[0].GetProperty("value").GetString()
                   ?? "—";
        }
        catch
        {
            desc = cur.GetProperty("weatherDesc")[0].GetProperty("value").GetString() ?? "—";
        }

        _ = double.TryParse(G(cur, "temp_C"), out var tempC);
        _ = double.TryParse(G(cur, "FeelsLikeC"), out var feelsC);
        _ = double.TryParse(G(cur, "humidity"), out var humidity);
        _ = double.TryParse(G(cur, "windspeedKmph"), out var wind);
        _ = double.TryParse(G(cur, "precipMM"), out var precipNow);

        double maxC = tempC, minC = tempC;
        double precipDay = precipNow;
        var chanceRainDay = 0;
        var hourlyHints = new List<string>();

        if (root.TryGetProperty("weather", out var days) && days.GetArrayLength() > 0)
        {
            var d0 = days[0];
            _ = double.TryParse(G(d0, "maxtempC"), out maxC);
            _ = double.TryParse(G(d0, "mintempC"), out minC);
            if (d0.TryGetProperty("hourly", out var hours) && hours.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hours.EnumerateArray())
                {
                    _ = double.TryParse(G(h, "precipMM"), out var p);
                    precipDay += p;
                    if (int.TryParse(G(h, "chanceofrain"), out var ch) && ch > chanceRainDay)
                        chanceRainDay = ch;
                    var t = G(h, "time");
                    if (t is "900" or "1200" or "1500" or "1800")
                    {
                        var hd = TryZhDesc(h) ?? G(h, "weatherDesc");
                        var ht = G(h, "tempC");
                        var hr = G(h, "chanceofrain");
                        hourlyHints.Add($"{FormatWttrHour(t)}约{ht}°C {hd}（降水概率{hr}%）");
                    }
                }
            }
        }

        var place = !string.IsNullOrWhiteSpace(preferredPlace)
            ? preferredPlace!
            : $"{country} {region} {areaName}".Trim();

        var alerts = BuildWeatherAlerts(tempC, feelsC, maxC, precipNow, precipDay, chanceRainDay, desc);

        var sb = new StringBuilder();
        sb.AppendLine("【天气播报】");
        sb.AppendLine($"定位参考: {place}");
        sb.AppendLine($"现在: {desc}，{tempC:0.#}°C（体感 {feelsC:0.#}°C）");
        sb.AppendLine($"湿度 {humidity:0.#}% · 风速 {wind:0.#} km/h · 近时降水 {precipNow:0.##} mm");
        sb.AppendLine($"今日: 最高 {maxC:0.#}°C / 最低 {minC:0.#}°C · 日累计降水约 {precipDay:0.##} mm · 最大降水概率 {chanceRainDay}%");
        if (hourlyHints.Count > 0)
        {
            sb.AppendLine("分段:");
            foreach (var h in hourlyHints.Take(4))
                sb.AppendLine("  · " + h);
        }

        sb.AppendLine("【提醒与预警】");
        if (alerts.Count == 0)
            sb.AppendLine("· 暂无高温/强降水类特别预警，仍建议看天出门。");
        else
            foreach (var a in alerts)
                sb.AppendLine("· " + a);

        sb.AppendLine($"更新时间: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("说明: 位置基于公网 IP/指定城市，精度到城市级；公司专线可能显示总部城市。");
        return sb.ToString().Trim();
    }

    public static List<string> BuildWeatherAlerts(
        double tempC, double feelsC, double maxC,
        double precipNowMm, double precipDayMm, int chanceRainPct, string desc)
    {
        var alerts = new List<string>();
        var maxFeel = Math.Max(tempC, Math.Max(feelsC, maxC));

        if (maxFeel >= 40 || maxC >= 40)
            alerts.Add("高温红色关注：气温/体感或今日最高 ≥40°C，避免长时间暴晒，多补水。");
        else if (maxFeel >= 37 || maxC >= 37)
            alerts.Add("高温橙色关注：≥37°C 级别炎热，外出做好防晒与补水。");
        else if (maxFeel >= 35 || maxC >= 35)
            alerts.Add("高温黄色关注：≥35°C，午后尽量少在户外逗留。");

        if (tempC <= 0 || maxC <= 2 && tempC < 5)
            alerts.Add("低温提醒：较冷，注意添衣，别冻着。");

        var rainyWord = (desc ?? "").Contains("雨", StringComparison.Ordinal)
                        || (desc ?? "").Contains("雪", StringComparison.Ordinal)
                        || (desc ?? "").Contains("雷", StringComparison.Ordinal);
        if (precipNowMm >= 5 || precipDayMm >= 15 || chanceRainPct >= 70 || rainyWord && chanceRainPct >= 40)
            alerts.Add("降水提醒：有一定降水可能/正在降水，出门建议带伞，路面可能湿滑。");
        else if (chanceRainPct >= 40 || precipDayMm >= 3)
            alerts.Add("零星降水可能：概率不低，包里备把伞更安心。");

        if ((desc ?? "").Contains("雷", StringComparison.Ordinal))
            alerts.Add("雷电提醒：注意安全，尽量避开空旷与高大金属物。");
        if ((desc ?? "").Contains("雾", StringComparison.Ordinal) || (desc ?? "").Contains("霾", StringComparison.Ordinal))
            alerts.Add("能见度提醒：雾/霾天气，出行注意交通安全。");

        return alerts;
    }

    private static string G(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.String => p.GetString() ?? "",
                JsonValueKind.Number => p.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => p.ToString(),
            }
            : "";

    private static string? TryZhDesc(JsonElement h)
    {
        try
        {
            if (h.TryGetProperty("lang_zh", out var zh) && zh.GetArrayLength() > 0)
                return zh[0].GetProperty("value").GetString();
            if (h.TryGetProperty("weatherDesc", out var wd) && wd.GetArrayLength() > 0)
                return wd[0].GetProperty("value").GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    private static string FormatWttrHour(string t) => t switch
    {
        "0" or "0000" => "凌晨",
        "300" => "凌晨3点",
        "600" => "清晨",
        "900" => "上午",
        "1200" => "中午",
        "1500" => "下午",
        "1800" => "傍晚",
        "2100" => "晚上",
        _ => t + "时",
    };
}
