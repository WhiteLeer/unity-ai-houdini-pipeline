#!/usr/bin/env hython
# -*- coding: utf-8 -*-
"""
Houdini Model Optimizer - 自动统一尺寸、清理和减面
用法: hython polyreduce.py <input.fbx> <output.fbx> <target_percent>
说明：自动创建Houdini场景，执行完整的模型优化流程
流程：File → Normalize Size (统一到2.0单位) → Delete Small Parts → PolyReduce → Export
"""

import sys
import os
import hou

def poly_reduce(input_path, output_path, target_percent):
    """执行模型统一尺寸、清理和减面"""
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
    vex_code = """
vector bbox_min, bbox_max;
getbbox(0, bbox_min, bbox_max);

vector size_vec = bbox_max - bbox_min;
float max_size = max(size_vec.x, max(size_vec.y, size_vec.z));

float target = 2.0;
float scale_factor = target / max_size;

v@P *= scale_factor;

if (@ptnum == 0) {
    printf("[Normalize] Original: %f, Scale: %f\\n", max_size, scale_factor);
}
"""

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
