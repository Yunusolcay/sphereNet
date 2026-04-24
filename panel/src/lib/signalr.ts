import * as signalR from '@microsoft/signalr'
import { useAuthStore } from '@/stores/auth'
import type { ServerStats } from './api'

export interface LogEntry {
  timestamp: string
  level: string
  message: string
  source: string
}

let connection: signalR.HubConnection | null = null

export function getConnection(): signalR.HubConnection {
  if (connection) return connection

  const auth = useAuthStore()

  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/server', {
      accessTokenFactory: () => auth.token ?? '',
    })
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build()

  return connection
}

export async function startConnection(): Promise<signalR.HubConnection> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Disconnected) {
    await conn.start()
  }
  return conn
}

export async function stopConnection() {
  if (connection) {
    await connection.stop()
    connection = null
  }
}

export function onLog(handler: (entry: LogEntry) => void) {
  getConnection().on('ReceiveLog', handler)
}

export function onLogBatch(handler: (entries: LogEntry[]) => void) {
  getConnection().on('ReceiveLogBatch', handler)
}

export function onStatsUpdate(handler: (stats: ServerStats) => void) {
  getConnection().on('StatsUpdate', handler)
}

export async function executeCommand(command: string): Promise<string[]> {
  const conn = getConnection()
  return conn.invoke<string[]>('ExecuteCommand', command)
}
