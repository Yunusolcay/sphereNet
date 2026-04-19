# SphereNet — Sistem Kurallari

Bu belge, SphereNet'in mimari kurallarini ve delta-based update sisteminin isleyisini tanimlar. Yeni obje eklerken veya mevcut sistemleri degistirirken bu kurallara uyulmalidir.

---

## 1. DirtyFlag Sistemi

Her oyun nesnesi (`ObjBase`) bir `DirtyFlag` bitfield'i tasir. Property setter'lar degisiklikleri otomatik olarak isaretler.

### DirtyFlag Enum

```csharp
[Flags]
public enum DirtyFlag : uint
{
    None       = 0,
    Position   = 1,
    Body       = 2,
    Hue        = 4,
    Name       = 8,
    Stats      = 16,
    Direction  = 32,
    Equip      = 64,
    StatFlags  = 128,
    Amount     = 256,
    Container  = 512,
    Deleted    = 1024,
}
```

### MarkDirty Mekanizmasi

```
Property setter cagirilir
  → MarkDirty(DirtyFlag.Xxx)
    → _dirty |= flags
    → Eger _dirty onceden None idiyse (clean → dirty gecisi):
        → _dirtyNotify?.Invoke(this)   // GameWorld.NotifyDirty
```

- `ConsumeDirty()` — Bayraklari okur ve sifirlar. Tick sonunda goruntulenme guncelleme sirasinda cagirilir.
- `SetDirtyNotify(Action<ObjBase>? notify)` — GameWorld tarafindan obje olusturulurken (CreateItem/CreateCharacter) baglanir.

### Kural: Yeni Property Eklerken

Eger bir property istemciye gonderilen veriyi etkiliyorsa, setter'inda uygun `DirtyFlag` ile `MarkDirty()` cagirilmalidir:

```csharp
public ushort Hue
{
    get => _hue;
    set
    {
        if (_hue == value) return;
        _hue = value;
        MarkDirty(DirtyFlag.Hue);
    }
}
```

---

## 2. Global Dirty Set

`GameWorld` sinifi, tick icerisinde degisen tum nesneleri izler:

```
ConcurrentDictionary<uint, ObjBase> _dirtyObjects
```

### Akis

1. Obje olusturulurken `SetDirtyNotify(NotifyDirty)` baglanir
2. `MarkDirty()` clean→dirty gecisinde `NotifyDirty(obj)` cagirir
3. `NotifyDirty` → `_dirtyObjects.TryAdd(obj.Uid.Value, obj)`
4. Tick sonunda `DrainDirtyObjects()` → tum degerleri listeye kopyalar, dictionary'yi temizler

### Kural

- `DrainDirtyObjects()` tick basina tam olarak **bir kez** cagirilir.
- Drain sonrasi yapilan degisiklikler bir sonraki tick'te islenir.
- `ConcurrentDictionary` kullanildigi icin coklu thread'den guvenle erisilebilir.

---

## 3. Delta View Update

Oyuncu istemcilerine gonderilecek guncellemeleri hesaplar. Iki yol vardir:

### V1 — Full Scan (Legacy)

Her tick'te tum istemciler icin:
1. `BuildViewDelta()` — Cevredeki sektorleri tara, gorunur nesneleri topla
2. `ApplyViewDelta(delta)` — Onceki bilinen kume ile karsilastir, fark paketleri gonder

### V2 — Delta-Only (`UseDeltaView=true`, varsayilan)

Her tick'te:
1. Oyuncu hareket ettiyse → V1 gibi full scan (`BuildViewDelta` + `ApplyViewDelta`)
2. Oyuncu yerindeyse → `ApplyDirtyOnly(dirtyObjects)`:
   - Silinen/menzil disi bilinen nesneleri kaldir
   - Dirty listesindeki nesneler icin bayraklara gore paket gonder:
     - `Deleted` → `PacketDeleteObject`
     - `StatFlags` → Gizlilik/olum kontrolu, tam yeniden cizim veya silme
     - `Body | Hue | Equip` → Tam yeniden cizim (`SendDrawObject`)
     - `Position | Direction` → Hareket/yon paketi
     - `Stats` → Saglik cubugu guncelleme

### Multicore V2

Paralel modda iki asama:
- **Build** (paralel): `BuildViewDeltaV2()` — salt okunur, thread-safe
- **Apply** (seri): `ApplyViewDeltaV2(result, dirtyObjects)` — paket gonderimi

### Kural

- V2 modunda, bir nesnenin istemciye gorunur herhangi bir property'si degistiyse `MarkDirty()` cagirilmalidir. Aksi halde istemci degisikligi goremez.
- `ApplyDirtyOnly` her dirty flag kombinasyonu icin dogru paketi gondermeli; yeni bir flag eklendiginde bu metot da guncellenmelidir.

---

## 4. Timer Wheel

NPC yapay zeka tick'lerini zamanlayan 256 slotlu hashed timer wheel.

### Yapisal Ozellikler

| Parametre | Deger |
|-----------|-------|
| Slot sayisi | 256 |
| Slot suresi | 250 ms |
| Tam dongu | 64 saniye (256 x 250ms) |
| Zamanlama karmasikligi | O(1) |
| Ilerleme karmasikligi | O(slot boyutu) |
| Cift zamanlama korumasi | `HashSet<uint>` ile UID kontrolu |

### Zamanlama Algoritmasi

```
Schedule(npc, fireTimeMs):
  1. _scheduled icinde UID varsa → atla (cift zamanlama korumasi)
  2. slot = (fireTimeMs / 250) & 255
  3. _slots[slot].Add(npc)
  4. _scheduled.Add(npc.Uid.Value)
```

### Ilerleme Algoritmasi

```
Advance(nowMs):
  1. Mevcut slot'tan hedef slot'a kadar ilerle
  2. Her slot icin:
     - Silinen NPC'leri atla (lazy delete)
     - Canli NPC'leri sonuc listesine ekle
     - Slot'u temizle
  3. Sonuc listesini dondur
```

### Kural: Yeni NPC Eklerken

1. NPC olusturulurken `TimerWheel.Schedule(npc, now + initialDelay)` cagirilmali
2. AI tick'i sonrasi `TimerWheel.Schedule(npc, npc.NextNpcActionTime)` ile yeniden zamanlanmali
3. NPC silindiginde `TimerWheel.Remove(npc)` cagirilmali (scheduled set'ten cikarilir)

---

## 5. Multicore Pipeline

### Faz Yapisi

```
Tick Baslangici
  │
  ├─ Faz 0: Snapshot (paralel)
  │    └─ Sektor tick, NPC snapshot, timer wheel advance
  │
  ├─ Faz 1: Build (paralel, salt okunur)
  │    ├─ NPC karar hesabi → ConcurrentBag<NpcDecision>
  │    ├─ Buyu/istatistik tick'leri
  │    ├─ DrainDirtyObjects()
  │    └─ V2 goruntulenme delta hesabi → ConcurrentDictionary
  │
  ├─ Faz 2: Apply (seri, deterministik)
  │    ├─ NPC kararlari UID sirasina gore uygulanir
  │    └─ Istemci goruntulenme delta'lari uygulanir
  │
  └─ Faz 3: Flush (seri)
       └─ Curume, isik, telnet, web status
```

### Fallback Mekanizmasi

- Tum fazlar `CancellationTokenSource(MulticorePhaseTimeoutMs)` ile sarmalanir
- Timeout veya herhangi bir exception → `_multicoreRuntimeEnabled = false`
- Sonraki tick'ler otomatik olarak tek thread'li modda calisir
- Loga uyari yazilir

### Kural: Paralel Kodda

- **Build fazinda** obje state'i DEGISTIRILMEZ (salt okunur). Tum mutasyonlar Apply fazinda yapilir.
- Apply fazinda UID sirasi korunarak determinizm saglanir.
- Yeni paralel islem eklenirken `CancellationToken` kontrolu yapilmali.

---

## 6. Yeni Obje Eklerken Kontrol Listesi

Yeni bir `Item` veya `Character` alt sinifi eklerken:

- [ ] Istemciye yansiyacak property'lerde `MarkDirty(DirtyFlag.Xxx)` cagir
- [ ] `GameWorld.CreateItem/CreateCharacter` uzerinden olustur (`SetDirtyNotify` otomatik baglanir)
- [ ] NPC ise `TimerWheel.Schedule()` ile zamanlama yap
- [ ] Silinirken `MarkDirty(DirtyFlag.Deleted)` cagir
- [ ] NPC silinirken `TimerWheel.Remove()` cagir
- [ ] Multicore modda Build fazinda salt okunur erisim sagla
- [ ] Yeni `DirtyFlag` ekleniyorsa `ApplyDirtyOnly` metodunu guncelle

---

## 7. Config Referansi

| Anahtar | Tip | Varsayilan | Aciklama |
|---------|-----|------------|----------|
| `UseDeltaView` | bool | `true` | Delta-based V2 view update |
| `MulticoreEnabled` | bool | `false` | Multicore pipeline |
| `MulticoreWorkerCount` | int | `0` (auto) | Is parcacigi sayisi |
| `MulticorePhaseTimeoutMs` | int | `5000` | Faz timeout (ms) |
| `MulticoreDeterminismDebug` | bool | `false` | Determinizm dogrulama |
