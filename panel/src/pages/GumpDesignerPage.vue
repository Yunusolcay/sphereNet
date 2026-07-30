<template>
  <div class="gump-page">
    <!-- Sol: araçlar + dialog tarayıcı -->
    <section class="toolbox">
      <h2>Dialog Tasarımcısı</h2>

      <h3>Sunucudan Yükle</h3>
      <select v-model="pickedDialog" class="full">
        <option value="">— dialog seç —</option>
        <option v-for="n in dialogNames" :key="n" :value="n">{{ n }}</option>
      </select>
      <button class="tool-btn" :disabled="!pickedDialog" @click="loadFromServer">Script'i Yükle</button>

      <h3>Kontrol Ekle</h3>
      <button v-for="tool in tools" :key="tool.type" class="tool-btn" @click="addControl(tool.type)">
        {{ tool.label }}
      </button>
      <button class="tool-btn danger" @click="removeSelected" :disabled="selectedIndex < 0">Seçileni Sil</button>

      <h3>Görünüm</h3>
      <label class="row"><input type="checkbox" v-model="showOutlines" /> kontrol çerçeveleri</label>
      <label class="row">Sayfa:
        <select v-model="activePage">
          <option value="all">tümü</option>
          <option v-for="p in pages" :key="p" :value="String(p)">{{ p }}</option>
        </select>
      </label>
    </section>

    <!-- Orta: canlı sahne -->
    <section class="canvas-panel">
      <div class="canvas-header">
        <input v-model="dialogName" class="name-input" placeholder="d_panel_test" />
        <span>{{ visibleControls.length }} kontrol · sürükleyerek taşı</span>
      </div>
      <div class="stage" @mousedown.self="selectedIndex = -1">
        <component
          :is="'div'"
          v-for="c in visibleControls"
          :key="c.id"
          class="ctl"
          :class="{ selected: controls.indexOf(c) === selectedIndex, outlined: showOutlines }"
          :style="ctlStyle(c)"
          :title="ctlTitle(c)"
          @mousedown.stop.prevent="startDrag(c, $event)"
        >
          <!-- resizepic: 9 parça -->
          <template v-if="c.type === 'resizepic'">
            <div v-for="(cell, ci) in nineSlice(c)" :key="ci" class="slice"
                 :style="cell.style"></div>
          </template>
          <img v-else-if="isImage(c)" :src="gumpUrl(imageId(c))" class="art" draggable="false"
               @error="onArtError($event, c)" />
          <div v-else-if="c.type === 'gumppictiled'" class="tiled"
               :style="{ backgroundImage: 'url(' + gumpUrl(c.gumpId) + ')' }"></div>
          <div v-else-if="c.type === 'text' || c.type === 'croppedtext'" class="txt"
               :style="{ color: hueColor(c.hue) }">{{ c.text }}</div>
          <div v-else-if="c.type === 'htmlgump'" class="html"
               :class="{ paper: c.background }" v-text="c.text"></div>
          <div v-else-if="c.type === 'textentry'" class="entry">{{ c.text }}</div>
          <div v-else-if="c.type === 'checkertrans'" class="trans"></div>
          <div v-else-if="c.type === 'tilepic'" class="ph">tile 0x{{ c.gumpId.toString(16) }}</div>
        </component>
      </div>
    </section>

    <!-- Sağ: özellikler + script -->
    <section class="props">
      <h3>Özellikler</h3>
      <template v-if="selected">
        <label>Tip <span class="ro">{{ selected.type }}</span></label>
        <label>X <input v-model.number="selected.x" type="number" /></label>
        <label>Y <input v-model.number="selected.y" type="number" /></label>
        <template v-if="hasSize(selected)">
          <label>Genişlik <input v-model.number="selected.width" type="number" /></label>
          <label>Yükseklik <input v-model.number="selected.height" type="number" /></label>
        </template>
        <label v-if="usesGumpId(selected)">Gump ID
          <input v-model.number="selected.gumpId" type="number" /></label>
        <label v-if="selected.type === 'button'">Basılı ID
          <input v-model.number="selected.pressedId" type="number" /></label>
        <label v-if="selected.type === 'button'">Buton ID
          <input v-model.number="selected.buttonId" type="number" /></label>
        <label v-if="selected.type === 'button'">Sayfa
          <input v-model.number="selected.targetPage" type="number" /></label>
        <label v-if="hasText(selected)">Hue
          <input v-model.number="selected.hue" type="number" /></label>
        <label v-if="selected.type === 'htmlgump'" class="row">
          <input type="checkbox" v-model="selected.background" /> arka plan</label>
        <label v-if="hasText(selected)" class="col">Metin
          <textarea v-model="selected.text" rows="3" /></label>
        <label>Önizleme sayfası <input v-model.number="selected.page" type="number" /></label>
      </template>
      <p v-else class="hint">Sahneden bir kontrol seç.</p>

      <div class="script-head">
        <h3>Script</h3>
        <button class="mini" @click="importFromScript">İçe Aktar ⤵</button>
        <button class="mini" @click="copyExport">Kopyala</button>
      </div>
      <textarea class="export" v-model="scriptText" spellcheck="false" />
      <p class="hint">Dışa aktarım kontrollerden üretilir; "İçe Aktar" kutudaki scripti sahneye çizer
        (&lt;ifade&gt; içeren satırlar olduğu gibi korunur). {{ copyMsg }}</p>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/lib/api'

type ControlType =
  | 'resizepic' | 'gumppic' | 'gumppictiled' | 'button' | 'checkbox' | 'radio'
  | 'text' | 'croppedtext' | 'htmlgump' | 'textentry' | 'checkertrans' | 'tilepic'

interface Ctl {
  id: number
  type: ControlType
  x: number; y: number
  width: number; height: number
  hue: number
  gumpId: number
  pressedId: number
  buttonId: number
  targetPage: number
  background: boolean
  text: string
  page: number
  cornerW: number; cornerH: number
}

const tools: { type: ControlType; label: string }[] = [
  { type: 'resizepic',  label: 'ResizePic (arka plan)' },
  { type: 'gumppic',    label: 'GumpPic (görsel)' },
  { type: 'gumppictiled', label: 'GumpPicTiled' },
  { type: 'button',     label: 'Button' },
  { type: 'checkbox',   label: 'CheckBox' },
  { type: 'radio',      label: 'Radio' },
  { type: 'text',       label: 'DText' },
  { type: 'croppedtext',label: 'DCroppedText' },
  { type: 'htmlgump',   label: 'DHtmlGump' },
  { type: 'textentry',  label: 'DTextEntry' },
  { type: 'checkertrans', label: 'CheckerTrans' },
]

const dialogName = ref('d_panel_gump')
const controls = ref<Ctl[]>([])
const selectedIndex = ref(-1)
const copyMsg = ref('')
const dialogNames = ref<string[]>([])
const pickedDialog = ref('')
const activePage = ref('all')
const showOutlines = ref(true)
const scriptText = ref('')
// <ifade> içeren / çözümlenemeyen satırlar — dışa aktarımda aynen korunur
const passthroughLines = ref<string[]>([])
let nextId = 1
let suppressExport = false

const selected = computed(() => (selectedIndex.value >= 0 ? controls.value[selectedIndex.value] : null))
const pages = computed(() => [...new Set(controls.value.map(c => c.page))].sort((a, b) => a - b))
const visibleControls = computed(() =>
  controls.value.filter(c => activePage.value === 'all' || c.page === 0 || c.page === Number(activePage.value)))

onMounted(() => {
  api.get<string[]>('/dialogs').then(r => { dialogNames.value = r.data }).catch(() => {})
  controls.value = [make('resizepic', 0, 0, { width: 420, height: 300, gumpId: 2600 }),
                    make('text', 40, 30, { text: 'Yeni dialog', hue: 90 })]
  regenerateScript()
})

function make(type: ControlType, x = 40, y = 40, extra: Partial<Ctl> = {}): Ctl {
  return {
    id: nextId++, type, x, y,
    width: type === 'resizepic' ? 240 : type === 'htmlgump' ? 200 : 120,
    height: type === 'resizepic' ? 160 : type === 'htmlgump' ? 90 : 24,
    hue: 0,
    gumpId: type === 'button' ? 4005 : type === 'checkbox' ? 210 : type === 'radio' ? 208 : 2600,
    pressedId: 4007, buttonId: 1, targetPage: 0,
    background: true, text: type === 'text' ? 'Metin' : '',
    page: 0, cornerW: 0, cornerH: 0,
    ...extra,
  }
}

function addControl(type: ControlType) {
  controls.value.push(make(type, 40 + controls.value.length * 10, 40 + controls.value.length * 10))
  selectedIndex.value = controls.value.length - 1
}
function removeSelected() {
  if (selectedIndex.value < 0) return
  controls.value.splice(selectedIndex.value, 1)
  selectedIndex.value = -1
}

// --- görünüm yardımcıları ---------------------------------------------------
function gumpUrl(id: number) { return '/gumpart/' + id + '.png' }
function isImage(c: Ctl) { return c.type === 'gumppic' || c.type === 'button' || c.type === 'checkbox' || c.type === 'radio' }
function imageId(c: Ctl) { return c.gumpId }
function usesGumpId(c: Ctl) { return isImage(c) || c.type === 'resizepic' || c.type === 'gumppictiled' || c.type === 'tilepic' }
function hasSize(c: Ctl) {
  return ['resizepic', 'htmlgump', 'croppedtext', 'textentry', 'checkertrans', 'gumppictiled'].includes(c.type)
}
function hasText(c: Ctl) { return ['text', 'croppedtext', 'htmlgump', 'textentry'].includes(c.type) }
function ctlTitle(c: Ctl) {
  return c.type === 'button' ? `button id=${c.buttonId} → sayfa ${c.targetPage}` : c.type
}
function ctlStyle(c: Ctl) {
  const sized = hasSize(c)
  return {
    left: c.x + 'px', top: c.y + 'px',
    width: sized ? c.width + 'px' : undefined,
    height: sized ? c.height + 'px' : undefined,
    zIndex: String(10 + controls.value.indexOf(c)),
  }
}
function hueColor(h: number) {
  if (!h) return '#e8e8e8'
  return `hsl(${(h * 137) % 360},55%,72%)`
}
function onArtError(ev: Event, c: Ctl) {
  const img = ev.target as HTMLImageElement
  img.style.display = 'none'
  const parent = img.parentElement
  if (parent && !parent.querySelector('.ph')) {
    const ph = document.createElement('div')
    ph.className = 'ph'
    ph.textContent = 'gump ' + c.gumpId
    parent.appendChild(ph)
  }
}

// resizepic 9-parça hücreleri (id..id+8, köşe boyutu ilk parçadan ölçülür)
const cornerSizes = new Map<number, { w: number; h: number }>()
function nineSlice(c: Ctl) {
  let cs = cornerSizes.get(c.gumpId)
  if (!cs) {
    cs = { w: 16, h: 16 }
    cornerSizes.set(c.gumpId, cs)
    const probe = new Image()
    probe.onload = () => {
      cornerSizes.set(c.gumpId, { w: probe.naturalWidth, h: probe.naturalHeight })
      controls.value = [...controls.value] // yeniden çiz
    }
    probe.src = gumpUrl(c.gumpId)
  }
  const cw = cs.w, ch = cs.h
  const mw = Math.max(0, c.width - cw * 2), mh = Math.max(0, c.height - ch * 2)
  const cell = (x: number, y: number, w: number, h: number, id: number) => ({
    style: {
      left: x + 'px', top: y + 'px', width: w + 'px', height: h + 'px',
      backgroundImage: `url(${gumpUrl(id)})`,
    },
  })
  return [
    cell(0, 0, cw, ch, c.gumpId),            cell(cw, 0, mw, ch, c.gumpId + 1),       cell(cw + mw, 0, cw, ch, c.gumpId + 2),
    cell(0, ch, cw, mh, c.gumpId + 3),       cell(cw, ch, mw, mh, c.gumpId + 4),      cell(cw + mw, ch, cw, mh, c.gumpId + 5),
    cell(0, ch + mh, cw, ch, c.gumpId + 6),  cell(cw, ch + mh, mw, ch, c.gumpId + 7), cell(cw + mw, ch + mh, cw, ch, c.gumpId + 8),
  ].filter(s => parseInt(s.style.width) > 0 && parseInt(s.style.height) > 0)
}

// --- sürükleme ---------------------------------------------------------------
function startDrag(c: Ctl, ev: MouseEvent) {
  selectedIndex.value = controls.value.indexOf(c)
  const startX = ev.clientX, startY = ev.clientY
  const origX = c.x, origY = c.y
  const move = (e: MouseEvent) => {
    c.x = Math.max(0, origX + e.clientX - startX)
    c.y = Math.max(0, origY + e.clientY - startY)
  }
  const up = () => {
    window.removeEventListener('mousemove', move)
    window.removeEventListener('mouseup', up)
    regenerateScript()
  }
  window.addEventListener('mousemove', move)
  window.addEventListener('mouseup', up)
}

// --- script üretimi ----------------------------------------------------------
watch(controls, () => regenerateScript(), { deep: true })

function regenerateScript() {
  if (suppressExport) return
  const name = dialogName.value.trim().replace(/[^a-zA-Z0-9_]/g, '_') || 'd_panel_gump'
  const lines = [`[DIALOG ${name}]`, '0,0']
  let curPage = -1
  const ordered = [...controls.value].sort((a, b) => a.page - b.page)
  for (const c of ordered) {
    if (c.page !== curPage) { curPage = c.page; if (curPage > 0) lines.push(`PAGE ${curPage}`) }
    switch (c.type) {
      case 'resizepic':    lines.push(`RESIZEPIC ${c.x} ${c.y} ${c.gumpId} ${c.width} ${c.height}`); break
      case 'gumppic':      lines.push(`GUMPPIC ${c.x} ${c.y} ${c.gumpId}`); break
      case 'gumppictiled': lines.push(`GUMPPICTILED ${c.x} ${c.y} ${c.width} ${c.height} ${c.gumpId}`); break
      case 'button':       lines.push(`BUTTON ${c.x} ${c.y} ${c.gumpId} ${c.pressedId} 1 ${c.targetPage} ${c.buttonId}`); break
      case 'checkbox':     lines.push(`CHECKBOX ${c.x} ${c.y} ${c.gumpId} ${c.gumpId + 1} 0 ${c.buttonId}`); break
      case 'radio':        lines.push(`RADIO ${c.x} ${c.y} ${c.gumpId} ${c.gumpId + 1} 0 ${c.buttonId}`); break
      case 'text':         lines.push(`DTEXT ${c.x} ${c.y} ${c.hue} ${c.text}`); break
      case 'croppedtext':  lines.push(`DCROPPEDTEXT ${c.x} ${c.y} ${c.width} ${c.height} ${c.hue} ${c.text}`); break
      case 'htmlgump':     lines.push(`DHTMLGUMP ${c.x} ${c.y} ${c.width} ${c.height} ${c.background ? 1 : 0} 1 ${c.text}`); break
      case 'textentry':    lines.push(`DTEXTENTRY ${c.x} ${c.y} ${c.width} ${c.height} ${c.hue} 0 ${c.text}`); break
      case 'checkertrans': lines.push(`CHECKERTRANS ${c.x} ${c.y} ${c.width} ${c.height}`); break
      case 'tilepic':      lines.push(`TILEPIC ${c.x} ${c.y} ${c.gumpId}`); break
    }
  }
  if (passthroughLines.value.length) {
    lines.push('// --- içe aktarımda korunan satırlar (ifade/akış) ---')
    lines.push(...passthroughLines.value)
  }
  lines.push('', `[DIALOG ${name} BUTTON]`, 'ON=1', 'RETURN 1')
  scriptText.value = lines.join('\n')
}

// --- script içe aktarımı -----------------------------------------------------
function loadFromServer() {
  if (!pickedDialog.value) return
  api.get('/dialog-source', { params: { name: pickedDialog.value }, responseType: 'text' })
    .then(r => {
      scriptText.value = typeof r.data === 'string' ? r.data : String(r.data)
      dialogName.value = pickedDialog.value
      importFromScript()
    })
    .catch(() => { copyMsg.value = 'kaynak okunamadı' })
}

function importFromScript() {
  const lines = scriptText.value.split(/\r?\n/)
  const out: Ctl[] = []
  const kept: string[] = []
  let curPage = 0
  let originX = 0, originY = 0
  let rowX = 0, rowY = 0
  let inLayout = false

  const coord = (tok: string, axis: 'x' | 'y'): number | null => {
    if (tok.includes('<')) return null
    let row = axis === 'x' ? rowX : rowY
    let v: number
    if (tok.startsWith('+')) v = row + (parseInt(tok.slice(1)) || 0)
    else if (tok.startsWith('*')) { row += parseInt(tok.slice(1)) || 0; v = row }
    else if (tok.startsWith('-')) v = row - (parseInt(tok.slice(1)) || 0)
    else { row = parseInt(tok); v = row }
    if (isNaN(v)) return null
    if (axis === 'x') rowX = row; else rowY = row
    return v + (axis === 'x' ? originX : originY)
  }

  for (const raw of lines) {
    const line = raw.trim()
    if (!line || line.startsWith('//')) continue
    if (line.startsWith('[')) {
      const up = line.toUpperCase()
      inLayout = up.startsWith('[DIALOG') && !up.includes(' BUTTON') && !up.includes(' TEXT]')
      continue
    }
    if (!inLayout) continue
    const sp = line.search(/[ \t=]/)
    const verb = (sp < 0 ? line : line.slice(0, sp)).toUpperCase()
    const arg = sp < 0 ? '' : line.slice(sp + 1).trim()
    const t = arg.split(/[ \t]+/)

    if (verb === 'PAGE') { curPage = parseInt(arg) || 0; continue }
    if (verb === 'DORIGIN') {
      const ox = parseInt(t[0]), oy = parseInt(t[1])
      if (!isNaN(ox)) originX = ox
      if (!isNaN(oy)) originY = oy
      rowX = 0; rowY = 0
      continue
    }
    if (/^\d+,\d+$/.test(line)) continue // "0,0" konum satırı

    const X = coord(t[0] ?? '0', 'x'), Y = coord(t[1] ?? '0', 'y')
    const num = (i: number) => { const n = parseInt(t[i]); return isNaN(n) ? null : n }
    const rest = (i: number) => t.slice(i).join(' ')
    const push = (c: Partial<Ctl> & { type: ControlType }) => {
      out.push({ ...make(c.type, 0, 0), ...c, id: nextId++, page: curPage } as Ctl)
    }

    let handled = true
    if (X === null || Y === null) handled = false
    else switch (verb) {
      case 'RESIZE':       push({ type: 'resizepic', x: X, y: Y, gumpId: 9200, width: num(2) ?? 100, height: num(3) ?? 100 }); break
      case 'RESIZEPIC':    push({ type: 'resizepic', x: X, y: Y, gumpId: num(2) ?? 2600, width: num(3) ?? 100, height: num(4) ?? 100 }); break
      case 'GUMPPIC': case 'GUMPIC': push({ type: 'gumppic', x: X, y: Y, gumpId: num(2) ?? 0 }); break
      case 'GUMPPICTILED': push({ type: 'gumppictiled', x: X, y: Y, width: num(2) ?? 50, height: num(3) ?? 50, gumpId: num(4) ?? 0 }); break
      case 'BUTTON':       push({ type: 'button', x: X, y: Y, gumpId: num(2) ?? 4005, pressedId: num(3) ?? 4007, targetPage: num(5) ?? 0, buttonId: num(6) ?? 0 }); break
      case 'CHECKBOX':     push({ type: 'checkbox', x: X, y: Y, gumpId: num(2) ?? 210, buttonId: num(5) ?? 0 }); break
      case 'RADIO':        push({ type: 'radio', x: X, y: Y, gumpId: num(2) ?? 208, buttonId: num(5) ?? 0 }); break
      case 'DTEXT': case 'TEXT': push({ type: 'text', x: X, y: Y, hue: num(2) ?? 0, text: rest(3) }); break
      case 'DCROPPEDTEXT': case 'CROPPEDTEXT':
        push({ type: 'croppedtext', x: X, y: Y, width: num(2) ?? 100, height: num(3) ?? 20, hue: num(4) ?? 0, text: rest(5) }); break
      case 'DHTMLGUMP': case 'HTMLGUMP':
        push({ type: 'htmlgump', x: X, y: Y, width: num(2) ?? 100, height: num(3) ?? 60, background: (num(4) ?? 0) !== 0, text: rest(6) }); break
      case 'XMFHTMLGUMP': case 'XMFHTMLGUMPCOLOR':
        push({ type: 'htmlgump', x: X, y: Y, width: num(2) ?? 100, height: num(3) ?? 60, background: false, text: '[cliloc ' + (t[4] ?? '') + ']' }); break
      case 'DTEXTENTRY': case 'TEXTENTRY': case 'DTEXTENTRYLIMITED': case 'TEXTENTRYLIMITED':
        push({ type: 'textentry', x: X, y: Y, width: num(2) ?? 120, height: num(3) ?? 22, hue: num(4) ?? 0, text: rest(7) }); break
      case 'CHECKERTRANS': push({ type: 'checkertrans', x: X, y: Y, width: num(2) ?? 100, height: num(3) ?? 100 }); break
      case 'TILEPIC': case 'TILEPICHUE': push({ type: 'tilepic', x: X, y: Y, gumpId: num(2) ?? 0 }); break
      case 'NOMOVE': case 'NOCLOSE': case 'NODISPOSE': case 'GROUP': case 'TOOLTIP': break
      default: handled = false
    }
    if (!handled) kept.push(raw)
  }

  suppressExport = true
  controls.value = out
  passthroughLines.value = kept
  selectedIndex.value = -1
  activePage.value = 'all'
  suppressExport = false
  copyMsg.value = `${out.length} kontrol içe aktarıldı${kept.length ? `, ${kept.length} satır korunarak geçildi` : ''}`
}

async function copyExport() {
  await navigator.clipboard.writeText(scriptText.value)
  copyMsg.value = 'panoya kopyalandı'
  window.setTimeout(() => (copyMsg.value = ''), 2000)
}
</script>

<style scoped>
.gump-page {
  display: grid;
  grid-template-columns: 230px 1fr 380px;
  gap: 14px;
  height: 100%;
  min-height: 0;
}
.toolbox, .canvas-panel, .props {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 14px;
  min-height: 0;
  overflow: auto;
}
h2, h3 { margin: 12px 0 8px; color: var(--text-primary); font-size: 14px; }
h2 { margin-top: 0; font-size: 16px; }
.hint { color: var(--text-muted); font-size: 12px; }
.full { width: 100%; }
.row { display: flex; align-items: center; gap: 6px; color: var(--text-muted); font-size: 12px; margin: 4px 0; }

.tool-btn {
  width: 100%; margin: 3px 0; padding: 7px 10px; text-align: left;
  border: 1px solid var(--border); border-radius: 6px;
  background: var(--bg-primary); color: var(--text-primary); cursor: pointer;
}
.tool-btn:hover { background: var(--bg-tertiary); }
.tool-btn.danger { color: var(--danger); }
.tool-btn:disabled { opacity: .5; cursor: not-allowed; }

.canvas-panel { display: flex; flex-direction: column; }
.canvas-header { display: flex; justify-content: space-between; align-items: center;
  margin-bottom: 10px; color: var(--text-muted); font-size: 12px; gap: 10px; }
.name-input { background: var(--bg-primary); color: var(--text-primary);
  border: 1px solid var(--border); border-radius: 6px; padding: 7px 9px; }

.stage {
  position: relative; flex: 1; min-height: 480px; border-radius: 8px;
  background: repeating-conic-gradient(#1d2027 0% 25%, #232733 0% 50%) 0 0/22px 22px;
  border: 1px solid var(--border);
  overflow: auto;
}
.ctl { position: absolute; cursor: move; user-select: none; }
.ctl.outlined { outline: 1px dashed rgba(110,170,255,.35); }
.ctl.selected { outline: 2px solid var(--accent); z-index: 999 !important; }
.art { display: block; image-rendering: pixelated; pointer-events: none; }
.slice { position: absolute; background-repeat: repeat; image-rendering: pixelated; }
.tiled { width: 100%; height: 100%; background-repeat: repeat; image-rendering: pixelated; }
.txt { white-space: nowrap; font: 13px/1.2 'Palatino Linotype', serif; text-shadow: 1px 1px 1px #000; }
.html { width: 100%; height: 100%; overflow: hidden; font: 12px/1.3 'Palatino Linotype', serif; color: #ddd; }
.html.paper { background: #ece5cf; color: #222; border: 2px ridge #b9a; }
.entry { width: 100%; height: 100%; background: #fff; color: #000;
  border: 1px inset #999; font: 12px Consolas, monospace; overflow: hidden; white-space: nowrap; }
.trans { width: 100%; height: 100%; background: rgba(0,0,0,.45); }
.ph { background: rgba(180,60,60,.4); border: 1px dashed #d66; color: #fbb;
  font-size: 10px; padding: 2px 4px; min-width: 36px; min-height: 14px; }

.props { display: flex; flex-direction: column; gap: 6px; }
label { display: grid; grid-template-columns: 92px 1fr; align-items: center; gap: 8px;
  color: var(--text-muted); font-size: 12px; }
label.col { grid-template-columns: 1fr; }
.ro { color: var(--text-primary); }
input, textarea, select {
  background: var(--bg-primary); color: var(--text-primary);
  border: 1px solid var(--border); border-radius: 6px; padding: 6px 8px; font-size: 12px;
}
.script-head { display: flex; align-items: center; gap: 8px; margin-top: 10px; }
.script-head h3 { margin: 0; flex: 1; }
.mini { padding: 4px 10px; border: 1px solid var(--border); border-radius: 6px;
  background: var(--bg-primary); color: var(--text-primary); cursor: pointer; font-size: 12px; }
.mini:hover { background: var(--bg-tertiary); }
.export { flex: 1; min-height: 200px; font-family: Consolas, Monaco, monospace;
  font-size: 12px; resize: none; white-space: pre; }
</style>
