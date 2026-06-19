# 0068 — Hakoniwa 斜め俯瞰ジオラマ視点（perspective stage）grill 決定記録

**日付**: 2026-06-20 / **ブランチ**: 実装用 feature ブランチを `main` から分岐予定（spike 着手時に作成）
**関連**: [issue #93](https://github.com/botterYosuke/backcast/issues/93)（hakoniwa を正対(flat)から TFWR 風の斜め俯瞰ジオラマ視点へ） /
[findings 0007](0007-hakoniwa-split-grid.md)（slot=正本・grid 派生 rect の不変条件） /
[findings 0006](0006-infinite-canvas.md)（InfiniteCanvas pan/zoom・`_floatingLayer` parallax） /
[ADR-0009](../adr/0009-scene-authored-workspace-composition-root.md) / [ADR-0010](../adr/0010-workspace-engine-host-unification.md)

## 文脈

issue #93: 現状の hakoniwa は `HakoniwaController` が `RectTransform` の `anchorMin/Max` でセル配置する **2D UI（画面に正対＝flat / front-parallel）**。これを "The Farmer Was Replaced" 風の **斜め上から覗き込む floating ジオラマ**（perspective 投影・俯角 pitch≈35-45°・軽いヨー・厚みのある土台）に寄せる。

本ドキュメントは **実装着手前の grill 決定の正本**。context 喪失による再導出/再ブレを防ぐため、確定済みの設計判断を永続化する。spike 実装はこの決定を前提に進め、empirical 実証で覆る点があれば本ドキュメントに追記する。

## 確定済み決定（grill 正本）

1. **Q1 投影方式 → (あ) 本物の perspective が必須**（owner 確定）。issue goals #1 のとおり、奥が手前より収束する遠近。正投影（skew/平行）では不可。

2. **zoom 意味論 → (A) 写真ズーム**（owner 確定）。既存 pan/zoom コード（`InfiniteCanvasController.ApplyView`）は**完全無改変**。「傾いた板を撮った 1 枚の写真」を拡大縮小するイメージで、zoom 中も遠近の傾き量（収束の度合い）は固定。

3. **盤面側を傾ける**（カメラ/floating は傾けない）— Navigator 自己確定。floating window を平行四辺形化させないため。owner も (あ)+(A) でこの前提に整合。

4. **採用アーキ = 方式②**: hakoniwa タイル群を専用 World-Space Canvas（**Hakoniwa Stage**）＋専用 layer に載せ、3D で傾ける（pitch≈40° / yaw≈15° / FOV≈35°）。専用 perspective カメラが **Hakoniwa layer だけ**を透過 RenderTexture(RT) に撮影し、RT を `RawImage`（`_content` の子・旧 HakoniwaRoot 位置）で表示する。pan/zoom は `ApplyView` が `_content` を書くだけ＝photo-zoom が無改変で成立。

5. **入力再ディスパッチ = math-pick（preflight 後に確定 → 本ドキュメント末尾「(a)(b) 入力ルーティング」節）**: `RawImage` への screen-press → RT ピクセル座標へ変換 → `HakoniwaStageMath.UnprojectToSlot` で傾いた盤面 plane に逆投影し board-normalized (sx,sy) を得る → `HakoniwaController.SlotAtNormalized` で slot を引く → cell 上端の header band 判定 → header band なら当該 slot の tile の swap-drag を開始、body/隙間/盤外なら pan へ fall-through。`GraphicRaycaster`/`ExecuteEvents` は使わない（preflight RAYCAST-DEAD で headless 不可と実証 §11）。`HakoniwaTileHeaderInput` の swap ロジック（SlotOf/SlotAtNormalized/Swap）は無改変で再利用する。

6. **layer 分離**: tilt は Hakoniwa のみ。`_floatingLayer`（parallax 1.2）は Overlay 平面のまま＝可読性確保＋parallax 回帰なし。

7. **probe C（構図検証 AFK gate）**: 不変条件/レンジでアサート（絶対ピクセル値は不可）。
   - ① 奥収束: 奥辺の射影幅 < 手前辺の射影幅。
   - ② 厚み可視: 正面壁（土台側面）の射影高さ > 0。
   - ③ 再投影ラウンドトリップ: tile 中心を順投影 → 逆変換で同一 slot に戻る。
   - yaw 非対称（3/4 ビューらしさ）は HITL 目視で確認。

8. **spike が empirical 実証必須**（コード読解の「動きそう」は不可）:
   - (a) `RawImage` → RT pixel → `HakoniwaStageMath.UnprojectToSlot` → `SlotAtNormalized` ＋ header band 判定（math-pick, §12-14）で実際に tile swap が起きる（旧 RTカメラ raycast→ExecuteEvents 経路は preflight RAYCAST-DEAD で破棄、§11）。
   - (b) body-drag → pan に fall-through する。
   - (c) 透過 RT 合成（clear alpha=0 / straight-alpha / 落ち影の抜け）が破綻しない。
   - (d) RT カメラ毎フレーム再撮影の perf（live ChartView / DepthLadder を載せた状態）。
   - RT / 専用カメラ / 専用 layer は repo 初導入のグリーンフィールド配管。

9. **退避判断は spike 読み合わせ時に owner へ**（今ブロック不要）: 入力再ディスパッチが faithful に作れなかった場合のみ、skew 退避（AC#2 perspective を放棄）/ 静止ジオラマ化 / hold のいずれかを owner 判断。（preflight 後 update §11-14: math-pick 経路は §10③ 再投影ラウンドトリップが GREEN で faithfulness 実証済みのため、この退避トリガーはほぼ解消。残る owner 判断は (c) 透過合成 / (d) perf / yaw 非対称の HITL sign-off のみ。）

## spike 実証ログ（probe C 構図ゲート RED→GREEN）

10. **probe C 構図ゲートの RED→GREEN を実走で実証**（2026-06-20、Unity 6000.4.11f1 / `<Unity.app>/Contents/MacOS/Unity` を `-batchmode -nographics -quit -executeMethod HakoniwaPerspectiveStageProbe.Run` で実走）。`HakoniwaPerspectiveStageProbe.cs` 冒頭コメントは本項を `§10` として参照する。

    **RED**（front-parallel STUB `HakoniwaStageMath`）:
    ```
    STEP1 compile-only: compile exit=0  (CS error 0 件)
    STEP2 probe executeMethod: probe exit=1
    [HAKONIWA PERSPECTIVE STAGE FAIL] ①奥収束: far-edge width 881.00 is not < 95% of near-edge width 881.00 (board is front-parallel / not converging — perspective tilt absent)
    ```
    flat board は奥収束も厚みも持たない＝①② が FAIL し、gate が flat と perspective を弁別することを証明。

    **GREEN**（tilt+thickness 実装差し替え後・同コマンド）:
    ```
    compile exit=0 / probe exit=0
    [HAKONIWA PERSPECTIVE STAGE PASS] 奥収束 + 厚み可視 + 再投影ラウンドトリップ verified (headless composition invariants, findings 0068 §7).
    ```
    ①②③（奥収束 / 厚み可視 / 再投影ラウンドトリップ）が全 PASS。

    この RED→GREEN は probe C（§7）の構図不変条件に対する behavior-to-e2e 回帰ガードの正本。後続の実配管（World-Space Stage + perspective カメラ + 透過 RT + RawImage 差し込み + 入力再ディスパッチ）の (a)(b) event routing は別 gate（AFK / playmode / HITL のいずれか）を preflight で確定する。(c) 透過 RT 合成・(d) perf は HITL 目視 sign-off に切り出し（owner 承認 2026-06-20）、本 findings に HITL チェックリストを後続で追記する。

## (a)(b) 入力ルーティング — 設計フォーク確定（math-pick / production==gate）

11. **preflight 実証（2026-06-20、`HakoniwaInputPreflightProbe` を `-batchmode -nographics -executeMethod ...Run` で実走）**: World-Space uGUI Graphic を perspective RT eventCamera 越しに `GraphicRaycaster.Raycast` で hit-test できるかを実測 → `[HAKONIWA INPUT PREFLIGHT] RAYCAST-DEAD hits=0 target=MISS ... rtCreated=True`（exit=3）。RT は生成できる（rtCreated=True）が raycast は 0 hit。→ **Option A（ネイティブ GraphicRaycaster ルート）は headless AFK 不可と確定**。§5 が当初前提とした GraphicRaycaster 経路は破棄。

12. **フォーク確定 → Option B（math-pick）を (a)(b) 入力ルーティングの正本に採用**（Navigator 設計判断、owner product 意図ではないため owner 判断不要 / lead 委任）。論拠:
    - **occlusion 不在**: 盤面は単層 disjoint ceil(√n) グリッド（`HakoniwaGridMath.CellRects` は重なり無し / `SlotAt` first-hit）を **1 枚の傾いた plane** に載せたもの。タイルは coplanar で相互 occlusion が無く、`UnprojectToSlot` は単一 plane 交点なので pick は一意。Unity の GraphicRaycaster occlusion 意味論（z-sort / 重なり Graphic / mask）を再実装する必要が **ゼロ**。
    - **既存 durable 資産の再利用**: `UnprojectToSlot`（逆投影）は §10③ 再投影ラウンドトリップで既に GREEN、`SlotAt`/`SlotAtNormalized` は #14 で AFK gate 済み。新規コードは「screen-press → RT pixel → (slot, header/body) 判定」の薄い入力サーフェスのみ。
    - **production==gate**: 本番入力経路も math-pick にするため、AFK gate が検証する経路と本番経路が同一（GraphicRaycaster と math-pick の fidelity gap が生じない）。
    - **AFK 被覆は維持（HITL 降格不要）**: (a)(b) ルーティングは純幾何なので AFK probe で弁別可能。owner が条件付き承認した「(a)(b) の HITL 降格」は **不要**（AFK 被覆を温存できる＝より良い）。HITL は §HITL チェックリストの (c) 透過合成 / (d) perf / yaw 非対称のみに限定（MEMORY「HITL surfaces bugs AFK gates miss」を complement として尊重）。

13. **header band 意味論 = board-normalized fraction**（Navigator 確定）: header strip は cell の board-normalized 上端の固定 fraction（cell 高さに対する割合）。`UnprojectToSlot` で先に board 空間へ戻すため、画面上の foreshortening は自動で正しく反映される（奥のタイルの header は画面上で薄く写る）。flat HITL harness の絶対 px（HEADER_H=26）はそのまま使わず、px→fraction 変換は Unity 境界で行い、pure-core（`HakoniwaGridMath`）は fraction で受ける（#14 の resolution-independent 規律を踏襲）。

14. **(a)(b) ルーティング AFK gate（behavior-to-e2e 確定・RED→GREEN で著す）**:
    - 新規 pure helper `HakoniwaGridMath.RouteBoardPoint(cells, pointNormalized, headerFrac) -> (slot, inHeader)`（または `IsInHeaderBand`）を pure-core に追加。
    - probe section（`HakoniwaPerspectiveStageProbe` に section ④ 追加、または新 `HakoniwaInputRoutingProbe` を立て E2ERunner へ昇格）で不変条件を assert:
      - (a) slot S の header band ピクセルを順投影 → `UnprojectToSlot` → route が `(slot=S, inHeader=true)`＝swap-drag 開始。
      - (b) slot S の body ピクセル → `(slot=S, inHeader=false)`＝pan fall-through。
      - (b) 盤外/隙間ピクセル → `(slot=-1)`＝pan fall-through。
    - **RED**: header band helper が未実装（または常に header / 常に body を返す stub）だと (a)/(b) 弁別が落ちる。**GREEN**: helper 実装後に全 section PASS。

    **RED→GREEN 実走**（2026-06-20、§10 と同コマンド `<Unity.app>/Contents/MacOS/Unity -batchmode -nographics -quit -projectPath /Users/sasac/backcast -executeMethod HakoniwaPerspectiveStageProbe.Run -logFile <log>`。Section④ `Section4_BoardPointRouting` が pure helper `HakoniwaGridMath.RouteBoardPoint(cells, pointNormalized, headerFrac, out inHeader)` の (a)(b)(c) を AFK gate 化）:

    **RED**（`RouteBoardPoint` の header band 判定を always-body stub `inHeader = false` に差し替え＝header band 未検出）:
    ```
    compile exit=0 / probe exit=1
    [HAKONIWA PERSPECTIVE STAGE FAIL] ④(a)header: slot 0 header point (0.250,0.950) routed to (slot=0, inHeader=False); expected (slot=0, inHeader=true) — header band → swap broken
    ```
    header band を検出しない stub では (a) header→swap が落ち、gate が header（swap）と body（pan）を弁別することを証明。

    **GREEN**（canonical `RouteBoardPoint`＝cell 上端 `headerFrac` band 判定に復帰・同コマンド）:
    ```
    compile exit=0 / probe exit=0
    [HAKONIWA PERSPECTIVE STAGE PASS] 奥収束 + 厚み可視 + 再投影ラウンドトリップ + 入力ルーティング verified (headless composition invariants, findings 0068 §7/§14).
    ```
    ①②③④（奥収束 / 厚み可視 / 再投影ラウンドトリップ / 入力ルーティング）が全 PASS。この RED→GREEN が §14 (a)(b)(c) routing 不変条件の behavior-to-e2e 回帰ガードの正本。math-pick が production==gate（§12）の入力決定経路を AFK で固定する（実 screen-press → RT pixel → `UnprojectToSlot` → `RouteBoardPoint` の実配管 binding と視覚は HITL = 台本 HAKONIWA-11b）。

## scene 配線（World-Space Stage 実配管）フォーク確定

15. **scene 配線スライス**（§4 が描いた runtime 配管＝§10-14 が「別 gate」に切り出した実配線）の設計フォークを確定する。挙動: hakoniwa が `_content` 直下の flat 2D UI から、専用 World-Space Stage canvas に載った斜め俯瞰ジオラマ（perspective camera が透過 RT へ撮影 → `_content` の `RawImage` で合成）へ変わる。AFK gate は `BackcastWorkspaceProbe`（後述 Section16 + Section2 反転 + Section1 RawImage ref）。

    **4 フォーク確定**:
    - **F1 RawImage 配置 / HakoniwaRoot reparent**: RT 表示 `RawImage` は `_content` の子・**旧 HakoniwaRoot 位置**（§4）。HakoniwaRoot は `_content` から **Stage canvas へ転出**（盤面は固定ジオラマ、写真＝RawImage が Content 側で pan/zoom する）。新 serialized ref `_hakoniwaRawImage` を root に追加。
    - **F2 専用 layer + Main camera 除外**: hakoniwa タイル群は専用 layer（"Hakoniwa"）。perspective camera の `cullingMask` は当該 layer のみ、Main camera の `cullingMask` は当該 layer を**除外**（二重描画防止・RT 専有）。
    - **F3 board tilt（§3 再掲・rotation の所在）**: 傾きは**盤面（Stage canvas / HakoniwaRoot）側**が `Euler(pitch,yaw,0)` を担い、camera は軸並行 `(0,0,-camDistance)` looking +Z（floating window を平行四辺形化させない §3）。
    - **F4 scene 配線 SoT = `HakoniwaStageMath.StageParams.Default`**: camera（fov/距離）と board（pitch/yaw/board 寸法）の scene 値は math-core の `StageParams.Default` から**導出**（scene magic number 禁止・math と probe と production が同一 SoT＝§7 不変条件規律）。

    **consumer sweep（HakoniwaRoot 転出の blast radius）**: `_hakoniwaRoot` を参照する全 consumer を列挙 → 唯一壊れるのは `BackcastWorkspaceProbe.Section2`（`if (hako.parent != content)` L150）のみ。他は serialized ref（`BackcastWorkspaceRoot` の `_hakoniwaRoot` 直接参照・`HakoniwaController` への直接注入）と `.sizeDelta`（box-grow）で Content 階層を辿らないため **survive**。pan/zoom（`ApplyView` が `_content` を書く）も Content 側の RawImage を動かすので photo-zoom が無改変で成立（§4）。

    **Section2 = tdd RED→GREEN inversion**: L150 の `if (hako.parent != content) return "HakoniwaRoot is not a child of Content"` を `hako.parent == content` を弾く向きへ**反転**し、`hako.parent` が WorldSpace Stage canvas であることを assert する。既存テストが旧挙動を正として assert している → RED→GREEN で反転するパターン（tdd skill）。

    **Section16 wiring assert（placement = (A) `BackcastWorkspaceProbe` に集約）**: scene 構造の正本を 1 本に集約し重複 assert を回避するため、新 `Section16_HakoniwaStageWiring()` を `BackcastWorkspaceProbe` に追加（#93 隔離より正本集約を優先 — Section2 反転で同ファイルを touch する以上 2 probe 分散はコストのみ）。assert 内容:
    - Stage canvas = `RenderMode.WorldSpace` ＋ 専用 layer（`_hakoniwaRoot` の親 Canvas／`_hakoniwaRoot.gameObject.layer` が非 Default 専用 layer）。
    - perspective camera: `targetTexture == RawImage.texture`（RT）・`orthographic==false`・`cullingMask == (1<<hakoLayer)` のみ・fov/位置が `StageParams.Default` 由来。
    - Main camera: `cullingMask & (1<<hakoLayer) == 0`（当該 layer 除外）。
    - board tilt: `_hakoniwaRoot.rotation`（world）== `Euler(StageParams.Default.pitchDeg, yawDeg, 0)`（tilt の所在＝canvas か root かに非依存）、board 寸法も Default 由来。

    **段階 RED→GREEN**（各 section の弁別を staged green で実証）:
    - RED: probe 3 改変（Section1 ref / Section2 反転 / Section16 追加）を land、production 未配線。`??` chain で **Section1 が最初に FAIL**（`_hakoniwaRawImage` serialized field missing）。gate RED 確認。
    - GREEN 段 1: `BackcastWorkspaceRoot._hakoniwaRawImage` 追加 ＋ `SceneBuilder` で RawImage を Content 配下に author・`SetRef` → Build で scene 再生成。Section1 green、しかし Section2 が FAIL（HakoniwaRoot 未転出）→ Section2 の弁別実証。
    - GREEN 段 2: `SceneBuilder` で Stage canvas（WorldSpace+layer）＋ perspective camera（RT/cullingMask/StageParams.Default）を author・HakoniwaRoot を Stage canvas へ reparent・Main camera layer 除外 → Build で scene 再生成 → commit。Section2 + Section16 green。全 section PASS。

    **RED 実走ログ**（2026-06-20、Unity 6000.4.11f1 / `<Unity.app>/Contents/MacOS/Unity -batchmode -nographics -projectPath /Users/sasac/backcast -executeMethod BackcastWorkspaceProbe.Run -logFile <log>`。lockfile 無し・editor 非起動を pgrep で確認した上で実走）:

    ```
    UNITY_EXIT=1
    [BACKCAST WORKSPACE FAIL] serialized field missing on root: _hakoniwaRawImage
    ```

    compile error 0 件（log の `error CS` grep が空）で `??` chain が Section1 で短絡＝予告どおりのクリーン RED。production（`BackcastWorkspaceRoot`）に `_hakoniwaRawImage` serialized field が未追加であることが gate で固定された。次は段 1（field 追加 ＋ RawImage author）で Section1 green / Section2 FAIL へ進む。

    **GREEN 段1 実走ログ**（2026-06-20、§10 と同コマンド `<Unity.app>/Contents/MacOS/Unity -batchmode -nographics -quit -projectPath /Users/sasac/backcast -executeMethod BackcastWorkspaceProbe.Run -logFile <log>`。lockfile 無し・editor 非起動を確認の上で実走。production に `_hakoniwaRawImage` field を追加し `SceneBuilder` が RawImage を Content 配下に author・`SetRef` → Build で scene 再生成後）:

    ```
    compile error 0 件 / UNITY_EXIT=1
    [BACKCAST WORKSPACE FAIL] HakoniwaRoot still a child of Content (must move to the World-Space Hakoniwa Stage canvas, #93/0068 §15)
    ```

    `??` chain の verdict が Section1 の `_hakoniwaRawImage serialized field missing` を**素通り**して Section2 の reparent assert（L156）で短絡＝**Section1 GREEN**（段1 の RawImage field 追加＋author が land）かつ **Section2 FAIL**（HakoniwaRoot 未転出）。予告どおりの段1 弁別で、Section2 が「flat（Content 直下）」と「World-Space Stage 転出後」を弁別することを実証。RawImage の `.texture`（RT）・Stage canvas・perspective camera・HakoniwaRoot reparent は未配線（段2）。次は段2（Stage canvas WorldSpace+layer ＋ perspective camera RT/cullingMask/StageParams.Default ＋ HakoniwaRoot reparent ＋ Main camera layer 除外）で Section2 + Section16 green → 全 section PASS。

    **GREEN 段2 実走ログ**（2026-06-20、§10 と同コマンド `<Unity.app>/Contents/MacOS/Unity -batchmode -nographics -quit -projectPath /Users/sasac/backcast -executeMethod BackcastWorkspaceProbe.Run -logFile <log>`。lockfile 無し・editor 非起動を pgrep で確認の上で実走。段2 production を land 済み＝`SceneBuilder` が World-Space Hakoniwa Stage canvas（`RenderMode.WorldSpace` ＋ 専用 "Hakoniwa" layer）＋ perspective camera（RT 撮影・`cullingMask`=Hakoniwa layer のみ・fov/位置 `StageParams.Default` 由来）を author・HakoniwaRoot を Stage canvas へ reparent・Main camera は Hakoniwa layer を除外・`HakoniwaStage.renderTexture` を新規 author → Build で scene 再生成後）:

    ```
    compile error 0 件（CS error 0 件・warning CS0618 FindFirstObjectByType は既存非関連のみ）/ UNITY_EXIT=0
    [BACKCAST WORKSPACE PASS] all sections green.
    ```

    `??` chain が Section1〜Section16（Section2 反転 reparent assert / Section16 Stage 配線 assert を含む全 section）を素通りして PASS＝**段2 GREEN（全 section PASS）**。段1 で FAIL していた Section2（HakoniwaRoot 未転出）と新規 Section16（Stage canvas WorldSpace+layer / perspective camera RT・cullingMask・StageParams.Default 由来 / Main camera layer 除外 / board tilt）が共に GREEN。production 配線（World-Space Hakoniwa Stage canvas ＋ perspective camera ＋ 透過 RT ＋ HakoniwaRoot reparent ＋ Main camera layer 除外）が scene に land し §15 段2 弁別が完了。段2 build 差分: `M Assets/Scenes/BackcastWorkspace.unity` / `M ProjectSettings/TagManager.asset`（"Hakoniwa" layer 追加）/ `?? Assets/Settings/HakoniwaStage.renderTexture(+.meta)`。probe C 回帰（`HakoniwaPerspectiveStageProbe.Run`）も同日 exit=0 `[HAKONIWA PERSPECTIVE STAGE PASS] 奥収束 + 厚み可視 + 再投影ラウンドトリップ + 入力ルーティング`（§10①②③ ＋ §14④）で no-regression を確認。

    **残課題（段2 外・R3 で扱う）**: runtime spawn される `chart:<id>` タイル GameObject への "Hakoniwa" layer 伝播が未配線。Section16 は scene-authored 階層のみを assert するため、Play 中に動的生成されるタイルが Hakoniwa layer に乗らないと perspective camera の `cullingMask` から外れ、live ChartView/DepthLadder が RT に描画されない（HITL (c)/(d) の前提が崩れる）。R3（math-pick dispatcher 実入力配線）と同じ runtime seam で配線する。

    **`HakoniwaInputPreflightProbe.cs` の扱い**: RAYCAST-DEAD を §11 で記録済みの throwaway 診断。本スライスでは残置（後続で削除可・Navigator 判断）。

    **behavior-to-e2e**: 本スライスは「挙動が変わる＋新不変条件（Stage 配線 = WorldSpace canvas / RT 専有 / layer 分離 / StageParams SoT）＋ probe RED→GREEN」に該当 → `behavior-to-e2e` を formal invoke 済み（台帳 `HakoniwaE2ERunner.md` に **HAKONIWA-14** を追加。⚠️ lead が口頭で言及した "HAKONIWA-12" は既存行（divider resize・対象外）と衝突するため、次の空き id **HAKONIWA-14** を採番した）。

## R1+R2 commit gate — code-review 修正（board-aspect SoT 整合）

16. **review pass（独立 3 観点 Explore ＋ probe 再走、2026-06-20）で board-aspect の math/scene 不整合を是正**。§15 F4 は「board 寸法も Default 由来」を Section16 assert に列挙していたが、実装は rotation/FOV のみ assert し **board 寸法 assert が欠落**していた。この欠落が次の latent bug を mask していた: 確立済みの hakoniwa 盤面は 1000×640（canvas 単位・findings 0007 以来）で、Stage canvas scale 0.01 → world **10×6.4**。しかし `StageParams.Default` は `boardH = 10`（square）＝実盤面を model していない。`UnprojectToSlot`/`SlotToBoardLocal` は self-consistent（§10③ round-trip GREEN）なので probe では露見しないが、**R3 math-pick（screen→RT pixel→`UnprojectToSlot`→slot）が Y 方向で mis-route する** latent bug。

    **RED→GREEN 実走**（§10 と同コマンド `<Unity.app>/Contents/MacOS/Unity -batchmode -nographics -quit -projectPath /Users/sasac/backcast -executeMethod BackcastWorkspaceProbe.Run -logFile <log>`）:
    - RED（Section16 に board world-dim assert (5b) を追加・`boardH=10` のまま）:
      ```
      [BACKCAST WORKSPACE FAIL] stage-wiring: HakoniwaRoot world board 10.00x6.40 != StageParams.Default 10x10 (board dims must derive from the math SoT, §15 F4)
      ```
    - GREEN（`StageParams.Default.boardH` 10→**6.4**＝確立盤面 1000×640 を model・同コマンド）:
      ```
      [BACKCAST WORKSPACE PASS] all sections green.
      ```
    併せて `HakoniwaPerspectiveStageProbe` の RT を `1600×1000`→**`1000×640`** に揃え、math/probe/production を同一 aspect（§15 F4「math と probe と production が同一 SoT」）に統一。probe C 再走も GREEN（`[HAKONIWA PERSPECTIVE STAGE PASS]`＝ boardH=6.4 でも ①奥収束 ≥5% / ②厚み可視 >1px / ③④ 維持）。**この修正は scene 無改変・C# のみ（rebuild 不要）**: 盤面の world 寸法はもとから 10×6.4 で、math Default が誤って 10×10 を称していたのを是正したもの。

    **owner HITL 確認事項**: boardH=6.4（＝確立盤面 1000×640 aspect 踏襲）は square ではない。盤面アスペクトの最終 feel は HITL sign-off（yaw 非対称と同枠）。

## R3 — 入力ディスパッチャ実配管 + runtime layer 伝播（grill 決定 / lead 承認 2026-06-20）

17. **(a) 入力ディスパッチャ = RawImage 上の単一 seam**（grill ロック・lead 承認、新 owner 判断なし）。§5/§11-14 の math-pick routing 決定（pure `RouteBoardPoint`）を **実 screen-press に配線する production 実体**。挙動が flat header-drag から「RawImage への screen-press → RT pixel → `UnprojectToSlot` → `RouteBoardPoint` → swap/pan dispatch」へ変わる。

    **(a) 確定設計**:
    - **単一 seam**: 新規 `HakoniwaStageInputSurface`（MonoBehaviour）を Content 側 `_hakoniwaRawImage`（RT を表示する「写真」）に 1 つだけ付与。盤面タイルは RT に焼かれ EventSystem に直接当たらない（World-Space Stage canvas 上）ため、live 入力経路はこの 1 seam に集約。
    - **`IBeginDrag`/`IDrag`/`IEndDrag` のみ実装**。scroll は実装せず viewport へ bubble＝既存 photo-zoom（`InfiniteCanvasController.ApplyView`）無改変（§2）。
    - **dispatch**: drag press を RawImage-local 正規化 → RT pixel（`rtW,rtH` = `HakoniwaStageMath.StageParams.Default` 由来）→ `HakoniwaStageMath.UnprojectToSlot` → `HakoniwaGridMath.RouteBoardPoint` → `(slot, inHeader)`。`inHeader` なら当該 source slot から swap 開始、drag END が別 cell なら `HakoniwaController.Swap` 発火。body/盤外 press は pan へ fall-through（`Swap` 非発火）。
    - **既存 `HakoniwaTileHeaderInput` は inert 残置**（削除しない）。RT 化でこの per-tile handler は EventSystem に当たらず dead だが、swap ロジック（`SlotOf`/`SlotAtNormalized`/`Swap`）は無改変前提（§据え置き節）なので残す。後続で削除可（Navigator 判断）。

18. **(b) layer culling モデル = 実機 GPU-batchmode preflight で確定**（§11 RAYCAST-DEAD 教訓: 「動きそう」を断定しない）。runtime spawn される `chart:<id>` タイル（`BackcastWorkspaceRoot` L672 `SetParent(_hakoniwaRoot)`）が perspective camera の `cullingMask`（Hakoniwa layer のみ）に乗るかは、Unity の World-Space Canvas culling が **canvas 単位か renderer 単位か**に依存（§15 残課題）。throwaway `HakoniwaInputCullPreflightProbe`（GPU batchmode・RT readback）で実測:
    - **CANVAS-CULL**（canvas layer が支配・child layer 無視）→ root-only layer-set で十分・step 4（再帰）不要。
    - **PER-RENDERER-CULL**（renderer ごとに own layer で cull）→ 全 descendant Graphic へ再帰 layer-set（step 4）。
    - **INCONCLUSIVE**（macOS GPU batchmode が RT に rasterize せず＝control Graphic も非描画）→ (b) の分岐を owner HITL（既定の「live Chart が傾いた RT に出るか」）に畳む。
    note: タイルは sub-canvas を持たず単一 Stage canvas 配下の plain Graphic（`BuildTileChrome`/chart spawn に `Canvas` 追加なし）＝CANVAS-CULL 寄りだが empirical で確定する。

    **verdict 確定 → CANVAS-CULL**（2026-06-20 GPU batchmode 実走・lockfile 無し / editor 非起動を確認）。`HakoniwaInputCullPreflightProbe` を `<Unity.app>/Contents/MacOS/Unity -batchmode -projectPath /Users/sasac/backcast -executeMethod HakoniwaInputCullPreflightProbe.Run -logFile <log>`（`-nographics` 無し＝RT readback に GPU 必須）で実走:
    ```
    UNITY_EXIT=0
    [HAKONIWA CULL PREFLIGHT] CANVAS-CULL rtCreated=True controlLit=True subjectLit=True hakoLayer=8 rt=1000x640 — a child on the EXCLUDED layer STILL rendered: culling is decided at the Canvas level. Root-only layer-set suffices; per-renderer recursion (step 4) NOT needed.
    ```
    `rtCreated/controlLit/subjectLit` 全 True＝GPU batchmode で RT readback が faithful（INCONCLUSIVE 不成立）。excluded layer（Default=0）の child Graphic も RT に描画された＝**culling は Canvas root の layer で決定**。→ runtime spawn される `chart:<id>` / BuyingPower タイルは **Stage Canvas 配下にある限り layer 0 のままでも perspective camera の RT に描画される**。**step 4（ChartView/DepthLadder Render 末尾の per-renderer 再帰 layer 伝播）は不要**と確定。throwaway probe（`HakoniwaInputCullPreflightProbe.cs`）は verdict を記録した本項が正本＝後続で削除可（Navigator 判断）。

19. **step 3 interim layer-set**（§18 で **CANVAS-CULL 確定**）: tile root を `_hakoniwaRoot.gameObject.layer` に揃える（spawn 経路 `BuildTileShell` L672 付近 ＋ base tile 構築）。CANVAS-CULL 確定後、この root-only layer-set は描画上は **harmless no-op に縮退**するが、(i) AFK 不変条件のアンカー、(ii) CANVAS-CULL 前提を守る guard として残す。step 3 AFK assert は次を含める: 「**spawn 後の tile root layer == `_hakoniwaRoot.layer`**」＋「**Stage Canvas root の layer == Hakoniwa**」＋「**spawn される tile / Stage subtree に nested Canvas が混入しない**」（nested Canvas は別 cull root になり CANVAS-CULL 前提＝Canvas-level culling を崩すため）。

20. **behavior-to-e2e（formal invoke 済み 2026-06-20）**: R3 は「挙動が変わる＋新不変条件（screen→RT pixel→slot dispatch / header→swap / body→pan / runtime layer 伝播）＋ AFK RED→GREEN」に該当。gate 配置:
    - **新規 probe `HakoniwaStageInputProbe.cs`**（探索 Probe・batchmode・後続で `HakoniwaInputRoutingE2ERunner` へ昇格＝台帳 HAKONIWA-11a が予告した昇格先）。pure-math の `HakoniwaPerspectiveStageProbe`（Camera/RT-free 契約）を汚さないため別 probe にする。合成 `PointerEventData` を `HakoniwaStageInputSurface` の handler へ直接注入（EventSystem dispatch は bypass＝RAYCAST-DEAD を踏まない。real EventSystem routing は HITL HAKONIWA-11b に残置）。section:
      - (1) header band press → 正しい source slot 捕捉。
      - (2) 別 cell へ drag END → `Swap` 発火・`_order` が入れ替わる。
      - (3) body press → pan spy が受領・`Swap` 非発火。
      - (4) screen→RawImage-local→RT pixel 変換が正しい（既知 rect で決定的に assert）。
      - (5) runtime spawn 後の `chart:<id>` tile root layer == `_hakoniwaRoot.layer`（step 3 不変条件）。
    - **台帳 HAKONIWA-15**（入力ディスパッチャ実配管）＋ **HAKONIWA-16**（runtime layer 伝播）を `HakoniwaE2ERunner.md` に追加（probe land と同時＝step 2 commit）。HAKONIWA-11a の「screen→RT pixel 変換は HITL」を AFK 化（`PointerEventData` 注入）し、11b は real-pixel/real-mouse HITL のみへ縮退。
    - RED→GREEN を本 §17 に実走ログで記録（後続）。

## R3 review consolidation — code-review (3 観点) ＋ Finding 1 cheap hardening（2026-06-20）

21. **独立 3 観点 review pass で Medium 以上ゼロを確定**（reviewer1=correctness / reviewer2=cross-file impact / reviewer3=cleanup-altitude、read-only Explore で live diff をレビュー）。確定事項:
    - **Finding 2 REFUTED**（pan fall-through が OnEndDrag を forward しない件）: `InfiniteCanvasInputSurface` は `IEndDragHandler` を実装せず `OnBeginDrag` も空＝pan は drag 跨ぎで stateless（各 `OnDrag` が live view を読む）。Begin+Drag のみ forward で正しく、End forward 不要。
    - **raycast 順序 OK**: `_hakoniwaRawImage.raycastTarget=true` でも `BackcastWorkspaceSceneBuilder` が RawImage を FloatingWindowLayer より前に author＝floating window が前面 raycast、RawImage は他ハンドラを shadow しない。
    - **double-swap 無し**: RT 焼き後 `HakoniwaTileHeaderInput` は EventSystem に当たらず inert（§17）。唯一の live swap 経路は `HakoniwaStageInputSurface`。
    - **StageParams/RT 整合**: production は `_hakoniwaRawImage.texture`(RT) 実寸から `StageParams` を導出（fallback 1000×640）＝scene-authored RT(1000×640・§16 boardH=6.4) と一致。
    - reviewer3 は Low のみ（probe の magic-number mirroring は意図的 decoupling・throwaway probe は削除可）。**Medium 以上ゼロ → tdd RED-first 修正は不要**。

22. **Finding 1（cheap hardening 採用）**: `HakoniwaStageInputSurface.ScreenToRtPixel` が `RectTransformUtility.ScreenPointToLocalPointInRectangle` の bool を無視していた。Content RawImage の press camera は overlay(null)＝実害は無く sibling `InfiniteCanvasInputSurface` も同 bool を無視する house convention だが、本 surface の route は order state を mutate する（`Swap`）ため、projection 失敗時の garbage `local` が spurious swap を撃つ理論リスクを 1 行 guard で clean no-route（`TryRoute` が NaN で false）に畳んだ。既存の degenerate-rect NaN guard と同じ防御姿勢で挙動変化ゼロ（overlay では bool 常に true）。**correctness bug ではなく防御 hardening のため GREEN-only**（再現に camera-space Content が要り存在しない＝RED 不能）。`HakoniwaStageInputProbe.Run` 再走で GREEN 維持（exit=0 `[HAKONIWA STAGE INPUT PASS]`、Section④ null-cam で guard 透過）。

23. **throwaway preflight probe 2 種を削除（commit から除外）**: `HakoniwaInputPreflightProbe.cs`（RAYCAST-DEAD verdict・§11）/ `HakoniwaInputCullPreflightProbe.cs`（CANVAS-CULL verdict・§18）は診断 scaffolding で verdict は本 findings に蒸留済み（再走コマンドも記載）＝regression gate ではないため削除。durable gate `HakoniwaStageInputProbe`（HAKONIWA-15/16 昇格予定）は残置。

## 実装フェーズの必須ゲート

本作業は「**挙動が変わる ＋ 新しい不変条件（構図 = 奥収束/厚み可視/再投影ラウンドトリップ）が生まれる ＋ AFK probe RED→GREEN**」に該当する。spike 着手時に `behavior-to-e2e` を formal invoke すること（手書き RED は代替にならない）。

## HITL 目視 sign-off チェックリスト（(c) 透過合成 / (d) perf / yaw 非対称）

spike item 8 の (c)(d) と item 7 の yaw 非対称は AFK で弁別不能なため owner HITL 目視で sign-off する（owner 承認 2026-06-20）。Play 中に live Chart + Depth を載せた hakoniwa Stage で確認する:

- [ ] **(c) 透過 RT 合成 — alpha 抜け**: RT clear alpha=0 / straight-alpha 合成で、盤面外（タイル隙間・板の外周）が背景（InfiniteCanvas / `_floatingLayer`）を透かして見える。黒/不透明の塗り潰しや白フチ（premultiplied 取り違え）が出ない。
- [ ] **(c) 落ち影の抜け**: タイルの落ち影（drop shadow）が盤面外へソフトに抜け、ハードクリップ（RT 矩形での切れ）や二重縁が出ない。
- [ ] **(d) perf budget**: live ChartView + DepthLadder を載せた状態で RT カメラを毎フレーム再撮影しても fps が budget 内（目標 fps は owner と確定。最低でも pan/zoom/tile drag が体感カクつきなく追従）。
- [ ] **yaw 非対称（3/4 ビューらしさ）**: yaw≈15° で左右が非対称な俯瞰に見え、正対（front-parallel）に戻っていない。傾き方向が owner 意図（TFWR 風）と一致。

各項目を owner 目視で PASS 判定し（日付・所見をログ）、本節のチェックボックスに反映する。

## 据え置き / 回帰させない既存資産

- `HakoniwaController` の slot=正本（findings 0007）、`HakoniwaTileHeaderInput`、`InfiniteCanvasController.ApplyView` の pan/zoom、`_floatingLayer` parallax（findings 0006）は**無改変**が前提。
- 回帰ガードは spike→probe で固定する。
