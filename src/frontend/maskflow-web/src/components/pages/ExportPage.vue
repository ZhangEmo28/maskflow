<script setup>
import { inject } from "vue";
import DatasetAnalysisPanel from "../DatasetAnalysisPanel.vue";

const projects = inject("projects");
const selectedProject = inject("selectedProject");
const selectedProjectDataTypeLabel = inject("selectedProjectDataTypeLabel");
const exportPage = inject("exportPage");
const loading = inject("loading");
const formatBytes = inject("formatBytes");
const formatDateTime = inject("formatDateTime");
const selectProject = inject("selectProject");
const createExport = inject("createExport");
const exportSplitTotal = inject("exportSplitTotal");
const downloadExportItem = inject("downloadExportItem");
const refreshExports = inject("refreshExports");
const validExportConfig = inject("validExportConfig");
</script>

<template>
  <main class="mf-main page-pad work-page">
    <section class="work-wrap">
      <header class="work-title">
        <div class="work-title-icon">E</div>
        <div><h1>数据集导出</h1><p>按 YOLO、COCO 或 Pascal VOC 格式生成训练集、验证集、测试集。</p></div>
      </header>
      <nav class="billing-proto-tabs work-tabs">
        <a href="#" :class="{ active: exportPage.tab === 'config' }" @click.prevent="exportPage.tab = 'config'">导出配置</a>
        <a href="#" :class="{ active: exportPage.tab === 'history' }" @click.prevent="exportPage.tab = 'history'">历史记录</a>
      </nav>
      <section class="project-bar">
        <div><strong>导出项目</strong><p>{{ selectedProject?.name || '请先选择项目' }}</p></div>
        <select :value="projects.selectedId" @change="selectProject($event.target.value)">
          <option value="">选择项目</option>
          <option v-for="project in projects.rows" :key="project.id" :value="project.id">{{ project.name }} · {{ project.imageCount || 0 }} 张 · {{ project.annotationCount || 0 }} 条标注</option>
        </select>
      </section>

      <DatasetAnalysisPanel v-if="exportPage.tab === 'config'" />
      <section v-if="exportPage.tab === 'config'" class="work-bottom export-config-grid">
        <article class="work-card">
          <h2>导出配置</h2>
          <p>项目：{{ selectedProject?.name || '-' }}</p>
          <p>任务类型：{{ selectedProject ? selectedProjectDataTypeLabel : '-' }}</p>
          <div class="export-format-picker">
            <label :class="{ active: exportPage.format === 'yolo-detect' }">
              <input v-model="exportPage.format" type="radio" value="yolo-detect" />
              <span>YOLO 检测</span>
              <small>使用矩形框，导出 detect 数据集</small>
            </label>
            <label :class="{ active: exportPage.format === 'yolo-segment' }">
              <input v-model="exportPage.format" type="radio" value="yolo-segment" />
              <span>YOLO 分割</span>
              <small>已分配标签的目标均需有有效分割轮廓</small>
            </label>
            <label :class="{ active: exportPage.format === 'classification-crops' }">
              <input v-model="exportPage.format" type="radio" value="classification-crops" />
              <span>分类裁剪</span>
              <small>按标注框裁出目标图，按标签目录归类</small>
            </label>
            <label :class="{ active: exportPage.format === 'coco' }">
              <input v-model="exportPage.format" type="radio" value="coco" />
              <span>COCO</span>
              <small>instances_*.json，含框与多边形（Detectron2 / MMDetection 可直接用）</small>
            </label>
            <label :class="{ active: exportPage.format === 'voc' }">
              <input v-model="exportPage.format" type="radio" value="voc" />
              <span>Pascal VOC</span>
              <small>每张图一个 XML，含 ImageSets 划分</small>
            </label>
          </div>
          <div class="export-options">
            <label><input v-model="exportPage.confirmedOnly" type="checkbox" /> 只导出人工确认的目标</label>
            <label><input v-model="exportPage.groupNearDuplicates" type="checkbox" /> 近重复图片放进同一集合（防止训练/测试泄漏）</label>
          </div>
          <div class="export-split-form">
            <label>训练集 train %<input v-model.number="exportPage.split.train" type="number" min="0" max="100" /></label>
            <label>验证集 val %<input v-model.number="exportPage.split.val" type="number" min="0" max="100" /></label>
            <label>测试集 test %<input v-model.number="exportPage.split.test" type="number" min="0" max="100" /></label>
            <label>随机种子<input v-model.number="exportPage.seed" type="number" min="-2147483648" max="2147483647" step="1" /></label>
          </div>
          <p :class="['export-split-total', { invalid: exportSplitTotal() !== 100 }]">当前合计：{{ exportSplitTotal() }}%（需等于 100%）</p>
          <button class="btn" :disabled="loading || !projects.selectedId || !validExportConfig() || exportPage.analysisLoading || !exportPage.analysis?.canExport" @click="createExport">
            {{ loading ? '正在导出...' : '导出当前项目 ZIP' }}
          </button>
          <p v-if="exportPage.status" class="export-status">{{ exportPage.status }}</p>
        </article>
        <article class="work-card wide">
          <h2>目录预览</h2>
          <pre v-if="exportPage.format === 'coco'">{{ selectedProject?.name || 'project' }}/
  images/train
  images/val
  images/test
  annotations/instances_train.json
  annotations/instances_val.json
  annotations/instances_test.json
  split-report.json</pre>
          <pre v-else-if="exportPage.format === 'voc'">{{ selectedProject?.name || 'project' }}/
  JPEGImages/
  Annotations/*.xml
  ImageSets/Main/train.txt
  ImageSets/Main/val.txt
  ImageSets/Main/test.txt
  labels.txt
  split-report.json</pre>
          <pre v-else-if="exportPage.format !== 'classification-crops'">{{ selectedProject?.name || 'project' }}/
  images/train
  images/val
  images/test
  labels/train
  labels/val
  labels/test
  data.yaml
  split-report.json</pre>
          <pre v-else>{{ selectedProject?.name || 'project' }}/
  classification/train/{label}/crop.jpg
  classification/val/{label}/crop.jpg
  classification/test/{label}/crop.jpg
  classes.txt
  split-report.json</pre>
        </article>
      </section>

      <section v-else class="work-card export-history-card">
        <div class="section-head">
          <h2>导出历史</h2>
          <button class="btn secondary compact-btn" type="button" :disabled="loading" @click="refreshExports">刷新</button>
        </div>
        <p v-if="exportPage.status" class="export-status">{{ exportPage.status }}</p>
        <table class="table">
          <thead>
            <tr>
              <th>导出 ID</th>
              <th>项目</th>
              <th>格式</th>
              <th>划分</th>
              <th>大小</th>
              <th>时间</th>
              <th>操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="item in exportPage.rows" :key="item.id">
              <td>{{ item.id }}</td>
              <td>{{ item.projectName || item.projectId || '-' }}</td>
              <td>{{ item.format || '-' }}</td>
              <td>{{ item.split ? `train ${item.split.train}% / val ${item.split.val}% / test ${item.split.test}%` : '-' }}</td>
              <td>{{ formatBytes(item.size) }}</td>
              <td>{{ formatDateTime(item.finishedAt || item.createdAt) }}</td>
              <td><button class="btn secondary compact-btn" type="button" :disabled="loading || item.status !== 'completed'" @click="downloadExportItem(item)">下载</button></td>
            </tr>
            <tr v-if="!exportPage.rows.length"><td colspan="7">当前项目还没有导出记录。</td></tr>
          </tbody>
        </table>
      </section>
    </section>
  </main>
</template>
