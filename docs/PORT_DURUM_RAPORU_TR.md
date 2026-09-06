# SphereNet ↔ Source-X Port Durum Raporu

**Tarih:** 2026-09-06
**Ölçüm dayanağı:** `oldSphere/Source-X-full/src` (referans C++ motor, 201.846 satır)
karşısında `src/` (SphereNet, test hariç 148.146 satır / 331 dosya).
**Doğrulama:** `dotnet test src/SphereNet.Tests/SphereNet.Tests.csproj` →
**3064 test, 0 başarısız, 0 atlanan** (1 dk 23 sn).

---

## 0. Ölçüm yöntemi ve güven sınırları

Bu rapordaki yüzdeler **tahmin değil**, Source-X'in kendi tablo dosyalarından
üretilmiş sayımlardır. Source-X, script'e açtığı her yüzeyi `src/tables/*.tbl`
içinde listeler (`triggers.tbl`, `CChar_props.tbl`, `CObjBase_functions.tbl`, …).
Bu listeler çıkarıldı ve her isim SphereNet kaynak ağacında (test dosyaları
hariç) hem tam eşleşme hem önek eşleşmesi (`"TAG0."`, `"VAR."` gibi) ile arandı.

**Yöntemin iki bilinen sapması var, ikisi de raporda düzeltildi:**

- **Yukarı sapma (kapsam):** bir ismin kaynakta bulunması "davranış birebir
  portlandı" demek değildir. Bu yüzden her kategoriye ayrı bir **Sadakat**
  puanı verildi; sadakat sayımla değil, kod okuma + guardrail testleri +
  `docs/reviews/` (100 dosya) + uyduruk-değer denetim geçmişiyle belirlendi.
- **Aşağı sapma (kapsam):** Source-X `SKILL_START`/`@Start` gibi bağlam-bağımlı
  isimleri SphereNet `SkillStart` diye adlandırır. Ham diff bunları "eksik"
  sayar. Trigger ve spell listelerinde alias eşlemesi elle yapıldı; ham sayı ile
  düzeltilmiş sayı ayrı ayrı verildi.

---

## 1. Genel tablo — 100 üzerinden

| # | Kategori | Kapsam | Sadakat | Kanıt |
|---|---|---:|---:|---|
| 1 | Trigger sistemi (`@Trigger`, EVENTS/TEVENTS zinciri) | **89** | **90** | 248 SX trigger, ~221 karşılığı var; ateşlenmeyen backlog = 1 (`@UserVirtue`) |
| 2 | Script motoru / ifade motoru | **85** | **88** | `CScriptObj_functions` 44/54; FEVAL/FLOATVAL/STRSUB/LOCAL/REF ailesi test kilitli |
| 3 | Nesne fiilleri (verbs) | **100** | **88** | `CObjBase/CChar/CItem/CClient_functions` 206/206 dispatch yolu var |
| 4 | Nesne özellikleri (props) | **73** | **85** | `*_props.tbl` toplamı 470/645 |
| 5 | Ağ / paket katmanı | **85** | **88** | 65 kayıtlı gelen handler + alt-dispatch; 109 giden paket sınıfı (SX 124) |
| 6 | Şifreleme / login zinciri | **95** | **92** | Blowfish + Twofish + Huffman + no-crypt, loopback login entegrasyon testi |
| 7 | Karakter / Item çekirdek modeli | **95** | **88** | `IT_*` 207/212, `SKILL_*` 60/60, `SPELL_*` 211/211 (gerçek büyü) |
| 8 | Savaş (combat) | **88** | **86** | Swing state machine, `@Hit*` ailesi, archery/parry/noto; C1–C7 parite dalgaları |
| 9 | Büyü (magic) | **82** | **88** | Magery 64/64 + Necromancy + Chivalry etkili; Bushido/Ninjitsu/Mysticism/Spellweaving enum-only |
| 10 | Skill sistemi | **85** | **90** | 60 skill tanımlı, 56'sı motorda referanslı; `Skill_Experience` birebir portlanmış |
| 11 | NPC AI / pet / vendor beyni | **85** | **82** | 4.913 satır (SX ~4.500); beyin tik temposu bilinçli sapma |
| 12 | Ölüm / ceset / yağma | **90** | **88** | `@Death`/`@Kill`/`@DeathCorpse`, NOCORPSE, 2 aşamalı decay |
| 13 | Vendor / ticaret / stable | **88** | **88** | `@Buy`/`@Sell` RETURN 1 vetosu, ham `0x3B`/`0x9F` roundtrip testi |
| 14 | Crafting / gathering | **85** | **85** | `Skill_MakeItem` band tablosu portlandı, stroke=DELAY×sayı modeli |
| 15 | Bölge / sektör / dünya | **88** | **88** | Region anahtarları 35/40, Sector 22/24 |
| 16 | Kalıcılık (save/load) | **88** | **90** | Klasik 56T save yükleniyor (2.660 NPC + 53 spawner); native `SAVESTATICS` yok |
| 17 | Hesap sistemi | **90** | **88** | `CAccount` anahtarları 44/46 |
| 18 | Gemi (ship) / multi hareketi | **85** | **85** | Diagonal sail, IsOnDeck, `@Ship_*` trigger'ları |
| 19 | Party | **88** | **85** | `CParty_functions` 9/10, `CParty_props` 6/7 |
| 20 | Gump / dialog / hedefleme | **88** | **88** | `CClient_functions` 61/61; dialog layout verb kapsaması script setine göre tam |
| 21 | Konuşma / speech / keyword | **85** | **85** | Command prefix güvenlik kapısı, HasWord kelime eşleşmesi |
| 22 | Harita verisi (`.mul`) | **80** | **85** | map/statics/multi/tiledata okuyucular var; map diff (`USEMAPDIFFS`) yok |
| 23 | Housing / multi script API | **62** | **72** | `CItemMulti` anahtarları 44/71; addon/component/vendor/moving-crate ailesi yok |
| 24 | Chat (conference / global) | **70** | **75** | 304 satır (SX ~1.300); `0xB3`/`0xB5` çalışıyor, `0xB2` legacy + `0xF9` ertelendi |
| 25 | Guild stone menü sistemi | **55** | **70** | `CItemStone_functions` 13/30; gump tabanlı stone menüleri yok |
| 26 | AOS bileşen-prop sistemi (`CCProps*`) | **47** | **80** | 65/139; direnç/regen/slayer/hit-* çekirdeği var, SA/ML/TOL uzun kuyruğu yok |
| 27 | `sphere.ini` konfigürasyon yüzeyi | **58** | **85** | 164/279 anahtar; **en zayıf ölçülen alan** |
| 28 | Sunucu / admin verb'leri (`SERV.*`) | **90** | **85** | 33 `sm_szVerbKeys` girişinin tamamı yollu; `SAVESTATICS` + güvenlik-hassas işler açık |

### Ağırlıklı özet

| Eksen | Puan |
|---|---:|
| **Kapsam** — Source-X yüzeyinin ne kadarı port edildi | **82 / 100** |
| **Sadakat** — port edilen kısım ne kadar doğru | **86 / 100** |
| **Sphere 56x hedefine göre kapsam** (AOS/SA/ML uzun kuyruğu hariç) | **90 / 100** |

Ağırlıklandırma, kategorinin bir shard'ın ayakta durması için gerekliliğine
göre yapıldı: trigger/script/paket/persistence ×3, combat/magic/skill/AI ×2,
housing/chat/guild/AOS-props ×1.

---

## 2. Kategori detayları — nerede ne eksik

### 2.1 Trigger sistemi — 89 / 90

Source-X `triggers.tbl` 248 giriş içeriyor. SphereNet `CharTrigger` + `ItemTrigger`
enum'ları 218 üye tanımlıyor; alias eşlemesinden sonra **~221 Source-X trigger'ının
karşılığı var**.

`TriggerCoverageGuardrailTests` her koşuda "tanımlı ama hiç ateşlenmiyor"
kümesini kaynaktan yeniden hesaplayıp dokümante backlog'a karşı doğruluyor.
Bugün o backlog **tek üye**: `@UserVirtue` (virtue gump'ı yok). Item trigger
backlog'u **boş**.

**Gerçekten eksik olanlar (~27):**

| Trigger | Alan |
|---|---|
| `@AfkMode`, `@Jailed`, `@Load`, `@SendPaperdoll` | oturum/karakter yaşam döngüsü |
| `@CharShove`, `@Falling`, `@ToggleFlying`, `@SeeHidden` | hareket / algı |
| `@PayGold`, `@PetRelease`, `@FollowersUpdate` | ekonomi / pet |
| `@RegenStat`, `@HitReactive` | stat / savaş kenar yolları |
| `@RegionResourceFound`, `@RegionResourceGather`, `@ResourceFound` | kaynak toplama |
| `@ArrowQuest_Add`, `@ArrowQuest_Close` | quest oku |
| `@HouseDesignCommitItem`, `@DelMulti` | custom housing |
| `@ClientTooltip_AfterDefault` ailesi (3 varyant) | AOS tooltip |
| `@itemFire`, `@itemSmelt`, `@itemSpell`, `@itemCarveCorpse` | char üzerindeki item ayna trigger'ları |

Sadakat tarafı güçlü: zincir sırası (`@Char*` → EVENTS → TEVENTS → CHARDEF →
EVENTSPET/PLAYER), `ARGN1/2/3` geri-yazımı, `ARGS`/`ARGO`/`LOCAL` paylaşımı,
`RETURN 1` iptali ve Source-X'in `IsTrigUsed` sıcak-yol kapısı portlanmış.

### 2.2 Script yüzeyi — fiiller 100, özellikler 73

Source-X'in script'e açtığı toplam prop + fonksiyon yüzeyi **872 isim**.
SphereNet'te **676'sı** çözülüyor (%77).

Ayrıştırıldığında tablo net bir şekil alıyor:

| Tablo | Kapsam |
|---|---:|
| `CObjBase_functions` (fiiller) | 57/57 — **%100** |
| `CChar_functions` | 74/74 — **%100** |
| `CItem_functions` | 14/14 — **%100** |
| `CClient_functions` | 61/61 — **%100** |
| `CCharBase_props` | 40/40 — **%100** |
| `CItemStone_props` | 18/18 — **%100** |
| `CSector_functions` | 12/13 — %92 |
| `CStoneMember_props` | 14/15 — %93 |
| `CChar_props` | 105/124 — %84 |
| `CScriptObj_functions` | 44/54 — %81 |
| `CCharPlayer_props` | 25/31 — %80 |
| `CBaseBaseDef_props` | 19/25 — %76 |
| `CCharNpc_props` | 10/14 — %71 |
| `CClient_props` | 13/20 — %65 |
| `CObjBase_props` | 43/73 — %58 |
| `CItem_props` | 45/91 — %49 |
| `CItemBase_props` | 36/83 — %43 |
| `CItemStone_functions` | 13/30 — %43 |

**Okuma:** *fiil* tarafı tamamlanmış, *özellik* tarafında delik var — ve deliğin
büyük kısmı tek bir yerden geliyor: **AOS/SE/ML çağı item özellik sistemi.**
`CItem_props` + `CItemBase_props` eksiklerinin ~35'i `BONUSSKILL1..5`,
`ITEMSET*`, `NPCKILLER`, `NPCPROTECTION`, `RECHARGE*`, `SELFREPAIR`,
`SUMMONING`, `RARITY`, `IMBUE`, `REFORGE`, `ENCHANT`, `RECIPE*` ailesi.
Bir Sphere 56x shard'ı bunları kullanmaz.

**Sphere 56x için gerçekten canını yakacak eksikler:**

| İsim | Neden önemli |
|---|---|
| `MODMAXHITS` / `MODMAXMANA` / `MODMAXSTAM` | script'te sık kullanılan stat tavanı değiştiricileri |
| `CANCAST`, `SPELLTIMEOUT` | büyü kapıları |
| `CANMAKE`, `CANMAKESKILL` | craft kontrolü |
| `SKILLCHECK`, `SKILLTEST`, `SKILLUSEQUICK`, `SKILLBEST`, `SKILLADJUSTED` | skill sorgulama ailesi |
| `FIGHTRANGE`, `SWING`, `DAMADJUSTED` | savaş sorguları |
| `BREATH` | NPC nefes saldırısı |
| `MEMORY` (obje tarafı) | hafıza item sorgusu |
| `DROPSOUND` / `EQUIPSOUND` / `PICKUPSOUND` / `DOOROPENSOUND` / `DOORCLOSESOUND` | item ses tablosu — tamamı yok |
| `RESDEF`, `RESDEF0`, `STRTOKEN`, `LISTCOL`, `STRFIRSTCAP`, `STRRANDRANGE` | script yardımcı fonksiyonları |
| `TAGAT`, `PROPSAT`, `PROPSCOUNT`, `CTAGCOUNT`, `DIALOGLIST` | koleksiyon indeksleme |
| `ISCONT`, `ISEVENT`, `ISTEVENT`, `ISDIALOGOPEN`, `ISNEARTYPETOP` | predicate ailesi |
| `OWNEDBY`, `TOPCONT`, `NODROP`, `NOTRADE`, `QUESTITEM` | item sahiplik/kısıt bayrakları |

`SYSCMD` ve `SYSSPAWN` (script'ten OS komutu çalıştırma) **bilinçli olarak
portlanmadı** — güvenlik kararı, eksik değil.

### 2.3 Ağ katmanı — 85 / 88

- **Gelen:** `NetworkManager` 65 paket sınıfı kaydediyor. Source-X `receive.h`
  108 sınıf tanımlıyor, ama bunların 14'ü custom-house design alt-komutları;
  SphereNet onları tek `EncodedCommand` + `0xD7` alt-dispatch'iyle karşılıyor.
  Etkin kapsam ~%85.
- **Giden:** 109 paket sınıfı (SX 124). Eksikler: `BondedStatus`,
  `ChangeCharacter`, `CharacterListUpdate`, `CloseContainer`, `CloseVendor`,
  `GameTime`, `GlobalChat`, `GumpChange`, `PropertyListVersionOld`,
  `QueryClient`, `SignGump`, `StatueAnimation`, `Telnet`, `TimeSyncResponse`,
  `ToggleHotbar`, `WarningMessage`, `WebPage`, `ZoneChange`.
- **Şifreleme tam:** Blowfish + Twofish + Huffman + no-crypt algılama, gerçek
  socket olmadan `InjectReceived`/`ProcessInput` üzerinden deterministik
  loopback login testi (`0x80` → `0xA8` → relay `0x8C` → `0x91` → `0xB9`/`0xA9`
  → `0x5D` → `0x1B`).
- Client-çağı kapıları (`ClientEra=Sphere56x` varsayılan, `0xDF` buff ve AOS
  tooltip sadece destekleyen client'ta) test kilitli.

### 2.4 Housing / multi — 62 / 72 (en büyük yapısal boşluk)

Source-X `CItemMulti.cpp` (3.942) + `CItemMultiCustom.cpp` (2.073) = 6.015 satır.
SphereNet Housing = 1.971 satır.

**Var olan:** yerleştirme + hesap limitleri, sahip/co-owner/friend/ban listeleri,
lockdown/secure sayaçları (`GetMaxLockdowns` birebir), decay aşamaları,
`HOUSE.n` script API'si, custom housing editörü (`0xD7`/`0xD8` design stream,
revision'lı commit, `DESIGN_n` tag kalıcılığı, `WalkCheck.ResolveCustomDesign`
üzerinden sanal yürüme geometrisi), ship redeed crate.

**Yok:** `ADDCOMPONENT`/`DELCOMPONENT`, `ADDADDON`/`DELADDON`/`ADDONS`,
`ADDVENDOR`/`DELVENDOR`, `ADDKEY`/`REMOVEKEYS`, `GENERATEBASECOMPONENTS`,
`MOVINGCRATE`/`MOVEALLTOCRATE`/`MOVELOCKSTOCRATE`, `GET*POS` indeksleme ailesi
(`GETCOMPPOS`, `GETFRIENDPOS`, `GETSECUREDCONTAINERS`, `GETLOCKEDITEMPOS`, …),
`SECURED`, `REMOVEALLCOMPS`.

Yani **ev çalışır, ev script'lenemez.** Bir shard'ın ev sistemini script'ten
yönetmesi gerekiyorsa bu kategori kırılma noktası.

### 2.5 `sphere.ini` — 58 / 85 (en zayıf ölçülen alan)

Source-X `CServerConfig::sm_szLoadKeys` 279 anahtar tanımlıyor; SphereNet 164'ünü
tanıyor. Eksiklerin ~35'i .NET yeniden yazımında **anlamsız** (`NTSERVICE`,
`MYSQLTICKS`, `NETWORKTHREADPRIORITY`, `USEASYNCNETWORK`, `USEEXTRABUFFER`,
`FORCEGARBAGECOLLECT`, `MAXSIZECLIENTIN/OUT`, `BUILDNUM`, `STRIPPATH`, …).
Onlar düşülünce oyun-anlamlı kapsam ~%67.

**Oyun davranışını doğrudan değiştiren, tanınmayan anahtarlar:**

`RUNNINGPENALTY`, `RUNNINGPENALTYOVERWEIGHT`, `STAMINALOSSATWEIGHT`,
`STAMINALOSSOVERWEIGHT`, `BACKPACKOVERLOAD`, `DRAGWEIGHTMAX`, `MOUNTHEIGHT`,
`MEDITATIONMOVEMENTABORT`, `MAGICUNLOCKDOOR`, `SPELLTIMEOUT`,
`NPCCANFIZZLEONHIT`, `NPCSHOVENPC`, `LOSTNPCTELEPORT`, `NPCTRAINPERCENT`,
`OVERSKILLMULTIPLY`, `SKILLPRACTICEMAX`, `HITSHUNGERLOSS`, `WOOLGROWTHTIME`,
`EXPERIENCESYSTEM`/`EXPERIENCEMODE`/`EXPERIENCEKOEFPVM`/`EXPERIENCEKOEFPVP`,
`LEVELSYSTEM`/`LEVELMODE`, `REVEALFLAGS`, `EMOTEFLAGS`, `STATSFLAGS`,
`AREAFLAGS`, `AUTOPRIVFLAGS`, `DISTANCEFORMULA`, `MAXHOUSESGUILD`,
`MAXSHIPSGUILD`, `AUTOHOUSEKEYS`/`AUTOSHIPKEYS`/`AUTONEWBIEKEYS`,
`VENDORMAXSELL`, `PAYFROMPACKONLY`, `TRADEWINDOWSNOOPING`,
`CANUNDRESSPETS`/`CANPETSDRINKPOTION`, `NORESROBE`, `NOWEATHER`,
`TELEPORTEFFECT*`/`TELEPORTSOUND*` (6 anahtar), `FLIPDROPPEDITEMS`,
`ITEMTIMERS`, `MAXPOLYSTATS`, `ZEROPOINT`, `DECIMALVARIABLES`,
`CHATSTATICCHANNELS`, `MEDIUMCANHEARGHOSTS`, `SUPPRESSCAPITALS`,
`SPEECHOTHER`, `WOP*` ailesi.

Bu, tek kategoride en yüksek getirili iş: mevcut bir shard'ın `sphere.ini`'si
sessizce yok sayılan satırlar içeriyor ve davranış farkı buradan doğuyor.

### 2.6 AOS bileşen-prop sistemi — 47 / 80

Source-X `CCProps*` tabloları 139 özellik. SphereNet 65'ini tanıyor — ve tanıdığı
65, **doğru 65:** `RES*`/`RES*MAX` (5 element + tavan), `DAM*` dağılımı,
`HIT*` on-hit efekt ailesi (14 adet), `HITAREA*`, `REGEN*`/`REGENVAL*`,
`BONUS*` stat/hits/mana/stam, `FASTERCASTING`/`FASTERCASTRECOVERY`,
`INCREASEDAM`/`INCREASEHITCHANCE`/`INCREASEDEFCHANCE`/`INCREASESWINGSPEED`,
`SLAYER_*`, `FACTION_*`, `LUCK`, `NIGHTSIGHT`, `REFLECTPHYSICALDAM`,
`WEIGHTREDUCTION`, `AMMO*` ailesi, `RANGE`/`RANGEH`/`RANGEL`.

Eksik 74'ün tamamı SE/ML/SA/TOL çağı: `ASSASSINHONED`, `BONEBREAKER`,
`SPLINTERING`, `SEARING`, `BATTLELUST`, `MYSTICWEAPON`, `MAGEWEAPON`,
`BALANCED`, `USEBESTWEAPONSKILL`, imbuing/reforging alanları vb.

Bu bir eksik değil, **hedef sürüm kararı**. Sphere 56x uyumluluğu hedefiyse
kategori fiilen tamamlanmıştır; tam Source-X paritesi hedefse %47'de.

### 2.7 Guild stone menüleri — 55 / 70

Guild'in kendisi çalışıyor (üyelik, ittifak, savaş, kanal konuşması `0xAE`
tip `0xD`/`0xE` ile). Eksik olan **stone gump menü ağacı**: `MASTERMENU`,
`VIEWROSTER`, `VIEWCANDIDATES`, `ACCEPTCANDIDATE`, `REFUSECANDIDATE`,
`RECRUIT`, `DISMISSMEMBER`, `DECLAREFEALTY`, `GRANTTITLE`, `SETCHARTER`,
`SETABBREVIATION`, `SETGMTITLE`, `SETNAME`, `VIEWENEMYS`, `VIEWTHREATS`,
`RETURNMAINMENU` — 30 stone fonksiyonundan 17'si yok.

### 2.8 Chat — 70 / 75

SphereNet 304 satır; Source-X `CChat` + `CChatChannel` + üye sınıfları ~1.300.
Conference chat (`0xB3` alt-komutlar, `0xB5` pencere açma, kanal listesi)
çalışıyor. `0xB2` legacy text-in kabul edilip yok sayılıyor, `0xF9` (KR varyantı)
portlanmadı — ikisi de bilinçli erteleme.

---

## 3. Sadakat tarafı — "düzgün port edilmiş mi?"

Kapsam sayılabilir; sadakat sayılamaz. Bu bölüm sadakat puanlarının dayanağı.

### 3.1 Lehte olan kanıtlar

**Sistematik "uyduruk değer" denetimi yapılmış ve büyük ölçüde kapatılmış.**
2026-07-14'te motorda Source-X atfı olmayan gömülü sabitler taranmış; 5 dalga
düzeltme uygulanmış. Kapatılanlardan bazıları:

| Uyduruk davranış | Düzeltme |
|---|---|
| DAM'sız silah hasarı = `BaseId/10` | Source-X `Weapon_GetAttack`: attackBase = 0 |
| Zehir tik tablosu 5/8/12/16/20 | OSI 3/3/6/6/8 (`CCharAct.cpp:4227`) |
| Tamir sentetik 50 dayanıklılık yazıyordu | `Use_Repair` birebir portu (SKILLMAKE, 1/6–1/3 fail tablosu) |
| Yer item decay 10 dk | 30 dk (`m_iDecay_Item`) |
| Container cap 500 (ve çelişik ikinci cap 125) | 255 (`MAX_ITEMS_CONT`) |
| Sentetik NPC loot (stat-tier → altın/reagent/gem) | kaldırıldı; script loot otorite |
| Summon statları düz sabit | chardef'ten okunuyor |
| Craft exceptional %20 dayanıklılık bonusu | `Skill_MakeItem` band tablosu |
| Spawner delay 60–300 sn | Source-X `rand 1–30 dk` (`CCSpawn.cpp:554`) |

Denetim ayrıca **yanlış alarmları da dokümante etmiş** (`web HP 60+rand250`,
`flee 20 adım`, `güreş baz hız 50`, `healing zorlukları`, `Focus /100 /200` —
hepsi Source-X'in kendisi, atıf yorumu eklenmiş). Bu, denetimin ciddi
yapıldığının işareti: "şüpheli"yi "hatalı" ile karıştırmamış.

**Kod, referansı satır düzeyinde alıntılıyor.** Örnek — `SkillEngine.cs`
skill kazanımı, `CChar::Skill_Experience` portu:

- safe area'da kazanım yok
- `difficulty × 10`, `[1, 1000]` clamp — **işaret korunuyor** (başarısız kullanım
  negatif gelir, 1'e clamp'lenir; `Abs()` alınmaz)
- `GAINRADIUS` sadece skill def'te tanımlıysa aktif — **uydurma varsayılan yok**
- `ADV_RATE` eğrisi yoksa **kazanım yok** (`CValueDefs.cpp:175` → 0), yerine
  uydurma eğri konmuyor
- `@SkillGain` kazanım zarından **önce** ateşleniyor, chance ve cap
  `ref` ile geri okunuyor
- decay zarı gain zarından önce ve toplam cap'ten bağımsız

**Regresyon zırhı gerçek.** 3064 test / 390 test dosyası. Bunların bir kısmı
"test" değil **guardrail**: `TriggerCoverageGuardrailTests` ateşlenmeyen trigger
kümesini her koşuda kaynaktan yeniden türetip dokümante backlog'a karşı
doğruluyor — yeni bir enum üyesi eklenip bağlanmazsa test kırılıyor.
`SourceXVerbInventoryGuardrailTests` Source-X'in kendi `.tbl` dosyalarını ve
`sm_szVerbKeys` tablosunu pinliyor; upstream yüzey büyürse CI görüyor.
Bu, paritenin **folklor** olmasını engelleyen mekanizma.

**Gerçek veriyle doğrulanmış.** Klasik 56T save yükleniyor (2.660 NPC +
53 spawner), harici script paketi çalışıyor, `docs/reviews/` altında 100 saha
inceleme dosyası var, 649 commit.

### 3.2 Aleyhte olan kanıtlar

- **NPC AI tik temposu bilinçli sapma.** Source-X modeli
  `(1+t)×100ms, t = max(0,(150-dex)/2) tenth, rand(t/2..t)`
  (`CCharNPCAct.cpp:2388`). SphereNet'inki saha ayarlı (hareket takılması
  denetiminden). Oyun-içi doğrulama olmadan geri portlanmamalı — ama parite
  açısından açık bir sapma.
- **Bilinçli sapma listesi var.** Örn. spawn'ın gem üstünde doğması
  (`MOREZ` sadece dolaşma tasması), Discordance 20 sn motor fallback'i.
  Bunlar dokümante ama parite denetiminde "hata" gibi görünüp geri
  "düzeltilme" riski taşıyor.
- **Kalan uyduruk-değer kuyruğu:** skillclass 225/7000 fallback,
  `ContainerMaxWeight` 400 (Source-X karşılığı doğrulanmadı),
  REGIONRESOURCE tanımsızken devreye giren fallback gathering ekonomisi.
- **Kapsam ölçümünün kendisi bir üst sınır.** "İsim çözülüyor" ile
  "Source-X ile aynı sonucu veriyor" arasındaki farkın tamamı ölçülmedi;
  ölçülen kısımda (skill gain, combat C1–C7, death/corpse, housing/movement,
  trigger arg/return) sadakat yüksek çıktı, ölçülmeyen uzun kuyruk açık.

---

## 4. Sonuç: emülatör mü, prototip mi?

**Emülatör. Prototip aşaması geride kaldı.**

Bir projeyi prototipten emülatöre taşıyan eşikler ve SphereNet'in durumu:

| Eşik | Durum |
|---|---|
| Gerçek client bağlanıp oynanabiliyor mu? | ✅ Login → char select → dünya → hareket → savaş → büyü → craft → save tam zinciri |
| **Kendi** verisi değil, **mevcut** shard verisi çalışıyor mu? | ✅ Klasik mortechUO/56T save (2.660 NPC, 53 spawner) + harici `.scp` paketi |
| Script motoru gerçek içerik koşuyor mu, demo mu? | ✅ 872 isimlik yüzeyin 676'sı, trigger zinciri arg/return semantiğiyle |
| Regresyon zırhı var mı? | ✅ 3064 test yeşil + kaynaktan türeyen guardrail'ler |
| Uzun süreli çalışma / operasyon | ✅ Canlı paket, RAM/GC, host konsol dayanıklılığı, runbook |
| Davranış farkları rastgele mi, dokümante mi? | ✅ Sapmaların çoğu adlandırılmış ve gerekçeli |

Prototipin tanımı "çalışıyor gibi görünen ama veri/ölçek/regresyon karşısında
dağılan" şeydir. Burada tersi var: **ölçek gerçek, veri gerçek, ve sapmalar
sayılmış.**

**Ama nitelemek gerek:** SphereNet bugün **Sphere 56x sınıfı bir emülatör**,
tam Source-X paritesinde bir emülatör değil. Fark tek bir yerde toplanıyor:
AOS/SE/ML/SA çağı uzun kuyruğu (item özellik sistemi, o çağın skill okulları,
bileşen prop'ları). Hedef Sphere 56x uyumluluğuysa proje **~90/100**; hedef
"Source-X'in yaptığı her şey"se **~82/100**.

---

## 5. Sonraki iş için getiri sıralaması

Etki ÷ maliyet oranına göre:

1. **`sphere.ini` anahtar boşluğu (58 → 85).** ~80 oyun-anlamlı anahtar
   sessizce yok sayılıyor. Çoğu tek bir okuma + tek bir kullanım noktası.
   Mevcut bir shard'ın ini'sini olduğu gibi çalıştırmanın önündeki tek engel.
2. **Item ses tablosu** (`DROPSOUND`/`EQUIPSOUND`/`PICKUPSOUND`/
   `DOOROPENSOUND`/`DOORCLOSESOUND`). Tamamı yok, hepsi ucuz, oyuncuya
   doğrudan hissedilir.
3. **Skill/craft sorgu ailesi** (`SKILLCHECK`, `SKILLTEST`, `SKILLUSEQUICK`,
   `SKILLBEST`, `CANMAKE`, `CANMAKESKILL`, `CANCAST`). Script yazarının
   sürekli kullandığı predicate'ler; motorda karşılıkları zaten var,
   sadece script yüzeyine bağlanmamış.
4. **`MODMAXHITS`/`MODMAXMANA`/`MODMAXSTAM`.** Sık kullanılan stat tavanı
   değiştiricileri; kalıcılık kuralına dikkat (türetilmişi değil base'i
   persist et).
5. **Housing script API'si** (component/addon/vendor/moving-crate/`GET*POS`).
   En büyük yapısal boşluk ama en pahalısı; ev sistemini script'ten yöneten
   bir pakete geçilecekse zorunlu.
6. **Guild stone menü ağacı** (17 fonksiyon). Klasik shard'larda görünür.
7. **Kalan 27 trigger.** Her biri küçük; `@PayGold`, `@SeeHidden`,
   `@RegionResource*` ve `@PetRelease` en çok script'lenenler.
8. **AOS bileşen prop'ları / SA-ML skill okulları.** Sadece hedef sürüm
   Sphere 56x'ten ileri taşınırsa.

---

### Ek — sayıların yeniden üretimi

```bash
# Source-X script yüzeyi (tablo dosyalarından)
grep -oE 'ADD\([^,]+,\s*"[^"]+"\)' oldSphere/Source-X-full/src/tables/*.tbl

# Source-X trigger listesi (248)
grep -oE 'ADD\([A-Za-z0-9_]+\)' oldSphere/Source-X-full/src/tables/triggers.tbl

# sphere.ini anahtar tablosu (279)
sed -n '759,1060p' oldSphere/Source-X-full/src/game/CServerConfig.cpp \
  | grep -oE '\{\s*"[A-Za-z0-9_]+"'

# SphereNet tarafı: test dışı kaynaktaki tüm string literal önekleri
find src -name '*.cs' -not -path '*Tests*' -not -path '*/obj/*' \
  | xargs cat | grep -oE '"[^"]*"'
```
