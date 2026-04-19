# SphereNet

> **[TR]** SphereNet, [SphereServer Source-X](https://github.com/Sphereserver/Source-X) projesinin .NET 9 (C#) ile modern bir portudur. Ultima Online icin tam donanimli bir ozel sunucu emulatorudur. Source-X C++ kod tabanini temiz mimari, guclu tipleme ve moduler tasarimla modern .NET ekosistemine tasir.

> **[EN]** SphereNet is a modern .NET 9 (C#) port of [SphereServer Source-X](https://github.com/Sphereserver/Source-X). It is a fully-featured private server emulator for Ultima Online. It brings the battle-tested Source-X C++ codebase to the modern .NET ecosystem with clean architecture, strong typing, and modular design.

---

## Ozellikler / Features

- **Tam UO Emulasyonu / Full UO Emulation** — T2A'dan TOL'a kadar tum eklenti paketlerini destekler (Classic ve Enhanced istemciler) / Supports all expansion packs from T2A to TOL (Classic and Enhanced clients)
- **Sphere Script Uyumlulugu / Sphere Script Compatibility** — Source-X uyumlu `.scp` script dosyalarini okur ve calistirir / Reads and executes Source-X compatible `.scp` script files
- **Modern .NET 9** — Nullable referans tipleri, implicit usings ve en son calisma zamani performansi / Nullable reference types, implicit usings and latest runtime performance
- **Moduler Mimari / Modular Architecture** — 7 ozellestirilmis kutuphane ile temiz katman ayrimi / Clean separation across 7 specialized libraries
- **Sifreleme / Encryption** — Tum UO istemci surumleri icin Blowfish, Twofish ve Huffman / Blowfish, Twofish and Huffman for all UO client versions
- **Coklu Platform / Cross-Platform** — Windows (GUI + headless), Linux ve macOS (headless) / Windows (GUI + headless), Linux and macOS (headless)
- **Yonetim Araclari / Admin Tools** — WinForms konsol (Windows), Telnet konsolu ve HTTP durum sayfasi / WinForms console (Windows), Telnet console and HTTP status page
- **Kalicilik / Persistence** — Source-X uyumlu `.scp` formatinda kaydetme/yukleme / Save/load in Source-X compatible `.scp` format
- **Script Sistemi / Script System** — Tam ifade ayristirici, tetikleyici tabanli olay sistemi, global hook'lar, DB entegrasyonu / Full expression parser, trigger-based event system, global hooks, DB integration
- **Multi-Client Destegi / Multi-Client Support** — Ayni hesaptan birden fazla karakter esanli giris / Multiple characters from same account online simultaneously
- **MemoryMapped Harita / MemoryMapped Maps** — UOP/MUL harita dosyalari MemoryMappedFile ile yuklenir, OS sayfa yonetimi / UOP/MUL map files loaded via MemoryMappedFile, OS page management
- **Multicore Pipeline** — Cok cekirdekli islemcilerde paralel tick destegi / Parallel tick processing on multi-core CPUs

---

## Desteklenen Oyun Sistemleri / Supported Game Systems

| Sistem / System | Durum / Status | Aciklama / Description |
|-----------------|:-:|------------|
| **Savas / Combat** | :white_check_mark: | Swing timer, hasar hesaplama, elemental damage (Physical/Fire/Cold/Poison/Energy), zirh resist / Swing timer, damage calc, elemental damage, armor resist |
| **Buyu / Magic** | :white_check_mark: | 60+ buyu, cast/effect, spell interruption (hasar/hareket/equip), reagent maliyeti / 60+ spells, cast/effect, spell interruption (damage/move/equip), reagent cost |
| **Yetenekler / Skills** | :white_check_mark: | 31+ handler, trigger entegrasyonu (@SkillPreStart → @SkillGain), gain/decay / 31+ handlers, trigger integration, gain/decay |
| **Zanaat / Crafting** | :white_check_mark: | Script-based recipe, gump UI, tool gereksinimi, maker's mark / Script-based recipes, gump UI, tool requirement, maker's mark |
| **NPC AI** | :white_check_mark: | Monster/Pet/Healer/Guard/Vendor/Animal brain turleri, A* pathfinding / Monster/Pet/Healer/Guard/Vendor/Animal brain types, A* pathfinding |
| **Konut / Housing** | :white_check_mark: | Multi.mul yukleme, decay sistemi (LikeNew → IDOC), co-owner/friend, lockdown/secure / Multi.mul loading, decay system, co-owner/friend, lockdown/secure |
| **Vendor & Ticaret / Trade** | :white_check_mark: | Buy/sell paket parse, envanter restock, fiyat hesaplama, SecureTrade / Buy/sell packet parse, inventory restock, price calc, SecureTrade |
| **Lonca & Parti / Guild & Party** | :white_check_mark: | Party paketleri (invite/chat/HP share), guild stone gump, savas/ittifak / Party packets (invite/chat/HP share), guild stone gump, war/alliance |
| **Olum / Death** | :white_check_mark: | Ceset, ganimet, curume, dirilis, karma/fame degisimi / Corpse, loot, decay, resurrection, karma/fame change |
| **Zehir / Poison** | :white_check_mark: | 5 seviye (Lesser → Lethal), tick-based hasar, resist, cure / 5 levels (Lesser → Lethal), tick-based damage, resist, cure |
| **Hareket / Movement** | :white_check_mark: | Sequence validation, time-based throttle, stamina, collision / Sequence validation, time-based throttle, stamina, collision |
| **Konusma / Speech** | :white_check_mark: | Metin yayini, NPC keyword yanit, script-driven SPEECH / Text broadcast, NPC keyword response, script-driven SPEECH |
| **Hava & Isik / Weather & Light** | :white_check_mark: | Yagmur/kar, gun/gece dongusu, mevsim, dungeon karanligi / Rain/snow, day/night cycle, seasons, dungeon darkness |
| **Binek / Mount** | :white_check_mark: | Mount/dismount, body→mountItem mapping, login sync / Mount/dismount, body→mountItem mapping, login sync |
| **Trigger & Events** | :white_check_mark: | @Login, @Death, @Hit, @Click, @DClick, @Equip, @RegionEnter, @Skill, @Spell ve dahasi / and more |
| **Ceza / Justice** | :white_check_mark: | Criminal flag (timer-based), murder count, sureli jail sistemi / Criminal flag (timer-based), murder count, timed jail system |
| **Region** | :white_check_mark: | Flag/area, weather/music trigger, NOMAGIC/NORECALL/SAFE / Flag/area, weather/music trigger, NOMAGIC/NORECALL/SAFE |

---

## Proje Yapisi / Project Structure

```
sphereNet/
├── src/
│   ├── SphereNet.Core/           # Temel tipler, enum'lar / Core types, enums
│   ├── SphereNet.Network/        # UO protokolu, TCP, sifreleme / UO protocol, TCP, encryption
│   ├── SphereNet.Scripting/      # Sphere script parser & execution
│   │   └── Execution/            #   ScriptSystemHooks, ScriptDbAdapter
│   ├── SphereNet.Game/           # Oyun mantigi / Game logic
│   │   ├── AI/                   #   NPC AI (Monster, Pet, Healer, Guard)
│   │   ├── Combat/               #   CombatEngine, elemental damage
│   │   ├── Crafting/             #   CraftingEngine, recipes
│   │   ├── Death/                #   DeathEngine, karma/fame
│   │   ├── Guild/                #   GuildManager, persistence
│   │   ├── Housing/              #   HousingEngine, decay, multi
│   │   ├── Magic/                #   SpellEngine, interruption
│   │   ├── Movement/             #   MovementEngine, pathfinding
│   │   ├── Skills/               #   SkillEngine, 31+ handlers
│   │   ├── Speech/               #   SpeechEngine, NPC dialog
│   │   ├── Trade/                #   TradeEngine, vendor buy/sell
│   │   ├── World/                #   GameWorld, WeatherEngine, regions
│   │   ├── Scheduling/           #   TimerWheel (NPC scheduling)
│   │   └── Gumps/                #   GumpBuilder (0xDD packet builder)
│   ├── SphereNet.MapData/        # UO .mul/.uop harita okuyucu / map file readers
│   ├── SphereNet.Persistence/    # Dunya kaydetme/yukleme / World save/load
│   └── SphereNet.Server/         # Uygulama giris noktasi / Application entry point
│       └── Admin/ConsoleForm.cs  #   WinForms admin konsolu / admin console
├── tests/
│   ├── SphereNet.Core.Tests/
│   ├── SphereNet.Game.Tests/
│   ├── SphereNet.MapData.Tests/
│   ├── SphereNet.Network.Tests/
│   ├── SphereNet.Persistence.Tests/
│   └── SphereNet.Scripting.Tests/
├── config/
│   ├── sphere.ini                # Sunucu yapilandirmasi / Server configuration
│   └── sphereCrypt.ini           # Istemci sifreleme anahtarlari / Client encryption keys
├── scripts/                      # Oyun script dosyalari / Game script files (.scp)
├── save/                         # Kaydedilmis dunya verileri / Saved world data
└── logs/                         # Log ciktisi / Log output
```

---

## Mimari / Architecture

```
┌─────────────────────────────────────────┐
│            SphereNet.Server             │  Entry point, DI, main loop
├─────────────────────────────────────────┤
│            SphereNet.Game               │  Game logic, world, mechanics
├──────────┬──────────┬───────────────────┤
│ Network  │Scripting │   Persistence     │  Protocol, scripts, save/load
├──────────┴──────────┴───────────────────┤
│           SphereNet.MapData             │  UO map file readers
├─────────────────────────────────────────┤
│            SphereNet.Core               │  Types, enums, interfaces
└─────────────────────────────────────────┘
```

### Temel Bilesenler / Core Components

| Bilesen / Component | Sinif / Class | Amac / Purpose |
|---------|-------|------|
| Dunya / World | `GameWorld` | Merkezi dunya, sektor izgarasi, UID yoneticisi, tick, dirty set / Central world, sector grid, UID manager, tick, dirty set |
| Ag / Network | `NetworkManager` | Baglanti havuzu, kabul dongusu / Connection pool, accept loop |
| Baglanti / Connection | `NetState` | Sifreleme destekli durum makinesi / Encryption-aware state machine |
| Paketler / Packets | `PacketManager` | 256+ paket tipi isleyici / 256+ packet type handlers |
| Scriptler / Scripts | `ResourceHolder` | Script dosya yukleyici, kaynak indeksleme / Script file loader, resource indexing |
| Yorumlayici / Interpreter | `ScriptInterpreter` | Script komut calistirici (IF, FOR, WHILE, vb.) / Script command executor |
| Savas / Combat | `CombatEngine` | Hasar, vurus sansi, elemental resist / Damage, hit chance, elemental resist |
| Buyu / Magic | `SpellEngine` | Buyu yapma, efektler, interruption / Spell casting, effects, interruption |
| Zamanlama / Timer | `TimerWheel` | 256 slotlu NPC zamanlama carki / 256-slot NPC scheduling wheel |
| Gump'lar / Gumps | `GumpBuilder` | Source-X uyumlu gump paket olusturucu / Source-X compatible gump packet builder |
| Hook'lar / Hooks | `ScriptSystemHooks` | Global script hook dispatcher |

---

## On Kosullar / Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Ultima Online istemci dosyalari (harita, karo, coklu yapi veri dosyalari) / Ultima Online client files (map, tile, multi data files)

---

## Baslangic / Getting Started

### 1. Depoyu Klonlayin / Clone the Repository

```bash
git clone https://github.com/Yunusolcay/sphereNet.git
cd sphereNet
```

### 2. Derleme / Build

```bash
dotnet build
```

### 3. Yapilandirma / Configuration

`config/sphere.ini` dosyasini duzenleyin / Edit `config/sphere.ini`:

```ini
SERVNAME=My Shard
SERVPORT=2593
CLIENTVERSION=7.0.15.1
CLIENTMAX=128
ADMINPASSWORD=changeme
```

Ultima Online istemci veri dosyalarinizi `MULFILES` ayarinda belirtilen yola yerlestirin.
Place your Ultima Online client data files at the path specified in the `MULFILES` setting.

### 4. Scriptleri Ekleyin / Add Scripts

Sphere uyumlu `.scp` script dosyalarinizi `scripts/` dizinine yerlestirin. Bunlar esyalari, karakterleri, buyuleri ve oyun davranislarini tanimlar.
Place Sphere-compatible `.scp` script files in the `scripts/` directory. These define items, characters, spells and game behaviors.

### 5. Calistirma / Run

```bash
# Windows — GUI modu / GUI mode (default)
dotnet run --project src/SphereNet.Server

# Windows — headless modu / headless mode (no GUI)
dotnet run --project src/SphereNet.Server -- --headless

# Linux / macOS — otomatik headless / auto headless
dotnet run --project src/SphereNet.Server
```

Sunucu yapilandirilmis portta (varsayilan: 2593) dinlemeye baslar.
The server starts listening on the configured port (default: 2593).

> **Not / Note:** Linux'ta `--headless` argumani gereksizdir; WinForms mevcut olmadigindan otomatik olarak headless modda calisir. / On Linux, `--headless` is unnecessary; it automatically runs in headless mode since WinForms is not available.

### Yonetici Erisimi / Admin Access

| Arayuz / Interface | Port | Aciklama / Description |
|--------|------|----------|
| Oyun / Game | `SERVPORT` (2593) | UO istemci baglantilar / UO client connections |
| Telnet | `SERVPORT + 1` (2594) | Yonetici komut konsolu / Admin command console |
| Web Durumu / Web Status | `SERVPORT + 2` (2595) | HTTP sunucu durum sayfasi / HTTP server status page |

---

## Calistirma Modlari / Run Modes

| Mod / Mode | Platform | Aciklama / Description |
|-----|----------|----------|
| **GUI** | Windows | WinForms konsol penceresi (varsayilan) / WinForms console window (default) |
| **Headless** | Tumu / All | Konsol modu, stdin komut, Ctrl+C kapatma / Console mode, stdin commands, Ctrl+C shutdown |

## WinForms Konsol / WinForms Console

SphereNet, Windows'ta zengin bir yonetim arayuzu sunar / SphereNet provides a rich admin interface on Windows:

- **Renk Kodlu Log / Color-Coded Log** — Hata (kirmizi), uyari (sari), debug (amber), bilgi (mavi) / Error (red), warning (yellow), debug (amber), info (blue)
- **CPU / RAM Metrikleri / Metrics** — Her saniye guncellenen gosterim / Updated every second
- **Canli Istatistikler / Live Stats** — Oyuncu, hesap, karakter, esya, script sayilari / Player, account, character, item, script counts
- **Komut Gecmisi / Command History** — Ok tuslari ile 100 komut gecmisi / Arrow keys for 100-entry command history
- **Hizli Butonlar / Quick Buttons** — DEBUG, RESYNC ve SAVE tek tikla / DEBUG, RESYNC and SAVE with one click
- **Karanlik Tema / Dark Theme** — 1200x760, Consolas 11pt

---

## Performans ve Optimizasyon / Performance & Optimization

### Sektor-Tabanli Goruntulenme / Sector-Based View Update

Her tick'te oyuncunun cevresindeki sektorler taranarak gorunur nesneler guncellenir. DirtyFlag sistemi ile degisen nesneler takip edilir.
Each tick scans sectors around the player to update visible objects. The DirtyFlag system tracks changed objects.

### Fast-Path View-Delta (ServUO-style)

Ana donguye HasDirty-gated bir sub-tick view update asamasi eklendi. Bir obje degistirildiginde (hareket, stat, equip, body morph) ~1ms icinde yakin istemcilere yayilir — 250ms tick beklemek gerekmez. Dirty set bos oldugunda bu asama ucuz bir sayi kontrolu ile atlanir.
A HasDirty-gated sub-tick view update phase runs each main-loop iteration. When any object is mutated (move, stat, equip, body morph), the change reaches nearby clients in ~1ms instead of waiting for the next 250ms tick. The phase short-circuits on an empty dirty set so idle iterations stay cheap.

Program.cs icindeki `FastPathViewDeltaEnabled` sabiti ile acilip kapatilabilir (tanilama icin).
Toggleable via the `FastPathViewDeltaEnabled` constant in Program.cs (for diagnostics).

### MemoryMapped Harita Dosyalari / MemoryMapped Map Files

UOP harita dosyalari decompress edildikten sonra MemoryMappedFile ile yuklenir. Isletim sistemi hangi sayfalarin RAM'de kalacagini yonetir, ~200MB RAM tasarrufu saglar.
UOP map files are decompressed then loaded via MemoryMappedFile. The OS manages which pages stay in RAM, saving ~200MB RAM.

### NPC Timer Wheel

Geleneksel her-NPC-her-tick yerine 256 slotlu zamanlama carki / Instead of per-NPC-per-tick, a 256-slot hashed timer wheel:

- **256 slot x 250ms** = 64 saniyelik dongu / 64-second full cycle
- **O(1) zamanlama / scheduling** — NPC'ler `nextActionTime` ile slot'a atanir / NPCs assigned to slots by `nextActionTime`

### Multicore Pipeline

Tick isleme her zaman dort fazda calisir; hata veya timeout durumunda
runtime otomatik olarak tek thread'e duser / Tick processing always
runs in four phases; on failure or timeout the runtime automatically
falls back to single-thread:

| Faz / Phase | Tur / Type | Aciklama / Description |
|-----|-----|----------|
| **Snapshot** | Paralel / Parallel | Sektor tick, NPC snapshot |
| **Build** | Paralel / Parallel | NPC karar hesabi, delta hesabi / NPC decision calc, delta calc (read-only) |
| **Apply** | Seri / Serial | NPC kararlari UID sirasinda uygulanir / NPC decisions applied in UID order |
| **Flush** | Seri / Serial | Curume, isik, telnet, web status / Decay, light, telnet, web status |

---

## Yapilandirma / Configuration

### Temel Ayarlar / Basic Settings (`sphere.ini`)

```ini
SERVNAME=My Shard
SERVPORT=2593
CLIENTVERSION=7.0.15.1
CLIENTMAX=128
ADMINPASSWORD=changeme
```

### Performans Ayarlari / Performance Settings

| Anahtar / Key | Varsayilan / Default | Aciklama / Description |
|---------|------------|----------|
| `MulticoreWorkerCount` | `0` | Is parcacigi sayisi (0=otomatik) / Worker count (0=auto) |
| `MulticorePhaseTimeoutMs` | `5000` | Faz timeout (ms), asildiysa tek thread / Phase timeout, falls back to single-thread |
| `TickSleepMode` | `2` | CPU yield: 0=spin, 1=sleep, 2=hybrid |

### Oyun Mekanikleri / Gameplay Mechanics

Her anahtar Source-X'teki esdegerine karsilik gelir ve kod tarafindan aktif olarak kullanilir. / Each key maps to its Source-X equivalent and is actively consumed by the code.

| Anahtar / Key | Varsayilan / Default | Aciklama / Description |
|---------|------------|----------|
| `CRIMINALTIMER` | `180` | Suclu (gri) flag suresi (sn) / Criminal flag duration (sec) |
| `MURDERMINCOUNT` | `5` | Katil (kirmizi) esigi / Murderer threshold |
| `MURDERDECAYTIME` | `28800` | Kill sayaci azalma suresi (sn) / Kill count decay period (sec) |
| `ATTACKINGISACRIME` | `1` | Innocent hedefe saldiri kriminal / Attacking innocent is a crime |
| `SNOOPCRIMINAL` | `1` | Basarisiz snoop kriminal / Failed snoop is a crime |
| `REAGENTSREQUIRED` | `1` | Buyu reagent maliyeti (wand/GM bypass) / Spell reagent cost (wand/GM bypass) |
| `LOOTINGISACRIME` | `1` | Baskasinin cesedini yagmalamak kriminal / Looting another's corpse is a crime |
| `CONTAINERMAXITEMS` | `125` | Normal konteyner esya limiti / Regular container item limit |
| `BANKMAXITEMS` | `125` | Banka kutusu esya limiti / Bank box item limit |
| `BANKMAXWEIGHT` | `1600` | Banka kutusu agirlik limiti (stone) / Bank box weight limit |
| `CORPSENPCDECAY` | `7` | NPC ceset curume (dakika) / NPC corpse decay (min) |
| `CORPSEPLAYERDECAY` | `7` | Oyuncu ceset curume (dakika) / Player corpse decay (min) |
| `MAXBASESKILL` | `1200` | Maksimum yetenek puani (x10) / Max skill value (x10) |

### Genisleme Paketi Flag'leri / Expansion Flags

Login'de `0xB9 SupportedFeatures` paketi ile istemciye OR'lanip gonderilir. / OR'd and sent to the client via `0xB9 SupportedFeatures` at login.

| Anahtar / Key | Bit | Eklenti / Expansion |
|---------|-----|----------|
| `FEATURET2A` | `0x01` | The Second Age |
| `FEATURELBR` | `0x02` | Lord Blackthorn's Revenge |
| `FEATUREAOS` | `0x20` | Age of Shadows |
| `FEATURESE` | `0x400` | Samurai Empire |
| `FEATUREML` | `0x800` | Mondain's Legacy |
| `FEATURESA` | `0x10000` | Stygian Abyss |
| `FEATURETOL` | `0x400000` | Time of Legends |

### Kaydetme Formati / Save Format

Dunya kaydi 4 farkli formatta tutulabilir, format + shard + rolling `sphere.ini`'de secilir. Loader dosya uzantisindan formati otomatik alglar, farkli snapshot'lar ayni dizinde birlikte bulunabilir. Account dosyalari da ayni formati kullanir (`sphereaccu.sbin.gz` vb). / World + account saves can be written in 4 formats with optional sharding / rolling. Loader auto-detects format from file extension.

| Format | Uzanti / Extension | Boyut / Size | Not / Notes |
|--------|-------------------|--------------|-------------|
| `Text` | `.scp` | 100% | Klasik Sphere metin, insan okunabilir, Source-X/Sphere legacy uyumlu / Classic Sphere text, human-readable, Source-X/Sphere legacy compat |
| `TextGz` | `.scp.gz` | ~15% | Ayni metin, GZip sarili / Same text, GZip-wrapped |
| `Binary` | `.sbin` | ~50% | Tag-stream binary (key,value envelope) / Tag-stream binary, no per-field schema |
| `BinaryGz` (default) | `.sbin.gz` | ~8-10% | Binary + gzip, en kucuk, en hizli load / Smallest disk footprint, fastest load |

**Shard modu** (`SAVESHARDS`):

| Değer / Value | Davranış / Behavior |
|---|---|
| `0` | Her zaman tek dosya, rolling kapalı / Always single file, no rolling |
| `1` (default) | Size-based rolling — `SAVESHARDSIZEMB`'e göre parçalar / Rolling by size |
| `2-16` | Sabit paralel hash shard / Fixed parallel hash shards (UID % N) |

**Size-based rolling**: `SAVESHARDS=1` + `SAVESHARDSIZEMB=50` → tek dosya olarak basla, limit asilinca yenisini ac. Kucuk worldler `{base}{ext}` olarak klasik layout'ta kalir (manifest yok), buyuk worldler `{base}.0{ext}`, `{base}.1{ext}` + `{base}.manifest` uretir. / Starts single-file, rolls over threshold. Small worlds keep classic layout.

**Hash shard**: `SAVESHARDS=N` (N≥2) — entity'ler UID hash ile N parcaya bolunur, her parca kendi Task'inde yazilir (paralel I/O). Her zaman `{base}.0{ext}..{base}.(N-1){ext}` + manifest. / Entities split into N shards by UID hash, each written in its own Task.

**Kapasite tahmini / Capacity estimate (BinaryGz, 75 MB shard):**

- **Item** (SERIAL + ID + P + COLOR + AMOUNT + TYPE + ATTR + 1-2 TAG)
  - Ham binary / Raw binary: ~120 byte/item
  - Gzip sonrasi / After gzip: ~12-20 byte/item (key'ler hep ayni → dictionary reuse cok iyi / repeating keys compress very well)
  - **75 MB ≈ 4M – 6M item**

- **NPC** (skill tablosu + stat + stat-max + flags + tags + equipment serial'leri → ~40+ property)
  - Ham binary / Raw binary: ~500-800 byte/NPC
  - Gzip sonrasi / After gzip: ~40-80 byte/NPC
  - **75 MB ≈ 900K – 1.8M NPC**

Dunya su limitlerin altinda kaliyorsa tek `.sbin.gz` dosyasi olur, manifest yazilmaz (klasik Sphere davranisi). / Worlds under these thresholds land in a single `.sbin.gz`, no manifest — classic Sphere layout preserved.

**Varsayilan config / Default config (`sphere.ini`):**
```ini
SAVEFORMAT=BinaryGz       # Text | TextGz | Binary | BinaryGz
SAVESHARDS=3              # 0=tek dosya, 1=rolling, 2-16=paralel hash shard
SAVESHARDSIZEMB=75        # rolling esigi (SAVESHARDS=1 iken) / rolling threshold
```

**Runtime degistirme / Runtime switch** (Owner+):
```
.SAVEFORMAT BinaryGz 4     # Format + shard sayisi, anlik save tetikler / triggers save now
.SAVEFORMAT Text           # Sadece format, shard degismez / format only
```

Komut once loader ile mevcut save'i okur, sonra yeni formatta yazar, eski dosyalari siler — migration tek adimda. / Loads current save, writes in new format, deletes old files — one-shot migration.

### Veritabani / Database (`sphere.ini`)

| Anahtar / Key | Aciklama / Description |
|---------|----------|
| `MySQL` | MySQL aktif/pasif (1/0) / Enable/disable |
| `MySQLHost` | Sunucu adresi / Server address |
| `MySQLUser` | Kullanici adi / Username |
| `MySQLPassword` | Sifre / Password |
| `MySQLDatabase` | Veritabani adi / Database name |

---

## Script Sistemi / Script System

SphereNet, Source-X uyumlu script dilini destekler / SphereNet supports the Source-X compatible script language:

```ini
[ITEMDEF 0401]
DEFNAME=i_sword_long
NAME=long sword
TYPE=t_weapon_sword
DAM=3,7
SPEED=40

ON=@Create
   COLOR=0

ON=@Equip
   SOUND=0x051
```

### Script Ozellikleri / Script Features

- **Tanimlar / Definitions:** `[ITEMDEF]`, `[CHARDEF]`, `[SPELL]`, `[SKILL]`, `[REGION]`, etc.
- **Tetikleyiciler / Triggers:** `@Create`, `@Delete`, `@Attack`, `@Death`, `@Speech`, `@UseSkill`, `@Hit`, `@GetHit`, `@RegionEnter`, `@Equip`, etc.
- **Kontrol Akisi / Control Flow:** `IF/ELIF/ELSE`, `FOR`, `WHILE`, `DORAND`, `DOSWITCH`
- **Degiskenler / Variables:** `TAG` (nesne-yerel / object-local), `CTAG` (karakter-yerel / character-local), `VAR`, `LOCAL`
- **Ifadeler / Expressions:** Matematik, string islemleri (`STRARG`, `STRSUB`, `STRLEN`, `STRMATCH`), karsilastirmalar / Math, string ops, comparisons
- **Kosullar / Conditions:** `ISNUMBER`, `ISEMPTY`, `ISPLAYER`, `ISNPC`

### Global Hook'lar / Global Hooks

| Hook Ailesi / Family | Ornek / Example | Aciklama / Description |
|-------------|-------|----------|
| `f_onserver_*` | `f_onserver_start` | Sunucu yasam dongusu / Server lifecycle |
| `f_onclient_*` | `f_onclient_helppage` | Istemci etkilesimi / Client interaction |
| `f_onaccount_*` | `f_onaccount_login` | Hesap islemleri / Account operations |
| `f_onobj_*` | `f_onobj_create` | Nesne yasam dongusu / Object lifecycle |
| `f_packet_0xNN` | `f_packet_0x12` | Ham paket yakalama / Raw packet interception |

### Veritabani Entegrasyonu / Database Integration

```
db.connect             // Varsayilan baglanti / Default connection
db.execute "INSERT..." // SQL calistir / Execute SQL
db.query "SELECT..."   // Sorgu calistir / Run query
db.row.numrows         // Sonuc satir sayisi / Result row count
db.row.0.name          // Ilk satirin sutunu / First row column
db.close               // Baglanti kapat / Close connection
```

---

## Testleri Calistirma / Running Tests

```bash
dotnet test

# Kapsam ile / With coverage
dotnet test --collect:"XPlat Code Coverage"
```

---

## Bagimliliklar / Dependencies

| Paket / Package | Surum / Version | Amac / Purpose |
|-------|-------|------|
| BouncyCastle.Cryptography | 2.6.2 | Blowfish/Twofish sifreleme / encryption |
| Microsoft.Extensions.DependencyInjection | 10.0.5 | DI konteyneri / DI container |
| Microsoft.Extensions.Logging | 10.0.5 | Loglama / Logging |
| Serilog + Sinks | 6.x - 10.x | Konsol ve dosya loglama / Console and file logging |
| System.Windows.Forms | — | WinForms konsol (opsiyonel / optional, Windows only) |
| xUnit | 2.9.2 | Birim test / Unit test framework |
| Coverlet | 6.0.2 | Kod kapsamasi / Code coverage |

---

## Katki Saglama / Contributing

1. Depoyu fork'layin / Fork the repository
2. Ozellik dali olusturun / Create a feature branch (`git checkout -b feature/my-feature`)
3. Degisiklikleri commit'leyin / Commit changes (`git commit -m 'Add new feature'`)
4. Dali push'layin / Push the branch (`git push origin feature/my-feature`)
5. Pull Request acin / Open a Pull Request

---

## Lisans / License

Bu proje acik kaynakli bir calismadir. Ayrintilar icin [LICENSE](LICENSE) dosyasina bakiniz.
This is an open-source project. See [LICENSE](LICENSE) for details.

---

## Tesekkurler / Acknowledgments

- [SphereServer Source-X](https://github.com/Sphereserver/Source-X) — Bu projenin port edildigi orijinal C++ sunucu / The original C++ server this project is ported from
- [Ultima Online](https://uo.com/) — Origin Systems / Electronic Arts tarafindan gelistirilen efsanevi MMORPG / The legendary MMORPG by Origin Systems / Electronic Arts
