<div align="center">

# MaskFlow

**中文 · [English](./README.en.md)**

**只管上传图片，剩下的交给 AI：标注、质检、训练、评估一站式全自动完成**

基于 SAM 3、YOLO26 / YOLO11、MCP、Vue 3、ASP.NET Core 与 FastAPI 的自托管视觉数据平台。<br/>
在网页里点几下，或者直接在 Claude、ChatGPT、Codex 的对话框里说一句话。

[![CI](https://github.com/ZhangEmo28/maskflow/actions/workflows/ci.yml/badge.svg)](https://github.com/ZhangEmo28/maskflow/actions/workflows/ci.yml)
[![License: AGPL-3.0](https://img.shields.io/badge/License-AGPL--3.0-blue.svg)](./LICENSE)
[![Release](https://img.shields.io/github/v/release/ZhangEmo28/maskflow)](https://github.com/ZhangEmo28/maskflow/releases)
[![MCP](https://img.shields.io/badge/MCP-Streamable%20HTTP%20%2B%20OAuth%202.1-8a2be2.svg)](./文档/智能体连接.md)
[![Vue 3](https://img.shields.io/badge/Vue-3-42b883.svg)](https://vuejs.org/)
[![.NET 9](https://img.shields.io/badge/.NET-9-512bd4.svg)](https://dotnet.microsoft.com/)

</div>

<p align="center">
  <a href="./docs/screenshots/annotation-workbench.jpg">
    <img src="./docs/screenshots/annotation-workbench.jpg" alt="MaskFlow 标注工作台：用平台里训练好的 yolo26x 模型自动标注" width="100%" />
  </a>
</p>

<p align="center">用平台里刚训练好的 <code>yolo26x</code> 模型给新图片自动标注——标注 → 训练 → 模型回到标注工具，形成闭环。</p>

## 三个关键词

<table>
  <tr>
    <td width="33%" valign="top">
      <h3>🤖 全自动</h3>
      SAM 3 按标签名自动识别并分类；后台批量标注，关掉页面也不停；视频标一帧自动向后传播；训练好的模型自动注册回标注工具；训练前自动检查数据并告诉你还缺多少张什么样的图。
    </td>
    <td width="33%" valign="top">
      <h3>🧩 一站式</h3>
      上传 → AI 标注 → 人工复核 → 质量检查 → 数据集划分与导出 → 一键训练 → 评估 → 下载训练包。数据、标注、权重、训练结果都在一个平台里，不用在多个工具和脚本之间搬运。
    </td>
    <td width="33%" valign="top">
      <h3>💬 MCP 智能体</h3>
      内置 MCP 服务，Claude、ChatGPT、Codex、Cursor 连上后，在对话里说需求就能完成标注、看图检查、修正、质检、训练和模型评估——智能体能“看见”标注画在图上的样子。
    </td>
  </tr>
</table>

```text
上传图片 / 视频 / 已有数据集
        │
        ▼
AI 自动标注 ──（SAM 3 按标签识别 · 分割一切 · 自己训练的模型）
        │
        ▼
人工或智能体复核 ── 质量检查（重复框、漏标签、低置信度、类别过少…）
        │
        ▼
训练前数据检查 ── 最少图片数、逐类划分、补充建议
        │
        ▼
一键训练（YOLO26 / YOLO11 / YOLOv8）── 验证集 / 测试集评估
        │
        ├──► 训练包下载：基础权重 + 数据集 + 训练脚本 + 全部结果
        └──► 模型自动回到标注工具，继续自动标注新图片
```

## 一站式训练与评估

<table>
  <tr>
    <td width="50%" align="center"><strong>开始训练前确认：图片数、划分和注意事项</strong></td>
    <td width="50%" align="center"><strong>实时曲线与验证 / 测试集评估</strong></td>
  </tr>
  <tr>
    <td><a href="./docs/screenshots/training-confirm.jpg"><img src="./docs/screenshots/training-confirm.jpg" alt="训练前确认弹窗" /></a></td>
    <td><a href="./docs/screenshots/training-metrics.jpg"><img src="./docs/screenshots/training-metrics.jpg" alt="训练曲线与评估指标" /></a></td>
  </tr>
  <tr>
    <td align="center"><strong>模型预测效果（与人工标注左右对照）</strong></td>
    <td align="center"><strong>训练文件与一键下载训练包</strong></td>
  </tr>
  <tr>
    <td><a href="./docs/screenshots/training-predictions.jpg"><img src="./docs/screenshots/training-predictions.jpg" alt="验证集模型预测效果" /></a></td>
    <td><a href="./docs/screenshots/training-package.jpg"><img src="./docs/screenshots/training-package.jpg" alt="训练文件与训练包下载" /></a></td>
  </tr>
  <tr>
    <td align="center"><strong>基础权重管理：官方 YOLO26 / YOLO11 / YOLOv8 一键下载</strong></td>
    <td align="center"><strong>数据集分析：类别分布、划分预估、近重复图片</strong></td>
  </tr>
  <tr>
    <td><a href="./docs/screenshots/base-weights.jpg"><img src="./docs/screenshots/base-weights.jpg" alt="基础权重管理" /></a></td>
    <td><a href="./docs/screenshots/dataset-analysis.jpg"><img src="./docs/screenshots/dataset-analysis.jpg" alt="数据集分析与划分" /></a></td>
  </tr>
</table>

## 用智能体操作 MaskFlow（MCP）

<p align="center">
  <a href="./docs/screenshots/mcp-connection.jpg"><img src="./docs/screenshots/mcp-connection.jpg" alt="账户设置中的智能体连接（MCP）页面" width="100%" /></a>
</p>

连上之后，你在对话框里这样说就行：

> 帮我看看「工地」项目，把安全帽和反光衣都标出来，先标两张给我看效果，没问题再全部标完，最后做一次质量检查。

> 数据够训练了吗？不够的话告诉我每个类别还要补多少张。够了就用 yolo26s 训练 100 轮，训练完把每个类别的效果和混淆矩阵讲给我听。

> 用刚训练的模型在已确认的图片上评估一下，把错得最多的几张图给我看。

智能体会调用 MaskFlow 的工具完成这些事：它能把标注画在图上“看”一遍再判断好坏，把框改对；它新增或修改的目标都保存为“待确认”，只有你同意后才会标记为人工确认。

**MCP 地址**：`http(s)://你的域名/mcp`（本机默认 `http://127.0.0.1:3000/mcp`），在「账户设置 → 智能体连接」可以复制。认证二选一：

| 客户端 | 连接方式 |
| --- | --- |
| **Claude Code** | `claude mcp add --transport http maskflow http://127.0.0.1:3000/mcp --header "Authorization: Bearer <API Token>"`<br/>也可以不加 `--header`，在 Claude Code 里运行 `/mcp` 走浏览器授权（OAuth）。 |
| **Codex** | 在 `~/.codex/config.toml` 中加入下方配置，首次使用按提示在浏览器中授权（OAuth）。 |
| **Cursor / Windsurf 等** | 在 `mcp.json` 中填写 `url` 与 `Authorization: Bearer <API Token>` 头（见下方）。 |
| **Claude 网页版 / 桌面版** | 设置 → 连接器 → 添加自定义连接器，填 MCP 地址，弹出 MaskFlow 授权页后点「允许访问」。 |
| **ChatGPT** | 在连接器设置中开启开发者模式，添加自定义 MCP 连接器，认证选 OAuth。 |

```toml
# ~/.codex/config.toml
[mcp_servers.maskflow]
url = "http://127.0.0.1:3000/mcp"
```

```json
{
  "mcpServers": {
    "maskflow": {
      "url": "http://127.0.0.1:3000/mcp",
      "headers": { "Authorization": "Bearer <API Token>" }
    }
  }
}
```

- **API Token**（`mf_…`）在「账户设置 → API Token」创建，适合本机或内网的客户端。
- **OAuth 2.1**（动态注册 + PKCE）适合云端连接器。ChatGPT 和 Claude 网页版是从云端连过来的，MaskFlow 必须部署在**公网 HTTPS 域名**下，并把 `MASKFLOW_PUBLIC_URL` 设成该域名。已连接的应用可以在「智能体连接」里随时断开。

主要工具（共 25 个，完整列表与说明见 [文档/智能体连接.md](./文档/智能体连接.md)）：

| 分类 | 工具 |
| --- | --- |
| 项目与数据 | `list_projects` `get_project` `create_project` `update_labels` `list_images` `export_dataset` |
| 标注 | `auto_annotate_image` `auto_annotate_project` `get_task` `get_annotations` `view_image`（返回画好标注的图片） `edit_annotations` `confirm_annotations` `clear_annotations` `review_image` |
| 质量 | `check_quality` `check_training_data` |
| 训练与评估 | `list_base_weights` `start_training` `get_training` `list_trainings` `cancel_training` `view_training_plot` `list_models` `evaluate_model` |

## 功能一览

**数据**
- 按项目管理图片：拖拽或选中即上传、按状态（未标注 / 待确认 / 已确认 / 背景图）筛选、Shift 连选后批量 AI 标注、清除标注或删除。
- 上传视频自动抽帧；导入已有数据集（YOLO / COCO / LabelMe / Pascal VOC，zip 或单个标注文件）。

<p align="center"><a href="./docs/screenshots/files-bulk-selection.jpg"><img src="./docs/screenshots/files-bulk-selection.jpg" alt="图片管理与批量操作" width="80%" /></a></p>

**全自动标注**
- SAM 3 按项目标签识别（中文标签可配英文提示词，如 安全帽 → hard hat），每个目标直接带上类别和置信度；也支持“分割一切”和正负点交互抠图。
- 用自己训练或上传的 YOLO 模型（ONNX）预标注；平台里训练完成的模型会自动注册，可直接选用。
- 后台批量标注：排队执行、显示进度、可取消，API 重启后续跑；已人工确认的目标不会被覆盖；没检测到目标的图片保持“未标注”并单独计数，不会被误当作已完成。
- 视频：标注一帧后用 SAM 按框逐帧向后传播，类别随目标延续。

**复核与质量**
- 拖动 / 缩放框、编辑多边形顶点、撤销重做、滚轮缩放与空格平移、全套快捷键（`R` 运行 AI、`1–9` 设标签、`A`/`D` 切图、`Ctrl+S` 保存…），可开启“保存后自动下一张”。
- 低置信度优先复核；确实没有目标的图片可“标为背景图”，训练时作为负样本减少误检。
- 质量检查：无类别、无效 / 极小 / 超大框、同类重复框、同一物体两个类别、低置信度未确认、分割缺多边形、类别过少或不平衡。

**导出与训练**
- 导出 YOLO 检测 / 分割、COCO、Pascal VOC、分类裁剪；按类别分层划分 train / val / test，近重复图片放进同一集合，可只导出人工确认的目标。
- 训练前数据检查：至少 20 张已标注图片、每类至少 10 张才能训练（可配置）；逐类列出训练 / 验证 / 测试的实际数量，并给出“还要补多少张、补什么样的图”的建议。
- 基础权重管理：Ultralytics 官方 YOLO26 / YOLO11 / YOLOv8（n–x，检测 / 分割 / 分类，共 45 个）一键下载、按官方大小校验、可删除。
- 一键训练：确认弹窗显示参与图片数和划分；实时损失与 mAP 曲线；验证集 / 测试集与逐类评估；预测效果与人工标注左右对照、混淆矩阵、PR / F1 曲线、增强样本。
- 训练包：基础权重、本次数据集、`train.py`（一条命令复现）、`predict.py`、`requirements.txt`、README（指标与用法）和训练产生的全部文件（best / last / ONNX、曲线、日志、环境信息）。
- 模型评估：用人工确认过的标注评估任意模型，给出逐类精确率 / 召回率 / F1 和错得最多的图片。

**协作与运维**
- 项目成员与角色（所有者 / 审核员 / 标注员 / 只读）、邀请、分配图片、提交 → 通过 / 驳回的审核流程；邮件发送邀请、找回密码和任务完成通知。
- 存储与每日 AI 额度、任务记录；Docker Compose 一键部署，数据存 MySQL 与 MinIO。

## 快速开始

### 1. 准备环境

- Docker Desktop 或 Docker Engine + Compose
- NVIDIA GPU、驱动与 NVIDIA Container Toolkit（SAM 3 推理与模型训练需要）
- 已获批的 SAM 3 模型权重

> SAM 3 权重不会自动下载。请先在 [Meta SAM 3 的 Hugging Face 页面](https://huggingface.co/facebook/sam3)申请访问，获批后下载 `sam3.pt`，并遵守模型页面上的许可条款。训练用的 YOLO 基础权重不需要手动准备，启动后在网页里一键下载。

### 2. 下载并配置

```bash
git clone https://github.com/ZhangEmo28/maskflow.git
cd maskflow
cp .env.example .env
mkdir -p 资源/模型
# 把下载的 sam3.pt 放到 资源/模型/sam3.pt
```

Windows PowerShell：

```powershell
git clone https://github.com/ZhangEmo28/maskflow.git
Set-Location maskflow
Copy-Item .env.example .env
New-Item -ItemType Directory -Force "资源/模型"
# 把下载的 sam3.pt 放到 资源/模型/sam3.pt
```

### 3. 启动

```bash
# 标注、导出、MCP 等全部功能
docker compose up -d --build

# 需要模型训练时，再启动训练服务（同一镜像，独立容器）
docker compose --profile training up -d --build
```

| 服务 | 本机地址 | 说明 |
|---|---|---|
| Web | http://127.0.0.1:3000 | MaskFlow 工作台；MCP 地址为 `/mcp` |
| API | http://127.0.0.1:8000 | 业务 API |
| SAM | http://127.0.0.1:8001 | 推理服务 |
| 训练服务 | 仅容器内部 | `--profile training` 启动，由 API 调用 |
| MinIO Console | http://127.0.0.1:9001 | 对象存储管理 |
| MySQL | 127.0.0.1:3307 | 数据库 |

默认只绑定回环地址，不会直接暴露到局域网或公网。首次启动会自动建库建表。

### 4. 第一次使用

1. 打开 http://127.0.0.1:3000 注册账号。**第一个注册的账号是管理员**，可以下载 / 删除基础权重（也可用 `MASKFLOW_ADMIN_EMAILS` 指定）。
2. 「模型训练 → 基础权重」下载一个起步权重，例如 `yolo26n.pt`（分割项目选 `-seg`，分类选 `-cls`）。
3. 「上传图片」新建项目、添加标签，把图片拖进上传区。
4. 「YOLO 标注」选择 AI 方式“按标签识别”，先对一张图按 `R` 看效果，再「批量运行 AI」；复核时用状态筛选和快捷键，确认无误后保存。
5. 「模型训练」查看右侧数据检查，按建议补数据；通过后点「开始训练」，在确认框核对图片数和划分后开始。
6. 训练完成后查看评估和效果图，下载训练包；训练好的模型已出现在标注页的“自定义模型”里，可以直接用来标注新图片。
7. 想用对话完成这些步骤：「账户设置 → 智能体连接」按上文接入 Claude / ChatGPT / Codex。

## 配置

常用配置在 `.env` 中（完整说明见 `.env.example` 与 `.env.production.example`）：

| 变量 | 说明 |
| --- | --- |
| `MASKFLOW_PUBLIC_URL` | 对外访问地址。用于邮件链接，也是 MCP / OAuth 的签发地址；ChatGPT、Claude 网页版连接时必须是公网 HTTPS。 |
| `MASKFLOW_ADMIN_EMAILS` | 可下载 / 删除基础权重的管理员邮箱，逗号分隔；留空时为第一个注册的账号。 |
| `MASKFLOW_TRAIN_MIN_IMAGES` / `MASKFLOW_TRAIN_MIN_PER_CLASS` | 训练的最低数据要求，默认 20 张 / 每类 10 张。 |
| `TRAIN_WEIGHT_BASE_URL` | 官方权重下载地址（Ultralytics assets v8.4.0）；访问 GitHub 慢时可换成镜像。 |
| `MASKFLOW_SMTP_*` | 邮件服务：找回密码、项目邀请、任务完成通知。 |
| `MASKFLOW_AI_DAILY_LIMIT` | 每个账户每日 AI 调用额度。 |
| `MASKFLOW_TRUSTED_PROXIES` | 前面还有反向代理时，信任的代理网段（用于识别真实客户端 IP）。 |
| `SAM3_INTERNAL_KEY` | API 与推理 / 训练服务之间的内部密钥。 |

## 生产部署

```bash
cp .env.production.example .env
```

1. 替换所有 `replace-me` 值，使用独立的高强度随机密钥。
2. 把 `MASKFLOW_PUBLIC_URL`、`MASKFLOW_CORS_ORIGINS`、`SAM3_CORS_ORIGINS` 改为真实的 HTTPS 域名。
3. 在 Web 服务前配置 TLS 反向代理。内置 nginx 已把 `/api`、`/mcp`（关闭缓冲以支持流式响应）、`/oauth/`、`/.well-known/oauth-*` 转发到 API；如果再套一层代理，请同样转发这些路径，并设置 `MASKFLOW_TRUSTED_PROXIES`。
4. 配置 SMTP，否则找回密码和邀请邮件无法发送。
5. 保持 SAM、MySQL、MinIO、API 与训练服务监听在 `127.0.0.1` 或受控私有网络。
6. 训练服务的权重目录 `资源/训练权重` 需要可写（网页下载权重时写入）。
7. 执行 `docker compose --profile training config --quiet` 检查最终配置，再启动服务。

Production 模式会拒绝示例 / 开发密钥，并禁止 `MASKFLOW_BILLING_DEV_MODE`。

## 常用命令

```bash
# 启动或更新全部服务（含训练服务）
docker compose --profile training up -d --build

# 只更新前端和 API
docker compose up -d --build web api

# 更新推理 / 训练服务
docker compose --profile training up -d --build sam-inference trainer

# 查看日志和状态
docker compose ps
docker compose logs -f api trainer

# 停止服务（保留数据）
docker compose --profile training down
```

## 技术架构

```text
浏览器（Vue 3）          智能体（Claude / ChatGPT / Codex / Cursor）
      │                          │  MCP（Streamable HTTP，API Token / OAuth 2.1）
      ▼                          ▼
   nginx ──► MaskFlow API（ASP.NET Core 9）──► MySQL / MinIO
                 │               │
                 ▼               ▼
         SAM 推理服务        训练服务
     （FastAPI + SAM 3 + ONNX） （FastAPI + Ultralytics，子进程训练）
```

| 组件 | 技术栈 | 目录 |
|---|---|---|
| Web 工作台 | Vue 3、Vite、nginx | `src/frontend/maskflow-web` |
| 业务 API 与 MCP | ASP.NET Core 9、ModelContextProtocol SDK、MySqlConnector、SkiaSharp、MailKit | `src/backend/maskflow-api` |
| 推理与训练服务 | FastAPI、Ultralytics、PyTorch、ONNX Runtime、OpenCV | `src/ai/sam-inference`（`app/main.py` 推理，`app/trainer.py` 训练） |
| 测试 | xUnit、WebApplicationFactory、pytest、node:test、GitHub Actions | `src/backend/maskflow-api.tests` 等 |

## 本地开发

```bash
# 前端（把 /api、/mcp、/oauth、/.well-known 代理到 127.0.0.1:8010）
cd src/frontend/maskflow-web
npm ci
npm run dev -- --host 127.0.0.1 --port 3000

# 后端
export MASKFLOW_MYSQL='Server=127.0.0.1;Port=3306;Database=maskflow;User ID=maskflow;Password=maskflow;Allow User Variables=true;'
export MASKFLOW_PUBLIC_URL=http://localhost:3000
dotnet run --project src/backend/maskflow-api/MaskFlow.Api.csproj --urls http://127.0.0.1:8010

# 推理服务 / 训练服务
cd src/ai/sam-inference
python -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
python -m uvicorn app.main:app --host 127.0.0.1 --port 8001
TRAIN_WEIGHTS_DIR=./weights TRAIN_RUNS_DIR=./runs python -m uvicorn app.trainer:app --host 127.0.0.1 --port 8002
```

后端通过 `SAM_SERVICE_URL`（默认 `http://localhost:8001`）和 `TRAINER_SERVICE_URL`（默认 `http://localhost:8002`）连接两个服务。开发环境 Swagger：http://127.0.0.1:8010/swagger

## 主要 API

```text
POST /api/auth/register | /api/auth/login
POST /api/files/upload           POST /api/files/bulk-delete
POST /api/annotations/auto       POST /api/annotations/batch      POST /api/annotations/clear
POST /api/export/dataset         POST /api/projects/{id}/import
GET  /api/training/weights       POST /api/training/weights/{name}/download
POST /api/projects/{id}/trainings/check
POST /api/projects/{id}/trainings
GET  /api/trainings/{id}         POST /api/trainings/{id}/download-link
POST /mcp                        （MCP，Streamable HTTP）
GET  /.well-known/oauth-protected-resource | /.well-known/oauth-authorization-server
```

完整接口可在开发环境 Swagger 中查看。

## 项目结构

```text
maskflow/
├── .github/                    # CI 与依赖更新
├── docs/screenshots/           # README 截图
├── src/
│   ├── frontend/maskflow-web/  # Vue 工作台
│   ├── backend/maskflow-api/   # .NET 业务 API 与 MCP 服务
│   ├── backend/maskflow-api.tests/
│   ├── ai/sam-inference/       # SAM 3 推理服务与训练服务
│   └── legacy/                 # 旧版实现，仅供参考
├── 文档/                       # 修改记录、智能体连接、产品说明
├── 资源/模型/                  # SAM 3 权重（不纳入 Git）
├── 资源/训练权重/              # 训练用基础权重（不纳入 Git）
├── .env.example
├── .env.production.example
├── docker-compose.yml
└── LICENSE
```

## 测试与持续集成

每次提交和 Pull Request 都会运行：

- 前端：`npm audit`、`npm test`、`npm run build`
- 后端：`dotnet test`（含 MySQL 8.4 集成测试、真实 API 端到端测试、MCP 与 OAuth 流程测试）
- 推理与训练服务：`pytest`（使用替身模型和替身 Ultralytics，不需要 GPU）
- `docker compose config`

## 参与贡献

欢迎通过 [Issues](https://github.com/ZhangEmo28/maskflow/issues) 报告问题、提出功能建议，或提交 Pull Request。提交前请确保 CI 全部通过，并避免提交模型权重、数据集、密钥和本地运行文件。各版本的详细改动见 [文档/修改记录.md](./文档/修改记录.md)。

## 许可证

MaskFlow 源代码采用 [GNU Affero General Public License v3.0](./LICENSE) 发布。通过网络提供修改后的 MaskFlow 服务时，请遵守 AGPL-3.0 的源码提供义务。

SAM 3、Ultralytics YOLO 模型权重及其他第三方组件分别受其自身许可条款约束，详见 [第三方声明](./THIRD_PARTY_NOTICES.md)。
