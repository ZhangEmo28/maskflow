<script setup>
import { computed, inject, ref } from "vue";

const projects = inject("projects");
const selectedProject = inject("selectedProject");
const files = inject("files");
const account = inject("account");
const loading = inject("loading");
const uploadQueue = inject("uploadQueue");
const uploadQueueStatusLabel = inject("uploadQueueStatusLabel");
const formatBytes = inject("formatBytes");
const selectProject = inject("selectProject");
const createProject = inject("createProject");
const deleteCurrentProject = inject("deleteCurrentProject");
const uploadFiles = inject("uploadFiles");
const deleteFile = inject("deleteFile");
const importer = inject("importer");
const importDataset = inject("importDataset");
const downloadFileItem = inject("downloadFileItem");
const formatDateTime = inject("formatDateTime");
const videos = inject("videos");
const uploadVideo = inject("uploadVideo");
const deleteVideo = inject("deleteVideo");
const annotate = inject("annotate");
const fileStatusText = inject("fileStatusText");
const fileStatusGroup = inject("fileStatusGroup");
const isFileSelected = inject("isFileSelected");
const toggleFileSelection = inject("toggleFileSelection");
const setAllSelected = inject("setAllSelected");
const clearFileSelection = inject("clearFileSelection");
const clearSelectedAnnotations = inject("clearSelectedAnnotations");
const deleteSelectedFiles = inject("deleteSelectedFiles");
const canReviewProject = inject("canReviewProject");
const go = inject("go");
const dropFilesToProject = inject("dropFilesToProject");
const dragOver = ref(false);

function chooseFiles(event) {
  files.selected = event.target.files;
  uploadFiles();
  event.target.value = "";
}

function onDrop(event) {
  dragOver.value = false;
  dropFilesToProject(event);
}

const statusFilter = ref("all");
const visibleFiles = computed(() => statusFilter.value === "all" ? files.rows : files.rows.filter((file) => fileStatusGroup(file) === statusFilter.value));
const allVisibleSelected = computed(() => visibleFiles.value.length > 0 && visibleFiles.value.every(isFileSelected));

async function confirmDelete(file) {
  const detail = file.annotationCount ? `，它的 ${file.annotationCount} 个目标也会一起删除` : "";
  if (window.confirm(`删除 ${file.name}${detail}？`)) await deleteFile(file.id);
}
</script>

<template>
  <main class="mf-main page-pad work-page">
    <section class="work-wrap">
      <header class="work-title">
        <div class="work-title-icon">U</div>
        <div><h1>上传图片</h1><p>按项目管理用于 SAM 分割、YOLO 标注和数据集导出的图片素材。</p></div>
      </header>
      <nav class="billing-proto-tabs work-tabs"><a class="active">图片文件</a><a>上传记录</a></nav>
      <section class="project-bar">
        <div><strong>当前项目</strong><p>{{ selectedProject?.name || '请先选择或创建项目' }}</p></div>
        <select v-model="projects.selectedId" @change="selectProject(projects.selectedId)">
          <option value="">选择项目</option>
          <option v-for="project in projects.rows" :key="project.id" :value="project.id">{{ project.name }} · {{ project.imageCount || 0 }} 张</option>
        </select>
        <input v-model="projects.newName" placeholder="新项目名称，例如 药瓶分类" @keyup.enter="createProject" />
        <select v-model="projects.newDataType" class="project-type-select" title="新建项目的任务类型">
          <option value="detection">目标检测</option>
          <option value="segmentation">实例分割</option>
        </select>
        <button class="btn compact-btn" type="button" @click="createProject">新增项目</button>
        <button class="btn secondary compact-btn" type="button" :disabled="!projects.selectedId" @click="deleteCurrentProject">删除项目</button>
      </section>
      <section class="work-bottom">
        <div class="files-side-column">
        <article class="work-card">
          <h2>上传图片</h2>
          <label :class="['drop-zone', { over: dragOver, disabled: loading || !projects.selectedId }]" @dragover.prevent="dragOver = true" @dragleave="dragOver = false" @drop.prevent="onDrop">
            <input type="file" multiple accept="image/*" :disabled="loading || !projects.selectedId" @change="chooseFiles" />
            <strong>{{ projects.selectedId ? '拖拽图片到这里，或点击选择' : '请先选择或创建项目' }}</strong>
            <small>可一次选择多张，选择后立即上传到当前项目</small>
          </label>
          <div v-if="uploadQueue.items.length" class="upload-queue-panel compact">
            <div class="upload-queue-head">
              <strong>上传进度</strong>
              <span>{{ uploadQueue.done + uploadQueue.failed + uploadQueue.skipped }} / {{ uploadQueue.total }}</span>
            </div>
            <div class="progress upload-progress">
              <i :style="{ width: uploadQueue.percent + '%' }"></i>
            </div>
            <p v-if="uploadQueue.currentName" class="upload-current">正在上传：{{ uploadQueue.currentName }}</p>
            <ul class="upload-queue-list">
              <li v-for="item in uploadQueue.items" :key="item.id" :class="['upload-queue-item', item.status]">
                <span class="upload-queue-name">{{ item.name }}</span>
                <em>{{ uploadQueueStatusLabel(item) }}</em>
              </li>
            </ul>
          </div>
          <p v-if="projects.status">{{ projects.status }}</p>
          <p v-if="account">空间：{{ formatBytes(account.usedBytes) }} / {{ formatBytes(account.quotaBytes) }}</p>
        </article>
        <article class="work-card video-card">
          <h2>上传视频抽帧</h2>
          <p>上传 mp4 / mov / webm 等视频，服务器按间隔抽帧，帧会作为普通图片加入项目；标注一帧后可在标注页“向后传播”。</p>
          <div class="import-options video-options">
            <label>每隔几秒抽一帧<input v-model.number="videos.everySeconds" type="number" min="0.04" step="0.5" /></label>
            <label>最多帧数<input v-model.number="videos.maxFrames" type="number" min="1" max="2000" /></label>
          </div>
          <input type="file" accept="video/*,.mkv" :disabled="videos.busy" @change="videos.file = $event.target.files[0] || null" />
          <button class="btn" type="button" :disabled="videos.busy || !videos.file || !projects.selectedId" @click="uploadVideo">
            {{ videos.busy ? '正在上传…' : '上传并抽帧' }}
          </button>
          <p v-if="videos.status">{{ videos.status }}</p>
          <ul v-if="videos.rows.length" class="video-list">
            <li v-for="video in videos.rows" :key="video.id">
              <div>
                <strong>{{ video.name }}</strong>
                <small>
                  {{ { queued: '排队中', processing: '抽帧中…', ready: video.frameCount + ' 帧 · ' + Number(video.durationSec).toFixed(1) + ' 秒', failed: '失败：' + video.error }[video.status] }}
                </small>
              </div>
              <button class="label-delete-btn" type="button" @click="deleteVideo(video)">删除</button>
            </li>
          </ul>
        </article>
        <article class="work-card import-card">
          <h2>导入已有数据集</h2>
          <p>上传 zip（图片 + 标注）或单个标注文件。支持 YOLO（txt + data.yaml / classes.txt）、COCO、LabelMe、Pascal VOC，按图片文件名对应；新类别会自动加入项目标签。</p>
          <div class="import-options">
            <label>格式
              <select v-model="importer.format">
                <option value="auto">自动识别</option>
                <option value="yolo">YOLO</option>
                <option value="coco">COCO</option>
                <option value="labelme">LabelMe</option>
                <option value="voc">Pascal VOC</option>
              </select>
            </label>
            <label>已有标注的图片
              <select v-model="importer.onExisting">
                <option value="skip">跳过，保留现有标注</option>
                <option value="replace">用导入的标注替换</option>
              </select>
            </label>
            <label class="import-check"><input v-model="importer.confirmed" type="checkbox" /> 导入的标注视为已人工确认</label>
          </div>
          <input type="file" accept=".zip,.json,.xml" :disabled="importer.busy" @change="importer.file = $event.target.files[0] || null" />
          <button class="btn" type="button" :disabled="importer.busy || !importer.file || !projects.selectedId" @click="importDataset">
            {{ importer.busy ? '正在导入…' : '导入到当前项目' }}
          </button>
          <p v-if="importer.error" class="form-error">{{ importer.error }}</p>
          <div v-if="importer.summary" class="import-summary">
            <strong>导入完成（{{ importer.summary.format }}）</strong>
            <p>新增图片 {{ importer.summary.imagesAdded }} 张 · 写入标注 {{ importer.summary.imagesAnnotated }} 张 / {{ importer.summary.annotationsImported }} 个目标<span v-if="importer.summary.skippedExisting"> · 跳过已有标注 {{ importer.summary.skippedExisting }} 张</span></p>
            <p v-if="importer.summary.newLabels.length">新增标签：{{ importer.summary.newLabels.join('、') }}</p>
            <ul v-if="importer.summary.warnings.length">
              <li v-for="warning in importer.summary.warnings" :key="warning">{{ warning }}</li>
            </ul>
          </div>
        </article>
        </div>
        <article class="work-card wide">
          <div class="files-table-head">
            <h2>文件列表 <small>{{ files.rows.length }} 张</small></h2>
            <select v-model="statusFilter" aria-label="按状态筛选">
              <option value="all">全部状态</option>
              <option value="unannotated">未标注</option>
              <option value="review">待确认</option>
              <option value="done">已确认 / 背景图</option>
            </select>
          </div>
          <div v-if="annotate.selectedIds.length" class="files-bulk-bar">
            <strong>已选 {{ annotate.selectedIds.length }} 张</strong>
            <button class="btn secondary compact-btn" type="button" @click="go('/annotate.html')">去标注页 AI 标注</button>
            <button class="btn secondary compact-btn" type="button" title="去掉未确认的 AI 结果，保留人工确认的目标" @click="clearSelectedAnnotations('unconfirmed')">清除未确认</button>
            <button v-if="canReviewProject" class="btn secondary compact-btn" type="button" @click="clearSelectedAnnotations('all')">清除全部标注</button>
            <button v-if="canReviewProject" class="btn danger compact-btn" type="button" @click="deleteSelectedFiles">删除所选</button>
            <button class="btn ghost compact-btn" type="button" @click="clearFileSelection">取消选择</button>
          </div>
          <table>
            <thead>
              <tr>
                <th class="check-cell"><input type="checkbox" :checked="allVisibleSelected" :indeterminate.prop="visibleFiles.some(isFileSelected) && !allVisibleSelected" title="全选当前筛选" @change="setAllSelected(visibleFiles, $event.target.checked)" /></th>
                <th>文件名</th>
                <th>大小</th>
                <th>状态</th>
                <th>上传时间</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="file in visibleFiles" :key="file.id" :class="{ 'row-selected': isFileSelected(file) }">
                <td class="check-cell"><input type="checkbox" :checked="isFileSelected(file)" :aria-label="'选择 ' + file.name" @click="toggleFileSelection(file, $event, visibleFiles)" /></td>
                <td>{{ file.name }}</td>
                <td>{{ formatBytes(file.size) }}</td>
                <td><em :class="['status-chip', file.status]">{{ fileStatusText[file.status] || '未标注' }}</em> {{ file.annotationCount ? file.annotationCount + ' 个目标' : '' }}</td>
                <td>{{ formatDateTime(file.createdAt) }}</td>
                <td><a href="#" @click.prevent="downloadFileItem(file)">下载</a> <button v-if="canReviewProject" class="text-danger" type="button" @click="confirmDelete(file)">删除</button></td>
              </tr>
              <tr v-if="!visibleFiles.length"><td colspan="6">{{ files.rows.length ? '当前筛选下没有图片。' : '当前项目暂无图片文件。' }}</td></tr>
            </tbody>
          </table>
        </article>
      </section>
    </section>
  </main>
</template>
