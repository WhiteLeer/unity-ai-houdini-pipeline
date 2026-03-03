using System;
using UnityEngine;

namespace CropEngine
{
    /// <summary>
    /// 即梦4.0请求参数（提交任务）
    /// </summary>
    [Serializable]
    public class PlantPreprocessRequest
    {
        public string req_key;                      // 固定值：jimeng_t2i_v40
        public string[] image_urls;                 // 图片URL数组（0-10张）
        public string[] binary_data_base64;         // Base64编码的图片数据数组（可替代image_urls）
        public string prompt;                       // 文本指令
        public float scale;                         // 文本描述影响程度（0-1，默认0.5）
        public int size;                            // 图片面积（1024*1024 到 4096*4096）
        public int width;                           // 图片宽度（可选，需与height同时传）
        public int height;                          // 图片高度（可选，需与width同时传）
        public bool force_single;                   // 是否强制生成单图（默认false）
        public float min_ratio;                     // 最小宽高比（默认1/3）
        public float max_ratio;                     // 最大宽高比（默认3）
    }

    /// <summary>
    /// 查询任务结果请求
    /// </summary>
    [Serializable]
    public class TaskQueryRequest
    {
        public string req_key;                      // 固定值：jimeng_t2i_v40
        public string task_id;                      // 任务ID
        public string req_json;                     // 可选：水印/返回URL配置
    }

    /// <summary>
    /// 提交任务响应
    /// </summary>
    [Serializable]
    public class SubmitTaskResponse
    {
        public int code;
        public string message;
        public SubmitTaskData data;
        public string request_id;
        public string time_elapsed;
    }

    [Serializable]
    public class SubmitTaskData
    {
        public string task_id;
    }

    /// <summary>
    /// 查询任务结果响应
    /// </summary>
    [Serializable]
    public class TaskResultResponse
    {
        public int code;
        public string message;
        public TaskResultData data;
        public string request_id;
        public string time_elapsed;
    }

    [Serializable]
    public class TaskResultData
    {
        public string status;               // 任务状态：in_queue, generating, done, not_found, expired
        public string[] image_urls;         // 生成的图片URL列表（24小时有效）
        public string[] binary_data_base64; // Base64编码的图片数据数组
    }
}
