# Pilot Testnet Runbook

## Secilen plane

Bu pilot paketi **FUTURES** plane icin tanimlidir.

Neden:
- worker pilot submit path'i ExchangeDataPlane.Futures ile dispatch eder
- gate/pilot override beklentisi DevelopmentFuturesTestnetPilot=True context'i ustunden calisir
- credential readiness testnet futures yetkisi (SupportsFutures=true) ile kapanir
- live executor secimi bu path'te BinanceExecutor olur
- BinanceSpotExecutor repo icinde ayri spot execution flow'u icin vardir; pilot smoke/acceptance zincirinin parcasi degildir

SupportsSpot bilgisi gorulebilir kalir ama plane secmez. Pilot kapanisinda zorunlu capability SupportsFutures=true olmaya devam eder.

## Scope

Bu runbook yalnız dar pilot scope icindir:
- 1 aktif Binance testnet credential
- 1 enabled bot
- 1 sembol
- 1 acik pozisyon limiti
- kucuk notional
- fail-closed guard zinciri

## Enable oncesi checklist

- `git status --short` yalniz beklenen kaynak/doc degisikliklerini gostermeli.
- `TradeMaster` state `Armed` olmali.
- global mode `Demo` kalmali; pilot path explicit activation env override ile acilir.
- `BotExecutionPilot:Enabled=true` olmali.
- `BotExecutionPilot:PilotActivationEnabled=false` default kalmali.
- `BotExecutionPilot:AllowedUserIds`, `AllowedBotIds`, `AllowedSymbols` tek kayda daraltilmali.
- `BotExecutionPilot:MaxPilotOrderNotional` tanimli ve pozitif olmali.
- hedef DB'de `Active` durumlu tek Binance account bulunmali.
- hedef DB'de enabled bot sayisi `1` olmali.
- acik execution order sayisi `0` olmali.
- acik position sayisi `0` olmali.
- ApiCredentialValidation sonucu Valid, EnvironmentScope=Demo, SupportsFutures=true, CanTrade=true olmali.
- runtime smoke script'i web/worker ortaminda scanner/handoff/history-filler gurultusunu kapatir, futures seed symbol setini smoke sembolune daraltir ve izole smoke DB'de global policy autonomy mode'unu RecommendOnly advisory durumda baslatir.
- `SupportsSpot` true veya false olabilir; pilot plane secimini etkilemez.
- testnet endpointleri resolve edilmeli:
  - `https://testnet.binancefuture.com`
  - `wss://fstream.binancefuture.com`

## EK-PILOT-0.1 hard-cap dogrulamasi

Pilot notional hard-cap submit oncesi fail-closed enforce edilir.
Asagidaki reason kodlari stabil kabul edilir:
- `UserExecutionPilotNotionalHardCapExceeded`
- `UserExecutionPilotNotionalConfigurationMissing`
- `UserExecutionPilotNotionalConfigurationInvalid`
- `UserExecutionPilotNotionalDataUnavailable`

Admin ayarlar ekraninda `Pilot hard cap` ozeti gorunmelidir.
Cap ustu request broker submit path'ine dusmemelidir.

## Runtime smoke

Manual smoke script:

```powershell
pwsh -File tests\SmokeTests\PilotLifecycleRuntimeSmoke.ps1
```

Script akisi:
1. mevcut dev DB'den aktif testnet credential ve validation metadata'sini okur
2. izole LocalDB smoke veritabani olusturur
3. deterministic pilot strategy + single bot graph seed eder
4. izole smoke DB icin GlobalRiskPolicy autonomy mode'unu RecommendOnly advisory seviyesine sabitler; market-anomaly worker'in smoke lifecycle kanitini reduce-only restriction'a cevirmesine izin vermez
5. `PilotActivationEnabled=false` ile warm-up kosar
6. market-data ve private-plane readiness olusmadan submit etmez
7. `PilotActivationEnabled=true` ile ayni bot icin gercek testnet submit dener
8. order / transition / trace / position / pnl / sync state ozetini `.diag\pilot-lifecycle-runtime-smoke\...` altina yazar
9. summary json ve stdout/stderr loglari yalniz lokal operasyon kaniti icindir; `.diag/` git-ignored kalir ve commit edilmez

Fail-closed davranis:
- aktif account/bot sayisi dar scope disindaysa script durur
- acik order veya acik position varsa script durur
- validation veya endpoint testnet degilse script durur
- market-data/private-plane readiness olusmazsa script durur
- broker submit'e ulasilamazsa exact blocker ile durur

## Kill switch testi

Pilot enable oncesi ve sonrasi en az bir kez yap:
1. `TradeMaster` `Disarmed` durumuna alin.
2. worker cycle tetiklenince yeni execution order'in `Rejected`/`TradeMasterDisarmed` oldugunu dogrula.
3. `LatestReject` ve decision/read-model yuzeyinde reason'in okunabildigini dogrula.
4. tekrar `Armed` durumuna al ve normal readiness'i bekle.

## Panic stop / kriz akisi

Operasyon seviyeleri:
- `SoftHalt`: yeni entry akisini durdurur, mevcut pozisyonu zorla kapatmaz.
- `OrderPurge`: bekleyen/pending emirleri iptal eder.
- `EmergencyFlatten`: approval akisi ile acik pozisyonlari kriz exit order'lariyla kapatir.

Kural:
- incident durumunda once `TradeMaster=Disarmed`
- sonra kapsam uygunsa `SoftHalt`
- bekleyen emir varsa `OrderPurge`
- pozisyon acik kaldiysa `EmergencyFlatten`

## Rollback / kapatma

Pilot kapatma sirasi:
1. `TradeMaster=Disarmed`
2. `BotExecutionPilot:PilotActivationEnabled=false`
3. enabled bot sayisini tekrar gozden gecir
4. bekleyen emir varsa `OrderPurge`
5. acik pozisyon varsa `EmergencyFlatten`
6. `ExecutionOrders`, `ExecutionOrderTransitions`, `ExchangePositions`, `ExchangeBalances`, `ExchangeAccountSyncStates` son durumunu kaydet
7. incident notuna blocker/failure code ve summary'leri ekle

## Incident halinde toplanacak minimum veri

- `.diag\pilot-lifecycle-runtime-smoke\...` altindaki smoke summary json ve ilgili stdout/stderr loglari
- worker stdout/stderr loglari
- web stdout/stderr loglari
- latest `ExecutionOrders` satiri
- ilgili `ExecutionOrderTransitions`
- varsa `ExecutionTraces`
- latest `ExchangeAccountSyncStates`
- latest `DegradedModeStates`
- latest reject/no-submit summary
- order state, failure code, exchange order id, reconciliation status, fill quantity, average fill price
- position quantity, entry price, unrealized pnl

## Duplicate / ghost order kontrolu

Her smoke sonrasi kontrol et:
- ayni `StrategySignalId` icin birden fazla aktif order olmamali
- tekrar worker cycle'i ikinci broker submit olusturmamali
- duplicate suppression outcome'u `SuppressedDuplicate` veya mevcut execution/order guard zinciriyle gorulebilmeli

## Beklenen sonuc siniflari

Kapanis icin kabul edilen sonuc:
- broker submit path'e ulasildi
- gercek exchange response alindi
- order lifecycle DB'de okunabiliyor
- reject/fill/position/pnl/sync telemetry okunabiliyor

Blocker durumunda mutlaka kaydet:
- exact blocker code
- blocker stage
- ilgili log satiri
- smoke summary path

## Reconciliation beklentisi

Runtime smoke kapanisinda `ReconciliationStatus=Unknown` gorulebilir ve bu **beklenen ara durum** kabul edilir. Bu tek basina smoke hatasi degildir.

Neden kabul edilir:
- smoke kapanisi anlik futures private-stream/order/position/balance telemetry uzerinden verilir
- submit/fill/position/balance zinciri goruldugunde broker path kaniti tamamlanmis sayilir
- async reconciliation worker'i ayni anda degil, sonraki cycle'da `LastReconciledAtUtc` ve reconciliation durumunu gunceller

Beklenen sonraki durum:
- async reconciliation calistiginda `LastReconciledAtUtc` dolar
- reconciliation durumu `Unknown` disindaki gozlenen exchange drift durumuna ilerler
- bu ilerleme olmuyorsa artik smoke-anlik ara durum degil, ayrica incelenecek reconciliation sorunudur

Kapanis icin minimum kanit:
- SubmittedToBroker=true
- ExternalOrderId dolu
- futures transition zinciri (GatePassed, Dispatching, Submitted ve terminal durum) yazilmis
- execution trace veya order query telemetry alinmis
- futures position/balance read-model zinciri akmis





