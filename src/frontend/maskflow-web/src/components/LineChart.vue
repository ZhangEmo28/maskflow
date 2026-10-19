<script setup>
import { computed } from "vue";

const props = defineProps({
  title: { type: String, default: "" },
  series: { type: Array, default: () => [] },
  // Metrics live in [0, 1]; losses get an axis fitted to their range.
  unitRange: { type: Boolean, default: false }
});

const palette = ["#1f63ff", "#f59e0b", "#16a34a", "#dc2626", "#7c3aed", "#0891b2"];
const width = 560;
const height = 220;
const pad = { left: 44, right: 12, top: 12, bottom: 26 };

const chart = computed(() => {
  const points = props.series.flatMap((item) => item.points);
  if (!points.length) return null;
  const xs = points.map(([x]) => x);
  const ys = points.map(([, y]) => y);
  const minX = Math.min(...xs);
  const maxX = Math.max(...xs, minX + 1);
  let minY = props.unitRange ? 0 : Math.min(...ys);
  let maxY = props.unitRange ? Math.max(1e-6, Math.min(1, Math.max(...ys) * 1.1)) : Math.max(...ys);
  if (maxY - minY < 1e-9) {
    minY -= 0.5;
    maxY += 0.5;
  }
  const sx = (x) => pad.left + ((x - minX) / (maxX - minX)) * (width - pad.left - pad.right);
  const sy = (y) => height - pad.bottom - ((y - minY) / (maxY - minY)) * (height - pad.top - pad.bottom);
  const ticks = Array.from({ length: 5 }, (_, i) => minY + ((maxY - minY) * i) / 4);
  const xTicks = [...new Set([minX, Math.round((minX + maxX) / 2), maxX])];
  const lines = props.series.map((item, index) => ({
    name: item.name,
    color: palette[index % palette.length],
    // Validation curves are dashed so train/val pairs read as one family.
    dashed: item.name.startsWith("val/"),
    d: item.points.map(([x, y], i) => `${i ? "L" : "M"}${sx(x).toFixed(1)},${sy(y).toFixed(1)}`).join(" "),
    last: item.points[item.points.length - 1]
  }));
  return { lines, ticks: ticks.map((value) => ({ value, y: sy(value) })), xTicks: xTicks.map((value) => ({ value, x: sx(value) })) };
});

function formatTick(value) {
  const abs = Math.abs(value);
  return abs >= 100 ? value.toFixed(0) : abs >= 1 ? value.toFixed(2) : value.toFixed(3);
}
</script>

<template>
  <figure class="line-chart">
    <figcaption v-if="title">{{ title }}</figcaption>
    <svg v-if="chart" :viewBox="`0 0 ${width} ${height}`" role="img" :aria-label="title">
      <g class="line-chart-grid">
        <g v-for="tick in chart.ticks" :key="`y${tick.value}`">
          <line :x1="pad.left" :x2="width - pad.right" :y1="tick.y" :y2="tick.y" />
          <text :x="pad.left - 6" :y="tick.y + 4" text-anchor="end">{{ formatTick(tick.value) }}</text>
        </g>
        <text v-for="tick in chart.xTicks" :key="`x${tick.value}`" :x="tick.x" :y="height - 8" text-anchor="middle">{{ tick.value }}</text>
      </g>
      <path v-for="line in chart.lines" :key="line.name" :d="line.d" :stroke="line.color" :stroke-dasharray="line.dashed ? '5 4' : null" fill="none" stroke-width="2" stroke-linejoin="round" />
    </svg>
    <p v-else class="line-chart-empty">第一轮结束后显示曲线</p>
    <ul v-if="chart" class="line-chart-legend">
      <li v-for="line in chart.lines" :key="line.name">
        <i :style="{ borderColor: line.color, borderStyle: line.dashed ? 'dashed' : 'solid' }"></i>{{ line.name }}
        <b>{{ formatTick(line.last[1]) }}</b>
      </li>
    </ul>
  </figure>
</template>
