// ============================================================
// ITokenService.cs  (추후 리팩토링 시 적용 권장)
//
// 현재 DecryptMesToken 로직이 LabelDesignerController와
// LabelPrintController 양쪽에 중복되어 있습니다.
// MVP 이후 아래처럼 분리하면 한 곳만 수정해도 됩니다.
// ============================================================

using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LabelApi.Services
{
    public interface ITokenService
    {
        (string SiteCd, string UserId, string IsAdmin) Decrypt(string encryptedToken);
        string Encrypt(string siteCd, string userId, string name, string isAdmin);
    }

    public class AesTokenService : ITokenService
    {
        private readonly string _key;
        private readonly string _iv;

        public AesTokenService(IConfiguration config)
        {
            _key = config["SecuritySettings:LabelAesKey"]!;
            _iv  = config["SecuritySettings:LabelAesIv"]!;
        }

        public (string SiteCd, string UserId, string IsAdmin) Decrypt(string encryptedToken)
        {
            // 로컬 개발 바이패스
            if (encryptedToken == "TEST_TOKEN" || string.IsNullOrEmpty(encryptedToken))
                return ("INFOSOLUTION", "15197", "Y");

            string base64 = encryptedToken.Replace("-", "+").Replace("_", "/");
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "=";  break;
            }

            byte[] keyBytes    = Encoding.UTF8.GetBytes(_key);
            byte[] ivBytes     = Encoding.UTF8.GetBytes(_iv);
            byte[] cipherBytes = Convert.FromBase64String(base64);

            using var aes = Aes.Create();
            aes.Key = keyBytes; aes.IV = ivBytes;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            using var ms = new MemoryStream(cipherBytes);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);

            string[] parts = sr.ReadToEnd().Split('|');
            return (parts[0], parts[1], parts[3]);
        }

        public string Encrypt(string siteCd, string userId, string name, string isAdmin)
        {
            string now      = DateTime.Now.ToString("yyyyMMddHHmmss");
            string plain    = $"{siteCd}|{userId}|{name}|{isAdmin}|{now}";

            byte[] keyBytes = Encoding.UTF8.GetBytes(_key);
            byte[] ivBytes  = Encoding.UTF8.GetBytes(_iv);

            using var aes = Aes.Create();
            aes.Key = keyBytes; aes.IV = ivBytes;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs)) { sw.Write(plain); }

            return Convert.ToBase64String(ms.ToArray())
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }
    }
}

// ============================================================
// Program.cs 또는 Startup.cs에 아래 한 줄 추가하면 DI 완성
// ============================================================
// builder.Services.AddSingleton<ITokenService, AesTokenService>();
//
// 컨트롤러 생성자에서는:
// public LabelDesignerController(IConfiguration config, ITokenService tokenService)
// public LabelPrintController(IConfiguration config, ITokenService tokenService)