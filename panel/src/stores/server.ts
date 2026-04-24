import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { ServerStats, PlayerInfo } from '@/lib/api'

export const useServerStore = defineStore('server', () => {
  const stats = ref<ServerStats | null>(null)
  const players = ref<PlayerInfo[]>([])
  const connected = ref(false)

  function updateStats(s: ServerStats) {
    stats.value = s
  }

  function updatePlayers(p: PlayerInfo[]) {
    players.value = p
  }

  function setConnected(v: boolean) {
    connected.value = v
  }

  return { stats, players, connected, updateStats, updatePlayers, setConnected }
})
