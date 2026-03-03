using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CropEngine
{
    /// <summary>
    /// 火山引擎ImageX图片上传器
    /// 用于上传本地图片到CDN，获取可访问的URL
    /// </summary>
    public class ImageXUploader
    {
        private VolcEngineAuth auth;
        private string serviceId;
        private string domain;
        private string endpoint;
        private bool enableDetailedLog;

        public ImageXUploader(string accessKeyId, string secretAccessKey, string serviceId, string domain, string endpoint, bool enableDetailedLog = false)
        {
            this.serviceId = serviceId;
            this.domain = domain;
            this.endpoint = endpoint;
            this.enableDetailedLog = enableDetailedLog;
            // ImageX服务使用"imagex"作为service名称
            this.auth = new VolcEngineAuth(accessKeyId, secretAccessKey, "cn-north-1", "imagex");
        }

        /// <summary>
        /// 上传图片到ImageX并获取访问URL
        /// </summary>
        public IEnumerator UploadImage(Texture2D texture, Action<string> onSuccess, Action<string> onError)
        {
            if (string.IsNullOrEmpty(serviceId))
            {
                onError?.Invoke("ImageX ServiceId未配置");
                yield break;
            }

            if (enableDetailedLog) Debug.Log($"<color=cyan>[ImageX]</color> 开始上传 ({texture.width}x{texture.height})");

            // 1. 编码图片
            byte[] imageBytes = texture.EncodeToPNG();
            var imageKey = GenerateImageKey();

            if (enableDetailedLog) Debug.Log($"<color=cyan>[ImageX]</color> 图片大小: {imageBytes.Length / 1024}KB | Key: {imageKey}");

            // 2. 申请上传凭证
            ImageXUploadResponse uploadResponse = null;
            yield return ApplyImageUpload(imageKey, response => uploadResponse = response, onError);

            if (uploadResponse == null || uploadResponse.Result == null)
            {
                yield break;
            }

            // 3. 上传图片数据
            string storeUri = null;
            yield return UploadImageData(uploadResponse, imageBytes, imageKey, uri => storeUri = uri, onError);

            if (string.IsNullOrEmpty(storeUri))
            {
                yield break;
            }

            // 4. 获取访问URL
            yield return GetResourceURL(storeUri, onSuccess, onError);
        }

        /// <summary>
        /// 步骤1：申请上传凭证
        /// </summary>
        private IEnumerator ApplyImageUpload(string imageKey, Action<ImageXUploadResponse> onSuccess, Action<string> onError)
        {
            var url = endpoint;
            var queryParams = new Dictionary<string, string>
            {
                { "Action", "ApplyImageUpload" },
                { "Version", "2018-08-01" },
                { "ServiceId", serviceId },
                { "StoreKeys", imageKey }
            };

            var queryString = string.Join("&", queryParams.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            var fullUrl = $"{url}?{queryString}";

            var headers = auth.GenerateHeaders("GET", url, queryParams, "");

            using (var webRequest = UnityWebRequest.Get(fullUrl))
            {
                foreach (var header in headers)
                {
                    if (header.Key != "Content-Type")
                    {
                        webRequest.SetRequestHeader(header.Key, header.Value);
                    }
                }

                yield return webRequest.SendWebRequest();

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var error = $"申请上传凭证失败 | HTTP: {webRequest.responseCode} | {webRequest.error}";
                    Debug.LogError($"<color=red>[ImageX]</color> {error}");
                    onError?.Invoke(error);
                    yield break;
                }

                var responseText = webRequest.downloadHandler.text;

                if (enableDetailedLog) Debug.Log($"<color=yellow>[ImageX Debug]</color> ApplyImageUpload响应: {responseText}");

                // 解析响应（不在try中使用yield）
                ImageXUploadResponse response = null;
                string parseError = null;

                try
                {
                    response = JsonUtility.FromJson<ImageXUploadResponse>(responseText);

                    // 首先检查是否有Error
                    if (response.ResponseMetadata != null && response.ResponseMetadata.Error != null && response.ResponseMetadata.Error.CodeN != 0)
                    {
                        parseError = $"ImageX API错误 | Code: {response.ResponseMetadata.Error.Code} ({response.ResponseMetadata.Error.CodeN})\n" +
                                   $"Message: {response.ResponseMetadata.Error.Message}";
                    }
                    else if (response.Result == null || response.Result.UploadAddress == null)
                    {
                        parseError = "上传凭证响应格式错误";
                    }
                }
                catch (Exception e)
                {
                    parseError = $"解析上传凭证失败: {e.Message}";
                }

                // 处理结果
                if (parseError != null)
                {
                    Debug.LogError($"<color=red>[ImageX]</color> {parseError}");
                    onError?.Invoke(parseError);
                    yield break;
                }

                if (enableDetailedLog) Debug.Log($"<color=cyan>[ImageX]</color> ✅ 获取上传凭证成功");
                onSuccess?.Invoke(response);
            }
        }

        /// <summary>
        /// 步骤2：上传图片数据
        /// </summary>
        private IEnumerator UploadImageData(ImageXUploadResponse uploadResponse, byte[] imageBytes, string imageKey, Action<string> onSuccess, Action<string> onError)
        {
            if (uploadResponse.Result.UploadAddress.UploadHosts == null || uploadResponse.Result.UploadAddress.UploadHosts.Length == 0)
            {
                onError?.Invoke("没有可用的上传Host");
                yield break;
            }

            if (uploadResponse.Result.UploadAddress.StoreInfos == null || uploadResponse.Result.UploadAddress.StoreInfos.Length == 0)
            {
                onError?.Invoke("没有可用的StoreInfo");
                yield break;
            }

            var uploadHost = uploadResponse.Result.UploadAddress.UploadHosts[0];
            var sessionKey = uploadResponse.Result.UploadAddress.SessionKey;
            var storeInfoObj = uploadResponse.Result.UploadAddress.StoreInfos[0];

            if (enableDetailedLog) Debug.Log($"<color=yellow>[ImageX Debug]</color> StoreInfo parsed: {(storeInfoObj == null ? "NULL!" : $"Uri={storeInfoObj.StoreUri}, HasAuth={!string.IsNullOrEmpty(storeInfoObj.Auth)}, UploadID={storeInfoObj.UploadID}")}");

            if (storeInfoObj == null || string.IsNullOrEmpty(storeInfoObj.StoreUri))
            {
                onError?.Invoke("StoreInfo解析失败或StoreUri为空");
                yield break;
            }

            // 计算MD5校验（Base64编码）
            var md5Hash = CalculateMD5(imageBytes);
            var md5Base64 = Convert.ToBase64String(md5Hash);
            if (enableDetailedLog) Debug.Log($"<color=yellow>[ImageX Debug]</color> MD5计算: {BitConverter.ToString(md5Hash).Replace("-", "")} -> Base64: {md5Base64}");

            // 将StoreInfo对象序列化为JSON字符串
            var storeInfoJson = JsonUtility.ToJson(storeInfoObj);
            if (enableDetailedLog) Debug.Log($"<color=yellow>[ImageX Debug]</color> StoreInfoJson: {storeInfoJson}");

            // 构建上传URL（不添加任何参数，让Auth token处理所有验证）
            var uploadUrl = $"http://{uploadHost}/{storeInfoObj.StoreUri}";
            if (enableDetailedLog) Debug.Log($"<color=yellow>[ImageX Debug]</color> UploadUrl: {uploadUrl}");

            using (var webRequest = new UnityWebRequest(uploadUrl, "POST"))
            {
                webRequest.uploadHandler = new UploadHandlerRaw(imageBytes);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                // 只发送Authorization，让服务器根据Auth token中的信息进行验证
                webRequest.SetRequestHeader("Authorization", storeInfoObj.Auth);

                if (enableDetailedLog)
                {
                    Debug.Log($"<color=yellow>[ImageX Debug]</color> 上传请求Headers:\n" +
                             $"  Method: POST\n" +
                             $"  Authorization: {storeInfoObj.Auth.Substring(0, Math.Min(50, storeInfoObj.Auth.Length))}...\n" +
                             $"  (MD5已计算但未发送: {md5Base64})");
                }

                yield return webRequest.SendWebRequest();

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var responseBody = webRequest.downloadHandler?.text ?? "无响应内容";
                    var error = $"上传图片数据失败 | HTTP: {webRequest.responseCode} | {webRequest.error}\n响应: {responseBody}";
                    Debug.LogError($"<color=red>[ImageX]</color> {error}");
                    onError?.Invoke(error);
                    yield break;
                }

                // 解析上传响应
                var responseText = webRequest.downloadHandler.text;
                if (enableDetailedLog) Debug.Log($"<color=yellow>[ImageX Debug]</color> 上传响应: {responseText}");

                // 先解析数据（不在try中使用yield）
                string storeUri = null;
                bool parseSuccess = false;
                string parseError = null;

                try
                {
                    var uploadResult = JsonUtility.FromJson<ImageUploadResult>(responseText);
                    if (enableDetailedLog) Debug.Log($"<color=yellow>[ImageX Debug]</color> uploadResult parsed, payload={uploadResult?.payload?.Substring(0, Math.Min(50, uploadResult?.payload?.Length ?? 0))}...");
                    if (!string.IsNullOrEmpty(uploadResult.payload))
                    {
                        // payload是base64编码的JSON
                        var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(uploadResult.payload));
                        var payloadData = JsonUtility.FromJson<ImageUploadPayload>(payloadJson);

                        if (payloadData != null && payloadData.Results != null && payloadData.Results.Length > 0)
                        {
                            storeUri = payloadData.Results[0].Uri;
                            parseSuccess = true;
                            if (enableDetailedLog) Debug.Log($"<color=cyan>[ImageX]</color> ✅ 上传数据成功 | StoreUri: {storeUri}");
                        }
                        else
                        {
                            parseError = "payload中没有Results";
                        }
                    }
                    else
                    {
                        parseError = "上传响应中没有payload";
                    }
                }
                catch (Exception e)
                {
                    parseError = $"解析上传响应失败: {e.Message}";
                }

                // 处理解析错误
                if (!parseSuccess)
                {
                    Debug.LogError($"<color=red>[ImageX]</color> {parseError}");
                    onError?.Invoke(parseError);
                    yield break;
                }

                // 提交确认（在try外部）
                yield return CommitImageUpload(sessionKey, onCommitSuccess =>
                {
                    if (onCommitSuccess)
                    {
                        onSuccess?.Invoke(storeUri);
                    }
                }, onError);
            }
        }

        /// <summary>
        /// 步骤3：提交确认上传
        /// </summary>
        private IEnumerator CommitImageUpload(string sessionKey, Action<bool> onSuccess, Action<string> onError)
        {
            var commitBodyObj = new CommitUploadBody
            {
                SessionKey = sessionKey
            };
            var commitBodyJson = JsonUtility.ToJson(commitBodyObj);

            var url = endpoint;
            var queryParams = new Dictionary<string, string>
            {
                { "Action", "CommitImageUpload" },
                { "Version", "2018-08-01" },
                { "ServiceId", serviceId }
            };

            var queryString = string.Join("&", queryParams.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            var fullUrl = $"{url}?{queryString}";

            var headers = auth.GenerateHeaders("POST", url, queryParams, commitBodyJson);

            using (var webRequest = new UnityWebRequest(fullUrl, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(commitBodyJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();

                foreach (var header in headers)
                {
                    webRequest.SetRequestHeader(header.Key, header.Value);
                }

                yield return webRequest.SendWebRequest();

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var error = $"提交确认失败 | HTTP: {webRequest.responseCode} | {webRequest.error}";
                    Debug.LogError($"<color=red>[ImageX]</color> {error}");
                    onError?.Invoke(error);
                    yield break;
                }

                if (enableDetailedLog) Debug.Log($"<color=cyan>[ImageX]</color> ✅ 提交确认成功");
                onSuccess?.Invoke(true);
            }
        }

        /// <summary>
        /// 步骤4：获取资源访问URL
        /// </summary>
        private IEnumerator GetResourceURL(string storeUri, Action<string> onSuccess, Action<string> onError)
        {
            // 模板名称
            var templateName = $"tplv-{serviceId}-image.image";

            var url = endpoint;
            var queryParams = new Dictionary<string, string>
            {
                { "Action", "GetResourceURL" },
                { "Version", "2018-08-01" },
                { "ServiceId", serviceId },
                { "Domain", domain },
                { "URI", storeUri },
                { "Tpl", templateName },
                { "Proto", "https" }
            };

            var queryString = string.Join("&", queryParams.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            var fullUrl = $"{url}?{queryString}";

            var headers = auth.GenerateHeaders("GET", url, queryParams, "");

            using (var webRequest = UnityWebRequest.Get(fullUrl))
            {
                foreach (var header in headers)
                {
                    if (header.Key != "Content-Type")
                    {
                        webRequest.SetRequestHeader(header.Key, header.Value);
                    }
                }

                yield return webRequest.SendWebRequest();

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var error = $"获取资源URL失败 | HTTP: {webRequest.responseCode} | {webRequest.error}";
                    Debug.LogError($"<color=red>[ImageX]</color> {error}");
                    onError?.Invoke(error);
                    yield break;
                }

                var responseText = webRequest.downloadHandler.text;

                // 解析响应（不在try中使用yield）
                GetResourceURLResponse response = null;
                string parseError = null;
                string resourceURL = null;

                try
                {
                    response = JsonUtility.FromJson<GetResourceURLResponse>(responseText);

                    if (response.ResponseMetadata.Error != null && response.ResponseMetadata.Error.CodeN != 0)
                    {
                        parseError = $"API错误 | code: {response.ResponseMetadata.Error.CodeN} | {response.ResponseMetadata.Error.Message}";
                    }
                    else if (response.Result != null && !string.IsNullOrEmpty(response.Result.ResourceURL))
                    {
                        resourceURL = response.Result.ResourceURL;
                    }
                    else
                    {
                        parseError = "响应中没有ResourceURL";
                    }
                }
                catch (Exception e)
                {
                    parseError = $"解析GetResourceURL响应失败: {e.Message}";
                }

                // 处理结果
                if (parseError != null)
                {
                    Debug.LogError($"<color=red>[ImageX]</color> {parseError}");
                    onError?.Invoke(parseError);
                    yield break;
                }

                if (enableDetailedLog) Debug.Log($"<color=green>[ImageX]</color> ✅ 获取URL成功（24小时有效）");
                onSuccess?.Invoke(resourceURL);
            }
        }

        private string GenerateImageKey()
        {
            return $"plant_{DateTime.Now:yyyyMMddHHmmss}_{UnityEngine.Random.Range(1000, 9999)}.png";
        }

        private byte[] CalculateMD5(byte[] data)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                return md5.ComputeHash(data);
            }
        }
    }

    #region ImageX响应数据结构

    [Serializable]
    public class ImageUploadBody
    {
        public string SessionKey;
        public string StoreInfo;
    }

    [Serializable]
    public class CommitUploadBody
    {
        public string SessionKey;
    }

    [Serializable]
    public class ImageXUploadResponse
    {
        public ResponseMetadata ResponseMetadata;
        public UploadResult Result;
    }

    [Serializable]
    public class UploadResult
    {
        public string RequestId;
        public UploadAddress UploadAddress;
    }

    [Serializable]
    public class UploadAddress
    {
        public string[] UploadHosts;
        public StoreInfo[] StoreInfos;
        public string SessionKey;
    }

    [Serializable]
    public class StoreInfo
    {
        public string StoreUri;
        public string Auth;
        public string UploadID;
    }

    [Serializable]
    public class ImageUploadResult
    {
        public string payload;
    }

    [Serializable]
    public class ImageUploadPayload
    {
        public ImageResult[] Results;
    }

    [Serializable]
    public class ImageResult
    {
        public string Uri;
        public int UriStatus;
    }

    [Serializable]
    public class GetResourceURLResponse
    {
        public ResponseMetadata ResponseMetadata;
        public ResourceURLResult Result;
    }

    [Serializable]
    public class ResponseMetadata
    {
        public string RequestId;
        public string Action;
        public string Version;
        public string Service;
        public string Region;
        public ApiError Error;
    }

    [Serializable]
    public class ApiError
    {
        public string Code;
        public int CodeN;
        public string Message;
    }

    [Serializable]
    public class ResourceURLResult
    {
        public string ResourceURL;
    }

    #endregion
}
