# Changelog

Tum onemli degisiklikler bu dosyada belgelenir.
All notable changes to this project are documented in this file.

Format [Keep a Changelog](https://keepachangelog.com/) tabanlidir.
The format is based on [Keep a Changelog](https://keepachangelog.com/).

---

## [Unreleased]

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
