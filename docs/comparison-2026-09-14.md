# Repeated measurement comparison

2 runs per arm. Values come from the same independent loop observer in every arm. Each run, not each frame, is one repetition. Lower values indicate fewer/shorter loop stalls.

Candidate minus baseline: a negative difference favors the candidate. 95% intervals use paired Student t estimates with df=1. They assume approximately normal, independent block differences; with this small sample this cannot be verified. Treat them as exploratory uncertainty estimates, not proof of equivalence or general performance gains.

### client: run-level observations

| Arm | Metric | Mean | Run minimum | Run maximum |
| --- | --- | ---: | ---: | ---: |
| old_ultra | mean_loop_ms | 33.532 | 33.312 | 33.751 |
| old_ultra | p95_loop_ms | 34.202 | 34.035 | 34.368 |
| old_ultra | max_loop_ms | 280.215 | 261.853 | 298.577 |
| old_ultra | stalls_per_minute | 10.590 | 5.522 | 15.659 |
| new_ultra | mean_loop_ms | 34.266 | 33.792 | 34.740 |
| new_ultra | p95_loop_ms | 35.898 | 34.786 | 37.010 |
| new_ultra | max_loop_ms | 315.136 | 288.914 | 341.358 |
| new_ultra | stalls_per_minute | 33.595 | 23.944 | 43.247 |
| new_classic | mean_loop_ms | 33.731 | 33.526 | 33.936 |
| new_classic | p95_loop_ms | 34.624 | 34.572 | 34.676 |
| new_classic | max_loop_ms | 228.956 | 121.828 | 336.084 |
| new_classic | stalls_per_minute | 15.653 | 12.888 | 18.417 |

### client: new_ultra minus old_ultra

| Metric | Mean difference | Paired difference range | Exploratory 95% interval |
| --- | ---: | ---: | ---: |
| mean_loop_ms | 0.735 | [0.481, 0.989] | [-2.495, 3.965] |
| p95_loop_ms | 1.696 | [0.751, 2.641] | [-10.312, 13.705] |
| max_loop_ms | 34.922 | [27.061, 42.782] | [-64.951, 134.794] |
| stalls_per_minute | 23.005 | [18.422, 27.588] | [-35.228, 81.238] |

### client: new_classic minus new_ultra

| Metric | Mean difference | Paired difference range | Exploratory 95% interval |
| --- | ---: | ---: | ---: |
| mean_loop_ms | -0.535 | [-0.804, -0.266] | [-3.951, 2.880] |
| p95_loop_ms | -1.274 | [-2.333, -0.214] | [-14.738, 12.191] |
| max_loop_ms | -86.180 | [-167.086, -5.275] | [-1114.186, 941.825] |
| stalls_per_minute | -17.943 | [-24.830, -11.056] | [-105.451, 69.565] |

### server: run-level observations

| Arm | Metric | Mean | Run minimum | Run maximum |
| --- | --- | ---: | ---: | ---: |
| old_ultra | mean_loop_ms | 33.783 | 33.661 | 33.904 |
| old_ultra | p95_loop_ms | 35.333 | 35.172 | 35.493 |
| old_ultra | max_loop_ms | 111.345 | 106.550 | 116.140 |
| old_ultra | stalls_per_minute | 20.702 | 13.796 | 27.608 |
| new_ultra | mean_loop_ms | 34.403 | 34.133 | 34.673 |
| new_ultra | p95_loop_ms | 36.473 | 36.294 | 36.652 |
| new_ultra | max_loop_ms | 177.283 | 142.236 | 212.330 |
| new_ultra | stalls_per_minute | 33.574 | 28.530 | 38.618 |
| new_classic | mean_loop_ms | 33.753 | 33.582 | 33.925 |
| new_classic | p95_loop_ms | 35.410 | 35.377 | 35.443 |
| new_classic | max_loop_ms | 228.829 | 126.407 | 331.251 |
| new_classic | stalls_per_minute | 9.662 | 5.520 | 13.803 |

### server: new_ultra minus old_ultra

| Metric | Mean difference | Paired difference range | Exploratory 95% interval |
| --- | ---: | ---: | ---: |
| mean_loop_ms | 0.620 | [0.472, 0.768] | [-1.261, 2.501] |
| p95_loop_ms | 1.140 | [1.121, 1.159] | [0.902, 1.378] |
| max_loop_ms | 65.938 | [35.686, 96.190] | [-318.449, 450.325] |
| stalls_per_minute | 12.872 | [11.010, 14.734] | [-10.784, 36.528] |

### server: new_classic minus new_ultra

| Metric | Mean difference | Paired difference range | Exploratory 95% interval |
| --- | ---: | ---: | ---: |
| mean_loop_ms | -0.650 | [-0.748, -0.552] | [-1.899, 0.599] |
| p95_loop_ms | -1.063 | [-1.275, -0.851] | [-3.757, 1.631] |
| max_loop_ms | 51.546 | [-15.829, 118.921] | [-804.535, 907.627] |
| stalls_per_minute | -23.913 | [-24.815, -23.010] | [-35.383, -12.442] |

### Interpretation limits

- Pacing, graphics mode, mod settings, fixture resets and external load must be checked against the experiment record.
- The old/new comparison estimates the effect of the complete diagnostics change; it does not isolate any one probe.
- The Classic/Ultra comparison changes simulation scope and is not a behavior-preserving mod optimization.
- Per-frame p95 here is a nearest-rank quantile from observer samples, not a percentile averaged across capture intervals.
- Loop timings are elapsed time and include pacing/scheduling. They are not exclusive CPU time or GPU time.
- Small samples, order effects and concurrent applications limit causal attribution. No LAN-client latency is measured.
