<script setup>
import { computed, inject, watch } from "vue";
import LineChart from "../LineChart.vue";
import { describeArtifact, groupResultImages, isActiveTraining, metricRows, trainingCurves, trainingStatusLabels, weightTaskLabels } from "../../lib/training";

const projects = inject("projects");
const selectedProject = inject("selectedProject");
const selectedProjectDataTypeLabel = inject("selectedProjectDataTypeLabel");
const selectProject = inject("selectProject");
const formatBytes = inject("formatBytes");
const formatDateTime = inject("formatDateTime");
const go = inject("go");
const training = inject("training");
const canTrain = inject("canTrain");
const canDeleteTraining = inject("canDeleteTraining");
const trainingWeights = inject("trainingWeights");
const trainingSplitTotal = inject("trainingSplitTotal");
const refreshTraining = inject("refreshTraining");
const openTraining = inject("openTraining");
const openTrainingConfirm = inject("openTrainingConfirm");
const confirmStartTraining = inject("confirmStartTraining");
const downloadBaseWeight = inject("downloadBaseWeight");
const deleteBaseWeight = inject("deleteBaseWeight");
const openLightbox = inject("openLightbox");
const cancelTraining = inject("cancelTraining");
const deleteTraining = inject("deleteTraining");
const downloadTrainingFile = inject("downloadTrainingFile");
const scheduleReadinessCheck = inject("scheduleReadinessCheck");

watch(() => [projects.selectedId, training.form.baseWeight, training.form.split.train, training.form.split.val, training.form.split.test, training.form.confirmedOnly],
  scheduleReadinessCheck);
const readiness = computed(() => training.readiness);
const blocked = computed(() => Boolean(readiness.value && (readiness.value.error || !readiness.value.ok)));

const run = computed(() => training.detail?.training || null);
const curves = computed(() => trainingCurves(training.detail?.history || [], run.value?.task));
const metrics = computed(() => training.detail?.metrics || null);
const images = computed(() => (run.value ? training.images[run.value.id] || {} : {}));
const gallery = computed(() => (run.value ? groupResultImages(run.value.artifacts) : []));
const nonImageFiles = computed(() => (run.value ? run.value.artifacts.filter((item) => !/\.(png|jpe?g)$/i.test(item.name)) : []));
const totalSize = computed(() => (run.value ? run.value.artifacts.reduce((sum, item) => sum + item.size, 0) : 0));

// ---- Base weight catalog ----
const taskFilters = [["all", "全部"], ["detect", "检测"], ["segment", "分割"], ["classify", "分类"]];
const catalogRows = computed(() => training.catalog.filter((item) => training.weightTask === "all" || item.task === training.weightTask));
const catalogFamilies = computed(() => [...new Set(catalogRows.value.map((item) => item.family))]);
const extraWeights = computed(() => training.weights.filter((weight) => !training.catalog.some((item) => item.name === weight.name)));

function downloadState(item) {
  const state = item.download;
  if (item.installed) return { text: "已下载", cls: "ok" };
  if (!state) return { text: "未下载", cls: "" };
  if (state.error) return { text: `失败：${state.error}`, cls: "fail" };
  return { text: `下载中 ${Math.floor((state.received / Math.max(1, state.total)) * 100)}%`, cls: "busy" };
}

async function copyLink(url) {
  try {
    await navigator.clipboard.writeText(url);
    training.status = "下载链接已复制";
  } catch {
    training.status = url;
  }
}

// ---- Confirmation ----
const selectedWeight = computed(() => trainingWeights.value.find((weight) => weight.name === training.form.baseWeight) || null);
function share(part, whole) {
  return whole ? `${Math.round((part / whole) * 100)}%` : "0%";
}

function statusClass(status) {
  return status === "completed" ? "ok" : status === "failed" ? "fail" : "";
}

function percent(item) {
  if (item.status === "completed") return 100;
  return item.epochs ? Math.min(100, Math.round((item.epoch / item.epochs) * 100)) : 0;
}

function headline(item) {
  const h = item.headline || {};
  if (Number.isFinite(h["mAP50-95"])) return `mAP50-95 ${h["mAP50-95"].toFixed(3)} · mAP50 ${h.mAP50?.toFixed(3) ?? "-"}`;
  if (Number.isFinite(h.top1)) return `Top-1 ${(h.top1 * 100).toFixed(1)}%`;
  return "";
}

function formatMetric(value) {
  return Number.isFinite(value) ? value.toFixed(3) : "-";
}
</script>

<template>
  <main class="mf-main page-pad work-page">
    <section class="work-wrap">
      <header class="work-title">
        <div class="work-title-icon">T</div>
        <div>
          <h1>模型训练</h1>
          <p>选一个已下载好的基础权重，用当前项目的标注直接训练、评估并导出权重。训练好的模型会自动加入项目模型，可直接用于自动标注。</p>
        </div>
      </header>
      <section class="project-bar">
        <div><strong>训练项目</strong><p>{{ selectedProject ? `${selectedProject.name} · ${selectedProjectDataTypeLabel}` : '请先选择项目' }}</p></div>
        <select :value="projects.selectedId" @change="selectProject($event.target.value)">
          <option value="">选择项目</option>
          <option v-for="project in projects.rows" :key="project.id" :value="project.id">{{ project.name }} · {{ project.imageCount || 0 }} 张 · {{ project.annotationCount || 0 }} 条标注</option>
        </select>
      </section>

      <nav class="billing-proto-tabs work-tabs">
        <a href="#" :class="{ active: training.tab === 'train' }" @click.prevent="training.tab = 'train'">训练</a>
        <a href="#" :class="{ active: training.tab === 'weights' }" @click.prevent="training.tab = 'weights'">基础权重 <small>{{ training.weights.length }}</small></a>
      </nav>

      <p v-if="!training.available" class="train-notice fail">
        训练服务暂不可用：{{ training.error || '无法连接' }}。部署时需要启动 trainer 服务（<code>docker compose --profile training up -d</code>）。
      </p>
      <p v-else-if="!training.weights.length && training.tab === 'train'" class="train-notice">
        还没有可用的基础权重。<a href="#" @click.prevent="training.tab = 'weights'">去「基础权重」一键下载</a>（推荐 YOLO26 / YOLO11 的 n 或 s 版本起步）。
      </p>
      <p v-if="training.status" class="train-notice">{{ training.status }}</p>

      <template v-if="training.tab === 'train'">
      <section class="train-grid">
        <article class="work-card train-form">
          <h2>新建训练</h2>
          <label>
            基础权重
            <select v-model="training.form.baseWeight" :disabled="!trainingWeights.length">
              <option value="" disabled>选择权重</option>
              <option v-for="weight in trainingWeights" :key="weight.name" :value="weight.name" :disabled="!weight.ok">
                {{ weight.name }} · {{ weightTaskLabels[weight.task] || weight.task }} · {{ formatBytes(weight.size) }}{{ weight.ok ? '' : `（${weight.reason}）` }}
              </option>
            </select>
          </label>
          <div class="train-params">
            <label>训练轮数<input v-model.number="training.form.epochs" type="number" min="1" max="1000" /></label>
            <label>图片尺寸<input v-model.number="training.form.imageSize" type="number" min="32" max="4096" step="32" /></label>
            <label>批大小<input v-model.number="training.form.batch" type="number" min="-1" max="512" title="-1 表示按显存自动选择" /></label>
            <label>早停轮数<input v-model.number="training.form.patience" type="number" min="0" max="1000" title="验证集指标连续多少轮不提升就停止" /></label>
          </div>
          <div class="train-split">
            <span>数据划分 <small :class="{ bad: trainingSplitTotal !== 100 }">合计 {{ trainingSplitTotal }}%</small></span>
            <label>训练<input v-model.number="training.form.split.train" type="number" min="0" max="100" /></label>
            <label>验证<input v-model.number="training.form.split.val" type="number" min="1" max="100" /></label>
            <label>测试<input v-model.number="training.form.split.test" type="number" min="0" max="100" /></label>
          </div>
          <label class="train-check"><input v-model="training.form.confirmedOnly" type="checkbox" />只用人工确认过的标注</label>
          <label>名称（可选）<input v-model="training.form.name" type="text" maxlength="200" :placeholder="selectedProject ? `${selectedProject.name} · ${training.form.baseWeight.replace(/\.pt$/, '')}` : ''" /></label>
          <button class="btn" type="button" :disabled="!canTrain || training.busy || !projects.selectedId || !training.form.baseWeight || blocked" :title="blocked ? '数据检查未通过，见右侧说明' : ''" @click="openTrainingConfirm">
            {{ training.busy ? '正在提交…' : '开始训练' }}
          </button>
          <p v-if="!canTrain && projects.selectedId" class="train-hint">只有项目所有者和审核员可以发起训练。</p>
          <p v-else class="train-hint">划分方式与数据集导出相同（按类别分层、相似图片分到同一组）。验证集用于挑选最佳权重，测试集只在最后评估一次。</p>
        </article>

        <article class="work-card train-check">
          <div class="section-head">
            <h2>数据检查</h2>
            <span v-if="training.checking" class="train-hint">检查中…</span>
          </div>
          <p v-if="!projects.selectedId" class="train-hint">先选择一个项目。</p>
          <p v-else-if="readiness?.error" class="train-notice fail">{{ readiness.error }}</p>
          <template v-else-if="readiness">
            <p :class="['train-verdict', readiness.ok ? 'ok' : 'fail']">
              {{ readiness.ok ? '可以开始训练' : '还不能训练' }}
              <small>{{ readiness.images }} 张可训练图片（训练 {{ readiness.train }} / 验证 {{ readiness.val }} / 测试 {{ readiness.test }}）<template v-if="readiness.backgroundImages"> · 背景图 {{ readiness.backgroundImages }}</template><template v-if="readiness.unannotatedImages"> · 未标注 {{ readiness.unannotatedImages }} 张不参与</template></small>
            </p>
            <ul v-if="readiness.blockers.length" class="train-list fail"><li v-for="item in readiness.blockers" :key="item">{{ item }}</li></ul>
            <ul v-if="readiness.warnings.length" class="train-list warn"><li v-for="item in readiness.warnings" :key="item">{{ item }}</li></ul>
            <div v-if="readiness.suggestions.length" class="train-suggest">
              <strong>建议补充</strong>
              <ul class="train-list"><li v-for="item in readiness.suggestions" :key="item">{{ item }}</li></ul>
            </div>
            <table v-if="readiness.classes.length" class="table train-classes">
              <thead><tr><th>类别</th><th>图片</th><th>目标</th><th>训练 / 验证 / 测试</th><th>建议补充</th></tr></thead>
              <tbody>
                <tr v-for="row in readiness.classes" :key="row.label" :class="{ short: row.images < readiness.thresholds.minImagesPerClass }">
                  <td>{{ row.label }}</td>
                  <td>{{ row.images }}</td>
                  <td>{{ row.instances }}</td>
                  <td>{{ row.train }} / {{ row.val }} / {{ row.test }}</td>
                  <td>{{ row.addImages ? `+${row.addImages}` : '—' }}</td>
                </tr>
              </tbody>
            </table>
            <p class="train-hint">至少 {{ readiness.thresholds.minImages }} 张已标注图片、每类至少 {{ readiness.thresholds.minImagesPerClass }} 张才能训练；每类 {{ readiness.thresholds.recommendedPerClass }} 张以上、类别间不超过 3:1 效果更稳定。</p>
          </template>
        </article>
      </section>

      <section class="train-runs-row">
        <article class="work-card train-runs">
          <div class="section-head">
            <h2>训练记录</h2>
            <button class="btn secondary compact-btn" type="button" @click="refreshTraining">刷新</button>
          </div>
          <button v-for="item in training.rows" :key="item.id" type="button" class="train-run" :class="{ active: item.id === training.selectedId }" @click="openTraining(item.id)">
            <span class="train-run-head">
              <strong>{{ item.name }}</strong>
              <span class="status-pill" :class="statusClass(item.status)">{{ trainingStatusLabels[item.status] || item.status }}</span>
            </span>
            <span class="progress"><i :style="{ width: `${percent(item)}%` }"></i></span>
            <span class="train-run-meta">
              {{ item.baseWeight }} · {{ item.epoch }}/{{ item.epochs }} 轮 · {{ item.imageCount }} 张 · {{ formatDateTime(item.createdAt) }}
            </span>
            <span v-if="headline(item)" class="train-run-score">{{ headline(item) }}</span>
          </button>
          <p v-if="!training.rows.length" class="train-hint">{{ projects.selectedId ? '这个项目还没有训练记录。' : '先选择一个项目。' }}</p>
        </article>
      </section>

      <article v-if="run" class="work-card train-detail">
        <div class="section-head">
          <div>
            <h2>{{ run.name }} <span class="status-pill" :class="statusClass(run.status)">{{ trainingStatusLabels[run.status] || run.status }}</span></h2>
            <p>{{ run.baseWeight }} · {{ weightTaskLabels[run.task] || run.task }} · 第 {{ run.epoch }}/{{ run.epochs }} 轮 · 图片 {{ run.request.imageSize }} · 批大小 {{ run.request.batch }} · {{ run.imageCount }} 张图片</p>
          </div>
          <div class="train-actions">
            <button v-if="isActiveTraining(run) && canTrain" class="btn secondary compact-btn" type="button" @click="cancelTraining(run)">取消训练</button>
            <button v-if="!isActiveTraining(run) && canDeleteTraining" class="btn ghost compact-btn" type="button" @click="deleteTraining(run)">删除</button>
          </div>
        </div>
        <p v-if="run.error" class="train-notice" :class="{ fail: run.status === 'failed' }">{{ run.error }}</p>
        <p v-if="run.modelId" class="train-notice ok">
          模型已注册到项目，在 <a href="#" @click.prevent="go('/annotate.html')">AI 标注工具</a> 里选择「自定义模型」即可用它自动标注。
        </p>

        <div class="train-charts">
          <LineChart title="损失（越低越好）" :series="curves.losses" />
          <LineChart :title="run.task === 'classify' ? '准确率' : '验证集 mAP'" :series="curves.metrics" unit-range />
        </div>

        <div v-if="metrics" class="train-eval">
          <section v-for="split in ['val', 'test']" :key="split">
            <template v-if="metrics[split]">
              <h3>{{ split === 'val' ? '验证集评估' : '测试集评估' }}</h3>
              <table class="table">
                <tbody>
                  <tr v-for="row in metricRows(metrics[split], run.task)" :key="row.label"><th>{{ row.label }}</th><td>{{ formatMetric(row.value) }}</td></tr>
                </tbody>
              </table>
              <table v-if="metrics[split].perClass?.length" class="table train-classes">
                <thead><tr><th>类别</th><th>mAP50</th><th>mAP50-95</th></tr></thead>
                <tbody>
                  <tr v-for="row in metrics[split].perClass" :key="row.class"><td>{{ row.class }}</td><td>{{ formatMetric(row.mAP50) }}</td><td>{{ formatMetric(row['mAP50-95']) }}</td></tr>
                </tbody>
              </table>
            </template>
          </section>
          <section v-if="!metrics.test" class="train-hint">没有测试集，指标来自验证集（训练时用于挑选最佳权重，会略偏乐观）。</section>
        </div>

        <section v-for="section in gallery" :key="section.key" class="train-gallery">
          <h3>{{ section.title }}</h3>
          <div v-if="section.pairs" class="train-pairs">
            <div v-for="pair in section.pairs" :key="pair.pred" class="train-pair">
              <figure v-if="pair.labels" @click="openLightbox(run, pair.labels, `${pair.title} · 人工标注`)">
                <img v-if="images[pair.labels]" :src="images[pair.labels]" :alt="`${pair.title} 人工标注`" loading="lazy" /><span v-else class="train-image-loading">加载中…</span>
                <figcaption>人工标注</figcaption>
              </figure>
              <figure @click="openLightbox(run, pair.pred, `${pair.title} · 模型预测`)">
                <img v-if="images[pair.pred]" :src="images[pair.pred]" :alt="`${pair.title} 模型预测`" loading="lazy" /><span v-else class="train-image-loading">加载中…</span>
                <figcaption>模型预测</figcaption>
              </figure>
            </div>
          </div>
          <div v-else class="train-images">
            <figure v-for="name in section.items" :key="name" @click="openLightbox(run, name, name)">
              <img v-if="images[name]" :src="images[name]" :alt="name" loading="lazy" /><span v-else class="train-image-loading">加载中…</span>
              <figcaption>{{ name }}</figcaption>
            </figure>
          </div>
        </section>

        <section v-if="run.artifacts.length" class="train-files">
          <div class="train-files-head">
            <div>
              <h3>训练文件</h3>
              <p class="train-hint">共 {{ run.artifacts.length }} 个文件，{{ formatBytes(totalSize) }}。</p>
            </div>
            <button v-if="!isActiveTraining(run) && canTrain" class="btn" type="button" @click="downloadTrainingFile(run)">下载训练包（.zip）</button>
          </div>
          <p class="train-hint">训练包包含：基础权重、本次训练的数据集、可复现的训练脚本 train.py、预测脚本 predict.py、依赖 requirements.txt、说明 README（含指标和使用方法），以及训练产生的全部文件（best.pt / last.pt / ONNX、曲线、混淆矩阵、预测效果图、results.csv、args.yaml、日志、环境信息）。</p>
          <table class="table train-file-table">
            <tbody>
              <tr v-for="artifact in nonImageFiles" :key="artifact.name">
                <td>{{ describeArtifact(artifact.name) }}<small v-if="describeArtifact(artifact.name) !== artifact.name">{{ artifact.name }}</small></td>
                <td>{{ formatBytes(artifact.size) }}</td>
                <td><button v-if="canTrain" class="text-link" type="button" @click="downloadTrainingFile(run, artifact.name)">下载</button></td>
              </tr>
            </tbody>
          </table>
        </section>
      </article>
      </template>

      <section v-else class="work-card weight-manager">
        <div class="section-head">
          <div>
            <h2>基础权重</h2>
            <p class="train-hint">Ultralytics 官方预训练权重（发布页 v8.4.0）。下载后保存在训练服务的权重目录（宿主机 <code>资源/训练权重</code>），所有项目都可以用来训练。也可以手动把 .pt 放进该目录。</p>
          </div>
          <div class="weight-filter">
            <button v-for="[key, label] in taskFilters" :key="key" type="button" :class="{ active: training.weightTask === key }" @click="training.weightTask = key">{{ label }}</button>
          </div>
        </div>
        <p v-if="!training.canManage" class="train-notice">只有管理员可以下载或删除基础权重（<code>MASKFLOW_ADMIN_EMAILS</code> 中的邮箱；未设置时为第一个注册的账号）。你可以使用已下载的权重训练。</p>
        <p v-else-if="training.available && !training.writable" class="train-notice fail">权重目录是只读的，无法下载。请把 compose 中的挂载改为可写（去掉 <code>:ro</code>）。</p>
        <p class="train-hint">选择建议：数据少或要部署到边缘设备用 n / s；追求精度且 GPU 充足用 m / l / x。YOLO26 最新，YOLO11 与 YOLOv8 也很成熟。分割项目用 -seg，分类用 -cls。</p>
        <div v-for="family in catalogFamilies" :key="family" class="weight-family">
          <h3>{{ family }}</h3>
          <table class="table weight-table">
            <thead><tr><th>权重</th><th>任务</th><th>规格</th><th>大小</th><th>状态</th><th>官方下载地址</th><th></th></tr></thead>
            <tbody>
              <tr v-for="item in catalogRows.filter((row) => row.family === family)" :key="item.name">
                <td><strong>{{ item.name }}</strong></td>
                <td>{{ weightTaskLabels[item.task] || item.task }}</td>
                <td>{{ item.scaleLabel }}</td>
                <td>{{ formatBytes(item.size) }}</td>
                <td>
                  <span :class="['weight-state', downloadState(item).cls]">{{ downloadState(item).text }}</span>
                  <span v-if="item.download && !item.download.done && !item.download.error" class="progress weight-progress"><i :style="{ width: `${Math.floor((item.download.received / Math.max(1, item.download.total)) * 100)}%` }"></i></span>
                </td>
                <td class="weight-url"><a :href="item.url" :title="item.url" target="_blank" rel="noopener noreferrer">{{ item.url.replace(/^https?:\/\//, '') }}</a> <button class="text-link" type="button" @click="copyLink(item.url)">复制</button></td>
                <td class="weight-actions">
                  <template v-if="training.canManage">
                    <button v-if="!item.installed" class="btn compact-btn" type="button" :disabled="!training.writable || (item.download && !item.download.done && !item.download.error)" @click="downloadBaseWeight(item)">{{ item.download?.error ? '重试' : '下载' }}</button>
                    <button v-else class="btn ghost compact-btn" type="button" :disabled="training.inUse === item.name" :title="training.inUse === item.name ? '正在被训练使用' : ''" @click="deleteBaseWeight(item.name)">删除</button>
                  </template>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <div v-if="extraWeights.length" class="weight-family">
          <h3>手动放入的权重</h3>
          <table class="table weight-table">
            <tbody>
              <tr v-for="weight in extraWeights" :key="weight.name">
                <td><strong>{{ weight.name }}</strong></td>
                <td>{{ weightTaskLabels[weight.task] || weight.task }}</td>
                <td>{{ formatBytes(weight.size) }}</td>
                <td class="weight-actions"><button v-if="training.canManage" class="btn ghost compact-btn" type="button" @click="deleteBaseWeight(weight.name)">删除</button></td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
    </section>

    <div v-if="training.confirmOpen && training.readiness && !training.readiness.error" class="modal-mask" @click.self="training.confirmOpen = false">
      <section class="train-confirm" role="dialog" aria-modal="true" aria-labelledby="train-confirm-title">
        <h2 id="train-confirm-title">确认开始训练</h2>
        <dl class="train-confirm-facts">
          <div><dt>项目</dt><dd>{{ selectedProject?.name }}（{{ selectedProjectDataTypeLabel }}）</dd></div>
          <div><dt>基础权重</dt><dd>{{ training.form.baseWeight }} · {{ weightTaskLabels[selectedWeight?.task] || '' }}</dd></div>
          <div><dt>训练参数</dt><dd>{{ training.form.epochs }} 轮 · 图片 {{ training.form.imageSize }} · 批大小 {{ training.form.batch }} · 早停 {{ training.form.patience }} 轮</dd></div>
          <div><dt>标注范围</dt><dd>{{ training.form.confirmedOnly ? '只用人工确认过的目标' : '包含未确认的 AI 结果' }}</dd></div>
          <div><dt>参与训练</dt><dd><strong>{{ training.readiness.images }} 张图片</strong><template v-if="training.readiness.backgroundImages">（含背景图 {{ training.readiness.backgroundImages }} 张）</template><template v-if="training.readiness.unannotatedImages">，另有 {{ training.readiness.unannotatedImages }} 张未标注不参与</template></dd></div>
          <div><dt>数据划分</dt><dd>
            训练集 <strong>{{ training.readiness.train }}</strong> 张（{{ share(training.readiness.train, training.readiness.images) }}）·
            验证集 <strong>{{ training.readiness.val }}</strong> 张（{{ share(training.readiness.val, training.readiness.images) }}）·
            测试集 <strong>{{ training.readiness.test }}</strong> 张（{{ share(training.readiness.test, training.readiness.images) }}）
            <small>设定 {{ training.form.split.train }}/{{ training.form.split.val }}/{{ training.form.split.test }}，按类别分层、相似图片分在同一组</small>
          </dd></div>
        </dl>
        <table class="table train-classes">
          <thead><tr><th>类别</th><th>图片</th><th>目标</th><th>训练</th><th>验证</th><th>测试</th></tr></thead>
          <tbody>
            <tr v-for="row in training.readiness.classes" :key="row.label">
              <td>{{ row.label }}</td><td>{{ row.images }}</td><td>{{ row.instances }}</td><td>{{ row.train }}</td><td>{{ row.val }}</td><td>{{ row.test }}</td>
            </tr>
          </tbody>
        </table>
        <h3>注意事项</h3>
        <ul class="train-list">
          <li v-for="item in training.readiness.warnings" :key="item" class="warn">{{ item }}</li>
          <li>数据集在训练真正开始时（轮到这个训练时）按当时的标注生成；之后再改标注不会影响本次训练。</li>
          <li>训练服务一次只运行一个训练，其他训练会排队；期间会占用 GPU，训练时长取决于 GPU、图片数量和轮数。</li>
          <li>验证集指标连续 {{ training.form.patience }} 轮没有提升会提前结束（早停），这是正常现象。</li>
          <li>完成后会保存：本次数据集副本、基础权重副本和全部训练结果，计入项目所有者的存储空间；可以随时下载训练包。</li>
          <li>可以离开页面，完成或失败都会发邮件通知（如已开启任务邮件通知）。</li>
        </ul>
        <div v-if="training.readiness.suggestions.length" class="train-suggest">
          <strong>还可以改进</strong>
          <ul class="train-list"><li v-for="item in training.readiness.suggestions" :key="item">{{ item }}</li></ul>
        </div>
        <div class="train-confirm-actions">
          <button class="btn secondary" type="button" @click="training.confirmOpen = false">返回修改</button>
          <button class="btn" type="button" :disabled="training.busy" @click="confirmStartTraining">确认开始训练</button>
        </div>
      </section>
    </div>

    <div v-if="training.lightbox" class="modal-mask lightbox" @click="training.lightbox = null">
      <figure @click.stop>
        <img :src="training.lightbox.url" :alt="training.lightbox.caption" />
        <figcaption>
          {{ training.lightbox.caption }}
          <button v-if="canTrain" class="text-link" type="button" @click="downloadTrainingFile(training.lightbox.run, training.lightbox.name)">下载原图</button>
          <button class="text-link" type="button" @click="training.lightbox = null">关闭</button>
        </figcaption>
      </figure>
    </div>
  </main>
</template>
