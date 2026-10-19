# 训练权重目录

把预先下载好的 Ultralytics 基础权重（`.pt`）放在这里，训练页面会列出这里的所有权重供选择：

| 文件名示例 | 任务 | 适用项目 |
| --- | --- | --- |
| `yolo11n.pt`、`yolo11s.pt`、`yolov8n.pt` | 检测 | 检测项目、分割项目（只用框） |
| `yolo11n-seg.pt` | 分割 | 分割项目 |
| `yolo11n-cls.pt` | 分类 | 任意项目（按标注框裁剪成分类数据） |

- 任务类型按文件名识别：含 `-seg` 为分割，含 `-cls` 为分类，含 `-pose`/`-obb` 暂不支持，其余为检测。
- `.pt` 是 pickle 文件，加载即可执行代码，**只放来自可信来源（Ultralytics 官方发布页）的文件**。用户无法通过网页上传 `.pt`。
- 下载地址：https://github.com/ultralytics/assets/releases
