#nullable enable
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;
using ZXing.Common;
using LabelApi.Models;

namespace LabelApi.Services
{
    public class LabelRenderingEngine
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        public static int MmToDot(double mm, int dpi)
            => (int)Math.Round(mm / 25.4 * dpi);

        private static int DpiToDpmm(int dpi) => dpi switch
        {
            300 => 12, 600 => 24, _ => 8
        };

        private static string ZplRot(int rotation) => rotation switch
        {
            90 => "R", 180 => "I", 270 => "B", _ => "N"
        };

        private static string ResolveFont(string? css) => (css ?? "") switch
        {
            "sans-serif"                                => "Malgun Gothic",
            "'Malgun Gothic', 맑은고딕, sans-serif"     => "Malgun Gothic",
            "'Gulim', 굴림, sans-serif"                 => "Gulim",
            "'Arial', sans-serif"                       => "Arial",
            "'Times New Roman', serif"                  => "Times New Roman",
            _                                           => "Malgun Gothic"
        };

        // ====================================================
        // RAW ZPL 생성
        // ====================================================
        public string GenerateRawZpl(LabelTemplateDto template)
        {
            if (template?.Config == null) return "^XA\n^XZ";

            int dpi = template.Config.Dpi <= 0 ? 203 : template.Config.Dpi;

            var zpl = new StringBuilder();
            zpl.AppendLine("^XA");
            zpl.AppendLine("^CI28");
            zpl.AppendLine($"^PW{MmToDot(template.Config.WidthMm, dpi)}");
            zpl.AppendLine($"^LL{MmToDot(template.Config.HeightMm, dpi)}");
            zpl.AppendLine("^LS0");

            foreach (var el in template.Elements ?? new())
            {
                int    xDot = MmToDot(el.XMm, dpi);
                int    yDot = MmToDot(el.YMm, dpi);
                string rot  = ZplRot(el.Rotation);
                string txt  = el.IsVariable && !string.IsNullOrEmpty(el.FieldName)
                    ? $"{{{{{el.FieldName}}}}}"
                    : (el.SampleValue ?? "");

                switch (el.Type)
                {
                    case "barcode":
                    {
                        // barcodeDot: QR/DM=scale(1~10), Code128=높이(dot)
                        int barcodeDot  = el.BarcodeDot ?? (el.BarcodeType == "QR" || el.BarcodeType == "DataMatrix" ? 4 : 72);
                        int moduleWidth = el.ModuleWidth ?? 2;
                        string show     = (el.ShowText ?? true) ? "Y" : "N";

                        if (el.BarcodeType == "QR")
                        {
                            // scale 안전 범위: 1~10
                            int scale = Math.Max(1, Math.Min(10, barcodeDot));
                            zpl.AppendLine($"^FO{xDot},{yDot}^BQ{rot},2,{scale}^FDQA,{txt}^FS");
                        }
                        else if (el.BarcodeType == "DataMatrix")
                        {
                            int scale = Math.Max(1, Math.Min(10, barcodeDot));
                            zpl.AppendLine($"^FO{xDot},{yDot}^BX{rot},{scale},200^FD{txt}^FS");
                        }
                        else // Code128
                        {
                            int heightDot = Math.Max(10, barcodeDot);
                            zpl.AppendLine($"^FO{xDot},{yDot}^BY{moduleWidth}^BC{rot},{heightDot},{show},N,N^FD{txt}^FS");
                        }
                        break;
                    }

                    case "rect":
                    {
                        int wDot = MmToDot(el.WidthMm,  dpi);
                        int hDot = MmToDot(el.HeightMm, dpi);
                        int fw   = (el.Rotation == 90 || el.Rotation == 270) ? hDot : wDot;
                        int fh   = (el.Rotation == 90 || el.Rotation == 270) ? wDot : hDot;
                        int sw   = Math.Max(1, el.StrokeDot ?? 1);
                        zpl.AppendLine($"^FO{xDot},{yDot}^GB{fw},{fh},{sw}^FS");
                        break;
                    }

                    case "line":
                    {
                        int sw = Math.Max(1, el.StrokeDot ?? 1);
                        if (el.SubType == "vertical")
                        {
                            int hDot = MmToDot(el.HeightMm, dpi);
                            zpl.AppendLine($"^FO{xDot},{yDot}^GB{sw},{hDot},{sw}^FS");
                        }
                        else
                        {
                            int wDot = MmToDot(el.WidthMm, dpi);
                            zpl.AppendLine($"^FO{xDot},{yDot}^GB{wDot},{sw},{sw}^FS");
                        }
                        break;
                    }

                    case "text":
                    {
                        int fontDot = Math.Max(10, el.FontSizeDot ?? 40);
                        if (el.IsVariable)
                        {
                            int    wDot = MmToDot(el.WidthMm, dpi);
                            string al   = el.Align == "center" ? "C" : el.Align == "right" ? "R" : "L";
                            zpl.AppendLine($"^FO{xDot},{yDot}^A0{rot},{fontDot},{fontDot}^FB{wDot},1,0,{al},0^FD{txt}^FS");
                        }
                        else
                        {
                            string gfa = BakeTextToGfa(el, txt, dpi);
                            if (!string.IsNullOrEmpty(gfa))
                                zpl.AppendLine(gfa);
                        }
                        break;
                    }
                }
            }

            zpl.AppendLine("^XZ");
            return zpl.ToString();
        }

        // ====================================================
        // PNG 미리보기 - Labelary API 호출
        // 실패 시 ZXing + SkiaSharp 폴백
        // ====================================================
        public string GeneratePreviewPngBase64(LabelTemplateDto template)
            => GeneratePreviewPngBase64Async(template).GetAwaiter().GetResult();

        public async Task<string> GeneratePreviewPngBase64Async(LabelTemplateDto template)
        {
            if (template?.Config == null) return "";

            int    dpi        = template.Config.Dpi <= 0 ? 203 : template.Config.Dpi;
            string rawZpl     = GenerateRawZpl(template);
            int    dpmm       = DpiToDpmm(dpi);
            double widthInch  = Math.Round(template.Config.WidthMm  / 25.4, 3);
            double heightInch = Math.Round(template.Config.HeightMm / 25.4, 3);
            string apiUrl     = $"http://api.labelary.com/v1/printers/{dpmm}dpmm/labels/{widthInch}x{heightInch}/0/";

            try
            {
                var content  = new FormUrlEncodedContent(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, string>("data", rawZpl)
                });
                var response = await _http.PostAsync(apiUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    byte[] png = await response.Content.ReadAsByteArrayAsync();
                    Console.WriteLine("[Labelary] PNG 생성 성공");
                    return Convert.ToBase64String(png);
                }

                Console.WriteLine($"[Labelary Error] {response.StatusCode} → SkiaSharp 폴백");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Labelary 연결 실패] {ex.Message} → SkiaSharp 폴백");
            }

            // Labelary 실패 시 SkiaSharp 폴백 (ZXing으로 바코드 정상 렌더링)
            return FallbackPng(template);
        }

        // ====================================================
        // SkiaSharp 폴백 - ZXing으로 바코드 정상 렌더링
        // ====================================================
        private string FallbackPng(LabelTemplateDto template)
        {
            if (template?.Config == null) return "";

            int dpi  = template.Config.Dpi <= 0 ? 203 : template.Config.Dpi;
            int wDot = MmToDot(template.Config.WidthMm,  dpi);
            int hDot = MmToDot(template.Config.HeightMm, dpi);

            Console.WriteLine($"[SkiaSharp Fallback] {wDot}x{hDot}");

            using var bitmap = new SKBitmap(wDot, hDot, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            foreach (var el in template.Elements ?? new())
            {
                int    xDot = MmToDot(el.XMm, dpi);
                int    yDot = MmToDot(el.YMm, dpi);
                string txt  = el.IsVariable && !string.IsNullOrEmpty(el.FieldName)
                    ? $"[{el.FieldName}]"
                    : (el.SampleValue ?? "");

                canvas.Save();
                canvas.Translate(xDot, yDot);
                canvas.RotateDegrees(el.Rotation);

                switch (el.Type)
                {
                    case "rect":
                    {
                        int   ewDot = MmToDot(el.WidthMm,  dpi);
                        int   ehDot = MmToDot(el.HeightMm, dpi);
                        float sw    = Math.Max(1f, el.StrokeDot ?? 1);
                        using var p = new SKPaint
                        {
                            Color       = SKColors.Black,
                            Style       = SKPaintStyle.Stroke,
                            StrokeWidth = sw
                        };
                        canvas.DrawRect(0, 0, ewDot, ehDot, p);
                        break;
                    }

                    case "line":
                    {
                        float sw = Math.Max(1f, el.StrokeDot ?? 1);
                        using var p = new SKPaint { Color = SKColors.Black, StrokeWidth = sw };
                        if (el.SubType == "vertical")
                        {
                            int ehDot = MmToDot(el.HeightMm, dpi);
                            canvas.DrawLine(0, 0, 0, ehDot, p);
                        }
                        else
                        {
                            int ewDot = MmToDot(el.WidthMm, dpi);
                            canvas.DrawLine(0, 0, ewDot, 0, p);
                        }
                        break;
                    }

                    case "text":
                    {
                        int    fontDot = Math.Max(10, el.FontSizeDot ?? 40);
                        double widthMm = el.WidthMm > 0 ? el.WidthMm : 50;
                        int    ewDot   = MmToDot(widthMm, dpi);
                        if (fontDot <= 0) break;

                        string resolvedFont = ResolveFont(el.FontFamily);
                        using var typeface  = SKTypeface.FromFamilyName(resolvedFont,
                            el.FontWeight == "bold" ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
                        if (typeface == null) break;

                        using var font  = new SKFont(typeface, fontDot);
                        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                        font.MeasureText(txt, out SKRect tb);
                        float drawX = 0;
                        if (el.Align == "center") drawX = (ewDot - tb.Width) / 2;
                        else if (el.Align == "right") drawX = ewDot - tb.Width;
                        canvas.DrawText(txt, drawX, Math.Abs(tb.Top), font, paint);
                        break;
                    }

                    case "barcode":
                    {
                        // ★ ZXing으로 실제 바코드/QR/DataMatrix 렌더링
                        int barcodeDot = el.BarcodeDot ?? (el.BarcodeType == "QR" || el.BarcodeType == "DataMatrix" ? 4 : 72);
                        string barcodeText = string.IsNullOrEmpty(txt) ? "12345" : txt;

                        try
                        {
                            if (el.BarcodeType == "QR")
                            {
                                // QR: scale → 픽셀 크기 계산
                                int scale   = Math.Max(1, Math.Min(10, barcodeDot));
                                int sizeDot = scale * 25; // 대략적인 크기
                                var writer  = new BarcodeWriter
                                {
                                    Format  = BarcodeFormat.QR_CODE,
                                    Options = new EncodingOptions
                                    {
                                        Width  = sizeDot,
                                        Height = sizeDot,
                                        Margin = 0
                                    }
                                };
                                using var bcBmp = writer.Write(barcodeText);
                                canvas.DrawBitmap(bcBmp, 0, 0);
                            }
                            else if (el.BarcodeType == "DataMatrix")
                            {
                                int scale   = Math.Max(1, Math.Min(10, barcodeDot));
                                int sizeDot = scale * 25;
                                var writer  = new BarcodeWriter
                                {
                                    Format  = BarcodeFormat.DATA_MATRIX,
                                    Options = new EncodingOptions
                                    {
                                        Width  = sizeDot,
                                        Height = sizeDot,
                                        Margin = 0
                                    }
                                };
                                using var bcBmp = writer.Write(barcodeText);
                                canvas.DrawBitmap(bcBmp, 0, 0);
                            }
                            else // Code128
                            {
                                // heightDot 기반으로 크기 결정
                                int heightDot = Math.Max(10, barcodeDot);
                                int widthDot  = MmToDot(el.WidthMm > 0 ? el.WidthMm : 40, dpi);
                                var writer    = new BarcodeWriter
                                {
                                    Format  = BarcodeFormat.CODE_128,
                                    Options = new EncodingOptions
                                    {
                                        Width       = widthDot,
                                        Height      = heightDot,
                                        Margin      = 0,
                                        PureBarcode = !(el.ShowText ?? true)
                                    }
                                };
                                using var bcBmp = writer.Write(barcodeText);
                                canvas.DrawBitmap(bcBmp, 0, 0);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Fallback Barcode Error] {ex.Message}");
                            // 바코드 생성 실패 시 텍스트로 대체
                            using var font  = new SKFont(SKTypeface.Default, 16);
                            using var paint = new SKPaint { Color = SKColors.Gray };
                            canvas.DrawText($"[{el.BarcodeType}]", 0, 20, font, paint);
                        }
                        break;
                    }
                }

                canvas.Restore();
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
            return Convert.ToBase64String(data.ToArray());
        }

        // ====================================================
        // 텍스트 → GFA (fontSizeDot 직접 사용)
        // ====================================================
        private string BakeTextToGfa(LabelElement el, string text, int dpi)
        {
            if (string.IsNullOrEmpty(text)) return "";

            int    fontDot  = Math.Max(10, el.FontSizeDot ?? 40);
            double widthMm  = el.WidthMm  > 0 ? el.WidthMm  : 50;
            double heightMm = el.HeightMm > 0 ? el.HeightMm : fontDot / (double)dpi * 25.4 * 1.4;

            int wDot = MmToDot(widthMm,  dpi);
            int hDot = MmToDot(heightMm, dpi);
            int xDot = MmToDot(el.XMm,   dpi);
            int yDot = MmToDot(el.YMm,   dpi);

            if (wDot <= 0 || hDot <= 0 || fontDot <= 0) return "";

            string resolvedFont = ResolveFont(el.FontFamily);
            using var typeface  = SKTypeface.FromFamilyName(resolvedFont,
                el.FontWeight == "bold" ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

            if (typeface == null) return "";

            using var bitmap = new SKBitmap(wDot, hDot, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            using var font  = new SKFont(typeface, fontDot);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

            font.MeasureText(text, out SKRect tb);
            float drawX = 0;
            if (el.Align == "center") drawX = (wDot - tb.Width) / 2;
            else if (el.Align == "right") drawX = wDot - tb.Width;
            canvas.DrawText(text, drawX, Math.Abs(tb.Top), font, paint);

            int bytesPerRow = (wDot + 7) / 8;
            int byteCount   = bytesPerRow * hDot;
            var hex         = new StringBuilder(byteCount * 2);

            for (int y = 0; y < hDot; y++)
            {
                for (int x = 0; x < bytesPerRow * 8; x += 8)
                {
                    byte b = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int px = x + bit;
                        if (px < wDot)
                        {
                            var c = bitmap.GetPixel(px, y);
                            if (c.Alpha > 128 && (c.Red * 0.299 + c.Green * 0.587 + c.Blue * 0.114) < 128)
                                b |= (byte)(1 << (7 - bit));
                        }
                    }
                    hex.Append(b.ToString("X2"));
                }
                hex.AppendLine();
            }

            if      (el.Rotation == 90)  { xDot -= hDot; }
            else if (el.Rotation == 180) { xDot -= wDot; yDot -= hDot; }
            else if (el.Rotation == 270) { yDot -= wDot; }

            return $"^FO{xDot},{yDot}^GFA,{byteCount},{byteCount},{bytesPerRow},{hex}";
        }
    }
}