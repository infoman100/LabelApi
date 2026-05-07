var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// 🌟 1. CORS 정책 추가 (builder.Build() 윗부분 어딘가에 넣으세요)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()   // 모든 IP 허용
              .AllowAnyMethod()   // GET, POST, PUT 등 모든 방식 허용
              .AllowAnyHeader();  // 모든 헤더 허용
    });
});

builder.Services.AddControllers();

var app = builder.Build();

// 🌟 2. CORS 사용 선언 (반드시 app.UseAuthorization() 보다 위에 있어야 합니다!)
app.UseCors("AllowAll"); 

app.UseAuthorization();
app.MapControllers();
app.Run();
