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
    /// Meshy API封装 - 图片转3D模型
    /// 支持：Image to 3D、Remesh、Retexture三个步骤
    /// </summary>
    public class MeshyAPI
    {
        private const string API_BASE = "https://api.meshy.ai";
        private const string API_VERSION = "/openapi/v1";

        private string apiKey;
        private bool enableDetailedLog;

        public MeshyAPI(string apiKey, bool enableDetailedLog = false)
        {
            this.apiKey = apiKey;
            this.enableDetailedLog = enableDetailedLog;
        }

        #region Image to 3D

        /// <summary>
        /// 步骤1：图片转3D模型
        /// </summary>
        public IEnumerator ImageTo3D(
            string imageUrl,
            string aiModel,
            Action<string> onTaskIdReceived,
            Action<string> onError)
        {
            Debug.Log($"<color=cyan>[Meshy]</color> 🎯 [ImageTo3D] 开始调用API | 模型: {aiModel}");
            var url = $"{API_BASE}{API_VERSION}/image-to-3d";

            var requestBody = new ImageTo3DRequest
            {
                image_url = imageUrl,
                ai_model = aiModel,
                enable_pbr = false,
                should_remesh = false,  // 不自动remesh，我们手动控制
                should_texture = false  // 不自动生成贴图，由步骤3的Retexture控制
            };

            var bodyJson = JsonUtility.ToJson(requestBody);
            Debug.Log($"<color=cyan>[Meshy]</color> [ImageTo3D] 请求URL: {url}");
            if (enableDetailedLog) Debug.Log($"<color=cyan>[Meshy]</color> [ImageTo3D] 请求体: {bodyJson}");

            using (var webRequest = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");
                webRequest.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                Debug.Log($"<color=cyan>[Meshy]</color> [ImageTo3D] 正在发送HTTP请求...");
                yield return webRequest.SendWebRequest();
                Debug.Log($"<color=cyan>[Meshy]</color> [ImageTo3D] HTTP请求完成 | 状态码: {webRequest.responseCode}");

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var error = $"ImageTo3D失败 | HTTP: {webRequest.responseCode} | {webRequest.error}";
                    Debug.LogError($"<color=red>[Meshy]</color> {error}");
                    onError?.Invoke(error);
                    yield break;
                }

                var responseText = webRequest.downloadHandler.text;
                if (enableDetailedLog) Debug.Log($"<color=yellow>[Meshy Debug]</color> ImageTo3D响应: {responseText}");

                try
                {
                    var response = JsonUtility.FromJson<MeshyTaskResponse>(responseText);
                    if (!string.IsNullOrEmpty(response.result))
                    {
                        Debug.Log($"<color=cyan>[Meshy]</color> ✅ ImageTo3D任务创建成功 | ID: {response.result}");
                        onTaskIdReceived?.Invoke(response.result);
                    }
                    else
                    {
                        onError?.Invoke("响应中没有task ID");
                    }
                }
                catch (Exception e)
                {
                    onError?.Invoke($"解析响应失败: {e.Message}");
                }
            }
        }

        #endregion

        #region Remesh

        /// <summary>
        /// 步骤2：重拓扑优化模型
        /// </summary>
        public IEnumerator Remesh(
            string inputTaskId,
            int targetPolycount,
            Action<string> onTaskIdReceived,
            Action<string> onError)
        {
            var url = $"{API_BASE}{API_VERSION}/remesh";

            var requestBody = new RemeshRequest
            {
                input_task_id = inputTaskId,
                target_polycount = targetPolycount,
                topology = "quad",
                target_formats = new string[] { "glb", "fbx" }
            };

            var bodyJson = JsonUtility.ToJson(requestBody);
            if (enableDetailedLog) Debug.Log($"<color=cyan>[Meshy]</color> [Remesh] 请求体: {bodyJson}");

            using (var webRequest = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");
                webRequest.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                yield return webRequest.SendWebRequest();

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var error = $"Remesh失败 | HTTP: {webRequest.responseCode} | {webRequest.error}";
                    Debug.LogError($"<color=red>[Meshy]</color> {error}");
                    onError?.Invoke(error);
                    yield break;
                }

                var responseText = webRequest.downloadHandler.text;
                if (enableDetailedLog) Debug.Log($"<color=yellow>[Meshy Debug]</color> Remesh响应: {responseText}");

                try
                {
                    var response = JsonUtility.FromJson<MeshyTaskResponse>(responseText);
                    if (!string.IsNullOrEmpty(response.result))
                    {
                        Debug.Log($"<color=cyan>[Meshy]</color> ✅ Remesh任务创建成功 | ID: {response.result}");
                        onTaskIdReceived?.Invoke(response.result);
                    }
                    else
                    {
                        onError?.Invoke("响应中没有task ID");
                    }
                }
                catch (Exception e)
                {
                    onError?.Invoke($"解析响应失败: {e.Message}");
                }
            }
        }

        #endregion

        #region Retexture

        /// <summary>
        /// 步骤3：重新生成纹理
        /// </summary>
        /// <param name="inputTaskId">输入任务ID</param>
        /// <param name="texturePrompt">文本风格提示（可选）</param>
        /// <param name="imageStyleUrl">参考图像URL（可选，支持Data URI）</param>
        /// <param name="aiModel">AI模型版本</param>
        /// <param name="onTaskIdReceived">任务ID回调</param>
        /// <param name="onError">错误回调</param>
        public IEnumerator Retexture(
            string inputTaskId,
            string texturePrompt,
            string imageStyleUrl,
            string aiModel,
            Action<string> onTaskIdReceived,
            Action<string> onError)
        {
            var url = $"{API_BASE}{API_VERSION}/retexture";

            var requestBody = new RetextureRequest
            {
                input_task_id = inputTaskId,
                text_style_prompt = texturePrompt,
                image_style_url = imageStyleUrl,
                ai_model = aiModel,
                enable_pbr = true,
                enable_original_uv = true
            };

            var bodyJson = JsonUtility.ToJson(requestBody);
            if (enableDetailedLog) Debug.Log($"<color=cyan>[Meshy]</color> [Retexture] 请求体: {bodyJson}");

            using (var webRequest = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");
                webRequest.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                yield return webRequest.SendWebRequest();

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var error = $"Retexture失败 | HTTP: {webRequest.responseCode} | {webRequest.error}";
                    Debug.LogError($"<color=red>[Meshy]</color> {error}");
                    onError?.Invoke(error);
                    yield break;
                }

                var responseText = webRequest.downloadHandler.text;
                if (enableDetailedLog) Debug.Log($"<color=yellow>[Meshy Debug]</color> Retexture响应: {responseText}");

                try
                {
                    var response = JsonUtility.FromJson<MeshyTaskResponse>(responseText);
                    if (!string.IsNullOrEmpty(response.result))
                    {
                        Debug.Log($"<color=cyan>[Meshy]</color> ✅ Retexture任务创建成功 | ID: {response.result}");
                        onTaskIdReceived?.Invoke(response.result);
                    }
                    else
                    {
                        onError?.Invoke("响应中没有task ID");
                    }
                }
                catch (Exception e)
                {
                    onError?.Invoke($"解析响应失败: {e.Message}");
                }
            }
        }

        #endregion

        #region Image to Image (Multi-View Generation)

        /// <summary>
        /// 图片转图片 - 支持多视图生成
        /// </summary>
        /// <param name="imageUrl">输入图片URL（支持Data URI）</param>
        /// <param name="prompt">风格提示词</param>
        /// <param name="generateMultiView">是否生成多视图（3个不同角度）</param>
        /// <param name="aiModel">AI模型版本</param>
        /// <param name="onTaskIdReceived">任务ID回调</param>
        /// <param name="onError">错误回调</param>
        public IEnumerator ImageToImage(
            string imageUrl,
            string prompt,
            bool generateMultiView,
            string aiModel,
            Action<string> onTaskIdReceived,
            Action<string> onError)
        {
            Debug.Log($"<color=cyan>[Meshy]</color> 🎯 [ImageToImage] 开始调用API | 多视图: {generateMultiView}");
            var url = $"{API_BASE}{API_VERSION}/image-to-image";

            var requestBody = new ImageToImageRequest
            {
                ai_model = "nano-banana",  // ImageToImage API固定使用nano-banana模型
                prompt = prompt,
                reference_image_urls = new string[] { imageUrl },
                generate_multi_view = generateMultiView
            };

            var bodyJson = JsonUtility.ToJson(requestBody);
            Debug.Log($"<color=cyan>[Meshy]</color> [ImageToImage] 请求URL: {url}");
            if (enableDetailedLog) Debug.Log($"<color=cyan>[Meshy]</color> [ImageToImage] 请求体: {bodyJson}");

            using (var webRequest = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");
                webRequest.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                Debug.Log($"<color=cyan>[Meshy]</color> [ImageToImage] 正在发送HTTP请求...");
                yield return webRequest.SendWebRequest();
                Debug.Log($"<color=cyan>[Meshy]</color> [ImageToImage] HTTP请求完成 | 状态码: {webRequest.responseCode}");

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var error = $"ImageToImage失败 | HTTP: {webRequest.responseCode} | {webRequest.error}";
                    Debug.LogError($"<color=red>[Meshy]</color> {error}");
                    onError?.Invoke(error);
                    yield break;
                }

                var responseText = webRequest.downloadHandler.text;
                if (enableDetailedLog) Debug.Log($"<color=yellow>[Meshy Debug]</color> ImageToImage响应: {responseText}");

                try
                {
                    var response = JsonUtility.FromJson<MeshyTaskResponse>(responseText);
                    if (!string.IsNullOrEmpty(response.result))
                    {
                        Debug.Log($"<color=cyan>[Meshy]</color> ✅ ImageToImage任务创建成功 | ID: {response.result}");
                        onTaskIdReceived?.Invoke(response.result);
                    }
                    else
                    {
                        onError?.Invoke("响应中没有task ID");
                    }
                }
                catch (Exception e)
                {
                    onError?.Invoke($"解析响应失败: {e.Message}");
                }
            }
        }

        #endregion

        #region Multi-Image to 3D

        /// <summary>
        /// 多图转3D模型（1-4张图片）
        /// </summary>
        /// <param name="imageUrls">图片URL数组（1-4张）</param>
        /// <param name="aiModel">AI模型版本</param>
        /// <param name="onTaskIdReceived">任务ID回调</param>
        /// <param name="onError">错误回调</param>
        public IEnumerator MultiImageTo3D(
            string[] imageUrls,
            string aiModel,
            Action<string> onTaskIdReceived,
            Action<string> onError)
        {
            Debug.Log($"<color=cyan>[Meshy]</color> 🎯 [MultiImageTo3D] 开始调用API | 图片数: {imageUrls.Length} | 模型: {aiModel}");
            var url = $"{API_BASE}{API_VERSION}/multi-image-to-3d";

            var requestBody = new MultiImageTo3DRequest
            {
                image_urls = imageUrls,
                ai_model = aiModel,
                enable_pbr = false,
                should_remesh = false,
                should_texture = false
            };

            var bodyJson = JsonUtility.ToJson(requestBody);
            Debug.Log($"<color=cyan>[Meshy]</color> [MultiImageTo3D] 请求URL: {url}");
            if (enableDetailedLog) Debug.Log($"<color=cyan>[Meshy]</color> [MultiImageTo3D] 请求体: {bodyJson}");

            using (var webRequest = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");
                webRequest.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                Debug.Log($"<color=cyan>[Meshy]</color> [MultiImageTo3D] 正在发送HTTP请求...");
                yield return webRequest.SendWebRequest();
                Debug.Log($"<color=cyan>[Meshy]</color> [MultiImageTo3D] HTTP请求完成 | 状态码: {webRequest.responseCode}");

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var error = $"MultiImageTo3D失败 | HTTP: {webRequest.responseCode} | {webRequest.error}";
                    Debug.LogError($"<color=red>[Meshy]</color> {error}");
                    onError?.Invoke(error);
                    yield break;
                }

                var responseText = webRequest.downloadHandler.text;
                if (enableDetailedLog) Debug.Log($"<color=yellow>[Meshy Debug]</color> MultiImageTo3D响应: {responseText}");

                try
                {
                    var response = JsonUtility.FromJson<MeshyTaskResponse>(responseText);
                    if (!string.IsNullOrEmpty(response.result))
                    {
                        Debug.Log($"<color=cyan>[Meshy]</color> ✅ MultiImageTo3D任务创建成功 | ID: {response.result}");
                        onTaskIdReceived?.Invoke(response.result);
                    }
                    else
                    {
                        onError?.Invoke("响应中没有task ID");
                    }
                }
                catch (Exception e)
                {
                    onError?.Invoke($"解析响应失败: {e.Message}");
                }
            }
        }

        #endregion

        #region Task Query

        /// <summary>
        /// 查询任务状态（通用，支持所有任务类型）
        /// </summary>
        public IEnumerator QueryTask(
            string taskId,
            string taskType,  // "image-to-3d", "remesh", "retexture"
            int maxRetries,
            float queryInterval,
            Action<MeshyTaskDetail> onSuccess,
            Action<string> onError)
        {
            int retryCount = 0;
            var queryStartTime = DateTime.Now;

            while (retryCount < maxRetries)
            {
                retryCount++;

                var url = $"{API_BASE}{API_VERSION}/{taskType}/{taskId}";

                using (var webRequest = UnityWebRequest.Get(url))
                {
                    webRequest.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                    yield return webRequest.SendWebRequest();

                    if (webRequest.result != UnityWebRequest.Result.Success)
                    {
                        var error = $"查询失败 | 第{retryCount}次 | HTTP: {webRequest.responseCode}";
                        Debug.LogError($"<color=red>[Meshy]</color> {error}");
                        onError?.Invoke(error);
                        yield break;
                    }

                    var responseText = webRequest.downloadHandler.text;
                    if (enableDetailedLog && retryCount % 5 == 1)  // 每5次打印一次日志
                    {
                        Debug.Log($"<color=yellow>[Meshy Debug]</color> Query响应: {responseText.Substring(0, Math.Min(200, responseText.Length))}...");
                    }

                    // 先解析响应（不在try中使用yield）
                    MeshyTaskDetail response = null;
                    string parseError = null;

                    try
                    {
                        response = JsonUtility.FromJson<MeshyTaskDetail>(responseText);
                    }
                    catch (Exception e)
                    {
                        parseError = $"解析失败: {e.Message}";
                    }

                    // 处理解析错误
                    if (parseError != null)
                    {
                        Debug.LogError($"<color=red>[Meshy]</color> {parseError}");
                        onError?.Invoke(parseError);
                        yield break;
                    }

                    // 处理响应（在try外部）
                    var totalElapsed = (DateTime.Now - queryStartTime).TotalSeconds;
                    Debug.Log($"<color=cyan>[Meshy]</color> 🔄 [{taskType}] 第{retryCount}/{maxRetries}次 | 状态: {response.status} | 进度: {response.progress}% | 已等待: {totalElapsed:F0}秒");

                    if (response.status == "SUCCEEDED")
                    {
                        Debug.Log($"<color=green>[Meshy]</color> ✅ [{taskType}] 完成！");
                        onSuccess?.Invoke(response);
                        yield break;
                    }
                    else if (response.status == "FAILED" || response.status == "CANCELED")
                    {
                        var error = $"任务{response.status} | 错误: {response.task_error?.message ?? "未知"}";
                        Debug.LogError($"<color=red>[Meshy]</color> {error}");
                        onError?.Invoke(error);
                        yield break;
                    }
                    else
                    {
                        // PENDING 或 IN_PROGRESS，继续等待
                        yield return new WaitForSeconds(queryInterval);
                    }
                }
            }

            var errorMsg = $"查询超时 | 已重试{maxRetries}次";
            Debug.LogError($"<color=red>[Meshy]</color> {errorMsg}");
            onError?.Invoke(errorMsg);
        }

        #endregion

        #region Download Thumbnail

        /// <summary>
        /// 下载缩略图作为预览
        /// </summary>
        public IEnumerator DownloadThumbnail(
            string thumbnailUrl,
            Action<Texture2D> onSuccess,
            Action<string> onError)
        {
            if (string.IsNullOrEmpty(thumbnailUrl))
            {
                onError?.Invoke("缩略图URL为空");
                yield break;
            }

            using (var webRequest = UnityWebRequestTexture.GetTexture(thumbnailUrl))
            {
                yield return webRequest.SendWebRequest();

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var error = $"下载缩略图失败 | {webRequest.error}";
                    Debug.LogError($"<color=red>[Meshy]</color> {error}");
                    onError?.Invoke(error);
                    yield break;
                }

                var texture = DownloadHandlerTexture.GetContent(webRequest);
                if (texture != null)
                {
                    Debug.Log($"<color=cyan>[Meshy]</color> ✅ 缩略图下载成功 | 尺寸: {texture.width}x{texture.height}");
                    onSuccess?.Invoke(texture);
                }
                else
                {
                    onError?.Invoke("下载的缩略图为空");
                }
            }
        }

        #endregion
    }

    #region 数据结构

    // 请求体结构
    [Serializable]
    public class ImageTo3DRequest
    {
        public string image_url;
        public string ai_model = "meshy-5";
        public bool enable_pbr = false;
        public bool should_remesh = false;
        public bool should_texture = true;
    }

    [Serializable]
    public class RemeshRequest
    {
        public string input_task_id;
        public int target_polycount = 30000;
        public string topology = "quad";
        public string[] target_formats;
    }

    [Serializable]
    public class RetextureRequest
    {
        public string input_task_id;
        public string text_style_prompt;
        public string image_style_url;  // 参考图像URL（支持Data URI）
        public string ai_model = "meshy-5";
        public bool enable_pbr = true;
        public bool enable_original_uv = true;
    }

    [Serializable]
    public class ImageToImageRequest
    {
        public string ai_model = "nano-banana";  // Image to Image专用模型
        public string prompt;
        public string[] reference_image_urls;  // 输入图片（支持Data URI）
        public bool generate_multi_view = false;
    }

    [Serializable]
    public class MultiImageTo3DRequest
    {
        public string[] image_urls;  // 1-4张图片URL（支持Data URI）
        public string ai_model = "meshy-5";
        public bool enable_pbr = false;
        public bool should_remesh = false;
        public bool should_texture = false;
    }

    // 响应结构
    [Serializable]
    public class MeshyTaskResponse
    {
        public string result;  // task ID
    }

    [Serializable]
    public class MeshyTaskDetail
    {
        public string id;
        public string type;
        public string status;  // PENDING, IN_PROGRESS, SUCCEEDED, FAILED, CANCELED
        public int progress;   // 0-100
        public long created_at;
        public long started_at;
        public long finished_at;
        public string thumbnail_url;
        public MeshyModelUrls model_urls;
        public string[] image_urls;  // ImageToImage返回的图片URL数组
        public MeshyTaskError task_error;
    }

    [Serializable]
    public class MeshyModelUrls
    {
        public string glb;
        public string fbx;
        public string obj;
        public string usdz;
    }

    [Serializable]
    public class MeshyTaskError
    {
        public string message;
    }

    #endregion
}
#endif
