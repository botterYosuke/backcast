---
name: grill-with-docs
description: >-
  Grilling session that challenges your plan against the existing domain model, sharpens terminology, and updates documentation (CONTEXT.md, ADRs) inline as decisions crystallise. Use when user wants to stress-test a plan against their project's language and documented decisions.
  **ユーザーが `gh issue #N を実施してください /plan /grill-with-docs` のようにコマンドリストに `/grill-with-docs` を含めたときは必ず発動する**（#199 実例: `/plan /grill-with-docs /diagnose` が羅列されていたのに tdd と diagnose の確認に注力して grill-with-docs を発動しなかった）。複数スキルが羅列されたコマンドでも各スキルを順番に invoke すること。
  **実装後の docs/wiki 整合確認にも使う**: 「実装した内容が wiki と食い違っていないか確認したい」「API の呼び出し側の挙動を docs と照合したい」「契約を明文化する前に docs を読みたい」といった場面でも起動する。実装に入る前の設計ドリルだけでなく、**実装後に呼び出し側コード（`_backend_impl.py` の `hasattr` dispatch など）と docs/wiki のどちらが正しいか確認するコードリーディングとしても機能**する（実例: #189 で `set_execution_hooks` の呼び出しパターンを `_backend_impl.py` で確認）。
---

<what-to-do>

Interview me relentlessly about every aspect of this plan until we reach a shared understanding. Walk down each branch of the design tree, resolving dependencies between decisions one-by-one. For each question, provide your recommended answer.

Ask the questions one at a time, waiting for feedback on each question before continuing.

If a question can be answered by exploring the codebase, explore the codebase instead.

</what-to-do>

<supporting-info>

## Domain awareness

During codebase exploration, also look for existing documentation:

**起動引数にドメインスキル参照（`/nautilus-trader` / `/tachibana` / `/kabusapi` など）が含まれるときは、grill を始める前にその `.claude/skills/<name>/SKILL.md` を読み、ドメイン不変条件を頭に入れる**こと。コード読解で挙動を裏取りするのは依然必須だが、ライブラリ内部仕様（例: nautilus の EXTERNAL/INTERNAL aggregation・backtest に data-client subscribe callback が無い等）に踏み込む主張は、コード＋ドメインスキルの両方で確認すると精度が上がる（#266-269 設計ドリル実例: `/nautilus-trader` 参照があったのにスキル本文を読まずコードだけで進めた）。

### File structure

Most repos have a single context:

```
/
├── CONTEXT.md
├── docs/
│   └── adr/
│       ├── 0001-event-sourced-orders.md
│       └── 0002-postgres-for-write-model.md
└── src/
```

If a `CONTEXT-MAP.md` exists at the root, the repo has multiple contexts. The map points to where each one lives:

```
/
├── CONTEXT-MAP.md
├── docs/
│   └── adr/                          ← system-wide decisions
├── src/
│   ├── ordering/
│   │   ├── CONTEXT.md
│   │   └── docs/adr/                 ← context-specific decisions
│   └── billing/
│       ├── CONTEXT.md
│       └── docs/adr/
```

Create files lazily — only when you have something to write. If no `CONTEXT.md` exists, create one when the first term is resolved. If no `docs/adr/` exists, create it when the first ADR is needed.

## During the session

### Challenge against the glossary

When the user uses a term that conflicts with the existing language in `CONTEXT.md`, call it out immediately. "Your glossary defines 'cancellation' as X, but you seem to mean Y — which is it?"

### Sharpen fuzzy language

When the user uses vague or overloaded terms, propose a precise canonical term. "You're saying 'account' — do you mean the Customer or the User? Those are different things."

### Discuss concrete scenarios

When domain relationships are being discussed, stress-test them with specific scenarios. Invent scenarios that probe edge cases and force the user to be precise about the boundaries between concepts.

### Cross-reference with code

When the user states how something works, check whether the code agrees. If you find a contradiction, surface it: "Your code cancels entire Orders, but you just said partial cancellation is possible — which is right?"

### Update CONTEXT.md inline

When a term is resolved, update `CONTEXT.md` right there. Don't batch these up — capture them as they happen. Use the format in [CONTEXT-FORMAT.md](./CONTEXT-FORMAT.md).

`CONTEXT.md` should be totally devoid of implementation details. Do not treat `CONTEXT.md` as a spec, a scratch pad, or a repository for implementation decisions. It is a glossary and nothing else.

### Offer ADRs sparingly

Only offer to create an ADR when all three are true:

1. **Hard to reverse** — the cost of changing your mind later is meaningful
2. **Surprising without context** — a future reader will wonder "why did they do it this way?"
3. **The result of a real trade-off** — there were genuine alternatives and you picked one for specific reasons

If any of the three is missing, skip the ADR. Use the format in [ADR-FORMAT.md](./ADR-FORMAT.md).

**Never edit an existing ADR that declares itself fixed.** Before proposing to amend an ADR,
read its end matter for a self-protection clause (e.g. ADR 0022's "The decisions are fixed …
reopening requires a new ADR superseding this one, not an edit to this file"). If present —
or if the precedent in sibling work recorded its resolution elsewhere — do **not** amend it.
Two cases are commonly mistaken for "needs an amend":
- The new decision merely **resolves a fork the ADR intentionally left open** (e.g. ADR 0022
  Decision 2 said "quantized re-raster *or* SDF", and #250 chose quantize). That is a
  confirmation, not a reopening — record it in the slice's `docs/findings/NNNN-*.md` (the
  canonical record for that slice) + `FLOWS.md` + `docs/wiki`, and have the slice **point to**
  the ADR ("方針: ADR NNNN"), never write back into it.
- The decision genuinely contradicts the ADR → write a **new superseding ADR**, not an edit.
Mirror what sibling slices did (in this repo, #247/#248/#249 all kept ADR 0022 immutable and
recorded in `docs/findings/`). Surface the clause to the user before touching any ADR file.

</supporting-info>
