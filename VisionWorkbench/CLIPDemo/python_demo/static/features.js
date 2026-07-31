const state = {
  caches: [],
  samples: [],
  selectedIds: new Set(),
  hoverIndex: null,
};

const els = {
  cacheSelect: document.getElementById("featureCacheSelect"),
  sampleList: document.getElementById("sampleList"),
  sampleCount: document.getElementById("sampleCount"),
  status: document.getElementById("featureStatus"),
  dimPill: document.getElementById("featureDimPill"),
  canvas: document.getElementById("featureCanvas"),
  chartTitle: document.getElementById("chartTitle"),
  chartRange: document.getElementById("chartRange"),
  hoverInfo: document.getElementById("hoverInfo"),
  selectAll: document.getElementById("selectAllButton"),
  clearAll: document.getElementById("clearAllButton"),
  normalize: document.getElementById("normalizeToggle"),
  mean: document.getElementById("meanToggle"),
  toast: document.getElementById("toast"),
};

const ctx = els.canvas.getContext("2d");

function imageUrl(path) {
  return `/api/image?path=${encodeURIComponent(path)}`;
}

function toast(message) {
  els.toast.textContent = message;
  els.toast.classList.add("show");
  window.clearTimeout(toast.timer);
  toast.timer = window.setTimeout(() => els.toast.classList.remove("show"), 3200);
}

async function getJson(url) {
  const response = await fetch(url);
  const data = await response.json();
  if (!response.ok || !data.ok) {
    throw new Error(data.error || `Request failed: ${response.status}`);
  }
  return data;
}

async function init() {
  try {
    const status = await getJson("/api/status");
    state.caches = status.caches || [];
    renderCacheOptions();
    if (state.caches.length > 0) {
      await loadCache(state.caches[0].path);
    }
  } catch (error) {
    toast(error.message);
  }
}

function renderCacheOptions() {
  els.cacheSelect.innerHTML = "";
  for (const cache of state.caches) {
    const option = document.createElement("option");
    option.value = cache.path;
    option.textContent = `${cache.productId} | OK ${cache.items} | NG ${cache.ngItems || 0}`;
    els.cacheSelect.appendChild(option);
  }
}

async function loadCache(cachePath) {
  const data = await getJson(`/api/cache-features?cachePath=${encodeURIComponent(cachePath)}`);
  state.samples = data.samples || [];
  state.selectedIds = new Set(state.samples.map((sample) => sample.id));
  els.status.textContent = `${data.productId} | ${data.cachePath}`;
  els.dimPill.textContent = `${data.featureDim} dim`;
  els.chartTitle.textContent = `${data.productId} Feature[0..${data.featureDim - 1}]`;
  renderSamples();
  drawChart();
}

function renderSamples() {
  els.sampleCount.textContent = `${state.samples.length} samples`;
  els.sampleList.innerHTML = "";
  for (const sample of state.samples) {
    const row = document.createElement("label");
    row.className = "sample-row";
    row.innerHTML = `
      <input type="checkbox" ${state.selectedIds.has(sample.id) ? "checked" : ""} data-id="${sample.id}" />
      <img class="sample-thumb" src="${imageUrl(sample.imagePath)}" alt="${sample.label} ${sample.index}" />
      <div class="sample-name">
        <strong>${sample.label} #${sample.index}</strong>
        <span title="${sample.imagePath}">${sample.fileName}</span>
      </div>
    `;
    els.sampleList.appendChild(row);
  }
}

function selectedSamples() {
  return state.samples.filter((sample) => state.selectedIds.has(sample.id));
}

function colorFor(sample, alpha = 1) {
  const hue = sample.label === "OK" ? 176 : 0;
  const offset = (sample.index * 37) % 28;
  return `hsla(${hue + offset}, 66%, ${sample.label === "OK" ? 34 : 43}%, ${alpha})`;
}

function resizeCanvas() {
  const rect = els.canvas.getBoundingClientRect();
  const scale = window.devicePixelRatio || 1;
  els.canvas.width = Math.max(600, Math.floor(rect.width * scale));
  els.canvas.height = Math.max(360, Math.floor(rect.height * scale));
  ctx.setTransform(scale, 0, 0, scale, 0, 0);
  return rect;
}

function featureBounds(samples) {
  let values = samples.flatMap((sample) => sample.feature);
  if (els.normalize.checked && values.length > 0) {
    values = samples.flatMap((sample) => normalizeFeature(sample.feature));
  }
  const min = Math.min(...values, -0.01);
  const max = Math.max(...values, 0.01);
  return { min, max };
}

function normalizeFeature(feature) {
  const mean = feature.reduce((sum, value) => sum + value, 0) / feature.length;
  const variance = feature.reduce((sum, value) => sum + (value - mean) ** 2, 0) / feature.length;
  const std = Math.sqrt(variance) || 1;
  return feature.map((value) => (value - mean) / std);
}

function mappedFeature(feature, bounds) {
  if (!els.normalize.checked) return feature;
  return normalizeFeature(feature);
}

function drawAxes(rect, padding, bounds, dim) {
  ctx.strokeStyle = "#d8dee6";
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(padding.left, padding.top);
  ctx.lineTo(padding.left, rect.height - padding.bottom);
  ctx.lineTo(rect.width - padding.right, rect.height - padding.bottom);
  ctx.stroke();

  ctx.fillStyle = "#627184";
  ctx.font = "12px Segoe UI";
  ctx.fillText(bounds.max.toFixed(3), 10, padding.top + 4);
  ctx.fillText(bounds.min.toFixed(3), 10, rect.height - padding.bottom);
  ctx.fillText("0", padding.left - 18, yFor(0, rect, padding, bounds));
  ctx.fillText(`dim ${dim - 1}`, rect.width - padding.right - 48, rect.height - 12);

  const zeroY = yFor(0, rect, padding, bounds);
  ctx.strokeStyle = "#edf0f3";
  ctx.beginPath();
  ctx.moveTo(padding.left, zeroY);
  ctx.lineTo(rect.width - padding.right, zeroY);
  ctx.stroke();
}

function xFor(index, length, rect, padding) {
  return padding.left + (index / Math.max(1, length - 1)) * (rect.width - padding.left - padding.right);
}

function yFor(value, rect, padding, bounds) {
  const t = (value - bounds.min) / Math.max(1e-9, bounds.max - bounds.min);
  return rect.height - padding.bottom - t * (rect.height - padding.top - padding.bottom);
}

function drawLine(feature, rect, padding, bounds, color, width = 1.4) {
  const values = mappedFeature(feature, bounds);
  ctx.strokeStyle = color;
  ctx.lineWidth = width;
  ctx.beginPath();
  values.forEach((value, index) => {
    const x = xFor(index, values.length, rect, padding);
    const y = yFor(value, rect, padding, bounds);
    if (index === 0) ctx.moveTo(x, y);
    else ctx.lineTo(x, y);
  });
  ctx.stroke();
}

function meanFeature(samples) {
  if (samples.length === 0) return null;
  const dim = samples[0].feature.length;
  const mean = new Array(dim).fill(0);
  for (const sample of samples) {
    sample.feature.forEach((value, index) => {
      mean[index] += value;
    });
  }
  return mean.map((value) => value / samples.length);
}

function drawHover(rect, padding, bounds, dim) {
  if (state.hoverIndex == null) return;
  const x = xFor(state.hoverIndex, dim, rect, padding);
  ctx.strokeStyle = "rgba(23,32,42,0.28)";
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(x, padding.top);
  ctx.lineTo(x, rect.height - padding.bottom);
  ctx.stroke();

  const selected = selectedSamples();
  const values = selected.map((sample) => sample.feature[state.hoverIndex]);
  const min = Math.min(...values);
  const max = Math.max(...values);
  els.hoverInfo.textContent = `dim ${state.hoverIndex}: selected ${selected.length}, raw value range ${min.toFixed(4)} .. ${max.toFixed(4)}`;
}

function drawChart() {
  const selected = selectedSamples();
  const rect = resizeCanvas();
  ctx.clearRect(0, 0, rect.width, rect.height);
  ctx.fillStyle = "#ffffff";
  ctx.fillRect(0, 0, rect.width, rect.height);

  if (selected.length === 0) {
    els.chartRange.textContent = "no selection";
    els.hoverInfo.textContent = "选择左侧样本后显示曲线";
    return;
  }

  const padding = { left: 54, right: 20, top: 22, bottom: 38 };
  const dim = selected[0].feature.length;
  const bounds = featureBounds(selected);
  els.chartRange.textContent = `${bounds.min.toFixed(4)} .. ${bounds.max.toFixed(4)}`;
  drawAxes(rect, padding, bounds, dim);

  for (const sample of selected) {
    drawLine(sample.feature, rect, padding, bounds, colorFor(sample, 0.58));
  }

  if (els.mean.checked) {
    const okMean = meanFeature(selected.filter((sample) => sample.label === "OK"));
    const ngMean = meanFeature(selected.filter((sample) => sample.label === "NG"));
    if (okMean) drawLine(okMean, rect, padding, bounds, "rgba(17,95,91,1)", 3.2);
    if (ngMean) drawLine(ngMean, rect, padding, bounds, "rgba(183,53,53,1)", 3.2);
  }

  drawHover(rect, padding, bounds, dim);
}

els.cacheSelect.addEventListener("change", () => {
  loadCache(els.cacheSelect.value).catch((error) => toast(error.message));
});

els.sampleList.addEventListener("change", (event) => {
  const checkbox = event.target;
  if (!(checkbox instanceof HTMLInputElement)) return;
  const id = checkbox.dataset.id;
  if (!id) return;
  if (checkbox.checked) state.selectedIds.add(id);
  else state.selectedIds.delete(id);
  drawChart();
});

els.selectAll.addEventListener("click", () => {
  state.selectedIds = new Set(state.samples.map((sample) => sample.id));
  renderSamples();
  drawChart();
});

els.clearAll.addEventListener("click", () => {
  state.selectedIds.clear();
  renderSamples();
  drawChart();
});

els.normalize.addEventListener("change", drawChart);
els.mean.addEventListener("change", drawChart);
window.addEventListener("resize", drawChart);

els.canvas.addEventListener("mousemove", (event) => {
  const selected = selectedSamples();
  if (selected.length === 0) return;
  const rect = els.canvas.getBoundingClientRect();
  const padding = { left: 54, right: 20, top: 22, bottom: 38 };
  const x = event.clientX - rect.left;
  const dim = selected[0].feature.length;
  const plotWidth = rect.width - padding.left - padding.right;
  const index = Math.round(((x - padding.left) / plotWidth) * (dim - 1));
  state.hoverIndex = Math.max(0, Math.min(dim - 1, index));
  drawChart();
});

els.canvas.addEventListener("mouseleave", () => {
  state.hoverIndex = null;
  els.hoverInfo.textContent = "移动到曲线上查看维度和值";
  drawChart();
});

init();
