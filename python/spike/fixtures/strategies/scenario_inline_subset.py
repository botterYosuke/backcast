"""scenario_inline_subset — #66 golden fixture for the C# ScenarioInlineReader.

Exercises the Python-literal SUBSET the C# reader must reproduce that the existing
kernel_spike_buy_sell.py fixture does NOT cover (findings 0043 §2):
  - single-quoted strings,
  - underscore-separated int (`5_000_000`),
  - a multi-instrument list,
  - a NESTED sibling dict (strategy_init_kwargs) holding True / False / None and a string
    with an embedded comma — the C# parser must skip past it without choking,
  - a trailing comma after the last entry.

A valid v3 scenario (instruments + required base keys + optional strategy_init_kwargs /
account_type) so engine.strategy_runtime.scenario.load_scenario accepts it as the golden SoT.
This module is loaded only by load_scenario (AST extract, no import), so it needs no Strategy.
"""

SCENARIO: dict = {
    'schema_version': 3,
    'instruments': ['7203.TSE', '6758.TSE'],
    'start': '2024-01-04',
    'end': '2024-06-28',
    'granularity': 'Minute',
    'initial_cash': 5_000_000,
    'strategy_init_kwargs': {'use_trailing': True, 'dry_run': False, 'cap': None, 'note': 'edge, case'},
    'account_type': 'MARGIN',
}
