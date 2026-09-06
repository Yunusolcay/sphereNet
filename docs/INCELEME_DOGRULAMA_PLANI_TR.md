# SphereNet İnceleme Doğrulama ve Uygulama Planı

Bu dosya, üç inceleme raporunun (`PROJE_GENEL_INCELEME_PLANI_TR.md`,
`HOUSE_SHIP_DEED_SISTEM_INCELEMESI_TR.md`, `PERFORMANS_LOG_INCELEMESI_TR.md` —
üçü de bu dosyaya emildiği için 2026-07-18 doküman temizliğinde silindi; ham
metinleri git geçmişinde) her somut iddiasının **güncel kodla doğrulanmış**
halidir. Açık maddelerin tek yaşayan takipçisi budur. Her madde 3 paralel salt-kod
doğrulama ajanıyla GERÇEK / BAYAT / YANLIŞ / KISMİ olarak sınıflandırıldı; sadece
**GERÇEK** çıkanlar aşağıda aksiyon maddesi olarak listeleniyor.

Sözleşme: bir madde bitince `[x]` işaretle + `(YAPILDI: <kısa kanıt / test>)` ekle.
Line numaraları doğrulama anındaki (2026-07-18) güncel koda göredir; koda dokunulunca
kayabilir — sembol adına güven, satıra değil.

Sonuç: HOUSE_SHIP ve PERFORMANS raporlarındaki **her** kod iddiası GERÇEK çıktı.
PROJE_GENEL'de 1 madde BAYAT (tarım, Wave 270'te düzeltilmiş), 1 madde KISMİ.

---

## ELENEN MADDELER (aksiyon YOK)

- **BAYAT — Tarım büyüme döngüsü.** Rapor "PlantSeed son Crops'u doğrudan kuruyor,
  HarvestPlant 60s REAP_TIME, büyüme evresi yok" diyordu; **Wave 270'te tam büyüme
  zinciri eklendi** (`Item.PlantOnTick/PlantCropReset/PlantStartGrowth/PlantDropFruit`,
  `ClientItemUseHandler.PlantSeed`→`PlantStartGrowth`, `HarvestPlant`→`PlantCropReset`,
  `REAP_TIME` kaldırıldı). Aksiyon yok. (Sulama/toprak hâlâ yok ama Source-X'te de yok.)
- **KISMİ — MapViewSize.** Network view range config-driven değil (client 0xC8, clamp
  4-24), AMA `MapViewSize` combat/witness radar için tüketiliyor (`Character.MapViewRadarTiles`,
  `Program.cs:689`). Tamamen kullanılmıyor değil → sadece "view range config'e bağlı değil"
  kısmı P2 olarak C-grubuna eklendi.

---

## A. Başlangıç bağlantı hataları (P0/P1 — sessiz runtime bug, testlerin yakalamadığı)

Ortak sebep: `Program.EngineWiring` çok uzun ve sıraya duyarlı; motorlar oluşturulmadan
önce bağlanıyor. Öneri: tüm nesneler kurulduktan sonra tek "finalize/wiring" aşaması +
zorunlu bağlantılar için fail-fast doğrulama.

- [x] **A1 (P1) — MovementEngine.SpellEngine null bağlanıyor.** (YAPILDI: atama SpellEngine
  oluşturulduktan sonraya taşındı, EngineWiring.) `Program.EngineWiring.cs:472`
  `_movement.SpellEngine = _spellEngine;` ama `_spellEngine` ancak `:1195`'te oluşturuluyor
  (`Program.cs:92` `null!`). `MovementEngine.cs:150` `SpellEngine?.TryInterruptFromMovement`
  null-conditional → **hareket ederek cast-interrupt gerçek sunucuda hiç çalışmıyor.** Fix:
  atamayı SpellEngine oluşturulduktan sonraya taşı.
- [x] **A2 (P1) — NpcAI gece ışığı bağlantısı komple atlanıyor.** (YAPILDI: `GetLightLevel`
  ataması WeatherEngine oluşturulduktan sonraya taşındı, koşulsuz.) `Program.EngineWiring.cs:1716`
  `if (_weatherEngine != null) _npcAI.GetLightLevel = ...` ama `_weatherEngine` `:2040`'ta
  kuruluyor → guard false → atama **hiç yapılmıyor**. `NpcAI.cs:370` `GetLightLevel?.Invoke()
  ?? 0` → hep 0 → **NPC'ler her zaman gündüz varsayıyor** (gece ışık yak/söndür çalışmıyor).
  Fix: WeatherEngine oluşturulduktan sonra bağla.
- [x] **A3 (P1) — `.SHUTDOWN` / `.BROADCAST` no-op.** (YAPILDI: `_commands.OnShutdownCommand`
  → main-loop `_running=false`, `_commands.OnBroadcastCommand` → tüm in-world client'lara
  SysMessage. NOT: event'ler `CommandHandler`'da, SpeechEngine'de değil.) `SpeechEngine.cs:1127/1137` event
  invoke ediyor ama `OnShutdownCommand`/`OnBroadcastCommand`'a **hiçbir yerde `+=` yok**
  (sadece `OnSaveCommand`/`OnResyncCommand` wire'lı). Komut Admin/GM'de kabul edilip sessizce
  hiçbir şey yapmıyor — operasyonda yanıltıcı. Fix: server-side handler bağla.
- [x] **A4 (P1) — Normal shutdown'da save yok.** (YAPILDI: shutdown bloğu `SaveOnShutdown`
  config'ine göre `PerformSave()` yapıyor, default açık, try/catch. Test:
  ConfigRegressionTests.SphereConfig_SaveOnShutdown_DefaultsOnAndParses.) `Program.Tick.cs:261` "Auto-save on shutdown
  is disabled" logluyor; shutdown bloğu (`:257-283`) save yapmıyor. Planlı kapatmada son
  periyodik save'den sonraki değişiklikler kaybolabilir. Fix: güvenli-varsayılan-açık
  configlenebilir shutdown-save.
- [x] **A5 (P0-fixture) — Composition-root doğrulaması.** (YAPILDI — kapsam kararı: test-fixture
  yerine BOOT-TIME fail-fast seçildi, çünkü test fixture'ı grafiği yine taklit ederdi; boot
  doğrulaması her açılışta GERÇEK production grafiğini kontrol eder. `ValidateEngineWiring()`
  `InitializeGameEngines` sonunda 11 zorunlu hook'u doğruluyor (MovementEngine.SpellEngine,
  NpcAI.GetLightLevel/OnWakeNpc, SpeechEngine OnNpcHear/OnItemHear/OnPlayerSpeech,
  shutdown/broadcast komutları — event'ler için Wired probe'ları eklendi —, TriggerDispatcher,
  ObjBase.ResolveWorld, Item.ResolveShipEngine); eksikte isimli InvalidOperationException ile
  boot reddediliyor. Canlı doğrulama: gerçek boot'ta "Engine wiring validated: 11 mandatory
  hooks connected" logu alındı (2026-07-18).)

---

## B. House / Ship / Deed sistemi (P0 bloklayıcı + P1)

- [x] **B1 (P0 — BLOKLAYICI) — MultiReader sadece 12-byte okuyor, format tespiti yok.**
  (YAPILDI: `MultiReader` artık 12/16-byte'ı auto-detect ediyor — index blok uzunluklarının
  strict-divisibility oyu (608%16=0/608%12=8 → HS), tie'da offset-plausibility tiebreak; HS'de
  trailing `ShipAccess` dword okunuyor (`MultiComponent.ShipAccess` eklendi). `ComponentSize`
  property expose edildi. Test: MultiReaderTests (+3: HS16 detect+bounds+shipAccess, orig12
  detect, ambiguous→plausibility→HS).)
  `MultiReader.cs:14` `ComponentSize = 12`, `:60` `dataLength / 12`, `:64` 12-byte read;
  hiçbir 16-byte/High Seas tespiti yok. Source-X iki formatı da modelliyor + auto-detect
  ediyor (`CUOMultiItemRec.h:28/39`, `CUOInstall.cpp:115`, `CServerMap.cpp:618`). 16-byte HS
  veride component offset'leri kayıyor → `MaxY` şişiyor → `PlaceHouse` (`HousingEngine.cs:567`)
  / `PlaceShip` (`ShipEngine.cs:90`) harita-sınırı kontrolünde **terrain'e bakmadan red** →
  "Cannot place here". Fix: `MultiFormat` (Auto/Original12/HighSeas16) + çok-kayıtlı sağlam
  auto-detect (tiledata boyutu + `%12`/`%16` + bounds plausibility) + `shipAccess` dword.
- [x] **B2 (P1) — `ID=i_deed` type inheritance yok.** (YAPILDI: `ResolveDupeItemInheritance`
  artık DisplayIdRef (`ID=<defname>`) için de child Type==Normal ise base TYPE'ı devralıyor —
  Source-X IBC_ID'nin typed base'i dupe etmesinin (IsDupedItem/DUPELIST) aynası. TYPE-only
  (grafik-only ID= referansı Layer/TData sürüklemesin). Source-X `CItemBase.cpp:1659` +
  aktif pack `i_deed_stone_and_plaster_house` (ID=i_deed, TYPE'sız) doğrulandı. Test:
  SourceXDeedInheritanceTests (inherit + no-over-inherit). Tam suite 1836.) `DefinitionLoader.cs:433-457` ID/DISPID
  ref zinciri sadece `DisplayIdRef`/`DispIndex` set ediyor, `Type`'ı kopyalamıyor
  (`ItemDefHelper.cs:51`, `ItemDef.cs:13` default Normal). DUPEITEM'de type inheritance VAR
  (`:925-953`) ama ID= zincirinde yok → scriptten üretilen deed `ItemType.Normal` kalıp deed
  handler'a hiç girmeyebilir. Fix: ID= zincirinde Type/TDATA/Layer devral.
- [~] **B3 (P1) — `[MULTIDEF]` metadata placement registry'ye merge edilmiyor.** (KISMEN
  YAPILDI: `MultiRegistry.MergeScriptMetadata` eklendi — MULTIDEF resource'larının StoredKeys'ini
  paylaşılan multi id ile geometriye overlay ediyor (NAME/TYPE/BaseStorage/BaseVendors);
  `MultiDef`'e bu alanlar eklendi; EngineWiring LoadFromMapData sonrası çağırıyor; PlaceHouse
  artık `def.BaseStorage`'ı house'a uyguluyor (B10'un storage kısmı da kapandı — 400 default
  yerine script değeri). Test: MultiRegistryMetadataTests (+2: merge, no-geometry skip). KALAN
  (büyük, Aşama 4 ile ortak): COMPONENT (dinamik fixture: kapı/sign/tillerman/plank world-item
  üretimi), MULTIREGION/REGIONFLAGS (script region), TSPEECH — bunlar ayrı geometry/fixture
  ayrımı işi.)
  `MultiRegistry.LoadFromMapData` (`HousingEngine.cs:470`) sadece geometri okuyor;
  `EngineWiring.cs:2308` MULTIDEF'i birleştirmiyor → type/name/component/storage/vendor/
  ship-speed placement'ta kayboluyor. (KISMİ: MULTIDEF script resource olarak TSPEECH/
  COMPONENTCOUNT için okunuyor.) Fix: raw multi ID altında binary geometri + script metadata
  merge.
- [x] **B4 (P1) — Yapısal placement result yok.** (YAPILDI: `PlacementFailure` enum
  (PlayerLimit/AccountLimit/MultiMissing/OutOfMap/LocationBlocked/ScriptVeto); PlaceHouse/
  PlaceShip `out failure` overload'u eklendi (eski 3-arg imza korundu → testler kırılmadı),
  her `return null` bir neden set ediyor; deed handler `PlacementFailureMessage` ile neden-özel
  mesaj gösteriyor. Test: SourceXPlacementResultTests. KALAN (ince): su/eğim/overlap ayrımı
  LocationBlocked altında toplu — CanPlace* içinde ayrıştırmak ayrı iş.) House/Ship motorları hep `null` dönüyor
  (`HousingEngine.cs:559-578`, `ShipEngine.cs:77-95`) → tek genel "Cannot place"
  (`ClientItemUseHandler.cs:1219`). Fix: neden-enum'u (limit/format/su/zemin/overlap/LOS/...)
  + ayrı oyuncu mesajı + structured log.
- [x] **B5 (P1) — WorldSaver item TYPE yazmıyor; house/ship raw BaseId'den kurulamaz.**
  (YAPILDI: WorldSaver artık structure item'lar (Multi/MultiCustom/Ship) için numeric `TYPE`
  yazıyor — BaseId raw multi index'i ITEMDEF'siz olduğu için loader def'ten türetemiyordu →
  restart'ta t_normal olup Housing/Ship DeserializeFromWorld bulamıyordu. Loader zaten `TYPE`
  restore ediyor (`case "TYPE"`) + `MaterializeDefinitionType` restore edileni ezmez (`_type==
  Normal` gate). Gerçek `WorldSaver→dosya→WorldLoader` roundtrip test'i eklendi. Test:
  SaveFormatTests.Roundtrip_PreservesMultiStructureType (Multi+Ship). Geriye-uyum: eski save
  TYPE'sız → MaterializeDefinitionType/legacy yolu korunur.)
  `WorldSaver.cs:535-593` ID/TDATA/MORE yazıyor ama instance `ItemType` (Multi/MultiCustom/
  Ship) yok; load'da raw index (`0x64`) normal ITEMDEF sanılabilir. Gerçek `WorldSaver→dosya→
  WorldLoader` roundtrip testi yok (mevcut testler aynı canlı world'de re-read). Fix: STRUCTURE.
  KIND/MULTIID/MULTIDEF persist + gerçek process-boundary roundtrip testi.
- [x] **B6 (P1) — Raw multi ID `0` redeed'de reddediliyor.** (YAPILDI: `TryParseDeedMultiId`'ye
  `allowZero` parametresi eklendi; `SHIP_MULTI_BASEID` branch'i `allowZero: true` geçiyor —
  explicit ship tag'i 0'ı (small ship north raw index) meşru kabul ediyor, dry-dock deed'i tekrar
  açılabiliyor. Ambiguous More1/BaseId fallback hâlâ 0'ı reddediyor.) `TryParseDeedMultiId`
  (`ClientItemUseHandler.cs:3028` `id != 0`) + fallback (`:3013` `targetId==0` fail) →
  dry-dock'tan üretilen classic small ship deed'i (`SHIP_MULTI_BASEID=0`/`More1=0`) tekrar
  açılamıyor. (İlk scripted `MORE=m_small_ship_n` yolu çalışıyor.) Fix: `0`'ı geçerli değer
  say, "yok" için nullable/explicit result.
- [x] **B7 — Yeniden çerçevelendi (recon) + gerçek bug bulundu.** Üç ajan (Source-X, SphereNet
  akışı, ClassicUO) doğruladı: **Source-X multi'yi TEK id ile modelliyor** (base 0x10000; raw
  multi.mul index = `id−0x10000` türetilir; wire art id = 0x4000-based yalnızca send'de). İki ayrı
  alan (RawMultiId/ClientMultiArtId) Source-X'te YOK → planın önerisi yanlıştı, iki-alan ayrımı
  yapılmadı. SphereNet'in raw index'i zaten Source-X'in multi.mul index'ine denk ve görseli
  component-materialize ediyor; save raw index saklıyor. Kullanıcı kararı: raw depolamayı + materialize'ı
  KORU, save'e dokunma. **Gerçek bug client-send sınırındaydı:** multi-tipi item'lar telde multi olarak
  işaretlenmiyordu — `PacketWorldItemSA` (0xF3) data-type byte'ı hardcoded 0, `PacketWorldItem` (0x1A)
  raw id (<0x4000) gönderiyordu; ClassicUO ikisini de statik tile render ediyor. Sonuç: custom-house
  foundation'ları SA istemcilerinde multi olmadığı için 0xD8 design stream sessizce düşüyor → **custom
  evler render olmuyordu.** FIX (boundary encode): MultiCustom item'ları telde multi işaretle —
  0x1A `graphic|0x4000`, 0xF3 `type=2` (id raw kalır, client `&0x3FFF` ile geri alır). Fixed Multi/Ship'e
  dokunulmadı (component-materialize ile çalışıyor; multi göndermek çift-render yapardı). Save değişmedi.
  Test: MultiWirePacketTests. NOT: uçtan uca custom-house render'ı canlı istemcide teyit edilmeli;
  fixed-multi body'sinin origin'de bıraktığı stray-tile ayrı/ön-mevcut kozmetik konu (kapsam dışı).
- [x] **B8 (P1) — 0x99 preview paketi + Source-X anchor-Y düzeltmesi.** (YAPILDI:
  `PacketTargetMulti` (0x99, Source-X send.cpp:1772 wire formatı; HS 7.0.13+ hue dword'lü 30B,
  classic 26B) + `SetPendingMultiTarget` cursor varyantı; deed handler footprint biliniyorsa
  0x99 kaldırıyor (yOff = rect.bottom = MultiDef.MaxY), cevapta Source-X anchor düzeltmesi
  `y -= (bottom-1)` (CItemMulti.cpp:3288) uygulanıyor. Test: HouseShipLightParityTests.
  PacketTargetMulti_WireFormat. Canlı client'ta ghost-preview görsel teyidi önerilir.)
- [x] **B9 (P1) — Target callback güvenlik yeniden-doğrulaması.** (YAPILDI: cursor cevabında
  Source-X OnTarg_Use_Item + CanUse zinciri — deed silinmemiş + cursor kalkarkenki parent'ında
  ("targ moved" anti-cheat) + non-GM için `CanReachTargetItem` + ölü/donmuş reddi; limit'ler
  zaten PlaceHouse/PlaceShip içinde yeniden kontrol ediliyor.)
- [x] **B10 (P2) — House storage sabit 400.** (YAPILDI — B3 kapsamında: `PlaceHouse` artık
  `def.BaseStorage > 0` ise script değerini uyguluyor (`HousingEngine.cs` "Apply script [MULTIDEF]
  BaseStorage"), `HOUSE.STORAGE` tag'iyle persist ediliyor. Test: MultiRegistryMetadataTests.
  Ev-tipi farkı script pack'in MULTIDEF BaseStorage değerlerinden gelir.)
- [x] **B11 (P2) — 0xF6 ship-move paketi component/yolcu listesi taşıyor.** (YAPILDI:
  `PacketBoatSmoothMove` artık Source-X PacketMoveShip (send.cpp:5402) gibi u16 count +
  {serial,x,y,z} listesi yazıyor (boş listede de count alanı var — eskiden hiç yoktu);
  `OnShipMoved` `ListShipObjects` ile güverte objelerini (component/yolcu/kargo, gemi hariç)
  dolduruyor. Test: HouseShipLightParityTests.PacketBoatSmoothMove_CarriesDeckObjectList.)
- [x] **B12 (P2) — Ship su kontrolü.** (YAPILDI — bir kısmı önceki dalgalarda `CanSailInto`
  ile kapanmıştı (bloklayıcı statik + diğer hull); bu dalga: wet STATIC artık su sayılıyor
  (Source-X GetHeightPoint2 CAN_I_WATER katkısı — statik döşenmiş kıyı/liman suyu yüzülebilir)
  + su hattındaki bloklayıcı dinamik world item'lar hull'u durduruyor. Her iki yol (placement
  `:221` + movement `:1017/:1024`) `CanSailInto`'dan geçiyor. Test:
  HouseShipLightParityTests.ShipWater_WetStaticCountsAsSailable.)
- [x] **B13 (P2) — Custom foundation deed tag'inden algılanıyor.** (YAPILDI: `HousingEngine.
  IsCustomFoundation(multiId)` = MULTIDEF `MultiTypeName=="t_multi_custom"` (B3'ten); deed handler
  `customFoundation = CUSTOMHOUSE tag ∨ IsCustomFoundation(multiId)` → ilk foundation deed'i (tag'siz)
  artık MultiCustom açılıyor. Test: SourceXPlacementResultTests.IsCustomFoundation.)
  `ClientItemUseHandler.cs:1175` sadece `CUSTOMHOUSE` deed tag'i; ilk foundation deed'inde tag
  yoksa custom yerine klasik multi açılıyor. Fix: resolved MULTIDEF `t_multi_custom` type'ından
  belirle.

---

## C. Config sözleşmesi (P1/P2 — okunuyor ama uygulanmıyor)

Öneri: her ayarı sınıflandır (uygulanıyor / alias / metadata / deprecated / bağlantısı-eksik);
desteklenmeyen ayar başlangıç uyarısı üretsin.

- [x] **C1 (P2) — TICKPERIOD/ServerTickMs/README uyuşmazlığı.** (YAPILDI: `<TICKPERIOD>` script
  okuması gerçek `ServerTickMs`'i döndürüyor; `TICKPERIOD` ini legacy alias (ServerTickMs yoksa);
  sphere.ini `TICKPERIOD=250`→`ServerTickMs=100` (efektif değere eşit, sürpriz yok). README
  perf-benchmark paragrafı o ölçümün config'i olabilir → dokunulmadı. Test: ConfigRegressionTests
  alias.) `sphere.ini:516 TICKPERIOD=250`
  (yorum "250ms" diyor, yanlış), runtime `ServerTickMs` (default **100**, `Program.Tick.cs:86`),
  `README-TR.md:145` "**50ms**". `TICKPERIOD` ini→ServerTickMs'e hiç parse edilmiyor; script var
  hardcoded "100" (`Program.Scripting.cs:82`). Fix: tek kanonik isim + alias + doc düzelt.
- [x] **C2 (P1) — GameMinuteLength inert.** (YAPILDI: Program.cs `_world.GameMinuteLengthMs =
  GameMinuteLength*1000`; config default 60→20 (mevcut 20s'e hizalı, default sürpriz yok);
  sphere.ini GAMEMINUTELENGTH=8 artık uygulanıyor.) `SphereConfig.cs:498` okuyor, runtime
  `GameWorld.GameMinuteLengthMs` sabit `20_000` (`:1093`), config akmıyor. sphere.ini'deki değer
  (8sn) uygulanmıyor.
- [x] **C3 (P2) — DistanceWhisper/Talk/Yell hardcoded.** (YAPILDI: SpeechEngine const'ları instance
  property'ye çevrildi, EngineWiring config'ten set ediyor (Say←DistanceTalk); config Yell default
  60→48 (mevcut efektife hizalı).) `SpeechEngine.cs:45` const
  Say=18/Whisper=3/Yell=**48**; config (`:408`, Yell **60**) yok sayılıyor (48≠60 kanıt).
- [x] **C4 (P2) — MaxFame/MaxKarma/MinKarma hardcoded clamp.** (YAPILDI: DeathEngine static config
  alanları (default'lar eşit → sürpriz yok), Program.cs config'ten set; clamp literalleri
  değiştirildi.) `DeathEngine.cs:459` literal
  `0,10000`; config (`:222`) referans edilmiyor (varsayılanlar eşit olduğu için şimdilik zararsız).
- [x] **C5 (P2) — MinCharDeleteTime yok sayılıyor.** (YAPILDI: `Character.CreatedUtcSeconds`
  eklendi (char-create'te damgalanıyor), Source-X `CREATE` anahtarıyla (yaş, tenths —
  CChar::r_Write/r_LoadVal birebir) player'lar için persist; `HandleCharDelete` Source-X
  Setup_Delete gate'i uyguluyor (gün cinsinden config, 0x85 reason 3, Counsel+ bypass,
  damgasız legacy char eski sayılır). Test: CharDeleteAndSpellGateTests (2).)
- [x] **C6 (P2) — UseHttp yok sayılıyor.** (YAPILDI: Program.AdminPanel web status `if (_config.UseHttp)`
  ile gate'lendi; config default false→true (mevcut koşulsuz davranış korunur, UseHttp=0 kapatır).) `SphereConfig.cs:716` okunuyor ama tüketen yok;
  `Program.AdminPanel.cs:175` web status'u koşulsuz `Start()` ediyor.
- [x] **C7 (P2) — MapReadId yerine MapSendId ile MUL okunuyor.** (YAPILDI: `_mapData.InitMap`
  artık `MapReadId` kullanıyor (hangi map*.mul); `_world.InitMap` MapSendId (client id). Default
  ikisi de 0 → değişim yok.) `Program.cs:713/716`
  `InitMap(mapDef.MapSendId,...)`; MapReadId sadece validation'da. İkisi farklıysa yanlış MUL
  okunur.
- [x] **C8 (P2) — MapViewSize network view range'e uygulanmıyor.** (YAPILDI: `NetState.DefaultViewRange`
  static'i eklendi, `ViewRange` init'i ondan; Program.cs `MapViewSize`'tan set ediyor. Client 0xC8
  max clamp'ine (24) dokunulmadı — 24→18 sürprizi olmasın. Test: NetStateViewRangeTests.) (KISMİ maddenin aksiyon
  kısmı) `NetState.ViewRange` default 18, sadece client 0xC8 ile değişiyor (clamp 4-24);
  `MapViewSize`/`MapViewSizeMax` view range'e bağlı değil. (Radar için kullanılıyor, dokunma.)

---

## D. Yarım mekanikler (P2)

- [x] **D1 (P2) — Işık kaynakları yaşam döngüsü.** (YAPILDI — Source-X CItem.cpp:6271 birebir:
  yakışta şarj DÜŞMÜYOR (eski davranış düzeltildi), `Item.OnLightBurnTick` 60sn'de bir şarj
  yakıyor (default 20), sıfırda `LIGHT_BURNED` + `ExtinguishLight` (0x4B8/0x3BE douse sesleri)
  ile `LightOut`'a dönüyor, burned kaynak bir daha yakılamıyor, `ATTR_MOVE_NEVER/STATIC`
  sonsuz yanıyor; kalan süre mevcut TIMER persist'iyle restart'ı atlatıyor. Yakma noktaları
  (player use + NPC gece ışığı) timer kuruyor. Test: HouseShipLightParityTests (2). NOT:
  Source-X'in lit/out grafik çifti swap'ı yapılmadı — SphereNet type-flip konvansiyonu korundu.)
- [x] **D2 (P2) — Spell school'ları: minimal sözleşme kapatıldı.** (YAPILDI:
  `SpellEngine.IsInertSchoolSpell` — 201..999 aralığında olup HİÇBİR davranış taşımayan
  (flag yok, effect/duration eğrisi yok, `HasScriptedStages` yok) spell CastStart'ta
  "not supported yet" ile reddediliyor; mana/reagent yanmıyor. Script'li spell'ler
  (flag/eğri/ON= stage → `ResourceLink.HasAnyTriggerBody` üzerinden `SpellDef.HasScriptedStages`)
  etkilenmez. School'ların GERÇEK implementasyonu ayrı büyük proje olarak PARITY.md
  "Deferred tail"de durmaya devam ediyor. Test: CharDeleteAndSpellGateTests.InertSchoolSpell_Classification.)

---

## E. Performans (P0/P1/P2 — hepsi kod düzeyinde GERÇEK)

Not: canlı log analizi ayrıca host/scheduler baskısına da işaret ediyor (yield/net_in
gecikmeleri); aşağıdakiler onu **büyüten** kesin kod borçları.

- [x] **E1 (P0 — YÜKSEK etki / DÜŞÜK efor, EN UCUZ KAZANIM) — StateRecorder her tick full
  `ToArray`.** (YAPILDI: `Tick` imzası `Func<IEnumerable<Character>>` lazy provider'a çevrildi;
  roster SADECE move-scan/snapshot due olduğunda (2s/15s) materialize ediliyor. Caller
  `GetAllObjects().OfType<Character>()` yerine char-only `GetAllCharactersSnapshot` metod
  grubunu geçiyor. Idle tick'te sıfır tahsis. Test: StateRecorderTests.
  Tick_InvokesRosterProviderOnlyWhenScanIsDue.) `Program.Tick.cs:566/788` `_stateRecorder?.Tick(..., _world.GetAllObjects()
  .OfType<Character>())`; `GetAllObjects()` (`GameWorld.cs:1468`) `_objects.Values.ToArray()`
  — argüman interval kontrolünden ÖNCE değerlendiği için ~52K obje dizisi **her tick** (10×/sn,
  ~4MB/s) kopyalanıyor, recorder içinde 2s/15s'de bir tarasa bile. Fix: interval kontrolünü öne
  al **veya** mevcut `GetAllCharactersSnapshot()` (`:1473`) / players-only kullan.
- [x] **E2 (P0) — Save background'a taşındı.** (YAPILDI: `WorldSaver` zaten immutable
  `SaveRecord` snapshot mimarisine sahipti — `Prepare(world)` (main-thread, tek dünya-okuyan faz;
  spheredata içeriği de string'e render ediliyor) / `WritePrepared` (herhangi bir thread:
  shard+encode+yazma+atomik commit) olarak ikiye bölündü. `SAVEBACKGROUND>0` → PerformSave
  capture'dan sonra `Task.Run(WritePrepared)`; tamamlanma yan etkileri (saveCount/hook/broadcast)
  ana döngüde `CompleteBackgroundSave` poll'üyle; üst üste save atlanır; shutdown
  `WaitForBackgroundSave` ile ucuştakini bekler. `SAVEBACKGROUND=0` eski senkron yol.
  `SAVESECTORSPERTICK/SAVESTEPMAXCOMPLEXITY` bilinçli no-op olarak belgelendi (Source-X'in
  tick-başına-aşama modeli yerine snapshot+worker seçildi; Source-X'te de ayrı thread YOK,
  recon doğruladı). Suite yeşil.)
- [x] **E3 (P1 — MED-YÜKSEK / MED) — TIMERF her tick full-world scan.** (YAPILDI: `_objectsWithTimerF`
  active-set — `ObjBase.AddTimerF` mevcut `ResolveWorld` üzerinden `GameWorld.TrackTimerFObject`'e
  kaydediyor (YENİ static YOK; tüm ObjBase world erişimiyle aynı resolver → cross-world riski yok).
  `TickTimerF` artık sadece timer taşıyan objeleri (küçük set) geziyor, tick sırasında boş/silinmiş
  olanları prune ediyor; `DeleteObject` set'ten çıkarıyor. Due-time heap yerine active-set seçildi —
  TIMERF nadir, set küçük, invalidation tek funnel (AddTimerF). Test: PerfIndexTests (ground + contained
  item fire-once + prune; contained case sector-only index'in kaçıracağını kanıtlıyor).)
- [x] **E4 (P2 — MED / MED) — Decay catch-up 5sn'de full-world scan.** (YAPILDI: `_groundItems`
  superset index'i — tek `sector.AddItem` choke point'inden (`PlaceItem`) besleniyor, bu yüzden her
  ground item garantili içeride (load dahil). `CollectExpiredGroundItems` artık `_objects.Values`
  yerine bu set'i geziyor; alınıp cebe konan item'lar (`IsOnGround==false`) tarama sırasında lazy
  prune ediliyor — DecayTime'ın 22 call-site'ına hook GEREKMEDİ. Maliyet obje sayısıyla değil loose
  ground item sayısıyla ölçekleniyor. Test: PerfIndexTests (expired-only collect + picked-up prune).)
- [x] **E5 (P1 — MED / MED) — Sleeping-sector maintenance 3dk'da tek tick'te toplu.** (YAPILDI:
  `TickSleepingSectorItems` (tüm dünyayı tek tick'te gezip her uygun sektöre `OnMaintenanceTick`)
  → `TickSleepingMaintenance(currentTime)` ile değiştirildi: interval bir sweep'i **arm** ediyor,
  sonra her tick **resume cursor**'dan (mapIdx + x + y) sınırlı bir dilim drain ediliyor; tüm grid
  bir kez gezilince sweep idle'a geçip bir sonraki interval'i bekliyor. Per-tick maliyet iki bütçeyle
  sınırlı: `MaintenanceCallsPerTick` (pahalı maintenance tick'i, default 256) + `MaintenanceExaminePerTick`
  (ucuz hücre ziyareti, default 4096). Cadence arm-time'dan ölçülüyor → drain süresi zamanlamayı
  kaydırmıyor. İki call-site (`OnTick` + `OnTickParallel`) de tek metoda indirgendi. Test:
  SleepingMaintenanceBudgetTests — bütçe=1'de iş K tick'e yayılıyor + her uygun sektör tam bir kez
  ziyaret ediliyor; bütçe bol olunca tek tick'te bitiyor.)
- [x] **E6 (P1) — Network bütçeleri.** (YAPILDI: (1) `MaxAcceptsPerPass=32` accept bütçesi —
  fazlası kernel backlog'da bir sonraki pass'i bekler; (2) IP limiti 1100-slot tarama yerine
  Init/Clear'da bakımlı `_ipTally` sayacı; (3) login/unknown bağlantılar da artık non-blocking
  batched send kullanıyor (soket Init'te non-blocking; WouldBlock'ta kalan bytes batch buffer'da
  taşınır, backpressure cap aynı) — zero-window login client flush pass'ini bloklayamaz.
  `NETWORKTHREADS`/`USEASYNCNETWORK` bilinçli no-op olarak SphereConfig'te belgelendi
  (main-loop network tasarımı). Suite yeşil.)
- [x] **E7 (P2) — Auto worker = ProcessorCount.** (YAPILDI: auto default artık
  `max(1, ProcessorCount-1)` — hem `RunMulticoreTick` hem `GameWorld.OnTickParallel`;
  açık `MulticoreWorkerCount` yine kazanır.)
- [x] **E8 (P1 — telemetri) — `snapshot` yanlış-etiket + apply↔flush arası ölçülmeyen işler.**
  (YAPILDI: `world_tick` alt-fazı (OnTickParallel ayrı ölçülüyor; dominant hesabında `snapshot`
  artık grubun kalanı) + `post_apply` fazı (replay/StateRecorder/macro/wheel-reschedule bloğu).
  slow_tick satırı ve /status Telemetry'ye `world_tick`/`post_apply` eklendi. NOT: GameWorld-içi
  daha ince kırılım (timerf_scan/sleeping_maintenance) istenirse ayrı iş.)

---

## F. Atıl altyapı temizliği (P2/P3)

> **Batch 5 kararı (doğrulama sonrası):** F grubu körlemesine "sil" değil — her biri referans
> sayımı + niyet analiziyle incelendi. Gerçekten **redundant/divergence riski** olanlar silindi
> (F3, F4); **kasıtlı ama henüz bağlanmamış** (doğru + testli) scaffolding ise silinmeyip doğru
> sınıflandırıldı (F1, F2, F5) — parity-testli/tutarlı kodu atmak bu projenin "doğrula, uydurma"
> ilkesine aykırı olurdu. Git geçmişi silinenleri korur.

- [~] **F1 (P3) — BotPerformanceGate CI'a bağlı değil → KASITLI (tutuldu).** Doğrulama: bot
  diagnostics alt sistemi CANLI (`BotEngine`, `TickHistogram`, `Program.cs`/`Program.Tick.cs`
  kullanıyor); yalnızca `BotPerformanceGate` (CI-gate eşik değerlendirici) henüz bot-report çıktısına
  ve CI exit-code'una bağlı değil. Doğru + tutarlı bir parça, eksik olan tek şey CI hookup'ı (kapsam
  dışı). Kaldırılmadı; STUB_INVENTORY'de "kasıtlı-bağlanmamış" olarak işaretlendi.
- [~] **F2 (P3) — Fast-walk stack paketleri → KASITLI (tutuldu).** `PacketFastWalkStackInit/Push`
  hiç construct/send edilmiyor AMA `DeferredParityTests.PacketFastWalkStackInit_BuildsExpectedPayload`
  ile wire-format PARITY TESTİ var — kasıtlı, doğrulanmış protokol scaffolding'i. SphereNet zaman
  tabanlı hareket kısıtı kullanıyor; bu paketler ileride era-uyumlu key rotasyonu için hazır. Testli
  kod silinmedi; STUB_INVENTORY'de belgelendi.
- [x] **F3 (P3) — ExpansionInfo tablosu → SİLİNDİ.** `ExpansionInfo.GetInfo` hiç çağrılmıyordu;
  feature mask'leri `GameClient.Login`/`NetState` bağımsız kuruyor. İkinci (divergence riskli) doğruluk
  kaynağıydı → `ExpansionInfo.cs` kaldırıldı. `Expansion`/`FeatureFlags` enum'ları (başka yerde
  kullanılıyor) korundu.
- [x] **F4 (P3) — ExpressionGlobals / ConditionalEvaluator → SİLİNDİ.** İkisi de hiç instantiate/call
  edilmeyen Source-X-şekilli redundant kabuk: gerçek fonksiyon zaten canlı motorda (global VAR/list
  state VarMap/ListMap'te, koşul değerlendirme `ExpressionParser.EvaluateConditional`'da). Bağlanırsa
  ikinci global-state/expression sistemi olurlardı (review'ın uyardığı divergence) → her iki dosya da
  kaldırıldı.
- [~] **F5 (P3) — OnSpeedHackDetected tüketicisi yok → KASITLI (tutuldu).** Doğrulama: speedhack
  ALGILAMA aktif (`ClientCombatHandler:362-379` — verdict, LogWarning, Kick→MarkClosing hepsi çalışıyor).
  `OnSpeedHackDetected` event'i operatörler için deliberate bir extensibility/audit hook'u (yorumu bunu
  açıkça belirtiyor); güvenlik açığı değil, kick yolu bağımsız. Gözlemlenebilirlik zaten log'da mevcut.
  API'den çıkarmak yerine STUB_INVENTORY'de "kasıtlı extensibility hook" olarak belgelendi.

---

## G. Test & doküman boşlukları (P2)

- [x] **G1 (P2) — Üç map diagnostic testi `return` ile "fake pass".** (YAPILDI: xUnit v2'de
  dynamic `Assert.Skip` yok → 3 diagnostic `[Fact]`'e static `[Fact(Skip=...)]` eklendi; artık
  gerçek **Skipped** raporlanıyor (suite 1838 geçti + 3 atlandı, eskiden 3 fake-pass). Local
  çalıştırmak için Skip kaldırılır.)
  `StairThrowDiagnosticTests.cs:29/111/309` `C:\mortechUO\mul` yoksa `WriteLine("SKIP")` + `return`
  → xUnit'te erken return = **passed** (skipped değil). "0 skipped" üç çalışmayan testi gizliyor.
  Fix: gerçek `Skip` (Assert.Skip / SkippableFact) + "Skips cleanly" yorumunu düzelt.
- [x] **G2 (P2) — Trigger dokümanları bayat (NPCSeeWantItem).** (YAPILDI: TRIGGERS.md,
  STUB_INVENTORY_TR.md ve o günkü PARITY_MATRIX.md — sonradan PARITY.md'ye birleştirildi —
  güncellendi; `NPCSeeWantItem` artık ateşleniyor (EngineWiring); güncel tek not-fired char
  trigger `UserVirtue` (virtue-gump, gump yok).) Trigger artık wire'lı+ateşleniyor
  (`Program.EngineWiring.cs:1660/1681`); bayat gösteren doc satırları düzeltildi.

---

## KAPANIŞ (2026-07-18, dalga 3)

Plan kapandı: A(5/5) + B(13/13) + C(8/8) + D(2/2) + E(8/8) + F(5/5) + G(2/2).
Dört madde `[~]`: F1/F2/F5 bilinçli olarak tutuldu (aksiyon yok), **B3'ün ise kalan
işi var** (COMPONENT dinamik fixture, MULTIREGION/REGIONFLAGS, TSPEECH) — Aşama 4
ile ortak, bu satır onu kapalı saymaz.
Son dalga (13 madde, 3 fazda): E7/E8/B10/C5/D2 → B8/B9/B11/B12/D1 → E2/E6/A5.
Her madde: Source-X recon → en küçük kök-neden fixi → build + tam suite (1875 yeşil / 3 skip)
→ çift changelog → commit. A5'in boot doğrulaması gerçek sunucu açılışında teyit edildi
("Engine wiring validated: 11 mandatory hooks connected"). Canlıda izlenecekler:
SAVEBACKGROUND>0 ile save stall'ının capture-süresine inmesi, 0x99 ghost-preview'un
client'ta görünmesi, ışık kaynaklarının 20 dakikada sönmesi.

## Önerilen sıra

1. **A1–A4** (başlangıç bağlantı bug'ları + shutdown-save) — sessiz, testsiz, düşük efor, yüksek
   güven kazanımı. **A5** (composition-root fixture) bunları kalıcı kılar.
2. **E1** (StateRecorder ToArray) — tek satırlık mantık, yüksek etki. **B1** (MultiReader 12/16) —
   house/ship'i modern veride açan bloklayıcı.
3. **E2** (save main-loop dışına) + **E3/E5/E6** (full-world scan / maintenance / network bütçe).
4. **C-grubu** config sözleşmesi (uygulanmayan ayarları bağla/işaretle).
5. **B2–B9** house/ship parity (deed inheritance, MULTIDEF, structured result, save TYPE, raw-0).
6. **D1** ışık yaşam döngüsü; **B10–B13** house/ship P2.
7. **F/G** atıl altyapı temizliği + test/doküman düzeltmeleri.
8. **D2** spell school'ları — ayrı büyük proje (PARITY_BACKLOG ile ortak).

## SX — Kategorili Source-X incelemesi (6 Eylül 2026)

Bu ek bölüm yukarıdaki tarihsel incelemeden ayrıdır; burada üç ajanla doğrulama
yapılmadı. SphereNet `da5972ca`, yerel Source-X `92ced0ba` üzerinde kaynak
karşılaştırması ve izole çalışma senaryoları kullanıldı. Çözüm testleri: 2333/2333.
[Kategori planı](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_KARSILASTIRMA_PLANI.md) ve
[01A kanıt raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_01A_TICARET_OLUM.md)
tarihli kapsam/kanıt kayıtlarıdır; aşağıdaki kutular yaşayan düzeltme durumudur.

- [x] **SX-01A-02 (P1)** — Sahipli satıcıdan dolu çanta tam alımında özgün nesne
  aktarılmalı. (YAPILDI: `ProcessBuy` artık `vendor.OwnerSerial.IsValid` ile Source-X'in
  oyuncu/NPC satıcı dallarını ayırıyor; oyuncu satıcısında tam alım stok nesnesini
  kabından çıkarıp alıcıya veriyor — UID, çocuk nesneler ve tag'ler korunuyor. NPC
  satıcı sanal şablon kolu klonlamaya devam ediyor. Test:
  VendorTransferParityTests.BuyingAFullBagFromAPlayerVendorKeepsItsContentsAndUid,
  APartialBuyFromAPlayerVendorStillLeavesTheRemainder,
  AnNpcVendorStillSellsFromItsVirtualTemplate.)
- [x] **SX-01A-05 (P1)** — İmleçteki düz Blessed eşya. (YAPILDI: SX-01A-04 ile tek
  düzeltme — aşağıya bakın. NOT: bu fark `da5972c` ile açıldı; öncesinde sürüklenen
  eşya hiç aktarılmıyordu ve düz Blessed yanlışlıkla korunuyordu.)
- [x] **SX-01A-01 (P2)** — NPC satıcısında istiflenemeyen çoklu alım. (YAPILDI:
  teslim `stock.IsStackable`'a bakıyor; istiflenemeyen miktar ayrı Amount=1
  nesnelere bölünüyor, her biri aynı taşıma/bırakma yolundan geçiyor. Test:
  VendorTransferParityTests.BuyingThreeNonStackableItemsDeliversThreeObjects,
  BuyingThreeStackableItemsStillDeliversOnePile.)
- [x] **SX-01A-03 (P2)** — Sahipli satıcının aldığı malı VendorExtra'ya aktar.
  (YAPILDI: `ProcessSell` sahipli satıcıda tam satışı özgün nesne olarak
  `Layer.VendorExtra`'ya taşıyor, kısmi satışta satılan miktarın kopyasını koyuyor;
  sahipsiz NPC silmeye devam ediyor. **Raporda olmayan bloklayıcı da giderildi:**
  `WorldSaver` LAYER 27'yi de sanal stok sayıp kayıttan hariç tutuyordu, yani
  transfer tek başına eklenseydi mal her restart'ta buharlaşacaktı — hariç tutma
  yalnız LAYER 26'ya daraltıldı. Test:
  VendorTransferParityTests.SellingToAPlayerVendorMovesTheObjectIntoItsExtraStore,
  SellingToAnOwnerlessNpcVendorStillDestroysTheGoods,
  APlayerVendorsBoughtGoodsSurviveASaveAndLoad.)
- [x] **SX-01A-04 (P2)** — Ölümde korunan ekipmanı çantaya taşı; aktarım sırasını
  eşleştir. (YAPILDI: `DropLootToCorpse` artık Source-X `DropAll` sırasını izliyor —
  **önce çanta, sonra ekipman**; korunan ekipman `KeepWithOwner` ile çantaya
  gidiyor ve çanta zaten boşaltıldığı için orada kalıyor. Sürüklenen eşya da aynı
  geçişte ekipman maskesiyle değerlendiriliyor, bu SX-01A-05'i kapatıyor. Cursor-only
  iptal için `Character.OnDragCancel` eklendi. Test: DeathInventoryEdgeTests
  (AProtectedItemOnTheCursorStaysWithTheOwner artık Newbie/Blessed/Nodropt/NotRading
  teorisi, ProtectedEquipmentIsPackedRatherThanLeftWorn,
  ProtectedEquipmentSurvivesTheEmptiedPack).)
- [x] **SX-01A-06 (P2)** — @Buy/@Sell N2 satır toplamı + @Buy LOCAL.TOTALCOST.
  (YAPILDI: `FilterVendorEntriesByTrigger` N2'ye `amount * price` koyuyor, @Buy'da
  `LOCAL.TOTALCOST` ayakta kalan satırların toplamını taşıyor ve veto edilen satır
  toplamdan düşülüyor. **Bilinçli sapma:** Source-X ARGN2'yi İSTEMCİNİN gönderdiği
  fiyattan kurar; SphereNet işlemin gerçekten tahsil edeceği sunucu fiyatını
  kullanır — parite turunda geri "düzeltilmemeli", kod yorumunda da yazılı. Test:
  GameSystemTests.GameClient_VendorBuyTrigger_PassesLineTotalAndTotalCost,
  GameClient_VendorBuyTrigger_VetoedLineLeavesTheRunningTotal.)

**01A kapanışı (6 Eylül 2026):** altı bulgunun tamamı uygulandı; tam suite
**2.348 başarılı / 0 başarısız**. Sözleşme değişikliği nedeniyle güncellenen mevcut
testler: DeathCorpseParityTests (korunan ekipman artık çantada, giyili değil),
VendorStableParityTests + VendorPacketRoundtripTests (istiflenemeyen çoklu alım
artık ayrı Amount=1 nesneler).

### 01B — kaldirma, birakma, kaplar ve kusanma (6 Eylül 2026)

[01B kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_01B_ENVANTER.md).
Alti bulgunun tamami kodda dogrulandi ve uygulandi.

- [x] **SX-01B-01 (P1)** — Tek surukleme kurali. (YAPILDI: `ResolvePreviousDrag`
  yeni pickup'tan once imlectekini lift-origin'ine geri birakiyor; ayni UID'nin
  tekrar kaldirilmasi Source-X ItemPickup gibi erken donuyor ve ilk origin
  korunuyor. Test: InventoryTransferParityTests.ASecondPickupSettlesTheFirstItem...,
  PickingUpTheSameItemTwiceKeepsTheOriginalDrag.)
- [x] **SX-01B-02 (P1)** — Kap olmayan hedef. (YAPILDI: `IsDropTargetContainer` +
  `RedirectNonContainerTarget`; duz bir esyaya birakma, Source-X
  CClientEvent.cpp:504 gibi o esyanin bulundugu kaba veya zemin karesine
  yonlendiriliyor. Stack birlesmesi ve gercek kap yollari degismedi. Test:
  DroppingOntoAPlainItemDoesNotMakeItAContainer, DroppingOntoARealContainerStillInserts.)
- [x] **SX-01B-03 (P2)** — Bankaya giren cantanin cocuklari. (YAPILDI: kapasite
  kontrolu gelen kabin derin sayisini ekliyor. **Rapordan sapma:** Source-X'te bu
  sayim yalnizca IT_EQ_BANK_BOX koluna ozgu (CItemContainer.cpp:941), bu yuzden
  normal kaplara uygulanmadi — yoksa dolu bir canta bos bir cantaya konamazdi.
  Test: AFullBagCannotWalkPastTheBankItemLimit, AnEmptyBagStillFitsInABankWithRoom.)
- [x] **SX-01B-04 (P1)** — Baska oyuncunun ic cantasi. (YAPILDI: layer'a gore parcali
  kontrol yerine hedef zincirinin KOKU cozuluyor; kok baska bir karakterse ve o
  karakter oyuncunun peti degilse birakma reddediliyor. Test:
  AnItemCannotBePushedIntoAnotherPlayersNestedBag, AnItemCanStillBePutIntoYourOwnPetsPack.)
- [x] **SX-01B-05 (P1)** — Acilmamis kaptan pickup. (YAPILDI: `OpenedContainerRegistry`
  eklendi — Source-X CClient::m_openedContainers karsiligi; SendOpenContainer kaydi
  aciyor, pickup dogruluyor. Kayit kabin acildigi andaki ust-nesnesini ve konumunu
  tutuyor, boylece yer/sahip degistiren kap artik acik sayilmiyor. Oyuncunun kendi
  cantasi/bankasi acik kayit gerektirmiyor (Source-X'te de ust-nesne karakterse
  gecerli). Test: AnItemCannotBeLiftedFromAContainerThatWasNeverOpened,
  AnItemCanBeLiftedOnceTheContainerHasBeenOpened, TheOwnBackpackNeverNeedsAnExplicitOpen,
  AnOpenedContainerThatMovesAwayStopsCounting.)
- [x] **SX-01B-06 (P2)** — Kusanma catismasi lanetli esyayi cikariyor. (YAPILDI:
  `Character.CanEquip` artik dolu layer'daki mevcut esyanin tasinabilirligini
  `ItemMoveRules.CanMove` ile denetliyor ve hicbir mutasyon yapmadan reddediyor
  (Source-X CanEquipLayer, CCharStatus.cpp:470); yeni `EquipDenial.LayerBlocked`.
  Script/loader Equip cagrilarinin zorunlu aktarim semantigi degismedi. Test:
  EquippingOverACursedItemIsRefused, SwappingTwoOrdinaryWeaponsStillWorks.)

**01B kapanisi:** tam suite **2.362 basarili / 0 basarisiz** (+14). Mevcut testlerde
sozlesme degisikligi gerekmedi.

**Kalan (rapor "Devam edecek kontroller"):** pickup miktar normalizasyonu ve trigger
argumanlari, imlec origin bilgisinin harita/teleport sonrasi gecerliligi, nested bank
weight/override kurallari, stack overflow parent limitleri, equip layer'in gercek
itemdef ile eslesmesi. Bunlar 01B'de dogrulanmadi.

### 02 + 02B — guvenli ticaret (6 Eylul 2026)

[02 kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_02_GUVENLI_TICARET.md) ·
[02B kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_02B_KAYIT_SCRIPT.md).
Yedi bulgunun tamami dogrulandi ve uygulandi.

**Kok neden (yedi bulgunun besini birden besliyordu):** trade penceresinin kimligi
yoktu. `InitiateTrade` iki jenerik `Container` yaratiyordu — sahipsiz, konumsuz,
kalici oturum kaydi yok. Source-X `Cmd_SecureTrade` (CClientUse.cpp:1414/1420)
her pencereyi `IT_EQ_TRADE_WINDOW` olarak `LAYER_SPECIAL`'da karaktere kusandirir.
`CreateTradeContainer` artik ayni seyi yapiyor; `ItemType.EqTradeWindow` ve
`Layer.Special` zaten tanimliydi, sadece kullanilmiyordu.

- [x] **SX-02-01 (P1)** — Onay toggle'i. (YAPILDI: `ToggleAccept` → `SetAccept(bool)`;
  paketteki deger ataniyor ve `false` iki tarafi birden temizliyor (Source-X
  Trade_Status, CItemContainer.cpp:144). **Raporda olmayan ek:** Source-X
  receive.cpp:1132 gonderenin KENDI kabini kullandigini da denetler — bu olmadan
  acik-durum atamasi, partnerin bayragini set etmeye izin veren tek tarafli
  tamamlama acigina donusurdu; kontrol eklendi.)
- [x] **SX-02-02 (P2→P1)** — Kendi teklifini geri alamama. (YAPILDI: kok neden
  yukarida; pencere artik sahibine bagli oldugu icin `CanReachInsideContainer`
  kendi penceresini geciriyor, partnerinkini reddediyor. **Bu, `6804d29`'da benim
  actigim regresyondu** — 01B kap erisim kapisi trade penceresini hesaba
  katmiyordu.)
- [x] **SX-02-03 (P1)** — Baslatilamayan ticaret esyayi ortada birakiyor. (YAPILDI:
  `InitiateTrade` artik `bool` donuyor; her erken rette esya lift-origin'ine geri
  birakiliyor ve istemciye `PacketDropAck` yerine reject gidiyor — Source-X
  Event_Item_Drop_Fail, CClientEvent.cpp:325.)
- [x] **SX-02-04 (P1)** — Icerik degisikligi onayi bozmuyor. (YAPILDI: sifirlama
  handler'lardan alinip kap mutasyonuna tasindi — `Item.OnTradeWindowChanged`
  hem `TryAddItem` hem `RemoveItem` icinde tetikleniyor. **Rapora duzeltme:**
  Source-X hem ContentAdd (CItemContainer.cpp:557) hem OnRemoveObj (:798) icinde
  sifirlar; mevcut drop-uzerine sifirlama zaten dogru pariteydi, eksik olan
  cikarma yariydi.)
- [x] **SX-02B-01 (P1)** — Kayittan yuklemede sahipsiz trade kabi. (YAPILDI: yapisal
  yari yukaridaki sahiplik; kurtarma yarisi `RecoverInterruptedTrades` — yuklemeden
  hemen sonra her trade penceresi sahibine bosaltilip kaldiriliyor (Source-X
  CItem::IsWeird → ItemBounce, CItem.cpp:1005). **Rapora duzeltme:** "kaplari
  kayittan haric tut" da "oturumu persist et" de Source-X'in yaptigi degil; referans
  kabi kusanik olarak persist edip yuklemede kendini onariyor. **Raporda olmayan
  tehlike:** `WorldLoader` CONT'suz esyayi kayitli konumuna YERLESTIRIR — yani eski
  davranista pencere, icindeki gercek oyuncu mali ile birlikte (0,0) karesinde
  acilabilir bir dunya kabi olarak geri geliyordu.)
- [x] **SX-02B-02 (P2)** — Cevrimdisi oyuncuyla ticaret. (YAPILDI: `partner.IsOnline`
  kapisi (Source-X CClientUse.cpp:1338 "and also offline players"). Reddedilen
  baslangic SX-02-03'un iade yolunu kullaniyor. NOT: Source-X ayrica karsilikli
  `CanSee` ister; SphereNet'te harita+mesafe var, LOS yok — bu tur eklenmedi,
  02C'ye birakildi.)
- [x] **SX-02B-03 (P2)** — Script vetosu tum ticareti iptal ediyor. (YAPILDI: veto
  artik yalnizca bu degisimi reddediyor, `CancelTrade` cagrilmiyor; pencere ve iki
  teklif yerinde kaliyor (Source-X CItemContainer.cpp:189 duz `return`).
  **Bilincli karar:** Source-X bu dalda iki onay isaretini de SET birakir, biz de
  oyle biraktik — temizlemek farkli bir sozlesme olurdu, kod yorumunda yazili.)

**02 kapanisi:** tam suite **2.374 basarili / 0 basarisiz** (+12). Sozlesme
degisikligi nedeniyle guncellenen mevcut testler: GameSystemTests (ToggleAccept →
SetAccept, `param=0` → `param=1`), TradeSafetyTests (ayni), ve alti `InitiateTrade`
cagri yerinde partnere `IsOnline = true`.

**Kalan (02C):** uzaklasma/harita degisimi, karsilikli CanSee, @TradeAccepted
numarali nesne argumanlari (Source-X m_VarObjs), veto sonrasi tekrar onay akisi,
ticaret surerken save/load'in canli oturumla etkilesimi. Ayrica kapasite
davranisi bilincli tasarim farki olarak birakildi: SphereNet `CanAcceptTradeItems`
ile on-kontrol yapar, Source-X kabulden sonra ItemBounce ile teslim eder.

### 03A — dovus ilk taramasi (6 Eylul 2026)

[03A kanit raporu](D:/Projeler/Yunus/sphereNet/SOURCE_X_BOLUM_03A_DOVUS.md).
Uc bulgunun tamami dogrulandi ve uygulandi; ucu de raporun anlattigindan genisti.

- [x] **SX-03A-02 (P1)** — Yansitilan hasar Invul korumasini atliyor. (YAPILDI:
  `CombatEngine.ApplyReflectedDamage` tek giris; uc yansima dali da (Blood Oath,
  Reactive Armor, REFLECTPHYSICALDAM) buradan geciyor. **Rapora duzeltme:**
  SphereNet'in "fixed hasar recurse edemez" yorumu Source-X'i yanlis okuyordu —
  DAMAGE_FIXED Invul'u atlamaz; recursion'u DAMAGE_REACTIVE engeller
  (CCharFight.cpp:1015), Invul kapisi girişte durur (:642). **Raporda olmayan
  ek dallar:** `SpellEngine` Reactive Armor yansimasi ve `CharacterPoisonState`
  zehir tikleri de bagisikligi hic kontrol etmiyordu; ikisi de kapatildi.
  NOT: `IsDamageImmune` yalnizca Invul+God kapsiyor; Source-X ayrica STATF_STONE,
  CAN_C_FIRE_IMMUNE ve SAFE bolge bayraklarini da kontrol eder — genisletilmedi,
  03B adayi.)
- [x] **SX-03A-01 (P2)** — Mühimmat aramasi kilitli alt kaplara iniyor. (YAPILDI:
  `FindAmmoInContainerCore` artik `IsSearchableContainer` ile iniyor. **Raporda
  olmayan ek:** ayni hata `CraftingEngine`'de sekiz ayri gezicide daha vardi
  (CountInContainerByType, HasItemOfTypeIn, FindItemOfTypeIn, FindInContainerByType,
  FindInContainer, CountInContainer, FindResourceItemByHue, CollectResourceHues) —
  crafting kilitli sandiktan malzeme tuketebiliyordu; hepsi kapatildi.
  **Bilincli istisna:** altin sayimi (`TradeEngine.EnumerateContainerContentsRecursive`)
  DOKUNULMADI — Source-X `ContentConsume` altin icin yalnizca IT_CONTAINER_LOCKED'i
  disliyor, tam IsSearchable kumesini degil (CContainer.cpp:443); oraya bu predikati
  uygulamak yeni bir sapma olurdu.)
- [x] **SX-03A-03 (P2)** — Vurus proc'lari ana hasardan once. (YAPILDI:
  `ApplyAosOnHitEffects` hasar uygulamasindan SONRAYA alindi (Source-X Fight_Hit:
  OnTakeDamage :2259, proc'lar :2270). **Rapora ek — asil zarar gozlemlenen HP
  degil:** proc oldurdugunde silahin kendi hasari hic islenmiyordu ve onun icin
  `RecordAttack` calismiyordu, yani oldurme kredisi, murder count ve loot hakki
  savrulan darbeden proc'a kayiyordu. **Tehlike:** proc cagrisi bagisiklik blogunun
  DISINDA birakildi; Source-X proc'lari `iDmg > 0` ile kapatir ve OnTakeDamage'in
  donusunu yok sayar, iceri tasimak yeni bir sapma olurdu.)

**03A kapanisi:** tam suite **2.388 basarili / 0 basarisiz** (+14). Sozlesme
degisikligi nedeniyle guncellenen mevcut test: `CombatAuditRegressionTests`
`OnHitProcKillStopsTheOriginalStrikePipeline` → `OnHitProcKillStillCreditsTheStrikeThatLanded`
(eski adi ve iddiasi parite disi sirayi sabitliyordu).

**Bu turda ortaya cikan, rapora dahil olmayan fark:** SphereNet Invul bir HEDEFE
hic vurus yapmiyor (`CombatHelper.IsInvalidSwingParticipant` Invul'u gecersiz hedef
sayar), Source-X ise vurup OnTakeDamage icinde sektiriyor — yani Source-X'te
proc'lar Invul hedefe karsi da calisir. Duzeltilmedi (bu turun bulgusu degil);
`CombatParity03ATests.AnInvulnerableTargetIsNotSwungAtAtAll` mevcut davranisi
sabitliyor ve gerekcesini tasiyor. 03B adayi.

**Elenen varsayım:** `OYUN_ICI_ANALIZ_RAPORU.md` G04'teki normal çanta içindeki
korumalı eşyanın ayrıca kurtarılması beklentisi Source-X kuralı değil.
`CContainer::ContentsTransfer` yalnızca doğrudan çocukları değerlendirir; güncel
SphereNet'in çantayla birlikte taşıması bu açıdan uyumlu. Bu nedenle G04 için
recursive ölüm koruması uygulanmamalı; shard kuralı istenirse ayrı tasarım kararıdır.

### SX-01B — Envanter ilk tarama (6 Eylül 2026)

SphereNet `7a11130da128af76417574a8003d7915ee6d737f`, Source-X `92ced0ba`.
[Ayrıntılı kanıt raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_01B_ENVANTER.md).
Altı yeni bulgu izole GameClient handler senaryolarıyla çalıştırıldı; Source-X
tarafı kaynak karşılaştırmasıdır. Üretim kodu değiştirilmedi. Tam test sonucu
2348/2348 başarılı. 01A'nın beş eski çalışma senaryosu da düzeltmeleri doğruladı.

**Yeniden kontrol:** Yukarıdaki tamamlanmış SX-01B-01–06 kayıtları geçerlidir.
`6804d29` üzerinde altı eski deney beklenen sonucu verdi; bu bölümdeki yinelenen
açık kutular kaldırıldı. Yeni kap kontrolünün trade bütünleşme eksiği SX-02-02'dir.

01B-04/05 eski veya özel hazırlanmış istek/UID bilgisi gerektiren erişim sınırı
senaryolarıdır; normal istemcinin bunları kendiliğinden ürettiği iddia edilmiyor.
Ek envanter varyantları rapor sonunda ayrıldı; sıradaki ana bölüm 02 güvenli ticaret.

### SX-02 — Güvenli ticaret ilk tur (6 Eylül 2026)

SphereNet `6804d29`, Source-X `92ced0ba`.
[Kanıt ve tekrar raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_02_GUVENLI_TICARET.md).
Tam suite 2362/2362 başarılı. Dört bulgu izole handler/world deneyleriyle üretildi.

- [ ] **SX-02-01 (P1)** — `HandleSecureTrade` param değerini kullanmalı;
  `param=0` mevcut toggle yaklaşımında onayı açıp transferi tamamlayabiliyor.
- [ ] **SX-02-02 (P2)** — Trade container sahipliği/açılma/erişim bağlamını
  bütünleştir; normal oyuncu kendi teklif nesnesini pickup ile geri alamıyor.
- [ ] **SX-02-03 (P1)** — Başlatılamayan trade'in ilk eşyasını iade et;
  REFUSETRADES reddinde eşya çanta ve DRAGGING dışında karaktere bağlı kalıyor.
- [ ] **SX-02-04 (P1)** — Trade içeriğinden nesne çıkarılınca iki onayı sıfırla;
  World.RemoveItem sonrası partner onayı korunup farklı teklif tamamlanıyor.

Normal tamamlama, disconnect iadesi ve ölüm öncesi trade iptal köprüsü kontrolleri
geçti. Sonraki 02B turu: save/load, script sözleşmesi, uzaklaşma/harita ve diğer
durum değişimleri. Tam kapsam henüz kapanmadı; kapasite ön reddi raporda tasarım
farkı olarak ayrıldı, yeni hata hükmü verilmedi.

### SX-02B — Yeni kayıt/script bulguları (6 Eylül 2026)

Kullanıcı SX-02-01–04 sorunlarının sürdüğünü bildirdi; açık durumları korundu.
Bu tur onları düzeltmeden üç ayrı senaryo çalıştırıldı. SphereNet `db97de6`.
[02B raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_02B_KAYIT_SCRIPT.md).

- [ ] **SX-02B-01 (P1)** — Aktif trade snapshot'ından yüklemede teklif eşyasını
  özgün sahibine geri bağla; nesne oturumsuz ve sahipsiz eski trade kabında kalıyor.
- [ ] **SX-02B-02 (P2)** — Partnerin aktif bağlantısını doğrula; IsPlayer=true
  fakat GameClient olmayan karakterle trade açılabiliyor.
- [ ] **SX-02B-03 (P2)** — @TradeAccepted veto ile gerçek iptali ayır; Source-X
  pencereyi açık bırakırken SphereNet kapatıp iki @TradeClose çalıştırıyor.

Üçü izole deneyle üretildi; Source-X karşılıkları kaynak okumadır. Üretim kodu
değiştirilmedi. Önceki 2362 test sonucu geçerli kaynak ağacına aittir; tam suite
bu tur tekrar çalıştırılmadı. Sonraki ana alan 03 dövüş; kalan ticaret varyantları
02B raporunda açık kapsam olarak korunuyor.

### SX-03A — Dövüş ilk tarama (6 Eylül 2026)

SphereNet `f42ea6a`, Source-X `92ced0ba`.
[Ayrıntılı rapor](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_03A_DOVUS.md).
Üç fark izole deneyle doğrulandı; tam suite 2374/2374 başarılı. Üretim koduna
bu incelemede dokunulmadı; ticaret düzeltmelerinin kapanışı bu turun konusu değil.

- [ ] **SX-03A-01 (P2)** — Normal mühimmat aramasında kilitli alt kaplara inme;
  FindAmmoInContainerCore IsSearchableContainer kontrolünü kullanmıyor.
- [ ] **SX-03A-02 (P1)** — REFLECTPHYSICALDAM hasarını bağışıklık sözleşmesinden
  geçir; Invul saldırgan doğrudan HP yazımı nedeniyle 20 can kaybediyor.
- [ ] **SX-03A-03 (P2)** — Vuruş sonrası proc'ları ana HP hasarından sonra uygula;
  HITFIREBALL callback'i 20 hasarlık vuruşta hedefi hâlâ 100 HP'de görüyor.

Sonraki 03B: windup/hedef değişimi, silah değiştirme, player/NPC zamanlaması,
miss/parry/veto cephane yolları. İlk tarama bütün dövüş kategorisini kapatmaz.
