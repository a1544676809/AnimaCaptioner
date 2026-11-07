using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AnimaCaptioner.Core;

/// <summary>
/// 一次请求里的一个归一化裁剪框（0~1）。来自 AnimaCaptioner 升级包的
/// <c>ModeCaptioningKit</c>，这里剥掉对 <c>CaptionMode</c> 的依赖单独成文件——
/// 多区域放大和"打标模式"没有关系，不该为了用它把整套模式框架也拖进来。
/// </summary>
public sealed record RegionBox(double X, double Y, double W, double H, string Label = "");

/// <summary>`view_regions` 解析结果。<see cref="Problem"/> 为空 = 可用。</summary>
public sealed record MultiRegionRequest(IReadOnlyList<RegionBox> Regions, string Problem)
{
    public bool Ok => Problem.Length == 0 && Regions.Count > 0;
}

/// <summary>
/// 多区域放大的参数解析。<c>view_regions</c> 允许模型**一轮**要最多 4 块放大图，
/// 省掉"裁一块 → 等一轮 → 再裁一块"的串行往返（对应 AnimaAIProxy 的批量视觉思路）。
/// </summary>
public static class MultiRegion
{
    /// <summary>一次最多几块（再多，图片 token 会顶掉预算，而收益递减）。</summary>
    public const int MaxRegions = 4;

    /// <summary>
    /// 解析 `{"regions":[{"x":..,"y":..,"w":..,"h":..,"label":".."}]}`。
    ///
    /// 校验刻意严：宽或高 ≤ 0.01 直接判错——那要么是模型算错了，要么是想"看整张"，
    /// 而整张图本来就已经在对话里了，再裁一次纯浪费 token。
    /// </summary>
    public static MultiRegionRequest Parse(string? jsonArguments, int maxRegions = MaxRegions)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(jsonArguments) ? "{}" : jsonArguments) as JsonObject;
            var arr = node?["regions"] as JsonArray;
            if (arr is null || arr.Count == 0)
                return new MultiRegionRequest(Array.Empty<RegionBox>(), "Missing or empty 'regions' array.");

            var boxes = new List<RegionBox>(Math.Min(arr.Count, maxRegions));
            foreach (var item in arr.Take(maxRegions))
            {
                if (item is not JsonObject obj) continue;

                double x = Num(obj, "x"), y = Num(obj, "y"), w = Num(obj, "w"), h = Num(obj, "h");
                var label = obj["label"]?.GetValue<string>() ?? "";

                if (w <= 0.01 || h <= 0.01)
                    return new MultiRegionRequest(Array.Empty<RegionBox>(),
                        "Region width and height must be > 0.01 (a full-image region is pointless — " +
                        "the whole image is already attached).");

                boxes.Add(new RegionBox(
                    X: Math.Clamp(x, 0.0, 0.99),
                    Y: Math.Clamp(y, 0.0, 0.99),
                    W: Math.Clamp(w, 0.01, 1.0),
                    H: Math.Clamp(h, 0.01, 1.0),
                    Label: label));
            }

            return new MultiRegionRequest(boxes, string.Empty);
        }
        catch (Exception ex)
        {
            return new MultiRegionRequest(Array.Empty<RegionBox>(), $"Invalid view_regions JSON: {ex.Message}");
        }
    }

    private static double Num(JsonObject o, string key)
    {
        try { return o[key]?.GetValue<double>() ?? 0.0; }
        catch { return 0.0; }
    }
}
