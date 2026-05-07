// ============================================================
// LabelTemplateModels.cs 하단에 아래 클래스 2개를 추가하세요
// (기존 코드는 그대로 유지)
// ============================================================

// Phase 3: 인쇄 미리보기 요청 DTO
public class PrintPreviewRequest
{
    // MES에서 발급한 AES-256 토큰
    public string Token { get; set; } = string.Empty;

    // 어떤 템플릿을 사용할지 (ZPL_LABEL_TEMPLATE_M.TEMPLATE_ID)
    public Guid TemplateId { get; set; }

    // 실제 인쇄할 변수값 딕셔너리
    // key   = fieldName  (예: "LOT_NO", "ITEM_NAME")
    // value = 실제값     (예: "LOT-2024-001", "삼성 베트남 수출용")
    public Dictionary<string, string>? PrintData { get; set; }
}