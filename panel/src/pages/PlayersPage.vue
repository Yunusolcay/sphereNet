<template>
  <div>
    <div class="toolbar">
      <span class="count">{{ players.length }} online</span>
      <button class="btn-ghost" @click="refetch()">
        <RefreshCw :size="15" :class="{ spin: isFetching }" /> Refresh
      </button>
    </div>

    <div class="table-wrap">
      <table class="table" v-if="players.length > 0">
        <thead>
          <tr>
            <th>Character</th>
            <th>Account</th>
            <th>Map</th>
            <th>Position</th>
            <th>IP</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="p in players" :key="p.charName">
            <td class="bold">{{ p.charName }}</td>
            <td>{{ p.accountName }}</td>
            <td>{{ mapName(p.mapId) }}</td>
            <td class="mono">{{ p.x }}, {{ p.y }}</td>
            <td class="mono text-muted">{{ p.ip }}</td>
          </tr>
        </tbody>
      </table>

      <div v-else class="empty">
        <Users :size="32" class="empty-icon" />
        <p>No players online.</p>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { RefreshCw, Users } from 'lucide-vue-next'
import { useQuery } from '@tanstack/vue-query'
import { playersApi } from '@/lib/api'

const { data, isFetching, refetch } = useQuery({
  queryKey: ['players'],
  queryFn: () => playersApi.list().then(r => r.data),
  refetchInterval: 5000,
})

const players = computed(() => data.value ?? [])

function mapName(id: number): string {
  const names: Record<number, string> = { 0: 'Felucca', 1: 'Trammel', 2: 'Ilshenar', 3: 'Malas', 4: 'Tokuno', 5: 'TerMur' }
  return names[id] ?? `Map${id}`
}
</script>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 16px;
}

.count {
  font-size: 14px;
  font-weight: 600;
  color: var(--text-muted);
}

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

.spin { animation: spin 1s linear infinite; }

@keyframes spin { to { transform: rotate(360deg); } }

.table-wrap {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  overflow: hidden;
}

.table {
  width: 100%;
  border-collapse: collapse;
  font-size: 13px;
}

.table thead th {
  text-align: left;
  padding: 12px 16px;
  font-size: 11px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--text-muted);
  border-bottom: 1px solid var(--border);
}

.table tbody td {
  padding: 12px 16px;
  border-bottom: 1px solid var(--border);
  color: var(--text-primary);
}

.table tbody tr:last-child td { border-bottom: none; }
.table tbody tr:hover { background: rgba(255,255,255,0.02); }

.bold { font-weight: 600; }
.mono { font-family: 'Courier New', monospace; font-size: 12px; }
.text-muted { color: var(--text-muted); }

.empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  padding: 64px;
  color: var(--text-muted);
}

.empty-icon { opacity: 0.3; }
.empty p { margin: 0; font-size: 14px; }
</style>
