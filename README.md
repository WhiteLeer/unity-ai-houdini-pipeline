# Unity-Houdini-AI-Pipeline

> Unity Editor工具，集成AI 3D生成API和Houdini优化流程

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Unity](https://img.shields.io/badge/Unity-2019.4%2B-blue.svg)](https://unity.com/)
[![Houdini](https://img.shields.io/badge/Houdini-20.0%2B-orange.svg)](https://www.sidefx.com/)

一个Unity Editor工具，整合了火山引擎即梦4.0、Meshy 3D生成API和Houdini Python自动化。主要解决了Unity Editor模式下的异步任务管理问题。

```
美术原画 → 火山引擎即梦4.0 → Meshy 3D → Houdini优化 → Unity资产
    ↓            ↓              ↓           ↓          ↓
Inspector    Base64直传    异步协程栈    hython脚本  自动配置
```

## 🎯 核心功能

### 1. Unity Editor异步协程管理
- ✅ 支持Editor模式下的异步任务（解决`StartCoroutine`在Editor中不可用的问题）
- ✅ 协程栈管理，支持嵌套`IEnumerator`
- ✅ 无需`EditorCoroutineUtility`（兼容Unity 2019.4+）

### 2. Houdini Python自动化
- 🎯 命令行调用`hython`执行模型优化
- 🧹 自动统一模型尺寸（最大边长2.0单位）+ 清理碎片 + 减面
- 📦 一键生成LOD0/LOD1/LOD2（100%/30%/10%）

### 3. 火山引擎API集成
- 🔐 AWS Signature V4认证实现
- 🎨 即梦4.0图生图（Base64/URL双模式）
- ⚡ 异步轮询，支持`in_queue`/`generating`/`done`状态

### 4. Meshy 3D生成
- 🖼️ Image to 3D → Remesh → Retexture 三步骤工作流
- 🎭 多视图生成（可选）
- 📦 自动下载FBX + 贴图

### 5. 即挂即用设计
- 🎮 单一脚本`GeneratorAI.cs`
- 📊 Odin Inspector优化，折叠组组织
- ⚙️ 零外部配置文件

## 📦 安装

### 前置要求

- **Unity** 2019.4+
- **Houdini** 20.0+ （Indie或FX版）
- **Odin Inspector**（Unity Asset Store，$55）
- **火山引擎账号**（即梦4.0 API）
- **Meshy账号**（meshy.ai）

### 快速开始

```bash
# 1. 克隆仓库
git clone https://github.com/WhiteLeer/Unity-Houdini-AI-Pipeline.git

# 2. 复制到Unity项目
cp -r Unity-Houdini-AI-Pipeline/Assets/Scripts/* \
     YourUnityProject/Assets/Scripts/

# 3. 复制Houdini脚本
cp Unity-Houdini-AI-Pipeline/Houdini/polyreduce.py \
   ~/houdini20.0/scripts/
```

### Unity配置

1. 在场景中创建空GameObject
2. 挂载`GeneratorAI.cs`脚本
3. 在Inspector中填写：
   - 火山引擎 Access Key ID
   - 火山引擎 Secret Access Key
   - Meshy API Key
   - Houdini hython.exe路径
4. 点击"🚀 一键初始化"
5. 拖入原画图片，点击"🎨 预处理原画"

## 📖 文档

- [完整技术文档](docs/unity-houdini-ai-3d-pipeline.md) - 技术细节和实现过程
- API配置指南 - 火山引擎和Meshy的API配置步骤
- Houdini脚本文档 - Python自动化脚本详解

## 🏗️ 架构设计

### 系统流程

```
美术原画
    ↓
火山引擎即梦4.0（图生图处理）
    ↓
Meshy 3D API（Image to 3D → Remesh → Retexture）
    ↓
Houdini Python（Normalize Size → Delete Small Parts → PolyReduce）
    ↓
Unity自动导入（配置FBX + 材质 + LOD）
```

### 核心模块

```
GeneratorAI.cs (主控脚本)
├── VolcEngineAuth.cs          - AWS Signature V4认证
├── JimengProcessor.cs         - 即梦4.0图生图
├── MeshyAPI.cs               - Meshy API封装
│   └── MeshyGenerator.cs     - 三步骤工作流
├── HoudiniProcessor.cs       - Houdini调用
│   └── polyreduce.py         - Python自动化脚本
└── ImageXUploader.cs         - ImageX图片上传（可选）
```

## 🔧 技术实现

### Editor协程栈

```csharp
// 支持嵌套协程的Editor异步管理
private Stack<IEnumerator> coroutineStack;

private void UpdateEditorCoroutine()
{
    IEnumerator activeCoroutine = coroutineStack.Count > 0
        ? coroutineStack.Peek()
        : currentCoroutine;

    if (activeCoroutine.Current is IEnumerator nestedCoroutine)
    {
        // 检测到嵌套，压入栈
        coroutineStack.Push(nestedCoroutine);
    }
}
```

### Houdini VEX统一尺寸

```python
# 自动将所有模型统一到最大边长2.0单位
vex_code = """
vector bbox_min, bbox_max;
getbbox(0, bbox_min, bbox_max);

vector size_vec = bbox_max - bbox_min;
float max_size = max(size_vec.x, max(size_vec.y, size_vec.z));

v@P *= 2.0 / max_size;  // 统一缩放
"""
```

### 火山引擎签名算法

```csharp
// AWS Signature V4实现
byte[] kSecret = Encoding.UTF8.GetBytes($"VOLC{secretAccessKey}");
byte[] kDate = HmacSha256(kSecret, dateStamp);
byte[] kRegion = HmacSha256(kDate, region);
byte[] kService = HmacSha256(kRegion, service);
byte[] kSigning = HmacSha256(kService, "request");

var signature = HmacSha256Hex(kSigning, stringToSign);
```

## 🚧 已知限制

- **依赖外部服务**：需要火山引擎和Meshy API（付费服务）
- **Houdini依赖**：需要本地安装Houdini（Indie版$269/年）
- **模型质量**：Meshy生成质量受AI能力限制
- **Unity版本兼容性**：Odin Inspector反射可能在Unity更新时受影响

## 📄 许可证

MIT License - 详见 [LICENSE](LICENSE)

## 🤝 贡献

欢迎提交Issue和Pull Request！

### 贡献指南

1. Fork本仓库
2. 创建特性分支：`git checkout -b feature/AmazingFeature`
3. 提交更改：`git commit -m 'Add some AmazingFeature'`
4. 推送分支：`git push origin feature/AmazingFeature`
5. 提交Pull Request

## 🙏 致谢

- **Claude Code** - AI辅助开发
- **SideFX Labs** - Houdini Labs工具包
- **Meshy** - AI 3D生成服务
- **火山引擎** - 即梦4.0图生图API

## 📮 联系

- GitHub: [@WhiteLeer](https://github.com/WhiteLeer)
- Issues: [提交问题](https://github.com/WhiteLeer/Unity-Houdini-AI-Pipeline/issues)

---

⭐ 如果这个项目对您有帮助，欢迎Star
