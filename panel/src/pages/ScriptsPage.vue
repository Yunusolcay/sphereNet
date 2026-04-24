<template>
  <div class="scripts-layout">
    <!-- Left: file tree -->
    <div class="file-panel">
      <div class="panel-header">
        <div class="header-row">
          <h2 class="panel-title">
            <i class="bi bi-journal-code" />
            Scripts
          </h2>
          <button class="btn-icon" @click="refresh" title="Refresh">
            <i class="bi bi-arrow-clockwise" :class="{ spin: loading }" />
          </button>
        </div>

        <div class="search-row">
          <i class="bi bi-search search-icon" />
          <input v-model="search" placeholder="Filter files…" class="search-input" />
        </div>
      </div>

      <div v-if="loading" class="empty">Loading…</div>

      <div v-else-if="treeNodes.length === 0" class="empty">
        <span v-if="files.length === 0">No .scp files found</span>
        <span v-else>No match for "{{ search }}"</span>
      </div>

      <div v-else class="file-list">
        <template v-for="node in treeNodes" :key="node.path">
          <!-- Folder -->
          <button
            v-if="node.type === 'folder'"
            class="tree-item folder"
            :style="{ paddingLeft: (node.depth * 16 + 10) + 'px' }"
            @click="toggleFolder(node.path)"
          >
            <i :class="expanded.has(node.path) ? 'bi bi-folder2-open' : 'bi bi-folder2'" class="tree-icon folder-icon" />
            <span class="tree-name">{{ node.name }}</span>
            <span class="tree-meta">{{ node.childCount }}</span>
          </button>

          <!-- File -->
          <button
            v-else
            class="tree-item file"
            :class="{ active: selected?.relativePath === node.file!.relativePath }"
            :style="{ paddingLeft: (node.depth * 16 + 10) + 'px' }"
            @click="openFile(node.file!)"
          >
            <i class="bi bi-file-earmark-text tree-icon" />
            <span class="tree-name">{{ node.name }}</span>
            <span class="tree-meta">{{ fmtSize(node.file!.sizeBytes) }}</span>
          </button>
        </template>
      </div>

      <!-- Download section -->
      <div class="download-section">
        <div class="download-info">
          <span>Download &amp; Install Scripts</span>
          <a
            href="https://github.com/UOSoftware/Scripts-T"
            target="_blank"
            rel="noopener"
            class="gh-link"
          >
            <i class="bi bi-github" />
            UOSoftware/Scripts-T
            <i class="bi bi-box-arrow-up-right" />
          </a>
        </div>
        <button
          class="btn-download"
          @click="downloadScripts"
          :disabled="downloading"
        >
          <i class="bi bi-cloud-download" />
          <span>{{ downloading ? 'Downloading…' : 'Install' }}</span>
        </button>
        <p v-if="downloadMsg" class="download-msg" :class="{ error: downloadError }">
          {{ downloadMsg }}
        </p>
      </div>
    </div>

    <!-- Right: content viewer -->
    <div class="content-panel">
      <div v-if="!selected" class="no-selection">
        <i class="bi bi-file-earmark-text" style="font-size: 36px; opacity: 0.2" />
        <p>Select a file to view its contents</p>
      </div>

      <template v-else>
        <div class="content-header">
          <span class="content-path">
            <i class="bi bi-file-earmark-text" />
            {{ selected.relativePath }}
          </span>
          <div class="content-actions">
            <span class="content-meta">{{ fmtSize(selected.sizeBytes) }}</span>
          </div>
        </div>

        <div v-if="contentLoading" class="no-selection">Loading…</div>

        <pre v-else class="content-body">{{ content }}</pre>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { scriptsApi } from '@/lib/api'
import type { ScriptFileInfo } from '@/lib/api'

interface TreeNode {
  type: 'folder' | 'file'
  name: string
  path: string
  depth: number
  file?: ScriptFileInfo
  childCount?: number
}

const files         = ref<ScriptFileInfo[]>([])
const loading       = ref(true)
const search        = ref('')
const selected      = ref<ScriptFileInfo | null>(null)
const content       = ref('')
const contentLoading = ref(false)
const downloading   = ref(false)
const downloadMsg   = ref('')
const downloadError = ref(false)
const expanded      = ref(new Set<string>())

const treeNodes = computed<TreeNode[]>(() => {
  const q = search.value.toLowerCase()
  const filtered = q
    ? files.value.filter(f => f.relativePath.toLowerCase().includes(q))
    : files.value

  const folderCounts = new Map<string, number>()
  for (const f of filtered) {
    const parts = f.relativePath.split('/')
    for (let i = 1; i < parts.length; i++) {
      const dir = parts.slice(0, i).join('/')
      folderCounts.set(dir, (folderCounts.get(dir) ?? 0) + 1)
    }
  }

  const nodes: TreeNode[] = []
  const seenFolders = new Set<string>()

  for (const f of filtered) {
    const parts = f.relativePath.split('/')

    for (let i = 1; i < parts.length; i++) {
      const dir = parts.slice(0, i).join('/')
      if (seenFolders.has(dir)) continue
      seenFolders.add(dir)

      const parentDir = parts.slice(0, i - 1).join('/')
      if (i > 1 && !expanded.value.has(parentDir)) continue

      nodes.push({
        type: 'folder',
        name: parts[i - 1],
        path: dir,
        depth: i - 1,
        childCount: folderCounts.get(dir) ?? 0,
      })
    }

    const parentDir = parts.length > 1 ? parts.slice(0, -1).join('/') : ''
    if (parentDir && !expanded.value.has(parentDir)) continue

    nodes.push({
      type: 'file',
      name: parts[parts.length - 1],
      path: f.relativePath,
      depth: parts.length - 1,
      file: f,
    })
  }

  return nodes
})

onMounted(refresh)

async function refresh() {
  loading.value = true
  try {
    const { data } = await scriptsApi.list()
    files.value = data
    // Auto-expand top-level folders
    const topFolders = new Set<string>()
    for (const f of data) {
      const first = f.relativePath.split('/')[0]
      if (f.relativePath.includes('/')) topFolders.add(first)
    }
    topFolders.forEach(d => expanded.value.add(d))
  } catch {
    files.value = []
  } finally {
    loading.value = false
  }
}

function toggleFolder(path: string) {
  if (expanded.value.has(path)) {
    // Collapse this folder and all children
    for (const p of [...expanded.value]) {
      if (p === path || p.startsWith(path + '/')) expanded.value.delete(p)
    }
  } else {
    expanded.value.add(path)
  }
}

async function openFile(f: ScriptFileInfo) {
  selected.value = f
  content.value = ''
  contentLoading.value = true
  try {
    const { data } = await scriptsApi.content(f.relativePath)
    content.value = data.content
  } catch {
    content.value = '(error loading file)'
  } finally {
    contentLoading.value = false
  }
}

async function downloadScripts() {
  downloading.value = true
  downloadMsg.value  = ''
  downloadError.value = false
  try {
    const { data } = await scriptsApi.download()
    downloadMsg.value = `Installed ${data.filesInstalled} files from UOSoftware/Scripts-T`
    await refresh()
  } catch (e: unknown) {
    const msg = (e as { response?: { data?: { detail?: string } } })?.response?.data?.detail
    downloadMsg.value  = msg ?? 'Download failed'
    downloadError.value = true
  } finally {
    downloading.value = false
  }
}

function fmtSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}
</script>

<style scoped>
.scripts-layout {
  display: flex;
  gap: 16px;
  height: 100%;
  min-height: 0;
}

/* File panel */
.file-panel {
  width: 300px;
  flex-shrink: 0;
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.panel-header {
  padding: 12px 14px;
  border-bottom: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.header-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.panel-title {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
  margin: 0;
}

.btn-icon {
  background: transparent;
  border: none;
  color: var(--text-muted);
  cursor: pointer;
  padding: 4px;
  border-radius: 4px;
  display: flex;
  align-items: center;
  font-size: 14px;
}

.btn-icon:hover { color: var(--text-primary); background: var(--bg-tertiary); }

.search-row {
  display: flex;
  align-items: center;
  gap: 6px;
  background: var(--bg-primary);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 5px 8px;
}

.search-icon { color: var(--text-muted); flex-shrink: 0; font-size: 12px; }

.search-input {
  background: transparent;
  border: none;
  outline: none;
  color: var(--text-primary);
  font-size: 12px;
  width: 100%;
}

.spin { animation: spin 1s linear infinite; display: inline-block; }
@keyframes spin { to { transform: rotate(360deg); } }

.empty {
  padding: 24px;
  text-align: center;
  font-size: 12px;
  color: var(--text-muted);
}

/* Tree */
.file-list {
  flex: 1;
  overflow-y: auto;
  padding: 4px 0;
}

.tree-item {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 5px 10px;
  border: none;
  background: transparent;
  text-align: left;
  cursor: pointer;
  transition: background 0.1s;
  color: var(--text-primary);
  font-size: 12px;
}

.tree-item:hover { background: var(--bg-tertiary); }
.tree-item.active { background: rgba(88, 166, 255, 0.1); }

.tree-icon { flex-shrink: 0; font-size: 13px; }
.folder-icon { color: var(--accent); }

.tree-name {
  flex: 1;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.folder .tree-name { font-weight: 500; }

.tree-meta {
  font-size: 10px;
  color: var(--text-muted);
  white-space: nowrap;
  flex-shrink: 0;
}

/* Download section */
.download-section {
  padding: 12px 14px;
  border-top: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.download-info {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.download-info > span {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.gh-link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11px;
  color: var(--accent);
  text-decoration: none;
}

.gh-link:hover { text-decoration: underline; }

.btn-download {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 7px 12px;
  background: var(--accent);
  border: none;
  border-radius: 6px;
  color: #fff;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: opacity 0.15s;
}

.btn-download:disabled { opacity: 0.5; cursor: not-allowed; }
.btn-download:not(:disabled):hover { opacity: 0.85; }

.download-msg {
  font-size: 11px;
  color: var(--success, #3fb950);
  margin: 0;
}

.download-msg.error { color: var(--danger); }

/* Content panel */
.content-panel {
  flex: 1;
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  min-width: 0;
}

.no-selection {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  color: var(--text-muted);
  font-size: 13px;
}

.content-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 16px;
  border-bottom: 1px solid var(--border);
  background: var(--bg-primary);
}

.content-path {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  font-family: monospace;
}

.content-actions {
  display: flex;
  align-items: center;
  gap: 10px;
}

.content-meta { font-size: 11px; color: var(--text-muted); }

.content-body {
  flex: 1;
  overflow: auto;
  padding: 14px 16px;
  margin: 0;
  font-size: 12px;
  line-height: 1.6;
  color: var(--text-primary);
  font-family: 'Consolas', 'Monaco', monospace;
  white-space: pre;
  tab-size: 4;
}
</style>
