# SphereNet

> **[TR]** .NET 9 ile yazilmis, [Source-X](https://github.com/Sphereserver/Source-X) tabanli Ultima Online ozel sunucu emulatoru.
>
> **[EN]** A .NET 9 Ultima Online private server emulator based on [Source-X](https://github.com/Sphereserver/Source-X).

![SphereNet Console](sphereNetConsole.png)

---

## Ozellikler / Features

- Source-X `.scp` script uyumlulugu / Source-X `.scp` script compatibility
- T2A → TOL eklenti paketi destegi / T2A → TOL expansion support
- Blowfish, Twofish, Huffman sifreleme / encryption
- Windows (GUI + headless), Linux, macOS
- Telnet yonetim konsolu + SignalR web panel / Telnet admin console + SignalR web panel

---

## Source-X'te Olmayan Ozellikler / Beyond Source-X

### Coklu Kaydetme Formati / Multiple Save Formats

Source-X yalnizca duz metin `.scp` formatinda kaydeder. SphereNet 4 farkli formati destekler ve runtime'da format degistirilebilir.
Source-X only saves in plain text `.scp` format. SphereNet supports 4 formats with runtime switching.

| Format | Uzanti / Ext | Boyut / Size | Aciklama / Description |
|---|---|---|---|
| `Text` | `.scp` | 100% | Source-X uyumlu, insan okunabilir / Source-X compatible, human-readable |
| `TextGz` | `.scp.gz` | ~15% | Ayni metin, GZip sarili / Same text, GZip-wrapped |
| `Binary` | `.sbin` | ~50% | Tag-stream binary |
| `BinaryGz` | `.sbin.gz` | ~8-10% | En kucuk, en hizli / Smallest and fastest |

**Shard destegi / Shard support:** `SAVESHARDS=0` tek dosya, `1` size-based rolling, `2-16` paralel hash shard (UID % N, paralel I/O).
Runtime'da `.SAVEFORMAT BinaryGz 4` komutuyla format + shard degistirilir, migration tek adimda. /
Runtime `.SAVEFORMAT BinaryGz 4` command switches format + shards, one-shot migration.

### Coklu Veritabani / Multi-Database

Source-X tek bir MySQL baglantisi destekler. SphereNet ayni anda birden fazla veritabanina baglanir — her birinin kendi ayarlari, thread modu ve timeout'u vardir.
Source-X supports a single MySQL connection. SphereNet connects to multiple databases simultaneously — each with its own settings, thread mode and timeouts.

```ini
[MYSQL default]
Host=localhost
User=root
Password=secret
Database=sphere
AutoConnect=1

[MYSQL logging]
Host=10.0.0.2
User=logger
Password=logpass
Database=logs
UseThread=1
```

Script'te `db.select <isim>` ile aktif baglanti degistirilir / Switch active connection with `db.select <name>` in scripts:
```
db.select logging
db.execute "INSERT INTO logs (msg) VALUES ('event')"
db.select default
db.query "SELECT * FROM users WHERE id=1"
```

### Multicore Tick Pipeline

Source-X tek thread'de calisir. SphereNet tick islemeyi dort faza ayirir, paralel calistirir — hata durumunda otomatik tek thread'e duser.
Source-X runs single-threaded. SphereNet splits tick processing into four parallel phases — auto-fallback to single-thread on failure.

| Faz / Phase | Tur / Type | Aciklama / Description |
|---|---|---|
| Snapshot | Paralel | Sektor tick, NPC snapshot |
| Build | Paralel | NPC karar hesabi (salt okunur) / NPC decisions (read-only) |
| Apply | Seri | Kararlar UID sirasinda uygulanir / Decisions applied in UID order |
| Flush | Seri | Decay, isik, telnet, web / Decay, light, telnet, web |

**Region Cache:** `FindRegion` her tick'te binlerce kez cagrilir (guard zone, PvP, muzik, hava). Source-X her cagride tum region listesini tarar (O(n)). SphereNet 8x8 tile grid bazli `ConcurrentDictionary` cache kullanir — ayni bolge icin tekrar tarama yapmaz, region eklendiginde cache otomatik temizlenir.
`FindRegion` is called thousands of times per tick (guard zone, PvP, music, weather). Source-X scans the full region list each time (O(n)). SphereNet uses an 8x8 tile grid `ConcurrentDictionary` cache — no rescan for the same area, cache auto-clears on region changes.

### Sektor Uyku / Sector Sleep

Source-X her tick'te tum sektorleri tarar. SphereNet sadece online oyuncu iceren sektorleri tick'ler (oyuncu etrafinda 5x5 sektor penceresi). Bos bolgelerdeki NPC ve item'ler sifir CPU maliyeti olusturur. 300 oyuncu bir sehre toplaninca haritanin geri kalani uyur, islemci sadece aktif bolgelerle ilgilenir.
Source-X iterates all sectors every tick. SphereNet only ticks sectors containing online players (5x5 sector window around each player). NPCs and items in empty areas cost zero CPU. When 300 players cluster in one city, the rest of the map sleeps.

**Timer Butunlugu / Timer Integrity:** Tum timerlar (item decay, spawn suresi, TIMER trigger) tick sayaci degil `Environment.TickCount64` bazli mutlak zaman damgasi kullanir. Uyuyan sektorlere 3 dakikada bir hafif bakim tick'i uygulanir — sadece item timerlari (decay, spawn, TIMER) islenir, NPC AI ve karakter tick'leri atlanir. Bu sayede oyuncusuz bolgelerde spawn noktalari uretmeye devam eder, suresi dolan itemler silinir ve TIMER trigger'lari zamaninda ateslanir. Aktif sektorlerle ayni absolute timestamp mekanizmasi kullanildigi icin hicbir timer kaybolmaz veya bozulmaz.
**Timer Integrity:** All timers (item decay, spawn interval, TIMER triggers) use absolute timestamps (`Environment.TickCount64`), not tick counters. Sleeping sectors receive a lightweight maintenance tick every 3 minutes — only item timers (decay, spawn, TIMER) are processed, NPC AI and character ticks are skipped. This ensures spawn points keep producing in empty areas, expired items are cleaned up, and TIMER triggers fire on schedule. The same absolute timestamp mechanism used by active sectors guarantees no timer is lost or corrupted.

### Delta View (Alan Bazli Degisiklik Takibi / Field-Level Change Tracking)

Source-X her tick'te gorunur objelerin tamamini yeniden gonderir. SphereNet her objede field bazli `DirtyFlag` bitmask tutar (Position, Body, Hue, Stats, Equip...). Client'a sadece degisen alanlar gonderilir. Ayrica view hesabi (`BuildViewDelta`) paralel fazda, paket gonderimleri (`ApplyViewDelta`) seri fazda calisir — paralel tick'e uyumlu.
Source-X resends visible objects in full each tick. SphereNet tracks field-level changes via `DirtyFlag` bitmasks on each object (Position, Body, Hue, Stats, Equip...). Only changed fields are sent to clients. View computation (`BuildViewDelta`) runs in the parallel phase, packet I/O (`ApplyViewDelta`) in the serial phase — safe for multicore ticks.

### MemoryMapped Harita / MemoryMapped Maps

Source-X harita dosyalarini tamamen RAM'e yukler. SphereNet `MemoryMappedFile` ile yukler — isletim sistemi hangi sayfalarin RAM'de kalacagini yonetir, ~200MB tasarruf.
Source-X loads map files entirely into RAM. SphereNet uses `MemoryMappedFile` — the OS manages page residency, saving ~200MB.

### WinForms Yonetim Konsolu / WinForms Admin Console

Source-X'te yalnizca terminal ciktisi vardir. SphereNet Windows'ta renk kodlu log, CPU/RAM metrikleri, canli istatistikler ve komut gecmisi sunan bir GUI konsol icerir.
Source-X only has terminal output. SphereNet includes a Windows GUI console with color-coded logs, CPU/RAM metrics, live stats and command history.

### Web Panel (SignalR Canli Dashboard / Live Dashboard)

HTTP durum sayfasinin otesinde, ASP.NET Core + SignalR tabanli gercek zamanli yonetim paneli. Canli log akisi, CPU/RAM metrikleri, oyuncu listesi ve sunucu kontrol komutlari tarayicidan calisir. Token tabanli kimlik dogrulama ve sıkistirma middleware icerir.
Beyond a simple HTTP status page — a real-time admin dashboard built on ASP.NET Core + SignalR. Live log streaming, CPU/RAM metrics, player list and server control commands run from the browser. Includes token-based auth and compression middleware.

### NPC Timer Wheel

Source-X her NPC'yi her tick'te tarar. SphereNet 256 slotlu zamanlama carki kullanir — NPC'ler `nextActionTime`'a gore slot'lara atanir, O(1) zamanlama.
Source-X scans every NPC every tick. SphereNet uses a 256-slot hashed timer wheel — NPCs assigned by `nextActionTime`, O(1) scheduling.

### Bot Stress Test Sistemi / Bot Stress Test System

Dahili bot sistemi ile TCP uzerinden gercek istemci baglantilari simule edilebilir. Botlar tam UO login akisini takip eder (login server → relay → game server) ve oyun ici aksiyonlar gerceklestirir (yurume, savas, loot, skill).
Built-in bot system simulates real client connections over TCP. Bots follow the full UO login flow and perform in-game actions (walk, combat, loot, skills).

```bash
.bot spawn 100        # 100 bot olustur / spawn 100 bots
.bot stop             # botlari durdur / stop bots
.bot status           # durum goster / show status
.bot spawn britain 50 # Britain'de 50 bot / 50 bots in Britain
```

### Durum Kaydi / State Recording

Source-X'te oyun ici olaylari geri izlemek icin bir sistem yoktur. SphereNet SQLite tabanli bir kayit/oynatma motoru icerir — karakter hareketleri ve durum snapshot'lari belirli araliklarla kaydedilir. GM sorusturmasi, hile tespiti ve debug icin gecmise donuk oynatma imkani saglar.
Source-X has no system for replaying past events. SphereNet includes a SQLite-backed recording/replay engine — character movements and state snapshots are captured at intervals. Enables retrospective playback for GM investigations, cheat detection and debugging.

---

## Performans / Performance

100ms tick intervali (10 tick/saniye) ile stress test sonuclari.
Stress test results with 100ms tick interval (10 ticks/second).

**Test Ortami / Environment:** ~50,000 NPC + ~101,000 item + 300 bot (TCP baglanti / connection)
**Test Environment:** ~50,000 NPCs + ~101,000 items + 300 bots (TCP connections)

| Olcum / Sample | Avg Tick | Max Tick | pps_in | pps_out | Budget |
|----------------|----------|----------|--------|---------|--------|
| Baslangic / Start | 8.7ms | 35.1ms | 2,370/s | 790/s | 8.7% |
| Kararli / Steady | 8.9ms | 33.5ms | 4,141/s | 802/s | 8.9% |
| Yuk doruk / Peak | 9.1ms | 37.6ms | 7,366/s | 846/s | 9.1% |

**Save:** 102,780 item + 50,363 char → 0.6 saniye / seconds (BinaryGz, 3 shard)

**Tick dagilimi / Tick breakdown (300 bot, tipik slow_tick):**

| Faz / Phase | Ortalama / Average |
|---|---|
| Snapshot | 1.6ms |
| NPC Build | 1.1ms |
| NPC Apply | 20.8ms |
| View Build | 0.7ms |
| Apply + Flush | 0.4ms |

**Not / Note:** 300 bot testi worst-case senaryodur — 50K NPC + 101K item yuku altinda 300 es zamanli baglanti. `npc_apply` fazindaki 20ms slow-tick spike'lari en buyuk bottleneck; ortalama tick sureleri bunun altinda kalir. Gercek kullanim senaryolarinda oyuncular haritaya dagilacagindan tick sureleri cok daha dusuk olacaktir.
The 300 bot test is a worst-case scenario — 300 simultaneous connections under 50K NPC + 101K item load. The 20ms `npc_apply` phase spike in slow ticks is the main bottleneck; average tick times remain well below that. Real-world scenarios with distributed players will have significantly lower tick times.

**Karsilastirma / Comparison (300 bot, ayni lokasyon / same location):**

| Emulator | Avg Tick | Max Tick |
|----------|----------|----------|
| Sphere 56x | 50-80ms | 150+ms |
| **SphereNet** | **9.0ms** | **37.6ms** |

---

## Oyun Sistemleri / Game Systems

| Sistem / System | Aciklama / Description |
|---|---|
| **Savas / Combat** | Swing timer, elemental damage, armor resist |
| **Buyu / Magic** | 60+ buyu, interruption, reagent / 60+ spells |
| **Yetenekler / Skills** | 31+ handler, trigger entegrasyonu / integration |
| **Zanaat / Crafting** | Script-based recipe, gump UI |
| **NPC AI** | Monster/Pet/Healer/Guard/Vendor/Animal, A* pathfinding |
| **Konut / Housing** | Multi.mul, decay, co-owner/friend, lockdown |
| **Ticaret / Trade** | Vendor buy/sell, restock, SecureTrade |
| **Lonca & Parti / Guild & Party** | Party chat, guild war/alliance |
| **Ceza / Justice** | Criminal flag, murder count, karma/fame, jail |
| **Hava / Weather** | Yagmur/kar, gun/gece, mevsim / Rain/snow, day/night, seasons |
| **Trigger** | @Login, @Death, @Hit, @Click, @DClick, @Equip ve dahasi / and more |

---

## Hizli Baslangic / Quick Start

```bash
git clone https://github.com/Yunusolcay/sphereNet.git
cd sphereNet
dotnet build
```

`config/sphere.ini` duzenleyin, UO istemci dosyalarini `MULFILES` yoluna koyun, scriptleri `scripts/` altina ekleyin.
Edit `config/sphere.ini`, place UO client files at `MULFILES` path, add scripts under `scripts/`.

```bash
dotnet run --project src/SphereNet.Server              # Windows GUI
dotnet run --project src/SphereNet.Server -- --headless # headless
```

| Port | Amac / Purpose |
|---|---|
| 2593 | UO istemci / client |
| 2594 | Telnet admin |
| 2595 | HTTP durum / status |

---

## Proje Yapisi / Project Structure

```
src/
├── SphereNet.Core/          # Temel tipler, enum / Core types, enums
├── SphereNet.Network/       # UO protokol, TCP, sifreleme / protocol, encryption
├── SphereNet.Scripting/     # Script parser & execution
├── SphereNet.Game/          # Oyun mantigi / Game logic (AI, Combat, Magic, Death, ...)
├── SphereNet.MapData/       # MUL/UOP harita okuyucu / map readers
├── SphereNet.Persistence/   # Save/load
└── SphereNet.Server/        # Giris noktasi / Entry point
```

---

## On Kosullar / Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Ultima Online istemci dosyalari / UO client data files

## Test

```bash
dotnet test
```

---

## Lisans / License

Acik kaynak / Open source — [LICENSE](LICENSE)

## Tesekkurler / Acknowledgments

- [SphereServer Source-X](https://github.com/Sphereserver/Source-X)
- [ServUO](https://github.com/ServUO/ServUO)
- [Ultima Online](https://uo.com/) — Origin Systems / Electronic Arts
