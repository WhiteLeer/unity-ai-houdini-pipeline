using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace CropEngine
{
    /// <summary>
    /// 火山引擎API签名认证
    /// 实现AWS Signature V4风格的签名算法
    /// </summary>
    public class VolcEngineAuth
    {
        private readonly string accessKeyId;
        private readonly string secretAccessKey;
        private readonly string region;
        private readonly string service;

        public VolcEngineAuth(string accessKeyId, string secretAccessKey, string region = "cn-north-1", string service = "cv")
        {
            this.accessKeyId = accessKeyId;
            this.secretAccessKey = secretAccessKey;
            this.region = region;
            this.service = service;
        }

        public Dictionary<string, string> GenerateHeaders(string httpMethod, string url, Dictionary<string, string> queryParams, string body)
        {
            var dateTime = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var date = DateTime.UtcNow.ToString("yyyyMMdd");

            var headers = new Dictionary<string, string>
            {
                { "X-Date", dateTime }
            };

            // 只有POST/PUT等有body的请求才需要Content-Type
            if (!string.IsNullOrEmpty(body) && httpMethod != "GET")
            {
                headers["Content-Type"] = "application/json";
            }

            var signature = GenerateSignature(httpMethod, url, queryParams, headers, body, date);

            // 调试日志：记录签名信息（脱敏）
            if (!string.IsNullOrEmpty(signature))
            {
                Debug.Log($"<color=cyan>[CropEngine Auth]</color> 签名生成成功: {signature.Substring(0, Math.Min(8, signature.Length))}***");
            }
            else
            {
                Debug.LogError("<color=red>[CropEngine Auth]</color> 签名生成失败！");
            }

            var credential = $"{accessKeyId}/{date}/{region}/{service}/request";
            var signedHeaders = string.Join(";", headers.Keys.Select(k => k.ToLower()).OrderBy(k => k));

            headers["Authorization"] = $"HMAC-SHA256 Credential={credential}, SignedHeaders={signedHeaders}, Signature={signature}";

            var credentialPreview = credential.Length > 20 ? credential.Substring(0, 20) + "..." : credential;
            Debug.Log($"<color=cyan>[CropEngine Auth]</color> Authorization header: HMAC-SHA256 Credential={credentialPreview}, SignedHeaders={signedHeaders}");

            return headers;
        }

        private string GenerateSignature(string httpMethod, string url, Dictionary<string, string> queryParams,
            Dictionary<string, string> headers, string body, string date)
        {
            try
            {
                var uri = new Uri(url);
                var canonicalUri = uri.AbsolutePath;
                var canonicalQueryString = BuildCanonicalQueryString(queryParams);
                var canonicalHeaders = BuildCanonicalHeaders(headers);
                var signedHeaders = string.Join(";", headers.Keys.Select(k => k.ToLower()).OrderBy(k => k));
                var payloadHash = ComputeSHA256Hash(body);

                var canonicalRequest = $"{httpMethod}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

                var algorithm = "HMAC-SHA256";
                var credentialScope = $"{date}/{region}/{service}/request";
                var canonicalRequestHash = ComputeSHA256Hash(canonicalRequest);
                var dateTime = headers["X-Date"];

                var stringToSign = $"{algorithm}\n{dateTime}\n{credentialScope}\n{canonicalRequestHash}";

                var signingKey = GetSignatureKey(secretAccessKey, date, region, service);
                var signature = ComputeHMACSHA256(stringToSign, signingKey);

                return ByteArrayToHexString(signature);
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[CropEngine Auth]</color> 签名生成失败: {e.Message}\n堆栈: {e.StackTrace}");
                return string.Empty;
            }
        }

        private string BuildCanonicalQueryString(Dictionary<string, string> queryParams)
        {
            if (queryParams == null || queryParams.Count == 0)
                return string.Empty;

            var sortedParams = queryParams.OrderBy(kvp => kvp.Key);
            var encodedParams = sortedParams.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
            return string.Join("&", encodedParams);
        }

        private string BuildCanonicalHeaders(Dictionary<string, string> headers)
        {
            var sortedHeaders = headers.OrderBy(kvp => kvp.Key.ToLower());
            var headerStrings = sortedHeaders.Select(kvp =>
                $"{kvp.Key.ToLower()}:{kvp.Value.Trim()}\n");
            return string.Concat(headerStrings);
        }

        private byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
        {
            var kDate = ComputeHMACSHA256(dateStamp, Encoding.UTF8.GetBytes(key));
            var kRegion = ComputeHMACSHA256(regionName, kDate);
            var kService = ComputeHMACSHA256(serviceName, kRegion);
            var kSigning = ComputeHMACSHA256("request", kService);
            return kSigning;
        }

        private string ComputeSHA256Hash(string data)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(data);
                var hash = sha256.ComputeHash(bytes);
                return ByteArrayToHexString(hash);
            }
        }

        private byte[] ComputeHMACSHA256(string data, byte[] key)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            }
        }

        private string ByteArrayToHexString(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLower();
        }
    }
}
