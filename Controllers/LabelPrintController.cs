using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using LabelApi.Models;
using LabelApi.Services;

namespace LabelApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LabelPrintController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly LabelRenderingEngine _renderingEngine;
        private readonly LabelDesignerController _designerCtrl; // 토큰 복호화 재사용

        public LabelPrintController(IConfiguration config)
        {
            _config = config;
            _renderingEngine = new LabelRenderingEngine();
            // DecryptMesToken은 LabelDesignerController에 private으로 있으므로
            // 실제 프로젝트에서는 ITokenService로 분리 권장
            // 여기서는 MVP 단계이므로 동일 로직을 인라인으로 구현
        }

        // ============================================================
        // [POST] 미리보기 생성 (변수 치환 후 PNG 반환)
        //
        // Request body 예시:
        // {
        //   "token": "...",
        //   "templateId": "uuid-...",
        //   "printData": {
        //     "LOT_NO": "LOT-2024-001",
        //     "ITEM_NAME": "삼성 베트남 수출용",
        //     "QTY": "100"
        //   }
        // }
        // ============================================================
        [HttpPost("Preview")]
        public async Task<IActionResult> Preview([FromBody] PrintPreviewRequest request)
        {
            try
            {
                // ── 1. 토큰 검증 ──────────────────────────────────────
                if (string.IsNullOrWhiteSpace(request.Token))
                    return Unauthorized(new { success = false, message = "토큰이 없습니다." });

                var (siteCd, userId, _) = DecryptMesToken(request.Token);

                if (request.TemplateId == Guid.Empty)
                    return BadRequest(new { success = false, message = "templateId가 없습니다." });

                // ── 2. DB에서 TEMPLATE_JSON 로드 ──────────────────────
                string connStr = _config.GetConnectionString("SupabaseConnection");
                using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync();

                const string sql = @"
                    SELECT d.TEMPLATE_JSON
                    FROM ZPL_LABEL_TEMPLATE_D d
                    JOIN ZPL_LABEL_TEMPLATE_M m ON m.TEMPLATE_ID = d.TEMPLATE_ID
                    WHERE d.TEMPLATE_ID = @TemplateId
                      AND m.SITE_CD    = @SiteCd";

                LabelTemplateDto? template = null;

                using (var cmd = new NpgsqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@TemplateId", request.TemplateId);
                    cmd.Parameters.AddWithValue("@SiteCd",     siteCd);

                    var raw = await cmd.ExecuteScalarAsync();
                    if (raw == null)
                        return NotFound(new { success = false, message = "템플릿을 찾을 수 없습니다." });

                    var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    template = JsonSerializer.Deserialize<LabelTemplateDto>(raw.ToString()!, jsonOpts);
                }

                if (template == null)
                    return BadRequest(new { success = false, message = "템플릿 JSON 파싱 실패" });

                // ── 3. 변수값 주입 (핵심) ─────────────────────────────
                // TEMPLATE_JSON의 각 element를 순회하면서
                // isVariable: true 인 element의 sampleValue를 실제 printData 값으로 교체
                // 이후 기존 LabelRenderingEngine이 그대로 처리 (GFA 자동 생성 포함)
                var printData = request.PrintData ?? new Dictionary<string, string>();

                foreach (var el in template.Elements)
                {
                    if (!el.IsVariable || string.IsNullOrWhiteSpace(el.FieldName))
                        continue;

                    if (printData.TryGetValue(el.FieldName, out string? actualValue))
                    {
                        // sampleValue 자리에 실제값을 넣으면
                        // 엔진이 이것을 최종 출력값으로 사용
                        el.SampleValue = actualValue;
                    }
                    // printData에 해당 키가 없으면 sampleValue(기본값) 유지
                }

                // ── 4. 기존 엔진 재실행 (변경 없음) ──────────────────
                string zpl        = _renderingEngine.GenerateRawZpl(template);
                string pngBase64  = _renderingEngine.GeneratePreviewPngBase64(template);

                // ── 5. 응답 ───────────────────────────────────────────
                return Ok(new
                {
                    success    = true,
                    zpl        = zpl,
                    pngPreview = "data:image/png;base64," + pngBase64,
                    // 어떤 변수가 치환됐는지 디버깅용으로 반환
                    resolvedFields = template.Elements
                        .Where(e => e.IsVariable && !string.IsNullOrWhiteSpace(e.FieldName))
                        .Select(e => new { field = e.FieldName, value = e.SampleValue })
                        .ToList()
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PRINT PREVIEW ERROR] {ex.Message}");
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // ============================================================
        // [GET] 특정 템플릿의 변수 필드 목록 조회
        // 프론트에서 "어떤 값을 입력해야 하나?" 확인용
        // ============================================================
        [HttpGet("Fields")]
        public async Task<IActionResult> GetFields(
            [FromQuery] string token,
            [FromQuery] Guid templateId)
        {
            try
            {
                var (siteCd, _, _) = DecryptMesToken(token);

                string connStr = _config.GetConnectionString("SupabaseConnection");
                using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync();

                const string sql = @"
                    SELECT f.FIELD_NAME, f.FIELD_LABEL, f.SAMPLE_VALUE, f.DATA_TYPE, f.SORT_NO
                    FROM ZPL_LABEL_FIELD_M f
                    JOIN ZPL_LABEL_TEMPLATE_M m ON m.TEMPLATE_ID = f.TEMPLATE_ID
                    WHERE f.TEMPLATE_ID = @TemplateId
                      AND m.SITE_CD    = @SiteCd
                    ORDER BY f.SORT_NO";

                var fields = new List<object>();
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@TemplateId", templateId);
                cmd.Parameters.AddWithValue("@SiteCd",     siteCd);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    fields.Add(new
                    {
                        fieldName   = reader.GetString(0),
                        fieldLabel  = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
                        sampleValue = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        dataType    = reader.GetString(3),
                        sortNo      = reader.GetInt32(4)
                    });
                }

                return Ok(new { success = true, fields = fields });
            }
            catch (Exception ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // ============================================================
        // 토큰 복호화 (LabelDesignerController와 동일 로직)
        // TODO: 추후 ITokenService 인터페이스로 분리하여 DI 처리 권장
        // ============================================================
        private (string SiteCd, string UserId, string IsAdmin) DecryptMesToken(string encryptedToken)
        {
            if (encryptedToken == "TEST_TOKEN" || string.IsNullOrEmpty(encryptedToken))
                return ("INFOSOLUTION", "15197", "Y");

            try
            {
                string base64 = encryptedToken.Replace("-", "+").Replace("/", "_");
                switch (base64.Length % 4)
                {
                    case 2: base64 += "=="; break;
                    case 3: base64 += "=";  break;
                }

                string aesKey = _config["SecuritySettings:LabelAesKey"]!;
                string aesIv  = _config["SecuritySettings:LabelAesIv"]!;

                byte[] keyBytes    = System.Text.Encoding.UTF8.GetBytes(aesKey);
                byte[] ivBytes     = System.Text.Encoding.UTF8.GetBytes(aesIv);
                byte[] cipherBytes = Convert.FromBase64String(base64);

                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key     = keyBytes;
                aes.IV      = ivBytes;
                aes.Mode    = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                using var ms = new System.IO.MemoryStream(cipherBytes);
                using var cs = new System.Security.Cryptography.CryptoStream(
                    ms, decryptor, System.Security.Cryptography.CryptoStreamMode.Read);
                using var sr = new System.IO.StreamReader(cs);

                string[] parts = sr.ReadToEnd().Split('|');
                return (parts[0], parts[1], parts[3]);
            }
            catch
            {
                throw new Exception("유효하지 않거나 만료된 토큰입니다.");
            }
        }
    }
}