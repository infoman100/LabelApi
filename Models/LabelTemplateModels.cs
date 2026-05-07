#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LabelApi.Models
{
    public class LabelTemplateDto
    {
        [JsonPropertyName("config")]
        public LabelConfig Config { get; set; } = new LabelConfig();

        [JsonPropertyName("elements")]
        public List<LabelElement> Elements { get; set; } = new List<LabelElement>();
    }

    public class LabelConfig
    {
        [JsonPropertyName("widthMm")]
        public double WidthMm { get; set; }

        [JsonPropertyName("heightMm")]
        public double HeightMm { get; set; }

        [JsonPropertyName("dpi")]
        public int Dpi { get; set; } = 203;
    }

    public class LabelElement
    {
        // ── 공통 ──────────────────────────────────────────────
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
        // "text" | "barcode" | "rect" | "line" | "image"
        // line: subType으로 "horizontal" | "vertical" 구분

        [JsonPropertyName("subType")]
        public string? SubType { get; set; }  // 실선 방향: "horizontal" | "vertical"

        // 위치/크기는 mm 단위 (배치 기준)
        [JsonPropertyName("xMm")]
        public double XMm { get; set; }

        [JsonPropertyName("yMm")]
        public double YMm { get; set; }

        [JsonPropertyName("widthMm")]
        public double WidthMm { get; set; }

        [JsonPropertyName("heightMm")]
        public double HeightMm { get; set; }

        [JsonPropertyName("rotation")]
        public int Rotation { get; set; }

        // ── 변수/데이터 바인딩 ────────────────────────────────
        [JsonPropertyName("isVariable")]
        public bool IsVariable { get; set; }

        [JsonPropertyName("fieldName")]
        public string? FieldName { get; set; }

        [JsonPropertyName("sampleValue")]
        public string? SampleValue { get; set; }

        // ── 텍스트 전용 ───────────────────────────────────────
        [JsonPropertyName("fontFamily")]
        public string? FontFamily { get; set; }

        /// <summary>폰트 크기 - dot 단위 (ZPL ^A0 직접 사용)</summary>
        [JsonPropertyName("fontSizeDot")]
        public int? FontSizeDot { get; set; }

        [JsonPropertyName("fontWeight")]
        public string? FontWeight { get; set; }

        [JsonPropertyName("align")]
        public string? Align { get; set; }

        [JsonPropertyName("verticalAlign")]
        public string? VerticalAlign { get; set; }

        [JsonPropertyName("wrap")]
        public bool? Wrap { get; set; }

        [JsonPropertyName("overflow")]
        public string? Overflow { get; set; }

        // ── 바코드 전용 ───────────────────────────────────────
        [JsonPropertyName("barcodeType")]
        public string? BarcodeType { get; set; }  // "Code128" | "QR" | "DataMatrix"

        [JsonPropertyName("moduleWidth")]
        public int? ModuleWidth { get; set; }     // Code128 ^BY scale (1~5)

        /// <summary>
        /// QR/DataMatrix scale (1~10, ZPL ^BQ/^BX 직접 사용)
        /// Code128 높이 dot 단위 (ZPL ^BC 직접 사용)
        /// </summary>
        [JsonPropertyName("barcodeDot")]
        public int? BarcodeDot { get; set; }

        [JsonPropertyName("showText")]
        public bool? ShowText { get; set; }

        [JsonPropertyName("ecLevel")]
        public string? EcLevel { get; set; }

        // ── 사각형/실선 전용 ──────────────────────────────────
        /// <summary>선 굵기 - dot 단위</summary>
        [JsonPropertyName("strokeDot")]
        public int? StrokeDot { get; set; }
    }

    // ── API Request DTO ────────────────────────────────────────
    public class SaveTemplateRequest
    {
        public string  Token         { get; set; } = string.Empty;
        public int     SlotIndex     { get; set; } = 1;
        public string? TargetUserId  { get; set; }
        public string? LabelType     { get; set; }
        public string? TemplateName  { get; set; }
        public LabelTemplateDto TemplateData { get; set; } = new LabelTemplateDto();
    }

    public class PrintPreviewRequest
    {
        public string Token      { get; set; } = string.Empty;
        public Guid   TemplateId { get; set; }
        public Dictionary<string, string>? PrintData { get; set; }
    }
}