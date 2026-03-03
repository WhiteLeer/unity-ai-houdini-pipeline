#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CropEngine
{
    /// <summary>
    /// 即梦4.0图片预处理器
    /// 负责将原画转换为适合3D建模的纯净贴图
    /// </summary>
    public class JimengProcessor
    {
        private const string API_ENDPOINT = "https://visual.volcengineapi.com";
        private const string API_VERSION = "2022-08-31";
        private const string SUBMIT_ACTION = "CVSync2AsyncSubmitTask";
        private const string QUERY_ACTION = "CVSync2AsyncGetResult";
        private const string REQ_KEY = "jimeng_t2i_v40";

        private VolcEngineAuth auth;
        private bool enableDebugLog;

        public JimengProcessor(string accessKeyId, string secretAccessKey, bool enableDebugLog = false)
        {
            this.auth = new VolcEngineAuth(accessKeyId, secretAccessKey);
            this.enableDebugLog = enableDebugLog;
        }

        /// <summary>
        /// 预处理原画（Base64模式）
        /// </summary>
        public IEnumerator ProcessImage(
            Texture2D sourceArtwork,
            string prompt,
            float scale,
            int width,
            int height,
            int maxQueryRetries,
            float queryInterval,
            Action<Texture2D> onSuccess,
            Action<string> onError)
        {
            var startTime = DateTime.Now;
            Log($"📤 [即梦4.0] 开始预处理 | 时间: {startTime:HH:mm:ss}");
            Log($"✅ [参数] prompt: {(prompt.Length > 40 ? prompt.Substring(0, 40) + "..." : prompt)} | scale: {scale:F2} | 尺寸: {width}x{height}");

            // Base64编码图片
            byte[] imageBytes = sourceArtwork.EncodeToPNG();
            string base64Image = Convert.ToBase64String(imageBytes);
            Log($"✅ [Base64] 编码完成 | 大小: {imageBytes.Length / 1024}KB");

            // 构建请求
            var request = new PlantPreprocessRequest
            {
                req_key = REQ_KEY,
                binary_data_base64 = new string[] { base64Image },
                prompt = prompt,
                scale = scale,
                size = width * height,
                width = width,
                height = height,
                force_single = true,
                min_ratio = 1f / 3f,
                max_ratio = 3f
            };

            // 提交任务
            string taskId = null;
            yield return SubmitTask(request, id => taskId = id, onError);

            if (string.IsNullOrEmpty(taskId))
            {
                LogError("提交任务失败");
                yield break;
            }

            // 查询结果
            Texture2D resultTexture = null;
            yield return QueryTaskResult(taskId, maxQueryRetries, queryInterval, texture => resultTexture = texture, onError);

            var endTime = DateTime.Now;
            var duration = (endTime - startTime).TotalSeconds;

            if (resultTexture != null)
            {
                Log($"✅ [完成] 成功 | 耗时: {duration:F1}秒");
                onSuccess?.Invoke(resultTexture);
            }
            else
            {
                LogError($"处理失败 | 耗时: {duration:F1}秒");
            }
        }

        private IEnumerator SubmitTask(PlantPreprocessRequest request, Action<string> onTaskIdReceived, Action<string> onError)
        {
            var bodyJson = JsonUtility.ToJson(request);
            var url = $"{API_ENDPOINT}?Action={SUBMIT_ACTION}&Version={API_VERSION}";

            var queryParams = new Dictionary<string, string>
            {
                { "Action", SUBMIT_ACTION },
                { "Version", API_VERSION }
            };

            var headers = auth.GenerateHeaders("POST", API_ENDPOINT, queryParams, bodyJson);

            using (var webRequest = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.timeout = 120;

                foreach (var header in headers)
                {
                    webRequest.SetRequestHeader(header.Key, header.Value);
                }

                Log($"🌐 [提交任务] 发送请求...");
                yield return webRequest.SendWebRequest();

                string responseText = webRequest.downloadHandler?.text ?? "";

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var error = $"请求失败 | HTTP: {webRequest.responseCode} | {webRequest.error}";
                    LogError(error);
                    onError?.Invoke(error);
                    yield break;
                }

                SubmitTaskResponse response = null;
                string parseError = null;

                try
                {
                    response = JsonUtility.FromJson<SubmitTaskResponse>(responseText);
                }
                catch (Exception e)
                {
                    parseError = $"解析响应失败: {e.Message}";
                }

                if (parseError != null)
                {
                    LogError(parseError);
                    onError?.Invoke(parseError);
                    yield break;
                }

                if (response.code != 10000)
                {
                    var error = $"API错误 | code: {response.code} | message: {response.message}";
                    LogError(error);
                    onError?.Invoke(error);
                    yield break;
                }

                if (response.data != null && !string.IsNullOrEmpty(response.data.task_id))
                {
                    Log($"✅ [提交] task_id: {response.data.task_id}");
                    onTaskIdReceived?.Invoke(response.data.task_id);
                }
                else
                {
                    LogError("响应中没有task_id");
                    onError?.Invoke("响应中没有task_id");
                }
            }
        }

        private IEnumerator QueryTaskResult(string taskId, int maxRetries, float queryInterval, Action<Texture2D> onSuccess, Action<string> onError)
        {
            var reqJson = "{\"return_url\":true}";

            var queryRequest = new TaskQueryRequest
            {
                req_key = REQ_KEY,
                task_id = taskId,
                req_json = reqJson
            };

            int retryCount = 0;
            var queryStartTime = DateTime.Now;

            while (retryCount < maxRetries)
            {
                retryCount++;

                var bodyJson = JsonUtility.ToJson(queryRequest);
                var url = $"{API_ENDPOINT}?Action={QUERY_ACTION}&Version={API_VERSION}";

                var queryParams = new Dictionary<string, string>
                {
                    { "Action", QUERY_ACTION },
                    { "Version", API_VERSION }
                };

                var headers = auth.GenerateHeaders("POST", API_ENDPOINT, queryParams, bodyJson);

                using (var webRequest = new UnityWebRequest(url, "POST"))
                {
                    byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);
                    webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    webRequest.downloadHandler = new DownloadHandlerBuffer();
                    webRequest.timeout = 120;

                    foreach (var header in headers)
                    {
                        webRequest.SetRequestHeader(header.Key, header.Value);
                    }

                    yield return webRequest.SendWebRequest();

                    string responseText = webRequest.downloadHandler?.text ?? "";

                    if (webRequest.result != UnityWebRequest.Result.Success)
                    {
                        var error = $"查询失败 | 第{retryCount}次 | HTTP: {webRequest.responseCode}";
                        LogError(error);
                        onError?.Invoke(error);
                        yield break;
                    }

                    TaskResultResponse response = null;
                    string parseError = null;

                    try
                    {
                        response = JsonUtility.FromJson<TaskResultResponse>(responseText);
                    }
                    catch (Exception e)
                    {
                        parseError = $"解析失败: {e.Message}";
                    }

                    if (parseError != null)
                    {
                        LogError(parseError);
                        onError?.Invoke(parseError);
                        yield break;
                    }

                    if (response.code != 10000)
                    {
                        var error = $"API错误 | code: {response.code} | {response.message}";
                        LogError(error);
                        onError?.Invoke(error);
                        yield break;
                    }

                    if (response.data == null)
                    {
                        LogError("响应data为空");
                        onError?.Invoke("响应data为空");
                        yield break;
                    }

                    var totalElapsed = (DateTime.Now - queryStartTime).TotalSeconds;
                    Log($"🔄 [查询] 第{retryCount}/{maxRetries}次 | 状态: {response.data.status} | 已等待: {totalElapsed:F0}秒");

                    if (response.data.status == "done")
                    {
                        // 优先检查image_urls（即梦通常返回URL）
                        if (response.data.image_urls != null && response.data.image_urls.Length > 0)
                        {
                            Log($"🎨 [下载] 从URL获取结果...");
                            yield return DownloadTexture(response.data.image_urls[0], onSuccess, onError);
                            yield break;
                        }
                        // 其次检查binary_data_base64
                        else if (response.data.binary_data_base64 != null && response.data.binary_data_base64.Length > 0)
                        {
                            Log($"🎨 [解码] 从Base64获取结果...");
                            var texture = Base64ToTexture(response.data.binary_data_base64[0]);
                            if (texture != null)
                            {
                                Log($"✅ [解码] 成功 | 尺寸: {texture.width}x{texture.height}");
                                onSuccess?.Invoke(texture);
                                yield break;
                            }
                            else
                            {
                                LogError("Base64解码失败");
                                onError?.Invoke("Base64解码失败");
                                yield break;
                            }
                        }
                        else
                        {
                            LogError("任务完成但没有返回图片数据");
                            onError?.Invoke("任务完成但没有返回图片数据");
                            yield break;
                        }
                    }
                    else if (response.data.status == "not_found" || response.data.status == "expired")
                    {
                        var error = $"任务{response.data.status}";
                        LogError(error);
                        onError?.Invoke(error);
                        yield break;
                    }
                    else
                    {
                        yield return new WaitForSeconds(queryInterval);
                    }
                }
            }

            var errorMsg = $"查询超时 | 已重试{maxRetries}次";
            LogError(errorMsg);
            onError?.Invoke(errorMsg);
        }

        private IEnumerator DownloadTexture(string imageUrl, Action<Texture2D> onSuccess, Action<string> onError)
        {
            using (var webRequest = UnityWebRequestTexture.GetTexture(imageUrl))
            {
                webRequest.timeout = 120;
                yield return webRequest.SendWebRequest();

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var error = $"下载失败 | {webRequest.error}";
                    LogError(error);
                    onError?.Invoke(error);
                    yield break;
                }

                var texture = DownloadHandlerTexture.GetContent(webRequest);
                if (texture != null)
                {
                    Log($"✅ [下载] 成功 | 尺寸: {texture.width}x{texture.height}");
                    onSuccess?.Invoke(texture);
                }
                else
                {
                    LogError("下载的图片为空");
                    onError?.Invoke("下载的图片为空");
                }
            }
        }

        private Texture2D Base64ToTexture(string base64String)
        {
            try
            {
                byte[] imageBytes = Convert.FromBase64String(base64String);
                Texture2D texture = new Texture2D(2, 2);
                if (texture.LoadImage(imageBytes))
                {
                    return texture;
                }
                return null;
            }
            catch (Exception e)
            {
                LogError($"Base64解码异常: {e.Message}");
                return null;
            }
        }

        private void Log(string message)
        {
            if (enableDebugLog)
            {
                Debug.Log($"<color=cyan>[即梦4.0]</color> {message}");
            }
        }

        private void LogError(string message)
        {
            Debug.LogError($"<color=red>[即梦4.0]</color> {message}");
        }
    }
}
#endif
