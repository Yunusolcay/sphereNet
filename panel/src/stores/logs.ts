import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { LogEntry } from '@/lib/signalr'

const MAX_LOGS = 2000

export const useLogsStore = defineStore('logs', () => {
  const entries = ref<LogEntry[]>([])
  const paused  = ref(false)

  function addEntry(entry: LogEntry) {
    if (paused.value) return
    entries.value.push(entry)
    if (entries.value.length > MAX_LOGS) {
      entries.value.splice(0, entries.value.length - MAX_LOGS)
    }
  }

  function addBatch(batch: LogEntry[]) {
    if (paused.value || batch.length === 0) return
    entries.value.push(...batch)
    if (entries.value.length > MAX_LOGS) {
      entries.value.splice(0, entries.value.length - MAX_LOGS)
    }
  }

  function clear() {
    entries.value = []
  }

  return { entries, paused, addEntry, addBatch, clear }
})
