const $ = (id) => document.getElementById(id);

const fields = {
  statusLine: $("statusLine"),
  devicePill: $("devicePill"),
  cacheCount: $("cacheCount"),
  productId: $("productId"),
  okDir: $("okDir"),
  ngDir: $("ngDir"),
  buildTopK: $("buildTopK"),
  buildThreshold: $("buildThreshold"),
  okTextPrompts: $("okTextPrompts"),
  ngTextPrompts: $("ngTextPrompts"),
  textWeight: $("textWeight"),
  cachePath: $("cachePath"),
  cacheSelect: $("cacheSelect"),
  imagePath: $("imagePath"),
  imageFileInput: $("imageFileInput"),
  okFolderInput: $("okFolderInput"),
  ngFolderInput: $("ngFolderInput"),
  pickImageButton: $("pickImageButton"),
  pickOkFolderButton: $("pickOkFolderButton"),
  pickNgFolderButton: $("pickNgFolderButton"),
  detectTopK: $("detectTopK"),
  detectThreshold: $("detectThreshold"),
  buildButton: $("buildButton"),
  detectButton: $("detectButton"),
  queryPreview: $("queryPreview"),
  resultBadge: $("resultBadge"),
  scoreValue: $("scoreValue"),
  ngScoreValue: $("ngScoreValue"),
  marginValue: $("marginValue"),
  thresholdValue: $("thresholdValue"),
  inferenceTimeValue: $("inferenceTimeValue"),
  matchTimeValue: $("matchTimeValue"),
  productMeta: $("productMeta"),
  featureMeta: $("featureMeta"),
  ngFeatureMeta: $("ngFeatureMeta"),
  imageScoreMeta: $("imageScoreMeta"),
  textScoreMeta: $("textScoreMeta"),
  totalTimeMeta: $("totalTimeMeta"),
  topKHint: $("topKHint"),
  topKList: $("topKList"),
  topNgKHint: $("topNgKHint"),
  topNgKList: $("topNgKList"),
  toast: $("toast"),
};

function imageUrl(path) {
  return `/api/image?path=${encodeURIComponent(path)}`;
}

function toast(message) {
  fields.toast.textContent = message;
  fields.toast.classList.add("show");
  window.clearTimeout(toast.timer);
  toast.timer = window.setTimeout(() => fields.toast.classList.remove("show"), 3600);
}

async function postJson(url, payload) {
  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
  const data = await response.json();
  if (!response.ok || !data.ok) {
    throw new Error(data.error || `Request failed: ${response.status}`);
  }
  return data;
}

async function postForm(url, formData) {
  const response = await fetch(url, {
    method: "POST",
    body: formData,
  });
  const data = await response.json();
  if (!response.ok || !data.ok) {
    throw new Error(data.error || `Request failed: ${response.status}`);
  }
  return data;
}

function setBusy(button, busy, label) {
  button.disabled = busy;
  button.textContent = busy ? "处理中..." : label;
}

function applyImageRatio(image, target = image.parentElement) {
  if (!target || !image.naturalWidth || !image.naturalHeight) return;
  const ratio = image.naturalWidth / image.naturalHeight;
  const boundedRatio = Math.min(2.6, Math.max(0.65, ratio));
  target.style.setProperty("--image-ratio", String(boundedRatio));
}

function setAdaptiveImage(image, src, target = image.parentElement) {
  if (target) {
    target.style.removeProperty("--image-ratio");
  }
  image.onload = () => applyImageRatio(image, target);
  image.src = src;
  if (image.complete) {
    applyImageRatio(image, target);
  }
}

function renderCaches(caches) {
  fields.cacheSelect.innerHTML = "";
  fields.cacheCount.textContent = `${caches.length} caches`;

  for (const cache of caches) {
    const option = document.createElement("option");
    option.value = cache.path;
    option.textContent = cache.error
      ? `${cache.name} - 无法读取`
      : `${cache.productId} | OK ${cache.items} | NG ${cache.ngItems || 0} | Text ${cache.okTextItems || 0}/${cache.ngTextItems || 0} | threshold ${cache.threshold}`;
    fields.cacheSelect.appendChild(option);
  }

  if (caches.length > 0) {
    fields.cacheSelect.value = caches[0].path;
    fields.cachePath.value = fields.cachePath.value || caches[0].path;
  }
}

async function refreshStatus() {
  const response = await fetch("/api/status");
  const data = await response.json();
  fields.statusLine.textContent = `${data.root} | Torch ${data.torch}`;
  fields.devicePill.textContent = `${data.device.toUpperCase()} · ${data.deviceName}`;
  renderCaches(data.caches || []);
}

async function buildCache() {
  setBusy(fields.buildButton, true, "建立 / 重建 Cache");
  try {
    const data = await postJson("/api/build-cache", {
      productId: fields.productId.value,
      okDir: fields.okDir.value,
      ngDir: fields.ngDir.value,
      cachePath: fields.cachePath.value,
      topK: Number(fields.buildTopK.value || 3),
      threshold: Number(fields.buildThreshold.value || 0.82),
      okTextPrompts: parsePrompts(fields.okTextPrompts.value),
      ngTextPrompts: parsePrompts(fields.ngTextPrompts.value),
      textWeight: Number(fields.textWeight.value || 0.2),
    });
    fields.cachePath.value = data.cachePath;
    renderCaches(data.caches || []);
    fields.cacheSelect.value = data.cachePath;
    toast(`Cache 已生成：OK ${data.items} 张，NG ${data.ngItems || 0} 张，文本 ${data.okTextItems || 0}/${data.ngTextItems || 0}`);
  } catch (error) {
    toast(error.message);
  } finally {
    setBusy(fields.buildButton, false, "建立 / 重建 Cache");
  }
}

async function uploadQueryImage(file) {
  if (!file) return;
  const formData = new FormData();
  formData.append("image", file);
  fields.pickImageButton.disabled = true;
  fields.pickImageButton.textContent = "上传中...";
  try {
    const data = await postForm("/api/upload-image", formData);
    fields.imagePath.value = data.imagePath;
    setAdaptiveImage(fields.queryPreview, imageUrl(data.imagePath));
    toast(`已选择图片：${data.fileName}`);
  } catch (error) {
    toast(error.message);
  } finally {
    fields.pickImageButton.disabled = false;
    fields.pickImageButton.textContent = "选择图片";
    fields.imageFileInput.value = "";
  }
}

async function uploadOkFolder(files) {
  return uploadSampleFolder(files, "ok");
}

async function uploadNgFolder(files) {
  return uploadSampleFolder(files, "ng");
}

async function uploadSampleFolder(files, label) {
  if (!files || files.length === 0) return;
  const formData = new FormData();
  formData.append("productId", fields.productId.value || "part_A");
  for (const file of files) {
    formData.append("images", file, file.webkitRelativePath || file.name);
  }
  const button = label === "ok" ? fields.pickOkFolderButton : fields.pickNgFolderButton;
  button.disabled = true;
  button.textContent = "上传中...";
  try {
    const data = await postForm(`/api/upload-${label}-folder`, formData);
    if (label === "ok") {
      fields.okDir.value = data.okDir;
    } else {
      fields.ngDir.value = data.ngDir;
    }
    toast(`已选择 ${label.toUpperCase()} 文件夹：${data.count} 张图片`);
  } catch (error) {
    toast(error.message);
  } finally {
    button.disabled = false;
    button.textContent = "选择文件夹";
    if (label === "ok") {
      fields.okFolderInput.value = "";
    } else {
      fields.ngFolderInput.value = "";
    }
  }
}

function renderResult(data) {
  setAdaptiveImage(fields.queryPreview, imageUrl(data.imagePath));
  fields.resultBadge.textContent = data.result;
  fields.resultBadge.className = `result-badge ${data.result.toLowerCase()}`;
  fields.scoreValue.textContent = formatScore(data.okScore ?? data.score);
  fields.ngScoreValue.textContent = formatScore(data.ngScore);
  fields.marginValue.textContent = formatScore(data.margin);
  fields.thresholdValue.textContent = data.threshold.toFixed(4);
  fields.inferenceTimeValue.textContent = formatMs(data.timing?.inferenceMs);
  fields.matchTimeValue.textContent = formatMs(data.timing?.matchMs);
  fields.productMeta.textContent = `Product: ${data.productId}`;
  fields.featureMeta.textContent = `OK Cache: ${data.cacheItems} images · ${data.featureDim} dim`;
  fields.ngFeatureMeta.textContent = `NG Cache: ${data.ngCacheItems || 0} images`;
  fields.imageScoreMeta.textContent = `Image branch: OK ${formatScore(data.imageOkScore)} · NG ${formatScore(data.imageNgScore)} · margin ${formatScore(data.imageMargin)}`;
  fields.textScoreMeta.textContent = `Text branch: OK ${formatScore(data.textOkScore)} · NG ${formatScore(data.textNgScore)} · margin ${formatScore(data.textMargin)} · weight ${data.textWeight ?? 0}`;
  fields.totalTimeMeta.textContent = `Total: ${formatMs(data.timing?.totalMs)}`;
  fields.topKHint.textContent = `${data.topK.length} samples`;
  fields.topKList.innerHTML = "";
  fields.topNgKList.innerHTML = "";

  renderTopKList(fields.topKList, data.topK);
  if (data.topNgK && data.topNgK.length > 0) {
    fields.topNgKHint.textContent = `${data.topNgK.length} samples`;
    renderTopKList(fields.topNgKList, data.topNgK);
  } else {
    fields.topNgKHint.textContent = "未启用 NG cache";
  }
}

function renderTopKList(list, items) {
  for (const item of items) {
    const card = document.createElement("article");
    card.className = "topk-card";
    card.innerHTML = `
      <div class="topk-image-wrap">
        <img alt="top ${item.rank}" />
      </div>
      <div class="topk-info">
        <strong>#${item.rank} · ${item.similarity.toFixed(4)}</strong>
        <span title="${item.imagePath}">${item.imagePath}</span>
      </div>
    `;
    list.appendChild(card);
    const image = card.querySelector("img");
    const imageWrap = card.querySelector(".topk-image-wrap");
    setAdaptiveImage(image, imageUrl(item.imagePath), imageWrap);
  }
}

function parsePrompts(value) {
  return value
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);
}

function formatScore(value) {
  if (value === undefined || value === null || Number.isNaN(Number(value))) {
    return "--";
  }
  return Number(value).toFixed(4);
}

function formatMs(value) {
  if (value === undefined || value === null || Number.isNaN(Number(value))) {
    return "--";
  }
  const ms = Number(value);
  if (ms < 10) return `${ms.toFixed(2)} ms`;
  if (ms < 100) return `${ms.toFixed(1)} ms`;
  return `${Math.round(ms)} ms`;
}

async function detectImage() {
  const cachePath = fields.cacheSelect.value || fields.cachePath.value;
  setBusy(fields.detectButton, true, "检测图片");
  try {
    const data = await postJson("/api/detect", {
      cachePath,
      imagePath: fields.imagePath.value,
      topK: fields.detectTopK.value,
      threshold: fields.detectThreshold.value,
    });
    renderResult(data);
    toast(`检测完成：${data.result}，score ${data.score.toFixed(4)}`);
  } catch (error) {
    toast(error.message);
  } finally {
    setBusy(fields.detectButton, false, "检测图片");
  }
}

fields.buildButton.addEventListener("click", buildCache);
fields.detectButton.addEventListener("click", detectImage);
fields.pickImageButton.addEventListener("click", () => fields.imageFileInput.click());
fields.pickOkFolderButton.addEventListener("click", () => fields.okFolderInput.click());
fields.pickNgFolderButton.addEventListener("click", () => fields.ngFolderInput.click());
fields.imageFileInput.addEventListener("change", () => uploadQueryImage(fields.imageFileInput.files[0]));
fields.okFolderInput.addEventListener("change", () => uploadOkFolder(fields.okFolderInput.files));
fields.ngFolderInput.addEventListener("change", () => uploadNgFolder(fields.ngFolderInput.files));
fields.cacheSelect.addEventListener("change", () => {
  fields.cachePath.value = fields.cacheSelect.value;
});
fields.imagePath.addEventListener("change", () => {
  setAdaptiveImage(fields.queryPreview, imageUrl(fields.imagePath.value));
});

refreshStatus().then(() => {
  setAdaptiveImage(fields.queryPreview, imageUrl(fields.imagePath.value));
});
