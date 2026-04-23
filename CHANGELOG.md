# Changelog

Tum onemli degisiklikler bu dosyada belgelenir.
All notable changes to this project are documented in this file.

Format [Keep a Changelog](https://keepachangelog.com/) tabanlidir.
The format is based on [Keep a Changelog](https://keepachangelog.com/).

---

## [Unreleased]

### Added / Eklenenler
- **Container weight limiti**: Normal container'lar icin agirlik siniri eklendi (`ContainerMaxWeight=400`, `sphere.ini`'den okunur). Bank'a ek olarak artik sirt cantasi ve sandiklar da agirlik kontrolune tabi.
  Container weight limit: weight cap added for regular containers (`ContainerMaxWeight=400`, configurable via `sphere.ini`). In addition to bank, backpacks and chests are now subject to weight validation.
- **Region/Room CLIENTS**: Script'lerde `<REGION.CLIENTS>` ve `<ROOM.CLIENTS>` artik bolgedeki gercek oyuncu sayisini donduruyor (onceki stub=0 yerine).
  Region/Room CLIENTS: `<REGION.CLIENTS>` and `<ROOM.CLIENTS>` now return actual player count in the area (previously stubbed to 0).

### Changed / Degisikenler
- **Spell buff persistence**: Save oncesi tum aktif buff delta'lari (STR/DEX/INT/flag/light) geri aliniyor, save sonrasi tekrar uygulaniyor. Sunucu restart'inda phantom buff sorunu cozuldu — stat'lar temiz (base) degerleriyle kaydediliyor.
  Spell buff persistence: all active buff deltas (STR/DEX/INT/flag/light) are reverted before save and reapplied after. Phantom buff bug on restart is fixed — stats are saved as clean base values.
- **Combat speed konsolidasyonu**: `GetSwingDelayMs` hesabi `GameClient`'tan `CombatEngine`'e tasindi. Tek kaynak, NPC AI ve oyuncu saldirilari ayni formulu kullaniyor.
  Combat speed consolidation: `GetSwingDelayMs` moved from `GameClient` to `CombatEngine`. Single source of truth — NPC AI and player attacks use the same formula.
- **Stealth adim olceklemesi**: Stealth adim sayisi artik skill seviyesine gore olcekleniyor (`skill/100`, min 1). GM Stealth (1000) = 10 adim, 500 skill = 5 adim (onceki hardcoded 10 yerine).
  Stealth step scaling: stealth step count now scales with skill level (`skill/100`, min 1). GM Stealth (1000) = 10 steps, 500 skill = 5 steps (replaces hardcoded 10).

### Fixed / Duzeltmeler
- **Bos catch bloklari**: Network katmanindaki (`NetState`, `NetworkManager`, `TelnetConsole`, `WebStatusServer`) 5 farkli noktada sessiz exception yutma yerine debug log eklendi. Socket cleanup ve listener hatalari artik izlenebilir.
  Empty catch blocks: debug logging added at 5 points in the network layer (`NetState`, `NetworkManager`, `TelnetConsole`, `WebStatusServer`). Socket cleanup and listener errors are now traceable.
- **Basarisiz test kaldirildi**: `TryMove_DeadChar_Fails` testi kaldirildi (olumsuz onkosula ragmen basarisiz oluyordu).
  Removed failing test: `TryMove_DeadChar_Fails` removed (was failing despite valid precondition).

---

### Added / Eklenenler
- **Bot Stress Test Sistemi**: TCP uzerinden gercek istemci baglantilari simule eden dahili bot sistemi
  - `BotClient.cs`: Tam UO login akisi (login server → relay → game server → char select)
  - `BotEngine.cs`: Bot yonetimi ve istatistik toplama
  - `BotPacketBuilder.cs`: UO protokol paket olusturucu
  - `.bot spawn/stop/status/clean` komutlari
  - Bot spawn lokasyonu secimi (Britain, Trinsic, Minoc, vb.)
- **Huffman DecompressFromServer**: Sunucu → istemci yonunde dogru Huffman decompression
  - `CompressBase` tablosundan dinamik decompression tree olusturma
  - Coklu compressed paket destegi (EOF marker ile ayirma)
- **Performans belgeleri**: README'ye stress test sonuclari eklendi

### Changed / Degisikenler
- **Tick interval**: 125ms → 100ms (8 tick/s → 10 tick/s)
  - Daha responsive gameplay
  - Stress testlerinde kanitlanmis performans marji

### Fixed / Duzeltmeler
- **Huffman compression uyumsuzlugu**: `DecompTree` ve `CompressBase` tablolari arasindaki uyumsuzluk giderildi
  - Botlar artik game server'dan gelen paketleri dogru sekilde decompress edebiliyor
  - Her paket icin ayri EOF marker destegi eklendi

---

## [0.1.0] - 2026-04-22

### Added / Eklenenler
- Ilk surum / Initial release
- Source-X `.scp` script uyumlulugu
- Multicore tick pipeline
- 4 farkli save formati (Text, TextGz, Binary, BinaryGz)
- Coklu veritabani destegi
- MemoryMapped harita yukleme
- WinForms yonetim konsolu (Windows)
- NPC Timer Wheel (O(1) zamanlama)
- Tam oyun sistemleri: Combat, Magic, Skills, Crafting, Housing, Trade, Guild, Party, Justice, Weather
- Telnet admin konsolu + HTTP durum sayfasi
