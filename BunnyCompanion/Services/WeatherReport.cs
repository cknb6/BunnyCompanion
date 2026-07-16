using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BunnyCompanion.Services;

/// <summary>
/// 统一天气快照：经纬度与各字段从接口 JSON 解析后完整传递，避免中间丢失。
/// </summary>
public sealed class WeatherSnapshot
{
    /// <summary>地点文字（国家/省/市 或 用户指定城市）。</summary>
    public string Place { get; init; } = "";

    /// <summary>纬度（十进制度，北纬为正）。有值时必须写入播报。</summary>
    public double? Latitude { get; init; }

    /// <summary>经度（十进制度，东经为正）。有值时必须写入播报。</summary>
    public double? Longitude { get; init; }

    /// <summary>海拔米，Open-Meteo 可能返回。</summary>
    public double? ElevationM { get; init; }

    /// <summary>时区 ID，如 Asia/Shanghai。</summary>
    public string? Timezone { get; init; }

    /// <summary>坐标从哪来：IP定位 / 地理编码 / 接口回写 / 用户指定。</summary>
    public string CoordSource { get; init; } = "";

    public string Source { get; init; } = "";
    public string Description { get; init; } = "—";
    public double TempC { get; init; }
    public double FeelsC { get; init; }
    public double Humidity { get; init; }
    public double WindKmh { get; init; }
    public double PrecipNowMm { get; init; }
    public double MaxC { get; init; }
    public double MinC { get; init; }
    public double PrecipDayMm { get; init; }
    public int ChanceRainPct { get; init; }
    public double? UvIndexMax { get; init; }
    public int? WeatherCode { get; init; }
    public IReadOnlyList<string> HourlyHints { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 天气播报纯逻辑：多源 JSON 安全解析、经纬度原样传递、高温/降水警示、关心提醒。
/// 无网络依赖，可单测。
/// </summary>
public static class WeatherReport
{
    /// <summary>兼容旧调用：解析 wttr j1 JSON。</summary>
    public static string FormatWeatherBroadcast(string wttrJson, string? preferredPlace = null) =>
        FormatWeatherBroadcast(wttrJson, preferredPlace, preferredLat: null, preferredLon: null, coordSource: null);

    /// <summary>
    /// 解析 wttr j1；若调用方已有更准的经纬度，优先写入快照（不丢弃上游传递的坐标）。
    /// </summary>
    public static string FormatWeatherBroadcast(
        string wttrJson,
        string? preferredPlace,
        double? preferredLat,
        double? preferredLon,
        string? coordSource = null)
    {
        var snap = ParseWttr(wttrJson, preferredPlace, preferredLat, preferredLon, coordSource);
        return FormatSnapshot(snap);
    }

    public static string FormatSnapshot(WeatherSnapshot snap)
    {
        var alerts = BuildWeatherAlerts(
            snap.TempC, snap.FeelsC, snap.MaxC,
            snap.PrecipNowMm, snap.PrecipDayMm, snap.ChanceRainPct, snap.Description,
            snap.UvIndexMax, snap.WindKmh, snap.Humidity, snap.MinC);

        var cares = BuildCareTips(snap);

        var sb = new StringBuilder();
        sb.AppendLine("【天气播报】");
        sb.AppendLine($"地点: {NullSafe(snap.Place, "未知")}");

        // 经纬度单独成行、字段名写死，便于模型与用户准确读取
        if (snap.Latitude is double lat && snap.Longitude is double lon)
        {
            sb.AppendLine($"纬度(Latitude): {FormatCoord(lat)}°（{(lat >= 0 ? "北纬" : "南纬")} {Math.Abs(lat):0.#####}°）");
            sb.AppendLine($"经度(Longitude): {FormatCoord(lon)}°（{(lon >= 0 ? "东经" : "西经")} {Math.Abs(lon):0.#####}°）");
            sb.AppendLine($"坐标对: {FormatCoord(lat)}, {FormatCoord(lon)}");
            if (!string.IsNullOrWhiteSpace(snap.CoordSource))
                sb.AppendLine($"坐标来源: {snap.CoordSource}");
        }
        else
        {
            sb.AppendLine("纬度(Latitude): 未知");
            sb.AppendLine("经度(Longitude): 未知");
            sb.AppendLine("坐标对: 未知（仅城市级查询，未拿到十进制经纬度）");
            if (!string.IsNullOrWhiteSpace(snap.CoordSource))
                sb.AppendLine($"坐标说明: {snap.CoordSource}");
        }

        if (snap.ElevationM is double elev)
            sb.AppendLine($"海拔: 约 {elev:0.#} 米");
        if (!string.IsNullOrWhiteSpace(snap.Timezone))
            sb.AppendLine($"时区: {snap.Timezone}");

        sb.AppendLine($"现在: {snap.Description}，{snap.TempC:0.#}°C（体感 {snap.FeelsC:0.#}°C）");
        sb.AppendLine($"湿度 {snap.Humidity:0.#}% · 风速 {snap.WindKmh:0.#} km/h · 近时降水 {snap.PrecipNowMm:0.##} mm");
        sb.AppendLine($"今日: 最高 {snap.MaxC:0.#}°C / 最低 {snap.MinC:0.#}°C · 日累计降水约 {snap.PrecipDayMm:0.##} mm · 最大降水概率 {snap.ChanceRainPct}%");
        if (snap.UvIndexMax is double uv)
            sb.AppendLine($"紫外线指数(今日峰值): {uv:0.#}（{UvLevelText(uv)}）");
        if (snap.WeatherCode is int code)
            sb.AppendLine($"天气代码(WMO): {code}");

        if (snap.HourlyHints.Count > 0)
        {
            sb.AppendLine("分段:");
            foreach (var h in snap.HourlyHints.Take(4))
                sb.AppendLine("  · " + h);
        }

        sb.AppendLine("【提醒与预警】");
        if (alerts.Count == 0)
            sb.AppendLine("· 暂无高温/强降水类特别预警，仍建议看天出门。");
        else
            foreach (var a in alerts)
                sb.AppendLine("· " + a);

        sb.AppendLine("【关心提醒】");
        if (cares.Count == 0)
            sb.AppendLine("· 天气还算温和，按自己节奏出门就好，我在桌角陪着你。");
        else
            foreach (var c in cares)
                sb.AppendLine("· " + c);

        sb.AppendLine($"数据源: {NullSafe(snap.Source, "未知")}");
        sb.AppendLine($"更新时间: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("说明: 预报按上述经纬度点位计算；IP 定位精度约城市级，公司专线可能显示总部城市。");
        return sb.ToString().Trim();
    }

    public static WeatherSnapshot ParseWttr(
        string wttrJson,
        string? preferredPlace = null,
        double? preferredLat = null,
        double? preferredLon = null,
        string? coordSource = null)
    {
        if (string.IsNullOrWhiteSpace(wttrJson))
            throw new ArgumentException("wttr JSON 为空", nameof(wttrJson));

        using var doc = JsonDocument.Parse(wttrJson);
        var root = doc.RootElement;

        if (!TryGetArrayItem(root, "current_condition", 0, out var cur))
            throw new InvalidOperationException("wttr JSON 缺少 current_condition[0]");
        if (!TryGetArrayItem(root, "nearest_area", 0, out var area))
            throw new InvalidOperationException("wttr JSON 缺少 nearest_area[0]");

        var areaName = GetNestedValue(area, "areaName");
        var region = GetNestedValue(area, "region");
        var country = GetNestedValue(area, "country");
        var desc = GetNestedValue(cur, "lang_zh");
        if (string.IsNullOrWhiteSpace(desc))
            desc = GetNestedValue(cur, "weatherDesc");
        if (string.IsNullOrWhiteSpace(desc))
            desc = "—";

        var tempC = ReadDouble(cur, "temp_C");
        var feelsC = ReadDouble(cur, "FeelsLikeC", tempC);
        var humidity = ReadDouble(cur, "humidity");
        var wind = ReadDouble(cur, "windspeedKmph");
        var precipNow = ReadDouble(cur, "precipMM");
        var uvNow = ReadDouble(cur, "uvIndex", double.NaN);

        double maxC = tempC, minC = tempC;
        double precipDay = precipNow;
        var chanceRainDay = 0;
        var hourlyHints = new List<string>();
        double? uvMax = !double.IsNaN(uvNow) && uvNow > 0 ? uvNow : null;

        // wttr 区域坐标（字符串数字）
        double? jsonLat = ReadDoubleNullable(area, "latitude");
        double? jsonLon = ReadDoubleNullable(area, "longitude");

        if (TryGetArrayItem(root, "weather", 0, out var d0))
        {
            maxC = ReadDouble(d0, "maxtempC", maxC);
            minC = ReadDouble(d0, "mintempC", minC);
            var dayUv = ReadDouble(d0, "uvIndex", double.NaN);
            if (!double.IsNaN(dayUv) && dayUv > 0)
                uvMax = uvMax is null ? dayUv : Math.Max(uvMax.Value, dayUv);

            if (d0.TryGetProperty("hourly", out var hours) && hours.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hours.EnumerateArray())
                {
                    precipDay += ReadDouble(h, "precipMM");
                    var ch = (int)Math.Round(ReadDouble(h, "chanceofrain"));
                    if (ch > chanceRainDay)
                        chanceRainDay = ch;
                    var t = ReadString(h, "time");
                    if (t is "900" or "1200" or "1500" or "1800")
                    {
                        var hd = GetNestedValue(h, "lang_zh");
                        if (string.IsNullOrWhiteSpace(hd))
                            hd = GetNestedValue(h, "weatherDesc");
                        if (string.IsNullOrWhiteSpace(hd))
                            hd = ReadString(h, "weatherDesc");
                        var ht = ReadString(h, "tempC");
                        var hr = ReadString(h, "chanceofrain");
                        hourlyHints.Add($"{FormatWttrHour(t)}约{ht}°C {hd}（降水概率{hr}%）");
                    }
                }
            }
        }

        var place = !string.IsNullOrWhiteSpace(preferredPlace)
            ? preferredPlace!
            : string.Join(" ", new[] { country, region, areaName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

        // 上游传入坐标优先；否则用 JSON 内坐标
        double? lat = preferredLat ?? jsonLat;
        double? lon = preferredLon ?? jsonLon;
        var csrc = !string.IsNullOrWhiteSpace(coordSource)
            ? coordSource!
            : preferredLat is not null && preferredLon is not null
                ? "调用方传入坐标"
                : jsonLat is not null && jsonLon is not null
                    ? "wttr nearest_area JSON 字段 latitude/longitude"
                    : "未解析到经纬度";

        return new WeatherSnapshot
        {
            Place = place,
            Latitude = lat,
            Longitude = lon,
            CoordSource = csrc,
            Source = "wttr.in（免费备源）",
            Description = desc,
            TempC = tempC,
            FeelsC = feelsC,
            Humidity = humidity,
            WindKmh = wind,
            PrecipNowMm = precipNow,
            MaxC = maxC,
            MinC = minC,
            PrecipDayMm = precipDay,
            ChanceRainPct = chanceRainDay,
            UvIndexMax = uvMax,
            HourlyHints = hourlyHints,
        };
    }

    /// <summary>
    /// 解析 Open-Meteo forecast JSON。preferredLat/Lon 用于覆盖或补全；
    /// 响应体里的 latitude/longitude 也会读取并回写，保证坐标可追溯。
    /// </summary>
    public static WeatherSnapshot ParseOpenMeteo(
        string json,
        string? preferredPlace = null,
        double? preferredLat = null,
        double? preferredLon = null,
        string? coordSource = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Open-Meteo JSON 为空", nameof(json));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("current", out var cur) || cur.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Open-Meteo 响应缺少 current 对象");

        var tempC = ReadDouble(cur, "temperature_2m");
        var feelsC = ReadDouble(cur, "apparent_temperature", tempC);
        var humidity = ReadDouble(cur, "relative_humidity_2m");
        var wind = ReadDouble(cur, "wind_speed_10m");
        var precipNow = ReadDouble(cur, "precipitation");
        var code = (int)Math.Round(ReadDouble(cur, "weather_code"));
        var desc = DescribeWmo(code);

        double maxC = tempC, minC = tempC, precipDay = precipNow;
        var chanceRain = 0;
        double? uvMax = null;

        if (root.TryGetProperty("daily", out var daily) && daily.ValueKind == JsonValueKind.Object)
        {
            maxC = FirstArrayDouble(daily, "temperature_2m_max", maxC);
            minC = FirstArrayDouble(daily, "temperature_2m_min", minC);
            precipDay = FirstArrayDouble(daily, "precipitation_sum", precipDay);
            chanceRain = (int)Math.Round(FirstArrayDouble(daily, "precipitation_probability_max", 0));
            var uv = FirstArrayDouble(daily, "uv_index_max", double.NaN);
            if (!double.IsNaN(uv))
                uvMax = uv;
        }

        var hourlyHints = new List<string>();
        if (root.TryGetProperty("hourly", out var hourly) && hourly.ValueKind == JsonValueKind.Object
            && hourly.TryGetProperty("time", out var times) && times.ValueKind == JsonValueKind.Array)
        {
            var temps = hourly.TryGetProperty("temperature_2m", out var tArr) ? tArr : default;
            var probs = hourly.TryGetProperty("precipitation_probability", out var pArr) ? pArr : default;
            var codes = hourly.TryGetProperty("weather_code", out var cArr) ? cArr : default;
            var uvs = hourly.TryGetProperty("uv_index", out var uArr) ? uArr : default;
            var wantHours = new HashSet<int> { 9, 12, 15, 18 };

            for (var i = 0; i < times.GetArrayLength(); i++)
            {
                var ts = times[i].ValueKind == JsonValueKind.String ? times[i].GetString() ?? "" : times[i].ToString();
                if (!DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    continue;
                if (!wantHours.Contains(dt.Hour))
                    continue;

                var ht = ArrayDoubleAt(temps, i, tempC);
                var hr = (int)Math.Round(ArrayDoubleAt(probs, i, chanceRain));
                var hc = (int)Math.Round(ArrayDoubleAt(codes, i, code));
                if (uvs.ValueKind == JsonValueKind.Array && i < uvs.GetArrayLength())
                {
                    var u = ArrayDoubleAt(uvs, i, double.NaN);
                    if (!double.IsNaN(u) && (uvMax is null || u > uvMax))
                        uvMax = u;
                }

                var label = dt.Hour switch
                {
                    9 => "上午",
                    12 => "中午",
                    15 => "下午",
                    18 => "傍晚",
                    _ => $"{dt.Hour}点",
                };
                hourlyHints.Add($"{label}约{ht:0.#}°C {DescribeWmo(hc)}（降水概率{hr}%）");
                if (hourlyHints.Count >= 4)
                    break;
            }
        }

        // 响应根上的 latitude/longitude 是接口实际用于网格的坐标
        var apiLat = ReadDoubleNullable(root, "latitude");
        var apiLon = ReadDoubleNullable(root, "longitude");
        var elev = ReadDoubleNullable(root, "elevation");
        var tz = ReadString(root, "timezone");
        if (string.IsNullOrWhiteSpace(tz))
            tz = null;

        double? lat = preferredLat ?? apiLat;
        double? lon = preferredLon ?? apiLon;

        var csrc = !string.IsNullOrWhiteSpace(coordSource)
            ? coordSource!
            : preferredLat is not null && preferredLon is not null
                ? "请求使用的经纬度（地理编码或 IP 定位传入）"
                : "Open-Meteo 响应根字段 latitude/longitude";

        // 若请求坐标与响应网格坐标有偏差，在来源里注明
        if (preferredLat is double pl && preferredLon is double po
            && apiLat is double al && apiLon is double ao
            && (Math.Abs(pl - al) > 0.01 || Math.Abs(po - ao) > 0.01))
        {
            csrc =
                $"请求坐标 {FormatCoord(pl)},{FormatCoord(po)} → 接口网格 {FormatCoord(al)},{FormatCoord(ao)}";
            // 播报以接口实际网格点为准更诚实
            lat = al;
            lon = ao;
        }

        var place = !string.IsNullOrWhiteSpace(preferredPlace)
            ? preferredPlace!
            : lat is double la2 && lon is double lo2
                ? $"坐标点 纬度{FormatCoord(la2)} 经度{FormatCoord(lo2)}"
                : "未知地点";

        return new WeatherSnapshot
        {
            Place = place,
            Latitude = lat,
            Longitude = lon,
            ElevationM = elev,
            Timezone = tz,
            CoordSource = csrc,
            Source = "Open-Meteo（免费主源，无密钥）",
            Description = desc,
            TempC = tempC,
            FeelsC = feelsC,
            Humidity = humidity,
            WindKmh = wind,
            PrecipNowMm = precipNow,
            MaxC = maxC,
            MinC = minC,
            PrecipDayMm = precipDay,
            ChanceRainPct = chanceRain,
            UvIndexMax = uvMax,
            WeatherCode = code,
            HourlyHints = hourlyHints,
        };
    }

    public static List<string> BuildWeatherAlerts(
        double tempC, double feelsC, double maxC,
        double precipNowMm, double precipDayMm, int chanceRainPct, string desc) =>
        BuildWeatherAlerts(tempC, feelsC, maxC, precipNowMm, precipDayMm, chanceRainPct, desc,
            uvIndexMax: null, windKmh: 0, humidity: 0, minC: tempC);

    public static List<string> BuildWeatherAlerts(
        double tempC, double feelsC, double maxC,
        double precipNowMm, double precipDayMm, int chanceRainPct, string desc,
        double? uvIndexMax, double windKmh, double humidity, double minC)
    {
        var alerts = new List<string>();
        var maxFeel = Math.Max(tempC, Math.Max(feelsC, maxC));

        if (maxFeel >= 40 || maxC >= 40)
            alerts.Add("高温红色关注：气温/体感或今日最高 ≥40°C，避免长时间暴晒，多补水。");
        else if (maxFeel >= 37 || maxC >= 37)
            alerts.Add("高温橙色关注：≥37°C 级别炎热，外出做好防晒与补水。");
        else if (maxFeel >= 35 || maxC >= 35)
            alerts.Add("高温黄色关注：≥35°C，午后尽量少在户外逗留。");

        if (tempC <= 0 || minC <= 0 || (maxC <= 2 && tempC < 5))
            alerts.Add("低温提醒：较冷，注意添衣，别冻着。");

        var rainyWord = (desc ?? "").Contains("雨", StringComparison.Ordinal)
                        || (desc ?? "").Contains("雪", StringComparison.Ordinal)
                        || (desc ?? "").Contains("雷", StringComparison.Ordinal)
                        || (desc ?? "").Contains("阵雨", StringComparison.Ordinal);
        if (precipNowMm >= 5 || precipDayMm >= 15 || chanceRainPct >= 70 || rainyWord && chanceRainPct >= 40)
            alerts.Add("降水提醒：有一定降水可能/正在降水，出门建议带伞，路面可能湿滑。");
        else if (chanceRainPct >= 40 || precipDayMm >= 3)
            alerts.Add("零星降水可能：概率不低，包里备把伞更安心。");

        if ((desc ?? "").Contains("雷", StringComparison.Ordinal) || (desc ?? "").Contains("雷暴", StringComparison.Ordinal))
            alerts.Add("雷电提醒：注意安全，尽量避开空旷与高大金属物。");
        if ((desc ?? "").Contains("雾", StringComparison.Ordinal) || (desc ?? "").Contains("霾", StringComparison.Ordinal))
            alerts.Add("能见度提醒：雾/霾天气，出行注意交通安全。");
        if ((desc ?? "").Contains("雪", StringComparison.Ordinal))
            alerts.Add("降雪提醒：路滑，出行放慢脚步，注意保暖。");

        if (uvIndexMax is >= 8)
            alerts.Add("强紫外线：今日 UV 很高，外出务必防晒（帽/伞/防晒霜）。");
        else if (uvIndexMax is >= 6)
            alerts.Add("紫外线偏强：午间外出建议防晒。");

        if (windKmh >= 50)
            alerts.Add("大风提醒：风力较强，注意高空坠物，骑行/驾车小心。");
        else if (windKmh >= 35)
            alerts.Add("风力偏大：外出注意防风，收好阳台物品。");

        if (humidity >= 85 && maxFeel >= 30)
            alerts.Add("闷热提醒：高湿+高温，体感更难受，注意通风降温。");

        return alerts;
    }

    public static List<string> BuildCareTips(WeatherSnapshot snap)
    {
        var tips = new List<string>();
        var maxFeel = Math.Max(snap.TempC, Math.Max(snap.FeelsC, snap.MaxC));
        var swing = snap.MaxC - snap.MinC;
        var desc = snap.Description ?? "";

        if (maxFeel >= 35)
            tips.Add("穿衣：浅色透气短袖就好，别硬撑外套；太阳大时戴帽子更舒服。");
        else if (maxFeel >= 28)
            tips.Add("穿衣：短袖轻装最合适，办公室若开冷气可备一件薄外套。");
        else if (snap.TempC <= 5 || snap.MinC <= 3)
            tips.Add("穿衣：今天偏冷，多穿一层，围巾/手套按需要带上。");
        else if (snap.TempC <= 12)
            tips.Add("穿衣：有点凉，薄外套或卫衣更稳妥，别冻到肚子。");
        else if (swing >= 10)
            tips.Add($"穿衣：昼夜温差约 {swing:0.#}°C，早晚凉、中午热，外套方便穿脱最好。");
        else if (snap.TempC is >= 18 and <= 26)
            tips.Add("穿衣：温度舒适，按自己习惯穿就行，不用太折腾。");

        if (maxFeel >= 35)
            tips.Add("补水：记得多喝水，别等口渴才喝；尽量少在午后暴晒下久站。");
        else if (maxFeel >= 30)
            tips.Add("补水：天热容易出汗，桌上放杯水，定时抿几口。");

        if (snap.PrecipNowMm >= 1 || snap.ChanceRainPct >= 60 || desc.Contains("雨", StringComparison.Ordinal))
            tips.Add("出行：带伞更安心，鞋底防滑一点；到家后擦干脚，别着凉。");
        else if (snap.ChanceRainPct >= 40)
            tips.Add("出行：有下雨可能，包里塞把折叠伞，心里更踏实。");
        if (desc.Contains("雪", StringComparison.Ordinal))
            tips.Add("出行：下雪路滑，出门放慢脚步，手套暖暖的。");

        if (snap.UvIndexMax is >= 8)
            tips.Add("防晒：紫外线很强，防晒霜+帽子/遮阳伞，别晒伤。");
        else if (snap.UvIndexMax is >= 6)
            tips.Add("防晒：UV 不低，露胳膊时抹点防晒更安心。");

        if (snap.WindKmh >= 40)
            tips.Add("防风：风大时注意帽子与骑行，阳台小物件收好。");

        if (snap.Humidity >= 80 && maxFeel >= 28)
            tips.Add("体感：又湿又热容易闷，室内开点通风或空调。");
        else if (snap.Humidity <= 30 && snap.TempC >= 20)
            tips.Add("体感：空气偏干，喝水润润嗓子。");

        if (desc.Contains("雷", StringComparison.Ordinal))
            tips.Add("安全：有雷电时尽量待室内，别在树下避雨。");
        if (desc.Contains("雾", StringComparison.Ordinal) || desc.Contains("霾", StringComparison.Ordinal))
            tips.Add("呼吸：能见度/空气一般时，敏感的话可少户外慢跑。");

        if (tips.Count == 0)
            tips.Add("今天天气还算温柔，适合按计划出门；累了就回家歇会儿，我在。");
        else if (tips.Count < 3 && maxFeel is >= 18 and <= 28 && snap.ChanceRainPct < 30)
            tips.Add("顺便说一句：天气不错的时候，记得出门透透气，也别忘了按时吃饭哦。");

        return tips.Take(5).ToList();
    }

    public static string DescribeWmo(int code) => code switch
    {
        0 => "晴",
        1 => "大部晴朗",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻毛毛雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 or 67 => "冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "雪粒",
        80 => "阵雨",
        81 => "中等阵雨",
        82 => "强阵雨",
        85 => "阵雪",
        86 => "强阵雪",
        95 => "雷暴",
        96 or 99 => "雷暴伴冰雹",
        _ => $"天气码{code}",
    };

    public static string UvLevelText(double uv) => uv switch
    {
        >= 11 => "极强",
        >= 8 => "很强",
        >= 6 => "强",
        >= 3 => "中等",
        _ => "弱",
    };

    /// <summary>十进制坐标固定用点号，避免中文环境逗号分隔。</summary>
    public static string FormatCoord(double v) =>
        v.ToString("0.#####", CultureInfo.InvariantCulture);

    // -------------------- JSON 安全读取（数字可能是 Number 或 String）--------------------

    private static bool TryGetArrayItem(JsonElement parent, string name, int index, out JsonElement item)
    {
        item = default;
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return false;
        if (index < 0 || index >= arr.GetArrayLength())
            return false;
        item = arr[index];
        return true;
    }

    /// <summary>读取 wttr 风格 nested: field[0].value</summary>
    private static string GetNestedValue(JsonElement parent, string arrayName)
    {
        try
        {
            if (!parent.TryGetProperty(arrayName, out var arr))
                return "";
            if (arr.ValueKind == JsonValueKind.String)
                return arr.GetString() ?? "";
            if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var first = arr[0];
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("value", out var v))
                    return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
                if (first.ValueKind == JsonValueKind.String)
                    return first.GetString() ?? "";
            }
        }
        catch
        {
            // ignore
        }

        return "";
    }

    private static string ReadString(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p))
            return "";
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? "",
            JsonValueKind.Number => p.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => p.ToString(),
        };
    }

    private static double ReadDouble(JsonElement e, string name, double fallback = 0)
    {
        var n = ReadDoubleNullable(e, name);
        return n ?? fallback;
    }

    private static double? ReadDoubleNullable(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDouble(),
            JsonValueKind.String when double.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            JsonValueKind.String when double.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.CurrentCulture, out var d2) => d2,
            _ => null,
        };
    }

    private static double FirstArrayDouble(JsonElement daily, string name, double fallback)
    {
        if (!daily.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return fallback;
        return ArrayDoubleAt(arr, 0, fallback);
    }

    private static double ArrayDoubleAt(JsonElement arr, int index, double fallback)
    {
        if (arr.ValueKind != JsonValueKind.Array || index < 0 || index >= arr.GetArrayLength())
            return fallback;
        var el = arr[index];
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => fallback,
        };
    }

    private static string NullSafe(string? s, string fallback) =>
        string.IsNullOrWhiteSpace(s) ? fallback : s;

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
