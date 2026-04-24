<template>
  <div class="stat-card">
    <div class="stat-header">
      <span class="stat-label">{{ label }}</span>
      <component :is="icon" :size="18" class="stat-icon" />
    </div>
    <div class="stat-value">{{ value }}</div>
    <div v-if="sub" class="stat-sub">{{ sub }}</div>
  </div>
</template>

<script setup lang="ts">
import type { Component } from 'vue'

defineProps<{
  label: string
  value: string | number
  icon: Component
  sub?: string
}>()
</script>

<style scoped>
.stat-card {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 20px;
}

.stat-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}

.stat-label {
  font-size: 12px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--text-muted);
}

.stat-icon { color: var(--text-muted); }

.stat-value {
  font-size: 28px;
  font-weight: 700;
  color: var(--text-primary);
  line-height: 1;
}

.stat-sub {
  font-size: 12px;
  color: var(--text-muted);
  margin-top: 6px;
}
</style>
