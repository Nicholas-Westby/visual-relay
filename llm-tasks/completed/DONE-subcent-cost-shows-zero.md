# Sub-cent costs display as $0.00

Per-task and per-stage cost reads `$0.00` even after a real run, so genuine spend on cheap
models is invisible and indistinguishable from a free / unpriced run.

The cost is computed correctly but lost in formatting. For the sample `add-multiply` run
(`stage1-attempt1.report.json`: 3556 prompt tokens, model `cheap-kimi`), `RelayCostEstimator`
yields ≈ $0.00051. `src/VisualRelay.Domain/MoneyFormatter.cs:7` then rounds to 2 decimals:

```csharp
var rounded = Math.Round(Math.Max(0, usd), 2, MidpointRounding.AwayFromZero);
return $"${rounded.ToString("0.00", ...)}";
```

so anything under half a cent collapses to `$0.00`. Note `cheap-kimi` *is* priced
(`RelayPricing.cs:11`), yet its output is identical to a model that isn't in the table (where
the estimator returns `Priced=false` and cost `0`).

## Recommended fix

Make `MoneyFormatter.Dollars` adaptive: keep 2 decimals for amounts ≥ $0.01, but for a
non-zero amount below $0.01 show enough significant digits that it never renders as zero
(e.g. `$0.0005`), reserving `$0.00` for a genuinely zero amount. Update
`RelayCostEstimatorTests` / the "dollar cost scale" expectations to match.

## Done when

- A ~$0.0005 run displays a non-zero cost; a truly zero cost still shows `$0.00`.
- (Related) an unpriced model (`Priced=false`) remains distinguishable from a priced
  near-zero cost in the UI.
- Unit tests cover the small-amount formatting boundary. Write the failing test first.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
