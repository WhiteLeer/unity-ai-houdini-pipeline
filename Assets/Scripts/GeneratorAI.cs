#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace CropEngine
{
    /// <summary>
    /// 植物AI生成器 - Master的AI植物生成完整解决方案
    /// 集成即梦4.0图像预处理 + Meshy 3D模型生成
    /// 即挂即用,所有配置集中管理
    /// </summary>
    [ExecuteAlways]
    public class GeneratorAI : MonoBehaviour
    {
        /// <summary>
        /// 图片传输模式(即梦API)
        /// </summary>
        public enum ImageTransferMode
        {
            /// <summary>Base64直接编码传输(推荐)- 零配置</summary>
            Base64直传 = 0,

            /// <summary>ImageX URL上传 - 支持大图</summary>
            URL上传 = 1
        }

        #region 快速操作

        [PropertyOrder(-1)] [BoxGroup("快速操作", centerLabel: true)] [LabelText("资源名称")] [InfoBox("用于生成文件名和保存路径,例如:Tomato", InfoMessageType.None)]
        public string assetName = "Plant";

        [BoxGroup("快速操作")]
        [HorizontalGroup("快速操作/按钮")]
        [Button("一键初始化", ButtonSizes.Large), GUIColor(0.3f, 1f, 0.3f)]
        public void InitAll()
        {
            // 路径会自动创建,无需预先检查

            // 初始化认证
            if (string.IsNullOrEmpty(accessKeyId) || string.IsNullOrEmpty(secretAccessKey))
            {
                EditorUtility.DisplayDialog("错误", "API密钥未配置!请在Inspector中填写", "确定");
                return;
            }

            auth = new VolcEngineAuth(accessKeyId, secretAccessKey);
            jimengProcessor = new JimengProcessor(accessKeyId, secretAccessKey, enableDebugLog);
            meshyAPI = new MeshyAPI(meshyApiKey, enableDebugLog);
            Debug.Log("<color=cyan>[AI生成器]</color> ✅ 初始化完成!");
        }

        [HorizontalGroup("快速操作/按钮")]
        [Button("预处理原画", ButtonSizes.Large), GUIColor(0.4f, 0.8f, 1f)]
        [EnableIf("CanProcess")]
        public void ProcessArtwork()
        {
            if (isProcessing)
            {
                EditorUtility.DisplayDialog("提示", "当前有任务正在处理中，请等待完成后再操作!", "确定");
                return;
            }

            if (sourceArtwork == null)
            {
                EditorUtility.DisplayDialog("错误", "请先选择美术原画!", "确定");
                return;
            }

            if (auth == null || jimengProcessor == null)
            {
                EditorUtility.DisplayDialog("错误", "请先点击'一键初始化'!", "确定");
                return;
            }

            // 检查并确保纹理可读
            if (!CheckAndFixTextureReadable(sourceArtwork))
            {
                EditorUtility.DisplayDialog("错误", "无法设置纹理为可读状态!\n请手动在纹理导入设置中启用 Read/Write。", "确定");
                return;
            }

            EditorUtility.DisplayProgressBar("AI预处理", "正在处理原画...", 0.5f);

            var finalPrompt = GetFinalPrompt();
            var (width, height) = GetWidthHeight(outputSize);

            // 使用 JimengProcessor 处理图片
            isProcessing = true;

            // Editor模式下使用EditorApplication.update驱动协程
            if (!Application.isPlaying)
            {
                // 清理之前的协程状态（如果有）
                StopCurrentCoroutine();

                currentCoroutine = jimengProcessor.ProcessImage(sourceArtwork, finalPrompt, scale, width, height, maxQueryRetries, queryInterval, result =>
                {
                    processedResult = result;
                    SaveProcessedImage(result);
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("成功", "原画预处理完成!", "确定");
                }, error =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("错误", $"处理失败:\n{error}", "确定");
                });
                currentAsyncOperation = null;
                waitUntilTime = 0f;
                coroutineStack = new Stack<IEnumerator>();
                EditorApplication.update += UpdateEditorCoroutine;
            }
        }

        [BoxGroup("快速操作")]
        [HorizontalGroup("快速操作/辅助")]
        [Button("打开图片目录", ButtonSizes.Medium), GUIColor(0.9f, 0.9f, 0.9f)]
        private void OpenJimengFolder()
        {
            string path = GetPictureSavePath();
            EditorUtility.RevealInFinder(path);
        }

        [HorizontalGroup("快速操作/辅助")]
        [Button("打开模型目录", ButtonSizes.Medium), GUIColor(0.9f, 0.9f, 0.9f)]
        private void OpenMeshyFolder()
        {
            string path = GetModelSavePath();
            EditorUtility.RevealInFinder(path);
        }

        [HorizontalGroup("快速操作/辅助")]
        [Button("清除结果", ButtonSizes.Medium)]
        private void ClearResults()
        {
            processedResult = null;
            multiView1 = null;
            multiView2 = null;
            multiView3 = null;
            meshyModelPreview = null;
            meshyRemeshPreview = null;
            meshyTexturePreview = null;

            // 清除Meshy中间状态
            meshyMultiViewTaskId = null;
            meshyStep1TaskId = null;
            meshyStep2TaskId = null;
            meshyStep3TaskId = null;
            meshyStep1ModelUrls = null;
            meshyStep2ModelUrls = null;
            meshyStep3ModelUrls = null;

            // 清除Houdini预览
            houdiniLod0Preview = null;
            houdiniLod1Preview = null;
            houdiniLod2Preview = null;
            houdiniInputModel = null;

            Debug.Log("<color=cyan>[AI生成器]</color> 已清除所有结果");
        }

        [BoxGroup("快速操作/Meshy流程", centerLabel: true)]
        [HorizontalGroup("快速操作/Meshy流程/按钮")]
        [Button("1. 生成模型", ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        [EnableIf("CanStartMeshyStep1")]
        public void MeshyStep1_GenerateModel()
        {
            if (isProcessing)
            {
                EditorUtility.DisplayDialog("提示", "当前有任务正在处理中，请等待完成后再操作!", "确定");
                return;
            }

            if (processedResult == null)
            {
                EditorUtility.DisplayDialog("错误", "请先完成原画预处理!", "确定");
                return;
            }

            if (meshyAPI == null)
            {
                EditorUtility.DisplayDialog("错误", "请先点击'一键初始化'!", "确定");
                return;
            }

            // 检查并确保纹理可读
            if (!CheckAndFixTextureReadable(processedResult))
            {
                EditorUtility.DisplayDialog("错误", "无法设置纹理为可读状态!\n请手动在纹理导入设置中启用 Read/Write。", "确定");
                return;
            }

            Debug.Log("<color=cyan>[AI生成器]</color> 🚀 开始Meshy步骤1:生成3D模型");
            EditorUtility.DisplayProgressBar("Meshy 步骤1", "正在生成3D模型...", 0f);
            isProcessing = true;

            if (!Application.isPlaying)
            {
                StopCurrentCoroutine();

                currentCoroutine = MeshyStep1Coroutine(() =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("成功", "步骤1完成!可以进行步骤2:重拓扑", "确定");
                }, error =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("错误", $"步骤1失败:\n{error}", "确定");
                });
                currentAsyncOperation = null;
                waitUntilTime = 0f;
                coroutineStack = new Stack<IEnumerator>();
                EditorApplication.update += UpdateEditorCoroutine;
            }
        }

        [HorizontalGroup("快速操作/Meshy流程/按钮")]
        [Button("2. 重拓扑", ButtonSizes.Large), GUIColor(0.8f, 0.6f, 1f)]
        [EnableIf("CanStartMeshyStep2")]
        public void MeshyStep2_Remesh()
        {
            if (isProcessing)
            {
                EditorUtility.DisplayDialog("提示", "当前有任务正在处理中，请等待完成后再操作!", "确定");
                return;
            }

            if (meshyAPI == null)
            {
                EditorUtility.DisplayDialog("错误", "请先点击'一键初始化'!", "确定");
                return;
            }

            EditorUtility.DisplayProgressBar("Meshy 步骤2", "正在重拓扑优化...", 0f);
            isProcessing = true;

            if (!Application.isPlaying)
            {
                StopCurrentCoroutine();

                currentCoroutine = MeshyStep2Coroutine(() =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("成功", "步骤2完成!可以进行步骤3:生成贴图", "确定");
                }, error =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("错误", $"步骤2失败:\n{error}", "确定");
                });
                currentAsyncOperation = null;
                waitUntilTime = 0f;
                coroutineStack = new Stack<IEnumerator>();
                EditorApplication.update += UpdateEditorCoroutine;
            }
        }

        [HorizontalGroup("快速操作/Meshy流程/按钮")]
        [Button("3. 生成贴图", ButtonSizes.Large), GUIColor(1f, 0.7f, 0.3f)]
        [EnableIf("CanStartMeshyStep3")]
        public void MeshyStep3_Retexture()
        {
            if (isProcessing)
            {
                EditorUtility.DisplayDialog("提示", "当前有任务正在处理中，请等待完成后再操作!", "确定");
                return;
            }

            if (meshyAPI == null)
            {
                EditorUtility.DisplayDialog("错误", "请先点击'一键初始化'!", "确定");
                return;
            }

            if (processedResult == null)
            {
                EditorUtility.DisplayDialog("错误", "请先完成原画预处理!\n步骤3需要使用处理结果作为参考图.", "确定");
                return;
            }

            // 检查并确保纹理可读
            if (!CheckAndFixTextureReadable(processedResult))
            {
                EditorUtility.DisplayDialog("错误", "无法设置纹理为可读状态!\n请手动在纹理导入设置中启用 Read/Write。", "确定");
                return;
            }

            EditorUtility.DisplayProgressBar("Meshy 步骤3", "正在生成贴图...", 0f);
            isProcessing = true;

            if (!Application.isPlaying)
            {
                StopCurrentCoroutine();

                currentCoroutine = MeshyStep3Coroutine(() =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("成功", $"全部完成!\n\n模型已生成,可以点击'下载模型'按钮", "确定");
                }, error =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("错误", $"步骤3失败:\n{error}", "确定");
                });
                currentAsyncOperation = null;
                waitUntilTime = 0f;
                coroutineStack = new Stack<IEnumerator>();
                EditorApplication.update += UpdateEditorCoroutine;
            }
        }

        [BoxGroup("快速操作/Meshy流程")]
        [HorizontalGroup("快速操作/Meshy流程/下载")]
        [Button("下载原始模型", ButtonSizes.Large), GUIColor(0.6f, 0.9f, 0.6f)]
        [EnableIf("CanDownloadStep1Model")]
        public void DownloadStep1Model()
        {
            if (isProcessing)
            {
                EditorUtility.DisplayDialog("提示", "当前有任务正在处理中，请等待完成后再操作!", "确定");
                return;
            }

            string savePath = GetModelSavePath();
            EditorUtility.DisplayProgressBar("下载模型", "正在下载步骤1模型(初始)...", 0f);
            isProcessing = true;

            if (!Application.isPlaying)
            {
                StopCurrentCoroutine();

                currentCoroutine = DownloadSpecificStepCoroutine(1, meshyStep1ModelUrls, "step0", () =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();

                    // 自动填充到Houdini输入
                    AutoFillHoudiniInput();

                    EditorUtility.DisplayDialog("成功", $"原始模型已下载到:\n{savePath}\n\n已自动填充到Houdini输入!", "确定");
                }, error =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("错误", $"下载失败:\n{error}", "确定");
                });
                currentAsyncOperation = null;
                waitUntilTime = 0f;
                coroutineStack = new Stack<IEnumerator>();
                EditorApplication.update += UpdateEditorCoroutine;
            }
        }

        [HorizontalGroup("快速操作/Meshy流程/下载")]
        [Button("下载重拓扑", ButtonSizes.Large), GUIColor(0.7f, 0.8f, 1f)]
        [EnableIf("CanDownloadStep2Model")]
        public void DownloadStep2Model()
        {
            if (isProcessing)
            {
                EditorUtility.DisplayDialog("提示", "当前有任务正在处理中，请等待完成后再操作!", "确定");
                return;
            }

            string savePath = GetModelSavePath();
            EditorUtility.DisplayProgressBar("下载模型", "正在下载步骤2模型(重拓扑)...", 0f);
            isProcessing = true;

            if (!Application.isPlaying)
            {
                StopCurrentCoroutine();

                currentCoroutine = DownloadSpecificStepCoroutine(2, meshyStep2ModelUrls, "step1", () =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();

                    // 自动填充到Houdini输入
                    AutoFillHoudiniInput();

                    EditorUtility.DisplayDialog("成功", $"重拓扑模型已下载到:\n{savePath}\n\n已自动填充到Houdini输入!", "确定");
                }, error =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("错误", $"下载失败:\n{error}", "确定");
                });
                currentAsyncOperation = null;
                waitUntilTime = 0f;
                coroutineStack = new Stack<IEnumerator>();
                EditorApplication.update += UpdateEditorCoroutine;
            }
        }

        [HorizontalGroup("快速操作/Meshy流程/下载")]
        [Button("下载贴图模型", ButtonSizes.Large), GUIColor(1f, 0.9f, 0.4f)]
        [EnableIf("CanDownloadStep3Model")]
        public void DownloadStep3Model()
        {
            if (isProcessing)
            {
                EditorUtility.DisplayDialog("提示", "当前有任务正在处理中，请等待完成后再操作!", "确定");
                return;
            }

            string savePath = GetModelSavePath();
            EditorUtility.DisplayProgressBar("下载模型", "正在下载步骤3模型(最终贴图)...", 0f);
            isProcessing = true;

            if (!Application.isPlaying)
            {
                StopCurrentCoroutine();

                currentCoroutine = DownloadSpecificStepCoroutine(3, meshyStep3ModelUrls, "step2", () =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();

                    // 自动填充到Houdini输入
                    AutoFillHoudiniInput();

                    EditorUtility.DisplayDialog("成功", $"贴图模型已下载到:\n{savePath}\n\n已自动填充到Houdini输入!", "确定");
                }, error =>
                {
                    isProcessing = false;
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("错误", $"下载失败:\n{error}", "确定");
                });
                currentAsyncOperation = null;
                waitUntilTime = 0f;
                coroutineStack = new Stack<IEnumerator>();
                EditorApplication.update += UpdateEditorCoroutine;
            }
        }

        [BoxGroup("快速操作/Houdini减面", centerLabel: true)]
        [HorizontalGroup("快速操作/Houdini减面/按钮")]
        [Button("处理并导出LOD1+LOD2", ButtonSizes.Large), GUIColor(0.9f, 0.5f, 0.3f)]
        [EnableIf("CanProcessHoudini")]
        public void HoudiniProcessAndExport()
        {
            if (string.IsNullOrEmpty(houdiniInputModel) || !File.Exists(houdiniInputModel))
            {
                EditorUtility.DisplayDialog("错误", "请拖入输入模型或点击 '使用下载的模型' 按钮!", "确定");
                return;
            }

            string hythonPath = Path.Combine(houdiniBinPath, "hython.exe");
            if (!File.Exists(hythonPath))
            {
                EditorUtility.DisplayDialog("错误", $"Hython未找到!\n请检查Houdini路径:\n{hythonPath}", "确定");
                return;
            }

            // LOD保存到model目录（与原模型同路径）
            string modelPath = GetModelSavePath();
            string inputFile = houdiniInputModel;

            // 步骤1：清理model文件夹，删除除了选中模型外的所有文件
            try
            {
                if (Directory.Exists(modelPath))
                {
                    // 获取输入文件的绝对路径
                    string inputFileFullPath = Path.GetFullPath(inputFile);

                    // 删除所有文件（除了选中的输入模型）
                    var allFiles = Directory.GetFiles(modelPath, "*.*", SearchOption.AllDirectories);
                    foreach (var file in allFiles)
                    {
                        string fileFullPath = Path.GetFullPath(file);
                        if (fileFullPath != inputFileFullPath)
                        {
                            try
                            {
                                File.Delete(file);
                                Debug.Log($"<color=yellow>[清理]</color> 已删除: {Path.GetFileName(file)}");
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"<color=yellow>[清理]</color> 无法删除文件 {file}: {ex.Message}");
                            }
                        }
                    }

                    // 删除空文件夹
                    var allDirs = Directory.GetDirectories(modelPath, "*", SearchOption.AllDirectories);
                    foreach (var dir in allDirs)
                    {
                        if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                        {
                            try
                            {
                                Directory.Delete(dir);
                                Debug.Log($"<color=yellow>[清理]</color> 已删除空文件夹: {Path.GetFileName(dir)}");
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"<color=yellow>[清理]</color> 无法删除文件夹 {dir}: {ex.Message}");
                            }
                        }
                    }

                    AssetDatabase.Refresh();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[AI生成器]</color> 清理文件夹失败: {e.Message}");
            }

            // 生成LOD0、LOD1、LOD2文件名
            string lod0OutputFile = Path.Combine(modelPath, $"{assetName}_lod0.fbx");
            string lod1OutputFile = Path.Combine(modelPath, $"{assetName}_lod1.fbx");
            string lod2OutputFile = Path.Combine(modelPath, $"{assetName}_lod2.fbx");

            // 步骤2：重命名原模型为_lod0
            if (inputFile.StartsWith("Assets/") && File.Exists(inputFile))
            {
                try
                {
                    // 如果已经是_lod0，不需要重命名
                    if (!inputFile.EndsWith("_lod0.fbx"))
                    {
                        AssetDatabase.MoveAsset(inputFile, lod0OutputFile);
                        AssetDatabase.Refresh();
                        Debug.Log($"<color=cyan>[AI生成器]</color> ✅ 原模型已重命名为: {Path.GetFileName(lod0OutputFile)}");

                        // 更新输入文件路径
                        inputFile = lod0OutputFile;
                        houdiniInputModel = lod0OutputFile;
                    }
                    else
                    {
                        inputFile = lod0OutputFile;
                        houdiniInputModel = lod0OutputFile;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"<color=red>[AI生成器]</color> 重命名原模型失败: {e.Message}");
                }
            }

            EditorUtility.DisplayProgressBar("Houdini处理", "正在清理LOD0...", 0.1f);
            isProcessing = true;

            try
            {
                var houdiniProcessor = new HoudiniProcessor(hythonPath, enableDebugLog);

                // 临时文件用于LOD0清理
                string lod0TempFile = Path.Combine(modelPath, $"{assetName}_lod0_temp.fbx");

                // 步骤3：先处理LOD0（100%面数，只清理小碎片）
                Debug.Log($"<color=cyan>[AI生成器]</color> 🧹 开始清理LOD0（100%面数+删除小碎片）...");
                houdiniProcessor.ProcessAndExport(inputFile, lod0TempFile, 1.0f, () =>
                    {
                        Debug.Log($"<color=cyan>[AI生成器]</color> ✅ LOD0清理完成");

                        // 删除原LOD0，用清理后的替换
                        try
                        {
                            if (File.Exists(inputFile))
                            {
                                File.Delete(inputFile);
                                string metaFile = inputFile + ".meta";
                                if (File.Exists(metaFile))
                                {
                                    File.Delete(metaFile);
                                }
                            }

                            // 重命名temp文件为LOD0
                            File.Move(lod0TempFile, inputFile);
                            AssetDatabase.Refresh();

                            // 更新LOD0预览
                            houdiniLod0Preview = LoadMeshFromFBX(inputFile);

                            Debug.Log($"<color=cyan>[AI生成器]</color> ✅ LOD0已更新为清理后的版本");

                            EditorUtility.DisplayProgressBar("Houdini处理", "正在生成LOD1...", 0.4f);

                            // 步骤4：处理LOD1
                            houdiniProcessor.ProcessAndExport(inputFile, lod1OutputFile, houdiniLod1Percent, () =>
                            {
                                Debug.Log($"<color=cyan>[AI生成器]</color> ✅ LOD1 完成: {lod1OutputFile}");
                                EditorUtility.DisplayProgressBar("Houdini处理", "正在生成LOD2...", 0.75f);

                                // 然后处理LOD2
                                houdiniProcessor.ProcessAndExport(inputFile, lod2OutputFile, houdiniLod2Percent, () =>
                                {
                                    isProcessing = false;
                                    EditorUtility.ClearProgressBar();
                                    AssetDatabase.Refresh();

                                    // 加载LOD1和LOD2的Mesh作为预览
                                    houdiniLod1Preview = LoadMeshFromFBX(lod1OutputFile);
                                    houdiniLod2Preview = LoadMeshFromFBX(lod2OutputFile);

                                    Debug.Log($"<color=cyan>[AI生成器]</color> ✅ LOD2 完成: {lod2OutputFile}");

                                    // 步骤3：处理.fbm文件夹，创建texture和material文件夹
                                    try
                                    {
                                        if (Directory.Exists(modelPath))
                                        {
                                            string texturePath = Path.Combine(modelPath, "texture");
                                            string materialPath = Path.Combine(modelPath, "material");

                                            // 创建texture文件夹（如果不存在）
                                            if (!Directory.Exists(texturePath))
                                            {
                                                Directory.CreateDirectory(texturePath);
                                            }

                                            // 处理所有子文件夹，只保留texture和material
                                            var subDirs = Directory.GetDirectories(modelPath);
                                            foreach (var subDir in subDirs)
                                            {
                                                string dirName = Path.GetFileName(subDir);

                                                // 跳过texture和material文件夹
                                                if (dirName.Equals("texture", StringComparison.OrdinalIgnoreCase) || dirName.Equals("material", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    continue;
                                                }

                                                // 其他任何文件夹都移动内容到texture，然后删除
                                                try
                                                {
                                                    // 移动文件夹中的所有文件到texture
                                                    var files = Directory.GetFiles(subDir, "*.*", SearchOption.AllDirectories);
                                                    foreach (var file in files)
                                                    {
                                                        string fileName = Path.GetFileName(file);
                                                        string destFile = Path.Combine(texturePath, fileName);

                                                        // 如果目标文件已存在，先删除
                                                        if (File.Exists(destFile))
                                                        {
                                                            File.Delete(destFile);
                                                        }

                                                        File.Move(file, destFile);
                                                    }

                                                    // 删除文件夹及其.meta文件
                                                    string metaFile = subDir + ".meta";

                                                    Directory.Delete(subDir, true);

                                                    if (File.Exists(metaFile))
                                                    {
                                                        File.Delete(metaFile);
                                                    }

                                                    Debug.Log($"<color=cyan>[AI生成器]</color> ✅ 已移动 {dirName} 内容到texture并删除原文件夹");
                                                }
                                                catch (Exception ex)
                                                {
                                                    Debug.LogWarning($"<color=yellow>[处理文件夹]</color> 删除 {dirName} 失败: {ex.Message}");
                                                }
                                            }

                                            AssetDatabase.Refresh();

                                            // 创建material文件夹
                                            if (!Directory.Exists(materialPath))
                                            {
                                                Directory.CreateDirectory(materialPath);
                                                AssetDatabase.Refresh();
                                                Debug.Log($"<color=cyan>[AI生成器]</color> ✅ 已创建material文件夹");
                                            }

                                            // 创建材质
                                            string createdMaterialPath = CreateMaterialWithTextures(materialPath, texturePath);

                                            // 在场景中创建物体
                                            if (!string.IsNullOrEmpty(createdMaterialPath))
                                            {
                                                CreateSceneObjectWithLOD0(lod0OutputFile, createdMaterialPath);
                                            }
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        Debug.LogError($"<color=red>[AI生成器]</color> 处理文件夹失败: {e.Message}");
                                    }

                                    EditorUtility.DisplayDialog("成功", $"Houdini减面完成!\n\nLOD0: {assetName}_lod0.fbx\nLOD1: {assetName}_lod1.fbx\nLOD2: {assetName}_lod2.fbx\n\n已保存到:\n{modelPath}", "确定");
                                }, error =>
                                {
                                    isProcessing = false;
                                    EditorUtility.ClearProgressBar();
                                    EditorUtility.DisplayDialog("错误", $"LOD2处理失败:\n{error}", "确定");
                                });
                            }, error =>
                            {
                                isProcessing = false;
                                EditorUtility.ClearProgressBar();
                                EditorUtility.DisplayDialog("错误", $"LOD1处理失败:\n{error}", "确定");
                            });
                        }
                        catch (Exception e)
                        {
                            isProcessing = false;
                            EditorUtility.ClearProgressBar();
                            EditorUtility.DisplayDialog("错误", $"LOD0文件替换失败:\n{e.Message}", "确定");
                        }
                    }, error =>
                    {
                        isProcessing = false;
                        EditorUtility.ClearProgressBar();
                        EditorUtility.DisplayDialog("错误", $"LOD0清理失败:\n{error}", "确定");
                    });
            }
            catch (Exception e)
            {
                isProcessing = false;
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("错误", $"调用Houdini时出错:\n{e.Message}", "确定");
            }
        }

        [BoxGroup("快速操作")]
        [ShowInInspector, ReadOnly, HideLabel]
        [GUIColor("GetStatusColor")]
        private string ProcessingStatus
        {
            get
            {
                if (auth == null) return "❌ 未初始化";
                if (isProcessing) return "⏳ 处理中...";
                return "✅ 就绪";
            }
        }

        private Color GetStatusColor()
        {
            if (auth == null) return new Color(1f, 0.5f, 0.5f);
            if (isProcessing) return new Color(1f, 1f, 0.5f);
            return new Color(0.5f, 1f, 0.5f);
        }

        private bool CanProcess => sourceArtwork != null && auth != null && !isProcessing;
        private bool CanStartMeshyStep1 => processedResult != null && !string.IsNullOrEmpty(meshyApiKey) && !isProcessing;
        private bool CanStartMeshyStep2 => !string.IsNullOrEmpty(meshyStep1TaskId) && meshyModelPreview != null && !isProcessing;
        private bool CanStartMeshyStep3 => !string.IsNullOrEmpty(meshyStep2TaskId) && meshyRemeshPreview != null && processedResult != null && !isProcessing;
        private bool CanDownloadStep1Model => !isProcessing && meshyStep1ModelUrls != null;
        private bool CanDownloadStep2Model => !isProcessing && meshyStep2ModelUrls != null;
        private bool CanDownloadStep3Model => !isProcessing && meshyStep3ModelUrls != null;

        #endregion

        #region JiMeng 处理

        [BoxGroup("JiMeng 处理", centerLabel: true)] [TitleGroup("JiMeng 处理/原画处理", "即梦4.0原画预处理", alignment: TitleAlignments.Centered, horizontalLine: true)] [HorizontalGroup("JiMeng 处理/原画处理/Split", 0.5f)] [VerticalGroup("JiMeng 处理/原画处理/Split/原画"), LabelText("美术原画"), PreviewField(120, ObjectFieldAlignment.Center), Required("请选择原画")]
        public Texture2D sourceArtwork;

        [VerticalGroup("JiMeng 处理/原画处理/Split/结果"), LabelText("处理结果"), PreviewField(120, ObjectFieldAlignment.Center), OnValueChanged("OnProcessedResultChanged")]
        public Texture2D processedResult;

        [FoldoutGroup("JiMeng 处理/即梦Prompt", false)] [LabelText("文本指令")] [TextArea(2, 3)]
        public string customPrompt = "物体居中,保持主体结构,背景改为纯白色,移除果实和花朵,移除冗余物件,移除 UI,移除水印,3D 手绘卡通动漫风格,细节圆润";

        [FoldoutGroup("JiMeng 处理/即梦Prompt")]
        [HorizontalGroup("JiMeng 处理/即梦Prompt/按钮")]
        [Button("使用默认", ButtonSizes.Medium), GUIColor(0.7f, 0.9f, 1f)]
        private void UseDefaultPrompt()
        {
            customPrompt = GetDefaultPrompt();
            Debug.Log("<color=cyan>[AI生成器]</color> 已应用默认Prompt");
        }

        [HorizontalGroup("JiMeng 处理/即梦Prompt/按钮")]
        [Button("清空", ButtonSizes.Medium), GUIColor(1f, 0.9f, 0.9f)]
        private void ClearPrompt()
        {
            customPrompt = "";
            Debug.Log("<color=cyan>[AI生成器]</color> 已清空");
        }

        [FoldoutGroup("JiMeng 处理/即梦配置", false)] [LabelText("API Key ID")]
        public string accessKeyId = "";  // 请填入您的火山引擎 Access Key ID

        [FoldoutGroup("JiMeng 处理/即梦配置")] [LabelText("API Key Secret")]
        public string secretAccessKey = "";  // 请填入您的火山引擎 Secret Access Key

        [FoldoutGroup("JiMeng 处理/即梦配置")] [HorizontalGroup("JiMeng 处理/即梦配置/参数")] [LabelText("创作自由度"), LabelWidth(80)] [PropertyRange(0f, 1f)] [Tooltip("0.0=严格保留原图 | 0.5=平衡(推荐) | 1.0=自由创作")]
        public float scale = 0.5f;

        [HorizontalGroup("JiMeng 处理/即梦配置/参数"), LabelWidth(80)] [LabelText("输出分辨率")] [ValueDropdown("GetSizeOptions")]
        public string outputSize = "2048x2048";

        [FoldoutGroup("JiMeng 处理/即梦配置")] [LabelText("ImageX Domain")] [Tooltip("URL模式下使用的ImageX域名")]
        public string imageXDomain = "7hfevl77xd.veimagex-pub.cn-north-1.volces.com";

        #endregion

        #region Meshy 处理

        [BoxGroup("Meshy 处理", centerLabel: true)] [TitleGroup("Meshy 处理/质量设置", "生成质量选项", alignment: TitleAlignments.Centered, horizontalLine: true)] [LabelText("使用多视图生成")] [InfoBox("启用后会先生成3个角度的视图，再用多视图生成3D模型，质量更高但耗时更久", InfoMessageType.None)] [ToggleLeft]
        public bool useMultiView = true;

        [TitleGroup("Meshy 处理/多视图预览", "多视图生成结果（3个角度）", alignment: TitleAlignments.Centered, horizontalLine: true)] [ShowIf("useMultiView")] [HorizontalGroup("Meshy 处理/多视图预览/图", 0.333f)] [VerticalGroup("Meshy 处理/多视图预览/图/视图1"), LabelText("视图1"), PreviewField(120, ObjectFieldAlignment.Center)]
        public Texture2D multiView1;

        [ShowIf("useMultiView")] [VerticalGroup("Meshy 处理/多视图预览/图/视图2"), LabelText("视图2"), PreviewField(120, ObjectFieldAlignment.Center)]
        public Texture2D multiView2;

        [ShowIf("useMultiView")] [VerticalGroup("Meshy 处理/多视图预览/图/视图3"), LabelText("视图3"), PreviewField(120, ObjectFieldAlignment.Center)]
        public Texture2D multiView3;

        [TitleGroup("Meshy 处理/模型生成", "Meshy三步骤3D模型生成预览", alignment: TitleAlignments.Centered, horizontalLine: true)] [HorizontalGroup("Meshy 处理/模型生成/图", 0.333f)] [VerticalGroup("Meshy 处理/模型生成/图/步骤1"), LabelText("1. 初始模型"), PreviewField(120, ObjectFieldAlignment.Center), OnValueChanged("OnMeshyStep1PreviewChanged")]
        public Texture2D meshyModelPreview;

        [VerticalGroup("Meshy 处理/模型生成/图/步骤2"), LabelText("2. 重拓扑"), PreviewField(120, ObjectFieldAlignment.Center), OnValueChanged("OnMeshyStep2PreviewChanged")]
        public Texture2D meshyRemeshPreview;

        [VerticalGroup("Meshy 处理/模型生成/图/步骤3"), LabelText("3. 最终贴图"), PreviewField(120, ObjectFieldAlignment.Center), OnValueChanged("OnMeshyStep3PreviewChanged")]
        public Texture2D meshyTexturePreview;

        [FoldoutGroup("Meshy 处理/Meshy Prompt", false)] [LabelText("纹理生成提示")] [TextArea(2, 3)] [InfoBox("可选,留空则不使用文本提示", InfoMessageType.None)]
        public string meshyTexturePrompt = "";

        [FoldoutGroup("Meshy 处理/Meshy配置", false)] [LabelText("API Key")]
        public string meshyApiKey = "msy_gJIcm7PQAtsd0zx1BWxQqSkrzWyYm2SKXYvL";

        [FoldoutGroup("Meshy 处理/Meshy配置")] [HorizontalGroup("Meshy 处理/Meshy配置/参数")] [LabelText("AI模型"), LabelWidth(80)] [ValueDropdown("GetMeshyModelOptions")] [Tooltip("meshy-5: 成本低(推荐) | latest: 效果更好但成本高")]
        public string meshyAiModel = "meshy-5";

        [HorizontalGroup("Meshy 处理/Meshy配置/参数"), LabelWidth(80)] [LabelText("目标多边形数")] [PropertyRange(100, 300000)] [Tooltip("重拓扑时的目标面数,推荐3000-30000")]
        public int meshyTargetPolycount = 3000;

        #endregion

        #region Houdini 处理

        [BoxGroup("Houdini 处理", centerLabel: true)] [TitleGroup("Houdini 处理/模型优化", "Houdini PolyReduce 模型减面", alignment: TitleAlignments.Centered, horizontalLine: true)] [HorizontalGroup("Houdini 处理/模型优化/图", 0.333f)] [VerticalGroup("Houdini 处理/模型优化/图/LOD0"), LabelText("LOD0 (原始)"), PreviewField(120, ObjectFieldAlignment.Center)]
        public UnityEngine.Mesh houdiniLod0Preview;

        [VerticalGroup("Houdini 处理/模型优化/图/LOD1"), LabelText("LOD1"), PreviewField(120, ObjectFieldAlignment.Center)]
        public UnityEngine.Mesh houdiniLod1Preview;

        [VerticalGroup("Houdini 处理/模型优化/图/LOD2"), LabelText("LOD2"), PreviewField(120, ObjectFieldAlignment.Center)]
        public UnityEngine.Mesh houdiniLod2Preview;

        [FoldoutGroup("Houdini 处理/Houdini配置", false)] [LabelText("Houdini安装路径")] [FolderPath] [Tooltip("Houdini安装目录的bin文件夹路径")]
        public string houdiniBinPath = "C:/Program Files/Side Effects Software/Houdini 20.5.487/bin";

        [FoldoutGroup("Houdini 处理/Houdini配置")] [LabelText("输入模型路径")] [Sirenix.OdinInspector.FilePath] [Tooltip("拖入要处理的模型文件路径")]
        public string houdiniInputModel = "";

        [FoldoutGroup("Houdini 处理/Houdini配置")] [HorizontalGroup("Houdini 处理/Houdini配置/减面参数")] [LabelText("LOD1 减面系数"), LabelWidth(100)] [PropertyRange(0.01f, 1f)] [Tooltip("LOD1保留的面数百分比,例如0.5表示减少到50%")]
        public float houdiniLod1Percent = 0.5f;

        [HorizontalGroup("Houdini 处理/Houdini配置/减面参数"), LabelWidth(100)] [LabelText("LOD2 减面系数")] [PropertyRange(0.01f, 1f)] [Tooltip("LOD2保留的面数百分比,例如0.25表示减少到25%")]
        public float houdiniLod2Percent = 0.25f;

        [FoldoutGroup("Houdini 处理/Houdini配置")]
        [HorizontalGroup("Houdini 处理/Houdini配置/按钮")]
        [Button("使用下载的模型", ButtonSizes.Medium), GUIColor(0.7f, 0.9f, 1f)]
        [EnableIf("CanAutoFillHoudiniInput")]
        public void AutoFillHoudiniInput()
        {
            // 查找最新下载的FBX文件（优先选择_lod0，否则选择非LOD文件）
            string modelPath = GetModelSavePath();
            if (Directory.Exists(modelPath))
            {
                var fbxFiles = Directory.GetFiles(modelPath, "*.fbx");
                if (fbxFiles.Length > 0)
                {
                    // 优先查找_lod0文件
                    var lod0File = fbxFiles.FirstOrDefault(f => f.Contains("_lod0.fbx"));
                    string latestFile = null;

                    if (lod0File != null)
                    {
                        latestFile = lod0File;
                    }
                    else
                    {
                        // 排除_lod1和_lod2，按修改时间排序取最新的
                        var nonLodFiles = fbxFiles.Where(f => !f.Contains("_lod1.fbx") && !f.Contains("_lod2.fbx")).ToArray();
                        if (nonLodFiles.Length > 0)
                        {
                            latestFile = nonLodFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                        }
                        else
                        {
                            // 如果只有LOD文件，则取最新的
                            latestFile = fbxFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                        }
                    }

                    if (latestFile != null)
                    {
                        houdiniInputModel = latestFile;

                        // 更新LOD0预览
                        if (latestFile.StartsWith("Assets/"))
                        {
                            houdiniLod0Preview = LoadMeshFromFBX(latestFile);
                        }

                        Debug.Log($"<color=cyan>[AI生成器]</color> 已自动填入: {Path.GetFileName(latestFile)}");
                    }
                }
            }
        }

        [HorizontalGroup("Houdini 处理/Houdini配置/按钮")]
        [Button("打开输出目录", ButtonSizes.Medium), GUIColor(0.9f, 0.9f, 0.9f)]
        private void OpenHoudiniFolder()
        {
            // LOD文件现在保存在model目录（与原模型同路径）
            string path = GetModelSavePath();
            EditorUtility.RevealInFinder(path);
        }

        private bool CanAutoFillHoudiniInput
        {
            get
            {
                if (isProcessing) return false;
                string modelPath = Path.Combine("Assets/Res/CropEngine/AI", GetTodayDateString(), assetName, "model");
                return Directory.Exists(modelPath);
            }
        }

        private bool CanProcessHoudini => !isProcessing && !string.IsNullOrEmpty(houdiniInputModel) && File.Exists(houdiniInputModel) && Directory.Exists(houdiniBinPath);

        // 预览点击事件 - 在Project窗口中选中资源
        private void OnProcessedResultChanged()
        {
            if (processedResult != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(processedResult);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
            }
        }


        private void OnMeshyStep1PreviewChanged()
        {
            if (meshyModelPreview != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(meshyModelPreview);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
            }
        }

        private void OnMeshyStep2PreviewChanged()
        {
            if (meshyRemeshPreview != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(meshyRemeshPreview);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
            }
        }

        private void OnMeshyStep3PreviewChanged()
        {
            if (meshyTexturePreview != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(meshyTexturePreview);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
            }
        }

        #endregion

        #region 内部参数和Helper方法

        // ==================== 路径Helper方法 ====================

        /// <summary>获取今日日期字符串(格式:yyyyMMdd)</summary>
        private string GetTodayDateString() => DateTime.Now.ToString("yyyyMMdd");

        /// <summary>获取图片保存路径</summary>
        private string GetPictureSavePath()
        {
            string path = Path.Combine("Assets/Res/CropEngine/AI", GetTodayDateString(), assetName, "pic");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        /// <summary>获取模型保存路径</summary>
        private string GetModelSavePath()
        {
            string path = Path.Combine("Assets/Res/CropEngine/AI", GetTodayDateString(), assetName, "model");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        /// <summary>
        /// [已废弃] 获取LOD模型保存路径
        /// LOD文件现在保存在model目录（与原模型同路径），不再使用lod子文件夹
        /// </summary>
        [Obsolete("LOD文件现在保存在model目录，请使用GetModelSavePath()")]
        private string GetLodSavePath()
        {
            string path = Path.Combine("Assets/Res/CropEngine/AI", GetTodayDateString(), assetName, "model", "lod");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        /// <summary>
        /// 从FBX文件中加载Mesh（避免加载GameObject导致的材质/shader问题）
        /// </summary>
        private UnityEngine.Mesh LoadMeshFromFBX(string fbxPath)
        {
            if (string.IsNullOrEmpty(fbxPath) || !fbxPath.StartsWith("Assets/"))
                return null;

            try
            {
                // 加载FBX中的所有资源
                UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);

                // 查找第一个Mesh（通常FBX只有一个主Mesh）
                UnityEngine.Mesh mesh = assets.OfType<UnityEngine.Mesh>().FirstOrDefault();

                if (mesh != null)
                {
                    Debug.Log($"<color=cyan>[加载Mesh]</color> ✅ 已从FBX加载Mesh: {mesh.name} | 顶点数: {mesh.vertexCount}");
                }
                else
                {
                    Debug.LogWarning($"<color=yellow>[加载Mesh]</color> ⚠️ FBX中未找到Mesh: {fbxPath}");
                }

                return mesh;
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[加载Mesh]</color> 加载失败: {e.Message}");
                return null;
            }
        }

        // ==================== 即梦4.0内部参数 ====================

        /// <summary>即梦默认Prompt模板</summary>
        private const string DEFAULT_JIMENG_PROMPT = "物体居中,保持主体结构,背景改为纯白色,移除果实和花朵,移除冗余物件,移除 UI,移除水印,3D 手绘卡通动漫风格,细节圆润";

        /// <summary>默认创作自由度</summary>
        private float defaultScale = 0.5f;

        /// <summary>默认文件大小限制</summary>
        private int defaultSize = 4194304;

        /// <summary>是否强制单图输出</summary>
        private bool defaultForceSingle = true;

        /// <summary>API请求超时时间(秒)</summary>
        private int timeoutSeconds = 120;

        /// <summary>最大查询重试次数</summary>
        private int maxQueryRetries = 90;

        /// <summary>查询间隔(秒)</summary>
        private float queryInterval = 2f;

        /// <summary>图片传输模式</summary>
        private ImageTransferMode transferMode = ImageTransferMode.Base64直传;

        /// <summary>ImageX服务ID</summary>
        private string imageXServiceId = "7hfevl77xd";

        // ==================== 即梦Helper方法 ====================

        /// <summary>检查即梦Access Key是否为空</summary>
        private bool IsAccessKeyEmpty => string.IsNullOrEmpty(accessKeyId);

        /// <summary>检查即梦Secret Key是否为空</summary>
        private bool IsSecretKeyEmpty => string.IsNullOrEmpty(secretAccessKey);

        /// <summary>获取分辨率下拉选项</summary>
        private IEnumerable<ValueDropdownItem<string>> GetSizeOptions()
        {
            return new ValueDropdownItem<string>[]
            {
                new ValueDropdownItem<string>("2K (2048x2048)", "2048x2048"),
                new ValueDropdownItem<string>("4K (4096x4096)", "4096x4096")
            };
        }

        /// <summary>获取即梦默认Prompt(带分辨率)</summary>
        private string GetDefaultPrompt()
        {
            return $"{DEFAULT_JIMENG_PROMPT}, {outputSize}";
        }

        /// <summary>获取最终使用的即梦Prompt</summary>
        private string GetFinalPrompt()
        {
            // 如果Prompt中没有包含分辨率(格式:2048x2048),则自动拼接
            if (!customPrompt.Contains("x"))
            {
                return $"{customPrompt}, {outputSize}";
            }

            return customPrompt;
        }

        // ==================== Meshy Helper方法 ====================

        /// <summary>检查Meshy API Key是否为空</summary>
        private bool IsMeshyKeyEmpty => string.IsNullOrEmpty(meshyApiKey);

        /// <summary>获取Meshy AI模型下拉选项</summary>
        private IEnumerable<ValueDropdownItem<string>> GetMeshyModelOptions()
        {
            return new ValueDropdownItem<string>[]
            {
                new ValueDropdownItem<string>("Meshy-5 (推荐)", "meshy-5"),
                new ValueDropdownItem<string>("Latest (最新)", "latest")
            };
        }

        /// <summary>获取最终使用的Meshy Prompt(可能为空)</summary>
        private string GetFinalMeshyPrompt()
        {
            // 如果为空就返回空字符串,不使用默认值
            return meshyTexturePrompt;
        }

        #endregion

        #region 全局配置

        [FoldoutGroup("全局配置", false)] [LabelText("详细日志")] [ToggleLeft] [Tooltip("开启后显示详细的API调用、图片上传和调试信息")]
        public bool enableDebugLog = false;

        #endregion

        #region 只读API配置(硬编码)

        private const string API_ENDPOINT = "https://visual.volcengineapi.com";
        private const string API_VERSION = "2022-08-31";
        private const string SUBMIT_ACTION = "CVSync2AsyncSubmitTask";
        private const string QUERY_ACTION = "CVSync2AsyncGetResult";
        private const string IMAGEX_ENDPOINT = "https://imagex.volcengineapi.com";
        private const string REQ_KEY = "jimeng_t2i_v40";

        #endregion

        #region 运行时状态

        private VolcEngineAuth auth;
        private JimengProcessor jimengProcessor;
        private MeshyAPI meshyAPI;

        private bool isProcessing = false;
        private IEnumerator currentCoroutine;
        private UnityEngine.AsyncOperation currentAsyncOperation;
        private float waitUntilTime = 0f;
        private Stack<IEnumerator> coroutineStack;

        // Meshy流程中间状态
        private string meshyMultiViewTaskId; // ImageToImage多视图生成 task ID
        private string[] multiViewImageUrls; // 多视图生成的3张图片URL
        private string meshyStep1TaskId; // Image to 3D 或 Multi-Image to 3D task ID
        private string meshyStep2TaskId; // Remesh task ID
        private string meshyStep3TaskId; // Retexture task ID

        // 每个步骤的模型URL
        private MeshyModelUrls meshyStep1ModelUrls;
        private MeshyModelUrls meshyStep2ModelUrls;
        private MeshyModelUrls meshyStep3ModelUrls;

        #endregion

        #region 核心处理流程

        private IEnumerator PreprocessCoroutine(Texture2D sourceArtwork, string finalPrompt, float finalScale, int finalSize, int width, int height, Action<Texture2D> onSuccess, Action<string> onError)
        {
            isProcessing = true;
            var startTime = DateTime.Now;

            Debug.Log($"<color=magenta>[模式检查]</color> transferMode = {transferMode}");
            Log($"📤 [开始预处理] 即梦4.0 | 模式: {(transferMode == ImageTransferMode.Base64直传 ? "Base64直传" : "URL上传")} | 时间: {startTime:HH:mm:ss}");

            Log($"✅ [参数] prompt: {(finalPrompt.Length > 40 ? finalPrompt.Substring(0, 40) + "..." : finalPrompt)} | scale: {finalScale:F2} | size: {finalSize} | 尺寸: {width}x{height}");

            // 准备请求
            PlantPreprocessRequest request = null;

            Debug.Log($"<color=magenta>[判断分支]</color> 即将进入: {(transferMode == ImageTransferMode.Base64直传 ? "Base64分支" : "URL分支")}");

            if (transferMode == ImageTransferMode.Base64直传)
            {
                // Base64模式
                Log("🎯 [Base64模式] 编码图片...");

                // 检查格式，如果是压缩格式，创建未压缩副本
                Texture2D textureToEncode = sourceArtwork;
                var format = sourceArtwork.format;
                bool isCompressed = !(format == TextureFormat.RGBA32 || format == TextureFormat.ARGB32 ||
                                     format == TextureFormat.RGB24 || format == TextureFormat.Alpha8 ||
                                     format == TextureFormat.RGBAFloat || format == TextureFormat.RGBAHalf);

                if (isCompressed)
                {
                    Debug.LogWarning($"<color=yellow>[Base64模式]</color> ⚠️ 检测到压缩格式 {format}，创建未压缩副本...");
                    textureToEncode = CreateUncompressedCopy(sourceArtwork);
                    if (textureToEncode == null)
                    {
                        LogError("创建未压缩副本失败！");
                        onError?.Invoke("无法创建未压缩纹理副本");
                        yield break;
                    }
                }

                byte[] imageBytes = textureToEncode.EncodeToPNG();
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    LogError("图片编码失败！纹理格式可能不支持。");
                    onError?.Invoke("图片编码失败，请检查纹理格式设置。");
                    yield break;
                }
                string base64Image = Convert.ToBase64String(imageBytes);
                Log($"✅ [Base64] 完成 | 大小: {imageBytes.Length / 1024}KB");

                request = new PlantPreprocessRequest
                {
                    req_key = REQ_KEY,
                    binary_data_base64 = new string[] { base64Image },
                    prompt = finalPrompt,
                    scale = finalScale,
                    size = finalSize,
                    width = width,
                    height = height,
                    force_single = defaultForceSingle,
                    min_ratio = 1f / 3f,
                    max_ratio = 3f
                };
            }
            else
            {
                // URL模式
                Log("📤 [URL模式] 上传到ImageX...");
                string imageUrl = null;
                var uploader = new ImageXUploader(accessKeyId, secretAccessKey, imageXServiceId, imageXDomain, IMAGEX_ENDPOINT, enableDebugLog);

                yield return uploader.UploadImage(sourceArtwork, url => imageUrl = url, error =>
                {
                    LogError($"上传失败: {error}");
                    onError?.Invoke($"上传失败: {error}");
                });

                if (string.IsNullOrEmpty(imageUrl))
                {
                    LogError("未能获取图片URL");
                    isProcessing = false;
                    yield break;
                }

                Log($"✅ [ImageX] URL: {imageUrl.Substring(0, Math.Min(80, imageUrl.Length))}...");

                request = new PlantPreprocessRequest
                {
                    req_key = REQ_KEY,
                    image_urls = new string[] { imageUrl },
                    prompt = finalPrompt,
                    scale = finalScale,
                    size = finalSize,
                    width = width,
                    height = height,
                    force_single = defaultForceSingle,
                    min_ratio = 1f / 3f,
                    max_ratio = 3f
                };
            }

            // 提交任务
            string taskId = null;
            yield return SubmitTask(request, id => taskId = id, onError);

            if (string.IsNullOrEmpty(taskId))
            {
                LogError("提交任务失败");
                isProcessing = false;
                yield break;
            }

            // 查询结果
            Texture2D resultTexture = null;
            yield return QueryTaskResult(taskId, texture => resultTexture = texture, onError);

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

            isProcessing = false;
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
                webRequest.timeout = timeoutSeconds;

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
                try
                {
                    response = JsonUtility.FromJson<SubmitTaskResponse>(responseText);
                }
                catch (Exception e)
                {
                    LogError($"解析响应失败: {e.Message}");
                    onError?.Invoke($"解析响应失败: {e.Message}");
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

        private IEnumerator QueryTaskResult(string taskId, Action<Texture2D> onSuccess, Action<string> onError)
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

            while (retryCount < maxQueryRetries)
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
                    webRequest.timeout = timeoutSeconds;

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
                    try
                    {
                        response = JsonUtility.FromJson<TaskResultResponse>(responseText);
                    }
                    catch (Exception e)
                    {
                        LogError($"解析失败: {e.Message}");
                        onError?.Invoke($"解析失败: {e.Message}");
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
                    Log($"🔄 [查询] 第{retryCount}/{maxQueryRetries}次 | 状态: {response.data.status} | 已等待: {totalElapsed:F0}秒");

                    if (response.data.status == "done")
                    {
                        if (response.data.image_urls != null && response.data.image_urls.Length > 0)
                        {
                            Log($"🎨 [下载] 从URL获取结果...");
                            yield return DownloadTexture(response.data.image_urls[0], onSuccess, onError);
                        }
                        else if (response.data.binary_data_base64 != null && response.data.binary_data_base64.Length > 0)
                        {
                            Log($"🎨 [解码] 从Base64获取结果...");
                            var texture = Base64ToTexture(response.data.binary_data_base64[0]);
                            if (texture != null)
                            {
                                Log($"✅ [解码] 成功 | 尺寸: {texture.width}x{texture.height}");
                                onSuccess?.Invoke(texture);
                            }
                            else
                            {
                                LogError("Base64解码失败");
                                onError?.Invoke("Base64解码失败");
                            }
                        }
                        else
                        {
                            LogError("任务完成但没有返回图片数据");
                            onError?.Invoke("任务完成但没有返回图片数据");
                        }

                        yield break;
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

            var errorMsg = $"查询超时 | 已重试{maxQueryRetries}次";
            LogError(errorMsg);
            onError?.Invoke(errorMsg);
        }

        private IEnumerator DownloadTexture(string imageUrl, Action<Texture2D> onSuccess, Action<string> onError)
        {
            using (var webRequest = UnityWebRequestTexture.GetTexture(imageUrl))
            {
                webRequest.timeout = timeoutSeconds;
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

        #endregion

        #region 处理工作流

        /// <summary>
        /// Meshy步骤1: Image to 3D
        /// </summary>
        private IEnumerator MeshyStep1Coroutine(Action onSuccess, Action<string> onError)
        {
            Debug.Log("<color=cyan>[AI生成器]</color> 📷 [Meshy步骤1] 进入协程");

            if (useMultiView)
            {
                Log($"📷 [Meshy步骤1] Multi-View to 3D - 开始（多视图模式）");
            }
            else
            {
                Log($"📷 [Meshy步骤1] Image to 3D - 开始（单图模式）");
            }

            // 检查并修复纹理可读性
            if (!CheckAndFixTextureReadable(processedResult))
            {
                LogError("无法设置纹理为可读状态！请检查纹理导入设置。");
                onError?.Invoke("纹理不可读，请检查导入设置。");
                yield break;
            }

            // 重新加载纹理引用（重要：CheckAndFixTextureReadable可能修改了Asset，需要重新加载）
            string assetPath = AssetDatabase.GetAssetPath(processedResult);
            if (!string.IsNullOrEmpty(assetPath))
            {
                processedResult = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                Debug.Log($"<color=yellow>[Meshy步骤1]</color> 已重新加载纹理引用: {processedResult.name}");
            }

            // 打印纹理格式信息
            Debug.Log($"<color=yellow>[Meshy步骤1]</color> 纹理信息: format={processedResult.format}, isReadable={processedResult.isReadable}, width={processedResult.width}, height={processedResult.height}");

            // 检查格式，如果是压缩格式，创建未压缩副本
            Texture2D textureToEncode = processedResult;
            var format = processedResult.format;
            bool isCompressed = !(format == TextureFormat.RGBA32 || format == TextureFormat.ARGB32 ||
                                 format == TextureFormat.RGB24 || format == TextureFormat.Alpha8 ||
                                 format == TextureFormat.RGBAFloat || format == TextureFormat.RGBAHalf);

            if (isCompressed)
            {
                Debug.LogWarning($"<color=yellow>[Meshy步骤1]</color> ⚠️ 检测到压缩格式 {format}，创建未压缩副本...");
                textureToEncode = CreateUncompressedCopy(processedResult);
                if (textureToEncode == null)
                {
                    LogError("创建未压缩副本失败！");
                    onError?.Invoke("无法创建未压缩纹理副本");
                    yield break;
                }
            }

            // 转换为Base64 Data URI
            Debug.Log("<color=cyan>[AI生成器]</color> 📷 [Meshy步骤1] 正在编码图片...");
            byte[] imageBytes = textureToEncode.EncodeToPNG();
            if (imageBytes == null || imageBytes.Length == 0)
            {
                LogError("图片编码失败！纹理格式可能不支持。");
                onError?.Invoke("图片编码失败，请检查纹理格式设置。");
                yield break;
            }
            string base64Image = Convert.ToBase64String(imageBytes);
            string imageDataUri = $"data:image/png;base64,{base64Image}";
            Debug.Log($"<color=cyan>[AI生成器]</color> 📷 [Meshy步骤1] 图片已编码,大小: {imageBytes.Length / 1024}KB");

            // ========== 多视图模式 ==========
            if (useMultiView)
            {
                // 步骤1.1: 生成多视图
                Log($"🎨 [Meshy步骤1.1] 开始生成多视图（3个角度）...");
                string multiViewTaskId = null;
                yield return meshyAPI.ImageToImage(
                    imageDataUri,
                    "generate multiple views of this object, showing it from different angles",
                    true, // generate_multi_view = true
                    meshyAiModel,
                    id => multiViewTaskId = id,
                    onError);

                if (string.IsNullOrEmpty(multiViewTaskId))
                {
                    LogError("多视图生成任务创建失败");
                    yield break;
                }

                meshyMultiViewTaskId = multiViewTaskId;

                // 查询多视图任务结果
                MeshyTaskDetail multiViewResult = null;
                yield return meshyAPI.QueryTask(
                    multiViewTaskId,
                    "image-to-image",
                    120,
                    3f,
                    r => multiViewResult = r,
                    onError);

                if (multiViewResult == null || multiViewResult.status != "SUCCEEDED")
                {
                    LogError("多视图生成失败");
                    yield break;
                }

                // 获取3张多视图图片URL
                if (multiViewResult.image_urls == null || multiViewResult.image_urls.Length < 3)
                {
                    LogError($"多视图生成返回的图片数量不足: {multiViewResult.image_urls?.Length ?? 0}");
                    yield break;
                }

                multiViewImageUrls = multiViewResult.image_urls;
                Log($"✅ [Meshy步骤1.1] 多视图生成完成 | 获得{multiViewImageUrls.Length}张图片");

                // 下载3张多视图预览
                yield return DownloadTexture(multiViewImageUrls[0], tex => multiView1 = tex, err => LogError($"下载视图1失败: {err}"));
                yield return DownloadTexture(multiViewImageUrls[1], tex => multiView2 = tex, err => LogError($"下载视图2失败: {err}"));
                yield return DownloadTexture(multiViewImageUrls[2], tex => multiView3 = tex, err => LogError($"下载视图3失败: {err}"));

                // 步骤1.2: 用多视图生成3D模型
                Log($"🎯 [Meshy步骤1.2] 使用多视图生成3D模型...");
                string taskId = null;
                yield return meshyAPI.MultiImageTo3D(
                    multiViewImageUrls,
                    meshyAiModel,
                    id => taskId = id,
                    onError);

                if (string.IsNullOrEmpty(taskId))
                {
                    LogError("Multi-Image to 3D任务创建失败");
                    yield break;
                }

                meshyStep1TaskId = taskId;

                // 查询任务结果
                MeshyTaskDetail result = null;
                yield return meshyAPI.QueryTask(
                    taskId,
                    "multi-image-to-3d",
                    120,
                    3f,
                    r => result = r,
                    onError);

                if (result == null || result.status != "SUCCEEDED")
                {
                    LogError("Multi-Image to 3D生成失败");
                    yield break;
                }

                // 保存模型URLs
                meshyStep1ModelUrls = result.model_urls;

                // 下载预览图
                yield return meshyAPI.DownloadThumbnail(result.thumbnail_url, texture => meshyModelPreview = texture, error => LogError($"下载预览失败: {error}"));

                Log($"✅ [Meshy步骤1] Multi-View to 3D完成 | task_id: {taskId}");
                if (result.model_urls != null)
                {
                    Log($"  - GLB: {result.model_urls.glb}");
                    Log($"  - FBX: {result.model_urls.fbx}");
                }
            }
            // ========== 单图模式 ==========
            else
            {
                // 提交Image to 3D任务
                Debug.Log($"<color=cyan>[AI生成器]</color> 📷 [Meshy步骤1] 正在提交任务到Meshy API | 模型: {meshyAiModel}");
                string taskId = null;
                yield return meshyAPI.ImageTo3D(imageDataUri, meshyAiModel, id => taskId = id, onError);

                if (string.IsNullOrEmpty(taskId))
                {
                    LogError("Image to 3D任务创建失败");
                    yield break;
                }

                // 保存任务ID
                meshyStep1TaskId = taskId;

                // 查询任务结果
                MeshyTaskDetail result = null;
                yield return meshyAPI.QueryTask(taskId, "image-to-3d", 120, 3f, r => result = r, onError);

                if (result == null || result.status != "SUCCEEDED")
                {
                    LogError("Image to 3D生成失败");
                    yield break;
                }

                // 保存模型URLs
                meshyStep1ModelUrls = result.model_urls;

                // 下载预览图
                yield return meshyAPI.DownloadThumbnail(result.thumbnail_url, texture => meshyModelPreview = texture, error => LogError($"下载预览失败: {error}"));

                Log($"✅ [Meshy步骤1] Image to 3D完成 | task_id: {taskId}");
                if (result.model_urls != null)
                {
                    Log($"  - GLB: {result.model_urls.glb}");
                    Log($"  - FBX: {result.model_urls.fbx}");
                }
            }

            onSuccess?.Invoke();
        }

        /// <summary>
        /// Meshy步骤2: Remesh
        /// </summary>
        private IEnumerator MeshyStep2Coroutine(Action onSuccess, Action<string> onError)
        {
            Log($"🔄 [Meshy步骤2] Remesh - 开始");

            // 提交Remesh任务
            string taskId = null;
            yield return meshyAPI.Remesh(meshyStep1TaskId, meshyTargetPolycount, id => taskId = id, onError);

            if (string.IsNullOrEmpty(taskId))
            {
                LogError("Remesh任务创建失败");
                yield break;
            }

            // 保存任务ID
            meshyStep2TaskId = taskId;

            // 查询任务结果
            MeshyTaskDetail result = null;
            yield return meshyAPI.QueryTask(taskId, "remesh", 90, 2f, r => result = r, onError);

            if (result == null || result.status != "SUCCEEDED")
            {
                LogError("Remesh失败");
                yield break;
            }

            // 保存模型URLs
            meshyStep2ModelUrls = result.model_urls;

            // 下载预览图
            yield return meshyAPI.DownloadThumbnail(result.thumbnail_url, texture => meshyRemeshPreview = texture, error => LogError($"下载预览失败: {error}"));

            Log($"✅ [Meshy步骤2] Remesh完成 | task_id: {taskId}");
            if (result.model_urls != null)
            {
                Log($"  - GLB: {result.model_urls.glb}");
                Log($"  - FBX: {result.model_urls.fbx}");
            }

            onSuccess?.Invoke();
        }

        /// <summary>
        /// Meshy步骤3: Retexture
        /// </summary>
        private IEnumerator MeshyStep3Coroutine(Action onSuccess, Action<string> onError)
        {
            Log($"🎨 [Meshy步骤3] Retexture - 开始");

            // 转换处理结果为Base64 Data URI作为参考图
            string imageStyleUrl = null;
            if (processedResult != null)
            {
                Log("📷 [Meshy步骤3] 正在编码参考图(处理结果)...");

                // 检查格式，如果是压缩格式，创建未压缩副本
                Texture2D textureToEncode = processedResult;
                var format = processedResult.format;
                bool isCompressed = !(format == TextureFormat.RGBA32 || format == TextureFormat.ARGB32 ||
                                     format == TextureFormat.RGB24 || format == TextureFormat.Alpha8 ||
                                     format == TextureFormat.RGBAFloat || format == TextureFormat.RGBAHalf);

                if (isCompressed)
                {
                    Debug.LogWarning($"<color=yellow>[Meshy步骤3]</color> ⚠️ 检测到压缩格式 {format}，创建未压缩副本...");
                    textureToEncode = CreateUncompressedCopy(processedResult);
                    if (textureToEncode == null)
                    {
                        LogError("创建未压缩副本失败！");
                        onError?.Invoke("无法创建未压缩纹理副本");
                        yield break;
                    }
                }

                byte[] imageBytes = textureToEncode.EncodeToPNG();
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    LogError("参考图编码失败！纹理格式可能不支持。");
                    onError?.Invoke("参考图编码失败，请检查纹理格式设置。");
                    yield break;
                }
                string base64Image = Convert.ToBase64String(imageBytes);
                imageStyleUrl = $"data:image/png;base64,{base64Image}";
                Log($"✅ [Meshy步骤3] 参考图已编码 | 大小: {imageBytes.Length / 1024}KB");
            }
            else
            {
                Log("⚠️ [Meshy步骤3] 未找到处理结果,不使用参考图");
            }

            // 提交Retexture任务
            string taskId = null;
            string finalPrompt = GetFinalMeshyPrompt();

            if (string.IsNullOrEmpty(finalPrompt))
            {
                Log($"  - Prompt: 无(仅使用参考图)");
            }
            else
            {
                Log($"  - Prompt: {(finalPrompt.Length > 40 ? finalPrompt.Substring(0, 40) + "..." : finalPrompt)}");
            }

            Log($"  - 参考图: {(imageStyleUrl != null ? "使用处理结果" : "无")}");
            Log($"  - AI模型: {meshyAiModel}");
            yield return meshyAPI.Retexture(meshyStep2TaskId, finalPrompt, imageStyleUrl, meshyAiModel, id => taskId = id, onError);

            if (string.IsNullOrEmpty(taskId))
            {
                LogError("Retexture任务创建失败");
                yield break;
            }

            // 保存任务ID
            meshyStep3TaskId = taskId;

            // 查询任务结果
            MeshyTaskDetail result = null;
            yield return meshyAPI.QueryTask(taskId, "retexture", 90, 2f, r => result = r, onError);

            if (result == null || result.status != "SUCCEEDED")
            {
                LogError("Retexture失败");
                yield break;
            }

            // 保存模型URLs
            meshyStep3ModelUrls = result.model_urls;

            // 下载预览图
            yield return meshyAPI.DownloadThumbnail(result.thumbnail_url, texture => meshyTexturePreview = texture, error => LogError($"下载预览失败: {error}"));

            Log($"✅ [Meshy步骤3] Retexture完成 | task_id: {taskId}");
            if (result.model_urls != null)
            {
                Log($"  - GLB: {result.model_urls.glb}");
                Log($"  - FBX: {result.model_urls.fbx}");
            }

            onSuccess?.Invoke();
        }

        /// <summary>
        /// 下载指定步骤的Meshy模型文件(仅FBX)
        /// </summary>
        /// <param name="stepNumber">步骤编号(1/2/3)</param>
        /// <param name="modelUrls">模型URLs</param>
        /// <param name="filePrefix">文件名前缀</param>
        /// <param name="onSuccess">成功回调</param>
        /// <param name="onError">失败回调</param>
        private IEnumerator DownloadSpecificStepCoroutine(int stepNumber, MeshyModelUrls modelUrls, string filePrefix, Action onSuccess, Action<string> onError)
        {
            if (modelUrls == null)
            {
                LogError($"步骤{stepNumber}的模型URL为空");
                onError?.Invoke($"步骤{stepNumber}的模型URL为空");
                yield break;
            }

            if (string.IsNullOrEmpty(modelUrls.fbx))
            {
                LogError($"步骤{stepNumber}的FBX模型URL为空");
                onError?.Invoke($"步骤{stepNumber}的FBX模型URL为空");
                yield break;
            }

            string stepName = stepNumber == 1 ? "初始模型" : stepNumber == 2 ? "重拓扑" : "最终贴图";
            Log($"📦 [下载] 开始下载步骤{stepNumber}模型({stepName})");

            string fileName = $"{assetName}_{filePrefix}.fbx";
            bool hasError = false;

            // 只下载FBX文件
            Log($"📥 下载FBX: {modelUrls.fbx}");
            yield return DownloadModelFile(modelUrls.fbx, fileName, error =>
            {
                LogError($"FBX下载失败: {error}");
                hasError = true;
            });

            if (!hasError)
            {
                Log($"✅ [下载完成] 步骤{stepNumber}模型已下载: {fileName}");
                onSuccess?.Invoke();
            }
            else
            {
                LogError($"步骤{stepNumber}模型下载失败");
                onError?.Invoke($"步骤{stepNumber}模型下载失败");
            }
        }

        /// <summary>
        /// 下载单个模型文件
        /// </summary>
        private IEnumerator DownloadModelFile(string url, string fileName, Action<string> onError)
        {
            string savePath = GetModelSavePath();
            string fullPath = Path.Combine(savePath, fileName);

            using (var webRequest = UnityWebRequest.Get(url))
            {
                webRequest.timeout = 300; // 5分钟超时
                yield return webRequest.SendWebRequest();

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    onError?.Invoke($"{fileName}: {webRequest.error}");
                    yield break;
                }

                try
                {
                    File.WriteAllBytes(fullPath, webRequest.downloadHandler.data);
                    AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceUpdate);
                    Debug.Log($"<color=cyan>[AI生成器]</color> ✅ 已保存: {fullPath}");
                }
                catch (Exception e)
                {
                    onError?.Invoke($"保存{fileName}失败: {e.Message}");
                }
            }
        }

        #endregion

        #region 工具方法

        private int GetSizeValue(string sizeString)
        {
            var parts = sizeString.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height))
            {
                return width * height;
            }

            return 4194304; // 默认2K
        }

        private (int width, int height) GetWidthHeight(string sizeString)
        {
            var parts = sizeString.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height))
            {
                return (width, height);
            }

            return (2048, 2048); // 默认2K
        }

        /// <summary>
        /// 检查并修复纹理的可读性（同步方法，在操作前调用）
        /// </summary>
        /// <returns>true表示纹理可读或已成功修复，false表示修复失败</returns>
        private bool CheckAndFixTextureReadable(Texture2D texture)
        {
            if (texture == null) return false;

            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(assetPath))
            {
                // 不是Asset，可能是运行时创建的纹理，通常是可读的
                try
                {
                    texture.GetPixel(0, 0);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return false;

            bool needsReimport = false;

            // 保持原有纹理类型（Sprite或Default），不要修改
            // 只确保Sprite类型有正确的导入模式
            if (importer.textureType == TextureImporterType.Sprite)
            {
                if (importer.spriteImportMode != SpriteImportMode.Single)
                {
                    importer.spriteImportMode = SpriteImportMode.Single;
                    needsReimport = true;
                    Debug.Log($"<color=yellow>[纹理检查]</color> 设置Sprite导入模式: {texture.name}");
                }
            }

            // 启用Read/Write
            if (!importer.isReadable)
            {
                importer.isReadable = true;
                needsReimport = true;
                Debug.Log($"<color=yellow>[纹理检查]</color> 启用 Read/Write: {texture.name}");
            }

            // 设置为无压缩（默认格式）
            if (importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                needsReimport = true;
                Debug.Log($"<color=yellow>[纹理检查]</color> 设置为无压缩: {texture.name}");
            }

            // 设置最大尺寸
            if (importer.maxTextureSize < 4096)
            {
                importer.maxTextureSize = 4096;
                needsReimport = true;
            }

            // 对于Sprite类型，不需要覆盖平台设置，保持Unity默认值即可
            // 对于其他类型，如果仍有压缩问题，CreateUncompressedCopy方法会处理

            if (needsReimport)
            {
                try
                {
                    importer.SaveAndReimport();

                    // 等待导入完成 - 使用同步导入
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    AssetDatabase.Refresh();

                    // 验证纹理是否真的可读
                    Texture2D reloadedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    if (reloadedTexture != null)
                    {
                        Debug.Log($"<color=green>[纹理检查]</color> ✅ 纹理已重新导入: {texture.name}");
                        Debug.Log($"<color=yellow>[纹理检查]</color> 格式: {reloadedTexture.format} | 可读: {reloadedTexture.isReadable} | 尺寸: {reloadedTexture.width}x{reloadedTexture.height}");

                        // 检查格式是否为未压缩格式
                        var format = reloadedTexture.format;
                        bool isUncompressed = (format == TextureFormat.RGBA32 || format == TextureFormat.ARGB32 ||
                                             format == TextureFormat.RGB24 || format == TextureFormat.Alpha8 ||
                                             format == TextureFormat.RGBAFloat || format == TextureFormat.RGBAHalf);

                        if (!isUncompressed)
                        {
                            Debug.LogWarning($"<color=yellow>[纹理检查]</color> ⚠️ 格式仍为压缩格式 {format}，EncodeToPNG可能失败");
                        }

                        try
                        {
                            reloadedTexture.GetPixel(0, 0);
                            Debug.Log($"<color=green>[纹理检查]</color> ✅ 验证通过：可以读取像素数据");
                            return true;
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"<color=red>[纹理检查]</color> ❌ 验证失败，纹理仍不可读: {e.Message}");
                            return false;
                        }
                    }

                    return true;
                }
                catch (Exception e)
                {
                    Debug.LogError($"<color=red>[纹理检查]</color> 设置失败: {e.Message}");
                    return false;
                }
            }

            // 验证纹理是否可读
            try
            {
                texture.GetPixel(0, 0);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[纹理检查]</color> 纹理不可读: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 创建未压缩的纹理副本（用于EncodeToPNG）
        /// </summary>
        private Texture2D CreateUncompressedCopy(Texture2D source)
        {
            if (source == null) return null;

            try
            {
                // 创建一个RGBA32格式的新纹理（未压缩）
                Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);

                // 使用RenderTexture作为中间桥梁（可以处理任何格式）
                // 使用sRGB模式保持颜色准确性
                RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(source, rt);

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;

                copy.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                copy.Apply();

                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);

                Debug.Log($"<color=cyan>[纹理处理]</color> 已创建未压缩副本: {source.name} -> RGBA32");
                return copy;
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[纹理处理]</color> 创建未压缩副本失败: {e.Message}");
                return null;
            }
        }

        private void SaveProcessedImage(Texture2D texture)
        {
            if (texture == null) return;

            try
            {
                string picPath = GetPictureSavePath();

                // 检查格式，如果是压缩格式，创建未压缩副本
                Texture2D textureToEncode = texture;
                var format = texture.format;
                bool isCompressed = !(format == TextureFormat.RGBA32 || format == TextureFormat.ARGB32 ||
                                     format == TextureFormat.RGB24 || format == TextureFormat.Alpha8 ||
                                     format == TextureFormat.RGBAFloat || format == TextureFormat.RGBAHalf);

                if (isCompressed)
                {
                    Debug.LogWarning($"<color=yellow>[保存图片]</color> ⚠️ 检测到压缩格式 {format}，创建未压缩副本...");
                    textureToEncode = CreateUncompressedCopy(texture);
                    if (textureToEncode == null)
                    {
                        Debug.LogError("<color=red>[AI生成器]</color> 创建未压缩副本失败！");
                        return;
                    }
                }

                byte[] bytes = textureToEncode.EncodeToPNG();
                if (bytes == null || bytes.Length == 0)
                {
                    Debug.LogError("<color=red>[AI生成器]</color> 图片编码失败！纹理格式可能不支持。");
                    return;
                }

                string fileName = $"{assetName}.png";
                string fullPath = Path.Combine(picPath, fileName);

                File.WriteAllBytes(fullPath, bytes);
                AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceUpdate);

                // 设置正确的纹理导入设置（使用Default类型，保持颜色准确性）
                TextureImporter importer = AssetImporter.GetAtPath(fullPath) as TextureImporter;
                if (importer != null)
                {
                    importer.textureType = TextureImporterType.Default;
                    importer.isReadable = true;
                    importer.sRGBTexture = true;  // 确保启用SRGB（颜色贴图需要）
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.maxTextureSize = 4096;
                    importer.SaveAndReimport();
                }

                // 重新加载为 Asset 并替换 processedResult，这样点击预览就能定位到资源
                AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Texture2D loadedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(fullPath);
                if (loadedTexture != null)
                {
                    processedResult = loadedTexture;
                    Debug.Log($"<color=cyan>[AI生成器]</color> ✅ 已保存并加载为Asset（Uncompressed）: {fullPath}");
                }
                else
                {
                    Debug.Log($"<color=cyan>[AI生成器]</color> ✅ 已保存: {fullPath}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[AI生成器]</color> 保存失败: {e.Message}");
            }
        }

        private void Log(string message)
        {
            if (enableDebugLog)
            {
                Debug.Log($"<color=cyan>[AI生成器]</color> {message}");
            }
        }

        private void LogError(string message)
        {
            Debug.LogError($"<color=red>[AI生成器]</color> {message}");
        }

        #endregion

        #region Editor协程支持

        private void UpdateEditorCoroutine()
        {
            if (currentCoroutine != null)
            {
                try
                {
                    if (coroutineStack == null)
                    {
                        coroutineStack = new Stack<IEnumerator>();
                    }

                    if (waitUntilTime > 0)
                    {
                        if (Time.realtimeSinceStartup < waitUntilTime)
                        {
                            return;
                        }

                        waitUntilTime = 0f;
                    }

                    if (currentAsyncOperation != null)
                    {
                        if (!currentAsyncOperation.isDone)
                        {
                            return;
                        }

                        currentAsyncOperation = null;
                    }

                    IEnumerator activeCoroutine = coroutineStack.Count > 0 ? coroutineStack.Peek() : currentCoroutine;

                    if (!activeCoroutine.MoveNext())
                    {
                        if (coroutineStack.Count > 0)
                        {
                            coroutineStack.Pop();
                        }
                        else
                        {
                            EditorApplication.update -= UpdateEditorCoroutine;
                            currentCoroutine = null;
                            coroutineStack = null;
                        }
                    }
                    else
                    {
                        var current = activeCoroutine.Current;

                        if (current is IEnumerator nestedCoroutine)
                        {
                            coroutineStack.Push(nestedCoroutine);
                        }
                        else if (current is UnityEngine.AsyncOperation asyncOp)
                        {
                            currentAsyncOperation = asyncOp;
                        }
                        else if (current is WaitForSeconds waitForSeconds)
                        {
                            var field = typeof(WaitForSeconds).GetField("m_Seconds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (field != null)
                            {
                                float seconds = (float)field.GetValue(waitForSeconds);
                                waitUntilTime = Time.realtimeSinceStartup + seconds;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    LogError($"协程执行错误: {e.Message}");
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("错误", $"协程执行错误:\n{e.Message}", "确定");
                    EditorApplication.update -= UpdateEditorCoroutine;
                    currentCoroutine = null;
                    currentAsyncOperation = null;
                    waitUntilTime = 0f;
                    coroutineStack = null;
                    isProcessing = false;
                }
            }
        }

        /// <summary>
        /// 停止当前协程并清理状态
        /// </summary>
        private void StopCurrentCoroutine()
        {
            if (currentCoroutine != null)
            {
                EditorApplication.update -= UpdateEditorCoroutine;
                currentCoroutine = null;
                currentAsyncOperation = null;
                waitUntilTime = 0f;
                coroutineStack = null;
                isProcessing = false;
            }
        }

        private void OnDestroy()
        {
            EditorUtility.ClearProgressBar();
            StopCurrentCoroutine();
        }

        /// <summary>
        /// 在场景中创建LOD0物体
        /// </summary>
        private void CreateSceneObjectWithLOD0(string lod0ModelPath, string materialAssetPath)
        {
            try
            {
                // 获取当前选中的物体
                GameObject selectedObject = Selection.activeGameObject;
                if (selectedObject == null)
                {
                    Debug.LogWarning($"<color=yellow>[创建场景物体]</color> 未选中任何物体，跳过创建");
                    return;
                }

                // 加载LOD0模型
                GameObject lod0Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(lod0ModelPath);
                if (lod0Prefab == null)
                {
                    Debug.LogError($"<color=red>[创建场景物体]</color> 无法加载LOD0模型: {lod0ModelPath}");
                    return;
                }

                // 加载材质
                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialAssetPath);
                if (material == null)
                {
                    Debug.LogError($"<color=red>[创建场景物体]</color> 无法加载材质: {materialAssetPath}");
                    return;
                }

                // 在选中物体下创建子物体
                GameObject childObject = new GameObject($"{assetName}_lod0");
                childObject.transform.SetParent(selectedObject.transform);
                childObject.transform.localPosition = Vector3.zero;
                childObject.transform.localRotation = Quaternion.identity;
                childObject.transform.localScale = Vector3.one;

                // 实例化LOD0模型到子物体下
                GameObject lod0Instance = (GameObject)PrefabUtility.InstantiatePrefab(lod0Prefab, childObject.transform);
                lod0Instance.transform.localPosition = Vector3.zero;
                lod0Instance.transform.localRotation = Quaternion.identity;
                lod0Instance.transform.localScale = Vector3.one;

                // 为所有MeshRenderer分配材质
                MeshRenderer[] renderers = lod0Instance.GetComponentsInChildren<MeshRenderer>();
                foreach (var renderer in renderers)
                {
                    renderer.sharedMaterial = material;
                }

                // 标记场景为已修改
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

                Debug.Log($"<color=cyan>[AI生成器]</color> ✅ 已在 {selectedObject.name} 下创建 {childObject.name}");
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[创建场景物体]</color> 失败: {e.Message}");
            }
        }

        /// <summary>
        /// 创建材质并分配texture文件夹的贴图
        /// </summary>
        /// <returns>创建的材质Asset路径</returns>
        private string CreateMaterialWithTextures(string materialPath, string texturePath)
        {
            try
            {
                // 查找Scene_Opaque shader（shader名称是"GrowGarden/Scene_Opaque"）
                Shader shader = Shader.Find("GrowGarden/Scene_Opaque");
                if (shader == null)
                {
                    Debug.LogWarning($"<color=yellow>[创建材质]</color> 未找到GrowGarden/Scene_Opaque shader，使用Standard shader代替");
                    shader = Shader.Find("Standard");
                }

                if (shader == null)
                {
                    Debug.LogError($"<color=red>[创建材质]</color> 无法找到任何可用的shader");
                    return null;
                }

                // 创建材质文件
                string materialAssetPath = Path.Combine(materialPath, $"{assetName}.mat");
                Material material = new Material(shader);

                // 查找texture文件夹中的贴图
                if (Directory.Exists(texturePath))
                {
                    var textureFiles = Directory.GetFiles(texturePath, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                   f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                   f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                   f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    if (textureFiles.Length > 0)
                    {
                        // 刷新并导入贴图 - 使用Unity默认设置，不做额外处理
                        AssetDatabase.Refresh();

                        // 尝试查找主纹理（通常是diffuse、albedo、base color等）
                        string mainTexturePath = null;
                        foreach (var texFile in textureFiles)
                        {
                            string fileName = Path.GetFileNameWithoutExtension(texFile).ToLower();
                            if (fileName.Contains("diffuse") || fileName.Contains("albedo") ||
                                fileName.Contains("base") || fileName.Contains("color") ||
                                fileName.Contains("basecolor"))
                            {
                                mainTexturePath = texFile;
                                break;
                            }
                        }

                        // 如果没找到，使用第一个贴图
                        if (string.IsNullOrEmpty(mainTexturePath))
                        {
                            mainTexturePath = textureFiles[0];
                        }

                        // 加载主纹理
                        if (!string.IsNullOrEmpty(mainTexturePath))
                        {
                            Texture2D mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(mainTexturePath);
                            if (mainTexture != null)
                            {
                                material.mainTexture = mainTexture;
                                Debug.Log($"<color=cyan>[创建材质]</color> 已设置主纹理: {Path.GetFileName(mainTexturePath)}");
                            }
                        }

                        // 尝试加载其他贴图（法线、金属度等）
                        foreach (var texFile in textureFiles)
                        {
                            string fileName = Path.GetFileNameWithoutExtension(texFile).ToLower();
                            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texFile);

                            if (texture == null) continue;

                            // 法线贴图
                            if (fileName.Contains("normal") || fileName.Contains("norm"))
                            {
                                if (material.HasProperty("_BumpMap"))
                                {
                                    material.SetTexture("_BumpMap", texture);
                                    Debug.Log($"<color=cyan>[创建材质]</color> 已设置法线贴图: {Path.GetFileName(texFile)}");
                                }
                            }
                            // 金属度贴图
                            else if (fileName.Contains("metallic") || fileName.Contains("metal"))
                            {
                                if (material.HasProperty("_MetallicGlossMap"))
                                {
                                    material.SetTexture("_MetallicGlossMap", texture);
                                    Debug.Log($"<color=cyan>[创建材质]</color> 已设置金属度贴图: {Path.GetFileName(texFile)}");
                                }
                            }
                            // 遮挡贴图
                            else if (fileName.Contains("occlusion") || fileName.Contains("ao"))
                            {
                                if (material.HasProperty("_OcclusionMap"))
                                {
                                    material.SetTexture("_OcclusionMap", texture);
                                    Debug.Log($"<color=cyan>[创建材质]</color> 已设置遮挡贴图: {Path.GetFileName(texFile)}");
                                }
                            }
                        }
                    }
                }

                // 保存材质
                AssetDatabase.CreateAsset(material, materialAssetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"<color=cyan>[AI生成器]</color> ✅ 已创建材质: {materialAssetPath}");
                return materialAssetPath;
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[创建材质]</color> 失败: {e.Message}");
                return null;
            }
        }

        #endregion
    }
}
#endif