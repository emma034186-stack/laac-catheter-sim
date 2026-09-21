# LAAC 導管模擬：新專案重建、效能修復與 GitHub 建檔紀錄

## 0. 起因

延續昨天的血管顯示修復工作，今天一開始想繼續在舊專案（`D:\UnityProjects\import`）上調攝影機、UI、血管材質。過程中使用者要求先還原到 9/16 的舊備份（血管顯示修好之前的版本），之後再手動移植血管 GameObject 回去。移植完成、碰撞也一度確認正常，但接著發現「內視鏡攝影機看不到血管」，往下查碰撞問題時，發現舊專案的 SOFA↔Unity 對接圖（DAGNodeManager reconnect）已經處於不穩定狀態：手動移植的 GameObject 沒有問題，但碰撞偵測（`CollisionPipeline` 的 debug draw）完全沒有任何輸出——問題比預期更根本。

對照組測試：完全沒被今天任何操作碰過的原廠示範場景 `BeamDemo_02_KV1.unity`（同一個舊專案裡）碰撞正常。這排除了「原生 DLL / 外掛安裝」層級的問題，把範圍鎖定在 `LAAC_Catheter_Demo.unity` 這個場景檔本身——很可能是今天多次手動 YAML 移植、還原、再移植疊加下來，把 Unity 端的物件狀態搞得跟原生端對不上。

嘗試用外掛官方的「Load SOFA Scene (.scn) file」重建對接圖，結果把血管網格資料清空成 0 頂點（比之前更糟）。判斷這條路本身就不穩定，不能用來收尾。

**決定**：與其繼續在這個狀態不明的場景上除錯，另開一個全新的 Unity 專案，把已經解決過的基礎設施（DLL、路徑、`.scn` 碰撞參數調校、材質修正、`ThirdPersonCamera.cs` 修正）原封不動搬過去，但**血管、導管的 Unity 端物件改成讓外掛自己從 `.scn` 重新生成**，不再手動搬 YAML。

---

## 1. 新專案建置

- 用 `Unity.exe -createProject` 建立 `D:\UnityProjects\LAAC_Catheter_Sim`（同版本 2022.3.46f1）
- 只搬 `Assets/SofaUnity/` 整包（319MB，robocopy），刻意不搬：
  - `LightBuzz Hand Tracking`（300MB，跟 SOFA 無關，且會在 Console 洗一個不相干的 DirectML 警告）
  - `YughuesFreeNatureMaterials`（231MB，確認沒被 SofaUnity 任何東西引用）
  - 舊專案 `Assets/Editor/` 底下的除錯腳本（路徑寫死，且是當次除錯用的一次性工具）
- 修正 `sofa.ini` 的絕對路徑（`SHARE_DIR`/`EXAMPLES_DIR`/`LICENSE_DIR`/`PYTHON_DIR`）指向新專案路徑
- **踩雷**：新建的空白 Unity 專案預設沒有裝 `com.unity.ugui` 等套件，導致 SofaUnity 腳本（用到 `UnityEngine.UI`）出現 20 個 `CS0234` 編譯錯誤，整個外掛沒在跑。修法：把舊專案完整的 `Packages/manifest.json` 複製過去，取代新專案的預設清單。
- 健康檢查：打開完全沒動過的 `BeamDemo_02_KV1.unity`，Play 確認碰撞正常——證實 DLL/路徑/套件設定都是好的，問題確實侷限在場景檔本身。

## 2. 乾淨建立 LAAC_Catheter_Demo 場景

- 以 `BeamDemo_02_KV1.unity` 為起點另存新場景，用 BFS 腳本移除 KV1 專屬的 `Organs`（腎臟/肝臟等 6 個道具，共 14 個物件），驗證無懸空引用
- 把 `SofaContext` 的 Scene Filename 指向 `LAAC_Catheter.scn`
- 在 Inspector 點「Load SOFA Scene (.scn) file」——因為是全新場景、沒有殘留物件干擾，這次乾淨生成了 `SofaNode - LAAC_Vessel`、`SofaNode - NavigationSceneNode`（導管）兩個節點，Console 完全乾淨
- **結果**：Play 後確認**碰撞正常**（有碰撞感），證實問題根源確實是舊場景的手動 YAML 移植歷史，不是碰撞參數本身

## 3. 血管材質修復

`.scn` 裡 `<OglModel name="OglVessel" color="0.8 0.2 0.2 0.35"/>` 沒有指定材質，只給顏色，導致「Load SOFA Scene」自動生成時套用了某個不相關的預設材質（先後出現過內視鏡材質、甚至另一個示範案例「Urinary_system」的攝護腺材質——推測是 Unity 在某些重新整理/存檔時機對「找不到指定材質」的物件做隨機/預設 fallback）。

修法：直接把 `OglVessel` 的 `MeshRenderer.m_Materials` 改指向 `LAAC_Vessel_Tissue.mat`（guid `68373d8ec08a41a4a271f6d47d9ba30d`，昨天已經改成雙面不透明 Standard shader 的那份）。

**這個問題重複發生了兩次**：第一次修好後，使用者在同一個沒重新載入的 Editor session 裡繼續工作、存了好幾次檔，Unity 每次存檔都拿記憶體舊狀態覆蓋硬碟，外部修改被蓋掉。**教訓：任何時候我從外部改完場景檔，使用者必須先重新開場景（丟棄目前變更）才能繼續動作，否則後續存檔會把修改蓋回去。** 這個坑今天踩了不只一次。

## 4. 內視鏡攝影機追蹤（最長的一段除錯）

### 4.1 根本原因

導管的可視網格（`SofaBeamAdapterModel` 機制）是**直接改寫網格頂點座標**來呈現前進/彎曲的，不是移動 GameObject 的 Transform。而場景裡原本的 `EndoCamera` 是掛在同一個父節點下的**子物件**，跟著 Unity 標準 Transform 階層走——頂點被重畫不會觸發父子物件連動，所以攝影機永遠不會真的跟著導管尖端移動。

### 4.2 排查過程中發現的三個疊加問題

1. **監視器材質裡藏著自發光層**：`BeamCamera-Endoscope.mat` 同時設定了 `_EmissionMap`（來源貼圖檔案在專案裡其實已經遺失）跟很亮的 `_EmissionColor`（1.4，超過正常亮度）。這層自發光很可能從頭到尾都蓋掉了底下真正的攝影機畫面，導致「不管攝影機在哪、朝哪，監視器永遠是同一個藍白漸層」——這也解釋了為什麼今天所有嘗試（挪位置、轉角度）在視覺上都沒有任何變化。修法：清空貼圖、顏色歸零。

2. **舊 `EndoCamera` 跟新建的 `Camera_TEST` 同時輸出到同一張 RenderTexture**：兩顆攝影機搶著寫同一張材質貼圖，後渲染的蓋過先渲染的，導致新攝影機的畫面被壞掉的舊攝影機蓋掉。修法：偵測到新攝影機就順便停用舊 `EndoCamera` 的 Camera 元件。

3. **執行期動態讀取材質貼圖不穩定**：一開始想在程式裡「讀」監視器目前用的貼圖、再指定回去，這個間接讀取常常失敗（材質參考本身就不穩定，見上一節）。改成直接用 `AssetDatabase.LoadAssetAtPath` 照確切檔案路徑載入 `BeamEndoscopeTexture.renderTexture`，繞開所有「猜材質現在指到哪」的環節。

### 4.3 追蹤演算法

寫了 `EndoCameraTracker.cs`（執行期動態掛載，不寫入場景檔）：每隔一段時間讀一次導管網格的頂點資料，取「離局部原點最遠」那一圈頂點的中心當作尖端位置，跟稍微靠後一點的一圈頂點中心算出前進方向（切線），把攝影機移動、旋轉過去（有指數平滑，減少每幀跳動）。

**踩過的方向錯誤**：一開始把「往後退一點避免貼牆閃爍」寫成往導管**身體內部**退（符號寫反了），導致鏡頭卡進導管自己裡面。修正成往前推、超出尖端範圍。

**最終做法調整**：多輪嘗試後，在 Editor 手動建立一顆全新、確認能正常渲染的攝影機（`Camera_TEST`），`EndoCameraTracker.cs` 改成只負責讓**這顆**攝影機的 Transform 跟著導管尖端走，不再碰其他任何設定（渲染設定、材質、Target Texture 除了指定一次以外都不動）。這樣把「攝影機本身能不能正常渲染」跟「攝影機位置追蹤」兩個問題解耦，最終成功。

**收尾**：把 `Camera_TEST` 的 Target Texture 直接存進場景檔（不只是執行期動態設定），讓非 Play 模式下主畫面/監視器畫面也正確，不用等按 Play 才生效。

## 5. 效能／物理同步 bug（影響碰撞手感的隱藏因素）

使用者反映新專案的碰撞彈性感覺沒有昨天研究紀錄裡那麼好，比較容易穿透。核對過 `.scn` 裡所有碰撞/彈性參數，跟研究紀錄定案的值完全一致，一個字元都沒差——但原生 SOFA 是直接讀 `.scn`、理論上物理行為該一模一樣。

追進 `SofaContext.cs` 的 `UpdateImplSync()` 才找到真正原因：

```csharp
// 原本：每個 Unity 畫面最多只 step 一次
if (Time.time >= nextUpdate) { nextUpdate += m_timeStep; m_impl.step(); ... }
```

物理模擬每秒實際推進的次數被綁在渲染 FPS 上限之下（`if` 不是 `while`）。如果渲染 FPS 比較低（新專案快取還沒熱起來、複雜度不同都可能導致），物理步進速率就會變慢——但按鍵推進導管是照「真實時間」算的（`SofaKeyEvent` 節流間隔），跟物理步進速率脫鉤。FPS 越低，每一次物理步進要「消化」的推進量就越大，碰撞偵測自然更容易漏檢/穿透。

**修法**：改成 `while` 迴圈，讓物理步進補齊到跟上真實時間，加了「每畫面最多補 10 步」的安全上限避免當機當底時越補越卡：

```csharp
int stepsThisFrame = 0;
const int maxStepsPerFrame = 10;
while (Time.time >= nextUpdate && stepsThisFrame < maxStepsPerFrame)
{
    nextUpdate += m_timeStep;
    m_impl.step();
    if (m_nodeGraphMgr != null) m_nodeGraphMgr.PropagateSetDirty(true);
    stepsThisFrame++;
}
```

確認修完後碰撞感有改善（但沒有完全回到最佳狀態）。

### 5.1 次要因素：診斷腳本本身的效能負擔

修完 FPS 同步問題後碰撞感仍有些微落差，懷疑是自己加的除錯工具（`EndoCameraTracker`、`CatheterWallDistanceHUD`）造成的：兩者都頻繁呼叫 `mesh.vertices`，這個 Unity API **每次呼叫都會重新配置一個新陣列**（血管網格有 2560+ 頂點），頻繁呼叫會製造 GC 負擔、間接拖慢畫面更新。實驗：暫時停用這兩支腳本後，碰撞感確實更接近研究紀錄裡的水準（除了轉彎處那個已知的結構性限制之外）。

**修法**：兩支腳本都改成用 `Mesh.GetVertices(List<Vector3>)` 重複使用同一份記憶體，取代會配置新陣列的 `.vertices` 屬性，更新頻率也放寬（0.02s→0.08s、0.15s→0.3s）。

## 6. 參數疊代（速度 vs 抗穿透的拉扯）

使用者這次的方向是「操作快一點，同時血管不要那麼容易穿」——這兩個目標本質上互相拉扯（速度越快，物理系統越難跟上）。疊代過程：

| 項目 | 起始值 | 最終值 | 備註 |
|---|---|---|---|
| `SofaKeyEvent.m_keyRepeatInterval`（前進/後退節流） | 0.04 | 0.025 | 中間試過 0.35/0.25/0.15/0.1/0.07/0.05/0.045，最後定案 0.025 |
| `SofaKeyEvent.m_rotationRepeatInterval`（旋轉節流） | 0.12 | 0.5 | 往變慢的方向調（安全方向），中間試過 0.2/0.35/0.7 |
| `InterventionalRadiologyController.step`（單次前進距離） | 0.08 | 0.4 | 中途試過 0.15（研究紀錄裡本來就試過的值）跟 1.5（ 10 倍速，明確伴隨嚴重卡頓/穿透副作用，隨後退到 0.4 這個折衷值） |
| `LocalMinDistance.alarmDistance` | 5 | 12 | |
| `LocalMinDistance.contactDistance` | 0.5 | 1.4 | |
| 導管 `LineCollisionModel`/`PointCollisionModel.proximity` | 1.8 | 3.0 | |
| 導管碰撞模型 `contactStiffness`（原本沒設，用預設） | — | 3000 | 血管、導管碰撞模型都加上 |

**注意**：`step="1.5"` 那次曾造成明顯卡頓——因為單步推進量暴增，LCP 求解器每步要處理的接觸複雜度跟著暴增，又剛好碰上前面加的「每畫面最多補 10 步」catch-up 機制，計算變慢時會在同一畫面硬做 10 次昂貴求解，直接卡頓。**這三個症狀（速度快、卡頓、穿透）在那個當下是同一個因造成的**，无法只改善卡頓又保留那個速度。目前 `step=0.4` 是最後接受的折衷點，還沒有做最終驗收確認。

## 7. 備份

**專案**（`D:\UnityProjects\LAAC_Catheter_Sim_backups\`）：

5. `05_before_github_push_20260918_172230.bak`

---

## 8. 目前狀態與後續建議

**已完成**：
- 新專案碰撞、血管顯示、材質、攝影機、內視鏡追蹤皆已確認正常運作
- 物理步進不再受畫面 FPS 拖累（`while` catch-up 修正）
- 診斷工具效能優化，不再明顯影響碰撞手感
- 專案已上傳 GitHub，README 補齊還原說明

**待確認**：
- `step=0.4`（連同目前的碰撞餘裕參數組合）是這次調參的折衷點，尚未做最終驗收，之後可能還會微調
- 轉彎處（穿房間隔點附近）穿透仍是已知結構性限制，跟昨天研究紀錄的結論一致，非本次工作範圍
- 建議之後如果要繼續調參，先確認 Unity Editor 焦點/重新載入場景
