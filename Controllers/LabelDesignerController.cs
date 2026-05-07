using Microsoft.AspNetCore.Mvc;
using System.Data;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using LabelApi.Models;
using LabelApi.Services;

namespace LabelApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LabelDesignerController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly LabelRenderingEngine _renderingEngine;
        private readonly string _aesKey;
        private readonly string _aesIv;

        public LabelDesignerController(IConfiguration config)
        {
            _config = config;
            _aesKey = _config["SecuritySettings:LabelAesKey"];
            _aesIv  = _config["SecuritySettings:LabelAesIv"];
            _renderingEngine = new LabelRenderingEngine();
        }

        // ============================================================
        // [공통 내부 메서드] DB 연결 생성
        // Oracle 전환 시 이 메서드만 OracleConnection으로 교체하면 됨
        // ============================================================
        private NpgsqlConnection CreateDbConnection()
        {
            string connStr = _config.GetConnectionString("SupabaseConnection");
            return new NpgsqlConnection(connStr);
        }

        // ============================================================
        // [공통 내부 메서드] 관리자 대행 포함 effectiveSiteCd 확정
        // Load / Save 양쪽에서 동일 로직 재사용
        // ============================================================
        private async Task<(string effectiveSiteCd, string effectiveUserId, string newToken)>
            ResolveContextAsync(string token, string targetUserId, NpgsqlConnection conn)
        {
            // 1. 토큰 복호화
            var (siteCd, userId, isAdmin) = DecryptMesToken(token);

            string effectiveSiteCd  = siteCd;
            string effectiveUserId  = userId;

            // 2. 관리자 권한 대행 처리
            if (isAdmin == "Y" && !string.IsNullOrWhiteSpace(targetUserId))
            {
                const string findSiteSql = 
                    "SELECT SITE_CD FROM SC_USER_M WHERE USER_ID = @TargetId";

                using var findCmd = new NpgsqlCommand(findSiteSql, conn);
                findCmd.Parameters.AddWithValue("@TargetId", targetUserId);

                var dbSite = await findCmd.ExecuteScalarAsync();
                if (dbSite == null)
                    throw new Exception($"[{targetUserId}] 사번은 존재하지 않습니다.");

                effectiveUserId = targetUserId;
                effectiveSiteCd = dbSite.ToString()!;
            }

            // 3. 갱신된 워킹 토큰 생성 (만료 시간 연장)
            string now          = DateTime.Now.ToString("yyyyMMddHHmmss");
            string payload      = $"{siteCd}|{userId}|이름|{isAdmin}|{now}";
            string newToken     = EncryptMesToken(payload);

            return (effectiveSiteCd, effectiveUserId, newToken);
        }

        // ============================================================
        // 1. 라벨 데이터 로드 (GET)
        //    - 슬롯 번호(slotIdx 1~5) 기준으로 조회
        // ============================================================
        [HttpGet("Load")]
        public async Task<IActionResult> Load(
            [FromQuery] string token,
            [FromQuery] int    slotIdx,
            [FromQuery] string targetUserId = null)
        {
            try
            {
                if (slotIdx < 1 || slotIdx > 5)
                    return BadRequest(new { success = false, message = "슬롯 번호는 1~5 사이여야 합니다." });

                using var conn = CreateDbConnection();
                await conn.OpenAsync();

                var (effectiveSiteCd, effectiveUserId, newToken) =
                    await ResolveContextAsync(token, targetUserId, conn);

                var (_, originalUserId, isAdmin) = DecryptMesToken(token);

                const string sql = @"
                    SELECT
                        m.TEMPLATE_ID,
                        m.LABEL_TYPE,
                        m.TEMPLATE_NAME,
                        m.LABEL_WIDTH_MM,
                        m.LABEL_HEIGHT_MM,
                        m.DPI,
                        m.STATUS,
                        d.TEMPLATE_JSON,
                        d.RAW_ZPL,
                        d.PREVIEW_PNG_BASE64
                    FROM ZPL_LABEL_TEMPLATE_M m
                    JOIN ZPL_LABEL_TEMPLATE_D d ON d.TEMPLATE_ID = m.TEMPLATE_ID
                    WHERE m.SITE_CD  = @SiteCd
                    AND m.USER_ID  = @UserId
                    AND m.SLOT_IDX = @SlotIdx";

                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@SiteCd",  effectiveSiteCd);
                cmd.Parameters.AddWithValue("@UserId",  effectiveUserId);
                cmd.Parameters.AddWithValue("@SlotIdx", slotIdx);

                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    string templateJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7);

                    // DB에서 꺼낸 DTO를 React가 읽는 형식(x, width, fontSize 등)으로 변환
                    var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var templateDto = JsonSerializer.Deserialize<LabelTemplateDto>(templateJson, jsonOpts);

                    var reactElements = templateDto?.Elements?.Select(el => new Dictionary<string, object?>
                    {
                        ["id"]           = el.Id,
                        ["type"]         = el.Type,
                        ["subType"]      = el.SubType,
                        ["xMm"]          = el.XMm,
                        ["yMm"]          = el.YMm,
                        ["widthMm"]      = el.WidthMm,
                        ["heightMm"]     = el.HeightMm,
                        ["rotation"]     = el.Rotation,
                        ["isVariable"]   = el.IsVariable,
                        ["fieldName"]    = el.FieldName,
                        ["text"]         = el.SampleValue,
                        ["fontFamily"]   = el.FontFamily,
                        ["fontSizeDot"]  = el.FontSizeDot  ?? 40,   // ★ dot 단위
                        ["fontWeight"]   = el.FontWeight,
                        ["align"]        = el.Align ?? "left",
                        ["barcodeType"]  = el.BarcodeType,
                        ["moduleWidth"]  = el.ModuleWidth  ?? 2,
                        ["barcodeDot"]   = el.BarcodeDot   ?? 72,   // ★ dot 단위
                        ["showText"]     = el.ShowText     ?? true,
                        ["ecLevel"]      = el.EcLevel      ?? "M",
                        ["strokeDot"]    = el.StrokeDot    ?? 1,    // ★ dot 단위
                        ["borderVisible"]= true
                    }).ToList();

                    return Ok(new
                    {
                        success  = true,
                        isEmpty  = false,
                        newToken = newToken,
                        userInfo = new { userId = originalUserId, isAdmin = isAdmin },
                        meta = new
                        {
                            templateId   = reader.GetGuid(0).ToString(),
                            labelType    = reader.GetString(1),
                            templateName = reader.GetString(2),
                            status       = reader.GetString(6)
                        },
                        config = new
                        {
                            widthMm  = reader.GetDecimal(3),
                            heightMm = reader.GetDecimal(4),
                            dpi      = reader.GetInt32(5)
                        },
                        elements   = reactElements,
                        rawZpl     = reader.IsDBNull(8)  ? null : reader.GetString(8),
                        pngPreview = reader.IsDBNull(9)  ? null : reader.GetString(9)
                    });
                }

                return Ok(new
                {
                    success  = true,
                    isEmpty  = true,
                    newToken = newToken,
                    userInfo = new { userId = originalUserId, isAdmin = isAdmin }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOAD ERROR] {ex.Message}");
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // ============================================================
        // 2. 라벨 템플릿 저장 (POST)
        //    핵심 변경:
        //    - DecryptMesToken 복구 (하드코딩 제거)
        //    - 슬롯 기준 UPSERT (있으면 UPDATE, 없으면 INSERT)
        //    - PNG Base64 도 DB에 함께 저장
        // ============================================================
        [HttpPost("Save")]
        public async Task<IActionResult> Save([FromBody] SaveTemplateRequest request)
        {
            // 임시 디버깅용
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(request.TemplateData));

            try
            {
                // ── 보안: 토큰 필수 검증 ──────────────────────────────
                if (string.IsNullOrWhiteSpace(request.Token))
                    return Unauthorized(new { success = false, message = "액세스 토큰이 없습니다." });

                // ── 슬롯 범위 검증 ────────────────────────────────────
                if (request.SlotIndex < 1 || request.SlotIndex > 5)
                    return BadRequest(new { success = false, message = "슬롯 번호는 1~5 사이여야 합니다." });

                using var conn = CreateDbConnection();
                await conn.OpenAsync();

                // ── 토큰 복호화 + 권한 대행 확정 ─────────────────────
                // 기존 코드에서 주석 처리된 부분 복구:
                // // var (siteCd, userId, isAdmin) = DecryptMesToken(request.Token);
                // string effectiveSiteCd = "INFOSOLUTION"; // ← 이 하드코딩 제거
                var (effectiveSiteCd, effectiveUserId, newToken) =
                    await ResolveContextAsync(request.Token, request.TargetUserId, conn);

                var (_, originalUserId, _) = DecryptMesToken(request.Token);

                // ── 렌더링 엔진 실행 (ZPL + PNG 생성) ───────────────
                string rawZpl        = _renderingEngine.GenerateRawZpl(request.TemplateData);
                string previewBase64 = _renderingEngine.GeneratePreviewPngBase64(request.TemplateData);

                // ── JSON 직렬화 (JSONB 저장용) ────────────────────────
                var jsonOptions  = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                string templateJson = JsonSerializer.Serialize(request.TemplateData, jsonOptions);

                // ── LabelType 결정 (요청값 우선, 없으면 기본값) ───────
                string labelType     = string.IsNullOrWhiteSpace(request.LabelType)
                                       ? "BOX_LABEL" : request.LabelType;
                string templateName  = string.IsNullOrWhiteSpace(request.TemplateName)
                                       ? $"슬롯 {request.SlotIndex} 라벨" : request.TemplateName;

                using var tx = await conn.BeginTransactionAsync();
                try
                {
                    // ── UPSERT 전략 ────────────────────────────────────
                    // (SITE_CD, USER_ID, SLOT_IDX) 가 이미 존재하면 UPDATE
                    // 존재하지 않으면 INSERT + TEMPLATE_ID(UUID) 자동 생성
                    // Oracle 전환 시: ON CONFLICT → MERGE INTO 로 교체

                    const string upsertMasterSql = @"
                        INSERT INTO ZPL_LABEL_TEMPLATE_M
                            (SITE_CD, LABEL_TYPE, USER_ID, SLOT_IDX, TEMPLATE_NAME,
                             LABEL_WIDTH_MM, LABEL_HEIGHT_MM, DPI, STATUS, CREATED_BY)
                        VALUES
                            (@SiteCd, @LabelType, @UserId, @SlotIdx, @TemplateName,
                             @Width, @Height, @Dpi, 'DRAFT', @CreatedBy)
                        ON CONFLICT (SITE_CD, USER_ID, SLOT_IDX)
                        DO UPDATE SET
                            LABEL_TYPE      = EXCLUDED.LABEL_TYPE,
                            TEMPLATE_NAME   = EXCLUDED.TEMPLATE_NAME,
                            LABEL_WIDTH_MM  = EXCLUDED.LABEL_WIDTH_MM,
                            LABEL_HEIGHT_MM = EXCLUDED.LABEL_HEIGHT_MM,
                            DPI             = EXCLUDED.DPI,
                            VERSION_NO      = ZPL_LABEL_TEMPLATE_M.VERSION_NO + 1,
                            STATUS          = 'DRAFT',
                            UPDATED_DT      = CURRENT_TIMESTAMP
                        RETURNING TEMPLATE_ID";

                    Guid templateId;
                    using (var cmdM = new NpgsqlCommand(upsertMasterSql, conn, tx))
                    {
                        cmdM.Parameters.AddWithValue("@SiteCd",       effectiveSiteCd);
                        cmdM.Parameters.AddWithValue("@LabelType",     labelType);
                        cmdM.Parameters.AddWithValue("@UserId",        effectiveUserId);
                        cmdM.Parameters.AddWithValue("@SlotIdx",       request.SlotIndex);
                        cmdM.Parameters.AddWithValue("@TemplateName",  templateName);
                        cmdM.Parameters.AddWithValue("@Width",         request.TemplateData.Config.WidthMm);
                        cmdM.Parameters.AddWithValue("@Height",        request.TemplateData.Config.HeightMm);
                        cmdM.Parameters.AddWithValue("@Dpi",           request.TemplateData.Config.Dpi);
                        cmdM.Parameters.AddWithValue("@CreatedBy",     originalUserId);

                        templateId = (Guid)(await cmdM.ExecuteScalarAsync())!;
                    }

                    // ── 디테일 UPSERT (마스터와 동일한 TEMPLATE_ID 기준) ──
                    const string upsertDetailSql = @"
                        INSERT INTO ZPL_LABEL_TEMPLATE_D
                            (TEMPLATE_ID, TEMPLATE_JSON, RAW_ZPL, PREVIEW_PNG_BASE64)
                        VALUES
                            (@TemplateId, @TemplateJson::jsonb, @RawZpl, @PreviewPng)
                        ON CONFLICT (TEMPLATE_ID)
                        DO UPDATE SET
                            TEMPLATE_JSON      = EXCLUDED.TEMPLATE_JSON,
                            RAW_ZPL            = EXCLUDED.RAW_ZPL,
                            PREVIEW_PNG_BASE64 = EXCLUDED.PREVIEW_PNG_BASE64";

                    using (var cmdD = new NpgsqlCommand(upsertDetailSql, conn, tx))
                    {
                        cmdD.Parameters.AddWithValue("@TemplateId",  templateId);
                        cmdD.Parameters.AddWithValue("@TemplateJson", templateJson);
                        cmdD.Parameters.AddWithValue("@RawZpl",       rawZpl);
                        cmdD.Parameters.AddWithValue("@PreviewPng",   "data:image/png;base64," + previewBase64);

                        await cmdD.ExecuteNonQueryAsync(); // ← 이 줄이 있는지 확인
                    }

                    // ── 변수 필드 동기화 (ZPL_LABEL_FIELD_M) ──────────
                    // isVariable: true 인 요소만 추출해서 메타 저장
                    // 기존 필드 전체 삭제 후 재삽입 (단순 동기화 전략)
                    const string deleteFieldSql =
                        "DELETE FROM ZPL_LABEL_FIELD_M WHERE TEMPLATE_ID = @TemplateId";

                    using (var cmdDel = new NpgsqlCommand(deleteFieldSql, conn, tx))
                    {
                        cmdDel.Parameters.AddWithValue("@TemplateId", templateId);
                        await cmdDel.ExecuteNonQueryAsync();
                    }

                    int sortNo = 1;
                    foreach (var el in request.TemplateData.Elements)
                    {
                        if (!el.IsVariable || string.IsNullOrWhiteSpace(el.FieldName)) continue;

                        const string insertFieldSql = @"
                            INSERT INTO ZPL_LABEL_FIELD_M
                                (TEMPLATE_ID, FIELD_NAME, FIELD_LABEL, SAMPLE_VALUE, DATA_TYPE, SORT_NO)
                            VALUES
                                (@TemplateId, @FieldName, @FieldLabel, @SampleValue, @DataType, @SortNo)
                            ON CONFLICT DO NOTHING";

                        using var cmdF = new NpgsqlCommand(insertFieldSql, conn, tx);
                        cmdF.Parameters.AddWithValue("@TemplateId",  templateId);
                        cmdF.Parameters.AddWithValue("@FieldName",   el.FieldName);
                        cmdF.Parameters.AddWithValue("@FieldLabel",  el.FieldName);  // 추후 UI에서 받을 수 있음
                        cmdF.Parameters.AddWithValue("@SampleValue", el.SampleValue ?? "");
                        cmdF.Parameters.AddWithValue("@DataType",    el.Type == "barcode" ? "BARCODE" : "STRING");
                        cmdF.Parameters.AddWithValue("@SortNo",      sortNo++);
                        await cmdF.ExecuteNonQueryAsync();
                    }

                    await tx.CommitAsync();
                }
                catch
                {
                    await tx.RollbackAsync();
                    throw;
                }

                return Ok(new
                {
                    success    = true,
                    message    = $"슬롯 {request.SlotIndex} 저장 완료",
                    newToken   = newToken,
                    zplPreview = rawZpl,
                    pngPreview = "data:image/png;base64," + previewBase64
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SAVE ERROR] {ex.Message}");
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // ============================================================
        // 3. 라벨 타입 목록 조회 (GET) - 드롭다운 룩업용
        // ============================================================
        [HttpGet("LabelTypes")]
        public async Task<IActionResult> GetLabelTypes([FromQuery] string token)
        {
            try
            {
                var (siteCd, _, _) = DecryptMesToken(token);

                using var conn = CreateDbConnection();
                await conn.OpenAsync();

                const string sql = @"
                    SELECT LABEL_TYPE, LABEL_NAME
                    FROM ZPL_LABEL_TYPE_M
                    WHERE SITE_CD = @SiteCd AND USE_YN = 'Y'
                    ORDER BY LABEL_TYPE";

                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@SiteCd", siteCd);

                var result = new List<object>();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result.Add(new
                    {
                        value = reader.GetString(0),
                        label = reader.GetString(1)
                    });
                }

                return Ok(new { success = true, items = result });
            }
            catch (Exception ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // ============================================================
        // 4. WikiEnter (개발/관리자 진입점 - 유지)
        // ============================================================
        [HttpGet("WikiEnter")]
        public IActionResult WikiEnter([FromQuery] string secret)
        {
            string expectedSecret = _config["SecuritySettings:WikiSecret"] ?? "KwanghoMaster123!";
            if (secret != expectedSecret)
                return Unauthorized("위키 관리자 접근 비밀번호가 틀렸습니다.");

            string now     = DateTime.Now.ToString("yyyyMMddHHmmss");
            string rawData = $"INFOSOLUTION|MASTER|위키관리자|Y|{now}";
            string token   = EncryptMesToken(rawData);

            string reactUrl = $"http://localhost:5173?token={Uri.EscapeDataString(token)}";
            return Redirect(reactUrl);
        }

        // ============================================================
        // 5. 토큰 복호화 (AES-256 CBC)
        //    TEST_TOKEN 은 로컬 개발 편의를 위한 바이패스
        // ============================================================
        private (string SiteCd, string UserId, string IsAdmin) DecryptMesToken(string encryptedToken)
        {
            // 로컬 개발 전용 바이패스 (운영 배포 시 이 블록 제거)
            if (encryptedToken == "TEST_TOKEN" || string.IsNullOrEmpty(encryptedToken))
                return ("INFOSOLUTION", "15197", "Y");

            try
            {
                // URL-safe Base64 → 표준 Base64 복원
                string base64 = encryptedToken.Replace("-", "+").Replace("_", "/");
                switch (base64.Length % 4)
                {
                    case 2: base64 += "=="; break;
                    case 3: base64 += "=";  break;
                }

                byte[] keyBytes    = Encoding.UTF8.GetBytes(_aesKey);
                byte[] ivBytes     = Encoding.UTF8.GetBytes(_aesIv);
                byte[] cipherBytes = Convert.FromBase64String(base64);

                using var aes = Aes.Create();
                aes.Key     = keyBytes;
                aes.IV      = ivBytes;
                aes.Mode    = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                using var ms = new MemoryStream(cipherBytes);
                using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
                using var sr = new StreamReader(cs);

                string plainText = sr.ReadToEnd();
                // 형식: SITE_CD|USER_ID|이름|IS_ADMIN|YYYYMMDDHHMMSS
                string[] parts = plainText.Split('|');
                return (parts[0], parts[1], parts[3]);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[토큰 복호화 오류]: {ex.Message}");
                throw new Exception($"유효하지 않거나 만료된 토큰입니다.");
            }
        }

        private string EncryptMesToken(string plainText)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(_aesKey);
            byte[] ivBytes  = Encoding.UTF8.GetBytes(_aesIv);

            using var aes = Aes.Create();
            aes.Key     = keyBytes;
            aes.IV      = ivBytes;
            aes.Mode    = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs))
            {
                sw.Write(plainText);
            }

            // URL-safe Base64 인코딩
            string base64 = Convert.ToBase64String(ms.ToArray());
            return base64.Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }
    }
}