# backcast

`The-Trader-Was-Replaced`（Bevy + 埋め込み Python の取引アプリ）の**後継・本線フロントエンド**。
Unity(C#) のゲーム内に同じ空間（Infinite canvas / Hakoniwa / Floating window）を再構築し、
取引 engine（Python/Nautilus）を pythonnet で**同一プロセスに埋め込む**。方針は ADR-0001。

## Language

**backcast**:
本線（going-forward）の Unity フロントエンド。取引 engine を所有し、埋め込み Python で
Replay / Live / Auto を動かす。
_Avoid_: Unity 版（曖昧）、新フロント

**The-Trader-Was-Replaced（TTWR）**:
backcast の前身となる Bevy(Rust) アプリ。カットオーバー（#5）までは凍結された fallback として
本番に温存し、その後**廃止**する。going-forward の開発は行わない。
_Avoid_: Bevy 版を「現行/本番」と呼ぶこと（fallback かつ廃止予定であり本線ではない）

**engine**:
host 非依存の Python 取引エンジン（Nautilus ベース、`python/engine`）。TTWR から backcast へ
**移植**して backcast が所有する。host（Bevy/Unity）とは **sink 注入点・2 入口モジュール
（`engine.core` / `engine.inproc_server`）・dict 境界**でのみ接し、host 型を import しない。
_Avoid_: backend、Python バックエンド（engine が正）

**adapter（C# adapter 層）**:
Unity(C#) 側で pythonnet を介し engine を駆動する単一の境界。engine の sink 口に C# 製 sink を
差し、結果を GIL なしで読める C#/native バッファへ渡す。engine を host 非依存に保つための seam。
_Avoid_: bridge、wrapper

**移植（port）**:
engine のソースを TTWR から backcast へ移し、backcast を唯一の home にすること。
submodule 参照でも pinned-package-from-TTWR でもない（TTWR は廃止されるため）。
_Avoid_: 共有、依存（TTWR を生かしたまま参照する含意を避ける）

**seam ゲート（S0 / S2-spike）**:
threading の継ぎ目を段ごとに検証する throwaway spike。**S0**（#2）= threaded **backtest**
（有界・1 回 run）、**S2-spike**（#7）= live **asyncio loop**（長時間・tokio・venue WS・polling）。
前段の green は後段の保証にならない、を前提に分けて立てる。
_Avoid_: spike をまとめて 1 つにすること

## Flagged ambiguities

- **「本番」**: backcast の文脈では将来の本線を指すが、移行期間中の **live 実弾**は当面 TTWR(Bevy) が
  担い得る。「本番フロント」=backcast（going-forward）、「現 live 実行系」=TTWR（fallback）と区別する。

## Example dialogue

> **Dev:** Live を Unity 側に出すのはいつ？
> **Owner:** S2-spike（#7）が green になってから。S0 は backtest の threading しか見てない。
> **Dev:** engine は TTWR から参照する？
> **Owner:** いや、**移植**。backcast が engine を所有する。TTWR は fallback で温存して、カットオーバーで**廃止**。
> **Dev:** じゃあ engine の host 結合は剥がす必要があるね。
> **Owner:** ほぼ剥がれてる。sink 注入点と `engine.core` / `engine.inproc_server` の 2 入口、dict 境界だけ。
>   そこに C# の **adapter** を差せば host 非依存のまま動く。
