#if UNITY_EDITOR
using System;
using System.Collections;
using UnityEngine;

namespace CropEngine
{
    /// <summary>
    /// Meshy 3D模型生成器
    /// 负责完整的三步骤工作流：Image to 3D → Remesh → Retexture
    /// </summary>
    public class MeshyGenerator
    {
        private MeshyAPI meshyAPI;
        private bool enableDebugLog;

        // 结果存储
        public Texture2D ModelPreview { get; private set; }
        public Texture2D RemeshPreview { get; private set; }
        public Texture2D TexturePreview { get; private set; }

        public string FinalGlbUrl { get; private set; }
        public string FinalFbxUrl { get; private set; }
        public string FinalObjUrl { get; private set; }

        public MeshyGenerator(string apiKey, bool enableDebugLog = false)
        {
            this.meshyAPI = new MeshyAPI(apiKey, enableDebugLog);
            this.enableDebugLog = enableDebugLog;
        }

        /// <summary>
        /// 执行完整的3D生成工作流
        /// </summary>
        public IEnumerator GenerateWorkflow(
            Texture2D sourceImage,
            string aiModel,
            int targetPolycount,
            string texturePrompt,
            Action onSuccess,
            Action<string> onError)
        {
            var startTime = DateTime.Now;
            Log($"🎨 [Meshy开始] 3D模型生成 | 模型: {aiModel} | 时间: {startTime:HH:mm:ss}");

            // 清空之前的结果
            ClearResults();

            // 转换为Base64 Data URI
            byte[] imageBytes = sourceImage.EncodeToPNG();
            string base64Image = Convert.ToBase64String(imageBytes);
            string imageDataUri = $"data:image/png;base64,{base64Image}";

            // 步骤1: Image to 3D
            string imageTo3DTaskId = null;
            Log($"📷 [步骤1/3] Image to 3D - 开始");
            yield return meshyAPI.ImageTo3D(
                imageDataUri,
                aiModel,
                taskId => imageTo3DTaskId = taskId,
                onError);

            if (string.IsNullOrEmpty(imageTo3DTaskId))
            {
                LogError("Image to 3D任务创建失败");
                yield break;
            }

            // 查询 Image to 3D 任务结果
            MeshyTaskDetail imageTo3DResult = null;
            yield return meshyAPI.QueryTask(
                imageTo3DTaskId,
                "image-to-3d",
                120,  // 最大查询次数
                3f,   // 查询间隔
                result => imageTo3DResult = result,
                onError);

            if (imageTo3DResult == null || imageTo3DResult.status != "SUCCEEDED")
            {
                LogError("Image to 3D生成失败");
                yield break;
            }

            // 下载步骤1预览
            yield return meshyAPI.DownloadThumbnail(
                imageTo3DResult.thumbnail_url,
                texture => ModelPreview = texture,
                error => LogError($"下载预览失败: {error}"));

            Log($"✅ [步骤1/3] Image to 3D完成");

            // 步骤2: Remesh
            string remeshTaskId = null;
            Log($"🔄 [步骤2/3] Remesh - 开始 | 目标多边形: {targetPolycount}");
            yield return meshyAPI.Remesh(
                imageTo3DTaskId,
                targetPolycount,
                taskId => remeshTaskId = taskId,
                onError);

            if (string.IsNullOrEmpty(remeshTaskId))
            {
                LogError("Remesh任务创建失败");
                yield break;
            }

            // 查询 Remesh 任务结果
            MeshyTaskDetail remeshResult = null;
            yield return meshyAPI.QueryTask(
                remeshTaskId,
                "remesh",
                90,
                2f,
                result => remeshResult = result,
                onError);

            if (remeshResult == null || remeshResult.status != "SUCCEEDED")
            {
                LogError("Remesh失败");
                yield break;
            }

            // 下载步骤2预览
            yield return meshyAPI.DownloadThumbnail(
                remeshResult.thumbnail_url,
                texture => RemeshPreview = texture,
                error => LogError($"下载预览失败: {error}"));

            Log($"✅ [步骤2/3] Remesh完成");

            // 步骤3: Retexture
            string retextureTaskId = null;
            Log($"🎨 [步骤3/3] Retexture - 开始");
            Log($"  Prompt: {texturePrompt}");

            // 转换原始图像为参考图
            string imageStyleUrl = $"data:image/png;base64,{base64Image}";

            yield return meshyAPI.Retexture(
                remeshTaskId,
                texturePrompt,
                imageStyleUrl,  // 使用原始图像作为参考
                aiModel,
                taskId => retextureTaskId = taskId,
                onError);

            if (string.IsNullOrEmpty(retextureTaskId))
            {
                LogError("Retexture任务创建失败");
                yield break;
            }

            // 查询 Retexture 任务结果
            MeshyTaskDetail retextureResult = null;
            yield return meshyAPI.QueryTask(
                retextureTaskId,
                "retexture",
                90,
                2f,
                result => retextureResult = result,
                onError);

            if (retextureResult == null || retextureResult.status != "SUCCEEDED")
            {
                LogError("Retexture失败");
                yield break;
            }

            // 下载步骤3预览
            yield return meshyAPI.DownloadThumbnail(
                retextureResult.thumbnail_url,
                texture => TexturePreview = texture,
                error => LogError($"下载预览失败: {error}"));

            // 保存最终模型URL
            if (retextureResult.model_urls != null)
            {
                FinalGlbUrl = retextureResult.model_urls.glb;
                FinalFbxUrl = retextureResult.model_urls.fbx;
                FinalObjUrl = retextureResult.model_urls.obj;
            }

            var endTime = DateTime.Now;
            var duration = (endTime - startTime).TotalSeconds;

            Log($"🎉 [Meshy完成] 所有步骤完成！| 耗时: {duration:F0}秒");
            Log($"📦 [最终模型]");
            Log($"  - GLB: {FinalGlbUrl}");
            Log($"  - FBX: {FinalFbxUrl}");
            Log($"  - OBJ: {FinalObjUrl}");

            onSuccess?.Invoke();
        }

        /// <summary>
        /// 清除所有结果
        /// </summary>
        public void ClearResults()
        {
            ModelPreview = null;
            RemeshPreview = null;
            TexturePreview = null;
            FinalGlbUrl = null;
            FinalFbxUrl = null;
            FinalObjUrl = null;
        }

        private void Log(string message)
        {
            if (enableDebugLog)
            {
                Debug.Log($"<color=magenta>[Meshy生成器]</color> {message}");
            }
        }

        private void LogError(string message)
        {
            Debug.LogError($"<color=red>[Meshy生成器]</color> {message}");
        }
    }
}
#endif
