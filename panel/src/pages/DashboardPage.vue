<template>
  <div>
    <div class="stats-grid">
      <StatCard label="Online Players" :value="stats?.onlinePlayers ?? '—'" :icon="Users"
        :sub="stats ? `${stats.accounts} accounts total` : undefined" />
      <StatCard label="Characters"     :value="stats?.totalChars ?? '—'"    :icon="UserRound" />
      <StatCard label="Items"          :value="fmt(stats?.totalItems)"       :icon="Package" />
      <StatCard label="Memory"         :value="stats ? `${stats.memoryMB} MB` : '—'" :icon="MemoryStick" />
      <StatCard label="CPU"            :value="stats ? `${stats.cpuPercent} %` : '—'" :icon="Cpu" />
      <StatCard label="Uptime"         :value="stats?.uptime ?? '—'"         :icon="Clock" />
      <StatCard label="Tick Count"     :value="fmt(stats?.tickCount)"        :icon="Activity"
        :sub="stats ? `${stats.totalSectors} sectors` : undefined" />
      <StatCard label="Threads"        :value="fmt(stats?.threadCount)"     :icon="Layers" />
    </div>

    <div v-if="stats" class="server-info-card">
      <h2 class="section-title">Server Info</h2>
      <div class="info-grid">
        <div class="info-row">
          <span class="info-key">Server Name</span>
          <span class="info-val">{{ stats.serverName }}</span>
        </div>
        <div class="info-row">
          <span class="info-key">Uptime (seconds)</span>
          <span class="info-val">{{ stats.uptimeSeconds.toLocaleString() }}</span>
        </div>
        <div class="info-row">
          <span class="info-key">Online / Accounts</span>
          <span class="info-val">{{ stats.onlinePlayers }} / {{ stats.accounts }}</span>
        </div>
        <div class="info-row">
          <span class="info-key">Memory</span>
          <span class="info-val">{{ stats.memoryMB }} MB</span>
        </div>
      </div>
    </div>

    <div v-else class="no-data">
      <Activity :size="32" class="no-data-icon" />
      <p>Waiting for server stats…</p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { Users, UserRound, Package, MemoryStick, Clock, Activity, Cpu, Layers } from 'lucide-vue-next'
import StatCard from '@/components/StatCard.vue'
import { useServerStore } from '@/stores/server'

const server = useServerStore()
const stats  = computed(() => server.stats)

function fmt(n: number | undefined): string {
  return n === undefined ? '—' : n.toLocaleString()
}
</script>

<style scoped>
.stats-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 16px;
  margin-bottom: 24px;
}

.server-info-card {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 20px;
}

.section-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.05em;
  margin: 0 0 16px;
}

.info-grid { display: flex; flex-direction: column; gap: 12px; }

.info-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--border);
}

.info-row:last-child { border-bottom: none; padding-bottom: 0; }

.info-key {
  font-size: 13px;
  color: var(--text-muted);
}

.info-val {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.no-data {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  padding: 64px 0;
  color: var(--text-muted);
}

.no-data-icon { opacity: 0.4; }

.no-data p { margin: 0; font-size: 14px; }
</style>
