<template>
  <div class="logs-page">
    <div class="toolbar">
      <div class="filters">
        <button
          v-for="lvl in levels"
          :key="lvl.value"
          class="level-btn"
          :class="{ active: activeLevel === lvl.value }"
          @click="activeLevel = activeLevel === lvl.value ? '' : lvl.value"
        >
          {{ lvl.label }}
        </button>
      </div>
      <div class="toolbar-right">
        <input v-model="search" class="search-input" placeholder="Filter…" />
        <button class="btn-ghost" @click="logs.paused = !logs.paused">
          <component :is="logs.paused ? Play : Pause" :size="15" />
          {{ logs.paused ? 'Resume' : 'Pause' }}
        </button>
        <button class="btn-ghost danger" @click="logs.clear()">
          <Trash2 :size="15" /> Clear
        </button>
      </div>
    </div>

    <div ref="scrollEl" class="log-body">
      <div
        v-for="(entry, i) in filtered"
        :key="i"
        class="log-row"
        :class="levelClass(entry.level)"
      >
        <span class="log-time">{{ formatTime(entry.timestamp) }}</span>
        <span class="log-level" :class="levelClass(entry.level)">{{ entry.level.slice(0, 3).toUpperCase() }}</span>
        <span class="log-src">{{ entry.source }}</span>
        <span class="log-msg">{{ entry.message }}</span>
      </div>

      <div v-if="filtered.length === 0" class="empty">
        <Terminal :size="28" class="empty-icon" />
        <p>No log entries yet.</p>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, nextTick } from 'vue'
import { Pause, Play, Trash2, Terminal } from 'lucide-vue-next'
import { useLogsStore } from '@/stores/logs'

const logs      = useLogsStore()
const search    = ref('')
const activeLevel = ref('')
const scrollEl  = ref<HTMLElement | null>(null)

const levels = [
  { value: 'Verbose',     label: 'VRB' },
  { value: 'Debug',       label: 'DBG' },
  { value: 'Information', label: 'INF' },
  { value: 'Warning',     label: 'WRN' },
  { value: 'Error',       label: 'ERR' },
  { value: 'Fatal',       label: 'FTL' },
]

const filtered = computed(() => {
  let list = logs.entries
  if (activeLevel.value) list = list.filter(e => e.level === activeLevel.value)
  if (search.value) {
    const q = search.value.toLowerCase()
    list = list.filter(e =>
      e.message.toLowerCase().includes(q) || e.source.toLowerCase().includes(q)
    )
  }
  return list
})

// Auto-scroll to bottom when new entries arrive (unless paused)
watch(
  () => logs.entries.length,
  async () => {
    if (!logs.paused) {
      await nextTick()
      const el = scrollEl.value
      if (el) el.scrollTop = el.scrollHeight
    }
  }
)

function formatTime(ts: string): string {
  const d = new Date(ts)
  return d.toLocaleTimeString('en-GB', { hour12: false })
}

function levelClass(level: string): string {
  const map: Record<string, string> = {
    Verbose:     'lvl-verbose',
    Debug:       'lvl-debug',
    Information: 'lvl-info',
    Warning:     'lvl-warn',
    Error:       'lvl-error',
    Fatal:       'lvl-fatal',
  }
  return map[level] ?? ''
}
</script>

<style scoped>
.logs-page {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}

.filters { display: flex; gap: 6px; }

.level-btn {
  padding: 5px 10px;
  border-radius: 5px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text-muted);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  font-family: 'Courier New', monospace;
  transition: all 0.15s;
}

.level-btn:hover        { border-color: var(--accent); color: var(--accent); }
.level-btn.active       { background: var(--accent); color: #0d1117; border-color: var(--accent); }

.toolbar-right { display: flex; align-items: center; gap: 8px; }

.search-input {
  background: var(--bg-tertiary);
  border: 1px solid var(--border);
  border-radius: 6px;
  color: var(--text-primary);
  font-size: 13px;
  padding: 6px 10px;
  outline: none;
  width: 180px;
}

.search-input:focus { border-color: var(--accent); }

.btn-ghost {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text-muted);
  font-size: 13px;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.15s;
}

.btn-ghost:hover { background: var(--bg-tertiary); color: var(--text-primary); }
.btn-ghost.danger:hover { border-color: var(--danger); color: var(--danger); }

.log-body {
  flex: 1;
  overflow-y: auto;
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 8px 0;
  font-family: 'Courier New', Consolas, monospace;
  font-size: 12.5px;
  min-height: 0;
}

.log-row {
  display: flex;
  align-items: baseline;
  gap: 10px;
  padding: 2px 14px;
  line-height: 1.6;
}

.log-row:hover { background: rgba(255,255,255,0.03); }

.log-time { color: var(--text-muted); white-space: nowrap; flex-shrink: 0; }

.log-level {
  font-weight: 700;
  white-space: nowrap;
  flex-shrink: 0;
  width: 30px;
  text-align: center;
}

.log-src {
  color: var(--text-muted);
  white-space: nowrap;
  flex-shrink: 0;
  max-width: 180px;
  overflow: hidden;
  text-overflow: ellipsis;
  font-size: 11px;
}

.log-msg { color: var(--text-primary); word-break: break-all; }

.lvl-verbose .log-level { color: #6e7681; }
.lvl-debug   .log-level { color: var(--text-muted); }
.lvl-info    .log-level { color: var(--accent); }
.lvl-warn    .log-level { color: var(--warning); }
.lvl-warn    .log-msg   { color: var(--warning); }
.lvl-error   .log-level { color: var(--danger); }
.lvl-error   .log-msg   { color: var(--danger); }
.lvl-fatal   .log-level { color: #ff7b72; }
.lvl-fatal   .log-msg   { color: #ff7b72; font-weight: 700; }

.empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 48px;
  color: var(--text-muted);
}

.empty-icon { opacity: 0.3; }
.empty p { margin: 0; font-size: 13px; font-family: 'Inter', sans-serif; }
</style>
