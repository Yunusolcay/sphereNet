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

### 03B — hazirlik sirasinda durum degisimi ve cephane (6 Eylul 2026)

[03B kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_03B_ZAMANLAMA.md).
Iki bulgu da dogrulandi ve uygulandi.

- [x] **SX-03B-01 (P2)** — Hazirlanmis darbe cozulurken freeze/sleep denetlenmiyor.
  (YAPILDI: `CombatHelper.EvaluateHitTime` artik saldirganin Freeze/Sleeping'ini ve
  HEDEFIN Sleeping'ini yeniden denetliyor. **Rapora duzeltme — disposition:** rapor
  bunlari gecersiz sayiyordu; Source-X `Fight_CanHit` (CCharFight.cpp:1696-1699) bu
  ucu de WAR_SWING_SWINGING dondurur, yani darbe DUSURULMEZ, BEKLETILIR →
  `HitTimeDecision.Wait`. `ClearPendingHit` yanlis olurdu. Yalniz dead/stone/invul/
  insubstantial INVALID'dir ve Drop olarak kaldi. **Rapordaki bir karistirma:**
  `pCharSrc->IsSleeping()` (dormant sektor → INVALID) ile STATF_SLEEPING (→ SWINGING)
  ayni sey degil. **Raporda olmayan ek:** uyuyan hedef SphereNet'te hicbir asamada
  korunmuyordu — `IsInvalidSwingParticipant` Sleeping'e bakmiyor — bu yuzden baslangic
  yollari (`ClientCombatHandler.TrySwingAt`, `NpcAI.TrySwingAttack`) de kapatildi.)
- [x] **SX-03B-02 (P2)** — Menzil disi iska cantadan bolt tuketiyor. (YAPILDI: miss
  dalinda throwing artik hic mühimmat aramiyor. **Rapora ek — sorun throwing'den
  genis:** Source-X'te mühimmat blogu `if (iHitCheck != WAR_SWING_READY) return`
  satirinin ALTINDA (CCharFight.cpp:1814) ve menzil yeniden kontrolu
  WAR_SWING_EQUIPPING dondurur (:1896) — yani "hedef uzaklasti" harcanan bir
  saldiridir ve YAY/ARBALET dahil hicbir mühimmata dokunmaz; ok yalnizca gercek iska
  zarinda tuketilir (:2023) ve yalnizca oyuncu icin (:2041). NPC yolu zaten dogru
  (`FindNpcAmmo` throwing icin null doner, NPC miss dali mühimmat tuketmez) —
  dokunulmadi.)

**03B kapanisi:** tam suite **2.397 basarili / 0 basarisiz** (+9). Mevcut testlerde
sozlesme degisikligi gerekmedi.

**03A'da acilan kusur duzeltildi:** `CombatHelper.FindAmmoInContainerCore` icindeki
uc satirlik yorum `f98612e`'de iki kez yazilmisti; tekillestirildi.

**Bilincli birakilanlar / 03C adaylari:**
- Hidden/Invisible hedef `EvaluateHitTime`'da `Drop` donuyor; Source-X SWINGING
  (bekle) der. Degistirilmedi: gizli kalan bir hedefte "sonsuza kadar beklet"
  bekleyen darbeyi kilitleyebilir; ayri bir tasarim karari.
- Menzil disi durum hala `Miss` olarak siniflaniyor (iska mesaji + sesi uretiyor);
  Source-X harcanan saldiri sayar. Mühimmat tuketimi kaldirildi, siniflandirma
  degismedi.
- `IsCasting` ve `Stam <= 0` yalnizca baslangicta denetleniyor; Source-X
  `Fight_CanHit`'te karsiligi yok, oldugu gibi birakildi.

### 03C — saldiri hizi ve hareket beklemesi (6 Eylul 2026)

[03C kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_03C_HIZ.md).
Iki bulgu da dogrulandi ve uygulandi; raporun aritmetigi bagimsiz olarak yeniden
turetildi ve dogru cikti.

- [x] **SX-03C-01 (P2)** — Hiz formulune temel DEX giriyor. (YAPILDI:
  `GetSwingDelayMs` stat'i era'ya gore seciyor — era 0 `EffectiveDex` (Source-X
  `Stat_GetAdjusted`, CResourceCalc.cpp:61/69), era 1-4 `attacker.Stam` (Source-X
  `Stat_GetVal`, :90/:101/:116/:127). `Stat_GetVal(STAT_DEX)`'in mevcut stamina
  oldugu varsayilmadi, kanitlandi: CChar.cpp:4271 STAM save alanini ondan yazar.
  Formullere ve tarihsel tamsayi yuvarlamalarina dokunulmadi; era 4'un
  `100/(100+SSI)` sifirlanmasi bilincli olarak korundu.
  **Raporda olmayan tehlike:** `Dex` setter'i `_maxStam`'i yukseltir ama mevcut
  havuzu ASLA — yalnizca asagi kirpar. Yani sadece `Dex` verilerek kurulan bir
  karakterin `Stam`'i 0 kalir ve era 1-4'te formulun en yavas degerinden vururdu.
  Uc uretim yolu bunu tohumlamiyordu: `CharDefHelper` (chardef'ten NPC),
  `GameClient.Login` (yeni oyuncu karakteri), `StressTestEngine`. Ucu de duzeltildi;
  setter'a dokunulmadi cunku kod tabaninin kurali "havuzlari yaratan doldurur".)
- [x] **SX-03C-02 (P2)** — Hazirlik sonrasi hareket beklemesi atlaniyor. (YAPILDI:
  `EvaluateHitTime` artik menzilli silahta hareket beklemesini yeniden denetliyor.
  **Disposition:** Source-X `WAR_SWING_EQUIPPING` dondurur (CCharFight.cpp:1854) —
  yani saldiri HARCANIR ve recoil yeniden baslar; bu, 03B'de ekledigim freeze/sleep
  `Wait`'inden FARKLI ve menzil disinin zaten kullandigi `Miss` ile ayni. Yalniz
  menzilli: SphereNet'in melee hareket gecikmesinin Source-X'te karsiligi yok, o
  yuzden hit asamasina tasinmadi.)

**03B'de yarim kalan is tamamlandi:** `f0db737`'de yorum "Source-X harcanan saldiride
mühimmata dokunmaz" diyordu ama kod yalnizca throwing'i disliyordu — yay/arbalet hala
ok tuketiyordu. Miss dali artik hicbir menzilli silahta mühimmat almiyor (Source-X'te
mühimmat blogu :1862, hem menzil (:1896) hem hareket (:1857) donuslerinin ALTINDA).
`Miss` yalnizca harcanan saldiridan uretiliyor; gercek iska zari ayri yoldan gelir.

**03C kapanisi:** tam suite **2.407 basarili / 0 basarisiz** (+10). Sozlesme
degisikligi nedeniyle guncellenen mevcut testler: dort zamanlama testi fixture'i
(`Dex` yanina `Stam` eklendi — beklenen sayilar degismedi) ve
`CombatAuditRegressionTests.StayInRangeRangedMissStillSpendsAmmo` →
`StayInRangeRangedMissDoesNotSpendAmmo` (eski adi ve iddiasi parite disiydi).

**Acik kalan (03D adaylari):** Source-X hareket kontrolunu `m_pClient` ile
kapatir (yalnizca oyuncu); SphereNet `LastMoveTick`'i NPC'ler icin de tutar ve
`ValidateSwingPrep`'te zaten oyuncu ayrimi yok — hit asamasina da ayrimsiz eklendi,
yani iki asama tutarli ama referanstan sapiyor. `IsPlayer` kapisi eklemek ayri bir
karar. Ayrica NPC `@HitTry` Anim/AnimDelay sozlesmesi ve era-specific trigger
davranislari acik.

**Elenen varsayım:** `OYUN_ICI_ANALIZ_RAPORU.md` G04'teki normal çanta içindeki
korumalı eşyanın ayrıca kurtarılması beklentisi Source-X kuralı değil.
`CContainer::ContentsTransfer` yalnızca doğrudan çocukları değerlendirir; güncel
SphereNet'in çantayla birlikte taşıması bu açıdan uyumlu. Bu nedenle G04 için
recursive ölüm koruması uygulanmamalı; shard kuralı istenirse ayrı tasarım kararıdır.

### 04A — buyu kaynagi ve hedefi (6 Eylul 2026)

[04A kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_04A_BUYU.md).
Iki bulgu da bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-04A-01 (P1)** — Buyunun KAYNAGI tamamlanmada yeniden cozulmuyordu.
  (YAPILDI: `TryResolveCastSource`, Source-X'in `m_Act_Prv_UID.ObjFind()`
  (CCharSpell.cpp:2882) ve ayni isaretciyi `Spell_CanCast`'e verme (:3010)
  modelini kuruyor. Kaynak yoksa (:2330) ya da ust-duzey sahibi caster degilse
  (:2422) buyu basarisiz. `ConsumeCastSource` artik cozulmus nesneyi aliyor, yani
  baskasinin cantasindaki parsomen tuketilemiyor.
  **GM istisnasi yok:** referans bu kontrolu PRIV_GM kisayolundan ONCE yapar
  (:2422 vs :2429), bu yuzden `IsItemAccessible`'in GM bypass'i kullanilmadi.
  **Raporda olmayan tehlike 1:** `IsCastingWithWand` tag'e degil, O AN KUSANMIS
  esyaya bakiyordu — oyuncu cast suresi boyunca bir asa kusanip siradan bir
  buyuyu asa fiyatina (mana 0, zorluk 10, reagent yok) bitirebiliyordu. Oyuncu
  icin artik yalnizca ETKINLESTIRILEN kaynak sayiliyor; NPC'ler kaynak
  etiketlemedigi icin onlarda kusanmis-asa okumasi korundu.
  **Raporda olmayan tehlike 2:** `ClearCastState` kaynak etiketlerini temizlemiyor,
  ve fizzle / mana-yetersiz / reagent-yetersiz dallari yalnizca onu cagiriyordu —
  WAND_UID/SCROLL_UID caster'in uzerinde kaliyor ve BIR SONRAKI cast o parsomeni
  tuketiyordu. Uc dal da artik etiketleri birakiyor.)
- [x] **SX-04A-02 (P2)** — Gecersiz hedef, maliyetler alindiktan SONRA reddediliyordu.
  (YAPILDI: hedef denetimi fizzle/mana/reagent/kaynak tuketiminin ONUNE tasindi;
  Source-X `Spell_CastDone`'i `Spell_TargCheck` ile acar (CCharSpell.cpp:2878) ve
  tuketime 130 satir sonra ulasir (:3010). Olu hedef (:2740), harita/menzil ve
  "hedef gerekli" (:2728, TARG_XYZ istisnasiyla) kontrolleri orada.
  **Bedava degil:** basarisizlik `FailCastAtCompletion` uzerinden ABORT olarak
  fiyatlaniyor — CCharSkill.cpp:3000 false donusu SKTRIG_ABORT'a cevirir ve
  `Spell_CastFail(fAbort)` MANALOSSABORT/REAGENTLOSSABORT'u uygular (:3316).
  Kosulsuz iade referanstan ayri bir sapma olurdu; rapor da bunu uyariyordu.
  LOS reddi de ayni yola baglandi.)

**04A kapanisi:** tam suite **2.419 basarili / 0 basarisiz** (+12). Her iki
duzeltme gecici olarak geri alinarak testlerin eski davranisi yakaladigi
kanitlandi (kaynak kapisi 3 test, hedef on-denetimi 5 test).
Sozlesme degisikligi nedeniyle guncellenen mevcut testler:
`SpellCastSourceTests` (asa/parsomen artik caster'in cantasinda) ve
`MagicCastFlowParityTests.ScrollCast_ChecksAndConsumesTheSameHalvedCost`
(`SCROLL_UID="0"` hayalet UID'si yerine gercek parsomen) — indirim artik
etiketin varligindan degil cozulmus kaynaktan fiyatlaniyor.

**Acik kalan (04B adaylari):** asa sarj kontrolu (Source-X :2437 sarj yoksa
reddeder; SphereNet CHARGES etiketi olmayan asayi sinirsiz sayar), `ATTR_MAGIC`
kapisi (:2415) ve `CastStart`'in yalniz Layer.OneHanded'a bakan asa okumasi.

### 04B — alan buyulerinde temas (6 Eylul 2026)

[04B kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_04B_ALAN_BUYULERI.md).
Uc bulgu da bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-04B-01 (P1)** — Ust uste binmis alanlarin hasari yuruyuste birikiyordu.
  (YAPILDI: Source-X bir konum kontrolunu TEK buyu etkisiyle sinirlar
  (CCharAct.cpp:4996) ve gerekcesini kodda yazar: yigilmis Fire Field'in tek adimi
  katlamasi ve Paralyze+Fire yiginin her hasar tick'inde yeniden dondurmesi.
  SphereNet'te durma yolu ilk alandan sonra donuyordu ama yurume yolu hepsini
  calistiriyordu — iki yol artik ayni siniri paylasiyor.
  **Kritik ayrinti:** sinir denemeyi degil SONUCU izler. Referans
  `fSpellHit = OnSpellEffect(...)` (:5008) atamasini yapar; etki reddedildiginde
  false doner ve sonraki alan hakkini korur. Tek bir "islendi" boolean'i bunu
  ifade edemedigi icin hook `FieldTouchResult` (NotHandled/Handled/SpellHit)
  donuyor. Barrier ve bagisik hedef artik arkasindaki alani yutmuyor.
  **Kapsam:** sinir yalniz buyu alanlarina ait; ayni hucredeki tuzak/telepad
  dongusu Source-X'teki gibi calismaya devam ediyor.)
- [x] **SX-04B-02 (P1)** — Alan temasinda kat/yukseklik ayrimi yoktu.
  (YAPILDI: `Item.IsWithinStepHeight`, Source-X CheckLocation penceresini kuruyor
  (CCharAct.cpp:4934): zdiff = itemZ - charZ, esya yuksekligi en az 3 sayilir,
  zdiff > height veya zdiff < -3 ise esya atlanir. Yurume ve durma yollari ayni
  yardimciyi kullaniyor.
  **Bilincli kapsam genislemesi:** referans bu testi @STEP'ten ONCE yapar, yani
  atlanan esya tetikleyici de calistirmaz. Bu nedenle filtre yalniz alanlara degil
  butun konum dongusune (tuzak, moongate, ag) uygulandi — raporun istedigi
  "paylasilan temas/yukseklik uygunlugu" bu. Alt kattaki moongate'in ust kattaki
  karakteri isinlamamasi da ayni duzeltmenin sonucu.)
- [x] **SX-04B-03 (P2)** — Poison Field, Invul karakterde zehir durumunu baslatiyordu.
  (YAPILDI: Source-X zararli buyu dalinin basinda Invul hedefi, herhangi bir etki
  uygulanmadan geri cevirir ve false doner (CCharSpell.cpp:3762). Fire kendi
  bagisiklik kontrolunu yapiyordu; poison ve paralyze yapmiyordu.
  **Raporda olmayan ikinci hedef:** ayni delik Paralyze Field'da da vardi — Invul
  karakter donuyordu. Referans pakette hem `s_poison_field` hem
  `s_paralyzation_field` SPELLFLAG_HARM tasidigi icin kapi tur listesine degil
  def'in Harm bayragina bagli; ucu birden tek kapidan geciyor.
  **Degismeyen:** zaten devam eden zehir icin tick tarafindaki koruma
  (CharacterPoisonState) korundu — rapor bunun kaldirilmamasini istiyordu.)

**04B kapanisi:** tam suite **2.439 basarili / 0 basarisiz** (+20). Uc duzeltme de
gecici olarak kapatilarak 7 testin eski davranisi yakaladigi kanitlandi.
Sozlesme degisikligi nedeniyle `FieldAndSummonParityTests`'in iki iddiasi
bool'dan `FieldTouchResult.SpellHit`'e guncellendi.

**Acik kalan (04C adaylari):** Source-X zararli dalinin `PLEVEL_Guest` reddi
(:3767) SphereNet'te hicbir yerde yok — Invul kapisiyla ayni satirda ama ayri bir
yetki kurali oldugu icin bu tur eklenmedi. Paralyze Field'in normal Paralyze
tanimina yonlendirilmesi, field uzerinde `@SpellEffect` veto sozlesmesi,
`FIELD_CASTER_UUID` uretilmesine ragmen temasin yalniz UID kullanmasi ve
`OverrideFields` ayari da acik.

### 04C — summon kapasitesi ve kayitli sure (6 Eylul 2026)

[04C kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_04C_SUMMON.md).
Iki bulgu da bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-04C-01 (P1)** — Menuden secilen summon'un gercek slot maliyeti kapasite
  kontrolunden SONRA uygulaniyordu. (YAPILDI: Source-X summon'u once secilen
  kimlikten kurar (`CreateBasic(m_atMagery.m_uiSummonID)`, CCharSpell.cpp:2640) ve
  ancak sonra `GetFollowerSlots()` ile olcer (:2662). SphereNet yer tutucuyu
  olcuyor, secimi sonradan uyguluyor ve kapasiteyi bir daha kontrol etmiyordu.
  `SummonCreature` artik secilen tanimi `TryAssignOwnership`'ten ONCE uyguluyor.
  **Ikinci yari - sira:** raporun "basari daline ve basari maliyetine ilerlememeli"
  maddesi. Referans `Spell_Summon_Try`'i :3002'de cagirir, tuketime :3010'da ulasir
  ve tuketim karsilanamazsa summon'u siler (:3012). Summon `PrepareSummon` ile
  butun maliyetlerin onune tasindi; mana/reagent karsilanamazsa `DiscardSummon`
  onu geri aliyor. Basarisizlik `FailCastAtCompletion` ile ABORT olarak
  fiyatlaniyor — kosulsuz iade degil, 04A'daki ayni sozlesme.)
- [x] **SX-04C-02 (P2)** — Summon bitis zamani save'e ham TickCount64 olarak gidiyordu.
  (YAPILDI: mutlak TickCount64 calisma suresidir; yeniden baslatilmis bir makinede
  anlamsizdir. Source-X timer'i KALAN milisaniye olarak yazar (CObjBase.cpp:2081) ve
  yukleme zamanina gore yeniden kurar (:2037). `WorldSaver` artik
  `SUMMON_EXPIRE_REMAINING` yaziyor, `Character.RestoreSummonExpiry` son tarihi
  calisan saatte yeniden kuruyor. Ayni dosyada POISON kaydinin zaten kalan sureyi
  saklamasi ic-emsal olarak izlendi.
  **Eski kayit politikasi:** rapor "eski alani sessizce yok sayip summon'u suresiz
  birakmak dogru degildir" diyordu. Eski mutlak deger yeni saat tabaninda
  okunamadigi icin son tarih summon'un kendi `SUMMON_DURATION` degerinden yeniden
  kuruluyor; suresi de yoksa bir sonraki tick'te bitiyor. En fazla bir sure kadar
  comert, ama SINIRLI.)

**04C kapanisi:** tam suite **2.453 basarili / 0 basarisiz** (+14). Iki duzeltme de
gecici olarak kapatilarak 10 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test sozlesme degisikligi nedeniyle guncellenmedi.

**Test kurulumunda ortaya cikan iki tuzak** (uretim hatasi degil, not olarak):
`DefinitionLoader.LoadAll()` spell registry'sini yeniden kurar — programatik
`Register` ondan SONRA yapilmali. Ve `SpellDef.GetDuration` egrisi
`DurationScale` verilmediginde yuksek skill'de NEGATIFE duser; testte iki uc da
verilmeli.

**Acik kalan (04D adaylari):** dispel/silme sonrasi follower cache, sahibin
olumu/silinmesi, summon `@Create` sirasi, `SummonWalkCheck` ve genel summon sayisi
siniri. Ayrica Source-X follower kapisini `!IsPriv(PRIV_GM)` ve `IsSetOF(OF_PetSlots)`
altinda tutar; SphereNet'in `TryAssignOwnership` kapisinda ikisi de yok — ayri bir
yetki/ayar karari oldugu icin bu tur degistirilmedi.

### 04D — dispel ve summon temizligi (6 Eylul 2026)

[04D kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_04D_SUMMON_TEMIZLIK.md).
Tek bulgu bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-04D-01 (P2)** — Silinen summon, sahibinin follower sayisi onbellegini
  gecersizlestirmiyordu. (YAPILDI: Source-X slotu yeniden hesaplamaz, GERI VERIR —
  NPC temizligi `NPC_PetClearOwners` cagirir (CChar.cpp:364) ve yaratigin maliyetini
  sahibinden duser (`FollowersUpdate(this, -iFollowerSlots)`, CCharNPCPet.cpp:597).
  SphereNet sayimi tarayarak yapiyor ve `CurFollowerCacheMs` boyunca tutuyordu;
  kaldirma sahiplik alanlarini yazmadigi icin onbellek kirlenmiyordu.
  **Merkezi nokta secildi:** raporun uyardigi gibi yalniz `DispelConjured`'a eklemek
  sure sonu ve diger silme yollarini acik birakirdi; invalidation butun yollarin
  paylastigi `Character.Delete`'e kondu — referansin da tek bir dispel metodunda
  degil ortak NPC temizliginde yaptigi gibi.
  **Kasitli olarak yapilmayan:** raporun ikinci uyarisi, genel silmede kosulsuz
  `ClearOwnership` cagirmanin silme scriptlerinin eski sahipligi gorme sirasini
  bozabilecegiydi. Bu yuzden sahiplik alanlarina DOKUNULMADI, yalnizca onbellek
  dusuruldu; sonraki sayim yaratigi zaten `IsDeleted` uzerinden atliyor.)

**04D kapanisi:** tam suite **2.462 basarili / 0 basarisiz** (+9). Duzeltme gecici
olarak kapatilarak 6 testin eski davranisi yakaladigi kanitlandi. Mevcut hicbir test
guncellenmedi.

**Acik kalan:** `DispelKillSummons` acikken olum dalinin follower davranisi (bu tur
yalniz dogrudan silme dali calistirildi), silme scriptlerinin veto/yeniden giris
sozlesmesi, sahibin olumu/hesabinin silinmesi/baglanti kopmasi ve summon etkisinin
bagimsiz suresi.

### 05A — tarif kimligi ve uretim aleti (6 Eylul 2026)

[05A kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_05A_URETIM.md).
Iki bulgu da bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-05A-01 (P1)** — Ayni display ID'yi kullanan tarifler birbirini eziyordu.
  (YAPILDI: `_recipes` sozlugu artik ITEMDEF KAYNAK kimligiyle anahtarlaniyor —
  Source-X bu kimligi `Skill_MakeItem` boyunca tasir ve tanimi dogrudan onunla bulur
  (CCharSkill.cpp:870/679). `MAKEITEM` de display kimligine indirmek yerine
  `irid.Index` geciriyor.
  **Uyumluluk kurali acikca yazildi:** raporun istedigi gibi eski display-ID lookup'i
  sessizce sonuncuyu secmiyor. `TryGetRecipe` once kaynak kimligine bakiyor; grafik
  uzerinden yalnizca o grafigi TEK bir tarif tasidiginda yanit veriyor, birden
  fazlasi paylastiginda `null` donuyor. Numeric ITEMDEF'ler ve siradan paketler
  birinci veya ikinci daldan cozuluyor.)
- [x] **SX-05A-02 (P2)** — Erisilebilir alet izin verirken kilitli kaptaki alet
  asiniyordu. (YAPILDI: `FindItemOfTypeIn` alt kaba inerken `IsSearchableContainer`
  istemiyordu, `HasItemOfTypeIn` ise istiyordu — izin veren alet ile asinan alet
  farkli olabiliyordu. Source-X `ContentFind` aranamaz kabi atlar
  (CContainer.cpp:236).
  **Raporun onerdigi yapisal cozum uygulandi:** iki helper'i ayri ayri duzeltmek
  yerine `HasItemOfTypeIn` artik `FindItemOfTypeIn` uzerinden cevapliyor; tek arama,
  bir daha ayrisamazlar.
  **Not:** bu, 03A'da cephane ve kaynak yurumeleri icin kapatilan hatanin ikizi —
  o turda gozden kacan tek yurume buydu.)

**05A kapanisi:** tam suite **2.475 basarili / 0 basarisiz** (+13). Iki duzeltme de
gecici olarak kapatilarak 7 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test guncellenmedi (`AllRecipes` anahtari `ushort`'tan `int`'e gecti, ama
disaridaki tek kullanim `GetRecipesBySkill`).

**Acik kalan:** kaynak toplama turu (dugum miktari, basarisizlik tuketimi, uretim
teslim/yerlestirme kosullari) raporun kendi devam kuyrugunda.

### 05B — kaynak toplama script sozlesmesi (6 Eylul 2026)

[05B kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_05B_KAYNAK_SCRIPT.md).
Iki bulgu da bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-05B-01 (P1)** — ResourceGather ARGN1'i miktar yerine esya kimligi olarak
  yorumlaniyordu. (YAPILDI: Source-X `Init(wAmount, 0, 0, pResBit)` +
  `LOCAL.ResourceID = m_ReapItem` (CCharSkill.cpp:1029) kurar; ARGN1 MIKTAR, nesne
  argumani marker, esya kimligi local'dir ve tetikleyiciden sonra oradan okunur
  (:1044). Sozlesme birebir kuruldu: `N1 = reapAmount`, `O1 = activeMarker`,
  `Locals["ResourceID"] = reapItemId`.
  **Sifir semantigi:** referans `ConsumeAmount(ARGN1)` ile tuketir ve sonuc <= 0 ise
  esya uretmez. Eskiden 0 "atanmamis" sayilip tam reap veriliyordu; artik hicbir sey
  vermiyor. Negatif de ayni.
  **Kirpma sirasi:** havuz kirpmasi tetikleyiciden ONCE yapiliyor (referans :1025),
  boylece script gercekten sunulan miktari okuyor.
  **Kasitli sapma:** script `LOCAL.ResourceID`'yi 0 veya gecersiz birakirsa tanimin
  kendi reap'i korunuyor; referans o durumda id 0 uretmeye calisirdi.)
- [x] **SX-05B-02 (P2)** — Toplanan esyanin ITEMDEF Create scripti atlaniyordu.
  (YAPILDI: Source-X `CItem::CreateScript` kullanir (CCharSkill.cpp:1050); bu
  `GenerateScript` uzerinden @Create'i calistirir (CItem.cpp:404/415) ve miktari
  ANCAK SONRA atar. `item.FireCreateTrigger()` eklendi; sira da referanstaki gibi
  (Create once, Amount sonra).
  **Raporun iki uyarisi karsilandi:** "iki kez calisabilir" — `FireCreateTrigger`
  instance-guard'li, teslim yolu tekrar cagirsa bile bir kez calisiyor. "Create
  nesneyi silerse olu nesne basariyla teslim edilmez" — `IsDeleted` kontrolu eklendi.)

**05B kapanisi:** tam suite **2.489 basarili / 0 basarisiz** (+14). Iki duzeltme de
gecici olarak kapatilarak 9 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test eski sozlesmeyi kodlamiyordu, guncelleme gerekmedi.

**Bilincli olarak kapsam disi:** referans, kaynak tetikleyicisinden ONCE karakter
uzerinde `CTRIG_RegionResourceGather` calistirir ve ayni arguman/LOCAL havuzunu
paylasir (CCharSkill.cpp:1035). SphereNet'te boyle bir karakter tetikleyicisi HIC
yok — bu, numaralandirilmis bulgunun duzeltmesi degil yeni bir trigger eklemek
olurdu; 04B'deki `PLEVEL_Guest` ve 04C'deki GM/`OF_PetSlots` kapilariyla ayni
gerekce. Ayrica named ITEMDEF/resource kimligi ushort'a indirgeniyor (raporun
"degerlendirilmelidir" notu) ve havuz rejenerasyonu/marker save-load bir sonraki
kesitte.

### 05C - kaynak dugumu yenilenmesi (6 Eylul 2026)

[05C kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_05C_KAYNAK_YENILENME.md).
Iki bulgu da bagimsiz olarak dogrulandi ve uygulandi. **Bu tur canli davranisi
degistiriyor** - ayrintisi changelog'da.

- [x] **SX-05C-01 (P1)** - REGEN zaman birimi ve deger egrisi korunmuyordu.
  (YAPILDI: referans REGEN'i deger egrisi olarak yukler ve yorumunda ONDA SANIYE
  yazar (CRegionResourceDef.cpp:73); zaman asimini
  `GetRandom() * MSECS_PER_TENTH` ile kurar (CWorldMap.cpp:148). SphereNet tek int
  + saniye okuyordu, yani her damar on kat uzun yasiyordu. `ParseExpressionCurve` +
  `GetRandomRegen` eklendi; noktalar ifade olabildigi icin duz `ParseIntegerCurve`
  kullanilamadi (`60*60*10`).
  **Uydurma fallback kaldirildi:** rapor "eski keyfi fallback yeni sozlesmeye
  otomatik tasinmamali" diyordu. Atanmamis REGEN artik sifir orneklenir; referansta
  `MoveToDecay(pt, 0)` neredeyse ani cozulmedir (`CItem.cpp:5879` yorumu).
  **Paket dogrulamasi:** `sphere_region.scp` REGEN=60*60*10 yaziyor ve yanindaki
  "seconds" yorumu ifadeyle celisiyor - `60*60*10` onda saniye olarak tam bir saat.
  Referans C++ carpani esas alindi.)
- [x] **SX-05C-02 (P2)** - Mevcut dugum kademeli doluyor ve omru uzatiliyordu.
  (YAPILDI: referans buldugu bit'i oldugu gibi dondurur (CWorldMap.cpp:71),
  `MoveToDecay`'i yalniz olusturmada cagirir (:148), tuketimde timer'a dokunmaz
  (CCharSkill.cpp:1046) ve sifir miktari tukenmis sayar (:1456). `RegenMarker` ve
  her temastaki deadline itmesi kaldirildi; `RES_MAX`/`RES_LAST` kayitlari da
  gereksiz kaldi.
  **Tasarim karari nasil verildi:** rapor bunu "tasarim karari gerektiren uyumluluk
  farki" olarak isaretlemisti. Kaynagi arastirdim - 55385e9 / changelog
  2026-06-30 girdisi bu davranisi **"Source-X kademeli vein regrow"** paritesi
  olarak kaydetmis. Yani kayitli bilincli bir sapma degil, HATALI bir parite
  iddiasi; bu yuzden parite kazandi ve davranis kaldirildi.
  **Emekli edilen test:** `CraftGatherRemainingTests.RegenMarker_PartiallyRefills...`
  eski sozlesmeyi kodluyordu; kaldirildi ve dosyanin bas yorumu duzeltildi.)

**05C kapanisi:** tam suite **2.499 basarili / 0 basarisiz** (+11 yeni, -1 emekli).
Duzeltmeler gecici olarak kapatilarak 5 testin eski davranisi yakaladigi kanitlandi.
Iki deadline testi once kapatilmis kodda da GECIYORDU - iki toplama ayni
milisaniyede olunca "now + lifetime" iki kez yazmak degismemis gibi okunuyor; testler
sentinel deger kullanacak sekilde saatten bagimsiz hale getirildi.

**Acik kalan:** raporun `RES_LAST` gozlemi kendiliginden cozuldu (alan artik hic
yazilmiyor). Marker `DecayTime`'i `WorldSaver` tarafindan kalan saniye olarak
yazildigi icin 04C sinifindan bir saat-tasima riski kalmadi; yine de gercek yeniden
baslatma + temizleme dongusu canli olarak calistirilmadi.

### 06A-06D - pet komutu, ahir, mount kimligi, figurin (6 Eylul 2026)

Kanit raporlari: [06A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06A_PET_KOMUT.md),
[06B](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06B_AHIR.md),
[06C](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06C_MOUNT_KIMLIK.md),
[06D](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06D_FIGURIN_SILME.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-06A-01 (P1)** - Pet arkadasligi sahip komutlari icin de yetki veriyordu.
  (YAPILDI: `IsFriendPermittedPetVerb` ile fiil bazli matris. Referans arkadaslara
  TAM OLARAK PC_FOLLOW/PC_STAY/PC_STOP acar (CCharNPCPet.cpp:129-152).
  **Raporun kacirdigi ayrinti:** COME ve FOLLOW ME arkadas kumesinde DEGIL - PC_COME
  (:38) ve PC_FOLLOW_ME (:43) ayri sabitler ve owner-only dala duser. Rapor bunu
  "yalniz FOLLOW, STAY ve STOP" diye gecmisti; birebir uyguladim.
  **Cursor yarisina karsi:** raporun uyardigi gibi yetki hem konusmada hem de hedef
  tiklandiginda yeniden denetleniyor.)
- [x] **SX-06A-02 (P2)** - Come emri eski Go hedefini iptal etmiyordu.
  (YAPILDI: `SupersedePendingPetOrder` yeni her emirde GO_TARGET/PREV_PET_MODE
  dusuruyor. Referans her komutta yeni NPC isi baslatir (:183 / :504).
  **Raporun uyarisi karsilandi:** AI'daki Go onceligi TERSINE CEVRILMEDI - gercek Go
  komutu bozulmasin diye. GO fiili PREV_PET_MODE'u kendi yonetmeye devam ediyor,
  boylece ikinci bir GO detour modunu degil orijinal modu koruyor.)
- [x] **SX-06B-01 (P1)** - Ahir onbellegi yeniden kullanilan owner UID'sini eski
  sahip saniyordu. (YAPILDI: onbellek kaydi artik `(OwnerUuid, Pets)` tutuyor ve
  yalnizca kuruldugu karaktere cevap veriyor.)
- [x] **SX-06B-02 (P2)** - Ahirdan cikan pet eski Attack emrini yeniden basliyordu.
  (YAPILDI: `PetStorage.Park` gecici emirleri dusuruyor.
  **Raporun uyarisi:** kalici pet tag'leri toplu silinmedi - bonding, script durumu
  ve slot override korunuyor.)
- [x] **SX-06C-01 (P1)** - Mount baglantisi silinme ve UID yeniden kullaniminda yanlis
  karaktere baglaniyordu. (YAPILDI: `ResolveMountNpc` - kaydedilmis UUID baglayici,
  serial yalnizca UUID'siz eski kayit icin; Player/silinmis nesne reddediliyor.
  Ayrica mount uzerine `MOUNT_RIDER_UUID` yaziliyor ve `OnMountNpcDeleted` binicinin
  tarafini kopariyor - referansin DeleteCleanup'ta yaptigi (CChar.cpp:395).)
- [x] **SX-06D-01 (P2)** - Figurin yok edilince pet yetim kaliyordu. (YAPILDI:
  `Item.Delete` merkezi yolunda `FigurineDeletedHook`; `PetFigurine.OnFigurineDeleted`
  yalnizca HALA O figurinde park edilmis peti siliyor. `Restore` figurini tuketmeden
  once baglantiyi kopariyor - raporun "kosulsuz pet silme normal acilisi da yok
  edebilir" uyarisi.)

**06 kapanisi:** tam suite **2.518 basarili / 0 basarisiz** (+19). Alti duzeltme de
gecici olarak kapatilarak 11 testin eski davranisi yakaladigi kanitlandi.

**Ortak kok:** alti bulgunun ucu ayni sinifta - kaydedilmis UUID varken serial'a
dusmek. `PetStorage.Resolve` ve `MountEngine.ResolveMountNpc` artik ayni kurali
uyguluyor, boylece ahir/figurin/mount bir daha ayrisamaz.

**Ilk denemede yakalanan tuzak:** iki 06A-02 testi ilk kapatma turunda GECTI - kapatma
kosulunun polaritesini ters yazmisim, yani duzeltme hala aciktil. Polarite duzeltilip
tekrarlandi ve iki test de kirmiziya dondu. Kapatma turunun kendisi de dogrulanmali.

**Acik kalan:** `SOURCE_X_BOLUM_06E_TEKIL_PET_DURUMU.md` bu partide istenmedi.
Ayrica raporlarin kendi kuyruklari: dismount yerlestirme basarisizligi, ayni mount'a
iki rider, zaten park edilmis NPC'nin yeniden paketlenmesi, stable hedefinde gorus
denetimi ve friend/unfriend/release fiillerinin ayri regresyonlari.

### 06E-06H - tekil pet durumu, bonded, besleme, @Eat (6 Eylul 2026)

Kanit raporlari: [06E](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06E_TEKIL_PET_DURUMU.md),
[06F](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06F_BONDED_SAHIPLIK.md),
[06G](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06G_PET_BESLEME.md),
[06H](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06H_EAT_SCRIPT.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-06E-01 (P2)** - Zaten saklanmis/binilmis pet yeniden mount kabul ediyordu.
  (YAPILDI: `TryMount` artik `npc.IsPlayer || npc.IsStatFlag(Ridden)` reddediyor -
  referansta Make_Figurine disconnected yaratigi ve oyuncuyu bastan reddeder
  (CCharAct.cpp:3619), Horse_Mount da onunla basarisiz olur (:3989).
  **Raporun istedigi iki katman:** "yalniz istemci kapisini degistirmek script/motor
  cagrilarini korumaz" - motor kapisi otoriter, `CanSeeCharacterForDoubleClick`'teki
  Ridden reddi ise eski tiklamanin geldigi yolu kapatiyor.)
- [x] **SX-06F-01 (P2)** - Sahipligi biten pet BONDED kaliyordu. (YAPILDI:
  `ClearOwnership` artik `IsBonded = false` yapiyor - referans ayni yerde
  `m_pNPC->m_bonded = 0` yazar (CCharNPCPet.cpp:559).
  **Ortak yol secildi:** rapor "yalniz DeathEngine'de sahiplik sarti eklemek canli
  sahipsiz NPC uzerindeki yanlis BONDED durumunu duzeltmez" diyordu; duzeltme
  release ve desert'i birlikte kapsayan sahiplik temizleme yoluna kondu.
  Tum `ClearOwnership` cagiranlari denetlendi; hepsi gercekten sahipligin bittigi
  durumlar.)
- [x] **SX-06G-01 (P2)** - Tok pete verilen yigin tamamen siliniyordu. (YAPILDI:
  yeni `EatEngine` bos alani olcuyor; tok ise hic yemiyor, kismi ise yalnizca gereken
  adedi tuketiyor, kalan yigin sahibin cantasina donuyor - Use_EatQty :891/:894 ve
  NPC_OnItemGive.)
- [x] **SX-06G-02 (P2)** - FOOD script degeri ile aclik havuzu ayrisiyordu. (YAPILDI:
  `NpcFood` artik `Food` uzerine alias; tek `_food` havuzu. Yan kazanc: `NpcFood` hic
  persist edilmiyordu, artik petin acligi save'i atlatiyor. `MaxFood` MAXFOOD
  etiketinden geliyor - raporun "MAXFOOD=60 sabitini butun yaratiklara yayma"
  uyarisi.)
- [x] **SX-06H-01 (P2)** - Pet beslemesi @Eat'i atliyordu. (YAPILDI: besleme ortak
  `EatEngine` yolundan geciyor, olay yiyen pet uzerinde O1=yiyecek ile calisiyor.)
- [x] **SX-06H-02 (P2)** - Oyuncu @Eat cagrisi ARGN1/LOCAL sozlesmesini uygulamiyordu.
  (YAPILDI: ARGN1 sifirdan baslayan STAT LIMITI, LOCAL.Hits/Mana/Stam/Food hazirlanip
  geri okunuyor (CCharAct.cpp:3456-3476).
  **Raporun isaret ettigi mevcut test duzeltildi:** `P1SkillEventTriggerTests` N1=5
  bekliyordu ve RETURN 1'in yiyecegi de kurtardigini test ediyordu. Referansta ikisi
  de yanlis: ARGN1 stat limiti, ve Use_EatQty EatAnim'den sonra her hâlükârda
  ConsumeAmount cagirir (:913). Iki iddia da referansa gore yeniden yazildi.
  **Bilincli sapma, kayda gecirildi:** EatAnim her local'e mevcut stat'i ekler ve
  toplami UpdateStatVal'e verir; o da mevcut degere BIR KEZ DAHA ekler
  (CCharAct.cpp:757). Bu, her ogunde eldekini ikiye katlar. Duz kazanim uygulandi;
  gerekce EatEngine icinde ve changelog'da yazili.)

**06E-06H kapanisi:** tam suite **2.535 basarili / 0 basarisiz** (+17). Alti duzeltme
de gecici olarak kapatilarak 11 testin eski davranisi yakaladigi kanitlandi; havuz
birlestirmesi icin ayrica ikinci bir kapatma turu yapildi.

**Acik kalan:** `SOURCE_X_BOLUM_06I_ZEHIRLI_YIYECEK.md` bu partide istenmedi.
Ayrica: Source-X `Use_EatQty` zehirli yiyecegi `SetPoison` ile uygular (:905) -
`EatEngine`'e tasinmadi, 06I'nin konusu. FOODVAL'in itemdef VOLUME yarisi SphereNet'te
karsiliksiz; simdilik FOODVAL etiketi ve 10 varsayilani kullaniliyor.

### 06I-06M - zehirli yiyecek ve ceset diriltme on-kontrolu (6 Eylul 2026)

Kanit raporlari: [06I](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06I_ZEHIRLI_YIYECEK.md),
[06J](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06J_VETERINER_DIRILTME.md),
[06K](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06K_CESET_DIRILTME_MENZILI.md),
[06L](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06L_GECERSIZ_CESET_HEDEFI.md),
[06M](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06M_CESET_BOLGE_KURALLARI.md).
Bes bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-06I-01 (P2)** - Zehirli yiyecek yenirken zehir uygulanmiyordu. (YAPILDI:
  `EatEngine.ApplyFoodPoison`, referansin sirasiyla @Eat'ten ONCE (CCharUse.cpp:905).
  **Raporun uyarisi karsilandi:** "POISON_SKILL ham beceri degeri ile ApplyPoison'in
  seviye parametresi ayni birim degildir; dogrudan byte cast yapilmamali" - zehirli
  silah yolunun zaten kullandigi `CombatEngine.CalcOsiPoisonLevel` bantlamasi
  kullanildi, yeni bir eslem uydurulmadi.
  **Yan sonuc:** tok pet hic lokma almadigi icin zehirlenmiyor - referansta da zehir
  Use_EatQty'nin doygunluk reddinden sonra gelir.)
- [x] **SX-06J-01 (P2)** - Savas modunda olmayan olu bonded pet diriltilemiyordu.
  (YAPILDI: kapi artik `target.IsPlayer && !target.IsInWarMode`.
  **Kaynak analizi:** referansin kapisi savas modu degil `STATF_INSUBSTANTIAL`;
  olum bunu yalnizca savas modunda OLMAYAN OYUNCUYA kurar (CCharAct.cpp:4468), NPC'ye
  asla. SphereNet'te hayalet-insubstantial diye bir modelleme yok - `ClientViewUpdater`
  savas modunu manifest sinyali olarak kullaniyor - bu yuzden oyuncu yarisi aynen
  korundu, NPC yarisi kaldirildi.
  **Acik kalan:** SphereNet olumde `Insubstantial` bayragini hic kurmuyor; referansla
  tam hizalama ayri bir hayalet-durum karari.)
- [x] **SX-06K-01 (P2)** - Ceset hedefli diriltme hayaletin konumunu dogrulamiyordu.
- [x] **SX-06L-01 (P2)** - Cozulemeyen ceset hedefi sessizce self-heal oluyordu.
- [x] **SX-06M-01 (P2)** - Cesedin bolgesindeki antimagic bayraklari uygulanmiyordu.
  (Ucu birlikte YAPILDI: yeni `IsCorpseResurrectable` yardimcisi referansin
  CItemCorpse.cpp:28-75 kosullarini tek yerde topluyor ve maliyetten once calisiyor -
  Skill_Healing bunu CCharSkill.cpp:2796'da yapar. Mevcut kap kontrolu de oraya
  tasindi.
  **Raporlarin uyarilari:** sifaci-ceset kontrolleri KALDIRILMADI, hayalet-ceset
  kontrolleri onlarin USTUNE eklendi (06K). `Character.Resurrect` icine Recall/
  NoTeleport eklenmedi - digger diriltme turlerini bozmasin diye ceset yoluna ozel
  on-kontrol tercih edildi (06M). Hedef verilmemis self-heal varsayilani korundu,
  yalnizca "secilmis hedef gecersiz" ondan ayrildi (06L).)

**06I-06M kapanisi:** tam suite **2.550 basarili / 0 basarisiz** (+15). Bes duzeltme de
gecici olarak kapatilarak 10 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test guncellenmedi.

**Acik kalan:** 06M'nin ikinci gozlemi - son asama diriltme reddinin `Healing`
tarafindan basari sayilmasi - bolge on-kontrolu sayesinde bu senaryoda ortadan kalkti,
ama `ResurrectTarget` sozlesmesi hala void; genel sonuc iletimi ayri bir konu.
Ayrica raporlarin kendi kuyruklari: bonding saati, MAXFOOD tanimlari, olu bonded
release, resurrection trigger sonucu, hayalet LOS'unun yanlis aktorle denetlenmesi.

### 06N-06O - MAXFOOD sinirlari ve pet devir temizligi (6 Eylul 2026)

Kanit raporlari: [06N](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06N_MAXFOOD_SINIRLARI.md),
[06O](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06O_PET_DEVIR_TEMIZLIGI.md).
Uc bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi. 06N, bir onceki turda
eklenen `Character.MaxFood` uzerinde - yani kendi yeni kodumun denetimi.

- [x] **SX-06N-01 (P2)** - CHARDEF MAXFOOD=0 etkili kapasitede 60'a donusuyordu.
  (YAPILDI: `CharDef.MaxFoodExplicit` eklendi; `Character.MaxFood` artik instance
  etiketi (yalniz pozitif) -> acik CHARDEF MAXFOOD (sifir dahil) -> 60 sirasini
  izliyor. Referans instance maksimumu 1'in altindayken tanima doner
  (CCharStat.cpp:276) ve sifir maksimumda Use_Eat besleme reddeder (:934).
  **Guvenlik olcumu - naif uygulama tehlikeliydi:** referans FOODTYPE'tan TURETILEN
  maksimuma da doner. Canli pakette insan/elf/gargoyle chardef'leri
  `FOODTYPE=t_food,t_drink,t_fruit,t_grain` yaziyor; sayisiz girdiler 1 turetiyor
  (`DeriveMaxFood`, referansta `GetResQty()` varsayilani 1). O yari da uygulansaydi
  HER OYUNCUNUN yiyecek tavani 1'e duserdi. Bu yuzden tanimdan yalnizca ACIK MAXFOOD
  cozuluyor; turetilen yari bilincli sapma olarak kayda gecirildi.
  Raporun kendisi de yalnizca acik MAXFOOD tanimlariyla (0/30/100) deney yapmisti.)
- [x] **SX-06N-02 (P3)** - MAXFOOD>60 yaratik 60 toklukla doguyordu.
  (YAPILDI: spawner artik MAXFOOD etiketini Food'dan ONCE yaziyor.
  **Durust not:** bu bulgunun belirtisi zaten 06N-01 tarafindan cozuluyor - tavan
  chardef'ten cozulunce spawn yolunda sira onemsizlesiyor. Kapatma turunda
  `AHeartyDefinitionSpawnsAtItsFullCeiling` sira geri cevrildiginde de GECTI, bunu
  fark edip sirayi bagimsiz sabitleyen ayri bir test yazdim
  (`AnInstanceCeilingMustBeSetBeforeTheValueItCaps`). Yeniden siralama, tavani
  yalnizca instance etiketinden gelen yaratiklar icin hala gerekli.)
- [x] **SX-06O-01 (P2)** - Devredilen pet eski arkadaslari ve bonded durumunu
  tasiyordu. (YAPILDI: `TryAssignOwnership` gercek sahip degisiminde `ClearFriends()`
  ve `IsBonded = false` yapiyor - referans NPC_PetSetOwner uzerinden
  NPC_PetClearOwners cagirir (CCharNPCPet.cpp:600 -> :558).
  **Raporun uc kosulu da karsilandi:** temizlik kapasite kontrolunden SONRA
  (reddedilen devir peti oldugu gibi birakiyor); AYNI sahibin yeniden atanmasinda
  hicbir sey degismiyor (ahir/figurin/dismount yollari); yeni sahip kendi
  arkadaslarini ekleyebiliyor.)

**06N-06O kapanisi:** tam suite **2.563 basarili / 0 basarisiz** (+13). Duzeltmeler
gecici olarak kapatilarak 4 testin eski davranisi yakaladigi kanitlandi; sirali
kapatma turu ayrica 06N-02'nin testinin yetersiz oldugunu ortaya cikardi ve test
guclendirildi.

**Acik kalan:** raporun not ettigi `TickBonding` cagrisizligi - src icinde tanim
disinda cagrilmiyor. Bunun eksik native davranis mi yoksa script tarafindan yonetilen
tasarim mi oldugu Source-X script politikasi dogrulanmadan hata sayilmadi; ayni
degerlendirme burada da gecerli, dokunulmadi. FOODTYPE-turetilen maksimum yarisi da
yukarida kayitli bilincli sapma.

### 07A-07D - kapi, dikey kapi, ozel tanim ve kol-link (6 Eylul 2026)

Kanit raporlari: [07A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07A_PORTCULLIS.md),
[07B](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07B_OZEL_KAPI_TANIMI.md),
[07C](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07C_KAP_ICINDE_KAPI.md),
[07D](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07D_KOL_KAPI_LINK.md).
Bes bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

**Ortak kok:** Source-X'te birbirinden AYRI uc yordam var - `Use_Portculis` (dikey,
Z hareketi), `Use_DoorNew` (ozel grafik + MOREP), `Use_Door` (klasik mentese) - ve
ikisi de top-level kontroluyle basliyor. SphereNet hepsini tek menteseli rutine
katlamisti; bes bulgunun besi de bu katlamadan cikiyor.

- [x] **SX-07A-01 (P2)** - Portcullis yukseklikleri kullanilmiyor, yalniz grafik
  degisiyordu. (YAPILDI: `UsePortcullis` - MORE1/MORE2 arasinda Z hareketi, grafik
  degismiyor, iki yukseklik esitse no-op (:4596), PORTCULISSOUND override (:4602).)
- [x] **SX-07A-02 (P2)** - Kilitli portcullis normal oyuncunun kullanimini kabul
  ediyordu. (YAPILDI: GM degilse ve link uzerinden gelmiyorsa ret; referansta
  `case IT_PORT_LOCKED` FALLTHROUGH ile `IT_PORTCULIS`'e duser (CCharUse.cpp:1771).)
- [x] **SX-07B-01 (P2)** - DOOROPENID desteklenmiyordu. (YAPILDI: `Item.DoorOpenId`
  (tag-destekli, persist ve scriptten yazilabilir) + `UseCustomDoor`. Grafik takasi ve
  MOREP kaymasi referanstaki gibi; yerine gecen grafik DOOROPENID'ye yaziliyor (:4681).
  **Uygulamada cikan tuzak:** ilk denemede ozel kapi ikinci kullanimda geri gelmiyordu -
  durum klasik `GetDoorDir` tablosundan okunuyor ve alternatif grafik (0x06A5) o
  tabloda KAPALI yuvaya denk geliyordu. Referans `ATTR_OPENED` bayragini okur; ozel
  kapida durum artik yalnizca DOOR_OPEN bayragindan cozuluyor.)
- [x] **SX-07C-01 (P2)** - Kap icindeki kapi haritaya birakiliyordu. (YAPILDI: hem
  `ToggleDoor` hem `Item.CloseDoor` basinda top-level reddi; raporun uyardigi gibi
  yalniz dclick tarafina eklemek timer yolunu acik birakirdi.)
- [x] **SX-07D-01 (P2)** - Kol grafigi degisiyor ama bagli kapi calismiyordu.
  (YAPILDI: `FollowItemLinks` - 64 adim siniri, kayip hedefte ve baslangica donuste
  durma (CCharUse.cpp:1962).
  **Raporun uyarisi karsilandi:** "butun linked hedeflere dogrudan HandleDoubleClick
  cagirmak olmamalidir" - zincir BAGLI kullanim uyguluyor: kapi icin just-open
  (:4641, ikinci cekiste kapanmiyor), kilitli kapi icin link yetkisi (:1771).
  **Bilincli kapsam:** referans MASK_RETURN_FOLLOW_LINKS'i HER item kullaniminda
  doner; zincir simdilik yalniz switch'ten takip ediliyor. Her cift tiklamaya baglamak
  canli shard'larda beklenmedik zincirler uretebilirdi - acik madde olarak birakildi.)

**07 kapanisi:** tam suite **2.579 basarili / 0 basarisiz** (+16). Bes duzeltme de
gecici olarak kapatilarak 10 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test guncellenmedi - 07A raporunun "NpcDoorOpeningTests icinde +2 grafik
bekleyen test var" uyarisi icin kontrol edildi: o testler NPC yolundaki
`DoorHelper.TryOpenDoorState` uzerinden gidiyor ve dokunulmadi.

**Acik kalan:** `DoorHelper.TryOpenDoorState` (NPC kapi acma) hala portcullis icin +2
grafik varsayimini tasiyor - oyuncu yolu duzeldi, NPC yolu ayri bir tur. Ayrica
itemdef door-switch tanimi (SphereNet'te karsiligi yok), DOOROPENSOUND/DOORCLOSESOUND
override'lari ve link zincirinin genel item kullanimina baglanmasi.

### 07E-07G - NPC adim siniri: capraz kose, eskimis rota, inilen Z (6 Eylul 2026)

Kanit raporlari: [07E](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07E_NPC_CAPRAZ_HAREKET.md),
[07F](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07F_NPC_ESKI_ROTA_SICRAMASI.md),
[07G](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07G_NPC_ROTA_YUKSEKLIGI.md).
Uc bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

**Ortak kok:** ucu de "adimi uygula" sinirinda. Source-X'te bu sinirda uc kosul var,
SphereNet'te hicbiri yoktu. Duzeltme tek paylasilan yola kondu: `CanNpcStepTo`
(capraz yan kareler) ve rota adiminda bitisiklik + yuzey cozumu.

- [x] **SX-07E-01 (P2)** - NPC iki engelin birlestigi koseden gecebiliyordu.
  (YAPILDI: `CanNpcStepTo` caprazda iki dik komsu kareyi de sinar
  (CheckValidMove, CCharStatus.cpp:1988). Direction enum'u referansla ayni duzende -
  tek sayilar capraz - bu yuzden `(dir & 1)` testi birebir tasindi.
  **Uygulamada yakalanan asiri-kisitlama:** ilk surumde yan kare kontrolu CANLI
  KARAKTERI de engel sayiyordu ve mesru capraz adimlari bloke etti (kendi rota
  testlerim duserek bunu gosterdi). Referans yan kareleri `CheckValidMove` ile sinar;
  o karakterleri bilmez - engelleyen canlilar yalnizca HEDEF karede tartilir
  (`CanMoveWalkTo` fCheckChars). `CanNpcMoveTo`'ya `checkChars` parametresi eklendi.)
- [x] **SX-07F-01 (P2)** - Yer degistiren NPC eski rotanin uzak karesine sicriyordu.
  (YAPILDI: onbellekteki adim uygulanmadan once ayni harita + tam 1 kare mesafe
  sarti; degilse rota atiliyor (NPC_WalkToPoint, CCharNPCAct.cpp:463).
  **Raporun uyarisi:** genel `MoveCharacter` API'sine kare limiti EKLENMEDI - teleport
  ve diger yerlestirmeler onu kullaniyor; denetim AI adim katmaninda.)
- [x] **SX-07G-01 (P2)** - Rota adimi A*'in yaklasik Z'siyle uygulaniyordu.
  (YAPILDI: `TryResolveNpcStepZ` ile inilen yuzey adimda cozuluyor; Pathfinder'in
  kendi yorumu Z'sinin yaklasik oldugunu zaten soyluyor.
  **Bilincli sinir:** yuzey bulunamadiginda adim reddediliyor, ama yalniz
  `_world.MapData != null` iken - haritasiz (test) dunyada cozucu her kare icin
  "bulunamadi" der ve kosulsuz ret NPC'yi tamamen hareketsiz birakirdi. Bunu da kendi
  testlerim duserek gosterdi.)

**07E-07G kapanisi:** tam suite **2.591 basarili / 0 basarisiz** (+12). Uc duzeltme de
gecici olarak kapatilarak 5 testin eski davranisi yakaladigi kanitlandi. Mevcut hicbir
test guncellenmedi.

**Acik kalan:** raporlarin kendi kuyruklari - kapali kapi ve statik harita duvarlariyla
capraz kural, pet/savas takip akislarinin ayri calistirilmasi, merdiven/multi
yuzeylerinde onbellekteki Z, ve tasima ile rota uretiminin ayni tick'e denk gelmesi.

### 07H-07K - NPC carpisma Z bilinci, gercek adim geometrisi, hareket izni (6 Eylul 2026)

Kanit raporlari: [07H](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07H_NPC_KATLAR_ARASI_ENGEL.md),
[07I](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07I_NPC_UST_KAT_NESNELERI.md),
[07J](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07J_NPC_GECERSIZ_DOGRUDAN_ADIM.md),
[07K](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07K_NPC_HAREKET_KISITLARI.md).
Dort bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

**Raporlarin gormedigi baglanti:** 07J'nin duzeltmesi 07H ve 07I'nin DOGRUDAN-ADIM
yarisini da kapatiyor. Raporlarin kendi olcumleri bunu gosteriyordu - ayni geometride
`WalkCheck` dogru cevap veriyor, yanlis cevap veren tur-tabanli NPC kontroluydu. Bu
yuzden uc ayri Z filtresi yamamak yerine dogrudan adim paylasilan denetime baglandi;
Z filtreleri de A* tarafi ve tile-seviyesi kontrol icin ayrica eklendi.

- [x] **SX-07H-01 (P2)** - Ust kattaki karakter alt kati engelliyordu. (YAPILDI:
  `SharesHeightWith` - referansin bes Z erisimi (CCharAct.cpp:4622); hem
  `CanNpcMoveTo` hem `GameWorld.IsPathTileBlockedByObject` kullaniyor.)
- [x] **SX-07I-01 (P2)** - `IsStaticBlock` butun dikey sutunu kapatiyordu. (YAPILDI:
  `BlocksAtHeight` - esyanin Z + yuksekligi ile yaratigin hacmi kesisiyor mu;
  `Item.DefHeight` 07B turunda eklenmisti, burada ise yaradi. Iki yolda da gecerli.)
- [x] **SX-07J-01 (P2)** - Dogrudan adim gercek yurume geometrisini calistirmiyordu.
  (YAPILDI: `TryNpcStep` - harita verisi varken `WalkCheck.CheckMovement` hem izni hem
  INILEN Z'yi veriyor; onbellekteki A* adimi da ayni yoldan gecip landing ile
  dogrulaniyor. Boylece 07G'de yakalanan "yutulan Found=false" da kapandi.
  **Kapsam:** harita verisi yoksa (test dunyalari) `CheckMovement` her seyi reddettigi
  icin eski tile kontrolleri + capraz kose kurali devrede kaliyor.)
- [x] **SX-07K-01 (P2)** - Freeze/Stone/sifir stamina NPC yuruyordu. (YAPILDI:
  `CanNpcMove` gercek adimda; GM muaf, Freeze/Stone ret, canli yaratikta stamina.
  **Referans ayrintisi:** stamina kapisi MEVCUT havuzu okur (`Stat_GetVal(STAT_DEX)`),
  temel Dex'i degil - 03C'de kurdugum ayrimin aynisi.
  **Kendi testlerimin yakaladigi tehlike:** ilk surum `Stam <= 0` diyordu ve
  `Dex` verilmemis NPC'leri (MaxStam 0, havuz hic doldurulmamis) tamamen dondurdu -
  tam paket 7 gerileme verdi. Kural `MaxStam > 0` ile sinirlandi; stamina modeli
  olmayan yaratik bitkin sayilmiyor.)

**07H-07K kapanisi:** tam suite **2.609 basarili / 0 basarisiz** (+18). Dort duzeltme
de gecici olarak kapatilarak 9 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test guncellenmedi.

**Test yazarken duzelttigim iddia:** engelleme testlerini once "NPC yerinde kalir"
diye yazmistim; gercekte NPC engelli kareye GIRMIYOR ama yanindan capraz dolasabiliyor.
Iddia "engelli kareye girmedi" olarak duzeltildi - dogru olan da bu.

**Acik kalan:** raporlarin kendi kuyruklari - `CAN_C_STATUE` gibi ozel karakter
turleri, ayni kattaki shove/stamina tuketimi, cok katli multi/gemi geometrisiyle uctan
uca takip, NOMOVETILL ve ozel hareket yetenekleri. Ayrica `Wander`/`TrySideStep`
yollarinin kisit kapisini ayri kullanmasi.

### 07L-07Q - guverte/su, gizli hedef takibi, follow trigger, GO ve pet komutlari (6 Eylul 2026)

Kanit raporlari: [07L](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07L_NPC_SU_USTU_PLATFORM.md),
[07M](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07M_PET_GIZLI_HEDEF_TAKIBI.md),
[07N](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07N_PET_FOLLOW_TRIGGER.md),
[07O](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07O_PET_GO_TAMAMLAMA.md),
[07P](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07P_SALDIRI_ONCESI_EMIR_KAYBI.md),
[07Q](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07Q_PET_EQUIP_GUC_KOSULU.md).
Yedi bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-07L-01 (P2)** - Yuzey yerine alttaki su arazisine gore yuzme sarti araniyordu.
  (YAPILDI: `StandsOnWater` - su kurali yalnizca adim gercekten suya iniyorsa gecerli;
  `CanNpcEnterTile` ve `Pathfinder.IsWalkable` ayni yardimciyi paylasiyor.
  **Uygulamada cikan ikinci kapi:** yalniz yuzme kuralini duzeltmek yetmedi - kendi
  tanisal testim gosterdi ki `mapData.IsPassable` de guverteyi gormuyor ve adimi yine
  reddediyordu. `CanNpcOccupy` ayrildi: paylasilan yurume denetimi geometriyi zaten
  onayladiginda, kaba land-seviyesi testleri o onayi geri almiyor.)
- [x] **SX-07M-01 (P2)** - Gizli hedefin guncel konumu kosulsuz okunuyordu. (YAPILDI:
  `ResolveFollowPoint` - gorunur hedefte konum guncellenir ve `FOLLOW_LAST_SEEN`
  etiketine yazilir; gizli hedefte son gorulen yer kullanilir.
  **Kapsam notu:** referansin INT tabanli "takibi birakma" olasiligi tasinmadi - raporun
  cekirdek iddiasi takip noktasinin guncellenmemesiydi; olasilik dali acik madde.)
- [x] **SX-07N-01 (P2)** - Uc durumlu trigger sonucu ve degisebilir argumanlar bool'a
  indirgeniyordu. (YAPILDI: `FollowTriggerResult` + `FollowTriggerArgs`; adapter N1/N2/N3
  besliyor ve geri okuyor.
  **Bilincli sapma:** ARGN2 referansin 1'i yerine SphereNet'in kendi mesafesi (2) ile
  besleniyor - referans degeri koymak hicbir sey scriptlemeyen paketlerde petlerin
  yaklasma mesafesini degistirirdi.
  **Kapatilamayan yari, kayda gecirildi:** `ScriptInterpreter` sifiri Default'a esledigi
  icin gercek SCP `RETURN 0` ile fonksiyon sonuna dusmek ayirt edilemiyor; bunu duzeltmek
  butun trigger'larin RETURN 0 anlamini degistirirdi.)
- [x] **SX-07O-01 (P2)** - `Enum.IsDefined` int/byte uyusmazligi ArgumentException
  firlatiyordu. (YAPILDI: `byte.TryParse` + byte overload. Gercek bir istisnaydi.)
- [x] **SX-07O-02 (P2)** - Bos hedefe bir kare kala varilmis sayiliyordu. (YAPILDI:
  `goDist == 0` varis; ulasilamayan son kare icin "adim atilamadiysa emri bitir"
  korumasi - raporun "sonsuz tekrar yaratma" uyarisi.
  **Mevcut test guncellendi:** `NpcPetGoOrderTests` `distance <= 1`'i varis sayiyordu;
  raporun kendisi de bunun degismesi gerektigini yaziyordu.)
- [x] **SX-07P-01 (P2)** - Saldiri emri kaydettigi onceki modu hemen siliyordu.
  (YAPILDI: `SupersedePendingPetOrder` artik PREV yaziminin ONUNDE.
  **Kendi regresyonum:** bu cagriyi 06A turunda eklemistim ve PREV yaziminin arkasina
  koymusum. 07O ile birlikte ele alinmasi gerekiyordu - PREV geri geldigi anda 07O'daki
  enum istisnasi gorunur hale gelirdi; iki duzeltme ayni turda yapildi.)
- [x] **SX-07Q-01 (P2)** - Pet equip komutu `CanEquip`'i atliyordu. (YAPILDI: komut once
  ortak uygunlugu soruyor; reddedilen esya cantada kaliyor ve tarama devam ediyor.)

**07L-07Q kapanisi:** tam suite **2.633 basarili / 0 basarisiz** (+24). Yedi duzeltme de
gecici olarak kapatilarak 15 testin eski davranisi yakaladigi kanitlandi.

**Acik kalan:** gercek SCP RETURN 0 ayrimi (yorumlayici seviyesinde); referansin INT
tabanli takibi birakma olasiligi; `Wander`/`TrySideStep` ve savas takibinin gizli hedef
kurali; multi gemi guvertesinde hareket eden zemin; iki el catismasi ve ekipman
paketleri.

### 07R-07U - pet ekipmani: iki el, trigger, yigin ve drop all (6 Eylul 2026)

Kanit raporlari: [07R](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07R_PET_IKI_EL_KUSANMA.md),
[07S](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07S_PET_EQUIP_TRIGGER.md),
[07T](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07T_PET_STACK_KUSANMA.md),
[07U](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07U_PET_DROP_ALL_EKIPMAN.md).
Dort bulgu da bagimsiz olarak dogrulandi; hepsi ayni `equip`/`drop` dallarinda oldugu
icin tek turda uygulandi. Sozlu equip artik referanstaki `ItemEquip` sirasini izliyor.

- [x] **SX-07R-01 (P2)** - Pet ayni anda kilic ve iki elli yay tutabiliyordu. (YAPILDI:
  `OtherHandIsTaken` - HAND2'ye giden silah HAND1'i, HAND1 equip'i HAND2'deki silahi
  gozetiyor; kalkan disarida.
  **Raporun otesinde bulunan:** `Character.Equip` iki elli silahi OneHanded'dan
  TwoHanded'a terfi ettiriyor, yani tiledata'sina gore tek elli gorunen bir yay eski
  kodda dogrudan `Equip`'e dusup kalkani cantaya iterdi. Katman artik terfi SONRASI
  puanlaniyor.
  **Bilincli tercih:** dolu el soyulmuyor, esya atlaniyor - referansta ItemEquipWeapon
  zaten yalnizca iki el de bosken silah arar (CCharUse.cpp:2051); raporun "lanetli/
  tasinamaz esyayi zorla cikarma" uyarisi da bu yonu isaret ediyor.
  **Kurulumda cikan tuzak:** `Item.IsTwoHanded` giyili esyada KATMANDAN okunuyor, yani
  TwoHanded'daki kalkan da "iki elli" gorunuyor. Kars el testi bu yuzden yalnizca
  `IsWeaponType` soruyor - ilk surumum kalkan+kilic ikilisini bozmustu, test yakaladi.)
- [x] **SX-07S-01 (P2)** - Sozlu equip @EquipTest ve @Equip tetiklemiyordu. (YAPILDI:
  veto once, hicbir sey tasinmadan; @Equip esya giyildikten sonra. Callback esyayi
  silerse veya cantadan cikarirsa esya kusanilmiyor - referansin CCharAct.cpp:3331
  denetimi.)
- [x] **SX-07T-01 (P2)** - Yigin butun halinde kusaniliyordu. (YAPILDI: `UnStackSplit(1)`
  karsiligi - giyilen parca asil kimligi korur, kalan tam kopya olarak cantada kalir.
  Raporun kendi ek istegi olan tag/dayaniklilik korunmasi da testte.)
- [x] **SX-07U-01 (P2)** - `drop all`, `drop` ile ayni canta dongusune indirgenmisti.
  (YAPILDI: once canta yere dokuluyor, SONRA ekipman cantaya aliniyor - raporun ozellikle
  uyardigi sira. Bos canta artik komutu erken bitirmiyor.
  **Raporun otesinde ele alinan iki yan kural:** conjured yaratik `drop all` ile hicbir
  sey birakmiyor (CCharAct.cpp:567) ve iki drop komutu da ATTR_OWNED/NEWBIE/MOVE_NEVER/
  CURSED2/BLESSED2 esyalarini cantada birakiyor (CContainer.cpp:502). Rapor ikisini de
  "ayrica dogrulanmadi" diye ayirmisti; ayni referans fonksiyonunda olduklari icin
  birlikte kapatildi.)

**Bulunan ve duzeltilen kendi hatam:** 07O-02 regresyon testi
(`AnUnreachableLastTileEndsTheOrderRatherThanRetryingForever`) CI'da kizardi. Sebep uretim
kodu degil testin kendisiydi: engellenen adima verilen yan adim rastgeledir (referansin
kendi zari, CCharNPCAct.cpp:497), yani emrin bitmesi %30'luk bir role bagliydi ve yerelde
10 tikta oluyordu, CI'da olmadi. Test artik yaratigi sekiz komsusuyla kapatiyor; sonuc
zara birakilmiyor. Uretim davranisi degismedi.

**07R-07U kapanisi:** tam suite **2.650 basarili / 0 basarisiz** (+17). Dort duzeltme de
gecici olarak kapatilarak 13 testin eski davranisi yakaladigi kanitlandi (ilk kapatma
denemem operator onceligi yuzunden ATTR filtresinin yalnizca ilk kosulunu kapatmisti;
duzeltilip tekrarlandi).

**Acik kalan:** 07T raporunun onerdigi tam canta / ikinci kez equip / gercek ITEMDEF
CAN_I_PILE varyantlari; `drop all` icin summoned+ATTR birlesimleri ve saci/sakali
olan pet; equip taramasinin referanstaki "en iyi silahi sec" puanlamasi
(NPC_GetWeaponUseScore) - SphereNet hala canta sirasina gore ilk uyani aliyor.

### 07V-07X - oyuncu kusanma yasam dongusu: obur el, surukleme sonu, ret yolu (6 Eylul 2026)

Kanit raporlari: [07V](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07V_OYUNCU_KILIC_KALKAN.md),
[07W](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07W_KUSANMA_SONRASI_SURUKLEME.md),
[07X](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07X_KUSANMA_MACRO_RET.md).
Uc bulgu da bagimsiz olarak dogrulandi. 07X'in kendi uyarisi ("basari ve ret yollari
07W ile birlikte ele alinmali") dogru cikti: ucu de tek bir tamamlama noktasinda
birlestirildi.

- [x] **SX-07V-01 (P2)** - `Item.IsTwoHanded` katmani tek basina yeterli sayiyordu.
  (YAPILDI: once ITEMDEF TWOHANDS, sonra `EquipLayer == TwoHanded && IsWeaponType`.
  Bu, bir onceki turda 07R'yi yazarken pet tarafinda fark edip yalnizca orada
  gecistirdigim tuzagin kaynaktaki karsiligi - raporun da gosterdigi gibi asil yer
  burasiymis; pet tarafindaki yerel geciciye artik gerek yok ama zararsiz oldugu icin
  duruyor.
  **Kapsam denetimi:** `IsTwoHanded`in butun cagrilari gozden gecirildi (NpcAI, combat
  swing gecikmesi, saldiri animasyonu, EquipLastWeapon katmani, `Character.Equip`
  terfisi) - hepsi zaten bir SILAH tasiyor, tek davranis degisikligi kalkanlarda.)
- [x] **SX-07W-01 (P2)** - Basarili `HandleItemEquip` suruklemeyi kapatmiyordu.
  (YAPILDI: `SettleEquipDrag` - referans equip istegini dogrular dogrulamaz surukleme
  modunu kapatir, receive.cpp:542.)
- [x] **SX-07X-01 (P2)** - Reddedilen esya katmansiz/capsiz/suruklemesiz kaliyordu.
  (YAPILDI: ret yolunda `RestoreToOrigin` + surukleme imleci iptali - referansin
  `Event_Item_Drop_Fail`i, CClientEvent.cpp:248.
  **Raporun otesinde kapatilan iki nokta:** (1) `TryDClickEquip` hedef katmani
  kusanmadan ONCE bosaltiyordu, yani reddedilen equip tasiyicinin elini bosaltiyordu -
  referansta guc sarti katman catismasina dokunulmadan once cozulur
  (CCharStatus.cpp:333); katman bosaltma artik kapilarin arkasinda ve paket yolunda da
  calisiyor. (2) `EquipLastWeapon` makrosu silahi cantadan cikarip suruklemesiz
  birakiyor; ret halinde bu esya da ortada kaliyordu - `ItemBounce` karsiligi cantaya
  donuyor.)

**07V-07X kapanisi:** tam suite **2.662 basarili / 0 basarisiz** (+12). Uc duzeltme de
gecici olarak kapatilarak 9 testin eski davranisi yakaladigi kanitlandi.

**Acik kalan:** 07W/07X raporlarinin kuyruklari - script kusanma sirasinda nesneyi
tasirsa/silerse, save/load ile yarim surukleme, gercek cift tiklama girisi, makro
listesinde ilk esya reddedilip sonrakinin kusanilmasi, dolu/silinmis baslangic cantasi.

### 07Y - envanter/esya kullanimi toplu tur: 6 bulgu (6 Eylul 2026)

Kanit raporu: [07Y](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07Y_ENVANTER_ESYA_TOPLU.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-07Y-01 (P2)** - Cikarma makrosu @Unequip'i atliyordu. (YAPILDI: trigger
  makroda da calisiyor.
  **BILINCLI SAPMA - kayit:** referans OnRemoveObj'de reddedemez ("It can not FAIL!"),
  raporun kendisi de bunu veto sozlesmesi saymamak gerektigini yaziyor. Yine de RETURN 1
  burada da ret sayiliyor: SphereNet'in pickup yolu bunu zaten yapiyor ve makroyu
  notification'a birakmak, o reddi asmanin yolu olurdu. Parite turunda geri
  "duzeltilmemeli"; degistirilecekse iki yol birlikte degistirilmeli.)
- [x] **SX-07Y-02 (P2)** - Makas yalnizca tur alanini degistiriyordu. (YAPILDI: cikti
  esyasi yaratiliyor, renk/adet tasiniyor, girdi siliniyor; kumas+giysi -> bandaj,
  hide -> TDATA1 derisi ya da duz deri.
  **Ayrica:** kanli bandaj makas dalindan cikarildi ve referanstaki su yolu eklendi
  (dclick -> hedef -> su). Raporun isaret ettigi mevcut test
  `Scissors_BloodyBandageStack_IsCleanedNotDeleted` uyduruk davranisi kodluyordu;
  yerine uc test geldi (suda temizlenir / kuru hedefte temizlenmez / makas dokunmaz).
  **Kapsam disi:** cloth bolt -> cloth (`ConvertBolttoCloth`) RESOURCES tanimi ister;
  rapor da kanit saymamisti - dokunulmadi.)
- [x] **SX-07Y-03 (P2)** - Aktif anahtar yolu yalniz `TAG.LINK == hedef UID` ariyordu.
  (YAPILDI: `KeyFits` - kilit kodu hedefin kendisi VEYA baglandigi yapi; `HandleKeyUse`
  ve `FindBackpackKeyFor` ayni cozumlemeyi paylasiyor. Test gercek
  `HousingEngine.CreateHouseKey` ile uretilen anahtari kullaniyor.)
- [x] **SX-07Y-04 (P2)** - Kova rengi yerine DYE_HUE etiketi uygulaniyordu. (YAPILDI:
  kovanin kendi Hue'su otorite; eski kayitlardaki etiket yalnizca kovanin kendi rengi
  yokken okunuyor - raporun istedigi "bayat etiket yeni rengi ezmesin" gecis kurali.)
- [x] **SX-07Y-05 (P2)** - Boyamada sahiplik/uygunluk denetimi yoktu. (YAPILDI:
  ust duzey sahip aktor olmali + Clothing/DYE/CAN_I_DYE; GM istisnasi korundu.)
- [x] **SX-07Y-06 (P2)** - @Dye ARGN1 geri okunmuyordu. (YAPILDI: TriggerArgs yerelde
  tutuluyor, N1 renge yaziliyor. **Ek:** referansin ARGN2 ses sozlesmesi de kuruldu -
  0x23E ile beslenip script degistirebiliyor; rapor bunu olcmemisti.)

**Mevcut testlerin duzeltilmesi:** raporun isaret ettigi iki beklenti referansa aykiriydi
ve degistirildi - makasla kanli bandaj temizleme (yukarida) ve
`GameClient_DyeVatApply_FiresDyeTriggerAndCanApplyHue`, ki normal oyuncunun YERDEKI genel
bir nesneyi boyamasini bekliyordu. Ikincisi artik oyuncunun kendi cantasindaki giysiyi
ve kovanin kendi rengini kullaniyor; @Dye tetiklenmesi iddiasi korundu.

**07Y kapanisi:** tam suite **2.684 basarili / 0 basarisiz** (+22). Alti duzeltme de
gecici olarak kapatilarak testlerin eski davranisi yakaladigi kanitlandi: birlesik
kapatmada 16 kirmizi, ardindan yalniz 07Y-05 kapatilarak uc boyama-hedefi testi ayrica
dogrulandi (birlesik kapatmada kova rengi de sifirlandigi icin o ucu gizliyordu).

**Acik kalan:** cloth bolt donusumu; kova renk secici paketi (0x95) ve save/load; sac
boyasinin dialog/harcama akisi; keyring icindeki anahtarlar ve legacy MORE/lock-code
import'u; @Unequip'in bir makroda birden fazla katman ve nesne silme/tasima varyantlari.

### 07Z - tarim, kovan ve su: 6 bulgu (6 Eylul 2026)

Kanit raporu: [07Z](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07Z_TARIM_KAYNAK_TOPLU.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-07Z-01 (P2)** - Ekimde toprak/agac/mevcut bitki kurallari yoktu. (YAPILDI:
  `HasSoilAt` + agac reddi + mevcut cropun DEGISTIRILMESI; GM istisnasi korundu.
  **Uyarlama notu:** referans toprak aramasini script paketinin TILETYPE tablosundan
  yapar (grass genelde t_dirt DEGILDIR; pakette t_dirt 0x3573 ailesi statiktir).
  SphereNet'te bu tablo yok, bu yuzden sirayla: karedeki dinamik `t_dirt` esya ->
  statiklerin ITEMDEF turu -> `ObjBase.ClassifyTerrainType` (P.TYPE ile ayni kaynak).
  `ClassifyTerrainType` bu yuzden internal yapildi.)
- [x] **SX-07Z-02 (P2)** - TDATA2 buyume asamasi denetlenmiyordu. (YAPILDI: grow id
  sifir degilse "olgun degil" - urun de yok, reset de yok.)
- [x] **SX-07Z-03 (P2)** - @ResourceTest/@ResourceGather hic calismiyordu. (YAPILDI:
  iki asama, referans sirasi ve read-back ile; ResourceGather vetosunda urun siliniyor
  ve crop reset EDILMIYOR.
  **Kapatilamayan yari, kayda gecirildi:** referansin `HALFBAKED` donusu (urunu ayaga
  dusurme) SphereNet'in `TriggerResult`'inda yok; o bosluk kapanmadan uygulanamaz.
  **Raporun otesinde:** ARGN3 (ornege ozel meyve gecersiz kilmasi) icin SphereNet'in
  karsiligi zaten vardi - `PlantDropFruit` MORE2'yi okuyor; hasat yolu da artik ayni
  degeri besliyor.)
- [x] **SX-07Z-04 (P2)** - Urun zorla Food yapiliyordu. (YAPILDI: tur tanimdan geliyor.
  **Raporun ayri saymadigi ikinci yol da kapatildi:** `Item.PlantDropFruit` zamanlayici
  yolunda ayni atama vardi; iki yol farkli tur uretmesin diye birlikte duzeltildi. Bu
  nedenle `SourceXWave270Tests` mature-stage testi urunu Food yerine kendi grafigiyle
  sayacak sekilde guncellendi - raporun "bu beklenti de incelenmeli" notu.)
- [x] **SX-07Z-05 (P2)** - Kovan stok/zamanlayici tanimiyordu. (YAPILDI: MORE1 stogu,
  3'lu zar (bal/balmumu/sokma), 15 dk timeout ve tick'te 5'e kadar dolum.
  **Sapma notu:** sokma referansta `OnTakeDamage(rand(5), POISON|GENERAL)`; SphereNet'te
  eskiden `ApplyPoison(1)` vardi - referansin hasar yolu (`ApplyScriptDamage`) alindi.)
- [x] **SX-07Z-06 (P2)** - Surahi hedefteki su nesnesini gormuyordu. (YAPILDI:
  `ResolveWaterTarget` - dinamik su esyasi / statik / arazi; kanli bandaj yolu da ayni
  yardimciyi kullaniyor.)

**07Z kapanisi:** tam suite **2.704 basarili / 0 basarisiz** (+20). Alti duzeltme de
gecici olarak kapatilarak 14 testin eski davranisi yakaladigi kanitlandi; ardindan yalniz
@ResourceTest asamasi kapatilarak iki trigger testi ayrica dogrulandi (birlesik kapatmada
olgunluk denetimi de kapali oldugu icin biri gizleniyordu).

**Acik kalan:** HALFBAKED donusu; gercek statik toprak ve farkli kat/LOS ile ekim;
ResourceTest'in nesneyi silmesi; kovanda balmumu/sokma dallarinin kontrollu RNG ile
dogrulanmasi ve save/load; gercek statik MUL su.

### 08A - eritme ve onarim: 6 bulgu (6 Eylul 2026)

Kanit raporu: [08A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_08A_ERITME_ONARIM_TOPLU.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-08A-01 (P2)** - Madenin native TDATA1 kulce tanimi okunmuyordu. (YAPILDI:
  oncelik acikca belirlendi - ornek/def TAG.SMELT_TO > ITEMDEF TDATA1 > duz demir;
  raporun "onceligi acik olmali" notu.)
- [x] **SX-08A-02 (P2)** - Basarisiz eritme butun yigini siliyordu. (YAPILDI:
  `ConsumeOreAmount` ile rand(adet/2)+1 kismi kayip + istemci bildirimi.)
- [x] **SX-08A-03 (P2)** - @Smelt sozlesmesi eksikti. (YAPILDI: ARGN1 Mining becerisi,
  ARGN2 kaynak cesit sayisi, ARGN3 minimum-beceri atlatma, LOCAL.resource.0.ID/.amount;
  hepsi geri okunuyor ve verim `adet x perOre` olarak uygulaniyor.
  **Mevcut test guncellendi:** `ItemUseParityTests` @Smelt vetosu ARGN1'i maden ADEDI
  bekliyordu - raporun isaret ettigi yanlis beklenti.)
- [x] **SX-08A-04 (P2)** - @Create birlesmis eski yiginda calisiyordu. (YAPILDI: trigger
  yeni kulcede, teslim/istifleme oncesinde.)
- [x] **SX-08A-05 (P2)** - Onarim ors ve kaynak kosullarini atliyordu. (YAPILDI:
  `HasAnvilNearby` (2 kare; dinamik + statik) ve `CraftingEngine.TryConsumeResourcePart`
  ile test-once/tuket-sonra.
  **Yapisal karar:** kaynak muhasebesi zaten CraftingEngine'de vardi; statik bir engine
  hook'u eklemek yerine `IActiveSkillSink.Crafting` (varsayilani null) ile veriliyor -
  yeni global durum yok, `ResetEngineStatics`'e ekleme gerekmiyor.)
- [x] **SX-08A-06 (P2)** - Onarimda Arms Lore asamasi yoktu. (YAPILDI: uretim
  becerisinden ONCE; basarisizlikta negatif kazanim ve erken cikis, basarida kazanim
  uretim rulosundan hemen once - referans sirasi.
  **Mevcut iki test guncellendi:** ikisi de orssuz/Arms Lore'suz basari bekliyordu.)

**08A kapanisi:** tam suite **2.718 basarili / 0 basarisiz** (+14). Alti duzeltme de
gecici olarak kapatilarak 10 testin eski davranisi yakaladigi kanitlandi.

**Yeni testlerde belirsizligi kaldirma:** onarim/eritme rulolari `Character.OnSkillUseQuick`
ile sabitlendi (raporun kendi yontemi). Bunu once yapmamistim; `ASmithWhoCannotIdentify...`
filtreli kosuda gecip tam suitede kizardi - 0 beceriyle bile can egrisi ara sira basari
veriyor.

**Acik kalan:** cok cesitli kaynak veren esyalarin eritilmesi (RESOURCES listesi >1);
gem ciktisi; kulce SKILLMAKE minimum-beceri araligi; onarimda kismi/coklu kaynak,
statik ors ve ic ice cantalar; skillgain miktarlari.

### 08B - hedefli esya kullanimi: 6 bulgu (6 Eylul 2026)

Kanit raporu: [08B](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_08B_HEDEFLI_KULLANIM_TOPLU.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **08B-1 (P2)** - Balik kesmede yalnizca erisim deneniyordu. (YAPILDI:
  `CanConsumeTarget` = referansin CanUse(hedef, MOVE) karsiligi; donusum de artik
  yerinde - yeni yigin uretmek baskasinin baligini kesenin eline tasiyordu.)
- [x] **08B-2 (P2)** - Kirkim yun yenilenmesini baslatmiyordu. (YAPILDI: `Layer.FlagWool`
  (=46, referansin kendi numarasi) uzerinde sureli isaret esyasi; suresi dolunca beden
  geri donuyor.
  **Yapisal not:** SphereNet'in Layer enum'u 31'de bitiyordu; referansin dahili katman
  bloguna yalnizca ihtiyac duyulan giris eklendi. `Layer.Qty` degistigi icin
  `ItemInventoryParityTests.Layer_MatchesUoLayerTypeNumbering` guncellendi - eski
  `Qty == 32` iddiasi SphereNet'in kesilmis modelini kodluyordu, referansta da 32 degil.
  **Raporun otesinde:** dahili katmanlar artik istemciye gonderilmiyor
  (`SendItemVisualUpdate` > Layer.Horse guard) - referans "don't bother sending these".
  Ayrica bicak dagiticisi kirkilmis koyunu da yanitliyor, yoksa yeni uyari mesaji
  erisilemez kalirdi.)
- [x] **08B-3 (P3)** - Yatak rulosu acilip kapanmiyordu. (YAPILDI: 0A57/0A58/0A59 ->
  acik, 0A55/0A56 -> kapali; cantadaki rulo icin "once yere koy". Tanimadigi grafik
  eski Camping davranisini koruyor.)
- [x] **08B-4 (P2)** - Top doldurmada hedef/mermi dogrulanmiyordu. (YAPILDI: muzzle icin
  erisim, mermi icin `CanConsumeTarget`.)
- [x] **08B-5 (P2)** - Meyve/ham reaktiften tohum dali yoktu. (YAPILDI: `CutSeedFrom` -
  DEFAULTSEED grafigi, IT_SEED turu ve "<ad> seed" adi, yerinde.)
- [x] **08B-6 (P2)** - Cikrik iki saniyelik mesgul duruma gecmiyordu. (YAPILDI:
  `Item.SetAnim`/`EndAnim` - onceki grafik MORE1'de, onceki tur MORE2'de, tur
  `AnimActive`, timer 2 sn; `Item.OnTick` geri donduruyor. `AnimActive` dclick mesaji
  zaten vardi, onu kuran yoktu.)

**08B kapanisi:** tam suite **2.736 basarili / 0 basarisiz** (+18). Alti duzeltme de
gecici olarak kapatilarak 15 testin eski davranisi yakaladigi kanitlandi.

**Kendi testimi duzelttim:** ilk kapatma kosusunda "sabit balik" ve "baskasinin baligi"
testleri yesil kalmisti - eski kod baligi silip filetoyu cantaya koydugu icin
`fish.ItemType` silinmis nesnede hala Fish okunuyordu. Iddia `IsDeleted == false` ve
"cantada fileto yok" olarak guclendirildi.

**Acik kalan:** yun sayisinin script tanimlarina gore degerlendirilmesi ve save/load
sonrasi yenilenme; cikrik anim durumunun kayittan donmesi; yatak rulosunun 1F24-1F27
ailesi; top atisi hasari; DEFAULTSEED'in gercek paketle cozulmesi.

### 08C - antrenman, icki ve iletisim: 6 bulgu (6 Eylul 2026)

Kanit raporu: [08C](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_08C_EGITIM_ICECEK_ILETISIM.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **08C-1 (P2)** - Yankesicilik antrenman esyasi calinabiliyordu. (YAPILDI:
  `TrainOnPickpocketDip` - yer/mesafe/binek/antrenman siniri, rulo ve `SetAnim`; esya
  yerinde kaliyor.
  **Kapsam notu:** referans bunu iki asamali NPCACT_TRAINING isi olarak kurar
  (baslat/bitir); SphereNet'te oyuncu icin o eylem makinesi yok, bu yuzden rulo ve
  animasyon tek adimda - antrenman zamanlamasi acik kaldi.)
- [x] **08C-2 (P2)** - Ok hedefinden muhimmat geri alinamiyordu. (YAPILDI:
  `GatherButteAmmo`, beceriye yonlenmeden once.)
- [x] **08C-3 (P2)** - Yirtilan ag uydurma kaynak birakiyordu. (YAPILDI: yalnizca silme.
  **Mevcut test guncellendi:** `ParityWaveH3Tests.Web_Struggle_DestroysWebLeavesSilk...`
  bu uydurma ciktiyi bekliyordu - raporun isaret ettigi yanlis beklenti.)
- [x] **08C-4 (P2)** - @Drink sozlesmesi yoktu. (YAPILDI: yeni `CharTrigger.Drink`,
  ARGN1/ARGN2/LOCAL.BottleId ve geri okuma; veto icmeyi durduruyor.
  **Kapsam:** referansin ELSEIF/HALFBAKED dallarindaki bos-kap adedi SphereNet'in
  `TriggerResult`'inda karsiliksiz - acik kaldi.)
- [x] **08C-5 (P2)** - Alkol sarhosluk etkisi baslatmiyordu. (YAPILDI: `SpellType.Liquor`
  dogrudan etkisi, gucu rand(300)+10.)
- [x] **08C-6 (P2)** - Kristal genel hedef imleci kullaniyordu. (YAPILDI:
  `SetPendingItemTarget` - kaynak dogrulamasi ve @TargOn_Item korumasi devreye giriyor.)

**08C kapanisi:** tam suite **2.749 basarili / 0 basarisiz** (+13). Alti duzeltme de
gecici olarak kapatilarak 9 testin (+1 guncellenen mevcut testin) eski davranisi
yakaladigi kanitlandi.

**Kendi testimi duzelttim:** ilk kapatma kosusunda yankesicilik testleri yesil kalmisti -
tezgahta `SkillHandlers` bagli olmadigi icin eski yol zaten hicbir sey yapmiyordu.
Tezgaha gercek beceri hattini bagladim; ancak o zaman eski davranis (esyanin cantaya
gitmesi) testleri kirmiziya cevirdi.

**Acik kalan:** antrenman isinin zamanlamasi ve tamamlanma kazanimi; @Drink'in bos-kap
uretimi; kristalin kendi kendine baglanmasi ve hedef turu kurallari.

### 08D - tuketim, antrenman ve hasar girisleri: 6 bulgu (6 Eylul 2026)

Kanit raporu: [08D](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_08D_TUKETIM_EGITIM_HASAR.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **08D-1 (P2)** - Tok oyuncuda yemek yine tuketiliyordu. (YAPILDI: `EatOneUnit` -
  motorun dondurdugu adet uzerinden tuketim; 0 ise mesaj ve tuketim yok.)
- [x] **08D-2 (P2)** - Tahil/ot son adette tukenmiyor, sabit hedef de doyuruyordu.
  (YAPILDI: Grain/Grass ayni yemek yoluna alindi + CanMove kapisi.
  **Mevcut test guncellendi:** `GrainStackDoubleClick_DecrementsButNeverDeletesLastUnit`
  son adedin korunmasini bekliyordu - raporun isaret ettigi uydurma politika; yerine
  "sonuna kadar yenir" ve "sabit kaynak kimseyi doyurmaz" testleri geldi.
  **Kapsam:** WaterWash referansta Use_Drink'tir, ayri sozlesme - eski yolunda birakildi.)
- [x] **08D-3 (P2)** - Bos oyun tahtasi taslarini olusturmuyordu. (YAPILDI:
  `SetUpGameBoard` - satranc/dama/tavla dizilimleri ve kap koordinatlari; dolu tahta
  korunuyor.)
- [x] **08D-4 (P2)** - Dovus mankeni antrenman islemi baslatmiyordu. (YAPILDI:
  `TrainOnDummy` + `ResolveWeaponSkill` - mesafe/binek/menzil/antrenman siniri, `SetAnim`
  ve silahin kendi becerisinde kazanim.
  **Kapsam notu:** 08C'deki gibi iki asamali NPCACT_TRAINING zamanlamasi modellenmedi.)
- [x] **08D-5 (P2)** - Tuzak hasari @GetHit sozlesmesini atliyordu. (YAPILDI:
  `CombatEngine.ApplyScriptDamage` ile kunt/genel hasar.)
- [x] **08D-6 (P2)** - Tapinak diriltmesi @SpellEffect calistirmiyordu. (YAPILDI: veto
  diriltmeyi durduruyor; @Resurrect zaten calisiyordu.)

**08D kapanisi:** tam suite **2.769 basarili / 0 basarisiz** (+20). Alti duzeltme de
gecici olarak kapatilarak 13 testin eski davranisi yakaladigi kanitlandi.

**Kendi testimi duzelttim:** manken testleri once `OnSkillGain` sayiyordu; kazanim
sansa bagli oldugu icin bu iki yolu ayirt etmiyordu. Olcut mankenin kendi animasyon
durumu yapildi - vurus varsa uc saniyelik AnimActive, ret varsa bos.

**Acik kalan:** WaterWash icme yolu; oyun taslarinin istemcide dizilimi ve tahta
kayit/yukleme; manken/dip antrenman zamanlamasi; tuzak hasarinda zirh/direnc ve olum
sirasi.

### 09A - sohbet kanallari ve moderasyon: 6 bulgu (6 Eylul 2026)

Kanit raporu: [09A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_09A_SOHBET_KANALLARI.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **09A-1 (P2)** - Basarisiz gecis eski kanali kaybettiriyordu. (YAPILDI: once hedef
  denetleniyor, sonra eski uyelik birakiliyor ve ayrilma bildirimi uretiliyor.)
- [x] **09A-2 (P2)** - Katilma komutunda parola okunmuyordu. (YAPILDI: `ParseJoinCommand`
  ("Ad" parola) ve `ParseCreateCommand` (Ad{parola}) ayrildi.)
- [x] **09A-3 (P2)** - DefaultVoice degisimi mevcut uyeleri susturuyordu. (YAPILDI: ses
  uyeye katilirken veriliyor; `CanSpeak` yalnizca uyenin kendi kaydini soruyor.
  **Mevcut test guncellendi:** `ChatSystemTests.ChatEngine_ModeratedChannel_...` eski
  uyenin ayarla sessizlesmesini bekliyordu - raporun isaret ettigi beklenti; yerine
  "eski uye konusmaya devam eder / sonradan gelen sessiz baslar / bireysel susturma
  calisir" geldi.)
- [x] **09A-4 (P2)** - Kurucunun moderatorlugu kaldirilamiyordu. (YAPILDI: `SeatFounder`
  ile kurucu da normal moderator kaydi aliyor; `IsModerator` yalnizca listeye bakiyor.)
- [x] **09A-5 (P2)** - Olusturma ve katilma tek isleme dusmustu. (YAPILDI: `Join(...,
  create)` - olusturmada ad bos olmali, katilmada kanal var olmali.
  **Sozlesme degisikligi:** `ChatEngine.Join` artik otomatik kanal yaratmiyor; mevcut
  motor testlerinin ad-hoc kanal kuran cagrilarina `create: true` eklendi.)
- [x] **09A-6 (P2)** - Kanal listesi degisimleri duyurulmuyordu. (YAPILDI:
  `AnnounceToChat` - olusturma, bosalan kanalin kaldirilmasi ve yeniden adlandirmada
  global kaldir/ekle. Motor `Participants` ve `Exists` verecek sekilde genisletildi;
  `Rename` eski adi geri donduruyor.)

**09A kapanisi:** tam suite **2.780 basarili / 0 basarisiz** (+11). Alti duzeltme de
gecici olarak kapatilarak 9 testin eski davranisi yakaladigi kanitlandi.

**Acik kalan:** kanal silme ve parola degisiminin ilani; raporun isaret ettigi referans
ici tutarsizlik (`RemoveVoice(true)`); parolanin uc durumlari; sohbet penceresi yeniden
acildiginda liste tazeligi.

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
