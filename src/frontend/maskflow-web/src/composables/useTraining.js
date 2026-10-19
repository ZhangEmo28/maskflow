import { computed, reactive } from "vue";
import { apiFetch, authHeaders, downloadAuthenticated } from "../lib/api";
import { isActiveTraining, weightCompatibility } from "../lib/training";

export function useTraining({ projects, selectedProject, currentRole }) {
  const training = reactive({
    available: true,
    error: "",
    weights: [],
    rows: [],
    selectedId: "",
    detail: null,
    images: {},
    status: "",
    busy: false,
    readiness: null,
    checking: false,
    form: {
      baseWeight: "",
      epochs: 50,
      imageSize: 640,
      batch: 16,
      patience: 20,
      split: { train: 70, val: 20, test: 10 },
      confirmedOnly: false,
      name: ""
    }
  });
  let timer = null;
  let checkTimer = null;
  let checkSeq = 0;

  function requestBody() {
    const form = training.form;
    return {
      baseWeight: form.baseWeight,
      epochs: Number(form.epochs),
      imageSize: Number(form.imageSize),
      batch: Number(form.batch),
      patience: Number(form.patience),
      split: { train: Number(form.split.train), val: Number(form.split.val), test: Number(form.split.test) },
      confirmedOnly: form.confirmedOnly,
      name: form.name.trim() || null
    };
  }

  // Re-checks the data whenever project, weight, split or "confirmed only" change (debounced).
  function scheduleReadinessCheck() {
    clearTimeout(checkTimer);
    checkTimer = setTimeout(checkReadiness, 350);
  }

  async function checkReadiness() {
    const projectId = projects.selectedId;
    const seq = ++checkSeq;
    if (!projectId) {
      training.readiness = null;
      return;
    }
    if (trainingSplitTotal.value !== 100) {
      training.readiness = { error: "训练、验证、测试比例之和需要等于 100%。" };
      return;
    }
    training.checking = true;
    try {
      const data = await apiFetch(`/api/projects/${projectId}/trainings/check`, { method: "POST", body: requestBody() });
      if (seq === checkSeq) training.readiness = data.readiness;
    } catch (error) {
      if (seq === checkSeq) training.readiness = { error: error.message };
    } finally {
      if (seq === checkSeq) training.checking = false;
    }
  }

  const canTrain = computed(() => ["owner", "reviewer"].includes(currentRole.value));
  const canDeleteTraining = computed(() => currentRole.value === "owner");
  const trainingWeights = computed(() => training.weights.map((weight) => ({
    ...weight,
    ...weightCompatibility(weight, selectedProject.value?.dataType)
  })));
  const trainingSplitTotal = computed(() => training.form.split.train + training.form.split.val + training.form.split.test);

  async function loadWeights() {
    try {
      const data = await apiFetch("/api/training/weights");
      training.available = data.available;
      training.error = data.error || "";
      training.weights = data.weights || [];
    } catch (error) {
      training.available = false;
      training.error = error.message;
      training.weights = [];
    }
    // Default to the weight that matches the project type (segment for segmentation, else detect).
    const usable = trainingWeights.value.filter((weight) => weight.ok);
    if (!usable.some((weight) => weight.name === training.form.baseWeight)) {
      const preferred = selectedProject.value?.dataType === "segmentation" ? "segment" : "detect";
      training.form.baseWeight = (usable.find((weight) => weight.task === preferred) || usable[0])?.name || "";
    }
  }

  async function loadRuns() {
    if (!projects.selectedId) {
      training.rows = [];
      training.detail = null;
      training.selectedId = "";
      return;
    }
    const data = await apiFetch(`/api/projects/${projects.selectedId}/trainings`);
    training.rows = data.trainings || [];
    if (!training.rows.some((run) => run.id === training.selectedId)) {
      training.selectedId = training.rows[0]?.id || "";
      training.detail = null;
    }
    if (training.selectedId) await loadDetail(training.selectedId);
  }

  async function loadDetail(id) {
    const data = await apiFetch(`/api/trainings/${id}`);
    if (training.selectedId !== id) return;
    const finishedNow = training.detail?.training?.id === id && isActiveTraining(training.detail.training) && !isActiveTraining(data.training);
    training.detail = data;
    if (finishedNow || !training.images[id]) await loadImages(data.training);
  }

  // Plots are behind auth, so they are fetched as blobs rather than linked.
  async function loadImages(run) {
    const urls = {};
    for (const name of ["confusion_matrix_normalized.png", "confusion_matrix.png", "results.png"]) {
      if (!run.artifacts.some((artifact) => artifact.name === name)) continue;
      const response = await fetch(`/api/trainings/${run.id}/artifacts/${name}`, { headers: authHeaders() });
      if (response.ok) urls[name] = URL.createObjectURL(await response.blob());
    }
    for (const url of Object.values(training.images[run.id] || {})) URL.revokeObjectURL(url);
    training.images = { ...training.images, [run.id]: urls };
  }

  function schedulePoll() {
    clearTimeout(timer);
    if (!training.rows.some(isActiveTraining)) return;
    timer = setTimeout(async () => {
      try {
        await loadRuns();
      } catch (error) {
        training.status = error.message;
      }
      schedulePoll();
    }, 3000);
  }

  async function refreshTraining() {
    training.status = "";
    try {
      await Promise.all([loadWeights(), loadRuns()]);
    } catch (error) {
      training.status = error.message;
    }
    schedulePoll();
    scheduleReadinessCheck();
  }

  function stopTrainingPoll() {
    clearTimeout(timer);
    clearTimeout(checkTimer);
  }

  async function openTraining(id) {
    training.selectedId = id;
    training.detail = null;
    await loadDetail(id);
  }

  async function startTraining() {
    if (!projects.selectedId || training.busy) return;
    if (trainingSplitTotal.value !== 100) {
      training.status = "训练、验证、测试比例之和需要等于 100%。";
      return;
    }
    training.busy = true;
    training.status = "";
    try {
      const data = await apiFetch(`/api/projects/${projects.selectedId}/trainings`, { method: "POST", body: requestBody() });
      training.selectedId = data.training.id;
      training.status = "训练已加入队列，可以离开页面，完成后会发邮件通知。";
      await loadRuns();
      schedulePoll();
    } catch (error) {
      training.status = error.message;
    } finally {
      training.busy = false;
    }
  }

  async function cancelTraining(run) {
    if (!window.confirm(`取消训练「${run.name}」？已训练的进度不会保留。`)) return;
    try {
      await apiFetch(`/api/trainings/${run.id}/cancel`, { method: "POST" });
      await loadRuns();
    } catch (error) {
      training.status = error.message;
    }
  }

  async function deleteTraining(run) {
    if (!window.confirm(`删除训练「${run.name}」及其权重文件？已注册到项目的模型会保留。`)) return;
    try {
      await apiFetch(`/api/trainings/${run.id}`, { method: "DELETE" });
      for (const url of Object.values(training.images[run.id] || {})) URL.revokeObjectURL(url);
      await loadRuns();
    } catch (error) {
      training.status = error.message;
    }
  }

  async function downloadTrainingArtifact(run, name) {
    try {
      await downloadAuthenticated(`/api/trainings/${run.id}/artifacts/${name}`, `${run.name}-${name}`.replace(/[\\/:*?"<>|\s]+/g, "_"));
    } catch (error) {
      training.status = error.message;
    }
  }

  return {
    training,
    canTrain,
    canDeleteTraining,
    trainingWeights,
    trainingSplitTotal,
    refreshTraining,
    stopTrainingPoll,
    scheduleReadinessCheck,
    openTraining,
    startTraining,
    cancelTraining,
    deleteTraining,
    downloadTrainingArtifact
  };
}
