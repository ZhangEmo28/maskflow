<script setup>
import { computed, inject, watch } from "vue";
import LineChart from "../LineChart.vue";
import { isActiveTraining, metricRows, trainingCurves, trainingStatusLabels, weightTaskLabels } from "../../lib/training";

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
const startTraining = inject("startTraining");
const cancelTraining = inject("cancelTraining");
const deleteTraining = inject("deleteTraining");
const downloadTrainingArtifact = inject("downloadTrainingArtifact");
const scheduleReadinessCheck = inject("scheduleReadinessCheck");

watch(() => [projects.selectedId, training.form.baseWeight, training.form.split.train, training.form.split.val, training.form.split.test, training.form.confirmedOnly],
  scheduleReadinessCheck);
const readiness = computed(() => training.readiness);
const blocked = computed(() => Boolean(readiness.value && (readiness.value.error || !readiness.value.ok)));

const run = computed(() => training.detail?.training || null);
const curves = computed(() => trainingCurves(training.detail?.history || [], run.value?.task));
const metrics = computed(() => training.detail?.metrics || null);
const images = computed(() => (run.value ? training.images[run.value.id] || {} : {}));
const artifactLabels = {
  "best.pt": "最佳权重 best.pt",
  "best.onnx": "ONNX 模型 best.onnx",
  "results.csv": "训练日志 results.csv",
  "results.png": "训练曲线 results.png",
  "confusion_matrix.png": "混淆矩阵",
  "confusion_matrix_normalized.png": "归一化混淆矩阵",
  "metrics.json": "评估结果 metrics.json"
};

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

      <p v-if="!training.available" class="train-notice fail">
        训练服务暂不可用：{{ training.error || '无法连接' }}。部署时需要启动 trainer 服务（<code>docker compose --profile training up -d</code>），并把基础权重放到 <code>资源/训练权重</code> 目录。
      </p>
      <p v-else-if="!training.weights.length" class="train-notice">
        训练权重目录里还没有 .pt 文件。请把下载好的基础权重（如 yolo11n.pt、yolo11s-seg.pt、yolo11n-cls.pt）放到 <code>资源/训练权重</code> 目录，刷新后即可选择。
      </p>
      <p v-if="training.status" class="train-notice">{{ training.status }}</p>

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
          <button class="btn" type="button" :disabled="!canTrain || training.busy || !projects.selectedId || !training.form.baseWeight || blocked" :title="blocked ? '数据检查未通过，见右侧说明' : ''" @click="startTraining">
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

        <div v-if="Object.keys(images).length" class="train-images">
          <figure v-for="(url, name) in images" :key="name"><img :src="url" :alt="artifactLabels[name]" /><figcaption>{{ artifactLabels[name] }}</figcaption></figure>
        </div>

        <div v-if="run.artifacts.length" class="train-artifacts">
          <h3>导出训练结果</h3>
          <button v-for="artifact in run.artifacts" :key="artifact.name" class="btn secondary compact-btn" type="button" @click="downloadTrainingArtifact(run, artifact.name)">
            {{ artifactLabels[artifact.name] || artifact.name }} · {{ formatBytes(artifact.size) }}
          </button>
        </div>
      </article>
    </section>
  </main>
</template>
