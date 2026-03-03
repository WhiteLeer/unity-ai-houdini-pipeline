#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CropEngine
{
    /// <summary>
    /// Houdini处理器 - 通过Python脚本调用Houdini进行模型处理
    /// 支持：统一模型尺寸（2.0单位）+ 删除小碎片（Labs）+ 模型减面（PolyReduce）
    /// </summary>
    public class HoudiniProcessor
    {
        private string houdiniPythonPath;
        private string scriptPath;
        private bool enableDetailedLog;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="houdiniPythonPath">Houdini Python可执行文件路径（hython.exe）</param>
        /// <param name="enableDetailedLog">是否启用详细日志</param>
        public HoudiniProcessor(string houdiniPythonPath, bool enableDetailedLog = false)
        {
            this.houdiniPythonPath = houdiniPythonPath;
            this.enableDetailedLog = enableDetailedLog;

            // 创建Python脚本目录
            string scriptDir = Path.Combine(Application.dataPath, "Scripts/Game/CropEngine/Generator/AI/Houdini");
            if (!Directory.Exists(scriptDir))
            {
                Directory.CreateDirectory(scriptDir);
            }

            // 生成Python脚本
            scriptPath = Path.Combine(scriptDir, "polyreduce.py");
            GeneratePythonScript();
        }

        /// <summary>
        /// 处理并导出模型（创建临时场景、减面、导出）
        /// </summary>
        /// <param name="inputModelPath">输入模型路径</param>
        /// <param name="outputModelPath">输出模型路径</param>
        /// <param name="targetPercent">目标面数百分比（0.0-1.0）</param>
        /// <param name="onSuccess">成功回调</param>
        /// <param name="onError">失败回调</param>
        public void ProcessAndExport(string inputModelPath, string outputModelPath, float targetPercent, Action onSuccess, Action<string> onError)
        {
            if (!File.Exists(houdiniPythonPath))
            {
                onError?.Invoke($"Houdini Python未找到: {houdiniPythonPath}");
                return;
            }

            if (!File.Exists(inputModelPath))
            {
                onError?.Invoke($"输入模型未找到: {inputModelPath}");
                return;
            }

            Log($"🔧 [Houdini] 开始减面处理");
            Log($"  - 输入: {Path.GetFileName(inputModelPath)}");
            Log($"  - 目标: {targetPercent:P0}");

            try
            {
                // 构建命令行参数
                string arguments = $"\"{scriptPath}\" \"{inputModelPath}\" \"{outputModelPath}\" {targetPercent}";

                Log($"🚀 [Houdini] 启动进程: {houdiniPythonPath}");
                if (enableDetailedLog) Log($"   参数: {arguments}");

                // 启动Houdini Python进程
                var processInfo = new ProcessStartInfo
                {
                    FileName = houdiniPythonPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processInfo))
                {
                    if (process == null)
                    {
                        onError?.Invoke("无法启动Houdini进程");
                        return;
                    }

                    // 读取输出
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();

                    process.WaitForExit();

                    if (enableDetailedLog && !string.IsNullOrEmpty(output))
                    {
                        Log($"[Houdini输出]\n{output}");
                    }

                    if (process.ExitCode == 0)
                    {
                        if (File.Exists(outputModelPath))
                        {
                            Log($"✅ [Houdini] 减面完成 | 输出: {Path.GetFileName(outputModelPath)}");
                            onSuccess?.Invoke();
                        }
                        else
                        {
                            LogError($"输出文件未生成: {outputModelPath}");
                            onError?.Invoke("输出文件未生成");
                        }
                    }
                    else
                    {
                        LogError($"Houdini处理失败 | 退出码: {process.ExitCode}");
                        if (!string.IsNullOrEmpty(error))
                        {
                            LogError($"错误信息: {error}");
                        }

                        onError?.Invoke($"Houdini处理失败: {error}");
                    }
                }
            }
            catch (Exception e)
            {
                LogError($"执行Houdini时出错: {e.Message}");
                onError?.Invoke($"执行出错: {e.Message}");
            }
        }

        /// <summary>
        /// 生成Python脚本
        /// </summary>
        private void GeneratePythonScript()
        {
            string script = @"#!/usr/bin/env hython
# -*- coding: utf-8 -*-
""""""
Houdini Model Optimizer - 自动统一尺寸、清理和减面
用法: hython polyreduce.py <input.fbx> <output.fbx> <target_percent>
说明：自动创建Houdini场景，执行完整的模型优化流程
流程：File → Normalize Size (统一到2.0单位) → Delete Small Parts → PolyReduce → Export
""""""

import sys
import os
import hou

def poly_reduce(input_path, output_path, target_percent):
    """"""执行模型统一尺寸、清理和减面""""""
    print(f'[Houdini] 开始处理: {os.path.basename(input_path)}')
    print(f'[Houdini] 统一尺寸: 最大边长 -> 2.0单位')
    print(f'[Houdini] 删除小碎片阈值: 100')
    print(f'[Houdini] 目标面数: {target_percent * 100:.0f}%')

    # 创建场景
    hou.hipFile.clear(suppress_save_prompt=True)

    # 创建OBJ网络
    obj = hou.node('/obj')
    geo = obj.createNode('geo', 'model_process')

    # 删除默认的file节点
    for child in geo.children():
        child.destroy()

    # 创建File节点导入模型
    file_node = geo.createNode('file', 'import')
    file_node.parm('file').set(input_path)

    # 创建AttributeWrangle节点统一模型尺寸
    wrangle = geo.createNode('attribwrangle', 'normalize_size')
    wrangle.setInput(0, file_node)

    # VEX代码：统一模型尺寸到最大边长2.0单位
    vex_code = """"""
vector bbox_min, bbox_max;
getbbox(0, bbox_min, bbox_max);

vector size_vec = bbox_max - bbox_min;
float max_size = max(size_vec.x, max(size_vec.y, size_vec.z));

float target = 2.0;
float scale_factor = target / max_size;

v@P *= scale_factor;

if (@ptnum == 0) {
    printf(""[Normalize] Original: %f, Scale: %f\\n"", max_size, scale_factor);
}
""""""

    wrangle.parm('snippet').set(vex_code)
    wrangle.parm('class').set(0)

    print('[Houdini] 已添加统一尺寸节点，目标最大边长: 2.0单位')

    # 创建Labs Delete Small Parts节点
    delete_small = geo.createNode('labs::delete_small_parts', 'delete_small_parts')
    delete_small.setInput(0, wrangle)
    delete_small.parm('threshold').set(100)

    print('[Houdini] 已添加Labs Delete Small Parts节点，阈值: 100')

    # 创建PolyReduce节点
    polyreduce = geo.createNode('polyreduce', 'reduce')
    polyreduce.setInput(0, delete_small)
    polyreduce.parm('target').set(1)
    polyreduce.parm('percentage').set(target_percent * 100)

    # 创建ROP FBX节点导出
    rop_fbx = geo.createNode('rop_fbx', 'export')
    rop_fbx.parm('sopoutput').set(output_path)
    rop_fbx.parm('startnode').set(polyreduce.path())
    rop_fbx.parm('vcformat').set(1)

    print('[Houdini] 开始渲染导出...')

    # 执行渲染
    rop_fbx.parm('execute').pressButton()

    print(f'[Houdini] 完成! 输出: {os.path.basename(output_path)}')
    return True

if __name__ == '__main__':
    if len(sys.argv) != 4:
        print('用法: hython polyreduce.py <input.fbx> <output.fbx> <target_percent>')
        sys.exit(1)

    input_file = sys.argv[1]
    output_file = sys.argv[2]
    target_percent = float(sys.argv[3])

    if not os.path.exists(input_file):
        print(f'错误: 输入文件不存在: {input_file}')
        sys.exit(1)

    if target_percent <= 0 or target_percent > 1:
        print(f'错误: target_percent必须在0-1之间')
        sys.exit(1)

    try:
        poly_reduce(input_file, output_file, target_percent)
        sys.exit(0)
    except Exception as e:
        print(f'错误: {str(e)}')
        import traceback
        traceback.print_exc()
        sys.exit(1)
";

            File.WriteAllText(scriptPath, script);
            Log($"✅ Python脚本已生成: {scriptPath}");
        }

        private void Log(string message)
        {
            if (enableDetailedLog)
            {
                Debug.Log($"<color=orange>[Houdini]</color> {message}");
            }
        }

        private void LogError(string message)
        {
            Debug.LogError($"<color=red>[Houdini]</color> {message}");
        }
    }
}
#endif